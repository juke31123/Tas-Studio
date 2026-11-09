using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using IGTAP.EngineSim;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Search;

// Runs the pinned upstream OfflineSolver itself. FrameSearch is its narrow
// execution adapter; archive selection, mutations, plateau walking, splicing,
// input compression and worker caches remain upstream code.
internal static class WaveSearch
{
    public static SearchResult Run(SearchJob job, Func<bool> cancel, Action<SearchResult> progress, Func<SearchJob, ISearchSimulation> factory)
    {
        if (job.Simulator != EngineSearchSpec.Backend) throw new NotSupportedException("Wave search requires the prepared game-code host.");
        var watch = Stopwatch.StartNew();
        var options = job.Options;
        long pus = 0, unsupported = 0;
        bool Stop() => cancel() || watch.Elapsed.TotalSeconds >= options.Seconds;
        bool Allowed(InputTick i) => float.IsFinite(i.x) && float.IsFinite(i.y) && Math.Abs(i.x) <= 1 && Math.Abs(i.y) <= 1 &&
            (!i.held || (options.Abilities & SearchAbilities.Jump) != 0) && (!i.dash || (options.Abilities & SearchAbilities.Dash) != 0) &&
            !i.reset && i.pause == 0 && i.pauseTick == 0 && i.pauseFrame == 0 && i.dashJump == 0 && i.springDash == 0 &&
            !i.lockX.HasValue && !i.lockY.HasValue && !i.turnX.HasValue && !i.turnY.HasValue;
        var seeds = new List<List<InputTick>>();
        var seed = job.SeedInputs.Take(options.Horizon).ToArray();
        if (seed.Length > 0 && seed.All(i => (i.Held & ~(Buttons.Jump | Buttons.Dash)) == 0))
        {
            var ticks = seed.Select(i => new InputTick { x = i.X, y = i.Y, held = i.Down(Buttons.Jump), dash = i.Down(Buttons.Dash) }).ToList();
            if (ticks.All(Allowed)) seeds.Add(ticks);
        }
        string caps = "NONE";
        if (job.WallJumpUnlocked && (options.Abilities & SearchAbilities.WallJump) != 0) caps = "WJ";
        if ((options.Abilities & SearchAbilities.Dash) != 0) caps = (caps == "NONE" ? "" : caps + "_") + "DS";
        OfflineSolver solver=null;
        solver = new OfflineSolver(() =>
        {
            var adapter = (EngineSearchSimulation)factory(job); var sim = adapter.Native;
            sim.ConfigureStudioOfflineSearch(input =>
            {
                int before = sim.StudioPus;
                var frame = new FrameInput { X = input.X, Y = input.Y, Held = (Buttons)input.Held,
                    ExactEdges = true, Press = (Buttons)input.Press, Release = (Buttons)input.Release };
                bool ok = adapter.StepNative(frame, default, options.Abilities, out var reason);
                Interlocked.Add(ref pus, sim.StudioPus - before);
                if (!ok && reason != null) Interlocked.Increment(ref unsupported);
                return ok;
            }, ()=>Stop() || solver.StudioStopped, options.Horizon, options.GoalX, options.GoalY, options.GoalRadius);
            return sim;
        }, caps, seeds, guide: 0, rng: options.Seed) { NoResets = true,
            StateBudget = 64L * 1024 * 1024, ArchiveBudget = 64L * 1024 * 1024,
            Accept = inputs => inputs.Count <= options.Horizon + 100 && inputs.All(Allowed) };
        var result = new SearchResult { JobId = job.Id, MovieHash = job.MovieHash, Simulator = job.Simulator, Algorithm = "wave",
            Status = "Wave search", Message = "Upstream archive/mutation/splice solver, adapted to movie frames. Native verification required." };
        void Publish(bool done)
        {
            List<InputTick> best;
            lock (solver.Shared) best = solver.Shared.Inputs?.ToList();
            result.Inputs = best == null ? Array.Empty<FrameInput>() : Frames(best, job.Start.Previous);
            result.BestFrames = best?.Count ?? -1; result.Depth = result.BestFrames < 0 ? 0 : result.BestFrames;
            result.Evaluated = Interlocked.Read(ref solver.Runs); result.SimulatedFrames = Interlocked.Read(ref solver.Ticks);
            result.Pus = Interlocked.Read(ref pus); result.Unsupported = Interlocked.Read(ref unsupported); result.Reached = Interlocked.Read(ref solver.Finishes);
            result.ArchiveCells = solver.StudioArchiveCells; result.Splices = Interlocked.Read(ref solver.Splices);
            result.PlateauDrifts = Interlocked.Read(ref solver.Drifts); result.StateHits = Interlocked.Read(ref solver.StateHits); result.StateReplays = Interlocked.Read(ref solver.StateReplays);
            result.Seconds = watch.Elapsed.TotalSeconds; result.Finished = done; progress?.Invoke(result.Copy());
        }
        var threads = solver.Start(options.Workers);
        try
        {
            while (threads.Any(t => t.IsAlive))
            {
                if (Stop()) solver.Stop();
                Publish(false);
                foreach (var thread in threads) thread.Join(50);
            }
            if (solver.StudioErrors.TryDequeue(out var error))
            { if(error is OperationCanceledException)throw error; throw new InvalidOperationException("Wave search worker failed: " + error.Message, error); }
            Publish(true);
            result.Status = cancel() ? "Cancelled" : result.Inputs.Length > 0 ? "Wave candidate found" : "Budget reached";
            result.Message = "Upstream exploration, mutations, plateau walking and splicing. Macro-tick technique offsets are excluded; input rows use this TAS's FPS. Best found, not a global optimum. Native verification required.";
            if (!cancel() && result.Inputs.Length > 0)
            {
                var sim = factory(job); sim.Restore(job.Start); var path = new List<MotionVector> { sim.State.Player.Position };
                foreach (var input in result.Inputs)
                { if (!sim.Step(input, options.Abilities, out var reason)) throw new InvalidOperationException("Wave search result failed its frame replay: " + reason); path.Add(sim.State.Player.Position); }
                result.Path = path.ToArray(); double dx=sim.State.Player.Position.X-options.GoalX,dy=sim.State.Player.Position.Y-options.GoalY; result.NearestDistance=Math.Sqrt(dx*dx+dy*dy);
                if (result.NearestDistance > options.GoalRadius) throw new InvalidOperationException("Wave search result did not reach its frame goal.");
            }
            return result;
        }
        finally { solver.Stop(); foreach (var thread in threads) thread.Join(); }
    }
    private static FrameInput[] Frames(List<InputTick> ticks, FrameInput previous)
    {
        var result = new FrameInput[ticks.Count];
        for (int i = 0; i < ticks.Count; i++)
        {
            var input = Simulation.StudioSearchInput(ticks[i], new StudioInput { Held = (int)previous.Held });
            result[i] = new FrameInput { X = input.X, Y = input.Y, Held = (Buttons)input.Held }; previous = result[i];
        }
        return result;
    }
}
