using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Igtap.Tas.Core
{
    // A storage codec, independent of the movie's gameplay/timing protocol.
    // Hashes still use the ordinary expanded model. Compression bytes must never
    // become fingerprint inputs: Mono and .NET can encode the same gzip differently.
    public sealed class MovieJson : JsonConverter
    {
        public const string EncodingName = "tas-studio-compact-v2";
        private const string PerFrameEncoding = "tas-studio-compact-v1";
        public const int MaxWorldBytes = 64 * 1024 * 1024;
        private const long MaxExpandedWorldBytes = 256L * 1024 * 1024;
        private const long MaxWorldFields = 8_000_000;
        private static readonly FieldInfo[] Fields = typeof(Movie).GetFields(BindingFlags.Public | BindingFlags.Instance);
        private static readonly Dictionary<string,FieldInfo> Header = Fields.ToDictionary(f=>f.Name,StringComparer.Ordinal);
        private static readonly JsonSerializerSettings WorldSettings = new JsonSerializerSettings {
            TypeNameHandling=TypeNameHandling.None,MissingMemberHandling=MissingMemberHandling.Error,
            FloatParseHandling=FloatParseHandling.Double,DateParseHandling=DateParseHandling.None,MaxDepth=32,
            Converters={new ExactDouble()}
        };
        private static readonly HashSet<string> ProbeKeys = new HashSet<string>(new[]{
            "Position","Velocity","Momentum","RenderPosition","PhysicsVelocity","State","MovementHash","Movement","MovementRemoved"
        },StringComparer.Ordinal);
        private static readonly HashSet<string> RowKeys = new HashSet<string>(ProbeKeys.Concat(new[]{"Input","Expected","FixedTicks","PuPhase","World"}),StringComparer.Ordinal);
        public override bool CanConvert(Type type)=>type==typeof(Movie);

        private sealed class Context
        {
            internal SortedDictionary<string,string> Movement;
            internal WorldWitness World;
            internal long ExpandedWorldBytes,WorldFields;
        }
        private sealed class WorldDelta
        {
            public SortedDictionary<string,string> Exact = new SortedDictionary<string,string>(StringComparer.Ordinal);
            public SortedDictionary<string,double> Numbers = new SortedDictionary<string,double>(StringComparer.Ordinal);
            public string[] RemovedExact=Array.Empty<string>(),RemovedNumbers=Array.Empty<string>();
        }
        private readonly struct InputRun
        {
            internal readonly int StartFrame,Duration;
            internal readonly FrameInput Input;
            internal InputRun(int start,int duration,FrameInput input){StartFrame=start;Duration=duration;Input=input;}
        }
        // A press/release is a per-frame event, not part of a held-key duration.
        // Compare the complete logical sample so edge transitions split runs.
        private static bool SameInput(FrameInput a,FrameInput b)=>Same(a.X,b.X)&&Same(a.Y,b.Y)&&a.Held==b.Held&&
            a.ExactEdges==b.ExactEdges&&a.Press==b.Press&&a.Release==b.Release;
        private static void WriteInput(JsonWriter writer,FrameInput input,JsonSerializer serializer)
        {
            object[] tuple=input.ExactEdges?new object[]{Number(input.X),Number(input.Y),(int)input.Held,(int)input.Press,(int)input.Release}:
                input.Press!=0||input.Release!=0?new object[]{Number(input.X),Number(input.Y),(int)input.Held,(int)input.Press,(int)input.Release,false}:new object[]{Number(input.X),Number(input.Y),(int)input.Held};
            Inline(writer,"Input",tuple,serializer);
        }
        private static void WriteInputRuns(JsonWriter writer,Movie movie,JsonSerializer serializer)
        {
            writer.WritePropertyName("FrameCount");writer.WriteValue(movie.Frames.Count);
            writer.WritePropertyName("InputRuns");writer.WriteStartArray();
            for(int start=0;start<movie.Frames.Count;)
            {
                var input=movie.Frames[start].Input;int end=start+1;
                while(end<movie.Frames.Count&&SameInput(input,movie.Frames[end].Input))end++;
                var format=writer.Formatting;
                // Leave object boundaries indented so a change occupies one line.
                writer.WriteStartObject();writer.Formatting=Formatting.None;
                try
                {
                    writer.WritePropertyName("StartFrame");writer.WriteValue(start);
                    writer.WritePropertyName("Duration");writer.WriteValue(end-start);
                    WriteInput(writer,input,serializer);writer.WriteEndObject();
                }
                finally{writer.Formatting=format;}
                start=end;
            }
            writer.WriteEndArray();
        }

        public override void WriteJson(JsonWriter writer,object value,JsonSerializer serializer)
        {
            if(value==null){writer.WriteNull();return;}
            var movie=(Movie)value;
            var context=new Context();
            writer.WriteStartObject();writer.WritePropertyName("Storage");writer.WriteValue(EncodingName);
            foreach(var field in Fields)
            {
                if(field.Name==nameof(Movie.Frames))WriteInputRuns(writer,movie,serializer);
                writer.WritePropertyName(field.Name);
                switch(field.Name)
                {
                    case nameof(Movie.StartState):
                        if(movie.StartState==null)writer.WriteNull();
                        else{writer.WriteStartObject();WriteProbe(writer,movie.StartState,context,serializer);writer.WriteEndObject();}
                        break;
                    case nameof(Movie.StartWorld):if(movie.StartWorld!=null)CountWorld(context,movie.StartWorld);writer.WriteValue(movie.StartWorld==null?null:Pack(movie.StartWorld,context));context.World=movie.StartWorld?.CompactForReplay();break;
                    case nameof(Movie.Frames):
                        writer.WriteStartArray();foreach(var frame in movie.Frames)WriteFrame(writer,frame,context,serializer);writer.WriteEndArray();break;
                    case nameof(Movie.WarmupFixedTicks):InlineValue(writer,movie.WarmupFixedTicks,serializer);break;
                    default:
                        var scalar=field.GetValue(movie);
                        if(scalar is double d&&BitConverter.DoubleToInt64Bits(d)==long.MinValue)writer.WriteValue("-0");else serializer.Serialize(writer,scalar);
                        break;
                }
            }
            writer.WriteEndObject();
        }
        private static void Inline(JsonWriter writer,string name,object value,JsonSerializer serializer)
        {writer.WritePropertyName(name);InlineValue(writer,value,serializer);}
        private static void InlineValue(JsonWriter writer,object value,JsonSerializer serializer)
        {
            var formatting=writer.Formatting;writer.Formatting=Formatting.None;
            try{serializer.Serialize(writer,value);}finally{writer.Formatting=formatting;}
        }
        private static bool Same(float a,float b)=>BitConverter.SingleToInt32Bits(a)==BitConverter.SingleToInt32Bits(b);
        private static bool Same(double a,double b)=>BitConverter.DoubleToInt64Bits(a)==BitConverter.DoubleToInt64Bits(b);
        // JObject's numeric conversion normalizes -0. Preserve that one bit
        // pattern explicitly; all other coordinates remain ordinary JSON numbers.
        private static object Number(float value)=>BitConverter.SingleToInt32Bits(value)==int.MinValue?(object)"-0":value;
        private static object[] Pair(float a,float b)=>new[]{Number(a),Number(b)};
        private static void WriteProbe(JsonWriter writer,StateProbe p,Context context,JsonSerializer serializer)
        {
            if(p.MovementFields?.Count>256)throw new InvalidDataException("Movement-field limit exceeded.");
            Inline(writer,"Position",Pair(p.X,p.Y),serializer);
            Inline(writer,"Velocity",Pair(p.VelocityX,p.VelocityY),serializer);
            Inline(writer,"Momentum",Pair(p.MomentumX,p.MomentumY),serializer);
            if(!Same(p.RenderX,p.X)||!Same(p.RenderY,p.Y))Inline(writer,"RenderPosition",Pair(p.RenderX,p.RenderY),serializer);
            if(!Same(p.BodyVelocityX,p.VelocityX)||!Same(p.BodyVelocityY,p.VelocityY))Inline(writer,"PhysicsVelocity",Pair(p.BodyVelocityX,p.BodyVelocityY),serializer);
            int flags=(p.Grounded?1:0)|(p.OnWall?2:0)|(p.Dashing?4:0)|(p.FacingRight?8:0)|(p.MenuOpen?16:0)|(p.GamePaused?32:0);
            Inline(writer,"State",new[]{flags,p.AirJumps,p.AirDashes,p.Cutscene},serializer);
            writer.WritePropertyName("MovementHash");writer.WriteValue(p.MovementHash);
            if(p.MovementFields==null)
            {if(context.Movement!=null){writer.WritePropertyName("Movement");writer.WriteNull();}}
            else
            {
                var changed=new SortedDictionary<string,string>(StringComparer.Ordinal);
                foreach(var pair in p.MovementFields)
                    if(context.Movement==null||!context.Movement.TryGetValue(pair.Key,out var old)||old!=pair.Value)changed.Add(pair.Key,pair.Value);
                if(context.Movement==null||changed.Count>0)Inline(writer,"Movement",changed,serializer);
                if(context.Movement!=null)
                {
                    var removed=context.Movement.Keys.Where(k=>!p.MovementFields.ContainsKey(k)).ToArray();
                    if(removed.Length>0)Inline(writer,"MovementRemoved",removed,serializer);
                }
            }
            context.Movement=p.MovementFields;
        }
        private static void WriteFrame(JsonWriter writer,MovieFrame frame,Context context,JsonSerializer serializer)
        {
            writer.WriteStartObject();
            if(frame.Expected==null){writer.WritePropertyName("Expected");writer.WriteNull();}
            else WriteProbe(writer,frame.Expected,context,serializer);
            writer.WritePropertyName("FixedTicks");writer.WriteValue(frame.FixedTicks);
            if(frame.PuPhase>=0){writer.WritePropertyName("PuPhase");if(BitConverter.DoubleToInt64Bits(frame.PuPhase)==long.MinValue)writer.WriteValue("-0");else writer.WriteValue(frame.PuPhase);}
            if(frame.World!=null)
            {
                var previous=context.World??new WorldWitness();var delta=new WorldDelta();
                foreach(var pair in frame.World.Exact)if(!previous.Exact.TryGetValue(pair.Key,out var old)||old!=pair.Value)delta.Exact.Add(pair.Key,pair.Value);
                foreach(var pair in frame.World.Numbers)if(!previous.Numbers.TryGetValue(pair.Key,out var old)||!Same(old,pair.Value))delta.Numbers.Add(pair.Key,pair.Value);
                delta.RemovedExact=previous.Exact.Keys.Where(k=>!frame.World.Exact.ContainsKey(k)).ToArray();
                delta.RemovedNumbers=previous.Numbers.Keys.Where(k=>!frame.World.Numbers.ContainsKey(k)).ToArray();
                CountWorld(context,frame.World);writer.WritePropertyName("World");writer.WriteValue(Pack(delta,context));context.World=frame.World;
            }
            writer.WriteEndObject();
        }

        public override object ReadJson(JsonReader reader,Type objectType,object existingValue,JsonSerializer serializer)
        {
            if(reader.TokenType==JsonToken.Null)return null;
            if(reader.TokenType!=JsonToken.StartObject)throw new JsonSerializationException("A TAS movie must be an object.");
            reader.DateParseHandling=DateParseHandling.None;
            var movie=new Movie();var context=new Context();bool compact=false,runsEncoding=false;
            int? frameCount=null;List<InputRun> runs=null;int runIndex=0;
            var seen=new HashSet<string>(StringComparer.Ordinal);
            while(reader.Read()&&reader.TokenType!=JsonToken.EndObject)
            {
                if(reader.TokenType!=JsonToken.PropertyName)throw new JsonSerializationException("Expected a TAS property.");
                string name=(string)reader.Value;
                if(!seen.Add(name)||!reader.Read())throw new JsonSerializationException("Duplicate or incomplete TAS property: "+name);
                if(name=="Storage")
                {
                    if(reader.TokenType!=JsonToken.String||((string)reader.Value!=EncodingName&&(string)reader.Value!=PerFrameEncoding))throw new JsonSerializationException("Unsupported TAS storage encoding.");
                    if(seen.Contains(nameof(Movie.StartState))||seen.Contains(nameof(Movie.StartWorld))||seen.Contains(nameof(Movie.Frames)))
                        throw new JsonSerializationException("Storage must precede the compact movie data.");
                    compact=true;runsEncoding=(string)reader.Value==EncodingName;continue;
                }
                if(runsEncoding&&name=="FrameCount")
                {
                    if(reader.TokenType!=JsonToken.Integer)throw new JsonSerializationException("FrameCount must be an integer.");
                    long count=Convert.ToInt64(reader.Value);if(count<0||count>Movie.MaxFrames)throw new JsonSerializationException("Movie frame limit exceeded.");
                    frameCount=(int)count;continue;
                }
                if(runsEncoding&&name=="InputRuns")
                {
                    if(!frameCount.HasValue)throw new JsonSerializationException("FrameCount must precede InputRuns.");
                    runs=ReadInputRuns(reader,frameCount.Value);continue;
                }
                if(!Header.TryGetValue(name,out var field))throw new JsonSerializationException("Unknown TAS property: "+name);
                if(name==nameof(Movie.Frames))
                {
                    if(reader.TokenType!=JsonToken.StartArray)throw new JsonSerializationException("Frames must be an array.");
                    if(runsEncoding&&(!frameCount.HasValue||runs==null))throw new JsonSerializationException("FrameCount and InputRuns must precede frame verification data.");
                    movie.Frames=new List<MovieFrame>();
                    while(reader.Read()&&reader.TokenType!=JsonToken.EndArray)
                    {
                        if(movie.Frames.Count>=Movie.MaxFrames)throw new JsonSerializationException("Movie frame limit exceeded.");
                        if(runsEncoding)
                        {
                            int index=movie.Frames.Count;
                            if(index>=frameCount.Value)throw new JsonSerializationException("Frame verification exceeds FrameCount.");
                            while(runIndex<runs.Count&&index>=runs[runIndex].StartFrame+runs[runIndex].Duration)runIndex++;
                            if(runIndex>=runs.Count)throw new JsonSerializationException("Input runs do not cover this frame.");
                            movie.Frames.Add(ReadFrame(Object(reader),context,runs[runIndex].Input));
                        }
                        else movie.Frames.Add(compact?ReadFrame(Object(reader),context):serializer.Deserialize<MovieFrame>(reader));
                    }
                    if(reader.TokenType!=JsonToken.EndArray)throw new JsonSerializationException("Incomplete frame list.");
                }
                else if(compact&&name==nameof(Movie.StartState))
                {if(reader.TokenType==JsonToken.Null)movie.StartState=null;else{var probe=Object(reader);Known(probe,ProbeKeys);movie.StartState=ReadProbe(probe,context);}}
                else if(compact&&name==nameof(Movie.StartWorld))
                {movie.StartWorld=reader.TokenType==JsonToken.Null?null:Unpack<WorldWitness>(Text(reader),context);if(movie.StartWorld!=null){movie.StartWorld.Validate();CountWorld(context,movie.StartWorld);}context.World=movie.StartWorld?.CompactForReplay();}
                else if(compact&&field.FieldType==typeof(double)&&reader.TokenType==JsonToken.String&&(string)reader.Value=="-0")field.SetValue(movie,BitConverter.Int64BitsToDouble(long.MinValue));
                else field.SetValue(movie,serializer.Deserialize(reader,field.FieldType));
            }
            if(reader.TokenType!=JsonToken.EndObject)throw new JsonSerializationException("Incomplete TAS movie.");
            if(runsEncoding&&(!frameCount.HasValue||runs==null||!seen.Contains(nameof(Movie.Frames))||movie.Frames.Count!=frameCount.Value))
                throw new JsonSerializationException("Input durations and frame verification count must equal FrameCount.");
            return movie;
        }
        private static JObject Object(JsonReader reader)=>JObject.Load(reader,new JsonLoadSettings{DuplicatePropertyNameHandling=DuplicatePropertyNameHandling.Error});
        private static string Text(JsonReader reader)
        {if(reader.TokenType!=JsonToken.String)throw new JsonSerializationException("Expected compressed world data.");return (string)reader.Value;}
        private static void Known(JObject data,HashSet<string> names)
        {foreach(var p in data.Properties())if(!names.Contains(p.Name))throw new JsonSerializationException("Unknown compact frame field: "+p.Name);}
        private static JArray Tuple(JObject data,string name,int minimum,int maximum)
        {
            if(!(data[name] is JArray values)||values.Count<minimum||values.Count>maximum)throw new JsonSerializationException("Invalid "+name+" tuple.");
            return values;
        }
        private static float Float(JToken token)
        {if(token?.Type==JTokenType.String&&token.Value<string>()=="-0")return BitConverter.Int32BitsToSingle(int.MinValue);if(token==null||token.Type!=JTokenType.Integer&&token.Type!=JTokenType.Float)throw new JsonSerializationException("Expected a numeric value.");float value=token.Value<float>();if(!Movie.Finite(value))throw new JsonSerializationException("Non-finite compact value.");return value;}
        private static int Int(JToken token)
        {if(token==null||token.Type!=JTokenType.Integer)throw new JsonSerializationException("Expected an integer value.");return token.Value<int>();}
        private static StateProbe ReadProbe(JObject row,Context context)
        {
            var pos=Tuple(row,"Position",2,2);var velocity=Tuple(row,"Velocity",2,2);var momentum=Tuple(row,"Momentum",2,2);var state=Tuple(row,"State",4,4);
            int flags=Int(state[0]);if((flags&~63)!=0)throw new JsonSerializationException("Unknown compact movement flags.");
            if(row["MovementHash"]==null||row["MovementHash"].Type!=JTokenType.String&&row["MovementHash"].Type!=JTokenType.Null)throw new JsonSerializationException("A verified frame requires its movement hash.");
            var p=new StateProbe{X=Float(pos[0]),Y=Float(pos[1]),VelocityX=Float(velocity[0]),VelocityY=Float(velocity[1]),MomentumX=Float(momentum[0]),MomentumY=Float(momentum[1]),
                Grounded=(flags&1)!=0,OnWall=(flags&2)!=0,Dashing=(flags&4)!=0,FacingRight=(flags&8)!=0,MenuOpen=(flags&16)!=0,GamePaused=(flags&32)!=0,
                AirJumps=Int(state[1]),AirDashes=Int(state[2]),Cutscene=Int(state[3]),MovementHash=row["MovementHash"].Value<string>()};
            var render=row["RenderPosition"]==null?pos:Tuple(row,"RenderPosition",2,2);
            var body=row["PhysicsVelocity"]==null?velocity:Tuple(row,"PhysicsVelocity",2,2);
            p.RenderX=Float(render[0]);p.RenderY=Float(render[1]);p.BodyVelocityX=Float(body[0]);p.BodyVelocityY=Float(body[1]);
            var fields=context.Movement;
            if(row.TryGetValue("Movement",out var movement))
            {
                if(movement.Type==JTokenType.Null)fields=null;
                else if(movement is JObject changes)
                {
                    fields=fields==null?new SortedDictionary<string,string>(StringComparer.Ordinal):new SortedDictionary<string,string>(fields,StringComparer.Ordinal);
                    foreach(var change in changes.Properties())
                    {if(change.Value.Type!=JTokenType.String&&change.Value.Type!=JTokenType.Null)throw new JsonSerializationException("Invalid movement-field text.");fields[change.Name]=change.Value.Value<string>();}
                }
                else throw new JsonSerializationException("Invalid movement delta.");
            }
            if(row.TryGetValue("MovementRemoved",out var removed))
            {
                if(!(removed is JArray list)||fields==null)throw new JsonSerializationException("Invalid removed movement fields.");
                if(ReferenceEquals(fields,context.Movement))fields=new SortedDictionary<string,string>(fields,StringComparer.Ordinal);
                foreach(var key in list){if(key.Type!=JTokenType.String||!fields.Remove(key.Value<string>()))throw new JsonSerializationException("Unknown removed movement field.");}
            }
            if(fields?.Count>256)throw new JsonSerializationException("Movement-field limit exceeded.");
            p.MovementFields=context.Movement=fields;return p;
        }
        private static FrameInput ReadInput(JObject row)
        {
            var values=Tuple(row,"Input",3,6);
            if(values.Count!=3&&values.Count!=5&&values.Count!=6)throw new JsonSerializationException("Invalid input tuple length.");
            if(values.Count==6&&(values[5].Type!=JTokenType.Boolean||values[5].Value<bool>()))throw new JsonSerializationException("Invalid inferred-edge marker.");
            var input=new FrameInput{X=Float(values[0]),Y=Float(values[1]),Held=(Buttons)Int(values[2]),ExactEdges=values.Count==5};
            if(values.Count>=5){input.Press=(Buttons)Int(values[3]);input.Release=(Buttons)Int(values[4]);}
            if(Math.Abs(input.X)>1||Math.Abs(input.Y)>1||(((int)input.Held|(int)input.Press|(int)input.Release)&~63)!=0)
                throw new JsonSerializationException("Invalid input axes or button masks.");
            return input;
        }
        private static List<InputRun> ReadInputRuns(JsonReader reader,int count)
        {
            if(reader.TokenType!=JsonToken.StartArray)throw new JsonSerializationException("InputRuns must be an array.");
            var runs=new List<InputRun>();int expected=0;
            var keys=new HashSet<string>(new[]{"StartFrame","Duration","Input"},StringComparer.Ordinal);
            while(reader.Read()&&reader.TokenType!=JsonToken.EndArray)
            {
                if(runs.Count>=Movie.MaxFrames)throw new JsonSerializationException("Input run limit exceeded.");
                var row=Object(reader);Known(row,keys);
                int start=Int(row["StartFrame"]),duration=Int(row["Duration"]);
                if(start!=expected||duration<=0||duration>count-expected)
                    throw new JsonSerializationException("Input runs must be contiguous, have positive durations and cover FrameCount exactly.");
                runs.Add(new InputRun(start,duration,ReadInput(row)));expected+=duration;
            }
            if(reader.TokenType!=JsonToken.EndArray||expected!=count)throw new JsonSerializationException("Input durations do not equal FrameCount.");
            return runs;
        }
        private static MovieFrame ReadFrame(JObject row,Context context,FrameInput? runInput=null)
        {
            Known(row,RowKeys);
            if(runInput.HasValue&&row["Input"]!=null)throw new JsonSerializationException("A run-encoded frame cannot override InputRuns.");
            var input=runInput??ReadInput(row);
            var frame=new MovieFrame{Input=input,FixedTicks=Int(row["FixedTicks"])};
            if(row.TryGetValue("Expected",out var empty))
            {if(empty.Type!=JTokenType.Null||row.Properties().Any(p=>ProbeKeys.Contains(p.Name)))throw new JsonSerializationException("An unverified frame cannot contain a state probe.");}
            else frame.Expected=ReadProbe(row,context);
            if(row.TryGetValue("PuPhase",out var phase))
            {if(phase.Type==JTokenType.String&&phase.Value<string>()=="-0")frame.PuPhase=BitConverter.Int64BitsToDouble(long.MinValue);else{if(phase.Type!=JTokenType.Float&&phase.Type!=JTokenType.Integer)throw new JsonSerializationException("Invalid PU phase.");frame.PuPhase=phase.Value<double>();}}
            if(row.TryGetValue("World",out var world))
            {
                if(world.Type!=JTokenType.String)throw new JsonSerializationException("Invalid world delta.");
                var delta=Unpack<WorldDelta>(world.Value<string>(),context);var previous=context.World??new WorldWitness();
                var next=new WorldWitness{Exact=new SortedDictionary<string,string>(previous.Exact,StringComparer.Ordinal),Numbers=new SortedDictionary<string,double>(previous.Numbers,StringComparer.Ordinal)};
                if(delta.Exact==null||delta.Numbers==null||delta.RemovedExact==null||delta.RemovedNumbers==null)throw new JsonSerializationException("Incomplete world delta.");
                foreach(var key in delta.RemovedExact)if(!next.Exact.Remove(key))throw new JsonSerializationException("Unknown removed world field.");
                foreach(var key in delta.RemovedNumbers)if(!next.Numbers.Remove(key))throw new JsonSerializationException("Unknown removed world field.");
                foreach(var pair in delta.Exact)next.Exact[pair.Key]=pair.Value;
                foreach(var pair in delta.Numbers)next.Numbers[pair.Key]=pair.Value;
                next.Validate();CountWorld(context,next);frame.World=context.World=next;
            }
            return frame;
        }
        private static void CountWorld(Context context,WorldWitness world)
        {context.WorldFields+=(long)world.Exact.Count+world.Numbers.Count;if(context.WorldFields>MaxWorldFields)throw new InvalidDataException("Movie world-witness field budget exceeded.");}
        private static string Pack(object value,Context context)
        {
            using var bytes=new MemoryStream();
            using(var gzip=new GZipStream(bytes,CompressionLevel.Optimal,true))
            using(var bounded=new BoundedWrite(gzip,Math.Min(MaxWorldBytes,MaxExpandedWorldBytes-context.ExpandedWorldBytes)))
            using(var text=new StreamWriter(bounded,new UTF8Encoding(false),16384))
            using(var json=new JsonTextWriter(text)){JsonSerializer.Create(WorldSettings).Serialize(json,value);json.Flush();text.Flush();context.ExpandedWorldBytes+=bounded.Position;}
            if(bytes.Length>MaxWorldBytes)throw new InvalidDataException("Compressed world witness exceeds its limit.");
            return Convert.ToBase64String(bytes.GetBuffer(),0,(int)bytes.Length);
        }
        private static T Unpack<T>(string text,Context context) where T:class
        {
            if(text==null||text.Length>(MaxWorldBytes/3+1)*4)throw new InvalidDataException("Compressed world witness exceeds its limit.");
            using var bytes=new MemoryStream(Convert.FromBase64String(text),false);
            using var gzip=new GZipStream(bytes,CompressionMode.Decompress);
            using var bounded=new BoundedRead(gzip,Math.Min(MaxWorldBytes,MaxExpandedWorldBytes-context.ExpandedWorldBytes));
            using var input=new StreamReader(bounded,new UTF8Encoding(false,true),false,16384);
            using var json=new JsonTextReader(input);
            var serializer=JsonSerializer.Create(WorldSettings);serializer.CheckAdditionalContent=true;
            var value=serializer.Deserialize<T>(json)??throw new InvalidDataException("Empty world witness.");context.ExpandedWorldBytes+=bounded.Position;return value;
        }
        // Bound decompressed bytes as well as the outer JSON file. A tiny gzip
        // must not be allowed to allocate an unbounded world during import.
        private sealed class BoundedRead : Stream
        {
            private readonly Stream _inner;private readonly long _maximum;private long _read;
            internal BoundedRead(Stream inner,long maximum){_inner=inner;_maximum=maximum;}
            public override int Read(byte[] buffer,int offset,int count){int n=_inner.Read(buffer,offset,count);_read+=n;if(_read>_maximum)throw new InvalidDataException("Expanded world witness exceeds its limit.");return n;}
            public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
            public override long Length=>throw new NotSupportedException();public override long Position{get=>_read;set=>throw new NotSupportedException();}
            public override void Flush(){}public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();
        }
        private sealed class BoundedWrite : Stream
        {
            private readonly Stream _inner;private readonly long _maximum;private long _written;
            internal BoundedWrite(Stream inner,long maximum){_inner=inner;_maximum=maximum;}
            public override void Write(byte[] buffer,int offset,int count){_written+=count;if(_written>_maximum)throw new InvalidDataException("Expanded world witness exceeds its limit.");_inner.Write(buffer,offset,count);}
            public override bool CanRead=>false;public override bool CanSeek=>false;public override bool CanWrite=>true;
            public override long Length=>throw new NotSupportedException();public override long Position{get=>_written;set=>throw new NotSupportedException();}
            public override void Flush()=>_inner.Flush();public override int Read(byte[] b,int o,int c)=>throw new NotSupportedException();public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();public override void SetLength(long v)=>throw new NotSupportedException();
        }
        private sealed class ExactDouble : JsonConverter
        {
            public override bool CanConvert(Type type)=>type==typeof(double);
            public override void WriteJson(JsonWriter writer,object value,JsonSerializer serializer)
            {double number=(double)value;if(BitConverter.DoubleToInt64Bits(number)==long.MinValue)writer.WriteValue("-0");else writer.WriteValue(number);}
            public override object ReadJson(JsonReader reader,Type type,object existing,JsonSerializer serializer)
            {if(reader.TokenType==JsonToken.String&&(string)reader.Value=="-0")return BitConverter.Int64BitsToDouble(long.MinValue);if(reader.TokenType!=JsonToken.Integer&&reader.TokenType!=JsonToken.Float)throw new JsonSerializationException("Expected a world number.");return Convert.ToDouble(reader.Value,System.Globalization.CultureInfo.InvariantCulture);}
        }
    }
}
