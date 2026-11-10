using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace Igtap.Tas.Core
{
    public sealed class MovieFile
    {
        public string Name { get; set; }
        public long Bytes { get; set; }
        public DateTime ModifiedUtc { get; set; }
    }
    public sealed class MovieStore
    {
        public string DirectoryPath { get; }
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.None, MaxDepth = 32,
            MissingMemberHandling = MissingMemberHandling.Error,
            FloatParseHandling = FloatParseHandling.Double,DateParseHandling=DateParseHandling.None,
            Converters={new MovieJson()}
        };
        public MovieStore(string directory) { DirectoryPath = Path.GetFullPath(directory); Directory.CreateDirectory(DirectoryPath); }
        public string PathFor(string name)
        {
            name = NormalizeName(name);
            return Path.Combine(DirectoryPath, name + ".tas.json");
        }
        public static string NormalizeName(string name)
        {
            name = name?.Trim();
            if (name != null && name.EndsWith(".tas.json", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 9);
            if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.StartsWith(".", StringComparison.Ordinal) || name.EndsWith(".", StringComparison.Ordinal) || name.Contains(".."))
                throw new ArgumentException("Use a movie name of 1-80 characters; paths are not allowed.");
            foreach (char c in name)
                if (!char.IsLetterOrDigit(c) && c != ' ' && c != '-' && c != '_' && c != '.') throw new ArgumentException("Movie names cannot contain paths or special characters.");
            return name;
        }
        public IReadOnlyList<MovieFile> ListMovies()
        {
            // Listing never deserializes a movie. A large/corrupt file cannot stop
            // the browser from displaying the other files, and backups stay hidden.
            return Directory.GetFiles(DirectoryPath)
                .Where(path => path.EndsWith(".tas.json", StringComparison.OrdinalIgnoreCase))
                .Select(path => new FileInfo(path))
                .Select(file => new MovieFile { Name = file.Name.Substring(0, file.Name.Length - 9), Bytes = file.Length, ModifiedUtc = file.LastWriteTimeUtc })
                .OrderByDescending(file => file.ModifiedUtc).ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        public void Save(string name, Movie movie)
        {
            movie.Validate();
            string path = PathFor(name), temporary = path + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    using(var json=new JsonTextWriter(writer){Formatting=Formatting.Indented,CloseOutput=false})
                    {JsonSerializer.Create(Settings).Serialize(json,movie);json.Flush();}
                    writer.Flush(); stream.Flush(true);
                }
                // Keep the previous successful save if power is lost during replacement.
                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public Movie Load(string name)
        {
            var path = PathFor(name);
            if (new FileInfo(path).Length > 256L * 1024 * 1024) throw new InvalidOperationException("Movie exceeds the 256 MiB import limit.");
            using var stream=File.OpenRead(path);
            using var text=new StreamReader(stream);
            using var json=new JsonTextReader(text);
            var serializer=JsonSerializer.Create(Settings);serializer.CheckAdditionalContent=true;
            var movie=serializer.Deserialize<Movie>(json)??throw new InvalidOperationException("Movie is null.");
            movie.Validate();
            return movie;
        }
        public static Movie Clone(Movie movie)
        {
            var copy = JsonConvert.DeserializeObject<Movie>(JsonConvert.SerializeObject(movie, Settings), Settings);
            for (int i = 0; i < movie.Frames.Count; i++) copy.Frames[i].PreviousFixedTicks = movie.Frames[i].PreviousFixedTicks;
            return copy;
        }
    }
}
