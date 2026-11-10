using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Simulation;

internal static class WorkerChecks
{
    private sealed class Digest : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public int Samples;
        public void Add(SimulationSample sample)
        {
            var row = new StringBuilder().Append(sample.Frame).Append('|').Append(sample.Pu).Append('|').Append(sample.Phase).Append('\n');
            foreach (var field in sample.Fields) row.Append(field.Key).Append('=').Append(field.Value).Append('\n');
            _hash.AppendData(Encoding.UTF8.GetBytes(row.ToString())); Samples++;
        }
        public string Finish() => Convert.ToBase64String(_hash.GetHashAndReset());
        public void Dispose() => _hash.Dispose();
    }
    internal sealed class Trial
    {
        public int Worker, Order, Samples;
        public string Branch, Hash, Error;
    }
    internal sealed class Report
    {
        public bool Passed;
        public int Fps;
        public string NativeHash;
        public Trial[] Trials;
    }
    public static Report Run(string directory, SimulationTrace native)
    {
        using var expected = new Digest();
        foreach (var sample in native.Samples) expected.Add(sample);
        string nativeHash = expected.Finish();
        var results = new Trial[6];
        var threads = Enumerable.Range(0, 2).Select(worker => new Thread(() => {
            // Reuse each OS thread with shuffled branches to expose stale game
            // statics as well as races between the two simultaneous worlds.
            for (int order = 0; order < 3; order++)
            {
                bool branch = (worker + order) % 2 != 0;
                var trial = results[worker * 3 + order] = new Trial {Worker=worker, Order=order, Branch=branch ? "B" : "A"};
                try
                {
                    var simulation = EngineFixture.Load(directory, native);
                    using var digest = new Digest(); int frame = 0;
                    digest.Add(EngineFixture.Sample(simulation, frame, 0, "start", native.CaptureOrbs, native.CaptureMovers, native.CaptureSprings,native.CaptureUpgrades));
                    simulation.StudioSample = (pu, phase) => digest.Add(EngineFixture.Sample(simulation, frame, pu, phase, native.CaptureOrbs, native.CaptureMovers, native.CaptureSprings,native.CaptureUpgrades));
                    FrameInput previous = native.Previous;
                    int end = branch ? Math.Min(native.Inputs.Count, 1200) : native.Inputs.Count;
                    for (; frame < end; frame++)
                    {
                        var input = native.Inputs[frame];
                        if (branch && frame >= 20 && frame < 40) input.X = -1;
                        simulation.StepStudioFrame(EngineFixture.Input(input, previous)); previous = input;
                    }
                    trial.Hash = digest.Finish(); trial.Samples = digest.Samples;
                }
                catch (Exception error) { trial.Error = error.ToString(); }
            }
        }) { IsBackground = true }).ToArray();
        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();
        bool passed = results.All(r => r.Error == null) && results.Where(r => r.Branch == "A").All(r => r.Hash == nativeHash);
        passed &= results.Where(r => r.Branch == "B").Select(r => r.Hash).Distinct().Count() == 1;
        return new Report { Passed=passed, Fps=native.Fps, NativeHash=nativeHash, Trials=results };
    }
}
