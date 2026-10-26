using System;
using BepInEx.Configuration;
using UnityEngine;

namespace AutopilotMod
{
    /// <summary>
    /// Shared MFD chrome drawn with IMGUI: bezel, corner screws, 20 option-select buttons (five per
    /// side), dragging, and the green-phosphor drawing primitives. A page supplies its button table
    /// and draws the display area in DrawScreen().
    /// </summary>
    internal abstract class MfdFrame
    {

        protected enum Side { Top, Right, Bottom, Left }

        protected enum Glyph { Text, Up, Down, Readout }

        protected struct Osb
        {
            public Side Side;
            public int Index;
            public ApCommand Command;
            public Glyph Glyph;
        }

        // OSB positions along each edge, as a fraction of the screen edge.
        protected static readonly float[] Slots = { 0.18f, 0.34f, 0.5f, 0.66f, 0.82f };

        // ---- palette ----
        protected static readonly Color BezelCol = new Color(0.115f, 0.122f, 0.13f, 0.97f);
        protected static readonly Color BezelEdge = new Color(0.27f, 0.285f, 0.3f, 1f);
        protected static readonly Color BezelShadow = new Color(0f, 0f, 0f, 0.45f);
        protected static readonly Color Recess = new Color(0.03f, 0.033f, 0.036f, 1f);
        protected static readonly Color ScreenBg = new Color(0.012f, 0.032f, 0.018f, 0.96f);
        protected static readonly Color OsbBody = new Color(0.2f, 0.21f, 0.22f, 1f);
        protected static readonly Color OsbPressed = new Color(0.12f, 0.13f, 0.135f, 1f);
        protected static readonly Color OsbTop = new Color(0.34f, 0.355f, 0.37f, 1f);
        protected static readonly Color Screw = new Color(0.22f, 0.23f, 0.24f, 1f);
        protected static readonly Color Green = new Color(0.36f, 1f, 0.45f, 1f);
        protected static readonly Color GreenDim = new Color(0.2f, 0.55f, 0.26f, 1f);
        protected static readonly Color GreenFaint = new Color(0.2f, 0.55f, 0.26f, 0.35f);
        protected static readonly Color Amber = new Color(1f, 0.74f, 0.2f, 1f);
        protected static readonly Color Black = new Color(0f, 0f, 0f, 1f);

        protected Texture2D white, tri;
        protected Font font;
        protected GUIStyle style;
        protected readonly GUIContent content = new GUIContent();

        protected Rect frame, screen;
        protected float S;
        protected bool dragging, resizing;
        private float resizeS;
        protected Vector2 dragOffset, dragPos;
        protected ApCommand flashCmd;
        protected float flashUntil;

        private readonly ConfigEntry<float> sizeCfg, xCfg, yCfg;

        protected MfdFrame(ConfigEntry<float> size, ConfigEntry<float> x, ConfigEntry<float> y)
        {
            sizeCfg = size;
            xCfg = x;
            yCfg = y;
        }

        protected abstract Osb[] Buttons { get; }

        protected abstract void DrawScreen();

        /// <summary>Mouse events over the display area (not the bezel). Return true if used.</summary>
        protected virtual bool ScreenMouse(Event e) => false;

        /// <summary>Briefly light the OSB bound to a command (so hotkeys show on the MFD too).</summary>
        public void Flash(ApCommand cmd)
        {
            flashCmd = cmd;
            flashUntil = Time.unscaledTime + 0.15f;
        }

        // =====================================================================
        // Entry point (call from OnGUI)
        // =====================================================================
        public void OnGUI(Action<ApCommand> execute)
        {
            EnsureResources();
            Layout();

            Event e = Event.current;
            bool cursor = Cursor.visible;
            if (cursor && HandleMouse(e, execute))
                return;
            if (e.type != EventType.Repaint)
                return;

            DrawBezel(cursor);
            DrawScreen();
        }

        // =====================================================================
        // Web mirror: draw into a render texture, take input from the browser
        // =====================================================================
        private bool mirror; // layout is the mirror's (frame at 0,0), not the game screen's

