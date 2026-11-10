using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Search;
using Igtap.Tas.Core.Simulation;

internal static class MovieStorageProbe
{
    private static int Main(string[] args)
    {
        try
        {
            if(args.Length<2)throw new ArgumentException("Usage: <new-output-directory> <source.tas.json> [...]");
            string output=Path.GetFullPath(args[0]);if(Directory.Exists(output))throw new IOException("Use a new output directory; source recordings are never overwritten.");
            var store=new MovieStore(output);var reports=new List<object>();
            foreach(var path in args.Skip(1))
            {
                string source=Path.GetFullPath(path),name=Path.GetFileName(source);string original=SimulationTraceComparer.FileHash(source);
                var movie=SearchFiles.Read<Movie>(source);movie.Validate();string hash=SearchFiles.Hash(movie);
                store.Save(name,movie);string target=store.PathFor(name);var read=store.Load(name);
                if(SearchFiles.Hash(read)!=hash)
                {
                    string Difference(Newtonsoft.Json.Linq.JToken a,Newtonsoft.Json.Linq.JToken b,string at)
                    {
                        if(Newtonsoft.Json.Linq.JToken.DeepEquals(a,b))return null;
                        if(a is Newtonsoft.Json.Linq.JObject obj&&b is Newtonsoft.Json.Linq.JObject other)
                        {foreach(var p in obj.Properties()){var difference=Difference(p.Value,other[p.Name],at+"/"+p.Name);if(difference!=null)return difference;}}
                        return at+": "+a+" != "+b;
                    }
                    foreach(var field in typeof(Movie).GetFields(System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public))
                    {
                        if(field.Name=="Frames")continue;
                        var a=Newtonsoft.Json.JsonConvert.SerializeObject(field.GetValue(movie));var b=Newtonsoft.Json.JsonConvert.SerializeObject(field.GetValue(read));
                        if(a!=b){int at=0;while(at<Math.Min(a.Length,b.Length)&&a[at]==b[at])at++;throw new InvalidDataException(name+" header "+field.Name+" at "+at+": "+a.Substring(Math.Max(0,at-30),Math.Min(120,a.Length-Math.Max(0,at-30)))+" != "+b.Substring(Math.Max(0,at-30),Math.Min(120,b.Length-Math.Max(0,at-30)))+"; "+Difference(Newtonsoft.Json.Linq.JToken.Parse(a),Newtonsoft.Json.Linq.JToken.Parse(b),field.Name));}
                    }
                    for(int i=0;i<movie.Frames.Count;i++)
                    {var a=Newtonsoft.Json.JsonConvert.SerializeObject(movie.Frames[i]);var b=Newtonsoft.Json.JsonConvert.SerializeObject(read.Frames[i]);if(a!=b)throw new InvalidDataException(name+" frame "+i+" "+Difference(Newtonsoft.Json.Linq.JToken.Parse(a),Newtonsoft.Json.Linq.JToken.Parse(b),""));}
                    throw new InvalidDataException("Expanded movie fingerprint differs: "+name);
                }
                if(SimulationTraceComparer.FileHash(source)!=original)throw new InvalidDataException("Source recording changed: "+name);
                var lines=new List<int>();bool frames=false;int current=0;
                foreach(string line in File.ReadLines(target))
                {
                    if(line=="  \"Frames\": [")frames=true;
                    else if(frames&&line=="    {")current=1;
                    else if(current>0){current++;if(line=="    },"||line=="    }"){lines.Add(current);current=0;}}
                }
                long before=new FileInfo(source).Length,after=new FileInfo(target).Length;
                var inputRuns=new List<int>();using(var input=File.OpenText(target))using(var json=new Newtonsoft.Json.JsonTextReader(input))
                {while(json.Read())if(json.TokenType==Newtonsoft.Json.JsonToken.PropertyName&&(string)json.Value=="InputRuns"){json.Read();while(json.Read()&&json.TokenType!=Newtonsoft.Json.JsonToken.EndArray){var run=Newtonsoft.Json.Linq.JObject.Load(json);inputRuns.Add((int)run["Duration"]);}break;}}
                reports.Add(new{Name=name,Frames=read.Frames.Count,BeforeBytes=before,AfterBytes=after,ReductionPercent=100.0*(before-after)/before,
                    InputRuns=inputRuns.Count,InputDurationSum=inputRuns.Sum(),
                    BytesPerFrame=after/(double)Math.Max(1,read.Frames.Count),MedianFrameLines=lines.Count==0?0:lines.OrderBy(n=>n).ElementAt(lines.Count/2),MaximumFrameLines=lines.Count==0?0:lines.Max(),CanonicalHashUnchanged=true,SourceFileUnchanged=true,
                    Source=source,SourceSha256=original,CompactSha256=SimulationTraceComparer.FileHash(target),Output=target});
            }
            SearchFiles.Write(Path.Combine(output,"comparison.json"),reports);Console.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(reports,Newtonsoft.Json.Formatting.Indented));return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
