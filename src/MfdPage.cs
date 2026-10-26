using System;
using UnityEngine;

namespace AutopilotMod
{
    /// <summary>
    /// Fighter-style MFD drawn with IMGUI: a bezel with 20 option-select buttons (OSBs, five per
    /// side) around a green-phosphor A/P page. Buttons are clickable whenever the mouse cursor is
    /// visible (MFD cursor key, map open, menus); every function also has a hotkey.
    ///
    ///            T1 A/P   T2 HDG   T3 ATT   T4 BALT  T5 RALT
    ///   L1 STEP  +--------------------------------------+  R1 V/S
    ///   L2 ^     |  heading tape                        |  R2 ^
    ///   L3 HDG   |  mode annunciators / messages        |  R3 ALT
    ///   L4 v     |  attitude indicator                  |  R4 v
    ///   L5 DATA  |  ALT / SPD / V/S / THR               |  R5 SYNC
    ///            +--------------------------------------+
    ///            B1 A/T   B2 v     B3 SPD   B4 ^     B5 HIDE
    /// </summary>
    internal sealed class MfdPage : MfdFrame
    {

        protected override Osb[] Buttons => ApButtons;

        private static readonly Osb[] ApButtons =
        {
            new Osb { Side = Side.Top, Index = 0, Command = ApCommand.ApToggle, Glyph = Glyph.Text },
            new Osb { Side = Side.Top, Index = 1, Command = ApCommand.ModeHdg, Glyph = Glyph.Text },
            new Osb { Side = Side.Top, Index = 2, Command = ApCommand.ModeAtt, Glyph = Glyph.Text },
            new Osb { Side = Side.Top, Index = 3, Command = ApCommand.ModeBalt, Glyph = Glyph.Text },
            new Osb { Side = Side.Top, Index = 4, Command = ApCommand.ModeRalt, Glyph = Glyph.Text },

            new Osb { Side = Side.Right, Index = 0, Command = ApCommand.ModeVs, Glyph = Glyph.Text },
            new Osb { Side = Side.Right, Index = 1, Command = ApCommand.VertUp, Glyph = Glyph.Up },
            new Osb { Side = Side.Right, Index = 2, Command = ApCommand.VertSync, Glyph = Glyph.Readout },
            new Osb { Side = Side.Right, Index = 3, Command = ApCommand.VertDown, Glyph = Glyph.Down },
            new Osb { Side = Side.Right, Index = 4, Command = ApCommand.SyncAll, Glyph = Glyph.Text },

            new Osb { Side = Side.Bottom, Index = 0, Command = ApCommand.AtToggle, Glyph = Glyph.Text },
            new Osb { Side = Side.Bottom, Index = 1, Command = ApCommand.SpdDown, Glyph = Glyph.Down },
            new Osb { Side = Side.Bottom, Index = 2, Command = ApCommand.SpdSync, Glyph = Glyph.Readout },
            new Osb { Side = Side.Bottom, Index = 3, Command = ApCommand.SpdUp, Glyph = Glyph.Up },
            new Osb { Side = Side.Bottom, Index = 4, Command = ApCommand.Hide, Glyph = Glyph.Text },

            new Osb { Side = Side.Left, Index = 0, Command = ApCommand.StepToggle, Glyph = Glyph.Text },
            new Osb { Side = Side.Left, Index = 1, Command = ApCommand.LatUp, Glyph = Glyph.Up },
            new Osb { Side = Side.Left, Index = 2, Command = ApCommand.LatSync, Glyph = Glyph.Readout },
            new Osb { Side = Side.Left, Index = 3, Command = ApCommand.LatDown, Glyph = Glyph.Down },
            new Osb { Side = Side.Left, Index = 4, Command = ApCommand.DataToggle, Glyph = Glyph.Text },
        };

        private readonly ApSettings cfg;
        private readonly FlightController ap;

