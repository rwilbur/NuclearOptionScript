namespace AutopilotMod
{
    /// <summary>Everything the pilot can ask of the autopilot - shared by hotkeys and MFD push-buttons.</summary>
    internal enum ApCommand
    {
        None,
        ApToggle,
        AtToggle,
        ModeHdg,
        ModeAtt,
        ModeBalt,
        ModeRalt,
        ModeVs,
        LatUp,
        LatDown,
        LatSync,
        VertUp,
        VertDown,
        VertSync,
        SpdUp,
        SpdDown,
        SpdSync,
        SyncAll,
        StepToggle,
        DataToggle,
        Hide,

        // radar coverage page
        RdrAll,
        RdrOwn,
        RdrTgt,
        RdrHide,
        RdrZoomIn,
        RdrZoomOut,
        RdrFollow,
        RdrMap,
        RdrEnemy,
    }
}
