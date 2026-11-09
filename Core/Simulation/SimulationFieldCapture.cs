using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Collections;

namespace Igtap.Tas.Core.Simulation
{
    // Shared by the native and retargeted hosts; type identity is intentionally
    // irrelevant because Unity types belong to different assemblies there.
    public static class SimulationFieldCapture
    {
        public static object AddMover(SortedDictionary<string, string> into, object mover, int id)
        {
            string key = "mover/" + id.ToString(CultureInfo.InvariantCulture);
            AddScript(into, mover, key);
            object transform = mover.GetType().GetProperty("transform").GetValue(mover);
            object body = mover.GetType().GetField("body", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(mover)
                ?? throw new InvalidOperationException("Moving platform has no physics body.");
            void Vector(object owner, string property, string suffix)
            {
                object value = owner.GetType().GetProperty(property).GetValue(owner);
                AddVector(into, key + "/" + suffix, (float)value.GetType().GetField("x").GetValue(value), (float)value.GetType().GetField("y").GetValue(value));
            }
            Vector(transform, "position", "position"); Vector(transform, "localPosition", "local-position");
            Vector(body, "position", "body"); Vector(body, "linearVelocity", "velocity");
            return body;
        }
        public static void AddOrb(SortedDictionary<string, string> into, object orb, int id)
        {
            string prefix = "orb/" + id.ToString(CultureInfo.InvariantCulture) + "/";
            foreach (string name in new[] { "cooldown", "usable", "refreshDash", "refreshJump", "ActivationSequence" })
            {
                var field = orb.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("Unsupported refill-orb field: " + name);
                object value = field.GetValue(orb);
                into[prefix + name] = value is float f ? SimulationTraceComparer.Float(f) : Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }
        public static void AddPlayer(SortedDictionary<string, string> into, object player) => AddScript(into, player, "movement");
        public static void AddScript(SortedDictionary<string, string> into, object player, string prefix)
        {
            foreach (var f in player.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (f.DeclaringType.Assembly != player.GetType().Assembly) continue;
                var type = f.FieldType; object value = f.GetValue(player); string key = prefix + "/" + f.Name;
                if (type == typeof(float)) into[key] = SimulationTraceComparer.Float((float)value);
                else if (type == typeof(double)) into[key] = SimulationTraceComparer.Double((double)value);
                else if (type.IsEnum || type.IsPrimitive) into[key] = Convert.ToString(value, CultureInfo.InvariantCulture);
                else if (type.FullName == "UnityEngine.Vector2" || type.FullName == "UnityEngine.Vector3")
                    foreach (string axis in type.FullName.EndsWith("Vector3", StringComparison.Ordinal) ? new[] { "x", "y", "z" } : new[] { "x", "y" })
                        into[key + "/" + axis] = SimulationTraceComparer.Float((float)type.GetField(axis).GetValue(value));
            }
        }
        public static void AddVector(SortedDictionary<string, string> into, string key, float x, float y)
        { into[key + "/x"] = SimulationTraceComparer.Float(x); into[key + "/y"] = SimulationTraceComparer.Float(y); }
        public static void AddRandom(SortedDictionary<string, string> into, object state)
        {
            for (int i = 0; i < 4; i++)
            {
                var field = state.GetType().GetField("s" + i, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("Unsupported Unity random-state layout.");
                into["random/s" + i] = Convert.ToString(field.GetValue(state), CultureInfo.InvariantCulture);
            }
        }
        public static void AddEconomy(SortedDictionary<string, string> into, IDictionary currencies, IDictionary upgrades)
        {
            void Capture(string key, IDictionary source)
            {
                if (source == null) { into[key + "/missing"] = "true"; return; }
                foreach (DictionaryEntry entry in source)
                    into[key + "/" + Convert.ToInt64(entry.Key, CultureInfo.InvariantCulture)] = SimulationTraceComparer.Double(Convert.ToDouble(entry.Value, CultureInfo.InvariantCulture));
            }
            Capture("economy/currencies", currencies); Capture("economy/upgrades", upgrades);
        }
    }
}
