using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Search;
using Igtap.Tas.Core.Simulation;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace Igtap.Tas.Recorder
{
    // A passive observer. No clock lease, input injection, scene loading or save guards.
    internal sealed class RecorderController : IDisposable
    {
        public string Root {get;}
        public string Error {get;private set;}="";
        public string Status {get;private set;}="Enter gameplay, then press Record or F8.";
        public string LastZip {get;private set;}="";
        public bool Recording {get;private set;}
        public bool Packing=>_packing!=null;
        public int CapturedFrames {get;private set;}
        public long CapturedPus {get;private set;}
        public Movement Player {get;private set;}
        public int Frame=>CapturedFrames;
        public int Pu {get;private set;}
        public bool CanStart=>!Recording && !Packing && FindPlayer()!=null;
        public ContactCapture Contacts;
        private string _startName,_startNotes,_stopReason,_directory,_partDirectory;
        private Task _packing;
        private GameplayJournal _journal;
        private PassiveFrame _frame;
        private JObject _manifest;
        private readonly JArray _parts=new JArray();
        private int _partNumber,_playerId,_sceneHandle,_zone,_areaState;
        private long _closedBytes;
        private PlatformMover[] _movers=Array.Empty<PlatformMover>();
        private JiggleDropScript[] _orbs=Array.Empty<JiggleDropScript>();
        private Collider2D[] _playerColliders=Array.Empty<Collider2D>();
        private readonly ContactPoint2D[] _contactPoints=new ContactPoint2D[32];
        private readonly FieldInfo _moverBody=typeof(PlatformMover).GetField("body",BindingFlags.Instance|BindingFlags.NonPublic);
        private readonly FieldInfo[] _inputFields=new[]{"moveAction","jumpAction","jumpAction2","dashAction","dashAction2","resetAction"}.Select(n=>typeof(Movement).GetField(n,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public)).ToArray();
        private readonly FieldInfo _pauseAction=typeof(pauseMenuScript).GetField("pauseAction",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public);
        public RecorderController(string root){Root=Path.GetFullPath(root);Directory.CreateDirectory(Path.Combine(Root,"exports"));}
        private static Movement FindPlayer()
        {
            if(SceneManager.GetActiveScene().name=="MainMenu")return null;
            var p=UnityEngine.Object.FindFirstObjectByType<Movement>();
            if(p==null || !p.isActiveAndEnabled || p.gameObject.scene!=SceneManager.GetActiveScene() || p.GetComponent<Rigidbody2D>()==null)return null;
            return typeof(Movement).GetField("jumpAction",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)?.GetValue(p) is InputAction ? p : null;
        }
        public void RequestStart(string name,string notes)
        {if(!CanStart)throw new InvalidOperationException("Enter gameplay and finish any pending export first.");_startName=MovieStore.NormalizeName(name);_startNotes=notes??"";}
        public void RequestStop(string reason="Stopped by player"){if(Recording)_stopReason=reason;_startName=null;}
        public void BeginFrame(){if(Recording){Pu=0;_frame=new PassiveFrame {Frame=CapturedFrames,EngineFrame=Time.frameCount};}}
        private bool Observing(Movement p)=>Recording && _journal!=null && _frame!=null && p!=null && p==Player && p.GetInstanceID()==_playerId && p.gameObject.scene.handle==_sceneHandle;
        public void BeforeFixed(Movement p){if(Observing(p))Sample("before-fixed-update",Pu+1);}
        public void BeforeInput(Movement p){if(Observing(p))Sample("before-input-update",Pu);}
        public void BeforePhysics(){if(Observing(Player) && Physics2D.simulationMode==SimulationMode2D.FixedUpdate){Pu++;Sample("before-physics",Pu);}}
        public void AfterPhysics(){if(Observing(Player) && Physics2D.simulationMode==SimulationMode2D.FixedUpdate)Sample("after-physics",Pu);}
        private FrameInput Input()
        {
            var input=new FrameInput {ExactEdges=true};var move=_inputFields[0].GetValue(Player) as InputAction;
            if(move!=null){var v=move.ReadValue<Vector2>();input.X=v.x;input.Y=v.y;}
            var buttons=new[]{Buttons.None,Buttons.Jump,Buttons.Jump2,Buttons.Dash,Buttons.Dash2,Buttons.Reset};
            void Read(InputAction a,Buttons b){if(a==null)return;if(a.IsPressed())input.Held|=b;if(a.WasPressedThisFrame())input.Press|=b;if(a.WasReleasedThisFrame())input.Release|=b;}
            for(int i=1;i<_inputFields.Length;i++)Read(_inputFields[i].GetValue(Player) as InputAction,buttons[i]);
            if(Player.pauseMenu!=null)Read(_pauseAction.GetValue(Player.pauseMenu) as InputAction,Buttons.Menu);return input;
        }
        private void Sample(string phase,int pu)
        {
            if(_frame.Samples.Count>=128){RequestStop("Per-frame observation limit reached");return;}
            var sample=new PassiveSample {Phase=phase,Pu=pu,Input=Input()};var f=sample.Fields;var b=Player.GetComponent<Rigidbody2D>();
            SimulationFieldCapture.AddPlayer(f,Player);
            SimulationFieldCapture.AddVector(f,"body/position",b.position.x,b.position.y);SimulationFieldCapture.AddVector(f,"body/velocity",b.linearVelocity.x,b.linearVelocity.y);
            SimulationFieldCapture.AddVector(f,"render/position",Player.transform.position.x,Player.transform.position.y);
            var scale=Player.transform.lossyScale;SimulationFieldCapture.AddVector(f,"render/scale",scale.x,scale.y);
            f["body/sleeping"]=b.IsSleeping().ToString();f["body/simulated"]=b.simulated.ToString();f["body/rotation"]=SimulationTraceComparer.Float(b.rotation);
            foreach(var collider in _playerColliders)if(collider!=null){string key="player-collider/"+collider.GetInstanceID();f[key+"/enabled"]=collider.enabled.ToString();f[key+"/exclude-layers"]=collider.excludeLayers.value.ToString();f[key+"/include-layers"]=collider.includeLayers.value.ToString();SimulationFieldCapture.AddVector(f,key+"/offset",collider.offset.x,collider.offset.y);if(collider is BoxCollider2D box)SimulationFieldCapture.AddVector(f,key+"/size",box.size.x,box.size.y);}
            if(phase=="after-physics"){
                int count=b.GetContacts(_contactPoints);f["contacts/count"]=count.ToString();f["contacts/buffer-full"]=(count==_contactPoints.Length).ToString();
                for(int i=0;i<count;i++){var c=_contactPoints[i];string key="contact/"+i;f[key+"/collider"]=(c.collider==null ? 0 : c.collider.GetInstanceID()).ToString();f[key+"/other"]=(c.otherCollider==null ? 0 : c.otherCollider.GetInstanceID()).ToString();SimulationFieldCapture.AddVector(f,key+"/point",c.point.x,c.point.y);SimulationFieldCapture.AddVector(f,key+"/normal",c.normal.x,c.normal.y);f[key+"/separation"]=SimulationTraceComparer.Float(c.separation);f[key+"/normal-impulse"]=SimulationTraceComparer.Float(c.normalImpulse);f[key+"/tangent-impulse"]=SimulationTraceComparer.Float(c.tangentImpulse);}
            }
            var origin=UnityEngine.Object.FindFirstObjectByType<FloatingOrigin>();SimulationFieldCapture.AddVector(f,"origin",origin==null ? 0 : origin.currentOrigin.x,origin==null ? 0 : origin.currentOrigin.y);
            f["clock/time"]=SimulationTraceComparer.Double(Time.timeAsDouble);f["clock/fixed-time"]=SimulationTraceComparer.Double(Time.fixedTimeAsDouble);f["clock/realtime"]=SimulationTraceComparer.Double(Time.realtimeSinceStartupAsDouble);
            f["clock/delta"]=SimulationTraceComparer.Float(Time.deltaTime);f["clock/unscaled-delta"]=SimulationTraceComparer.Float(Time.unscaledDeltaTime);f["clock/fixed-delta"]=SimulationTraceComparer.Float(Time.fixedDeltaTime);f["clock/scale"]=SimulationTraceComparer.Float(Time.timeScale);
            f["clock/capture-delta"]=SimulationTraceComparer.Float(Time.captureDeltaTime);f["clock/target-fps"]=Application.targetFrameRate.ToString();f["clock/vsync"]=QualitySettings.vSyncCount.ToString();
            f["focused"]=Application.isFocused.ToString();f["menu/open"]=(Player.pauseMenu!=null && Player.pauseMenu.menuOpen).ToString();f["menu/game-paused"]=Player.GamePaused.ToString();
            SimulationFieldCapture.AddRandom(f,UnityEngine.Random.state);SimulationFieldCapture.AddEconomy(f,globalStats.currencyLookup,globalStats.globalUpgradeDict);
            foreach(var mover in _movers)if(mover!=null && mover.gameObject.activeInHierarchy && ((Vector2)mover.transform.position-b.position).sqrMagnitude<2048f*2048f){var body=_moverBody.GetValue(mover) as Rigidbody2D;if(body==null)continue;SimulationFieldCapture.AddMover(f,mover,mover.GetInstanceID());f["mover/"+mover.GetInstanceID()+"/sleeping"]=body.IsSleeping().ToString();}
            foreach(var orb in _orbs)if(orb!=null && orb.gameObject.activeInHierarchy && ((Vector2)orb.transform.position-b.position).sqrMagnitude<1024f*1024f)SimulationFieldCapture.AddOrb(f,orb,orb.GetInstanceID());
            _frame.Samples.Add(sample);
        }
        private static int Zone()=>UnityEngine.Object.FindFirstObjectByType<ZoneLoader>()?.activeZone??0;
        private static int Area(){var s=UnityEngine.Object.FindFirstObjectByType<globalStats>();return s==null ? 0 : (int)s.currentA1State;}
        public void EndFrame()
        {
            if(_packing?.IsCompleted==true){if(_packing.IsFaulted){Error=_packing.Exception.GetBaseException().Message;Status="Export failed; raw data remains in captures.";}else Status="Gameplay ZIP ready: "+Path.GetFileName(LastZip);_packing=null;}
            var current=FindPlayer();
            if(_startName!=null && !Recording && !Packing){string name=_startName;_startName=null;Start(current,name,_startNotes);return;}
            if(!Recording)return;
            bool changed=current==null || current.GetInstanceID()!=_playerId || current.gameObject.scene.handle!=_sceneHandle || Zone()!=_zone || Area()!=_areaState;
            if(!changed && _frame!=null && _journal!=null){Sample("frame-end",Pu);_frame.PhysicsUpdates=Pu;if(_journal.Append(_frame)){CapturedFrames++;CapturedPus+=Pu;}else RequestStop(_journal.Error.Length>0 ? _journal.Error : "Diagnostic size or writer backlog limit reached");}
            _frame=null;if(_stopReason!=null){Finish();return;}
            if(changed){ClosePart("Scene, player or area changed");Player=current;if(_closedBytes>=GameplayJournal.DefaultLimit || _partNumber>=64){_stopReason="Diagnostic session limit reached";Finish();}else if(current!=null)StartPart(current);else Status="Recording armed; waiting for gameplay to resume.";}
        }
        private void Start(Movement player,string name,string notes)
        {
            if(player==null)throw new InvalidOperationException("Enter gameplay before recording.");if(name.Length>35)name=name.Substring(0,35);
            string id=name+"-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N").Substring(0,6);
            _directory=Path.Combine(Root,"captures",id);Directory.CreateDirectory(_directory);CapturedFrames=0;CapturedPus=0;_partNumber=0;_closedBytes=0;_parts.Clear();_stopReason=null;Error="";LastZip="";
            _manifest=new JObject {["schema"]=2,["recorderVersion"]="1.1.1",["mode"]="passive-gameplay",["name"]=id,["notes"]=notes.Substring(0,Math.Min(notes.Length,1000)),["startedUtc"]=DateTime.UtcNow.ToString("O"),
                ["gameHash"]=SimulationTraceComparer.FileHash(typeof(Movement).Assembly.Location),["unityVersion"]=Application.unityVersion,["timing"]="Native variable frame deltas and physics updates",["savePolicy"]="Normal game saving remains enabled",
                ["coverage"]="Current-state snapshots and nearby mechanic observations. Native contact caches and pending timer/coroutine deadlines are not fully reconstructible.",["parts"]=_parts};
            SearchFiles.Write(Path.Combine(_directory,"environment.json"),new {Mode="passive-gameplay",Unity=Application.unityVersion,TargetFps=Application.targetFrameRate,VSync=QualitySettings.vSyncCount,FixedDelta=Time.fixedDeltaTime,CaptureDelta=Time.captureDeltaTime,
                Mods=Directory.GetDirectories(Path.Combine(Directory.GetParent(Application.dataPath).FullName,"Recharge","Mods")).Where(d=>File.Exists(Path.Combine(d,"mod.json"))).Select(d=>new {Manifest=SearchFiles.Read<JObject>(Path.Combine(d,"mod.json")),Dlls=Directory.GetFiles(d,"*.dll").Select(f=>new {Name=Path.GetFileName(f),Hash=SimulationTraceComparer.FileHash(f)}).ToArray()}).ToArray()});
            Recording=true;try{StartPart(player);Status="Recording normal gameplay. F8 stops and exports.";}catch{Recording=false;throw;}
        }
        private void StartPart(Movement player)
        {
            Player=player;_playerId=player.GetInstanceID();_sceneHandle=player.gameObject.scene.handle;_zone=Zone();_areaState=Area();
            _partDirectory=Path.Combine(_directory,"part-"+(++_partNumber).ToString("D4"));Directory.CreateDirectory(_partDirectory);LiveWorldCapture.Export(player,_partDirectory);
            _movers=UnityEngine.Object.FindObjectsByType<PlatformMover>(FindObjectsInactive.Include,FindObjectsSortMode.None);_orbs=UnityEngine.Object.FindObjectsByType<JiggleDropScript>(FindObjectsInactive.Include,FindObjectsSortMode.None);
            _playerColliders=player.GetComponents<Collider2D>();
            _journal=new GameplayJournal(Path.Combine(_partDirectory,"gameplay.frames.jsonl.gz"),CapturedFrames,Math.Max(1,GameplayJournal.DefaultLimit-_closedBytes));Contacts?.Begin(_partDirectory);
            _parts.Add(new JObject {["directory"]=Path.GetFileName(_partDirectory),["scene"]=player.gameObject.scene.path,["playerId"]=_playerId,["firstFrame"]=CapturedFrames,["zone"]=_zone,["areaState"]=_areaState});
        }
        private void ClosePart(string reason)
        {
            if(_journal==null)return;Contacts?.End();_journal.Dispose();var part=(JObject)_parts.Last;part["frames"]=_journal.Frames;part["pus"]=_journal.Pus;part["rawBytes"]=_journal.RawBytes;part["endReason"]=reason;part["error"]=_journal.Error;_closedBytes+=_journal.RawBytes;if(_journal.Error.Length>0)Error=_journal.Error;_journal=null;
        }
        private void Finish()
        {
            Recording=false;ClosePart(_stopReason);Player=null;_frame=null;_manifest["finishedUtc"]=DateTime.UtcNow.ToString("O");_manifest["frames"]=CapturedFrames;_manifest["pus"]=CapturedPus;_manifest["stopReason"]=_stopReason;_manifest["complete"]=Error.Length==0;
            _manifest["parts"]=_parts.DeepClone();
            string root=_directory;var manifest=(JObject)_manifest.DeepClone();LastZip=Path.Combine(Root,"exports",(string)_manifest["name"]+"-validation.zip");string zip=LastZip;
            _packing=Task.Run(()=>PassiveBundle.Write(root,zip,manifest));Status="Packing gameplay observations...";_stopReason=null;
        }
        public void RuntimeError(Exception e){Error=e.Message;Status=e.Message;if(Recording){_stopReason="Capture error: "+e.Message;try{Finish();}catch{Recording=false;}}}
        public void OpenOutput()=>Process.Start(new ProcessStartInfo {FileName=Path.Combine(Root,"exports"),UseShellExecute=true});
        public void Dispose(){if(Recording){_stopReason="Recorder unloaded";Finish();}}
    }
}
