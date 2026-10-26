using HarmonyLib;

namespace AutopilotMod
{
    /// <summary>
    /// PilotPlayerState.FixedUpdateState() does:  PlayerAxisControls();  aircraft.FilterInputs();
    /// A postfix on PlayerAxisControls therefore sees the pilot's stick (for override detection) and
    /// can replace it before the fly-by-wire runs - the same place the game's own input lands, so
    /// there is no race with other scripts and no OS/gamepad round-trip.
    /// </summary>
    [HarmonyPatch(typeof(PilotPlayerState), "PlayerAxisControls")]
    internal static class PlayerAxisControlsPatch
    {
        private static void Postfix(PilotPlayerState __instance)
        {
            AutopilotPlugin.Instance?.OnPlayerAxisControls(__instance);
        }
    }

    /// <summary>The throttle lever is read in Update (and in FixedUpdate during auto-hover).</summary>
    [HarmonyPatch(typeof(PilotPlayerState), "PlayerThrottleAxis1Controls")]
    internal static class PlayerThrottlePatch
    {
        private static void Postfix(PilotPlayerState __instance)
        {
            AutopilotPlugin.Instance?.OnPlayerThrottle(__instance);
        }
    }
}
