using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Igtap.Tas.Core.Simulation
{
    // Wire data only. Neither process may put engine objects or type names to
    // instantiate into this contract.
    public sealed class SimulationSample
    {
        public int Frame, Pu;
        public string Phase = "frame-end";
        public SortedDictionary<string, string> Fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
    }

    public sealed class SimulationTrace
    {
        public const int Schema = 1;
        // Full before/after-PU diagnostics include every platform/orb field and
        // can exceed the smaller job/result import bound. Keep that bound intact.
        public const long MaxFileBytes = 1024L * 1024 * 1024;
        public static SimulationTrace Read(string path) => Search.SearchFiles.Read<SimulationTrace>(path, MaxFileBytes);
        public int Version = Schema, Fps, TimingProtocol = FrameTiming.Protocol, StartFrame;
        public string GameHash = "", WorldHash = "", Backend = "native";
        public string AdvanceMode = "step";
        public string AnimationHash = "";
        public bool CaptureOrbs;
        public bool CaptureMovers;
        public bool CaptureSprings;
        public bool CaptureUpgrades;
        public double StartPhase;
        public FrameInput Previous;
        public List<FrameInput> Inputs = new List<FrameInput>();
        // Diagnostic captures can mix running frames and the final paused
        // frame of a frame/PU step. Their End clocks have different pacing.
        public List<bool> PreparedNextFrames = new List<bool>();
        public List<SimulationSample> Samples = new List<SimulationSample>();
        public string[] Coverage = Array.Empty<string>();

        public void Validate()
        {
            if (Version != Schema || TimingProtocol != FrameTiming.Protocol || StartFrame < 0 || (AdvanceMode != "step" && AdvanceMode != "continuous" && AdvanceMode != "before-pacing") ||
                Array.IndexOf(new[] { 30, 50, 60, 100, 120, 144, 165, 244, 280 }, Fps) < 0 ||
                double.IsNaN(StartPhase) || double.IsInfinity(StartPhase) || StartPhase < 0 || StartPhase >= FrameTiming.PuSeconds ||
                Inputs == null || Inputs.Count > Movie.MaxFrames || Samples == null || Samples.Count > 4 * Movie.MaxFrames ||
                PreparedNextFrames == null || PreparedNextFrames.Count != 0 && PreparedNextFrames.Count != Inputs.Count ||
                string.IsNullOrEmpty(GameHash) || string.IsNullOrEmpty(WorldHash))
                throw new InvalidDataException("Incomplete simulator trace or unsupported timing contract.");
            foreach (var input in Inputs)
                if (!Movie.Finite(input.X) || !Movie.Finite(input.Y) || Math.Abs(input.X) > 1 || Math.Abs(input.Y) > 1 ||
                    (((int)input.Held | (int)input.Press | (int)input.Release) & ~63) != 0)
                    throw new InvalidDataException("Invalid simulator trace input.");
            int frame = -1, pu = -1;
            foreach (var sample in Samples)
            {
                if (sample == null || sample.Frame < 0 || sample.Frame > Inputs.Count || sample.Frame < frame || sample.Pu < 0 ||
                    sample.Frame == frame && sample.Pu < pu || sample.Fields == null || sample.Fields.Count > 20000 ||
                    (sample.Phase != "start" && sample.Phase != "before-pu" && sample.Phase != "after-pu" && sample.Phase != "frame-end"))
                    throw new InvalidDataException("Invalid simulator trace sample ordering.");
                frame = sample.Frame; pu = sample.Pu;
                foreach (var field in sample.Fields)
                    if (string.IsNullOrEmpty(field.Key) || field.Value == null) throw new InvalidDataException("Invalid simulator trace field.");
            }
        }
    }

    public sealed class SimulationComparison
    {
        public bool Passed;
        public int Compared, Frame = -1, Pu = -1;
        public string Phase = "", Field = "", Expected = "", Actual = "", Message = "";
    }

    public static class SimulationTraceComparer
    {
        public static string FileHash(string path)
        { using var hash = SHA256.Create(); using var file = File.OpenRead(path); return Convert.ToBase64String(hash.ComputeHash(file)); }
        // Float bits deliberately have no epsilon. Contact/branch thresholds and
        // signed zero must not disappear inside a position tolerance.
        public static string Float(float value) => BitConverter.SingleToInt32Bits(value).ToString("x8", CultureInfo.InvariantCulture);
        public static string Double(double value) => BitConverter.DoubleToInt64Bits(value).ToString("x16", CultureInfo.InvariantCulture);
        public static SimulationComparison Compare(SimulationTrace native, SimulationTrace offline)
        {
            native.Validate(); offline.Validate();
            var result = new SimulationComparison();
            if (native.GameHash != offline.GameHash || native.WorldHash != offline.WorldHash || native.AnimationHash != offline.AnimationHash || native.CaptureOrbs != offline.CaptureOrbs || native.CaptureMovers != offline.CaptureMovers || native.CaptureSprings != offline.CaptureSprings || native.CaptureUpgrades!=offline.CaptureUpgrades || native.Fps != offline.Fps || native.AdvanceMode != offline.AdvanceMode ||
                native.StartFrame != offline.StartFrame || Double(native.StartPhase) != Double(offline.StartPhase) ||
                !SameInput(native.Previous, offline.Previous) || native.Inputs.Count != offline.Inputs.Count ||
                native.Inputs.Where((input, i) => !SameInput(input, offline.Inputs[i])).Any())
            { result.Message = "Trace identities, clocks or inputs differ."; return result; }
            for (int i = 0; i < Math.Max(native.Samples.Count, offline.Samples.Count); i++)
            {
                var a = i < native.Samples.Count ? native.Samples[i] : null;
                var b = i < offline.Samples.Count ? offline.Samples[i] : null;
                result.Frame = a?.Frame ?? b.Frame; result.Pu = a?.Pu ?? b.Pu; result.Phase = a?.Phase ?? b.Phase;
                if (a == null || b == null || a.Frame != b.Frame || a.Pu != b.Pu || a.Phase != b.Phase)
                { result.Message = "Sample schedule differs at sample " + i + "."; return result; }
                foreach (string key in a.Fields.Keys.Union(b.Fields.Keys).OrderBy(k => k, StringComparer.Ordinal))
                {
                    a.Fields.TryGetValue(key, out string av); b.Fields.TryGetValue(key, out string bv);
                    if (av == bv) continue;
                    result.Field = key; result.Expected = av ?? "<missing>"; result.Actual = bv ?? "<missing>";
                    result.Message = "First divergence at frame " + a.Frame + ", PU " + a.Pu + " (" + a.Phase + "): " + key;
                    return result;
                }
                result.Compared++;
            }
            result.Passed = result.Compared > 0;
            result.Message = result.Passed ? "All " + result.Compared + " samples match exactly." : "Empty traces cannot establish simulator parity.";
            return result;
        }
        private static bool SameInput(FrameInput a, FrameInput b) => Float(a.X) == Float(b.X) && Float(a.Y) == Float(b.Y) &&
            a.Held == b.Held && a.ExactEdges == b.ExactEdges && a.Press == b.Press && a.Release == b.Release;
    }
}
