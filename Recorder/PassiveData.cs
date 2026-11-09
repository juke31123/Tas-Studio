using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Search;
using Igtap.Tas.Core.Simulation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace Igtap.Tas.Recorder
{
    public sealed class PassiveSample
    {public string Phase="";public int Pu;public FrameInput Input;public SortedDictionary<string,string> Fields=new SortedDictionary<string,string>(StringComparer.Ordinal);}
    public sealed class PassiveFrame
    {public int Frame,EngineFrame,PhysicsUpdates;public List<PassiveSample> Samples=new List<PassiveSample>();}
    // The game thread never waits for compression/disk I/O. A bounded backlog
    // stops capture rather than pausing gameplay or silently dropping frames.
    public sealed class GameplayJournal : IDisposable
    {
        public const long DefaultLimit=384L*1024*1024;
        private readonly BlockingCollection<string> _queue=new BlockingCollection<string>(32);
        private readonly Task _writer;private readonly long _limit;private readonly int _first;
        private volatile string _error="";private bool _closed;
        public string Error=>_error;public int Frames {get;private set;}public long Pus {get;private set;}public long RawBytes {get;private set;}
        public GameplayJournal(string path,int firstFrame=0,long limit=DefaultLimit)
        {
            _first=firstFrame;_limit=limit;Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
            _writer=Task.Run(()=>{try{using(var gzip=new GZipStream(file,CompressionLevel.Fastest))using(var writer=new StreamWriter(gzip,new UTF8Encoding(false)) {NewLine="\n"})foreach(string line in _queue.GetConsumingEnumerable()){writer.WriteLine(line);writer.Flush();}}catch(Exception e){_error=e.Message;file.Dispose();}});
        }
        public bool Append(PassiveFrame frame)
        {
            if(_closed)throw new ObjectDisposedException(nameof(GameplayJournal));
            if(frame==null || frame.Frame!=_first+Frames || frame.PhysicsUpdates<0 || frame.Samples==null || frame.Samples.Count==0 || frame.Samples.Count>128 || frame.Samples.Last().Phase!="frame-end")throw new InvalidDataException("Invalid passive frame boundary.");
            if(frame.Samples.Count(s=>s.Phase=="before-physics")!=frame.PhysicsUpdates || frame.Samples.Count(s=>s.Phase=="after-physics")!=frame.PhysicsUpdates)throw new InvalidDataException("Missing native physics observations.");
            string line=JsonConvert.SerializeObject(frame,SearchFiles.Settings);long bytes=Encoding.UTF8.GetByteCount(line)+1;
            if(_error.Length>0 || RawBytes+bytes>_limit || !_queue.TryAdd(line))return false;
            Frames++;Pus+=frame.PhysicsUpdates;RawBytes+=bytes;return true;
        }
        public void Dispose(){if(_closed)return;_closed=true;_queue.CompleteAdding();_writer.GetAwaiter().GetResult();_queue.Dispose();}
    }
    public static class PassiveBundle
    {
        public static void Write(string directory,string path,JObject metadata)
        {
            string[] partFiles={"world.json","starting-state.json","animation.json","gameplay.frames.jsonl.gz","contacts.jsonl.gz","contacts-status.json"};
            var files=new List<string>{"environment.json"};
            foreach(string part in Directory.GetDirectories(directory,"part-????").OrderBy(d=>d,StringComparer.Ordinal))
                foreach(string name in partFiles)if(File.Exists(Path.Combine(part,name)))files.Add(Path.GetFileName(part)+"/"+name);
            var hashes=new JObject();foreach(string name in files){string file=Path.Combine(directory,name);hashes[name]=new JObject {["bytes"]=new FileInfo(file).Length,["sha256"]=SimulationTraceComparer.FileHash(file)};}
            metadata["files"]=hashes;SearchFiles.Write(Path.Combine(directory,"capture.json"),metadata);files.Add("capture.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));string temporary=path+".tmp";
            try{using(var stream=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None))using(var zip=new ZipArchive(stream,ZipArchiveMode.Create))foreach(string name in files){var entry=zip.CreateEntry(name,CompressionLevel.Optimal);using(var input=File.OpenRead(Path.Combine(directory,name)))using(var output=entry.Open())input.CopyTo(output);}if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);}finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
    }
}