        private string message;
        private bool messageWarn;
        private float messageUntil;

        public MfdPage(ApSettings cfg, FlightController ap) : base(cfg.MfdSize, cfg.MfdX, cfg.MfdY)
        {
            this.cfg = cfg;
            this.ap = ap;
        }

        public void ShowMessage(string text, bool warning)
        {
            message = text;
            messageWarn = warning;
            messageUntil = Time.unscaledTime + (warning ? 5f : 2.5f);
        }

        /// <summary>Flashing master-caution style PULL UP cue in the upper middle of the screen.</summary>
        public void DrawPullUp()
        {
            if (Event.current.type != EventType.Repaint) return;
            EnsureResources();
            if (Mathf.Repeat(Time.unscaledTime, 0.5f) > 0.33f) return;

            float h = Mathf.Max(28f, Screen.height * 0.07f);
            float w = h * 4.2f;
            Rect box = new Rect((Screen.width - w) * 0.5f, Screen.height * 0.26f, w, h);
            Color red = new Color(1f, 0.16f, 0.12f, 1f);
            GUI.DrawTexture(box, white, ScaleMode.StretchToFill, true, 0f, new Color(0f, 0f, 0f, 0.55f), 0f, h * 0.12f);
            GUI.DrawTexture(box, white, ScaleMode.StretchToFill, true, 0f, red, Mathf.Max(2f, h * 0.06f), h * 0.12f);
            style.fontSize = Mathf.RoundToInt(h * 0.62f);
            style.alignment = TextAnchor.MiddleCenter;
            style.normal.textColor = red;
            GUI.Label(box, "PULL UP", style);
        }

        // =====================================================================
        // Display
        // =====================================================================
        protected override void DrawScreen()
        {
            FlightState s = ap.State;
            bool apOn = ap.ApEngaged;

            DrawOsbLegends(s);

            if (!s.Valid)
            {
                Text(Sc(0.2f, 0.4f, 0.6f, 0.2f), "NO DATA", 0.05f, Amber, TextAnchor.MiddleCenter);
                return;
            }

            DrawHeadingTape(Sc(0.215f, 0.115f, 0.57f, 0.07f), s);
            DrawAnnunciators(Sc(0.215f, 0.195f, 0.57f, 0.055f), apOn);

            if (message != null && Time.unscaledTime < messageUntil)
            {
                bool blinkOn = !messageWarn || Mathf.Repeat(Time.unscaledTime, 0.6f) < 0.4f;
                if (blinkOn)
                    Text(Sc(0.215f, 0.255f, 0.57f, 0.045f), message, 0.03f, messageWarn ? Amber : Green, TextAnchor.MiddleCenter);
            }
            else if (apOn && ap.Rotorcraft && ap.RotorRpmLimited)
            {
                Text(Sc(0.215f, 0.255f, 0.57f, 0.045f), $"ROTOR RPM LIM {ap.RotorNr * 100f:0}%", 0.028f, Amber, TextAnchor.MiddleCenter);
            }
            else if (apOn && ap.BankLimitedBySurface)
            {
                Text(Sc(0.215f, 0.255f, 0.57f, 0.045f), "BANK LIM - LOW", 0.028f, Amber, TextAnchor.MiddleCenter);
            }

            bool debug = cfg.ShowDebug.Value;
            DrawAdi(Sc(0.215f, 0.305f, 0.57f, debug ? 0.335f : 0.395f), s, apOn);
            if (debug) DrawDebug(Sc(0.215f, 0.645f, 0.57f, 0.06f), s);

            // current-value readouts
            DrawReadout(Sc(0.215f, 0.71f, 0.285f, 0.08f), "ALT", $"{Units.Alt(s.Altitude):F0}");
            DrawReadout(Sc(0.5f, 0.71f, 0.285f, 0.08f), "SPD", $"{Units.Speed(s.Speed):F0}");
            DrawReadout(Sc(0.215f, 0.795f, 0.285f, 0.08f), "V/S", $"{Units.Vs(s.VerticalSpeed):+0;-0;0}");
            DrawReadout(Sc(0.5f, 0.795f, 0.285f, 0.08f), "THR", $"{s.Throttle * 100f:F0}%");
        }

