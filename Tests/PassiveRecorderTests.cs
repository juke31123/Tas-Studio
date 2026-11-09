using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Igtap.Tas.Core.Search;
using Igtap.Tas.Recorder;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
internal static class PassiveRecorderTests
{
    public static void Run()
    {
        int checks=0;void Check(bool v,string m){if(!v)throw new Exception(m);checks++;}
        void Reject(Action action){try{action();}catch(InvalidDataException){checks++;return;}throw new Exception("Expected passive capture rejection.");}
        PassiveFrame Frame(int index,int pus){var f=new PassiveFrame {Frame=index,EngineFrame=100+index,PhysicsUpdates=pus};for(int p=1;p<=pus;p++){f.Samples.Add(new PassiveSample {Phase="before-physics",Pu=p});f.Samples.Add(new PassiveSample {Phase="after-physics",Pu=p});}f.Samples.Add(new PassiveSample {Phase="before-input-update",Pu=pus,Input=new Igtap.Tas.Core.FrameInput {X=.5f,ExactEdges=true}});f.Samples.Add(new PassiveSample {Phase="frame-end",Pu=pus});f.Samples.Last().Fields["clock/delta"]=index==0 ? "3c23d70a" : "3c888889";return f;}
        string root=Path.Combine(Path.GetTempPath(),"passive-recorder-"+Guid.NewGuid().ToString("N"));
        try {
            string part=Path.Combine(root,"part-0001");Directory.CreateDirectory(part);string path=Path.Combine(part,"gameplay.frames.jsonl.gz");
            using(var journal=new GameplayJournal(path)) {
                Reject(()=>journal.Append(Frame(1,0)));var bad=Frame(0,1);bad.Samples.RemoveAt(1);Reject(()=>journal.Append(bad));
                Check(journal.Append(Frame(0,0)),"Normal frames without a PU are retained.");
                Check(journal.Append(Frame(1,3)),"A long native frame may contain several physics updates.");
                Check(journal.Frames==2 && journal.Pus==3,"Native frame and physics counters stay separate.");
            }
            using(var r=new StreamReader(new GZipStream(File.OpenRead(path),CompressionMode.Decompress))) {
                var first=JsonConvert.DeserializeObject<PassiveFrame>(r.ReadLine());var second=JsonConvert.DeserializeObject<PassiveFrame>(r.ReadLine());
                Check(first.Samples.Last().Fields["clock/delta"]!=second.Samples.Last().Fields["clock/delta"],"Variable native frame deltas survive export.");
                Check(first.Samples[0].Input.X==.5f && second.Samples.Count(s=>s.Phase=="after-physics")==3,"Analog inputs and all native PU observations roundtrip.");
                Check(r.ReadLine()==null,"The background writer flushes every accepted frame on stop.");
            }
            using(var limited=new GameplayJournal(Path.Combine(root,"limited.gz"),0,1)){Check(!limited.Append(Frame(0,0)) && limited.Frames==0,"The byte bound rejects a whole frame instead of dropping individual samples.");}
            using(var continuation=new GameplayJournal(Path.Combine(root,"continuation.gz"),17)){Check(continuation.Append(Frame(17,0)),"A new scene part can continue the global frame numbering.");}
            File.WriteAllText(Path.Combine(root,"environment.json"),"{}");File.WriteAllText(Path.Combine(part,"world.json"),"{}");File.WriteAllText(Path.Combine(root,"unrelated.dll"),"exclude");
            string zip=Path.Combine(root,"data.zip");var metadata=new JObject {["schema"]=2,["mode"]="passive-gameplay"};PassiveBundle.Write(root,zip,metadata);
            using(var archive=ZipFile.OpenRead(zip)){Check(archive.GetEntry("capture.json")!=null && archive.GetEntry("part-0001/gameplay.frames.jsonl.gz")!=null,"A passive bundle contains the segmented native observations.");Check(!archive.Entries.Any(e=>e.FullName.EndsWith(".dll") || e.FullName.EndsWith(".tas.json")),"Passive data is not mislabeled as a replayable TAS and includes no binaries.");}
            Check((string)metadata["files"]["part-0001/gameplay.frames.jsonl.gz"]["sha256"]==Igtap.Tas.Core.Simulation.SimulationTraceComparer.FileHash(path),"Manifest hashes bind the exact recorded data.");
            Console.WriteLine("Passive gameplay recorder checks passed: "+checks);
        }finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
