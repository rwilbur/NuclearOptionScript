using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace AutopilotMod
{
    /// <summary>
    /// The game's target-list panel (map filters + selected targets) for the web page: a JSON
    /// snapshot of it, and the panel's own actions. Main thread only.
    /// </summary>
    internal static class TargetPanel
    {
        private static readonly List<TargetListSelector_ToggleButton> toggles = new List<TargetListSelector_ToggleButton>();
        private static readonly List<string> groups = new List<string>();

        /// <summary>The panel's toggles in a fixed order; their index is the id the browser sends back.</summary>
        private static TargetListSelector Collect()
        {
            toggles.Clear();
            groups.Clear();
            TargetListSelector t = SceneSingleton<TargetListSelector>.i;
            if (t == null) return null;
            void Add(TargetListSelector_ToggleButton b, string g)
            {
                if (b == null) return;
                toggles.Add(b);
                groups.Add(g);
            }
            Add(t.toggleFollowHUD, "OPTIONS");
            Add(t.toggleLaser, "OPTIONS");
            foreach (var b in t.toggleFactionItems) Add(b, "FACTION");
            foreach (var b in t.toggleUnitTypesItems) Add(b, "UNIT TYPE");
            foreach (var b in t.toggleVehicleTypesItems) Add(b, "VEHICLE TYPE");
            return t;
        }

        private static string Label(TargetListSelector t, TargetListSelector_ToggleButton b)
        {
            if (b == t.toggleFollowHUD) return "FOLLOW HUD";
            if (b == t.toggleLaser) return "LASED ONLY";
            string s = b.label != null ? b.label.text : b.name;
            return string.IsNullOrEmpty(s) ? b.name : s.Replace('\n', ' ');
        }

        public static string Json()
        {
            TargetListSelector t = Collect();
            var sb = new StringBuilder("{\"filters\":[");
            for (int i = 0; t != null && i < toggles.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"i\":").Append(i).Append(",\"g\":");
                Str(sb, groups[i]);
                sb.Append(",\"l\":");
                Str(sb, Label(t, toggles[i]));
                sb.Append(",\"on\":").Append(toggles[i].status ? "true" : "false").Append('}');
            }
            sb.Append("],\"selected\":[");
            List<Unit> targets = Targets();
            for (int i = 0; targets != null && i < targets.Count; i++)
            {
                Unit u = targets[i];
                if (u == null) continue;
                if (sb[sb.Length - 1] != '[') sb.Append(',');
                sb.Append("{\"u\":").Append(u.GetInstanceID()).Append(",\"n\":");
                Str(sb, u.unitName);
                sb.Append('}');
            }

            // selectable enemies, nearest first (by tracked position, as the map shows them)
            sb.Append("],\"contacts\":[");
            nearby.Clear();
            DynamicMap map = SceneSingleton<DynamicMap>.i;
            CombatHUD hud = SceneSingleton<CombatHUD>.i;
            if (map != null && hud != null && hud.aircraft != null)
            {
                GlobalPosition me = hud.aircraft.GlobalPosition();
                foreach (MapIcon icon in map.mapIcons)
                {
                    if (!(icon is UnitMapIcon ui) || !ShownEnemy(ui, t)) continue;
                    TrackingInfo track = map.HQ != null ? map.HQ.GetTrackingData(ui.unit.persistentID) : null;
                    nearby.Add(new KeyValuePair<float, Unit>(((track != null ? track.GetPosition() : ui.unit.GlobalPosition()) - me).magnitude, ui.unit));
                }
                nearby.Sort((a, b) => a.Key.CompareTo(b.Key));
            }
            for (int i = 0; i < nearby.Count && i < MaxContacts; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"u\":").Append(nearby[i].Value.GetInstanceID()).Append(",\"n\":");
                Str(sb, nearby[i].Value.unitName);
                sb.Append(",\"d\":").Append((nearby[i].Key / 1000f).ToString("F1", CultureInfo.InvariantCulture));
                sb.Append(",\"s\":").Append(targets != null && targets.Contains(nearby[i].Value) ? "true" : "false").Append('}');
            }
            return sb.Append("]}").ToString();
        }

        private const int MaxContacts = 80;
        private static readonly List<KeyValuePair<float, Unit>> nearby = new List<KeyValuePair<float, Unit>>();

        /// <summary>
        /// An enemy the game map shows: its icon exists and isn't hidden (pilot toggle), and the
        /// map's target filters don't exclude it.
        /// </summary>
        public static bool ShownEnemy(UnitMapIcon ui, TargetListSelector filter)
        {
            if (ui == null || !ui.gameObject.activeSelf || ui.iconImage == null) return false;
            Unit u = ui.unit;
            // Enemy = a faction other than our jet's, the same test the radar page uses for friendlies.
            CombatHUD hud = SceneSingleton<CombatHUD>.i;
            FactionHQ mine = hud != null && hud.aircraft != null ? hud.aircraft.NetworkHQ : null;
            return u != null && !u.disabled && mine != null && u.NetworkHQ != null && u.NetworkHQ != mine
                   && (filter == null || !filter.CheckExclusions(u));
        }

        /// <summary>Run one browser action: a=toggle|only (i=filter), a=reset, a=clear, a=desel (u=unit).</summary>
        public static void Apply(string action, string arg)
        {
            TargetListSelector t = Collect();
            if (t == null) return;
            int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n);
            bool valid = n >= 0 && n < toggles.Count;
            switch (action)
            {
                case "toggle" when valid: toggles[n].Toggle(); break;
                case "only" when valid: t.SetOnlyItem(toggles[n]); break;
                case "reset": t.ResetFilters(); break;
                case "clear": t.DeselectAll(); break;
                case "desel":
                    List<Unit> targets = Targets();
                    Unit u = targets?.Find(x => x != null && x.GetInstanceID() == n);
                    if (u != null) t.ForceDeselect(u);
                    break;
                case "sel":
                    // only units the map shows, like a map click
                    DynamicMap map = SceneSingleton<DynamicMap>.i;
                    List<Unit> list = Targets();
                    if (map == null || list == null) break;
                    foreach (MapIcon icon in map.mapIcons)
                        if (icon is UnitMapIcon ui && ShownEnemy(ui, t) && ui.unit.GetInstanceID() == n)
                        {
                            if (!list.Contains(ui.unit)) SceneSingleton<CombatHUD>.i.SelectUnit(ui.unit);
                            break;
                        }
                    break;
            }
        }

        /// <summary>The jet's weapon target list (what the map and HUD show as selected), or null.</summary>
        public static List<Unit> Targets()
        {
            CombatHUD hud = SceneSingleton<CombatHUD>.i;
            return hud != null && hud.aircraft != null && !hud.aircraft.disabled && hud.aircraft.weaponManager != null
                ? hud.aircraft.weaponManager.GetTargetList() : null;
        }

        private static void Str(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            sb.Append('"');
        }
    }
}
