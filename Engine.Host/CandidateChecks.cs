using System;
using System.IO;
using Igtap.Tas.Core.Search;
using Igtap.Tas.Core.Simulation;

internal static class CandidateChecks
{
    public static int Run(string jobPath,string resultPath)
    {
        string directory=Path.GetDirectoryName(Path.GetFullPath(jobPath));
        try
        {
            var job=SearchFiles.Read<SearchJob>(jobPath);job.Validate();
            var result=SearchFiles.Read<SearchResult>(resultPath);
            if(result.MovieHash!=job.MovieHash || result.Inputs.Length==0 || result.Inputs.Length>job.Options.FrameLimit)
                throw new InvalidDataException("Candidate does not belong to this movie or exceeds the section horizon.");
            string capture=Path.Combine(directory,"engine");var prefix=SimulationTrace.Read(Path.Combine(capture,"native.trace.json"));
            EngineFixture.Validate(capture,prefix);
            var route=new CheckpointRoute(job.Options);
            var simulation=new RouteSearchSimulation(new EngineSearchSimulation(job,capture,prefix,()=>false),route);
            int reached=-1;
            simulation.CaptureBranch();
            long allocated=GC.GetAllocatedBytesForCurrentThread();var clock=System.Diagnostics.Stopwatch.StartNew();
            double Distance()=>route.Remaining(simulation.State);
            for(int i=0;i<result.Inputs.Length;i++)
            {
                var input=result.Inputs[i];
                if(!float.IsFinite(input.X)||!float.IsFinite(input.Y)||Math.Abs(input.X)>1||Math.Abs(input.Y)>1)
                    throw new InvalidDataException("Candidate has an invalid axis.");
                if(!simulation.Step(input,job.Options.Abilities,out var reason))throw new InvalidOperationException("Candidate stopped at frame "+i+": "+reason);
                if(reached<0 && route.Finished(simulation.State))reached=i+1;
            }
            clock.Stop();allocated=GC.GetAllocatedBytesForCurrentThread()-allocated;
            bool passed=reached>=0 && route.Finished(simulation.State);
            var report=new {Passed=passed,GoalFrame=reached,Frames=result.Inputs.Length,Distance=Distance(),CheckpointsReached=simulation.State.Checkpoint,CheckpointCount=route.Count,
                AllocatedBytes=allocated,BytesPerFrame=allocated/(double)result.Inputs.Length,ReplaySeconds=clock.Elapsed.TotalSeconds,
                Scope="Offline replay through the current host. Native in-game verification is still required."};
            SearchFiles.Write(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(resultPath)),"candidate-check.json"),report);
            Console.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(report));return passed?0:1;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 2;}
    }
}
