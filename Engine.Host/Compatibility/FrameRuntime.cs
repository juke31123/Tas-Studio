using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
using Igtap.Tas.Core.Simulation;

namespace IGTAP.EngineSim
{
    public struct StudioInput
    {
        public float X, Y;
        public int Held, Press, Release;
    }
    public sealed class StudioObservedFrame
    {
        public StudioInput[] FixedInputs = Array.Empty<StudioInput>();
        public double[] FixedTimes = Array.Empty<double>();
        public float[] FixedUnscaledDeltas = Array.Empty<float>();
        public double Time, LastFixedTime;
        public float Delta, UnscaledDelta, FixedDelta;
    }
    public sealed class StudioSnapshot
    {
        internal object Owner;
        internal SimSnapshot World;
        internal StudioScheduler.State Scheduled;
        internal StudioInput Input;
        internal double Phase, Time;
        internal float Scale;
        internal bool EndPacing;
        internal int Frame, Pus;
        internal NativeFrameClock NativeClock;
        internal AnimationTimelineState Animation;
        internal float AnimationSpeed;
        internal object[][][] CoursePaths;
        internal bool Interpolate;
        internal int Checkpoint;
        internal MoverFrameState[] Movers;
        internal double VmanTime,VmanScore;
    }
    public sealed partial class Simulation
    {
        private StudioScheduler _studioScheduler;
        private StudioInput _studioInput;
        private int _studioFps, _studioFrame, _studioPus;
        private double _studioPhase, _studioTime;
        private float _studioScale = 1;
        private bool _studioContinuous;
        private NativeFrameClock _studioNativeClock;
        private Action _studioMenuUpdate;
        private bool _studioInterpolate;
        private Rigidbody2D _studioRenderBody;
        public double StudioNativeTime => _studioNativeClock.Time;
        public double StudioNativeFixedTime => _studioNativeClock.FixedTime;
        public long StudioCaptureTicks => _studioNativeClock.CaptureTicks;
        public void ConfigureStudioEndPacing(bool prepared) => _studioContinuous=prepared;
        public long StudioPreparedCaptureTicks()
        {
            // Continuous native End prepares the next capture step; a paused
            // single-frame End retains the consumed one. Compare the requested
            // boundary without changing the simulation's clocks or state.
            var clock=_studioNativeClock;
            double total=_studioPhase + (1.0/_studioFps)*_studioScale;
            int pus=(int)Math.Floor(total/.02 + 1e-10);
            clock.Prepare(pus,Math.Max(0,total-pus*.02),_studioScale,_studioFps);
            return clock.CaptureTicks;
        }
        public int StudioFrame => _studioFrame;
        public int StudioPus => _studioPus;
        public void ConfigureStudioObservedScale(float scale) { _studioScale=scale; Time.timeScale=scale; }
        public void ConfigureStudioObservedRender(float x,float y,float z) => Player.transform.StoreWorldPosition(new Vector3(x,y,z));
        public int StudioPendingInvokes => _studioScheduler?.PendingCount ?? 0;
        public int StudioCoroutines => _studioScheduler?.RoutineCount ?? 0;
        public void ConfigureStudioPreviewTimers(System.Text.Json.JsonElement start)
        {
            if (!start.TryGetProperty("previewInvokes", out var pending)) return;
            foreach (var timer in pending.EnumerateArray())
                _studioScheduler.ImportInvoke(Player, timer.GetProperty("method").GetString(), timer.GetProperty("seconds").GetDouble(), timer.GetProperty("fixed").GetBoolean());
        }
        public double StudioPhase => _studioPhase;
        public Action<int, string> StudioSample;
        public Action StudioAfterMovement;
        public void ConfigureStudioInterpolation(bool valid)
        { _studioInterpolate=valid; for(int i=0; i<_studioMovers.Length; i++) _studioMovers[i].Interpolate=valid; }

