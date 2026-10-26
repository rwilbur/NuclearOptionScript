using HarmonyLib;
using UnityEngine;

namespace AutopilotMod
{
    /// <summary>
    /// Autopilot for the "collective" aircraft (throttle = collective). All three run the game's
    /// HeloControlsFilter, a rate-command fly-by-wire (stick = body rate / max rate), so these laws
    /// command body rates directly and let the FBW's own integrator trim them out.
    ///
    /// Modelled on real rotorcraft AFCS coupled modes:
    ///   SAH-46 Chicane (single rotor + tail rotor) - AH-64/UH-60 style: collective holds altitude, pitch
    ///     attitude holds airspeed, pedals hold heading in the hover, bank + turn coordination above
    ///     ~25 m/s, hover hold (velocity hold) when the speed target is 0.
    ///   UH-90 Ibis (coaxial + pusher fans) - S-97/X2 style: helicopter at low speed; at cruise the
    ///     fuselage is held level and the pusher (customAxis1) holds speed.
    ///   VL-49 Tarantula (quad tiltwing) - CL-84 / V-22 style: control allocation blends with wing angle,
    ///     helicopter mode (collective -> altitude, pitch -> speed) to airplane mode (pitch -> altitude,
    ///     power -> speed). The game's "Auto Wing Tilt" keeps scheduling the wing; with it off the
    ///     pilot owns the wing and the A/P just flies whatever angle it is at.
    ///
    /// Attitude, collective and pusher are each driven by an integrator on an ACCELERATION error, so
    /// trim (nose-down for drag, hover collective, cruise prop thrust) is found without knowing it.
    /// </summary>
    internal sealed partial class FlightController
    {
        public bool Rotorcraft { get; private set; }
        public float NacelleDeg = float.NaN;   // tiltwing only, 0 = vertical lift, 90 = airplane mode

        private const float HoverSpeed = 12f, CruiseSpeed = 25f;   // m/s blend for heading/bank logic
        private const float RotorMaxVs = 8f, VrsMaxSink = 3f;        // VRSThreshold is 4 m/s in the game
        // Rotor speed floor and collective rate (1/s) per unit of Nr below/above it. The game's governor
        // asks for 110% power once Nr is 0.3% low, so healthy Nr sits near 1.0; under 0.97 the engine is
        // saturated and the rotor is being dragged down. At Nr 0.99 the normal 0.6/s rate is still allowed.
        private const float NrLimit = 0.97f, NrBackoff = 30f;

        public bool RotorRpmLimited { get; private set; }
        public float RotorNr = float.NaN;   // main rotor speed / nominal, NaN without a RotorShaft

        private Aircraft rotorAc;
        private RotorShaft[] rotorShafts;
        private TiltWingController tilt;
        private CompoundHeloController compound;
        private Vector3 heloMaxRate;   // HeloFlyByWire.maxAngularVel (pitch, yaw, roll) rad/s
        private PilotPlayerState lastPps;

        private float pitchCmdDeg, collective, pusher;
        private float prevFwd = float.NaN, prevVs = float.NaN, fwdAccel, vsAccel;
        private bool storedFlightAssist, flightAssistParked;

        private void BindRotorcraft(Aircraft ac)
        {
            if (ac == rotorAc) return;
            rotorAc = ac;
            // The Tarantula's ConstantSpeedProps govern their own RPM (blade pitch flattens on droop);
            // only the helicopters' RotorShafts can be dragged down by the collective.
            rotorShafts = ac.GetComponentsInChildren<RotorShaft>();
            tilt = ac.GetComponentInChildren<TiltWingController>();
            compound = ac.GetComponentInChildren<CompoundHeloController>();
            heloMaxRate = new Vector3(0.8f, 0.8f, 1f);
            if (ac.GetControlsFilter() is HeloControlsFilter hcf)
            {
                Vector3 r = Traverse.Create(hcf).Field("heloFlyByWire").Field("maxAngularVel").GetValue<Vector3>();
                if (r.x > 0.05f && r.y > 0.05f && r.z > 0.05f) heloMaxRate = r;
            }
        }

