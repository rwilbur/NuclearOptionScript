using System;
using HarmonyLib;
using UnityEngine;

namespace AutopilotMod
{
    internal enum LateralMode { Hdg, Att }
    internal enum VerticalMode { Balt, Ralt, Vs }

    /// <summary>Aircraft state in an aviation frame (nose-up / right-wing-down positive), SI units.</summary>
    internal struct FlightState
    {
        public bool Valid;
        public float Speed;          // m/s (same value the HUD shows)
        public float Altitude;       // m above sea level (floating-origin safe)
        public float RadarAlt;       // m above ground/water, gear-bottom reference (matches the HUD "R")
        public float Clearance;      // m from the airframe's lowest point (wingtips/nose/tail/gear) to the surface
        public float HalfSpan;       // m, half the wingspan
        public float GearDrop;       // m from the aircraft origin down to the gear-bottom reference
        public float VerticalSpeed;  // m/s, + up
        public float Heading;        // deg [0,360), same as the HUD compass
        public float Pitch;          // deg, + nose up
        public float Bank;           // deg, + right wing down
        public float Gamma;          // deg, flight path angle, + climbing
        public float PitchRate;      // rad/s body axis, + nose up
        public float RollRate;       // rad/s body axis, + rolling right
        public float Throttle;       // 0..1 as last applied
        public float AirDensity;     // kg/m^3
        public float FwdSpeed;       // m/s, horizontal ground speed along the nose heading
        public float SideSpeed;      // m/s, horizontal ground speed to the right of the nose
    }

    /// <summary>
    /// Autopilot control laws. Runs inside PilotPlayerState's physics tick, after the game has
    /// written the player's stick into ControlInputs and before Aircraft.FilterInputs() feeds the
    /// fly-by-wire, so the autopilot "holds the stick" exactly like the pilot does.
    ///
    /// Structure (everything proportional except the innermost rate loops):
    ///   vertical: altitude err -> climb rate -> flight-path angle -> pitch rate -> stick
    ///   lateral:  heading err  -> turn rate  -> bank angle        -> roll rate  -> stick
    ///   speed:    speed err    -> throttle (PI + acceleration damping)
    /// Most of the old script's oscillation came from stacking integrators (vs_pid.ki + pitch_pid.ki
    /// + the FBW's own integrator) on top of a rate-commanded airframe, plus ViGEm/gamepad latency.
    /// Here only the rate loops integrate, and they act on measured body rates.
    /// </summary>
    internal sealed partial class FlightController
    {
        private const float G = 9.81f;
        private const float AttHoldSnapDeg = 7f;   // ATT mode snaps to wings-level inside this bank

        private readonly ApSettings cfg;
        private readonly Action<string, bool> report;

        public event Action ApDisconnected;

        public bool ApEngaged { get; private set; }
        public bool AtEngaged { get; private set; }
        public LateralMode Lateral = LateralMode.Hdg;
        public VerticalMode Vertical = VerticalMode.Balt;
        public bool CoarseStep;

        public float TgtHeading, TgtBank, TgtAltitude, TgtRadarAlt, TgtVs, TgtSpeed;

        public FlightState State;
        public bool HasFbw { get; private set; }
        public bool BankLimitedBySurface { get; private set; }

        // Exposed for the MFD's data line
        public float CmdVs, CmdGammaDeg, CmdBankDeg, CmdQ, CmdP, OutPitch, OutRoll, OutThrottle;

        // Rate-loop integrators (stick units) and throttle integrator
        private float qI, pI, thrI;
        private float gammaCmdSlewed, bankCmdSlewed;
        private float gammaPrev = float.NaN, gammaRate;
        private float raltFiltered;
        private float prevSpeed = float.NaN, accelFiltered;
        private float playerThrottle = float.NaN, playerThrottleAtEngage = float.NaN;
        private bool suppressStickOverride;
        private float pilotPitch, pilotRoll;   // raw stick from the latest tick, before the A/P overrides it

