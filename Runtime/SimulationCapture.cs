using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Search;
using Igtap.Tas.Core.Simulation;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Igtap.Tas.Runtime
{
    // Explicit development export, taken at a frozen frame boundary. It never
    // enables live colliders, advances game physics, clears spikes or changes
    // live fields. Missing geometry is baked on collider-only private copies.
    internal static class SimulationCapture
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private static readonly Dictionary<(Type,bool),FieldInfo[]> FieldSchemas=new Dictionary<(Type,bool),FieldInfo[]>();
        private static readonly Dictionary<(Type,bool),PropertyInfo[]> PropertySchemas=new Dictionary<(Type,bool),PropertyInfo[]>();
        private static readonly Dictionary<Assembly,string> AssemblyNames=new Dictionary<Assembly,string>();
        private static string AssemblyName(Type type)
        {if(!AssemblyNames.TryGetValue(type.Assembly,out var name))AssemblyNames.Add(type.Assembly,name=type.Assembly.GetName().Name);return name;}
        private static FieldInfo[] Schema(Type type,bool script)
        {
            if(FieldSchemas.TryGetValue((type,script),out var schema))return schema;
            var fields=new List<FieldInfo>();
            for(var t=type;t!=null && t!=(script?typeof(MonoBehaviour):typeof(object)) && (!script || t!=typeof(Object));t=t.BaseType)
                foreach(var field in t.GetFields(Fields))if(!typeof(Delegate).IsAssignableFrom(field.FieldType))fields.Add(field);
            FieldSchemas.Add((type,script),schema=fields.ToArray());return schema;
        }
        private static Rigidbody2D _historyBody;
        private static Vector2 _before, _from, _to;
        private static Vector3 _historyOrigin;
        private static FloatingOrigin _origin;
        private static bool _pending, _historyValid;
        private static JiggleDropScript[] _orbs = Array.Empty<JiggleDropScript>();
        private static PlatformMover[] _movers = Array.Empty<PlatformMover>();
        private static PlatformMover[] _historyMovers;
        private static SpringScript[] _springs = Array.Empty<SpringScript>();
        private static upgradeBox[] _boxes=Array.Empty<upgradeBox>();
        private static localUpgrades[] _upgrades=Array.Empty<localUpgrades>();
        private sealed class RestHistory { public int Pus; public bool Awake; public Vector2 Position; public float Rotation; }
        private static readonly Dictionary<Rigidbody2D,RestHistory> MoverRest = new Dictionary<Rigidbody2D,RestHistory>();
        private static readonly FieldInfo MoverBody = typeof(PlatformMover).GetField("body", BindingFlags.Instance | BindingFlags.NonPublic);
        public static void BeginHistory() { MoverRest.Clear(); _historyMovers = null; }
        public static bool CollectHistory;
        public static void ResetHistory()
        { _historyBody = null; _historyMovers = null; _origin = null; _pending = _historyValid = CollectHistory = false; _orbs = Array.Empty<JiggleDropScript>(); _movers = Array.Empty<PlatformMover>(); _springs = Array.Empty<SpringScript>(); _boxes=Array.Empty<upgradeBox>(); _upgrades=Array.Empty<localUpgrades>(); MoverRest.Clear(); SimulationCoroutineCapture.Clear(); }
        public static void ObserveBeforePhysics()
        {
            var s = GamePatches.Session; _pending = false;
            if (s == null || !s.Active || s.Frozen || s.WaitingForLoad || s.Adapter.Player == null) return;
            var body = s.Adapter.Player.GetComponent<Rigidbody2D>();
            if (_historyBody != body) { _historyBody = body; _historyValid = false; _origin = null; _historyMovers = null; MoverRest.Clear(); }
            if (body == null) return;
            _before = body.position; _pending = true;
        }
        public static void ObserveAfterPhysics()
        {
            if (!_pending || _historyBody == null) return;
            _from = _before; _to = _historyBody.position;
            if (_origin == null) _origin=Object.FindFirstObjectByType<FloatingOrigin>();
            _historyOrigin=_origin==null ? Vector3.zero : _origin.currentOrigin;
            _historyValid = true; _pending = false;
            // Keep just the player's last PU endpoints even with the preview
            // off. This is two vector reads; expensive mover inspection stays
            // opt-in. Enabling a mid-run preview then has real interpolation.
            if (!CollectHistory) return;
            float linearLimit=Physics2D.linearSleepTolerance*Physics2D.linearSleepTolerance;
            // Include inactive movers so later activation does not require a
            // scene-wide search every PU. Scene/player replacement invalidates.
            if (_historyMovers == null) _historyMovers = Resources.FindObjectsOfTypeAll<PlatformMover>();
            foreach(var mover in _historyMovers) {
                if(mover==null || !mover.gameObject.scene.IsValid() || !mover.gameObject.activeInHierarchy) continue;
                var body=(Rigidbody2D)MoverBody.GetValue(mover); if(body==null || !body.simulated) continue;
                if(!MoverRest.TryGetValue(body,out var rest)) MoverRest.Add(body,rest=new RestHistory {Awake=true});
                bool awake=body.IsAwake();
                bool quiet=body.linearVelocity.sqrMagnitude<=linearLimit && Math.Abs(body.angularVelocity)<=Physics2D.angularSleepTolerance;
                bool unmoved=body.position==rest.Position && body.rotation==rest.Rotation;
                rest.Pus=awake && quiet && body.sleepMode!=RigidbodySleepMode2D.NeverSleep ? (rest.Awake && unmoved ? rest.Pus+1 : 1) : 0;
                rest.Awake=awake; rest.Position=body.position; rest.Rotation=body.rotation;
            }
        }
        internal sealed class Snapshot
        {
            internal JObject World,Start,Animation,Geometry;
            internal SimulationTrace Trace;
            // Everything is detached primitive/JToken data. This method can
            // serialize/hash on a worker without any Unity/native API access.
            internal void Write(string directory)
            {
                Directory.CreateDirectory(directory);
                SearchFiles.Write(Path.Combine(directory,"animation.json"),Animation);
                SearchFiles.Write(Path.Combine(directory,"geometry-report.json"),Geometry);
                SearchFiles.Write(Path.Combine(directory,"world.json"),World);
                SearchFiles.Write(Path.Combine(directory,"start.json"),Start);
                Trace.AnimationHash=SimulationTraceComparer.FileHash(Path.Combine(directory,"animation.json"));
                Trace.WorldHash=SimulationTraceComparer.FileHash(Path.Combine(directory,"world.json"));
                SearchFiles.Write(Path.Combine(directory,"native.trace.json"),Trace);
            }
        }
        public static string Export(TasSession session, string directory)
        {
            var capture=Capture(session);
            using(session.Profiler.Measure(ProfileSection.CaptureWrite))capture.Write(directory);
            return directory;
        }
        internal static Snapshot Capture(TasSession session)
        {
            if (!session.Active || !session.Frozen || session.WaitingForLoad || !session.Adapter.IsAlive || session.HasPendingEdits)
                throw new InvalidOperationException("Pause an initialized TAS and finish pending edits before exporting the simulator test.");
            session.Adapter.ValidateLoadedMap(session.Movie.StartSetup,true);
            JObject animation;
            using(session.Profiler.Measure(ProfileSection.CaptureAnimation))
                animation=SimulationAnimationCapture.Capture(session.Adapter.Player);
            var coverage = new SortedSet<string>(StringComparer.Ordinal);
            GameObject[] objects;Dictionary<GameObject,Component[]> componentSets;Component[] allComponents;
            using(session.Profiler.Measure(ProfileSection.CaptureDiscovery))
            {
                objects=Resources.FindObjectsOfTypeAll<GameObject>().Where(go=>go!=null && go.scene.IsValid() && (go.hideFlags&HideFlags.HideAndDontSave)==0).ToArray();
                componentSets=objects.ToDictionary(go=>go,go=>go.GetComponents<Component>());
                allComponents=objects.SelectMany(go=>componentSets[go]).Where(c=>c!=null).ToArray();
            }
            var colliders = allComponents.OfType<Collider2D>().ToArray();
            _orbs = allComponents.OfType<JiggleDropScript>().ToArray();
            _movers = allComponents.OfType<PlatformMover>().ToArray();
            _historyMovers = _movers;
            _springs = allComponents.OfType<SpringScript>().ToArray();
            _boxes=allComponents.OfType<upgradeBox>().ToArray();
            _upgrades=allComponents.OfType<localUpgrades>().ToArray();
            string randomBefore=JsonUtility.ToJson(UnityEngine.Random.state);
            using var geometry=new SimulationGeometryCapture();
            var nativeShapes=new Dictionary<int,JObject>();
            using(session.Profiler.Measure(ProfileSection.CaptureGeometry))foreach(var collider in colliders)
            {
                var baked=SimulationGeometryCapture.Read(collider); nativeShapes.Add(collider.GetInstanceID(),baked);
                geometry.ValidateActive(collider,baked);
            }
            var world = new JObject { ["schema"] = 1, ["activeScene"] = SceneManager.GetActiveScene().name,
                ["time"] = new JObject { ["fixedDeltaTime"] = Time.fixedDeltaTime, ["timeScale"] = session.PreviewTimeScale,
                    ["nativeTime"] = Time.timeAsDouble, ["nativeFixedTime"] = Time.fixedTimeAsDouble,
                    ["captureTicks"] = Time.captureDeltaTimeRational.Count },
                ["physics2d"] = Properties(null, typeof(Physics2D), coverage),
                ["layerNames"] = new JArray(Enumerable.Range(0, 32).Select(LayerMask.LayerToName)),
                ["layerCollisionMasks"] = new JArray(Enumerable.Range(0, 32).Select(Physics2D.GetLayerCollisionMask)) };
            var nodes = new JArray(); world["objects"] = nodes;
            var external=new JArray();world["externalMechanics"]=external;
            using(session.Profiler.Measure(ProfileSection.CaptureMetadata))foreach (var go in objects)
            {
                var t = go.transform;
                var node = new JObject { ["id"] = go.GetInstanceID(), ["transformId"] = t.GetInstanceID(), ["name"] = go.name,
                    ["scene"] = go.scene.name, ["parent"] = t.parent == null ? 0 : t.parent.gameObject.GetInstanceID(),
                    ["siblingIndex"] = t.GetSiblingIndex(), ["activeSelf"] = go.activeSelf, ["activeInHierarchy"] = go.activeInHierarchy,
                    ["layer"] = go.layer, ["tag"] = go.tag, ["position"] = Value(t.position), ["rotation"] = Value(t.rotation),
                    ["lossyScale"] = Value(t.lossyScale), ["localPosition"] = Value(t.localPosition),
                    ["localRotation"] = Value(t.localRotation), ["localScale"] = Value(t.localScale) };
                var components = new JArray(); node["components"] = components; nodes.Add(node);
                foreach (var c in componentSets[go])
                {
                    if (c == null || c is Transform) continue;
                    var type = c.GetType();
                    var part = new JObject { ["id"] = c.GetInstanceID(), ["type"] = type.FullName, ["assembly"] = AssemblyName(type) };
                    if (c is Behaviour behaviour) part["enabled"] = behaviour.enabled;
                    if (c is MonoBehaviour && type.Assembly == typeof(Movement).Assembly) part["fields"] = ReadFields(c);
                    if (c is Rigidbody2D || c is Collider2D) part["props"] = Properties(c, type, coverage);
                    if (c is Collider2D collider)
                    {
                        // Detached geometry remains usable on custom maps. Stop
                        // unknown Navigator interactions instead of silently
                        // treating an upgrade/group/area trigger as empty space.
                        string[] mechanics=collider.GetComponentsInParent<MonoBehaviour>(true).Where(script=>script!=null&&script.enabled&&
                            (AssemblyName(script.GetType())=="Recharge.Module.Maps"||AssemblyName(script.GetType())=="RechargeMaps")&&
                            !new[]{"MapFinishLog","MapCloneVisibility","MapCreditsShow"}.Contains(script.GetType().Name))
                            .Select(script=>script.GetType().Name).Distinct().ToArray();
                        if(mechanics.Length>0)external.Add(new JObject{["collider"]=collider.GetInstanceID(),["types"]=new JArray(mechanics)});
                        // Collider2D's effective material can be inherited from
                        // its body. Keep the native friction/bounciness getters.
                        var baked=nativeShapes[collider.GetInstanceID()];
                        bool source=collider.compositeOperation!=Collider2D.CompositeOperation.None &&
                            (collider.composite!=null || collider.attachedRigidbody?.GetComponent<CompositeCollider2D>()!=null);
                        if (baked["list"].Count()==0 && !source)
                        {
                            JObject recovered;using(session.Profiler.Measure(ProfileSection.CaptureGeometry))recovered=geometry.Recover(collider);
                            if (recovered!=null) { baked=recovered; part["geometrySource"]="isolated-native-bake"; }
                        }
                        part["shapes"]=baked;
                        if (collider is CompositeCollider2D composite)
                        {
                            JObject allSources;using(session.Profiler.Measure(ProfileSection.CaptureGeometry))allSources=geometry.AllCompositeSources(composite);
                            if(allSources!=null) part["sourceShapes"]=allSources;
                            else coverage.Add("Composite source activation requires validation: " + Hierarchy(collider.transform));
                        }
                        if (baked["list"].Count()==0 && !source && part["geometrySource"]==null)
                            coverage.Add("Missing baked geometry: " + type.Name + " " + Hierarchy(collider.transform));
                        if (collider.usedByEffector) coverage.Add("Effector requires validation: " + Hierarchy(collider.transform));
                    }
                    if (c is Joint2D) coverage.Add("Joint requires validation: " + type.Name);
                    components.Add(part);
                }
            }
            var statics = new JArray(); world["statics"] = statics;
            // Reading a static can run its type initializer. Limit capture to
            // instantiated component types whose constructors already ran.
            var componentTypes=allComponents.OfType<MonoBehaviour>().Where(c=>c.GetType().Assembly==typeof(Movement).Assembly).Select(c=>c.GetType()).Distinct().ToArray();
            foreach (Type type in componentTypes.Where(t => !t.ContainsGenericParameters))
                foreach (var f in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (!f.IsLiteral && !typeof(Delegate).IsAssignableFrom(f.FieldType))
                        statics.Add(new JArray(type.FullName, f.Name, Value(f.GetValue(null))));
            foreach (Type type in componentTypes)
            {
                Type singleton = type.BaseType;
                while (singleton != null && !(singleton.IsGenericType && singleton.GetGenericTypeDefinition().FullName == "Singleton`1")) singleton = singleton.BaseType;
                if (singleton == null) continue;
                foreach (var f in singleton.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (!f.IsLiteral) statics.Add(new JArray("Singleton`1[" + singleton.GetGenericArguments()[0].FullName + "]", f.Name, Value(f.GetValue(null))));
            }
            var p = session.Adapter.Player; var body = p.GetComponent<Rigidbody2D>();
            // IsTouching reads the last native physics update, not geometry
            // overlap. This distinguishes Enter from Stay after a snapshot;
            // spike grace and other trigger scripts depend on that difference.
            var triggerContacts = new JArray();
            foreach(var self in p.GetComponents<Collider2D>())
                foreach(var other in colliders)
                    if((self.isTrigger || other.isTrigger) && other.attachedRigidbody!=body && self.IsTouching(other))
                        triggerContacts.Add(new JArray(self.GetInstanceID(),other.GetInstanceID()));
            var start = new JObject { ["movement"] = ReadFields(p), ["engine"] = new JArray(),
                ["playerTriggerContacts"] = triggerContacts,
                ["position"] = Value(p.transform.position), ["localScale"] = Value(p.transform.localScale),
                ["transformRotation"] = Value(p.transform.rotation), ["bodyPosition"] = Value(body.position),
                ["velocity"] = Value(body.linearVelocity), ["rotation"] = body.rotation, ["angularVelocity"] = body.angularVelocity,
                ["courses"] = new JArray(), ["pending"] = new JArray(),
                ["moverContinuations"] = SimulationCoroutineCapture.Export(_movers, value => Value(value)),
                ["moverSleep"] = new JArray(_movers.Select(m => (Rigidbody2D)MoverBody.GetValue(m)).Where(b => b!=null).Distinct().Select(b =>
                    new JObject { ["id"]=b.GetInstanceID(), ["sleeping"]=b.IsSleeping(),
                        ["restSeconds"]=(MoverRest.TryGetValue(b,out var h) && b.position==h.Position && b.rotation==h.Rotation ? h.Pus : 0)*Time.fixedDeltaTime })),
                ["colliders"] = new JArray(p.GetComponents<BoxCollider2D>().Select(c => new JArray(c.offset.x, c.offset.y,
                    c.size.x, c.size.y, c.excludeLayers.value, c.enabled))) };
            var random = JObject.Parse(JsonUtility.ToJson(UnityEngine.Random.state));
            if(VmanScript.isCurrentlyVman)
                start["vman"]=new JObject{["timeLeft"]=VmanScript.vmanTimeLeft,["blockEnding"]=(bool)typeof(VmanScript).GetField("BlockEnding",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).GetValue(Singleton<VmanScript>.Instance)};
            if(session.Movie.StartSetup?.IsCustomMap==true)
                world["customMap"]=new JObject{["id"]=session.Movie.StartSetup.CustomMapId,["hash"]=session.Movie.StartSetup.CustomMapHash};
            start["random"] = new JArray(Enumerable.Range(0, 4).Select(i => (int)random["s" + i]));
            if (_historyValid && _historyBody == body)
            {
                var origin=Object.FindFirstObjectByType<FloatingOrigin>(); var currentOrigin=origin==null ? Vector3.zero : origin.currentOrigin;
                // A rebase resumes after Physics2DFixedUpdate. Unity's following
                // early physics sync invalidates interpolation; the last PU
                // observer still holds positions from the old origin.
                bool valid=currentOrigin==_historyOrigin;
                start["interpolationValid"]=valid;
                start["stepFrom"]=Value(valid ? _from : body.position); start["stepTo"]=Value(valid ? _to : body.position);
            }
            else coverage.Add("Native interpolation history is not available at this capture boundary.");
            foreach (var method in new[] { "cancelDashBuffer", "cancelJumpBuffer", "cancelJumpCutBuffer", "endRecentJump", "reEnableNormalCollider", "deathRespawn", "cancelSpringCutscene" })
                if (session.MovementTimers.Contains(p, method)) coverage.Add("Pending native Invoke must be reconstructed: Movement." + method);
            coverage.Add("Native contact caches and running coroutines are not exported; this development capture needs prefix reconstruction validation.");
            if (randomBefore!=JsonUtility.ToJson(UnityEngine.Random.state)) throw new InvalidOperationException("Geometry inspection changed the live random state.");
            var previous = session.CurrentInput;
            if (!previous.ExactEdges)
            {
                previous.Press = previous.Held & ~session.PreviousInput.Held;
                previous.Release = session.PreviousInput.Held & ~previous.Held;
                previous.ExactEdges = true;
            }
            var trace = new SimulationTrace { CaptureAllUpgradeBoxes=true, CaptureUpgrades=true, CaptureOrbs = true, CaptureMovers = true, CaptureSprings = true, Fps = session.Movie.FrameRate, StartFrame = session.Cursor, StartPhase = session.PuPhase,
                Previous = previous, GameHash = session.Movie.GameHash, Coverage = coverage.ToArray() };
            trace.Samples.Add(Sample(session, 0, 0, "start"));
            return new Snapshot{World=world,Start=start,Animation=animation,Geometry=geometry.Report,Trace=trace};
        }

        public static SimulationSample Sample(TasSession session, int frame, int pu, string phase)
        {
            var p = session.Adapter.Player; var b = p.GetComponent<Rigidbody2D>();
            var sample = new SimulationSample { Frame = frame, Pu = pu, Phase = phase };
            SimulationFieldCapture.AddPlayer(sample.Fields, p);
            SimulationFieldCapture.AddVector(sample.Fields, "body/position", b.position.x, b.position.y);
            SimulationFieldCapture.AddVector(sample.Fields, "body/velocity", b.linearVelocity.x, b.linearVelocity.y);
            SimulationFieldCapture.AddVector(sample.Fields, "render/position", p.transform.position.x, p.transform.position.y);
            if (_origin == null) _origin = Object.FindFirstObjectByType<FloatingOrigin>();
            var origin = _origin;
            SimulationFieldCapture.AddVector(sample.Fields, "origin", origin == null ? 0 : origin.currentOrigin.x, origin == null ? 0 : origin.currentOrigin.y);
            sample.Fields["clock/native-time"] = SimulationTraceComparer.Double(Time.timeAsDouble);
            sample.Fields["clock/fixed-time"] = SimulationTraceComparer.Double(Time.fixedTimeAsDouble);
            sample.Fields["clock/capture-ticks"] = Time.captureDeltaTimeRational.Count.ToString(CultureInfo.InvariantCulture);
            SimulationFieldCapture.AddRandom(sample.Fields, UnityEngine.Random.state);
            SimulationFieldCapture.AddEconomy(sample.Fields, globalStats.currencyLookup, globalStats.globalUpgradeDict);
            if(VmanScript.isCurrentlyVman)SimulationFieldCapture.AddVman(sample.Fields,VmanScript.vmanTimeLeft,VmanScript.vmanScore);
            foreach(var box in _boxes)if(box!=null)SimulationFieldCapture.AddCashBox(sample.Fields,box,box.GetInstanceID());
            foreach(var upgrades in _upgrades)if(upgrades!=null)SimulationFieldCapture.AddLocalUpgrades(sample.Fields,upgrades,upgrades.GetInstanceID());
            foreach (var orb in _orbs) if (orb != null) SimulationFieldCapture.AddOrb(sample.Fields, orb, orb.GetInstanceID());
            foreach (var spring in _springs) if (spring != null) SimulationFieldCapture.AddScript(sample.Fields, spring, "spring/" + spring.GetInstanceID());
            foreach (var mover in _movers) if (mover != null) {
                var body=(Rigidbody2D)SimulationFieldCapture.AddMover(sample.Fields, mover, mover.GetInstanceID());
                sample.Fields["mover/"+mover.GetInstanceID()+"/sleeping"]=body.IsSleeping().ToString();
            }
            return sample;
        }

        private static JArray ReadFields(object target)
        {
            var result = new JArray();
            foreach(var f in Schema(target.GetType(),true))result.Add(new JArray(f.DeclaringType.FullName,f.Name,Value(f.GetValue(target))));
            return result;
        }
        private static JObject Properties(object target, Type type, SortedSet<string> coverage)
        {
            var result = new JObject();
            bool isStatic=target==null;
            if(!PropertySchemas.TryGetValue((type,isStatic),out var schema))
            {
                schema=type.GetProperties(BindingFlags.Public|(isStatic?BindingFlags.Static:BindingFlags.Instance)).Where(p=>p.CanRead && p.GetIndexParameters().Length==0 && !p.IsDefined(typeof(ObsoleteAttribute),true) && Simple(p.PropertyType)).ToArray();
                PropertySchemas.Add((type,isStatic),schema);
            }
            foreach (var p in schema)
            {
                try { result[p.Name] = Value(p.GetValue(target)); }
                catch (Exception e) { coverage.Add("Property not captured: " + type.Name + "." + p.Name + " (" + e.GetType().Name + ")"); }
            }
            return result;
        }
        private static bool Simple(Type t) => t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(Vector2) ||
            t == typeof(Vector3) || t == typeof(Quaternion) || t == typeof(LayerMask) || typeof(Object).IsAssignableFrom(t);
        private static JToken Value(object value, int depth = 0)
        {
            if (value == null || depth > 12 || value is Delegate) return JValue.CreateNull();
            Type type = value.GetType();
            if (value is Object o) return o == null ? JValue.CreateNull() : new JObject { ["$ref"] = o.GetInstanceID() };
            if (type.IsEnum) return new JValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            if (value is LayerMask mask) return new JValue(mask.value);
            if (type.IsPrimitive || value is string || value is decimal) return new JValue(value);
            if (value is IDictionary dictionary)
            { var pairs = new JArray(); foreach (DictionaryEntry entry in dictionary) pairs.Add(new JArray(Value(entry.Key, depth + 1), Value(entry.Value, depth + 1))); return new JObject { ["$dict"] = pairs }; }
            if (value is IEnumerable items)
            { var array = new JArray(); foreach (var item in items) { if (array.Count >= Movie.MaxFrames) throw new InvalidDataException("Export collection exceeds limit."); array.Add(Value(item, depth + 1)); } return array; }
            // Engine-owned action/audio/renderer handles are recreated by the shim.
            if (AssemblyName(type).StartsWith("Unity", StringComparison.Ordinal) && !type.IsValueType) return JValue.CreateNull();
            // Unity's vectors use x/y/z as their serialized field names.
            if (type.Namespace == "UnityEngine")
            { var simple = new JObject(); foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public)) simple[f.Name] = Value(f.GetValue(value), depth + 1); return simple; }
            var fields = new JObject();
            foreach(var f in Schema(type,false))fields[f.DeclaringType.Name+"::"+f.Name]=Value(f.GetValue(value),depth+1);
            return fields;
        }
        private static string Hierarchy(Transform transform) => transform.parent == null ? transform.name : Hierarchy(transform.parent) + "/" + transform.name;
    }
}
