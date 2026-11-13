using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Search;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Igtap.Tas.Runtime
{
    internal sealed class TrajectoryController : ITrajectoryPreview
    {
        private sealed class Work
        {
            internal TrajectoryRequest Request;
            internal TrajectoryJournal.Snapshot History;
        }
        private sealed class Run
        {
            internal string Directory;
            internal readonly CancellationTokenSource Cancel = new CancellationTokenSource();
            internal Work Pending;
            internal volatile TrajectoryResult Result;
            internal volatile string Status = "Preparing trajectory simulator...";
            internal volatile bool Failed;
            internal bool Started;
            internal SimulationCapture.Snapshot Capture;
        }
        private readonly TasSession _session;
        private readonly string _data, _root;
        private Run _run;
        private Movie _movie;
        private int _anchor, _playerId, _lastFrame = -1;
        private long _serial;
        private FrameInput _lastInput;
        private string _map;
        private double _nextRequest, _retry;
        private bool _history;
        private bool _refreshRequired;
        private Rigidbody2D _body;
        private FloatingOrigin _origin;
        private TrajectoryJournal _journal;
        public bool Enabled { get; private set; }
        public int Horizon { get; private set; } = 280;
        public string Status { get; private set; } = "Enable the trajectory preview while paused in a TAS.";
        public TrajectoryResult Visible { get; private set; }
        public int VisibleOffset { get; private set; }

        internal TrajectoryController(TasSession session)
        {
            _session = session; _data = Path.GetDirectoryName(session.Store.DirectoryPath);
            _root = Path.Combine(_data, "trajectory");
        }
        public void Configure(bool enabled, int frames)
        {
            if (frames < 1 || frames > 2000) throw new ArgumentException("Preview length must be 1-2000 TAS frames.");
            bool changed = Horizon != frames, wasEnabled = Enabled; Horizon = frames; Enabled = enabled;
            _lastFrame = -1;
            if (!enabled)
            {
                Stop(); _refreshRequired = false; Status = "Trajectory preview off.";
                if (_history && _session.Search?.Busy != true) SimulationCapture.CollectHistory = false;
                _history = false;
            }
            else
            {
                if (!SimulationCapture.CollectHistory) { SimulationCapture.CollectHistory = true; _history = true; }
                if (changed) Visible = null;
                if (changed || !wasEnabled) _refreshRequired = false;
                if (_run?.Failed == true && !_refreshRequired) Stop();
                _retry = _nextRequest = 0;
            }
        }
        public void Refresh()
        {
            if (!Enabled) throw new InvalidOperationException("Enable the trajectory preview first.");
            if (!_session.Active || !_session.Frozen) throw new InvalidOperationException("Pause the TAS before refreshing its trajectory snapshot.");
            Stop(); _refreshRequired = false; _retry = _nextRequest = 0;
            Status = "Refreshing trajectory snapshot...";
        }
        public void Tick()
        {
            Visible = null;
            if (!Enabled) return;
            if (!SimulationCapture.CollectHistory) { SimulationCapture.CollectHistory = true; _history = true; }
            try
            {
                if (!_session.Active || _session.WaitingForLoad || _session.Mode == SessionMode.Warmup ||
                    _session.RestoringState || _session.SearchPreview || _session.Search?.Busy == true ||
                    _session.Mode == SessionMode.Desync || _session.Mode == SessionMode.Complete || !_session.Adapter.IsAlive)
                { Stop(); _refreshRequired = false; Status = "Preview waits for an initialized recording or replay."; return; }
                var player = _session.Adapter.Player;
                if (_session.PreviewTimeScale <= 0 || player.GamePaused || player.pauseMenu?.menuOpen == true)
                { Status = "Preview suspended during game menus/hitstop."; return; }
                string map = _session.Adapter.LoadedMapKey;
                bool changed = _movie != null && (!ReferenceEquals(_movie, _session.Movie) || _playerId != player.GetInstanceID() ||
                    _map != map || _session.Cursor < _anchor || _session.Cursor - _anchor > 20000);
                if (changed) { Stop(); _refreshRequired = false; }
                double now = Time.realtimeSinceStartupAsDouble;
                if (_refreshRequired) return;
                if (_run == null)
                {
                    if (_session.HasPendingEdits) { Status = "Save or discard edits before capturing a trajectory snapshot."; return; }
                    if (!_session.Frozen || now < _retry) { Status = "Pause once to capture the trajectory world."; return; }
                    Start(map); _nextRequest = 0;
                }
                var run = _run;
                if (run.Failed) { RequireRefresh(run.Status); return; }
                var input = _session.PreviewInput(out _);
                var result = run.Result;
                var world = PlayerWorld();
                var velocity = _body.linearVelocity;
                int offset = result?.Offset(_session.Cursor, input, world, new MotionVector(velocity.x, velocity.y)) ?? -1;
                if (offset >= 0) { Visible = result; VisibleOffset = offset; Status = result.Message; }
                else Status = result != null ? result.Message : run.Status;
                // A failed history check never leaves an old path on screen.
                // A fresh frozen snapshot can recover after savestate/rebuilds.
                if (result != null && result.End == "unavailable" && result.Id == _serial)
                { RequireRefresh(result.Message); return; }
                bool sameInput = _lastFrame >= 0 && TrajectoryRequest.Same(input, _lastInput);
                // Do not continuously supersede an in-flight forecast while
                // holding the same controls. Its path can cover later frames.
                if (sameInput && (result == null || result.Id < _serial)) return;
                // Keep using a matching forecast while it has useful range.
                // Live changes get a new forecast at up to 30 Hz; paused edits
                // and individual frame/PU steps do not wait for that throttle.
                bool enough = offset >= 0 && (result.End != "horizon" || offset < Math.Max(1, Horizon / 3));
                if (sameInput && (_session.Cursor == _lastFrame && (result == null || offset >= 0) || enough)) return;
                if (!_session.Frozen && now < _nextRequest) return;
                _nextRequest = now + 1.0 / 30; _lastFrame = _session.Cursor; _lastInput = input;
                int length = _session.Cursor - _anchor;
                if (_journal.Count > length) _journal.Truncate(length);
                while (_journal.Count < length) _journal.Append(_session.Movie.Frames[_anchor + _journal.Count].Input);
                var request = new TrajectoryRequest { Id = ++_serial, Frame = _session.Cursor, Frames = Horizon, Input = input,
                    NextFramePrepared = !_session.Frozen,
                    Expected = SimulationCapture.Sample(_session, 0, 0, "start") };
                Interlocked.Exchange(ref run.Pending, new Work { Request = request, History = _journal.Publish() });
            }
            catch (Exception error) { Stop(); RequireRefresh("Trajectory unavailable: " + error.Message); }
        }
        private void RequireRefresh(string message)
        { Visible = null; _refreshRequired = true; Status = message + " Preview remains enabled. Pause and use Refresh snapshot to retry."; }
        private MotionVector PlayerWorld()
        {
            var position = _body.position;
            if (_origin != null) position -= (Vector2)_origin.currentOrigin;
            return new MotionVector(position.x, position.y);
        }
        private void Start(string map)
        {
            using var timing = _session.Profiler.Measure(ProfileSection.TrajectoryCapture);
            _anchor = _session.Cursor; _movie = _session.Movie; _map = map; _playerId = _session.Adapter.Player.GetInstanceID();
            _body = _session.Adapter.Player.GetComponent<Rigidbody2D>(); _origin = UnityEngine.Object.FindFirstObjectByType<FloatingOrigin>();
            var run = new Run { Directory = Path.Combine(_root, Guid.NewGuid().ToString("N")) };
            _run = run; _lastFrame = -1;
            _journal = new TrajectoryJournal();
            var capture=SimulationCapture.Capture(_session);
            // A current-frame snapshot also needs the exact remaining native
            // Invoke deadlines. Do not quantize these to 50 Hz scheduler ticks.
            capture.Start["previewInvokes"] = new JArray(_session.MovementTimers.NativeDeadlines(_session.ScaledNow)
                .Where(t => ReferenceEquals(t.Owner, _session.Adapter.Player)).Select(t => new JObject {
                    ["method"] = t.Method, ["seconds"] = t.Remaining, ["fixed"] = t.FixedOrigin }));
            capture.Trace.AdvanceMode = "before-pacing";run.Capture=capture;
            run.Started = true; Task.Run(() => Worker(run));
        }
        private void Worker(Run run)
        {
            Process process = null;
            try
            {
                var token = run.Cancel.Token;
                token.ThrowIfCancellationRequested();
                run.Status="Writing detached trajectory snapshot...";
                run.Capture.Write(run.Directory);run.Capture=null;
                token.ThrowIfCancellationRequested();
                string executable = EngineHostLocation.Find(_data);
                if (string.IsNullOrEmpty(executable)) throw new FileNotFoundException("Install the full Tas Studio package with Bruteforce tools.");
                if (EngineHostPackage.Exists(_data) && string.Equals(executable,
                    Path.Combine(EngineHostPackage.BundledPath(_data), "Host", "TasStudio.EngineHost.exe"), StringComparison.OrdinalIgnoreCase))
                    executable = new EngineHostPackage(EngineHostPackage.BundledPath(_data), _data,
                        Path.GetDirectoryName(typeof(Movement).Assembly.Location)).Prepare(token);
                token.ThrowIfCancellationRequested();
                process = Process.Start(new ProcessStartInfo { FileName = executable,
                    Arguments = "trajectory \"" + run.Directory + "\" " + Process.GetCurrentProcess().Id,
                    WorkingDirectory = Path.GetDirectoryName(executable), UseShellExecute = false, CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true });
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                var began = Stopwatch.StartNew(); long last = 0; DateTime read = DateTime.MinValue; bool checkedProtocol = false;
                while (!token.IsCancellationRequested)
                {
                    if (process.HasExited) throw new IOException("Trajectory host exited. " + stderr.GetAwaiter().GetResult());
                    string failure = Path.Combine(run.Directory, "failure.json");
                    if (File.Exists(failure)) throw new IOException((string)JObject.Parse(File.ReadAllText(failure))["Message"]);
                    bool ready = File.Exists(Path.Combine(run.Directory, "ready.json"));
                    if (!ready && began.Elapsed.TotalSeconds > 90) throw new TimeoutException("Trajectory initialization timed out.");
                    if (ready)
                    {
                        if (!checkedProtocol)
                        {
                            if (!TrajectoryMailbox.TryRead<JObject>(Path.Combine(run.Directory,"ready.json"),out var handshake)) { token.WaitHandle.WaitOne(5); continue; }
                            if ((int?)handshake["Protocol"] != TrajectoryWire.Version) throw new InvalidDataException("Trajectory worker protocol differs. Install the matching full Tas Studio package.");
                            checkedProtocol = true;
                        }
                        run.Status = "Predicting held controls...";
                        var pending = Interlocked.Exchange(ref run.Pending, null);
                        if (pending != null)
                        {
                            if (!TrajectoryWire.WriteRequest(Path.Combine(run.Directory, "request.bin"), pending.Request, pending.History))
                                Interlocked.CompareExchange(ref run.Pending, pending, null);
                        }
                        string result = Path.Combine(run.Directory, "result.bin");
                        if (File.Exists(result) && File.GetLastWriteTimeUtc(result) != read)
                        {
                            var stamp = File.GetLastWriteTimeUtc(result);
                            if (TrajectoryWire.ReadResult(result, out var value))
                            { read = stamp; if (value.Id > last) { last = value.Id; run.Result = value; } }
                        }
                    }
                    token.WaitHandle.WaitOne(5);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { run.Status = "Trajectory unavailable: " + error.Message; run.Failed = true; }
            finally
            {
                run.Capture=null;
                try { if (process != null) { if (!process.HasExited) process.Kill(); process.WaitForExit(3000); process.Dispose(); } } catch { }
                // Only our GUID-named capture directory may be removed.
                Clean(run.Directory);
                run.Cancel.Dispose();
            }
        }
        public MotionPrediction Predict(PlayerMotion current, FrameInput input, int frame)
        {
            var result = Visible;
            if (result == null || !TrajectoryRequest.Same(input, _lastInput)) return new MotionPrediction(null, Status, 0, _session.StepSeconds);
            int index = VisibleOffset;
            if (index + 1 >= result.Points.Length) return new MotionPrediction(null, result.Message, 0, _session.StepSeconds);
            var next = result.Points[index + 1].Motion.Copy();
            // Engine coordinates are independent of Unity's floating origin.
            var here = PlayerWorld();
            next.Position.X += current.Position.X - here.X; next.Position.Y += current.Position.Y - here.Y;
            return new MotionPrediction(next, result.Message, (int)(result.Points[index + 1].Pus - result.Points[index].Pus), _session.StepSeconds,result.Points[index+1].Actions);
        }
        private void Stop()
        {
            Visible = null; var old = _run; _run = null; _lastFrame = -1;
            if (old != null) try { old.Cancel.Cancel(); } catch (ObjectDisposedException) { }
            if (old != null && !old.Started) { Clean(old.Directory); old.Cancel.Dispose(); }
        }
        private void Clean(string directory)
        {
            try { string full = Path.GetFullPath(directory), prefix = Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(Path.GetFileName(full), "N", out _) && Directory.Exists(full)) Directory.Delete(full, true); } catch { }
        }
        public void Dispose()
        {
            Enabled = false; Stop();
            if (_history && _session.Search?.Busy != true) SimulationCapture.CollectHistory = false;
            _history = false;
        }
    }
}