        // ---- FBW internals (private serialized fields on ControlsFilter.FlyByWire) ----
        private static readonly AccessTools.FieldRef<ControlsFilter.FlyByWire, float> FbwGLimit = TryRef("gLimitPositive");
        private static readonly AccessTools.FieldRef<ControlsFilter.FlyByWire, float> FbwCorner = TryRef("cornerSpeed");
        private static readonly AccessTools.FieldRef<ControlsFilter.FlyByWire, float> FbwMaxPitchRate = TryRef("maxPitchAngularVel");
        private static readonly AccessTools.FieldRef<ControlsFilter.FlyByWire, float> FbwLimitFactor = TryRef("limitFactorSmoothed");
        private static readonly AccessTools.FieldRef<ControlsFilter.FlyByWire, float> FbwMaxRollRate = TryRef("maxRollAngularVel");
        private static readonly AccessTools.FieldRef<ControlsFilter.FlyByWire, float> FbwMaxRollSpeed = TryRef("maxRollSpeed");
        private static readonly bool FbwReadable = FbwGLimit != null && FbwCorner != null && FbwMaxPitchRate != null
                                                   && FbwLimitFactor != null && FbwMaxRollRate != null && FbwMaxRollSpeed != null;

        private static AccessTools.FieldRef<ControlsFilter.FlyByWire, float> TryRef(string name)
        {
            try { return AccessTools.FieldRefAccess<ControlsFilter.FlyByWire, float>(name); }
            catch { return null; }
        }

        public FlightController(ApSettings cfg, Action<string, bool> report)
        {
            this.cfg = cfg;
            this.report = report;
        }

        /// <summary>Drop everything (new aircraft, death, leaving the mission).</summary>
        public void Reset()
        {
            ApEngaged = false;
            AtEngaged = false;
            Lateral = LateralMode.Hdg;
            Vertical = VerticalMode.Balt;
            State = default;
            prevSpeed = float.NaN;
            playerThrottle = float.NaN;
            playerThrottleAtEngage = float.NaN;
            if (flightAssistParked && rotorAc != null) rotorAc.SetFlightAssist(storedFlightAssist);
            flightAssistParked = false;
            rotorAc = null;
            prevFwd = float.NaN;
        }

        // =====================================================================
        // State sampling
        // =====================================================================
        public static FlightState Sample(Aircraft ac)
        {
            var s = new FlightState();
            Rigidbody rb = ac.CockpitRB();
            if (rb == null) return s;
            Transform t = rb.transform;

            Vector3 v = rb.velocity;
            Vector3 fwd = t.forward;
            Vector3 w = t.InverseTransformDirection(rb.angularVelocity);

            s.Speed = ac.speed;
            s.Altitude = t.position.GlobalY();
            SampleSurface(ac, ref s);
            s.VerticalSpeed = v.y;
            s.Heading = Mathf.Repeat(Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg, 360f);
            s.Pitch = Mathf.Asin(Mathf.Clamp(fwd.y, -1f, 1f)) * Mathf.Rad2Deg;
            s.Bank = Mathf.Atan2(-t.right.y, t.up.y) * Mathf.Rad2Deg;
            float spd = v.magnitude;
            s.Gamma = spd > 1f ? Mathf.Asin(Mathf.Clamp(v.y / spd, -1f, 1f)) * Mathf.Rad2Deg : 0f;
            // Unity: +x local rotation is nose-DOWN, +z local rotation is roll-LEFT.
            s.PitchRate = -w.x;
            s.RollRate = -w.z;
            s.Throttle = ac.GetInputs().throttle;
            s.AirDensity = Mathf.Max(ac.airDensity, 0.05f);
            Vector3 fwdH = new Vector3(fwd.x, 0f, fwd.z).normalized;
            s.FwdSpeed = v.x * fwdH.x + v.z * fwdH.z;
            s.SideSpeed = v.x * fwdH.z - v.z * fwdH.x;
            s.Valid = true;
            return s;
        }

