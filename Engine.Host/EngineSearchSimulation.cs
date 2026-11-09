using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using IGTAP.EngineSim;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Search;
using Igtap.Tas.Core.Simulation;

internal sealed class EngineSearchSimulation : ISearchSimulation
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
    private readonly Dictionary<History,StudioSnapshot> _cache=new Dictionary<History,StudioSnapshot>();
    private readonly Queue<History> _order=new Queue<History>();
    private readonly FieldInfo _dead=typeof(Movement).GetField("isDead",BindingFlags.Instance|BindingFlags.NonPublic);
    private History _history;
    private AbilityResources _resources;
    private bool _observing;
    private string _policyFailure;
    public SearchState State { get; private set; }
    internal Simulation Native => _simulation;
    public EngineSearchSimulation(SearchJob job,string directory,SimulationTrace prefix,Func<bool> cancel)
    {
        _job=job; _simulation=EngineFixture.Load(directory,prefix);
        int observed=0;
        void Match(SimulationSample actual)
        {
            if(observed>=prefix.Samples.Count) throw new InvalidDataException("Native prefix witness is incomplete.");
            var expected=prefix.Samples[observed++];
            if(actual.Frame!=expected.Frame || actual.Pu!=expected.Pu || actual.Phase!=expected.Phase || actual.Fields.Count!=expected.Fields.Count ||
                expected.Fields.Any(p => !actual.Fields.TryGetValue(p.Key,out string value) || value!=p.Value))
            {
                var field=expected.Fields.FirstOrDefault(p => !actual.Fields.TryGetValue(p.Key,out string value) || value!=p.Value).Key;
                throw new InvalidDataException("Native search prefix differs at frame " + actual.Frame + " (" + (field ?? "sample schedule") + ").");
            }
        }
        Match(EngineFixture.Sample(_simulation,0,0,"start",prefix.CaptureOrbs,prefix.CaptureMovers,prefix.CaptureSprings));
        int frame=0;
        _simulation.StudioSample=(pu,phase) => { if(phase=="frame-end") Match(EngineFixture.Sample(_simulation,frame,pu,phase,prefix.CaptureOrbs,prefix.CaptureMovers,prefix.CaptureSprings)); };
        try
        {
            for(;frame<prefix.Inputs.Count;frame++)
            {
                if((frame&127)==0 && cancel()) throw new OperationCanceledException("Search cancelled while reconstructing the native prefix.");
                _simulation.StepStudioFrame(EngineFixture.Input(prefix.Inputs[frame],frame==0 ? prefix.Previous : prefix.Inputs[frame-1]));
            }
        }
        finally { _simulation.StudioSample=null; }
        if(observed!=prefix.Samples.Count) throw new InvalidDataException("Unconsumed native prefix samples.");
        using(var world=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"world.json")))) _simulation.ConfigureStudioSearchGuards(world.RootElement,prefix.CaptureSprings);
        _simulation.StudioAfterMovement=ObserveResources;
        _root=_simulation.CaptureStudioState();
        _resources=Resources(); ReadState(prefix.Inputs.Count==0 ? prefix.Previous : prefix.Inputs.Last());
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
        State=new SearchState {Previous=previous, Phase=_simulation.StudioPhase, Time=_simulation.StudioNativeTime, Pus=_simulation.StudioPus,
            Scale=UnityEngine.Time.timeScale, BackendState=_history,
            Player=new PlayerMotion {Position=new MotionVector(body.px-origin.x,body.py-origin.y), Velocity=new MotionVector(body.vx,body.vy),
                Momentum=new MotionVector(p.momentum.x,p.momentum.y), Grounded=p.onGround, Dashing=p.dashActive,
                AirDashes=p.airDashesLeft, AirJumps=p.airJumpsLeft, Dead=!Resources().Alive, PhysicsSimulated=true} };
    }
    public void Restore(SearchState snapshot)
    {
        if(snapshot.BackendState!=null && !(snapshot.BackendState is History)) throw new InvalidOperationException("Invalid game-code search history.");
        var target=(History)snapshot.BackendState;
        if(target!=null && target.Owner!=_job.Id) throw new InvalidOperationException("Search histories belong to their creating job.");
        var path=new Stack<History>(); var cursor=target;
        while(cursor!=null && !_cache.ContainsKey(cursor)) { path.Push(cursor); cursor=cursor.Parent; }
        _simulation.RestoreStudioState(cursor==null ? _root : _cache[cursor]); _history=cursor;
        FrameInput previous=cursor?.Input ?? _job.Start.Previous;
        while(path.Count>0)
        {
            var node=path.Pop(); _simulation.StepStudioFrame(EngineFixture.Input(node.Input,previous)); previous=node.Input; _history=node;
            Remember(node);
        }
        _resources=Resources(); ReadState(previous);
    }
    private void Remember(History node)
    {
        if(node.Depth%32!=0 || _cache.ContainsKey(node)) return;
        _cache.Add(node,_simulation.CaptureStudioState()); _order.Enqueue(node);
        if(_cache.Count>64) _cache.Remove(_order.Dequeue());
    }
    public bool Step(FrameInput input,SearchAbilities abilities,out string unsupported)
    {
        if (!StepNative(input,State.Previous,abilities,out unsupported)) return false;
        _history=new History(_history,input,_job.Id); ReadState(input); Remember(_history); return true;
    }
    internal bool StepNative(FrameInput input,FrameInput previous,SearchAbilities abilities,out string unsupported)
    {
        unsupported=null;
        if(abilities!=_job.Options.Abilities) throw new InvalidOperationException("A worker's ability policy cannot change during a job.");
        if(!Resources().Alive) return false;
        if(input.Down(Buttons.Menu|Buttons.Reset) || ((abilities&SearchAbilities.Jump)==0 && input.Down(Buttons.Jump|Buttons.Jump2)) ||
            ((abilities&SearchAbilities.Dash)==0 && input.Down(Buttons.Dash|Buttons.Dash2)))
        { unsupported="Input is outside this search's allowed actions."; return false; }
        try
        {
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