        private void RotorEngage()
        {
            pitchCmdDeg = State.Pitch;
            collective = State.Throttle;
            bankCmdSlewed = State.Bank;
            gammaPrev = float.NaN;
            gammaRate = 0f;
            raltFiltered = State.RadarAlt;
            playerThrottleAtEngage = playerThrottle;
        }

        private void RotorDisengage(string reason)
        {
            SetRotorSpeedHold(false, null, quiet: true);
            // Leave the collective where the A/P had it (keyboard/relative levers) - dropping it to the
            // lever's old position could sink the aircraft. Not when the pilot pulled it themselves.
            if (reason == null || !reason.EndsWith("COLLECTIVE"))
                PlayerThrottleBridge.SetLever(lastPps, collective, true);
        }

        /// <summary>SPD on a rotorcraft is an A/P coupled mode (airspeed hold / hover hold), not an A/T.</summary>
        private void SetRotorSpeedHold(bool on, Aircraft ac, bool quiet = false)
        {
            if (!on)
            {
                if (!AtEngaged) return;
                AtEngaged = false;
                if (flightAssistParked && rotorAc != null) rotorAc.SetFlightAssist(storedFlightAssist);
                flightAssistParked = false;
                if (!quiet) report("SPD HOLD OFF", false);
                return;
            }
            if (AtEngaged) return;
            if (!ApEngaged)
            {
                report("SPD N/A - A/P OFF", true);
                return;
            }
            TgtSpeed = State.FwdSpeed < 3f ? 0f : Mathf.Round(State.FwdSpeed);
            pusher = compound != null ? PusherFromAxis(ac.GetInputs().customAxis1) : 0f;
            // The Ibis's "Auto Thrust" drives the pusher from pitch attitude and would fight us.
            if (compound != null && ac.flightAssist)
            {
                storedFlightAssist = true;
                flightAssistParked = true;
                ac.SetFlightAssist(false);
            }
            AtEngaged = true;
            report(TgtSpeed == 0f ? "HOVER HOLD ENGAGED" : "SPD HOLD ENGAGED", false);
        }