        /// <summary>
        /// Our own radar altimeter, every physics tick. The game's Aircraft.radarAlt only refreshes
        /// every 0.1 s and raycasts terrain only, so over shallow water it measures to the seabed.
        /// Here the surface is the higher of terrain/ship deck and sea level, like the game's AI uses.
        /// </summary>
        private static void SampleSurface(Aircraft ac, ref FlightState s)
        {
            Transform t = ac.transform;
            Vector3 pos = t.position;
            float surfaceY = Datum.LocalSeaY;
            if (Physics.Linecast(pos, pos - Vector3.up * 10000f, out RaycastHit hit,
                    (int)PhysicsLayers.StaticsMask | (int)PhysicsLayers.ShipsMask))
                surfaceY = Mathf.Max(hit.point.y, surfaceY);

            AircraftDefinition def = ac.definition;
            s.GearDrop = Mathf.Max(def.spawnOffset.y, 0f);
            s.HalfSpan = Mathf.Max(def.width * 0.5f, 0f);
            float halfLength = Mathf.Max(def.length * 0.5f, 0f);

            float gearY = pos.y - s.GearDrop;
            s.RadarAlt = gearY - surfaceY;

            // Lowest of: gear reference, both wingtips, nose, tail (wing/fuselage line ~ origin height).
            float lowest = gearY;
            Vector3 span = t.right * s.HalfSpan;
            Vector3 len = t.forward * halfLength;
            lowest = Mathf.Min(lowest, pos.y - Mathf.Abs(span.y));
            lowest = Mathf.Min(lowest, pos.y - Mathf.Abs(len.y));
            s.Clearance = lowest - surfaceY;
        }

        // =====================================================================
        // Engage / disengage / modes
        // =====================================================================
        public void SetAutopilot(Aircraft ac, bool on, string reason = null)
        {
            if (!on)
            {
                if (!ApEngaged) return;
                ApEngaged = false;
                if (Rotorcraft) RotorDisengage(reason);
                report(reason ?? "AUTOPILOT OFF", reason != null);
                ApDisconnected?.Invoke();
                return;
            }
            if (ApEngaged) return;

            string fail = CheckEngageable(ac);
            if (fail != null)
            {
                report("A/P FAIL - " + fail, true);
                return;
            }

            // Like an F-16/F-18 autopilot, engaging holds what you have: targets sync to the
            // current state, then you dial them with the rockers.
            SyncLateral();
            SyncVertical();
            // Bumpless transfer: start the rate integrators at the pilot's last raw stick. (Not the
            // live ControlInputs - by now FilterInputs() has overwritten those with FBW output.)
            qI = Mathf.Clamp(-pilotPitch, -0.6f, 0.6f);
            pI = Mathf.Clamp(pilotRoll, -0.4f, 0.4f);
            gammaCmdSlewed = State.Gamma * Mathf.Deg2Rad;
            gammaPrev = float.NaN;
            gammaRate = 0f;
            bankCmdSlewed = State.Bank;
            raltFiltered = State.RadarAlt;
            if (Rotorcraft) RotorEngage();
            ApEngaged = true;
            report("AUTOPILOT ENGAGED", false);
        }

        public void SetAutothrottle(Aircraft ac, PilotPlayerState pps, bool on, string reason = null)
        {
            if (Rotorcraft)
            {
                SetRotorSpeedHold(on, ac);
                return;
            }
            if (!on)
            {
                if (!AtEngaged) return;
                AtEngaged = false;
                // Leave the throttle lever where the A/T had it (keyboard/relative throttles) unless the
                // pilot disconnected it by moving the throttle themselves.
                if (pps != null && reason == null) PlayerThrottleBridge.SetLever(pps, OutThrottle);
                report(reason ?? "AUTOTHROTTLE OFF", reason != null);
                return;
            }
            if (AtEngaged) return;
            if (!State.Valid || ac == null) return;
            TgtSpeed = State.Speed;
            thrI = State.Throttle;
            OutThrottle = State.Throttle;
            accelFiltered = 0f;
            playerThrottleAtEngage = playerThrottle;
            AtEngaged = true;
            report("AUTOTHROTTLE ENGAGED", false);
        }

