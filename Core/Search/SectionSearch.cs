using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Igtap.Tas.Core.Search
{
    // Bounded beam search: candidate evaluations run in parallel independent
    // worlds. Stable ordering keeps a fixed seed/budget independent of task order.
    // It reports best found, never global optimality or exhaustive coverage.
    public sealed class SectionSearch
    {
        private sealed class Node
        {
            public Node Parent;
            public SearchState State;
            public FrameInput Input;
            public int Duration, Depth;
            public long Id;
            public double Score;
        }
        public SearchResult Run(SearchJob job, Func<bool> cancel, Action<SearchResult> progress,
            Func<SearchJob, ISearchSimulation> factory = null)
        {
            job.Validate();
            if(job.Options.Mode=="wave") throw new NotSupportedException("Use Wave search for this search mode.");
            if(factory==null && job.Simulator!=SearchJob.Backend) throw new NotSupportedException("A game-code search requires its prepared engine host.");
            factory = factory ?? (j => new StaticPlatformSimulation(j));
            var options = job.Options; var clock = Stopwatch.StartNew(); double lastPublish = -1;
            var result = new SearchResult { JobId = job.Id, MovieHash = job.MovieHash, Simulator=job.Simulator, Status = "Searching",
                Message = "Experimental external search; all results require native verification." };
            var reasons = new List<string>(); var reasonLock = new object();
            var actions = Actions(options.Abilities).ToArray();
            var root = new Node { State = job.Start.Copy(), Depth = 0, Id = 0 };
            var beam = new List<Node> { root };
            Node best = null; long evaluations = 0, frames = 0, pus = 0, unsupported = 0, reached = 0;
            using var simulations = new ThreadLocal<ISearchSimulation>(() => factory(job), true);
            bool Stop() => cancel?.Invoke() == true || clock.Elapsed.TotalSeconds >= options.Seconds;
            bool AtGoal(SearchState state) => Distance(job, state) <= options.GoalRadius;
            void Publish(bool finished)
            {
                result.Evaluated = Interlocked.Read(ref evaluations); result.SimulatedFrames = Interlocked.Read(ref frames);
                result.Pus = Interlocked.Read(ref pus); result.Unsupported = Interlocked.Read(ref unsupported); result.Reached = Interlocked.Read(ref reached);
                result.Seconds = clock.Elapsed.TotalSeconds; result.Finished = finished;
                lock (reasonLock) result.BlockedExamples = reasons.ToArray();
                if (best != null) { result.BestFrames = best.Depth; result.Inputs = Inputs(best); result.Path = Path(root, result.Inputs, simulations.Value, options.Abilities); }
                progress?.Invoke(result.Copy()); lastPublish = result.Seconds;
            }
            if (AtGoal(root.State))
            { result.Status = "Already at goal"; result.Message = "The selected start already meets this goal. Choose a different location or a smaller radius."; Publish(true); return result; }
            // The existing suffix is only a search seed, and cannot bypass the
            // selected action restrictions. The live movie is never truncated here.
            if (job.SeedInputs.Length > 0)
            {
                var sim = simulations.Value; sim.Restore(root.State); Node parent = root;
                foreach (var raw in job.SeedInputs.Take(options.Horizon))
                {
                    var input = raw;
                    if ((options.Abilities & SearchAbilities.Jump) == 0) input.Held &= ~(Buttons.Jump | Buttons.Jump2);
                    if ((options.Abilities & SearchAbilities.Dash) == 0) input.Held &= ~(Buttons.Dash | Buttons.Dash2);
                    input.Held &= ~(Buttons.Menu | Buttons.Reset); input.ExactEdges = false;
                    if (Stop() || !sim.Step(input, options.Abilities, out _)) break;
                    parent = new Node { Parent = parent, Input = input, Duration = 1, Depth = parent.Depth + 1, State = sim.State.Copy() };
                    if (AtGoal(parent.State)) { best = parent; break; }
                }
            }
            Publish(false);
            for (int layer = 0; layer < options.Horizon && !Stop(); layer += options.ChunkFrames)
            {
                int duration = Math.Min(options.ChunkFrames, options.Horizon - layer);
                var children = new Node[beam.Count * actions.Length];
                Parallel.For(0, children.Length, new ParallelOptions { MaxDegreeOfParallelism = options.Workers }, index =>
                {
                    if (Stop()) return;
                    Node parent = beam[index / actions.Length]; var input = actions[index % actions.Length];
                    if (best != null && parent.Depth >= best.Depth) return;
                    var sim = simulations.Value; sim.Restore(parent.State);
                    int steps = 0; long startPus = sim.State.Pus;
                    for (; steps < duration;)
                    {
                        Interlocked.Increment(ref frames);
                        if (!sim.Step(input, options.Abilities, out var reason))
                        {
                            if (reason != null) { Interlocked.Increment(ref unsupported); lock (reasonLock) if (reasons.Count < 8 && !reasons.Contains(reason)) reasons.Add(reason); }
                            Interlocked.Increment(ref evaluations); return;
                        }
                        steps++;
                        if (AtGoal(sim.State)) break;
                    }
                    Interlocked.Add(ref pus, sim.State.Pus - startPus); Interlocked.Increment(ref evaluations);
                    var state = sim.State.Copy();
                    children[index] = new Node { Parent = parent, State = state, Input = input, Duration = steps,
                        Depth = parent.Depth + steps, Id = ((long)layer + 1) * 100000000 + index,
                        Score = Score(job, state) };
                });
                var next = new List<Node>(children.Length);
                foreach (var child in children)
                {
                    if (child == null) continue;
                    double distance = Distance(job, child.State); result.NearestDistance = Math.Min(result.NearestDistance, distance);
                    if (AtGoal(child.State))
                    {
                        reached++;
                        if (best == null || child.Depth < best.Depth || child.Depth == best.Depth && child.Score < best.Score) best = child;
                    }
                    else next.Add(child);
                }
                result.Depth = Math.Min(options.Horizon, layer + duration);
                if (best != null && best.Depth <= layer + duration) break;
                // Coarse grouping is a heuristic archive only, never an exact
                // state equivalence claim. Keep multiple velocity/resource states.
                beam = next.OrderBy(n => n.Score).ThenBy(n => n.Id).GroupBy(Key).Select(g => g.First()).Take(options.BeamWidth).ToList();
                if (clock.Elapsed.TotalSeconds - lastPublish > .1) Publish(false);
                if (beam.Count == 0) break;
            }
            result.Status = cancel?.Invoke() == true ? "Cancelled" : best != null ? "Candidate found" : clock.Elapsed.TotalSeconds >= options.Seconds ? "Budget reached" : "No candidate";
            result.Message = best != null ? "Unverified candidate. Verify in game before accepting. Search was heuristic, not exhaustive." :
                "No solution found within these limits. Unsupported world interactions stop individual rollouts.";
            if (best == null && reasons.Count > 0) result.Message += " " + reasons[0];
            Publish(true); return result;
        }
        private static IEnumerable<FrameInput> Actions(SearchAbilities abilities)
        {
            for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++)
                for (int jump = 0; jump <= ((abilities & SearchAbilities.Jump) != 0 ? 1 : 0); jump++)
                    for (int dash = 0; dash <= ((abilities & SearchAbilities.Dash) != 0 ? 1 : 0); dash++)
                        yield return new FrameInput { X = x, Y = y, Held = (jump == 1 ? Buttons.Jump : 0) | (dash == 1 ? Buttons.Dash : 0) };
        }
        private static double Distance(SearchJob job, SearchState state)
        { double x = state.Player.Position.X - job.Options.GoalX, y = state.Player.Position.Y - job.Options.GoalY; return Math.Sqrt(x * x + y * y); }
        private static double Score(SearchJob job, SearchState state)
        {
            var p = state.Player; double dx = job.Options.GoalX - p.Position.X, dy = job.Options.GoalY - p.Position.Y;
            double distance = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
            return distance - Math.Min(distance * .25, (dx * p.Velocity.X + dy * p.Velocity.Y) / distance * .03);
        }
        private static string Key(Node node)
        {
            var p = node.State.Player;
            return Math.Floor(p.Position.X / 8) + ":" + Math.Floor(p.Position.Y / 8) + ":" + Math.Floor(p.Velocity.X / 100) + ":" +
                Math.Floor(p.Velocity.Y / 100) + ":" + p.AirDashes + ":" + p.AirJumps + ":" + p.Dashing + ":" + p.Grounded + ":" +
                (int)node.Input.Held + ":" + node.Input.X + ":" + node.Input.Y + ":" + node.State.Phase.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }
        private static FrameInput[] Inputs(Node node)
        {
            var result = new FrameInput[node.Depth];
            for (var n = node; n.Parent != null; n = n.Parent) for (int i = n.Depth - n.Duration; i < n.Depth; i++) result[i] = n.Input;
            return result;
        }
        private static MotionVector[] Path(Node root, FrameInput[] inputs, ISearchSimulation sim, SearchAbilities abilities)
        {
            sim.Restore(root.State); var path = new List<MotionVector> { sim.State.Player.Position };
            foreach (var input in inputs) { if (!sim.Step(input, abilities, out _)) break; path.Add(sim.State.Player.Position); }
            return path.ToArray();
        }
    }
}