        private void MirrorLayout(float size)
        {
            mirror = true;
            S = size;
            frame = new Rect(0f, 0f, size, size);
            float m = Bezel * S;
            screen = new Rect(m, m, size - 2f * m, size - 2f * m);
        }

        /// <summary>Draw the whole MFD into rt (square). Call from OnGUI during Repaint.</summary>
        public void RenderMirror(RenderTexture rt)
        {
            EnsureResources();
            MirrorLayout(rt.width);
            RenderTexture prev = RenderTexture.active;
            Matrix4x4 savedGui = GUI.matrix;
            RenderTexture.active = rt;
            GL.PushMatrix();
            GL.LoadPixelMatrix(0f, rt.width, rt.height, 0f);
            GL.Clear(true, true, Color.black);
            GUI.matrix = Matrix4x4.identity;
            try
            {
                DrawBezel(false);
                DrawScreen();
            }
            finally
            {
                GUI.matrix = savedGui;
                GL.PopMatrix();
                RenderTexture.active = prev;
            }
        }

        /// <summary>A click/drag/scroll from the browser, at a position in mirror pixels.</summary>
        public void RemoteMouse(EventType type, Vector2 pos, float wheel, float size, Action<ApCommand> execute)
        {
            MirrorLayout(size);
            var e = new Event { type = type, mousePosition = pos, button = 0, delta = new Vector2(0f, wheel) };
            HandleMouse(e, execute);
        }

        protected void Layout()
        {
            mirror = false;
            S = resizing ? resizeS : Mathf.Clamp(sizeCfg.Value * Screen.height, MinSize, Screen.height * 0.9f);
            Vector2 pos = dragging || resizing
                ? dragPos
                : new Vector2(Mathf.Clamp01(xCfg.Value) * (Screen.width - S), Mathf.Clamp01(yCfg.Value) * (Screen.height - S));
            frame = new Rect(Mathf.Round(pos.x), Mathf.Round(pos.y), Mathf.Round(S), Mathf.Round(S));
            float m = Bezel * S;
            screen = new Rect(frame.x + m, frame.y + m, frame.width - 2f * m, frame.height - 2f * m);
        }

        // =====================================================================
        // Mouse
        // =====================================================================
        private const float MinSize = 240f;
        private const float Bezel = 0.07f; // bezel width as a fraction of the MFD size

        /// <summary>Bottom-right corner of the bezel: drag it to resize.</summary>
        private Rect GripRect() => new Rect(frame.xMax - Bezel * S, frame.yMax - Bezel * S, Bezel * S, Bezel * S);

        private void SavePosition(Vector2 topLeft)
        {
            xCfg.Value = Mathf.Clamp01(topLeft.x / Mathf.Max(1f, Screen.width - S));
            yCfg.Value = Mathf.Clamp01(topLeft.y / Mathf.Max(1f, Screen.height - S));
        }

        protected bool HandleMouse(Event e, Action<ApCommand> execute)
        {
            Vector2 mp = e.mousePosition;
            if (!dragging && !resizing && e.type != EventType.MouseDown && ScreenMouse(e))
                return true;
            switch (e.type)
            {
                case EventType.MouseDown when e.button == 0 && !mirror && GripRect().Contains(mp):
                    resizing = true;
                    resizeS = S;
                    dragPos = frame.position; // top-left stays put while resizing
                    e.Use();
                    return true;

                case EventType.MouseDrag when resizing:
                    resizeS = Mathf.Clamp(Mathf.Max(mp.x - dragPos.x, mp.y - dragPos.y), MinSize, Screen.height * 0.9f);
                    e.Use();
                    return true;

                case EventType.MouseUp when resizing:
                    resizing = false;
                    S = resizeS;
                    sizeCfg.Value = resizeS / Screen.height;
                    SavePosition(dragPos);
                    e.Use();
                    return true;

                case EventType.MouseDown when e.button == 0 && frame.Contains(mp):
                    foreach (Osb b in Buttons)
                    {
                        if (ButtonRect(b, 1.35f).Contains(mp) || LabelRect(b).Contains(mp))
                        {
                            Flash(b.Command);
                            execute(b.Command);
                            e.Use();
                            return true;
                        }
                    }
                    if (screen.Contains(mp) && ScreenMouse(e))
                        return true;
                    if (!mirror && !screen.Contains(mp)) // the browser can't move the in-game MFD
                    {
                        dragging = true;
                        dragOffset = mp - frame.position;
                        dragPos = frame.position;
                    }
                    e.Use();
                    return true;

                case EventType.MouseDrag when dragging:
                    dragPos = mp - dragOffset;
                    e.Use();
                    return true;

                case EventType.MouseUp when dragging:
                    dragging = false;
                    SavePosition(dragPos);
                    e.Use();
                    return true;
            }
            return false;
        }

