using System;
using System.Linq;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Search;

internal static class CheckpointTests
{
    internal static void Run()
    {
        int count=0;
        void Check(bool value,string reason){if(!value)throw new Exception(reason);count++;}
        void Reject(Action action,string reason){try{action();}catch{count++;return;}throw new Exception(reason);}
        var job=SearchTests.FlatJob();
        job.Options.Checkpoints=new[]{new SearchCheckpoint{X=5,Y=1,Radius=1},new SearchCheckpoint{X=-3,Y=1,Radius=1},new SearchCheckpoint{X=10,Y=1,Radius=1}};
        var route=new CheckpointRoute(job.Options);
        Check(route.Advance(0,new MotionVector(10,1))==0,"Touching the finish first does not skip earlier checkpoints.");
        Check(route.Advance(0,new MotionVector(5,1))==1,"First checkpoint advances the trajectory.");
        Check(route.Advance(1,new MotionVector(10,1))==1,"Later checkpoints remain locked until their predecessor is visited.");
        Check(route.Advance(1,new MotionVector(-3,1))==2 && route.Advance(2,new MotionVector(10,1))==3,"Ordered checkpoints can finish after a reversal.");
        var copy=job.Options.Copy();copy.Checkpoints[0].X=99;
        Check(job.Options.Checkpoints[0].X==5,"Mailbox option copies do not alias mutable point coordinates.");
        var sim=new RouteSearchSimulation(new StaticPlatformSimulation(job),route);
        var initial=sim.CaptureBranch();
        for(int i=0;i<7;i++)sim.Step(new FrameInput{X=1},SearchAbilities.None,out _);
        Check(sim.State.Checkpoint==1,"Per-frame stepping updates route progress.");
        var progressed=sim.CaptureBranch();
        sim.RestoreBranch(initial);Check(sim.State.Checkpoint==0,"Restoring a branch also restores its checkpoint index.");
        sim.RestoreBranch(progressed);Check(sim.State.Checkpoint==1,"A later branch retains its own completed checkpoints.");
        var other=new RouteSearchSimulation(new StaticPlatformSimulation(job),route);
        Reject(()=>other.RestoreBranch(initial),"Checkpoint snapshot tokens must remain worker-local.");
        var ending=sim.State.Copy();ending.Checkpoint=route.Count;ending.Player.Position=new MotionVector(-20,1);
        Check(!route.Finished(ending),"A chain that leaves the finish after touching it is not a valid endpoint.");
        job.Options.Mode="chain";job.Options.Chain="R:6-6; L:9-9; R:13-13";
        var chain=new ChainSearch().Run(job,()=>false,null);
        Check(chain.BestFrames>0 && chain.CheckpointsReached==3 && chain.CheckpointCount==3,"Chain enumeration honours all checkpoints across a turnback.");
        job.Options.Mode="beam";job.Options.Seconds=1;job.Options.Workers=2;job.Options.BeamWidth=32;
        job.SeedInputs=chain.Inputs;
        var beam=new SectionSearch().Run(job,()=>false,null);
        Check(beam.BestFrames>0 && beam.CheckpointsReached==3,"Beam archive and refinement preserve checkpoint progress.");
        var replay=new RouteSearchSimulation(new StaticPlatformSimulation(job),route);
        foreach(var input in beam.Inputs)Check(replay.Step(input,job.Options.Abilities,out _),"Checkpoint candidate replays.");
        Check(route.Finished(replay.State),"Returned candidate hits every circle in order and finishes in the last.");
        var overlap=new CheckpointRoute(new SearchOptions{Checkpoints=new[]{new SearchCheckpoint{Radius=1},new SearchCheckpoint{Radius=2}}});
        Check(overlap.Advance(0,default)==2,"Overlapping ordered checkpoints can be reached on the same frame.");
        job.Options.Checkpoints=new[]{new SearchCheckpoint{X=float.NaN}};Reject(job.Validate,"Non-finite checkpoint rejected.");
        job.Options.Checkpoints=new SearchCheckpoint[]{null};Reject(job.Validate,"Missing checkpoint rejected.");
        job.Options.Checkpoints=Enumerable.Range(0,65).Select(_=>new SearchCheckpoint()).ToArray();Reject(job.Validate,"Checkpoint count is bounded.");
        Console.WriteLine("Checkpoint route checks passed: "+count);
    }
}
