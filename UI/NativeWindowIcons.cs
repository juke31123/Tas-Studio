using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using N = Igtap.Tas.UI.NativeMethods;

namespace Igtap.Tas.UI
{
    // Each window owns its small/large HICON pair. The source image is embedded
    // once; no temporary files or shared native handles are needed.
    internal sealed class NativeWindowIcons : IDisposable
    {
        private static readonly Lazy<byte[]> Image = new Lazy<byte[]>(ReadImage);
        private IntPtr _window, _large, _small;
        private uint _dpi;

        public void Attach(IntPtr window)
        {
            _window = window; _dpi = 0;
            Refresh();
        }
        public void Refresh()
        {
            if (_window == IntPtr.Zero) return;
            uint dpi = N.GetDpiForWindow(_window);
            if (dpi == 0) dpi = 96;
            if (_dpi == dpi) return;
            IntPtr large = Create(11, 12, dpi), small;
            try { small = Create(49, 50, dpi); }
            catch { N.DestroyIcon(large); throw; }
            // Install both replacements before releasing the old pair. Windows
            // uses ICON_SMALL for the caption and ICON_BIG for Alt+Tab/taskbar.
            N.SendMessage(_window, 0x80, new IntPtr(1), large);
            N.SendMessage(_window, 0x80, IntPtr.Zero, small);
            Release(); _large = large; _small = small; _dpi = dpi;
        }
        private static IntPtr Create(int widthMetric, int heightMetric, uint dpi)
        {
            var bytes = Image.Value;
            var icon = N.CreateIconFromResourceEx(bytes, (uint)bytes.Length, true, 0x00030000,
                N.GetSystemMetricsForDpi(widthMetric, dpi), N.GetSystemMetricsForDpi(heightMetric, dpi), 0);
            if (icon == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot load the Tas Studio window icon.");
            return icon;
        }
        private static byte[] ReadImage()
        {
            using var stream = typeof(NativeWindowIcons).Assembly.GetManifestResourceStream("TasStudio.Assets.Tas.ico")
                ?? throw new InvalidDataException("The Tas Studio icon is missing from the mod.");
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt16() != 0 || reader.ReadUInt16() != 1) throw new InvalidDataException("Invalid ICO header.");
            int count = reader.ReadUInt16(), quality = -1;
            if (count == 0 || count > (stream.Length - 6) / 16) throw new InvalidDataException("Invalid ICO directory.");
            uint offset = 0, length = 0;
            for (int i = 0; i < count; i++)
            {
                int width = reader.ReadByte(), height = reader.ReadByte();
                if (width == 0) width = 256;
                if (height == 0) height = 256;
                reader.ReadUInt16(); reader.ReadUInt16(); // palette/reserved, planes
                int bits = reader.ReadUInt16();
                uint bytes = reader.ReadUInt32(), start = reader.ReadUInt32();
                if (bytes == 0 || start < 6 + 16 * count || start > stream.Length || bytes > stream.Length - start)
                    throw new InvalidDataException("Invalid ICO image bounds.");
                int score = width * height * 64 + Math.Min(63, bits);
                if (score > quality) { quality = score; offset = start; length = bytes; }
            }
            stream.Position = offset;
            return reader.ReadBytes(checked((int)length));
        }
        private void Release()
        {
            if (_large != IntPtr.Zero) N.DestroyIcon(_large);
            if (_small != IntPtr.Zero) N.DestroyIcon(_small);
            _large = _small = IntPtr.Zero;
        }
        public void Dispose() { Release(); _window = IntPtr.Zero; _dpi = 0; }
    }
}
