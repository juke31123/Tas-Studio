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
        private sealed class TasInput { }
        private sealed class TasBeforePhysics { }
        private sealed class TasAfterPhysics { }
        public LoopHooks(Action early, Action end, Action input)
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            if (!Append(ref loop, typeof(UnityEngine.PlayerLoop.EarlyUpdate), typeof(TasEarly), early.Invoke) ||
                !Before(ref loop, typeof(UnityEngine.PlayerLoop.Update.ScriptRunBehaviourUpdate), typeof(TasInput), input.Invoke) ||
                !Before(ref loop, typeof(UnityEngine.PlayerLoop.FixedUpdate.Physics2DFixedUpdate), typeof(TasBeforePhysics), SimulationCapture.ObserveBeforePhysics) ||
                !Before(ref loop, typeof(UnityEngine.PlayerLoop.FixedUpdate.Physics2DFixedUpdate), typeof(TasAfterPhysics), AfterPhysics, true) ||
                !Append(ref loop, typeof(UnityEngine.PlayerLoop.PostLateUpdate), typeof(TasEnd), end.Invoke))
                throw new NotSupportedException("Unity PlayerLoop phases were not found.");
            PlayerLoop.SetPlayerLoop(loop);
        }
        private static void AfterPhysics()
        { SimulationCapture.ObserveAfterPhysics(); GamePatches.Session?.NotifyPhysicsCompleted(); }
        private static bool Before(ref PlayerLoopSystem node, Type target, Type marker, PlayerLoopSystem.UpdateFunction callback, bool after = false)
        {
            if (node.subSystemList == null) return false;
            for (int i = 0; i < node.subSystemList.Length; i++)
            {
                if (node.subSystemList[i].type == target)
                {
                    var children = new List<PlayerLoopSystem>(node.subSystemList);
                    children.Insert(i + (after ? 1 : 0), new PlayerLoopSystem { type = marker, updateDelegate = callback });
                    node.subSystemList = children.ToArray(); return true;
                }
                if (Before(ref node.subSystemList[i], target, marker, callback, after)) return true;
            }
            return false;
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
                if (child.type == typeof(TasEarly) || child.type == typeof(TasEnd) || child.type == typeof(TasInput) ||
                    child.type == typeof(TasBeforePhysics) || child.type == typeof(TasAfterPhysics)) continue;
                var copy = child; Remove(ref copy); children.Add(copy);
            }
            node.subSystemList = children.ToArray();
        }
        public void Dispose() { var loop = PlayerLoop.GetCurrentPlayerLoop(); Remove(ref loop); PlayerLoop.SetPlayerLoop(loop); SimulationCapture.ResetHistory(); }
    }
}
