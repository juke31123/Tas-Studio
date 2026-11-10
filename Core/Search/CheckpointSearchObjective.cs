using System;

namespace Igtap.Tas.Core.Search
{
    // Runtime-only policy for one checkpoint stage. It scores arrival plus an
    // estimated continuation, without committing any of the preview inputs.
    public sealed class CheckpointSearchObjective
    {
        public SearchCheckpoint Next;
        public int LookaheadFrames,RemainingFrames;
        public float PixelsPerFrame;
        public Func<bool> Stop;
        public bool HasLookahead=>Next!=null&&LookaheadFrames>0;
        public readonly struct Evaluation
        {
            public readonly double Score,Distance;
            public readonly long Frames,Pus,Unsupported;
            public Evaluation(double score,double distance,long frames=0,long pus=0,long unsupported=0)
            {Score=score;Distance=distance;Frames=frames;Pus=pus;Unsupported=unsupported;}
        }
        private double Distance(MotionVector point)
        {double x=point.X-Next.X,y=point.Y-Next.Y;return Math.Max(0,Math.Sqrt(x*x+y*y)-Next.Radius);}
        public Evaluation Evaluate(ISearchSimulation simulation,int arrivalFrames,SearchAbilities abilities)
        {
            if(!HasLookahead)return new Evaluation(arrivalFrames,0);
            var cached=simulation as IBranchSearchSimulation;
            object branch=cached?.CaptureBranch();var state=cached==null?simulation.State.Copy():null;
            void Restore(){if(cached!=null)cached.RestoreBranch(branch);else simulation.Restore(state);}
            return Evaluate(arrivalFrames,simulation.State.Player.Position,simulation.State.Previous,
                ()=>simulation.State.Player.Position,()=>simulation.State.Pus,
                input=>simulation.Step(input,abilities,out _),Restore);
        }
        public Evaluation Evaluate(int arrivalFrames,MotionVector origin,FrameInput previous,Func<MotionVector> position,
            Func<long> pus,Func<FrameInput,bool> advance,Action restore)
        {
            if(!HasLookahead)return new Evaluation(arrivalFrames,0);
            int horizon=Math.Min(LookaheadFrames,Math.Max(0,RemainingFrames-arrivalFrames));
            double speed=Math.Max(.05,PixelsPerFrame),initial=Distance(origin),best=double.MaxValue,bestDistance=initial;
            long frames=0,physics=0,unsupported=0;bool completed=false;
            try
            {
                // Hold current controls, steer toward the next point, or coast.
                // Only ordinary held inputs are repeated; taps are never latched.
                for(int policy=0;policy<3;policy++)
                {
                    if(Stop?.Invoke()==true)break;restore();
                    int used=0;bool alive=true;double distance=initial;
                    for(;used<horizon&&distance>0;)
                    {
                        if(Stop?.Invoke()==true)break;
                        var p=position();FrameInput input;
                        if(policy==0)input=new FrameInput{X=previous.X,Y=previous.Y,Held=previous.Held};
                        else if(policy==1)input=new FrameInput{X=Math.Sign(Next.X-p.X),Y=Math.Sign(Next.Y-p.Y)};
                        else input=default;
                        long before=pus();frames++;used++;
                        alive=advance(input);physics+=pus()-before;
                        if(!alive){unsupported++;break;}
                        distance=Distance(position());
                    }
                    if(alive&&(used==horizon||distance==0))
                    {
                        completed=true;double rank=arrivalFrames+used+distance/speed;
                        if(rank<best){best=rank;bestDistance=distance;}
                    }
                }
            }
            finally{restore();}
            // A valid checkpoint arrival remains a fallback if every preview
            // dies/is unsupported, or the stage deadline interrupts its probes.
            if(!completed)best=1_000_000+arrivalFrames+horizon+initial/speed;
            return new Evaluation(best,bestDistance,frames,physics,unsupported);
        }
    }
}
