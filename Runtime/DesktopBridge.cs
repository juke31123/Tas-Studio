using System;
using System.Globalization;
using System.Linq;
using Igtap.Tas.Core;
using Igtap.Tas.DesktopModel;
using UnityEngine;
using Igtap.Tas.UI;

namespace Igtap.Tas.Runtime
{
    // Main-thread controller for the native window. No subprocess or IPC.
    internal sealed class DesktopBridge : IDisposable
    {
        private readonly TasSession _session;
        internal readonly StudioChannel Channel = new StudioChannel();
        private readonly IStudioWindow _window;
        private Command _pending;
        private Movie _undo;
        private int _undoStart, _revision, _completedSequence;
        private Movie _observed;
        private string _ack = "", _error = "";
        private bool _attached, _backgroundHeld, _background, _disposed;
        private double _nextSample, _nextFiles;
        private string[] _movies = Array.Empty<string>(), _states = Array.Empty<string>();
        public bool Connected => Channel.IsOpen;
        public string Error => string.IsNullOrEmpty(Channel.Error) ? _error : Channel.Error;
        public DesktopBridge(TasSession session, Func<StudioChannel, IStudioWindow> factory = null)
        {
            _session = session;
            _window = factory == null ? new NativeStudioWindow(Channel) : factory(Channel);
        }
        public void Open(int page = 0)
        {
            if (_disposed) return;
            HoldBackground();
            try
            {
                if (_session.Active && !_session.Frozen && (_session.Mode == SessionMode.Recording || _session.Mode == SessionMode.Playback))
                    _session.TogglePause();
                Channel.RequestedPage = page;
                _window.Open(); _error = "";
            }
            catch { ReleaseBackground(); throw; }
        }
        public void Early()
        {
            if (_disposed) return;
            if (Connected && !_attached)
            {
                _attached = true; HoldBackground();
            }
            if (!Connected)
            {
                if (_attached)
                {
                    // Closing the native window must not leave an invisible
                    // recording advancing. Preserve the movie and pause at a boundary.
                    if (_session.Active && !_session.Frozen &&
                        (_session.Mode == SessionMode.Recording || _session.Mode == SessionMode.Playback))
                        if (!_session.TryQueue(_session.TogglePause)) return;
                    _attached = false;
                }
                Channel.Clear();
                _pending = null;
                if (!Channel.Starting) ReleaseBackground();
                return;
            }
            if (_pending == null) Channel.TryTake(out _pending);
            if (_pending == null) return;
            var command = _pending;
            if (_session.TryQueue(() => {
                if (!Connected) return; // Discard an action if its window closed before this boundary.
                try { Execute(command); _error = ""; }
                catch (Exception e) { _error = e.Message; }
                finally { _ack = command.Id ?? ""; _completedSequence = command.Sequence; _nextSample = _nextFiles = 0; }
            })) _pending = null;
        }
        private void Execute(Command c)
        {
            ObserveMovie();
            if (string.IsNullOrEmpty(c.Id) || c.Id.Length > 64 || (c.Name?.Length ?? 0) > 200)
                throw new ArgumentException("Invalid desktop command.");
            switch (c.Action)
            {
                case "pause": RequireTransport(); _session.TogglePause(); break;
                case "step": RequireTransport(); _session.Step(); break;
                case "stepInput":
                    RequireTransport();
                    if (_session.Mode == SessionMode.Recording) { _session.AuthoredInput = Input(c); _session.UseAuthoredInput = c.Authored; }
                    _session.Step(); break;
                case "stop": if (_session.Active) _session.Stop(); break;
                case "save": _session.FileName = MovieStore.NormalizeName(c.Name); _session.Save(); break;
                case "load": _session.LoadNamed(c.Name); _undo = null; break;
                case "replay":
                    if (!_session.IsMainMenu) throw new InvalidOperationException("Start Replay is available from the main menu.");
                    _session.StartPlayback(); break;
                case "create":
                    var setup = new TasStartSetup { FrameRate = c.Fps, Course = c.Course, AirDashes = c.Dashes, WallJump = c.WallJump,
                        DoubleJump = c.AirJump, Dash = c.UnlockDash, OmniDash = c.OmniDash, BlockSwap = c.BlockSwap,
                        Light = c.Light, RefillOrbs = c.Orbs, Zippers = c.Zippers };
                    setup.Validate();
                    if (_session.Active || !_session.IsMainMenu) throw new InvalidOperationException("Create New Tas is available from the main menu.");
                    _session.FileName = MovieStore.NormalizeName(c.Name); _session.StartPaused = true;
                    _session.StartConfiguredRecording(setup); _undo = null; break;
                case "speed":
                    if (!PlaybackSpeed.TryParse(c.Speed, out var speed)) throw new ArgumentException(PlaybackSpeed.Error);
                    _session.SetSpeed(speed); break;
                case "timelineInput": _session.UseTimelineInput = c.Authored; if (!c.Authored) _session.UseAuthoredInput = false; break;
                case "paint": case "insertRows": case "deleteRows": case "clearRows":
                    if (c.Revision != _revision) throw new InvalidOperationException("The timeline changed. Try the edit again.");
                    if (!_session.CanEditTimeline) throw new InvalidOperationException("Pause the run before editing frames.");
                    var edited = MovieStore.Clone(_session.EditorMovie);
                    if (c.Action == "paint") FrameCellEdit.Paint(edited, c.Start, c.Count, (InputColumn)c.Column, c.CellHeld, c.Value);
                    else if (c.Action == "insertRows") Timeline.Insert(edited, c.Start, c.Count, default);
                    else if (c.Action == "deleteRows") Timeline.Delete(edited, c.Start, c.Count);
                    else Timeline.Replace(edited, c.Start, c.Count, default);
                    edited.Validate();
                    var before = MovieStore.Clone(_session.EditorMovie);
                    _session.StageTimelineEdit(edited, c.Start); _undo = before; _undoStart = c.Start; ObserveMovie(true); break;
                case "saveEdits": _session.SaveTimelineEdits(); ObserveMovie(true); break;
                case "discardEdits": _session.DiscardTimelineEdits(); _undo = null; ObserveMovie(); break;
                case "input": _session.AuthoredInput = Input(c); _session.UseAuthoredInput = c.Authored; break;
                case "saveState": _session.SaveState(c.Name); break;
                case "loadState":
                    if (!_session.CanSaveState) throw new InvalidOperationException("Pause recording before loading a state.");
                    _session.LoadState(c.Name); break;
                case "replace": case "insert": case "delete":
                    if (!_session.CanEdit) throw new InvalidOperationException("Stop the run before editing the timeline.");
                    var candidate = MovieStore.Clone(_session.Movie);
                    if (c.Action == "replace") Timeline.Replace(candidate, c.Start, c.Count, Input(c));
                    else if (c.Action == "insert") Timeline.Insert(candidate, c.Start, c.Count, Input(c));
                    else Timeline.Delete(candidate, c.Start, c.Count);
                    candidate.Validate(); _undo = MovieStore.Clone(_session.Movie); _undoStart = c.Start; _session.RestoreEdit(candidate); ObserveMovie(true); break;
                case "undo":
                    if (_undo == null) throw new InvalidOperationException("No desktop edit to undo.");
                    _session.StageTimelineEdit(_undo, _undoStart); _undo = null; ObserveMovie(); break;
                default: throw new ArgumentException("Unknown desktop command.");
            }
        }
        private void RequireTransport()
        {
            if (!_session.Active || (_session.Mode != SessionMode.Recording && _session.Mode != SessionMode.Playback))
                throw new InvalidOperationException("No recording or replay is ready.");
        }
        private static FrameInput Input(Command c)
        {
            if (float.IsNaN(c.X) || float.IsInfinity(c.X) || float.IsNaN(c.Y) || float.IsInfinity(c.Y) || Math.Abs(c.X) > 1 || Math.Abs(c.Y) > 1)
                throw new ArgumentException("Input axes must be between -1 and 1.");
            return new FrameInput { X = c.X, Y = c.Y, Held = (c.Jump ? Buttons.Jump : 0) | (c.Dash ? Buttons.Dash : 0) | (c.Reset ? Buttons.Reset : 0) };
        }
        public void Publish(PlayerStatsMonitor stats)
        {
            if (!Connected || Time.realtimeSinceStartupAsDouble < _nextSample) return;
            _nextSample = Time.realtimeSinceStartupAsDouble + .1;
            try
            {
                ObserveMovie();
                if (Time.realtimeSinceStartupAsDouble >= _nextFiles)
                {
                    _movies = _session.Store.ListMovies().Take(500).Select(f => f.Name).ToArray();
                    _states = _session.States.List().Take(500).Select(f => f.Name).ToArray();
                    _nextFiles = Time.realtimeSinceStartupAsDouble + 2;
                }
                var editorMovie = _session.EditorMovie;
                int page = Math.Max(0, Math.Min(Channel.Page, editorMovie.Frames.Count));
                var snapshot = new Snapshot {
                    Bindings = _session.Bindings.Copy(),
                    Mode = _session.Mode.ToString(), Status = _session.Status, Name = _session.FileName, Ack = _ack, Error = _error,
                    Revision = _revision, CompletedSequence = _completedSequence, CanEditTimeline = _session.CanEditTimeline, TimelineInput = _session.UseTimelineInput, PendingEdits = _session.HasPendingEdits,
                    Cursor = _session.Cursor, Frames = editorMovie.Frames.Count, Fps = _session.Movie.FrameRate,
                    FramesUntilPu = _session.FramesUntilPu, Pus = _session.PuCount,
                    HasPerformanceSample = _session.Performance.HasSample, ActualFps = _session.Performance.ActualFps,
                    AchievedSpeed = _session.Performance.AchievedSpeed, PerformanceError = _session.Performance.Error,
                    Rerecords = _session.Movie.Rerecords, Speed = _session.Speed, Active = _session.Active, Paused = _session.Frozen,
                    MainMenu = _session.IsMainMenu, CanEdit = _session.CanEdit, CanState = _session.CanSaveState && !_session.HasPendingEdits,
                    CanTransport = !_session.HasPendingEdits && _session.Active && (_session.Mode == SessionMode.Recording || _session.Mode == SessionMode.Playback),
                    Dirty = _session.Dirty, Authored = _session.UseAuthoredInput, Movies = _movies, States = _states, Page = page,
                    SelectedInput = new FrameRow { X = _session.AuthoredInput.X, Y = _session.AuthoredInput.Y,
                        Jump = _session.AuthoredInput.Down(Buttons.Jump), Dash = _session.AuthoredInput.Down(Buttons.Dash), Reset = _session.AuthoredInput.Down(Buttons.Reset) },
                    Timeline = editorMovie.Frames.Skip(page).Take(200).Select((f, i) => new FrameRow {
                        Frame = page + i, Pus = f.FixedTicks, X = f.Input.X, Y = f.Input.Y, Jump = f.Input.Down(Buttons.Jump) || f.Input.Down(Buttons.Jump2),
                        Dash = f.Input.Down(Buttons.Dash) || f.Input.Down(Buttons.Dash2), Reset = f.Input.Down(Buttons.Reset), Verified = f.Expected != null
                    }).ToArray(),
                    Prediction = stats.Prediction?.Explanation ?? stats.Message,
                    InputSource = stats.InputSource
                };
                var current = stats.Current; var next = stats.Prediction?.Next;
                if (current != null) snapshot.Stats = new[] {
                    Row("Position X", current.Position.X, next?.Position.X), Row("Position Y", current.Position.Y, next?.Position.Y),
                    Row("Velocity X", current.Velocity.X, next?.Velocity.X), Row("Velocity Y", current.Velocity.Y, next?.Velocity.Y),
                    Row("Speed", current.Velocity.Magnitude, next?.Velocity.Magnitude),
                    new StatRow { Name = "State", Current = current.State.ToString(), Next = next?.State.ToString() ?? "--" },
                    Row("Air jumps", current.AirJumps, next?.AirJumps), Row("Dashes", current.AirDashes, next?.AirDashes),
                    Row("Momentum X", current.Momentum.X, next?.Momentum.X), Row("Momentum Y", current.Momentum.Y, next?.Momentum.Y)
                };
                Channel.Latest = snapshot;
            }
            catch (Exception e) { _error = "Desktop snapshot: " + e.Message; }
        }
        private static StatRow Row(string label, float value, float? next) => new StatRow {
            Name = label, Current = value.ToString("0.000", CultureInfo.InvariantCulture), Next = next?.ToString("0.000", CultureInfo.InvariantCulture) ?? "--"
        };
        private void ObserveMovie(bool preserveUndo = false)
        {
            if (ReferenceEquals(_observed, _session.EditorMovie)) return;
            _observed = _session.EditorMovie; _revision++;
            if (!preserveUndo) _undo = null;
        }
        private void HoldBackground()
        {
            if (!_backgroundHeld) { _background = Application.runInBackground; _backgroundHeld = true; }
            Application.runInBackground = true;
        }
        private void ReleaseBackground() { if (_backgroundHeld) Application.runInBackground = _background; _backgroundHeld = false; }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; _window.Dispose(); Channel.IsOpen = false; Channel.Clear();
            ReleaseBackground();
            // The window thread destroys only its own HWND and GDI resources.
        }
    }
}