        private void DrawOsbLegends(FlightState s)
        {
            bool apOn = ap.ApEngaged;
            foreach (Osb b in Buttons)
            {
                Rect r = LabelRect(b);
                TextAnchor anchor = b.Side == Side.Left ? TextAnchor.MiddleLeft
                    : b.Side == Side.Right ? TextAnchor.MiddleRight
                    : b.Side == Side.Top ? TextAnchor.UpperCenter : TextAnchor.LowerCenter;

                switch (b.Glyph)
                {
                    case Glyph.Up:
                    case Glyph.Down:
                        DrawArrow(r, b, b.Glyph == Glyph.Up);
                        continue;
                    case Glyph.Readout:
                        DrawTargetReadout(r, b, anchor, s);
                        continue;
                }

                string label;
                bool boxed = false, inverse = false;
                switch (b.Command)
                {
                    case ApCommand.ApToggle: label = "A/P"; inverse = apOn; boxed = true; break;
                    case ApCommand.AtToggle: label = ap.Rotorcraft ? "SPD" : "A/T"; inverse = ap.AtEngaged; boxed = true; break;
                    case ApCommand.ModeHdg: label = "HDG"; boxed = ap.Lateral == LateralMode.Hdg; break;
                    case ApCommand.ModeAtt: label = "ATT"; boxed = ap.Lateral == LateralMode.Att; break;
                    case ApCommand.ModeBalt: label = "BALT"; boxed = ap.Vertical == VerticalMode.Balt; break;
                    case ApCommand.ModeRalt: label = "RALT"; boxed = ap.Vertical == VerticalMode.Ralt; break;
                    case ApCommand.ModeVs: label = "V/S"; boxed = ap.Vertical == VerticalMode.Vs; break;
                    case ApCommand.StepToggle: label = ap.CoarseStep ? "STEP\nCRSE" : "STEP\nFINE"; break;
                    case ApCommand.DataToggle: label = "DATA"; boxed = cfg.ShowDebug.Value; break;
                    case ApCommand.SyncAll: label = "SYNC"; break;
                    case ApCommand.Hide: label = "HIDE"; break;
                    default: label = ""; break;
                }
                bool flashing = flashCmd == b.Command && Time.unscaledTime < flashUntil;
                Legend(r, label, anchor, boxed, inverse, flashing);
            }
        }

        private void DrawTargetReadout(Rect r, Osb b, TextAnchor anchor, FlightState s)
        {
            string tag, value;
            bool active;
            switch (b.Command)
            {
                case ApCommand.LatSync:
                    active = ap.ApEngaged;
                    if (ap.Lateral == LateralMode.Hdg)
                    {
                        tag = "HDG";
                        value = $"{Mathf.Repeat(Mathf.Round(ap.TgtHeading), 360f):000}°";
                    }
                    else
                    {
                        tag = "BANK";
                        float bk = Mathf.Round(ap.TgtBank);
                        value = bk == 0f ? "LVL" : (bk > 0 ? "R" : "L") + Mathf.Abs(bk).ToString("F0");
                    }
                    break;
                case ApCommand.VertSync:
                    active = ap.ApEngaged;
                    switch (ap.Vertical)
                    {
                        case VerticalMode.Balt: tag = "ALT " + Units.AltUnit; value = $"{Units.Alt(ap.TgtAltitude):F0}"; break;
                        case VerticalMode.Ralt: tag = "RALT " + Units.AltUnit; value = $"{Units.Alt(ap.TgtRadarAlt):F0}"; break;
                        default: tag = "V/S " + Units.VsUnit; value = $"{Units.Vs(ap.TgtVs):+0;-0;0}"; break;
                    }
                    break;
                default:
                    active = ap.AtEngaged;
                    tag = "SPD " + Units.SpeedUnit;
                    value = $"{Units.Speed(ap.TgtSpeed):F0}";
                    break;
            }

            Color c = active ? Green : GreenDim;
            float tagH = r.height * 0.4f;
            Text(new Rect(r.x, r.y, r.width, tagH), tag, 0.024f, GreenDim, Upper(anchor));
            Text(new Rect(r.x, r.y + tagH, r.width, r.height - tagH), value, 0.042f, c, Upper(anchor));
        }

