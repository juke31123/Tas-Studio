using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Igtap.Tas.Core;
using Recharge.ModApi;
using UnityEngine;

namespace Igtap.Tas.Runtime
{
    // Optional Navigator integration. No loader dependency is shipped with Studio.
    // Build through Navigator's own map builder, keeping its save swap out of TAS runs.
    internal static class CustomMaps
    {
        private const BindingFlags Any=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
        private static Type _managerType;
        private static Assembly _assembly;
        private static object _manager;
        private static bool _built;
        private static int _settle;
        private static bool _leased;
        private static string _loadedId="",_loadedName="",_loadedHash="",_observedId="",_observedName="";
        private static int _generation;
        internal static string CurrentId=>Resolve()?(_managerType.GetProperty("CurrentMapId",Any)?.GetValue(null) as string??""):"";
        internal static string CurrentLabel
        {
            get
            {
                string id=CurrentId;
                if(id.Length==0)return UnityEngine.SceneManagement.SceneManager.GetActiveScene().path==ConfiguredStart.MenuScene?"Main menu":"Base game";
                if(id==_loadedId)return "Custom: "+_loadedName+" ["+id+"]";
                if(id!=_observedId){_observedId=id;_observedName=CustomMapCatalog.List(MapsRoot).FirstOrDefault(m=>m.Id==id)?.Name??id;}
                return "Custom: "+_observedName+" ["+id+"]";
            }
        }
        internal static string CurrentKey=>_generation+":"+UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle+":"+CurrentId;
        internal static void RequireLoaded(TasStartSetup setup,bool verifyFiles=false)
        {
            string id=CurrentId;
            if(setup==null){if(id.Length>0)throw new InvalidOperationException("The loaded custom map has no configured TAS identity.");return;}
            setup.RequireLoadedMap(id,_built&&id==_loadedId?_loadedHash:"");
            if(verifyFiles)PrepareStart(setup,false);
        }
        internal static string MapsRoot
        {
            get
            {
                Resolve();var paths=_assembly?.GetType(!string.IsNullOrEmpty(_managerType.Namespace)?_managerType.Namespace+".MapPaths":"MapPaths");
                return paths?.GetProperty("MapsDir",Any)?.GetValue(null) as string??Path.Combine(Directory.GetParent(Application.dataPath).FullName,"Recharge","Mods","recharge.maps","maps");
            }
        }
        private static bool Resolve()
        {
            if(_managerType!=null)return true;
            foreach(var name in new[]{"Recharge.Module.Maps","RechargeMaps"})
            {
                var assembly=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.GetName().Name==name);
                var type=assembly?.GetType(name=="Recharge.Module.Maps"?"Recharge.Module.Maps.MapManager":"MapManager");
                if(type==null)continue;_assembly=assembly;_managerType=type;return true;
            }
            return false;
        }
        internal static CustomMapInfo[] Available()=>Resolve()&&_managerType.GetProperty("Instance",Any)?.GetValue(null)!=null?CustomMapCatalog.List(MapsRoot):Array.Empty<CustomMapInfo>();
        internal static void PrepareStart(TasStartSetup setup,bool recording)
        {
            if(setup?.IsCustomMap!=true)return;
            if(!Resolve()||_managerType.GetProperty("Instance",Any)?.GetValue(null)==null)throw new InvalidOperationException("Enable Recharge Navigator to play this custom-map TAS.");
            string hash=CustomMapCatalog.Fingerprint(MapsRoot,setup.CustomMapId);
            if(!recording&&hash!=setup.CustomMapHash)throw new InvalidOperationException("The installed custom map differs from this TAS. Install the same map version: "+setup.CustomMapName);
            if(recording){setup.CustomMapHash=hash;setup.CustomMapName=CustomMapCatalog.List(MapsRoot).First(m=>m.Id==setup.CustomMapId).Name;}
        }
        internal static void Begin()
        {
            _built=false;_settle=0;_manager=null;_loadedId=_loadedName=_loadedHash="";_generation++;
            if(!Resolve())return;
            var saves=Type("MapSaves");
            saves.GetProperty("ActiveMapId",Any).SetValue(null,null);saves.GetProperty("StartedFresh",Any).SetValue(null,false);_leased=true;
        }
        internal static void End()
        {
            if(!_leased)return;var saves=Type("MapSaves");
            // Stopping leaves the map for the main menu. A previous map-save
            // owner must not autosave this TAS player into that older map.
            saves.GetProperty("ActiveMapId",Any).SetValue(null,null);saves.GetProperty("StartedFresh",Any).SetValue(null,false);
            _leased=false;_manager=null;
        }
        private static Type Type(string name)=>_assembly.GetType(string.IsNullOrEmpty(_managerType.Namespace)?name:_managerType.Namespace+"."+name,true);
        private static object Call(Type type,object target,string method,params object[] args)
        {
            try{return (type.GetMethod(method,Any)??throw new MissingMethodException(type.FullName,method)).Invoke(target,args);}
            catch(TargetInvocationException e){throw new InvalidOperationException("Navigator: "+e.InnerException?.Message,e.InnerException??e);}
        }
        internal static bool LoadDuringBarrier(TasStartSetup setup,Movement player)
        {
            if(setup?.IsCustomMap!=true)return true;
            if(!_built)
            {
                _manager=_managerType.GetProperty("Instance",Any).GetValue(null);
                Call(Type("MapUpgrades"),null,"Reset");
                Call(_managerType,_manager,"LoadMap",setup.CustomMapId);
                if((string)_managerType.GetProperty("CurrentMapId",Any).GetValue(null)!=setup.CustomMapId)throw new InvalidOperationException("Navigator could not build custom map "+setup.CustomMapName+".");
                _loadedId=setup.CustomMapId;_loadedName=setup.CustomMapName;_loadedHash=setup.CustomMapHash;
                _built=true;return false;
            }
            if(++_settle<6)return false;
            if(_settle==6)
            {
                // Tilemap colliders must be baked before the final native spawn raycast.
                foreach(var collider in UnityEngine.Object.FindObjectsByType<Collider2D>(FindObjectsInactive.Include,FindObjectsSortMode.None))
                    if(collider.GetType().Name=="TilemapCollider2D")collider.GetType().GetMethod("ProcessTilemapChanges",Any)?.Invoke(collider,null);
                Physics2D.SyncTransforms();
                var at=(Vector3)Call(Type("MapSpawn"),null,"SnapToGround",player.transform,player.transform.position);
                player.transform.position=at;var body=player.GetComponent<Rigidbody2D>();body.position=at;body.linearVelocity=Vector2.zero;
                var origin=UnityEngine.Object.FindFirstObjectByType<FloatingOrigin>();
                player.respawnPoint=(Vector2)at-(origin!=null?(Vector2)origin.currentOrigin:Vector2.zero)+new Vector2(0,12);
                player.courseResetPoint=player.Velocity=player.momentum=Vector2.zero;
                var world=_managerType.GetField("_world",Any).GetValue(_manager);
                Call(Type("MapUpgrades"),null,"RefreshLevelRefreshers",world);
                ConfiguredStart.ApplyAbilities(player,setup);
                player.cam.setup(at,player.cam.camSize);
            }
            return true;
        }
        private static bool AllowExternalTick()=>GamePatches.Session==null||!GamePatches.Session.Active||!GamePatches.Session.Frozen;
        private static bool AllowExternalSave()=>GamePatches.Session==null||!GamePatches.Session.Active;
        private static bool AllowExternalCoroutine(ref bool __result)
        {
            var session=GamePatches.Session;
            if(session==null||!session.Active||!session.Frozen||session.WaitingForLoad)return true;
            __result=true;return false;
        }
        private static bool SkipSettle(ref IEnumerator __result)
        {
            if(AllowExternalSave())return true;
            __result=Empty();return false;
        }
        private static IEnumerator Empty(){yield break;}
        internal static void Install(Harmony harmony)
        {
            if(!Resolve())return;
            foreach(var type in _assembly.GetTypes())
            foreach(var method in type.GetMethods(Any|BindingFlags.DeclaredOnly))
            {
                if(method.IsAbstract||method.ContainsGenericParameters||method.GetMethodBody()==null)continue;
                if(type.Name=="MapSaves"&&method.ReturnType==typeof(void)&&!method.IsSpecialName||type.Name=="MapUpgrades"&&(method.Name=="Save"||method.Name=="Load")||type.Name=="MapSpawn"&&method.Name=="TickSwitcher"||type.Name=="MapManager"&&(method.Name=="PlayMap"||method.Name=="LeaveTo"))
                    harmony.Patch(method,prefix:new HarmonyMethod(typeof(CustomMaps),nameof(AllowExternalSave)));
                else if(type.Name=="MapSpawn"&&(method.Name=="Settle"||method.Name=="HoldCamSize"))
                    harmony.Patch(method,prefix:new HarmonyMethod(typeof(CustomMaps),nameof(SkipSettle)));
                else if(method.Name=="MoveNext"&&typeof(IEnumerator).IsAssignableFrom(type))
                    harmony.Patch(method,prefix:new HarmonyMethod(typeof(CustomMaps),nameof(AllowExternalCoroutine)));
                else if(typeof(MonoBehaviour).IsAssignableFrom(type)&&(method.Name=="Update"||method.Name=="FixedUpdate"||method.Name=="LateUpdate"||method.Name=="OnTriggerEnter2D"||method.Name=="OnTriggerStay2D"||method.Name=="OnTriggerExit2D"))
                    harmony.Patch(method,prefix:new HarmonyMethod(typeof(CustomMaps),nameof(AllowExternalTick)));
            }
        }
    }
}