        private string CheckEngageable(Aircraft ac)
        {
            if (ac == null || !State.Valid) return "NO DATA";
            if (ac.IsAutoHoverEnabled()) return "AUTO HOVER ON";
            if (ac.cockpit.IsDetached()) return "NO COCKPIT";
            if (State.RadarAlt < cfg.MinEngageRadarAlt.Value) return "ON GROUND";
            if (Rotorcraft) return Mathf.Abs(State.Bank) > 45f || Mathf.Abs(State.Pitch) > 30f ? "ATTITUDE" : null;
            if (State.Speed < cfg.MinEngageSpeed.Value) return "SPEED LOW";
            if (Mathf.Abs(State.Bank) > 60f || Mathf.Abs(State.Pitch) > 45f) return "ATTITUDE";
            return null;
        }

        private static bool IsCollective(Aircraft ac)
        {
            // Same test the game uses to decide the throttle is a collective (helicopters/VTOL).
            return ac.GetAircraftParameters().takeoffDistance == 0f;
        }

        public void SelectLateral(LateralMode m)
        {
            Lateral = m;
            SyncLateral();
        }

        public void SelectVertical(VerticalMode m)
        {
            Vertical = m;
            SyncVertical();
        }

        public void SyncLateral()
        {
            if (!State.Valid) return;
            TgtHeading = Mathf.Round(State.Heading) % 360f;
            TgtBank = Mathf.Abs(State.Bank) < AttHoldSnapDeg ? 0f
                : Mathf.Round(Mathf.Clamp(State.Bank, -cfg.MaxBank.Value, cfg.MaxBank.Value));
        }

        public void SyncVertical()
        {
            if (!State.Valid) return;
            TgtAltitude = State.Altitude;
            TgtRadarAlt = Mathf.Max(State.RadarAlt, cfg.GroundFloor.Value + 2f);
            raltFiltered = State.RadarAlt;
            // V/S captures the current rate rounded to the display step
            float step = Units.VsStep(false);
            TgtVs = Mathf.Round(State.VerticalSpeed / step) * step;
        }

        public void SyncSpeed()
        {
            if (State.Valid) TgtSpeed = Rotorcraft ? Mathf.Max(0f, Mathf.Round(State.FwdSpeed)) : State.Speed;
        }

        public void SyncAll()
        {
            SyncLateral();
            SyncVertical();
            SyncSpeed();
        }

        // ---- target rockers ----
        public void NudgeLateral(int dir, bool coarse)
        {
            coarse |= CoarseStep;
            if (Lateral == LateralMode.Hdg)
            {
                float step = coarse ? 10f : 1f;
                TgtHeading = Mathf.Repeat(Snap(TgtHeading, dir, step), 360f);
            }
            else
            {
                float step = coarse ? 5f : 1f;
                TgtBank = Mathf.Clamp(Snap(TgtBank, dir, step), -cfg.MaxBank.Value, cfg.MaxBank.Value);
            }
        }

        public void NudgeVertical(int dir, bool coarse)
        {
            coarse |= CoarseStep;
            switch (Vertical)
            {
                case VerticalMode.Balt:
                    TgtAltitude = Snap(TgtAltitude, dir, Units.AltStep(coarse));
                    break;
                case VerticalMode.Ralt:
                    TgtRadarAlt = Mathf.Max(cfg.GroundFloor.Value, Snap(TgtRadarAlt, dir, Units.RaltStep(coarse)));
                    break;
                default:
                    float lim = cfg.MaxClimbRate.Value;
                    TgtVs = Mathf.Clamp(Snap(TgtVs, dir, Units.VsStep(coarse)), -lim, lim);
                    break;
            }
        }

        public void NudgeSpeed(int dir, bool coarse)
        {
            coarse |= CoarseStep;
            TgtSpeed = Mathf.Max(Rotorcraft ? 0f : cfg.MinEngageSpeed.Value, Snap(TgtSpeed, dir, Units.SpeedStep(coarse)));
        }

        /// <summary>Step to the next multiple of <paramref name="step"/> in the given direction.</summary>
        private static float Snap(float value, int dir, float step)
        {
            float n = value / step;
            float snapped = dir > 0 ? Mathf.Floor(n + 1e-3f) + 1f : Mathf.Ceil(n - 1e-3f) - 1f;
            return snapped * step;
        }

