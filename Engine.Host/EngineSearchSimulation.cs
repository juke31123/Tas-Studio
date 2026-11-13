using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Text.Json;
using IGTAP.EngineSim;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Search;
using Igtap.Tas.Core.Simulation;

internal sealed class EngineSearchSimulation : IBranchSearchSimulation
{
    // Nodes shared by a beam contain immutable input history, not another
    // worker's engine objects. Each worker reconstructs/caches its own states.
    private sealed class History
    {
        public readonly History Parent;
        public readonly FrameInput Input;
        public readonly int Depth;
        public readonly string Owner;
        public History(History parent,FrameInput input,string owner) { Parent=parent; Input=input; Owner=owner; Depth=(parent?.Depth ?? 0)+1; }
    }
    private readonly SearchJob _job;
    private readonly Simulation _simulation;
    private readonly StudioSnapshot _root;
    private sealed class Branch
    {
        internal EngineSearchSimulation Owner;
        internal History History;
        internal StudioSnapshot Snapshot;
        internal long Bytes;
        internal LinkedListNode<Branch> Link;
    }
    private readonly long _cacheBudget;
    private readonly Dictionary<History,Branch> _cache=new Dictionary<History,Branch>();
    private readonly LinkedList<Branch> _lru=new LinkedList<Branch>();
    private readonly int _ownerThread = Thread.CurrentThread.ManagedThreadId;
    private Branch _rootBranch;
    private FrameInput _rootPrevious;
    private bool _historyValid = true, _branchMode;
    private long _cacheBytes, _cacheHits, _cacheReplays, _replayFrames, _replayPus;
    private readonly Func<bool> _cancel;
    private static readonly FieldInfo JumpBuffer = Field("jumpBuffer"), JumpCut = Field("jumpCutBuffer"), DashBuffer = Field("dashBuffer"),
        RecentJump = Field("recentlyJumped"), WallCoyote = Field("wallCoyoteFrames"), DashFrames = Field("dashFramesRemaining"),
        NormalJump = Field("MostRecentUpwardsMoveWasNormalJump"), DashBounce = Field("DashBounceFrames"), CanHyper = Field("canHyper"),
        DashDirection = Field("dashDirection"), PlatformCoyote = Field("movingPlatformCoyote"), FinalCling = Field("isFinalWallClingActive");
    private static FieldInfo Field(string name) => typeof(Movement).GetField(name, BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)
        ?? throw new MissingFieldException("Movement", name);
    private readonly FieldInfo _dead=typeof(Movement).GetField("isDead",BindingFlags.Instance|BindingFlags.NonPublic);
    private History _history;
    private AbilityResources _resources;
    private bool _observing;
    private string _policyFailure;
    public SearchState State { get; private set; }
    internal Simulation Native => _simulation;
    internal PlayerActionAvailability PreviewActions()
    {
        var p=_simulation.Player;
        var motion=State.Player;
        return PlayerActionAvailability.Evaluate(motion,new MovementParameters {
            DoubleJumpUnlocked=p.doubleJumpUnlocked,DashUnlocked=p.dashUnlocked,
            MaxWallCoyoteFrames=p.maxWallCoyoteFrames,WallJumpLockFrames=p.wallJumpMovementLockFrames
        },new PlayerActionContext {MenuOpen=p.pauseMenu==null || p.pauseMenu.menuOpen,GamePaused=p.GamePaused,
            WallJumpUnlocked=p.wallJumpUnlocked,WallJumps=p.wallJumpsLeft,CanHyper=(bool)CanHyper.GetValue(p)});
    }
    internal int NativePhysicsResiduals {get;private set;}
    internal int RecoveredContactSamples {get;private set;}
    internal int AdditionalVelocityResiduals {get;private set;}
    public SearchCacheStatistics CacheStatistics => new SearchCacheStatistics(_cacheHits,_cacheReplays,_replayFrames,_cacheBytes,_replayPus);
    public EngineSearchSimulation(SearchJob job,string directory,SimulationTrace prefix,Func<bool> cancel,long cacheBudget=64L*1024*1024)
    {
        if(cacheBudget<0) throw new ArgumentOutOfRangeException(nameof(cacheBudget));
        _cacheBudget=cacheBudget; _job=job; _cancel=cancel; _simulation=EngineFixture.Load(directory,prefix);
        using var definition=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"world.json")));
        bool customMap=definition.RootElement.TryGetProperty("customMap",out _);
        var validator=new NativePrefixValidator(prefix.Samples,customMap);
        void Match(SimulationSample actual)
        {
            validator.Match(actual,-50f*UnityEngine.Time.fixedDeltaTime*_simulation.Player.gravity,
                _simulation.Physics.PlayerTouch.Ceiling || _simulation.Player.onGround);
        }
        Match(EngineFixture.Sample(_simulation,0,0,"start",prefix.CaptureOrbs,prefix.CaptureMovers,prefix.CaptureSprings,prefix.CaptureUpgrades));
        int frame=0;
        _simulation.StudioSample=(pu,phase) => { if(phase=="frame-end") Match(EngineFixture.Sample(_simulation,frame,pu,phase,prefix.CaptureOrbs,prefix.CaptureMovers,prefix.CaptureSprings,prefix.CaptureUpgrades)); };
        try
        {
            for(;frame<prefix.Inputs.Count;frame++)
            {
                if((frame&127)==0 && cancel()) throw new OperationCanceledException("Search cancelled while reconstructing the native prefix.");
                if(prefix.PreparedNextFrames.Count!=0)_simulation.ConfigureStudioEndPacing(prefix.PreparedNextFrames[frame]);
                _simulation.StepStudioFrame(EngineFixture.Input(prefix.Inputs[frame],frame==0 ? prefix.Previous : prefix.Inputs[frame-1]));
            }
        }
        finally { _simulation.StudioSample=null; }
        validator.Complete();
        _simulation.ConfigureStudioEndPacing(prefix.AdvanceMode=="continuous");
        NativePhysicsResiduals=validator.PhysicsResiduals;
        RecoveredContactSamples=validator.RecoveredContactSamples;
        AdditionalVelocityResiduals=validator.AdditionalVelocityResiduals;
        using(var world=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"world.json")))) _simulation.ConfigureStudioSearchGuards(world.RootElement,prefix.CaptureSprings,prefix.CaptureUpgrades);
        _simulation.StudioAfterMovement=ObserveResources;
        _root=_simulation.CaptureStudioState();
        _rootPrevious=prefix.Inputs.Count==0 ? prefix.Previous : prefix.Inputs.Last();
        _rootBranch=new Branch {Owner=this,Snapshot=_root};
        _resources=Resources(); ReadState(_rootPrevious);
    }
    private AbilityResources Resources() => new AbilityResources {AirJumps=_simulation.Player.airJumpsLeft, Dashes=_simulation.Player.airDashesLeft,
        WallJumps=_simulation.Player.wallJumpsLeft, Alive=!(bool)_dead.GetValue(_simulation.Player)};
    private void ObserveResources()
    {
        if(!_observing) return;
        var now=Resources(); var allowed=_job.Options.Abilities;
        if((allowed&SearchAbilities.AirJump)==0 && now.AirJumps<_resources.AirJumps) _policyFailure="Extra air jump is disabled for this search.";
        if((allowed&SearchAbilities.WallJump)==0 && now.WallJumps<_resources.WallJumps) _policyFailure="Wall jump is disabled for this search.";
        if((allowed&SearchAbilities.Dash)==0 && now.Dashes<_resources.Dashes) _policyFailure="Dash is disabled for this search.";
        _resources=now;
    }
    private void ReadState(FrameInput previous)
    {
        var p=_simulation.Player; var body=_simulation.Physics.PlayerBody(); var origin=_simulation.WorldOriginInScene;
        // Working state is owned by this simulator. Archive nodes explicitly
        // Copy() it, retaining their detached motion and immutable history.
        if(State==null)State=new SearchState();
        State.Previous=previous;State.Phase=_simulation.StudioPhase;State.Time=_simulation.StudioNativeTime;State.Pus=_simulation.StudioPus;
        State.Scale=UnityEngine.Time.timeScale;State.BackendState=_history;State.WallJumps=p.wallJumpsLeft;
        var motion=State.Player;
        motion.Position=new MotionVector(body.px-origin.x,body.py-origin.y);motion.Velocity=new MotionVector(body.vx,body.vy);
        motion.Momentum=new MotionVector(p.momentum.x,p.momentum.y);motion.Grounded=p.onGround;motion.Dashing=p.dashActive;
        motion.ControlVelocity=new MotionVector(p.Velocity.x,p.Velocity.y);motion.PlatformVelocity=new MotionVector(p.movingPlatformVelocity.x,p.movingPlatformVelocity.y);
        motion.FramesSinceDash=p.framesSinceLastDash;
        motion.OnWall=p.OnWall;motion.FacingRight=p.facingRight;motion.Cutscene=(int)p.cutsceneMode;
        motion.JumpBuffered=(bool)JumpBuffer.GetValue(p);motion.JumpCutBuffered=(bool)JumpCut.GetValue(p);motion.DashBuffered=(bool)DashBuffer.GetValue(p);
        motion.RecentlyJumped=(bool)RecentJump.GetValue(p);motion.WallCoyoteFrames=(float)WallCoyote.GetValue(p);
        motion.DashFramesRemaining=(float)DashFrames.GetValue(p);motion.DashCooldown=p.dashCooldown;
        motion.WallJumpLock=p.wallJumpMovementLockRemaining;motion.WallJumpDirection=p.wallJumpDirection;
        motion.AirDashes=p.airDashesLeft;motion.AirJumps=p.airJumpsLeft;motion.Dead=!Resources().Alive;
        motion.PhysicsSimulated=p.GetComponent<UnityEngine.Rigidbody2D>().simulated;
        motion.NormalJump=(bool)NormalJump.GetValue(p);motion.DashBounceFrames=(int)DashBounce.GetValue(p);
        var direction=(UnityEngine.Vector2)DashDirection.GetValue(p);motion.DashDirection=new MotionVector(direction.x,direction.y);
        motion.OnMovingPlatform=p.currentlyTouchedMovingPlatform!=null || (float)PlatformCoyote.GetValue(p)>0;
        motion.FinalWallCling=(bool)FinalCling.GetValue(p);
    }
    public void Restore(SearchState snapshot)
    {
        CheckThread();
        if(snapshot.BackendState!=null && !(snapshot.BackendState is History)) throw new InvalidOperationException("Invalid game-code search history.");
        var target=(History)snapshot.BackendState;
        if(target!=null && target.Owner!=_job.Id) throw new InvalidOperationException("Search histories belong to their creating job.");
        var path=new Stack<History>(); var cursor=target;
        while(cursor!=null && !_cache.ContainsKey(cursor)) { path.Push(cursor); cursor=cursor.Parent; }
        var branch=cursor==null ? _rootBranch : _cache[cursor];
        RestoreBranch(branch);
        FrameInput previous=cursor?.Input ?? _rootPrevious;
        if(path.Count>0) _cacheReplays++;
        while(path.Count>0)
        {
            if((_replayFrames&63)==0 && _cancel()) throw new OperationCanceledException("Search cancelled during branch reconstruction.");
            var node=path.Pop(); long before=_simulation.StudioPus;
            _simulation.StepStudioFrame(EngineFixture.Input(node.Input,previous)); previous=node.Input; _history=node;
            _replayPus+=_simulation.StudioPus-before;
            _replayFrames++;
            Remember(node);
        }
        _historyValid=true; _resources=Resources(); ReadState(previous);
    }
    private void Remember(History node)
    {
        if(_branchMode || node.Depth%32!=0 || _cache.ContainsKey(node)) return;
        CaptureCurrent();
    }
    private void CheckThread()
    { if(Thread.CurrentThread.ManagedThreadId!=_ownerThread) throw new InvalidOperationException("Search snapshots must stay on their owning worker thread."); }
    private void Touch(Branch branch)
    { if(branch.Link!=null){_lru.Remove(branch.Link);_lru.AddLast(branch.Link);} }
    private Branch CaptureCurrent()
    {
        if(_history==null) return _rootBranch;
        if(_cache.TryGetValue(_history,out var existing)){Touch(existing);return existing;}
        long before=GC.GetAllocatedBytesForCurrentThread()-_simulation.SharedBytes;
        var snapshot=_simulation.CaptureStudioState();
        long bytes=Math.Max(4096,GC.GetAllocatedBytesForCurrentThread()-_simulation.SharedBytes-before);
        var branch=new Branch {Owner=this,History=_history,Snapshot=snapshot,Bytes=bytes};
        // Retained snapshots have an allocation-based byte budget. A single
        // oversized temporary parent token is usable without entering the cache.
        if(bytes<=_cacheBudget)
        {
            while(_cacheBytes+bytes>_cacheBudget && _lru.First!=null)
            {var old=_lru.First.Value;_lru.RemoveFirst();old.Link=null;_cache.Remove(old.History);_cacheBytes-=old.Bytes;}
            branch.Link=_lru.AddLast(branch);_cache.Add(_history,branch);_cacheBytes+=bytes;
        }
        return branch;
    }
    public object CaptureBranch()
    {
        CheckThread(); _branchMode=true;
        if(!_historyValid) throw new InvalidOperationException("Restore a valid search state before capturing a branch.");
        return CaptureCurrent();
    }
    public void RestoreBranch(object token)
    {
        CheckThread();
        if(!(token is Branch branch) || !ReferenceEquals(branch.Owner,this)) throw new InvalidOperationException("Branch snapshots belong to their creating simulator.");
        _historyValid=false;
        _simulation.RestoreStudioState(branch.Snapshot);_history=branch.History;_historyValid=true;Touch(branch);_cacheHits++;
        _resources=Resources();ReadState(_history?.Input ?? _rootPrevious);
    }
    public bool Step(FrameInput input,SearchAbilities abilities,out string unsupported)
    {
        CheckThread();
        if (!StepNative(input,State.Previous,abilities,out unsupported)) return false;
        _history=new History(_history,input,_job.Id); _historyValid=true;ReadState(input); Remember(_history); return true;
    }
    private sealed class PreviewBranch
    {
        internal EngineSearchSimulation Owner;
        internal StudioSnapshot Snapshot;
        internal FrameInput Previous;
    }
    // Preview never enters the search history/cache or its ability policy.
    // Resets, menus and death/respawn frames are real recorded inputs too.
    internal object CapturePreview()
    {
        CheckThread();
        return new PreviewBranch { Owner=this, Snapshot=_simulation.CaptureStudioState(), Previous=State.Previous };
    }
    internal void RestorePreview(object token)
    {
        CheckThread();
        if (!(token is PreviewBranch branch) || !ReferenceEquals(branch.Owner,this)) throw new InvalidOperationException("Preview snapshots belong to their creating simulator.");
        _simulation.RestoreStudioState(branch.Snapshot); _historyValid=false; _observing=false;
        ReadState(branch.Previous);
    }
    internal bool StepPreview(FrameInput input, out string unsupported)
    {
        CheckThread(); unsupported=null; _historyValid=false; _observing=false;
        try { _simulation.ConfigureStudioEndPacing(false); _simulation.StepStudioFrame(EngineFixture.Input(input,State.Previous)); ReadState(input); return true; }
        catch (NotSupportedException error) { unsupported=error.Message; return false; }
    }
    internal bool StepNative(FrameInput input,FrameInput previous,SearchAbilities abilities,out string unsupported)
    {
        unsupported=null;
        if(abilities!=_job.Options.Abilities) throw new InvalidOperationException("A worker's ability policy cannot change during a job.");
        if(!Resources().Alive) return false;
        var actions=input.Held|input.Press;
        if((actions&(Buttons.Menu|Buttons.Reset))!=0 || ((abilities&SearchAbilities.Jump)==0 && (actions&(Buttons.Jump|Buttons.Jump2))!=0) ||
            ((abilities&SearchAbilities.Dash)==0 && (actions&(Buttons.Dash|Buttons.Dash2))!=0))
        { unsupported="Input is outside this search's allowed actions."; return false; }
        try
        {
            _historyValid=false;
            _resources=Resources(); _policyFailure=null; _observing=true;
            _simulation.StepStudioFrame(EngineFixture.Input(input,previous)); ObserveResources();
            if(_policyFailure!=null) { unsupported=_policyFailure; return false; }
            if(!Resources().Alive) return false;
            return true;
        }
        catch(NotSupportedException error) { unsupported=error.Message; return false; }
        finally { _observing=false; }
    }
}
