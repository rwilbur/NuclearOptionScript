using UnityEngine;

namespace AutopilotMod
{
    /// <summary>Synthesised two-tone "autopilot disconnect" warble, so no audio assets are needed.</summary>
    internal sealed class DisconnectTone
    {
        private readonly AudioSource source;

        public DisconnectTone(GameObject host)
        {
            const int rate = 44100;
            const float seconds = 1.1f, toggle = 0.11f;
            int n = (int)(rate * seconds);
            var data = new float[n];
            float phase = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)rate;
                float f = ((int)(t / toggle) % 2 == 0) ? 1100f : 820f;
                phase += 2f * Mathf.PI * f / rate;
                float env = Mathf.Clamp01(t / 0.01f) * Mathf.Clamp01((seconds - t) / 0.05f);
                data[i] = 0.22f * env * Mathf.Sin(phase);
            }
            AudioClip clip = AudioClip.Create("ap_disconnect", n, 1, rate, false);
            clip.SetData(data, 0);

            source = host.AddComponent<AudioSource>();
            source.clip = clip;
            source.playOnAwake = false;
            source.spatialBlend = 0f;
            source.ignoreListenerPause = false;
        }

        /// <summary>Play the synthesised warble, or <paramref name="recording"/> instead when supplied.</summary>
        public void Play(AudioClip recording = null)
        {
            if (source == null) return;
            if (recording != null) source.PlayOneShot(recording);
            else if (source.clip != null) source.Play();
        }
    }
}