        // =====================================================================
        // Geometry
        // =====================================================================
        protected Vector2 ButtonCenter(Osb b)
        {
            float m = Bezel * S;
            float f = Slots[b.Index];
            switch (b.Side)
            {
                case Side.Top: return new Vector2(screen.x + f * screen.width, frame.y + m * 0.5f);
                case Side.Bottom: return new Vector2(screen.x + f * screen.width, frame.yMax - m * 0.5f);
                case Side.Left: return new Vector2(frame.x + m * 0.5f, screen.y + f * screen.height);
                default: return new Vector2(frame.xMax - m * 0.5f, screen.y + f * screen.height);
            }
        }

        protected Rect ButtonRect(Osb b, float scale = 1f)
        {
            Vector2 c = ButtonCenter(b);
            bool horizontal = b.Side == Side.Top || b.Side == Side.Bottom;
            float w = (horizontal ? 0.085f : 0.052f) * S * scale;
            float h = (horizontal ? 0.052f : 0.085f) * S * scale;
            return new Rect(c.x - w * 0.5f, c.y - h * 0.5f, w, h);
        }

        /// <summary>Where the OSB's legend sits on the display, just inside the bezel button.</summary>
        protected Rect LabelRect(Osb b)
        {
            float f = Slots[b.Index];
            switch (b.Side)
            {
                case Side.Top: return Sc(f - 0.08f, 0.015f, 0.16f, 0.08f);
                case Side.Bottom:
                    return b.Glyph == Glyph.Readout ? Sc(f - 0.09f, 0.885f, 0.18f, 0.1f) : Sc(f - 0.08f, 0.905f, 0.16f, 0.08f);
                case Side.Left: return Sc(0.012f, f - 0.05f, 0.19f, 0.1f);
                default: return Sc(0.798f, f - 0.05f, 0.19f, 0.1f);
            }
        }

        /// <summary>Rect in screen-relative fractions.</summary>
        protected Rect Sc(float x, float y, float w, float h)
        {
            return new Rect(screen.x + x * screen.width, screen.y + y * screen.height, w * screen.width, h * screen.height);
        }

        // =====================================================================
        // Bezel
        // =====================================================================
        private static readonly float[] GripLines = { 0.035f, 0.055f, 0.075f };

