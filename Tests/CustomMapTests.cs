using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Search;
using Igtap.Tas.Core.Simulation;

internal static class CustomMapTests
{
    public static void Run()
    {
        int count=0;void Check(bool value,string why){if(!value)throw new Exception(why);count++;}
        void Reject(Action action,string why){try{action();}catch{count++;return;}throw new Exception("Expected rejection: "+why);}
        string root=Path.Combine(Path.GetTempPath(),"tas-custom-maps-"+Guid.NewGuid().ToString("N"));
        try
        {
            Check(CustomMapCatalog.List(root).Length==0,"A missing Navigator map folder has no custom choices.");
            Directory.CreateDirectory(Path.Combine(root,"second"));Directory.CreateDirectory(Path.Combine(root,"first"));Directory.CreateDirectory(Path.Combine(root,"unrelated"));
            File.WriteAllText(Path.Combine(root,"second","map.json"),"{\"groups\":[],\"name\":\"Zoo\",\"uid\":\"two\"}");
            File.WriteAllText(Path.Combine(root,"first","map.json"),"{\"formatVersion\":1,\"name\":\"Alpha\",\"groups\":[]}");
            var maps=CustomMapCatalog.List(root);
            Check(maps.Length==2&&maps[0].Id=="first"&&maps[1].Name=="Zoo","Map discovery reads names, sorts choices and ignores unrelated folders.");
            Directory.CreateDirectory(Path.Combine(root,"newly-installed"));File.WriteAllText(Path.Combine(root,"newly-installed","map.json"),"{\"name\":\"Added while running\",\"groups\":[]}");
            Check(CustomMapCatalog.List(root).Any(m=>m.Id=="newly-installed"),"A later scan discovers maps installed after the first scan without a fixed map list.");
            string hash=CustomMapCatalog.Fingerprint(root,"first");
            File.WriteAllText(Path.Combine(root,"first","hub.json"),"{\"title\":\"metadata only\"}");
            Check(CustomMapCatalog.Fingerprint(root,"first")==hash,"Hub metadata does not change the gameplay fingerprint.");
            File.WriteAllText(Path.Combine(root,"first","texture.png"),"native map asset");
            Check(CustomMapCatalog.Fingerprint(root,"first")!=hash,"Bundled map assets participate in replay identity.");
            Reject(()=>CustomMapCatalog.Fingerprint(root,"../first"),"Path traversal");Reject(()=>CustomMapCatalog.Fingerprint(root,"missing"),"Missing map");
            var setup=new TasStartSetup{CustomMapId="first",CustomMapName="Alpha",CustomMapHash=CustomMapCatalog.Fingerprint(root,"first"),VmanMode=2};setup.Validate();
            var clone=setup.Clone();Check(clone.IsCustomMap&&clone.CustomMapHash==setup.CustomMapHash&&clone.VmanMode==2,"Configured starts retain map and VMAN settings.");
            setup.RequireLoadedMap("first",setup.CustomMapHash);count++;
            Reject(()=>setup.RequireLoadedMap("second",setup.CustomMapHash),"Capturing the wrong loaded map");
            Reject(()=>setup.RequireLoadedMap("first",new string('0',64)),"Capturing a different loaded map revision");
            Reject(()=>new TasStartSetup().RequireLoadedMap("first",setup.CustomMapHash),"Capturing a custom map for a base-game TAS");
            string path=Path.Combine(root,"start.json");SearchFiles.Write(path,setup);var loaded=SearchFiles.Read<TasStartSetup>(path);
            Check(SearchFiles.Hash(loaded)==SearchFiles.Hash(setup),"Map identity and VMAN settings survive file round trips.");
            var job=SearchTests.FlatJob();job.CustomMapId=setup.CustomMapId;job.CustomMapName=setup.CustomMapName;job.CustomMapHash=setup.CustomMapHash;job.Validate();
            SearchFiles.Write(Path.Combine(root,"job.json"),job);var loadedJob=SearchFiles.Read<SearchJob>(Path.Combine(root,"job.json"));
            Check(loadedJob.CustomMapId==setup.CustomMapId&&loadedJob.CustomMapHash==setup.CustomMapHash,"External search jobs preserve the captured map's identity.");
            job.CustomMapHash="invalid";Reject(job.Validate,"A search job missing its map fingerprint");
            setup.Course=1;Reject(setup.Validate,"Mixing a built-in course and custom map");setup.Course=0;
            setup.VmanMode=3;Reject(setup.Validate,"Unknown VMAN mode");setup.VmanMode=1;setup.VmanSeconds=0;Reject(setup.Validate,"Empty VMAN time limit");
            setup.VmanSeconds=120;setup.Validate();Check(setup.VmanSeconds==120,"Timed VMAN accepts an authored duration.");
            string Float(float value)=>SimulationTraceComparer.Float(value);
            Check(CustomMapPhysics.Matches("body/velocity/x",Float(0),Float(-.00006103515625f),true),"Measured custom-tile native velocity residuals can reconstruct a search prefix.");
            Check(!CustomMapPhysics.Matches("body/position/y",Float(0),Float(.001f),true),"Meaningful position drift still rejects a custom-map search prefix.");
            Check(!CustomMapPhysics.Matches("body/velocity/x",Float(0),Float(.000001f),false),"Built-in map prefix checks remain bit exact.");
            Check(!CustomMapPhysics.Matches("player/momentum/x",Float(0),Float(.000001f),true)&&!CustomMapPhysics.Matches("vman/time","0","1",true),"Movement state and clocks never use custom-map body tolerances.");
            Check(!CustomMapPhysics.Matches("body/velocity/x",Float(0),Float(float.NaN),true),"Non-finite custom-map physics residuals are rejected.");
            PrefixChecks(Check,Reject);
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
        Console.WriteLine("Custom-map identity and VMAN setup checks passed: "+count);
    }

    private static void PrefixChecks(Action<bool,string> check,Action<Action,string> reject)
    {
        float gravityStep=-50f*.0199999921f*27f;
        string F(float value)=>SimulationTraceComparer.Float(value);
        SimulationSample Sample(int frame,int pu,string phase="frame-end")=>new SimulationSample {Frame=frame,Pu=pu,Phase=phase,
            Fields=new SortedDictionary<string,string>(StringComparer.Ordinal) {
                ["movement/Velocity/y"]=F(0),["movement/Velocity/x"]=F(460),["movement/momentum/y"]=F(0),
                ["movement/recentlyJumped"]="False",["body/position/x"]=F(138),["body/position/y"]=F(56),
                ["body/velocity/y"]=F(0),["random/s0"]="1234",["economy/currencies/0"]="0000000000000000"}};
        SimulationSample[] Expected()=>new[]{Sample(0,0,"start"),Sample(85,1),Sample(86,0),Sample(87,1),Sample(88,0)};
        SimulationSample[] Actual()
        {
            var samples=Expected();
            for(int i=1;i<=2;i++) {samples[i].Fields["movement/Velocity/y"]=F(gravityStep);samples[i].Fields["body/velocity/y"]=F(.000439f);}
            return samples;
        }
        NativePrefixValidator Replay(SimulationSample[] expected,SimulationSample[] actual,bool custom=true,bool solid=true,float? step=null)
        {
            var validator=new NativePrefixValidator(expected,custom);
            foreach(var sample in actual)validator.Match(sample,step ?? gravityStep,solid);
            validator.Complete(); return validator;
        }
        var native=Expected();var reconstructed=Actual();
        var recovered=Replay(native,reconstructed);
        check(recovered.RecoveredContactSamples==2 && recovered.AdditionalVelocityResiduals==2,"A single-PU custom-composite contact clear can recover before the search anchor.");
        check(reconstructed[1].Fields["movement/Velocity/y"]==F(gravityStep) && native[1].Fields["movement/Velocity/y"]==F(0),"Prefix verification never replaces simulated or witnessed state to conceal a mismatch.");
        reject(()=>Replay(native.Take(2).ToArray(),reconstructed.Take(2).ToArray()),"Searching at a mismatching anchor instead of after recovery");
        reject(()=>Replay(native,reconstructed,false),"Contact recovery on a built-in map");
        reject(()=>Replay(native,reconstructed,solid:false),"Contact recovery without a solid contact");
        reject(()=>Replay(native,reconstructed,step:float.NaN),"Contact recovery with invalid gravity");
        reject(()=>Replay(native,reconstructed,step:gravityStep/2),"An unrelated movement-velocity difference");
        void Mutation(string field,string value,int index=1)
        {var actual=Actual();actual[index].Fields[field]=value;Replay(Expected(),actual);}
        reject(()=>Mutation("body/position/y",F(56.001f)),"Intermediate position drift despite a matching final anchor");
        reject(()=>Mutation("body/velocity/y",F(.01f)),"Meaningful intermediate velocity drift");
        reject(()=>Mutation("body/velocity/y",F(.000439f),4),"A contact remainder exceeding the usual bound at the final anchor");
        reject(()=>Mutation("movement/Velocity/x",F(459)),"Unrelated internal movement drift");
        reject(()=>Mutation("random/s0","1235"),"Intermediate RNG drift even if it later clears");
        reject(()=>Mutation("economy/currencies/0","3ff0000000000000"),"Intermediate currency drift even if it later clears");
        reject(()=>Mutation("movement/momentum/y",F(1)),"Different momentum");
        foreach(string field in new[]{"movement/recentlyJumped","movement/momentum/y"}) {
            var expected=Expected();var actual=Actual();
            expected[1].Fields[field]=actual[1].Fields[field]=field.EndsWith("recentlyJumped") ? "True" : F(5);
            reject(()=>Replay(expected,actual),"Recovery while jumping or retaining vertical momentum");
        }
        var stale=Actual();var schedule=Expected();schedule[2].Pu=stale[2].Pu=1;
        reject(()=>Replay(schedule,stale),"A contact mismatch surviving a second PU");
        var noPu=Actual();var noPuExpected=Expected();noPuExpected[1].Pu=noPu[1].Pu=0;
        reject(()=>Replay(noPuExpected,noPu),"A new contact mismatch appearing outside a PU");
        var incomplete=new NativePrefixValidator(Expected(),true);incomplete.Match(Expected()[0],gravityStep,true);
        reject(incomplete.Complete,"Incomplete prefix witnesses");
        var extra=Actual();extra[1].Fields["unknown"]="1";
        reject(()=>Replay(Expected(),extra),"A changed witness field set");
    }
}