        private void DrawAnnunciators(Rect r, bool apOn)
        {
            float w = r.width / 3f;
            string lat = ap.Lateral == LateralMode.Hdg ? "HDG SEL" : "ATT HLD";
            string vert = ap.Vertical == VerticalMode.Balt ? "ALT HLD" : ap.Vertical == VerticalMode.Ralt ? "RALT HLD" : "V/S";
            Annunciator(new Rect(r.x, r.y, w, r.height), lat, apOn);
            Annunciator(new Rect(r.x + w, r.y, w, r.height), vert, apOn);
            Annunciator(new Rect(r.x + 2f * w, r.y, w, r.height), ap.Rotorcraft && ap.TgtSpeed == 0f ? "HVR HLD" : "SPD", ap.AtEngaged);
        }

        private void Annunciator(Rect cell, string label, bool on)
        {
            Rect inner = new Rect(cell.x + 0.006f * S, cell.y + 0.004f * S, cell.width - 0.012f * S, cell.height - 0.008f * S);
            if (on) Border(inner, Green, Mathf.Max(1f, 0.003f * S), 0f);
            Text(inner, label, 0.027f, on ? Green : GreenFaint, TextAnchor.MiddleCenter, on);
        }

        private void DrawReadout(Rect r, string tag, string value)
        {
            float tw = r.width * 0.3f;
            Text(new Rect(r.x + 0.01f * S, r.y, tw, r.height), tag, 0.024f, GreenDim, TextAnchor.MiddleLeft);
            Text(new Rect(r.x + tw, r.y, r.width - tw - 0.012f * S, r.height), value, 0.04f, Green, TextAnchor.MiddleRight);
        }

        private void DrawDebug(Rect r, FlightState s)
        {
            string fbw = !ap.Rotorcraft ? (ap.HasFbw ? "FBW" : "MAN") : float.IsNaN(ap.NacelleDeg) ? "HELO" : $"NAC{ap.NacelleDeg:0}";
            string l1 = $"BNK {ap.CmdBankDeg:+0.0;-0.0}/{s.Bank:+0.0;-0.0} {(ap.Rotorcraft ? "PIT" : "FPA")} {ap.CmdGammaDeg:+0.0;-0.0}/{(ap.Rotorcraft ? s.Pitch : s.Gamma):+0.0;-0.0}";
            string l2 = $"Q {ap.CmdQ * Mathf.Rad2Deg:+0.0;-0.0}/{s.PitchRate * Mathf.Rad2Deg:+0.0;-0.0} P {ap.CmdP * Mathf.Rad2Deg:+0;-0}/{s.RollRate * Mathf.Rad2Deg:+0;-0} {fbw}";
            string l3 = $"STK P{ap.OutPitch:+0.00;-0.00} R{ap.OutRoll:+0.00;-0.00} T{ap.OutThrottle:0.00} VS* {Units.Vs(ap.CmdVs):+0;-0}";
            float h = r.height / 3f;
            Text(new Rect(r.x, r.y, r.width, h), l1, 0.019f, GreenDim, TextAnchor.MiddleCenter, false);
            Text(new Rect(r.x, r.y + h, r.width, h), l2, 0.019f, GreenDim, TextAnchor.MiddleCenter, false);
            Text(new Rect(r.x, r.y + 2f * h, r.width, h), l3, 0.019f, GreenDim, TextAnchor.MiddleCenter, false);
        }

