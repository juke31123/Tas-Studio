using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Search;
using Igtap.Tas.Recorder;
using Newtonsoft.Json;
using Recharge.ModApi;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;

public sealed class RecorderProbeMod : IRechargeMod
{
    public string Id=>"Tas.Studio.Recorder.Test";public string DisplayName=>"Temporary passive recorder test";public Version Version=>new Version(1,1,0);
    private GameObject obj;
    public void OnLoad(IRechargeHost host){obj=new GameObject("Passive recorder test");UnityEngine.Object.DontDestroyOnLoad(obj);obj.AddComponent<RecorderProbe>();}
    public void OnUnload(){if(obj!=null)UnityEngine.Object.Destroy(obj);}
}
public sealed class RecorderProbe : MonoBehaviour
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static readonly string Root=Environment.GetEnvironmentVariable("TAS_RECORDER_TEST");
    object controller;int stage;double began,loadedAt;Keyboard keyboard;Key right,jump;bool failed;
    float fixedStep,capture,scale;int target,vsync,scene;
    object Get(string n)=>controller.GetType().GetProperty(n,Flags).GetValue(controller);
    object Call(string n,params object[] args)=>controller.GetType().GetMethod(n,Flags).Invoke(controller,args);
    void Log(string value)=>File.AppendAllText(Path.Combine(Root,"probe.log"),DateTime.UtcNow.ToString("O")+" "+value+"\n");
    void Start(){began=Time.realtimeSinceStartupAsDouble;Application.runInBackground=true;InputSystem.settings.backgroundBehavior=InputSettings.BackgroundBehavior.IgnoreFocus;keyboard=Keyboard.current??InputSystem.AddDevice<Keyboard>();InputSystem.EnableDevice(keyboard);}
    void Update()
    {
        if(failed)return;
        try {
            if(controller==null){var a=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x=>x.GetName().Name=="TasStudioRecorder");if(a==null)return;controller=a.GetType("Igtap.Tas.Recorder.RecorderMod").GetField("Current",Flags).GetValue(null);if(controller==null)return;
                foreach(string t in new[]{"TasSession","ClockLease","GamePatches","ReplayAudit"})if(a.GetType("Igtap.Tas.Runtime."+t)!=null)throw new Exception("TAS runtime remains in the passive recorder: "+t);
                foreach(string method in new[]{"Step","StepPu","Verify","TogglePause"})if(controller.GetType().GetMethod(method,Flags)!=null)throw new Exception("Unexpected transport control: "+method);
                Log("Passive build has no TAS runtime or stepping/replay controls.");}
            if(Time.realtimeSinceStartupAsDouble-began>160)throw new Exception("Timeout at stage "+stage+": "+Get("Status")+" / "+Get("Error"));
            if(stage==0){if(SceneManager.GetActiveScene().name!="MainMenu" || Time.realtimeSinceStartupAsDouble-began<3)return;SceneManager.LoadScene("Assets/Scenes/Overworld.unity");loadedAt=Time.realtimeSinceStartupAsDouble;stage=1;}
            else if(stage==1){if(!(bool)Get("CanStart") || Time.realtimeSinceStartupAsDouble-loadedAt<2)return;
                var p=UnityEngine.Object.FindFirstObjectByType<Movement>();var move=(InputAction)typeof(Movement).GetField("moveAction",Flags).GetValue(p);var action=(InputAction)typeof(Movement).GetField("jumpAction",Flags).GetValue(p);move.Enable();action.Enable();
                var key=action.controls.OfType<KeyControl>().First();keyboard=(Keyboard)key.device;jump=key.keyCode;string path=move.bindings.First(b=>b.name.Equals("right",StringComparison.OrdinalIgnoreCase)&&b.effectivePath.Contains("Keyboard")).effectivePath;
                right=keyboard.allKeys.First(k=>k.name.Equals(path.Substring(path.LastIndexOf('/')+1),StringComparison.OrdinalIgnoreCase)).keyCode;
                fixedStep=Time.fixedDeltaTime;capture=Time.captureDeltaTime;scale=Time.timeScale;target=Application.targetFrameRate;vsync=QualitySettings.vSyncCount;scene=SceneManager.GetActiveScene().handle;
                Call("RequestStart","normal-play","Native passive test; normal saves and timing");stage=2;Log("Starting capture in the already-running saved game.");}
            else if(stage==2 && (bool)Get("Recording")){
                if(Time.fixedDeltaTime!=fixedStep || Time.captureDeltaTime!=capture || Time.timeScale!=scale || Application.targetFrameRate!=target || QualitySettings.vSyncCount!=vsync || SceneManager.GetActiveScene().handle!=scene)throw new Exception("Recorder changed timing or reloaded the scene.");
                int frame=(int)Get("CapturedFrames");
                InputSystem.QueueStateEvent(keyboard,new KeyboardState(frame>=10 && frame<20 ? new[]{right,jump} : frame>=25 && frame<28 ? new[]{right,Key.F7,Key.F11} : new[]{right}));
                if(frame>=35 && target!=60){target=60;Application.targetFrameRate=60;Log("Changed game FPS during recording; observer must follow.");}
                if(frame>=100){InputSystem.QueueStateEvent(keyboard,new KeyboardState());Call("RequestStop","Native passive test complete");stage=3;}
            }
            else if(stage==3 && !(bool)Get("Recording") && !(bool)Get("Packing")) {
                if(((string)Get("Error")).Length>0)throw new Exception((string)Get("Error"));
                if(Time.fixedDeltaTime!=fixedStep || Time.captureDeltaTime!=capture || Time.timeScale!=scale || Application.targetFrameRate!=target || QualitySettings.vSyncCount!=vsync || SceneManager.GetActiveScene().handle!=scene)throw new Exception("Stopping capture changed gameplay settings.");
                var root=(string)Get("Root");var data=Directory.GetDirectories(Path.Combine(root,"captures")).OrderBy(d=>d).Last();var paths=Directory.GetFiles(data,"gameplay.frames.jsonl.gz",SearchOption.AllDirectories);
                var frames=paths.SelectMany(file=>Read(file)).ToArray();
                if(frames.Length<100 || !frames.SelectMany(f=>f.Samples).Any(s=>s.Input.X>0) || !frames.SelectMany(f=>f.Samples).Any(s=>s.Input.Down(Buttons.Jump)) || !frames.Any(f=>f.PhysicsUpdates>0))throw new Exception("Movement/jump/physics observations are missing.");
                if(!frames.SelectMany(f=>f.Samples).Any(s=>s.Fields["clock/target-fps"]=="60"))throw new Exception("Changed native FPS was not observed.");
                if(frames.SelectMany(f=>f.Samples).Any(s=>s.Fields["clock/capture-delta"]!=Igtap.Tas.Core.Simulation.SimulationTraceComparer.Float(capture)))throw new Exception("Recorder introduced a capture clock.");
                var firstPart=Path.GetDirectoryName(paths[0]);
                var starting=SearchFiles.Read<Newtonsoft.Json.Linq.JObject>(Path.Combine(firstPart,"starting-state.json"));
                var colors=starting["movement"].Single(e=>(string)e[1]=="afterimageColors")[2];
                if(!(colors is Newtonsoft.Json.Linq.JArray) || colors.Count()<2)throw new Exception("Unity color arrays were omitted.");
                var world=SearchFiles.Read<Newtonsoft.Json.Linq.JObject>(Path.Combine(firstPart,"world.json"));
                if(world["layerNames"]?.Count()!=32)throw new Exception("Layer-name metadata is missing.");
                var manifest=SearchFiles.Read<Newtonsoft.Json.Linq.JObject>(Path.Combine(data,"capture.json"));
                if(manifest["parts"]?.Count()!=paths.Length || (int?)manifest["parts"][0]["frames"]!=frames.Length)throw new Exception("Completed part metadata is missing.");
                foreach(string method in new[]{"manualSave","autosave"}){var m=typeof(Saveloader).GetMethod(method,Flags);var patches=m==null ? null : Harmony.GetPatchInfo(m);if(patches!=null && patches.Owners.Any(owner=>owner.StartsWith("Tas.Studio.Recorder")))throw new Exception("Recorder patches normal saving.");}
                SearchFiles.Write(Path.Combine(Root,"summary.json"),new {Passed=true,Frames=frames.Length,Pus=frames.Sum(f=>f.PhysicsUpdates),Zip=(string)Get("LastZip"),NativeTimingPreserved=true,FpsChangeFollowed=true,SaveMethodsUnpatched=true,NoSteppingRuntime=true});
                Log("PASS passive recording, natural timing/FPS changes, F7/F11 harmless, export and normal save-method availability.");Application.Quit();enabled=false;
            }
        }catch(Exception e){failed=true;Log("FAIL "+e);Application.Quit();}
    }
    static System.Collections.Generic.IEnumerable<PassiveFrame> Read(string path)
    {using(var r=new StreamReader(new GZipStream(File.OpenRead(path),CompressionMode.Decompress)))for(string line;(line=r.ReadLine())!=null;)yield return JsonConvert.DeserializeObject<PassiveFrame>(line);}
    void OnDestroy(){if(keyboard!=null)InputSystem.QueueStateEvent(keyboard,new KeyboardState());}
}
