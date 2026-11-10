using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace Igtap.Tas.Core.Search
{
    [Flags] public enum SearchAbilities { None = 0, Jump = 1, AirJump = 2, Dash = 4, WallJump = 8 }
    public sealed class SearchOptions
    {
        public int StartFrame, Horizon = 180, Seconds = 30, Workers = 2, Seed = 12345;
        public int BeamWidth = 128, ChunkFrames = 1;
        public SearchAbilities Abilities = SearchAbilities.Jump | SearchAbilities.AirJump | SearchAbilities.Dash | SearchAbilities.WallJump;
        public float GoalX, GoalY, GoalRadius = 24;
        public bool GoalSet;
        public bool ExperimentalWorkStealing;
        public bool UseFrameTarget,SecondsPerCheckpoint,UseCheckpointLookahead;
        public int FrameAllowance=1000,CheckpointLookaheadFrames=60;
        [JsonIgnore] public int FrameLimit=>Horizon+(UseFrameTarget?FrameAllowance:0);
        public SearchCheckpoint[] Checkpoints=Array.Empty<SearchCheckpoint>();
        public string GlitchPolicy = "none";
        public string Mode = "beam", Chain = "R:1-30; R+J:1-6; R:1-40";
        public void Validate(int frames)
        {
            if (StartFrame < 0 || StartFrame > frames) throw new ArgumentException("Start frame must be within the recorded TAS.");
            if(Horizon<1||Horizon>20000||FrameAllowance<0||FrameAllowance>20000||FrameLimit>20000||FrameLimit>Movie.MaxFrames-StartFrame)
                throw new ArgumentException("Use 1-20000 frames. In target mode, target + extra frames must not exceed 20000 or the remaining TAS capacity.");
            if (Seconds < 1 || Seconds > 86400 || Workers < 1 || Workers > 32 ||
                BeamWidth < 1 || BeamWidth > 4096 || ChunkFrames < 1 || ChunkFrames > 30) throw new ArgumentException("Invalid search budget.");
            if(Checkpoints==null || Checkpoints.Length>64)throw new ArgumentException("Use at most 64 checkpoints.");
            foreach(var checkpoint in Checkpoints)checkpoint?.Validate();
            if(Array.Exists(Checkpoints,p=>p==null))throw new ArgumentException("A checkpoint is missing.");
            if (Checkpoints.Length==0 && (!GoalSet || !Movie.Finite(GoalX) || !Movie.Finite(GoalY) || !Movie.Finite(GoalRadius) || GoalRadius < 1 || GoalRadius > 1000))
                throw new ArgumentException("Set a finite goal with a radius between 1 and 1000.");
            if (((int)Abilities & ~15) != 0 || GlitchPolicy != "none") throw new NotSupportedException("Glitch search is not supported by the initial simulator.");
            if (Mode != "beam" && Mode != "chain" && Mode != "wave") throw new ArgumentException("Unknown search mode.");
            if(CheckpointLookaheadFrames<1||CheckpointLookaheadFrames>20000)throw new ArgumentException("Lookahead must be 1-20000 frames.");
            if(UseCheckpointLookahead&&!SecondsPerCheckpoint)throw new ArgumentException("Checkpoint lookahead requires per-checkpoint time budgets.");
            if (Mode == "chain") InputChain.Parse(Chain, Abilities, FrameLimit);
        }
        public SearchOptions Copy(){var copy=(SearchOptions)MemberwiseClone();copy.Checkpoints=Checkpoints==null?null:Array.ConvertAll(Checkpoints,p=>p?.Copy());return copy;}
    }
    public sealed class SearchBody
    {
        public string Id = "", Kind = "solid";
        public float MinX, MinY, MaxX, MaxY;
        public MotionVector[] Vertices = Array.Empty<MotionVector>();
        public float Radius;
        public bool Contains(float x, float y) => x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;
    }
    public sealed class SearchState
    {
        public PlayerMotion Player = new PlayerMotion();
        public FrameInput Previous;
        public double Phase, Time;
        public long Pus;
        public float Scale = 1, FreezeRemaining;
        public int WallJumps, Checkpoint;
        [JsonIgnore] public object BackendState;
        public SearchState Copy() => new SearchState { Player = Player.Copy(), Previous = Previous, Phase = Phase, Time = Time,
            Pus = Pus, Scale = Scale, FreezeRemaining = FreezeRemaining, WallJumps = WallJumps, Checkpoint=Checkpoint, BackendState = BackendState };
    }
    public sealed class SearchJob
    {
        public const string Backend = "static-platform-experimental-v1";
        public int Version = 1, Fps;
        public string Id = Guid.NewGuid().ToString("N"), MovieHash = "", PrefixHash = "", GameHash = "", Scene = "";
        public string Simulator = Backend;
        public EngineSearchSpec Engine;
        public SearchOptions Options = new SearchOptions();
        public SearchState Start = new SearchState();
        public MovementParameters Parameters = new MovementParameters();
        public float HalfWidth, HalfHeight, OffsetX, OffsetY;
        public int MaxDashes = 1, MaxAirJumps = 1;
        public bool WallJumpUnlocked, OmniDashUnlocked;
        public SearchBody[] Geometry = Array.Empty<SearchBody>();
        public string[] Coverage = Array.Empty<string>();
        public FrameInput[] SeedInputs = Array.Empty<FrameInput>();
        [JsonIgnore] public CheckpointSearchObjective Objective;
        public SearchJob CopyForSearch(){var copy=(SearchJob)MemberwiseClone();copy.Options=Options.Copy();copy.Start=Start.Copy();return copy;}
        public void Validate()
        {
            if (Version != 1 || (Simulator != Backend && Simulator != EngineSearchSpec.Backend) || string.IsNullOrEmpty(Id) || Options == null || Start?.Player == null || Parameters == null || Geometry == null || Geometry.Length > 200000)
                throw new InvalidDataException("Unsupported/incomplete search job.");
            if (Simulator == EngineSearchSpec.Backend) (Engine ?? throw new InvalidDataException("Missing game-code capture.")).Validate();
            else if (Engine != null) throw new InvalidDataException("Engine capture supplied to a static search.");
            Options.Validate(Options.StartFrame);
            if(Options.Mode=="wave" && Simulator!=EngineSearchSpec.Backend) throw new NotSupportedException("Wave search requires the prepared game-code host.");
            if (Array.IndexOf(new[] { 30, 50, 60, 100, 120, 144, 165, 244, 280 }, Fps) < 0 ||
                !Movie.Finite(HalfWidth) || !Movie.Finite(HalfHeight) || HalfWidth <= 0 || HalfHeight <= 0 || HalfWidth > 1000 || HalfHeight > 1000 ||
                double.IsNaN(Start.Phase) || Start.Phase < 0 || Start.Phase >= .02 || !Movie.Finite(Start.Player.Position.X) || !Movie.Finite(Start.Player.Position.Y))
                throw new InvalidDataException("Invalid search clock or player shape.");
            foreach (var shape in Geometry)
                if (shape == null || !Movie.Finite(shape.MinX) || !Movie.Finite(shape.MinY) || !Movie.Finite(shape.MaxX) || !Movie.Finite(shape.MaxY) ||
                    shape.MaxX <= shape.MinX || shape.MaxY <= shape.MinY || (shape.Kind != "solid" && shape.Kind != "hazard" && shape.Kind != "unsupported"))
                    throw new InvalidDataException("Invalid exported geometry: " + (shape?.Id ?? "missing"));
            foreach (var shape in Geometry)
                if (shape.Vertices == null || shape.Vertices.Length > 64 || shape.Vertices.Length > 0 && shape.Vertices.Length < 3 ||
                    !Movie.Finite(shape.Radius) || shape.Radius < 0 || Array.Exists(shape.Vertices, v => !Movie.Finite(v.X) || !Movie.Finite(v.Y)))
                    throw new InvalidDataException("Invalid collider vertices.");
        }
    }
    public struct AbilityResources
    {
        public int AirJumps, Dashes, WallJumps;
        public bool Alive;
    }
    public sealed class SearchResult
    {
        public int Version = 1;
        public string JobId = "", MovieHash = "", Simulator = SearchJob.Backend;
        public string Status = "Ready", Message = "", Verification = "Unverified";
        public string Algorithm = "";
        public long ArchiveCells, Splices, PlateauDrifts, StateHits, StateReplays;
        public long ReplayFrames, Refinements, CacheBytes;
        public long StolenTasks, WorkerTasks;
        public string Scheduling = "affinity";
        public long Evaluated, SimulatedFrames, Pus, Unsupported, Reached;
        public long ChainCombinations;
        public int Depth, BestFrames = -1;
        public int CheckpointsReached, CheckpointCount;
        public int ActiveCheckpoint,CommittedFrames,StageBestFrames=-1;
        public double StageSeconds,BestScore=double.MaxValue,LookaheadDistance=double.MaxValue;
        public long LookaheadSimulatedFrames;
        public double Seconds, NearestDistance = double.MaxValue;
        public FrameInput[] Inputs = Array.Empty<FrameInput>();
        public MotionVector[] Path = Array.Empty<MotionVector>();
        public bool Finished;
        public string[] BlockedExamples = Array.Empty<string>();
        public SearchResult Copy() => (SearchResult)MemberwiseClone();
    }
    public static class SearchFiles
    {
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.None,
            MissingMemberHandling = MissingMemberHandling.Error, MaxDepth = 48, FloatParseHandling = FloatParseHandling.Double,
            DateParseHandling=DateParseHandling.None,Converters={new MovieJson()} };
        private static readonly JsonSerializerSettings HashSettings=new JsonSerializerSettings{TypeNameHandling=TypeNameHandling.None,
            MissingMemberHandling=MissingMemberHandling.Error,MaxDepth=48,FloatParseHandling=FloatParseHandling.Double,Converters={new CanonicalDictionaryJson()}};
        public static string Hash(object value)
        {
            using var sha = SHA256.Create();
            using var stream=new CryptoStream(Stream.Null,sha,CryptoStreamMode.Write);
            using var text=new StreamWriter(stream,new UTF8Encoding(false),16384,true);
            using var json=new JsonTextWriter(text){CloseOutput=false};
            JsonSerializer.Create(HashSettings).Serialize(json,value);json.Flush();text.Flush();stream.FlushFinalBlock();
            return Convert.ToBase64String(sha.Hash);
        }
        public static string PrefixHash(Movie movie, int frame)
        {
            var prefix = movie.CopyForEdit(); prefix.Frames.RemoveRange(frame, prefix.Frames.Count - frame);
            return Hash(prefix);
        }
        public static void WriteProgress(string path,SearchResult result)
        {
            var sample=result.Copy();
            if(!sample.Finished){sample.Inputs=Array.Empty<FrameInput>();sample.Path=Array.Empty<MotionVector>();}
            Write(path,sample,false);
        }
        public static void Write(string path, object value,bool durable=true)
        {
            string temporary = path + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            try
            {
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                using (var json = new JsonTextWriter(writer) { CloseOutput = false })
                { JsonSerializer.Create(Settings).Serialize(json, value); json.Flush(); writer.Flush(); stream.Flush(durable); }
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public static T Read<T>(string path, long maxBytes = 256L * 1024 * 1024)
        {
            if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes));
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > maxBytes) throw new InvalidDataException("Search file exceeds the import limit.");
            using var reader = new StreamReader(stream);
            using var json = new JsonTextReader(reader);
            var serializer=JsonSerializer.Create(Settings); serializer.CheckAdditionalContent=true;
            return serializer.Deserialize<T>(json);
        }
        public static bool TryReadProgress(string path,out SearchResult result)
        {
            try{result=Read<SearchResult>(path);return result!=null;}
            catch(IOException){result=null;return false;}
            catch(JsonException){result=null;return false;}
        }
        public static bool TryInt(string text, int minimum, int maximum, out int value) =>
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= minimum && value <= maximum;
    }
    public interface ISearchSimulation
    {
        SearchState State { get; }
        void Restore(SearchState snapshot);
        bool Step(FrameInput input, SearchAbilities abilities, out string unsupported);
    }
    // Optional acceleration. Tokens belong to one simulator and must only be
    // captured/restored on its owning worker; they never enter a search job.
    public interface IBranchSearchSimulation : ISearchSimulation
    {
        object CaptureBranch();
        void RestoreBranch(object branch);
        SearchCacheStatistics CacheStatistics { get; }
    }
    public readonly struct SearchCacheStatistics
    {
        public readonly long Hits, Replays, ReplayFrames, Bytes, ReplayPus;
        public SearchCacheStatistics(long hits, long replays, long replayFrames, long bytes, long replayPus = 0)
        { Hits = hits; Replays = replays; ReplayFrames = replayFrames; Bytes = bytes; ReplayPus = replayPus; }
    }
}
