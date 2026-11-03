using System;
using System.Collections.Generic;
using UnityEngine.LowLevel;

namespace Igtap.Tas.Runtime
{
    // Own markers run even when game MonoBehaviours are gated. Removal preserves
    // other mods' PlayerLoop changes rather than restoring a stale whole-loop copy.
    internal sealed class LoopHooks : IDisposable
    {
        private sealed class TasEarly { }
        private sealed class TasEnd { }
        public LoopHooks(Action early, Action end)
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            if (!Append(ref loop, typeof(UnityEngine.PlayerLoop.EarlyUpdate), typeof(TasEarly), early.Invoke) ||
                !Append(ref loop, typeof(UnityEngine.PlayerLoop.PostLateUpdate), typeof(TasEnd), end.Invoke))
                throw new NotSupportedException("Unity PlayerLoop phases were not found.");
            PlayerLoop.SetPlayerLoop(loop);
        }
        private static bool Append(ref PlayerLoopSystem node, Type parent, Type marker, PlayerLoopSystem.UpdateFunction callback)
        {
            if (node.type == parent)
            {
                var children = new List<PlayerLoopSystem>(node.subSystemList ?? Array.Empty<PlayerLoopSystem>());
                children.Add(new PlayerLoopSystem { type = marker, updateDelegate = callback });
                node.subSystemList = children.ToArray(); return true;
            }
            if (node.subSystemList != null)
                for (int i = 0; i < node.subSystemList.Length; i++) if (Append(ref node.subSystemList[i], parent, marker, callback)) return true;
            return false;
        }
        private static void Remove(ref PlayerLoopSystem node)
        {
            if (node.subSystemList == null) return;
            var children = new List<PlayerLoopSystem>();
            foreach (var child in node.subSystemList)
            {
                if (child.type == typeof(TasEarly) || child.type == typeof(TasEnd)) continue;
                var copy = child; Remove(ref copy); children.Add(copy);
            }
            node.subSystemList = children.ToArray();
        }
        public void Dispose() { var loop = PlayerLoop.GetCurrentPlayerLoop(); Remove(ref loop); PlayerLoop.SetPlayerLoop(loop); }
    }
}
