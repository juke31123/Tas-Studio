using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Search;
using Igtap.Tas.Core.Simulation;

namespace Igtap.Tas.Runtime
{
    internal sealed class BruteforceController : IDisposable
    {
        private readonly TasSession _session;
        private readonly string _root, _bindingsPath;
        private Process _worker;
        private string _directory, _sourceHash;
        private Movie _source, _verification;
        private bool _preparing, _verifying, _verified;
        private double _nextPoll;
        private bool _trackingResources;
        private AbilityResources _resources;
        private string _policyFailure;
        private IEngineSearchCapture _engineCapture;
        private SimulationTrace _enginePrefix;
        private bool _useEngine, _engineBootstrap;
        private string _engineHostError;
        private EngineHostPackage _portableHost;
        private Task<string> _setup;
        private CancellationTokenSource _setupCancellation;
        private bool _portableReady, _setupCancelled;
        public SearchOptions Options { get; private set; } = new SearchOptions();
        public SearchResult Result { get; private set; } = new SearchResult();
        public SearchJob Job { get; private set; }
        public GoalCameraBindings CameraBindings { get; private set; }
        public IGoalPlacement GoalPlacement;
        public bool Busy => _setup != null || _preparing || _verifying || _worker != null;
        public bool PlacingGoal => GoalPlacement?.Active == true;
        public bool Verifying => _verifying;
        public bool CanAccept => _verified && Result.Inputs.Length > 0 && !Busy && _session.Active && _session.Frozen && !_session.HasPendingEdits;
        public bool CanStart => !Busy && !PlacingGoal && _session.Active && _session.Frozen && _session.CanEditTimeline && !_session.HasPendingEdits;
        internal string WorkerPath { get; set; }
        internal string EngineWorkerPath { get; set; }
        public BruteforceController(TasSession session, string root)
        {
            _session = session; _root = Path.Combine(root, "search"); _bindingsPath = Path.Combine(root, "goal-camera.json");
            Directory.CreateDirectory(_root);
            try { CameraBindings = GoalCameraBindings.Load(_bindingsPath); }
            catch { CameraBindings = new GoalCameraBindings(); }
            WorkerPath = Path.Combine(Path.GetDirectoryName(typeof(BruteforceController).Assembly.Location), "Solver", "TasStudio.Solver.exe");
            try {
                EngineWorkerPath=EngineHostLocation.Find(root);
                if (EngineHostPackage.Exists(root) && string.Equals(EngineWorkerPath,
                    Path.Combine(EngineHostPackage.BundledPath(root), "Host", "TasStudio.EngineHost.exe"), StringComparison.OrdinalIgnoreCase))
                    _portableHost = new EngineHostPackage(EngineHostPackage.BundledPath(root), root,
                        Path.GetDirectoryName(AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "Assembly-CSharp").Location));
            }
            catch(Exception error) { _engineHostError=error.Message; }
            if (!string.IsNullOrEmpty(EngineWorkerPath)) Result.Simulator = EngineSearchSpec.Backend;
        }
        public void Open()
        {
            if (!Options.GoalSet && _session.Active && _session.Frozen && !_session.WaitingForLoad) GoalFromPlayer();
        }
        public void Configure(SearchOptions options)
        {
            if (Busy || PlacingGoal) throw new InvalidOperationException("Cancel the current search/goal selection before changing its options.");
            options.Validate(_session.Movie.Frames.Count); Options = options.Copy(); _verified = false;
        }
        public void SetGoal(float x, float y)
        {
            if (Busy || !Movie.Finite(x) || !Movie.Finite(y)) throw new InvalidOperationException("Cannot change this goal while a search is active.");
            Options.GoalX = x; Options.GoalY = y; Options.GoalSet = true; _verified = false;
            if (Job != null) Result = new SearchResult { Simulator = string.IsNullOrEmpty(EngineWorkerPath) ? SearchJob.Backend : EngineSearchSpec.Backend,
                Status = "Goal changed", Message = "Start a new search for this goal." };
        }
        public void GoalFromPlayer()
        {
            if (!_session.Active || !_session.Frozen || _session.WaitingForLoad || !_session.Adapter.IsAlive)
                throw new InvalidOperationException("Pause the TAS in gameplay to use the current player position as the goal.");
            var position = _session.Adapter.SearchWorldPosition(); SetGoal(position.X, position.Y);
        }
        public void ToggleGoalPlacement()
        {
            if (GoalPlacement == null) throw new NotSupportedException("Goal camera is unavailable.");
            if (PlacingGoal) GoalPlacement.End();
            else { if (!CanStart) throw new InvalidOperationException("Pause the run and finish any edits first."); GoalPlacement.Begin(); }
        }
        public void SaveCameraBinding(int index, string key)
        {
            var candidate = CameraBindings.Copy(); candidate.Assign(index, key); candidate.Save(_bindingsPath); CameraBindings = candidate;
        }
        public void Start(SearchOptions options)
        {
            if (!CanStart) throw new InvalidOperationException("Pause recording or playback and save/discard edits before starting a search.");
            Configure(options);
            if(_engineHostError!=null) throw new InvalidOperationException(_engineHostError);
            if (options.StartFrame > _session.Cursor) throw new InvalidOperationException("Record or replay through the selected start frame before searching from it.");
            if (_session.Movie.StartState == null) throw new InvalidOperationException("The TAS starting state is unavailable.");
            if (_portableHost != null && !_portableReady)
            {
                _setupCancelled = false;
                _setupCancellation = new CancellationTokenSource();
                var token = _setupCancellation.Token;
                _setup = Task.Run(() => _portableHost.Prepare(token));
                Result = new SearchResult { Simulator = EngineSearchSpec.Backend, Status = "Preparing simulator",
                    Message = "Preparing the simulator from your installed game. First use takes longer; no SDK or separate .NET installation is needed." };
                return;
            }
            StartPrepared();
        }
        private void StartPrepared()
        {
            if (!_session.Active || !_session.Frozen || _session.HasPendingEdits || Options.StartFrame > _session.Cursor)
                throw new InvalidOperationException("Pause the TAS at or after the selected start frame and save edits before searching.");
            _engineCapture=(_session.Adapter as object) as IEngineSearchCapture;
            _useEngine=!string.IsNullOrEmpty(EngineWorkerPath) && _engineCapture!=null;
            if (Options.Mode=="wave" && !_useEngine) throw new InvalidOperationException("Wave search requires the prepared local game-code host. Chain and beam remain available.");
            if (_portableHost != null && !_useEngine) throw new InvalidOperationException("The portable simulator requires the game-code capture adapter.");
            string worker=_useEngine ? EngineWorkerPath : WorkerPath;
            if (!File.Exists(worker)) throw new FileNotFoundException(_useEngine ? "The registered local game-code host is missing. Prepare or register it again." : "The external Solver folder is missing. Install the full Tas Studio package.", worker);
            // Probes/witnesses are immutable; copy mutable frame rows without
            // re-parsing untyped witness values through JSON (which can change
            // float/date representation and therefore the source fingerprint).
            _source = _session.Movie.CopyForEdit(); _sourceHash = SearchFiles.Hash(_source); _verified = false;
            Job = new SearchJob { Options = Options.Copy(), Fps = _source.FrameRate, MovieHash = _sourceHash,
                PrefixHash = SearchFiles.PrefixHash(_source, Options.StartFrame), GameHash = _source.GameHash, Scene = _source.Scene,
                SeedInputs = _source.Frames.Skip(Options.StartFrame).Take(Options.Horizon).Select(f => f.Input).ToArray() };
            if(_useEngine) Job.Simulator=EngineSearchSpec.Backend;
            _directory = Path.Combine(_root, Job.Id); Directory.CreateDirectory(_directory);
            SearchFiles.Write(Path.Combine(_directory, "original.tas.json"), _source);
            Result = new SearchResult { JobId = Job.Id, MovieHash = _sourceHash, Simulator=Job.Simulator, Status = "Preparing start",
                Message = "Reconstructing the selected frame. Existing future inputs are retained until you accept a verified result." };
            if(_useEngine)
            {
                _preparing=true; _engineBootstrap=false; _engineCapture.BeginEngineCapture();
                try { _session.PrepareSearchStart(_source,0); }
                catch { _preparing=false; _engineCapture.EndEngineCapture(); _engineCapture=null; throw; }
            }
            else if (_session.Cursor == Options.StartFrame) Launch();
            else { _preparing = true; _session.PrepareSearchStart(_source, Options.StartFrame); }
        }
        private void Launch()
        {
            _preparing = false;
            if(_useEngine)
            {
                if(_enginePrefix==null || _enginePrefix.Inputs.Count!=Options.StartFrame) throw new InvalidOperationException("The native engine prefix is incomplete.");
                string capture=Path.Combine(_directory,"engine");
                _enginePrefix.Validate(); SearchFiles.Write(Path.Combine(capture,"native.trace.json"),_enginePrefix);
                _engineCapture.CaptureEngineSummary(_session,Job);
                Job.Simulator=EngineSearchSpec.Backend;
                Job.Engine=new EngineSearchSpec {TraceHash=SimulationTraceComparer.FileHash(Path.Combine(capture,"native.trace.json")),
                    StartHash=SimulationTraceComparer.FileHash(Path.Combine(capture,"start.json")),WorldHash=_enginePrefix.WorldHash,AnimationHash=_enginePrefix.AnimationHash};
                _engineCapture.EndEngineCapture(); _engineCapture=null; _enginePrefix=null;
            }
            else _session.Adapter.CaptureSearch(_session, Job);
            Job.Validate(); SearchFiles.Write(Path.Combine(_directory, "job.json"), Job);
            string worker=_useEngine ? EngineWorkerPath : WorkerPath;
            var info = new ProcessStartInfo { FileName = worker, Arguments = (_useEngine ? "solve " : "") + "\"" + Path.Combine(_directory, "job.json") + "\"",
                WorkingDirectory = Path.GetDirectoryName(worker), UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            _worker = Process.Start(info) ?? throw new InvalidOperationException("The external solver did not start.");
            Result.Simulator=Job.Simulator; Result.Status = "Searching"; Result.Message = "External exploratory simulation running. The game remains paused."; _nextPoll = 0;
        }
        public void ObserveFrame()
        {
            if(!_preparing || !_useEngine || _enginePrefix==null) return;
            int frame=_session.Cursor-1;
            if(frame!=_enginePrefix.Inputs.Count || frame>=Options.StartFrame) throw new InvalidOperationException("Engine prefix capture skipped a frame.");
            _enginePrefix.Inputs.Add(_session.CurrentInput);
            _enginePrefix.Samples.Add(_engineCapture.CaptureEngineFrame(_session,frame,_session.Movie.Frames[frame].FixedTicks));
        }
        public void Tick(double now)
        {
            GoalPlacement?.Tick();
            try
            {
                if (_setup != null && _setup.IsCompleted)
                {
                    var setup = _setup; _setup = null;
                    _setupCancellation.Dispose(); _setupCancellation = null;
                    try { EngineWorkerPath = setup.GetAwaiter().GetResult(); _portableReady = true; }
                    catch (OperationCanceledException) { Result.Status = "Cancelled"; return; }
                    if (_setupCancelled) { Result.Status = "Cancelled"; return; }
                    if (!_session.Active || !_session.Frozen || _session.HasPendingEdits)
                        throw new InvalidOperationException("Simulator prepared. Pause the TAS and save edits before starting a search.");
                    StartPrepared();
                }
                if (_preparing)
                {
                    if (!_session.Active || _session.Mode == SessionMode.Desync) { Cancel(); Result.Message = "Could not reconstruct start: " + _session.Status; return; }
                    if(_useEngine && !_engineBootstrap && _session.Frozen && _session.Cursor==0 && !_session.WaitingForLoad && _session.Mode!=SessionMode.Warmup)
                    {
                        string capture=Path.Combine(_directory,"engine"); _engineCapture.ExportEngineBootstrap(_session,capture);
                        _enginePrefix=SearchFiles.Read<SimulationTrace>(Path.Combine(capture,"native.trace.json"));
                        _enginePrefix.AdvanceMode="before-pacing"; _engineBootstrap=true;
                        if(Options.StartFrame>0) _session.ContinueSearchPreparation(Options.StartFrame);
                    }
                    if ((!_useEngine || _engineBootstrap) && _session.Frozen && _session.Cursor == Options.StartFrame && _session.Mode != SessionMode.Loading && _session.Mode != SessionMode.Warmup) Launch();
                }
                if (_worker != null && now >= _nextPoll)
                {
                    _nextPoll = now + .15;
                    string path = Path.Combine(_directory, "result.json"); bool exited = _worker.HasExited;
                    if (!File.Exists(path)) path = Path.Combine(_directory, "progress.json");
                    if (File.Exists(path))
                    {
                        var incoming = SearchFiles.Read<SearchResult>(path);
                        if (incoming != null && incoming.Version == 1 && incoming.JobId == Job.Id && incoming.MovieHash == _sourceHash && incoming.Simulator==Job.Simulator) Result = incoming;
                    }
                    if (exited)
                    {
                        if (!Result.Finished) { Result.Finished = true; Result.Status = "Worker failed"; Result.Message = "The external worker exited without a valid final result. See search/" + Job.Id + "."; }
                        _worker.Dispose(); _worker = null;
                    }
                }
                if (_verifying)
                {
                    if (_session.Mode == SessionMode.Desync || !_session.Active || _session.Mode == SessionMode.Stopping)
                    { _verifying = false; Result.Verification = "Rejected"; Result.Message = _session.Status; }
                    else if (_session.Mode == SessionMode.Complete && _session.Frozen)
                    {
                        var at = _session.Adapter.SearchWorldPosition();
                        double dx = at.X - Job.Options.GoalX, dy = at.Y - Job.Options.GoalY;
                        ObserveResources(_session.Adapter.SearchResources());
                        _verified = _policyFailure == null && _session.Adapter.SearchResources().Alive && dx * dx + dy * dy <= Job.Options.GoalRadius * Job.Options.GoalRadius;
                        _verifying = false; Result.Verification = _verified ? "Goal verified in game" : "Rejected";
                        Result.Message = _verified ? "Native replay reached the goal. Accept result stages a replacement from frame " + Options.StartFrame + ". Save edits applies it." :
                            _policyFailure ?? "The native replay did not reach the predicted goal alive. Keep the original TAS and adjust the simulation/search.";
                        SearchFiles.Write(Path.Combine(_directory, "verification.json"), new { Passed = _verified, At = at, Job.Options.GoalX, Job.Options.GoalY, Job.Options.GoalRadius,
                            Scope = "Native goal reachability; experimental simulator parity is not certified." });
                    }
                }
            }
            catch (Exception e) { Cancel(); Result.Status = "Error"; Result.Message = e.Message; }
        }
        public void Verify()
        {
            if (Busy || PlacingGoal || _session.SearchPreview || Result.Inputs.Length == 0 || Job == null) throw new InvalidOperationException("Wait for a candidate and finish the previous preview before verifying it.");
            CheckSource();
            if (SearchFiles.Hash(Options) != SearchFiles.Hash(Job.Options)) throw new InvalidOperationException("Search options changed. Run a new search.");
            if (Result.Inputs.Length > Options.Horizon) throw new InvalidOperationException("Candidate exceeds the configured horizon.");
            Buttons allowed = ((Options.Abilities & SearchAbilities.Jump) != 0 ? Buttons.Jump : 0) | ((Options.Abilities & SearchAbilities.Dash) != 0 ? Buttons.Dash : 0);
            if (Result.Inputs.Any(input => !Movie.Finite(input.X) || !Movie.Finite(input.Y) || Math.Abs(input.X) > 1 || Math.Abs(input.Y) > 1 ||
                ((input.Held | input.Press | input.Release) & ~allowed) != 0)) throw new InvalidOperationException("Candidate contains disabled or invalid input actions.");
            _trackingResources = false; _policyFailure = null;
            _verification = Candidate(); _session.VerifySearchCandidate(_verification); _verifying = true; _verified = false;
            Result.Verification = "Verifying in game"; Result.Message = "Native replay of the original prefix and candidate; gameplay inputs are locked.";
        }
        public void ObserveResources(AbilityResources resources)
        {
            if (!_verifying || _session.Mode != SessionMode.Playback && _session.Mode != SessionMode.Complete || _session.Cursor < Options.StartFrame) return;
            if (!resources.Alive) _policyFailure = "Rejected: native replay died during the searched section.";
            if (_trackingResources)
            {
                if ((Options.Abilities & SearchAbilities.AirJump) == 0 && resources.AirJumps < _resources.AirJumps) _policyFailure = "Rejected: native replay used a disabled air jump.";
                if ((Options.Abilities & SearchAbilities.Dash) == 0 && resources.Dashes < _resources.Dashes) _policyFailure = "Rejected: native replay used a disabled dash.";
                if ((Options.Abilities & SearchAbilities.WallJump) == 0 && resources.WallJumps >= 0 && resources.WallJumps < _resources.WallJumps) _policyFailure = "Rejected: native replay used a disabled wall jump.";
            }
            _trackingResources = true; _resources = resources;
        }
        private Movie Candidate()
        {
            var candidate = _source.CopyForEdit();
            candidate.Frames.RemoveRange(Options.StartFrame, candidate.Frames.Count - Options.StartFrame);
            foreach (var input in Result.Inputs) candidate.Frames.Add(new MovieFrame { Input = input });
            candidate.Rerecords++; candidate.Validate(); return candidate;
        }
        private void CheckSource()
        {
            if (_session.HasPendingEdits || _source == null || _session.Movie != _verification && SearchFiles.Hash(_session.Movie) != _sourceHash)
                throw new InvalidOperationException("This TAS changed since the search began. Start a new search.");
            if (EnvironmentFingerprint.Game() != Job.GameHash) throw new InvalidOperationException("The game build changed since export.");
        }
        public Movie Accept()
        {
            if (!CanAccept) throw new InvalidOperationException("Verify this candidate in game before accepting it.");
            CheckSource();
            var candidate = Candidate();
            // Restore the original movie as the editor's live baseline before
            // creating the normal undoable draft patch. Keep the native world paused.
            _session.RestoreSearchOriginal(_source);
            _session.StageTimelineEdit(candidate, Options.StartFrame);
            _verified = false; Result.Message = "Candidate staged. Save edits to replace the suffix; Discard keeps the original inputs.";
            return _source;
        }
        public void Cancel()
        {
            _setupCancelled = true; _setupCancellation?.Cancel();
            _engineCapture?.EndEngineCapture(); _engineCapture=null; _enginePrefix=null;
            GoalPlacement?.End();
            if (_directory != null && _worker != null) File.WriteAllText(Path.Combine(_directory, "cancel"), "cancel");
            if (_preparing || _verifying) _session.PauseSearchOperation();
            _preparing = _verifying = false; _verified = false;
            Result.Status = "Cancelled";
        }
        public void DiscardPreview()
        {
            Cancel();
            if (_session.SearchPreview && _source != null) _session.DiscardSearchPreview(_source);
        }
        public void Dispose()
        {
            Cancel(); GoalPlacement?.Dispose();
            if (_setup != null)
            {
                var cancellation = _setupCancellation;
                _setup.ContinueWith(task => { var ignored = task.Exception; cancellation?.Dispose(); }, TaskScheduler.Default);
                _setup = null; _setupCancellation = null;
            }
            _worker?.Dispose(); _worker = null; // Cancel file lets a detached worker exit cooperatively.
        }
    }
}