        // =====================================================================
        // Per-physics-tick entry points (called from the Harmony patches)
        // =====================================================================

        /// <summary>
        /// Called after the game wrote the pilot's stick into <paramref name="inputs"/> and before
        /// FilterInputs(). Detects stick override, then replaces pitch/roll/throttle as needed.
        /// </summary>
        public void FixedTick(Aircraft ac, PilotPlayerState pps, ControlInputs inputs, bool cursorMode)
        {
            float dt = Time.fixedDeltaTime;
            State = Sample(ac);
            if (!State.Valid) return;

            suppressStickOverride = cursorMode;
            lastPps = pps;
            Rotorcraft = IsCollective(ac);
            if (Rotorcraft) BindRotorcraft(ac);
            pilotPitch = inputs.pitch;
            pilotRoll = inputs.roll;

            // Speed derivative for the A/T damping term
            if (!float.IsNaN(prevSpeed))
            {
                float a = (State.Speed - prevSpeed) / dt;
                accelFiltered += (a - accelFiltered) * (dt / (0.4f + dt));
            }
            prevSpeed = State.Speed;

            if (ApEngaged)
            {
                string why = AutoDisconnectReason(ac, inputs);
                if (why != null)
                    SetAutopilot(ac, false, why);
            }

            if (ApEngaged && Rotorcraft)
                FlyRotor(ac, inputs, dt);
            else if (ApEngaged)
            {
                ControlsFilter.FlyByWire fbw = GetFbw(ac);
                HasFbw = fbw != null;
                FlyVertical(fbw, dt);
                FlyLateral(fbw, dt);
                inputs.pitch = OutPitch;
                inputs.roll = OutRoll;
            }

            if (AtEngaged && !Rotorcraft)
            {
                if (ac.cockpit.IsDetached())
                    SetAutothrottle(ac, pps, false, "A/T DISC");
                else
                {
                    FlySpeed(dt);
                    inputs.throttle = OutThrottle;
                }
            }
        }

        /// <summary>
        /// Called after the game computed the throttle from the pilot's lever. Records the lever
        /// (to detect throttle override) and re-applies the A/T output.
        /// </summary>
        public void OnPlayerThrottle(Aircraft ac, PilotPlayerState pps, ControlInputs inputs)
        {
            playerThrottle = inputs.throttle;
            // On a rotorcraft the A/P owns the collective; otherwise only the A/T owns the throttle.
            if (Rotorcraft ? !ApEngaged : !AtEngaged) return;
            if (float.IsNaN(playerThrottleAtEngage))
                playerThrottleAtEngage = playerThrottle;
            else if (Mathf.Abs(playerThrottle - playerThrottleAtEngage) > 0.08f)
            {
                if (Rotorcraft) SetAutopilot(ac, false, "A/P DISC - COLLECTIVE");
                else SetAutothrottle(ac, pps, false, "A/T DISC - THROTTLE");
                return;
            }
            inputs.throttle = OutThrottle;
        }

        private string AutoDisconnectReason(Aircraft ac, ControlInputs pilotInputs)
        {
            if (ac.cockpit.IsDetached()) return "A/P DISC";
            if (ac.IsAutoHoverEnabled()) return "A/P DISC - AUTO HOVER";
            if (State.RadarAlt < 0.5f && State.Speed < cfg.MinEngageSpeed.Value) return "A/P DISC - ON GROUND";

            // Paddle-switch behaviour: a deliberate stick input takes the jet back.
            float thr = cfg.StickOverride.Value;
            if (!suppressStickOverride && (Mathf.Abs(pilotInputs.pitch) > thr || Mathf.Abs(pilotInputs.roll) > thr))
                return "A/P DISC - STICK";
            if (Rotorcraft && !suppressStickOverride && Mathf.Abs(pilotInputs.yaw) > thr) return "A/P DISC - PEDALS";
            return null;
        }

        private static ControlsFilter.FlyByWire GetFbw(Aircraft ac)
        {
            if (!FbwReadable) return null;
            ControlsFilter cf = ac.GetControlsFilter();
            if (cf == null) return null;
            ControlsFilter.FlyByWire fbw = cf.GetFlyByWire();
            return fbw != null && fbw.Enabled ? fbw : null;
        }

