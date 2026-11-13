using System;
using System.Collections.Generic;
using System.IO;

namespace Igtap.Tas.Core.Simulation
{
    // A reconstructed prefix supplies a search anchor, not a replacement movie.
    // Custom composites can reorder a diagonal and an axial contact for one PU.
    // Allow only that recognizable transient; never copy witness values into the
    // simulation, relax positions, or accept a different state at the anchor.
    public sealed class NativePrefixValidator
    {
        private readonly IReadOnlyList<SimulationSample> _expected;
        private readonly bool _customMap;
        private int _observed, _contactPus;
        public int PhysicsResiduals { get; private set; }
        public int RecoveredContactSamples { get; private set; }
        public int AdditionalVelocityResiduals { get; private set; }
        public const double ContactVelocityResidual = .001;

        public NativePrefixValidator(IReadOnlyList<SimulationSample> expected, bool customMap)
        { _expected=expected ?? throw new ArgumentNullException(nameof(expected)); _customMap=customMap; }

        public void Match(SimulationSample actual, float gravityStep, bool solidContact)
        {
            if(_observed>=_expected.Count) throw new InvalidDataException("Native prefix witness is incomplete.");
            var expected=_expected[_observed];
            if(actual.Frame!=expected.Frame || actual.Pu!=expected.Pu || actual.Phase!=expected.Phase || actual.Fields.Count!=expected.Fields.Count)
                Fail(actual,"sample schedule");
            bool intermediate=_customMap && _observed>0 && _observed<_expected.Count-1;
            bool contactMismatch=false;
            int residuals=0, extraVelocity=0;
            foreach(var pair in expected.Fields)
            {
                if(!actual.Fields.TryGetValue(pair.Key,out string value)) Fail(actual,pair.Key);
                if(pair.Value==value) continue;
                if(CustomMapPhysics.Matches(pair.Key,pair.Value,value,_customMap)) { residuals++; continue; }
                if(intermediate && solidContact && pair.Key=="movement/Velocity/y" &&
                    expected.Fields.TryGetValue("movement/recentlyJumped",out var recent) && recent=="False" &&
                    expected.Fields.TryGetValue("movement/momentum/y",out var momentum) && Read(momentum,out var m) && m==0 &&
                    ContactClear(pair.Value,value,gravityStep))
                { contactMismatch=true; continue; }
                // Keep the established position bound. Only the almost-zero
                // contact-solver remainder in vertical body velocity gets its
                // own bound, and the final sample uses the usual strict policy.
                if(intermediate && solidContact && pair.Key=="body/velocity/y" &&
                    ContactRemainder(pair.Value,value,gravityStep))
                { residuals++; extraVelocity++; continue; }
                Fail(actual,pair.Key);
            }
            if(contactMismatch)
            {
                _contactPus+=actual.Pu;
                if(_contactPus>1 || _contactPus==0) Fail(actual,"movement/Velocity/y; contact difference did not clear by the next PU");
                RecoveredContactSamples++;
            }
            else _contactPus=0;
            PhysicsResiduals+=residuals;
            AdditionalVelocityResiduals+=extraVelocity;
            _observed++;
        }

        public void Complete()
        { if(_observed!=_expected.Count || _observed==0) throw new InvalidDataException("Unconsumed native prefix samples."); }

        private static bool ContactClear(string expected,string actual,float step)
            => Movie.Finite(step) && step<0 && Read(expected,out var a) && Read(actual,out var b) &&
                (a==0 && b==step || b==0 && a==step);
        private static bool ContactRemainder(string expected,string actual,float step)
            => Movie.Finite(step) && Read(expected,out var a) && Read(actual,out var b) &&
                Math.Abs((double)a-b)<=ContactVelocityResidual &&
                (Math.Abs(a)<=ContactVelocityResidual && Math.Abs(b)<=ContactVelocityResidual ||
                 Math.Abs((double)a-step)<=ContactVelocityResidual && Math.Abs((double)b-step)<=ContactVelocityResidual);
        private static bool Read(string value,out float number)
        {
            number=0;
            if(value?.Length!=8 || !uint.TryParse(value,System.Globalization.NumberStyles.HexNumber,System.Globalization.CultureInfo.InvariantCulture,out var bits)) return false;
            number=BitConverter.Int32BitsToSingle(unchecked((int)bits)); return Movie.Finite(number);
        }
        private static void Fail(SimulationSample sample,string field)
            => throw new InvalidDataException("Native search prefix differs at frame " + sample.Frame + " (" + field + ").");
    }
}
