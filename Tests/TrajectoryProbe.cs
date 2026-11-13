using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Search;
using Igtap.Tas.Core.Simulation;

internal static class TrajectoryProcessProbe
{
    private static int Main(string[] args)
    {
        if (args.Length != 3 && (args.Length != 4 || args[3] != "origin-only")) return 2;
        bool originOnly=args.Length==4;
        try
        {
            string host = Path.GetFullPath(args[0]), source = Path.GetFullPath(args[1]), directory = Path.GetFullPath(args[2]);
            Directory.CreateDirectory(directory);
            foreach (string file in new[] { "world.json", "start.json", "animation.json" }) File.Copy(Path.Combine(source, file), Path.Combine(directory, file));
            var native = SimulationTrace.Read(Path.Combine(source, "native.trace.json"));
            var root = SimulationTrace.Read(Path.Combine(source, "native.trace.json"));
            root.AdvanceMode = "before-pacing"; root.Inputs.Clear(); root.PreparedNextFrames.Clear(); root.Samples = root.Samples.Take(1).ToList();
            SearchFiles.Write(Path.Combine(directory, "native.trace.json"), root);
            using var process = Process.Start(new ProcessStartInfo { FileName = host,
                Arguments = "trajectory \"" + directory + "\" " + Environment.ProcessId, UseShellExecute = false,
                CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true });
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            long id = 0; int cases = 0, compared = 0, actionFrames=0;
            double maxSimulation=0,maxRoundTrip=0;long resultBytes=0;
            void Wait(Func<bool> condition)
            {
                var clock = Stopwatch.StartNew();
                while (!condition())
                {
                    if (process.HasExited) throw new Exception("Trajectory host exited: " + errors.GetAwaiter().GetResult());
                    if (File.Exists(Path.Combine(directory, "failure.json"))) throw new Exception(File.ReadAllText(Path.Combine(directory, "failure.json")));
                    if (clock.Elapsed.TotalSeconds > 60) throw new Exception("Trajectory protocol timed out.");
                    Thread.Sleep(10);
                }
            }
            try
            {
                Wait(() => File.Exists(Path.Combine(directory, "ready.json")));
                SimulationSample Sample(int frame) => frame == 0 ? native.Samples[0] : native.Samples.Last(s => s.Frame == frame - 1 && s.Phase == "frame-end");
                TrajectoryResult Request(int frame, int length, FrameInput row, bool corrupt = false)
                {
                    var expected = new SimulationSample { Fields = new SortedDictionary<string, string>(Sample(frame).Fields) };
                    if (corrupt) expected.Fields["body/position/x"] = SimulationTraceComparer.Float(-99999);
                    var request = new TrajectoryRequest { Id = ++id, Frame = native.StartFrame+frame, Frames = length,
                        Input = row, Prefix = native.Inputs.Take(frame).ToArray(), Expected = expected,
                        NextFramePrepared=frame>0 && (native.PreparedNextFrames.Count==0 ? native.AdvanceMode=="continuous" : native.PreparedNextFrames[frame-1]) };
                    var elapsed=Stopwatch.StartNew();
                    Wait(() => TrajectoryWire.WriteRequest(Path.Combine(directory, "request.bin"), request));
                    TrajectoryResult result = null;
                    Wait(() => {
                        string file = Path.Combine(directory, "result.bin");
                        if (!File.Exists(file)) return false;
                        return TrajectoryWire.ReadResult(file, out result) && result.Id == id;
                    });
                    maxSimulation=Math.Max(maxSimulation,result.SimulationMilliseconds);maxRoundTrip=Math.Max(maxRoundTrip,elapsed.Elapsed.TotalMilliseconds);
                    resultBytes=Math.Max(resultBytes,new FileInfo(Path.Combine(directory,"result.bin")).Length);
                    return result;
                }
                float Number(SimulationSample sample, string key) => BitConverter.Int32BitsToSingle(Convert.ToInt32(sample.Fields[key], 16));
                PlayerActionAvailability NativeActions(SimulationSample sample)
                {
                    string Key(string key)=>"movement/"+key;
                    bool Flag(string key)=>bool.Parse(sample.Fields[Key(key)]);
                    float Value(string key)=>Number(sample,Key(key));
                    int Count(string key)=>int.Parse(sample.Fields[Key(key)]);
                    int mode=sample.Fields[Key("cutsceneMode")] switch {"none"=>0,"dash"=>1,"deathFreeze"=>2,"longfall"=>3,"spring"=>4,_=>throw new Exception("Unknown native movement mode.")};
                    // These fixtures contain an active physics body and no menu
                    // inputs. Everything that changes jump/dash eligibility is
                    // read from that future native frame, not the live anchor.
                    return PlayerActionAvailability.Evaluate(new PlayerMotion {
                        Grounded=Flag("onGround"),OnWall=Flag("OnWall"),Dead=Flag("isDead"),Cutscene=mode,
                        ControlVelocity=new MotionVector(Value("Velocity/x"),Value("Velocity/y")),NormalJump=Flag("MostRecentUpwardsMoveWasNormalJump"),
                        WallCoyoteFrames=Value("wallCoyoteFrames"),WallJumpLock=Value("wallJumpMovementLockRemaining"),
                        AirJumps=Count("airJumpsLeft"),AirDashes=Count("airDashesLeft"),DashCooldown=Value("dashCooldown"),
                        DashBuffered=Flag("dashBuffer"),DashBounceFrames=Count("DashBounceFrames"),FramesSinceDash=Value("framesSinceLastDash")
                    },new MovementParameters {DoubleJumpUnlocked=Flag("doubleJumpUnlocked"),DashUnlocked=Flag("dashUnlocked"),
                        MaxWallCoyoteFrames=Value("maxWallCoyoteFrames"),WallJumpLockFrames=Value("wallJumpMovementLockFrames")},
                        new PlayerActionContext {GamePaused=Flag("GamePaused"),WallJumpUnlocked=Flag("wallJumpUnlocked"),WallJumps=Count("wallJumpsLeft"),CanHyper=Flag("canHyper")});
                }
                var starts = new HashSet<int> { 0, Math.Min(1, native.Inputs.Count - 1), native.Inputs.Count / 2 };
                for (int i = 1; i < native.Inputs.Count; i++)
                    if (native.Inputs[i].Held != native.Inputs[i - 1].Held || native.Inputs[i].X != native.Inputs[i - 1].X || native.Inputs[i].Y != native.Inputs[i - 1].Y)
                    { starts.Add(i); if (i + 1 < native.Inputs.Count) starts.Add(i + 1); }
                foreach (int frame in (originOnly ? new[]{0} : starts.OrderBy(i => i).Take(40)))
                {
                    var input = native.Inputs[frame]; int span = 1;
                    while (span < 32 && frame + span < native.Inputs.Count)
                    {
                        var row = native.Inputs[frame + span];
                        if (row.X != input.X || row.Y != input.Y || row.Held != input.Held || row.ExactEdges && (row.Press | row.Release) != 0) break;
                        span++;
                    }
                    for(int i=0;i<span;i++)if(Sample(frame+i+1).Fields["movement/isDead"]=="True"){span=i+1;break;}
                    var result = Request(frame, span, input);
                    if (!result.Ready || result.Points.Length != span + 1) throw new Exception("Incomplete forecast at " + frame + ": " + result.Message);
                    if(Sample(frame+span).Fields["movement/isDead"]=="True" && (result.End!="death" || !result.Points[span].Motion.Dead))throw new Exception("The native fatal frame is missing from the death endpoint.");
                    for (int i = 0; i < span; i++)
                    {
                        var sample = Sample(frame + i + 1); var motion = result.Points[i + 1].Motion;
                        float x = Number(sample, "body/position/x") - Number(sample, "origin/x"), y = Number(sample, "body/position/y") - Number(sample, "origin/y");
                        if (Math.Abs(x - motion.Position.X) > .0001 || Math.Abs(y - motion.Position.Y) > .0001 ||
                            Math.Abs(Number(sample, "body/velocity/x") - motion.Velocity.X) > .0001 || Math.Abs(Number(sample, "body/velocity/y") - motion.Velocity.Y) > .0001)
                            throw new Exception("Native forecast differs at " + (frame + i));
                        var expectedActions=NativeActions(sample);var actions=result.Points[i+1].Actions;
                        if(actions==null || actions.Jump!=expectedActions.Jump || actions.CanDash!=expectedActions.CanDash ||
                            actions.JumpText!=expectedActions.JumpText || actions.DashText!=expectedActions.DashText)
                            throw new Exception("Native next-frame readiness differs at "+(frame+i));
                        actionFrames++;
                        compared++;
                    }
                    Request(frame, span, new FrameInput { X = -input.X, Held = Buttons.Jump });
                    var restored = Request(frame, span, input);
                    if (SearchFiles.Hash(restored.Points) != SearchFiles.Hash(result.Points)) throw new Exception("A hypothetical route leaked state into the next forecast.");
                    cases++;
                }
                if (Request(0, 1, native.Inputs[0], true).Ready) throw new Exception("A wrong live-state witness was accepted.");
                if (!Request(0, 1, native.Inputs[0]).Ready) throw new Exception("A rejected request corrupted the simulator.");
                var obsolete=new TrajectoryRequest {Id=++id,Frame=native.StartFrame,Frames=2000,Input=new FrameInput{X=-1,Held=Buttons.Jump},Expected=Sample(0)};
                Wait(()=>TrajectoryWire.WriteRequest(Path.Combine(directory,"request.bin"),obsolete));
                if(!Request(0,1,native.Inputs[0]).Ready)throw new Exception("A superseded long forecast blocked the latest controls or leaked its branch.");
                SearchFiles.Write(Path.Combine(directory, "validation.json"), new { Passed = true, native.Fps, Forecasts = cases, ComparedFrames = compared,
                    PrefixRewinds = !originOnly, BranchRollback = true, WrongStateRejected = true,ActionFrames=actionFrames,
                    MaxSimulationMilliseconds=maxSimulation,MaxRoundTripMilliseconds=maxRoundTrip,MaxResultBytes=resultBytes });
                Console.WriteLine("PASS: " + cases + " forecasts / " + compared + " native frames at " + native.Fps + " FPS; " + (originOnly ? "origin only, " : "rewinds, ") + "rollback and stale-state rejection.");
            }
            finally
            {
                File.WriteAllText(Path.Combine(directory, "stop"), "stop");
                if (!process.WaitForExit(3000)) process.Kill();
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
