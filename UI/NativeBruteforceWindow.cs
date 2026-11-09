using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Igtap.Tas.Core.Search;
using Igtap.Tas.DesktopModel;
using N = Igtap.Tas.UI.NativeMethods;

namespace Igtap.Tas.UI
{
    // Third top-level window on Studio's existing STA thread. Unity is reached
    // only by queued commands; the search itself runs in the external worker.
    internal sealed class NativeBruteforceWindow : IDisposable
    {
        private readonly StudioChannel _channel;
        private readonly IntPtr _owner;
        private readonly bool _visible;
        private readonly N.WindowProc _procedure;
        private readonly NativeTheme _theme = new NativeTheme();
        private readonly Dictionary<int, IntPtr> _controls = new Dictionary<int, IntPtr>();
        private readonly Dictionary<int, string> _text = new Dictionary<int, string>();
        private IntPtr _handle;
        private string _className, _localError = "", _goalLabel = "";
        private float _scale = 1;
        private bool _initialised;
        private int _capture = -1;
        private int _scroll;
        internal IntPtr Handle => _handle;
        public NativeBruteforceWindow(StudioChannel channel, IntPtr owner, bool visible)
        { _channel = channel; _owner = owner; _visible = visible; _procedure = Procedure; }
        public void Open()
        {
            if (_handle != IntPtr.Zero) { if (_visible) { N.ShowWindow(_handle, 9); N.SetForegroundWindow(_handle); } return; }
            _goalLabel = "";
            if (_className == null)
            {
                _className = "TasStudio.Bruteforce." + Guid.NewGuid().ToString("N");
                var wc = new N.WindowClass { Size = (uint)Marshal.SizeOf<N.WindowClass>(), Instance = N.GetModuleHandle(null),
                    Procedure = Marshal.GetFunctionPointerForDelegate(_procedure), Cursor = N.LoadCursor(IntPtr.Zero, new IntPtr(32512)),
                    Background = _theme.Brush(NativeTheme.Back), Name = _className };
                if (N.RegisterClassEx(ref wc) == 0) { _className = null; throw new Win32Exception(Marshal.GetLastWin32Error()); }
            }
            _handle = N.CreateWindowEx(0, _className, "Tas Studio - Bruteforce", 0x02EF0000,
                _visible ? unchecked((int)0x80000000) : -30000, _visible ? unchecked((int)0x80000000) : -30000,
                860, 880, _owner, IntPtr.Zero, N.GetModuleHandle(null), IntPtr.Zero);
            if (_handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            NativeTheme.DarkTitle(_handle); _scale = Math.Max(1, N.GetDpiForWindow(_handle) / 96f); _theme.Scale(_scale);
            Add(1, "STATIC", "BRUTEFORCE / EXTERNAL SEARCH");
            Add(2, "STATIC", "Start frame"); Add(3, "EDIT", "0"); Add(4, "BUTTON", "Use TAS cursor");
            Add(5, "STATIC", "Maximum frames"); Add(6, "EDIT", "180");
            Add(7, "STATIC", "Time budget (seconds)"); Add(8, "EDIT", "30");
            Add(9, "STATIC", "Workers (1-32)"); Add(10, "EDIT", "2");
            Add(11, "STATIC", "Goal radius"); Add(12, "EDIT", "24");
            Add(13, "STATIC", "Input hold (frames)"); Add(14, "EDIT", "1");
            Add(15, "STATIC", "Goal X / Y (world)"); Add(16, "EDIT", "0"); Add(17, "EDIT", "0");
            Add(18, "BUTTON", "Goal = current player position"); Add(19, "BUTTON", "Place goal with free camera");
            Add(20, "STATIC", "Allowed abilities (never grants an upgrade)");
            Add(21, "BUTTON", "Jump", true); Add(22, "BUTTON", "Extra air jump", true);
            Add(23, "BUTTON", "Dash", true); Add(24, "BUTTON", "Wall jump", true);
            Add(25, "STATIC", "Glitch-specific search controls are not yet available. Search uses normal frame inputs and the selected abilities.");
            Add(26, "BUTTON", "Start search"); Add(27, "BUTTON", "Cancel / discard preview");
            Add(28, "BUTTON", "Verify candidate in game"); Add(29, "BUTTON", "Accept result as draft");
            Add(30, "STATIC", ""); Add(31, "STATIC", "");
            Add(32, "COMBOBOX", "");
            foreach(var mode in new[] {"Our beam search", "Our exhaustive input chain", "Wave search"}) N.SendText(_controls[32],0x143,IntPtr.Zero,mode);
            N.SendMessage(_controls[32],0x14E,IntPtr.Zero,IntPtr.Zero);
            Add(33, "EDIT", "R:1-30; R+J:1-6; R:1-40");
            N.SendMessage(_controls[33], 0xC5, new IntPtr(4000), IntPtr.Zero);
            Add(40, "STATIC", "Goal camera keys: click a key to rebind. Mouse wheel zooms; click in the game to set the goal; Esc cancels.");
            Add(41, "BUTTON", "Up: UpArrow"); Add(42, "BUTTON", "Down: DownArrow");
            Add(43, "BUTTON", "Left: LeftArrow"); Add(44, "BUTTON", "Right: RightArrow");
            Add(45, "STATIC", "Experimental static-platform simulation. Native verification required. Accept replaces inputs from Start frame onward; Save edits commits.");
            Fonts(); Layout(); _initialised = false; Refresh();
            Send(new Command { Action = "searchOpen" });
            if (_visible) N.ShowWindow(_handle, 5);
        }
        private void Add(int id, string kind, string value, bool check = false)
        {
            uint style = kind == "COMBOBOX" ? 0x00200003u : kind == "EDIT" ? 0x00800080u : kind == "BUTTON" ? check ? 3u : 11u : 0u;
            var handle = N.CreateWindowEx(0, kind, value, 0x50010000 | style, 0, 0, 1, 1, _handle, new IntPtr(id), N.GetModuleHandle(null), IntPtr.Zero);
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            _controls[id] = handle;
            if (check) { N.SetWindowTheme(handle, "", ""); N.SendMessage(handle, 0xF1, new IntPtr(1), IntPtr.Zero); }
            if (kind == "EDIT") N.SendMessage(handle, 0xC5, new IntPtr(32), IntPtr.Zero);
        }
        private void Fonts() { _theme.Scale(_scale); foreach (var handle in _controls.Values) N.SendMessage(handle, 0x30, _theme.Normal, new IntPtr(1)); }
        private void Move(int id, int x, int y, int width, int height) => N.MoveWindow(_controls[id], (int)(x * _scale), (int)((y - _scroll) * _scale), (int)(width * _scale), (int)(height * _scale), true);
        private void Layout()
        {
            if (_controls.Count == 0 || _handle == IntPtr.Zero) return;
            N.GetClientRect(_handle, out var rect); int w = (int)(rect.Right / _scale), viewport = (int)(rect.Bottom / _scale), h = Math.Max(900, viewport), half = (w - 48) / 2;
            _scroll = Math.Max(0, Math.Min(_scroll, h - viewport));
            var scroll = new N.Scroll { Size = (uint)Marshal.SizeOf<N.Scroll>(), Mask = 7, Min = 0, Max = h - 1, Page = (uint)Math.Max(1, viewport), Position = _scroll };
            N.SetScrollInfo(_handle, 1, ref scroll, true);
            Move(1, 20, 16, w - 40, 32);
            Move(2, 20, 62, 120, 24); Move(3, 150, 58, 100, 28); Move(4, 266, 58, half - 246, 28);
            Move(5, half + 40, 62, 150, 24); Move(6, w - 130, 58, 110, 28);
            Move(7, 20, 102, 170, 24); Move(8, 200, 98, 100, 28);
            Move(9, half + 40, 102, 160, 24); Move(10, w - 130, 98, 110, 28);
            Move(11, 20, 142, 170, 24); Move(12, 200, 138, 100, 28);
            Move(13, half + 40, 142, 160, 24); Move(14, w - 130, 138, 110, 28);
            Move(15, 20, 182, 170, 24); Move(16, 200, 178, half - 180, 28); Move(17, half + 40, 178, half, 28);
            Move(18, 20, 218, half, 32); Move(19, half + 40, 218, half, 32);
            Move(20, 20, 266, w - 40, 24);
            for (int i = 0; i < 4; i++) Move(21 + i, 20 + i * (w - 40) / 4, 296, (w - 40) / 4, 26);
            Move(32, 20, 332, w - 40, 150); Move(33, 20, 368, w - 40, 28);
            Move(25, 20, 406, w - 40, 40);
            Move(26, 20, 456, half, 32); Move(27, half + 40, 456, half, 32);
            Move(28, 20, 498, half, 32); Move(29, half + 40, 498, half, 32);
            Move(30, 20, 550, w - 40, Math.Max(110, h - 754));
            Move(40, 20, h - 194, w - 40, 42);
            for (int i = 0; i < 4; i++) Move(41 + i, 20 + i * (w - 40) / 4, h - 146, (w - 40) / 4 - 6, 30);
            Move(45, 20, h - 106, w - 40, 42); Move(31, 20, h - 58, w - 40, 48);
        }
        private IntPtr Procedure(IntPtr hwnd, uint message, IntPtr wp, IntPtr lp)
        {
            try
            {
                switch (message)
                {
                    case 0x10: Send(new Command { Action = "searchEndGoal" }); N.DestroyWindow(hwnd); return IntPtr.Zero;
                    case 0x2: _handle = IntPtr.Zero; _controls.Clear(); _text.Clear(); _capture = -1; return IntPtr.Zero;
                    case 0x24:
                        var size = Marshal.PtrToStructure<N.MinMax>(lp); size.MinTrack.X = (int)(820 * _scale); size.MinTrack.Y = (int)(600 * _scale);
                        Marshal.StructureToPtr(size, lp, false); return IntPtr.Zero;
                    case 0x5: Layout(); return IntPtr.Zero;
                    case 0x20A: _scroll -= Math.Sign((short)((wp.ToInt64() >> 16) & 0xffff)) * 60; Layout(); return IntPtr.Zero;
                    case 0x115:
                        var info = new N.Scroll { Size = (uint)Marshal.SizeOf<N.Scroll>(), Mask = 0x10 }; N.GetScrollInfo(hwnd, 1, ref info);
                        int code = (int)(wp.ToInt64() & 0xffff);
                        _scroll = code == 0 ? _scroll - 30 : code == 1 ? _scroll + 30 : code == 2 ? _scroll - 300 : code == 3 ? _scroll + 300 : code == 4 || code == 5 ? info.Track : _scroll;
                        Layout(); return IntPtr.Zero;
                    case 0x2E0:
                        _scale = (wp.ToInt64() & 0xffff) / 96f; var rect = Marshal.PtrToStructure<N.Rect>(lp);
                        N.MoveWindow(hwnd, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, true); Fonts(); Layout(); return IntPtr.Zero;
                    case 0x111: if (((wp.ToInt64() >> 16) & 0xffff) == 0) Click((int)(wp.ToInt64() & 0xffff)); return IntPtr.Zero;
                    case 0x2B:
                        var item = Marshal.PtrToStructure<N.DrawItem>(lp); _theme.Button(item, item.Id == 26 || item.Id == 29, false, item.Id == 27); return new IntPtr(1);
                    case 0x133: case 0x134: case 0x135: case 0x138:
                        uint color = message == 0x133 || message == 0x134 ? NativeTheme.Input : NativeTheme.Back;
                        N.SetBkColor(wp, color); N.SetTextColor(wp, NativeTheme.Text); return _theme.Brush(color);
                    case 0x14: return new IntPtr(1);
                    case 0xF:
                        var dc = N.BeginPaint(hwnd, out var paint); N.GetClientRect(hwnd, out var area);
                        N.FillRect(dc, ref area, _theme.Brush(NativeTheme.Back)); N.EndPaint(hwnd, ref paint); return IntPtr.Zero;
                }
            }
            catch (Exception e) { _localError = e.Message; }
            return N.DefWindowProc(hwnd, message, wp, lp);
        }
        public bool ProcessKey(ref N.Message message)
        {
            if (_handle == IntPtr.Zero || message.Id != 0x100) return false;
            bool own = message.Hwnd == _handle || _controls.ContainsValue(message.Hwnd); if (!own) return false;
            int key = (int)message.WParam.ToUInt64();
            if (_capture >= 0)
            {
                if (key == 27) { _capture = -1; return true; }
                string name = GoalCameraBindings.FromVirtualKey(key);
                if (name != null) { Send(new Command { Action = "searchCameraBinding", Start = _capture, Name = name }); _capture = -1; }
                return true;
            }
            if (_channel.Latest?.Search.PlacingGoal == true)
            {
                if (key == 27) { Send(new Command { Action = "searchEndGoal" }); return true; }
                var name = new StringBuilder(64); N.GetClassName(message.Hwnd, name, name.Capacity);
                if (name.ToString().Equals("Edit", StringComparison.OrdinalIgnoreCase)) return false;
                var keys = _channel.Latest.Search.CameraBindings.Keys;
                for (int i = 0; i < keys.Length; i++) if (GoalCameraBindings.VirtualKey(keys[i]) == key)
                { Send(new Command { Action = "searchCameraPan", X = i == 2 ? -1 : i == 3 ? 1 : 0, Y = i == 0 ? 1 : i == 1 ? -1 : 0 }); return true; }
            }
            return false;
        }
        private bool Checked(int id) => N.SendMessage(_controls[id], 0xF0, IntPtr.Zero, IntPtr.Zero).ToInt32() == 1;
        private string Text(int id) { var s = new StringBuilder(1024); N.GetWindowText(_controls[id], s, s.Capacity); return s.ToString(); }
        private void Text(int id, string value)
        { if (_text.TryGetValue(id, out var old) && old == value) return; N.SetWindowText(_controls[id], value); _text[id] = value; }
        private int Number(int id, int minimum, int maximum)
        { if (!SearchFiles.TryInt(Text(id), minimum, maximum, out int value)) throw new ArgumentException("Enter a whole number between " + minimum + " and " + maximum + "."); return value; }
        private float Decimal(int id)
        { if (!float.TryParse(Text(id).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || float.IsNaN(value) || float.IsInfinity(value)) throw new ArgumentException("Enter a finite decimal number."); return value; }
        private SearchOptions Options()
        {
            var current = _channel.Latest.Search.Options.Copy();
            current.StartFrame = Number(3, 0, _channel.Latest.Frames); current.Horizon = Number(6, 1, 20000);
            current.Seconds = Number(8, 1, 86400); current.Workers = Number(10, 1, 32); current.ChunkFrames = Number(14, 1, 30);
            current.GoalRadius = Decimal(12); current.GoalX = Decimal(16); current.GoalY = Decimal(17); current.GoalSet = true;
            int mode=N.SendMessage(_controls[32],0x147,IntPtr.Zero,IntPtr.Zero).ToInt32();
            current.Mode = mode==2 ? "wave" : mode==1 ? "chain" : "beam"; current.Chain = Text(33);
            current.Abilities = (Checked(21) ? SearchAbilities.Jump : 0) | (Checked(22) ? SearchAbilities.AirJump : 0) |
                (Checked(23) ? SearchAbilities.Dash : 0) | (Checked(24) ? SearchAbilities.WallJump : 0);
            current.Validate(_channel.Latest.Frames); return current;
        }
        private void Click(int id)
        {
            if (id >= 41 && id <= 44) { _capture = id - 41; _localError = "Press a new camera key. Escape cancels."; return; }
            switch (id)
            {
                case 4: N.SetWindowText(_controls[3], (_channel.Latest?.Cursor ?? 0).ToString()); break;
                case 18: Send(new Command { Action = "searchGoalPlayer" }); break;
                case 19: Send(new Command { Action = "searchGoalCamera" }); break;
                case 26: Send(new Command { Action = "searchStart", SearchOptions = Options() }); break;
                case 27: Send(new Command { Action = "searchCancel" }); break;
                case 28: RequireMatchingOptions(); Send(new Command { Action = "searchVerify" }); break;
                case 29: RequireMatchingOptions(); Send(new Command { Action = "searchAccept" }); break;
            }
        }
        private void RequireMatchingOptions()
        {
            if (SearchFiles.Hash(Options()) != SearchFiles.Hash(_channel.Latest.Search.Options))
                throw new InvalidOperationException("These controls changed. Start a new search before verifying or accepting a candidate.");
        }
        private void Send(Command command) { _localError = _channel.Send(command) ? "" : "The Studio command queue is unavailable."; }
        public void Refresh()
        {
            if (_handle == IntPtr.Zero || _channel.Latest == null) return;
            var snapshot = _channel.Latest; var search = snapshot.Search; var options = search.Options; var result = search.Result;
            if (!_initialised)
            {
                foreach (var value in new[] { (3, result.JobId.Length > 0 ? options.StartFrame : snapshot.Cursor), (6, options.Horizon), (8, options.Seconds), (10, options.Workers), (14, options.ChunkFrames) })
                    N.SetWindowText(_controls[value.Item1], value.Item2.ToString(CultureInfo.InvariantCulture));
                N.SetWindowText(_controls[12], options.GoalRadius.ToString("R", CultureInfo.InvariantCulture)); _initialised = true;
                for (int i = 0; i < 4; i++) N.SendMessage(_controls[21 + i], 0xF1, new IntPtr(((int)options.Abilities & (1 << i)) != 0 ? 1 : 0), IntPtr.Zero);
                // UI order is Jump, Air jump, Dash, Wall jump: it matches the flag bits.
                N.SendMessage(_controls[32], 0x14E, new IntPtr(options.Mode == "wave" ? 2 : options.Mode == "chain" ? 1 : 0), IntPtr.Zero);
                N.SetWindowText(_controls[33], options.Chain);
            }
            string goal = options.GoalSet + ":" + options.GoalX + ":" + options.GoalY;
            if (goal != _goalLabel)
            {
                N.SetWindowText(_controls[16], options.GoalX.ToString("R", CultureInfo.InvariantCulture));
                N.SetWindowText(_controls[17], options.GoalY.ToString("R", CultureInfo.InvariantCulture)); _goalLabel = goal;
            }
            Text(19, search.PlacingGoal ? "Exit goal camera" : "Place goal with free camera");
            Text(30, "Status: " + result.Status + (search.PlacingGoal ? " / GOAL CAMERA" : "") + "\r\n" +
                "Depth " + result.Depth + " / " + options.Horizon + " frames | Evaluations " + result.Evaluated + " | Simulated frames " + result.SimulatedFrames +
                "\r\nElapsed " + result.Seconds.ToString("0.0", CultureInfo.InvariantCulture) + " / " + options.Seconds + " s | PUs " + result.Pus + " | Unsupported rollouts " + result.Unsupported +
                (result.Seconds > 0 ? " | " + (result.SimulatedFrames / result.Seconds).ToString("0", CultureInfo.InvariantCulture) + " sim frames/s" : "") +
                "\r\nBest " + (result.BestFrames < 0 ? "--" : result.BestFrames + " frames") + " | Nearest " + (result.NearestDistance == double.MaxValue ? "--" : result.NearestDistance.ToString("0.0", CultureInfo.InvariantCulture)) +
                " | " + result.Verification + (search.Preview ? " | Native frame " + snapshot.Cursor + " / " + (options.StartFrame + result.Inputs.Length) : "") +
                (result.ChainCombinations > 0 ? "\r\nChain combinations: up to " + result.ChainCombinations : "") +
                (result.Algorithm=="wave" ? "\r\nArchive cells " + result.ArchiveCells + " | Splices " + result.Splices + " | Plateau changes " + result.PlateauDrifts + " | Cached states " + result.StateHits + " / replays " + result.StateReplays : "") + "\r\n" + result.Message);
            Text(31, _localError.Length > 0 ? _localError : snapshot.Error);
            Text(45, (result.Simulator==Igtap.Tas.Core.Search.EngineSearchSpec.Backend ? "Game-code simulator. " : "Experimental static-platform simulator. ") +
                "Native verification required. Accept stages replacement inputs from Start frame; Save edits commits them.");
            for (int i = 0; i < 4; i++) Text(41 + i, new[] { "Up: ", "Down: ", "Left: ", "Right: " }[i] + search.CameraBindings.Keys[i]);
            N.EnableWindow(_controls[26], search.CanStart); N.EnableWindow(_controls[28], !search.Busy && !search.Preview && result.Inputs.Length > 0);
            N.EnableWindow(_controls[29], search.CanAccept); N.EnableWindow(_controls[18], !search.Busy && snapshot.Active && snapshot.Paused);
            N.EnableWindow(_controls[19], !search.Busy && snapshot.Active && snapshot.Paused);
            foreach (int id in new[] { 3, 4, 6, 8, 10, 12, 14, 16, 17, 21, 22, 23, 24, 32, 33 })
                N.EnableWindow(_controls[id], !search.Busy && !search.Preview && !search.PlacingGoal);
        }
        public void Dispose()
        {
            if (_handle != IntPtr.Zero) N.DestroyWindow(_handle);
            if (_className != null) { N.UnregisterClass(_className, N.GetModuleHandle(null)); _className = null; }
            _theme.Dispose();
        }
    }
}
