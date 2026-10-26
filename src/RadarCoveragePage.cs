using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace AutopilotMod
{
    /// <summary>
    /// Radar-coverage MFD page over the game's own map image, north-up. ALL shows every friendly
    /// radar; OWN shows only your jet's radar inside its scan cone. Green = a target at the TGT
    /// height above the ground is in range, inside the radar's cone, and in line of sight.
    /// Red = in range but hidden behind terrain. Red triangles/squares = enemy contacts the game map
    /// shows (same map filters), pointing along their map heading.
    /// ENMY shows enemy radars at the positions your side knows (its tracking data), colours
    /// swapped: red = the enemy sees a target there, green = hidden behind terrain.
    /// A jammed radar (Radar.IsJammed: it detects nothing) adds no coverage; its site turns amber.
    ///
    ///            T1 ALL   T2 OWN   T3 ENMY  T4 TGT   T5 HIDE
    ///   L1 FOLW  +--------------------------------------+  R1 -
    ///   L2 MAP   |                                      |  R2 ^ (zoom in)
    ///            |  map + coverage (scroll = zoom,      |  R3 RNG
    ///            |  drag = pan)                         |  R4 v (zoom out)
    ///            +--------------------------------------+
    ///
    /// The map's terrain is raycast once into a TerrainHeightGrid. Each radar's polar profile
    /// (Az bearings x Steps range bins) is read from that grid over a few frames, and redone when
    /// the radar moves 2% of its range. The overlay is redrawn only when something changed, a few
    /// radars per frame.
    /// </summary>
    internal sealed class RadarCoveragePage : MfdFrame
    {
        public enum Mode { All, Own, Enemy }

        private const int Az = 360, Steps = 200, Tex = 512;
        private const float MinRange = 2000f;
        private static readonly float[] AglsM = { 5f, 30f, 150f, 500f, 2000f };

        private static readonly AccessTools.FieldRef<Radar, float> RadarConeRef =
            AccessTools.FieldRefAccess<Radar, float>("radarCone");

        private static readonly Color32 Covered = new Color32(60, 230, 90, 95);
        private static readonly Color32 Masked = new Color32(240, 50, 40, 130);
        private static readonly Color32 None = new Color32(0, 0, 0, 0);
        private static readonly Color SiteCol = new Color(0.4f, 0.75f, 1f, 1f);
        private static readonly Color EnemySiteCol = new Color(1f, 0.3f, 0.25f, 1f);
        private static readonly Color LegendBack = new Color(0f, 0f, 0f, 0.6f);

        private static readonly Osb[] RdrButtons =
        {
            new Osb { Side = Side.Top, Index = 0, Command = ApCommand.RdrAll, Glyph = Glyph.Text },
            new Osb { Side = Side.Top, Index = 1, Command = ApCommand.RdrOwn, Glyph = Glyph.Text },
            new Osb { Side = Side.Top, Index = 2, Command = ApCommand.RdrEnemy, Glyph = Glyph.Text },
            new Osb { Side = Side.Top, Index = 3, Command = ApCommand.RdrTgt, Glyph = Glyph.Text },
            new Osb { Side = Side.Top, Index = 4, Command = ApCommand.RdrHide, Glyph = Glyph.Text },
            new Osb { Side = Side.Left, Index = 0, Command = ApCommand.RdrFollow, Glyph = Glyph.Text },
            new Osb { Side = Side.Left, Index = 1, Command = ApCommand.RdrMap, Glyph = Glyph.Text },
            new Osb { Side = Side.Right, Index = 1, Command = ApCommand.RdrZoomIn, Glyph = Glyph.Up },
            new Osb { Side = Side.Right, Index = 3, Command = ApCommand.RdrZoomOut, Glyph = Glyph.Down },
        };

        protected override Osb[] Buttons => RdrButtons;

        private sealed class Profile
        {
            public TargetDetector Radar;
            public GlobalPosition Origin;     // scan point the profile was taken from
            public float Range, Alt;          // Alt = scan point height above sea level
            public bool Valid, Jammed, Enemy;
            public float ConeHeading = float.NaN; // enemy: heading off its map icon (deg); NaN = unknown
            public float ConePitch = float.NaN;   // enemy: antenna pitch while that heading is live, then the last one (deg); NaN = never known
            public float[] Height = new float[Az * Steps]; // terrain height above sea level at each bin's far edge (m)
            public float[] Before = new float[Az * Steps]; // steepest terrain slope seen from the radar inside the bin, short of its far edge
            public readonly byte[] Vis = new byte[Az * Steps];
            public float VisAgl = -1f;        // target height Vis was computed for; -1 = stale

            // Sweep in progress: written into back buffers over several frames, then swapped in.
            public GlobalPosition NextOrigin;
            public float NextRange, NextAlt;
            public int RaysDone = Az;
            public float[] NextHeight = new float[Az * Steps], NextBefore = new float[Az * Steps];
        }

        private const int SampleBudget = 50000; // terrain lookups per frame for profile sweeps

        private readonly ApSettings cfg;
        private readonly Dictionary<TargetDetector, Profile> profiles = new Dictionary<TargetDetector, Profile>();
        private TerrainHeightGrid grid;
        private float nextScan, nextBuild;
        private bool dirty = true;

        // The overlay is rasterised a few radars per frame into `state`, then uploaded in one go.
        private const int PixelBudget = 60000;
        private readonly List<Profile> building = new List<Profile>();
        private int buildIdx;
        private bool buildActive;
        private float bX, bZ, bR;                    // view being built
        private bool bEnemy;

        private readonly byte[] state = new byte[Tex * Tex]; // 0 none, 1 masked, 2 covered
        private readonly Color32[] pixels = new Color32[Tex * Tex];
        private Texture2D overlay;

        // Enemy contacts as the game map shows them, re-filtered a few times a second.
        private struct Contact
        {
            public Unit Unit;
            public TrackingInfo Track;
            public Image Icon;       // the map icon's image: sprite, colour and rotation (= map heading)
            public bool Orient;
        }
        private readonly List<Contact> contacts = new List<Contact>();
        private float nextContacts;
        private static readonly Dictionary<Texture2D, Texture2D> iconAlpha = new Dictionary<Texture2D, Texture2D>();

        /// <summary>
        /// Most map icons are DXT1 (white shape on opaque black; the map's shader hides the black).
        /// Bake a copy once per texture: white, with the shape's brightness as alpha.
        /// </summary>
        private static void BakeIcon(Texture2D src)
        {
            if (src == null || iconAlpha.ContainsKey(src)) return;
            int w = src.width, h = src.height;
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(src, rt); // GPU copy: the source isn't CPU-readable
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            var t = new Texture2D(w, h, TextureFormat.RGBA32, true) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
            t.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            Color32[] px = t.GetPixels32();
            for (int i = 0; i < px.Length; i++)
            {
                Color32 c = px[i];
                px[i] = new Color32(255, 255, 255, (byte)(Mathf.Max(c.r, Mathf.Max(c.g, c.b)) * c.a / 255));
            }
            t.SetPixels32(px);
            t.Apply(true, true); // mipmaps keep small icons smooth
            iconAlpha[src] = t;
        }

        private MapSettings mapSettings;
        private Aircraft me;

        public Mode Current = Mode.All;
        public bool Visible;                         // drawn on the game screen (the web mirror shows it regardless)
        private bool follow = true, panning;
        private float viewX, viewZ, viewR = 40000f;  // view centre (global x/z) and half-width (m)
        private float texX, texZ, texR;              // view the overlay texture was built for
        private Vector2 lastMouse;
        private bool ownRadarMissing;
        private int siteCount, jammedCount;

        public RadarCoveragePage(ApSettings cfg) : base(cfg.RadarSize, cfg.RadarX, cfg.RadarY)
        {
            this.cfg = cfg;
            if (System.Array.IndexOf(AglsM, cfg.RadarTargetAgl.Value) < 0) cfg.RadarTargetAgl.Value = 150f;
        }

        /// <summary>F9: hidden -> ALL -> OWN -> ENMY -> hidden.</summary>
        public void CycleMode()
        {
            if (!Visible)
            {
                Visible = true;
                Current = Mode.All;
            }
            else if (Current == Mode.All) Current = Mode.Own;
            else if (Current == Mode.Own) Current = Mode.Enemy;
            else Visible = false;
            dirty = true;
            nextScan = 0f; // friendly <-> enemy swaps the radar set
        }

        public void Reset()
        {
            profiles.Clear();
            building.Clear();
            contacts.Clear();
            buildActive = false;
            me = null;
            nextScan = 0f;
            dirty = true;
        }

        public void Command(ApCommand cmd)
        {
            switch (cmd)
            {
                case ApCommand.RdrAll: Current = Mode.All; nextScan = 0f; break;
                case ApCommand.RdrOwn: Current = Mode.Own; nextScan = 0f; break;
                case ApCommand.RdrEnemy: Current = Mode.Enemy; nextScan = 0f; break;
                case ApCommand.RdrHide: Visible = false; break;
                case ApCommand.RdrTgt:
                    int i = System.Array.IndexOf(AglsM, cfg.RadarTargetAgl.Value);
                    cfg.RadarTargetAgl.Value = AglsM[(i + 1) % AglsM.Length];
                    break;
                case ApCommand.RdrZoomIn: Zoom(0.5f); break;
                case ApCommand.RdrZoomOut: Zoom(2f); break;
                case ApCommand.RdrFollow: follow = true; break;
                case ApCommand.RdrMap:
                    follow = false;
                    viewX = viewZ = 0f; // global origin is the map centre
                    viewR = MapHalfSize() * 1.02f;
                    break;
            }
            dirty = true;
        }

        private float MapHalfSize() => mapSettings != null ? Mathf.Max(mapSettings.MapSize.x, mapSettings.MapSize.y) * 0.5f : 100000f;

        private void Zoom(float k) => viewR = Mathf.Clamp(viewR * k, MinRange, MapHalfSize() * 1.5f);

        // =====================================================================
        // Update: map the terrain once, keep radar profiles current, rasterise the overlay
        // =====================================================================
        /// <param name="active">false = nobody is looking (hidden in-game and not mirrored): do nothing.</param>
        public void Tick(Aircraft aircraft, bool active)
        {
            me = aircraft;
            if (!active) return;
            float now = Time.unscaledTime;
            if (now >= nextScan)
            {
                nextScan = now + 1f;
                if (mapSettings == null) // Unity null also after a map unload
                {
                    mapSettings = Object.FindObjectOfType<MapSettings>();
                    grid = null;
                    profiles.Clear();
                }
                if (mapSettings != null && grid == null && MapHalfSize() > 0f)
                    grid = new TerrainHeightGrid(MapHalfSize() * 2f);
                CollectRadars();
            }
            if (now >= nextContacts)
            {
                nextContacts = now + 0.25f;
                CollectContacts();
                jammedCount = 0;
                // enemy jam state only when the game map shows it too (its jammed markers)
                MapOptions mo = SceneSingleton<MapOptions>.i;
                bool showJam = mo != null && mo.showJamming, enemyView = Current == Mode.Enemy;
                foreach (Profile p in profiles.Values)
                {
                    bool shown = p.Enemy == enemyView;
                    bool j = (!p.Enemy || showJam) && p.Radar is Radar rr && rr.IsJammed();
                    if (j != p.Jammed && shown) dirty = true;
                    p.Jammed = j;
                    if (j && shown) jammedCount++;

                    // kept up in every mode so the last known pitch survives leaving ENMY
                    if (p.Enemy) UpdateEnemyCone(p);
                }
            }

            if (grid != null && !grid.Ready) grid.Fill();
            else if (grid != null)
            {
                // Own radar first so the OWN page fills quickly.
                int budget = SampleBudget;
                if (me.radar != null && profiles.TryGetValue(me.radar, out Profile own))
                    budget -= Advance(own, budget);
                foreach (Profile p in profiles.Values)
                {
                    if (budget <= 0) break;
                    budget -= Advance(p, budget);
                }
            }

            if (follow)
            {
                GlobalPosition g = me.GlobalPosition();
                viewX = g.x;
                viewZ = g.z;
            }

            if (buildActive) ContinueBuild();
            else if (now >= nextBuild && (dirty || ViewMoved()))
            {
                nextBuild = now + 0.25f;
                StartBuild();
            }
        }

        // The overlay is built Margin x wider than the view and drawn where it was built, so panning
        // (and follow mode) only needs a rebuild once the view nears the overlay's edge.
        private const float Margin = 1.4f;

        private bool ViewMoved()
        {
            float slack = viewR * 0.15f;
            return viewR * Margin != texR || Mathf.Abs(viewX - texX) > slack || Mathf.Abs(viewZ - texZ) > slack;
        }

        private void CollectRadars()
        {
            // radar -> scan point to sweep from; enemy radars sit where our side last saw them
            var live = new Dictionary<TargetDetector, GlobalPosition>();
            bool enemy = Current == Mode.Enemy;
            siteCount = 0;
            foreach (Unit u in UnitRegistry.allUnits)
            {
                TargetDetector r = u != null ? u.radar : null;
                if (r == null || u.disabled || r.GetRadarRange() <= 0f || r.GetScanPoint() == null) continue;
                GlobalPosition scan = r.GetScanPoint().GlobalPosition();
                bool isOwn = u == me;
                if (enemy)
                {
                    // activated is ignored (worst case: on). Damage is all-or-nothing in the game: a radar
                    // knocked out destroys its component, so u.radar goes null above and the site drops off.
                    // Only the antenna height (fixed per unit type) is added to the known position.
                    if (u.NetworkHQ == null || me.NetworkHQ == null || u.NetworkHQ == me.NetworkHQ || !r.IsOperational()
                        || !me.NetworkHQ.TryGetKnownPosition(u, out GlobalPosition known)) continue;
                    scan = known + new Vector3(0f, (scan - u.GlobalPosition()).y, 0f);
                }
                else if (u.NetworkHQ != me.NetworkHQ || (!isOwn && (!r.activated || !r.IsOperational()))) continue;
                live[r] = scan;
                if (!isOwn) siteCount++;
            }
            ownRadarMissing = me.radar == null || !live.ContainsKey(me.radar);

            foreach (KeyValuePair<TargetDetector, GlobalPosition> kv in live)
            {
                TargetDetector r = kv.Key;
                if (!profiles.TryGetValue(r, out Profile p) || p.Enemy != enemy) // new, or changed side
                {
                    p = new Profile { Radar = r, Enemy = enemy };
                    profiles[r] = p;
                    TakeProfile(p, kv.Value);
                }
                else if (p.RaysDone >= Az)
                {
                    // Redo the profile once the radar has moved 2% of its range (others' 5%: many moving
                    // friendly radars would otherwise keep sweeping every frame), or its range changed.
                    float moved = (kv.Value - p.Origin).magnitude;
                    if (moved > p.Range * (r == me.radar ? 0.02f : 0.05f) || Mathf.Abs(r.GetRadarRange() - p.Range) > 1f) TakeProfile(p, kv.Value);
                }
            }
            // The other side's profiles are kept (switching modes would otherwise reallocate ~1 MB per
            // radar in one frame); they go once their radar is destroyed.
            var dead = new List<TargetDetector>();
            foreach (KeyValuePair<TargetDetector, Profile> kv in profiles)
                if (kv.Key == null || (kv.Value.Enemy == enemy && !live.ContainsKey(kv.Key))) dead.Add(kv.Key);
            foreach (TargetDetector r in dead) profiles.Remove(r);
            if (dead.Count > 0) dirty = true;
        }

        /// <summary>
        /// Enemy units the game map has an icon for, minus what the map hides (pilot toggle) or its
        /// target filters exclude.
        /// </summary>
        private void CollectContacts()
        {
            contacts.Clear();
            DynamicMap map = SceneSingleton<DynamicMap>.i;
            if (map == null) return;
            TargetListSelector filter = SceneSingleton<TargetListSelector>.i;
            foreach (MapIcon icon in map.mapIcons)
            {
                if (!(icon is UnitMapIcon ui) || !TargetPanel.ShownEnemy(ui, filter)) continue;
                Unit u = ui.unit;
                if (ui.iconImage.sprite != null) BakeIcon(ui.iconImage.sprite.texture);
                contacts.Add(new Contact
                {
                    Unit = u,
                    Track = map.HQ != null ? map.HQ.GetTrackingData(u.persistentID) : null,
                    Icon = ui.iconImage,
                    Orient = u.definition.mapOrient,
                });
            }
        }

        /// <summary>
        /// Enemy cone aim from what the map gives: heading off the icon (which the map freezes once the
        /// track is stale), and antenna pitch only while that heading is live, else the last pitch seen.
        /// </summary>
        private void UpdateEnemyCone(Profile p)
        {
            Unit u = p.Radar.GetAttachedUnit();
            float h = MapHeading(u), pitch = p.ConePitch;
            TrackingInfo t = u != null && me.NetworkHQ != null ? me.NetworkHQ.GetTrackingData(u.persistentID) : null;
            Transform scan = p.Radar.GetScanPoint();
            if (!float.IsNaN(h) && t != null && t.Observed() && scan != null)
                pitch = Mathf.Asin(Mathf.Clamp(scan.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
            if (Differs(h, p.ConeHeading) || Differs(pitch, p.ConePitch))
            {
                p.ConeHeading = h;
                p.ConePitch = pitch;
                if (Current == Mode.Enemy) dirty = true;
            }
        }

        private static bool Differs(float a, float b) =>
            float.IsNaN(a) != float.IsNaN(b) || Mathf.Abs(Mathf.DeltaAngle(a, b)) > 3f;

        /// <summary>Heading the game map's icon shows for a unit (deg from north), NaN if the map shows none.</summary>
        private static float MapHeading(Unit u)
        {
            DynamicMap map = SceneSingleton<DynamicMap>.i;
            if (u == null || map == null || map.mapImage == null || !u.definition.mapOrient
                || !DynamicMap.TryGetMapIcon(u, out UnitMapIcon icon) || icon.iconImage == null) return float.NaN;
            return map.mapImage.transform.eulerAngles.z - icon.iconImage.transform.eulerAngles.z;
        }

        /// <summary>Start a new sweep from the radar's scan point (true or known position).</summary>
        private static void TakeProfile(Profile p, GlobalPosition origin)
        {
            p.NextOrigin = origin;
            p.NextRange = p.Radar.GetRadarRange();
            p.NextAlt = p.NextOrigin.ToLocalPosition().y - Datum.LocalSeaY;
            p.RaysDone = 0;
        }

        /// <summary>
        /// Continue a sweep from the height grid (no raycasts). Each range bin stores the terrain at
        /// its far edge plus the steepest slope of grid-spaced samples inside it, so ridges narrower
        /// than a bin still mask what lies behind them. Returns the lookups used.
        /// </summary>
        private int Advance(Profile p, int budget)
        {
            if (p.RaysDone >= Az) return 0;
            float step = p.NextRange / Steps;
            int fine = Mathf.Clamp(Mathf.CeilToInt(step / grid.Cell), 1, 32);
            float sub = step / fine, ox = p.NextOrigin.x, oz = p.NextOrigin.z, alt = p.NextAlt;
            int used = 0;
            while (used < budget && p.RaysDone < Az)
            {
                int a = p.RaysDone++, b = a * Steps;
                float brg = (a + 0.5f) * (2f * Mathf.PI / Az), sx = Mathf.Sin(brg), sz = Mathf.Cos(brg);
                for (int i = 0; i < Steps; i++)
                {
                    float before = float.NegativeInfinity, d0 = i * step;
                    for (int j = 1; j < fine; j++)
                    {
                        float d = d0 + j * sub;
                        before = Mathf.Max(before, (grid.Height(ox + sx * d, oz + sz * d) - alt) / d);
                    }
                    float de = d0 + step;
                    p.NextHeight[b + i] = grid.Height(ox + sx * de, oz + sz * de);
                    p.NextBefore[b + i] = before;
                }
                used += Steps * fine;
            }
            if (p.RaysDone >= Az)
            {
                (p.Height, p.NextHeight) = (p.NextHeight, p.Height);
                (p.Before, p.NextBefore) = (p.NextBefore, p.Before);
                p.Origin = p.NextOrigin;
                p.Range = p.NextRange;
                p.Alt = p.NextAlt;
                p.Valid = true;
                p.VisAgl = -1f;
                dirty = true;
            }
            return used;
        }

        /// <summary>
        /// Line-of-sight along each bearing: a target at terrain + agl at the far edge of bin i is
        /// visible when its elevation angle from the radar clears the steepest terrain before it
        /// (earlier bins, plus samples inside bin i) and is above the radar horizon. vis: 1 = masked, 2 = visible.
        /// </summary>
        internal static void Viewshed(float[] height, float[] before, float radarAlt, float agl, float step, byte[] vis)
        {
            // The map is flat, but the game (DetectorManager.RequestRadarCheck) drops targets past the
            // curved-earth horizon: sqrt(2R*radarASL) + sqrt(2R*targetASL) < horizontal distance.
            const float TwoR = 12742000f;
            float radarHorizon = Mathf.Sqrt(TwoR * Mathf.Max(0f, radarAlt));
            for (int a = 0; a < Az; a++)
            {
                int b = a * Steps;
                float maxSlope = float.NegativeInfinity;
                for (int i = 0; i < Steps; i++)
                {
                    float d = (i + 1) * step, t = height[b + i];
                    maxSlope = Mathf.Max(maxSlope, before[b + i]);
                    bool aboveHorizon = radarHorizon + Mathf.Sqrt(TwoR * Mathf.Max(0f, t + agl)) >= d;
                    vis[b + i] = aboveHorizon && (t + agl - radarAlt) / d >= maxSlope ? (byte)2 : (byte)1;
                    maxSlope = Mathf.Max(maxSlope, (t - radarAlt) / d);
                }
            }
        }

        private void StartBuild()
        {
            dirty = false;
            bX = viewX;
            bZ = viewZ;
            bR = viewR * Margin;
            bEnemy = Current == Mode.Enemy;
            System.Array.Clear(state, 0, state.Length);
            building.Clear();
            foreach (Profile p in profiles.Values)
                if (p.Valid && !p.Jammed && p.Enemy == bEnemy && (Current != Mode.Own || p.Radar == me.radar)) building.Add(p);
            buildIdx = 0;
            buildRow = -1;
            buildActive = true;
            ContinueBuild();
        }

        private void ContinueBuild()
        {
            int budget = PixelBudget;
            while (budget > 0 && buildIdx < building.Count)
            {
                budget -= Rasterize(building[buildIdx], budget);
                if (buildRow < 0) buildIdx++; // that radar is done
            }
            if (buildIdx < building.Count) return;

            // enemy view: seen by them is the danger (red), terrain-masked is safe (green)
            Color32 seen = bEnemy ? Masked : Covered, hidden = bEnemy ? Covered : Masked;
            for (int k = 0; k < pixels.Length; k++)
                pixels[k] = state[k] == 2 ? seen : state[k] == 1 ? hidden : None;
            if (overlay == null)
                overlay = new Texture2D(Tex, Tex, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
            overlay.SetPixels32(pixels); // row 0 = south, so the texture draws north-up
            overlay.Apply();
            texX = bX;
            texZ = bZ;
            texR = bR;
            buildActive = false;
        }

        private int buildRow = -1; // row to resume the current radar at; -1 = start the next radar

        /// <summary>
        /// Merge one radar's coverage into `state`, a budget of pixels at a time (one long-range
        /// radar can cover the whole texture). Returns the pixels it looked at; buildRow is -1
        /// once the radar is finished.
        /// </summary>
        private int Rasterize(Profile p, int budget)
        {
            Transform scan = p.Radar != null ? p.Radar.GetScanPoint() : null;
            int resume = buildRow;
            buildRow = -1;
            if (scan == null) return 0;
            float half = Tex * 0.5f, mPerPx = bR / half, agl = cfg.RadarTargetAgl.Value;
            float radarAlt = p.Alt;

            // Scan cone (half-angle, 0 = all round) is checked in 3D, so it also covers the
            // radar's up/down limits for a target at that spot's terrain + agl.
            float cone = p.Radar is Radar rr ? RadarConeRef(rr) : 0f;
            Vector3 fwd = scan.forward;
            bool flat = false, elevOnly = false; // flat = azimuth-only check; elevOnly = pitch band, any azimuth
            if (bEnemy)
            {
                // Enemy: heading + pitch -> full cone; heading only -> azimuth check; pitch only (heading
                // lost) -> the band of elevations the cone can reach turned any way; neither -> all-round.
                bool hKnown = !float.IsNaN(p.ConeHeading), pKnown = !float.IsNaN(p.ConePitch);
                float hr = p.ConeHeading * Mathf.Deg2Rad, pr = pKnown ? p.ConePitch * Mathf.Deg2Rad : 0f;
                if (hKnown) fwd = new Vector3(Mathf.Sin(hr) * Mathf.Cos(pr), Mathf.Sin(pr), Mathf.Cos(hr) * Mathf.Cos(pr));
                flat = hKnown && !pKnown;
                elevOnly = !hKnown && pKnown;
                if (!hKnown && !pKnown) cone = 0f;
            }

            // Only touch pixels inside this radar's range box.
            float ox = (p.Origin.x - bX) / mPerPx + half, oy = (p.Origin.z - bZ) / mPerPx + half, rp = p.Range / mPerPx;
            int x0 = Mathf.Max(0, (int)(ox - rp)), x1 = Mathf.Min(Tex - 1, (int)(ox + rp));
            int y0 = Mathf.Max(0, (int)(oy - rp)), y1 = Mathf.Min(Tex - 1, (int)(oy + rp));
            if (x1 < x0 || y1 < y0) return 0;
            if (p.VisAgl != agl) // only for radars in view: it isn't counted against the pixel budget
            {
                Viewshed(p.Height, p.Before, radarAlt, agl, p.Range / Steps, p.Vis);
                p.VisAgl = agl;
            }
            int y = Mathf.Max(y0, resume), used = 0;
            float binsPerM = Steps / p.Range, azPerRad = Az / (2f * Mathf.PI), r2 = p.Range * p.Range;
            for (; y <= y1 && used < budget; y++, used += x1 - x0 + 1)
            {
                float dz = (y + 0.5f - oy) * mPerPx;
                for (int x = x0; x <= x1; x++)
                {
                    float dx = (x + 0.5f - ox) * mPerPx, d2 = dx * dx + dz * dz;
                    if (d2 >= r2) continue;
                    int k = y * Tex + x;
                    if (state[k] == 2) continue; // already covered by another radar
                    float brg = Mathf.Atan2(dx, dz);
                    if (brg < 0f) brg += 2f * Mathf.PI;
                    int bin = Mathf.Min(Az - 1, (int)(brg * azPerRad)) * Steps + Mathf.Min(Steps - 1, (int)(Mathf.Sqrt(d2) * binsPerM));
                    if (cone > 0f)
                    {
                        float dy = p.Height[bin] + agl - radarAlt;
                        if (elevOnly ? Mathf.Abs(Mathf.Atan2(dy, Mathf.Sqrt(d2)) * Mathf.Rad2Deg - p.ConePitch) > cone
                            : Vector3.Angle(new Vector3(dx, flat ? 0f : dy, dz), fwd) > cone) continue;
                    }
                    byte v = p.Vis[bin];
                    if (v > state[k]) state[k] = v;
                }
            }
            if (y <= y1) buildRow = y;
            return used;
        }

        // =====================================================================
        // Mouse: scroll = zoom about the cursor, drag = pan
        // =====================================================================
        protected override bool ScreenMouse(Event e)
        {
            Vector2 mp = e.mousePosition;
            float mPerScreenPx = viewR / (screen.width * 0.5f);
            switch (e.type)
            {
                case EventType.ScrollWheel when screen.Contains(mp):
                    float before = viewR;
                    Zoom(e.delta.y > 0f ? 1.25f : 0.8f);
                    float k = viewR / before;
                    if (!follow)
                    {
                        // keep the map point under the cursor fixed
                        float gx = viewX + (mp.x - screen.center.x) * mPerScreenPx;
                        float gz = viewZ - (mp.y - screen.center.y) * mPerScreenPx;
                        viewX = gx + (viewX - gx) * k;
                        viewZ = gz + (viewZ - gz) * k;
                    }
                    e.Use();
                    return true;
                case EventType.MouseDown when e.button == 0:
                    if (ClickContact(mp))
                    {
                        e.Use();
                        return true;
                    }
                    panning = true;
                    lastMouse = mp;
                    e.Use();
                    return true;
                case EventType.MouseDrag when panning:
                    Vector2 d = mp - lastMouse;
                    lastMouse = mp;
                    follow = false;
                    viewX -= d.x * mPerScreenPx;
                    viewZ += d.y * mPerScreenPx;
                    e.Use();
                    return true;
                case EventType.MouseUp when panning:
                    panning = false;
                    e.Use();
                    return true;
            }
            return false;
        }

        // =====================================================================
        // Display
        // =====================================================================
        protected override void DrawScreen()
        {
            if (me == null) return;
            GUI.BeginGroup(screen);

            // the game's own map image: global origin is the map centre
            Sprite img = mapSettings != null ? mapSettings.MapImage : null;
            if (img != null)
            {
                Vector2 ms = mapSettings.MapSize;
                Vector2 tl = ToScreen(-ms.x * 0.5f, ms.y * 0.5f), br = ToScreen(ms.x * 0.5f, -ms.y * 0.5f);
                Texture2D t = img.texture;
                Rect tr = img.textureRect;
                Color saved = GUI.color;
                GUI.color = new Color(0.8f, 0.8f, 0.8f, 1f);
                GUI.DrawTextureWithTexCoords(Rect.MinMaxRect(tl.x, tl.y, br.x, br.y), t,
                    new Rect(tr.x / t.width, tr.y / t.height, tr.width / t.width, tr.height / t.height));
                GUI.color = saved;
            }

            // coverage overlay, placed where it was built (it lags pans by up to 0.1 s)
            if (overlay != null)
            {
                Vector2 tl = ToScreen(texX - texR, texZ + texR), br = ToScreen(texX + texR, texZ - texR);
                GUI.DrawTexture(Rect.MinMaxRect(tl.x, tl.y, br.x, br.y), overlay);
            }

            // radar sites (friendly blue / enemy red, amber = jammed)
            if (Current != Mode.Own)
            {
                foreach (Profile p in profiles.Values)
                {
                    if (p.Radar == me.radar || !p.Valid || p.Enemy != (Current == Mode.Enemy)) continue;
                    Vector2 s = ToScreen(p.Origin.x, p.Origin.z);
                    Fill(new Rect(s.x - 3f, s.y - 3f, 6f, 6f), p.Jammed ? Amber : Current == Mode.Enemy ? EnemySiteCol : SiteCol, 0f);
                }
            }

            // enemy contacts drawn with the map icon's own sprite and colour; position from the same
            // track the icon uses, heading read off the icon (the map freezes it while a track is stale)
            DynamicMap map = SceneSingleton<DynamicMap>.i;
            float mapRot = map != null && map.mapImage != null ? map.mapImage.transform.eulerAngles.z : 0f;
            float es = ContactSize;
            List<Unit> targets = TargetPanel.Targets();
            Color savedCol = GUI.color;
            foreach (Contact k in contacts)
            {
                if (k.Unit == null || k.Icon == null || k.Icon.sprite == null) continue;
                Vector2 s = ContactPos(k);
                if (s.x < -es || s.y < -es || s.x > screen.width + es || s.y > screen.height + es) continue;
                Sprite sp = k.Icon.sprite;
                if (!iconAlpha.TryGetValue(sp.texture, out Texture2D t)) continue; // baked in CollectContacts
                Rect tr = sp.textureRect;
                float w = es * Mathf.Clamp(k.Unit.definition.mapIconSize, 0.6f, 1.2f), h = w * tr.height / Mathf.Max(1f, tr.width);
                if (targets != null && targets.Contains(k.Unit))
                    Border(new Rect(s.x - es * 0.8f, s.y - es * 0.8f, es * 1.6f, es * 1.6f), Color.white, Mathf.Max(1.5f, 0.004f * S), 0f);
                Matrix4x4 saved = GUI.matrix;
                if (k.Orient) GUIUtility.RotateAroundPivot(mapRot - k.Icon.transform.eulerAngles.z, s);
                GUI.color = k.Icon.color;
                GUI.DrawTextureWithTexCoords(new Rect(s.x - w * 0.5f, s.y - h * 0.5f, w, h), t,
                    new Rect(tr.x / t.width, tr.y / t.height, tr.width / t.width, tr.height / t.height));
                GUI.matrix = saved;
            }
            GUI.color = savedCol;

            // own jet: dot + heading stick
            GlobalPosition g = me.GlobalPosition();
            Vector2 c = ToScreen(g.x, g.z);
            Vector3 f = me.transform.forward;
            Matrix4x4 m = GUI.matrix;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg, c);
            Fill(new Rect(c.x - 1f, c.y - 0.05f * S, 2f, 0.05f * S), Color.white, 0f);
            GUI.matrix = m;
            Fill(new Rect(c.x - 3.5f, c.y - 3.5f, 7f, 7f), Color.white, 3.5f);
            GUI.EndGroup();

            DrawLegends();

            // bottom status line: what is shown + colour key
            Fill(Sc(0f, 0.92f, 1f, 0.08f), LegendBack, 0f);
            bool noRadar = Current == Mode.Own && ownRadarMissing;
            bool ownJammed = !noRadar && profiles.TryGetValue(me.radar, out Profile op) && op.Jammed;
            string status = grid != null && !grid.Ready ? $"MAPPING TERRAIN {grid.Progress * 100f:F0}%"
                : mapSettings == null ? "NO MAP"
                : Current == Mode.Own
                ? (noRadar ? "NO RADAR" : ownJammed ? "OWN RDR JAMMED" : me.radar.activated ? "OWN RDR" : "OWN RDR OFF")
                : (Current == Mode.Enemy ? $"{siteCount} ENMY RDR" : $"{siteCount} RDR SITES") + (jammedCount > 0 ? $" {jammedCount} JAM" : "");
            Text(Sc(0.02f, 0.92f, 0.5f, 0.08f), status, 0.03f, noRadar || (Current == Mode.Own ? ownJammed : jammedCount > 0) ? Amber : Green, TextAnchor.MiddleLeft);
            Color green = new Color(0.35f, 0.95f, 0.45f), red = new Color(1f, 0.3f, 0.25f);
            bool en = Current == Mode.Enemy;
            Text(Sc(0.5f, 0.92f, 0.48f, 0.08f), "SEEN", 0.026f, en ? red : green, TextAnchor.MiddleLeft);
            Text(Sc(0.66f, 0.92f, 0.32f, 0.08f), "TERRAIN", 0.026f, en ? green : red, TextAnchor.MiddleRight);
        }

        /// <summary>Global x/z -> position inside the screen rect (0,0 = its top-left).</summary>
        private Vector2 ToScreen(float gx, float gz)
        {
            float pxPerM = screen.width * 0.5f / viewR;
            return new Vector2(screen.width * 0.5f + (gx - viewX) * pxPerM, screen.height * 0.5f - (gz - viewZ) * pxPerM);
        }

        private float ContactSize => Mathf.Max(8f, 0.016f * S);

        private Vector2 ContactPos(Contact k)
        {
            GlobalPosition p = k.Track != null ? k.Track.GetPosition() : k.Unit.GlobalPosition();
            return ToScreen(p.x, p.z);
        }

        /// <summary>Click on a contact: select it as a target, or deselect it if already selected.</summary>
        private bool ClickContact(Vector2 mp)
        {
            List<Unit> targets = TargetPanel.Targets();
            if (targets == null) return false;
            float best = Mathf.Max(10f, 0.025f * S); // hit radius: larger than the icon so it is easy to tap
            Unit hit = null;
            foreach (Contact k in contacts)
            {
                if (k.Unit == null) continue;
                float d = (ContactPos(k) + screen.position - mp).magnitude;
                if (d < best)
                {
                    best = d;
                    hit = k.Unit;
                }
            }
            if (hit == null) return false;
            CombatHUD hud = SceneSingleton<CombatHUD>.i;
            if (targets.Contains(hit)) hud.DeSelectUnit(hit);
            else hud.SelectUnit(hit);
            return true;
        }

        private void DrawLegends()
        {
            foreach (Osb b in Buttons)
            {
                Rect r = LabelRect(b);
                bool lit = flashCmd == b.Command && Time.unscaledTime < flashUntil;
                if (b.Glyph != Glyph.Text)
                {
                    DrawArrow(r, b, b.Glyph == Glyph.Up);
                    continue;
                }
                TextAnchor anchor = b.Side == Side.Left ? TextAnchor.MiddleLeft : TextAnchor.UpperCenter;
                string label;
                bool boxed = false;
                switch (b.Command)
                {
                    case ApCommand.RdrAll: label = "ALL"; boxed = Current == Mode.All; break;
                    case ApCommand.RdrOwn: label = "OWN"; boxed = Current == Mode.Own; break;
                    case ApCommand.RdrEnemy: label = "ENMY"; boxed = Current == Mode.Enemy; break;
                    case ApCommand.RdrTgt: label = $"TGT {cfg.RadarTargetAgl.Value:F0}"; break;
                    case ApCommand.RdrHide: label = "HIDE"; break;
                    case ApCommand.RdrFollow: label = "FOLW"; boxed = follow; break;
                    default: label = "MAP"; break;
                }
                Legend(r, label, anchor, boxed, false, lit);
            }

            // range readout between the zoom arrows (half-width of the view)
            Rect rr = Sc(0.8f, 0.45f, 0.19f, 0.1f);
            Fill(rr, LegendBack, 0f);
            Text(new Rect(rr.x, rr.y, rr.width - 0.01f * S, rr.height * 0.45f), "RNG KM", 0.022f, GreenDim, TextAnchor.UpperRight);
            Text(new Rect(rr.x, rr.y + rr.height * 0.4f, rr.width - 0.01f * S, rr.height * 0.6f), $"{viewR / 1000f:F0}", 0.036f, Green, TextAnchor.UpperRight);
        }
    }
}
