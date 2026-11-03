using System;
using System.Collections.Generic;
using System.IO;
using Igtap.Tas.Core;
using Newtonsoft.Json;

namespace Igtap.Tas.Runtime
{
    // Runs inside the real game: no input-injection helper or second mod needed.
    // A neutral route isolates reset/physics behavior from gameplay decisions.
    internal sealed class RepeatabilityCheck
    {
        private readonly TasSession _session;
        private readonly List<object> _runs = new List<object>();
        private int _stage, _replay, _pauseFrames;
        private bool _pauseIssued, _oldStartPaused;
        public bool Running => _stage != 0;
        public string Result { get; private set; } = "Not run.";
        public RepeatabilityCheck(TasSession session) => _session = session;
        public void Start()
        {
            if (Running || _session.Active) throw new InvalidOperationException("Stop the active session before running the repeatability test.");
            if (_session.Dirty) _session.Save();
            _runs.Clear(); _replay = 0; _oldStartPaused = _session.StartPaused;
            _session.FileName = "reset-check-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            _session.StartPaused = false; _session.DiagnosticNeutralInput = true;
            _session.SetSpeed(1);
            try { _session.StartRecording(); _stage = 1; Result = "Recording 120 neutral frames, then testing three replays."; }
            catch { Finish("FAILED to start", false); throw; }
        }
        public void Tick()
        {
            if (!Running) return;
            try
            {
                if (_session.Mode == SessionMode.Desync)
                {
                    _runs.Add(new { Replay = _replay, Passed = false, Frame = _session.Cursor, Detail = _session.Status });
                    Finish("FAILED: " + _session.Status, false); return;
                }
                if (_stage == 1 && _session.Mode == SessionMode.Recording && _session.Cursor == 120)
                {
                    _session.Save(); _session.Stop(); _stage = 2;
                }
                else if (_stage == 2 && !_session.Active)
                {
                    if (_replay == 3) { Finish("PASSED: three 120-frame idle replays, including pause/resume and different viewing speeds.", true); return; }
                    _session.SetSpeed(new[] { 1f, 0.5f, 0.1f }[_replay]);
                    _replay++; _pauseIssued = false; _pauseFrames = 0;
                    _session.StartPlayback(); _stage = 3;
                }
                else if (_stage == 3)
                {
                    if (_session.Mode == SessionMode.Complete)
                    {
                        _runs.Add(new { Replay = _replay, Passed = true, Frame = _session.Cursor, Detail = _session.Status });
                        _session.Stop(); _stage = 2;
                    }
                    else if (_session.Mode == SessionMode.Playback && _replay >= 2)
                    {
                        if (!_pauseIssued && _session.Cursor >= 30) { _pauseIssued = true; _session.TogglePause(); }
                        else if (_session.Frozen && ++_pauseFrames == 8) _session.TogglePause();
                    }
                }
                if (!_session.Active && _stage != 2) Finish("CANCELLED or interrupted: " + _session.Status, false);
            }
            catch (Exception error) { Finish("FAILED: " + error.Message, false); }
        }
        public void Cancel()
        {
            if (!Running) return;
            Finish("Cancelled by user.", false);
            if (_session.Active) _session.Stop();
        }
        private void Finish(string result, bool passed)
        {
            _stage = 0; Result = result;
            _session.DiagnosticNeutralInput = false;
            _session.StartPaused = _oldStartPaused; _session.SetSpeed(1);
            File.WriteAllText(Path.Combine(_session.Store.DirectoryPath, "repeatability-result.json"),
                JsonConvert.SerializeObject(new { Passed = passed, Result = result, Movie = _session.FileName, Runs = _runs }, Formatting.Indented));
        }
    }
}