        // =====================================================================
        // Vertical: altitude / radar altitude / vertical speed
        // =====================================================================
        private void FlyVertical(ControlsFilter.FlyByWire fbw, float dt)
        {
            FlightState s = State;
            float v = Mathf.Max(s.Speed, 40f);

            // Gain schedule: flight path lags pitch more at low speed, so soften the loops there.
            float sched = Mathf.Clamp(v / 150f, 0.6f, 1.6f);

            // Complementary filter for radar altitude: vertical speed carries the fast motion (no lag),
            // the radar pulls the estimate in over ~0.5 s (smooths terrain spikes).
            raltFiltered += s.VerticalSpeed * dt + (s.RadarAlt - raltFiltered) * Mathf.Clamp01(2f * dt);

            float maxVs = cfg.MaxClimbRate.Value;
            float k = cfg.AltitudeGain.Value * Mathf.Min(sched, 1f);
            float a = cfg.AltitudeCaptureAccel.Value;
            float vsCmd;
            switch (Vertical)
            {
                case VerticalMode.Balt: vsCmd = CaptureProfile(TgtAltitude - s.Altitude, k, a, maxVs); break;
                case VerticalMode.Ralt: vsCmd = CaptureProfile(TgtRadarAlt - raltFiltered, k, a, maxVs); break;
                default: vsCmd = Mathf.Clamp(TgtVs, -maxVs, maxVs); break;
            }

            // Surface floor, on the airframe's LOWEST point (wingtips/nose/tail/gear): the closer the
            // floor, the slower we may sink toward it; below it, climb back out.
            float floor = cfg.GroundFloor.Value;
            float floorVs = -(s.Clearance - floor) * cfg.FloorGain.Value;
            vsCmd = Mathf.Max(vsCmd, floorVs);
            CmdVs = vsCmd;

            float maxGammaSin = Mathf.Sin(cfg.MaxFlightPathAngle.Value * Mathf.Deg2Rad);
            float gammaCmd = Mathf.Asin(Mathf.Clamp(vsCmd / v, -maxGammaSin, maxGammaSin));
            float gammaFloor = Mathf.Asin(Mathf.Clamp(floorVs / v, -maxGammaSin, maxGammaSin));
            gammaCmdSlewed = Mathf.MoveTowards(gammaCmdSlewed, gammaCmd, 4f * Mathf.Deg2Rad * dt);
            // The floor is never delayed by the slew limiter.
            gammaCmdSlewed = Mathf.Max(gammaCmdSlewed, gammaFloor);
            CmdGammaDeg = gammaCmdSlewed * Mathf.Rad2Deg;

            // Coordinated-turn feedforward: in a level turn the body pitch rate is g*sin(phi)*tan(phi)/V.
            float phi = Mathf.Clamp(Mathf.Abs(s.Bank), 0f, 70f) * Mathf.Deg2Rad;
            float gamma = s.Gamma * Mathf.Deg2Rad;
            float turnFf = G / v * Mathf.Sin(phi) * Mathf.Tan(phi) * Mathf.Cos(gamma);

            // Flight-path-rate damping. Gamma lags pitch by the airframe's lift lag (seconds at low
            // speed), which is what makes a plain gamma loop porpoise; feeding back measured
            // d(gamma)/dt damps it without needing to know that lag.
            if (!float.IsNaN(gammaPrev))
                gammaRate += ((gamma - gammaPrev) / dt - gammaRate) * (dt / (0.2f + dt));
            gammaPrev = gamma;

            float qCmd = cfg.FlightPathGain.Value * sched * (gammaCmdSlewed - gamma)
                         - cfg.FlightPathDamping.Value * gammaRate + turnFf;
            qCmd = Mathf.Clamp(qCmd, -cfg.MaxPushG.Value * G / v, cfg.MaxPullG.Value * G / v + turnFf);
            CmdQ = qCmd;

            float qPerStick = PitchRatePerStick(fbw, s);
            float qErr = qCmd - s.PitchRate;
            float ki = fbw != null ? cfg.PitchRateIntegralFbw.Value : cfg.PitchRateIntegralNoFbw.Value;
            float qINext = Mathf.Clamp(qI + ki * qErr / qPerStick * dt, -0.6f, 0.6f);

            float stickUp = (qCmd + cfg.PitchRateGain.Value * qErr) / qPerStick + qINext;
            // Conditional anti-windup: only keep integrating if it doesn't push further into saturation.
            if (!(stickUp > 1f && qErr > 0f) && !(stickUp < -1f && qErr < 0f))
                qI = qINext;
            stickUp = Mathf.Clamp((qCmd + cfg.PitchRateGain.Value * qErr) / qPerStick + qI, -1f, 1f);

            OutPitch = -stickUp;   // ControlInputs.pitch is + nose-down
        }

