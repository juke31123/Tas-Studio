using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Igtap.Tas.UI
{
    // Unicode Win32 ABI; pointer-sized fields are essential in Unity's x64 process.
    internal static class NativeMethods
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WindowClass
        {
            public uint Size, Style; public IntPtr Procedure; public int ClassExtra, WindowExtra;
            public IntPtr Instance, Icon, Cursor, Background;
            public string Menu, Name; public IntPtr SmallIcon;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct Message { public IntPtr Hwnd; public uint Id; public UIntPtr WParam; public IntPtr LParam; public uint Time; public Point Point; public uint Private; }
        [StructLayout(LayoutKind.Sequential)] internal struct MinMax { public Point Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack; }
        [StructLayout(LayoutKind.Sequential)] internal struct Paint { public IntPtr Dc; public int Erase; public Rect Rect; public int Restore, Update; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved; }
        [StructLayout(LayoutKind.Sequential)] internal struct Scroll { public uint Size, Mask; public int Min, Max; public uint Page; public int Position, Track; }
        [StructLayout(LayoutKind.Sequential)] internal struct DrawItem { public uint Type, Id, Item, Action, State; public IntPtr Window, Dc; public Rect Rect; public UIntPtr Data; }
        [DllImport("gdi32.dll")] internal static extern bool RoundRect(IntPtr dc, int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreatePen(int style, int width, uint color);
        [DllImport("gdi32.dll")] internal static extern bool MoveToEx(IntPtr dc, int x, int y, IntPtr previous);
        [DllImport("gdi32.dll")] internal static extern bool LineTo(IntPtr dc, int x, int y);
        [DllImport("gdi32.dll")] internal static extern bool Ellipse(IntPtr dc, int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")] internal static extern int SaveDC(IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern bool RestoreDC(IntPtr dc, int saved);
        [DllImport("gdi32.dll")] internal static extern int IntersectClipRect(IntPtr dc, int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")] internal static extern IntPtr GetStockObject(int index);
        [DllImport("user32.dll")] internal static extern bool DrawFocusRect(IntPtr dc, ref Rect rect);
        [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] internal static extern int SetWindowTheme(IntPtr hwnd, string app, string classes);
        [DllImport("user32.dll")] internal static extern IntPtr BeginPaint(IntPtr hwnd, out Paint paint);
        [DllImport("user32.dll")] internal static extern bool EndPaint(IntPtr hwnd, ref Paint paint);
        [DllImport("user32.dll")] internal static extern int FillRect(IntPtr dc, ref Rect rect, IntPtr brush);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int DrawText(IntPtr dc, string text, int count, ref Rect rect, uint format);
        [DllImport("user32.dll")] internal static extern IntPtr SetCapture(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern IntPtr GetCapture();
        [DllImport("user32.dll")] internal static extern bool ReleaseCapture();
        [DllImport("user32.dll")] internal static extern IntPtr SetFocus(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern short GetKeyState(int key);
        [DllImport("user32.dll")] internal static extern int SetScrollInfo(IntPtr hwnd, int bar, ref Scroll scroll, bool redraw);
        [DllImport("user32.dll")] internal static extern bool GetScrollInfo(IntPtr hwnd, int bar, ref Scroll scroll);
        [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll", SetLastError = true)] internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll", SetLastError = true)] internal static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
        [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
        [DllImport("gdi32.dll")] internal static extern int SetBkMode(IntPtr dc, int mode);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ushort RegisterClassEx(ref WindowClass value);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool UnregisterClass(string name, IntPtr instance);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetMessage(out Message message, IntPtr hwnd, uint first, uint last);
        [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref Message message);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr DispatchMessage(ref Message message);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool IsDialogMessage(IntPtr hwnd, ref Message message);
        [DllImport("user32.dll")] internal static extern void PostQuitMessage(int code);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] internal static extern IntPtr SendText(IntPtr hwnd, uint message, IntPtr wparam, string text);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool SetWindowText(IntPtr hwnd, string text);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr hwnd, StringBuilder buffer, int size);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hwnd, int show);
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool EnableWindow(IntPtr hwnd, bool enabled);
        [DllImport("user32.dll")] internal static extern bool MoveWindow(IntPtr hwnd, int x, int y, int width, int height, bool repaint);
        [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
        [DllImport("user32.dll")] internal static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
        [DllImport("user32.dll")] internal static extern bool InvalidateRect(IntPtr hwnd, ref Rect rect, bool erase);
        [DllImport("user32.dll")] internal static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint milliseconds, IntPtr callback);
        [DllImport("user32.dll")] internal static extern bool KillTimer(IntPtr hwnd, UIntPtr id);
        [DllImport("user32.dll")] internal static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
        [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] internal static extern IntPtr GetFocus();
        [DllImport("user32.dll")] internal static extern bool IsChild(IntPtr parent, IntPtr child);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int maxCount);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateSolidBrush(uint color);
        [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll")] internal static extern uint SetTextColor(IntPtr dc, uint color);
        [DllImport("gdi32.dll")] internal static extern uint SetBkColor(IntPtr dc, uint color);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr CreateFont(int height, int width, int angle, int orientation, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrecision, uint clipPrecision, uint quality, uint pitch, string face);
    }
}