        public void ConfigureStudioFrames(int fps, double phase, StudioInput previous, double nativeTime, double nativeFixedTime, long captureTicks, bool continuous)
        {
            if (Array.IndexOf(new[] {30,50,60,100,120,144,165,244,280}, fps) < 0 || phase < 0 || phase >= .02 || double.IsNaN(phase) ||
                double.IsNaN(nativeTime) || double.IsInfinity(nativeTime) || double.IsNaN(nativeFixedTime) || double.IsInfinity(nativeFixedTime))
                throw new ArgumentException("Invalid frame clock.");
            _studioFps = fps; _studioPhase = phase; _studioTime = 0; _studioFrame = _studioPus = 0;
            _studioContinuous = continuous;
            Physics.StudioFrames = true;
            _studioScale = 1; _inRun = true;
            _studioNativeClock = new NativeFrameClock(nativeTime, nativeFixedTime, captureTicks);
            _stepFrom = AtLoadedStart ? _loadedStepFrom : BodyPosition();
            _stepTo = AtLoadedStart ? _loadedStepTo : BodyPosition();
            _studioInterpolate = true;
            _studioRenderBody=Player.GetComponent<Rigidbody2D>();
            StudioFrameControls.Reset();
            StudioFrameControls.Now = (float)_studioNativeClock.Time; Time.timeScale = _studioScale;
            foreach (var pair in new[] { ("jumpAction", 1), ("jumpAction2", 2), ("dashAction", 4), ("dashAction2", 8), ("resetAction", 16) })
            {
                var field = typeof(Movement).GetField(pair.Item1, BindingFlags.Instance | BindingFlags.NonPublic);
                var action = (InputAction)field.GetValue(Player) ?? new InputAction(); field.SetValue(Player, action);
                StudioFrameControls.Bind(action, pair.Item2);
            }
            var move = typeof(Movement).GetField("moveAction", BindingFlags.Instance | BindingFlags.NonPublic);
            if (move.GetValue(Player) == null) move.SetValue(Player, new InputAction());
            if (Player.pauseMenu == null) throw new InvalidOperationException("The native pause menu was not exported.");
            var pauseAction = typeof(pauseMenuScript).GetField("pauseAction", BindingFlags.Instance | BindingFlags.NonPublic);
            var pause = (InputAction)pauseAction.GetValue(Player.pauseMenu) ?? new InputAction();
            pauseAction.SetValue(Player.pauseMenu, pause); StudioFrameControls.Bind(pause, 32);
            _studioMenuUpdate = (Action)Delegate.CreateDelegate(typeof(Action), Player.pauseMenu,
                typeof(pauseMenuScript).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic));
            // No macro/tick injection: the original Movement code reads its real
            // per-frame logical action states in both Update and FixedUpdate.
            Hooks.PlayerInput = _ => { };
            Hooks.WallTouch = (_, __) => { };
            Hooks.TimeStop = null; // Execute the original coroutine, including its first fixed-phase yield.
            Hooks.Death = _ => { };
            // The reference mod's category policy must not override the TAS's
            // captured checkpoint-respawn setting on a native reset action.
            Hooks.Respawn = _ => { };
            IGTAP.EngineSim.Engine.Stick = () => new Vector2(_studioInput.X, _studioInput.Y);
            _studioScheduler = new StudioScheduler(Touch);
            _studioScheduler.Now = _studioNativeClock.Time;
            _studioScheduler.InvokeAnchor = _studioNativeClock.FixedTime;
            _studioScheduler.CoroutineAnchor = _studioNativeClock.Time;
            IGTAP.EngineSim.Engine.Scheduler = _studioScheduler;
            ApplyStudioInput(previous);
            ConfigureStudioMovers(true);
        }
        private void ApplyStudioInput(StudioInput input)
        { _studioInput = input; StudioFrameControls.Held = input.Held; StudioFrameControls.Press = input.Press; StudioFrameControls.Release = input.Release; }

