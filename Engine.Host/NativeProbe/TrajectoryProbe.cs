using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Search;
using Igtap.Tas.Core.Simulation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

internal static class TrajectoryProbe
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static ITrajectoryPreview _preview;
    private static TrajectoryResult _initial;
    private static string _before, _random;
    private static int _frames;
    private static int _start;
    private static bool _rendered;
    private static int _stability;
    private static bool _checkStability;
    private static object _failedRun;
    private static double _failureAt;
    private static long _captures;
    private static bool _recoveryPassed;
    private static object _profiler;
    private static int _liveFrames,_liveVisible;
    private static long _liveRequests;
    private static double _maxSimulation;
    private static bool Failed()
    {
        var run=_preview.GetType().GetField("_run",Flags).GetValue(_preview);
        if(run==null)return false;
        return (bool)run.GetType().GetField("Failed",Flags).GetValue(run) ||
            ((TrajectoryResult)run.GetType().GetField("Result",Flags).GetValue(run))?.End=="unavailable";
    }
    internal static bool ShouldStart(string root, int frame) => File.Exists(Path.Combine(root, "trajectory-smoke.txt")) && frame == int.Parse(File.ReadAllText(Path.Combine(root, "trajectory-smoke.txt")));
    internal static bool Begin(object session, string root, int fps, int frame = 0)
    {
        _initial = null; _rendered = false; _stability = 0; _recoveryPassed = false;
        _liveFrames=_liveVisible=0;_liveRequests=0;_maxSimulation=0;
        if (!ShouldStart(root, frame))
        {
            if (frame == 0 && File.Exists(Path.Combine(root,"trajectory-smoke.txt")))
                session.GetType().Assembly.GetType("Igtap.Tas.Runtime.SimulationCapture").GetField("CollectHistory").SetValue(null,false);
            return false;
        }
        _preview = (ITrajectoryPreview)session.GetType().GetProperty("Trajectory", Flags).GetValue(session);
        _profiler = session.GetType().GetProperty("Profiler",Flags).GetValue(session);
        _checkStability = File.Exists(Path.Combine(root,"trajectory-stability.txt"));
        if (_preview == null) throw new Exception("Trajectory controller was not initialized.");
        _start = frame;
        int until = frame < fps / 10 ? fps / 10 : frame < fps / 5 ? fps / 5 : frame < fps * 2 / 5 ? fps * 2 / 5 : frame == fps * 2 / 5 ? frame + 1 : frame + 16;
        _frames = Math.Min(16, Math.Max(1, until - frame));
        _before = State(session); _random = JsonUtility.ToJson(UnityEngine.Random.state);
        _preview.Configure(true, _frames); return true;
    }
    internal static bool Ready(object session, string directory)
    {
        object Run() => _preview.GetType().GetField("_run",Flags).GetValue(_preview);
        long Captures() => Newtonsoft.Json.Linq.JObject.FromObject(session.GetType().GetProperty("Profiler",Flags).GetValue(session)
            .GetType().GetMethod("Report").Invoke(session.GetType().GetProperty("Profiler",Flags).GetValue(session),null))
            ["OperationsIncludingPausedEditor"]["TrajectoryCapture"]["Calls"].Value<long>();
        if (_stability == 1)
        {
            if (Time.realtimeSinceStartupAsDouble - _failureAt < 2.5) return false;
            if (!_preview.Enabled || !ReferenceEquals(Run(),_failedRun) || Captures()!=_captures)
                throw new Exception("A failed forecast changed the enabled setting or repeatedly rebuilt its snapshot.");
            _preview.Refresh(); _stability = 2; return false;
        }
        var result = (TrajectoryResult)_preview.GetType().GetProperty("Visible", Flags).GetValue(_preview);
        if (result == null)
        {
            if (Failed()) throw new Exception(_preview.Status);
            return false;
        }
        if (!result.Ready || result.Frame != _start || result.Points.Length != _frames + 1 && result.End!="death") throw new Exception("Incomplete native trajectory forecast: " + result.Message);
        if(result.End=="death")_frames=result.Points.Length-1;
        if (_checkStability && _stability == 0)
        {
            _failedRun = Run(); _captures = Captures(); _failureAt = Time.realtimeSinceStartupAsDouble;
            _failedRun.GetType().GetField("Result",Flags).SetValue(_failedRun,new TrajectoryResult{Id=result.Id,Frame=result.Frame,End="unavailable",Message="Controlled preview witness failure"});
            _stability = 1; return false;
        }
        if (_stability == 2)
        {
            if (!_preview.Enabled || ReferenceEquals(Run(),_failedRun) || Captures()!=_captures+1)
                throw new Exception("Explicit snapshot refresh did not recover exactly once while remaining enabled.");
            _recoveryPassed = true; _stability = 3;
        }
        // The renderer removes duplicate vertices; an idle held-input path
        // legitimately consists of a single point.
        _rendered = Resources.FindObjectsOfTypeAll<LineRenderer>().Any(l => l.name == "Trajectory" && l.positionCount > 0);
        if (!_rendered) return false;
        string after = State(session), randomAfter = JsonUtility.ToJson(UnityEngine.Random.state);
        if (after != _before || randomAfter != _random)
        {
            SearchFiles.Write(Path.Combine(directory,"trajectory-side-effect.json"),new{Before=_before,After=after,RandomBefore=_random,RandomAfter=randomAfter});
            throw new Exception("The preview changed the live player/movie/random state.");
        }
        _initial = result;
        SearchFiles.Write(Path.Combine(directory, "trajectory-preview.json"), result);
        if(File.Exists(Path.Combine(Path.GetDirectoryName(directory),"trajectory-live.txt")))_preview.Configure(true,280);
        return true;
    }
    internal static void ObserveLive(object session)
    {
        _liveFrames++;
        if(!_preview.Enabled || Failed())throw new Exception("Live preview failed: "+_preview.Status);
        var controller=_preview.GetType();
        _liveRequests=(long)controller.GetField("_serial",Flags).GetValue(_preview);
        var result=(TrajectoryResult)controller.GetProperty("Visible",Flags).GetValue(_preview);
        if(result!=null){_liveVisible++;_maxSimulation=Math.Max(_maxSimulation,result.SimulationMilliseconds);}
    }
    internal static void SaveFailure(string directory)
    {
        if(_preview==null)return;
        var run=_preview.GetType().GetField("_run",Flags).GetValue(_preview);
        if(run==null)return;
        string source=(string)run.GetType().GetField("Directory",Flags).GetValue(run);
        string destination=Path.Combine(directory,"trajectory-failure");Directory.CreateDirectory(destination);
        foreach(string file in new[]{"world.json","start.json","animation.json","native.trace.json","request.bin","result.bin","failure.json"})
            if(File.Exists(Path.Combine(source,file)))File.Copy(Path.Combine(source,file),Path.Combine(destination,file),true);
    }
    internal static void CheckLive(object session,string directory)
    {
        if(_liveFrames<16 || _liveVisible<10 || _liveRequests<3)throw new Exception("Live trajectory test did not cover enough advancing forecasts.");
        SearchFiles.Write(Path.Combine(directory,"trajectory-live-validation.json"),new {Passed=true,Frames=_liveFrames,VisibleFrames=_liveVisible,Requests=_liveRequests,MaxSimulationMilliseconds=_maxSimulation,
            NoWitnessFailure=true,EnabledThroughout=true});
    }
    internal static void Check(SimulationTrace trace, string directory)
    {
        if (_initial == null) return;
        float Number(SimulationSample sample, string key) => BitConverter.ToSingle(BitConverter.GetBytes(Convert.ToInt32(sample.Fields[key], 16)), 0);
        // Mono can retain a wider arithmetic intermediate in a float local.
        // Materialize world-coordinate subtraction to the float32 wire format
        // before comparing large custom-map coordinates with deserialized data.
        float Rounded(float value) => BitConverter.ToSingle(BitConverter.GetBytes(value), 0);
        for (int frame = 0; frame < _frames; frame++)
        {
            var sample = trace.Samples.Last(s => s.Frame == _start - trace.StartFrame + frame && s.Phase == "frame-end");
            var predicted = _initial.Points[frame + 1].Motion;
            float x = Rounded(Number(sample, "body/position/x") - Number(sample, "origin/x")), y = Rounded(Number(sample, "body/position/y") - Number(sample, "origin/y"));
            if (Math.Abs(x - predicted.Position.X) > .0001 || Math.Abs(y - predicted.Position.Y) > .0001 ||
                Math.Abs(Number(sample, "body/velocity/x") - predicted.Velocity.X) > .0001 || Math.Abs(Number(sample, "body/velocity/y") - predicted.Velocity.Y) > .0001)
                throw new Exception("Trajectory differs from native movement on frame " + frame);
        }
        SearchFiles.Write(Path.Combine(directory, "trajectory-validation.json"), new { Passed = true, StartFrame = _start, Frames = _frames, trace.Fps,
            LivePhysicsAndMovieUnchanged = true, RandomUnchanged = true, HeldInputForecastMatchesNative = true,
            RenderPathPresent = _rendered, FailureDoesNotRebuildOrDisable = _recoveryPassed, ExplicitRefreshRecovered = _recoveryPassed });
        SearchFiles.Write(Path.Combine(directory,"trajectory-performance.json"),_profiler.GetType().GetMethod("Report").Invoke(_profiler,null));
        _preview.Configure(false, _frames);
    }
    private static string State(object session)
    {
        object adapter = session.GetType().GetProperty("Adapter", Flags).GetValue(session);
        var probe = (StateProbe)adapter.GetType().GetMethod("Probe", Flags).Invoke(adapter, null);
        // Unity may settle its interpolated sprite after stopping, even while
        // the physics body and all game fields remain frozen. Compare physics.
        probe.RenderX = probe.RenderY = 0;
        return JsonConvert.SerializeObject(probe) + "/" +
            session.GetType().GetProperty("Cursor", Flags).GetValue(session) + "/" +
            ((Movie)session.GetType().GetProperty("Movie", Flags).GetValue(session)).Frames.Count;
    }
}
