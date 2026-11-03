using System;
using System.Globalization;

namespace Igtap.Tas.Core
{
    public static class PlaybackSpeed
    {
        public const float Minimum = 0.1f;
        public const float Maximum = 1f;
        public const string Error = "Enter a speed from 0.1 to 1.0 (for example 0.5 or 0,5).";

        public static bool TryParse(string text, out float speed)
        {
            // A comma is a decimal separator here, never a thousands separator.
            return float.TryParse(text?.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out speed) && IsValid(speed);
        }
        public static bool IsValid(float speed) => Movie.Finite(speed) && speed >= Minimum && speed <= Maximum;
        public static void Validate(float speed)
        {
            if (!IsValid(speed)) throw new ArgumentOutOfRangeException(nameof(speed), Error);
        }
        public static int TargetFrameRate(int movieFrameRate, float speed)
        {
            Validate(speed);
            // Round down: integer FPS pacing must never exceed the selected rate.
            return Math.Max(1, (int)Math.Floor(movieFrameRate * (decimal)speed));
        }
    }
}
