using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Igtap.Tas.Core.Search;
using Igtap.Tas.Core.Simulation;

namespace Igtap.Tas.Core.Validation
{
    public sealed class ValidationFile
    { public long Bytes; public string Sha256=""; }
    public sealed class ValidationManifest
    {
        public int Version=1;
        public string RecorderVersion="1.0.0", Name="", Notes="", StartedUtc="", FinishedUtc="", StopReason="";
        public string GameHash="", UnityVersion="", ModSetHash="", SaveHashBefore="", SaveHashAfter;
        public int Fps, PhysicsHz=50, Frames, Samples, TimingProtocol=FrameTiming.Protocol;
        public long Pus;
        public bool Complete, TruncatedAtLimit;
        public bool? SavesUnchanged;
        public string ReplayVerification="Not run";
        public Dictionary<string,ValidationFile> Files=new Dictionary<string,ValidationFile>(StringComparer.Ordinal);
    }
    public static class ValidationBundle
    {
        // An explicit list prevents raw saves, game binaries, unrelated files or
        // interrupted journals from accidentally entering a shareable bundle.
        public static readonly string[] Required={"recording.tas.json","native.trace.json","world.json","start.json","animation.json","environment.json"};
        public static readonly string[] Optional={"geometry-report.json","contacts.jsonl.gz","contacts-status.json","performance.csv","replay-report.json"};
        public static void Write(string directory,string zipPath,ValidationManifest manifest)
        {
            directory=Path.GetFullPath(directory); zipPath=Path.GetFullPath(zipPath);
            if(manifest==null || manifest.Frames<1)throw new InvalidDataException("Record at least one complete frame before exporting.");
            var files=Required.Concat(Optional.Where(name=>File.Exists(Path.Combine(directory,name)))).ToArray();
            manifest.Files.Clear();
            foreach(string name in files) {
                string file=Path.Combine(directory,name);if(!File.Exists(file))throw new FileNotFoundException("Missing validation data: "+name);
                manifest.Files.Add(name,new ValidationFile {Bytes=new FileInfo(file).Length,Sha256=SimulationTraceComparer.FileHash(file)});
            }
            SearchFiles.Write(Path.Combine(directory,"capture.json"),manifest);
            Directory.CreateDirectory(Path.GetDirectoryName(zipPath)); string temporary=zipPath+".tmp";
            try {
                using(var stream=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None))
                using(var zip=new ZipArchive(stream,ZipArchiveMode.Create))
                    foreach(string name in files.Concat(new[]{"capture.json"})) {
                        var entry=zip.CreateEntry(name,CompressionLevel.Optimal);
                        using(var source=File.OpenRead(Path.Combine(directory,name)))using(var target=entry.Open())source.CopyTo(target);
                    }
                if(File.Exists(zipPath))File.Replace(temporary,zipPath,null);else File.Move(temporary,zipPath);
            } finally {if(File.Exists(temporary))File.Delete(temporary);}
        }
    }
}
