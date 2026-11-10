using System;
using System.IO;
using System.Linq;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Search;

internal static class SearchTests
{
    public static SearchJob FlatJob(int fps = 50) => new SearchJob {
        Fps = fps, MovieHash = "test", HalfWidth = 1, HalfHeight = 1,
        Options = new SearchOptions { GoalSet = true, GoalX = 10, GoalY = 1, GoalRadius = 1, Horizon = 40, Seconds = 10,
            Workers = 2, Abilities = SearchAbilities.None, ChunkFrames = 1, BeamWidth = 32 },
        Start = new SearchState { Player = new PlayerMotion { Position = new MotionVector(0, 1), Grounded = true, WallJumpLock = -1 }, Phase = FrameTiming.InitialPhase(fps) },
        Parameters = new MovementParameters { RunSpeed = 5, FramesToTopSpeed = 1, Gravity = 10, MaxDownwardsSpeed = 100, FastFallMultiplier = 1 },
        Geometry = new[] { new SearchBody { MinX = -100, MaxX = 100, MinY = -10, MaxY = 0 } }
    };
    public static void Run()
    {
        int count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        void Reject(Action action, string message) { try { action(); } catch { count++; return; } throw new Exception("Expected rejection: " + message); }
        string path = Path.Combine(Path.GetTempPath(), "tas-search-" + Guid.NewGuid().ToString("N"));
        try
        {
            var job = FlatJob(); job.Validate();
            var bad = FlatJob(); bad.Options.GoalX = float.NaN; Reject(bad.Validate, "Non-finite goal");
            bad = FlatJob(); bad.Options.Workers = 0; Reject(bad.Validate, "Zero workers");
            bad = FlatJob(); bad.Options.GlitchPolicy = "all"; Reject(bad.Validate, "Unsupported glitch simulation");
            bad = FlatJob(); bad.Geometry[0].MinX = float.NegativeInfinity; Reject(bad.Validate, "Bad geometry");
            var simulator = new StaticPlatformSimulation(job); var snapshot = simulator.State.Copy();
            for (int i = 0; i < 8; i++) Check(simulator.Step(new FrameInput { X = 1 }, SearchAbilities.None, out _), "Flat static rollout advances");
            var endpoint = simulator.State.Copy(); simulator.Restore(snapshot);
            for (int i = 0; i < 8; i++) simulator.Step(new FrameInput { X = -1 }, SearchAbilities.None, out _);
            simulator.Restore(snapshot);
            for (int i = 0; i < 8; i++) simulator.Step(new FrameInput { X = 1 }, SearchAbilities.None, out _);
            Check(SearchFiles.Hash(simulator.State) == SearchFiles.Hash(endpoint), "Offline branch restore does not depend on intervening candidates");
            Check(snapshot.Player.Position.X == 0 && snapshot.Player.Grounded, "Snapshots do not alias mutable candidate player state");
            var result = new SectionSearch().Run(job, () => false, _ => { });
            Check(result.Finished && result.BestFrames > 0 && result.Inputs.Length == result.BestFrames, "External beam search finds a static ground route");
            Check(result.Verification == "Unverified" && result.Message.Contains("not exhaustive"), "Offline result never claims native verification or global optimality");
            var chainJob = FlatJob(); chainJob.Options.Mode = "chain"; chainJob.Options.Chain = "R:1-20";
            var chain = InputChain.Parse(chainJob.Options.Chain, chainJob.Options.Abilities, chainJob.Options.Horizon);
            Check(InputChain.Combinations(chain) == 20, "Finite chain has countable bounded choices");
            var chainResult = new ChainSearch().Run(chainJob, () => false, null);
            Check(chainResult.Evaluated == 20 && chainResult.BestFrames > 0 && chainResult.Finished, "Exhaustive chain enumeration tests every configured duration");
            Check(chainResult.Message.Contains("enumeration complete") && chainResult.Verification == "Unverified", "Enumeration completeness does not certify native physics");
            Reject(() => InputChain.Parse("R+Dash:1-2", SearchAbilities.None, 40), "Chains honour disabled actions");
            Reject(() => InputChain.Parse("R:0-2", SearchAbilities.None, 40), "Chains reject zero-length actions");
            Reject(() => InputChain.Parse("R:1-999999", SearchAbilities.None, 40), "Chains enforce finite horizon bounds");
            job.Options.Workers = 1;
            var serial = new SectionSearch().Run(job, () => false, null);
            Check(SearchFiles.Hash(result.Inputs) == SearchFiles.Hash(serial.Inputs), "Fixed search order yields same route with one or two workers");
            simulator.Restore(job.Start);
            foreach (var input in result.Inputs) Check(simulator.Step(input, SearchAbilities.None, out _), "Returned input route replays outside Unity");
            Check(Math.Abs(simulator.State.Player.Position.X - job.Options.GoalX) <= job.Options.GoalRadius, "Returned route reaches requested goal");
            var cancelled = new SectionSearch().Run(job, () => true, null);
            Check(cancelled.Status == "Cancelled" && cancelled.Inputs.Length == 0 && cancelled.Finished, "Cooperative cancel preserves incomplete status");
            var obstacle = FlatJob(); obstacle.Geometry = obstacle.Geometry.Concat(new[] { new SearchBody { Id = "mover", Kind = "unsupported", MinX = -2, MaxX = 2, MinY = .1f, MaxY = 4 } }).ToArray();
            var rejected = new SectionSearch().Run(obstacle, () => false, null);
            Check(rejected.BestFrames == -1 && rejected.Unsupported > 0, "Unknown interactions cannot become pass-through solver shortcuts");
            var polygon = FlatJob(); polygon.Start.Player.Position = new MotionVector(10, 1);
            polygon.Geometry = polygon.Geometry.Concat(new[] { new SearchBody { Id = "triangle", Kind = "hazard", MinX = -50, MaxX = 50, MinY = -50, MaxY = 50,
                Vertices = new[] { new MotionVector(-50, -50), new MotionVector(50, -50), new MotionVector(-50, 50) } } }).ToArray();
            var triangle = new StaticPlatformSimulation(polygon);
            Check(triangle.Step(default, SearchAbilities.None, out _), "Polygon hazard uses its actual outline rather than killing anywhere inside its bounding box");
            var menu = new StaticPlatformSimulation(job);
            Check(!menu.Step(new FrameInput { Held = Buttons.Menu }, SearchAbilities.None, out var menuReason) && menuReason != null, "Unsupported menu input is explicit");
            foreach (int fps in new[] { 30, 50, 60, 100, 120, 144, 165, 244, 280 })
            {
                var rate = FlatJob(fps); var sim = new StaticPlatformSimulation(rate); var timing = new FrameTiming(fps);
                long ticks = 0;
                for (int i = 0; i < fps; i++) { ticks += timing.Advance(1); sim.Step(default, SearchAbilities.None, out _); }
                Check(sim.State.Pus == ticks && Math.Abs(sim.State.Phase - timing.Phase) < 2e-6, "Separate frame and PU clocks at " + fps);
            }
            var jump = FlatJob(); jump.Parameters.JumpForce = 250; jump.Options.Abilities = SearchAbilities.Jump;
            var jumping = new StaticPlatformSimulation(jump);
            jumping.Step(new FrameInput { Held = Buttons.Jump }, SearchAbilities.Jump, out _);
            Check(jumping.State.Player.Position.Y == 1, "Dynamic jump input follows first frame's physics");
            jumping.Step(new FrameInput { Held = Buttons.Jump }, SearchAbilities.Jump, out _);
            Check(jumping.State.Player.Position.Y > 1, "Jump buffered on one frame affects a later PU");
            var noUpgrade = FlatJob(); noUpgrade.Options.Abilities = SearchAbilities.Dash;
            var dash = new StaticPlatformSimulation(noUpgrade);
            dash.Step(new FrameInput { Held = Buttons.Dash }, SearchAbilities.Dash, out _); dash.Step(default, SearchAbilities.Dash, out _);
            Check(!dash.State.Player.Dashing, "Ability selection never grants a missing dash upgrade");
            SearchFiles.Write(Path.Combine(path, "job.json"), job);
            var imported = SearchFiles.Read<SearchJob>(Path.Combine(path, "job.json")); imported.Validate();
            Check(imported.Fps == job.Fps && imported.Geometry.Length == 1, "Worker job roundtrips without Unity references");
            string jobPath=Path.Combine(path,"job.json"), bytes=File.ReadAllText(jobPath);
            Check(bytes==Newtonsoft.Json.JsonConvert.SerializeObject(job,SearchFiles.Settings),"Streaming export preserves exact protocol bytes and hashes.");
            long size=new FileInfo(jobPath).Length;
            Reject(()=>SearchFiles.Read<SearchJob>(jobPath,size-1),"Bounded streaming import rejects an oversized file");
            Check(SearchFiles.Read<SearchJob>(jobPath,size).Fps==job.Fps,"The exact byte limit is accepted.");
            File.AppendAllText(jobPath," {}");
            Reject(()=>SearchFiles.Read<SearchJob>(jobPath),"Streaming import still rejects trailing JSON documents");
            File.WriteAllText(jobPath,bytes);
            var loop=new object[1];loop[0]=loop;
            Reject(()=>SearchFiles.Write(jobPath,loop),"Serialization errors abort atomic streaming export");
            Check(File.ReadAllText(jobPath)==bytes && !File.Exists(jobPath+".tmp"),"Failed streaming export preserves the old file and removes the temporary file.");
            var progress=new SearchResult {BestFrames=2,Inputs=new[]{new FrameInput{X=1}},Path=new[]{new MotionVector(1,2)}};
            string progressPath=Path.Combine(path,"progress.json");SearchFiles.WriteProgress(progressPath,progress);
            var compact=SearchFiles.Read<SearchResult>(progressPath);
            Check(compact.BestFrames==2 && compact.Inputs.Length==0 && compact.Path.Length==0,"Live progress retains metrics without repeatedly serializing the candidate.");
            Check(progress.Inputs.Length==1 && progress.Path.Length==1,"Compact progress never mutates the solver's candidate.");
            progress.Finished=true;SearchFiles.WriteProgress(progressPath,progress);
            Check(SearchFiles.Read<SearchResult>(progressPath).Inputs.Length==1,"Finished progress retains a complete fallback candidate.");
            Check(SearchFiles.TryReadProgress(progressPath,out var live) && live.Finished,"A complete progress report is readable.");
            File.WriteAllText(progressPath,"{unfinished");
            Check(!SearchFiles.TryReadProgress(progressPath,out _),"A temporarily unreadable progress report does not cancel live search.");
            Reject(()=>SearchFiles.Read<SearchResult>(progressPath),"Final result parsing remains strict.");
            File.Delete(progressPath);
            Check(!SearchFiles.TryReadProgress(progressPath,out _),"A progress replacement race retains the previous metrics.");
            var movie = new Movie { BootstrapVersion = 4, Frames = Enumerable.Range(0, 5).Select(i => new MovieFrame { Input = new FrameInput { X = i / 5f } }).ToList() };
            string prefix = SearchFiles.PrefixHash(movie, 2); movie.Frames[4].Input.X = -1;
            Check(prefix == SearchFiles.PrefixHash(movie, 2), "Suffix edits do not change prefix fingerprint");
            movie.Frames[0].Input.X = -.5f;
            Check(prefix != SearchFiles.PrefixHash(movie, 2), "Executed-prefix edits invalidate search snapshot fingerprint");
            Console.WriteLine("PASS: " + count + " external search, simulation boundaries, snapshots, parallelism, and cancellation checks (experimental static model).");
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
}