        /// <summary>
        /// Climb-rate command for an altitude error. Linear (gain k) near the target; further out a
        /// square-root profile, i.e. the rate from which a constant vertical deceleration <paramref name="a"/>
        /// stops exactly on the target. Big changes go at full rate and only round out at the end,
        /// instead of creeping in exponentially.
        /// </summary>
        private static float CaptureProfile(float err, float k, float a, float maxVs)
        {
            float e0 = 2f * a / (k * k);   // where the linear and sqrt segments meet
            float abs = Mathf.Abs(err);
            float vs = abs <= e0 ? k * abs : Mathf.Sqrt(2f * a * abs);
            return Mathf.Sign(err) * Mathf.Min(vs, maxVs);
        }

        /// <summary>Body pitch rate (rad/s) the airframe produces per unit of stick.</summary>
        private static float PitchRatePerStick(ControlsFilter.FlyByWire fbw, FlightState s)
        {
            if (fbw != null)
            {
                // Mirrors ControlsFilter.FlyByWire.Filter(): the FBW turns stick into a pitch-rate demand.
                float corner = Mathf.Max(FbwCorner(fbw), 1f);
                float qbarRatio = s.Speed * s.Speed * s.AirDensity / (corner * corner * 1.225f);
                float q = FbwGLimit(fbw) * G / Mathf.Max(s.Speed, corner * 0.75f);
                if (qbarRatio < 1f) q *= Mathf.Clamp(qbarRatio, 0.3f, 1f);
                q = Mathf.Lerp(FbwMaxPitchRate(fbw), q, Mathf.Clamp01(FbwLimitFactor(fbw)));
                return Mathf.Max(q, 0.05f);
            }
            // Conventional controls: elevator authority grows with dynamic pressure.
            return Mathf.Clamp(0.35f * QbarRatio(s, 150f), 0.08f, 1f);
        }

