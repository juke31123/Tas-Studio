using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Igtap.Tas.DesktopModel;
using N = Igtap.Tas.UI.NativeMethods;

namespace Igtap.Tas.UI
{
    // Shared desktop presentation inspired by TatsuyaTas's dark cards, state
    // badges and aligned values. UI-thread-only GDI resources, never Unity work.
    internal sealed class NativeTheme : IDisposable
    {
        // Win32 COLORREF is BGR.
        internal const uint Back = 0x17100E, Panel = 0x211C18, Input = 0x191512,
            Raised = 0x342B24, Border = 0x443A31, Text = 0xFAF2ED, Muted = 0xC2AB9E,
            Accent = 0xC4D14F, Amber = 0x40BDFA, Green = 0x85DB66, Blue = 0xFFA673,
            Red = 0x6661FF, Disabled = 0x796B61, Held = 0x594525;
        private readonly Dictionary<uint, IntPtr> _brushes = new Dictionary<uint, IntPtr>();
        internal IntPtr Normal, Mono, Title, Small;
        private float _scale;
        public IntPtr Brush(uint color)
        {
            if (!_brushes.TryGetValue(color, out var brush)) _brushes[color] = brush = N.CreateSolidBrush(color);
            return brush;
        }
        public void Scale(float scale)
        {
            if (_scale == scale && Normal != IntPtr.Zero) return;
            ClearFonts(); _scale = scale;
            Normal = Font(15, 400, "Segoe UI"); Small = Font(12, 400, "Segoe UI");
            Title = Font(22, 600, "Segoe UI"); Mono = Font(14, 400, "Consolas");
        }
        private IntPtr Font(int size, int weight, string face) => N.CreateFont(-(int)(size * _scale), 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, face);
        public static void DarkTitle(IntPtr hwnd)
        {
            try { int enabled = 1; N.DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int)); }
            catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
        }
        public void Box(IntPtr dc, N.Rect rect, uint color, int radius = 10)
        {
            var oldBrush = N.SelectObject(dc, Brush(color)); var oldPen = N.SelectObject(dc, N.GetStockObject(8));
            N.RoundRect(dc, rect.Left, rect.Top, rect.Right, rect.Bottom, (int)(radius * _scale), (int)(radius * _scale));
            N.SelectObject(dc, oldPen); N.SelectObject(dc, oldBrush);
        }
        public void Label(IntPtr dc, N.Rect rect, string text, uint color, IntPtr font, uint format = 0x8824)
        {
            var old = N.SelectObject(dc, font); int oldMode = N.SetBkMode(dc, 1); uint oldColor = N.SetTextColor(dc, color);
            try { N.DrawText(dc, text ?? "", -1, ref rect, format); }
            finally { N.SetTextColor(dc, oldColor); N.SetBkMode(dc, oldMode); N.SelectObject(dc, old); }
        }
        private N.Rect R(N.Rect origin, int x, int y, int w, int h) => new N.Rect {
            Left = origin.Left + (int)(x * _scale), Top = origin.Top + (int)(y * _scale),
            Right = origin.Left + (int)((x + w) * _scale), Bottom = origin.Top + (int)((y + h) * _scale) };
        public void Button(N.DrawItem item, bool primary = false, bool selected = false, bool danger = false)
        {
            bool disabled = (item.State & 4) != 0, pressed = (item.State & 1) != 0;
            var rect = item.Rect; N.FillRect(item.Dc, ref rect, Brush(Panel));
            uint color = primary || selected ? Accent : danger ? Red : Text;
            Box(item.Dc, rect, pressed ? Accent : selected ? Accent : Border, 8);
            rect.Left++; rect.Top++; rect.Right--; rect.Bottom--;
            Box(item.Dc, rect, pressed ? Accent : primary || selected ? Held : Raised, 8);
            var text = new StringBuilder(256); N.GetWindowText(item.Window, text, text.Capacity);
            Label(item.Dc, rect, text.ToString(), disabled ? Disabled : pressed ? Back : color, Normal, 0x8825);
            if ((item.State & 0x10) != 0)
            {
                rect.Left += 4; rect.Right -= 4; rect.Top += 4; rect.Bottom -= 4;
                N.DrawFocusRect(item.Dc, ref rect);
            }
        }
        public static uint StateColor(Snapshot s) => s == null ? Blue : s.Mode == "Desync" ? Red : s.Paused ? Amber :
            s.Mode == "Recording" ? Red : s.Mode == "Playback" || s.Mode == "Complete" ? Green : Blue;
        public void Header(N.DrawItem item, Snapshot s, string title, bool compact = false)
        {
            var dc = item.Dc; var rect = item.Rect; int w = (int)((rect.Right - rect.Left) / _scale);
            N.FillRect(dc, ref rect, Brush(Back)); Box(dc, rect, Panel, 14);
            int height = (int)((rect.Bottom - rect.Top) / _scale);
            Box(dc, R(rect, 0, 12, 4, height - 24), StateColor(s), 4);
            if (compact)
            {
                Label(dc, R(rect, 14, 1, w - 180, 23), title + (s == null ? "" : "  /  " + s.Name), Text, Normal);
                Label(dc, R(rect, 14, 23, w - 180, 18), s == null ? "Waiting for timeline" :
                    s.Mode + "   •   Frame " + s.Cursor.ToString("N0") + " / " + s.Frames.ToString("N0") + (s.PendingEdits ? "   •   Unsaved edits" : "   •   Click or drag cells to edit"),
                    s?.PendingEdits == true ? Amber : Muted, Small);
                string badge = s?.PendingEdits == true ? "UNSAVED EDITS" : s?.Paused == true ? "PAUSED" : s?.Mode.ToUpperInvariant() ?? "READY";
                Box(dc, R(rect, w - 152, 10, 134, 23), StateColor(s), 22);
                Label(dc, R(rect, w - 152, 10, 134, 23), badge, Back, Small, 0x8825);
                return;
            }
            Label(dc, R(rect, 18, 10, w - 190, 30), title, Text, Title);
            string state = s == null ? "READY" : s.Mode == "Desync" ? "DESYNC" : s.Paused ? "PAUSED" : s.Mode.ToUpperInvariant();
            Box(dc, R(rect, w - 152, 16, 134, 23), StateColor(s), 22);
            Label(dc, R(rect, w - 152, 16, 134, 23), state, Back, Small, 0x8825);
            if (s == null) return;
            Label(dc, R(rect, 18, 43, w - 36, 24), s.Mode + "   •   " + s.Name + "   /   Frame " + s.Cursor.ToString("N0") + " of " + s.Frames.ToString("N0") +
                (s.PendingEdits ? "   •   Unsaved edits" : ""), s.PendingEdits ? Amber : Muted, Normal);
            string actual = s.HasPerformanceSample ? s.ActualFps.ToString("0.0", CultureInfo.InvariantCulture) + " FPS  /  " +
                s.AchievedSpeed.ToString("0.00", CultureInfo.InvariantCulture) + "x actual" : "Measuring actual FPS";
            Label(dc, R(rect, 18, 71, w - 36, 24), s.Fps + " FPS target    •    " + s.Speed.ToString("0.##", CultureInfo.InvariantCulture) +
                "x requested    •    " + actual + (s.Paused && s.HasPerformanceSample ? " (last sample)" : "") + "    •    " + s.Rerecords + " rerecords", Accent, Normal);
            Box(dc, R(rect, 18, 108, w - 36, 4), Raised, 4);
            if (s.Frames > 0) Box(dc, R(rect, 18, 108, Math.Max(1, (int)((w - 36) * Math.Min(1, s.Cursor / (double)s.Frames))), 4), Accent, 4);
        }
        public void Stats(N.DrawItem item, Snapshot s)
        {
            var rect = item.Rect; var dc = item.Dc; N.FillRect(dc, ref rect, Brush(Panel));
            int w = (int)((rect.Right - rect.Left) / _scale), h = (int)((rect.Bottom - rect.Top) / _scale);
            int step = Math.Min(32, (h - 28) / Math.Max(1, s?.Stats.Length ?? 10));
            Label(dc, R(rect, 0, 0, w * 42 / 100, 24), "PLAYER", Muted, Small);
            Label(dc, R(rect, w * 42 / 100, 0, w * 28 / 100, 24), "CURRENT", Muted, Small, 0x8826);
            Label(dc, R(rect, w * 72 / 100, 0, w * 28 / 100, 24), "NEXT FRAME", Muted, Small, 0x8826);
            if (s == null || s.Stats.Length == 0) { Label(dc, R(rect, 0, 40, w, 40), "Waiting for player data", Muted, Normal); return; }
            for (int i = 0; i < s.Stats.Length; i++)
            {
                var row = s.Stats[i]; int y = 28 + i * step;
                var line = R(rect, 0, y + step - 1, w, 1); N.FillRect(dc, ref line, Brush(Raised));
                Label(dc, R(rect, 0, y, w * 42 / 100, step - 1), row.Name, Muted, Normal);
                Label(dc, R(rect, w * 42 / 100, y, w * 28 / 100, step - 1), row.Current, Text, Mono, 0x8826);
                Label(dc, R(rect, w * 72 / 100, y, w * 28 / 100, step - 1), row.Next, Accent, Mono, 0x8826);
            }
        }
        private void ClearFonts()
        {
            foreach (var f in new[] { Normal, Mono, Title, Small }) if (f != IntPtr.Zero) N.DeleteObject(f);
            Normal = Mono = Title = Small = IntPtr.Zero;
        }
        public void Dispose() { ClearFonts(); foreach (var b in _brushes.Values) N.DeleteObject(b); _brushes.Clear(); }
    }
}
