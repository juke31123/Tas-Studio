using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Search;
using Igtap.Tas.Core.Simulation;
using Igtap.Tas.Core.Validation;

internal static class RecorderTests
{
    public static void Run()
    {
        int checks=0;
        void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
        void Reject(Action action){try{action();}catch(InvalidDataException){checks++;return;}throw new Exception("Expected recorder rejection.");}
        SimulationSample Sample(int f,int p,string phase)=>new SimulationSample {Frame=f,Pu=p,Phase=phase,
            Fields=new SortedDictionary<string,string>{{"signed-zero","80000000"}}};
        SimulationTrace Header()=>new SimulationTrace {Fps=50,StartPhase=.01,GameHash="game",WorldHash="world",AdvanceMode="before-pacing",CaptureOrbs=true,CaptureMovers=true,
            Samples=new List<SimulationSample>{Sample(0,0,"start")}};
        string root=Path.Combine(Path.GetTempPath(),"tas-recorder-"+Guid.NewGuid().ToString("N"));
        try {
            string capture=Path.Combine(root,"capture");var header=Header();var journal=new CaptureJournal(capture,header);
            var first=new MovieFrame {Input=new FrameInput {X=1,Held=Buttons.Jump,Press=Buttons.Jump,ExactEdges=true},FixedTicks=1};
            var samples=new[]{Sample(0,1,"before-pu"),Sample(0,1,"after-pu"),Sample(0,1,"frame-end")};
            Reject(()=>journal.Append(1,first,samples));
            Reject(()=>journal.Append(0,first,new[]{Sample(0,1,"after-pu"),Sample(0,1,"frame-end")}));
            Check(journal.Frames==0 && journal.Samples==1,"Rejected frames must not alter committed observations.");
            Check(journal.Append(0,first,samples),"A complete PU frame is journaled.");
            var second=new MovieFrame {Input=new FrameInput {X=-1,ExactEdges=true},FixedTicks=0};
            Check(journal.Append(1,second,new[]{Sample(1,0,"frame-end")}),"Frames without a PU are retained.");
            Check(journal.Frames==2 && journal.Pus==1 && journal.Samples==5,"Streaming capture counts input frames and PUs separately.");
            journal.Dispose();
            Check(File.ReadAllLines(Path.Combine(capture,"frames.jsonl")).Length==2,"The incremental input journal contains complete committed frames.");
            var movie=new Movie {BootstrapVersion=4,FrameRate=50,Frames=new List<MovieFrame>{first,second}};
            journal.FinishTrace(movie);
            var actual=SimulationTrace.Read(Path.Combine(capture,"native.trace.json"));actual.Validate();
            var expected=Header();expected.Inputs.Add(first.Input);expected.Inputs.Add(second.Input);expected.Samples.AddRange(samples);expected.Samples.Add(Sample(1,0,"frame-end"));
            Check(SimulationTraceComparer.Compare(expected,actual).Passed,"The streamed native trace matches exact sample order, input edges and signed-zero bits.");
            Reject(()=>journal.FinishTrace(new Movie()));
            using(var limited=new CaptureJournal(Path.Combine(root,"limited"),Header(),1)) {
                Check(!limited.Append(0,first,samples),"The diagnostic byte bound stops before appending a partial frame.");
                Check(limited.Frames==0 && limited.Samples==1,"Byte-limit truncation preserves the previous complete prefix.");
            }
            foreach(string name in ValidationBundle.Required.Where(n=>n!="native.trace.json"))File.WriteAllText(Path.Combine(capture,name),"{}");
            File.WriteAllText(Path.Combine(capture,"unrelated.dll"),"excluded");
            string zip=Path.Combine(root,"data.zip");var manifest=new ValidationManifest {Name="test",Frames=2,Fps=50,Samples=5,Pus=1,Complete=true};
            ValidationBundle.Write(capture,zip,manifest);
            using(var archive=ZipFile.OpenRead(zip)) {
                Check(archive.Entries.Count==ValidationBundle.Required.Length+1,"Only required data and the manifest enter a minimal bundle.");
                Check(!archive.Entries.Any(e=>e.FullName.EndsWith(".dll") || e.FullName.Contains("jsonl")),"Game binaries, unrelated files and recovery journals stay outside the shareable ZIP.");
                var entry=archive.GetEntry("native.trace.json");using(var stream=entry.Open())using(var sha=System.Security.Cryptography.SHA256.Create())
                    Check(Convert.ToBase64String(sha.ComputeHash(stream))==manifest.Files["native.trace.json"].Sha256,"Manifest hashes bind the actual archive bytes.");
            }
            Check(!manifest.Files.ContainsKey("replay-report.json"),"A capture does not claim replay verification before it runs.");
            File.WriteAllText(Path.Combine(capture,"replay-report.json"),"{\"Passed\":false}");manifest.ReplayVerification="Not passed";ValidationBundle.Write(capture,zip,manifest);
            using(var archive=ZipFile.OpenRead(zip))Check(archive.GetEntry("replay-report.json")!=null,"Repacking attaches a failed replay report without discarding useful data.");
            Check(manifest.Files["recording.tas.json"].Bytes==2,"Manifest sizes describe payload bytes.");
            Reject(()=>ValidationBundle.Write(capture,zip,new ValidationManifest()));
            Check(File.Exists(zip) && !File.Exists(zip+".tmp"),"Rejected export retains the last successful bundle.");
            Console.WriteLine("Validation recorder checks passed: "+checks);
        } finally {if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
