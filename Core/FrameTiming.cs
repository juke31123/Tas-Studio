using System;

namespace Igtap.Tas.Core
{
    // A movie frame and a physics update are independent. This clock describes
    // their schedule; Unity still executes all FixedUpdates and physics itself.
    public sealed class FrameTiming
    {
        public const double PuSeconds = 1.0 / 50;
        public const float NativePuSeconds = .02f;
        public const int Protocol = 1;
        public readonly int Fps;
        public double FrameSeconds => 1.0 / Fps;
        public double Phase { get; private set; }
        public long Pus { get; private set; }
        public FrameTiming(int fps)
        {
            ValidateFps(fps); Fps = fps;
            // Half the smallest interval shared by the two clocks keeps normal
            // frames away from an ambiguous floating-point PU boundary.
            Phase = InitialPhase(fps);
        }
        public static void ValidateFps(int fps)
        { if (fps < 1 || fps > 10000) throw new ArgumentOutOfRangeException(nameof(fps), "Invalid recording FPS."); }
        public static double InitialPhase(int fps)
        {
            ValidateFps(fps);
            int a = fps, b = 50;
            while (b != 0) { int r = a % b; a = b; b = r; }
            return a / (100.0 * fps);
        }
        public int Advance(double scale)
        {
            if (double.IsNaN(scale) || double.IsInfinity(scale) || scale < 0 || scale > 100)
                throw new ArgumentOutOfRangeException(nameof(scale));
            double total = Phase + FrameSeconds * scale;
            int ticks = (int)Math.Floor(total / PuSeconds + 1e-10);
            Phase = Math.Max(0, total - ticks * PuSeconds); Pus += ticks;
            return ticks;
        }
        // Inclusive: 1 means the very next frame contains a PU. -1 means the
        // game clock is frozen; its unscaled coroutine may resume it later.
        public static int FramesUntilPu(double phase, int fps, double scale)
        {
            ValidateFps(fps);
            if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale)) return -1;
            return Math.Max(1, (int)Math.Ceiling((PuSeconds - phase) * fps / scale - 1e-9));
        }
    }
}
