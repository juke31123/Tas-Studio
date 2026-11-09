using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using HarmonyLib;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Search;
using Igtap.Tas.Core.Simulation;
using Recharge.ModApi;
using UnityEngine;
using UnityEngine.LowLevel;

public sealed class EngineProbeMod : IRechargeMod
{
    public string Id => "tas.studio.simulation-probe"; public string DisplayName => "Temporary simulator fidelity capture";
    public Version Version => new Version(1,0,0); GameObject obj;
    public void OnLoad(IRechargeHost host) { obj = new GameObject("Simulator capture"); UnityEngine.Object.DontDestroyOnLoad(obj); obj.AddComponent<EngineProbeBootstrap>(); }
    public void OnUnload() { if (obj != null) UnityEngine.Object.Destroy(obj); }
}
public sealed class EngineProbeBootstrap : MonoBehaviour
{
    void Update()
    {
        if (!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "TasStudio")) return;
        gameObject.AddComponent<EngineProbe>(); enabled = false;
    }
}
public sealed class GateContactProbe : MonoBehaviour
{
    void OnTriggerEnter2D(Collider2D other) => EngineProbe.GateContact(this, other, "enter");
    void OnTriggerStay2D(Collider2D other) => EngineProbe.GateContact(this, other, "stay");
    void OnTriggerExit2D(Collider2D other) => EngineProbe.GateContact(this, other, "exit");
}
public sealed class EngineProbe : MonoBehaviour
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static readonly string Root = Environment.GetEnvironmentVariable("TAS_STUDIO_SIM_CAPTURE") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tas Studio", "SimulationValidation");
    static EngineProbe Instance; object session; Type capture; SimulationTrace trace;
    int stage, frame, pu, rateIndex; int[] rates = { 30,50,60,100,120,144,165,244,280 };
    double began; string dir, saved; bool failing;
    Movie reference;
    int referenceLimit, referenceCompared, referenceMismatchFrame = -1;
    int referenceUnprobed;
    string referenceMismatch;
    float searchGoalX,searchGoalY;
    string searchSourceHash;
    int searchSourceFrames;
    string[] searchModes={"chain","beam"};int searchModeIndex;
    string SearchMode=>searchModes[searchModeIndex];
    int searchStart = 100, searchEnd = 130;
    string searchChain;
    SearchAbilities searchAbilities;
    int physicsDiagnosticsEnd;
    int timeoutSeconds = 180;
    PlatformMover[] movers;
    readonly Dictionary<int,bool> moverSleep=new Dictionary<int,bool>();
    Harmony installedHarmony;
    bool shadowCut, shadowRecent;
    object Get(string name) => session.GetType().GetProperty(name,Flags).GetValue(session);
    object Call(string name, params object[] args) => session.GetType().GetMethod(name,Flags).Invoke(session,args);
    void Set(string name, object value) => session.GetType().GetField(name,Flags).SetValue(session,value);
    void Queue(Action action) => Call("Queue", (Action)(() => { try { action(); } catch(Exception e) { Fail(e.ToString()); } }));
    string Mode => Get("Mode").ToString(); bool Frozen => (bool)Get("Frozen"); bool Active => (bool)Get("Active"); int Cursor => (int)Get("Cursor");
    void Log(string s) => File.AppendAllText(Path.Combine(Root,"result.log"), DateTime.UtcNow.ToString("O") + " " + s + "\n");
    string SaveHash() => (string)session.GetType().Assembly.GetType("Igtap.Tas.Runtime.EnvironmentFingerprint").GetMethod("Saves",Flags).Invoke(null,null);
    void Fail(string s) { if(failing)return; failing=true; Log("FAIL "+s); try { if(Active)Queue(()=>Call("Stop")); else Application.Quit(); }catch{Application.Quit();} }
    void Start()
    {
        try { InitializeCapture(); }
        catch (Exception error) { Fail(error.ToString()); }
    }
    void InitializeCapture()
    {
        Instance=this; began=Time.realtimeSinceStartupAsDouble;
        if(File.Exists(Path.Combine(Root,"timeout-seconds.txt"))) timeoutSeconds=int.Parse(File.ReadAllText(Path.Combine(Root,"timeout-seconds.txt")));
        if(File.Exists(Path.Combine(Root,"search-modes.txt")))searchModes=File.ReadAllText(Path.Combine(Root,"search-modes.txt")).Trim().Split(',');
        if(searchModes.Length<1 || searchModes.Any(m=>m!="chain" && m!="beam" && m!="wave"))throw new InvalidOperationException("Invalid smoke search modes.");
        if(timeoutSeconds<30 || timeoutSeconds>1800) throw new ArgumentOutOfRangeException("Native test timeout");
        if(File.Exists(Path.Combine(Root,"physics-end.txt"))) physicsDiagnosticsEnd=int.Parse(File.ReadAllText(Path.Combine(Root,"physics-end.txt")));
        if(File.Exists(Path.Combine(Root,"search-range.txt"))) {
            var range=File.ReadAllText(Path.Combine(Root,"search-range.txt")).Trim().Split(','); searchStart=int.Parse(range[0]); searchEnd=int.Parse(range[1]);
        }
        if (File.Exists(Path.Combine(Root,"rates.txt"))) rates=File.ReadAllText(Path.Combine(Root,"rates.txt")).Split(',').Select(int.Parse).ToArray();
        if (File.Exists(Path.Combine(Root, "reference.tas.json")))
        {
            reference = SearchFiles.Read<Movie>(Path.Combine(Root, "reference.tas.json"));
            reference.Validate();
            if (reference.StartSetup == null || reference.Frames.Count == 0) throw new InvalidOperationException("A configured nonempty reference TAS is required.");
            if (reference.Seed != 12345 || reference.WarmupFrames != 10) throw new NotSupportedException("This capture driver requires the standard seed and ten-frame bootstrap.");
            referenceLimit = File.Exists(Path.Combine(Root, "reference-end.txt")) ? int.Parse(File.ReadAllText(Path.Combine(Root, "reference-end.txt"))) : reference.Frames.Count;
            if (referenceLimit < 1 || referenceLimit > reference.Frames.Count) throw new ArgumentOutOfRangeException("Reference end frame");
            rates = new[] { reference.FrameRate };
        }
        var harmony = installedHarmony = new Harmony("tas.engine.capture");
        harmony.Patch(typeof(Movement).GetMethod("FixedUpdate",Flags), prefix:new HarmonyMethod(typeof(EngineProbe),nameof(BeforePu)),postfix:new HarmonyMethod(typeof(EngineProbe),nameof(AfterMovementPu)));
        if(physicsDiagnosticsEnd>0) {
            harmony.Patch(typeof(Movement).GetMethod("Start",Flags),prefix:new HarmonyMethod(typeof(EngineProbe),nameof(BeforePlayerStart)),postfix:new HarmonyMethod(typeof(EngineProbe),nameof(AfterPlayerStart)));
            harmony.Patch(AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="TasStudio").GetType("Igtap.Tas.Runtime.ConfiguredStart").GetMethod("Apply",Flags),
                prefix:new HarmonyMethod(typeof(EngineProbe),nameof(BeforeConfiguredStart)),postfix:new HarmonyMethod(typeof(EngineProbe),nameof(AfterConfiguredStart)));
        }
        if (reference != null)
        {
            harmony.Patch(typeof(pauseMenuScript).GetMethod("Update", Flags), prefix:new HarmonyMethod(typeof(EngineProbe), nameof(BeforeMenu)), postfix:new HarmonyMethod(typeof(EngineProbe), nameof(AfterMenu)));
            harmony.Patch(typeof(Movement).GetMethod("Update", Flags), prefix:new HarmonyMethod(typeof(EngineProbe), nameof(BeforeMovementUpdate)));
            harmony.Patch(typeof(MonoBehaviour).GetMethod("Invoke", new[] {typeof(string), typeof(float)}), prefix:new HarmonyMethod(typeof(EngineProbe), nameof(TimerScheduled)));
            foreach (string callback in new[] {"cancelJumpBuffer", "cancelJumpCutBuffer", "cancelDashBuffer", "endRecentJump", "reEnableNormalCollider", "deathRespawn", "cancelSpringCutscene"})
                harmony.Patch(typeof(Movement).GetMethod(callback, Flags), prefix:new HarmonyMethod(typeof(EngineProbe), nameof(TimerFired)));
            foreach (var target in new[] {typeof(Movement), typeof(Audio), typeof(PlayerAnimationEventProxy)})
                foreach (var method in target.GetMethods(Flags).Where(m => m.Name == "PlayFootstep" || m.Name == "RelayFootstep" || m.Name == "playSFX" || m.Name == "PlayAudio"))
                    harmony.Patch(method, prefix:new HarmonyMethod(typeof(EngineProbe), nameof(BeforeSound)), postfix:new HarmonyMethod(typeof(EngineProbe), nameof(AfterSound)));
        }
        var loop=PlayerLoop.GetCurrentPlayerLoop();
        if(!After(ref loop,typeof(UnityEngine.PlayerLoop.FixedUpdate.Physics2DFixedUpdate),typeof(AfterPhysicsMarker),AfterPu) ||
            !After(ref loop,typeof(UnityEngine.PlayerLoop.Update.ScriptRunDelayedDynamicFrameRate),typeof(AfterDelayedMarker),AfterDelayed) ||
            !Append(ref loop,typeof(UnityEngine.PlayerLoop.PostLateUpdate),typeof(EndMarker),EndFrame)) throw new Exception("Probe loop phases missing");
        PlayerLoop.SetPlayerLoop(loop); Log("Loaded");
    }
    sealed class AfterPhysicsMarker {} sealed class EndMarker {} sealed class AfterDelayedMarker {}
    static void BeforeMenu(pauseMenuScript __instance) => MenuEvent("before-menu", __instance);
    static void AfterMenu(pauseMenuScript __instance) => MenuEvent("after-menu", __instance);
    static void BeforeMovementUpdate(Movement __instance) => MenuEvent("before-movement", __instance.pauseMenu);
    static void MenuEvent(string phase, pauseMenuScript menu)
    {
        var p = Instance; if (p == null || !p.Open || p.reference == null || p.frame >= p.reference.Frames.Count ||
            (p.reference.Frames[p.frame].Input.Held & Buttons.Menu) == 0) return;
        File.AppendAllText(Path.Combine(Root, "menu-events.jsonl"), Newtonsoft.Json.JsonConvert.SerializeObject(new {
            p.frame, phase, menu.menuOpen, PlayerPaused=UnityEngine.Object.FindFirstObjectByType<Movement>().GamePaused, Scale=Time.timeScale }) + "\n");
    }
    public static void GateContact(Component gate, Collider2D other, string phase)
    {
        var p = Instance; if (p == null || !p.Open || !other.CompareTag("Player")) return;
        File.AppendAllText(Path.Combine(Root, "contact-events.jsonl"), Newtonsoft.Json.JsonConvert.SerializeObject(new {
            p.frame, p.pu, phase, Gate=gate.GetInstanceID(), Other=other.GetInstanceID(), X=other.transform.position.x, Y=other.transform.position.y,
            ScaleX=other.transform.lossyScale.x, MinX=other.bounds.min.x, MaxX=other.bounds.max.x,
            GateMinX=gate.GetComponent<Collider2D>().bounds.min.x,
            Width=(other as BoxCollider2D)?.size.x, OffsetX=(other as BoxCollider2D)?.offset.x }) + "\n");
    }
    void OnDestroy()
    {
        installedHarmony?.UnpatchAll("tas.engine.capture");
        var loop = PlayerLoop.GetCurrentPlayerLoop(); RemoveProbeMarkers(ref loop); PlayerLoop.SetPlayerLoop(loop);
        if (ReferenceEquals(Instance, this)) Instance = null;
    }
    static void RemoveProbeMarkers(ref PlayerLoopSystem node)
    {
        if (node.subSystemList == null) return;
        var children = new List<PlayerLoopSystem>();
        foreach (var child in node.subSystemList)
        {
            if (child.type == typeof(AfterPhysicsMarker) || child.type == typeof(EndMarker) || child.type == typeof(AfterDelayedMarker)) continue;
            var copy = child; RemoveProbeMarkers(ref copy); children.Add(copy);
        }
        node.subSystemList = children.ToArray();
    }
    static bool After(ref PlayerLoopSystem n,Type target,Type marker,PlayerLoopSystem.UpdateFunction callback)
    { if(n.subSystemList==null)return false; for(int i=0;i<n.subSystemList.Length;i++){if(n.subSystemList[i].type==target){var a=n.subSystemList.ToList();a.Insert(i+1,new PlayerLoopSystem{type=marker,updateDelegate=callback});n.subSystemList=a.ToArray();return true;}if(After(ref n.subSystemList[i],target,marker,callback))return true;}return false; }
    static bool Append(ref PlayerLoopSystem n,Type target,Type marker,PlayerLoopSystem.UpdateFunction callback)
    {if(n.type==target){var a=(n.subSystemList??Array.Empty<PlayerLoopSystem>()).ToList();a.Add(new PlayerLoopSystem{type=marker,updateDelegate=callback});n.subSystemList=a.ToArray();return true;}if(n.subSystemList!=null)for(int i=0;i<n.subSystemList.Length;i++)if(Append(ref n.subSystemList[i],target,marker,callback))return true;return false;}
    bool Open => trace!=null && !failing && (bool)session.GetType().GetField("_frameOpen",Flags).GetValue(session) && !Frozen && Mode=="Recording";
    void Sample(string phase)
    {
        trace.Samples.Add((SimulationSample)capture.GetMethod("Sample",Flags).Invoke(null,new object[]{session,frame,pu,phase}));
        if (frame < physicsDiagnosticsEnd) MoverDiagnostic(phase);
    }
    void MoverDiagnostic(string phase)
    {
        if(movers==null) movers=UnityEngine.Object.FindObjectsByType<PlatformMover>(FindObjectsInactive.Include,FindObjectsSortMode.None);
        var fields=new SortedDictionary<string,string>(StringComparer.Ordinal);
        foreach(var mover in movers) {
            if(mover==null) continue;
            string key="mover/"+mover.GetInstanceID();
            SimulationFieldCapture.AddScript(fields,mover,key);
            SimulationFieldCapture.AddVector(fields,key+"/position",mover.transform.position.x,mover.transform.position.y);
            var body=(Rigidbody2D)typeof(PlatformMover).GetField("body",Flags).GetValue(mover);
            if(body!=null) {
                SimulationFieldCapture.AddVector(fields,key+"/body",body.position.x,body.position.y);
                SimulationFieldCapture.AddVector(fields,key+"/velocity",body.linearVelocity.x,body.linearVelocity.y);
            }
        }
        File.AppendAllText(Path.Combine(Root,"mover-events.jsonl"),Newtonsoft.Json.JsonConvert.SerializeObject(new {frame,pu,phase,Fields=fields})+"\n");
    }
    static void BeforePlayerStart(Movement __instance)=>BootstrapPhysics("before-start",__instance);
    static void AfterPlayerStart(Movement __instance)=>BootstrapPhysics("after-start",__instance);
    static void BeforeConfiguredStart(Movement player)=>BootstrapPhysics("before-configured-start",player);
    static void AfterConfiguredStart(Movement player)=>BootstrapPhysics("after-configured-start",player);
    static void BootstrapPhysics(string phase,Movement player=null)
    {
        var p=Instance;if(p==null || p.physicsDiagnosticsEnd<=0 || p.trace!=null || !p.Active || p.Mode=="Stopping")return;
        player=player ?? UnityEngine.Object.FindFirstObjectByType<Movement>();if(player==null)return;
        var bodies=UnityEngine.Object.FindObjectsByType<Rigidbody2D>(FindObjectsSortMode.None).Where(b=>b.gameObject==player.gameObject || b.GetComponent<PlatformMover>()!=null).Select(b=>new {
            Id=b.GetInstanceID(),b.name,X=b.position.x,Y=b.position.y,TX=b.transform.position.x,TY=b.transform.position.y,Sleeping=b.IsSleeping(),b.simulated,
            Colliders=b.GetComponents<Collider2D>().Select(c=>new {Id=c.GetInstanceID(),c.enabled,c.isTrigger,Width=(c as BoxCollider2D)?.size.x,Height=(c as BoxCollider2D)?.size.y,OffsetX=c.offset.x,OffsetY=c.offset.y}).ToArray()}).ToArray();
        File.AppendAllText(Path.Combine(Root,"bootstrap-physics.jsonl"),Newtonsoft.Json.JsonConvert.SerializeObject(new {phase,Mode=p.Mode,Frame=Time.frameCount,Time=Time.timeAsDouble,FixedTime=Time.fixedTimeAsDouble,Bodies=bodies})+"\n");
    }
    static void BeforePu(Movement __instance){ var p=Instance;if(p!=null&&p.Open){p.pu++;p.Sample("before-pu");} else BootstrapPhysics("before-pu",__instance); }
    static void AfterMovementPu(Movement __instance)
    {
        var p=Instance;if(p==null || !p.Open || p.frame>=p.physicsDiagnosticsEnd) return;
        var body=__instance.GetComponent<Rigidbody2D>();
        File.AppendAllText(Path.Combine(Root,"movement-physics-events.jsonl"),Newtonsoft.Json.JsonConvert.SerializeObject(new {
            p.frame,p.pu,VX=SimulationTraceComparer.Float(body.linearVelocity.x),VY=SimulationTraceComparer.Float(body.linearVelocity.y),X=body.position.x,Y=body.position.y})+"\n");
    }
    static void AfterPu(){ var p=Instance;if(p!=null&&p.Open) {p.Sample("after-pu");p.PhysicsDiagnostic();} else BootstrapPhysics("after-pu"); }
    void PhysicsDiagnostic()
    {
        if(frame>=physicsDiagnosticsEnd) return;
        var player=UnityEngine.Object.FindFirstObjectByType<Movement>(); var body=player.GetComponent<Rigidbody2D>();
        var buffer=new ContactPoint2D[64]; int count=body.GetContacts(buffer); var contacts=new List<object>();
        for(int i=0;i<count;i++)
        {
            var c=buffer[i]; contacts.Add(new {Collider=c.collider?.GetInstanceID(),Other=c.otherCollider?.GetInstanceID(),
                X=c.point.x,Y=c.point.y,NX=c.normal.x,NY=c.normal.y,c.separation,c.normalImpulse,c.tangentImpulse});
        }
        File.AppendAllText(Path.Combine(Root,"physics-events.jsonl"),Newtonsoft.Json.JsonConvert.SerializeObject(new {frame,pu,body.mass,body.inertia,Contacts=contacts})+"\n");
        foreach(var mover in movers ?? Array.Empty<PlatformMover>()) {
            if(mover==null || !mover.gameObject.activeInHierarchy) continue;
            var actor=(Rigidbody2D)typeof(PlatformMover).GetField("body",Flags).GetValue(mover);
            int id=mover.GetInstanceID(); bool sleeping=actor.IsSleeping();
            if(moverSleep.TryGetValue(id,out bool wasSleeping) && wasSleeping && !sleeping) {
                var pairs=new List<object>(); int found=actor.GetContacts(buffer);
                for(int j=0;j<found;j++) { var p=buffer[j]; pairs.Add(new {Collider=p.collider?.GetInstanceID(),Name=p.collider?.name,Other=p.otherCollider?.GetInstanceID(),OtherName=p.otherCollider?.name,
                    X=p.point.x,Y=p.point.y,NX=p.normal.x,NY=p.normal.y}); }
                var near=UnityEngine.Object.FindObjectsByType<Rigidbody2D>(FindObjectsSortMode.None).Where(b => b!=actor && Math.Abs(b.position.x-actor.position.x)<256 && Math.Abs(b.position.y-actor.position.y)<256)
                    .Select(b => new {Id=b.GetInstanceID(),b.name,b.bodyType,b.simulated,X=b.position.x,Y=b.position.y,VX=b.linearVelocity.x,VY=b.linearVelocity.y}).ToArray();
                File.AppendAllText(Path.Combine(Root,"mover-wake-events.jsonl"),Newtonsoft.Json.JsonConvert.SerializeObject(new {frame,pu,Id=id,X=actor.position.x,Y=actor.position.y,Contacts=pairs,NearbyBodies=near})+"\n");
            }
            moverSleep[id]=sleeping;
        }
    }
    static void TimerScheduled(MonoBehaviour __instance, string methodName, float time)
    {
        if (!(__instance is Movement)) return;
        TimerEvent("schedule", methodName, time);
        if (Instance?.trace == null) return;
        if (methodName == "cancelJumpCutBuffer") Instance.shadowCut = true;
        if (methodName == "endRecentJump") Instance.shadowRecent = true;
    }
    static void AfterDelayed()
    {
        var p = Instance; if (p?.trace == null || p.Frozen) return;
        if (p.shadowCut) { p.shadowCut = false; TimerEvent("late-schedule", "ShadowCutExpired", .26f); p.Invoke(nameof(ShadowCutExpired), .26f); }
        if (p.shadowRecent) { p.shadowRecent = false; TimerEvent("late-schedule", "ShadowRecentExpired", .03f); p.Invoke(nameof(ShadowRecentExpired), .03f); }
    }
    void ShadowCutExpired() => TimerEvent("call", nameof(ShadowCutExpired), 0);
    void ShadowRecentExpired() => TimerEvent("call", nameof(ShadowRecentExpired), 0);
    static void TimerFired(MethodBase __originalMethod) => TimerEvent("call", __originalMethod.Name, 0);
    static void TimerEvent(string kind, string method, float delay)
    {
        var p = Instance;
        if (p?.trace == null || !p.Active || p.Frozen) return;
        var entry = new { Frame = p.frame, Pu = p.pu, Phase = Time.inFixedTimeStep ? "fixed" : "frame", Kind = kind, Method = method,
            Delay = delay, Time = Time.timeAsDouble, FixedTime = Time.fixedTimeAsDouble, Delta = Time.deltaTime };
        File.AppendAllText(Path.Combine(Root, "timer-events.jsonl"), Newtonsoft.Json.JsonConvert.SerializeObject(entry) + "\n");
    }
    static void BeforeSound(ref string __state)
    {
        var p = Instance;
        if (p?.trace != null && p.Active && !p.Frozen) __state = JsonUtility.ToJson(UnityEngine.Random.state);
    }
    static void AfterSound(MethodBase __originalMethod, string __state)
    {
        var p = Instance;
        if (__state == null || p?.trace == null) return;
        string after = JsonUtility.ToJson(UnityEngine.Random.state);
        if (__state == after) return;
        var entry = new { Frame = p.frame, Pu = p.pu, Phase = Time.inFixedTimeStep ? "fixed" : "frame",
            Method = __originalMethod.DeclaringType.FullName + "." + __originalMethod.Name, Before = __state, After = after };
        File.AppendAllText(Path.Combine(Root, "random-events.jsonl"), Newtonsoft.Json.JsonConvert.SerializeObject(entry) + "\n");
    }
    static void EndFrame()
    {
        var p=Instance; if(p?.trace==null||p.failing||p.Cursor<=p.frame)return;
        try
        {
            var movie=(Movie)p.Get("Movie");
            var recorded = movie.Frames[p.frame];
            p.trace.Inputs.Add(recorded.Input);p.Sample("frame-end");
            if (p.reference != null)
            {
                var expected = p.reference.Frames[p.frame];
                string difference = expected.Expected == null ? null : ProbeComparer.Difference(expected.Expected, recorded.Expected);
                if(expected.Expected==null) p.referenceUnprobed++;
                if (difference == null && expected.FixedTicks >= 0 && expected.FixedTicks != recorded.FixedTicks) difference = "Reference PU count differs.";
                if (difference == null && expected.PuPhase >= 0 && Math.Abs(expected.PuPhase - recorded.PuPhase) > .000002) difference = "Reference PU phase differs.";
                if (difference != null && p.referenceMismatch == null)
                { p.referenceMismatch = difference; p.referenceMismatchFrame = p.frame; p.Log("Reference first difference at frame " + p.frame + ": " + difference); }
                p.referenceCompared++;
            }
            p.frame++;p.pu=0;
            if (p.reference != null)
            {
                if (p.frame < p.referenceLimit) p.Set("AuthoredInput", p.reference.Frames[p.frame].Input);
                else { p.stage = 3; p.Queue(p.FinishReference); }
            }
        }
        catch(Exception e){p.Fail(e.ToString());}
    }
    void Update()
    {
        try
        {
            if(session==null){var a=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x=>x.GetName().Name=="TasStudio");if(a==null)return;session=a.GetType("Igtap.Tas.Runtime.GamePatches").GetField("Session",Flags).GetValue(null);if(session==null)return;capture=a.GetType("Igtap.Tas.Runtime.SimulationCapture");}
            if(failing){if(!Active){Application.Quit();enabled=false;}return;}
            if(Time.realtimeSinceStartupAsDouble-began>timeoutSeconds){Fail("timeout "+Mode);return;}
            if(Mode=="Desync"){Fail(Get("Status").ToString());return;}
            if (reference != null) { UpdateReference(); return; }
            if(stage==0){if(!(bool)Get("IsMainMenu")||Time.realtimeSinceStartupAsDouble-began<3)return;saved=SaveHash();stage=1;NewRun();}
            else if(stage==1&&Mode=="Recording"&&Frozen)
            {
                stage=2; Queue(()=>{dir=Path.Combine(Root,"fps-"+rates[rateIndex]);capture.GetMethod("Export",Flags).Invoke(null,new object[]{session,dir});trace=SearchFiles.Read<SimulationTrace>(Path.Combine(dir,"native.trace.json"));frame=pu=0;Set("UseAuthoredInput",true);NextFrame();});
            }
            else if(stage==2&&Frozen&&Cursor==frame)
            {
                if(frame>=rates[rateIndex]){stage=3;Queue(()=>{trace.Validate();SearchFiles.Write(Path.Combine(dir,"native.trace.json"),trace);Log("Captured "+rates[rateIndex]+" FPS, "+trace.Inputs.Count+" frames, "+trace.Samples.Count+" samples");trace=null;Call("Stop");});}
                else {stage=4;Queue(NextFrame);}
            }
            else if(stage==4&&Cursor==frame&&Frozen)stage=2;
            else if(stage==3&&!Active){if(saved!=SaveHash())throw new Exception("Saved progress changed");if(++rateIndex<rates.Length){stage=1;NewRun();}else{Log("PASS captures complete; original saved progress unchanged");Application.Quit();enabled=false;}}
        }
        catch(Exception e){Fail(e.ToString());}
    }
    void NewRun()=>Queue(()=>{capture.GetMethod("BeginHistory",Flags).Invoke(null,null);capture.GetField("CollectHistory",Flags).SetValue(null,true);Set("FileName","engine-probe-"+rates[rateIndex]+"-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));Call("StartConfiguredRecording",new TasStartSetup{Course=1,FrameRate=rates[rateIndex],Dash=true,DoubleJump=true,WallJump=true,OmniDash=true,AirDashes=2});});
    void NextFrame(){int fps=rates[rateIndex];var input=new FrameInput{X=1};if(frame>=fps/10&&frame<fps/5)input.Held|=Buttons.Jump;if(frame==fps*2/5)input.Held|=Buttons.Dash;Set("AuthoredInput",input);Call("Step");}

    void UpdateReference()
    {
        if(stage>=10) { UpdateSearchSmoke(); return; }
        if (stage == 0)
        {
            if (!(bool)Get("IsMainMenu") || Time.realtimeSinceStartupAsDouble - began < 3) return;
            saved = SaveHash(); stage = 1;
            Queue(() =>
            {
                capture.GetMethod("BeginHistory",Flags).Invoke(null,null);
                capture.GetField("CollectHistory", Flags).SetValue(null, true);
                Set("FileName", "engine-probe-reference-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
                Call("StartConfiguredRecording", reference.StartSetup.Clone());
                var movie = (Movie)Get("Movie");
                if (movie.GameHash != reference.GameHash) throw new InvalidOperationException("Reference TAS belongs to another game assembly.");
            });
        }
        else if (stage == 1 && Mode == "Recording" && Frozen)
        {
            stage = 2;
            Queue(() =>
            {
                var actual = (Movie)Get("Movie");
                string difference = ProbeComparer.Difference(reference.StartState, actual.StartState);
                if (difference == null && !reference.WarmupFixedTicks.SequenceEqual(actual.WarmupFixedTicks)) difference = "Reference warmup PU schedule differs.";
                if (difference == null && Math.Abs(reference.StartPuPhase - actual.StartPuPhase) > .000002) difference = "Reference starting PU phase differs.";
                if (difference != null) { referenceMismatch = "Starting state: " + difference; referenceMismatchFrame = -1; Log(referenceMismatch); }
                dir = Path.Combine(Root, "fps-" + reference.FrameRate);
                capture.GetMethod("Export", Flags).Invoke(null, new object[] {session, dir});
                if (physicsDiagnosticsEnd > 0) CollisionProbe.Capture(Root, ((Movement)Get("Adapter").GetType().GetProperty("Player",Flags).GetValue(Get("Adapter"))).gameObject.layer);
                trace = SearchFiles.Read<SimulationTrace>(Path.Combine(dir, "native.trace.json"));
                trace.AdvanceMode = "continuous";
                foreach (var gate in UnityEngine.Object.FindObjectsByType<startGate>(FindObjectsSortMode.None)) gate.gameObject.AddComponent<GateContactProbe>();
                frame = pu = 0;
                Set("UseAuthoredInput", true); Set("AuthoredInput", reference.Frames[0].Input);
                Call("TogglePause");
                Log("Reference " + reference.Name + ": capturing [0," + referenceLimit + ") at " + reference.FrameRate + " FPS.");
            });
        }
        else if (stage == 3 && !Active)
        {
            if (saved != SaveHash()) throw new Exception("Saved progress changed");
            Log("PASS captures complete; original saved progress unchanged"); Application.Quit(); enabled = false;
        }
    }

    void FinishReference()
    {
        trace.Validate(); SearchFiles.Write(Path.Combine(dir, "native.trace.json"), trace);
        var adapter = Get("Adapter");
        var goal = (MotionVector)adapter.GetType().GetMethod("SearchWorldPosition", Flags).Invoke(adapter, null);
        SearchFiles.Write(Path.Combine(dir, "reference-comparison.json"), new {
            Name = reference.Name, FrameRate = reference.FrameRate, StartFrame = 0, EndFrame = referenceLimit,
            ComparedFrames = referenceCompared, Passed = referenceMismatch == null,
            UnprobedSourceFrames=referenceUnprobed,
            FirstDifferenceFrame = referenceMismatchFrame, Difference = referenceMismatch,
            GoalWorldX = goal.X, GoalWorldY = goal.Y,
            SourceHash = SimulationTraceComparer.FileHash(Path.Combine(Root, "reference.tas.json"))
        });
        Log("Captured " + reference.FrameRate + " FPS, " + trace.Inputs.Count + " frames, " + trace.Samples.Count + " samples; original-reference comparison " + (referenceMismatch == null ? "passed" : "differs"));
        if(File.Exists(Path.Combine(Root,"search-smoke.txt")))
        {
            var input=trace.Inputs[searchStart];
            if((input.X!=-1 && input.X!=0 && input.X!=1) || (input.Y!=-1 && input.Y!=0 && input.Y!=1) || (input.Held & ~(Buttons.Jump|Buttons.Dash))!=0 ||
                trace.Inputs.Skip(searchStart).Take(searchEnd-searchStart).Any(i => i.X!=input.X || i.Y!=input.Y || i.Held!=input.Held))
                throw new InvalidOperationException("SearchSmoke requires a constant digital input segment with optional Jump/Dash.");
            searchAbilities=SearchAbilities.None;
            searchChain=(input.Y>0 ? "U" : input.Y<0 ? "D" : "")+(input.X>0 ? "R" : input.X<0 ? "L" : "");
            if(searchChain.Length==0) searchChain="N";
            if(input.Down(Buttons.Jump)) {searchChain+="+J";searchAbilities|=SearchAbilities.Jump|SearchAbilities.AirJump|SearchAbilities.WallJump;}
            if(input.Down(Buttons.Dash)) {searchChain+="+Dash";searchAbilities|=SearchAbilities.Dash;}
            int duration=searchEnd-searchStart;
            searchChain+=":"+Math.Max(1,duration-2)+"-"+(duration+2);
            var end=trace.Samples.Last(s => s.Frame==searchEnd-1 && s.Phase=="frame-end");
            float Number(string key) => BitConverter.ToSingle(BitConverter.GetBytes(Convert.ToInt32(end.Fields[key],16)),0);
            searchGoalX=Number("body/position/x")-Number("origin/x"); searchGoalY=Number("body/position/y")-Number("origin/y");
            trace=null; stage=10; Call("TogglePause"); return;
        }
        trace = null; Call("Stop");
    }
    object Search => Get("Search");
    object SearchGet(string name) => Search.GetType().GetProperty(name,Flags).GetValue(Search);
    object SearchCall(string name,params object[] args) => Search.GetType().GetMethod(name,Flags).Invoke(Search,args);
    void UpdateSearchSmoke()
    {
        if(stage==10 && Frozen && Mode=="Recording")
        {
            stage=11;
            Queue(() => {
                string executable=File.ReadAllText(Path.Combine(Root,"search-smoke.txt")).Trim();
                Search.GetType().GetProperty("EngineWorkerPath",Flags).SetValue(Search,executable);
                string package=Path.GetDirectoryName(Path.GetDirectoryName(executable));
                if(File.Exists(Path.Combine(package,"package.json")))
                {
                    // Exercise the mod's real background preparation path from
                    // an isolated extracted package, without installing that
                    // package over the user's existing mod/data directory.
                    var type=Search.GetType().Assembly.GetType("Igtap.Tas.Core.Search.EngineHostPackage",true);
                    var portable=Activator.CreateInstance(type,package,Path.Combine(Root,"portable-setup"),Path.GetDirectoryName(typeof(Movement).Assembly.Location));
                    Search.GetType().GetField("_portableHost",Flags).SetValue(Search,portable);
                }
                var source=(Movie)Get("Movie"); searchSourceHash=SearchFiles.Hash(source); searchSourceFrames=source.Frames.Count;
                SearchFiles.Write(Path.Combine(Root,"search-before.tas.json"),source);
                SearchCall("Start",new SearchOptions {StartFrame=searchStart,GoalSet=true,GoalX=searchGoalX,GoalY=searchGoalY,GoalRadius=1,
                    Horizon=searchEnd-searchStart+2,Seconds=20,Workers=2,BeamWidth=16,Mode=SearchMode,Chain=searchChain,Abilities=searchAbilities});
                Log("Started native controller -> engine host " + SearchMode + " search.");
            });
        }
        else if(stage==11 && !(bool)SearchGet("Busy"))
        {
            File.AppendAllText(Path.Combine(Root,"search-smoke-jobs.txt"),(string)Search.GetType().GetField("_directory",Flags).GetValue(Search)+"\n");
            var result=(SearchResult)SearchGet("Result");
            if(!result.Finished || result.Inputs.Length==0 || result.Simulator!=EngineSearchSpec.Backend) throw new InvalidOperationException("Engine search smoke failed: " + result.Status + " / " + result.Message);
            SearchFiles.Write(Path.Combine(Root,"search-after.tas.json"),(Movie)Get("Movie"));
            if(SearchFiles.Hash((Movie)Get("Movie"))!=searchSourceHash) throw new InvalidOperationException("Search changed the source movie before acceptance.");
            stage=12; Queue(() => SearchCall("Verify")); Log("Verifying engine candidate in the native game.");
        }
        else if(stage==12 && !(bool)SearchGet("Busy"))
        {
            if(!(bool)SearchGet("CanAccept")) throw new InvalidOperationException("Native verification rejected engine candidate: " + ((SearchResult)SearchGet("Result")).Message);
            stage=13;
            Queue(() => {
                SearchCall("Accept");
                if(!(bool)Get("HasPendingEdits") || SearchFiles.Hash((Movie)Get("Movie"))!=searchSourceHash || ((Movie)Get("Movie")).Frames.Count!=searchSourceFrames)
                    throw new InvalidOperationException("Accept must stage a draft and retain the original input suffix until Save edits.");
                var result=(SearchResult)SearchGet("Result"); var job=(SearchJob)SearchGet("Job");
                SearchFiles.Write(Path.Combine(Root,"search-"+SearchMode+"-smoke.json"),new {Passed=true,job.Id,job.Fps,job.Simulator,NativeVerified=true,DraftStaged=true,
                    OriginalInputsRetained=true,Frames=result.Inputs.Length,result.Evaluated,GoalX=searchGoalX,GoalY=searchGoalY});
                Log("PASS engine " + SearchMode + " candidate verified in game; acceptance staged only a draft.");
                Call("DiscardTimelineEdits");
            });
        }
        else if(stage==13 && Frozen && Mode=="Recording" && !(bool)Get("HasPendingEdits"))
        {
            if(++searchModeIndex<searchModes.Length)stage=10;
            else {SearchFiles.Write(Path.Combine(Root,"search-smoke.json"),new {Passed=true,Modes=searchModes}); stage=3; Queue(() => Call("Stop"));}
        }
    }
}