        // ---- heading tape ----
        private void DrawHeadingTape(Rect r, FlightState s)
        {
            const float span = 50f;
            float ppd = r.width / span;
            float lw = Mathf.Max(1f, 0.003f * S);

            GUI.BeginGroup(r);
            float w = r.width, h = r.height;
            float baseY = h - 1f;
            Line(new Vector2(0f, baseY), new Vector2(w, baseY), lw, GreenDim);

            float start = Mathf.Floor((s.Heading - span * 0.5f - 5f) / 5f) * 5f;
            for (float d = start; d <= s.Heading + span * 0.5f + 5f; d += 5f)
            {
                float x = w * 0.5f + Mathf.DeltaAngle(s.Heading, d) * ppd;
                bool major = Mathf.Repeat(d, 10f) < 0.01f;
                float len = major ? h * 0.32f : h * 0.18f;
                Line(new Vector2(x, baseY), new Vector2(x, baseY - len), lw, Green);
                if (major)
                {
                    int hd = Mathf.RoundToInt(Mathf.Repeat(d, 360f));
                    string lbl = hd == 0 ? "N" : hd == 90 ? "E" : hd == 180 ? "S" : hd == 270 ? "W" : (hd / 10).ToString("00");
                    Text(new Rect(x - 0.04f * S, 0f, 0.08f * S, h * 0.6f), lbl, 0.022f, GreenDim, TextAnchor.MiddleCenter, false);
                }
            }

            // heading bug (HDG mode)
            if (ap.Lateral == LateralMode.Hdg)
            {
                float bx = Mathf.Clamp(w * 0.5f + Mathf.DeltaAngle(s.Heading, ap.TgtHeading) * ppd, 3f, w - 3f);
                float bw = 0.022f * S, bh = h * 0.3f;
                Color bc = ap.ApEngaged ? Green : GreenDim;
                Line(new Vector2(bx - bw * 0.5f, baseY), new Vector2(bx - bw * 0.5f, baseY - bh), lw * 1.5f, bc);
                Line(new Vector2(bx + bw * 0.5f, baseY), new Vector2(bx + bw * 0.5f, baseY - bh), lw * 1.5f, bc);
                Line(new Vector2(bx - bw * 0.5f, baseY - bh), new Vector2(bx + bw * 0.5f, baseY - bh), lw * 1.5f, bc);
            }
            GUI.EndGroup();

            // digital heading box over the lubber line
            float boxW = 0.075f * S, boxH = r.height * 0.62f;
            Rect box = new Rect(r.center.x - boxW * 0.5f, r.y - boxH * 0.15f, boxW, boxH);
            Fill(box, ScreenBg, 0f);
            Border(box, Green, lw, 0f);
            Text(box, $"{Mathf.Repeat(Mathf.Round(s.Heading), 360f):000}", 0.03f, Green, TextAnchor.MiddleCenter);
            Line(new Vector2(r.center.x, box.yMax), new Vector2(r.center.x, r.yMax), lw, Green);
        }

        // ---- attitude indicator ----
        private static readonly int[] BankTicks = { -60, -45, -30, -20, -10, 0, 10, 20, 30, 45, 60 };

