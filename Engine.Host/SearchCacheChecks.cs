using System;
using System.IO;
using System.Linq;
using System.Threading;
using Igtap.Tas.Core.Search;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Simulation;

internal static class SearchCacheChecks
{
    public static int Run(string path)
    {
        string directory=Path.GetDirectoryName(Path.GetFullPath(path));
        try
        {
            var job=SearchFiles.Read<SearchJob>(path);job.Validate();
            if(job.Simulator!=EngineSearchSpec.Backend || job.SeedInputs.Length<32) throw new InvalidDataException("Cache checks need a native job with at least 32 seed frames.");
            string capture=Path.Combine(directory,"engine");var prefix=SimulationTrace.Read(Path.Combine(capture,"native.trace.json"));prefix.Validate();EngineFixture.Validate(capture,prefix);
            const long budget=256*1024;
            var sim=new EngineSearchSimulation(job,capture,prefix,()=>false,budget);
            string Hash() => SearchFiles.Hash(EngineFixture.Sample(sim.Native,0,0,"cache-check",true,true,true).Fields);
            int checks=0;
            void Check(bool value,string why){if(!value)throw new InvalidOperationException(why);checks++;}
            void Advance(int first,int last)
            {
                foreach(var input in job.SeedInputs.Skip(first).Take(last-first))
                    if(!sim.Step(input,job.Options.Abilities,out var reason)) throw new InvalidOperationException("Fixture seed failed: "+reason);
            }
            var root=sim.CaptureBranch();string rootHash=Hash();
            Advance(0,4);var early=sim.State.Copy();string earlyHash=Hash(),earlyDetachedHash=SearchFiles.Hash(early);sim.CaptureBranch();
            for(int i=4;i<32;i++){Advance(i,i+1);sim.CaptureBranch();Check(sim.CacheStatistics.Bytes<=budget,"Native cache exceeded its retained byte budget.");}
            var at32=sim.CaptureBranch();string a=Hash();
            Advance(32,Math.Min(64,job.SeedInputs.Length));string tail=Hash();
            Check(SearchFiles.Hash(early)==earlyDetachedHash,"Reusing the working motion state mutated an archived detached node.");
            sim.RestoreBranch(root);Check(Hash()==rootHash,"Root branch restoration differs.");
            sim.RestoreBranch(at32);Check(Hash()==a,"A/B/A branch restoration differs.");
            Advance(32,Math.Min(64,job.SeedInputs.Length));Check(Hash()==tail,"Restored branch continuation differs.");
            sim.Restore(early);Check(Hash()==earlyHash,"Evicted history reconstruction differs.");
            Check(sim.CacheStatistics.Replays>0 && sim.CacheStatistics.ReplayFrames>0,"Fixture did not exercise history reconstruction after eviction.");
            // Even an unchanged history must restore the world rather than
            // assuming that no state has changed since the previous restore.
            sim.Native.Player.airDashesLeft++;
            sim.Restore(early);Check(Hash()==earlyHash,"Same-history restoration did not reinstate the world's state.");
            if((job.Options.Abilities&SearchAbilities.Jump)==0)
                Check(!sim.Step(new FrameInput{ExactEdges=true,Press=Buttons.Jump,Release=Buttons.Jump},job.Options.Abilities,out var jumpReason) && jumpReason!=null,"A same-frame jump tap bypassed the disabled ability.");
            if((job.Options.Abilities&SearchAbilities.Dash)==0)
                Check(!sim.Step(new FrameInput{ExactEdges=true,Press=Buttons.Dash,Release=Buttons.Dash},job.Options.Abilities,out var dashReason) && dashReason!=null,"A same-frame dash tap bypassed the disabled ability.");
            var other=new EngineSearchSimulation(job,capture,prefix,()=>false,budget);
            bool rejected=false;
            try{other.RestoreBranch(at32);}catch(InvalidOperationException){rejected=true;}
            Check(rejected,"A snapshot was accepted by another simulator.");
            bool threadRejected=false;
            var thread=new Thread(()=>{try{sim.RestoreBranch(at32);}catch(InvalidOperationException){threadRejected=true;}});
            thread.Start();thread.Join();Check(threadRejected,"A snapshot was restored from a different thread.");
            SearchFiles.Write(Path.Combine(directory,"beam-cache-checks.json"),new {Passed=true,Checks=checks,CacheBudget=budget,sim.CacheStatistics.Hits,
                sim.CacheStatistics.Replays,sim.CacheStatistics.ReplayFrames,sim.CacheStatistics.Bytes});
            Console.WriteLine("Native Beam cache checks passed: "+checks);return 0;
        }
        catch(Exception error){SearchFiles.Write(Path.Combine(directory,"beam-cache-checks.json"),new {Passed=false,Error=error.ToString()});Console.Error.WriteLine(error);return 1;}
    }
}
