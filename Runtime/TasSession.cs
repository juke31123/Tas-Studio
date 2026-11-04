using System;
using System.Collections.Generic;
using Igtap.Tas.Core;
using Recharge.ModApi;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace Igtap.Tas.Runtime
{
    internal enum SessionMode { Idle, Loading, Stopping, Warmup, Recording, Playback, Complete, Desync }

    internal sealed class TasSession : IDisposable
    {
        private readonly IRechargeHost _host;
        private ClockLease _clock;
        private Action _command;
        private bool _sceneReady, _playing, _step, _pauseAtEnd, _resumeAtEnd, _frameOpen;
        private int _warmup, _fixedTicks, _logicalSteps, _loadWait, _seek = -1;
        private bool _seekBranch;
        private bool _readyAtEnd;
        private float _bootstrapScale = 1;
        private readonly List<object> _bootstrapTrace = new List<object>();
        private object _recordingTrace;
        private float _speed = 1;
        private readonly HashSet<object> _zoneLoads = new HashSet<object>();
        private bool _playerStarted, _zoneLoadSeen;
        private int _loadSettledFrames;
        private bool _returnToMenu;
        private string _stopScene, _setupError;
        private Savestate _restoringState;
        public bool RestoringState => _restoringState != null;
        public int DesktopPageRequest = -1;
        internal float PreviewTimeScale => _clock?.SimulationScale ?? Time.timeScale;
        public bool CanSaveState => Active && Frozen && Mode == SessionMode.Recording && !_resumeAtEnd && !_frameOpen && !HasPendingEdits;
        public bool IsMainMenu => Adapter.OnMainMenu;
        internal bool SuppressSavedProgressLoad => Active && Movie.StartSetup != null && Mode != SessionMode.Stopping;
        internal void NotifySetupError(string message) { _setupError = message; }
        internal bool WaitingForLoad => Active && (Mode == SessionMode.Loading || Mode == SessionMode.Stopping);
        private FrameInput _current, _previous;
        public IgtapAdapter Adapter { get; }
        internal SimulationTimers MovementTimers { get; } = new SimulationTimers();
        public MovieStore Store { get; }
        public SavestateStore States { get; }
        public Movie Movie { get; private set; } = new Movie();
        public SessionMode Mode { get; private set; }
        public bool Active => _clock != null;
        public bool Frozen { get; private set; }
        public int Cursor { get; private set; }
        public string Status { get; private set; } = "Ready. Create a new Tas or load a saved run.";
        public string FileName = "my-run";
        public bool UseAuthoredInput;
        public bool UseTimelineInput;
        public Movie PendingTimeline { get; private set; }
        private int _pendingFirst;
        public bool HasPendingEdits => PendingTimeline != null;
        public Movie EditorMovie => PendingTimeline ?? Movie;
        public void StageTimelineEdit(Movie movie, int firstChanged)
        {
            if (!CanEditTimeline) throw new InvalidOperationException("Pause the run before editing frames.");
            movie.Validate();
            _pendingFirst = HasPendingEdits ? Math.Min(_pendingFirst, firstChanged) : firstChanged;
            PendingTimeline = movie;
            Status = "Unsaved timeline edits. Save edits to apply them, or Discard to keep the current run.";
        }
        public void SaveTimelineEdits()
        {
            if (!HasPendingEdits) return;
            if (!CanEditTimeline) throw new InvalidOperationException("Pause the run before saving edits.");
            var pending = PendingTimeline; var original = Movie; int first = _pendingFirst;
            PendingTimeline = null;
            try { ApplyTimelineEdit(pending, first); Save(); }
            catch { if (ReferenceEquals(Movie, original)) PendingTimeline = pending; throw; }
        }
        public void DiscardTimelineEdits() { PendingTimeline = null; Status = "Pending timeline edits discarded."; }
        private void RequireNoPendingEdits()
        {
            if (HasPendingEdits) throw new InvalidOperationException("Save edits or Discard in the Frame Editor first.");
        }
        private bool _rebuildingEdit, _editReturnRecording;
        public bool CanEditTimeline => !Active || (Frozen && !_frameOpen && !_resumeAtEnd && _restoringState == null &&
            (Mode == SessionMode.Recording || Mode == SessionMode.Playback || Mode == SessionMode.Complete));
        public bool StartPaused = true;
        internal bool DiagnosticNeutralInput;
        public FrameInput AuthoredInput;
        public FrameInput CurrentInput => _current;
        public FrameInput PreviousInput => _previous;
        public float StepSeconds => 1f / Movie.FrameRate;
        public double LogicalTime => _logicalSteps * (double)StepSeconds;
        public bool CanEdit => !Active;
        public bool Dirty { get; private set; }
        public float Speed => _speed;

        public TasSession(IRechargeHost host, string modId)
        {
            _host = host; Adapter = new IgtapAdapter();
            Store = new MovieStore(System.IO.Path.Combine(host.ModDataDir(modId), "movies"));
            States = new SavestateStore(System.IO.Path.Combine(host.ModDataDir(modId), "states"));
            SceneManager.sceneLoaded += SceneLoaded;
        }
        // UI requests are serialized at the loop boundary, never from OnGUI mid-frame.
        public void Queue(Action command)
        {
            if (!TryQueue(command)) Status = "A command is already pending.";
        }
        internal bool TryQueue(Action command)
        {
            if (_command != null) return false;
            _command = command; return true;
        }
        private void SceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!Active) return;
            if ((Mode != SessionMode.Loading && Mode != SessionMode.Stopping) || scene.path != (Mode == SessionMode.Stopping ? _stopScene : Movie.Scene))
            {
                Fail("Scene changed during the movie. Cross-scene movies are not supported yet."); return;
            }
            _sceneReady = true;
            _clock.HoldLoading();
            TraceBootstrap("scene-loaded");
        }
        public void NotifyPlayerStarted()
        {
            if (!WaitingForLoad || !_sceneReady) return;
            _playerStarted = true;
            // The game's Start writes its own timeScale. Remember it, then keep
            // the loading barrier closed until a whole-frame boundary is ready.
            _bootstrapScale = Time.timeScale > 0 ? Time.timeScale : 1;
            _clock.HoldLoading();
            TraceBootstrap("movement-started");
        }
        internal void NotifyZoneLoadStarted(object iterator)
        {
            if (!WaitingForLoad || !_sceneReady) return;
            _zoneLoadSeen = true;
            if (_zoneLoads.Add(iterator))
            {
                _loadSettledFrames = 0; _readyAtEnd = false;
                TraceBootstrap("zone-load-started");
            }
        }
        internal void NotifyZoneLoadFinished(object iterator)
        {
            if (_zoneLoads.Remove(iterator)) TraceBootstrap("zone-load-finished");
        }
        private void ResetLoadingBarrier()
        {
            _zoneLoads.Clear(); _playerStarted = _zoneLoadSeen = false;
            _loadSettledFrames = 0; _readyAtEnd = false;
        }
        private bool LoadingReady() => Mode == SessionMode.Stopping && _returnToMenu
            ? _sceneReady && IsMainMenu
            : _sceneReady && _playerStarted && _zoneLoadSeen && _zoneLoads.Count == 0 && Adapter.Bind();
        public void StartRecording()
        {
            RequireIdle();
            var scene = SceneManager.GetActiveScene();
            if (!Adapter.Bind()) throw new InvalidOperationException("Enter a gameplay scene first.");
            if (string.IsNullOrEmpty(scene.path)) throw new InvalidOperationException("The active scene cannot be reloaded by path.");
            if (Dirty && (Movie.StartState != null || Movie.Frames.Count > 0)) Save(); // Never discard actual input data.
            Movie = new Movie {
                Name = FileName, Scene = scene.path, GameHash = EnvironmentFingerprint.Game(),
                Difficulty = Adapter.CurrentDifficulty,
                UnityVersion = Application.unityVersion, ModSetHash = EnvironmentFingerprint.Mods(),
                SaveHash = EnvironmentFingerprint.Saves(), FixedDeltaTime = Time.fixedDeltaTime
            };
            Movie.BootstrapVersion = 3;
            _recordingTrace = null;
            Cursor = 0; Dirty = true; Begin(false);
        }
        public void StartConfiguredRecording(TasStartSetup setup)
        {
            RequireIdle();
            if (!IsMainMenu) throw new InvalidOperationException("Create New Tas is available from the game's main menu.");
            if (setup == null) throw new ArgumentNullException(nameof(setup));
            setup.Validate();
            string scene = Adapter.SceneFor(setup);
            if (!Adapter.CanLoadScene(scene)) throw new InvalidOperationException("The selected gameplay scene is unavailable.");
            FileName = MovieStore.NormalizeName(FileName);
            if (Dirty && (Movie.StartState != null || Movie.Frames.Count > 0)) Save();
            Movie = new Movie {
                Name = FileName, Scene = scene, Difficulty = setup.Difficulty, StartSetup = setup.Clone(),
                GameHash = EnvironmentFingerprint.Game(), ModSetHash = EnvironmentFingerprint.Mods(),
                UnityVersion = Application.unityVersion, BootstrapVersion = 3, FixedDeltaTime = .02f
            };
            _recordingTrace = null; Cursor = 0; Dirty = true;
            Begin(false);
        }
        public void StartPlayback(int seek = -1, bool branch = false)
        {
            RequireIdle(); ValidatePlayback(Movie, seek);
            _seek = seek; _seekBranch = branch; Begin(true);
        }
        private void ValidatePlayback(Movie movie, int seek)
        {
            movie.Validate();
            if (movie.WarmupFixedTicks.Count != movie.WarmupFrames) throw new InvalidOperationException("The movie has no complete startup tick schedule.");
            if ((movie.Frames.Count == 0 && seek != 0) || movie.StartState == null) throw new InvalidOperationException("Record or load a movie with a starting state first.");
            if (seek < -1 || seek > movie.Frames.Count) throw new ArgumentOutOfRangeException(nameof(seek));
            if (EnvironmentFingerprint.Game() != movie.GameHash) throw new InvalidOperationException("Game assembly differs from this movie.");
            if (Application.unityVersion != movie.UnityVersion) throw new InvalidOperationException("Unity version differs from this movie.");
            if (movie.ModSetHash != EnvironmentFingerprint.Mods())
                throw new InvalidOperationException("Incompatible TAS run: recorded with different mod files. Record a new run with the installed version.");
            if (movie.StartSetup == null && EnvironmentFingerprint.Saves() != movie.SaveHash) throw new InvalidOperationException("Saved progress differs. Restore the original save externally before playback.");
            if (movie.StartSetup != null && Adapter.SceneFor(movie.StartSetup) != movie.Scene) throw new InvalidOperationException("The selected start does not match the movie scene.");
            if (!IsMainMenu && SceneManager.GetActiveScene().path != movie.Scene) throw new InvalidOperationException("Enter this movie's scene or the main menu first: " + movie.Scene);
            if (!Adapter.CanLoadScene(movie.Scene)) throw new InvalidOperationException("The movie's gameplay scene is unavailable.");
        }
        private WorldWitness CaptureWorld()
        {
            var witness = Adapter.CaptureWorld();
            MovementTimers.Capture(witness);
            witness.Numbers["session/scale"] = _clock.SimulationScale;
            witness.Numbers["session/logicalTime"] = LogicalTime;
            witness.Validate(); return witness;
        }
        public void SaveState(string label)
        {
            RequireNoPendingEdits();
            if (!CanSaveState) throw new InvalidOperationException("Pause recording at a complete frame before saving a state.");
            var state = new Savestate {
                TasName = MovieStore.NormalizeName(FileName), Label = MovieStore.NormalizeName(label), Frame = Cursor,
                Movie = MovieStore.Clone(Movie), World = CaptureWorld()
            };
            // Planned future rows are not part of the current world checkpoint.
            state.Movie.Frames.RemoveRange(Cursor, state.Movie.Frames.Count - Cursor);
            string name = States.Save(state);
            Status = "Saved " + name + ".state.json at frame " + Cursor + ".";
        }
        public void LoadState(string name)
        {
            RequireNoPendingEdits();
            if (Active && !CanSaveState) throw new InvalidOperationException("Pause recording before loading a savestate.");
            // Complete every validation and backup before leaving the live state.
            var state = States.Load(name);
            ValidatePlayback(state.Movie, state.Frame);
            if (Movie.StartState != null || Movie.Frames.Count > 0)
                Store.Save("before-state-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8), Movie);
            int rerecords = Math.Max(Movie.Rerecords, state.Movie.Rerecords);
            Release();
            Movie = state.Movie; FileName = state.TasName; Dirty = false;
            Movie.Rerecords = rerecords;
            _recordingTrace = null; _restoringState = state;
            _seek = state.Frame; _seekBranch = false;
            Begin(true);
            Status = "Rebuilding savestate at frame " + state.Frame + ". F8 cancels. Recording resumes paused after verification.";
        }
        private void Begin(bool playing)
        {
            _returnToMenu = IsMainMenu || Movie.StartSetup != null;
            _stopScene = _returnToMenu ? Adapter.MainMenuScene : Movie.Scene;
            _setupError = null;
            _playing = playing; Cursor = 0; _logicalSteps = 0; _warmup = 0; _loadWait = 0;
            _current = _previous = default; Frozen = true; _sceneReady = false; _readyAtEnd = false;
            _bootstrapScale = 1; _bootstrapTrace.Clear();
            ResetLoadingBarrier();
            _step = _pauseAtEnd = _resumeAtEnd = _frameOpen = false;
            MovementTimers.Clear();
            _clock = new ClockLease(Movie.FrameRate, Movie.FixedDeltaTime);
            _clock.HoldLoading();
            _clock.SetSpeed(_speed);
            Mode = SessionMode.Loading;
            UnityEngine.Random.InitState(Movie.Seed);
            Status = Movie.StartSetup != null ? "Loading the selected TAS start and abilities." : "Reloading saved scene; unsaved gameplay progress is discarded.";
            try { Adapter.BeginRunEnvironment(Movie.Difficulty, Movie.StartSetup != null); SceneManager.LoadScene(Movie.Scene); }
            catch { Release(); throw; }
        }
        public void Early()
        {
            try
            {
                if (Active)
                {
                    _clock.EnforcePacing();
                    InputSystem.Update(); // exactly one manual input update per rendered frame
                }
                Hotkeys();
                var command = _command; _command = null;
                try { command?.Invoke(); }
                catch (Exception error) { Status = error.Message; _host.LogWarning(error.Message); }
                _frameOpen = false;
                if (!Active) return;
                if (_setupError != null) { var message = _setupError; _setupError = null; throw new InvalidOperationException(message); }
                if (Mode == SessionMode.Loading || Mode == SessionMode.Stopping)
                {
                    _clock.HoldLoading();
                    if (++_loadWait > 1800) throw new InvalidOperationException("Timed out waiting for the scene, player and zone loader to finish.");
                    Status = "Waiting for the game to finish loading. TAS frames are frozen.";
                    if (!LoadingReady()) { _loadSettledFrames = 0; return; }
                    // Newly activated zone objects get complete Start/render passes
                    // before physics synchronization and the first warmup tick.
                    _readyAtEnd = ++_loadSettledFrames >= 2;
                    return;
                }
                if (Frozen) return;
                if (!Adapter.IsAlive) throw new InvalidOperationException("Player was replaced or disabled during the movie.");
                if (Adapter.Player.pauseMenu != null && Adapter.Player.pauseMenu.menuOpen)
                    throw new InvalidOperationException("A game menu opened during the movie.");
                if (Mode == SessionMode.Complete || Mode == SessionMode.Desync) return;
                _previous = _current;
                _current = Mode == SessionMode.Warmup ? default : _playing ? Movie.Frames[Cursor].Input : DiagnosticNeutralInput ? default :
                    UseTimelineInput && Cursor < Movie.Frames.Count ? Movie.Frames[Cursor].Input : UseAuthoredInput ? AuthoredInput : Adapter.ReadPhysicalInput();
                _fixedTicks = 0; _frameOpen = true;
                MovementTimers.Advance(StepSeconds * (double)Time.timeScale);
            }
            catch (Exception error) { HandleError(error); }
        }
        public void End()
        {
            try
            {
                if (!Active) return;
                if (_readyAtEnd)
                {
                    _readyAtEnd = false;
                    if (!LoadingReady()) { _loadSettledFrames = 0; return; }
                    if (Mode == SessionMode.Stopping)
                    {
                        float scale = _bootstrapScale;
                        Release(); Time.timeScale = scale;
                        Status = _returnToMenu ? "Stopped and saved; returned to the main menu." : "Stopped and saved; original saved scene finished reloading.";
                        return;
                    }
                    Adapter.FinishWorldSetup(Movie.StartSetup);
                    Adapter.PrepareSimulation();
                    TraceBootstrap("before-first-warmup-frame");
                    _clock.FinishLoading(_bootstrapScale);
                    Frozen = false; Mode = SessionMode.Warmup;
                    Status = "Game loaded. Preparing the deterministic starting state.";
                    return;
                }
                if (_frameOpen)
                {
                    _logicalSteps++;
                    if (Mode == SessionMode.Warmup)
                    {
                        TraceBootstrap("warmup-" + _warmup);
                        if (!_playing) Movie.WarmupFixedTicks.Add(_fixedTicks);
                        else if (Movie.WarmupFixedTicks[_warmup] != _fixedTicks)
                        {
                            Desync("Startup physics schedule differs at warmup " + _warmup + ": expected " + Movie.WarmupFixedTicks[_warmup] + ", actual " + _fixedTicks);
                            return;
                        }
                        if (++_warmup >= Movie.WarmupFrames)
                        {
                            var start = Adapter.Probe();
                            if (_playing)
                            {
                                var difference = ProbeComparer.Difference(Movie.StartState, start);
                                if (difference != null) { Desync("Starting state: " + difference); return; }
                            }
                            else Movie.StartState = start;
                            if (!_playing) _recordingTrace = _bootstrapTrace.ToArray();
                            WriteBootstrapReport();
                            Mode = _playing ? SessionMode.Playback : SessionMode.Recording;
                            Status = _playing ? "Playing input movie." : "Recording input movie.";
                            if (_seek == 0) FinishSeek();
                            else if (!_playing && StartPaused) PauseNow();
                        }
                    }
                    else if (Mode == SessionMode.Recording || Mode == SessionMode.Playback)
                    {
                        var state = Adapter.Probe();
                        if (_playing)
                        {
                            var frame = Movie.Frames[Cursor];
                            var difference = ProbeComparer.Difference(frame.Expected, state);
                            if (difference == null && frame.FixedTicks >= 0 && frame.FixedTicks != _fixedTicks) difference = "Physics tick schedule differs";
                            if (difference != null) { Desync("Frame " + Cursor + ": " + difference); return; }
                            // A recording rebuild observes the changed prefix afresh.
                            // These become recording references, not independent proof.
                            if (_rebuildingEdit && _editReturnRecording && frame.Expected == null)
                            { frame.Expected = state; frame.FixedTicks = _fixedTicks; }
                            // Validate edits by replaying them, but don't label new probes as independent proof.
                        }
                        else
                        {
                            if (Cursor >= Core.Movie.MaxFrames) throw new InvalidOperationException("Maximum movie length reached; save the recording.");
                            var recorded = new MovieFrame { Input = _current, Expected = state, FixedTicks = _fixedTicks };
                            if (Cursor < Movie.Frames.Count)
                            {
                                if (!UseTimelineInput) Timeline.Invalidate(Movie, Cursor);
                                Movie.Frames[Cursor] = recorded;
                            }
                            else Movie.Frames.Add(recorded);
                            Dirty = true;
                        }
                        Cursor++;
                        if (_seek >= 0 && Cursor == _seek) FinishSeek();
                        else if (_playing && Cursor == Movie.Frames.Count)
                        {
                            Mode = SessionMode.Complete; PauseNow();
                            Status = "Playback complete. " + (Movie.Frames.Exists(f => f.Expected == null) ? "Edited frames had no reference probes." : "All recorded probes matched.");
                        }
                    }
                }
                _frameOpen = false;
                if (_pauseAtEnd || _step)
                {
                    _pauseAtEnd = _step = false; PauseNow();
                }
                else if (_resumeAtEnd)
                {
                    _resumeAtEnd = false; Frozen = false; _clock.Resume();
                    // Step requests arm one NEXT complete frame, never the remainder of this one.
                    _step = _resumeSingle; _resumeSingle = false;
                }
            }
            catch (Exception error) { HandleError(error); }
            finally { _clock?.EnforcePacing(); }
        }
        private bool _resumeSingle;
        public void TogglePause()
        {
            RequireRunning();
            if (Frozen) _resumeAtEnd = true; else _pauseAtEnd = true;
        }
        public void Step()
        {
            RequireRunning();
            if (!Frozen) { _pauseAtEnd = true; return; }
            _resumeSingle = true; _resumeAtEnd = true;
        }
        private void PauseNow() { Frozen = true; _clock?.Pause(); }
        private void FinishSeek()
        {
            _seek = -1;
            if (_rebuildingEdit)
            {
                _rebuildingEdit = false; _playing = !_editReturnRecording;
                Mode = _editReturnRecording ? SessionMode.Recording : Cursor < Movie.Frames.Count ? SessionMode.Playback : SessionMode.Complete;
                Dirty = true; PauseNow(); Status = "Edited timeline rebuilt to frame " + Cursor + ". Paused; future inputs are preserved.";
                return;
            }
            if (_restoringState != null)
            {
                var expectedWorld = _restoringState.World;
                var actualWorld = CaptureWorld();
                string difference = expectedWorld.Difference(actualWorld);
                _restoringState = null;
                if (difference != null)
                {
                    try
                    {
                        System.IO.File.WriteAllText(System.IO.Path.Combine(States.DirectoryPath, "last-state-desync.json"),
                            Newtonsoft.Json.JsonConvert.SerializeObject(new { Frame = Cursor, Reason = difference, Expected = expectedWorld, Actual = actualWorld }, Newtonsoft.Json.Formatting.Indented));
                    }
                    catch (Exception error) { _host.LogWarning("Could not write savestate report: " + error.Message); }
                    Desync("Savestate verification: " + difference); return;
                }
                Timeline.Branch(Movie, Cursor); Dirty = true;
                _playing = false; Mode = SessionMode.Recording;
                PauseNow(); Status = "Savestate restored at frame " + Cursor + ". Recording is paused; F6 resumes, F7 steps.";
                return;
            }
            if (_seekBranch)
            {
                // Save the original before truncating so rerecords are recoverable.
                Store.Save(FileName + "-before-branch", Movie);
                Timeline.Branch(Movie, Cursor); Dirty = true;
                _playing = false; Mode = SessionMode.Recording;
            }
            else if (Cursor == Movie.Frames.Count) Mode = SessionMode.Complete;
            PauseNow(); Status = "Reached frame " + Cursor + (_seekBranch ? ". Recording branch is paused." : ". Playback is paused.");
        }
        public void BranchHere()
        {
            RequireNoPendingEdits();
            if (!Active || !Frozen || Mode != SessionMode.Playback) throw new InvalidOperationException("Pause playback at the branch frame first.");
            Store.Save(FileName + "-before-branch", Movie);
            Timeline.Branch(Movie, Cursor); _playing = false; Mode = SessionMode.Recording; Dirty = true;
            Status = "Rerecording from frame " + Cursor + ".";
        }
        public void Stop()
        {
            RequireNoPendingEdits();
            if (!Active || Mode == SessionMode.Stopping) return;
            _rebuildingEdit = false;
            _restoringState = null;
            if (Dirty && (Movie.StartState != null || Movie.Frames.Count > 0)) Save();
            else Dirty = false;
            string scene = _stopScene;
            // LoadScene returns before the new scene starts. Keep save/physics
            // guards until the replacement player has actually initialized.
            Mode = SessionMode.Stopping; Frozen = true; _clock.HoldLoading();
            MovementTimers.Clear();
            Adapter.ReleaseSimulation();
            _sceneReady = false; _readyAtEnd = false; _frameOpen = false; _loadWait = 0;
            ResetLoadingBarrier();
            Adapter.RestoreStartEnvironment();
            SceneManager.LoadScene(scene);
            Status = "Stopping; waiting for the saved scene to finish reloading.";
        }
        public void Save()
        {
            RequireNoPendingEdits();
            if (Movie.StartState == null && Movie.Frames.Count == 0) throw new InvalidOperationException("There is no recording to save yet. Select a saved run below to load it.");
            FileName = MovieStore.NormalizeName(FileName); Movie.Name = FileName;
            Store.Save(FileName, Movie); Dirty = false; Status = "Saved " + FileName + ".tas.json";
        }
        public void Load() => LoadNamed(FileName);
        public void LoadNamed(string name)
        {
            // Load the selected file before changing anything. A failed import
            // leaves the current movie and filename untouched.
            RequireIdle();
            var loaded = Store.Load(name);
            if (Dirty && (Movie.StartState != null || Movie.Frames.Count > 0))
                Store.Save("autosave-before-load-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"), Movie);
            Movie = loaded; FileName = MovieStore.NormalizeName(name); Cursor = 0; _recordingTrace = null; Dirty = false;
            Status = "Loaded " + FileName + ": " + Movie.Frames.Count + " frames." +
                (Movie.ModSetHash != EnvironmentFingerprint.Mods()
                    ? " Incompatible TAS run: recorded with different mod files. Record a new run with the installed version." : "");
        }
        public void Edited() { RequireIdle(); Movie.Validate(); Dirty = true; Cursor = 0; }
        public void RestoreEdit(Movie movie) { RequireIdle(); movie.Validate(); Movie = movie; Dirty = true; Cursor = 0; }
        public void ApplyTimelineEdit(Movie movie, int firstChanged)
        {
            if (!CanEditTimeline) throw new InvalidOperationException("Pause recording or replay before editing frames.");
            movie.Validate();
            if (!Active) { RestoreEdit(movie); UseTimelineInput = true; return; }
            int target = Math.Min(Cursor, movie.Frames.Count);
            bool recording = Mode == SessionMode.Recording;
            if (firstChanged < Cursor)
            {
                ValidatePlayback(movie, target);
                Store.Save("before-timeline-edit-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8), Movie);
                movie.Rerecords = Math.Max(movie.Rerecords, Movie.Rerecords) + 1;
                Release(); Movie = movie; Dirty = true; UseTimelineInput = true;
                _seek = target; _seekBranch = false; _rebuildingEdit = true; _editReturnRecording = recording;
                Begin(true); Status = "Rebuilding edited timeline to frame " + target + ".";
            }
            else
            {
                Movie = movie; Dirty = true; UseTimelineInput = true;
                if (Mode == SessionMode.Complete && Cursor < Movie.Frames.Count) Mode = SessionMode.Playback;
                Status = "Future frame inputs updated.";
            }
        }
        public void SetSpeed(float speed)
        {
            PlaybackSpeed.Validate(speed);
            _speed = speed; _clock?.SetSpeed(speed);
        }
        public void CountFixedTick() { if (_frameOpen && !Frozen) _fixedTicks++; }
        private void Desync(string reason)
        {
            _restoringState = null;
            var expected = Mode == SessionMode.Warmup ? Movie.StartState : Cursor < Movie.Frames.Count ? Movie.Frames[Cursor].Expected : null;
            Mode = SessionMode.Desync; PauseNow(); Status = "DESYNC: " + reason; _host.LogWarning(Status);
            try
            {
                var report = new {
                    Reason = reason, Frame = Cursor, Movie = FileName, GameHash = Movie.GameHash,
                    StartSetup = Movie.StartSetup, Difficulty = Movie.Difficulty, Scene = Movie.Scene,
                    Expected = expected, Actual = Adapter.Probe(), CurrentInput = _current, PreviousInput = _previous,
                    ActualFixedTicks = _fixedTicks,
                    BootstrapVersion = Movie.BootstrapVersion, WarmupFramesCompleted = _warmup,
                    RecordingBootstrap = _recordingTrace, CurrentBootstrap = _bootstrapTrace,
                    ActualDetails = Adapter.Diagnostics(),
                    ExpectedFixedTicks = Cursor < Movie.Frames.Count ? Movie.Frames[Cursor].FixedTicks : -1
                };
                System.IO.File.WriteAllText(System.IO.Path.Combine(Store.DirectoryPath, "last-desync.json"),
                    Newtonsoft.Json.JsonConvert.SerializeObject(report, Newtonsoft.Json.Formatting.Indented));
            }
            catch (Exception error) { _host.LogWarning("Could not write desync report: " + error.Message); }
        }
        private void TraceBootstrap(string phase)
        {
            if (_bootstrapTrace.Count >= 620) return;
            _bootstrapTrace.Add(new {
                Phase = phase, EngineFrame = Time.frameCount, Mode = Mode.ToString(),
                TimeScale = Time.timeScale, DeltaTime = Time.deltaTime, FixedDeltaTime = Time.fixedDeltaTime,
                FixedTicks = _fixedTicks, Physics = Physics2D.simulationMode.ToString(),
                Player = Adapter.Diagnostics()
            });
        }
        private void WriteBootstrapReport()
        {
            try
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(Store.DirectoryPath, "last-bootstrap.json"),
                    Newtonsoft.Json.JsonConvert.SerializeObject(new { Movie = FileName, Playing = _playing, StartSetup = Movie.StartSetup, Difficulty = Movie.Difficulty, Scene = Movie.Scene, Recording = _recordingTrace, Current = _bootstrapTrace }, Newtonsoft.Json.Formatting.Indented));
            }
            catch (Exception e) { _host.LogWarning("Could not write bootstrap report: " + e.Message); }
        }
        private void Fail(string reason)
        {
            if (Active && Movie.StartSetup != null && Mode != SessionMode.Stopping)
            {
                Stop(); Status = reason + " Returning to the main menu.";
            }
            else { Release(); Status = reason; }
            _host.LogError(reason);
        }
        private void HandleError(Exception error)
        {
            if (Active)
            {
                try { if (Dirty) Store.Save("recovery", Movie); } catch (Exception saveError) { _host.LogError("Recovery save failed: " + saveError.Message); }
                if (Movie.StartSetup != null && Mode != SessionMode.Stopping)
                {
                    Stop(); Status = error.Message + " Returning to the main menu.";
                    _host.LogError(error.ToString()); return;
                }
                Release();
            }
            Status = error.Message; _host.LogError(error.ToString());
        }
        private void RequireIdle() { RequireNoPendingEdits(); if (Active) throw new InvalidOperationException("Stop the active session first."); }
        private void RequireRunning()
        {
            RequireNoPendingEdits();
            if (!Active || (Mode != SessionMode.Recording && Mode != SessionMode.Playback)) throw new InvalidOperationException("Start a recording or playback first.");
        }
        private void Hotkeys()
        {
            var k = Keyboard.current; if (k == null) return;
            if (k.f5Key.wasPressedThisFrame || k.f10Key.wasPressedThisFrame) DesktopPageRequest = 0;
            if (k.f9Key.wasPressedThisFrame) DesktopPageRequest = 3;
            if (k.f8Key.wasPressedThisFrame && Active) Queue(Stop);
            else if (k.f6Key.wasPressedThisFrame && (Mode == SessionMode.Recording || Mode == SessionMode.Playback)) Queue(TogglePause);
            else if (k.f7Key.wasPressedThisFrame && (Mode == SessionMode.Recording || Mode == SessionMode.Playback)) Queue(Step);
        }
        private void Release()
        {
            _rebuildingEdit = false;
            _restoringState = null;
            Adapter.RestoreStartEnvironment();
            MovementTimers.Clear();
            Adapter.ReleaseSimulation();
            _clock?.Dispose(); _clock = null; Frozen = false; Mode = SessionMode.Idle;
            _current = _previous = default; _seek = -1; _frameOpen = false;
            _step = _pauseAtEnd = _resumeAtEnd = _resumeSingle = _readyAtEnd = false;
            ResetLoadingBarrier();
        }
        public void Dispose()
        {
            SceneManager.sceneLoaded -= SceneLoaded;
            try { if (HasPendingEdits) Store.Save("recovery-pending-edits", PendingTimeline); if (Dirty) Store.Save("recovery", Movie); }
            finally { Release(); }
        }
    }
}