        private void DrawAdi(Rect area, FlightState s, bool apOn)
        {
            float side = Mathf.Min(area.width, area.height);
            Rect a = new Rect(area.center.x - side * 0.5f, area.center.y - side * 0.5f, side, side);
            Vector2 c = a.center;
            float rad = side * 0.5f;
            float lw = Mathf.Max(1f, 0.0035f * S);
            float ppd = rad * 0.85f / 30f;   // +-30 deg of pitch fills the dial

            Matrix4x4 saved = GUI.matrix;
            GUIUtility.RotateAroundPivot(-s.Bank, c);

            // horizon (clipped to the dial by chord length)
            float hy = s.Pitch * ppd;
            if (Mathf.Abs(hy) < rad * 0.95f)
            {
                float half = Mathf.Sqrt(rad * rad - hy * hy) * 0.95f;
                Line(new Vector2(c.x - half, c.y + hy), new Vector2(c.x + half, c.y + hy), lw * 1.6f, Green);
            }
            // pitch ladder
            for (int p = -30; p <= 30; p += 10)
            {
                if (p == 0) continue;
                float y = (s.Pitch - p) * ppd;
                if (Mathf.Abs(y) > rad * 0.8f) continue;
                float half = rad * (Mathf.Abs(p) % 20 == 0 ? 0.3f : 0.22f);
                if (p > 0)
                {
                    Line(new Vector2(c.x - half, c.y + y), new Vector2(c.x + half, c.y + y), lw, Green);
                }
                else
                {
                    // negative pitch: dashed
                    float seg = half * 0.4f;
                    for (float x0 = -half; x0 < half - 0.1f; x0 += seg * 1.5f)
                        Line(new Vector2(c.x + x0, c.y + y), new Vector2(c.x + Mathf.Min(x0 + seg, half), c.y + y), lw, Green);
                }
                Text(new Rect(c.x + half + 2f, c.y + y - 0.015f * S, 0.05f * S, 0.03f * S), Mathf.Abs(p).ToString(), 0.02f, GreenDim, TextAnchor.MiddleLeft, false);
            }
            GUI.matrix = saved;

            // bank scale (fixed) + sky pointer (rotates). Tick endpoints are computed directly:
            // Line() rotates internally, and a RotateAroundPivot nested in another one pivots wrongly.
            float tickR = rad * 0.92f;
            foreach (int ang in BankTicks)
            {
                float len = ang % 30 == 0 ? rad * 0.1f : rad * 0.06f;
                var dir = new Vector2(Mathf.Sin(ang * Mathf.Deg2Rad), -Mathf.Cos(ang * Mathf.Deg2Rad));
                Line(c + dir * tickR, c + dir * (tickR - len), lw * 1.5f, ang == 0 ? Green : GreenDim);
            }
            float tri = 0.03f * S;
            GUIUtility.RotateAroundPivot(-s.Bank, c);
            Triangle(new Rect(c.x - tri * 0.5f, c.y - tickR + rad * 0.1f, tri, tri), true, Green);
            GUI.matrix = saved;
            if (apOn)
            {
                // flight-director bank command caret
                GUIUtility.RotateAroundPivot(-ap.CmdBankDeg, c);
                Triangle(new Rect(c.x - tri * 0.4f, c.y - tickR - tri * 0.85f, tri * 0.8f, tri * 0.8f), false, GreenDim);
                GUI.matrix = saved;
            }

            // fixed aircraft symbol
            float wl = rad * 0.34f, gap = rad * 0.1f, drop = rad * 0.07f;
            float aw = lw * 2f;
            Line(new Vector2(c.x - wl, c.y), new Vector2(c.x - gap, c.y), aw, Green);
            Line(new Vector2(c.x - gap, c.y), new Vector2(c.x - gap, c.y + drop), aw, Green);
            Line(new Vector2(c.x + gap, c.y), new Vector2(c.x + wl, c.y), aw, Green);
            Line(new Vector2(c.x + gap, c.y), new Vector2(c.x + gap, c.y + drop), aw, Green);
            Fill(new Rect(c.x - aw, c.y - aw, aw * 2f, aw * 2f), Green, 0f);

            // radar altitude under the dial
            Text(new Rect(a.x, a.yMax - 0.045f * S, a.width, 0.04f * S),
                $"R {Units.Alt(s.RadarAlt):F0}", 0.022f, GreenDim, TextAnchor.MiddleCenter, false);
        }

    }
}