        public JiggleDropScript[] StudioOrbs => _orbs;
        public bool StudioActiveOrb => _orbs.Any(orb => OrbCooldown(orb) > 0);
        public bool StudioActiveMover => World.All<PlatformMover>().Any(m => m.isActiveAndEnabled &&
            (bool)typeof(PlatformMover).GetField("isPhaseChangeInProgess", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(m));
        public bool StudioMovingMover => World.All<PlatformMover>().Any(m => m.isActiveAndEnabled && m.linearV.sqrMagnitude > .0001f);
        public void ConfigureStudioMoverContinuations(System.Text.Json.JsonElement start)
        {
            if (!start.TryGetProperty("moverContinuations", out var routines)) return;
            foreach (var row in routines.EnumerateArray())
            {
                var type = typeof(PlatformMover).Assembly.GetType(row.GetProperty("type").GetString());
                bool belongs = false;
                for (var owner = type; owner != null; owner = owner.DeclaringType) if (owner == typeof(PlatformMover)) belongs = true;
                if (type == null || !belongs || !typeof(IEnumerator).IsAssignableFrom(type) || !type.FullName.Contains("JumpToState"))
                    throw new NotSupportedException("Unsupported native mover continuation.");
                var target = World.Objects[row.GetProperty("target").GetInt32()] as PlatformMover
                    ?? throw new InvalidOperationException("Native mover continuation has no owner.");
                var body = World.Read(row.GetProperty("fields"), type) as IEnumerator
                    ?? throw new InvalidOperationException("Native mover continuation has no iterator.");
                int state = (int)type.GetField("<>1__state", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(body);
                if (state != 1 && state != 2) throw new InvalidOperationException("Native platform reset has no pending fixed-phase wait.");
                _studioScheduler.ImportFixed(target, body);
            }
        }

        public int StepStudioFrame(StudioInput input) => StepStudioFrame(input, null);
        public int StepStudioFrame(StudioInput input, StudioObservedFrame observed)
        {
            if (_studioScheduler == null || IGTAP.EngineSim.Engine.Scheduler != _studioScheduler)
                throw new InvalidOperationException("This worker thread does not own the simulation.");
            if (float.IsNaN(input.X) || float.IsNaN(input.Y) || Math.Abs(input.X) > 1 || Math.Abs(input.Y) > 1 ||
                ((input.Held | input.Press | input.Release) & ~63) != 0) throw new ArgumentException("Invalid offline input.");
            double dt = observed == null ? 1.0 / _studioFps : observed.UnscaledDelta;
            float scale = _studioScale;
            Time.timeScale = scale;
            double total = _studioPhase + dt * scale;
            int pus = observed == null ? (int)Math.Floor(total / .02 + 1e-10) : observed.FixedInputs.Length;
            double nextPhase = Math.Max(0, total - pus * .02);
            float nativeDelta;
            if (observed == null) nativeDelta = _studioNativeClock.Advance(pus, nextPhase, scale, _studioFps);
            else
            {
                if (observed.FixedTimes.Length != pus || observed.FixedUnscaledDeltas.Length != pus || pus > 42 || observed.FixedDelta != World.FixedDeltaTime) throw new ArgumentException("Invalid observed physics schedule.");
                _studioNativeClock.TimeTicks = (long)Math.Round(observed.Time * NativeFrameClock.Rate);
                nativeDelta = observed.Delta;
            }
            _studioScheduler.CoroutineAnchor = _studioNativeClock.Time;
            Physics.FrameStart();
            Shop.Economy?.StartPass();
            for (int pu = 0; pu < pus; pu++)
            {
                _studioScheduler.FixedPhase=true;
                if (observed == null) _studioNativeClock.FixedTimeTicks += NativeFrameClock.FixedTicks;
                else { _studioNativeClock.FixedTimeTicks = (long)Math.Round(observed.FixedTimes[pu] * NativeFrameClock.Rate); ApplyStudioInput(observed.FixedInputs[pu]); }
                StudioFrameControls.Delta = World.FixedDeltaTime;
                StudioFrameControls.UnscaledDelta = observed == null ? (scale > 0 ? NativeFrameClock.RequestedFixedStep / scale : 0) : observed.FixedUnscaledDeltas[pu];
                _studioScheduler.Now = _studioNativeClock.FixedTime;
                _studioScheduler.InvokeAnchor = _studioNativeClock.FixedTime;
                StudioFrameControls.Now = (float)_studioNativeClock.FixedTime;
                Shop.Economy?.FixedStep();
                double vmanBefore=_vmanTimeLeft;
                EconomyFixedUpdates();
                if(VmanScript.isCurrentlyVman)
                {
                    if(globalStats.globalUpgradeDict[globalStats.globalUpgradeSet.vmanTime]<0)_vmanTimeLeft=vmanBefore+World.FixedDeltaTime;
                    VmanScript.vmanTimeLeft=_vmanTimeLeft;
                    if(globalStats.globalUpgradeDict[globalStats.globalUpgradeSet.vmanTime]>=0&&_vmanTimeLeft<=0&&!_vmanBlockEnding)
                        throw new NotSupportedException("The timed VMAN run ended; a section cannot cross the scene transition.");
                }
                StudioSample?.Invoke(pu + 1, "before-pu");
                Vector3 render = Player.transform.m_Position;
                bool pendingTransformWrite = Physics.PlayerTransformDirty;
                _fixedUpdate();
                StudioAfterMovement?.Invoke();
                if (observed != null) StudioSample?.Invoke(pu + 1, "before-physics");
                // A transform read does not teleport the interpolated body.
                if (!pendingTransformWrite && Player.transform.m_Position == render) Player.transform.StoreWorldPosition(_stepTo);
                Physics.SyncTransforms(); _stepFrom = BodyPosition();
                BeforeStudioMoverPhysics();
                Physics.Simulate(World.FixedDeltaTime);
                _studioInterpolate = !Physics.PlayerTransformDirty;
                // Contact callbacks may move the Transform (springs do this).
                // Native after-physics observers still see the old body pose.
                // Leave the write pending until the next native sync phase;
                // synchronising here teleports the body one phase too early.
                _stepTo = BodyPosition(); _studioPus++;
                AfterStudioMoverPhysics();
                Shop.StudioSyncCurrencies();
                StudioSample?.Invoke(pu + 1, "after-pu");
                Shop.FixedStep();
                _studioScheduler.Resume(StudioScheduler.Wait.Fixed);
                if(observed != null) _studioScheduler.Fire(false,true);
                int rebases = _rebases.Length; ResumeRebase();
                if (_rebases.Length != rebases)
                { Vector3 by = _rebases[_rebases.Length - 1]; _stepFrom -= by; _stepTo -= by; ShiftStudioMovers(by); }
            }
            _studioScheduler.FixedPhase=false;
            // PreUpdate.Physics2DEarlyUpdate follows the fixed loop. A script
            // transform write therefore remains unsynchronised in before-PU
            // observations, but is visible even in frames without a PU.
            bool playerWrite = Physics.PlayerTransformDirty;
            if (playerWrite || StudioMoverWritesPending())
            {
                Physics.SyncTransforms();
                if(playerWrite) { _stepFrom = _stepTo = BodyPosition(); _studioInterpolate = false; }
            }
            if(observed != null) _studioNativeClock.FixedTimeTicks = (long)Math.Round(observed.LastFixedTime * NativeFrameClock.Rate);
            _studioTime += dt * scale; _studioPhase = nextPhase;
            _studioScheduler.Now = _studioNativeClock.Time; _studioScheduler.InvokeAnchor = _studioNativeClock.FixedTime;
            _studioScheduler.Frame = ++_studioFrame;
            StudioFrameControls.Now = (float)_studioNativeClock.Time;
            StudioFrameControls.Delta = nativeDelta; StudioFrameControls.UnscaledDelta = (float)dt;
            Shop.StepClones(nativeDelta); Shop.StudioSyncCurrencies();
            ApplyStudioInput(input);
            float alpha = observed == null ? _studioNativeClock.Interpolation : (float)((observed.Time - observed.LastFixedTime) / observed.FixedDelta);
            RenderStudioMovers(alpha);
            Player.transform.StoreWorldPosition(_studioInterpolate && Physics.StudioBodyAwake(_studioRenderBody) ? new Vector3(Blend(_stepFrom.x, _stepTo.x, alpha), Blend(_stepFrom.y, _stepTo.y, alpha), Blend(_stepFrom.z, _stepTo.z, alpha)) : _stepTo);
            Touch(Player.pauseMenu); _studioMenuUpdate();
            if (observed != null) StudioSample?.Invoke(pus, "before-input-update");
            _update();
            StudioAfterMovement?.Invoke();
            _studioScheduler.Fire();
            _studioScheduler.Resume(StudioScheduler.Wait.Frame);
            StepStudioAnimation(nativeDelta);
            FloatingOriginFrame();
            _studioScheduler.Resume(StudioScheduler.Wait.End);
            _studioScale = Time.timeScale;
            Shop.StudioSyncCurrencies();
            if (_studioContinuous && observed == null)
            {
                double nextTotal = _studioPhase + dt * _studioScale;
                int nextPus = (int)Math.Floor(nextTotal / .02 + 1e-10);
                _studioNativeClock.Prepare(nextPus, Math.Max(0, nextTotal - nextPus * .02), _studioScale, _studioFps);
            }
            if(StudioHasCheckpoints)AdvanceStudioCheckpoint();
            StudioSample?.Invoke(pus, "frame-end");
            return pus;
        }
        private static float Blend(float from, float to, float alpha)
        { alpha = Math.Clamp(alpha, 0f, 1f); float left = (1f - alpha) * from; float right = alpha * to; return left + right; }
        public StudioSnapshot CaptureStudioState() => new StudioSnapshot { Owner = this, World = Capture(_studioFrame),
            Scheduled = _studioScheduler.Capture(), Input = _studioInput, Phase = _studioPhase, Time = _studioTime,
            Scale = _studioScale, EndPacing = _studioContinuous, Frame = _studioFrame, Pus = _studioPus, NativeClock = _studioNativeClock,
            Animation = _studioAnimation?.State ?? default, AnimationSpeed = Player.animator?.speed ?? 1,
            Interpolate = _studioInterpolate, Movers = CaptureStudioMovers(),Checkpoint=_studioCheckpoint,
            VmanTime=VmanScript.vmanTimeLeft,VmanScore=VmanScript.vmanScore,
            CoursePaths = _coursePathLists.Select(paths => paths.Select(path => path.Cast<object>().ToArray()).ToArray()).ToArray() };
        public void RestoreStudioState(StudioSnapshot state)
        {
            if (!ReferenceEquals(state.Owner, this)) throw new InvalidOperationException("Offline snapshots belong to their creating worker.");
            Restore(state.World); _inRun = true; _studioScheduler.Restore(state.Scheduled);
            Shop.StudioSyncCurrencies();
            Shop.StudioSyncLocals();
            for (int course = 0; course < _coursePathLists.Length; course++)
                for (int path = 0; path < _coursePathLists[course].Length; path++)
                {
                    var list = _coursePathLists[course][path]; list.Clear();
                    foreach (object value in state.CoursePaths[course][path]) list.Add(value);
                }
            _studioPhase = state.Phase; _studioTime = state.Time; _studioScale = state.Scale;
            _studioFrame = state.Frame; _studioPus = state.Pus; ApplyStudioInput(state.Input);
            _studioNativeClock = state.NativeClock;
            _studioContinuous = state.EndPacing;
            _studioInterpolate = state.Interpolate;
            _studioCheckpoint=state.Checkpoint;
            VmanScript.vmanTimeLeft=state.VmanTime;VmanScript.vmanScore=state.VmanScore;
            RestoreStudioMovers(state.Movers);
            _studioAnimation?.Restore(state.Animation);
            if (Player.animator != null) Player.animator.speed = state.AnimationSpeed;
            StudioFrameControls.Now = (float)_studioNativeClock.Time; Time.timeScale = _studioScale;
        }
    }

    // Times are scaled seconds, not rounded PU counts. Frame-yield and
    // FixedUpdate-yield coroutines resume in separate phases.
    internal sealed class StudioScheduler : IScheduler
    {
        internal enum Wait { Seconds, Fixed, Frame, End }
        internal sealed class Pending { public MonoBehaviour Target; public string Method; public double Due; public float Repeat; public bool FixedOrigin; }
        internal sealed class Routine { public MonoBehaviour Target; public IEnumerator Body; public Coroutine Handle; public Wait Wait; public double Due; public int Frame; }
        internal sealed class State { public Pending[] Pending; public Routine[] Routines; public double Now, InvokeAnchor, CoroutineAnchor; public int Frame; public bool FixedPhase; }
        private readonly List<Pending> _pending = new List<Pending>();
        private readonly List<Routine> _routines = new List<Routine>();
        private readonly Stack<List<Pending>> _pendingScratch = new Stack<List<Pending>>();
        private readonly Stack<List<Routine>> _routineScratch = new Stack<List<Routine>>();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type,string),MethodInfo> Methods =
            new System.Collections.Concurrent.ConcurrentDictionary<(Type,string),MethodInfo>();
        private readonly Action<MonoBehaviour> _touch;
        private static readonly MethodInfo Clone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
        public double Now, InvokeAnchor, CoroutineAnchor; public int Frame;
        public bool FixedPhase;
        public int PendingCount => _pending.Count;
        public int RoutineCount => _routines.Count;
        public void ImportInvoke(MonoBehaviour target, string method, double seconds, bool fixedOrigin) =>
            _pending.Add(new Pending { Target = target, Method = method, Due = Now + Math.Max(0, seconds), Repeat = -1, FixedOrigin = fixedOrigin });
        public StudioScheduler(Action<MonoBehaviour> touch) { _touch = touch; }
        public void ImportFixed(MonoBehaviour target, IEnumerator body)
        {
            _touch(target);
            _routines.Add(new Routine { Target = target, Body = body, Handle = new Coroutine { routine = body }, Wait = Wait.Fixed, Frame = Frame });
        }
        public void Invoke(MonoBehaviour target, string method, float delay, float repeatRate) =>
            _pending.Add(new Pending { Target = target, Method = method, Due = InvokeAnchor + Math.Max(0, delay), Repeat = repeatRate, FixedOrigin=FixedPhase });
        public void CancelInvoke(MonoBehaviour target, string method) => _pending.RemoveAll(p => ReferenceEquals(p.Target, target) && (method == null || p.Method == method));
        public Coroutine StartCoroutine(MonoBehaviour target, IEnumerator routine)
        {
            if (!target.gameObject.activeInHierarchy) return null;
            var handle = new Coroutine { routine = routine }; var r = new Routine { Target = target, Body = routine, Handle = handle };
            _touch(target); if (routine.MoveNext()) Park(r, routine.Current); return handle;
        }
        private void Park(Routine r, object yielded)
        {
            r.Frame = Frame;
            if (yielded is WaitForSeconds seconds) { r.Wait = Wait.Seconds; r.Due = CoroutineAnchor + seconds.m_Seconds; }
            else if (yielded is WaitForFixedUpdate) r.Wait = Wait.Fixed;
            else if (yielded is WaitForEndOfFrame) r.Wait = Wait.End;
            else if (yielded == null) r.Wait = Wait.Frame;
            else throw new NotSupportedException("Unsupported offline coroutine yield: " + yielded.GetType().FullName);
            _routines.Add(r);
        }
        public void Fire(bool resumeSeconds = true, bool fixedOriginsOnly = false)
        {
            if (_pending.Count > 0)
            {
                var due = _pendingScratch.Count == 0 ? new List<Pending>() : _pendingScratch.Pop();
                try
                {
                    // Stable insertion order at equal deadlines, just as
                    // OrderBy. Snapshot eligibility before running callbacks:
                    // new invokes cannot execute twice in the same phase.
                    foreach(var pending in _pending)
                        if(pending.Due <= Now && (!fixedOriginsOnly || pending.FixedOrigin))
                        {
                            int index=due.Count;
                            while(index>0 && due[index-1].Due>pending.Due)index--;
                            due.Insert(index,pending);
                        }
                    foreach(var p in due)
                    {
                        if (!_pending.Remove(p) || !p.Target) continue;
                        if (p.Repeat >= 0) Invoke(p.Target, p.Method, Math.Max(p.Repeat, .000001f), p.Repeat);
                        _touch(p.Target);
                        var method = Methods.GetOrAdd((p.Target.GetType(),p.Method),key =>
                            key.Item1.GetMethod(key.Item2, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null)
                            ?? throw new MissingMethodException(key.Item1.FullName,key.Item2));
                        method.Invoke(p.Target, null);
                    }
                }
                finally {due.Clear();_pendingScratch.Push(due);}
            }
            if(resumeSeconds) Resume(Wait.Seconds);
        }
        public void Resume(Wait phase)
        {
            if (_routines.Count == 0) return;
            var due = _routineScratch.Count == 0 ? new List<Routine>() : _routineScratch.Pop();
            try
            {
                foreach(var r in _routines)
                    if(r.Wait == phase && (phase != Wait.Frame || r.Frame < Frame) && (phase != Wait.Seconds || r.Due <= Now))due.Add(r);
                foreach(var r in due)
                {
                    if (!_routines.Remove(r) || !r.Target || !r.Target.gameObject.activeInHierarchy) continue;
                    _touch(r.Target); if (r.Body.MoveNext()) Park(r, r.Body.Current);
                }
            }
            finally {due.Clear();_routineScratch.Push(due);}
        }
        public State Capture() => new State { Now = Now, InvokeAnchor = InvokeAnchor, CoroutineAnchor = CoroutineAnchor, Frame = Frame, FixedPhase=FixedPhase, Pending = _pending.Select(p => (Pending)Clone.Invoke(p, null)).ToArray(),
            Routines = Copy(_routines.ToArray()) };
        private static Routine[] Copy(Routine[] routines)
        {
            var bodies = CoroutineSnapshot.CopyAll(routines.Select(r => r.Body).ToArray(),
                value => value is UnityEngine.Object || value is WaitForEndOfFrame || value is WaitForFixedUpdate || value is WaitForSeconds);
            return routines.Select((r, i) => {
                var copy = (Routine)Clone.Invoke(r, null); copy.Body = bodies[i]; return copy;
            }).ToArray();
        }
        public void Restore(State state)
        { Now = state.Now; InvokeAnchor = state.InvokeAnchor; CoroutineAnchor = state.CoroutineAnchor; Frame = state.Frame; FixedPhase=state.FixedPhase; _pending.Clear(); _pending.AddRange(state.Pending.Select(p => (Pending)Clone.Invoke(p, null))); _routines.Clear(); _routines.AddRange(Copy(state.Routines)); }
    }
}