        protected void DrawBezel(bool cursor)
        {
            float r = 0.045f * S;
            Fill(new Rect(frame.x + 3f, frame.y + 4f, frame.width, frame.height), BezelShadow, r);
            Fill(frame, BezelCol, r);
            Border(frame, BezelEdge, Mathf.Max(1.5f, S * 0.004f), r);

            // corner screws
            float sw = 0.026f * S, inset = 0.03f * S;
            Vector2[] screws =
            {
                new Vector2(frame.x + inset, frame.y + inset), new Vector2(frame.xMax - inset, frame.y + inset),
                new Vector2(frame.x + inset, frame.yMax - inset), new Vector2(frame.xMax - inset, frame.yMax - inset)
            };
            for (int i = 0; i < (cursor ? 3 : 4); i++)
            {
                Vector2 c = screws[i];
                Rect sr = new Rect(c.x - sw * 0.5f, c.y - sw * 0.5f, sw, sw);
                Fill(sr, Screw, sw * 0.5f);
                Border(sr, BezelEdge, 1f, sw * 0.5f);
                Line(new Vector2(sr.x + sw * 0.22f, sr.y + sw * 0.78f), new Vector2(sr.xMax - sw * 0.22f, sr.y + sw * 0.22f), Mathf.Max(1f, sw * 0.12f), Recess);
            }

            if (cursor)
            {
                bool hot = resizing || GripRect().Contains(Event.current.mousePosition);
                float e = 0.015f * S, w = Mathf.Max(1.5f, 0.005f * S);
                foreach (float k in GripLines)
                    Line(new Vector2(frame.xMax - k * S, frame.yMax - e), new Vector2(frame.xMax - e, frame.yMax - k * S), w, hot ? Green : GreenDim);
            }

            // display recess + glass
            float g = 0.014f * S;
            Fill(new Rect(screen.x - g, screen.y - g, screen.width + 2f * g, screen.height + 2f * g), Recess, 0.02f * S);
            Fill(screen, ScreenBg, 0.012f * S);

            // OSBs
            Vector2 mouse = Event.current.mousePosition;
            foreach (Osb b in Buttons)
            {
                Rect br = ButtonRect(b);
                bool lit = flashCmd == b.Command && Time.unscaledTime < flashUntil;
                bool hover = cursor && (ButtonRect(b, 1.35f).Contains(mouse) || LabelRect(b).Contains(mouse));
                Rect body = lit ? new Rect(br.x, br.y + 1f, br.width, br.height) : br;
                Fill(new Rect(br.x + 1f, br.y + 2f, br.width, br.height), BezelShadow, 0.008f * S);
                Fill(body, lit ? OsbPressed : OsbBody, 0.008f * S);
                if (!lit) Fill(new Rect(body.x + 2f, body.y + 1f, body.width - 4f, Mathf.Max(1f, 0.004f * S)), OsbTop, 0f);
                if (hover) Border(body, GreenDim, 1f, 0.008f * S);
            }

            if (cursor)
                Text(new Rect(frame.x, frame.y + 0.005f * S, frame.width, 0.05f * S), "CRSR", 0.022f, GreenDim, TextAnchor.UpperCenter, false);
        }

        protected static TextAnchor Upper(TextAnchor a)
        {
            switch (a)
            {
                case TextAnchor.MiddleLeft: return TextAnchor.UpperLeft;
                case TextAnchor.MiddleRight: return TextAnchor.UpperRight;
                default: return TextAnchor.UpperCenter;
            }
        }

        protected void DrawArrow(Rect r, Osb b, bool up)
        {
            float t = 0.034f * S;
            Vector2 c;
            switch (b.Side)
            {
                case Side.Left: c = new Vector2(r.x + t * 0.6f, r.center.y); break;
                case Side.Right: c = new Vector2(r.xMax - t * 0.6f, r.center.y); break;
                case Side.Top: c = new Vector2(r.center.x, r.y + t * 0.6f); break;
                default: c = new Vector2(r.center.x, r.yMax - t * 0.6f); break;
            }
            bool lit = flashCmd == b.Command && Time.unscaledTime < flashUntil;
            Triangle(new Rect(c.x - t * 0.5f, c.y - t * 0.5f, t, t), up, lit ? Color.white : Green);
        }

        protected void Legend(Rect r, string label, TextAnchor anchor, bool boxed, bool inverse, bool flashing)
        {
            if (string.IsNullOrEmpty(label)) return;
            int size = FontPx(0.032f);
            style.fontSize = size;
            content.text = label;
            Vector2 sz = style.CalcSize(content);
            float pad = 0.006f * S;
            float w = sz.x + 2f * pad, h = sz.y + pad;
            float x = anchor == TextAnchor.MiddleLeft ? r.x
                : anchor == TextAnchor.MiddleRight ? r.xMax - w
                : r.center.x - w * 0.5f;
            float y = anchor == TextAnchor.UpperCenter ? r.y
                : anchor == TextAnchor.LowerCenter ? r.yMax - h
                : r.center.y - h * 0.5f;
            Rect box = new Rect(x, y, w, h);

            Color textCol = flashing ? Color.white : Green;
            Fill(box, new Color(0f, 0f, 0f, 0.6f), 0f); // keeps legends readable over map imagery
            if (inverse)
            {
                Fill(box, Green, 0f);
                textCol = Black;
            }
            else if (boxed)
            {
                Border(box, Green, Mathf.Max(1f, 0.0035f * S), 0f);
            }
            Text(box, label, 0.032f, textCol, TextAnchor.MiddleCenter, !inverse);
        }

