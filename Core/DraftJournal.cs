using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace Igtap.Tas.Core
{
    // One baseline followed by flushed input deltas. No full-movie rewrite per
    // mouse stroke. An incomplete final line after a crash is never applied.
    public sealed class DraftJournal
    {
        private readonly string _root;
        private string _active;
        private string _recovered;
        private int _sequence;
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings {
            TypeNameHandling = TypeNameHandling.None, MissingMemberHandling = MissingMemberHandling.Error, MaxDepth = 32 };
        private sealed class Entry { public int Sequence; public InputEdit Edit; }
        public DraftJournal(string root) { _root = root; Directory.CreateDirectory(root); }
        private string Latest() => Directory.GetDirectories(_root, "draft-*")
            .Where(d => !File.Exists(Path.Combine(d, "resolved")) && File.Exists(Path.Combine(d, "base.tas.json")))
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).FirstOrDefault();
        public bool Available => Latest() != null;
        public void Append(string name, Movie before, Movie after)
        {
            var edit = InputEdit.Between(before, after);
            if (edit == null) return;
            if (_active == null)
            {
                var directory = Path.Combine(_root, "draft-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff") + "-" + Guid.NewGuid().ToString("N"));
                var baseline = before.CopyForEdit(); baseline.Name = MovieStore.NormalizeName(name);
                new MovieStore(directory).Save("base", baseline);
                _active = directory; _sequence = 0;
            }
            string path = Path.Combine(_active, "edits.jsonl");
            if (File.Exists(path) && new FileInfo(path).Length > 256L * 1024 * 1024)
                throw new IOException("Draft journal is full. Save or discard edits before continuing.");
            using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
            {
                long length = stream.Length;
                try
                {
                    stream.Position = length;
                    byte[] data = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new Entry { Sequence = _sequence + 1, Edit = edit }, Settings) + "\n");
                    stream.Write(data, 0, data.Length); stream.Flush(true); _sequence++;
                }
                catch { stream.SetLength(length); throw; }
            }
        }
        public Movie Recover()
        {
            string directory = Latest() ?? throw new InvalidOperationException("No recoverable draft.");
            var movie = new MovieStore(directory).Load("base");
            string path = Path.Combine(directory, "edits.jsonl");
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > 256L * 1024 * 1024) throw new IOException("Draft journal exceeds the import limit.");
                string text = File.ReadAllText(path); int end = text.LastIndexOf('\n'), sequence = 0;
                if (end >= 0)
                foreach (string line in text.Substring(0, end).Split('\n'))
                {
                    var entry = JsonConvert.DeserializeObject<Entry>(line, Settings);
                    if (entry?.Edit == null || entry.Sequence != ++sequence) throw new InvalidOperationException("Damaged draft journal. The original files were preserved.");
                    movie = entry.Edit.Apply(movie);
                }
            }
            movie.Validate(); _recovered = directory; return movie;
        }
        public void Resolve()
        {
            if (_active != null) File.WriteAllText(Path.Combine(_active, "resolved"), DateTime.UtcNow.ToString("O"));
            if (_recovered != null) File.WriteAllText(Path.Combine(_recovered, "resolved"), DateTime.UtcNow.ToString("O"));
            _active = _recovered = null; _sequence = 0;
        }
        public void ResolveRecovered()
        {
            string directory = Latest();
            if (directory != null) File.WriteAllText(Path.Combine(directory, "resolved"), DateTime.UtcNow.ToString("O"));
        }
    }
}
