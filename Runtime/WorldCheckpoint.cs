using System;
using System.Collections;
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
        public static WorldWitness Capture()
        {
            var w = new WorldWitness();
            Put(w, "currencies", globalStats.currencyLookup);
            Put(w, "global-upgrades", globalStats.globalUpgradeDict);
            w.Exact["random"] = JsonUtility.ToJson(UnityEngine.Random.state);
            Read<globalStats>(w, "currentA1State", "Zone2toZone1PathOpen", "secretsFound", "visitedTeleporters", "DjBoxesTriggered", "DashBoxesTriggered", "grossAtomCount", "effectiveAtomCount", "vmanScore", "vmanMult");
            Read<ZoneLoader>(w, "activeZone");
            Read<FloatingOrigin>(w, "currentOrigin", "RebaseInProgress", "queueUpdate");
            Read<PlatformMover>(w, "indexInPositions", "linearV", "diff", "nextFramePos", "currentFramePos", "isPhaseChangeInProgess", "onCooldown", "exitedLastFrame", "reset");
            Read<JiggleDropScript>(w, "cooldown", "usable", "refreshDash", "refreshJump");
            Read<courseScript>(w, "courseNumber", "currentPathTime", "bestPathTime", "tracking", "currentPlayerPath", "currentPlayerScales", "reward", "gpReward", "rpReward", "CdReward", "tier", "rewardTier", "exemptFromAtomPrestige", "compBoostTimeLeft", "compBoostBonus");
            Read<localUpgrades>(w, "localUpgradeDict");
            Read<timer>(w, "time");
            // Include inactive bodies and colliders: an inactive object may be
            // enabled later. Stable hierarchy paths replace process-specific IDs.
            foreach (var body in SceneObjects<Rigidbody2D>())
            {
                string key = Key(body);
                Put(w, key + "/position", body.position); Put(w, key + "/velocity", body.linearVelocity);
                Put(w, key + "/rotation", body.rotation); Put(w, key + "/angularVelocity", body.angularVelocity);
                Put(w, key + "/simulated", body.simulated); Put(w, key + "/bodyType", body.bodyType);
                Put(w, key + "/active", body.gameObject.activeInHierarchy);
            }
            foreach (var collider in SceneObjects<Collider2D>())
            {
                string key = Key(collider);
                Put(w, key + "/enabled", collider.enabled); Put(w, key + "/active", collider.gameObject.activeInHierarchy);
                Put(w, key + "/position", collider.transform.position); Put(w, key + "/scale", collider.transform.lossyScale);
                Put(w, key + "/rotation", collider.transform.eulerAngles);
            }
            return w;
        }
        private static T[] SceneObjects<T>() where T : Component => UnityEngine.Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(c => c.gameObject.scene == SceneManager.GetActiveScene()).ToArray();
        private static string Key(Component component)
        {
            var path = "";
            for (var t = component.transform; t != null; t = t.parent)
                path = "/" + t.GetSiblingIndex() + ":" + t.name + path;
            return path + "/" + component.GetType().Name + "#" + Array.IndexOf(component.GetComponents<Component>(), component);
        }
        private static void Read<T>(WorldWitness w, params string[] names) where T : Component
        {
            var fields = names.Select(name => typeof(T).GetField(name, Fields)
                ?? throw new NotSupportedException("Unsupported game build: missing " + typeof(T).Name + "." + name)).ToArray();
            foreach (var component in SceneObjects<T>())
            {
                string key = Key(component);
                Put(w, key + "/active", component.gameObject.activeInHierarchy);
                foreach (var field in fields) Put(w, key + "/" + field.Name, field.GetValue(component));
            }
        }
        private static void Put(WorldWitness w, string key, object value)
        {
            if (value == null) { w.Exact[key] = "null"; return; }
            if (value is Vector2 v2) { Put(w, key + "/x", v2.x); Put(w, key + "/y", v2.y); return; }
            if (value is Vector3 v3) { Put(w, key + "/x", v3.x); Put(w, key + "/y", v3.y); Put(w, key + "/z", v3.z); return; }
            if (value is IDictionary dict)
            {
                Put(w, key + "/count", dict.Count);
                foreach (DictionaryEntry pair in dict) Put(w, key + "/" + Convert.ToString(pair.Key, CultureInfo.InvariantCulture), pair.Value);
                return;
            }
            if (value is IList list)
            {
                Put(w, key + "/count", list.Count);
                for (int i = 0; i < list.Count; i++) Put(w, key + "/" + i, list[i]);
                return;
            }
            if (value is float f) { w.Numbers[key] = f; return; }
            // Money/upgrade values are doubles: preserve and compare them exactly,
            // even when their magnitude exceeds the precision of a float.
            if (value is double d) { w.Exact[key] = d.ToString("R", CultureInfo.InvariantCulture); return; }
            if (value.GetType().IsPrimitive || value.GetType().IsEnum || value is string)
            { w.Exact[key] = Convert.ToString(value, CultureInfo.InvariantCulture); return; }
            throw new NotSupportedException("No checkpoint provider for " + key + " (" + value.GetType().Name + ")");
        }
    }
}
