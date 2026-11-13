using System;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Simulation;

internal static class SimulationTraceTests
{
    private sealed class Orb
    {
        public float cooldown = 2;
        public bool usable = false, refreshDash = true, refreshJump = true;
        public int ActivationSequence = 1;
    }
    private struct Point { public float x,y; public Point(float x,float y) {this.x=x;this.y=y;} }
    private sealed class Pose { public Point position {get;set;} public Point localPosition {get;set;} }
    private sealed class Body { public Point position {get;set;} public Point linearVelocity {get;set;} }
    private sealed class ScalarProbe {public float F;public double D;public bool B;public object Ignored;}
    private sealed class Mover
    {
        private readonly Body body;
        public bool reset=true;
        public Pose transform {get;} = new Pose {position=new Point(-0f,2),localPosition=new Point(3,4)};
        public Mover(Body value) {body=value;}
    }
    public static void Run()
    {
        int count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        SimulationTrace Trace()
        {
            var trace = new SimulationTrace { Fps = 280, GameHash = "game", WorldHash = "world", StartPhase = FrameTiming.InitialPhase(280) };
            trace.Inputs.Add(new FrameInput { X = 1, Held = Buttons.Jump, ExactEdges = true, Press = Buttons.Jump });
            trace.Samples.Add(new SimulationSample { Phase = "start", Fields = { ["body/x"] = SimulationTraceComparer.Float(0) } });
            trace.Samples.Add(new SimulationSample { Phase = "frame-end", Fields = { ["body/x"] = SimulationTraceComparer.Float(1) } });
            return trace;
        }
        var a = Trace(); var b = Trace();
        a.PreparedNextFrames.Add(true);a.Validate();
        a.PreparedNextFrames.Add(false);
        bool badPacing=false;try{a.Validate();}catch(System.IO.InvalidDataException){badPacing=true;}
        Check(badPacing,"Mixed step/run pacing metadata must account for every captured input frame.");a.PreparedNextFrames.Clear();
        Check(SimulationTraceComparer.Compare(a, b).Passed, "Equivalent native and offline traces match.");
        b.CaptureOrbs = true;
        Check(!SimulationTraceComparer.Compare(a, b).Passed, "A trace without orb witnesses cannot certify an orb capture.");
        b.CaptureOrbs = false;
        b.CaptureMovers = true;
        Check(!SimulationTraceComparer.Compare(a,b).Passed,"Mover witnesses are part of the trace identity."); b.CaptureMovers=false;
        b.CaptureSprings=true;
        Check(!SimulationTraceComparer.Compare(a,b).Passed,"Spring witnesses are part of the trace identity."); b.CaptureSprings=false;
        var body = new Body {position=new Point(1,5),linearVelocity=new Point(-7,8)};
        var moverFields = new System.Collections.Generic.SortedDictionary<string,string>();
        object capturedBody=SimulationFieldCapture.AddMover(moverFields,new Mover(body),42);
        Check(ReferenceEquals(capturedBody,body),"Mover observation returns its own body for host-specific sleep observation.");
        Check(moverFields["mover/42/position/x"]=="80000000" && moverFields["mover/42/body/x"]=="3f800000","Rendered and physical poses remain separate, including signed zero.");
        Check(moverFields["mover/42/local-position/y"]=="40800000" && moverFields["mover/42/velocity/x"]=="c0e00000" && moverFields["mover/42/reset"]=="True","Local pose, velocity and internal mover state share one witness.");
        var scalar=new ScalarProbe{F=BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)),D=double.Epsilon,B=true,Ignored=new object()};
        var scalarFields=new System.Collections.Generic.SortedDictionary<string,string>();SimulationFieldCapture.AddScript(scalarFields,scalar,"probe");
        Check(scalarFields.Count==3 && scalarFields["probe/F"]=="80000000" && scalarFields["probe/D"]==SimulationTraceComparer.Double(double.Epsilon),"Cached field schemas preserve exact scalar bits and omit unsupported references.");
        scalar.F=2;scalar.B=false;SimulationFieldCapture.AddScript(scalarFields,scalar,"probe");
        Check(scalarFields["probe/F"]=="40000000" && scalarFields["probe/B"]=="False","Cached metadata always reads current values rather than caching observations.");
        var orb = new Orb(); var orbFields = new System.Collections.Generic.SortedDictionary<string, string>();
        SimulationFieldCapture.AddOrb(orbFields, orb, 42);
        Check(orbFields["orb/42/cooldown"] == "40000000" && orbFields["orb/42/usable"] == "False", "Orb cooldown bits and usability are observed directly.");
        Check(orbFields["orb/42/refreshDash"] == "True" && orbFields["orb/42/refreshJump"] == "True" && orbFields["orb/42/ActivationSequence"] == "1", "Witnesses distinguish dash-only and combined refill mechanics.");
        orb.cooldown = 0; orb.usable = true;
        SimulationFieldCapture.AddOrb(orbFields, orb, 42);
        Check(orbFields["orb/42/cooldown"] == "00000000" && orbFields["orb/42/usable"] == "True", "Reusing a witness map captures the restored orb state.");
        b.Samples[1].Fields["body/x"] = SimulationTraceComparer.Float(1.0000001f);
        var different = SimulationTraceComparer.Compare(a, b);
        Check(!different.Passed && different.Frame == 0 && different.Field == "body/x" && different.Compared == 1, "First differing float bit is reported at its exact sample.");
        b = Trace(); b.Samples[1].Fields["missing-native-field"] = "1";
        different = SimulationTraceComparer.Compare(a, b);
        Check(!different.Passed && different.Expected == "<missing>", "Extra offline state fields are not silently ignored.");
        b = Trace(); b.Samples.RemoveAt(1);
        Check(!SimulationTraceComparer.Compare(a, b).Passed, "Missing sample cannot establish parity.");
        b = Trace(); b.WorldHash = "another world";
        Check(!SimulationTraceComparer.Compare(a, b).Passed, "Trace identity binds the exact exported world.");
        b = Trace(); b.AdvanceMode = "continuous";
        Check(!SimulationTraceComparer.Compare(a, b).Passed, "Stepped and continuous captures identify different end-of-frame clock observations.");
        b = Trace(); var input = b.Inputs[0]; input.Press = Buttons.None; b.Inputs[0] = input;
        Check(!SimulationTraceComparer.Compare(a, b).Passed, "Same held input with different edges is a different experiment.");
        Check(SimulationTraceComparer.Float(0f) != SimulationTraceComparer.Float(BitConverter.ToSingle(BitConverter.GetBytes(unchecked((int)0x80000000)), 0)), "Signed zeros stay distinct.");
        foreach(int bits in new[]{0,int.MinValue,1,-1,0x7f800000,unchecked((int)0xff800000),0x7fc01234,unchecked((int)0xffc04321)})
        {
            float value=BitConverter.Int32BitsToSingle(bits);
            Check(SimulationTraceComparer.Float(value)==BitConverter.ToInt32(BitConverter.GetBytes(value),0).ToString("x8",System.Globalization.CultureInfo.InvariantCulture),"Allocation-free float witnesses retain legacy bits, including infinities and NaN payloads.");
        }
        a = Trace(); b = Trace(); a.Samples.Clear(); b.Samples.Clear();
        Check(!SimulationTraceComparer.Compare(a, b).Passed, "Empty traces do not pass.");
        a = Trace(); a.StartPhase = double.PositiveInfinity;
        try { a.Validate(); throw new Exception("Infinite phase accepted."); } catch (System.IO.InvalidDataException) { count++; }
        a = Trace(); a.Samples[1].Pu = -1;
        try { a.Validate(); throw new Exception("Negative PU index accepted."); } catch (System.IO.InvalidDataException) { count++; }
        Check(NativeFrameClock.FixedTicks == 2822399 && NativeFrameClock.Rate == 141120000, "Native fixed-step float converts onto the integer clock grid.");
        var prepared = new NativeFrameClock(.01607142857142857, 0, 0);
        var originalClock = prepared; prepared.Prepare(0, .01964285714285714, 1, 280);
        Check(prepared.TimeTicks == originalClock.TimeTicks && prepared.FixedTimeTicks == originalClock.FixedTimeTicks && prepared.CaptureTicks > 0,
            "Preparing a following frame records its interval without advancing simulated time or physics.");
        // Native 280 FPS capture: the public clock/timestep expression matters
        // even while the body endpoints and integer clock counters agree.
        var interpolation = new NativeFrameClock(BitConverter.Int64BitsToDouble(0x3ffb7ae0a417b6cd), BitConverter.Int64BitsToDouble(0x3ffb33329183cab4), 0);
        float fromY = BitConverter.Int32BitsToSingle(unchecked((int)0xc396c290));
        float toY = BitConverter.Int32BitsToSingle(unchecked((int)0xc3972e15));
        float left = (1f - interpolation.Interpolation) * fromY, right = interpolation.Interpolation * toY;
        Check(SimulationTraceComparer.Float(left + right) == "c39720a4", "Native interpolation rounding reproduces the observed 280 FPS render pose.");
        foreach (int fps in new[] { 30, 50, 60, 100, 120, 144, 165, 244, 280 })
        {
            var schedule = new FrameTiming(fps);
            var native = new NativeFrameClock(schedule.Phase, 0, 0);
            int multiple = 0;
            for (int i = 0; i < fps * 10; i++)
            {
                int pus = schedule.Advance(1); if (pus > 1) multiple++;
                native.Advance(pus, schedule.Phase, 1, fps); native.FixedTimeTicks += pus * NativeFrameClock.FixedTicks;
            }
            Check(Math.Abs(native.Phase - schedule.Phase) < .000002 && schedule.Pus == 500, "Native reanchoring preserves the ideal schedule at " + fps + " FPS.");
            Check(fps != 30 || multiple > 0, "30 FPS executes multiple PUs in one input frame.");
            var before = native; native.Advance(0, schedule.Phase, 0, fps);
            Check(native.TimeTicks == before.TimeTicks && native.FixedTimeTicks == before.FixedTimeTicks, "Hitstop does not advance scaled native time at " + fps + " FPS.");
        }
        Console.WriteLine("Simulator trace checks passed: " + count);
    }
}
