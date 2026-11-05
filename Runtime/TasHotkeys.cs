using System;
using System.Collections.Generic;
using Igtap.Tas.Core;
using UnityEngine.InputSystem;

namespace Igtap.Tas.Runtime
{
    internal static class TasHotkeys
    {
        private static readonly Dictionary<string, Key> Keys = MakeKeys();
        private static Dictionary<string, Key> MakeKeys()
        {
            var keys = new Dictionary<string, Key>();
            foreach (string name in Keybinds.Allowed) keys.Add(name, (Key)Enum.Parse(typeof(Key), name));
            return keys;
        }
        internal static bool Modified(Keyboard keyboard) => keyboard.shiftKey.isPressed || keyboard.ctrlKey.isPressed || keyboard.altKey.isPressed || keyboard.leftMetaKey.isPressed || keyboard.rightMetaKey.isPressed;
        internal static bool Pressed(Keybinds bindings, TasShortcut action)
        {
            var keyboard = Keyboard.current;
            return keyboard != null && !Modified(keyboard) && Keys.TryGetValue(bindings[action], out var key) && keyboard[key].wasPressedThisFrame;
        }
    }
}
