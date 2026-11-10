using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Search;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class MovieStorageTests
{
    internal static void Run()
    {
        int checks=0;
        void Check(bool condition,string message){if(!condition)throw new Exception(message);checks++;}
        void Reject(Action action,string message){try{action();}catch{checks++;return;}throw new Exception("Expected rejection: "+message);}
        string root=Path.Combine(Path.GetTempPath(),"tas-movie-storage-"+Guid.NewGuid().ToString("N"));
        try
        {
            var store=new MovieStore(root);var movie=new Movie{BootstrapVersion=4,FrameRate=280,StartState=new StateProbe{MovementHash="start",MovementFields=new SortedDictionary<string,string>{{"unchanged","1"},{"removed","x"}}},
                StartWorld=new WorldWitness{Exact={{"random","test-rng"},{"scene/Collider2D#1/enabled","True"}},Numbers={{"cash",1},{"scene/Collider2D#1/position/x",3.25}}}};
            var previous=movie.StartWorld.CompactForReplay();
            for(int i=0;i<24;i++)
            {
                var p=new StateProbe{X=i+.125f,Y=-0f,RenderX=i+.125f,RenderY=-0f,VelocityX=1,BodyVelocityX=1,
                    Grounded=i%2==0,OnWall=i%3==0,Dashing=i%4==0,FacingRight=true,MenuOpen=i%5==0,GamePaused=i%6==0,
                    AirJumps=1,AirDashes=2,Cutscene=i%3,MovementHash="hash-"+i,
                    MovementFields=new SortedDictionary<string,string>{{"unchanged","1"},{"value",(i/4).ToString()}}};
                if(i==5)p.MovementFields=null;
                if(i==6)p.MovementFields=new SortedDictionary<string,string>();
                if(i==8){p.RenderX+=.001f;p.BodyVelocityY=-3;p.MomentumY=-0f;}
                var input=new FrameInput{X=.25f,Y=-.5f,Held=Buttons.Jump|Buttons.Dash,ExactEdges=i%2==0,Press=Buttons.Jump,Release=Buttons.Jump};
                var frame=new MovieFrame{Input=input,Expected=i==9?null:p,FixedTicks=i%4==0?1:0,PuPhase=.01+i*1e-8};
                if(i==4||i==12){frame.World=new WorldWitness{Exact=new SortedDictionary<string,string>(previous.Exact),Numbers=new SortedDictionary<string,double>(previous.Numbers)};frame.World.Numbers["cash"]=i;frame.World.Exact.Remove("random");frame.World.Exact["added"]="state";previous=frame.World;}
                movie.Frames.Add(frame);
            }
            string expanded=JsonConvert.SerializeObject(movie);string hash=SearchFiles.Hash(movie);
            using(var sha=SHA256.Create())Check(hash==Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(expanded))),"Streaming source hash preserves the previous expanded-model fingerprint.");
            store.Save("roundtrip",movie);var restored=store.Load("roundtrip");
            string after=JsonConvert.SerializeObject(restored);
            Check(SearchFiles.Hash(restored)==hash,"Compact storage preserves every expanded input, probe, dictionary, hash and world value.");
            Check(SearchFiles.Hash(restored)==hash,"Compression and roundtrip never change source or prefix hashes.");
            var cultureOrder=new StateProbe{MovementFields=new SortedDictionary<string,string>{{"a","1"},{"B","2"}}};
            var ordinalOrder=new StateProbe{MovementFields=new SortedDictionary<string,string>(StringComparer.Ordinal){{"a","1"},{"B","2"}}};
            Check(SearchFiles.Hash(cultureOrder)==SearchFiles.Hash(ordinalOrder),"Dictionary ordering is excluded from canonical source fingerprints.");
            Check(restored.Frames[0].Input.Pressed(Buttons.Jump,restored.Frames[0].Input)&&restored.Frames[0].Input.Released(Buttons.Jump,restored.Frames[0].Input),"Exact held retaps survive storage.");
            Check(restored.Frames[1].Input.ExactEdges==false&&restored.Frames[1].Input.Press==Buttons.Jump,"Unused inferred-edge bits remain lossless for source fingerprints.");
            Check(restored.Frames[9].Expected==null,"Authored rows remain unverified.");
            Check(restored.Frames[5].Expected.MovementFields==null&&restored.Frames[6].Expected.MovementFields.Count==0,"Null and empty movement dictionaries remain distinct.");
            Check(restored.Frames[0].Expected.MovementFields.ContainsKey("value")&&!restored.Frames[0].Expected.MovementFields.ContainsKey("removed"),"Movement delta removes stale keys.");
            Check(restored.Frames[4].World.Exact.ContainsKey("added")&&!restored.Frames[4].World.Exact.ContainsKey("random")&&restored.Frames[12].World.Numbers["cash"]==12,"World deltas add, remove and update fields.");
            Check(movie.StartWorld.Difference(restored.StartWorld)==null,"The complete starting-world witness is retained.");
            Check(ProbeComparer.Difference(movie.Frames[8].Expected,restored.Frames[8].Expected)==null,"Distinct interpolation, momentum and body velocity remain observable.");
            Check(BitConverter.SingleToInt32Bits(restored.Frames[8].Expected.MomentumY)==BitConverter.SingleToInt32Bits(-0f),"Signed zero is preserved.");
            var zeros=movie.CopyForEdit();zeros.StartPuPhase=BitConverter.Int64BitsToDouble(long.MinValue);zeros.StartWorld=new WorldWitness{Numbers={{"zero",BitConverter.Int64BitsToDouble(long.MinValue)}}};
            zeros.Frames=new List<MovieFrame>{new MovieFrame{Input=new FrameInput{X=float.Epsilon,Y=-0f},Expected=new StateProbe{X=float.MaxValue,Y=float.Epsilon,RenderX=float.MaxValue,RenderY=float.Epsilon},PuPhase=zeros.StartPuPhase,FixedTicks=0}};
            store.Save("edge-numbers",zeros);Check(SearchFiles.Hash(store.Load("edge-numbers"))==SearchFiles.Hash(zeros),"Subnormals, finite float extremes and signed-zero clocks/world values survive compact storage.");
            var clone=MovieStore.Clone(movie);clone.Frames[0].Expected.MovementFields["value"]="changed";
            Check(movie.Frames[0].Expected.MovementFields["value"]!="changed","Clone data is detached from the source.");
            File.WriteAllText(store.PathFor("expanded"),expanded);var previousLoader=JsonConvert.DeserializeObject<Movie>(expanded);Check(SearchFiles.Hash(store.Load("expanded"))==SearchFiles.Hash(previousLoader),"Expanded movies retain the previous loader's semantics.");
            var compact=JObject.Parse(File.ReadAllText(store.PathFor("roundtrip")));
            Check((string)compact["Storage"]==MovieJson.EncodingName&&compact["StartWorld"].Type==JTokenType.String,"Storage is explicitly identified and starting-world data is packed.");
            var rows=(JArray)compact["Frames"];
            Check(rows[0]["Movement"]?["unchanged"]==null&&rows[1]["Movement"]==null,"Unchanged movement values are not repeated on disk.");
            void Bad(Action<JObject> change,string reason){var broken=(JObject)compact.DeepClone();change(broken);File.WriteAllText(store.PathFor("broken"),broken.ToString());Reject(()=>store.Load("broken"),reason);}
            Bad(d=>d["Storage"]="future-encoding","Unknown storage protocol rejected.");
            Bad(d=>d["StartWorld"]="not gzip!","Invalid compressed world rejected.");
            Bad(d=>d["InputRuns"][0]["Input"]=new JArray(0,0,0,1),"Incomplete edge tuple rejected.");
            Bad(d=>d["InputRuns"][0]["Duration"]=0,"Zero-duration input run rejected.");
            Bad(d=>d["InputRuns"][0]["Duration"]=-1,"Negative-duration input run rejected.");
            Bad(d=>d["InputRuns"][0]["Duration"]=int.MaxValue,"Overlong input run rejected before expansion.");
            Bad(d=>d["InputRuns"][1]["StartFrame"]=0,"Overlapping input runs rejected.");
            Bad(d=>d["InputRuns"][1]["StartFrame"]=99,"Gaps in input runs rejected.");
            Bad(d=>((JArray)d["InputRuns"]).RemoveAt(1),"Missing input change rejected by explicit frame positions.");
            Bad(d=>((JArray)d["InputRuns"]).RemoveAt(((JArray)d["InputRuns"]).Count-1),"Missing final input run rejected.");
            Bad(d=>((JArray)d["Frames"]).RemoveAt(0),"Missing verification frame rejected by FrameCount.");
            Bad(d=>d["FrameCount"]=Movie.MaxFrames+1,"Declared frame count is bounded.");
            Bad(d=>((JObject)d).Remove("FrameCount"),"Missing total frame count rejected.");
            Bad(d=>d["Frames"][0]["Input"]=new JArray(0,0,0),"Per-frame overrides cannot hide missing input runs.");
            Bad(d=>d["Frames"][0]["State"][0]=128,"Unknown movement flags rejected.");
            Bad(d=>((JObject)d["Frames"][0]).Remove("MovementHash"),"Verified rows cannot silently lose their hash.");
            Bad(d=>d["Frames"][0]["unexpected"]=true,"Unknown frame fields rejected.");
            Bad(d=>d["StartState"]["unexpected"]=true,"Unknown start-probe fields rejected.");
            Bad(d=>d["Frames"][0]["Position"]=new JArray("1",0),"Text is not accepted as a coordinate.");
            string saved=File.ReadAllText(store.PathFor("roundtrip"));File.WriteAllText(store.PathFor("broken"),saved+" {}");Reject(()=>store.Load("broken"),"Trailing documents rejected by streaming import.");
            var many=movie.CopyForEdit();many.Frames=Enumerable.Range(0,1000).Select(_=>new MovieFrame{Input=default,Expected=movie.Frames[0].Expected,FixedTicks=0,PuPhase=.01}).ToList();
            store.Save("many",many);
            Check(new FileInfo(store.PathFor("many")).Length<JsonConvert.SerializeObject(many,Formatting.Indented).Length*.6,"Repeated data is removed rather than only hiding newlines.");
            var repeated=JObject.Parse(File.ReadAllText(store.PathFor("many")));
            Check((int)repeated["FrameCount"]==1000&&((JArray)repeated["InputRuns"]).Count==1&&(int)repeated["InputRuns"][0]["Duration"]==1000,"One unchanged input sample stores one explicit thousand-frame duration.");
            Check(((JArray)repeated["Frames"]).Count==1000&&repeated["Frames"][0]["Input"]==null,"Per-frame verification remains intact without repeating inputs.");
            var hold=movie.CopyForEdit();hold.Frames=new List<MovieFrame>();
            hold.Frames.Add(new MovieFrame{Input=new FrameInput{Held=Buttons.Jump,ExactEdges=true,Press=Buttons.Jump}});
            for(int i=0;i<7;i++)hold.Frames.Add(new MovieFrame{Input=new FrameInput{Held=Buttons.Jump,ExactEdges=true}});
            hold.Frames.Add(new MovieFrame{Input=new FrameInput{ExactEdges=true,Release=Buttons.Jump}});
            store.Save("held-jump",hold);var held=JObject.Parse(File.ReadAllText(store.PathFor("held-jump")));
            Check(((JArray)held["InputRuns"]).Count==3&&(int)held["InputRuns"][1]["Duration"]==7,"Press, steady hold and release are separate input runs.");
            var heldRead=store.Load("held-jump");
            Check(heldRead.Frames.Count==9&&heldRead.Frames.Skip(1).Take(7).All(f=>f.Input.Press==0&&f.Input.Release==0)&&heldRead.Frames[8].Input.Release==Buttons.Jump,"Expanding held input never repeats its initial press edge.");
            Check(SearchFiles.Hash(heldRead)==SearchFiles.Hash(hold),"Run expansion preserves the full logical source fingerprint.");
            var taps=hold.CopyForEdit();taps.Frames=Enumerable.Range(0,5).Select(_=>new MovieFrame{Input=new FrameInput{ExactEdges=true,Press=Buttons.Jump,Release=Buttons.Jump}}).ToList();
            store.Save("repeated-taps",taps);var tapRead=store.Load("repeated-taps");
            Check(tapRead.Frames.All(f=>f.Input.Pressed(Buttons.Jump,default)&&f.Input.Released(Buttons.Jump,default))&&SearchFiles.Hash(tapRead)==SearchFiles.Hash(taps),"Deliberate same-frame taps on every frame remain repeated events after expansion.");
            hold.Frames.Clear();store.Save("empty",hold);var empty=store.Load("empty");
            Check(empty.Frames.Count==0,"An empty movie has zero runs and a zero FrameCount.");
            var signs=movie.CopyForEdit();signs.Frames=new List<MovieFrame>{new MovieFrame{Input=new FrameInput{X=0f}},new MovieFrame{Input=new FrameInput{X=BitConverter.Int32BitsToSingle(int.MinValue)}}};
            store.Save("input-signs",signs);var signRuns=JObject.Parse(File.ReadAllText(store.PathFor("input-signs")));
            Check(((JArray)signRuns["InputRuns"]).Count==2&&SearchFiles.Hash(store.Load("input-signs"))==SearchFiles.Hash(signs),"Input coalescing preserves signed-zero axes rather than merging their bit patterns.");
            Console.WriteLine("Compact movie storage checks passed: "+checks);
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
