using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Search;

internal static class BeamSearchTests
{
    private sealed class Counts
    {
        internal long Steps, Captures, Restores;
        internal readonly ConcurrentDictionary<int, int> Owners = new ConcurrentDictionary<int, int>();
    }
    private sealed class Sim : IBranchSearchSimulation
    {
        private readonly SearchJob _job;
        private readonly Counts _counts;
        private readonly int _thread = Thread.CurrentThread.ManagedThreadId, _kind;
        private int _frame, _stage;
        private readonly struct Mark
        {
            internal readonly int Frame, Stage;
            internal Mark(int frame, int stage) { Frame = frame; Stage = stage; }
        }
        private sealed class Branch { internal Sim Owner; internal SearchState State; }
        public SearchState State { get; private set; }
        public SearchCacheStatistics CacheStatistics => new SearchCacheStatistics(_counts.Restores, 0, 0, 0);
        internal Sim(SearchJob job, Counts counts, int kind)
        { _job = job; _counts = counts; _kind = kind; _counts.Owners.TryAdd(_thread, 1); Restore(job.Start); }
        private void CheckThread()
        { if (Thread.CurrentThread.ManagedThreadId != _thread) throw new Exception("A simulator moved between worker threads."); }
        public void Restore(SearchState state)
        {
            CheckThread(); State = state.Copy();
            var mark = state.BackendState is Mark saved ? saved : default; _frame = mark.Frame; _stage = mark.Stage;
        }
        public object CaptureBranch()
        { CheckThread(); Interlocked.Increment(ref _counts.Captures); return new Branch { Owner = this, State = State.Copy() }; }
        public void RestoreBranch(object token)
        {
            CheckThread(); var branch = (Branch)token;
            if (!ReferenceEquals(branch.Owner, this)) throw new Exception("A branch crossed simulator ownership.");
            Interlocked.Increment(ref _counts.Restores); Restore(branch.State);
        }
        public bool Step(FrameInput input, SearchAbilities abilities, out string reason)
        {
            CheckThread(); Interlocked.Increment(ref _counts.Steps); reason = null;
            _frame++; var p = State.Player;
            if (_kind == 0)
            {
                float x = Math.Max(-3, Math.Min(5, p.Position.X + input.X)), y = Math.Max(0, Math.Min(4, p.Position.Y + input.Y));
                if (p.Position.X <= 0 && x > 0 && y < 3) x = 0;
                p.Position = new MotionVector(x, y);
            }
            else if (_kind == 1 || _kind == 2)
            {
                bool press = input.Pressed(Buttons.Jump, State.Previous), release = input.Released(Buttons.Jump, State.Previous);
                if (press && _frame != 3 || _frame == 3 && (!press || _kind == 2 && !release)) return false;
                if (_frame == 3) _stage = 1;
                p.JumpBuffered = _stage == 1;
                if (_frame >= 8 && _stage == 1) p.Position = new MotionVector(10, 0);
            }
            else p.Position = new MotionVector(p.Position.X + input.X * (input.Y < 0 ? 2 : 1), 0);
            State.Previous = input; State.WallJumps = _stage;
            State.Time = (double)_frame / _job.Fps; State.Pus = _frame * 50 / _job.Fps; State.Phase = State.Time % .02;
            State.BackendState = new Mark(_frame, _stage); return true;
        }
    }
    private static SearchJob Job(int horizon = 20) => new SearchJob { Fps = 280, HalfWidth = 1, HalfHeight = 1,
        Start = new SearchState { Player = new PlayerMotion() }, Options = new SearchOptions {
            GoalSet = true, GoalX = 4, GoalY = 0, GoalRadius = 1, Horizon = horizon, Workers = 2, Seconds = 2,
            Abilities = SearchAbilities.None, BeamWidth = 8, ChunkFrames = 6 } };
    public static void Run()
    {
        int checks = 0;
        void Check(bool condition, string why) { if (!condition) throw new Exception(why); checks++; }
        var detour = Job(); var counts = new Counts(); int publications = 0;
        var route = new SectionSearch().Run(detour, () => false, _ => publications++, j => new Sim(j, counts, 0));
        Check(route.Inputs.Length > 0, "Archive-guided Beam finds a route that initially moves away from its goal.");
        Check(route.Path.Max(p => p.Y) >= 3, "The detour actually goes around the wall.");
        Check(route.ArchiveCells > 1 && route.Algorithm == "beam", "Beam exposes its own archive and algorithm identity.");
        Check(route.Path.Length == route.Inputs.Length + 1, "Winning previews contain every movie frame.");
        Check(counts.Steps == route.SimulatedFrames, "Progress and final previews do not replay the winning route.");
        Check(counts.Captures > 0 && counts.Restores > counts.Captures, "Sibling branches reuse captured parent snapshots.");
        Check(counts.Owners.Count <= detour.Options.Workers, "Beam creates at most one world per dedicated worker.");
        Check(publications >= 2 && route.Verification == "Unverified", "Progress never substitutes for native verification.");

        var slow = Job(200); slow.Fps = 50; slow.Options.GoalX = 3; slow.Options.GoalY = 1; slow.Options.ChunkFrames = 1;
        slow.Start.Player.Position = new MotionVector(0, 1); slow.Start.Player.Grounded = true; slow.Start.Player.WallJumpLock = -1;
        slow.Parameters = new MovementParameters { RunSpeed = .1f, FramesToTopSpeed = 1, Gravity = 10, FastFallMultiplier = 1, MaxDownwardsSpeed = 100 };
        slow.Geometry = new[] { new SearchBody { MinX = -1000, MinY = -10, MaxX = 1000, MaxY = 0 } };
        var slowResult = new SectionSearch().Run(slow, () => false, null);
        Check(slowResult.Inputs.Length > 0, "Slow sub-bucket motion continues until the next spatial archive bucket: " + slowResult.Status + ", depth " + slowResult.Depth + ", nearest " + slowResult.NearestDistance + ", cells " + slowResult.ArchiveCells);

        var narrow = Job(8); narrow.Options.GoalX = 10; narrow.Options.Abilities = SearchAbilities.Jump;
        var window = new SectionSearch().Run(narrow, () => false, null, j => new Sim(j, new Counts(), 1));
        Check(window.Inputs.Length == 8, "Mixed holds retain a one-frame input window even with a six-frame hint.");
        Check(window.Inputs[2].Pressed(Buttons.Jump, window.Inputs[1]), "Jump is performed on frame three between PUs.");

        narrow.SeedInputs = Enumerable.Repeat(default(FrameInput), 8).ToArray();
        narrow.SeedInputs[2] = new FrameInput { ExactEdges = true, Press = Buttons.Jump, Release = Buttons.Jump };
        var tap = new SectionSearch().Run(narrow, () => false, null, j => new Sim(j, new Counts(), 2));
        Check(tap.Inputs.Length == 8 && tap.Inputs[2].ExactEdges && tap.Inputs[2].Press == Buttons.Jump && tap.Inputs[2].Release == Buttons.Jump,
            "Existing same-frame press/release taps survive seed evaluation and result output.");

        var optimise = Job(30); optimise.Options.GoalX = 20;
        optimise.SeedInputs = Enumerable.Repeat(new FrameInput { X = 1 }, 20).ToArray();
        var improved = new SectionSearch().Run(optimise, () => false, null, j => new Sim(j, new Counts(), 3));
        Check(improved.Inputs.Length == 10 && improved.Refinements > 0, "Beam improves an existing route rather than returning its first seed.");
        Check(improved.Inputs.All(i => (i.Held | i.Press | i.Release) == Buttons.None), "Refinement honours disabled abilities.");
        var final = new Sim(optimise, new Counts(), 3);
        foreach (var input in improved.Inputs) Check(final.Step(input, SearchAbilities.None, out _), "A refined sequence is concrete replayable input.");
        Check(Math.Abs(final.State.Player.Position.X - optimise.Options.GoalX) <= optimise.Options.GoalRadius, "The refined route reaches the goal when replayed.");

        var cancelCounts = new Counts(); var cancelJob = Job(200); cancelJob.Options.GoalX = 10000;
        var cancelled = new SectionSearch().Run(cancelJob, () => Interlocked.Read(ref cancelCounts.Steps) >= 50, null, j => new Sim(j, cancelCounts, 3));
        Check(cancelled.Status == "Cancelled" && cancelled.Finished, "Cancellation stops dedicated workers and publishes a final result.");
        Check(cancelCounts.Steps < 100, "Workers observe cancellation inside input holds.");

        var queues=new[] {Enumerable.Range(0,20).ToList(),new System.Collections.Generic.List<int>()};
        var visits=new int[20];
        using(var rendezvous=new ManualResetEventSlim())
        using(var workers=new BeamWorkers(2,()=>throw new Exception("This queue test does not need a world.")))
        {
            long stolen=workers.RunTasks(queues,true,(worker,index)=>{
                if(worker.Index==1)rendezvous.Set();
                if(worker.Index==0 && index==0 && !rendezvous.Wait(3000))throw new Exception("An idle worker never stole queued work.");
                Interlocked.Increment(ref visits[index]);
            });
            Check(stolen>0,"Idle experimental workers steal queued tasks.");
            Check(visits.All(count=>count==1),"Every work-stealing task executes exactly once.");
        }
        var stealingJob=Job();stealingJob.Options.ExperimentalWorkStealing=true;
        var stealingCounts=new Counts();
        var stealing=new SectionSearch().Run(stealingJob,()=>false,null,j=>new Sim(j,stealingCounts,0));
        Check(stealing.Inputs.Length>0 && stealing.Scheduling=="experimental work stealing","Experimental scheduling finds and labels a concrete detour candidate.");
        Check(stealingCounts.Owners.Count<=2 && stealingCounts.Restores>0,"Stealing retains fixed simulator threads and worker-owned snapshot restoration.");
        var cancellingSteal=Job(200);cancellingSteal.Options.ExperimentalWorkStealing=true;cancellingSteal.Options.GoalX=10000;
        var stealingCancelCounts=new Counts();
        var stealCancelled=new SectionSearch().Run(cancellingSteal,()=>Interlocked.Read(ref stealingCancelCounts.Steps)>=50,null,j=>new Sim(j,stealingCancelCounts,3));
        Check(stealCancelled.Finished && stealCancelled.Status=="Cancelled","Cancellation joins all experimental workers.");

        var raw = new FrameInput { Held = Buttons.Jump, ExactEdges = true, Press = Buttons.Jump };
        var joined = SectionSearch.RebaseInput(raw, default, new FrameInput { Held = Buttons.Jump });
        Check(!joined.ExactEdges && !joined.Pressed(Buttons.Jump, new FrameInput { Held = Buttons.Jump }), "A splice does not turn a continuous hold into a fresh press.");
        var sameFrame = new FrameInput { ExactEdges = true, Press = Buttons.Jump, Release = Buttons.Jump };
        var preserved = SectionSearch.RebaseInput(sameFrame, new FrameInput { Held = Buttons.Jump }, default);
        Check(preserved.Press == Buttons.Jump && preserved.Release == Buttons.Jump, "An edited join preserves explicit same-frame taps.");
        Console.WriteLine("Beam archive/refinement checks passed: " + checks);
    }
}
