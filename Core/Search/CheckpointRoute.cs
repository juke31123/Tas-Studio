using System;

namespace Igtap.Tas.Core.Search
{
    public sealed class SearchCheckpoint
    {
        public float X,Y,Radius=24;
        public SearchCheckpoint Copy()=>(SearchCheckpoint)MemberwiseClone();
        public void Validate(){if(!Movie.Finite(X)||!Movie.Finite(Y)||!Movie.Finite(Radius)||Radius<1||Radius>1000)throw new ArgumentException("Checkpoint coordinates must be finite; radius must be 1-1000.");}
    }
    // Progress belongs to a trajectory, not to a map cell or global search.
    public sealed class CheckpointRoute
    {
        private readonly SearchCheckpoint[] _points;
        private readonly double[] _remaining;
        public int Count=>_points.Length;
        public SearchCheckpoint this[int index]=>_points[index];
        public CheckpointRoute(SearchOptions options)
        {
            _points=options.Checkpoints.Length==0?new[]{new SearchCheckpoint{X=options.GoalX,Y=options.GoalY,Radius=options.GoalRadius}}:
                Array.ConvertAll(options.Checkpoints,p=>p.Copy());
            _remaining=new double[Count];
            for(int i=Count-2;i>=0;i--)_remaining[i]=_remaining[i+1]+Math.Max(0,Distance(_points[i].X,_points[i].Y,_points[i+1])-_points[i+1].Radius);
        }
        private static double Distance(float x,float y,SearchCheckpoint point){double dx=x-point.X,dy=y-point.Y;return Math.Sqrt(dx*dx+dy*dy);}
        public int Advance(int progress,MotionVector position)
        {
            if(progress<0||progress>Count)throw new ArgumentOutOfRangeException(nameof(progress));
            while(progress<Count && Distance(position.X,position.Y,_points[progress])<=_points[progress].Radius)progress++;
            return progress;
        }
        public SearchCheckpoint Next(int progress)=>_points[Math.Min(progress,Count-1)];
        public double DistanceToNext(SearchState state)=>state.Checkpoint==Count?0:Distance(state.Player.Position.X,state.Player.Position.Y,Next(state.Checkpoint));
        public bool Finished(SearchState state)=>state.Checkpoint==Count && Distance(state.Player.Position.X,state.Player.Position.Y,_points[Count-1])<=_points[Count-1].Radius;
        public double Remaining(SearchState state)=>state.Checkpoint==Count?0:Math.Max(0,DistanceToNext(state)-Next(state.Checkpoint).Radius)+_remaining[state.Checkpoint];
        public int[] Progress(MotionVector[] path){var result=new int[path.Length];int progress=0;for(int i=0;i<path.Length;i++)result[i]=progress=Advance(progress,path[i]);return result;}
    }
    internal sealed class RouteSearchSimulation : IBranchSearchSimulation
    {
        private readonly ISearchSimulation _inner;
        private readonly CheckpointRoute _route;
        private int _progress;
        private sealed class Branch {internal RouteSearchSimulation Owner;internal object Token;internal SearchState State;internal int Progress;}
        internal RouteSearchSimulation(ISearchSimulation inner,CheckpointRoute route)
        {_inner=inner;_route=route;_progress=route.Advance(0,inner.State.Player.Position);inner.State.Checkpoint=_progress;}
        public SearchState State=>_inner.State;
        public SearchCacheStatistics CacheStatistics=>(_inner as IBranchSearchSimulation)?.CacheStatistics??default;
        public void Restore(SearchState state){int progress=state.Checkpoint;_inner.Restore(state);_progress=progress;State.Checkpoint=progress;}
        public bool Step(FrameInput input,SearchAbilities abilities,out string reason)
        {bool alive=_inner.Step(input,abilities,out reason);if(alive)_progress=_route.Advance(_progress,State.Player.Position);State.Checkpoint=_progress;return alive;}
        public object CaptureBranch()=>new Branch{Owner=this,Token=(_inner as IBranchSearchSimulation)?.CaptureBranch(),State=_inner is IBranchSearchSimulation?null:State.Copy(),Progress=_progress};
        public void RestoreBranch(object token)
        {
            if(!(token is Branch branch)||!ReferenceEquals(branch.Owner,this))throw new InvalidOperationException("A route snapshot belongs to another worker.");
            if(_inner is IBranchSearchSimulation cached)cached.RestoreBranch(branch.Token);else _inner.Restore(branch.State);
            _progress=branch.Progress;State.Checkpoint=_progress;
        }
    }
}
