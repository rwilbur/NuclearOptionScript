namespace AutopilotMod
{
    /// <summary>Display units follow the game's own Metric/Imperial setting. Internals stay SI.</summary>
    internal static class Units
    {
        private const float FtPerM = 3.28084f;
        private const float KtPerMs = 1.94384f;
        private const float KmhPerMs = 3.6f;
        private const float FpmPerMs = 196.850f;

        public static bool Metric => PlayerSettings.unitSystem == PlayerSettings.UnitSystem.Metric;

        public static string AltUnit => Metric ? "M" : "FT";
        public static string SpeedUnit => Metric ? "KMH" : "KT";
        public static string VsUnit => Metric ? "M/S" : "FPM";

        public static float Alt(float m) => Metric ? m : m * FtPerM;
        public static float Speed(float ms) => Metric ? ms * KmhPerMs : ms * KtPerMs;
        public static float Vs(float ms) => Metric ? ms : ms * FpmPerMs;

        // Rocker steps, returned in SI so they snap cleanly in the displayed unit.
        public static float AltStep(bool coarse) => Metric ? (coarse ? 500f : 50f) : (coarse ? 1000f : 100f) / FtPerM;
        public static float RaltStep(bool coarse) => Metric ? (coarse ? 10f : 1f) : (coarse ? 50f : 5f) / FtPerM;
        public static float VsStep(bool coarse) => Metric ? (coarse ? 5f : 1f) : (coarse ? 1000f : 100f) / FpmPerMs;
        public static float SpeedStep(bool coarse) => Metric ? (coarse ? 50f : 10f) / KmhPerMs : (coarse ? 50f : 5f) / KtPerMs;
    }
}
