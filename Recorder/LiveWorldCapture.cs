using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Igtap.Tas.Core.Search;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object=UnityEngine.Object;
namespace Igtap.Tas.Recorder
{
    // Read native state at an end-of-frame boundary. No SyncTransforms, private
    // physics simulation, collider enabling, Animator previews or live writes.
    internal static class LiveWorldCapture
    {
        private const BindingFlags Fields=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly;
        private static JArray ReadFields(object target)
        {var result=new JArray();for(var t=target.GetType();t!=null && t!=typeof(MonoBehaviour) && t!=typeof(Object);t=t.BaseType)foreach(var f in t.GetFields(Fields))if(!typeof(Delegate).IsAssignableFrom(f.FieldType))result.Add(new JArray(t.FullName,f.Name,Value(f.GetValue(target))));return result;}
        private static JToken Value(object value,int depth=0)
        {
            if(value==null || depth>10 || value is Delegate)return JValue.CreateNull();var type=value.GetType();
            if(value is Object o)return o==null ? JValue.CreateNull() : new JObject {["$ref"]=o.GetInstanceID()};
            if(type.IsEnum)return new JValue(Convert.ToInt64(value,CultureInfo.InvariantCulture));if(value is LayerMask mask)return new JValue(mask.value);
            if(type.IsPrimitive || value is string || value is decimal)return new JValue(value);
            if(value is IDictionary dict){var list=new JArray();foreach(DictionaryEntry e in dict)list.Add(new JArray(Value(e.Key,depth+1),Value(e.Value,depth+1)));return new JObject {["$dict"]=list};}
            if(value is IEnumerable items){var list=new JArray();foreach(var item in items){if(list.Count>=50000)break;list.Add(Value(item,depth+1));}return list;}
            if(type.Assembly.GetName().Name.StartsWith("Unity",StringComparison.Ordinal) && !type.IsValueType)return JValue.CreateNull();
            if(type.Namespace=="UnityEngine"){var simple=new JObject();foreach(var f in type.GetFields(BindingFlags.Instance|BindingFlags.Public))simple[f.Name]=Value(f.GetValue(value),depth+1);return simple;}
            if(type.Assembly!=typeof(Movement).Assembly)return new JObject {["$type"]=type.FullName,["opaque"]=true};
            var fields=new JObject();for(var t=type;t!=null && t!=typeof(object);t=t.BaseType)foreach(var f in t.GetFields(Fields))fields[t.Name+"::"+f.Name]=Value(f.GetValue(value),depth+1);return fields;
        }
        private static JObject Properties(object owner,Type type)
        {
            var result=new JObject();foreach(var p in type.GetProperties(BindingFlags.Public|(owner==null ? BindingFlags.Static : BindingFlags.Instance))){var t=p.PropertyType;
                if(!p.CanRead || p.GetIndexParameters().Length!=0 || p.IsDefined(typeof(ObsoleteAttribute),true) || !(t.IsPrimitive || t.IsEnum || t==typeof(string) || t==typeof(Vector2) || t==typeof(Vector3) || t==typeof(Quaternion) || t==typeof(LayerMask) || typeof(Object).IsAssignableFrom(t)))continue;
                try{result[p.Name]=Value(p.GetValue(owner));}catch(Exception e){result[p.Name]="Unavailable: "+e.GetType().Name;}}
            return result;
        }
        private static JObject Shapes(Collider2D c)
        {
            var group=new PhysicsShapeGroup2D();int count=c.GetShapes(group);var list=new JArray();
            for(int i=0;i<count;i++){var s=group.GetShape(i);var points=new System.Collections.Generic.List<Vector2>();group.GetShapeVertices(i,points);list.Add(new JObject {["type"]=s.shapeType.ToString(),["radius"]=s.radius,["vertices"]=new JArray(points.SelectMany(p=>new[]{p.x,p.y})),["useAdjacentStart"]=s.useAdjacentStart,["useAdjacentEnd"]=s.useAdjacentEnd,["adjacentStart"]=Value(s.adjacentStart),["adjacentEnd"]=Value(s.adjacentEnd)});}
            return new JObject {["list"]=list,["inactiveGeometryUnavailable"]=count==0 && !c.isActiveAndEnabled};
        }
        public static void Export(Movement player,string directory)
        {
            var body=player.GetComponent<Rigidbody2D>();var position=body.position;var velocity=body.linearVelocity;float scale=Time.timeScale,fixedStep=Time.fixedDeltaTime,capture=Time.captureDeltaTime;int target=Application.targetFrameRate,vsync=QualitySettings.vSyncCount;
            string random=JsonUtility.ToJson(UnityEngine.Random.state);
            var objects=Resources.FindObjectsOfTypeAll<GameObject>().Where(go=>go!=null && go.scene.IsValid() && (go.hideFlags&HideFlags.HideAndDontSave)==0).ToArray();
            var nodes=new JArray();
            foreach(var go in objects){var t=go.transform;var components=new JArray();nodes.Add(new JObject {["id"]=go.GetInstanceID(),["name"]=go.name,["scene"]=go.scene.name,["transformId"]=t.GetInstanceID(),["parent"]=t.parent==null?0:t.parent.gameObject.GetInstanceID(),["siblingIndex"]=t.GetSiblingIndex(),["layer"]=go.layer,["tag"]=go.tag,["activeSelf"]=go.activeSelf,["activeInHierarchy"]=go.activeInHierarchy,["position"]=Value(t.position),["rotation"]=Value(t.rotation),["localPosition"]=Value(t.localPosition),["localRotation"]=Value(t.localRotation),["localScale"]=Value(t.localScale),["lossyScale"]=Value(t.lossyScale),["components"]=components});
                foreach(var c in go.GetComponents<Component>()){if(c==null || c is Transform)continue;var type=c.GetType();var component=new JObject {["id"]=c.GetInstanceID(),["type"]=type.FullName,["assembly"]=type.Assembly.GetName().Name};components.Add(component);
                    if(c is Behaviour b)component["enabled"]=b.enabled;if(c is MonoBehaviour && type.Assembly==typeof(Movement).Assembly)component["fields"]=ReadFields(c);
                    if(c is Rigidbody2D || c is Collider2D)component["props"]=Properties(c,type);if(c is Rigidbody2D rb)component["sleeping"]=rb.IsSleeping();
                    if(c is Collider2D collider)try{component["shapes"]=Shapes(collider);}catch(Exception e){component["geometryError"]=e.Message;}
                }
            }
            var statics=new JArray();foreach(var type in objects.SelectMany(go=>go.GetComponents<MonoBehaviour>()).Where(c=>c!=null && c.GetType().Assembly==typeof(Movement).Assembly).Select(c=>c.GetType()).Distinct())
                foreach(var f in type.GetFields(BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly))if(!f.IsLiteral && !typeof(Delegate).IsAssignableFrom(f.FieldType))statics.Add(new JArray(type.FullName,f.Name,Value(f.GetValue(null))));
            SearchFiles.Write(System.IO.Path.Combine(directory,"world.json"),new JObject {["schema"]=2,["mode"]="passive-gameplay",["activeScene"]=SceneManager.GetActiveScene().name,["objects"]=nodes,["statics"]=statics,["physics2d"]=Properties(null,typeof(Physics2D)),["layerNames"]=new JArray(Enumerable.Range(0,32).Select(LayerMask.LayerToName)),["layerCollisionMasks"]=new JArray(Enumerable.Range(0,32).Select(Physics2D.GetLayerCollisionMask))});
            SearchFiles.Write(System.IO.Path.Combine(directory,"starting-state.json"),new JObject {["engineFrame"]=Time.frameCount,["time"]=Time.timeAsDouble,["fixedTime"]=Time.fixedTimeAsDouble,["fixedDelta"]=fixedStep,["timeScale"]=scale,["captureDelta"]=capture,["targetFps"]=target,["vSync"]=vsync,["playerId"]=player.GetInstanceID(),["movement"]=ReadFields(player),["body"]=Properties(body,body.GetType()),["random"]=JObject.Parse(random),
                ["pendingInvokes"]=new JArray(new[]{"cancelDashBuffer","cancelJumpBuffer","cancelJumpCutBuffer","endRecentJump","reEnableNormalCollider","deathRespawn","cancelSpringCutscene"}.Select(n=>new JObject {["method"]=n,["pending"]=player.IsInvoking(n)})),["scope"]="Observed current state. Sleep elapsed time, native contact caches and remaining coroutine/Invoke waits are not fully exported."});
            var animations=new JArray();foreach(var animator in player.GetComponentsInChildren<Animator>(true)){var state=animator.GetCurrentAnimatorStateInfo(0);animations.Add(new JObject {["id"]=animator.GetInstanceID(),["stateHash"]=state.fullPathHash,["normalizedTime"]=state.normalizedTime,["speed"]=animator.speed,["transition"]=animator.IsInTransition(0),["clips"]=new JArray((animator.runtimeAnimatorController?.animationClips??Array.Empty<AnimationClip>()).Distinct().Select(clip=>new JObject {["id"]=clip.GetInstanceID(),["name"]=clip.name,["length"]=clip.length,["frameRate"]=clip.frameRate,["events"]=new JArray(clip.events.Select(e=>new JObject {["time"]=e.time,["function"]=e.functionName,["string"]=e.stringParameter,["float"]=e.floatParameter,["int"]=e.intParameter}))}))});}
            SearchFiles.Write(System.IO.Path.Combine(directory,"animation.json"),animations);
            if(body.position!=position || body.linearVelocity!=velocity || Time.timeScale!=scale || Time.fixedDeltaTime!=fixedStep || Time.captureDeltaTime!=capture || Application.targetFrameRate!=target || QualitySettings.vSyncCount!=vsync || JsonUtility.ToJson(UnityEngine.Random.state)!=random)throw new InvalidOperationException("Read-only snapshot changed live state.");
        }
    }
}
