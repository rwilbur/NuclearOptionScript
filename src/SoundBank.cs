using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Networking;

namespace AutopilotMod
{
    /// <summary>
    /// Optional user-supplied recordings, loaded from BepInEx\plugins\AutopilotMod\sounds.
    /// Any clip that is present replaces the built-in voice/tone for that event.
    /// </summary>
    internal sealed class SoundBank
    {
        public const string TerrainPullUp = "terrain_pull_up";
        public const string PullUp = "pull_up";
        public const string ApDisconnect = "ap_disconnect";

        public static readonly string Folder = Path.Combine(Path.Combine(Paths.PluginPath, "AutopilotMod"), "sounds");

        private static readonly (string ext, AudioType type)[] Formats =
        {
            (".wav", AudioType.WAV),
            (".ogg", AudioType.OGGVORBIS),
            (".mp3", AudioType.MPEG),
        };

        private readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();

        public AudioClip Get(string name)
        {
            return clips.TryGetValue(name, out AudioClip c) ? c : null;
        }

        public IEnumerator LoadAll(ManualLogSource log)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string readme = Path.Combine(Folder, "README.txt");
                if (!File.Exists(readme))
                    File.WriteAllText(readme, ReadmeText);
            }
            catch (Exception e)
            {
                log.LogWarning($"Could not create sound folder {Folder}: {e.Message}");
                yield break;
            }

            foreach (string name in new[] { TerrainPullUp, PullUp, ApDisconnect })
            {
                foreach ((string ext, AudioType type) in Formats)
                {
                    string path = Path.Combine(Folder, name + ext);
                    if (!File.Exists(path)) continue;

                    using (UnityWebRequest req = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, type))
                    {
                        yield return req.SendWebRequest();
                        if (req.result != UnityWebRequest.Result.Success)
                        {
                            log.LogWarning($"Could not load {path}: {req.error}");
                            continue;
                        }
                        AudioClip clip = DownloadHandlerAudioClip.GetContent(req);
                        if (clip == null || clip.length <= 0f)
                        {
                            log.LogWarning($"{path} decoded to an empty clip; skipped.");
                            continue;
                        }
                        clip.name = name;
                        clips[name] = clip;
                        log.LogInfo($"Loaded sound '{name}' ({clip.length:F2}s) from {Path.GetFileName(path)}");
                    }
                    break;
                }
            }
        }

        private const string ReadmeText =
@"Drop your own warning recordings in this folder (WAV, OGG or MP3). Restart the game to load them.

  terrain_pull_up.*   first call of a terrain warning   (e.g. ""Terrain, terrain, pull up"")
  pull_up.*           repeated calls while it persists  (e.g. ""Pull up, pull up"")
  ap_disconnect.*     autopilot disconnect tone

Only one of terrain_pull_up / pull_up is needed; the other falls back to it.
Missing files fall back to Windows text-to-speech, then to the built-in siren / tone.
Trim silence off the start of clips so the warning plays instantly.
";
    }
}
