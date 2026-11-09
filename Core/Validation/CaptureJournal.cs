using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Igtap.Tas.Core.Search;
using Igtap.Tas.Core.Simulation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Igtap.Tas.Core.Validation
{
    // Only a single frame's samples are retained by the caller. The durable
    // journals also remain available if capture/export is interrupted.
    public sealed class CaptureJournal : IDisposable
    {
        public const long DefaultByteLimit = 384L * 1024 * 1024;
        private readonly string _directory;
        private readonly SimulationTrace _header;
        private readonly StreamWriter _samples, _frames;
        private readonly long _limit;
        private bool _closed;
        public int Frames { get; private set; }
        public int Samples { get; private set; }
        public long Pus { get; private set; }
        public long RawBytes { get; private set; }
        public CaptureJournal(string directory, SimulationTrace header, long limit = DefaultByteLimit)
        {
            if(header.StartFrame!=0 || header.Inputs.Count!=0 || header.Samples.Count!=1 || header.Samples[0].Phase!="start")
                throw new InvalidDataException("Validation requires the original frozen TAS bootstrap.");
            header.Validate(); if(limit<1)throw new ArgumentOutOfRangeException(nameof(limit));
            _directory=Path.GetFullPath(directory); Directory.CreateDirectory(_directory); _header=header; _limit=limit;
            _samples=new StreamWriter(new GZipStream(new FileStream(Path.Combine(_directory,"samples.jsonl.gz"),FileMode.CreateNew,FileAccess.Write,FileShare.Read),CompressionLevel.Fastest),new UTF8Encoding(false));
            try { _frames=new StreamWriter(new FileStream(Path.Combine(_directory,"frames.jsonl"),FileMode.CreateNew,FileAccess.Write,FileShare.Read),new UTF8Encoding(false)); }
            catch { _samples.Dispose(); throw; }
            _samples.NewLine="\n";_frames.NewLine="\n";
            string start=JsonConvert.SerializeObject(header.Samples[0],SearchFiles.Settings);
            _samples.WriteLine(start); _samples.Flush(); RawBytes=Encoding.UTF8.GetByteCount(start)+1; Samples=1;
        }
        public bool Append(int frame, MovieFrame input, IReadOnlyList<SimulationSample> samples)
        {
            if(_closed)throw new ObjectDisposedException(nameof(CaptureJournal));
            if(frame!=Frames || input==null || samples==null || samples.Count<1 || samples.Count>9 ||
                samples.Any(s=>s==null || s.Frame!=frame) || samples.Last().Phase!="frame-end")
                throw new InvalidDataException("Capture skipped a complete frame boundary.");
            int pu=0; bool waiting=false;
            foreach(var sample in samples.Take(samples.Count-1)) {
                if(!waiting && sample.Phase=="before-pu" && sample.Pu==pu+1) {pu++;waiting=true;}
                else if(waiting && sample.Phase=="after-pu" && sample.Pu==pu) waiting=false;
                else throw new InvalidDataException("Invalid before/after-PU capture sequence.");
            }
            if(waiting || samples.Last().Pu!=pu || input.FixedTicks!=pu)throw new InvalidDataException("Captured PU count differs from the recorded input.");
            var lines=samples.Select(s=>JsonConvert.SerializeObject(s,SearchFiles.Settings)).ToArray();
            long bytes=lines.Sum(s=>(long)Encoding.UTF8.GetByteCount(s)+1);
            if(RawBytes+bytes>_limit)return false;
            foreach(string line in lines)_samples.WriteLine(line);
            _samples.Flush(); // Commit samples before the frame/input journal.
            _frames.WriteLine(JsonConvert.SerializeObject(input,SearchFiles.Settings)); _frames.Flush();
            RawBytes+=bytes; Samples+=lines.Length; Frames++; Pus+=pu; return true;
        }
        public void FinishTrace(Movie movie)
        {
            Dispose();
            if(movie.Frames.Count!=Frames)throw new InvalidDataException("Movie and diagnostic trace have different frame counts.");
            var serializer=JsonSerializer.Create(SearchFiles.Settings);
            var metadata=JObject.FromObject(_header,serializer); metadata.Remove("Inputs");metadata.Remove("Samples");
            string path=Path.Combine(_directory,"native.trace.json"),temporary=path+".tmp";
            try {
                using(var stream=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None))
                using(var writer=new StreamWriter(stream,new UTF8Encoding(false)))
                using(var json=new JsonTextWriter(writer)) {
                    json.WriteStartObject();foreach(var property in metadata.Properties())property.WriteTo(json);
                    json.WritePropertyName("Inputs");serializer.Serialize(json,movie.Frames.Select(f=>f.Input));
                    json.WritePropertyName("Samples");json.WriteStartArray();
                    using(var source=new StreamReader(new GZipStream(File.OpenRead(Path.Combine(_directory,"samples.jsonl.gz")),CompressionMode.Decompress)))
                        for(string line;(line=source.ReadLine())!=null;)json.WriteRawValue(line);
                    json.WriteEndArray();json.WriteEndObject();json.Flush();writer.Flush();stream.Flush(true);
                }
                if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);
            } finally {if(File.Exists(temporary))File.Delete(temporary);}
        }
        public void Dispose()
        { if(_closed)return;_closed=true;try{_samples.Dispose();}finally{_frames.Dispose();} }
    }
}
