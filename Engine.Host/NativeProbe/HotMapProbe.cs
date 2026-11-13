using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Search;
using Newtonsoft.Json.Linq;
using UnityEngine;

// Creates an owned temporary map after startup, with the real Create TAS
// screen open. The driver removes it after the game exits.
internal static class HotMapProbe
{
    const BindingFlags Any=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static MonoBehaviour panel;
    static string prototype,id;
    static int stage;
    public static string InstalledId=>id;
    public static bool Ready(string root)
    {
        string request=Path.Combine(root,"hot-install-map.json");if(!File.Exists(request))return true;
        if(stage==0)
        {
            panel=Resources.FindObjectsOfTypeAll<MonoBehaviour>().FirstOrDefault(p=>p!=null&&p.GetType().FullName=="Igtap.Tas.UI.TasSettingsPanel");
            if(panel==null)return false;
            var config=SearchFiles.Read<JObject>(request);prototype=(string)config["Prototype"];id=(string)config["Id"];
            CustomMapCatalog.ValidateId(id);if(!id.StartsWith("tas-studio-probe-",StringComparison.Ordinal))throw new Exception("Invalid owned map probe ID.");
            var type=typeof(Movie).Assembly.GetType("Igtap.Tas.Runtime.CustomMaps",true);
            string maps=(string)type.GetProperty("MapsRoot",Any).GetValue(null);
            if(CustomMapCatalog.List(maps).Any(m=>m.Id==id))throw new Exception("Probe map already existed before installation.");
            panel.gameObject.SetActive(true);panel.GetType().GetMethod("ShowPage",Any).Invoke(panel,new object[]{1});
            var setup=(TasStartSetup)panel.GetType().GetField("_setup",Any).GetValue(panel);setup.Course=0;setup.CustomMapId=prototype;
            panel.GetType().GetMethod("RefreshStartSelection",Any).Invoke(panel,new object[]{true});
            var document=JObject.Parse(File.ReadAllText(Path.Combine(maps,prototype,"map.json")));
            if(document["customImages"] is JArray images&&images.Count>0)throw new Exception("Use an asset-free prototype for this probe.");
            document["name"]="Hot install map probe";document["uid"]=id;
            var objects=(JArray)document["groups"][0]["objects"];
            var tile=(JObject)objects.First(o=>(string)o["type"]=="tile").DeepClone();tile["cellX"]=(int)tile["cellX"]+256;tile["cellY"]=(int)tile["cellY"]+256;objects.Add(tile);
            string directory=Path.Combine(maps,id);if(Directory.Exists(directory))throw new Exception("Refusing to overwrite a probe map.");
            Directory.CreateDirectory(directory);File.WriteAllText(Path.Combine(directory,"tas-studio-test-owner.txt"),id);
            File.WriteAllText(Path.Combine(root,"hot-install-directory.txt"),directory);File.WriteAllText(Path.Combine(directory,"map.json"),document.ToString());
            stage=1;return false;
        }
        if(stage==1)
        {
            string key=(string)panel.GetType().GetField("_mapChoicesKey",Any).GetValue(panel);
            if(!key.Contains(id))return false;
            var setup=(TasStartSetup)panel.GetType().GetField("_setup",Any).GetValue(panel);
            if(setup.CustomMapId!=prototype)throw new Exception("Installing another map lost the current menu selection.");
            SearchFiles.Write(Path.Combine(root,"hot-install-menu.json"),new{Passed=true,InstalledAfterStartup=true,DetectedInOpenCreateMenu=true,SelectionPreserved=true,MapId=id});
            panel.gameObject.SetActive(false);stage=2;
        }
        return true;
    }
    public static void CheckCapture(object session,string root,string directory)
    {
        if(id==null)return;
        object Get(string name)=>session.GetType().GetProperty(name,Any).GetValue(session);
        var movie=(Movie)Get("Movie");var adapter=Get("Adapter");var search=Get("Search");
        string label=(string)adapter.GetType().GetProperty("LoadedMapLabel",Any).GetValue(adapter);
        search.GetType().GetMethod("Open",Any).Invoke(search,null);search.GetType().GetMethod("CaptureMap",Any).Invoke(search,null);
        var geometry=(SearchBody[])search.GetType().GetProperty("MapGeometry",Any).GetValue(search);
        var player=(MotionVector)search.GetType().GetProperty("MapPlayer",Any).GetValue(search);
        if(movie.StartSetup.CustomMapId!=id||!label.Contains(id)||!label.Contains("Hot install map probe")||!geometry.Any(g=>g.MaxX>player.X+4000))
            throw new Exception("The newly installed map's identity or extra tile geometry was not captured.");
        var world=JObject.Parse(File.ReadAllText(Path.Combine(directory,"world.json")));
        if((string)world["customMap"]["id"]!=id)throw new Exception("The world export has the wrong map identity.");
        var job=new SearchJob{Fps=movie.FrameRate,Options=((SearchOptions)search.GetType().GetProperty("Options",Any).GetValue(search)).Copy()};
        adapter.GetType().GetMethod("CaptureEngineSummary",Any).Invoke(adapter,new[]{session,job});job.Validate();
        if(job.CustomMapId!=id||job.CustomMapHash!=movie.StartSetup.CustomMapHash)throw new Exception("The search job lost its map identity.");
        string original=movie.StartSetup.CustomMapId;bool rejected=false;
        try{movie.StartSetup.CustomMapId="wrong-map";search.GetType().GetMethod("CaptureMap",Any).Invoke(search,null);}
        catch(TargetInvocationException e) when(e.InnerException is InvalidOperationException){rejected=true;}
        finally{movie.StartSetup.CustomMapId=original;}
        if(!rejected)throw new Exception("Wrong-map capture was not rejected.");
        SearchFiles.Write(Path.Combine(root,"hot-install-capture.json"),new{Passed=true,MapId=id,LoadedMap=label,GeometryBodies=geometry.Length,FreshTileCaptured=true,JobIdentityPreserved=true,WrongMapRejected=true});
    }
}
