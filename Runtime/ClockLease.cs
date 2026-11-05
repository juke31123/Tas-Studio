using System;
using Igtap.Tas.Core;
using UnityEngine;
using UnityEngine.InputSystem;
using FrameTiming = Igtap.Tas.Core.FrameTiming;

namespace Igtap.Tas.Runtime
{
    // Every global setting is restored on stop, error and unload.
    internal sealed class ClockLease : IDisposable
    {
        private readonly float _capture = Time.captureDeltaTime, _fixed = Time.fixedDeltaTime, _scale = Time.timeScale;
        private readonly int _target = Application.targetFrameRate, _vsync = QualitySettings.vSyncCount;
        private readonly InputSettings.UpdateMode _inputMode = InputSystem.settings.updateMode;
        private readonly SimulationMode2D _physics = Physics2D.simulationMode;
        private float _resumeScale;
        private bool _paused;
        private readonly int _fps;
        private float _speed = 1;
        private readonly FrameTiming _timing;
        private double _nextPhase;
        private int _nextPus, _alignmentAttempts;
        private float _nextScale;
        private double _scaledTime, _frameNativeTime;
        public double ScaledNow => _scaledTime - (Time.inFixedTimeStep ? _frameNativeTime - Time.fixedTimeAsDouble : 0);
        public float FixedUnscaledDelta => _nextScale > 0 ? FrameTiming.NativePuSeconds / _nextScale : 0;
        public double FixedUnscaledLag => _nextScale > 0 ? (_frameNativeTime - Time.fixedTimeAsDouble) / _nextScale : 0;
        public bool Aligning { get; private set; }
        public double Phase => _timing.Phase;
        public long Pus => _timing.Pus;
        public int FramesUntilPu => FrameTiming.FramesUntilPu(Phase, _fps, SimulationScale);
        public float SimulationScale => _paused ? _resumeScale : Time.timeScale;

        public ClockLease(int fps, float fixedDelta)
        {
            if (fixedDelta != FrameTiming.NativePuSeconds) throw new InvalidOperationException("TAS physics must use 50 PUs per second.");
            if (_inputMode != InputSettings.UpdateMode.ProcessEventsInDynamicUpdate)
                throw new InvalidOperationException("This timing protocol requires the game's dynamic input update mode.");
            _fps = fps;
            _timing = new FrameTiming(fps);
            Time.captureDeltaTime = 1f / fps;
            Time.fixedDeltaTime = fixedDelta;
            Time.timeScale = 1;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = fps;
            // Keep Unity's dynamic input update, after FixedUpdate and before
            // Update. Sampling in EarlyUpdate changes which PU sees a new axis.
        }
        public void SetSpeed(float multiplier)
        {
            PlaybackSpeed.Validate(multiplier);
            _speed = multiplier;
            EnforcePacing();
        }
        public void EnforcePacing()
        {
            // SettingsScript.Awake reapplies the game's FPS/VSync on scene reload.
            // A 100 FPS game setting with a 50 Hz movie otherwise becomes 2x.
            int target = PlaybackSpeed.TargetFrameRate(_fps, _speed);
            if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
            if (Application.targetFrameRate != target) Application.targetFrameRate = target;
        }
        public void HoldLoading()
        {
            // LoadScene is deferred. Native physics must be held even before the
            // new Movement.Start has initialized the fields that Bind depends on.
            _paused = true;
            Time.timeScale = 0;
            Physics2D.simulationMode = SimulationMode2D.Script;
        }
        public void FinishLoading(float gameScale)
        {
            _resumeScale = gameScale;
            Aligning = true; _alignmentAttempts = 0;
            AlignNextFrame();
        }
        private double NativePhase => Time.timeAsDouble - Time.fixedTimeAsDouble;
        private void AlignNextFrame()
        {
            // Advance only Unity's clocks while the loading gate still blocks
            // game callbacks and native physics. No movie frame is consumed.
            Time.timeScale = 1;
            Physics2D.simulationMode = SimulationMode2D.Script;
            double delta = Phase * FrameTiming.NativePuSeconds / FrameTiming.PuSeconds - NativePhase;
            delta -= Math.Floor(delta / FrameTiming.NativePuSeconds) * FrameTiming.NativePuSeconds;
            Time.captureDeltaTime = (float)(FrameTiming.NativePuSeconds + delta);
        }
        public bool CompleteAlignment()
        {
            double expected = Phase * FrameTiming.NativePuSeconds / FrameTiming.PuSeconds;
            if (Math.Abs(NativePhase - expected) > .000002)
            {
                if (++_alignmentAttempts >= 4) throw new InvalidOperationException("Could not align the frame/PU clocks. Restart the game and try again.");
                AlignNextFrame(); return false;
            }
            Aligning = false; Resume(); return true;
        }
        public void BeginFrame()
        {
            _frameNativeTime = Time.timeAsDouble;
            _scaledTime += Time.deltaTime;
            // The step prepared at the preceding complete-frame boundary has
            // now been consumed, including a game-driven timeScale of zero.
            int count = _timing.Advance(_nextScale);
            if (count != _nextPus || Math.Abs(Phase - _nextPhase) > 1e-8)
                throw new InvalidOperationException("The frame timing schedule changed unexpectedly.");
        }
        public void CompleteFrame(int observedPus)
        {
            if (observedPus != _nextPus)
                throw new InvalidOperationException("PU schedule differs: expected " + _nextPus + ", observed " + observedPus + ".");
            double expected = Phase * FrameTiming.NativePuSeconds / FrameTiming.PuSeconds;
            if (Math.Abs(NativePhase - expected) > .00002)
                throw new InvalidOperationException("Frame/PU timing offset differs.");
        }
        public void PrepareNextFrame()
        {
            if (_paused || Aligning) return;
            _nextScale = Time.timeScale;
            double total = Phase + _timing.FrameSeconds * _nextScale;
            _nextPus = (int)Math.Floor(total / FrameTiming.PuSeconds + 1e-10);
            _nextPhase = Math.Max(0, total - _nextPus * FrameTiming.PuSeconds);
            // Re-anchor to the actual native clock each frame. Float rounding
            // must not drift into an extra Update or move a PU to another row.
            Time.captureDeltaTime = _nextScale <= 0 ? (float)_timing.FrameSeconds :
                (float)((_nextPus * (double)FrameTiming.NativePuSeconds +
                    _nextPhase * FrameTiming.NativePuSeconds / FrameTiming.PuSeconds - NativePhase) / _nextScale);
            if (Time.captureDeltaTime <= 0 || float.IsNaN(Time.captureDeltaTime))
                throw new InvalidOperationException("Invalid next-frame clock step.");
        }
        public void Pause()
        {
            if (_paused) return;
            _resumeScale = Time.timeScale; _paused = true;
            Time.timeScale = 0;
            // Keep native interpolation history intact across editor pauses.
        }
        public void Resume()
        {
            if (!_paused) return;
            Physics2D.simulationMode = _physics;
            Time.timeScale = _resumeScale; _paused = false;
            PrepareNextFrame();
        }
        public void Dispose()
        {
            Physics2D.simulationMode = _physics;
            Time.captureDeltaTime = _capture;
            Time.fixedDeltaTime = _fixed;
            Time.timeScale = _scale;
            Application.targetFrameRate = _target;
            QualitySettings.vSyncCount = _vsync;
            InputSystem.settings.updateMode = _inputMode;
        }
    }
}
