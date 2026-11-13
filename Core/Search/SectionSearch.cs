using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Igtap.Tas.Core.Search
{
    // Archive labels preserve diversity; they are not exact-state equivalence.
    public sealed partial class SectionSearch
    {
        private sealed class Node
        {
            internal Node Parent;
            internal SearchState State;
            internal FrameInput Input;
            internal FrameInput[] Rows;
            internal MotionVector[] Positions;
            internal int Duration, Depth, Owner;
            internal long Id;
            internal double Score;
            internal double GoalScore=double.MaxValue,GoalDistance;
        }
        private sealed class Route
        {
            internal Node End;
            internal FrameInput[] Inputs;
            internal MotionVector[] Path;
            internal Node[] Nodes;
        }
        private sealed class Batch
        {
            internal readonly List<Node> Nodes = new List<Node>();
            internal readonly List<string> Reasons = new List<string>();
            internal long Frames, Pus, Evaluated, Unsupported;
            internal long LookaheadFrames;
        }
        private sealed class Candidate
        {
            internal FrameInput[] Inputs;
            internal Node Resume;
            internal bool Splice;
        }
        public SearchResult Run(SearchJob job, Func<bool> cancel, Action<SearchResult> progress,
            Func<SearchJob, ISearchSimulation> factory = null)
        {
            job.Validate();
            if (job.Options.Mode == "wave") throw new NotSupportedException("Use Wave search for this search mode.");
            if (factory == null && job.Simulator != SearchJob.Backend) throw new NotSupportedException("A game-code search requires its prepared engine host.");
            return new Runner(job, cancel, progress, factory ?? (j => new StaticPlatformSimulation(j))).Run();
        }
        private sealed class Runner
        {
            private readonly SearchJob _job;
            private readonly SearchOptions _options;
            private readonly Func<bool> _cancel;
            private readonly Action<SearchResult> _progress;
            private readonly Func<SearchJob, ISearchSimulation> _factory;
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private readonly FrameInput[] _actions;
            private readonly Random _random;
            private readonly SearchResult _result;
            private readonly List<string> _reasons = new List<string>();
            private readonly List<Route> _plateaus = new List<Route>();
            private Archive _archive;
            private readonly CheckpointRoute _route;
            private BeamWorkers _workers;
            private Node _root;
            private Route _best;
            private long _nextId, _frames, _pus;
            private double _lastPublish = -1;
            private int _round, _mutationSerial, _refinementStalls;
            internal Runner(SearchJob job, Func<bool> cancel, Action<SearchResult> progress, Func<SearchJob, ISearchSimulation> factory)
            {
                _job = job; _options = job.Options; _cancel = cancel; _progress = progress; _factory = factory;
                _route=new CheckpointRoute(_options);
                _actions = Actions(_options.Abilities).ToArray(); _random = new Random(_options.Seed);
                _result = new SearchResult { JobId = job.Id, MovieHash = job.MovieHash, Simulator = job.Simulator, Algorithm = "beam",
                    Scheduling = _options.ExperimentalWorkStealing ? "experimental work stealing" : "affinity",
                    Status = "Searching beam", Message = "Archive-guided external search; native verification required." };
            }
            private bool Stop() => _cancel?.Invoke() == true || _clock.Elapsed.TotalSeconds >= _options.Seconds;
            private bool AtGoal(SearchState state) => state.Checkpoint==_route.Count;
            internal SearchResult Run()
            {
                _root = new Node { State = _job.Start.Copy(), Owner = 0 };
                _root.State.Checkpoint=_route.Advance(0,_root.State.Player.Position);
                _result.CheckpointsReached=_root.State.Checkpoint;
                _archive = new Archive(_job);
                if (Stop()) return Finish();
                if (AtGoal(_root.State))
                { _result.Status = "Already at goal"; _result.Message = "The selected start already meets this goal."; Publish(true); return _result; }
                using (_workers = new BeamWorkers(_options.Workers, () => new RouteSearchSimulation(_factory(_job),_route)))
                try
                {
                    _workers.RunAll(worker => {
                        var sim = worker.Sim; var start=_job.Start.Copy();start.Checkpoint=_route.Advance(0,sim.State.Player.Position);sim.Restore(start);
                        if (worker.Index == 0) _root.State = sim.State.Copy();
                        (sim as IBranchSearchSimulation)?.CaptureBranch();
                    });
                    _root.Score = Score(_job, _root.State, 0,_route); _archive.Offer(_root);
                    if (!Stop() && _job.SeedInputs.Length > 0)
                    {
                        var seed = FilterSeed(_job.SeedInputs.Take(_options.FrameLimit).ToArray(), _options.Abilities);
                        Batch batch = null;
                        _workers.Run(0, worker => batch = Evaluate(worker, new Candidate { Inputs = seed, Resume = _root }, true));
                        Consume(batch);
                    }
                    Publish(false);
                    if (_best != null && !Stop()) Refine();
                    while (!Stop())
                    {
                        int limit = _job.Objective?.HasLookahead==true?_options.FrameLimit+1:_best?.Inputs.Length ?? _options.FrameLimit + 1;
                        var parents = _archive.Select(Math.Min(_options.BeamWidth, Math.Max(4, _options.Workers * 8)), _round, limit);
                        if (parents.Count == 0)
                        {
                            // An exhausted frontier or a local minimum is not
                            // an exhausted time budget. Keep mutating the best
                            // route, and explore mixed controls from retained
                            // states when refinement stalls.
                            bool refined = _best != null && Refine();
                            if (!Stop() && (!refined || _refinementStalls >= 192))
                            { Explore(); _refinementStalls = 0; }
                            _round++; Publish(false); continue;
                        }
                        Expand(parents); _round++;
                        if (_best != null && !Stop() && (_round % 2 == 0 || _archive.Pending == 0)) Refine();
                        Publish(false);
                    }
                }
                catch (OperationCanceledException) when (Stop()) { }
                finally { CaptureCacheStatistics(); }
                return Finish();
            }
            private SearchResult Finish()
            {
                _result.Status = _cancel?.Invoke() == true ? "Cancelled" : _best != null ? "Candidate found" :
                    _clock.Elapsed.TotalSeconds >= _options.Seconds ? "Budget reached" : "No candidate";
                _result.Message = _best != null ? "Unverified candidate. Verify in game before accepting. Search was heuristic, not exhaustive." :
                    "No solution found within these limits. Unsupported world interactions stop individual rollouts.";
                if (_best == null && _reasons.Count > 0) _result.Message += " " + _reasons[0];
                Publish(true); return _result;
            }
            private void CaptureCacheStatistics()
            {
                long hits = 0, replays = 0, replayFrames = 0, bytes = 0, replayPus = 0;
                if (_workers != null)
                foreach (var worker in _workers.Items)
                    if (worker.Simulation is IBranchSearchSimulation cached)
                    { var s = cached.CacheStatistics; hits += s.Hits; replays += s.Replays; replayFrames += s.ReplayFrames; bytes += s.Bytes; replayPus += s.ReplayPus; }
                _result.StateHits = hits; _result.StateReplays = replays; _result.ReplayFrames = replayFrames; _result.CacheBytes = bytes;
                _result.Pus = _pus + replayPus;
            }
            private void Publish(bool done)
            {
                if (!done && _clock.Elapsed.TotalSeconds - _lastPublish < .15) return;
                CaptureCacheStatistics(); _result.SimulatedFrames = _frames + _result.ReplayFrames;
                _result.Seconds = _clock.Elapsed.TotalSeconds; _result.Finished = done;
                _result.ArchiveCells = _archive?.Cells ?? 0; _result.BlockedExamples = _reasons.ToArray();
                _result.CheckpointCount=_route.Count;
                if (!done && _best != null)
                { _result.Status = "Improving Beam route"; _result.Message = "Reducing route frames and exploring alternatives for the remaining search budget. Native verification required."; }
                if (_best != null) { _result.BestFrames = _best.Inputs.Length; _result.Inputs = _best.Inputs; _result.Path = _best.Path;_result.BestScore=_best.End.GoalScore;_result.LookaheadDistance=_best.End.GoalDistance; }
                _progress?.Invoke(_result.Copy()); _lastPublish = _result.Seconds;
            }
            private void Consume(Batch batch)
            {
                if (batch == null) return;
                _frames += batch.Frames; _pus += batch.Pus; _result.Evaluated += batch.Evaluated; _result.Unsupported += batch.Unsupported;
                _result.LookaheadSimulatedFrames+=batch.LookaheadFrames;
                foreach (string reason in batch.Reasons) if (_reasons.Count < 8 && !_reasons.Contains(reason)) _reasons.Add(reason);
                foreach (var node in batch.Nodes)
                {
                    node.Id = ++_nextId; node.Score = Score(_job, node.State, node.Depth,_route);
                    _result.Depth = Math.Max(_result.Depth, node.Depth);
                    _result.CheckpointsReached=Math.Max(_result.CheckpointsReached,node.State.Checkpoint);
                    _result.NearestDistance = Math.Min(_result.NearestDistance, Distance(_route, node.State));
                    if (AtGoal(node.State))
                    {
                        _result.Reached++;
                        if (_best == null || node.GoalScore<_best.End.GoalScore || node.GoalScore==_best.End.GoalScore&&node.Depth < _best.Inputs.Length)
                        { _best = BuildRoute(node); _plateaus.Clear(); _plateaus.Add(_best); _mutationSerial = 0; _refinementStalls = 0; }
                        else if (node.GoalScore==_best.End.GoalScore&&node.Depth == _best.Inputs.Length && _plateaus.Count < 4)
                        {
                            var route = BuildRoute(node);
                            if (!_plateaus.Any(p => SameRows(p.Inputs, route.Inputs))) _plateaus.Add(route);
                        }
                    }
                    else _archive.Offer(node);
                }
            }
            private void Expand(List<Node> parents)
            {
                var groups = Partition(parents); var output = new Batch[parents.Count];
                int bestDepth = _job.Objective?.HasLookahead==true?_options.FrameLimit:_best?.Inputs.Length ?? _options.FrameLimit;
                _result.StolenTasks += _workers.RunTasks(groups,_options.ExperimentalWorkStealing,(worker,index) => {
                    {
                        if (Stop()) return;
                        var parent = parents[index]; var sim = worker.Sim; sim.Restore(parent.State);
                        var fast = sim as IBranchSearchSimulation; object branch = fast?.CaptureBranch();
                        var saved = fast == null ? sim.State.Copy() : null;
                        var batch = new Batch(); output[index] = batch;
                        int maximum = Math.Min(_options.FrameLimit - parent.Depth, bestDepth - parent.Depth);
                        maximum = Math.Min(maximum, Math.Max(_options.ChunkFrames, Math.Min(64, (int)Math.Ceiling(_job.Fps * .12))));
                        if (_route.DistanceToNext(parent.State) < Math.Max(_job.HalfWidth, _job.HalfHeight) * 3 + _route.Next(parent.State.Checkpoint).Radius)
                            maximum = Math.Min(maximum, Math.Max(2, (int)Math.Ceiling((double)_job.Fps / 50)));
                        foreach (var input in OrderedActions(parent))
                        {
                            if (Stop() || maximum <= 0) break;
                            if (fast != null) fast.RestoreBranch(branch); else sim.Restore(saved);
                            var positions = new List<MotionVector>(maximum); var nodes = new List<Node>();
                            for (int step = 1; step <= maximum && !Stop(); step++)
                            {
                                long before = sim.State.Pus; batch.Frames++;
                                if (!sim.Step(input, _options.Abilities, out string reason)) { Blocked(batch, reason); break; }
                                batch.Pus += sim.State.Pus - before; positions.Add(sim.State.Player.Position);
                                bool goal = AtGoal(sim.State);
                                if (goal || HoldEndpoint(step, maximum, _options.ChunkFrames))
                                {
                                    var node=new Node { Parent = parent, Input = input, Duration = step, Depth = parent.Depth + step,
                                        State = sim.State.Copy(), Owner = worker.Index };
                                    if(goal)PriceGoal(sim,node,batch);nodes.Add(node);
                                }
                                if (goal) break;
                            }
                            var trace = positions.ToArray(); foreach (var node in nodes) { node.Positions = trace; batch.Nodes.Add(node); }
                            batch.Evaluated++;
                        }
                    }
                });
                foreach (var batch in output) {if(batch!=null)_result.WorkerTasks++;Consume(batch);}
            }
            private List<int>[] Partition(List<Node> nodes)
            {
                var groups = Enumerable.Range(0, _options.Workers).Select(_ => new List<int>()).ToArray();
                int target = (nodes.Count + _options.Workers - 1) / _options.Workers;
                for (int i = 0; i < nodes.Count; i++)
                {
                    int owner = nodes[i].Owner, least = 0;
                    for (int j = 1; j < groups.Length; j++) if (groups[j].Count < groups[least].Count) least = j;
                    if (owner < 0 || owner >= groups.Length || groups[owner].Count >= target) owner = least;
                    groups[owner].Add(i);
                }
                return groups;
            }
            private Batch Evaluate(BeamWorkers.Worker worker, Candidate candidate, bool retain)
            {
                var batch = new Batch(); var sim = worker.Sim; var parent = candidate.Resume ?? _root;
                sim.Restore(parent.State);
                var rows = new List<FrameInput>(16); var positions = new List<MotionVector>(16);
                for (int frame = parent.Depth; frame < Math.Min(candidate.Inputs.Length, _options.FrameLimit) && !Stop(); frame++)
                {
                    var input = candidate.Inputs[frame]; long before = sim.State.Pus; batch.Frames++;
                    if (!sim.Step(input, _options.Abilities, out var reason)) { Blocked(batch, reason); break; }
                    batch.Pus += sim.State.Pus - before; rows.Add(input); positions.Add(sim.State.Player.Position);
                    bool goal = AtGoal(sim.State);
                    if (goal || rows.Count == 16 || frame + 1 == candidate.Inputs.Length)
                    {
                        parent = new Node { Parent = parent, Rows = rows.ToArray(), Positions = positions.ToArray(), Duration = rows.Count,
                            Depth = frame + 1, Owner = worker.Index, State = sim.State.Copy() };
                        if(goal)PriceGoal(sim,parent,batch);
                        batch.Nodes.Add(parent); rows.Clear(); positions.Clear();
                        if (retain) (sim as IBranchSearchSimulation)?.CaptureBranch();
                    }
                    if (goal) break;
                }
                batch.Evaluated++; return batch;
            }
            private bool Refine()
            {
                if (_best == null || Stop()) return false;
                var candidates = new List<Candidate>();
                for (int attempt = 0; attempt < 24 && candidates.Count < Math.Max(2, _options.Workers * 2); attempt++)
                {
                    var route = _plateaus[(_mutationSerial / 8) % _plateaus.Count];
                    var candidate = Mutate(route, _mutationSerial++, _random, _job, _archive);
                    if (candidate == null || candidate.Inputs.Length == 0 || candidate.Inputs.Length > _options.FrameLimit || SameRows(candidate.Inputs, route.Inputs)) continue;
                    if (!ReferenceEquals(route, _best)) _result.PlateauDrifts++;
                    candidates.Add(candidate);
                }
                if (candidates.Count == 0) { _refinementStalls += 4; return false; }
                var before = _best;
                var groups = Partition(candidates.Select(c => c.Resume ?? _root).ToList()); var output = new Batch[candidates.Count];
                _result.StolenTasks += _workers.RunTasks(groups,_options.ExperimentalWorkStealing,(worker,index) => {
                    if (!Stop()) output[index] = Evaluate(worker, candidates[index], false);
                });
                for (int i = 0; i < output.Length; i++)
                {
                    if (output[i] == null) continue;
                    _result.Refinements++; if (candidates[i].Splice) _result.Splices++;
                    _result.WorkerTasks++;
                    Consume(output[i]);
                }
                _refinementStalls = !ReferenceEquals(_best, before) ? 0 : _refinementStalls + candidates.Count; return true;
            }
            private void Explore()
            {
                // Random continuations can discover combinations that a
                // single repeated input cannot. Snapshots remain worker-owned;
                // Evaluate restores the selected state's immutable history.
                int limit = _job.Objective?.HasLookahead == true ? _options.FrameLimit : _best?.Inputs.Length ?? _options.FrameLimit;
                var candidates = new List<Candidate>();
                for (int attempt = 0; attempt < Math.Max(2, _options.Workers * 2) && !Stop(); attempt++)
                {
                    var parent = attempt % 4 == 0 ? _root : _archive.Sample(limit, _random) ?? _root;
                    if (parent.Depth >= limit) continue;
                    int end = Math.Min(limit, parent.Depth + Math.Max(32, Math.Min(512, _job.Fps * 2)));
                    var rows = new FrameInput[end];
                    // Prefix rows are already represented by parent; Evaluate
                    // reads only the generated suffix from parent.Depth onward.
                    for (int frame = parent.Depth; frame < end;)
                    {
                        var input = _actions[_random.Next(_actions.Length)];
                        int hold = Math.Min(end - frame, 1 << _random.Next(0, 7));
                        for (int i = 0; i < hold; i++) rows[frame++] = input;
                    }
                    candidates.Add(new Candidate { Inputs = rows, Resume = parent });
                }
                var output = new Batch[candidates.Count];
                var groups = Partition(candidates.Select(c => c.Resume).ToList());
                _result.StolenTasks += _workers.RunTasks(groups, _options.ExperimentalWorkStealing, (worker, index) => {
                    if (!Stop()) output[index] = Evaluate(worker, candidates[index], false);
                });
                foreach (var batch in output) { if (batch != null) _result.WorkerTasks++; Consume(batch); }
            }
            private void PriceGoal(ISearchSimulation simulation,Node node,Batch batch)
            {
                var price=_job.Objective?.Evaluate(simulation,node.Depth,_options.Abilities)??new CheckpointSearchObjective.Evaluation(node.Depth,0);
                node.GoalScore=price.Score;node.GoalDistance=price.Distance;
                batch.LookaheadFrames+=price.Frames;batch.Frames+=price.Frames;batch.Pus+=price.Pus;batch.Unsupported+=price.Unsupported;
            }
            private FrameInput[] OrderedActions(Node parent)
            {
                var ordered=(FrameInput[])_actions.Clone(); var p=parent.State.Player;
                var target=_route.Next(parent.State.Checkpoint);
                double dx=target.X-p.Position.X,dy=target.Y-p.Position.Y;
                double distance=Math.Max(1,Math.Sqrt(dx*dx+dy*dy));
                double Priority(FrameInput input)
                {
                    double value=(dx*input.X+dy*input.Y)/distance;
                    if(input.Down(Buttons.Jump))value+=dy>_job.HalfHeight && p.Grounded ? .5 : -.1;
                    if(input.Down(Buttons.Dash))value+=distance>_job.HalfWidth*6 && p.AirDashes>0 ? .25 : -.15;
                    return value;
                }
                Array.Sort(ordered,(a,b) => {int c=Priority(b).CompareTo(Priority(a));if(c!=0)return c;
                    c=((int)a.Held).CompareTo((int)b.Held);if(c!=0)return c;c=a.X.CompareTo(b.X);return c!=0?c:a.Y.CompareTo(b.Y);});
                return ordered;
            }
        }
        private static void Blocked(Batch batch, string reason)
        { if (reason != null) { batch.Unsupported++; if (batch.Reasons.Count < 8 && !batch.Reasons.Contains(reason)) batch.Reasons.Add(reason); } }
        private static bool HoldEndpoint(int frame, int maximum, int hint) => frame == 1 || frame == maximum || frame == hint || (frame & (frame - 1)) == 0;
        private static IEnumerable<FrameInput> Actions(SearchAbilities abilities)
        {
            for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++)
                for (int jump = 0; jump <= ((abilities & SearchAbilities.Jump) != 0 ? 1 : 0); jump++)
                    for (int dash = 0; dash <= ((abilities & SearchAbilities.Dash) != 0 ? 1 : 0); dash++)
                        yield return new FrameInput { X = x, Y = y, Held = (jump == 1 ? Buttons.Jump : 0) | (dash == 1 ? Buttons.Dash : 0) };
        }
        private static double Distance(CheckpointRoute route, SearchState state)
        { return route.Remaining(state); }
        private static double Score(SearchJob job, SearchState state, int depth,CheckpointRoute route)
        {
            var p = state.Player;var next=route.Next(state.Checkpoint);double dx=next.X-p.Position.X,dy=next.Y-p.Position.Y;
            double distance = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
            return route.Remaining(state) - Math.Min(distance * .25, (dx * p.Velocity.X + dy * p.Velocity.Y) / distance * .04) + depth * .015;
        }
        private static FrameInput[] FilterSeed(FrameInput[] inputs, SearchAbilities abilities)
        {
            Buttons mask = ((abilities & SearchAbilities.Jump) != 0 ? Buttons.Jump : 0) | ((abilities & SearchAbilities.Dash) != 0 ? Buttons.Dash : 0);
            for (int i = 0; i < inputs.Length; i++) { var row = inputs[i]; row.Held &= mask; row.Press &= mask; row.Release &= mask; inputs[i] = row; }
            return inputs;
        }
        private static Route BuildRoute(Node end)
        {
            var nodes = new List<Node>(); for (var n = end; n != null; n = n.Parent) nodes.Add(n); nodes.Reverse();
            var inputs = new FrameInput[end.Depth]; var path = new MotionVector[end.Depth + 1]; path[0] = nodes[0].State.Player.Position;
            foreach (var n in nodes) for (int i = 0; i < n.Duration; i++)
            { int frame = n.Depth - n.Duration + i; inputs[frame] = n.Rows == null ? n.Input : n.Rows[i]; path[frame + 1] = n.Positions[i]; }
            return new Route { End = end, Inputs = inputs, Path = path, Nodes = nodes.ToArray() };
        }
        private static bool SameRow(FrameInput a, FrameInput b) => a.X == b.X && a.Y == b.Y && a.Held == b.Held && a.ExactEdges == b.ExactEdges && a.Press == b.Press && a.Release == b.Release;
        private static bool SameRows(FrameInput[] a, FrameInput[] b)
        { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (!SameRow(a[i], b[i])) return false; return true; }
    }
}
