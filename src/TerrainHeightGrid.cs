using Unity.Collections;
using UnityEngine;

namespace AutopilotMod
{
    /// <summary>
    /// Terrain height (m above sea level) for the whole map on a square grid in global x/z, filled
    /// once with batched multi-threaded raycasts against the same Statics layer the game's radar
    /// line-of-sight uses. Terrain never changes, so every radar profile then reads it for free.
    /// </summary>
    internal sealed class TerrainHeightGrid
    {
        private const int BatchPerFrame = 8192;
        private const float TargetCell = 60f, QuarterMetre = 4f;

        public readonly int N;
        public readonly float Cell;
        private readonly float half;
        private readonly ushort[] height; // quarter-metres above sea level, so 2048^2 cells fit in 8 MB
        private int filled;

        public bool Ready => filled >= height.Length;
        public float Progress => filled / (float)height.Length;

        public TerrainHeightGrid(float mapSize)
        {
            N = Mathf.Clamp(Mathf.CeilToInt(mapSize / TargetCell), 64, 2048);
            Cell = mapSize / N;
            half = mapSize * 0.5f;
            height = new ushort[N * N];
        }

        /// <summary>Raycast the next batch of cells. Completed in the same frame, so a
        /// floating-origin shift can't land between scheduling and reading the hits.</summary>
        public void Fill()
        {
            if (Ready) return;
            int count = Mathf.Min(BatchPerFrame, height.Length - filled);
            float sea = Datum.LocalSeaY;
            var query = new QueryParameters(PhysicsLayers.StaticsMask, false, QueryTriggerInteraction.Ignore, false);
            var cmds = new NativeArray<RaycastCommand>(count, Allocator.TempJob);
            var hits = new NativeArray<RaycastHit>(count, Allocator.TempJob);
            try
            {
                for (int k = 0; k < count; k++)
                {
                    int idx = filled + k;
                    Vector3 p = new GlobalPosition((idx % N + 0.5f) * Cell - half, 0f, (idx / N + 0.5f) * Cell - half).ToLocalPosition();
                    p.y = sea + 10000f;
                    cmds[k] = new RaycastCommand(p, Vector3.down, query, 10500f);
                }
                RaycastCommand.ScheduleBatch(cmds, hits, 64, 1).Complete();
                for (int k = 0; k < count; k++)
                {
                    float h = hits[k].collider != null ? hits[k].point.y - sea : 0f;
                    height[filled + k] = (ushort)Mathf.Clamp(Mathf.RoundToInt(h * QuarterMetre), 0, ushort.MaxValue);
                }
                filled += count;
            }
            finally
            {
                cmds.Dispose();
                hits.Dispose();
            }
        }

        /// <summary>Height above sea level at a global x/z, bilinear between cell centres; 0 off the map.</summary>
        public float Height(float gx, float gz)
        {
            float fx = (gx + half) / Cell - 0.5f, fz = (gz + half) / Cell - 0.5f;
            if (fx < -0.5f || fz < -0.5f || fx > N - 0.5f || fz > N - 0.5f) return 0f;
            int ix = Mathf.Clamp((int)fx, 0, N - 2), iz = Mathf.Clamp((int)fz, 0, N - 2);
            float tx = Mathf.Clamp01(fx - ix), tz = Mathf.Clamp01(fz - iz);
            int i = iz * N + ix;
            float a = height[i] + (height[i + 1] - height[i]) * tx;
            float b = height[i + N] + (height[i + N + 1] - height[i + N]) * tx;
            return (a + (b - a) * tz) / QuarterMetre;
        }
    }
}
