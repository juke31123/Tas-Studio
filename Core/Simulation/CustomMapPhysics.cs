using System;
using System.Globalization;

namespace Igtap.Tas.Core.Simulation
{
    public static class CustomMapPhysics
    {
        // Navigator's rebuilt tile contacts can leave tiny native Box2D float
        // residuals. This allowance is only for reconstructing a custom-map
        // search prefix; movie hashes and in-game replay checks stay unchanged.
        public const double PrefixTolerance=.0001;
        public static bool Matches(string field,string expected,string actual,bool customMap)
        {
            if(expected==actual)return true;
            if(!customMap||(field!="body/position/x"&&field!="body/position/y"&&field!="body/velocity/x"&&field!="body/velocity/y"))return false;
            if(expected?.Length!=8||actual?.Length!=8||!uint.TryParse(expected,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out var a)||!uint.TryParse(actual,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out var b))return false;
            float first=BitConverter.Int32BitsToSingle(unchecked((int)a)),second=BitConverter.Int32BitsToSingle(unchecked((int)b));
            return Movie.Finite(first)&&Movie.Finite(second)&&Math.Abs((double)first-second)<=PrefixTolerance;
        }
    }
}
