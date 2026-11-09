using System;

namespace Igtap.Tas.Core.Simulation
{
    // Unity 6 represents these clocks on an integer time grid. The movie's
    // ideal 50 Hz phase remains a separate value; ClockLease reanchors the next
    // float capture step to the actual native clock after every frame.
    internal struct NativeFrameClock
    {
        public const long Rate = 141120000;
        public const float RequestedFixedStep = .02f;
        public static readonly long FixedTicks = Quantize(RequestedFixedStep);
        public long TimeTicks, FixedTimeTicks;
        public long CaptureTicks;
        public double Time => TimeTicks * (1.0 / Rate);
        public double FixedTime => FixedTimeTicks * (1.0 / Rate);
        public double Phase => Time - FixedTime;
        // Native interpolation divides the difference of the public double
        // clocks by the public float timestep. Using the integer tick ratio
        // changes blends by an ULP even when both clocks themselves match.
        public float Interpolation => (float)(Phase / (float)(FixedTicks * (1.0 / Rate)));
        public NativeFrameClock(double time, double fixedTime, long captureTicks)
        { TimeTicks = (long)Math.Round(time * Rate); FixedTimeTicks = (long)Math.Round(fixedTime * Rate); CaptureTicks = captureTicks; }
        public float Advance(int pus, double idealNextPhase, float scale, int fps)
        {
            float capture = Prepare(pus, idealNextPhase, scale, fps);
            TimeTicks += Quantize(capture * scale);
            return (float)(Quantize(capture * scale) / (double)Rate);
        }
        public float Prepare(int pus, double idealNextPhase, float scale, int fps)
        {
            float capture = scale <= 0 ? 1f / fps : (float)((pus * (double)RequestedFixedStep +
                idealNextPhase * RequestedFixedStep / .02 - Phase) / scale);
            if (capture <= 0 || float.IsNaN(capture) || float.IsInfinity(capture)) throw new InvalidOperationException("Invalid native capture step.");
            CaptureTicks = Quantize(capture);
            return capture;
        }
        public static long Quantize(double seconds) => (long)(seconds * Rate);
    }
}
