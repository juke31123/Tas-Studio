using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Igtap.Tas.Runtime
{
    // Observe the original iterator; never advance it or replace its yield.
    // Only the known fixed-phase platform-reset continuation is importable.
    internal static class SimulationCoroutineCapture
    {
        private static readonly Dictionary<IEnumerator, PlatformMover> Movers = new Dictionary<IEnumerator, PlatformMover>();
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        public static void Clear() => Movers.Clear();
        public static void Observe(IEnumerator routine, bool running)
        {
            if (!running) { Movers.Remove(routine); return; }
            if (!SimulationCapture.CollectHistory) return;
            PlatformMover Find(object value, int depth)
            {
                if (value is PlatformMover mover) return mover;
                if (value == null || depth > 3 || value is UnityEngine.Object) return null;
                var type = value.GetType();
                if (type.Assembly != typeof(PlatformMover).Assembly || type.IsPrimitive || type.IsValueType) return null;
                foreach (var field in type.GetFields(Fields))
                {
                    if (field.Name == "<>2__current") continue;
                    var found = Find(field.GetValue(value), depth + 1); if (found != null) return found;
                }
                return null;
            }
            var target = Find(routine, 0);
            if (target != null) Movers[routine] = target;
        }
        public static JArray Export(IEnumerable<PlatformMover> owners, Func<object, JToken> value)
        {
            var result = new JArray();
            var tracked = new HashSet<PlatformMover>();
            foreach (var entry in Movers.ToArray())
            {
                if (entry.Value == null) { Movers.Remove(entry.Key); continue; }
                if (!entry.Value.gameObject.activeInHierarchy) continue;
                string type = entry.Key.GetType().FullName;
                if (!type.Contains("JumpToState") || !(entry.Key.Current is WaitForFixedUpdate))
                    throw new NotSupportedException("Native platform continuation needs an importer: " + type);
                result.Add(new JObject { ["target"] = entry.Value.GetInstanceID(), ["type"] = type, ["fields"] = value(entry.Key) });
                tracked.Add(entry.Value);
            }
            foreach (var owner in owners.Where(o => o != null && o.gameObject.activeInHierarchy))
            {
                bool Flag(string name) => (bool)typeof(PlatformMover).GetField(name, Fields).GetValue(owner);
                if (Flag("isPhaseChangeInProgess") || Flag("onCooldown") || Flag("reset") && !tracked.Contains(owner))
                    throw new NotSupportedException("Native platform wait was not captured: " + owner.name);
            }
            return result;
        }
    }
}
