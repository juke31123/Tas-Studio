using System;
using System.Globalization;
using System.Linq;
using Igtap.Tas.Core;
using Igtap.Tas.DesktopModel;
using UnityEngine;
using Igtap.Tas.UI;

namespace Igtap.Tas.Runtime
{
    // Main-thread controller for native UI commands. Only the dedicated search
    // controller launches the external computational worker.
    internal sealed class DesktopBridge : IDisposable
    {
        private readonly TasSession _session;
        internal readonly StudioChannel Channel = new StudioChannel();
        private readonly IStudioWindow _window;
        private Command _pending;
        private readonly EditHistory _history = new EditHistory();
        private int _revision, _completedSequence;
        private Movie _observed;
        private Movie _timelineMovie;
        private int _timelinePage;
        private FrameRow[] _timeline=Array.Empty<FrameRow>();
        private string _ack = "", _error = "";
        private bool _attached, _backgroundHeld, _background, _disposed;
        private double _nextSample, _nextFiles;
        private string[] _movies = Array.Empty<string>(), _states = Array.Empty<string>();
        private bool _recoverable;
        public bool Connected => Channel.IsOpen;
        public bool SampleDue => Connected && Time.realtimeSinceStartupAsDouble >= _nextSample;
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
                        if (!_session.TryQueue(() => { _session.Audit.Cancel(); if (_session.Search.Busy || _session.SearchPreview) _session.Search.DiscardPreview(); else _session.TogglePause(); })) return;
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
                case "trajectory":
                    if (_session.Trajectory == null) throw new InvalidOperationException("The trajectory preview is unavailable.");
                    _session.Trajectory.Configure(c.Authored, c.Count); break;
                case "trajectoryRefresh":
                    if (!_session.Active || !_session.Frozen) throw new InvalidOperationException("Pause the TAS before refreshing its trajectory snapshot.");
                    _session.Trajectory?.Refresh(); break;
                case "searchOpen": _session.Search.Open(); break;
                case "searchMap": _session.Search.CaptureMap(); break;
                case "searchCheckpoints": _session.Search.SetCheckpoints(c.SearchOptions?.Checkpoints??throw new ArgumentException("Missing checkpoints.")); break;
                case "searchCheckpointPlayer": var point=_session.Adapter.SearchWorldPosition();_session.Search.AppendCheckpoint(point.X,point.Y,c.Value);break;
                case "searchStart": _session.Search.Start(c.SearchOptions ?? throw new ArgumentException("Missing search options.")); break;
                case "searchCancel": _session.Search.DiscardPreview(); break;
                case "searchVerify": _session.Search.Verify(); break;
                case "searchAccept":
                    var baseline = _session.Search.Accept();
                    _history.Push(InputEdit.Between(baseline, _session.EditorMovie)); ObserveMovie(true); break;
                case "pause": RequireTransport(); _session.TogglePause(); break;
                case "step": RequireTransport(); _session.Step(); break;
                case "stepPu": RequireTransport(); _session.StepPu(); break;
                case "stepInput":
                    RequireTransport();
                    if (_session.Mode == SessionMode.Recording) { _session.AuthoredInput = Input(c); _session.UseAuthoredInput = c.Authored; }
                    _session.Step(); break;
                case "stop": _session.Audit.Cancel(); if (_session.Active) _session.Stop(); break;
                case "verifyReplay": _session.Audit.Start(); break;
                case "save": _session.FileName = MovieStore.NormalizeName(c.Name); _session.Save(); break;
                case "load": _session.LoadNamed(c.Name); _history.Clear(); break;
                case "recoverDraft": _session.RecoverDraft(); _history.Clear(); ObserveMovie(); break;
                case "saveProfile": _session.ExportProfile(); break;
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
                    _session.StartConfiguredRecording(setup); _history.Clear(); break;
                case "speed":
                    if (_session.Audit.Running) throw new InvalidOperationException("Stop replay verification before changing its speed.");
                    if (!PlaybackSpeed.TryParse(c.Speed, out var speed)) throw new ArgumentException(PlaybackSpeed.Error);
                    _session.SetSpeed(speed); break;
                case "timelineInput": _session.UseTimelineInput = c.Authored; if (!c.Authored) _session.UseAuthoredInput = false; break;
                case "paint": case "insertRows": case "deleteRows": case "clearRows":
                    if (c.Revision != _revision) throw new InvalidOperationException("The timeline changed. Try the edit again.");
                    if (!_session.CanEditTimeline) throw new InvalidOperationException("Pause the run before editing frames.");
                    var edited = _session.EditorMovie.CopyForEdit();
                    if (c.Action == "paint") FrameCellEdit.Paint(edited, c.Start, c.Count, (InputColumn)c.Column, c.CellHeld, c.Value);
                    else if (c.Action == "insertRows") Timeline.Insert(edited, c.Start, c.Count, default);
                    else if (c.Action == "deleteRows") Timeline.Delete(edited, c.Start, c.Count);
                    else Timeline.Replace(edited, c.Start, c.Count, default);
                    edited.Validate();
                    var change = InputEdit.Between(_session.EditorMovie, edited);
                    if (change != null) { _session.StageTimelineEdit(edited, change.Start); _history.Push(change); ObserveMovie(true); }
                    break;
                case "saveEdits": _session.SaveTimelineEdits(); ObserveMovie(true); break;
                case "discardEdits": _session.DiscardTimelineEdits(); _history.Clear(); ObserveMovie(); break;
                case "input": _session.AuthoredInput = Input(c); _session.UseAuthoredInput = c.Authored; break;
                case "saveState": _session.SaveState(c.Name); break;
                case "loadState":
                    if (!_session.CanSaveState) throw new InvalidOperationException("Pause recording before loading a state.");
                    _session.LoadState(c.Name); break;
                case "replace": case "insert": case "delete":
                    if (!_session.CanEdit) throw new InvalidOperationException("Stop the run before editing the timeline.");
                    var candidate = _session.Movie.CopyForEdit();
                    if (c.Action == "replace") Timeline.Replace(candidate, c.Start, c.Count, Input(c));
                    else if (c.Action == "insert") Timeline.Insert(candidate, c.Start, c.Count, Input(c));
                    else Timeline.Delete(candidate, c.Start, c.Count);
                    candidate.Validate();
                    var legacyEdit = InputEdit.Between(_session.Movie, candidate);
                    if (legacyEdit != null) { _session.StageTimelineEdit(candidate, legacyEdit.Start); _history.Push(legacyEdit); ObserveMovie(true); }
                    break;
                case "undo": case "redo":
                    if (!_session.CanEditTimeline) throw new InvalidOperationException("Pause the run before undo/redo.");
                    bool redo = c.Action == "redo";
                    var historyEdit = _history.Peek(redo);
                    _session.StageTimelineEdit(historyEdit.Apply(_session.EditorMovie, !redo), historyEdit.Start);
                    _history.Commit(redo); ObserveMovie(true); break;
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
            return new FrameInput { X = c.X, Y = c.Y, Held = (c.Jump ? Buttons.Jump : 0) | (c.Dash ? Buttons.Dash : 0) | (c.Reset ? Buttons.Reset : 0) | (c.Menu ? Buttons.Menu : 0) };
        }
        public void Publish(PlayerStatsMonitor stats)
        {
            if (!SampleDue) return;
            using var timing = _session.Profiler.Measure(ProfileSection.DesktopSnapshot);
            _nextSample = Time.realtimeSinceStartupAsDouble + .1;
            try
            {
                ObserveMovie();
                if (Time.realtimeSinceStartupAsDouble >= _nextFiles)
                {
                    _movies = _session.Store.ListMovies().Take(500).Select(f => f.Name).ToArray();
                    _states = _session.States.List().Take(500).Select(f => f.Name).ToArray();
                    _recoverable = _session.Journal.Available;
                    _nextFiles = Time.realtimeSinceStartupAsDouble + 2;
                }
                var editorMovie = _session.EditorMovie;
                int page = Math.Max(0, Math.Min(Channel.Page, editorMovie.Frames.Count));
                var snapshot = new Snapshot {
                    Bindings = _session.Bindings.Copy(),
                    Mode = _session.Mode.ToString(), Status = _session.Status, Name = _session.FileName, Ack = _ack, Error = _error,
                    LoadedMap=_session.Adapter.LoadedMapLabel,LoadedMapKey=_session.Adapter.LoadedMapKey,
                    Revision = _revision, CompletedSequence = _completedSequence, CanEditTimeline = _session.CanEditTimeline, TimelineInput = _session.UseTimelineInput, PendingEdits = _session.HasPendingEdits,
                    UndoCount = _history.UndoCount, RedoCount = _history.RedoCount, CanRecoverDraft = !_session.Active && _recoverable,
                    Cursor = _session.Cursor, Frames = editorMovie.Frames.Count, Fps = _session.Movie.FrameRate,
                    FramesUntilPu = _session.FramesUntilPu, Pus = _session.PuCount,
                    HasPerformanceSample = _session.Performance.HasSample, ActualFps = _session.Performance.ActualFps,
                    AchievedSpeed = _session.Performance.AchievedSpeed, PerformanceError = _session.Performance.Error.Length > 0 ? _session.Performance.Error : _session.Profiler.Error,
                    Rerecords = _session.Movie.Rerecords, Speed = _session.Speed, Active = _session.Active, Paused = _session.Frozen,
                    MainMenu = _session.IsMainMenu, CanEdit = _session.CanEdit, CanState = _session.CanSaveState && !_session.HasPendingEdits,
                    CanTransport = !_session.Search.Busy && !_session.SearchPreview && !_session.Audit.Running && !_session.HasPendingEdits && _session.Active && (_session.Mode == SessionMode.Recording || _session.Mode == SessionMode.Playback),
                    AuditRunning = _session.Audit.Running, AuditResult = _session.Audit.Result,
                    Dirty = _session.Dirty, Authored = _session.UseAuthoredInput, Movies = _movies, States = _states, Page = page,
                    SelectedInput = new FrameRow { X = _session.AuthoredInput.X, Y = _session.AuthoredInput.Y,
                        Jump = _session.AuthoredInput.Down(Buttons.Jump), Dash = _session.AuthoredInput.Down(Buttons.Dash), Reset = _session.AuthoredInput.Down(Buttons.Reset), Menu = _session.AuthoredInput.Down(Buttons.Menu) },
                    Timeline = TimelinePage(editorMovie,page),
                    Prediction = stats.Prediction?.Explanation ?? stats.Message,
                    TrajectoryEnabled = _session.Trajectory?.Enabled == true,
                    TrajectoryFrames = _session.Trajectory?.Horizon ?? 280,
                    TrajectoryStatus = _session.Trajectory?.Status ?? "Trajectory preview unavailable.",
                    Search = new SearchView { Options = _session.Search.Options.Copy(), Result = _session.Search.Result.Copy(),
                        Geometry=_session.Search.MapGeometry,MapRevision=_session.Search.MapRevision,Player=_session.Search.MapPlayer,
                        CapturedMapKey=_session.Search.MapKey,
                        Busy = _session.Search.Busy,
                        CanStart = _session.Search.CanStart, CanAccept = _session.Search.CanAccept, Preview = _session.SearchPreview },
                    InputSource = stats.InputSource
                };
                var current = stats.Current; var next = stats.Prediction?.Next;
                if (current != null) snapshot.Stats = new[] {
                    Row("Position X", current.Position.X, next?.Position.X), Row("Position Y", current.Position.Y, next?.Position.Y),
                    Row("Velocity X", current.Velocity.X, next?.Velocity.X), Row("Velocity Y", current.Velocity.Y, next?.Velocity.Y),
                    Row("Speed", current.Velocity.Magnitude, next?.Velocity.Magnitude),
                    new StatRow { Name = "State", Current = current.State.ToString(), Next = next?.State.ToString() ?? "--" },
                    new StatRow { Name = "Can jump", Current = stats.Actions?.JumpText ?? "--", Next = stats.Prediction?.Actions?.JumpText ?? "--" },
                    new StatRow { Name = "Can dash", Current = stats.Actions?.DashText ?? "--", Next = stats.Prediction?.Actions?.DashText ?? "--" },
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
        private FrameRow[] TimelinePage(Movie movie,int page)
        {
            int count=Math.Min(200,movie.Frames.Count-page);
            bool reuse=ReferenceEquals(movie,_timelineMovie) && page==_timelinePage && count==_timeline.Length;
            var rows=reuse?_timeline:new FrameRow[count];bool copied=!reuse;
            for(int i=0;i<rows.Length;i++)
            {
                var f=movie.Frames[page+i];
                int pus=f.FixedTicks>=0?f.FixedTicks:f.PreviousFixedTicks;
                bool pending=f.FixedTicks<0 && f.PreviousFixedTicks>=0;
                bool jump=f.Input.Down(Buttons.Jump|Buttons.Jump2),dash=f.Input.Down(Buttons.Dash|Buttons.Dash2),reset=f.Input.Down(Buttons.Reset),menu=f.Input.Down(Buttons.Menu);
                var old=rows[i];
                if(old!=null && old.Frame==page+i && old.Pus==pus && old.PuTimingPending==pending && old.X==f.Input.X && old.Y==f.Input.Y &&
                    old.Jump==jump && old.Dash==dash && old.Reset==reset && old.Menu==menu && old.Verified==(f.Expected!=null))continue;
                // Published rows are immutable. Updating one observed PU/input
                // must not modify an older snapshot being painted by the UI.
                if(!copied){rows=(FrameRow[])rows.Clone();copied=true;}
                rows[i]=new FrameRow {Frame=page+i,Pus=f.FixedTicks>=0?f.FixedTicks:f.PreviousFixedTicks,
                    PuTimingPending=pending,X=f.Input.X,Y=f.Input.Y,Jump=jump,Dash=dash,Reset=reset,Menu=menu,Verified=f.Expected!=null};
            }
            _timelineMovie=movie;_timelinePage=page;_timeline=rows;return rows;
        }
        private void ObserveMovie(bool preserveUndo = false)
        {
            if (ReferenceEquals(_observed, _session.EditorMovie)) return;
            _observed = _session.EditorMovie; _revision++;
            if (!preserveUndo) _history.Clear();
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
