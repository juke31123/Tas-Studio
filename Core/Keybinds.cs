using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace Igtap.Tas.Core
{
    public enum TasShortcut { Editors, Pause, Step, Stop, Savestates, Stats }

    // Platform-independent bindings. Snapshots receive a copy, never the live settings.
    public sealed class Keybinds
    {
        public static readonly string[] Titles = { "Open desktop editors", "Pause / resume", "Step one frame", "Stop + reload", "Open savestates", "Open player stats" };
        public static readonly string[] Allowed = Enumerable.Range(1, 12).Select(n => "F" + n)
            .Concat(Enumerable.Range('A', 26).Select(n => ((char)n).ToString()))
            .Concat(Enumerable.Range(0, 10).Select(n => "Digit" + n)).ToArray();
        private string[] _keys = { "F5", "F6", "F7", "F8", "F9", "F10" };
        public string this[TasShortcut action] => _keys[(int)action];
        public string Label(TasShortcut action) => Display(this[action]);
        public static string Display(string key) => string.IsNullOrEmpty(key) ? "Unbound" : key.StartsWith("Digit", StringComparison.Ordinal) ? key.Substring(5) : key;
        public Keybinds Copy() => new Keybinds { _keys = (string[])_keys.Clone() };
        public void Assign(TasShortcut action, string key)
        {
            if (key == null || (key.Length > 0 && !Allowed.Contains(key))) throw new ArgumentException("Use F1-F12, A-Z or 0-9. Escape cancels; Delete clears.");
            int conflict = key.Length == 0 ? -1 : Array.IndexOf(_keys, key);
            if (conflict >= 0 && conflict != (int)action) throw new ArgumentException(Display(key) + " is already assigned to " + Titles[conflict] + ". Clear that binding first.");
            _keys[(int)action] = key;
        }
        public static int VirtualKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return 0;
            if (key.Length == 1 && key[0] >= 'A' && key[0] <= 'Z') return key[0];
            if (key.StartsWith("Digit", StringComparison.Ordinal) && key.Length == 6) return key[5];
            return key[0] == 'F' && int.TryParse(key.Substring(1), out int f) && f >= 1 && f <= 12 ? 0x6f + f : 0;
        }
        public int MatchVirtualKey(int key, bool modified, bool typing, bool repeat)
        {
            if (key == 0 || modified || typing || repeat) return -1;
            for (int i = 0; i < _keys.Length; i++) if (VirtualKey(_keys[i]) == key) return i;
            return -1;
        }
        public static Keybinds Load(string path)
        {
            if (!File.Exists(path)) return new Keybinds();
            var data = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path),
                new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.None, MaxDepth = 8 });
            if (data == null || data.Count != Titles.Length) throw new InvalidDataException("Invalid keybind settings.");
            var result = new Keybinds { _keys = new string[Titles.Length] };
            for (int i = 0; i < Titles.Length; i++)
            {
                var action = (TasShortcut)i;
                if (!data.TryGetValue(action.ToString(), out var key)) throw new InvalidDataException("Missing shortcut: " + action);
                result.Assign(action, key);
            }
            return result;
        }
        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            var data = new Dictionary<string, string>();
            for (int i = 0; i < Titles.Length; i++) data.Add(((TasShortcut)i).ToString(), _keys[i]);
            string temp = path + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonConvert.SerializeObject(data, Formatting.Indented));
                if (File.Exists(path)) File.Replace(temp, path, path + ".bak"); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
