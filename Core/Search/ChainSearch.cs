using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Igtap.Tas.Core.Search
{
    public sealed class ChainSearch
    {
        public SearchResult Run(SearchJob job, Func<bool> cancel, Action<SearchResult> progress, Func<SearchJob, ISearchSimulation> factory = null)
        {
            job.Validate(); var actions = InputChain.Parse(job.Options.Chain, job.Options.Abilities, job.Options.FrameLimit);
            var route=new CheckpointRoute(job.Options);
            if(factory==null && job.Simulator!=SearchJob.Backend) throw new NotSupportedException("A game-code search requires its prepared engine host.");
            var watch = Stopwatch.StartNew(); var gate = new object(); long evaluated = 0, frames = 0, pus = 0, unsupported = 0, reaches = 0;
            var result = new SearchResult { JobId = job.Id, MovieHash = job.MovieHash, Simulator=job.Simulator, Status = "Searching chain", ChainCombinations = InputChain.Combinations(actions) };
            int best = int.MaxValue; FrameInput[] winner = null; MotionVector[] winnerPath = null; double nearest = double.MaxValue, lastReport = -1,bestScore=double.MaxValue;
            long lookahead=0;
            bool Stop() => cancel?.Invoke() == true || watch.Elapsed.TotalSeconds >= job.Options.Seconds;
            double Distance(SearchState state)
            { return route.Remaining(state); }
            void Publish(bool done)
            {
                lock (gate)
                {
                    if (!done && watch.Elapsed.TotalSeconds - lastReport < .15) return;
                    result.Evaluated = Interlocked.Read(ref evaluated); result.SimulatedFrames = Interlocked.Read(ref frames); result.Pus = Interlocked.Read(ref pus);
                    result.Unsupported = Interlocked.Read(ref unsupported); result.Reached = reaches; result.Seconds = watch.Elapsed.TotalSeconds;
                    result.BestFrames = winner == null ? -1 : winner.Length; result.Inputs = winner ?? Array.Empty<FrameInput>(); result.Path = winnerPath ?? Array.Empty<MotionVector>();
                    result.CheckpointCount=route.Count;
                    result.BestScore=bestScore;result.LookaheadSimulatedFrames=Interlocked.Read(ref lookahead);
                    result.NearestDistance = nearest; result.Finished = done; progress?.Invoke(result.Copy()); lastReport = result.Seconds;
                }
            }
            int firstChoices = actions[0].Maximum - actions[0].Minimum + 1;
            using var simulations = new ThreadLocal<ISearchSimulation>(() => new RouteSearchSimulation(factory == null ? new StaticPlatformSimulation(job) : factory(job),route), true);
            Parallel.For(0, firstChoices, new ParallelOptions { MaxDegreeOfParallelism = job.Options.Workers }, first =>
            {
                var sim = simulations.Value;var root=job.Start.Copy();root.Checkpoint=route.Advance(0,sim.State.Player.Position);sim.Restore(root);
                var inputs = new List<FrameInput>(job.Options.FrameLimit); var path = new List<MotionVector> { sim.State.Player.Position };
                void Explore(int actionIndex)
                {
                    if (Stop()) return;
                    if (actionIndex == actions.Length)
                    {
                        Interlocked.Increment(ref evaluated); double distance = Distance(sim.State);
                        bool reached=route.Finished(sim.State);
                        var price=reached?job.Objective?.Evaluate(sim,inputs.Count,job.Options.Abilities)??new CheckpointSearchObjective.Evaluation(inputs.Count,0):default;
                        if(reached){Interlocked.Add(ref frames,price.Frames);Interlocked.Add(ref pus,price.Pus);Interlocked.Add(ref unsupported,price.Unsupported);Interlocked.Add(ref lookahead,price.Frames);}
                        lock (gate)
                        {
                            nearest = Math.Min(nearest, distance); result.Depth = Math.Max(result.Depth, inputs.Count);
                            result.CheckpointsReached=Math.Max(result.CheckpointsReached,sim.State.Checkpoint);
                            if (reached)
                            {
                                reaches++;
                                // Lexicographic input order makes equal-length ties
                                // independent of which worker published first.
                                var candidate = inputs.ToArray();
                                if (price.Score<bestScore || price.Score==bestScore&&(candidate.Length < best || candidate.Length == best && Earlier(candidate, winner)))
                                { bestScore=price.Score;best = candidate.Length; winner = candidate; winnerPath = path.ToArray();result.LookaheadDistance=price.Distance; }
                            }
                        }
                        Publish(false); return;
                    }
                    var action = actions[actionIndex]; int minimum = actionIndex == 0 ? action.Minimum + first : action.Minimum;
                    int maximum = actionIndex == 0 ? minimum : action.Maximum;
                    var start = sim.State.Copy(); int startLength = inputs.Count, pathLength = path.Count;
                    for (int duration = 1; duration <= maximum && !Stop(); duration++)
                    {
                        if (inputs.Count >= job.Options.FrameLimit) break;
                        long before = sim.State.Pus; Interlocked.Increment(ref frames);
                        if (!sim.Step(action.Input, job.Options.Abilities, out string reason))
                        { if (reason != null) Interlocked.Increment(ref unsupported); Interlocked.Increment(ref evaluated); break; }
                        Interlocked.Add(ref pus, sim.State.Pus - before); inputs.Add(action.Input); path.Add(sim.State.Player.Position);
                        if (duration < minimum) continue;
                        var resume = sim.State.Copy(); int length = inputs.Count, paths = path.Count;
                        Explore(actionIndex + 1);
                        sim.Restore(resume); inputs.RemoveRange(length, inputs.Count - length); path.RemoveRange(paths, path.Count - paths);
                    }
                    sim.Restore(start); inputs.RemoveRange(startLength, inputs.Count - startLength); path.RemoveRange(pathLength, path.Count - pathLength);
                }
                Explore(0);
            });
            bool stopped = Stop(); result.Status = cancel?.Invoke() == true ? "Cancelled" : stopped ? "Budget reached" : winner == null ? "Chain exhausted" : "Chain candidate found";
            result.Message = (stopped ? "Incomplete chain search. " : "Finite chain enumeration complete. ") +
                "Upper-bound combinations " + InputChain.Combinations(actions) + ". " +
                (unsupported > 0 ? "Unsupported branches prevent an optimality claim. " : "") + "All predictions require native verification.";
            Publish(true); return result;
        }
        private static bool Earlier(FrameInput[] a, FrameInput[] b)
        {
            if (b == null) return true;
            for (int i = 0; i < a.Length; i++)
            {
                int compare = a[i].X.CompareTo(b[i].X); if (compare != 0) return compare < 0;
                compare = a[i].Y.CompareTo(b[i].Y); if (compare != 0) return compare < 0;
                compare = a[i].Held.CompareTo(b[i].Held); if (compare != 0) return compare < 0;
            }
            return false;
        }
    }
}
