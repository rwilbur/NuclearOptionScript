using System;
using System.Threading;
using UnityEngine;

namespace AutopilotMod
{
    /// <summary>
    /// "Terrain, pull up" ground-collision warning, independent of the autopilot.
    ///
    /// Each physics tick: raycast along the velocity vector to the first terrain / ship / water
    /// surface, measure how far that surface is perpendicular to its plane, and compare with the room
    /// an escape needs: reaction time + time to roll wings-level, then a pull-up arc at PullUpG
    /// (depth R*(1-cos beta), R = V^2 / (g*(n-1))). Flying parallel to the surface (level sea
    /// skimming) never closes on it, so it doesn't nag.
    /// </summary>
    internal sealed class TerrainWarning
    {
        private const float G = 9.81f;
        private const float RollOutRate = 120f;   // deg/s assumed for rolling wings-level first

        private readonly ApSettings cfg;
        private readonly BepInEx.Logging.ManualLogSource log;
        private readonly AudioSource whoop;
        private readonly AudioSource recording;
        private readonly SoundBank bank;

        public bool Active { get; private set; }
        public float TimeToImpact { get; private set; }

        private float lastThreat = -10f, lastCall = -10f, callInterval = 1.8f;
        private int callsThisEvent;
        private volatile bool speaking;
        private volatile bool voiceFailed;

        public TerrainWarning(ApSettings cfg, GameObject host, SoundBank bank, BepInEx.Logging.ManualLogSource log)
        {
            this.cfg = cfg;
            this.bank = bank;
            this.log = log;
            recording = host.AddComponent<AudioSource>();
            recording.playOnAwake = false;
            recording.spatialBlend = 0f;
            whoop = host.AddComponent<AudioSource>();
            whoop.clip = MakeWhoop();
            whoop.playOnAwake = false;
            whoop.spatialBlend = 0f;
        }

        public void Reset()
        {
            Active = false;
            callsThisEvent = 0;
        }

        /// <summary>Call once per physics tick while the local player is flying.</summary>
        public void Tick(Aircraft ac, float bankDeg)
        {
            if (!cfg.GpwsEnabled.Value)
            {
                Active = false;
                return;
            }

            float now = Time.time;
            if (Threat(ac, bankDeg))
                lastThreat = now;
            bool wasActive = Active;
            Active = now - lastThreat < 0.6f;   // short hold-off so it doesn't flicker
            if (!Active)
            {
                callsThisEvent = 0;
                return;
            }
            if (!wasActive || Time.unscaledTime - lastCall > callInterval)
                Announce();
        }

        private bool Threat(Aircraft ac, float bankDeg)
        {
            if (ac.cockpit.IsDetached() || ac.radarAlt < 0.5f) return false;
            Rigidbody rb = ac.CockpitRB();
            if (rb == null) return false;
            Vector3 v = rb.velocity;
            float speed = v.magnitude;
            if (speed < 30f) return false;
            Vector3 dir = v / speed;

            float react = Mathf.Max(cfg.GpwsReactionTime.Value, 0f);
            float n = Mathf.Max(cfg.GpwsPullG.Value, 1.5f);
            float radius = speed * speed / (G * (n - 1f));
            float look = Mathf.Min(speed * (react + 3f) + radius, 10000f);

            // From the gear-bottom reference (the same point the game's radar altitude measures).
            Vector3 origin = ac.transform.position - Vector3.up * Mathf.Max(ac.definition.spawnOffset.y, 0f);
            float dist = float.MaxValue;
            Vector3 normal = Vector3.up;
            if (Datum.WaterPlane().Raycast(new Ray(origin, dir), out float enter) && enter > 0f && enter < look)
                dist = enter;
            if (Physics.Raycast(origin, dir, out RaycastHit hit, look, (int)PhysicsLayers.StaticsMask | (int)PhysicsLayers.ShipsMask)
                && hit.distance < dist)
            {
                dist = hit.distance;
                normal = hit.normal;
            }
            if (dist == float.MaxValue) return false;

            float sinBeta = -Vector3.Dot(dir, normal);   // closing angle with the surface
            if (sinBeta < 0.005f) return false;          // parallel or moving away
            if (ac.gearDeployed && !cfg.GpwsWithGearDown.Value && sinBeta < Mathf.Sin(8f * Mathf.Deg2Rad))
                return false;                             // normal approach / landing

            TimeToImpact = dist / speed;
            float cosBeta = Mathf.Sqrt(Mathf.Max(0f, 1f - sinBeta * sinBeta));
            float rollOut = Mathf.Abs(bankDeg) / RollOutRate;
            float needed = speed * (react + rollOut) * sinBeta + radius * (1f - cosBeta);
            return dist * sinBeta < needed;
        }

        private void Announce()
        {
            lastCall = Time.unscaledTime;
            callInterval = 1.8f;
            callsThisEvent++;

            // 1) user-supplied recording
            AudioClip first = bank.Get(SoundBank.TerrainPullUp) ?? bank.Get(SoundBank.PullUp);
            AudioClip repeat = bank.Get(SoundBank.PullUp) ?? first;
            AudioClip clip = callsThisEvent == 1 ? first : repeat;
            if (cfg.GpwsVoice.Value && clip != null)
            {
                if (recording.isPlaying) return;
                recording.volume = cfg.GpwsVoiceVolume.Value / 100f;
                recording.clip = clip;
                recording.Play();
                // Leave a beat between calls, like the real VWS (~2 s period for "Pull up, pull up").
                callInterval = Mathf.Max(1.8f, clip.length + 0.9f);
                return;
            }

            // 2) Windows text-to-speech, 3) siren
            if (cfg.GpwsVoice.Value && !voiceFailed)
            {
                if (speaking) return;
                Speak(callsThisEvent == 1 ? "Terrain, terrain. Pull up!" : "Pull up! Pull up!");
            }
            else
            {
                whoop.Play();
            }
        }

        private void Speak(string text)
        {
            int rate = cfg.GpwsVoiceRate.Value, volume = cfg.GpwsVoiceVolume.Value;
            speaking = true;
            // SAPI blocks while talking, so run it off the main thread like the game's chat TTS does.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var tts = new WindowsTTS();
                    tts.Speak(rate, volume, text, false);
                    while (tts.IsPlaying()) Thread.Sleep(50);
                }
                catch (Exception e)
                {
                    voiceFailed = true;   // no SAPI voice: fall back to the siren from now on
                    log.LogWarning($"Terrain warning voice unavailable, using siren instead: {e.Message}");
                }
                finally
                {
                    speaking = false;
                }
            });
        }

        /// <summary>Two rising "whoop" sweeps.</summary>
        private static AudioClip MakeWhoop()
        {
            const int rate = 44100;
            const float sweep = 0.38f, gap = 0.06f;
            int per = (int)(rate * (sweep + gap));
            var data = new float[per * 2];
            for (int w = 0; w < 2; w++)
            {
                float phase = 0f;
                for (int i = 0; i < per; i++)
                {
                    float t = i / (float)rate;
                    float s = 0f;
                    if (t < sweep)
                    {
                        float f = Mathf.Lerp(450f, 1250f, t / sweep);
                        phase += 2f * Mathf.PI * f / rate;
                        float env = Mathf.Clamp01(t / 0.01f) * Mathf.Clamp01((sweep - t) / 0.03f);
                        // a little odd-harmonic content so it cuts through engine noise
                        s = 0.28f * env * (Mathf.Sin(phase) + 0.3f * Mathf.Sin(3f * phase));
                    }
                    data[w * per + i] = s;
                }
            }
            AudioClip clip = AudioClip.Create("gpws_whoop", data.Length, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
