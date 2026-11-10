using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Igtap.Tas.Core.Search
{
    public delegate SearchResult SearchAlgorithm(SearchJob job,Func<bool> cancel,Action<SearchResult> progress,Func<SearchJob,ISearchSimulation> factory);

    // Optional sequential search: keep one checkpoint arrival at each deadline.
    // The native backend reconstructs its immutable input history on each worker;
    // no Unity/Box2D objects or snapshot tokens cross their owning threads.
    public static class CheckpointSearch
    {
        public static SearchResult Run(SearchJob job,Func<bool> cancel,Action<SearchResult> progress,
            Func<SearchJob,ISearchSimulation> factory,SearchAlgorithm algorithm)
        {
            job.Validate();
            if(!job.Options.SecondsPerCheckpoint)return algorithm(job,cancel,progress,factory);
            if(factory==null)factory=j=>new StaticPlatformSimulation(j);
            var clock=Stopwatch.StartNew();var route=new CheckpointRoute(job.Options);
            var simulation=factory(job);var inputs=new List<FrameInput>();var path=new List<MotionVector>{simulation.State.Player.Position};
            int reached=route.Advance(0,simulation.State.Player.Position),attempt=0;
            var result=new SearchResult{JobId=job.Id,MovieHash=job.MovieHash,Simulator=job.Simulator,Algorithm=job.Options.Mode,
                CheckpointCount=route.Count,Status="Checkpoint search",Scheduling=job.Options.ExperimentalWorkStealing?"experimental work stealing":"affinity"};
            bool Cancel()=>cancel?.Invoke()==true;
            void Publish(SearchResult stage,Stopwatch timer,bool finished=false)
            {
                var report=result.Copy();report.Seconds=clock.Elapsed.TotalSeconds;report.StageSeconds=timer?.Elapsed.TotalSeconds??0;
                report.CheckpointsReached=reached;report.CommittedFrames=inputs.Count;report.ActiveCheckpoint=reached<route.Count?reached+1:0;
                report.Finished=finished;
                if(stage!=null)
                {
                    report.StageBestFrames=stage.BestFrames;report.Depth=inputs.Count+stage.Depth;report.NearestDistance=stage.NearestDistance;
                    report.BestScore=stage.BestScore;report.LookaheadDistance=stage.LookaheadDistance;
                    Add(report,stage);report.Message="Checkpoint "+(reached+1)+" / "+route.Count+"; each stage has "+job.Options.Seconds+" seconds. "+stage.Message;
                }
                progress?.Invoke(report);
            }
            while(reached<route.Count&&!Cancel())
            {
                int remaining=job.Options.FrameLimit-inputs.Count;
                if(remaining<=0){result.Status="Frame limit reached";break;}
                string chain=job.Options.Chain;
                if(job.Options.Mode=="chain")
                {
                    var actions=InputChain.Parse(chain,job.Options.Abilities,job.Options.FrameLimit);
                    if(actions.Sum(a=>a.Minimum)>remaining){result.Status="Frame limit reached";break;}
                    // Reuse the requested chain at each stage, trimming only
                    // duration ranges that exceed the remaining section cap.
                    var parts=chain.Split(';');
                    chain=string.Join(";",parts.Select((part,index)=>part.Split(':')[0]+":"+actions[index].Minimum+"-"+Math.Min(actions[index].Maximum,remaining)));
                }
                var timer=Stopwatch.StartNew();SearchResult best=null;double bestScore=double.MaxValue;
                var start=simulation.State.Copy();start.Checkpoint=0;
                bool StageStop()=>Cancel()||timer.Elapsed.TotalSeconds>=job.Options.Seconds;
                while(!StageStop())
                {
                    var stage=job.CopyForSearch();stage.Start=start.Copy();stage.Options.SecondsPerCheckpoint=false;stage.Options.UseCheckpointLookahead=false;
                    stage.Options.UseFrameTarget=false;stage.Options.Horizon=remaining;
                    stage.Options.Chain=chain;
                    stage.Options.Seconds=Math.Max(1,(int)Math.Ceiling(job.Options.Seconds-timer.Elapsed.TotalSeconds));
                    stage.Options.Seed=unchecked(job.Options.Seed+attempt++*7919);
                    var goal=route[reached];stage.Options.Checkpoints=new[]{goal.Copy()};stage.Options.GoalSet=true;stage.Options.GoalX=goal.X;stage.Options.GoalY=goal.Y;stage.Options.GoalRadius=goal.Radius;
                    stage.SeedInputs=best?.Inputs??job.SeedInputs.Skip(inputs.Count).Take(remaining).ToArray();
                    stage.Objective=new CheckpointSearchObjective{Next=job.Options.UseCheckpointLookahead&&reached+1<route.Count?route[reached+1].Copy():null,
                        LookaheadFrames=job.Options.CheckpointLookaheadFrames,RemainingFrames=remaining,PixelsPerFrame=Math.Max(.05f,job.Parameters.RunSpeed*10/job.Fps),Stop=StageStop};
                    SearchResult latest=null,trial=null;
                    void Observe(SearchResult update){latest=update.Copy();Publish(update,timer);}
                    try{trial=algorithm(stage,StageStop,Observe,factory);}
                    catch(OperationCanceledException) when(StageStop()){trial=latest;}
                    catch(AggregateException error) when(StageStop()&&error.Flatten().InnerExceptions.All(e=>e is OperationCanceledException)){trial=latest;}
                    if(trial!=null)
                    {
                        Add(result,trial);
                        if(trial.Inputs.Length>0&&trial.Inputs.Length<=remaining)
                        {
                            double score=trial.BestScore==double.MaxValue?trial.Inputs.Length:trial.BestScore;
                            if(score<bestScore||score==bestScore&&(best==null||trial.Inputs.Length<best.Inputs.Length)){best=trial.Copy();bestScore=score;}
                        }
                    }
                    // Finite chain enumeration can finish before its time cap;
                    // repeating the same complete set adds no new candidates.
                    if(job.Options.Mode=="chain"&&trial?.Status!="Budget reached"&&trial?.Status!="Cancelled")break;
                }
                result.StageSeconds=timer.Elapsed.TotalSeconds;
                if(Cancel()){result.Status="Cancelled";break;}
                if(best==null){result.Status="Checkpoint "+(reached+1)+" not reached";break;}
                // Replay only the chosen checkpoint segment. Lookahead was
                // rolled back on the worker and is not part of these inputs.
                // A Wave final replay may have installed another world into the
                // coordinator thread's shim statics. Rebuild an owning world
                // before using the immutable accepted history on this thread.
                simulation=factory(job);simulation.Restore(start);int before=reached;
                foreach(var input in best.Inputs)
                {
                    if(Cancel())break;
                    if(!simulation.Step(input,job.Options.Abilities,out var reason))throw new InvalidOperationException("Selected checkpoint segment failed replay: "+reason);
                    inputs.Add(input);path.Add(simulation.State.Player.Position);reached=route.Advance(reached,simulation.State.Player.Position);
                }
                result.StageBestFrames=best.Inputs.Length;result.BestScore=bestScore;result.LookaheadDistance=best.LookaheadDistance;
                if(Cancel()){result.Status="Cancelled";break;}
                if(reached==before)throw new InvalidOperationException("Selected segment missed its checkpoint on replay.");
                Publish(null,timer);
            }
            bool success=reached==route.Count&&route.Finished(new SearchState{Player=simulation.State.Player,Checkpoint=reached});
            result.Seconds=clock.Elapsed.TotalSeconds;result.CheckpointsReached=reached;result.CommittedFrames=inputs.Count;result.ActiveCheckpoint=reached<route.Count?reached+1:0;
            result.Depth=inputs.Count;result.NearestDistance=route.Remaining(new SearchState{Player=simulation.State.Player,Checkpoint=reached});
            result.Finished=true;
            if(success)
            {result.Status=inputs.Count==0?"Already at goal":"Checkpoint route found";result.Inputs=inputs.ToArray();result.Path=path.ToArray();result.BestFrames=inputs.Count;result.Message="Sequential checkpoint choices. Native verification required; not a globally optimal whole-route search.";}
            else
            {if(Cancel())result.Status="Cancelled";result.Inputs=Array.Empty<FrameInput>();result.Path=path.ToArray();result.Message="No complete route. "+reached+" / "+route.Count+" checkpoints committed in this search; the source TAS is unchanged.";}
            progress?.Invoke(result.Copy());return result;
        }
        private static void Add(SearchResult target,SearchResult stage)
        {
            target.Evaluated+=stage.Evaluated;target.SimulatedFrames+=stage.SimulatedFrames;target.Pus+=stage.Pus;target.Unsupported+=stage.Unsupported;
            target.Reached+=stage.Reached;target.ArchiveCells+=stage.ArchiveCells;target.Splices+=stage.Splices;target.Refinements+=stage.Refinements;
            target.PlateauDrifts+=stage.PlateauDrifts;target.ChainCombinations=Math.Max(target.ChainCombinations,stage.ChainCombinations);
            target.StateHits+=stage.StateHits;target.StateReplays+=stage.StateReplays;target.ReplayFrames+=stage.ReplayFrames;
            target.LookaheadSimulatedFrames+=stage.LookaheadSimulatedFrames;target.WorkerTasks+=stage.WorkerTasks;target.StolenTasks+=stage.StolenTasks;
            target.CacheBytes=Math.Max(target.CacheBytes,stage.CacheBytes);target.BlockedExamples=target.BlockedExamples.Concat(stage.BlockedExamples).Distinct().Take(8).ToArray();
        }
    }
}
