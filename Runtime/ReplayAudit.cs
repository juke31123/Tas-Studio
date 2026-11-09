using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Igtap.Tas.Core;
using Newtonsoft.Json;

namespace Igtap.Tas.Runtime
{
    // Runs the installed game, first continuously, then one whole frame at a
    // time. Each pass verifies the original recording, including world witnesses.
    internal sealed class ReplayAudit
    {
        private readonly TasSession _session;
        private readonly List<object> _passes = new List<object>();
        private int _phase, _pass;
        private float _oldSpeed;
        public bool Running => _phase != 0;
        public bool Stepping => Running && _pass == 1;
        public string Result { get; private set; } = "";
        public string ReportPath { get; private set; } = "";
        public ReplayAudit(TasSession session) { _session = session; }
        public void Start()
        {
            if (Running || _session.Active || !_session.IsMainMenu || _session.HasPendingEdits)
                throw new InvalidOperationException("Return to the main menu and save edits before verifying a replay.");
            var movie = _session.Movie;
            if (movie.Frames.Count == 0 || movie.StartWorld == null || movie.Frames.Any(f => f.Expected == null || f.FixedTicks < 0))
                throw new InvalidOperationException("Verification needs a fully recorded movie with world checkpoints. Record the planned frames first.");
            for (int i = 0; i < movie.Frames.Count; i += movie.FrameRate)
                if (movie.Frames[i].World == null) throw new InvalidOperationException("This movie has incomplete world checkpoints.");
            _oldSpeed = _session.Speed; _pass = 0; _passes.Clear(); _phase = 1;
            try { _session.SetSpeed(1); _session.StartPlayback(); Result = "Verifying continuous playback, then frame stepping. Stop cancels."; }
            catch { _phase = 0; _session.SetSpeed(_oldSpeed); throw; }
        }
        public void Tick()
        {
            if (!Running) return;
            try
            {
                if (_session.Mode == SessionMode.Desync) { Finish(false, _session.Status); return; }
                if (_phase == 2)
                {
                    if (_session.Active) return;
                    if (_pass == 1) { Finish(true, "Continuous and frame-stepped playback matched the recording."); return; }
                    _pass++; _phase = 1; _session.StartPlayback(); return;
                }
                if (_session.Mode == SessionMode.Complete)
                {
                    _passes.Add(new { Mode = _pass == 0 ? "continuous" : "frame-stepped", Frames = _session.Cursor, Pus = _session.PuCount });
                    _phase = 2; _session.Stop();
                }
                else if (!_session.Active || _session.Mode == SessionMode.Stopping) Finish(false, "Cancelled or interrupted.");
                else if (Stepping && _session.Mode == SessionMode.Playback && _session.Frozen)
                    _session.TryQueue(_session.AuditStep);
            }
            catch (Exception e) { Finish(false, e.Message); }
        }
        public void Cancel() { if (Running) Finish(false, "Cancelled by user."); }
        private void Finish(bool passed, string detail)
        {
            _phase = 0; _session.SetSpeed(_oldSpeed); Result = (passed ? "PASS: " : "NOT PASSED: ") + detail;
            string path = Path.Combine(_session.Store.DirectoryPath, "replay-audit-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".json");
            ReportPath=path;
            try
            {
                File.WriteAllText(path, JsonConvert.SerializeObject(new { Passed = passed, Result, Movie = _session.FileName,
                    Fps = _session.Movie.FrameRate, PhysicsHz = 50, Passes = _passes,
                    Scope = "Matches the supplied recording in the installed game; does not independently prove unmodded-game equivalence." }, Formatting.Indented));
            }
            catch (Exception e) { Result += " Could not save report: " + e.Message; }
            _session.ReportAudit(Result);
        }
    }
}
