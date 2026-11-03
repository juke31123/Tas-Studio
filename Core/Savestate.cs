using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace Igtap.Tas.Core
{
    // A reconstruction checkpoint, not a dump of Unity's native memory. The movie
    // rebuilds hidden engine state; the witness rejects a divergent reconstruction.
    public sealed class WorldWitness
    {
        public SortedDictionary<string, string> Exact = new SortedDictionary<string, string>(StringComparer.Ordinal);
        public SortedDictionary<string, double> Numbers = new SortedDictionary<string, double>(StringComparer.Ordinal);

        public void Validate()
        {
            if (Exact == null || Numbers == null || Exact.Count + Numbers.Count == 0)
                throw new InvalidOperationException("Savestate has no world verification data.");
            foreach (var pair in Numbers)
                if (double.IsNaN(pair.Value) || double.IsInfinity(pair.Value))
                    throw new InvalidOperationException("Non-finite savestate value: " + pair.Key);
        }
        public string Difference(WorldWitness actual)
        {
            if (actual == null) return "World verification data missing";
            foreach (var pair in Exact)
                if (!actual.Exact.TryGetValue(pair.Key, out var value) || value != pair.Value)
                    return pair.Key + " differs (expected " + pair.Value + ", actual " + (value ?? "missing") + ")";
            foreach (var pair in Numbers)
                if (!actual.Numbers.TryGetValue(pair.Key, out var value) || double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(pair.Value - value) > 0.0001)
                    return pair.Key + " differs";
            return Exact.Count != actual.Exact.Count || Numbers.Count != actual.Numbers.Count ? "World object/field count differs" : null;
        }
    }

    public sealed class Savestate
    {
        public int FormatVersion = 1;
        public string Method = "replay-checkpoint-v1";
        public string TasName;
        public string Label;
        public string CreatedUtc = DateTime.UtcNow.ToString("O");
        public int Frame;
        public Movie Movie;
        public WorldWitness World;
        public void Validate()
        {
            if (FormatVersion != 1 || Method != "replay-checkpoint-v1") throw new InvalidOperationException("Unsupported savestate format.");
            MovieStore.NormalizeName(TasName); MovieStore.NormalizeName(Label);
            if (Movie == null || World == null) throw new InvalidOperationException("Incomplete savestate.");
            Movie.Validate(); World.Validate();
            if (Frame < 0 || Frame != Movie.Frames.Count || Movie.StartState == null || Movie.WarmupFixedTicks.Count != Movie.WarmupFrames)
                throw new InvalidOperationException("Savestate must contain a complete recording up to its saved frame.");
            if (Movie.Frames.Exists(f => f.Expected == null || f.FixedTicks < 0))
                throw new InvalidOperationException("Savestates require recorded verification data for every frame.");
        }
    }

    public sealed class SavestateStore
    {
        private const string Extension = ".state.json";
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings {
            TypeNameHandling = TypeNameHandling.None, MissingMemberHandling = MissingMemberHandling.Error, MaxDepth = 32
        };
        public string DirectoryPath { get; }
        public SavestateStore(string directory) { DirectoryPath = Path.GetFullPath(directory); Directory.CreateDirectory(DirectoryPath); }
        public static string FileName(string tasName, string label) => MovieStore.NormalizeName(tasName) + "--" + MovieStore.NormalizeName(label);
        public string PathFor(string fileName)
        {
            // Each of the two user-controlled parts is validated separately so
            // two legal 80-character names still fit in one filename.
            if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 162 || fileName.StartsWith(".") || fileName.EndsWith(".") || fileName.Contains(".."))
                throw new ArgumentException("Invalid savestate filename.");
            foreach (char c in fileName)
                if (!char.IsLetterOrDigit(c) && c != ' ' && c != '-' && c != '_' && c != '.') throw new ArgumentException("Paths are not allowed in savestate names.");
            return Path.Combine(DirectoryPath, fileName + Extension);
        }
        public IReadOnlyList<MovieFile> List()
        {
            return Directory.GetFiles(DirectoryPath, "*" + Extension).Select(p => new FileInfo(p))
                .Select(f => new MovieFile { Name = f.Name.Substring(0, f.Name.Length - Extension.Length), Bytes = f.Length, ModifiedUtc = f.LastWriteTimeUtc })
                .OrderByDescending(f => f.ModifiedUtc).ThenBy(f => f.Name, StringComparer.Ordinal).ToArray();
        }
        public string Save(Savestate state)
        {
            state.Validate();
            string name = FileName(state.TasName, state.Label), path = PathFor(name), temp = path + ".tmp";
            // Named checkpoints are immutable. A typo must not destroy a good state.
            if (File.Exists(path)) throw new IOException("That savestate already exists. Choose a new state name.");
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(JsonConvert.SerializeObject(state, Formatting.Indented, Settings)); writer.Flush(); stream.Flush(true);
                    if (stream.Length > 256L * 1024 * 1024) throw new InvalidOperationException("Savestate exceeds the 256 MiB file limit.");
                }
                File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return name;
        }
        public Savestate Load(string name)
        {
            string path = PathFor(name);
            if (new FileInfo(path).Length > 256L * 1024 * 1024) throw new InvalidOperationException("Savestate exceeds the 256 MiB import limit.");
            var state = JsonConvert.DeserializeObject<Savestate>(File.ReadAllText(path), Settings) ?? throw new InvalidOperationException("Savestate is null.");
            state.Validate(); return state;
        }
    }
}
