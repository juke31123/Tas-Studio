using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Igtap.Tas.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Igtap.Tas.Runtime
{
    // Verification providers deliberately read fields, never arbitrary properties
    // or Unity object graphs. Extend these providers as new mechanics are supported.
    // Reconstruction executes the movie; these values must NEVER be teleported
    // back into the game to hide a replay mismatch.
    internal static class WorldCheckpoint
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private sealed class Provider
        {
            internal Type Type; internal FieldInfo[] Members; internal int Kind;
            internal Provider(Type type,int kind,params string[] names)
            {
                Type=type;Kind=kind;Members=Array.ConvertAll(names,name=>type.GetField(name,Fields)??
                    throw new NotSupportedException("Unsupported game build: missing "+type.Name+"."+name));
            }
        }
        private static readonly Provider[] Providers={
            new Provider(typeof(globalStats),0,"currentA1State","Zone2toZone1PathOpen","secretsFound","visitedTeleporters","DjBoxesTriggered","DashBoxesTriggered","grossAtomCount","effectiveAtomCount","vmanScore","vmanMult"),
            new Provider(typeof(ZoneLoader),0,"activeZone"),new Provider(typeof(FloatingOrigin),0,"currentOrigin","RebaseInProgress","queueUpdate"),
            new Provider(typeof(PlatformMover),0,"indexInPositions","linearV","diff","nextFramePos","currentFramePos","isPhaseChangeInProgess","onCooldown","exitedLastFrame","reset"),
            new Provider(typeof(JiggleDropScript),0,"cooldown","usable","refreshDash","refreshJump"),
            new Provider(typeof(courseScript),0,"courseNumber","currentPathTime","bestPathTime","tracking","currentPlayerPath","currentPlayerScales","reward","gpReward","rpReward","CdReward","tier","rewardTier","exemptFromAtomPrestige","compBoostTimeLeft","compBoostBonus"),
            new Provider(typeof(localUpgrades),0,"localUpgradeDict"),new Provider(typeof(timer),0,"time"),
            new Provider(typeof(Rigidbody2D),1),new Provider(typeof(Collider2D),2)
        };
        private static readonly Dictionary<Type,Provider> Matched=new Dictionary<Type,Provider>();
        private static readonly WorldWitnessCache ReplayEncoding=new WorldWitnessCache();
        private static Provider Resolve(Type type)
        {
            if(!Matched.TryGetValue(type,out var provider))
            {provider=Array.Find(Providers,p=>p.Type.IsAssignableFrom(type));Matched.Add(type,provider);}
            return provider;
        }
        private sealed class KeyNode
        {
            internal readonly string Text;private Dictionary<string,KeyNode> _children;
            internal KeyNode(string text){Text=text;}
            internal KeyNode Child(string suffix)
            {
                if(_children==null)_children=new Dictionary<string,KeyNode>(StringComparer.Ordinal);
                if(!_children.TryGetValue(suffix,out var node))_children.Add(suffix,node=new KeyNode(Text+"/"+suffix));
                return node;
            }
        }
        private readonly struct Key
        {
            internal readonly string Text;private readonly KeyNode _node;
            internal Key(string text){Text=text;_node=null;}
            internal Key(KeyNode node){Text=node.Text;_node=node;}
            internal Key Child(string suffix)=>_node==null?new Key(Text+"/"+suffix):new Key(_node.Child(suffix));
            // Course lists and upgrade dictionaries may grow/reset. Never keep
            // an unbounded hierarchy of keys for their historical entries.
            internal Key Dynamic(string suffix)=>new Key(Text+"/"+suffix);
        }
        // Reuse key strings, but re-read the hierarchy and component order on
        // every observation. A rename/reparent/sibling/component change creates
        // new immutable keys; earlier witnesses retain their original strings.
        private sealed class Context : IDisposable
        {
            private sealed class PathEntry {internal long Seen;internal string Parent,Name,Path;internal int Sibling;}
            private sealed class KeyEntry {internal long Seen;internal string Path;internal int Index;internal KeyNode Key;}
            private static readonly Context Shared=new Context();
            private readonly Dictionary<Transform,PathEntry> _paths = new Dictionary<Transform,PathEntry>();
            private readonly Dictionary<Component,KeyEntry> _keys = new Dictionary<Component,KeyEntry>();
            private readonly Dictionary<GameObject,Component[]> _components = new Dictionary<GameObject,Component[]>();
            private readonly List<Transform> _deadPaths=new List<Transform>();private readonly List<Component> _deadKeys=new List<Component>();
            private Scene _scene;private long _epoch;private bool _busy;
            internal static void Reset()
            {
                var c=Shared;if(c._busy)throw new InvalidOperationException("A capture is using the scene key cache.");
                c._paths.Clear();c._keys.Clear();c._components.Clear();c._deadPaths.Clear();c._deadKeys.Clear();c._scene=default;
                ReplayEncoding.Clear();
            }
            internal static Context Begin(Scene scene)
            {
                var c=Shared;if(c._busy)throw new InvalidOperationException("Nested world capture is not supported.");
                if(c._scene!=scene){c._paths.Clear();c._keys.Clear();c._scene=scene;ReplayEncoding.Clear();}
                c._components.Clear();c._epoch++;c._busy=true;return c;
            }
            private string Path(Transform t)
            {
                if(t == null) return "";
                if(!_paths.TryGetValue(t,out var entry))_paths.Add(t,entry=new PathEntry());
                if(entry.Seen==_epoch)return entry.Path;
                string parent=Path(t.parent),name=t.name;int sibling=t.GetSiblingIndex();
                if(entry.Path==null || entry.Parent!=parent || entry.Name!=name || entry.Sibling!=sibling)
                {entry.Parent=parent;entry.Name=name;entry.Sibling=sibling;entry.Path=parent+"/"+sibling+":"+name;}
                entry.Seen=_epoch;return entry.Path;
            }
            internal Key Key(Component component)
            {
                if(!_keys.TryGetValue(component,out var entry))_keys.Add(component,entry=new KeyEntry());
                if(entry.Seen==_epoch)return new Key(entry.Key);
                var gameObject=component.gameObject;
                if(!_components.TryGetValue(gameObject,out var components))
                {_components.Add(gameObject,components=component.GetComponents<Component>());}
                string path=Path(component.transform);int index=Array.IndexOf(components,component);
                if(entry.Key==null || entry.Path!=path || entry.Index!=index)
                {entry.Path=path;entry.Index=index;entry.Key=new KeyNode(path+"/"+component.GetType().Name+"#"+index);}
                entry.Seen=_epoch;return new Key(entry.Key);
            }
            public void Dispose()
            {
                _deadKeys.Clear();foreach(var pair in _keys)if(pair.Value.Seen!=_epoch)_deadKeys.Add(pair.Key);foreach(var key in _deadKeys)_keys.Remove(key);
                _deadPaths.Clear();foreach(var pair in _paths)if(pair.Value.Seen!=_epoch)_deadPaths.Add(pair.Key);foreach(var key in _deadPaths)_paths.Remove(key);
                _components.Clear();_busy=false;
            }
        }
        public static WorldWitness Capture()=>Capture(false);
        public static WorldWitness CaptureReplay()=>Capture(true);
        public static void Reset()=>Context.Reset();
        private static WorldWitness Capture(bool compact)
        {
            using var context=Context.Begin(SceneManager.GetActiveScene());
            using var w = new WorldWitnessBuilder(compact,compact,ReplayEncoding);
            var preferences=new WorldWitness();RunPreferences.Capture(preferences);
            foreach(var pair in preferences.Exact)w.Exact(pair.Key,pair.Value);
            foreach(var pair in preferences.Numbers)w.Number(pair.Key,pair.Value);
            Put(w, new Key("currencies"), globalStats.currencyLookup);
            Put(w, new Key("global-upgrades"), globalStats.globalUpgradeDict);
            w.Exact("random",JsonUtility.ToJson(UnityEngine.Random.state));
            if(VmanScript.isCurrentlyVman)
            {w.Exact("vman/time",VmanScript.vmanTimeLeft.ToString("R",CultureInfo.InvariantCulture));w.Exact("vman/score",VmanScript.vmanScore.ToString("R",CultureInfo.InvariantCulture));}
            // One fresh inventory sees newly created/destroyed components and
            // inactive objects. No cross-frame scene topology cache can go stale.
            var scene=SceneManager.GetActiveScene();
            foreach(var component in UnityEngine.Object.FindObjectsByType<Component>(FindObjectsInactive.Include,FindObjectsSortMode.None))
            {
                var provider=Resolve(component.GetType());
                if(provider==null || component.gameObject.scene!=scene)continue;
                var key=context.Key(component);
                if(provider.Kind==1)
                {
                    var body=(Rigidbody2D)component;
                    Put(w, key.Child("position"), body.position); Put(w, key.Child("velocity"), body.linearVelocity);
                    Put(w, key.Child("rotation"), body.rotation); Put(w, key.Child("angularVelocity"), body.angularVelocity);
                    Put(w, key.Child("simulated"), body.simulated); Put(w, key.Child("bodyType"), body.bodyType);
                    Put(w, key.Child("active"), body.gameObject.activeInHierarchy);
                }
                else if(provider.Kind==2)
                {
                    var collider=(Collider2D)component;
                    Put(w, key.Child("enabled"), collider.enabled); Put(w, key.Child("active"), collider.gameObject.activeInHierarchy);
                    Put(w, key.Child("position"), collider.transform.position); Put(w, key.Child("scale"), collider.transform.lossyScale);
                    Put(w, key.Child("rotation"), collider.transform.eulerAngles);
                }
                else
                {
                    Put(w,key.Child("active"),component.gameObject.activeInHierarchy);
                    foreach(var field in provider.Members)Put(w,key.Child(field.Name),field.GetValue(component));
                }
            }
            return w.Build();
        }
        private static void Put(WorldWitnessBuilder w, Key key, object value)
        {
            if (value == null) { w.Exact(key.Text,"null"); return; }
            if (value is Vector2 v2) { Put(w, key.Child("x"), v2.x); Put(w, key.Child("y"), v2.y); return; }
            if (value is Vector3 v3) { Put(w, key.Child("x"), v3.x); Put(w, key.Child("y"), v3.y); Put(w, key.Child("z"), v3.z); return; }
            if (value is IDictionary dict)
            {
                Put(w, key.Child("count"), dict.Count);
                foreach (DictionaryEntry pair in dict) Put(w, key.Dynamic(Convert.ToString(pair.Key, CultureInfo.InvariantCulture)), pair.Value);
                return;
            }
            if (value is IList list)
            {
                Put(w, key.Child("count"), list.Count);
                for (int i = 0; i < list.Count; i++) Put(w, key.Dynamic(i.ToString()), list[i]);
                return;
            }
            if (value is float f) { w.Number(key.Text,f); return; }
            // Money/upgrade values are doubles: preserve and compare them exactly,
            // even when their magnitude exceeds the precision of a float.
            if (value is double d) { w.Exact(key.Text,d.ToString("R", CultureInfo.InvariantCulture)); return; }
            if (value.GetType().IsPrimitive || value.GetType().IsEnum || value is string)
            { w.Exact(key.Text,Convert.ToString(value, CultureInfo.InvariantCulture)); return; }
            throw new NotSupportedException("No checkpoint provider for " + key.Text + " (" + value.GetType().Name + ")");
        }
    }
}
