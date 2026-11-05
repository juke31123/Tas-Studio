using System;
using Igtap.Tas.Core;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Igtap.Tas.DesktopModel;
using N = Igtap.Tas.UI.NativeMethods;

namespace Igtap.Tas.UI
{
    // Second top-level window, owned/pumped by the studio's native UI thread.
    // Painting is a single range command on mouse-up, not one write per pixel.
    internal sealed class NativeFrameEditor : IDisposable
    {
        private readonly StudioChannel _channel;
        private readonly IntPtr _owner;
        private readonly bool _visible;
        private readonly N.WindowProc _procedure;
        private readonly Dictionary<int, IntPtr> _controls = new Dictionary<int, IntPtr>();
        private readonly List<IntPtr> _brushes = new List<IntPtr>();
        private readonly NativeTheme _theme = new NativeTheme();
        private string _className, _localError = "", _pending = "";
        private IntPtr _handle, _font, _cellEdit;
        private IntPtr _bufferDc, _bufferBitmap, _originalBitmap;
        private int _bufferWidth, _bufferHeight;
        private string _heading, _footer, _pauseLabel, _stepLabel;
        private float _scale = 1;
        private Snapshot _snapshot;
        private int _top, _anchor, _end, _column = 1, _editFrame, _editColumn, _revision;
        private int _strokeStart, _strokeEnd, _strokeColumn, _strokeRevision;
        private int _pendingSequence;
        private bool _stroke, _strokeHeld, _follow = true;
        private bool _saveAfterCell;
        private const int GridY = 140, Header = 28, RowHeight = 24;
        private static readonly string[] Titles = { "Frame", "U", "D", "L", "R", "Jump", "Dash", "Reset", "X", "Y" };
        internal IntPtr Handle => _handle;
        public NativeFrameEditor(StudioChannel channel, IntPtr owner, bool visible)
        { _channel = channel; _owner = owner; _visible = visible; _procedure = Procedure; }
        public void Open()
        {
            if (_handle != IntPtr.Zero) { if (_visible) { N.ShowWindow(_handle, 9); N.SetForegroundWindow(_handle); } return; }
            if (_className == null)
            {
                _className = "TasStudio.FrameEditor." + Guid.NewGuid().ToString("N");
                foreach (uint color in new uint[] { NativeTheme.Back, NativeTheme.Panel, NativeTheme.Raised, NativeTheme.Held, 0x604129, NativeTheme.Border,
                    0x202048, 0x3535A0, 0x35357C, 0x303064 }) _brushes.Add(N.CreateSolidBrush(color));
                var wc = new N.WindowClass { Size = (uint)Marshal.SizeOf<N.WindowClass>(), Style = 8, Procedure = Marshal.GetFunctionPointerForDelegate(_procedure),
                    Instance = N.GetModuleHandle(null), Cursor = N.LoadCursor(IntPtr.Zero, new IntPtr(32512)), Background = _brushes[0], Name = _className };
                if (N.RegisterClassEx(ref wc) == 0) { _className = null; throw new Win32Exception(Marshal.GetLastWin32Error()); }
            }
            _handle = N.CreateWindowEx(0, _className, "Tas Studio - Frame Editor", 0x02EF0000,
                _visible ? unchecked((int)0x80000000) : -30000, _visible ? unchecked((int)0x80000000) : -30000,
                800, 820, _owner, IntPtr.Zero, N.GetModuleHandle(null), IntPtr.Zero);
            if (_handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            NativeTheme.DarkTitle(_handle);
            _scale = Math.Max(1, N.GetDpiForWindow(_handle) / 96f);
            _theme.Scale(_scale);
            Add(1, "BUTTON", "Pause", 0); Add(2, "BUTTON", "Step", 0); Add(3, "BUTTON", "Undo", 0);
            Add(4, "BUTTON", "Insert", 0); Add(5, "BUTTON", "Delete", 0); Add(6, "BUTTON", "Clear", 0);
            Add(7, "BUTTON", "Follow cursor", 3); Add(8, "EDIT", "0", 0x00800080); Add(9, "BUTTON", "Go to frame", 0);
            Add(10, "STATIC", "", 0); Add(11, "STATIC", "", 0);
            Add(12, "BUTTON", "Save edits", 0); Add(13, "BUTTON", "Discard", 0);
            N.SendMessage(_controls[7], 0xF1, new IntPtr(_follow ? 1 : 0), IntPtr.Zero);
            Fonts(); Layout(); Refresh();
            if (_visible) N.ShowWindow(_handle, 5);
        }
        private void Add(int id, string kind, string text, uint style)
        {
            if (kind == "BUTTON" && style == 0) style = 11;
            if (kind == "STATIC" && id == 10) style = 13;
            var control = N.CreateWindowEx(0, kind, text, 0x50010000 | style, 0, 0, 1, 1, _handle, new IntPtr(id), N.GetModuleHandle(null), IntPtr.Zero);
            if (control == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            _controls.Add(id, control);
            if (kind == "BUTTON" && style == 3) N.SetWindowTheme(control, "", "");
        }
        private void Fonts()
        {
            _theme.Scale(_scale); _font = _theme.Mono;
            foreach (var c in _controls.Values) N.SendMessage(c, 0x30, _theme.Normal, new IntPtr(1));
        }
        private void Layout()
        {
            if (_handle == IntPtr.Zero || _controls.Count == 0) return;
            CloseCell(false);
            int width = Width, button = (width - 32) / 6;
            Move(10, 16, 8, width - 32, 44);
            for (int i = 0; i < 6; i++) Move(1 + i, 16 + button * i, 58, button - 4, 30);
            Move(7, 16, 98, 140, 28); Move(8, 160, 98, 80, 28); Move(9, 248, 98, 110, 28);
            Move(12, 370, 98, 138, 28); Move(13, 516, 98, 140, 28);
            Move(11, 16, Height - 65, width - 32, 58); UpdateScroll(); Invalidate();
        }
        private int Width { get { N.GetClientRect(_handle, out var r); return (int)(r.Right / _scale); } }
        private int Height { get { N.GetClientRect(_handle, out var r); return (int)(r.Bottom / _scale); } }
        private int VisibleRows => Math.Max(1, (Height - GridY - Header - 70) / RowHeight);
        private int CellWidth => Math.Max(40, (Width - 32 - 88) / 9);
        private N.Rect Cell(int row, int column)
        {
            int left = column == 0 ? 16 : 104 + (column - 1) * CellWidth;
            int top = GridY + Header + (row - _top) * RowHeight;
            return Rect(left, top, column == 0 ? 88 : CellWidth, RowHeight);
        }
        private N.Rect Rect(int x, int y, int w, int h) => new N.Rect { Left = (int)(x * _scale), Top = (int)(y * _scale), Right = (int)((x + w) * _scale), Bottom = (int)((y + h) * _scale) };
        private void Move(int id, int x, int y, int w, int h) => N.MoveWindow(_controls[id], (int)(x * _scale), (int)(y * _scale), (int)(w * _scale), (int)(h * _scale), true);
        private void Invalidate() { if (_handle != IntPtr.Zero) N.InvalidateRect(_handle, IntPtr.Zero, false); }
        public bool ProcessKey(ref N.Message message)
        {
            if (_cellEdit == IntPtr.Zero || message.Hwnd != _cellEdit || message.Id != 0x100) return false;
            try
            {
                if (message.WParam.ToUInt64() == 13) { CloseCell(true); return true; }
                if (message.WParam.ToUInt64() == 27) { CloseCell(false); return true; }
                if (message.WParam.ToUInt64() == 83 && N.GetKeyState(17) < 0) { Button(12); return true; }
                // Other keys continue to the Edit control; the message loop suppresses
                // TAS shortcuts while any text field has focus.
            }
            catch (Exception e) { _localError = e.Message; }
            return false;
        }
        private IntPtr Procedure(IntPtr hwnd, uint message, IntPtr wp, IntPtr lp)
        {
            try
            {
                switch (message)
                {
                    case 0x10: _saveAfterCell = false; CancelStroke(); CloseCell(false); N.DestroyWindow(hwnd); return IntPtr.Zero;
                    case 0x2: ReleaseBuffer(); _heading = _footer = _pauseLabel = _stepLabel = null; _handle = IntPtr.Zero; _controls.Clear(); _cellEdit = IntPtr.Zero; return IntPtr.Zero;
                    case 0x24:
                        var size = Marshal.PtrToStructure<N.MinMax>(lp); size.MinTrack.X = (int)(720 * _scale); size.MinTrack.Y = (int)(520 * _scale);
                        Marshal.StructureToPtr(size, lp, false); return IntPtr.Zero;
                    case 0x5: Layout(); return IntPtr.Zero;
                    case 0x2E0:
                        _scale = (wp.ToInt64() & 0xffff) / 96f; var rect = Marshal.PtrToStructure<N.Rect>(lp);
                        N.MoveWindow(hwnd, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, true); Fonts(); Layout(); return IntPtr.Zero;
                    case 0xF: Paint(hwnd); return IntPtr.Zero;
                    case 0x2B:
                        var item = Marshal.PtrToStructure<N.DrawItem>(lp);
                        if (item.Type == 4) _theme.Button(item, item.Id == 1 || item.Id == 12, false, item.Id == 5);
                        else if (item.Id == 10) _theme.Header(item, _snapshot, "FRAME EDITOR", true);
                        return new IntPtr(1);
                    case 0x14: return new IntPtr(1);
                    case 0x201: MouseDown(lp, false); return IntPtr.Zero;
                    case 0x203: MouseDown(lp, true); return IntPtr.Zero;
                    case 0x200: MouseMove(lp); return IntPtr.Zero;
                    case 0x202: FinishStroke(); return IntPtr.Zero;
                    case 0x215: _stroke = false; Invalidate(); return IntPtr.Zero;
                    case 0x20A: ScrollTo(_top - Math.Sign((short)((wp.ToInt64() >> 16) & 0xffff)) * 3); return IntPtr.Zero;
                    case 0x115:
                        var info = new N.Scroll { Size = (uint)Marshal.SizeOf<N.Scroll>(), Mask = 0x10 }; N.GetScrollInfo(hwnd, 1, ref info);
                        int code = (int)(wp.ToInt64() & 0xffff);
                        ScrollTo(code == 0 ? _top - 1 : code == 1 ? _top + 1 : code == 2 ? _top - VisibleRows : code == 3 ? _top + VisibleRows : code == 4 || code == 5 ? info.Track : _top); return IntPtr.Zero;
                    case 0x100:
                        int key = wp.ToInt32();
                        if (key == 27) { CancelStroke(); CloseCell(false); }
                        else if (key == 46) RowCommand("deleteRows");
                        else if (key == 45) RowCommand("insertRows");
                        else if (key == 90 && N.GetKeyState(17) < 0) Send(new Command { Action = "undo" });
                        else if (key == 83 && N.GetKeyState(17) < 0) Button(12);
                        else if (key == 32 && Editable && _column < 8 && TryRow(_anchor, out var selected))
                            Send(new Command { Action = "paint", Start = Math.Min(_anchor, _end), Count = Math.Abs(_end - _anchor) + 1,
                                Column = _column - 1, CellHeld = !Held(selected, _column), Revision = _snapshot.Revision });
                        else if (key == 38 || key == 40) { _end = Math.Max(0, Math.Min(_snapshot?.Frames ?? 0, _end + (key == 38 ? -1 : 1))); if (N.GetKeyState(16) >= 0) _anchor = _end; ScrollTo(Math.Max(0, _end - VisibleRows / 2)); }
                        return IntPtr.Zero;
                    case 0x111:
                        int id = (int)(wp.ToInt64() & 0xffff);
                        if (((wp.ToInt64() >> 16) & 0xffff) == 0) Button(id);
                        return IntPtr.Zero;
                    case 0x133: case 0x135: case 0x138:
                        uint surface = message == 0x133 ? NativeTheme.Input : NativeTheme.Panel;
                        N.SetBkColor(wp, surface); N.SetTextColor(wp, message == 0x138 ? NativeTheme.Muted : NativeTheme.Text);
                        return _theme.Brush(surface);
                }
            }
            catch (Exception e) { _localError = e.Message; Invalidate(); }
            return N.DefWindowProc(hwnd, message, wp, lp);
        }
        private bool Editable => _snapshot?.CanEditTimeline == true && _pending.Length == 0;
        private void Button(int id)
        {
            switch (id)
            {
                case 1: if (_snapshot?.CanTransport == true) Send(new Command { Action = "pause" }); break;
                case 2: if (_snapshot?.CanTransport == true) Send(new Command { Action = "step" }); break;
                case 3: if (Editable) Send(new Command { Action = "undo" }); break;
                case 4: RowCommand("insertRows"); break;
                case 5: RowCommand("deleteRows"); break;
                case 6: RowCommand("clearRows"); break;
                case 7: _follow = N.SendMessage(_controls[7], 0xF0, IntPtr.Zero, IntPtr.Zero).ToInt32() == 1; break;
                case 9: if (int.TryParse(Text(_controls[8]), out var target)) ScrollTo(target); break;
                case 12:
                    if (!Editable) break;
                    if (_cellEdit != IntPtr.Zero) { CloseCell(true); _saveAfterCell = true; }
                    else if (_snapshot.PendingEdits) Send(new Command { Action = "saveEdits" });
                    break;
                case 13:
                    if (!Editable) break;
                    _saveAfterCell = false; CloseCell(false);
                    if (_snapshot.PendingEdits) Send(new Command { Action = "discardEdits" });
                    break;
            }
        }
        private void RowCommand(string action)
        {
            if (!Editable) { _localError = "Pause the run and wait for any pending edit."; return; }
            int start = Math.Min(_anchor, _end), last = Math.Max(_anchor, _end);
            int count = last - start + 1;
            if (action != "insertRows") count = Math.Min(count, _snapshot.Frames - start);
            if (count < 1) return;
            Send(new Command { Action = action, Start = start, Count = count, Revision = _snapshot.Revision });
        }
        private bool Hit(IntPtr lp, out int row, out int column)
        {
            int x = (int)((short)(lp.ToInt64() & 0xffff) / _scale), y = (int)((short)((lp.ToInt64() >> 16) & 0xffff) / _scale);
            row = _top + (y - GridY - Header) / RowHeight; column = x < 104 ? 0 : 1 + (x - 104) / CellWidth;
            return y >= GridY + Header && row < _top + VisibleRows && row <= (_snapshot?.Frames ?? -1) && x >= 16 && column < 10;
        }
        private void MouseDown(IntPtr lp, bool twice)
        {
            CloseCell(true); N.SetFocus(_handle);
            if (!Hit(lp, out int row, out int column)) return;
            _follow = false; N.SendMessage(_controls[7], 0xF1, IntPtr.Zero, IntPtr.Zero);
            if (column == 0) { if (N.GetKeyState(16) >= 0) _anchor = row; _end = row; Invalidate(); return; }
            _column = column; _anchor = _end = row;
            if (!Editable) { _localError = "Pause the run and wait for any pending edit."; Invalidate(); return; }
            if (!TryRow(row, out var frame)) return;
            if (column >= 8) { if (twice) OpenCell(row, column, frame); Invalidate(); return; }
            _stroke = true; _strokeStart = _strokeEnd = row; _strokeColumn = column;
            _strokeRevision = _snapshot.Revision; _strokeHeld = !Held(frame, column);
            N.SetCapture(_handle); Invalidate();
        }
        private void MouseMove(IntPtr lp)
        {
            if (!_stroke) return;
            if (Hit(lp, out int row, out _)) { _strokeEnd = _end = row; Invalidate(); }
        }
        private void FinishStroke()
        {
            if (!_stroke) return;
            _stroke = false; N.ReleaseCapture();
            Send(new Command { Action = "paint", Start = Math.Min(_strokeStart, _strokeEnd), Count = Math.Abs(_strokeEnd - _strokeStart) + 1,
                Column = _strokeColumn - 1, CellHeld = _strokeHeld, Revision = _strokeRevision }); Invalidate();
        }
        private void CancelStroke() { _stroke = false; N.ReleaseCapture(); Invalidate(); }
        private void OpenCell(int row, int column, FrameRow frame)
        {
            var rect = Cell(row, column); _editFrame = row; _editColumn = column; _revision = _snapshot.Revision;
            _cellEdit = N.CreateWindowEx(0, "EDIT", (column == 8 ? frame.X : frame.Y).ToString(CultureInfo.InvariantCulture), 0x50810080,
                rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, _handle, new IntPtr(99), N.GetModuleHandle(null), IntPtr.Zero);
            N.SendMessage(_cellEdit, 0x30, _font, new IntPtr(1)); N.SendMessage(_cellEdit, 0xC5, new IntPtr(16), IntPtr.Zero);
            N.SetFocus(_cellEdit); N.SendMessage(_cellEdit, 0xB1, IntPtr.Zero, new IntPtr(-1));
        }
        private void CloseCell(bool commit)
        {
            if (_cellEdit == IntPtr.Zero) return;
            if (commit)
            {
                if (!float.TryParse(Text(_cellEdit).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || float.IsNaN(value) || float.IsInfinity(value) || Math.Abs(value) > 1)
                    throw new ArgumentException("Axis values must be between -1 and 1 (dot or comma).");
                Send(new Command { Action = "paint", Start = _editFrame, Count = 1, Column = _editColumn - 1, Value = value, Revision = _revision });
            }
            N.DestroyWindow(_cellEdit); _cellEdit = IntPtr.Zero; Invalidate();
        }
        private static string Text(IntPtr control) { var buffer = new StringBuilder(512); N.GetWindowText(control, buffer, buffer.Capacity); return buffer.ToString(); }
        private void Send(Command command)
        {
            if (!_channel.Send(command)) { _localError = "Command not sent: studio closing or command queue full."; return; }
            _pending = command.Id; _pendingSequence = command.Sequence; _localError = "";
        }
        private bool TryRow(int row, out FrameRow frame)
        {
            frame = null; if (_snapshot == null) return false;
            if (row == _snapshot.Frames) { frame = new FrameRow { Frame = row }; return true; }
            int index = row - _snapshot.Page;
            if (index < 0 || index >= _snapshot.Timeline.Length) return false;
            frame = _snapshot.Timeline[index]; return true;
        }
        private static bool Held(FrameRow f, int column) => column == 1 ? f.Y > 0 : column == 2 ? f.Y < 0 : column == 3 ? f.X < 0 : column == 4 ? f.X > 0 : column == 5 ? f.Jump : column == 6 ? f.Dash : f.Reset;
        private void ScrollTo(int top)
        {
            if (_stroke || _cellEdit != IntPtr.Zero) return;
            _top = Math.Max(0, Math.Min(_snapshot?.Frames ?? 0, top)); _follow = false;
            N.SendMessage(_controls[7], 0xF1, IntPtr.Zero, IntPtr.Zero); _channel.Page = _top; UpdateScroll(); Invalidate();
        }
        private void UpdateScroll()
        {
            var scroll = new N.Scroll { Size = (uint)Marshal.SizeOf<N.Scroll>(), Mask = 7, Min = 0,
                Max = (_snapshot?.Frames ?? 0) + VisibleRows - 1, Page = (uint)VisibleRows, Position = _top };
            N.SetScrollInfo(_handle, 1, ref scroll, true);
        }
        public void Refresh()
        {
            if (_handle == IntPtr.Zero) return;
            var snapshot = _channel.Latest; if (snapshot == null) return;
            bool gridChanged = !SameGrid(_snapshot, snapshot);
            int oldTop = _top, oldAnchor = _anchor, oldEnd = _end;
            if (_pending == snapshot.Ack || snapshot.CompletedSequence >= _pendingSequence) _pending = "";
            if (_snapshot != null && snapshot.Revision != _snapshot.Revision) { CancelStroke(); CloseCell(false); }
            _snapshot = snapshot;
            SetLabel(1, ref _pauseLabel, snapshot.Bindings.Label(TasShortcut.Pause) + " Pause");
            SetLabel(2, ref _stepLabel, snapshot.Bindings.Label(TasShortcut.Step) + " Step");
            if (_saveAfterCell && _pending.Length == 0)
            {
                _saveAfterCell = false;
                if (snapshot.Error.Length == 0 && snapshot.PendingEdits && snapshot.CanEditTimeline) Send(new Command { Action = "saveEdits" });
            }
            _anchor = Math.Min(_anchor, snapshot.Frames); _end = Math.Min(_end, snapshot.Frames);
            if (_follow && !_stroke && _cellEdit == IntPtr.Zero) _top = Math.Max(0, snapshot.Cursor - VisibleRows / 2);
            _top = Math.Min(_top, snapshot.Frames); _channel.Page = _top;
            SetLabel(10, ref _heading, snapshot.Name + (snapshot.PendingEdits ? " * UNSAVED EDITS" : "") + " / " + snapshot.Mode + (snapshot.Paused ? " [PAUSED]" : "") + "  Frame " + snapshot.Cursor + " / " + snapshot.Frames);
            string info = _localError.Length > 0 ? _localError : snapshot.Error.Length > 0 ? snapshot.Error : _pending.Length > 0 ? "Applying edit..." : snapshot.Status;
            SetLabel(11, ref _footer, info + "\r\n" + (snapshot.PendingEdits ? "Save edits applies the batch and rebuilds if needed. Discard keeps the current run." : "Click/drag inputs. Double-click X/Y to type. Shift+frame selects rows. * = unverified."));
            for (int i = 3; i <= 6; i++) N.EnableWindow(_controls[i], Editable);
            N.EnableWindow(_controls[1], snapshot.CanTransport); N.EnableWindow(_controls[2], snapshot.CanTransport);
            N.EnableWindow(_controls[12], Editable && (snapshot.PendingEdits || _cellEdit != IntPtr.Zero));
            N.EnableWindow(_controls[13], Editable && (snapshot.PendingEdits || _cellEdit != IntPtr.Zero));
            if (gridChanged || oldTop != _top || oldAnchor != _anchor || oldEnd != _end)
            {
                UpdateScroll();
                var grid = Rect(16, GridY, Width - 32, Header + VisibleRows * RowHeight);
                N.InvalidateRect(_handle, ref grid, false);
            }
        }
        private void SetLabel(int id, ref string previous, string value)
        {
            if (previous == value) return;
            N.SetWindowText(_controls[id], value); previous = value;
        }
        private static bool SameGrid(Snapshot a, Snapshot b)
        {
            if (a == null || a.Cursor != b.Cursor || a.Frames != b.Frames || a.Page != b.Page || a.Revision != b.Revision || a.Timeline.Length != b.Timeline.Length) return false;
            for (int i = 0; i < a.Timeline.Length; i++)
            {
                var x = a.Timeline[i]; var y = b.Timeline[i];
                if (x.Frame != y.Frame || x.Pus != y.Pus || x.X != y.X || x.Y != y.Y || x.Jump != y.Jump || x.Dash != y.Dash || x.Reset != y.Reset || x.Verified != y.Verified) return false;
            }
            return true;
        }
        private void PrepareBuffer(IntPtr dc, int width, int height)
        {
            if (_bufferDc != IntPtr.Zero && width == _bufferWidth && height == _bufferHeight) return;
            ReleaseBuffer();
            _bufferDc = N.CreateCompatibleDC(dc);
            // Use the display DC, not the initially monochrome memory DC, for the bitmap.
            _bufferBitmap = N.CreateCompatibleBitmap(dc, width, height);
            if (_bufferDc == IntPtr.Zero || _bufferBitmap == IntPtr.Zero)
            {
                ReleaseBuffer(); throw new Win32Exception("Could not allocate the frame editor drawing buffer.");
            }
            _originalBitmap = N.SelectObject(_bufferDc, _bufferBitmap);
            _bufferWidth = width; _bufferHeight = height;
        }
        private void ReleaseBuffer()
        {
            if (_originalBitmap != IntPtr.Zero) N.SelectObject(_bufferDc, _originalBitmap);
            if (_bufferBitmap != IntPtr.Zero) N.DeleteObject(_bufferBitmap);
            if (_bufferDc != IntPtr.Zero) N.DeleteDC(_bufferDc);
            _originalBitmap = _bufferBitmap = _bufferDc = IntPtr.Zero;
            _bufferWidth = _bufferHeight = 0;
        }
        private void Paint(IntPtr hwnd)
        {
            var target = N.BeginPaint(hwnd, out var paint);
            try
            {
                N.GetClientRect(hwnd, out var background);
                if (target == IntPtr.Zero || background.Right <= 0 || background.Bottom <= 0) return;
                PrepareBuffer(target, background.Right, background.Bottom);
                var dc = _bufferDc;
                var old = N.SelectObject(dc, _font);
                try
                {
                    N.SetBkMode(dc, 1); N.SetTextColor(dc, NativeTheme.Text);
                    N.FillRect(dc, ref background, _brushes[0]);
                    _theme.Box(dc, Rect(16, 54, Width - 32, 76), NativeTheme.Panel);
                    for (int c = 0; c < 10; c++)
                    {
                        var rect = Cell(_top, c); rect.Top -= (int)(Header * _scale); rect.Bottom = rect.Top + (int)(Header * _scale);
                        N.FillRect(dc, ref rect, _brushes[2]); N.DrawText(dc, Titles[c], -1, ref rect, 0x25);
                    }
                    for (int i = 0; i < VisibleRows; i++)
                    {
                        int row = _top + i; if (_snapshot == null || row > _snapshot.Frames) break;
                        bool loaded = TryRow(row, out var f);
                        for (int c = 0; c < 10; c++)
                        {
                            var rect = Cell(row, c); rect.Right--; rect.Bottom--;
                            bool held = loaded && c > 0 && c < 8 && Held(f, c);
                            if (_stroke && c == _strokeColumn && row >= Math.Min(_strokeStart, _strokeEnd) && row <= Math.Max(_strokeStart, _strokeEnd)) held = _strokeHeld;
                            int brush = held ? 3 : row == _snapshot.Cursor ? 4 : row >= Math.Min(_anchor, _end) && row <= Math.Max(_anchor, _end) ? 2 : i % 2 == 0 ? 0 : 1;
                            if (loaded && f.Pus > 0) brush = held ? 7 : row == _snapshot.Cursor ? 8 :
                                row >= Math.Min(_anchor, _end) && row <= Math.Max(_anchor, _end) ? 9 : 6;
                            N.FillRect(dc, ref rect, _brushes[brush]);
                            string text = c == 0 ? (row == _snapshot.Cursor ? ">" : "") + row.ToString("D6") + (row == _snapshot.Frames ? "+" : loaded && !f.Verified ? "*" : "") :
                                !loaded ? "..." : c == 8 ? f.X.ToString("0.###", CultureInfo.InvariantCulture) : c == 9 ? f.Y.ToString("0.###", CultureInfo.InvariantCulture) : held ? Titles[c] : "";
                            N.DrawText(dc, text, -1, ref rect, 0x25);
                        }
                    }
                    // Present only the completed image; the target DC clips child controls and the update region.
                    N.BitBlt(target, 0, 0, background.Right, background.Bottom, dc, 0, 0, 0x00CC0020);
                    }
                finally { N.SelectObject(dc, old); }
            }
            finally { N.EndPaint(hwnd, ref paint); }
        }
        public void Dispose()
        {
            if (_handle != IntPtr.Zero) N.DestroyWindow(_handle);
            ReleaseBuffer();
            if (_className != null) { N.UnregisterClass(_className, N.GetModuleHandle(null)); _className = null; }
            _theme.Dispose(); _font = IntPtr.Zero;
            foreach (var brush in _brushes) N.DeleteObject(brush); _brushes.Clear();
        }
    }
}
