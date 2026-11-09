using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Igtap.Tas.Runtime
{
    // Inspect assets using a private Animator with events/root motion disabled.
    // The live player's controller and pose are never changed.
    internal static class SimulationAnimationCapture
    {
        private const BindingFlags PublicInstance = BindingFlags.Instance | BindingFlags.Public;
        private static object Get(object target, string name) => target.GetType().GetProperty(name, PublicInstance)?.GetValue(target);
        private static void Set(object target, string name, object value)
        {
            var property = target.GetType().GetProperty(name, PublicInstance) ?? throw new MissingMemberException(target.GetType().Name, name);
            property.SetValue(target, property.PropertyType.IsEnum ? Enum.ToObject(property.PropertyType, value) : value);
        }
        private static object Call(object target, string method, Type[] types, params object[] values) =>
            (target.GetType().GetMethod(method, PublicInstance, null, types, null) ?? throw new MissingMethodException(target.GetType().Name, method)).Invoke(target, values);
        private static JArray ClipInfo(object animator, string method)
        {
            var result = new JArray();
            foreach (var entry in (IEnumerable)Call(animator, method, new[] {typeof(int)}, 0))
            {
                var clip = (Object)Get(entry, "clip");
                result.Add(new JObject { ["id"] = clip.GetInstanceID(), ["name"] = clip.name, ["weight"] = Convert.ToSingle(Get(entry, "weight")) });
            }
            return result;
        }
        public static JObject State(object animator)
        {
            object info = Call(animator, "GetCurrentAnimatorStateInfo", new[] {typeof(int)}, 0);
            return new JObject { ["parameter"] = Convert.ToInt32(Call(animator, "GetInteger", new[] {typeof(string)}, "Animation")),
                ["speed"] = Convert.ToSingle(Get(animator, "speed")), ["stateHash"] = Convert.ToInt32(Get(info, "fullPathHash")),
                ["normalizedTime"] = Convert.ToSingle(Get(info, "normalizedTime")), ["length"] = Convert.ToSingle(Get(info, "length")),
                ["stateSpeed"] = Convert.ToSingle(Get(info, "speed")), ["speedMultiplier"] = Convert.ToSingle(Get(info, "speedMultiplier")),
                ["loop"] = Convert.ToBoolean(Get(info, "loop")),
                ["transition"] = Convert.ToBoolean(Call(animator, "IsInTransition", new[] {typeof(int)}, 0)),
                ["clips"] = ClipInfo(animator, "GetCurrentAnimatorClipInfo") };
        }
        public static JObject Capture(Movement player)
        {
            object animator = typeof(Movement).GetField("animator").GetValue(player);
            if (animator == null) throw new NotSupportedException("Player Animator is unavailable.");
            var controller = (Object)Get(animator, "runtimeAnimatorController");
            if (controller == null) throw new NotSupportedException("Player animation controller is unavailable.");
            var result = new JObject { ["version"] = 1, ["controller"] = controller.name,
                ["layerCount"] = Convert.ToInt32(Get(animator, "layerCount")),
                ["enabled"] = Convert.ToBoolean(Get(animator, "enabled")), ["fireEvents"] = Convert.ToBoolean(Get(animator, "fireEvents")),
                ["updateMode"] = Convert.ToInt32(Get(animator, "updateMode")), ["cullingMode"] = Convert.ToInt32(Get(animator, "cullingMode")),
                ["initial"] = State(animator) };
            var parameters = new JArray(); result["parameters"] = parameters;
            foreach (var parameter in (IEnumerable)Get(animator, "parameters"))
                parameters.Add(new JObject { ["name"] = (string)Get(parameter, "name"), ["type"] = Convert.ToInt32(Get(parameter, "type")) });
            var clips = new JArray(); result["clips"] = clips;
            foreach (var clip in (IEnumerable)Get(controller, "animationClips"))
            {
                var asset = (Object)clip;
                var events = new JArray();
                foreach (var item in (IEnumerable)Get(clip, "events"))
                    events.Add(new JObject { ["time"] = Convert.ToSingle(Get(item, "time")), ["function"] = (string)Get(item, "functionName"),
                        ["float"] = Convert.ToSingle(Get(item, "floatParameter")), ["int"] = Convert.ToInt32(Get(item, "intParameter")),
                        ["string"] = (string)Get(item, "stringParameter"), ["hasObject"] = Get(item, "objectReferenceParameter") is Object obj && obj != null });
                clips.Add(new JObject { ["id"] = asset.GetInstanceID(), ["name"] = asset.name, ["length"] = Convert.ToSingle(Get(clip, "length")),
                    ["frameRate"] = Convert.ToSingle(Get(clip, "frameRate")), ["loop"] = Convert.ToBoolean(Get(clip, "isLooping")), ["events"] = events });
            }
            var behaviourType = animator.GetType().Assembly.GetType("UnityEngine.StateMachineBehaviour");
            var getBehaviours = animator.GetType().GetMethods().Single(m => m.Name == "GetBehaviours" && m.IsGenericMethodDefinition);
            if (((Array)getBehaviours.MakeGenericMethod(behaviourType).Invoke(animator, null)).Length != 0)
                throw new NotSupportedException("Player controller has StateMachineBehaviours; isolated probing needs explicit support.");
            string random = JsonUtility.ToJson(UnityEngine.Random.state);
            var probe = new GameObject("Tas Studio animation asset inspector") {hideFlags = HideFlags.HideAndDontSave};
            try
            {
                var copy = probe.AddComponent(animator.GetType());
                Set(copy, "fireEvents", false); Set(copy, "applyRootMotion", false); Set(copy, "cullingMode", 0);
                Set(copy, "runtimeAnimatorController", controller); Set(copy, "speed", 1f);
                var states = new JArray(); result["states"] = states;
                for (int parameter = 0; parameter <= 7; parameter++)
                {
                    Call(copy, "Rebind", Type.EmptyTypes);
                    Call(copy, "SetInteger", new[] {typeof(string), typeof(int)}, "Animation", parameter);
                    Call(copy, "Update", new[] {typeof(float)}, 1f);
                    var state = State(copy); state["parameter"] = parameter; states.Add(state);
                }
                var transitions = new JArray(); result["transitions"] = transitions;
                foreach (JObject from in states)
                foreach (JObject to in states)
                {
                    Call(copy, "Rebind", Type.EmptyTypes);
                    Call(copy, "SetInteger", new[] {typeof(string), typeof(int)}, "Animation", (int)from["parameter"]);
                    Call(copy, "Play", new[] {typeof(int), typeof(int), typeof(float)}, (int)from["stateHash"], 0, .25f);
                    Call(copy, "Update", new[] {typeof(float)}, 0f);
                    Call(copy, "SetInteger", new[] {typeof(string), typeof(int)}, "Animation", (int)to["parameter"]);
                    Call(copy, "Update", new[] {typeof(float)}, 0f);
                    var state = State(copy); state["from"] = (int)from["parameter"]; state["to"] = (int)to["parameter"];
                    if ((bool)state["transition"])
                    {
                        object transition = Call(copy, "GetAnimatorTransitionInfo", new[] {typeof(int)}, 0);
                        state["duration"] = Convert.ToSingle(Get(transition, "duration"));
                        state["durationUnit"] = Convert.ToInt32(Get(transition, "durationUnit"));
                    }
                    Call(copy, "SetInteger", new[] {typeof(string), typeof(int)}, "Animation", (int)from["parameter"]);
                    Call(copy, "Play", new[] {typeof(int), typeof(int), typeof(float)}, (int)from["stateHash"], 0, 1.25f);
                    Call(copy, "Update", new[] {typeof(float)}, 0f);
                    Call(copy, "SetInteger", new[] {typeof(string), typeof(int)}, "Animation", (int)to["parameter"]);
                    Call(copy, "Update", new[] {typeof(float)}, 0f);
                    var later = State(copy); state["lateStateHash"] = (int)later["stateHash"]; state["lateTransition"] = (bool)later["transition"];
                    transitions.Add(state);
                }
            }
            finally { Object.DestroyImmediate(probe); }
            if (random != JsonUtility.ToJson(UnityEngine.Random.state)) throw new InvalidOperationException("Animation asset inspection changed the live RNG state.");
            return result;
        }
    }
}