        // =====================================================================
        // Primitives
        // =====================================================================
        protected void EnsureResources()
        {
            if (white == null)
            {
                white = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                white.SetPixel(0, 0, Color.white);
                white.Apply();
            }
            if (tri == null)
                tri = MakeTriangle(64);
            if (style == null)
            {
                if (font == null)
                {
                    try { font = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Lucida Console", "Courier New" }, 32); }
                    catch { font = null; }
                }
                style = new GUIStyle(GUI.skin.label)
                {
                    font = font,
                    richText = false,
                    wordWrap = false,
                    clipping = TextClipping.Overflow,
                    padding = new RectOffset(0, 0, 0, 0),
                    margin = new RectOffset(0, 0, 0, 0),
                    fontStyle = FontStyle.Bold,
                };
            }
        }

        protected static Texture2D MakeTriangle(int n)
        {
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
            // apex at the top (texture row n-1), base at the bottom; 4x4 supersampled edges
            Vector2 a = new Vector2(n * 0.5f, n - 3f), b = new Vector2(3f, 5f), c = new Vector2(n - 3f, 5f);
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int hits = 0;
                for (int sy = 0; sy < 4; sy++)
                for (int sx = 0; sx < 4; sx++)
                {
                    Vector2 p = new Vector2(x + (sx + 0.5f) / 4f, y + (sy + 0.5f) / 4f);
                    if (InTri(p, a, b, c)) hits++;
                }
                t.SetPixel(x, y, new Color(1f, 1f, 1f, hits / 16f));
            }
            t.Apply();
            return t;
        }

        protected static bool InTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
            float d2 = (p.x - c.x) * (b.y - c.y) - (b.x - c.x) * (p.y - c.y);
            float d3 = (p.x - a.x) * (c.y - a.y) - (c.x - a.x) * (p.y - a.y);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        protected int FontPx(float frac) => Mathf.Max(8, Mathf.RoundToInt(frac * S));

        protected void Fill(Rect r, Color c, float radius)
        {
            GUI.DrawTexture(r, white, ScaleMode.StretchToFill, true, 0f, c, 0f, radius);
        }

        protected void Border(Rect r, Color c, float width, float radius)
        {
            GUI.DrawTexture(r, white, ScaleMode.StretchToFill, true, 0f, c, width, radius);
        }

        protected void Line(Vector2 a, Vector2 b, float width, Color c)
        {
            Vector2 d = b - a;
            float len = d.magnitude;
            if (len < 0.01f) return;
            Matrix4x4 saved = GUI.matrix;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg, a);
            GUI.DrawTexture(new Rect(a.x, a.y - width * 0.5f, len, width), white, ScaleMode.StretchToFill, true, 0f, c, 0f, 0f);
            GUI.matrix = saved;
        }

        protected void Triangle(Rect r, bool up, Color c)
        {
            Color saved = GUI.color;
            GUI.color = c;
            if (up) GUI.DrawTexture(r, tri);
            else GUI.DrawTextureWithTexCoords(r, tri, new Rect(0f, 1f, 1f, -1f));
            GUI.color = saved;
        }

        protected void Text(Rect r, string s, float sizeFrac, Color c, TextAnchor anchor, bool glow = true)
        {
            style.fontSize = FontPx(sizeFrac);
            style.alignment = anchor;
            if (glow && !mirror) // 4 extra labels per text; invisible after JPEG anyway
            {
                style.normal.textColor = new Color(c.r, c.g, c.b, c.a * 0.16f);
                GUI.Label(new Rect(r.x - 1f, r.y, r.width, r.height), s, style);
                GUI.Label(new Rect(r.x + 1f, r.y, r.width, r.height), s, style);
                GUI.Label(new Rect(r.x, r.y - 1f, r.width, r.height), s, style);
                GUI.Label(new Rect(r.x, r.y + 1f, r.width, r.height), s, style);
            }
            style.normal.textColor = c;
            GUI.Label(r, s, style);
        }
    }
}
