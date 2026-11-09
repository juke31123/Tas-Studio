using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using IGTAP.EngineSim;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Search;
using Igtap.Tas.Core.Simulation;
using Igtap.Tas.Core.Validation;
using Newtonsoft.Json.Linq;

internal static class PassiveReplay
{
    internal sealed class Difference { public string Part,Phase,Field,Expected,Actual; public int Frame,Pu; }
    internal sealed class Report
    {
        public bool Imported,Passed,GameBuildMatches;
        public int Frames,PhysicsUpdates,ComparedSamples,MatchingSamples;
        public int SamplesAfterInitialFrame,MatchingAfterInitialFrame;
        public bool PostInitialFramePassed;
        public int FirstSpringFrame = -1, SamplesFromFirstSpring, MatchingFromFirstSpring;
        public bool PostFirstSpringPassed;
        public Difference FirstDifference, LastDifference;
        public SortedDictionary<string,int> FirstDifferenceFields = new SortedDictionary<string,int>(StringComparer.Ordinal);
        public List<Difference> Differences=new List<Difference>();
        public List<object> PhysicsDifferences=new List<object>();
        public string Error;
        public string Scope="Continuous replay using recorded native frame/PU clocks and action edges. Compared player scalar/vector state, body/render poses, velocity and player collider state. Initial native contact caches and pending timer deadlines remain coverage limits. No expected state is written back during replay.";
        public string[] Warnings;
    }
    public static int Run(string path,string output)
    {
        Directory.CreateDirectory(output); var report=new Report(); Simulation sim=null; JObject pendingStart=null;
        string partName=null; JObject current=null; int frameNumber=0,firstPartFrame=-1; bool initialised=false,springSeen=false;
        try
        {
            var prepared=SearchFiles.Read<JObject>(Path.Combine(AppContext.BaseDirectory,"engine-host.manifest.json"));
            string expectedGame=Convert.ToBase64String(Convert.FromHexString((string)prepared["GameSha256"]));
            var audit=PassiveCaptureAudit.Read(path,expectedGame); report.GameBuildMatches=audit.PreparedGameMatches==true; report.Warnings=audit.Warnings;
            PassiveCaptureAudit.Visit(path,(part,world,start)=>
            {
                partName=part; pendingStart=start; initialised=false;firstPartFrame=-1;springSeen=false;
                var folder=Path.Combine(output,part);Directory.CreateDirectory(folder);
                world["time"]=new JObject {["fixedDeltaTime"]=start["fixedDelta"].DeepClone(),["timeScale"]=start["timeScale"].DeepClone()};
                if(world["layerNames"]==null)world["layerNames"]=new JArray(Enumerable.Range(0,32).Select(i=>i==0?"Default":i==2?"Ignore Raycast":i==5?"UI":""));
                DeriveSingletons(world);
                OrderHierarchy(world);
                SearchFiles.Write(Path.Combine(folder,"world.json"),world);
                sim=new Simulation(World.Load(Path.Combine(folder,"world.json")));
                var node=world["objects"].OfType<JObject>().Single(n=>n["components"].Any(c=>(int?)c["id"]==(int)start["playerId"]));
                var body=start["body"];var boxes=node["components"].OfType<JObject>().Where(c=>(string)c["type"]=="UnityEngine.BoxCollider2D").Select(c=>c["props"]).ToArray();
                var state=new JObject {["movement"]=start["movement"].DeepClone(),["engine"]=new JArray(),["courses"]=new JArray(),["pending"]=new JArray(),
                    ["position"]=node["position"].DeepClone(),["localScale"]=node["localScale"].DeepClone(),["transformRotation"]=node["rotation"].DeepClone(),
                    ["bodyPosition"]=body["position"].DeepClone(),["velocity"]=body["linearVelocity"].DeepClone(),["rotation"]=body["rotation"].DeepClone(),["angularVelocity"]=body["angularVelocity"].DeepClone(),
                    ["random"]=new JArray(Enumerable.Range(0,4).Select(i=>start["random"]["s"+i].DeepClone())),
                    ["colliders"]=new JArray(boxes.Select(c=>new JArray(c["offset"]["x"],c["offset"]["y"],c["size"]["x"],c["size"]["y"],c["excludeLayers"],c["enabled"])))};
                SearchFiles.Write(Path.Combine(folder,"start.json"),state);
                using var doc=JsonDocument.Parse(state.ToString());sim.LoadStart(doc.RootElement,false);
                var palette=typeof(Movement).GetField("afterimageColors",BindingFlags.Instance|BindingFlags.NonPublic);
                if(palette?.GetValue(sim.Player)==null) palette?.SetValue(sim.Player,new UnityEngine.Color[2]);
                int fps=(int)start["targetFps"];if(Array.IndexOf(new[]{30,50,60,100,120,144,165,244,280},fps)<0)fps=280;
                sim.ConfigureStudioFrames(fps,0,default,(double)start["time"],(double)start["fixedTime"],0,false);
                sim.ConfigureStudioEconomy();
                var render=node["position"];sim.ConfigureStudioObservedRender((float)render["x"],(float)render["y"],(float)render["z"]);
                sim.StudioSample=(pu,phase)=>
                {
                    string wanted=phase=="before-pu"?"before-fixed-update":phase=="after-pu"?"after-physics":phase;
                    var expected=current["Samples"].OfType<JObject>().SingleOrDefault(s=>(string)s["Phase"]==wanted && (int)s["Pu"]==pu);
                    if(expected==null)throw new InvalidDataException("Missing observed phase: "+wanted);
                    var actual=EngineFixture.Sample(sim,frameNumber,pu,phase).Fields;
                    var scale=sim.Player.transform.lossyScale;SimulationFieldCapture.AddVector(actual,"render/scale",scale.x,scale.y);
                    foreach(var box in sim.Player.GetComponents<UnityEngine.BoxCollider2D>())
                    {
                        string key="player-collider/"+box.GetInstanceID();actual[key+"/enabled"]=box.enabled.ToString();actual[key+"/include-layers"]=box.includeLayers.value.ToString();actual[key+"/exclude-layers"]=box.excludeLayers.value.ToString();
                        SimulationFieldCapture.AddVector(actual,key+"/offset",box.offset.x,box.offset.y);SimulationFieldCapture.AddVector(actual,key+"/size",box.size.x,box.size.y);
                    }
                    report.ComparedSamples++; Difference first=null;
                    foreach(var p in ((JObject)expected["Fields"]).Properties().Where(p=>p.Name.StartsWith("movement/",StringComparison.Ordinal)||p.Name.StartsWith("body/position/",StringComparison.Ordinal)||p.Name.StartsWith("body/velocity/",StringComparison.Ordinal)||p.Name.StartsWith("render/",StringComparison.Ordinal)||p.Name.StartsWith("player-collider/",StringComparison.Ordinal)))
                        if(!actual.TryGetValue(p.Name,out var value) || value!=(string)p.Value) {first=new Difference{Part=partName,Frame=frameNumber,Pu=pu,Phase=wanted,Field=p.Name,Expected=(string)p.Value,Actual=value??"<missing>"};break;}
                    if(frameNumber>firstPartFrame){report.SamplesAfterInitialFrame++;if(first==null)report.MatchingAfterInitialFrame++;}
                    if(string.Equals((string)expected["Fields"]["movement/cutsceneMode"],"spring",StringComparison.Ordinal))
                    {springSeen=true;if(report.FirstSpringFrame<0)report.FirstSpringFrame=frameNumber;}
                    if(springSeen){report.SamplesFromFirstSpring++;if(first==null)report.MatchingFromFirstSpring++;}
                    if(first==null)report.MatchingSamples++;else {
                        if(report.FirstDifference==null)report.FirstDifference=first;if(report.Differences.Count<100)report.Differences.Add(first);
                        report.LastDifference=first;
                        report.FirstDifferenceFields.TryGetValue(first.Field,out int count);report.FirstDifferenceFields[first.Field]=count+1;
                        if(wanted=="after-physics" && first.Field.StartsWith("body/",StringComparison.Ordinal) && report.PhysicsDifferences.Count<6)
                            report.PhysicsDifferences.Add(new {frameNumber,pu,Expected=expected["Fields"],Actual=actual,Contacts=sim.Physics.StudioPlayerContacts()});
                    }
                };
                report.Imported=true;
            },(part,frame)=>
            {
                current=frame;frameNumber=(int)frame["Frame"];
                if(firstPartFrame<0)firstPartFrame=frameNumber;
                var rows=frame["Samples"].OfType<JObject>().ToArray();var input=rows.Single(s=>(string)s["Phase"]=="before-input-update");
                var fixedRows=rows.Where(s=>(string)s["Phase"]=="before-fixed-update").ToArray();
                var fields=input["Fields"];float F(string key)=>PassiveCaptureAudit.Float((string)fields[key]);double D(string key)=>PassiveCaptureAudit.Double((string)fields[key]);
                if(!initialised){sim.ConfigureStudioObservedScale((float)pendingStart["timeScale"]);initialised=true;}
                sim.StepStudioFrame(Input(input),new StudioObservedFrame {FixedInputs=fixedRows.Select(Input).ToArray(),
                    FixedTimes=fixedRows.Select(s=>PassiveCaptureAudit.Double((string)s["Fields"]["clock/fixed-time"])).ToArray(),
                    FixedUnscaledDeltas=fixedRows.Select(s=>PassiveCaptureAudit.Float((string)s["Fields"]["clock/unscaled-delta"])).ToArray(),
                    Time=D("clock/time"),LastFixedTime=D("clock/fixed-time"),Delta=F("clock/delta"),UnscaledDelta=F("clock/unscaled-delta"),FixedDelta=F("clock/fixed-delta")});
                report.Frames++;report.PhysicsUpdates+=(int)frame["PhysicsUpdates"];
            });
            report.Passed=report.FirstDifference==null && report.ComparedSamples>0;
            report.PostInitialFramePassed=report.SamplesAfterInitialFrame>0 && report.MatchingAfterInitialFrame==report.SamplesAfterInitialFrame;
            report.PostFirstSpringPassed=report.SamplesFromFirstSpring>0 && report.MatchingFromFirstSpring==report.SamplesFromFirstSpring;
            SearchFiles.Write(Path.Combine(output,"comparison.json"),report);
            Console.WriteLine($"Imported {report.Frames} frames / {report.PhysicsUpdates} PUs; {report.MatchingSamples}/{report.ComparedSamples} player samples match. Same game build: {report.GameBuildMatches}.");
            return report.Passed?0:1;
        }
        catch(Exception error){report.Error=error.ToString();SearchFiles.Write(Path.Combine(output,"comparison.json"),report);Console.Error.WriteLine(error.Message);return 2;}
        finally{if(sim!=null)sim.StudioSample=null;}
    }
    private static StudioInput Input(JObject sample)
    {var f=sample["Input"].ToObject<FrameInput>();return new StudioInput{X=f.X,Y=f.Y,Held=(int)f.Held,Press=(int)f.Press,Release=(int)f.Release};}
    private static void DeriveSingletons(JObject world)
    {
        var statics=(JArray)world["statics"];
        var owners=new Dictionary<Type,List<JObject>>();
        foreach(var node in world["objects"].OfType<JObject>())foreach(var component in node["components"].OfType<JObject>())
        {
            if((string)component["assembly"]!="Assembly-CSharp")continue;
            var type=typeof(Movement).Assembly.GetType((string)component["type"]);
            for(var parent=type?.BaseType;parent!=null;parent=parent.BaseType)
                if(parent.IsGenericType && parent.GetGenericTypeDefinition().Name=="Singleton`1")
                {if(!owners.TryGetValue(parent,out var candidates))owners[parent]=candidates=new List<JObject>();candidates.Add(component);break;}
        }
        foreach(var pair in owners)
        {
            if(pair.Value.Count!=1)continue;
            foreach(var field in pair.Key.GetFields(BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly))
                if(field.FieldType.IsAssignableFrom(pair.Key.GetGenericArguments()[0]))statics.Add(new JArray("Singleton`1["+pair.Key.GetGenericArguments()[0].FullName+"]",field.Name,new JObject{["$ref"]=(int)pair.Value[0]["id"]}));
        }
    }
    private static void OrderHierarchy(JObject world)
    {
        // Resources.FindObjectsOfTypeAll is not native scene registration order.
        // Instantiate the captured hierarchy in sibling order, retaining each
        // node's component order (especially neighbouring upgrade-box solids).
        var nodes=world["objects"].OfType<JObject>().ToDictionary(n=>(int)n["id"]);
        var keys=new Dictionary<int,string>();
        string Key(JObject n,int depth=0)
        {
            int id=(int)n["id"];if(keys.TryGetValue(id,out var key))return key;
            if(depth>128)throw new InvalidDataException("Invalid captured transform hierarchy.");
            int parent=(int)n["parent"];
            return keys[id]=(parent!=0 && nodes.TryGetValue(parent,out var p)?Key(p,depth+1):(string)n["scene"])+"/"+((int)n["siblingIndex"]).ToString("D6",CultureInfo.InvariantCulture);
        }
        world["objects"]=new JArray(nodes.Values.OrderBy(n=>Key(n),StringComparer.Ordinal).Select(n=>n.DeepClone()));
    }
}
