using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Igtap.Tas.DesktopModel;
using N = Igtap.Tas.UI.NativeMethods;

namespace Igtap.Tas.UI
{
    // Own HWND and message loop, but the SAME process/Mono runtime as IGTAP.
    // This thread must never call Unity or mutate the session. All work goes
    // through StudioChannel and the existing main-thread command boundary.
    internal sealed class NativeStudioWindow : IStudioWindow
    {
        private readonly StudioChannel _channel;
        private readonly bool _visible;
        private Thread _thread;
        private IntPtr _handle;
        private volatile bool _close;
        private readonly N.WindowProc _procedure;
        private readonly Dictionary<int, IntPtr> _controls = new Dictionary<int, IntPtr>();
        private readonly List<Control> _layout = new List<Control>();
        private readonly Dictionary<int, bool> _availability = new Dictionary<int, bool>();
        private IntPtr _background, _normalFont, _monoFont;
        private float _scale = 1;
        private int _tab;
        private Snapshot _shown;
        private string _name = "", _localError = "";
        private string[] _movies = Array.Empty<string>(), _states = Array.Empty<string>();
        private NativeFrameEditor _editor;
        internal IntPtr EditorHandle => _editor?.Handle ?? IntPtr.Zero;
        private const uint Child = 0x50000000, TabStop = 0x10000;
        private const uint Back = 0x26170C, Fore = 0xF2E6D9;
        private sealed class Control { public int Id, X, Y, Width, Height, Page; }
        internal IntPtr Handle => Interlocked.CompareExchange(ref _handle, IntPtr.Zero, IntPtr.Zero);
        internal bool ThreadAlive => _thread?.IsAlive == true;
        public NativeStudioWindow(StudioChannel channel, bool visible = true) { _channel = channel; _visible = visible; _procedure = Procedure; }
        public void Open()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) throw new PlatformNotSupportedException("The native studio window requires Windows.");
            if (ThreadAlive)
            {
                if (_close) throw new InvalidOperationException("The studio window is still closing. Open it again shortly.");
                if (Handle != IntPtr.Zero) N.PostMessage(Handle, 0x8001, IntPtr.Zero, IntPtr.Zero);
                return;
            }
            _close = false; _channel.Starting = true; _channel.Error = ""; _channel.Clear();
            _thread = new Thread(Run) { IsBackground = true, Name = "Tas Studio native window" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
        private void Run()
        {
            string className = "TasStudio.Native." + Guid.NewGuid().ToString("N");
            IntPtr instance = N.GetModuleHandle(null), dpi = IntPtr.Zero;
            bool registered = false;
            try
            {
                // Set DPI awareness only for this UI thread, never the game process.
                dpi = N.SetThreadDpiAwarenessContext(new IntPtr(-4));
                _background = N.CreateSolidBrush(Back);
                var wc = new N.WindowClass { Size = (uint)Marshal.SizeOf<N.WindowClass>(), Procedure = Marshal.GetFunctionPointerForDelegate(_procedure),
                    Instance = instance, Cursor = N.LoadCursor(IntPtr.Zero, new IntPtr(32512)), Background = _background, Name = className };
                if (N.RegisterClassEx(ref wc) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                registered = true;
                IntPtr hwnd = N.CreateWindowEx(0, className, "Tas Studio 0.6.1 - in-game process", 0x02CF0000,
                    _visible ? unchecked((int)0x80000000) : -30000, _visible ? unchecked((int)0x80000000) : -30000,
                    980, 850, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
                if (hwnd == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                Interlocked.Exchange(ref _handle, hwnd);
                _scale = Math.Max(1, N.GetDpiForWindow(hwnd) / 96f);
                Build(); Fonts(); Layout();
                _editor = new NativeFrameEditor(_channel, hwnd, _visible);
                if (_close) { N.DestroyWindow(hwnd); return; }
                _channel.IsOpen = true; _channel.Starting = false;
                N.SetTimer(hwnd, new UIntPtr(1), 100, IntPtr.Zero);
                if (_visible) N.ShowWindow(hwnd, 5);
                int result;
                while ((result = N.GetMessage(out var message, IntPtr.Zero, 0, 0)) > 0)
                {
                    if (_editor != null && _editor.ProcessKey(ref message)) continue;
                    // F6-F8 are local to this window, not global hotkeys.
                    if (message.Id == 0x100 && message.WParam.ToUInt64() >= 0x75 && message.WParam.ToUInt64() <= 0x77)
                    {
                        try { Click(message.WParam.ToUInt64() == 0x75 ? 100 : message.WParam.ToUInt64() == 0x76 ? 101 : 102); }
                        catch (Exception e) { _localError = e.Message; }
                        continue;
                    }
                    if (!N.IsDialogMessage(hwnd, ref message)) { N.TranslateMessage(ref message); N.DispatchMessage(ref message); }
                }
                if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            catch (Exception e) { _channel.Error = "Native window: " + e.Message; }
            finally
            {
                _editor?.Dispose(); _editor = null;
                var hwnd = Handle;
                if (hwnd != IntPtr.Zero) N.DestroyWindow(hwnd);
                Interlocked.Exchange(ref _handle, IntPtr.Zero);
                _channel.IsOpen = _channel.Starting = false; _channel.Clear();
                _controls.Clear(); _layout.Clear(); _availability.Clear(); _shown = null; _name = _localError = "";
                _movies = _states = Array.Empty<string>();
                if (_normalFont != IntPtr.Zero) N.DeleteObject(_normalFont);
                if (_monoFont != IntPtr.Zero) N.DeleteObject(_monoFont);
                if (registered) N.UnregisterClass(className, instance);
                if (_background != IntPtr.Zero) N.DeleteObject(_background);
                _normalFont = _monoFont = _background = IntPtr.Zero;
                if (dpi != IntPtr.Zero) N.SetThreadDpiAwarenessContext(dpi);
            }
        }
        private IntPtr Procedure(IntPtr hwnd, uint message, IntPtr wp, IntPtr lp)
        {
            try
            {
                switch (message)
                {
                    case 0x10: _close = true; _channel.IsOpen = false; N.DestroyWindow(hwnd); return IntPtr.Zero; // WM_CLOSE
                    case 0x2: N.KillTimer(hwnd, new UIntPtr(1)); Interlocked.Exchange(ref _handle, IntPtr.Zero); N.PostQuitMessage(0); return IntPtr.Zero;
                    case 0x5: if (_controls.Count > 0) Layout(); return IntPtr.Zero;
                    case 0x24:
                        var size = Marshal.PtrToStructure<N.MinMax>(lp);
                        size.MinTrack.X = (int)(820 * _scale); size.MinTrack.Y = (int)(680 * _scale);
                        Marshal.StructureToPtr(size, lp, false); return IntPtr.Zero;
                    case 0x2E0:
                        _scale = (wp.ToInt64() & 0xffff) / 96f;
                        var rect = Marshal.PtrToStructure<N.Rect>(lp);
                        N.MoveWindow(hwnd, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, true);
                        Fonts(); Layout(); return IntPtr.Zero;
                    case 0x113: Refresh(); _editor?.Refresh(); return IntPtr.Zero; // WM_TIMER
                    case 0x111:
                        int id = (int)(wp.ToInt64() & 0xffff), notification = (int)((wp.ToInt64() >> 16) & 0xffff);
                        if (notification == 0) Click(id);
                        return IntPtr.Zero;
                    case 0x133: case 0x134: case 0x138: // edit/listbox/static colors
                        N.SetTextColor(wp, Fore); N.SetBkColor(wp, Back); return _background;
                    case 0x8001: if (_visible) { N.ShowWindow(hwnd, 9); N.SetForegroundWindow(hwnd); } return IntPtr.Zero;
                }
            }
            catch (Exception e) { _localError = e.Message; }
            // Never let a managed exception escape the native callback.
            return N.DefWindowProc(hwnd, message, wp, lp);
        }
        private void Add(int id, string kind, string text, int x, int y, int w, int h, int page = -1, uint style = 0)
        {
            IntPtr child = N.CreateWindowEx(0, kind, text, Child | style, 0, 0, 1, 1, Handle, new IntPtr(id), N.GetModuleHandle(null), IntPtr.Zero);
            if (child == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            _controls.Add(id, child); _layout.Add(new Control { Id = id, X = x, Y = y, Width = w, Height = h, Page = page });
            if (kind == "EDIT") N.SendMessage(child, 0xC5, new IntPtr(60), IntPtr.Zero);
        }
        private void Label(int id, string text, int y, int page, int height = 24) => Add(id, "STATIC", text, 0, y, 320, height, page);
        private void Button(int id, string text, int y, int page) => Add(id, "BUTTON", text, 0, y, 320, 30, page, TabStop);
        private void Edit(int id, string text, int y, int page, int x = 0, int width = 320) => Add(id, "EDIT", text, x, y, width, 26, page, 0x00800080 | TabStop);
        private void Check(int id, string text, int y, int page) => Add(id, "BUTTON", text, 0, y, 320, 24, page, 3 | TabStop);
        private void Combo(int id, int y, int page) => Add(id, "COMBOBOX", "", 0, y, 320, 250, page, 0x00200003 | TabStop);
        private void Build()
        {
            Add(1, "STATIC", "TAS STUDIO / Opening native window...", 0, 0, 1, 1);
            Add(2, "STATIC", "", 0, 0, 1, 1);
            for (int i = 0; i < 2; i++) Add(10 + i, "BUTTON", new[] { "Run", "States" }[i], 0, 0, 1, 1, -1, TabStop);
            Add(20, "STATIC", "PLAYER / LIVE + NEXT FRAME", 0, 0, 1, 1);
            Add(21, "STATIC", "Waiting for player...", 0, 0, 1, 1);
            Add(22, "STATIC", "", 0, 0, 1, 1);
            Add(23, "STATIC", "", 0, 0, 1, 1);
            Button(100, "F6  Pause / Resume", 0, 0); Button(101, "F7  Step one frame", 36, 0); Button(102, "F8  Stop + reload", 72, 0);
            Label(103, "Speed (0.1 to 1.0; dot or comma)", 112, 0); Edit(104, "1", 138, 0, 0, 110);
            Add(105, "BUTTON", "Apply speed", 122, 138, 198, 28, 0, TabStop);
            Label(106, "TAS filename", 176, 0); Edit(107, "my-run", 202, 0); Button(108, "Save movie", 238, 0);
            Label(114, "F5 reopens the desktop windows.\r\nClosing this window pauses an advancing run.\r\nCreate/load/replay: in-game Mods > Tas Studio.", 330, 0, 95);
            Button(115, "Open frame editor", 440, 0);
            Check(116, "Record inputs from frame list", 485, 0);
            Label(400, "SAVESTATES / paused recording", 0, 3); Label(401, "State name", 38, 3); Edit(402, "checkpoint-1", 64, 3);
            Button(403, "Save current state", 104, 3); Combo(404, 156, 3); Button(405, "Load selected state", 196, 3);
            Label(406, "Loading reconstructs the saved frame by replay,\r\nthen pauses recording. This is not an instant\r\nmemory restore.", 244, 3, 85);
        }
        private void Fonts()
        {
            IntPtr oldNormal = _normalFont, oldMono = _monoFont;
            _normalFont = N.CreateFont(-(int)(15 * _scale), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            _monoFont = N.CreateFont(-(int)(14 * _scale), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Consolas");
            foreach (var control in _controls) N.SendMessage(control.Value, 0x30, control.Key == 3 || control.Key == 4 || control.Key == 21 ? _monoFont : _normalFont, new IntPtr(1));
            if (oldNormal != IntPtr.Zero) N.DeleteObject(oldNormal); if (oldMono != IntPtr.Zero) N.DeleteObject(oldMono);
        }
        private void Layout()
        {
            if (Handle == IntPtr.Zero || _controls.Count == 0) return;
            N.GetClientRect(Handle, out var r); int width = (int)(r.Right / _scale), height = (int)(r.Bottom / _scale);
            int middle = (width - 48) / 2, mx = 16, rx = mx + middle + 16;
            float vertical = Math.Max(.6f, Math.Min(1, (height - 200) / 640f));
            Move(1, 16, 16, width - 32, 54); Move(2, 16, height - 54, width - 32, 48);
            for (int i = 0; i < 2; i++) Move(10 + i, mx + i * (middle / 2), 86, middle / 2 - 3, 30);
            Move(20, rx, 86, width - rx - 16, 34); Move(21, rx, 130, width - rx - 16, 230);
            Move(22, rx, 372, width - rx - 16, 62); Move(23, rx, 446, width - rx - 16, height - 500);
            foreach (var c in _layout.Where(c => c.Page >= 0))
            {
                Move(c.Id, mx + c.X * middle / 320, 130 + (int)(c.Y * vertical), c.Width * middle / 320,
                    c.Height > 100 ? c.Height : Math.Max(20, (int)(c.Height * vertical)));
                N.ShowWindow(_controls[c.Id], c.Page == _tab ? 5 : 0);
            }
            N.InvalidateRect(Handle, IntPtr.Zero, true);
        }
        private void Move(int id, int x, int y, int w, int h) { if (_controls.TryGetValue(id, out var c)) N.MoveWindow(c, (int)(x * _scale), (int)(y * _scale), Math.Max(1, (int)(w * _scale)), Math.Max(1, (int)(h * _scale)), true); }
        private string Text(int id) { var s = new StringBuilder(512); N.GetWindowText(_controls[id], s, s.Capacity); return s.ToString(); }
        private void Text(int id, string text) { if (Text(id) != text) N.SetWindowText(_controls[id], text ?? ""); }
        private bool Checked(int id) => N.SendMessage(_controls[id], 0xF0, IntPtr.Zero, IntPtr.Zero).ToInt64() == 1;
        private void SetCheck(int id, bool check) => N.SendMessage(_controls[id], 0xF1, new IntPtr(check ? 1 : 0), IntPtr.Zero);
        private int Selection(int id) => (int)N.SendMessage(_controls[id], id == 4 ? 0x188u : 0x147u, IntPtr.Zero, IntPtr.Zero);
        private void Enable(int id, bool enabled) { _availability[id] = enabled; N.EnableWindow(_controls[id], enabled); }
        private void Send(Command c) { _localError = _channel.Send(c) ? "" : "Window is closing or commands are full. Nothing was sent."; }
        private void Click(int id)
        {
            if (_availability.TryGetValue(id, out bool available) && !available) return;
            if (id >= 10 && id <= 11) { _tab = id == 10 ? 0 : 3; Layout(); return; }
            switch (id)
            {
                case 100: Send(new Command { Action = "pause" }); break;
                case 101: Send(new Command { Action = "step" }); break;
                case 102: Send(new Command { Action = "stop" }); break;
                case 105: Send(new Command { Action = "speed", Speed = Text(104) }); break;
                case 108: Send(new Command { Action = "save", Name = Text(107) }); break;
                case 115: _editor?.Open(); break;
                case 116: Send(new Command { Action = "timelineInput", Authored = Checked(116) }); break;
                case 403: Send(new Command { Action = "saveState", Name = Text(402) }); break;
                case 405: Send(new Command { Action = "loadState", Name = Text(404) }); break;
            }
        }
        private void Refresh()
        {
            var s = _channel.Latest; if (s == null) return;
            bool relayout = _shown == null || _shown.Mode != s.Mode || _shown.MainMenu != s.MainMenu;
            _shown = s;
            if (_channel.RequestedPage >= 0)
            {
                _tab = _channel.RequestedPage == 3 ? 3 : 0;
                _channel.RequestedPage = -1; _editor?.Open(); relayout = true;
            }
            Text(1, "TAS STUDIO / " + s.Mode.ToUpperInvariant() + (s.Paused ? " [PAUSED]" : "") + "    " + s.Cursor + " / " + s.Frames + " frames\r\n" +
                s.Speed.ToString("0.##", CultureInfo.InvariantCulture) + "x   |   " + s.Fps + " fps   |   Rerecords " + s.Rerecords + "   |   Native window in the game process");
            Text(2, !string.IsNullOrEmpty(_localError) ? _localError : !string.IsNullOrEmpty(s.Error) ? s.Error : s.Status);
            SetCheck(116, s.TimelineInput);
            if (_name != s.Name && N.GetFocus() != _controls[107]) { Text(107, s.Name); _name = s.Name; }
            Enable(100, s.CanTransport); Enable(101, s.CanTransport); Enable(102, s.Active); Enable(108, s.Frames > 0);

            Enable(403, s.CanState); Enable(405, s.CanState);
            Text(108, s.Dirty ? "Save movie *" : "Save movie");
            List(404, s.States, ref _states);
            Text(21, "STAT          CURRENT        NEXT\r\n\r\n" + string.Join("\r\n", s.Stats.Select(r => r.Name.PadRight(12) + r.Current.PadLeft(9) + " " + r.Next.PadLeft(9))));
            Text(22, "Prediction: " + s.InputSource + "\r\nRecording: " + (s.Authored ? "selected input" : "physical controls"));
            Text(23, s.Prediction + "\r\n\r\nEstimates do not simulate collisions or triggers.");
            if (relayout) Layout();
        }
        private void List(int id, string[] values, ref string[] previous)
        {
            if (previous.SequenceEqual(values)) return;
            string selected = Text(id); N.SendMessage(_controls[id], 0x14B, IntPtr.Zero, IntPtr.Zero);
            foreach (string value in values) N.SendText(_controls[id], 0x143, IntPtr.Zero, value);
            N.SendMessage(_controls[id], 0x14E, new IntPtr(Math.Max(0, Array.IndexOf(values, selected))), IntPtr.Zero); previous = values;
        }
        public void Dispose()
        {
            _close = true; _channel.IsOpen = false;
            if (Handle != IntPtr.Zero) N.PostMessage(Handle, 0x10, IntPtr.Zero, IntPtr.Zero);
            // Do not Join from Unity's main thread: Windows can send synchronous
            // activation messages there while this thread is destroying its HWND.
        }
    }
}
