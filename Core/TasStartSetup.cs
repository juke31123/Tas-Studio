using System;

namespace Igtap.Tas.Core
{
    // This is part of the movie, not an editor preference: replay must use it too.
    public sealed class TasStartSetup
    {
        public int Difficulty; // Release game: normal only.
        public int FrameRate = 50;
        public int Course; // UI level: 0 new game, 1-10 original, 11-15 overgrown 1-5.
        public bool WallJump, DoubleJump, Dash, OmniDash, BlockSwap, Light;
        public bool RefillOrbs, Zippers;
        public int AirJumps = 1, AirDashes = 1;
        [Newtonsoft.Json.JsonIgnore]
        public int CourseCount => 15;
        [Newtonsoft.Json.JsonIgnore]
        public int Area => Course <= 5 ? 1 : 2;
        [Newtonsoft.Json.JsonIgnore]
        public bool Overgrown => Course >= 11;
        [Newtonsoft.Json.JsonIgnore]
        public int SourceCourse => Overgrown ? Course - 10 : Course;
        // The second visit to courses 1-5 uses the original zone's geometry.
        // Native zone 2 explicitly disables their course objects.
        [Newtonsoft.Json.JsonIgnore]
        public int ZoneToLoad => Overgrown ? 1 : Area;
        public TasStartSetup Clone() => (TasStartSetup)MemberwiseClone();
        public void Validate()
        {
            FrameTiming.ValidateFps(FrameRate);
            if (Difficulty != 0 || Course < 0 || Course > CourseCount)
                throw new InvalidOperationException("Choose New game or a release-game level from 1 to 15.");
            if (AirJumps != 1 || AirDashes < 1 || AirDashes > 2)
                throw new InvalidOperationException("The maximum is one extra air jump and two dashes.");
            if (OmniDash && !Dash) throw new InvalidOperationException("Omnidirectional dash requires Dash.");
        }
    }
}
