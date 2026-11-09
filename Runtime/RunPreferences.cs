using Igtap.Tas.Core;
using UnityEngine;

namespace Igtap.Tas.Runtime
{
    // The atom door is progression stored outside Savedata. Game call sites use
    // this session-local value, so recording cannot persist it to the real save.
    internal static class RunPreferences
    {
        internal const string AtomDoor = "OpenA2Door";
        private static bool _active, _present;
        private static int _value;
        public static void Begin(Movie movie, bool recording)
        {
            if (recording)
            {
                movie.AtomDoorPresent = movie.StartSetup == null && PlayerPrefs.HasKey(AtomDoor);
                movie.AtomDoorValue = movie.AtomDoorPresent ? PlayerPrefs.GetInt(AtomDoor) : 0;
            }
            _present = movie.AtomDoorPresent; _value = movie.AtomDoorValue; _active = true;
        }
        public static void End() { _active = false; }
        public static bool HasKey(string key) => _active && key == AtomDoor ? _present : PlayerPrefs.HasKey(key);
        public static int GetInt(string key) => GetInt(key, 0);
        public static int GetInt(string key, int fallback) => _active && key == AtomDoor ? _present ? _value : fallback : PlayerPrefs.GetInt(key, fallback);
        public static void SetInt(string key, int value)
        { if (_active && key == AtomDoor) { _present = true; _value = value; } else PlayerPrefs.SetInt(key, value); }
        public static void DeleteKey(string key)
        { if (_active && key == AtomDoor) { _present = false; _value = 0; } else PlayerPrefs.DeleteKey(key); }
        public static void Capture(WorldWitness witness)
        {
            witness.Exact["preferences/OpenA2Door/present"] = HasKey(AtomDoor).ToString();
            witness.Exact["preferences/OpenA2Door/value"] = GetInt(AtomDoor).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
