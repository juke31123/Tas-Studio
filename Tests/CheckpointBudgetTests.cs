using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Search;

internal static class CheckpointBudgetTests
{
    private sealed class Sim : IBranchSearchSimulation
    {
        private sealed class Branch{internal Sim Owner;internal SearchState State;}
        private readonly bool _inertia;
        public SearchState State{get;private set;}
        public SearchCacheStatistics CacheStatistics=>default;
        internal Sim(SearchJob job,bool inertia=false){_inertia=inertia;Restore(job.Start);}
        public void Restore(SearchState state)=>State=state.Copy();
        public object CaptureBranch()=>new Branch{Owner=this,State=State.Copy()};
        public void RestoreBranch(object value){var branch=(Branch)value;if(!ReferenceEquals(branch.Owner,this))throw new Exception("Cross-world token.");Restore(branch.State);}
        public bool Step(FrameInput input,SearchAbilities abilities,out string reason)
        {
            reason=null;var p=State.Player;p.Velocity.X=_inertia?p.Velocity.X+input.X*.2f:input.X;
            p.Position.X+=p.Velocity.X;State.Previous=input;State.Time+=.02;State.Pus++;State.BackendState=(State.BackendState is int old?old:0)+1;
            State.WallJumps=(int)State.BackendState;return true;
        }
    }
    public static void Run()
    {
        int checks=0;void Check(bool value,string why){if(!value)throw new Exception(why);checks++;}
        void Reject(Action action,string why){try{action();}catch{checks++;return;}throw new Exception("Expected rejection: "+why);}
        var job=SearchTests.FlatJob();job.Options.Horizon=5;job.Options.Seconds=1;job.Options.Workers=1;
        Check(job.Options.FrameAllowance==1000&&!job.Options.UseFrameTarget,"Frame allowance defaults to 1000 and the hard maximum remains the default mode.");
        var bounded=new SectionSearch().Run(job,()=>false,null);
        Check(bounded.Inputs.Length==0,"The original maximum still forbids a route beyond its frame limit.");
        job.Options.UseFrameTarget=true;job.Options.FrameAllowance=20;
        var beyond=new SectionSearch().Run(job,()=>false,null);
        Check(beyond.Inputs.Length>5&&beyond.Inputs.Length<=25,"Frame target mode permits a route beyond the target within the configured allowance.");
        job.Options.FrameAllowance=20000;Reject(job.Validate,"Combined frame limit is bounded.");
        job.Options.FrameAllowance=20;job.Options.UseCheckpointLookahead=true;Reject(job.Validate,"Lookahead cannot be enabled without per-checkpoint budgets.");
        job.Options.UseCheckpointLookahead=false;

        var forecast=new CheckpointSearchObjective{Next=new SearchCheckpoint{X=12,Y=1,Radius=1},LookaheadFrames=3,RemainingFrames=20,PixelsPerFrame=1,Stop=()=>false};
        var moving=SearchTests.FlatJob();moving.Start.Player.Position=new MotionVector(0,1);moving.Start.Player.Velocity.X=2;moving.Start.BackendState=17;
        var good=new Sim(moving,true);string before=SearchFiles.Hash(good.State);object hidden=good.State.BackendState;
        var goodScore=forecast.Evaluate(good,4,SearchAbilities.None);
        Check(goodScore.Frames>0&&goodScore.Pus==goodScore.Frames,"Lookahead performs and counts actual future simulation frames/PUs.");
        Check(SearchFiles.Hash(good.State)==before&&Equals(hidden,good.State.BackendState),"Lookahead restores position, velocity, clocks, input and hidden-world state.");
        moving.Start.Player.Velocity.X=-2;var bad=new Sim(moving,true);var badScore=forecast.Evaluate(bad,4,SearchAbilities.None);
        Check(goodScore.Score<badScore.Score&&goodScore.Distance<badScore.Distance,"A checkpoint arrival moving toward the next point ranks above one moving away.");

        SearchResult Segment(SearchJob stage,Func<bool> stop,Action<SearchResult> publish,Func<SearchJob,ISearchSimulation> create)
        {
            var sim=create(stage);var route=new CheckpointRoute(stage.Options);var rows=new List<FrameInput>();
            while(rows.Count<stage.Options.FrameLimit&&Math.Abs(sim.State.Player.Position.X-stage.Options.GoalX)>stage.Options.GoalRadius)
            {var row=new FrameInput{X=Math.Sign(stage.Options.GoalX-sim.State.Player.Position.X)};sim.Step(row,stage.Options.Abilities,out _);rows.Add(row);}
            bool reached=Math.Abs(sim.State.Player.Position.X-stage.Options.GoalX)<=stage.Options.GoalRadius;
            var result=new SearchResult{Inputs=reached?rows.ToArray():Array.Empty<FrameInput>(),BestFrames=reached?rows.Count:-1,BestScore=reached?rows.Count:double.MaxValue,Depth=rows.Count,Finished=true};
            publish(result.Copy());while(!stop())Thread.Sleep(2);return result;
        }
        var sequential=SearchTests.FlatJob();sequential.Options.SecondsPerCheckpoint=true;sequential.Options.Seconds=1;sequential.Options.Horizon=20;
        sequential.Options.Checkpoints=new[]{new SearchCheckpoint{X=3,Y=1,Radius=1},new SearchCheckpoint{X=7,Y=1,Radius=1}};
        var reports=new List<SearchResult>();var seenStarts=new List<SearchState>();
        SearchResult ObserveSegment(SearchJob stage,Func<bool> stop,Action<SearchResult> publish,Func<SearchJob,ISearchSimulation> create)
        {seenStarts.Add(stage.Start.Copy());return Segment(stage,stop,publish,create);}
        var watch=Stopwatch.StartNew();var resultRoute=CheckpointSearch.Run(sequential,()=>false,r=>reports.Add(r.Copy()),j=>new Sim(j),ObserveSegment);
        Check(resultRoute.Inputs.Length==6&&resultRoute.CheckpointsReached==2,"Sequential search joins reached checkpoint segments into one complete route.");
        Check(watch.Elapsed.TotalSeconds>=1.8&&reports.Any(r=>r.ActiveCheckpoint==1)&&reports.Any(r=>r.ActiveCheckpoint==2&&r.StageSeconds<.5),"Each checkpoint receives a fresh one-second clock.");
        Check(seenStarts.Count==2&&seenStarts[1].Player.Position.X==2&&seenStarts[1].Player.Velocity.X==1&&Equals(seenStarts[1].BackendState,2)&&seenStarts[1].Pus==2,"Checkpoint handoff retains full dynamic state and hidden history rather than resetting or teleporting the player.");
        Check(resultRoute.Path.Length==resultRoute.Inputs.Length+1&&resultRoute.CommittedFrames==6,"Only selected segment inputs are committed; path/frame counts remain aligned.");
        sequential.Options.Horizon=5;var failure=CheckpointSearch.Run(sequential,()=>false,null,j=>new Sim(j),Segment);
        Check(failure.Inputs.Length==0&&failure.CheckpointsReached==1&&failure.Status.Contains("not reached"),"The frame cap applies to the whole section; an incomplete route is not an acceptable candidate.");
        var cancelled=CheckpointSearch.Run(sequential,()=>true,null,j=>new Sim(j),Segment);
        Check(cancelled.Status=="Cancelled"&&cancelled.Inputs.Length==0,"Cancelling a staged search never exposes a partial route as a complete candidate.");
        sequential.Options.Horizon=20;bool stopAfterFirst=false;
        var partial=CheckpointSearch.Run(sequential,()=>stopAfterFirst,r=>{if(r.ActiveCheckpoint==2&&r.CommittedFrames>0)stopAfterFirst=true;},j=>new Sim(j),Segment);
        Check(partial.Status=="Cancelled"&&partial.CheckpointsReached==1&&partial.CommittedFrames>0&&partial.Inputs.Length==0,"Cancellation after a committed checkpoint still withholds the incomplete route.");
        sequential.Options.Horizon=3;sequential.Options.UseFrameTarget=true;sequential.Options.FrameAllowance=12;
        sequential.Options.UseCheckpointLookahead=true;sequential.Options.CheckpointLookaheadFrames=3;sequential.Options.Workers=2;
        var beam=CheckpointSearch.Run(sequential,()=>false,null,j=>new Sim(j),(j,c,p,f)=>new SectionSearch().Run(j,c,p,f));
        Check(beam.Inputs.Length>=6&&beam.Inputs.Length<=15&&beam.CheckpointsReached==2&&beam.LookaheadSimulatedFrames>0,"Real Beam search uses lookahead and per-checkpoint budgets within the target plus allowance.");
        sequential.Options.Mode="chain";sequential.Options.Chain="R:1-15";
        var chain=CheckpointSearch.Run(sequential,()=>false,null,j=>new Sim(j),(j,c,p,f)=>new ChainSearch().Run(j,c,p,f));
        Check(chain.Inputs.Length>=6&&chain.CheckpointsReached==2&&chain.LookaheadSimulatedFrames>0,"Chain duration ranges shrink to the remaining global allowance across checkpoint stages.");
        Check(SearchFiles.Hash(sequential.Options)==SearchFiles.Hash(Newtonsoft.Json.JsonConvert.DeserializeObject<SearchOptions>(Newtonsoft.Json.JsonConvert.SerializeObject(sequential.Options))),"Search options preserve target, allowance, checkpoint time and lookahead settings through serialization.");
        Console.WriteLine("Checkpoint budget and lookahead checks passed: "+checks);
    }
}
