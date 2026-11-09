using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Validation;
using Igtap.Tas.Core.Simulation;
using Igtap.Tas.Recorder;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class PassiveCaptureAuditTests
{
    public static void Run()
    {
        int checks=0;
        void Check(bool v,string why){if(!v)throw new Exception(why);checks++;}
        void Reject(Action action){try{action();}catch(InvalidDataException){checks++;return;}throw new Exception("Expected capture rejection.");}
        string root=Path.Combine(Path.GetTempPath(),"capture-audit-"+Guid.NewGuid().ToString("N"));
        try
        {
            string part=Path.Combine(root,"part-0001");Directory.CreateDirectory(part);
            foreach(string name in new[]{"world.json","starting-state.json","animation.json"})File.WriteAllText(Path.Combine(part,name),"{}");
            File.WriteAllText(Path.Combine(root,"environment.json"),"{}");
            File.WriteAllBytes(Path.Combine(part,"contacts.jsonl.gz"),Array.Empty<byte>());
            File.WriteAllText(Path.Combine(part,"contacts-status.json"),"{\"Events\":0,\"DroppedAtLimit\":0,\"Error\":\"\"}");
            PassiveFrame Frame(int i,int pus,FrameInput input)
            {
                var f=new PassiveFrame {Frame=i,EngineFrame=100+i,PhysicsUpdates=pus};
                PassiveSample Sample(string phase,int pu)=>new PassiveSample {Phase=phase,Pu=pu,Input=input};
                for(int p=1;p<=pus;p++){f.Samples.Add(Sample("before-fixed-update",p));f.Samples.Add(Sample("before-physics",p));f.Samples.Add(Sample("after-physics",p));}
                f.Samples.Add(Sample("before-input-update",pus));f.Samples.Add(Sample("frame-end",pus));
                f.Samples.Last().Fields["clock/unscaled-delta"]=SimulationTraceComparer.Float(i==0 ? .01f : .014f);
                f.Samples[f.Samples.Count-2].Fields["movement/jumpBuffer"]="False";f.Samples.Last().Fields["movement/jumpBuffer"]=(input.Press & Buttons.Jump)!=0 ? "True" : "False";
                return f;
            }
            var first=Frame(0,0,new FrameInput {ExactEdges=true});
            var second=Frame(1,0,new FrameInput {ExactEdges=true,Held=Buttons.Jump|Buttons.Menu,Press=Buttons.Jump|Buttons.Menu});
            var third=Frame(2,2,new FrameInput {ExactEdges=true,Release=Buttons.Jump|Buttons.Menu});
            third.Samples[0].Input=new FrameInput {ExactEdges=true,Held=Buttons.Jump|Buttons.Menu};
            string journal=Path.Combine(part,"gameplay.frames.jsonl.gz"),zip=Path.Combine(root,"capture.zip");
            void WriteFrames(){using(var gzip=new GZipStream(File.Create(journal),CompressionLevel.Fastest))using(var w=new StreamWriter(gzip))foreach(var f in new[]{first,second,third})w.WriteLine(JsonConvert.SerializeObject(f));}
            JObject metadata=null;
            void Bundle(){metadata=new JObject {["schema"]=2,["mode"]="passive-gameplay",["name"]="fixture",["complete"]=true,["frames"]=3,["pus"]=2,["parts"]=new JArray()};PassiveBundle.Write(root,zip,metadata);}
            WriteFrames();Bundle();var report=PassiveCaptureAudit.Read(zip);
            Check(report.IntegrityPassed && !report.SimulatorValidated,"Integrity is separate from full simulator parity.");
            Check(report.Frames==3 && report.PhysicsUpdates==2 && report.FramesWithoutPhysics==2,"Zero and multiple PU frames remain native frames.");
            Check(report.InputChangesWithoutPhysics==1 && report.MenuPresses==1,"Jump and pause actions on a non-PU frame are retained.");
            Check(report.FramesWithDifferentPhysicsInput==1,"FixedUpdate can observe the previous input before the new dynamic input update.");
            Check(report.Inputs.Single(x=>x.Frame==1).ChangedFields.Contains("movement/jumpBuffer"),"Observed Update effects retain their input-frame witness.");
            Check(Math.Abs(report.UnscaledSeconds-(double).01f-2*(double).014f)<1e-12,"Variable native deltas are never snapped to target FPS.");
            Check(report.Warnings.Any(w=>w.Contains("Part metadata")),"Existing recorder bundles with empty part metadata use hashed journals and report the gap.");
            Check(PassiveCaptureAudit.Read(zip,"different-build").PreparedGameMatches==false,"A foreign game build must not be reported as same-build evidence.");
            int parts=0,visited=0;
            PassiveCaptureAudit.Visit(zip,(name,world,start)=>parts++,(name,frame)=>visited++);
            Check(parts==1 && visited==3,"The replay visitor delivers validated part snapshots and every native frame without resampling.");
            Check(PassiveCaptureAudit.Double(SimulationTraceComparer.Double(123.456))==123.456,"Native double-clock bits retain their precision.");
            second.Samples[0].Input.Press=(Buttons)128;WriteFrames();Bundle();Reject(()=>PassiveCaptureAudit.Read(zip));second.Samples[0].Input.Press=Buttons.Jump|Buttons.Menu;
            third.Samples.RemoveAt(2);WriteFrames();Bundle();Reject(()=>PassiveCaptureAudit.Read(zip));third=Frame(2,2,new FrameInput {ExactEdges=true,Release=Buttons.Jump|Buttons.Menu});
            third.Frame=4;WriteFrames();Bundle();Reject(()=>PassiveCaptureAudit.Read(zip));third.Frame=2;
            third.Samples.Last().Fields["clock/unscaled-delta"]="7fc00000";WriteFrames();Bundle();Reject(()=>PassiveCaptureAudit.Read(zip));third.Samples.Last().Fields["clock/unscaled-delta"]=SimulationTraceComparer.Float(.014f);
            WriteFrames();Bundle();using(var archive=ZipFile.Open(zip,ZipArchiveMode.Update)){using(var w=new StreamWriter(archive.CreateEntry("../world.json").Open()))w.Write("{}");}Reject(()=>PassiveCaptureAudit.Read(zip));
            Bundle();using(var archive=ZipFile.Open(zip,ZipArchiveMode.Update)){using(var w=new StreamWriter(archive.CreateEntry("environment.json").Open()))w.Write("{}");}Reject(()=>PassiveCaptureAudit.Read(zip));
            Bundle();using(var archive=ZipFile.Open(zip,ZipArchiveMode.Update)){archive.GetEntry("environment.json").Delete();using(var w=new StreamWriter(archive.CreateEntry("environment.json").Open()))w.Write("[]");}Reject(()=>PassiveCaptureAudit.Read(zip));
            Reject(()=>PassiveCaptureAudit.Float("NaN"));
            Console.WriteLine("Passive capture audit checks passed: "+checks);
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
