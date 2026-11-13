using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Search;
using Igtap.Tas.Core.Simulation;

internal static class TrajectoryRunner
{
    private sealed class Superseded : Exception { }
    // One owning thread and one private world. Forecasting always restores its
    // branch, including contacts, timers, movers, economy and random state.
    public static int Run(string directory, int parent)
    {
        try
        {
            var trace = SimulationTrace.Read(Path.Combine(directory, "native.trace.json"));
            EngineFixture.Validate(directory, trace);
            var job = new SearchJob { Fps = trace.Fps, Options = new SearchOptions { Abilities = (SearchAbilities)15 } };
            long nextPoll = 0; bool stopped = false;
            bool Stop()
            {
                long now = Stopwatch.GetTimestamp();
                if (now >= nextPoll) { nextPoll = now + Stopwatch.Frequency / 10; stopped = File.Exists(Path.Combine(directory, "stop")) || !ParentAlive(parent); }
                return stopped;
            }
            var sim = new EngineSearchSimulation(job, directory, trace, Stop);
            bool custom = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(directory, "world.json")))["customMap"] != null;
            // Bounded native snapshots belong to this process/thread only.
            // Forecast frames allocate no search nodes or periodic branch cache.
            var checkpoints = new List<(int Frame, object State)> { (0, sim.CapturePreview()) };
            object committed = checkpoints[0].State; int committedFrame = 0;
            var history = new List<FrameInput>(); long last = 0;
            DateTime read = DateTime.MinValue;
            SearchFiles.Write(Path.Combine(directory, "ready.json"), new { Ready = true, Protocol = TrajectoryWire.Version });
            while (!Stop())
            {
                string path = Path.Combine(directory, "request.bin");
                if (!File.Exists(path)) { Thread.Sleep(5); continue; }
                var stamp = File.GetLastWriteTimeUtc(path);
                if (stamp == read) { Thread.Sleep(5); continue; }
                if (!TrajectoryWire.ReadRequest(path, out var request)) { Thread.Sleep(5); continue; }
                read = stamp;
                if (request.Id <= last) { Thread.Sleep(5); continue; }
                last = request.Id;
                var result = new TrajectoryResult { Id = request.Id, Frame = request.Frame, Fps = trace.Fps, Input = request.Input,
                    SnapshotEstimate = trace.StartFrame != 0 };
                try
                {
                    request.Validate();
                    if (request.Frame != trace.StartFrame + request.Prefix.Length) throw new InvalidDataException("Preview frame does not match its input history.");
                    int common = 0;
                    while (common < history.Count && common < request.Prefix.Length && TrajectoryRequest.Same(history[common], request.Prefix[common])) common++;
                    var checkpoint = committedFrame >= 0 && committedFrame <= common ? (Frame:committedFrame, State:committed) : checkpoints.Last(c => c.Frame <= common);
                    sim.RestorePreview(checkpoint.State);
                    checkpoints.RemoveAll(c => c.Frame > common);
                    history.RemoveRange(checkpoint.Frame, history.Count - checkpoint.Frame);
                    for (int i = checkpoint.Frame; i < request.Prefix.Length; i++)
                    {
                        if (Stop()) throw new OperationCanceledException();
                        if (!sim.StepPreview(request.Prefix[i], out string reason)) throw new InvalidOperationException(reason ?? "The preview history contains a transition outside simulator coverage.");
                        history.Add(request.Prefix[i]);
                        if (history.Count % 128 == 0)
                        {
                            checkpoints.RemoveAll(c => c.Frame >= history.Count);
                            checkpoints.Add((history.Count, sim.CapturePreview()));
                            if (checkpoints.Count > 8) checkpoints.RemoveAt(1); // retain the original root for arbitrary rewind
                        }
                    }
                    committed = sim.CapturePreview(); committedFrame = history.Count;
                    var actual = EngineFixture.Sample(sim.Native, 0, 0, "start", trace.CaptureOrbs, trace.CaptureMovers, trace.CaptureSprings, trace.CaptureUpgrades);
                    if (request.NextFramePrepared) actual.Fields["clock/capture-ticks"]=sim.Native.StudioPreparedCaptureTicks().ToString(System.Globalization.CultureInfo.InvariantCulture);
                    string difference = request.Expected.Fields.FirstOrDefault(p => !actual.Fields.TryGetValue(p.Key, out string value) ||
                        !CustomMapPhysics.Matches(p.Key, p.Value, value, custom)).Key;
                    if (difference != null) throw new InvalidOperationException("Live state differs at " + difference + " (native " + request.Expected.Fields[difference] + ", simulated " +
                        (actual.Fields.TryGetValue(difference,out var differentValue) ? differentValue : "missing") + "). Pause to refresh the preview snapshot.");
                    var points = new List<TrajectoryPoint>(request.Frames + 1) { Point(sim) };
                    var elapsed = Stopwatch.StartNew();
                    try
                    {
                        for (int i = 0; i < request.Frames; i++)
                        {
                            if (Stop()) throw new OperationCanceledException();
                            if ((i & 31) == 0 && File.GetLastWriteTimeUtc(path) != read) throw new Superseded();
                            var row = i == 0 ? request.Input : TrajectoryRequest.Hold(request.Input);
                            if (!sim.StepPreview(row, out string reason))
                            {
                                result.End = "unsupported"; result.Message = reason; break;
                            }
                            points.Add(Point(sim));
                            // Include the fatal position, after the actual native
                            // spike grace/callbacks, rather than the preceding PU.
                            if (sim.State.Player.Dead) { result.End = "death"; result.Message = "Native death ends this preview."; break; }
                        }
                    }
                    finally { sim.RestorePreview(committed); }
                    result.SimulationMilliseconds = elapsed.Elapsed.TotalMilliseconds;
                    result.Points = points.ToArray(); result.Ready = true;
                    if (result.Message.Length == 0) result.Message = result.SnapshotEstimate
                        ? "Game-code snapshot estimate; some pre-snapshot physics history is unavailable."
                        : "Game-code preview: held controls, TAS FPS and 50 Hz physics. Unsupported interactions stop the path.";
                }
                catch (Superseded) { continue; } // branch was restored; start the newest controls immediately
                catch (Exception error) { committedFrame=-1; result.Message = error.Message; result.End = "unavailable"; }
                while (!Stop() && !TrajectoryWire.WriteResult(Path.Combine(directory, "result.bin"), result)) Thread.Sleep(5);
            }
            return 0;
        }
        catch (Exception error)
        { SearchFiles.Write(Path.Combine(directory, "failure.json"), new { Message = error.ToString() }); return 1; }
    }
    private static TrajectoryPoint Point(EngineSearchSimulation sim) => new TrajectoryPoint { Motion = sim.State.Player.Copy(), Pus = sim.State.Pus, Actions=sim.PreviewActions() };
    private static bool ParentAlive(int id)
    { try { using var process = Process.GetProcessById(id); return !process.HasExited; } catch { return false; } }
}
