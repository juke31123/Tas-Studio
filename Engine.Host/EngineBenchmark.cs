using System;
using System.Diagnostics;
using System.Linq;
using IGTAP.EngineSim;
using Igtap.Tas.Core.Simulation;

internal static class EngineBenchmark
{
    internal sealed class Report
    {
        public bool Passed;
        public int Fps, Runs, Frames, Pus, SnapshotPairs;
        public long AllocatedBytes;
        public double Seconds, FramesPerSecond, PusPerSecond, BytesPerFrame, SnapshotMillisecondsPerPair, SnapshotBytesPerPair;
        public string Scope="Sequential offline replay without trace observers; excludes loading, parallel search and per-candidate snapshot costs.";
    }
    public static Report Run(string directory, SimulationTrace native)
    {
        var simulation=EngineFixture.Load(directory,native); var root=simulation.CaptureStudioState();
        int pus=0;
        void Replay()
        {
            simulation.RestoreStudioState(root);
            for (int frame=0; frame<native.Inputs.Count; frame++)
            {
                if(native.PreparedNextFrames.Count!=0)simulation.ConfigureStudioEndPacing(native.PreparedNextFrames[frame]);
                pus+=simulation.StepStudioFrame(EngineFixture.Input(native.Inputs[frame],frame==0 ? native.Previous : native.Inputs[frame-1]));
            }
        }
        Replay(); pus=0; // Warm runtime/JIT and physics caches before measuring.
        long allocated=GC.GetAllocatedBytesForCurrentThread(); var watch=Stopwatch.StartNew();
        const int runs=3;
        for (int run=0; run<runs; run++) Replay();
        watch.Stop(); allocated=GC.GetAllocatedBytesForCurrentThread()-allocated;
        double seconds=watch.Elapsed.TotalSeconds; int frames=runs*native.Inputs.Count;
        var expected=native.Samples.Last();
        var actual=EngineFixture.Sample(simulation,expected.Frame,expected.Pu,expected.Phase,native.CaptureOrbs,native.CaptureMovers,native.CaptureSprings,native.CaptureUpgrades,native.CaptureAllUpgradeBoxes);
        bool passed=expected.Fields.Count==actual.Fields.Count && expected.Fields.All(p => actual.Fields.TryGetValue(p.Key,out string value) && p.Value==value);
        long snapshotBytes=GC.GetAllocatedBytesForCurrentThread(); var snapshots=Stopwatch.StartNew();
        const int snapshotsCount=32;
        for (int i=0; i<snapshotsCount; i++) { var copy=simulation.CaptureStudioState(); simulation.RestoreStudioState(copy); }
        snapshots.Stop(); snapshotBytes=GC.GetAllocatedBytesForCurrentThread()-snapshotBytes;
        return new Report { Passed=passed, Fps=native.Fps, Runs=runs, Frames=frames, Pus=pus, Seconds=seconds,
            FramesPerSecond=frames/seconds, PusPerSecond=pus/seconds, AllocatedBytes=allocated, BytesPerFrame=allocated/(double)frames,
            SnapshotPairs=snapshotsCount, SnapshotMillisecondsPerPair=snapshots.Elapsed.TotalMilliseconds/snapshotsCount,
            SnapshotBytesPerPair=snapshotBytes/(double)snapshotsCount };
    }
}
