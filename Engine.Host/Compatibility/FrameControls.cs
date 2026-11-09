using System;
using System.Collections.Generic;
using UnityEngine.InputSystem;

namespace IGTAP.EngineSim
{
    // Compiled into the private shim, never into the native mod. The mask refers
    // to logical actions, including the game's distinct secondary bindings.
    public static class StudioFrameControls
    {
        [ThreadStatic] private static Dictionary<InputAction, int> _actions;
        [ThreadStatic] public static int Held, Press, Release;
        [ThreadStatic] public static float Delta, UnscaledDelta, Now;
        public static void Bind(InputAction action, int button)
        {
            if (action == null) throw new InvalidOperationException("A movement InputAction was not reconstructed.");
            (_actions ??= new Dictionary<InputAction, int>())[action] = button;
        }
        public static bool IsHeld(InputAction action) => _actions != null && _actions.TryGetValue(action, out int flag) && (Held & flag) != 0;
        public static bool IsPressed(InputAction action) => _actions != null && _actions.TryGetValue(action, out int flag) && (Press & flag) != 0;
        public static bool IsReleased(InputAction action) => _actions != null && _actions.TryGetValue(action, out int flag) && (Release & flag) != 0;
        public static void Reset() { _actions = new Dictionary<InputAction, int>(); Held = Press = Release = 0; Delta = UnscaledDelta = Now = 0; }
    }
}

namespace UnityEngine
{
    public partial struct Color
    {
        public static Color Lerp(Color from, Color to, float amount)
        {
            amount = Math.Clamp(amount, 0f, 1f);
            return new Color(from.r + (to.r - from.r) * amount, from.g + (to.g - from.g) * amount,
                from.b + (to.b - from.b) * amount, from.a + (to.a - from.a) * amount);
        }
    }
}
