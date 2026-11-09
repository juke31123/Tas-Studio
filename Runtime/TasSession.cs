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
        private bool _stepPu;
        private int _puStepFrames;
        internal bool SteppingPu => _stepPu;
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
        public bool CanSaveState => Active && Frozen && Mode == SessionMode.Recording && !_resumeAtEnd && !_frameOpen && !HasPendingEdits && !SearchPreview;
        public bool IsMainMenu => Adapter.OnMainMenu;
        internal bool SuppressSavedProgressLoad => Active && Movie.StartSetup != null && Mode != SessionMode.Stopping;
        internal void NotifySetupError(string message) { _setupError = message; }
        internal bool WaitingForLoad => Active && (Mode == SessionMode.Loading || Mode == SessionMode.Stopping);
        private FrameInput _current, _previous;
        private bool _menuToggleRequested;
        internal bool FrameOpen => _frameOpen;
        internal event Action PhysicsStarting, PhysicsCompleted, FrameCompleted;
        internal void NotifyPhysicsCompleted() { if(_frameOpen && !Frozen) PhysicsCompleted?.Invoke(); }
        // Mouse Resume must not change timeScale in the middle of an editor pause.
        // Turn it into an input on the next recorded frame instead.
        internal void RequestMenuToggle()
        {
            if (Mode == SessionMode.Recording && !_playing && !_rebuildingEdit && !RestoringState)
                _menuToggleRequested = true;
        }
        public IgtapAdapter Adapter { get; }
        internal SimulationTimers MovementTimers { get; } = new SimulationTimers();
        public MovieStore Store { get; }
        public Keybinds Bindings { get; private set; } = new Keybinds();
        private readonly string _bindingsPath;
        public bool SuppressHotkeys;
        public string KeybindError { get; private set; } = "";
        public void SaveBindings(Keybinds bindings)
        {
            bindings.Save(_bindingsPath);
            Bindings = bindings.Copy(); KeybindError = "";
        }
        public SavestateStore States { get; }
        public PerformanceTrace Performance { get; }
        public FrameProfiler Profiler { get; } = new FrameProfiler();
        public DraftJournal Journal { get; }
        public RebuildCheckpoints Checkpoints { get; } = new RebuildCheckpoints();
        public ReplayAudit Audit { get; }
        public BruteforceController Search { get; }
        private Movie _searchOriginal;
        private bool _searchOriginalDirty, _searchReturnRecording, _searchSeek, _searchMustRebuild;
        public bool SearchPreview => _searchOriginal != null;
        private WorldWitness _worldExpected, _worldActual;
        private readonly string _profileDirectory;
        private string _profilePath;
        internal int LastFramePus => _fixedTicks;
        public void SaveProfile() => Profiler.Save(_profilePath ?? System.IO.Path.Combine(_profileDirectory, "latest-profile.json"));
        public void ExportProfile()
        {
            SaveProfile();
            if (Profiler.Error.Length > 0) throw new System.IO.IOException(Profiler.Error);
            Status = "Saved performance profile to " + (_profilePath ?? System.IO.Path.Combine(_profileDirectory, "latest-profile.json"));
        }
        internal void ReportAudit(string result) { Status = result; }
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
            using (Profiler.Measure(ProfileSection.Journal)) Journal.Append(FileName, EditorMovie, movie);
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
            try { ApplyTimelineEdit(pending, first); Save(); Journal.Resolve(); }
            catch { if (ReferenceEquals(Movie, original)) PendingTimeline = pending; throw; }
        }
        public void DiscardTimelineEdits()
        {
            Journal.Resolve(); PendingTimeline = null;
            if (_searchMustRebuild) { ApplyTimelineEdit(Movie.CopyForEdit(), 0); return; }
            Status = "Pending timeline edits discarded.";
        }
        public void RecoverDraft()
        {
            RequireIdle();
            var recovered = Journal.Recover();
            if (Dirty && (Movie.StartState != null || Movie.Frames.Count > 0)) Save();
            Movie = recovered.CopyForEdit(); FileName = MovieStore.NormalizeName(recovered.Name);
            PendingTimeline = recovered; _pendingFirst = 0; Cursor = 0; Dirty = true;
            Status = "Recovered draft inputs. Save edits to save the recovered movie. Replay remains a separate action.";
        }
        private void RequireNoPendingEdits()
        {
            if (HasPendingEdits) throw new InvalidOperationException("Save edits or Discard in the Frame Editor first.");
        }
        private bool _rebuildingEdit, _editReturnRecording;
        public bool CanEditTimeline => !SearchPreview && Audit?.Running != true && (!Active || (Frozen && !_frameOpen && !_resumeAtEnd && _restoringState == null &&
            (Mode == SessionMode.Recording || Mode == SessionMode.Playback || Mode == SessionMode.Complete)));
        public bool StartPaused = true;
        internal bool DiagnosticNeutralInput;
        public FrameInput AuthoredInput;
        public FrameInput CurrentInput => _current;
        public FrameInput PreviousInput => _previous;
        public float StepSeconds => 1f / Movie.FrameRate;
        public double PuPhase => _clock?.Phase ?? 0;
        public long PuCount => _clock?.Pus ?? 0;
        public int FramesUntilPu => _clock?.FramesUntilPu ?? -1;
        public double LogicalTime => _logicalSteps * (double)StepSeconds;
        public double ScaledNow => _clock?.ScaledNow ?? Time.timeAsDouble;
        public float UnscaledDelta => Frozen ? 0 : Time.inFixedTimeStep ? _clock.FixedUnscaledDelta : StepSeconds;
        public double UnscaledNow => (_logicalSteps + (_frameOpen ? 1 : 0)) / (double)Movie.FrameRate -
            (Time.inFixedTimeStep && !Frozen ? _clock.FixedUnscaledLag : 0);
        public bool CanEdit => !Active;
        public bool Dirty { get; private set; }
        public float Speed => _speed;

        public TasSession(IRechargeHost host, string modId, bool enableSearch = true)
        {
            _host = host; Adapter = new IgtapAdapter();
            _bindingsPath = System.IO.Path.Combine(host.ModDataDir(modId), "keybinds.json");
            try { Bindings = Keybinds.Load(_bindingsPath); }
            catch (Exception e) { KeybindError = "Could not load keybinds; defaults are active. " + e.Message; }
            Store = new MovieStore(System.IO.Path.Combine(host.ModDataDir(modId), "movies"));
            States = new SavestateStore(System.IO.Path.Combine(host.ModDataDir(modId), "states"));
            Performance = new PerformanceTrace(System.IO.Path.Combine(host.ModDataDir(modId), "performance"));
            _profileDirectory = System.IO.Path.Combine(host.ModDataDir(modId), "performance");
            Journal = new DraftJournal(System.IO.Path.Combine(host.ModDataDir(modId), "recovery"));
            Audit = new ReplayAudit(this);
            if(enableSearch) Search = new BruteforceController(this, host.ModDataDir(modId));
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
            Movie.BootstrapVersion = 4;
            _recordingTrace = null;
            Cursor = 0; Dirty = true; Begin(false);
        }
        public void StartConfiguredRecording(TasStartSetup setup)
        {
            RequireIdle();
            if (!IsMainMenu) throw new InvalidOperationException("Create New Tas is available from the game's main menu.");
            if (setup == null) throw new ArgumentNullException(nameof(setup));
            setup.Validate();
            GameFrameRates.RequireAvailable(setup.FrameRate);
            string scene = Adapter.SceneFor(setup);
            if (!Adapter.CanLoadScene(scene)) throw new InvalidOperationException("The selected gameplay scene is unavailable.");
            FileName = MovieStore.NormalizeName(FileName);
            if (Dirty && (Movie.StartState != null || Movie.Frames.Count > 0)) Save();
            Movie = new Movie {
                Name = FileName, Scene = scene, Difficulty = setup.Difficulty, StartSetup = setup.Clone(),
                GameHash = EnvironmentFingerprint.Game(), ModSetHash = EnvironmentFingerprint.Mods(),
                UnityVersion = Application.unityVersion, BootstrapVersion = 4, FixedDeltaTime = .02f,
                FrameRate = setup.FrameRate
            };
            _recordingTrace = null; Cursor = 0; Dirty = true;
            Begin(false);
        }
        public void StartPlayback(int seek = -1, bool branch = false)
        {
            RequireIdle(); ValidatePlayback(Movie, seek);
            _seek = seek; _seekBranch = branch; Begin(true);
        }
        internal void PrepareSearchStart(Movie original, int frame)
        {
            ValidatePlayback(original, frame);
            _searchReturnRecording = Mode == SessionMode.Recording;
            Release(); Movie = original; _seek = frame; _seekBranch = false; _searchSeek = true;
            Begin(true); Status = "Preparing brute-force start at frame " + frame + ".";
        }
        internal void ContinueSearchPreparation(int frame)
        {
            if(!Active || !Frozen || Cursor!=0 || frame<1 || frame>Movie.Frames.Count) throw new InvalidOperationException("The engine prefix must resume from the prepared TAS start.");
            _playing=true; Mode=SessionMode.Playback; _seek=frame; _searchSeek=true;
            _resumeSingle=false; _resumeAtEnd=true; Status="Capturing the native prefix to frame " + frame + ".";
        }
        internal void VerifySearchCandidate(Movie candidate)
        {
            if (!Active || !Frozen || HasPendingEdits || SearchPreview) throw new InvalidOperationException("Pause at the prepared start before verification.");
            ValidatePlayback(candidate, -1);
            var original = Movie; bool dirty = Dirty;
            _searchReturnRecording = Mode == SessionMode.Recording;
            Release(); _searchOriginal = original; _searchOriginalDirty = dirty;
            Movie = candidate; Begin(true); Status = "Verifying brute-force candidate in the native game.";
        }
        internal void RestoreSearchOriginal(Movie original)
        {
            Movie = original; _searchOriginal = null; Dirty = _searchOriginalDirty;
            _playing = !_searchReturnRecording;
            Mode = _searchReturnRecording ? SessionMode.Recording : Cursor < Movie.Frames.Count ? SessionMode.Playback : SessionMode.Complete;
            _searchMustRebuild = true;
            // Native state is still the candidate's paused end. Saving a draft
            // forces reconstruction; transport cannot resume this mismatched view.
            _searchOriginalDirty = false;
        }
        internal void DiscardSearchPreview(Movie original)
        {
            RestoreSearchOriginal(original);
            ApplyTimelineEdit(original.CopyForEdit(), 0);
        }
        internal void PauseSearchOperation()
        {
            _searchSeek = false; _seek = -1;
            if (Active && (Mode == SessionMode.Recording || Mode == SessionMode.Playback))
            { if (_frameOpen) _pauseAtEnd = true; else PauseNow(); }
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
            using var timing = Profiler.Measure(ProfileSection.WorldProbe);
            var witness = Adapter.CaptureWorld();
            RunPreferences.Capture(witness);
            MovementTimers.Capture(witness);
            witness.Numbers["session/scale"] = _clock.SimulationScale;
            witness.Numbers["session/logicalTime"] = LogicalTime;
            witness.Numbers["session/puPhase"] = PuPhase;
            witness.Numbers["session/puCount"] = PuCount;
            witness.Validate(); return witness;
        }
        private StateProbe CapturePlayer()
        { using var timing = Profiler.Measure(ProfileSection.PlayerProbe); return Adapter.Probe(); }
        private bool VerifyWorld(WorldWitness expected, WorldWitness actual, string context)
        {
            string difference = expected?.Difference(actual);
            if (difference == null) return true;
            _worldExpected = expected; _worldActual = actual;
            Desync(context + ": " + difference); return false;
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
            if (CanSaveState && Cursor == state.Frame && RebuildCheckpoints.SamePrefix(Movie, state.Movie, Cursor))
            {
                var live = CaptureWorld();
                if (Checkpoints.LiveDifference(Cursor, live) == null && state.World.Difference(live) == null)
                {
                    Movie = state.Movie; FileName = state.TasName; Movie.Rerecords = rerecords;
                    Timeline.Branch(Movie, Cursor); Dirty = true; _playing = false;
                    Checkpoints.InvalidateFrom(Cursor);
                    Status = "Reused verified live savestate at frame " + Cursor + ". Recording remains paused.";
                    return;
                }
            }
            Release();
            Movie = state.Movie; FileName = state.TasName; Dirty = false;
            Movie.Rerecords = rerecords;
            _recordingTrace = null; _restoringState = state;
            _seek = state.Frame; _seekBranch = false;
            Begin(true);
            Status = "Rebuilding savestate at frame " + state.Frame + ". Use Stop to cancel. Recording resumes paused after verification.";
        }
        private void Begin(bool playing)
        {
            Checkpoints.Clear(); _worldExpected = _worldActual = null;
            Profiler.Reset();
            _profilePath = System.IO.Path.Combine(_profileDirectory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" +
                (playing ? "playback" : "recording") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "-profile.json");
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
            try { RunPreferences.Begin(Movie, !playing); Adapter.BeginRunEnvironment(Movie.Difficulty, Movie.StartSetup != null); SceneManager.LoadScene(Movie.Scene); }
            catch { Release(); throw; }
        }
        public void Early()
        {
            try
            {
                if (Active)
                {
                    _clock.EnforcePacing();
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
                if (Mode == SessionMode.Complete || Mode == SessionMode.Desync) return;
                _fixedTicks = 0; _frameOpen = true;
                _clock.BeginFrame();
                MovementTimers.Advance(Time.deltaTime);
            }
            catch (Exception error) { HandleError(error); }
        }
        public void End()
        {
            using var timing = Profiler.Measure(ProfileSection.Session);
            try
            {
                if (!Active) return;
                if (Mode == SessionMode.Warmup && _clock.Aligning)
                {
                    if (_clock.CompleteAlignment()) Frozen = false;
                    return;
                }
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
                    // Keep game callbacks gated during the clock-only alignment frame.
                    Frozen = true; Mode = SessionMode.Warmup;
                    Status = "Game loaded. Preparing the deterministic starting state.";
                    return;
                }
                if (_frameOpen)
                {
                    try { _clock.CompleteFrame(_fixedTicks); }
                    catch (InvalidOperationException error) { Desync(error.Message); _frameOpen = false; return; }
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
                            var start = CapturePlayer();
                            var startWorld = CaptureWorld();
                            if (_playing)
                            {
                                if (Math.Abs(Movie.StartPuPhase - PuPhase) > .000002) { Desync("Starting frame/PU offset differs"); return; }
                                var difference = ProbeComparer.Difference(Movie.StartState, start);
                                if (difference != null) { Desync("Starting state: " + difference); return; }
                                if (!VerifyWorld(Movie.StartWorld, startWorld, "Starting world")) return;
                            }
                            else { Movie.StartState = start; Movie.StartPuPhase = PuPhase; Movie.StartWorld = startWorld; }
                            Checkpoints.Remember(0, startWorld);
                            if (!_playing) _recordingTrace = _bootstrapTrace.ToArray();
                            WriteBootstrapReport();
                            Mode = _playing ? SessionMode.Playback : SessionMode.Recording;
                            Status = _playing ? "Playing input movie." : "Recording input movie.";
                            if (_seek == 0) FinishSeek();
                            else if (!_playing && StartPaused || Audit.Stepping) PauseNow();
                        }
                    }
                    else if (Mode == SessionMode.Recording || Mode == SessionMode.Playback)
                    {
                        var state = CapturePlayer();
                        WorldWitness world = null;
                        // One complete witness per movie second, plus any supplied
                        // reference. Never enumerate every scene object at 280 Hz.
                        if (Cursor % Movie.FrameRate == 0 || Cursor < Movie.Frames.Count && Movie.Frames[Cursor].World != null)
                            world = CaptureWorld().CompactForReplay();
                        if (_playing)
                        {
                            var frame = Movie.Frames[Cursor];
                            var difference = ProbeComparer.Difference(frame.Expected, state);
                            if (difference == null && frame.FixedTicks >= 0 && frame.FixedTicks != _fixedTicks) difference = "Physics tick schedule differs";
                            if (difference == null && frame.PuPhase >= 0 && Math.Abs(frame.PuPhase - PuPhase) > .000002) difference = "Frame/PU offset differs";
                            if (difference != null) { Desync("Frame " + Cursor + ": " + difference); return; }
                            if (frame.World != null && !VerifyWorld(frame.World, world, "World at frame " + Cursor)) return;
                            // Timing is an observed fact even when edited movement
                            // has no independent reference probe. Keep both distinct.
                            if (frame.FixedTicks < 0 || frame.PuPhase < 0)
                            { frame.FixedTicks = _fixedTicks; frame.PuPhase = PuPhase; Dirty = true; }
                            frame.PreviousFixedTicks = -1;
                            // A recording rebuild observes the changed prefix afresh.
                            // These become recording references, not independent proof.
                            if (_rebuildingEdit && _editReturnRecording && frame.Expected == null)
                            { frame.Expected = state; frame.FixedTicks = _fixedTicks; frame.PuPhase = PuPhase; frame.World = world; }
                            // Validate edits by replaying them, but don't label new probes as independent proof.
                        }
                        else
                        {
                            if (Cursor >= Core.Movie.MaxFrames) throw new InvalidOperationException("Maximum movie length reached; save the recording.");
                            var recorded = new MovieFrame { Input = _current, Expected = state, FixedTicks = _fixedTicks, PuPhase = PuPhase, World = world };
                            if (Cursor < Movie.Frames.Count)
                            {
                                if (!UseTimelineInput && !SameInput(Movie.Frames[Cursor].Input, _current, _previous)) Timeline.Invalidate(Movie, Cursor);
                                Movie.Frames[Cursor] = recorded;
                            }
                            else Movie.Frames.Add(recorded);
                            Dirty = true;
                        }
                        Cursor++;
                        Search?.ObserveFrame();
                        FrameCompleted?.Invoke();
                        if (world != null) Checkpoints.Remember(Cursor, world);
                        if (_stepPu)
                        {
                            _puStepFrames++;
                            if (_fixedTicks > 0 || Adapter.Player.pauseMenu?.menuOpen == true || _puStepFrames >= Movie.FrameRate * 5)
                            {
                                _stepPu = false; _pauseAtEnd = true;
                                Status = _fixedTicks > 0 ? "PU step complete: advanced " + _puStepFrames + " frame(s), " + _fixedTicks + " PU(s) in the last frame." :
                                    "PU step paused: " + (Adapter.Player.pauseMenu?.menuOpen == true ? "the game menu is open. Close it using Step frame." : "no physics update occurred within five seconds of movie frames.");
                            }
                        }
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
            finally
            {
                _clock?.EnforcePacing(); ScheduleNextFrame();
                Performance.Observe(Time.realtimeSinceStartupAsDouble, Mode.ToString(), FileName, Cursor, Movie.FrameRate,
                    PuCount, Frozen, Speed, Application.targetFrameRate, QualitySettings.vSyncCount,
                    PreviewTimeScale, Application.isFocused);
                Search?.Tick(Time.realtimeSinceStartupAsDouble);
            }
        }
        // Installed immediately before ScriptRunBehaviourUpdate, after Unity's
        // dynamic input event processing. Earlier PUs still see the prior input.
        public void SampleFrameInput()
        {
            using var timing = Profiler.Measure(ProfileSection.Input);
            if (!_frameOpen || Frozen || !Active) return;
            try
            {
                _previous = _current;
                if (Mode == SessionMode.Warmup || DiagnosticNeutralInput && !_playing) _current = default;
                else if (_playing) _current = Movie.Frames[Cursor].Input;
                else if (UseTimelineInput && Cursor < Movie.Frames.Count) _current = Movie.Frames[Cursor].Input;
                else if (UseAuthoredInput) _current = AuthoredInput;
                else
                {
                    _current = Adapter.ReadPhysicalInput();
                    if (_current.ExactEdges)
                    {
                        // Unity's per-update flags expire while the TAS is paused.
                        // Compare with the last simulated input, not the preceding
                        // rendered pause frame. Retain native edges too: a quick tap
                        // can have both press and release without changing Held.
                        _current.Press |= _current.Held & ~_previous.Held;
                        _current.Release |= _previous.Held & ~_current.Held;
                    }
                }
                if (_menuToggleRequested && Mode == SessionMode.Recording && !_playing)
                {
                    if (!_current.ExactEdges)
                    {
                        _current.Press = _current.Held & ~_previous.Held;
                        _current.Release = _previous.Held & ~_current.Held;
                        _current.ExactEdges = true;
                    }
                    _current.Press |= Buttons.Menu;
                    _current.Held |= Buttons.Menu;
                    _menuToggleRequested = false;
                }
            }
            catch (Exception error) { HandleError(error); }
        }
        public void ScheduleNextFrame()
        {
            try { if (Active && !Frozen && Mode != SessionMode.Loading && Mode != SessionMode.Stopping) _clock.PrepareNextFrame(); }
            catch (Exception error) { HandleError(error); }
        }
        private static bool SameInput(FrameInput a, FrameInput b, FrameInput previous)
        {
            return a.X == b.X && a.Y == b.Y && a.Held == b.Held &&
                (a.ExactEdges ? a.Press : a.Held & ~previous.Held) == (b.ExactEdges ? b.Press : b.Held & ~previous.Held) &&
                (a.ExactEdges ? a.Release : previous.Held & ~a.Held) == (b.ExactEdges ? b.Release : previous.Held & ~b.Held);
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
            _stepPu = false;
            if (!Frozen) { _pauseAtEnd = true; return; }
            _resumeSingle = true; _resumeAtEnd = true;
        }
        internal void AuditStep()
        {
            if (Audit.Stepping && Mode == SessionMode.Playback && Frozen)
            { _resumeSingle = true; _resumeAtEnd = true; }
        }
        public void StepPu()
        {
            RequireRunning();
            if (!Frozen) { _pauseAtEnd = true; return; }
            if (Adapter.Player.pauseMenu?.menuOpen == true)
                throw new InvalidOperationException("Close the game menu using Step frame before stepping to a PU.");
            _puStepFrames = 0; _stepPu = true;
            _resumeSingle = false; _resumeAtEnd = true;
            Status = "Advancing complete frames until the next physics update.";
        }
        private void PauseNow()
        {
            _stepPu = false; Frozen = true; _clock?.Pause();
            if (Active && (Mode == SessionMode.Recording || Mode == SessionMode.Playback || Mode == SessionMode.Complete))
                Checkpoints.Remember(Cursor, CaptureWorld());
        }
        private void FinishSeek()
        {
            _seek = -1;
            if (_searchSeek)
            {
                _searchSeek = false; _playing = !_searchReturnRecording;
                Mode = _searchReturnRecording ? SessionMode.Recording : Cursor < Movie.Frames.Count ? SessionMode.Playback : SessionMode.Complete;
                PauseNow(); Status = "Brute-force start reconstructed at frame " + Cursor + "."; return;
            }
            if (_rebuildingEdit)
            {
                _rebuildingEdit = false; _playing = !_editReturnRecording;
                Mode = _editReturnRecording ? SessionMode.Recording : Cursor < Movie.Frames.Count ? SessionMode.Playback : SessionMode.Complete;
                Dirty = true; PauseNow(); Save(); Status = "Edited timeline rebuilt to frame " + Cursor + ". Paused; future inputs are preserved.";
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
                PauseNow(); Status = "Savestate restored at frame " + Cursor + ". Recording is paused; use Pause / Run to resume or Step frame to advance.";
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
            Search?.Cancel();
            _searchMustRebuild = false;
            if (_searchOriginal != null) { Movie = _searchOriginal; Dirty = _searchOriginalDirty; _searchOriginal = null; }
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
            RunPreferences.End();
            SceneManager.LoadScene(scene);
            Status = "Stopping; waiting for the saved scene to finish reloading.";
        }
        public void Save()
        {
            if (SearchPreview) throw new InvalidOperationException("Accept or cancel the brute-force preview before saving. The original TAS is preserved.");
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
            bool forceRebuild = _searchMustRebuild;
            // Edits may have been undone before Save. Compare the actual prefix,
            // not the earliest button click, before deciding to reload the scene.
            if (!forceRebuild && RebuildCheckpoints.SamePrefix(Movie, movie, Cursor))
            {
                var actual = CaptureWorld();
                if (Checkpoints.LiveDifference(Cursor, actual) == null)
                {
                    for (int i = 0; i < Cursor; i++)
                    {
                        movie.Frames[i].Expected = Movie.Frames[i].Expected; movie.Frames[i].World = Movie.Frames[i].World;
                        movie.Frames[i].FixedTicks = Movie.Frames[i].FixedTicks; movie.Frames[i].PuPhase = Movie.Frames[i].PuPhase;
                    }
                    Movie = movie; Dirty = true; UseTimelineInput = true;
                    Checkpoints.InvalidateFrom(Cursor);
                    if (Mode == SessionMode.Complete && Cursor < Movie.Frames.Count) Mode = SessionMode.Playback;
                    Status = "Reused verified live checkpoint at frame " + Cursor + "; no rebuild needed.";
                    return;
                }
                // Even unchanged inputs cannot reuse externally modified state.
                forceRebuild = true;
            }
            if (firstChanged < Cursor || forceRebuild)
            {
                ValidatePlayback(movie, target);
                Store.Save("before-timeline-edit-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8), Movie);
                movie.Rerecords = Math.Max(movie.Rerecords, Movie.Rerecords) + 1;
                Release(); _searchMustRebuild = false; Movie = movie; Dirty = true; UseTimelineInput = true;
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
        public void CountFixedTick() { if (_frameOpen && !Frozen) { _fixedTicks++; PhysicsStarting?.Invoke(); } }
        private void Desync(string reason)
        {
            bool restoring = _restoringState != null;
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
                    Fps = Movie.FrameRate, PuPhase = PuPhase, NativePuPhase = Time.timeAsDouble - Time.fixedTimeAsDouble,
                    PuCount = PuCount, InputMode = InputSystem.settings.updateMode.ToString(),
                    BootstrapVersion = Movie.BootstrapVersion, WarmupFramesCompleted = _warmup,
                    RecordingBootstrap = _recordingTrace, CurrentBootstrap = _bootstrapTrace,
                    ActualDetails = Adapter.Diagnostics(),
                    ExpectedWorld = _worldExpected, ActualWorld = _worldActual,
                    ExpectedFixedTicks = Cursor < Movie.Frames.Count ? Movie.Frames[Cursor].FixedTicks : -1
                };
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(report, Newtonsoft.Json.Formatting.Indented);
                System.IO.File.WriteAllText(System.IO.Path.Combine(Store.DirectoryPath, "last-desync.json"), json);
                if (restoring) System.IO.File.WriteAllText(System.IO.Path.Combine(States.DirectoryPath, "last-state-desync.json"), json);
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
                Fps = Movie.FrameRate, PuPhase = PuPhase, NativePuPhase = Time.timeAsDouble - Time.fixedTimeAsDouble,
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
            if (SearchPreview || _searchMustRebuild || Search?.Busy == true || Search?.PlacingGoal == true)
                throw new InvalidOperationException("Finish or cancel the brute-force operation before advancing the TAS.");
            if (Audit.Running) throw new InvalidOperationException("Replay verification controls transport. Stop to cancel it first.");
            RequireNoPendingEdits();
            if (!Active || (Mode != SessionMode.Recording && Mode != SessionMode.Playback)) throw new InvalidOperationException("Start a recording or playback first.");
        }
        private void Hotkeys()
        {
            if (SuppressHotkeys || !Application.isFocused) return;
            if (Search?.PlacingGoal == true) return;
            if (TasHotkeys.Pressed(Bindings, TasShortcut.Editors) || TasHotkeys.Pressed(Bindings, TasShortcut.Stats)) DesktopPageRequest = 0;
            if (TasHotkeys.Pressed(Bindings, TasShortcut.Savestates)) DesktopPageRequest = 3;
            if (TasHotkeys.Pressed(Bindings, TasShortcut.Stop) && Active) Queue(Stop);
            else if (TasHotkeys.Pressed(Bindings, TasShortcut.Pause) && (Mode == SessionMode.Recording || Mode == SessionMode.Playback)) Queue(TogglePause);
            else if (TasHotkeys.Pressed(Bindings, TasShortcut.Step) && (Mode == SessionMode.Recording || Mode == SessionMode.Playback)) Queue(Step);
            else if (TasHotkeys.Pressed(Bindings, TasShortcut.StepPu) && (Mode == SessionMode.Recording || Mode == SessionMode.Playback)) Queue(StepPu);
        }
        private void Release()
        {
            Search?.GoalPlacement?.End(); _searchSeek = false;
            _menuToggleRequested = false;
            _stepPu = false; _puStepFrames = 0;
            Performance.Finish("released");
            SaveProfile(); Checkpoints.Clear(); RunPreferences.End();
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
            Search?.Dispose();
            if (_searchOriginal != null) { Movie = _searchOriginal; Dirty = _searchOriginalDirty; _searchOriginal = null; }
            Audit.Cancel();
            SceneManager.sceneLoaded -= SceneLoaded;
            try { if (HasPendingEdits) Store.Save("recovery-pending-edits", PendingTimeline); if (Dirty) Store.Save("recovery", Movie); }
            finally { Release(); }
        }
    }
}
