using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Igtap.Tas.Runtime
{
    internal static class EnvironmentFingerprint
    {
        public static string Game() => IgtapAdapter.Hash(File.ReadAllBytes(typeof(Movement).Assembly.Location));
        public static string Saves()
        {
            // Includes normal/hard save and backup folders, without reading unrelated user files.
            var root = Application.persistentDataPath;
            var builder = new StringBuilder();
            if (Directory.Exists(root))
            foreach (var directory in Directory.GetDirectories(root, "Savedata*").OrderBy(p => p, StringComparer.Ordinal))
            foreach (var path in Directory.GetFiles(directory, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            {
                builder.Append(path.Substring(root.Length).Replace('\\', '/')).Append(':');
                builder.Append(IgtapAdapter.Hash(File.ReadAllBytes(path))).Append('\n');
            }
            return IgtapAdapter.Hash(Encoding.UTF8.GetBytes(builder.ToString()));
        }
        public static string Mods()
        {
            var root = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Recharge", "Mods");
            var builder = new StringBuilder();
            if (Directory.Exists(root))
            foreach (var directory in Directory.GetDirectories(root).OrderBy(p => p, StringComparer.Ordinal))
            {
                var manifest = Path.Combine(directory, "mod.json");
                if (File.Exists(manifest)) builder.Append(Path.GetFileName(directory)).Append(File.ReadAllText(manifest));
                foreach (var dll in Directory.GetFiles(directory, "*.dll").OrderBy(p => p, StringComparer.Ordinal))
                    builder.Append(Path.GetFileName(dll)).Append(IgtapAdapter.Hash(File.ReadAllBytes(dll)));
            }
            return IgtapAdapter.Hash(Encoding.UTF8.GetBytes(builder.ToString()));
        }
    }
}
