using System;
using System.IO;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Search;
using Newtonsoft.Json;

internal static class EngineSearchTests
{
    private sealed class Linear : ISearchSimulation
    {
        public SearchState State {get;private set;}
        public Linear(SearchJob job) => Restore(job.Start);
        public void Restore(SearchState state) => State=state.Copy();
        public bool Step(FrameInput input,SearchAbilities abilities,out string reason)
        { reason=null; State.Player.Position=new MotionVector(State.Player.Position.X+input.X,State.Player.Position.Y); State.Pus++; return true; }
    }
    public static void Run()
    {
        int checks=0;
        void Check(bool value,string why) { if(!value) throw new Exception(why); checks++; }
        void Reject(Action action,string why) { try {action();} catch(InvalidDataException) {checks++;return;} catch(NotSupportedException) {checks++;return;} throw new Exception(why); }
        string hash=Convert.ToBase64String(new byte[32]);
        SearchJob Job() => new SearchJob { Fps=280, Simulator=EngineSearchSpec.Backend, HalfWidth=1,HalfHeight=1,
            Engine=new EngineSearchSpec {TraceHash=hash,StartHash=hash,WorldHash=hash,AnimationHash=hash},
            Options=new SearchOptions {StartFrame=0,GoalSet=true,GoalX=3,GoalY=0,GoalRadius=1,Horizon=3,Seconds=2,Workers=2,
                Abilities=SearchAbilities.None,Mode="chain",Chain="R:1-3"} };
        var job=Job(); job.Validate(); Check(job.Fps==280,"Game-code jobs retain the selected recording FPS.");
        var archive=Job();archive.Options.Mode="wave";archive.Validate();Check(archive.Options.Mode=="wave","Wave search is a distinct selectable mode.");
        Reject(()=>new SectionSearch().Run(archive,()=>false,null,j=>new Linear(j)),"Archive search must not silently execute beam search.");
        archive.Simulator=SearchJob.Backend;archive.Engine=null;Reject(archive.Validate,"The archive mode needs the verified frame backend.");
        var metrics=JsonConvert.DeserializeObject<SearchResult>(JsonConvert.SerializeObject(new SearchResult{Algorithm="wave",ArchiveCells=12,Splices=3,PlateauDrifts=2,StateHits=44,StateReplays=5},SearchFiles.Settings),SearchFiles.Settings);
        Check(metrics.Algorithm=="wave" && metrics.ArchiveCells==12 && metrics.Splices==3 && metrics.StateHits==44,"Wave search progress metrics survive the worker protocol.");
        var restored=JsonConvert.DeserializeObject<SearchJob>(JsonConvert.SerializeObject(job,SearchFiles.Settings),SearchFiles.Settings);
        restored.Validate(); Check(restored.Engine.TraceHash==hash && restored.Simulator==EngineSearchSpec.Backend,"Engine identities survive a type-free JSON round trip.");
        var token=new object(); var state=new SearchState {BackendState=token};
        Check(ReferenceEquals(state.Copy().BackendState,token),"A detached node retains its backend's immutable input-history token.");
        Check(!JsonConvert.SerializeObject(state,SearchFiles.Settings).Contains("BackendState"),"Worker-only state must never be serialised into jobs or TAS files.");
        var bad=Job(); bad.Engine=null; Reject(bad.Validate,"Missing native file identities must fail.");
        bad=Job(); bad.Engine.WorldHash=Convert.ToBase64String(new byte[31]); Reject(bad.Validate,"Hashes must be SHA-256 length.");
        bad=Job(); bad.Engine.StartHash="../start.json"; Reject(bad.Validate,"Paths must not masquerade as file identities.");
        bad=Job(); bad.Simulator=SearchJob.Backend; Reject(bad.Validate,"Static jobs cannot carry an engine capture.");
        bad=Job(); bad.Simulator="unknown"; Reject(bad.Validate,"Unknown backends must fail.");
        Reject(() => new StaticPlatformSimulation(job),"Never run a game-code job in the approximate static model.");
        Reject(() => new ChainSearch().Run(job,()=>false,null),"A chain needs the explicit game-code factory.");
        Reject(() => new SectionSearch().Run(job,()=>false,null),"A beam needs the explicit game-code factory.");
        var chain=new ChainSearch().Run(job,()=>false,null,j=>new Linear(j));
        Check(chain.Simulator==EngineSearchSpec.Backend && chain.Inputs.Length>0 && chain.Verification=="Unverified","Pluggable chain results retain their backend and require native verification.");
        job.Options.Mode="beam";
        var beam=new SectionSearch().Run(job,()=>false,null,j=>new Linear(j));
        Check(beam.Simulator==EngineSearchSpec.Backend && beam.Inputs.Length>0 && beam.Verification=="Unverified","Pluggable beam results retain their backend and require native verification.");
        Console.WriteLine("Engine search contract checks passed: " + checks);
    }
}
