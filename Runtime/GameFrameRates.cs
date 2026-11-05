using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;

namespace Igtap.Tas.Runtime
{
    // PlayerPrefs FPS stores an option index, not a rate. Read the shipped
    // settings dropdown, including inactive menu panels, instead of guessing.
    internal static class GameFrameRates
    {
        private static readonly FieldInfo FpsField = typeof(SettingsScript).GetField("FPS", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static int[] _cached = Array.Empty<int>();
        public static int[] Available()
        {
            foreach (var settings in UnityEngine.Object.FindObjectsByType<SettingsScript>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!(FpsField?.GetValue(settings) is TMP_Dropdown dropdown)) continue;
                var rates = dropdown.options.Select(o => int.TryParse(o.text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int fps) ? fps : 0)
                    .Where(fps => fps > 0 && fps <= 10000).Distinct().ToArray();
                if (rates.Length > 0) { _cached = rates; break; }
            }
            return (int[])_cached.Clone();
        }
        public static void RequireAvailable(int fps)
        {
            if (!Available().Contains(fps)) throw new InvalidOperationException("Choose an FPS from the game's frame-rate options in Create New Tas.");
        }
    }
}