        // =====================================================================
        // Lateral: heading select / attitude (bank) hold
        // =====================================================================
        private void FlyLateral(ControlsFilter.FlyByWire fbw, float dt)
        {
            FlightState s = State;
            float v = Mathf.Max(s.Speed, 40f);
            float maxBank = cfg.MaxBank.Value;

            float bankCmd;
            if (Lateral == LateralMode.Hdg)
            {
                float err = Mathf.DeltaAngle(s.Heading, TgtHeading);  // + => turn right
                float rateCmd = Mathf.Clamp(cfg.HeadingGain.Value * err, -cfg.MaxTurnRate.Value, cfg.MaxTurnRate.Value);
                bankCmd = Mathf.Atan(rateCmd * Mathf.Deg2Rad * v / G) * Mathf.Rad2Deg;
            }
            else
            {
                bankCmd = TgtBank;
            }
            // Near the surface, limit bank so the low wingtip stays above the floor
            // (a 15 m span at 35 deg drops the tip ~4 m below the radar-altitude reference).
            float originHeight = s.RadarAlt + s.GearDrop;
            if (s.HalfSpan > 0.5f)
            {
                float room = (originHeight - cfg.GroundFloor.Value - 0.5f) / s.HalfSpan;
                float surfaceBank = Mathf.Asin(Mathf.Clamp01(room)) * Mathf.Rad2Deg;
                BankLimitedBySurface = surfaceBank < maxBank;
                maxBank = Mathf.Min(maxBank, surfaceBank);
            }
            else BankLimitedBySurface = false;

            bankCmd = Mathf.Clamp(bankCmd, -maxBank, maxBank);
            bankCmdSlewed = Mathf.MoveTowards(bankCmdSlewed, bankCmd, 0.6f * cfg.MaxRollRate.Value * dt);
            CmdBankDeg = bankCmdSlewed;

            float maxP = cfg.MaxRollRate.Value * Mathf.Deg2Rad;
            float pCmd = Mathf.Clamp(cfg.BankGain.Value * Mathf.DeltaAngle(s.Bank, bankCmdSlewed) * Mathf.Deg2Rad, -maxP, maxP);
            CmdP = pCmd;

            float pPerStick = RollRatePerStick(fbw, s);
            float pErr = pCmd - s.RollRate;
            float pINext = Mathf.Clamp(pI + cfg.RollRateIntegral.Value * pErr / pPerStick * dt, -0.4f, 0.4f);
            float stick = (pCmd + cfg.RollRateGain.Value * pErr) / pPerStick + pINext;
            if (!(stick > 1f && pErr > 0f) && !(stick < -1f && pErr < 0f))
                pI = pINext;
            OutRoll = Mathf.Clamp((pCmd + cfg.RollRateGain.Value * pErr) / pPerStick + pI, -1f, 1f);
        }

        /// <summary>Body roll rate (rad/s) per unit of stick.</summary>
        private static float RollRatePerStick(ControlsFilter.FlyByWire fbw, FlightState s)
        {
            if (fbw != null)
            {
                float corner = Mathf.Max(FbwCorner(fbw), 1f);
                float qbarRatio = s.Speed * s.Speed * s.AirDensity / (corner * corner * 1.225f);
                float p = FbwMaxRollRate(fbw) * Mathf.Clamp(qbarRatio / Mathf.Max(FbwMaxRollSpeed(fbw), 0.01f), 0.5f, 1f);
                return Mathf.Clamp(p, 0.5f, 8f);
            }
            return Mathf.Clamp(2f * QbarRatio(s, 150f), 0.3f, 4f);
        }

        private static float QbarRatio(FlightState s, float refSpeed)
        {
            return s.Speed * s.Speed * s.AirDensity / (refSpeed * refSpeed * 1.225f);
        }

        // =====================================================================
        // Autothrottle
        // =====================================================================
        private void FlySpeed(float dt)
        {
            float lo = Mathf.Clamp01(cfg.AtMinThrottle.Value);
            float hi = Mathf.Clamp(cfg.AtMaxThrottle.Value, lo, 1f);
            float e = TgtSpeed - State.Speed;
            float pd = cfg.SpeedGain.Value * e - cfg.SpeedDamping.Value * accelFiltered;

            thrI += cfg.SpeedIntegral.Value * e * dt;
            thrI = Mathf.Clamp(thrI, lo - pd, hi - pd);   // back-calculation anti-windup
            OutThrottle = Mathf.Clamp(thrI + pd, lo, hi);
        }
    }

    /// <summary>Access to PilotPlayerState's private throttle lever, for bumpless A/T disconnect.</summary>
    internal static class PlayerThrottleBridge
    {
        private static readonly AccessTools.FieldRef<PilotPlayerState, float> SimulatedThrottle = TryRef();

        private static AccessTools.FieldRef<PilotPlayerState, float> TryRef()
        {
            try { return AccessTools.FieldRefAccess<PilotPlayerState, float>("simulatedThrottle"); }
            catch { return null; }
        }

        public static void SetLever(PilotPlayerState pps, float throttle, bool collective = false)
        {
            if (SimulatedThrottle == null || pps == null) return;
            // Inverse of PilotPlayerState.PlayerThrottleAxis1Controls' mapping.
            if (collective && PlayerSettings.invertCollective) throttle = 1f - throttle;
            SimulatedThrottle(pps) = PlayerSettings.throttleUseNegative ? throttle * 2f - 1f : throttle;
        }
    }
}
