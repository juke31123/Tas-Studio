using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Search;
using TasStudioProbe.Reference;
using UnityEngine;

internal static class PerformanceProbe
{
    internal static void Run(object session,string directory)
    {
        const BindingFlags flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
        var adapter=session.GetType().GetProperty("Adapter",flags).GetValue(session);
        var capture=adapter.GetType().GetMethod("CaptureWorld",flags);
        var captureCompact=adapter.GetType().GetMethod("CaptureReplayWorld",flags);
        WorldWitness Current()=>(WorldWitness)capture.Invoke(adapter,null);
        WorldWitness Compact()=>(WorldWitness)captureCompact.Invoke(adapter,null);
        string Difference(WorldWitness a,WorldWitness b)=>a.Difference(b)??b.Difference(a);
        var expected=ReferenceWorldCheckpoint.Capture();var actual=Current();
        var difference=Difference(expected,actual);if(difference!=null)throw new Exception("World capture changed: "+difference);
        WorldPerformanceTests.Run();
        if(SearchFiles.Hash(Compact())!=SearchFiles.Hash(WorldPerformanceTests.LegacyCompact(expected)))throw new Exception("Direct replay capture differs from legacy digest.");
        object Measure(Func<WorldWitness> read,int count)
        {
            read();var watch=Stopwatch.StartNew();WorldWitness last=null;
            for(int i=0;i<count;i++)last=read();watch.Stop();
            if(Difference(last,read())!=null)throw new Exception("Paused observations changed while benchmarking.");
            return new {Calls=count,Milliseconds=watch.Elapsed.TotalMilliseconds,MeanMilliseconds=watch.Elapsed.TotalMilliseconds/count,
                ExactFields=last.Exact.Count,NumberFields=last.Numbers.Count,Hash=SearchFiles.Hash(last)};
        }
        var before=Measure(ReferenceWorldCheckpoint.Capture,12);var after=Measure(Current,12);
        var compact=Measure(()=>WorldPerformanceTests.LegacyCompact(actual),12);
        var replay=Measure(Compact,12);
        var geometry=GeometryParity();
        int mutations=0;var root=new GameObject("Tas Studio owned capture benchmark");
        void Check()
        {
            var old=ReferenceWorldCheckpoint.Capture();var fresh=Current();
            string change=Difference(old,fresh);if(change!=null)throw new Exception("Mutated world capture differs: "+change);
            if(SearchFiles.Hash(old)!=SearchFiles.Hash(fresh) || SearchFiles.Hash(WorldPerformanceTests.LegacyCompact(old))!=SearchFiles.Hash(Compact()))throw new Exception("Compacted witness differs after scene mutation.");
            mutations++;
        }
        try
        {
            var child=new GameObject("child");child.transform.SetParent(root.transform,false);var collider=child.AddComponent<BoxCollider2D>();
            var body=child.AddComponent<Rigidbody2D>();body.simulated=false;Check();
            root.name="renamed";Check();child.transform.localScale=new Vector3(2,3,1);Check();
            collider.enabled=false;Check();root.SetActive(false);Check();root.SetActive(true);
            child.transform.SetParent(null,true);Check();child.transform.SetParent(root.transform,true);
            child.AddComponent<BoxCollider2D>();Check();UnityEngine.Object.DestroyImmediate(collider);Check();
        }
        finally {UnityEngine.Object.DestroyImmediate(root);}
        SearchFiles.Write(Path.Combine(directory,"runtime-performance.json"),new {Passed=true,LegacyWorld=before,CurrentWorld=after,LegacyCompaction=compact,DirectReplay=replay,Geometry=geometry,
            MutationChecks=mutations,FullWitnessUnchanged=true,CompactWitnessUnchanged=true,Scope="Paused native observations. No forced GC; rendering, pacing and simulation excluded."});
    }
    private static object GeometryParity()
    {
        var originals=Resources.FindObjectsOfTypeAll<Collider2D>();
        var type=typeof(Movie).Assembly.GetType("Igtap.Tas.Runtime.SimulationGeometryCapture",true);
        using var legacy=new ReferenceGeometryCapture();
        using var current=(IDisposable)Activator.CreateInstance(type);
        var read=type.GetMethod("Recover");int count=0;double oldTime=0,newTime=0;
        foreach(var collider in originals)
        {
            if(collider==null || !collider.gameObject.scene.IsValid() || (collider.gameObject.hideFlags&HideFlags.HideAndDontSave)!=0 || collider.compositeOperation!=Collider2D.CompositeOperation.None)continue;
            var watch=Stopwatch.StartNew();var expected=legacy.Recover(collider);oldTime+=watch.Elapsed.TotalMilliseconds;
            watch.Restart();var actual=(Newtonsoft.Json.Linq.JObject)read.Invoke(current,new object[]{collider});newTime+=watch.Elapsed.TotalMilliseconds;
            if(!Newtonsoft.Json.Linq.JToken.DeepEquals(expected,actual))throw new Exception("Private geometry bake differs: "+collider.GetType().Name+" / "+collider.name);
            count++;
        }
        return new {Compared=count,LegacyMilliseconds=oldTime,CurrentMilliseconds=newTime,EveryShapeMatched=true};
    }
}
