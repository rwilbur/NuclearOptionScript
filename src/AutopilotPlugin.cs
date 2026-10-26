using System;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AutopilotMod
{
    [BepInPlugin(Guid, "Autopilot MFD", "2.0.0")]
    public class AutopilotPlugin : BaseUnityPlugin
    {
        public const string Guid = "com.yourname.autopilotmod";

        internal static AutopilotPlugin Instance;

        // Our own bit in the game's cursor-request mask (the game's flags stop at 0x100).
        private const CursorFlags MfdCursorFlag = (CursorFlags)0x4000;
        private const float KeyRepeatDelay = 0.35f, KeyRepeatRate = 0.07f, HoldAccelerateAfter = 1.5f;

        private static readonly AccessTools.FieldRef<PilotBaseState, Pilot> PilotRef =
            AccessTools.FieldRefAccess<PilotBaseState, Pilot>("pilot");

        private ApSettings cfg;
        private FlightController ap;
        private MfdPage mfd;
        private RadarCoveragePage radarPage;
        private WebMirror mirror;
        private DisconnectTone tone;
        private Harmony harmony;

        private Aircraft aircraft;
        private PilotPlayerState playerState;
        private float lastAxisTick = -10f;
        private bool cursorMode;

        private TerrainWarning gpws;
        private SoundBank sounds;

        private ApCommand holdCmd = ApCommand.None;
        private Func<bool> holdStillHeld;
        private float holdSince, holdNext;

        private void Awake()
        {
            Instance = this;
            cfg = new ApSettings(Config);
            ap = new FlightController(cfg, Report);
            mfd = new MfdPage(cfg, ap);
            radarPage = new RadarCoveragePage(cfg);
            mirror = new WebMirror(cfg, Logger, Report, mfd, radarPage);
            sounds = new SoundBank();
            StartCoroutine(sounds.LoadAll(Logger));
            tone = new DisconnectTone(gameObject);
            gpws = new TerrainWarning(cfg, gameObject, sounds, Logger);
            ap.ApDisconnected += () =>
            {
                if (cfg.DisconnectTone.Value) tone.Play(sounds.Get(SoundBank.ApDisconnect));
            };

            harmony = new Harmony(Guid);
            harmony.PatchAll(typeof(AutopilotPlugin).Assembly);
            Logger.LogInfo($"Autopilot MFD ready. {cfg.KeyApToggle.Value} = A/P, {cfg.KeyAtToggle.Value} = A/T, " +
                           $"{cfg.KeyMfdToggle.Value} = show/hide MFD, {cfg.KeyCursorToggle.Value} = MFD cursor.");
        }

        private void OnDestroy()
        {
            SetCursorMode(false);
            mirror?.Dispose();
            harmony?.UnpatchSelf();
            if (Instance == this) Instance = null;
        }

        /// <summary>True while the local player is actually flying (PilotPlayerState is ticking).</summary>
        private bool Flying => aircraft != null && Time.time - lastAxisTick < 0.5f;

        // =====================================================================
        // Harmony callbacks (physics tick / input read)
        // =====================================================================
        internal void OnPlayerAxisControls(PilotPlayerState pps)
        {
            Aircraft ac = LocalAircraftOf(pps);
            if (ac == null) return;
            playerState = pps;
            lastAxisTick = Time.time;
            ap.FixedTick(ac, pps, ac.GetInputs(), cursorMode);
            gpws.Tick(ac, ap.State.Bank);
        }

        internal void OnPlayerThrottle(PilotPlayerState pps)
        {
            Aircraft ac = LocalAircraftOf(pps);
            if (ac == null) return;
            ap.OnPlayerThrottle(ac, pps, ac.GetInputs());
        }

        private Aircraft LocalAircraftOf(PilotPlayerState pps)
        {
            Pilot pilot = PilotRef(pps);
            if (pilot == null) return null;
            Aircraft ac = pilot.aircraft;
            return ac != null && ac == aircraft ? ac : null;
        }

        // =====================================================================
        // Frame update: aircraft binding + hotkeys
        // =====================================================================
        private void Update()
        {
            mirror.Update();
            Aircraft local = GameManager.GetLocalAircraft(out Aircraft a) ? a : null;
            if (local != aircraft)
            {
                ap.Reset();
                gpws.Reset();
                radarPage.Reset();
                aircraft = local;
                playerState = null;
                holdCmd = ApCommand.None;
                SetCursorMode(false);
            }
            if (aircraft == null) return;

            if (!Flying)
            {
                // Pilot left the seat (parked, ejected, menu flow): drop everything quietly.
                if (ap.ApEngaged || ap.AtEngaged) ap.Reset();
                gpws.Reset();
                return;
            }

            UpdateHold();
            radarPage.Tick(aircraft, radarPage.Visible || mirror.Watching(1));

            if (cursorMode && !cfg.MfdVisible.Value) SetCursorMode(false);

            if (!CursorManager.GetFlag(CursorFlags.Chat) && !CursorManager.GetFlag(CursorFlags.GameMenu))
                HandleKeys();

            // While clicking the MFD with mouse-flight enabled, don't let the cursor drag the
            // virtual stick around underneath the autopilot.
            if (cursorMode && ap.ApEngaged && PlayerSettings.virtualJoystickEnabled && SceneSingleton<FlightHud>.i != null)
                SceneSingleton<FlightHud>.i.SetVirtualJoystick(Vector3.zero);
        }

        private void HandleKeys()
        {
            bool coarse = CoarseHeld();

            if (Pressed(cfg.KeyApToggle)) Execute(ApCommand.ApToggle, coarse);
            if (Pressed(cfg.KeyAtToggle)) Execute(ApCommand.AtToggle, coarse);
            if (Pressed(cfg.KeyMfdToggle))
            {
                cfg.MfdVisible.Value = !cfg.MfdVisible.Value;
                if (!cfg.MfdVisible.Value) SetCursorMode(false);
            }
            if (Pressed(cfg.KeyRadarPage)) radarPage.CycleMode();
            if (Pressed(cfg.KeyCursorToggle))
            {
                if (!cfg.MfdVisible.Value) cfg.MfdVisible.Value = true;
                SetCursorMode(!cursorMode);
            }

            RockerKey(cfg.KeyVertUp, ApCommand.VertUp, coarse);
            RockerKey(cfg.KeyVertDown, ApCommand.VertDown, coarse);
            RockerKey(cfg.KeyHdgRight, ApCommand.LatUp, coarse);
            RockerKey(cfg.KeyHdgLeft, ApCommand.LatDown, coarse);
            RockerKey(cfg.KeySpdUp, ApCommand.SpdUp, coarse);
            RockerKey(cfg.KeySpdDown, ApCommand.SpdDown, coarse);
        }

        /// <summary>
        /// Like KeyboardShortcut.IsDown(), but extra modifiers (Shift for the coarse step) don't block it.
        /// </summary>
        private static bool Pressed(ConfigEntry<KeyboardShortcut> entry)
        {
            KeyboardShortcut sc = entry.Value;
            return sc.MainKey != KeyCode.None && Input.GetKeyDown(sc.MainKey) && sc.Modifiers.All(Input.GetKey);
        }

        private bool CoarseHeld()
        {
            KeyCode k = cfg.KeyCoarseStep.Value;
            if (k == KeyCode.LeftShift || k == KeyCode.RightShift)
                return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            return k != KeyCode.None && Input.GetKey(k);
        }

        private void RockerKey(ConfigEntry<KeyboardShortcut> entry, ApCommand cmd, bool coarse)
        {
            if (!Pressed(entry)) return;
            KeyCode key = entry.Value.MainKey;
            BeginHold(cmd, () => Input.GetKey(key), coarse);
        }

        private static bool IsRocker(ApCommand cmd)
        {
            switch (cmd)
            {
                case ApCommand.LatUp:
                case ApCommand.LatDown:
                case ApCommand.VertUp:
                case ApCommand.VertDown:
                case ApCommand.SpdUp:
                case ApCommand.SpdDown:
                    return true;
                default:
                    return false;
            }
        }

        // ---- press-and-hold auto-repeat for rockers (keyboard and MFD buttons alike) ----
        // One step on press; after KeyRepeatDelay it repeats every KeyRepeatRate, and once held for
        // HoldAccelerateAfter it switches to the coarse (10x) step so big changes don't take forever.
        private void BeginHold(ApCommand cmd, Func<bool> stillHeld, bool coarse)
        {
            holdCmd = cmd;
            holdStillHeld = stillHeld;
            holdSince = Time.unscaledTime;
            holdNext = holdSince + KeyRepeatDelay;
            Execute(cmd, coarse);
        }

        private void UpdateHold()
        {
            if (holdCmd == ApCommand.None) return;
            if (!holdStillHeld())
            {
                holdCmd = ApCommand.None;
                return;
            }
            float now = Time.unscaledTime;
            if (now < holdNext) return;
            holdNext = now + KeyRepeatRate;
            bool coarse = CoarseHeld() || now - holdSince > HoldAccelerateAfter;
            Execute(holdCmd, coarse);
        }

        // =====================================================================
        // Commands (shared by hotkeys and OSBs)
        // =====================================================================
        private void Execute(ApCommand cmd, bool coarse)
        {
            if (aircraft == null) return;
            mfd.Flash(cmd);
            radarPage.Flash(cmd);
            switch (cmd)
            {
                case ApCommand.ApToggle: ap.SetAutopilot(aircraft, !ap.ApEngaged); break;
                case ApCommand.AtToggle: ap.SetAutothrottle(aircraft, playerState, !ap.AtEngaged); break;
                case ApCommand.ModeHdg: ap.SelectLateral(LateralMode.Hdg); break;
                case ApCommand.ModeAtt: ap.SelectLateral(LateralMode.Att); break;
                case ApCommand.ModeBalt: ap.SelectVertical(VerticalMode.Balt); break;
                case ApCommand.ModeRalt: ap.SelectVertical(VerticalMode.Ralt); break;
                case ApCommand.ModeVs: ap.SelectVertical(VerticalMode.Vs); break;
                case ApCommand.LatUp: ap.NudgeLateral(+1, coarse); break;
                case ApCommand.LatDown: ap.NudgeLateral(-1, coarse); break;
                case ApCommand.LatSync: ap.SyncLateral(); break;
                case ApCommand.VertUp: ap.NudgeVertical(+1, coarse); break;
                case ApCommand.VertDown: ap.NudgeVertical(-1, coarse); break;
                case ApCommand.VertSync: ap.SyncVertical(); break;
                case ApCommand.SpdUp: ap.NudgeSpeed(+1, coarse); break;
                case ApCommand.SpdDown: ap.NudgeSpeed(-1, coarse); break;
                case ApCommand.SpdSync: ap.SyncSpeed(); break;
                case ApCommand.SyncAll: ap.SyncAll(); break;
                case ApCommand.StepToggle: ap.CoarseStep = !ap.CoarseStep; break;
                case ApCommand.DataToggle: cfg.ShowDebug.Value = !cfg.ShowDebug.Value; break;
                case ApCommand.RdrAll:
                case ApCommand.RdrOwn:
                case ApCommand.RdrTgt:
                case ApCommand.RdrHide:
                case ApCommand.RdrZoomIn:
                case ApCommand.RdrZoomOut:
                case ApCommand.RdrFollow:
                case ApCommand.RdrMap:
                case ApCommand.RdrEnemy:
                    radarPage.Command(cmd);
                    break;
                case ApCommand.Hide:
                    cfg.MfdVisible.Value = false;
                    SetCursorMode(false);
                    break;
            }
        }

        private void Report(string text, bool warning)
        {
            mfd.ShowMessage(text, warning);
            AircraftActionsReport hud = SceneSingleton<AircraftActionsReport>.i;
            if (hud != null)
                hud.ReportText(warning ? $"<color=#FFBF33>{text}</color>" : text, warning ? 5f : 3f);
        }

        private void SetCursorMode(bool on)
        {
            if (cursorMode == on) return;
            cursorMode = on;
            CursorManager.SetFlag(MfdCursorFlag, on);
        }

        // =====================================================================
        // MFD
        // =====================================================================
        private void OnGUI()
        {
            if (!Flying) return;
            if (CursorManager.GetFlag(CursorFlags.GameMenu) || CursorManager.GetFlag(CursorFlags.Loading)) return;
            if (gpws.Active)
                mfd.DrawPullUp();
            if (radarPage.Visible) radarPage.OnGUI(cmd => Execute(cmd, false));
            if (cfg.MfdVisible.Value)
                mfd.OnGUI(cmd =>
                {
                    if (IsRocker(cmd)) BeginHold(cmd, () => Input.GetMouseButton(0), false);
                    else Execute(cmd, false);
                });
            // after the in-game draws: the mirror re-lays out the pages for its own render textures
            mirror.OnGUI(cmd => Execute(cmd, false));
        }
    }
}