        private void FlyRotor(Aircraft ac, ControlInputs inputs, float dt)
        {
            FlightState s = State;
            HasFbw = true;
            NacelleDeg = tilt != null ? tilt.GetWingAngle() : float.NaN;

            if (!float.IsNaN(prevFwd))
            {
                fwdAccel += ((s.FwdSpeed - prevFwd) / dt - fwdAccel) * (dt / (0.3f + dt));
                vsAccel += ((s.VerticalSpeed - prevVs) / dt - vsAccel) * (dt / (0.2f + dt));
            }
            prevFwd = s.FwdSpeed;
            prevVs = s.VerticalSpeed;

            // Regime weights: f = forward-flight (turn coordination), b = airplane mode (tiltwing wing angle
            // past ~30 deg, fully converted by 75), p = pusher owns speed (compound above ~30 m/s).
            float f = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(HoverSpeed, CruiseSpeed, s.FwdSpeed));
            float b = float.IsNaN(NacelleDeg) ? 0f : Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(30f, 75f, NacelleDeg));
            float p = compound != null ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(25f, 40f, s.FwdSpeed)) : 0f;
            float v = Mathf.Max(s.Speed, 15f);

            // ---------------- vertical command ----------------
            raltFiltered += s.VerticalSpeed * dt + (s.RadarAlt - raltFiltered) * Mathf.Clamp01(2f * dt);
            float maxVs = Mathf.Lerp(RotorMaxVs, cfg.MaxClimbRate.Value, b);
            float k = cfg.AltitudeGain.Value, a = cfg.AltitudeCaptureAccel.Value;
            float vsCmd;
            switch (Vertical)
            {
                case VerticalMode.Balt: vsCmd = CaptureProfile(TgtAltitude - s.Altitude, k, a, maxVs); break;
                case VerticalMode.Ralt: vsCmd = CaptureProfile(TgtRadarAlt - raltFiltered, k, a, maxVs); break;
                default: vsCmd = Mathf.Clamp(TgtVs, -maxVs, maxVs); break;
            }
            // Vortex-ring guard: no fast sink at low airspeed with the rotors in helicopter mode.
            float vrs = Mathf.Lerp(VrsMaxSink, maxVs, Mathf.Max(f, b));
            vsCmd = Mathf.Max(vsCmd, -vrs);
            float floorVs = -(s.Clearance - cfg.GroundFloor.Value) * cfg.FloorGain.Value;
            vsCmd = Mathf.Max(vsCmd, floorVs);
            CmdVs = vsCmd;

            // ---------------- speed command ----------------
            // Forward-acceleration demand from the speed error (hover hold when the target is 0).
            float axErr = AtEngaged
                ? Mathf.Clamp(0.3f * (TgtSpeed - s.FwdSpeed), -2f, 2f) - fwdAccel
                : 0f;

            // Pitch attitude: helicopter-mode speed loop. With Auto Wing Tilt on, the Tarantula's wing
            // add ~4.4 deg of thrust tilt per degree of body pitch in the hover, so soften the loop there.
            float attScale = tilt != null && ac.flightAssist ? 0.2f : 1f;
            pitchCmdDeg -= (1f - b) * (1f - p) * 3f * attScale * axErr * dt;
            // Compound cruise: fuselage level, the pusher does the speed work (X2/S-97 style).
            if (AtEngaged) pitchCmdDeg = Mathf.MoveTowards(pitchCmdDeg, 0f, p * 2f * dt);
            // Airplane mode: attitude follows the aircraft so a reconversion starts from where it is.
            pitchCmdDeg += (s.Pitch - pitchCmdDeg) * Mathf.Clamp01(2f * b * dt);
            pitchCmdDeg = Mathf.Clamp(pitchCmdDeg, -20f, 15f);
            CmdGammaDeg = pitchCmdDeg;

            // ---------------- collective / power ----------------
            float azErr = Mathf.Clamp(0.8f * (vsCmd - s.VerticalSpeed), -3f, 3f) - vsAccel;
            float dColl = (1f - b) * 0.08f * azErr + b * 0.05f * axErr;
            dColl = NrLimitedRate(dColl);
            collective = Mathf.Clamp01(collective + Mathf.Clamp(dColl, -0.6f, 0.6f) * dt);
            OutThrottle = collective;

            if (compound != null && AtEngaged)
            {
                float dPush = p * 0.1f * axErr - (1f - p) * pusher;
                // The fans share the rotor's transmission: no extra thrust while the rotor is drooping.
                if (RotorRpmLimited && Mathf.Abs(pusher + dPush * dt) > Mathf.Abs(pusher)) dPush = 0f;
                pusher += dPush * dt;
                pusher = Mathf.Clamp(pusher, -1f, 1f);
                inputs.customAxis1 = AxisFromPusher(pusher);
            }

            // ---------------- pitch rate ----------------
            float phi = Mathf.Clamp(s.Bank, -60f, 60f) * Mathf.Deg2Rad;
            float turnFf = G / v * Mathf.Sin(phi) * Mathf.Tan(phi) * f;
            float qHelo = 1.5f * (pitchCmdDeg - s.Pitch) * Mathf.Deg2Rad;
            float gamma = s.Gamma * Mathf.Deg2Rad;
            if (!float.IsNaN(gammaPrev))
                gammaRate += ((gamma - gammaPrev) / dt - gammaRate) * (dt / (0.2f + dt));
            gammaPrev = gamma;
            float gammaCmd = Mathf.Asin(Mathf.Clamp(vsCmd / v, -0.25f, 0.25f));
            float sched = Mathf.Clamp(v / 150f, 0.6f, 1.6f);
            float qPlane = cfg.FlightPathGain.Value * sched * (gammaCmd - gamma) - cfg.FlightPathDamping.Value * gammaRate;
            float qCmd = Mathf.Lerp(qHelo, qPlane, b) + turnFf;
            CmdQ = qCmd;

            // ---------------- lateral / directional ----------------
            float hdgErr = Mathf.DeltaAngle(s.Heading, TgtHeading);
            float bankHover, bankCruise;
            if (Lateral == LateralMode.Att)
                bankHover = bankCruise = TgtBank;
            else
            {
                // Hover: wings level, or null sideways drift when a speed/hover hold is on.
                bankHover = AtEngaged ? Mathf.Clamp(Mathf.Atan(-0.3f * s.SideSpeed / G) * Mathf.Rad2Deg, -10f, 10f) : 0f;
                float rate = Mathf.Clamp(cfg.HeadingGain.Value * hdgErr, -cfg.MaxTurnRate.Value, cfg.MaxTurnRate.Value);
                bankCruise = Mathf.Atan(rate * Mathf.Deg2Rad * v / G) * Mathf.Rad2Deg;
            }
            float maxBank = Mathf.Min(cfg.MaxBank.Value, 30f);
            float bankCmd = Mathf.Clamp(Mathf.Lerp(bankHover, bankCruise, f), -maxBank, maxBank);
            bankCmdSlewed = Mathf.MoveTowards(bankCmdSlewed, bankCmd, 0.6f * cfg.MaxRollRate.Value * dt);
            CmdBankDeg = bankCmdSlewed;
            BankLimitedBySurface = false;
            float maxP = cfg.MaxRollRate.Value * Mathf.Deg2Rad;
            float pCmd = Mathf.Clamp(cfg.BankGain.Value * Mathf.DeltaAngle(s.Bank, bankCmdSlewed) * Mathf.Deg2Rad, -maxP, maxP);
            CmdP = pCmd;

            // Yaw: pedals hold heading in the hover; at speed, coordinated-turn rate plus sideslip nulling.
            float rHover = Lateral == LateralMode.Hdg ? Mathf.Clamp(0.8f * hdgErr, -15f, 15f) * Mathf.Deg2Rad : 0f;
            float beta = s.Speed > 5f ? Mathf.Atan2(s.SideSpeed, Mathf.Max(s.FwdSpeed, 1f)) : 0f;
            float rCruise = G * Mathf.Sin(phi) / v + 0.5f * beta;
            float rCmd = Mathf.Lerp(rHover, rCruise, f);

            OutPitch = Mathf.Clamp(-qCmd / heloMaxRate.x, -1f, 1f);   // ControlInputs.pitch is + nose-down
            OutRoll = Mathf.Clamp(pCmd / heloMaxRate.z, -1f, 1f);
            inputs.pitch = OutPitch;
            inputs.roll = OutRoll;
            inputs.yaw = Mathf.Clamp(rCmd / heloMaxRate.y, -1f, 1f);
            inputs.throttle = OutThrottle;
        }

        /// <summary>
        /// Rotor-droop limiter, like a real helicopter's Nr/power limiting: the collective demand is
        /// min-selected against a droop limit, so below NrLimit the collective can't rise and backs off
        /// in proportion to the droop until the rotor recovers. The height loop just waits; it can't wind up
        /// because the collective itself is the integrator.
        /// </summary>
        private float NrLimitedRate(float dColl)
        {
            RotorNr = float.NaN;
            float sum = 0f;
            int n = 0;
            foreach (RotorShaft r in rotorShafts)
            {
                if (r == null) continue;   // shot off
                sum += r.GetRPMRatio();
                n++;
            }
            if (n == 0)
            {
                RotorRpmLimited = false;
                return dColl;
            }
            RotorNr = sum / n;
            float limit = -NrBackoff * (NrLimit - RotorNr);   // > 0 while above the floor
            RotorRpmLimited = dColl > limit && RotorNr < NrLimit + 0.01f;
            return Mathf.Min(dColl, limit);
        }

        // Pusher fans: customAxis1 0.47..0.53 is the idle band, above is forward thrust, below reverse.
        private static float AxisFromPusher(float u) => u > 0f ? 0.53f + 0.47f * u : u < 0f ? 0.47f + 0.47f * u : 0.5f;
        private static float PusherFromAxis(float x) => x > 0.53f ? (x - 0.53f) / 0.47f : x < 0.47f ? (x - 0.47f) / 0.47f : 0f;
    }
}
