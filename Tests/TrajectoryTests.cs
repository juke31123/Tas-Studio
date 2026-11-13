using System;
using System.IO;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Simulation;

internal static class TrajectoryTests
{
    public static void Run()
    {
        int count = 0;
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); count++; }
        void Reject(Action action) { try { action(); } catch { count++; return; } throw new Exception("Invalid preview accepted."); }
        var tap = new FrameInput { X = 1, ExactEdges = true, Press = Buttons.Jump, Release = Buttons.Jump };
        var futureMotion=new PlayerMotion {AirJumps=1,AirDashes=1,ControlVelocity=new MotionVector(0,100),WallCoyoteFrames=-1,WallJumpLock=-100,DashCooldown=-1};
        var futureActions=PlayerActionAvailability.Evaluate(futureMotion,new MovementParameters {DoubleJumpUnlocked=true,DashUnlocked=true,MaxWallCoyoteFrames=3,WallJumpLockFrames=10},default);
        var point=new TrajectoryPoint {Motion=futureMotion,Actions=futureActions};
        var encoded=Newtonsoft.Json.JsonConvert.SerializeObject(point);
        var decoded=Newtonsoft.Json.JsonConvert.DeserializeObject<TrajectoryPoint>(encoded);
        Check(decoded.Actions?.UsesAirJump==true && decoded.Actions.CanDash && decoded.Actions.JumpText=="Yes (air)","Future jump type and dash readiness survive the external forecast protocol.");
        Check(Newtonsoft.Json.JsonConvert.DeserializeObject<TrajectoryPoint>("{\"Motion\":{}}").Actions==null,"A forecast without action metadata leaves readiness unknown.");
        Check(new MotionPrediction(null,"unavailable",0,.02f,futureActions).Actions==null,"Unavailable motion cannot retain stale next-frame readiness.");
        var held = TrajectoryRequest.Hold(tap);
        Check(held.X == 1 && held.Held == 0 && !held.ExactEdges && held.Press == 0 && held.Release == 0, "A same-frame tap is not repeated on future frames.");
        var press = new FrameInput { Held = Buttons.Jump | Buttons.Dash, ExactEdges = true, Press = Buttons.Jump | Buttons.Dash };
        var continuation = TrajectoryRequest.Hold(press);
        Check(continuation.Down(Buttons.Jump) && continuation.Down(Buttons.Dash) && !continuation.Pressed(Buttons.Jump, press), "Held actions stay held without generating new press events.");
        Check(tap.ExactEdges && tap.Press == Buttons.Jump, "Preview controls never mutate the selected row.");
        var request = new TrajectoryRequest { Id = 1, Expected = new SimulationSample(), Input = tap };
        request.Validate(); count++;
        request.Frames = 0; Reject(request.Validate); request.Frames = 2001; Reject(request.Validate); request.Frames = 2000; request.Validate(); count++;
        request.Input.X = float.NaN; Reject(request.Validate); request.Input = tap;
        request.Prefix = new FrameInput[20001]; Reject(request.Validate); request.Prefix = Array.Empty<FrameInput>();
        request.Expected = null; Reject(request.Validate);
        var result = new TrajectoryResult { Ready = true, Frame = 10, Input = press, Points = new[] {
            new TrajectoryPoint { Motion = new PlayerMotion { Position = new MotionVector(100, 200) } },
            new TrajectoryPoint { Motion = new PlayerMotion { Position = new MotionVector(101, 200) } } } };
        Check(result.Offset(10, press, new MotionVector(100, 200)) == 0, "A current request can be displayed.");
        Check(result.Offset(11, continuation, new MotionVector(101, 200)) == 1, "A matching advancing run follows the predicted path.");
        Check(result.Offset(11, press, new MotionVector(101, 200)) == -1, "A fresh explicit retap invalidates an older held-input forecast.");
        Check(result.Offset(9, press, new MotionVector(100, 200)) == -1, "Rewinding hides a stale forecast.");
        Check(result.Offset(12, continuation, new MotionVector(101, 200)) == -1, "A forecast is not extended beyond its simulated horizon.");
        Check(result.Offset(10, continuation, new MotionVector(100, 200)) == -1, "A changed explicit first-frame edge invalidates a forecast.");
        Check(result.Offset(11, default, new MotionVector(101, 200)) == -1, "Releasing the controls immediately invalidates a held-input path.");
        Check(result.Offset(11, continuation, new MotionVector(102, 200)) == -1, "A diverging player position hides the old path.");
        Check(result.Offset(11, continuation, new MotionVector(101, 200), new MotionVector(10, 0)) == -1, "A changed velocity also hides the old path while paused.");
        result.Ready = false; Check(result.Offset(10, press, new MotionVector(100, 200)) == -1, "Failed predictions have no visible trajectory.");
        var timers = new SimulationTimers(); object owner = new object();
        timers.ObserveNative(owner, "cancelJumpBuffer", .123, 10, true);
        var snapshot = timers.NativeDeadlines(10.023);
        Check(snapshot.Length == 1 && Math.Abs(snapshot[0].Remaining - .1) < 1e-12 && snapshot[0].FixedOrigin, "Snapshot Invoke deadlines retain sub-PU precision and their originating phase.");
        timers.NativeCalled(owner, "cancelJumpBuffer");
        Check(snapshot.Length == 1 && timers.NativeDeadlines(11).Length == 0, "Detached timer snapshots survive changes to the live queue.");
        string path = Path.Combine(Path.GetTempPath(), "trajectory-mailbox-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Check(TrajectoryMailbox.TryWrite(path, new TrajectoryResult { Id = 1 }), "A preview result is written atomically.");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Check(!TrajectoryMailbox.TryRead<TrajectoryResult>(path, out _), "A transient reader lock asks for a retry.");
                Check(!TrajectoryMailbox.TryWrite(path, new TrajectoryResult { Id = 2 }), "A transient writer lock asks for a retry without losing the old file.");
            }
            Check(TrajectoryMailbox.TryRead<TrajectoryResult>(path, out var old) && old.Id == 1, "A locked replacement preserves the previous complete result.");
            Check(TrajectoryMailbox.TryWrite(path, new TrajectoryResult { Id = 2 }) && TrajectoryMailbox.TryRead<TrajectoryResult>(path, out var next) && next.Id == 2,
                "Mailbox traffic resumes after a file lock is released.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
        var journal = new TrajectoryJournal();
        for (int i=0;i<600;i++) journal.Append(new FrameInput { X=i%2, ExactEdges=true, Press=Buttons.Jump });
        var published=journal.Publish(); journal.Truncate(257); journal.Append(default);
        Check(published.Count==600 && published.ToArray()[599].X==1 && journal.Count==258,"Published history remains immutable across rewind and append.");
        var branched=journal.Publish(); journal.Truncate(0); journal.Append(press);
        Check(branched.ToArray()[257].Held==0 && branched.ToArray()[256].Press==Buttons.Jump && published.ToArray()[257].Press==Buttons.Jump,"Chunk boundaries and partial tails retain their original input edges.");
        Check(journal.Publish().ToArray().Length==1 && journal.Publish().ToArray()[0].Held==press.Held,"Rewind to the initial anchor starts a separate input branch.");
        Reject(()=>journal.Truncate(2));
        string binary=path+".bin";
        try
        {
            var inputRequest=new TrajectoryRequest {Id=3,Frame=600,Input=press,Prefix=published.ToArray(),Expected=new SimulationSample()};
            inputRequest.Expected.Fields.Add("movement/jumpBuffer","True");
            Check(TrajectoryWire.WriteRequest(binary,inputRequest) && TrajectoryWire.ReadRequest(binary,out var restoredRequest) &&
                restoredRequest.Frame==600 && restoredRequest.Prefix.Length==600 && TrajectoryRequest.Same(restoredRequest.Prefix[599],inputRequest.Prefix[599]) &&
                restoredRequest.Expected.Fields["movement/jumpBuffer"]=="True","Binary history preserves explicit repeated taps, frame count and complete witnesses.");
            inputRequest.Prefix=Array.Empty<FrameInput>();
            Check(TrajectoryWire.WriteRequest(binary,inputRequest,published) && TrajectoryWire.ReadRequest(binary,out var streamed) && streamed.Prefix.Length==600 &&
                TrajectoryRequest.Same(streamed.Prefix[599],published.ToArray()[599]),"Immutable chunks stream into binary IPC without flattening the whole history in the game process.");
            inputRequest.Prefix=new FrameInput[20000];
            for(int i=0;i<inputRequest.Prefix.Length;i++)inputRequest.Prefix[i]=tap;
            inputRequest.Frame=20000;inputRequest.NextFramePrepared=true;
            Check(TrajectoryWire.WriteRequest(binary,inputRequest) && new FileInfo(binary).Length<200 && TrajectoryWire.ReadRequest(binary,out var repeated) &&
                repeated.NextFramePrepared && repeated.Prefix.Length==20000 && TrajectoryRequest.Same(repeated.Prefix[19999],tap),"Long constant input spans compress without dropping per-frame explicit taps or the pacing boundary.");
            var allMotion=new PlayerMotion();
            foreach(var field in typeof(PlayerMotion).GetFields())
                field.SetValue(allMotion,field.FieldType==typeof(bool)?(object)true:field.FieldType==typeof(int)?42:field.FieldType==typeof(float)?1.25f:
                    field.FieldType==typeof(double)?double.PositiveInfinity:new MotionVector(-123.25f,987.5f));
            var wireResult=new TrajectoryResult {Id=4,Frame=7,Fps=280,Ready=true,SimulationMilliseconds=1.5,Input=press,
                Points=new[]{new TrajectoryPoint {Motion=allMotion,Actions=futureActions,Pus=17},new TrajectoryPoint {Motion=futureMotion,Pus=18}}};
            Check(TrajectoryWire.WriteResult(binary,wireResult) && TrajectoryWire.ReadResult(binary,out var restoredResult) &&
                Newtonsoft.Json.JsonConvert.SerializeObject(restoredResult)==Newtonsoft.Json.JsonConvert.SerializeObject(wireResult),"All motion fields, timer infinities, action readiness and absent metadata round-trip exactly.");
            Check(new FileInfo(binary).Length < System.Text.Encoding.UTF8.GetByteCount(Newtonsoft.Json.JsonConvert.SerializeObject(wireResult))/2,"Binary forecast avoids repeated JSON property/computed-state overhead.");
            using(var locked=new FileStream(binary,FileMode.Open,FileAccess.Read,FileShare.None))
            {
                Check(!TrajectoryWire.ReadResult(binary,out _),"Binary reader retries a transient lock.");
                Check(!TrajectoryWire.WriteResult(binary,wireResult),"Binary writer preserves the previous message during a transient lock.");
            }
            Check(TrajectoryWire.ReadResult(binary,out var retained) && retained.Id==4,"The complete forecast survives a rejected replacement.");
            Check(!TrajectoryWire.ReadRequest(binary,out _),"The result message cannot be mistaken for an input request.");
            var bytes=File.ReadAllBytes(binary);File.WriteAllBytes(binary,new byte[]{1,2,3});
            Check(!TrajectoryWire.ReadResult(binary,out _),"Truncated messages are rejected.");
            File.WriteAllBytes(binary,bytes);using(var extra=new FileStream(binary,FileMode.Append))extra.WriteByte(1);
            Check(!TrajectoryWire.ReadResult(binary,out _),"Trailing binary garbage is rejected.");
        }
        finally {if(File.Exists(binary))File.Delete(binary);}
        Console.WriteLine("Trajectory protocol and snapshot checks passed: " + count);
    }
}
