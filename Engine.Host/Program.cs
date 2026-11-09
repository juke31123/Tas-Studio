using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using IGTAP.EngineSim;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Search;
using Igtap.Tas.Core.Simulation;
using Igtap.Tas.Core.Validation;

if(args.Length==3 && args[0]=="capture-replay") return PassiveReplay.Run(args[1],Path.GetFullPath(args[2]));
if(args.Length==3 && args[0]=="capture-audit")
{
    try {
        var prepared=SearchFiles.Read<Newtonsoft.Json.Linq.JObject>(Path.Combine(AppContext.BaseDirectory,"engine-host.manifest.json"));
        var report=PassiveCaptureAudit.Read(args[1],Convert.ToBase64String(Convert.FromHexString((string)prepared["GameSha256"])));
        SearchFiles.Write(args[2],report);
        Console.WriteLine($"Capture integrity passed: {report.Frames} frames, {report.PhysicsUpdates} PUs, {report.InputChangesWithoutPhysics} input changes without a PU. This is not a simulator parity result.");
        return 0;
    } catch(Exception error) { Console.Error.WriteLine(error.Message); return 1; }
}
if(args.Length==2 && args[0]=="solve") return EngineSearchRunner.Run(args[1]);
if(args.Length==2 && args[0]=="collision-check") return CollisionChecks.Run(Path.GetFullPath(args[1]));
if(args.Length==1 && args[0]=="self-check") return CompatibilityChecks.Run();
if (args.Length != 2 || (args[0] != "trace" && args[0] != "restore-check" && args[0] != "worker-check" && args[0] != "benchmark"))
{
    Console.Error.WriteLine("Usage: TasStudio.EngineHost trace|restore-check|worker-check|benchmark <captured-test-directory>; capture-audit <gameplay.zip> <report.json>; collision-check <probe-directory>; solve <job.json>");
    return 2;
}
string directory = Path.GetFullPath(args[1]);
bool wroteTrace = false;
try
{
    var native = SimulationTrace.Read(Path.Combine(directory, "native.trace.json")); native.Validate();
    string worldPath = Path.Combine(directory, "world.json");
    if (SimulationTraceComparer.FileHash(worldPath) != native.WorldHash) throw new InvalidDataException("World export hash differs from this trace.");
    var manifest = SearchFiles.Read<Newtonsoft.Json.Linq.JObject>(Path.Combine(AppContext.BaseDirectory, "engine-host.manifest.json"));
    string hash = Convert.ToBase64String(Convert.FromHexString((string)manifest["GameSha256"]));
    if (hash != native.GameHash && !string.Equals((string)manifest["GameSha256"], native.GameHash, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("This local engine was prepared for a different game assembly.");
    string animationPath = Path.Combine(directory, "animation.json");
    if (string.IsNullOrEmpty(native.AnimationHash) || SimulationTraceComparer.FileHash(animationPath) != native.AnimationHash) throw new InvalidDataException("Animation asset definition hash differs from this trace.");
    if (args[0] == "trace" || args[0] == "restore-check") File.Delete(Path.Combine(directory,"engine-error.json"));
    var simulation = EngineFixture.Load(directory, native);
    if (args[0] == "benchmark")
    {
        var benchmark=EngineBenchmark.Run(directory,native);
        SearchFiles.Write(Path.Combine(directory,"benchmark.json"),benchmark);
        Console.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(benchmark));
        return benchmark.Passed ? 0 : 1;
    }
    if (args[0] == "worker-check")
    {
        var workers=WorkerChecks.Run(directory, native);
        SearchFiles.Write(Path.Combine(directory, "worker-comparison.json"), workers);
        Console.WriteLine("Shuffled parallel workers at " + native.Fps + " FPS: " + (workers.Passed ? "passed" : "FAILED"));
        foreach (var trial in workers.Trials) Console.WriteLine("Worker " + trial.Worker + ", " + trial.Branch + ": " + (trial.Error ?? trial.Hash));
        return workers.Passed ? 0 : 1;
    }
    System.Collections.Generic.SortedDictionary<string,string> MoverFields()
    {
        var fields=new System.Collections.Generic.SortedDictionary<string,string>(StringComparer.Ordinal);
        foreach(var mover in simulation.World.All<PlatformMover>()) {
            string key="mover/"+mover.GetInstanceID(); SimulationFieldCapture.AddScript(fields,mover,key);
            SimulationFieldCapture.AddVector(fields,key+"/position",mover.transform.position.x,mover.transform.position.y);
            var body=(UnityEngine.Rigidbody2D)typeof(PlatformMover).GetField("body",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(mover);
            if(body!=null) {
                SimulationFieldCapture.AddVector(fields,key+"/body",body.position.x,body.position.y);
                SimulationFieldCapture.AddVector(fields,key+"/velocity",body.linearVelocity.x,body.linearVelocity.y);
            }
        }
        return fields;
    }
    var checkpoints = new System.Collections.Generic.Dictionary<string, (StudioSnapshot State, int NextFrame, System.Collections.Generic.SortedDictionary<string,string> Movers)>();
    string physicsEndFile=Path.Combine(Directory.GetParent(directory).FullName,"physics-end.txt");
    int physicsEnd=File.Exists(physicsEndFile) ? int.Parse(File.ReadAllText(physicsEndFile)) : 0;
    SimulationTrace Template() => new SimulationTrace { Fps = native.Fps, StartFrame = native.StartFrame, StartPhase = native.StartPhase,
        Previous = native.Previous, GameHash = native.GameHash, WorldHash = native.WorldHash, Backend = "retargeted-frame-host-development-v1",
        Inputs = native.Inputs.ToList(), Coverage = native.Coverage, AdvanceMode = native.AdvanceMode, AnimationHash = native.AnimationHash, CaptureOrbs = native.CaptureOrbs, CaptureMovers = native.CaptureMovers, CaptureSprings = native.CaptureSprings };
    void Checkpoint(int completed)
    {
        void Save(string name) { if (!checkpoints.ContainsKey(name)) checkpoints.Add(name, (simulation.CaptureStudioState(), completed + 1, MoverFields())); }
        if (simulation.StudioPendingInvokes > 0) Save("pending-invoke");
        if (simulation.StudioCoroutines > 0 && UnityEngine.Time.timeScale == 0 && !simulation.Player.pauseMenu.menuOpen) Save("active-hitstop");
        if (simulation.Physics.PlayerTransformDirty) Save("pending-transform-write");
        if (simulation.StudioActiveOrb) Save("active-refill-cooldown");
        if (simulation.Player.cutsceneMode == Movement.cutsceneModes.spring) Save("active-spring-lock");
        if (simulation.World.All<SpringScript>().Any(s => (bool)typeof(SpringScript).GetField("cooldown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public).GetValue(s))) Save("active-spring-cooldown");
        if (simulation.StudioActiveMover) Save("active-mover-wait");
        if (simulation.StudioMovingMover) Save("moving-platform");
        if (simulation.Courses.Any(c => Simulation.CourseTime(c) > .1f)) Save("active-course");
        if (simulation.Player.pauseMenu.menuOpen) Save("open-menu");
    }
    SimulationTrace Run(int first = 0, int until = int.MaxValue, bool write = true, bool diagnostics = true, bool frameEndsOnly = false, Action<int> completed = null)
    {
        var trace = Template();
        int frame = first;
        int transformFrame=int.TryParse(Environment.GetEnvironmentVariable("TAS_STUDIO_DIAG_TRANSFORM_FRAME"),out int selected) ? selected : -1;
        var transformChanging=IGTAP.EngineSim.Engine.TransformChanging;
        if(diagnostics && transformFrame>=0) IGTAP.EngineSim.Engine.TransformChanging=target => {
            if(frame==transformFrame && ReferenceEquals(target,simulation.Player.transform))
                File.AppendAllText(Path.Combine(directory,"transform-writes.jsonl"),Newtonsoft.Json.JsonConvert.SerializeObject(new {frame,Position=new {target.position.x,target.position.y,target.position.z},Stack=Environment.StackTrace})+"\n");
            transformChanging?.Invoke(target);
        };
        string moverLog=Path.Combine(directory,"offline-mover-events.jsonl");
        if(write && diagnostics) File.Delete(moverLog);
        var contacts = new System.Collections.Generic.Queue<object>();
        object movementBody=null;
        simulation.StudioAfterMovement=() => { var body=simulation.Physics.PlayerBody(); movementBody=new {VX=SimulationTraceComparer.Float(body.vx),VY=SimulationTraceComparer.Float(body.vy),body.px,body.py}; };
        simulation.Physics.StudioContact = (a, b, contactPhase) => {
            if (a.GetComponent<startGate>() == null && b.GetComponent<startGate>() == null) return;
            if (a.gameObject != simulation.Player.gameObject && b.gameObject != simulation.Player.gameObject) return;
            var body = simulation.Physics.PlayerBody();
            contacts.Enqueue(new {frame, contactPhase, A=a.GetInstanceID(), B=b.GetInstanceID(), body.px, body.py,
                ScaleX=simulation.Player.transform.lossyScale.x, Details=simulation.Physics.StudioLastContact});
            if (contacts.Count > 16) contacts.Dequeue();
        };
        SimulationSample Sample(int pu, string phase) => EngineFixture.Sample(simulation, frame, pu, phase, native.CaptureOrbs, native.CaptureMovers, native.CaptureSprings);
        if (first == 0) trace.Samples.Add(Sample(0, "start"));
        if (!diagnostics) { simulation.Physics.StudioContact = null; simulation.StudioAfterMovement=null; }
        bool diagnosed = false;
        var recentPhysics = new System.Collections.Generic.Queue<object>();
        simulation.StudioSample = (pu, phase) => {
            if (frameEndsOnly && phase != "frame-end") return;
            if(write && diagnostics && frame<physicsEnd) {
                var fields=MoverFields();
                File.AppendAllText(moverLog,Newtonsoft.Json.JsonConvert.SerializeObject(new {frame,pu,phase,Fields=fields,Pairs=simulation.Physics.StudioMoverPairs()})+"\n");
            }
            var sample = Sample(pu, phase);
            if (diagnostics && !diagnosed && phase == "after-pu") {
                var body=simulation.Physics.PlayerBody(); recentPhysics.Enqueue(new {frame,body.px,body.py, Contacts=simulation.Physics.StudioGateContacts()});
                if (recentPhysics.Count > 4) recentPhysics.Dequeue();
            }
            int index = trace.Samples.Count;
            trace.Samples.Add(sample);
            if (diagnostics && !diagnosed && index < native.Samples.Count && native.Samples[index].Fields.Any(p =>
                !sample.Fields.TryGetValue(p.Key, out string actual) || actual != p.Value))
            {
                diagnosed = true;
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                SearchFiles.Write(Path.Combine(directory, "first-divergence-state.json"), new {
                    frame, phase, Expected = native.Samples[index], Actual = sample, Contacts=contacts.ToArray(),
                    RecentPhysics=recentPhysics.ToArray(),
                    PlayerContacts=simulation.Physics.StudioPlayerContacts(),
                    MoverPairs=simulation.Physics.StudioMoverPairs(),
                    MovementBody=movementBody,
                    Colliders=simulation.World.All<startGate>().Where(g => g.gameObject.activeInHierarchy &&
                        Math.Abs(g.transform.position.x - simulation.Player.transform.position.x) < 300)
                        .Select(g => simulation.Physics.StudioCollider(g.GetComponent<UnityEngine.Collider2D>()))
                        .Concat(simulation.Player.GetComponents<UnityEngine.Collider2D>().Select(simulation.Physics.StudioCollider)).ToArray(),
                    Courses = simulation.Courses.Select(c => new {c.courseNumber, c.enabled, Active=c.gameObject.activeInHierarchy,
                        Tracking=typeof(courseScript).GetField("tracking", flags).GetValue(c), Time=Simulation.CourseTime(c)})
                });
            }
        };
        try
        {
            for (; frame < Math.Min(until, native.Inputs.Count); frame++) {
                simulation.StepStudioFrame(EngineFixture.Input(native.Inputs[frame], frame == 0 ? native.Previous : native.Inputs[frame - 1]));
                completed?.Invoke(frame);
            }
        }
        finally { simulation.StudioSample = null; simulation.Physics.StudioContact = null; simulation.StudioAfterMovement=null;
            IGTAP.EngineSim.Engine.TransformChanging=transformChanging;
            if (write) { SearchFiles.Write(Path.Combine(directory, "offline.trace.json"), trace); wroteTrace = true; } }
        return trace;
    }
    var snapshot = simulation.CaptureStudioState();
    var offline = Run(completed: args[0] == "restore-check" ? Checkpoint : null);
    SearchFiles.Write(Path.Combine(directory, "offline.trace.json"), offline);
    var comparison = SimulationTraceComparer.Compare(native, offline);
    SearchFiles.Write(Path.Combine(directory, "comparison.json"), comparison);
    if (comparison.Passed)
    {
        File.Delete(Path.Combine(directory, "engine-error.json"));
        File.Delete(Path.Combine(directory, "first-divergence-state.json"));
    }
    Console.WriteLine(comparison.Message);
    if (!comparison.Passed) Console.WriteLine("Expected: " + comparison.Expected + "; actual: " + comparison.Actual);
    bool restorePassed = true;
    if (args[0] == "restore-check")
    {
        simulation.RestoreStudioState(snapshot);
        // A different branch must not change a later restored replay (A/B/A).
        for (int i = 0; i < 20; i++) simulation.StepStudioFrame(new StudioInput { X = -1 });
        simulation.RestoreStudioState(snapshot);
        var again = Run();
        var restore = SimulationTraceComparer.Compare(offline, again);
        SearchFiles.Write(Path.Combine(directory, "restore-comparison.json"), restore);
        Console.WriteLine("Restore A/B/A: " + restore.Message); restorePassed = restore.Passed;
        foreach (var checkpoint in checkpoints)
        {
            simulation.RestoreStudioState(checkpoint.Value.State);
            for (int i = 0; i < 20; i++) simulation.StepStudioFrame(new StudioInput {X=-1});
            simulation.RestoreStudioState(checkpoint.Value.State);
            var moverFields=MoverFields();
            bool moverMatch=checkpoint.Value.Movers.Count==moverFields.Count && checkpoint.Value.Movers.All(p => moverFields.TryGetValue(p.Key,out string value) && value==p.Value);
            SearchFiles.Write(Path.Combine(directory,"mover-restore-"+checkpoint.Key+".json"),new {Passed=moverMatch});
            restorePassed &= moverMatch;
            int first=checkpoint.Value.NextFrame, end=Math.Min(native.Inputs.Count, first + native.Fps);
            var branch=Run(first, end, write:false, diagnostics:false);
            var expected=Template(); expected.Samples=offline.Samples.Where(s => s.Frame >= first && s.Frame < end && s.Phase != "start").ToList();
            var check=SimulationTraceComparer.Compare(expected, branch);
            SearchFiles.Write(Path.Combine(directory, "restore-" + checkpoint.Key + ".json"), check);
            Console.WriteLine("Restore " + checkpoint.Key + " at " + first + ": " + check.Message);
            restorePassed &= check.Passed;
        }
        simulation.RestoreStudioState(snapshot);
        var quiet=Run(write:false, diagnostics:false, frameEndsOnly:true);
        var endpoints=Template(); endpoints.Samples=offline.Samples.Where(s => s.Phase == "start" || s.Phase == "frame-end").ToList();
        var observer=SimulationTraceComparer.Compare(endpoints, quiet);
        SearchFiles.Write(Path.Combine(directory, "observer-comparison.json"), observer);
        Console.WriteLine("Without PU observers: " + observer.Message); restorePassed &= observer.Passed;
        SearchFiles.Write(Path.Combine(directory,"restore-summary.json"),new {Passed=restorePassed});
    }
    if (native.Coverage.Length > 0) Console.WriteLine("Coverage gaps: " + native.Coverage.Length + ". Agreement on a trace does not certify the full simulator.");
    return comparison.Passed && restorePassed ? 0 : 1;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.ToString());
    SearchFiles.Write(Path.Combine(directory, "engine-error.json"), new { Error = error.ToString(), Passed = false });
    if (wroteTrace)
    {
        var native = SimulationTrace.Read(Path.Combine(directory, "native.trace.json"));
        var offline = SimulationTrace.Read(Path.Combine(directory, "offline.trace.json"));
        var difference = SimulationTraceComparer.Compare(native, offline);
        SearchFiles.Write(Path.Combine(directory, "comparison.json"), difference); Console.Error.WriteLine(difference.Message);
    }
    return 2;
}
