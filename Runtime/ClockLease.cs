using System;
using Igtap.Tas.Core;
using UnityEngine;
using UnityEngine.InputSystem;

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
        public float SimulationScale => _paused ? _resumeScale : Time.timeScale;

        public ClockLease(int fps, float fixedDelta)
        {
            _fps = fps;
            Time.captureDeltaTime = 1f / fps;
            Time.fixedDeltaTime = fixedDelta;
            Time.timeScale = 1;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = fps;
            InputSystem.settings.updateMode = InputSettings.UpdateMode.ProcessEventsManually;
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
            Resume();
        }
        public void Pause()
        {
            if (_paused) return;
            _resumeScale = Time.timeScale; _paused = true;
            Time.timeScale = 0;
            Physics2D.simulationMode = SimulationMode2D.Script;
        }
        public void Resume()
        {
            if (!_paused) return;
            Physics2D.simulationMode = _physics;
            Time.timeScale = _resumeScale; _paused = false;
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
