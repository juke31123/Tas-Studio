using System;
using System.IO;
using System.Linq;

namespace Igtap.Tas.Core.Search
{
    // Separate from TAS transport bindings so arrows can remain the defaults.
    public sealed class GoalCameraBindings
    {
        public string Up = "UpArrow", Down = "DownArrow", Left = "LeftArrow", Right = "RightArrow";
        public static string[] Allowed => Keybinds.Allowed.Concat(new[] { "UpArrow", "DownArrow", "LeftArrow", "RightArrow" }).ToArray();
        public GoalCameraBindings Copy() => (GoalCameraBindings)MemberwiseClone();
        public string[] Keys => new[] { Up, Down, Left, Right };
        public void Validate()
        {
            var keys = Keys;
            if (keys.Any(k => !Allowed.Contains(k)) || keys.Distinct(StringComparer.Ordinal).Count() != keys.Length)
                throw new ArgumentException("Use four different arrow, letter, digit or function keys for the goal camera.");
        }
        public void Assign(int direction, string key)
        {
            var candidate = Copy();
            switch (direction) { case 0: candidate.Up = key; break; case 1: candidate.Down = key; break; case 2: candidate.Left = key; break; case 3: candidate.Right = key; break; default: throw new ArgumentOutOfRangeException(nameof(direction)); }
            candidate.Validate(); Up = candidate.Up; Down = candidate.Down; Left = candidate.Left; Right = candidate.Right;
        }
        public static GoalCameraBindings Load(string path)
        {
            var result = File.Exists(path) ? SearchFiles.Read<GoalCameraBindings>(path) : new GoalCameraBindings();
            if (result == null) throw new InvalidDataException("Camera bindings are missing.");
            result.Validate(); return result;
        }
        public void Save(string path) { Validate(); SearchFiles.Write(path, this); }
        public static int VirtualKey(string name) => name == "UpArrow" ? 38 : name == "DownArrow" ? 40 : name == "LeftArrow" ? 37 : name == "RightArrow" ? 39 : Keybinds.VirtualKey(name);
        public static string FromVirtualKey(int key) => Allowed.FirstOrDefault(name => VirtualKey(name) == key);
    }
}
