using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Igtap.Tas.Core.Search;
using Igtap.Tas.Core.Simulation;

internal static class EngineSearchRunner
{
    public static int Run(string path, bool prefixOnly = false)
    {
        path=Path.GetFullPath(path); string directory=Path.GetDirectoryName(path); SearchJob job=null;
        try
        {
            job=SearchFiles.Read<SearchJob>(path); job.Validate();
            if(job.Simulator!=EngineSearchSpec.Backend) throw new InvalidDataException("This host requires a game-code job.");
            string capture=Path.Combine(directory,"engine"); var spec=job.Engine;
            if(!string.IsNullOrEmpty(job.CustomMapId))
            {
                using var world=JsonDocument.Parse(File.ReadAllText(Path.Combine(capture,"world.json")));
                if(!world.RootElement.TryGetProperty("customMap",out var map)||map.GetProperty("id").GetString()!=job.CustomMapId||map.GetProperty("hash").GetString()!=job.CustomMapHash)
                    throw new InvalidDataException("Captured geometry belongs to a different custom map or map revision.");
            }
            foreach(var file in new[] {("native.trace.json",spec.TraceHash),("start.json",spec.StartHash),("world.json",spec.WorldHash),("animation.json",spec.AnimationHash)})
                if(SimulationTraceComparer.FileHash(Path.Combine(capture,file.Item1))!=file.Item2) throw new InvalidDataException("Engine capture changed: " + file.Item1);
            var prefix=SimulationTrace.Read(Path.Combine(capture,"native.trace.json")); prefix.Validate();
            EngineFixture.Validate(capture,prefix);
            if(prefix.Fps!=job.Fps || prefix.GameHash!=job.GameHash || prefix.StartFrame!=0 || prefix.Inputs.Count!=job.Options.StartFrame ||
                prefix.AdvanceMode!="before-pacing" || !prefix.CaptureOrbs || !prefix.CaptureMovers || !prefix.CaptureSprings || prefix.Samples.Count!=prefix.Inputs.Count+1 || prefix.Samples[0].Phase!="start" ||
                prefix.Samples.Skip(1).Where((sample,index) => sample.Frame!=index || sample.Phase!="frame-end").Any())
                throw new InvalidDataException("Search requires a complete same-FPS native witness from the TAS bootstrap to Start frame.");
            using(var start=JsonDocument.Parse(File.ReadAllText(Path.Combine(capture,"start.json"))))
                foreach(string field in new[] {"moverSleep","moverContinuations"})
                    if(!start.RootElement.TryGetProperty(field,out var value) || value.ValueKind!=JsonValueKind.Array)
                        throw new InvalidDataException("Search requires captured mover sleep and reset-continuation state. Export the job again.");
            if(prefixOnly)
            {
                var simulation=new EngineSearchSimulation(job,capture,prefix,()=>false,0);
                Console.WriteLine("Native search start verified after " + prefix.Inputs.Count + " frames; body physics residuals: " + simulation.NativePhysicsResiduals + "; recovered contact samples: " + simulation.RecoveredContactSamples + ".");
                return 0;
            }
            long nextPoll=0; int cancelled=0;
            bool Cancel()
            {
                long now=Stopwatch.GetTimestamp(),due=Interlocked.Read(ref nextPoll);
                if(now>=due && Interlocked.CompareExchange(ref nextPoll,now+Stopwatch.Frequency/10,due)==due && File.Exists(Path.Combine(directory,"cancel"))) Interlocked.Exchange(ref cancelled,1);
                return Volatile.Read(ref cancelled)!=0;
            }
            void Progress(SearchResult result) => SearchFiles.WriteProgress(Path.Combine(directory,"progress.json"),result);
            var workers=new System.Collections.Concurrent.ConcurrentDictionary<int,int>();
            var residuals=new System.Collections.Concurrent.ConcurrentDictionary<int,int>();
            var contacts=new System.Collections.Concurrent.ConcurrentDictionary<int,int>();
            var velocityResiduals=new System.Collections.Concurrent.ConcurrentDictionary<int,int>();
            Func<SearchJob,ISearchSimulation> factory=j => {
                var simulation=new EngineSearchSimulation(j,capture,prefix,()=>Cancel()||j.Objective?.Stop?.Invoke()==true);
                if(j.Start.BackendState!=null)simulation.Restore(j.Start);
                workers.TryAdd(Thread.CurrentThread.ManagedThreadId,prefix.Inputs.Count);
                residuals.AddOrUpdate(Thread.CurrentThread.ManagedThreadId,simulation.NativePhysicsResiduals,(_,old)=>Math.Max(old,simulation.NativePhysicsResiduals));
                contacts.AddOrUpdate(Thread.CurrentThread.ManagedThreadId,simulation.RecoveredContactSamples,(_,old)=>Math.Max(old,simulation.RecoveredContactSamples));
                velocityResiduals.AddOrUpdate(Thread.CurrentThread.ManagedThreadId,simulation.AdditionalVelocityResiduals,(_,old)=>Math.Max(old,simulation.AdditionalVelocityResiduals));return simulation;
            };
            SearchResult Algorithm(SearchJob j,Func<bool> stop,Action<SearchResult> report,Func<SearchJob,ISearchSimulation> create)=>
                j.Options.Mode=="wave"?WaveSearch.Run(j,stop,report,create):j.Options.Mode=="chain"?new ChainSearch().Run(j,stop,report,create):new SectionSearch().Run(j,stop,report,create);
            var result=CheckpointSearch.Run(job,Cancel,Progress,factory,Algorithm);
            if(contacts.Values.Any(n=>n>0)||velocityResiduals.Values.Any(n=>n>0))
                result.Message+=" Custom-map prefix had transient contact differences; positions remained within "+CustomMapPhysics.PrefixTolerance+" units and the search-start state passed the normal checks. Native candidate verification is required.";
            else if(residuals.Values.Any(n=>n>0))result.Message+=" Custom-map prefix body physics matched within "+CustomMapPhysics.PrefixTolerance+" world units; all other witnessed fields matched exactly. Native candidate verification is required.";
            SearchFiles.Write(Path.Combine(directory,"engine-search-validation.json"),new {job.Fps,PrefixFrames=prefix.Inputs.Count,
                VerifiedWorkerThreads=workers.Keys.OrderBy(i => i).ToArray(),NativePrefixVerified=workers.Count>0,
                NativeSearchStartVerified=workers.Count>0,RecoveredContactSamples=contacts.Values.Sum(),AdditionalVelocityResiduals=velocityResiduals.Values.Sum(),
                NativePrefixMatchedExactly=!residuals.Values.Any(n=>n>0)&&!contacts.Values.Any(n=>n>0),NativePhysicsResiduals=residuals.Values.Sum(),CustomMapPrefixTolerance=CustomMapPhysics.PrefixTolerance,
                IntermediateContactVelocityResidual=NativePrefixValidator.ContactVelocityResidual,NativeCandidateVerificationRequired=true});
            SearchFiles.Write(Path.Combine(directory,"result.json"),result); return 0;
        }
        catch(Exception error)
        {
            var cause=error is AggregateException aggregate ? aggregate.Flatten().InnerExceptions[0] : error;
            string detail=cause.Message;
            if(prefixOnly) {Console.Error.WriteLine(detail);return 1;}
            SearchFiles.Write(Path.Combine(directory,"result.json"),new SearchResult {JobId=job?.Id ?? "",MovieHash=job?.MovieHash ?? "",
                Simulator=EngineSearchSpec.Backend,Status=cause is OperationCanceledException ? "Cancelled" : "Error",Message=detail,Finished=true});
            SearchFiles.Write(Path.Combine(directory,"engine-search-error.json"),new {Error=error.ToString()});
            Console.Error.WriteLine(detail); return 1;
        }
    }
}
