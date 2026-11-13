using System;

namespace Igtap.Tas.Core
{
    // This is part of the movie, not an editor preference: replay must use it too.
    public sealed class TasStartSetup
    {
        public int Difficulty; // Release game: normal only.
        public int FrameRate = 50;
        public int Course; // UI level: 0 new game, 1-10 original, 11-15 overgrown 1-5.
        public string CustomMapId="",CustomMapName="",CustomMapHash="";
        public int VmanMode; // 0 normal, 1 timed VMAN, 2 VMAN practice.
        public int VmanSeconds=60;
        [Newtonsoft.Json.JsonIgnore] public bool IsCustomMap=>!string.IsNullOrEmpty(CustomMapId);
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
        public void RequireLoadedMap(string loadedId,string loadedHash)
        {
            loadedId=loadedId??"";
            if((CustomMapId??"")!=loadedId)throw new InvalidOperationException("The loaded map does not match this TAS. Reload its configured start before capturing or searching.");
            if(IsCustomMap&&(string.IsNullOrEmpty(CustomMapHash)||CustomMapHash!=loadedHash))throw new InvalidOperationException("The loaded custom-map revision does not match this TAS. Reload the same map version.");
        }
        public void Validate()
        {
            FrameTiming.ValidateFps(FrameRate);
            if (Difficulty != 0 || Course < 0 || Course > CourseCount)
                throw new InvalidOperationException("Choose New game or a release-game level from 1 to 15.");
            if(IsCustomMap){CustomMapCatalog.ValidateId(CustomMapId);if(Course!=0)throw new InvalidOperationException("Choose a built-in course or a custom map, not both.");}
            if(VmanMode<0||VmanMode>2||VmanSeconds<1||VmanSeconds>86400)throw new InvalidOperationException("Choose Normal, timed VMAN or VMAN practice; the VMAN duration must be 1-86400 seconds.");
            if (AirJumps != 1 || AirDashes < 1 || AirDashes > 2)
                throw new InvalidOperationException("The maximum is one extra air jump and two dashes.");
            if (OmniDash && !Dash) throw new InvalidOperationException("Omnidirectional dash requires Dash.");
        }
    }
}
