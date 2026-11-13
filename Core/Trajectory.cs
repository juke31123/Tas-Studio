using System;
using System.IO;
using System.Collections.Generic;
using Igtap.Tas.Core.Simulation;

namespace Igtap.Tas.Core
{
    // The game thread appends only newly recorded inputs. Published chunks are
    // immutable, so the transport thread can flatten them without reading the
    // live movie or copying its entire history on every preview request.
    public sealed class TrajectoryJournal
    {
        private const int ChunkSize = 256;
        private readonly List<FrameInput[]> _chunks = new List<FrameInput[]>();
        private FrameInput[] _tail = new FrameInput[ChunkSize];
        private int _used;
        public int Count => _chunks.Count * ChunkSize + _used;
        public void Append(FrameInput input)
        {
            _tail[_used++] = input;
            if (_used == ChunkSize) { _chunks.Add(_tail); _tail = new FrameInput[ChunkSize]; _used = 0; }
        }
        public void Truncate(int count)
        {
            if (count < 0 || count > Count) throw new ArgumentOutOfRangeException(nameof(count));
            if (count == Count) return;
            int full = count / ChunkSize, tail = count % ChunkSize;
            var next = new FrameInput[ChunkSize];
            if (tail != 0) Array.Copy(full < _chunks.Count ? _chunks[full] : _tail, next, tail);
            _chunks.RemoveRange(full, _chunks.Count - full); _tail = next; _used = tail;
        }
        public Snapshot Publish()
        {
            var chunks = new FrameInput[_chunks.Count + (_used == 0 ? 0 : 1)][];
            _chunks.CopyTo(chunks);
            if (_used != 0) { chunks[chunks.Length - 1] = new FrameInput[_used]; Array.Copy(_tail, chunks[chunks.Length - 1], _used); }
            return new Snapshot(chunks, Count);
        }
        public sealed class Snapshot
        {
            private readonly FrameInput[][] _chunks;
            public int Count { get; }
            internal Snapshot(FrameInput[][] chunks, int count) { _chunks = chunks; Count = count; }
            public IEnumerable<FrameInput> Rows()
            { foreach (var chunk in _chunks) foreach (var input in chunk) yield return input; }
            public FrameInput[] ToArray()
            {
                var result = new FrameInput[Count]; int at = 0;
                foreach (var chunk in _chunks) { Array.Copy(chunk, 0, result, at, chunk.Length); at += chunk.Length; }
                return result;
            }
        }
    }
    public static class TrajectoryMailbox
    {
        // Atomic replacement can still meet a transient Windows file lock.
        // Retry in the background instead of losing the latest preview request.
        public static bool TryRead<T>(string path, out T value)
        {
            try { value = Search.SearchFiles.Read<T>(path); return true; }
            catch (IOException) { value = default; return false; }
            catch (Newtonsoft.Json.JsonException) { value = default; return false; }
        }
        public static bool TryWrite(string path, object value)
        {
            try { Search.SearchFiles.Write(path, value, false); return true; }
            catch (IOException) { return false; }
        }
    }
    public interface ITrajectoryPreview : IDisposable
    {
        bool Enabled { get; }
        int Horizon { get; }
        string Status { get; }
        void Configure(bool enabled, int frames);
        void Refresh();
        MotionPrediction Predict(PlayerMotion current, FrameInput input, int frame);
    }

    // Detached protocol shared with the persistent preview process. The first
    // row retains explicit taps; subsequent rows hold controls without retaps.
    public sealed class TrajectoryRequest
    {
        public long Id;
        public int Frame, Frames = 280;
        public bool NextFramePrepared;
        public FrameInput Input;
        public FrameInput[] Prefix = Array.Empty<FrameInput>();
        public SimulationSample Expected;
        public void Validate()
        {
            if (Id <= 0 || Frame < 0 || Frames < 1 || Frames > 2000 || Prefix == null || Prefix.Length > 20000 ||
                Expected?.Fields == null || Expected.Fields.Count > 20000)
                throw new ArgumentException("Invalid trajectory request.");
            Check(Input); foreach (var row in Prefix) Check(row);
        }
        internal static void Check(FrameInput row)
        {
            if (!Movie.Finite(row.X) || !Movie.Finite(row.Y) || Math.Abs(row.X) > 1 || Math.Abs(row.Y) > 1 ||
                (((int)row.Held | (int)row.Press | (int)row.Release) & ~63) != 0)
                throw new ArgumentException("Invalid trajectory input.");
        }
        public static FrameInput Hold(FrameInput input) => new FrameInput { X = input.X, Y = input.Y, Held = input.Held };
        public static bool Same(FrameInput a, FrameInput b) => a.X == b.X && a.Y == b.Y && a.Held == b.Held &&
            a.ExactEdges == b.ExactEdges && (!a.ExactEdges || a.Press == b.Press && a.Release == b.Release);
    }

    public sealed class TrajectoryPoint
    {
        public PlayerMotion Motion;
        public PlayerActionAvailability Actions;
        public long Pus;
    }
    public sealed class TrajectoryResult
    {
        public long Id;
        public int Frame, Fps;
        public FrameInput Input;
        public bool Ready, SnapshotEstimate;
        public double SimulationMilliseconds;
        public string Message = "", End = "horizon";
        public TrajectoryPoint[] Points = Array.Empty<TrajectoryPoint>();
        // Reject stale paths immediately on input changes, rebuilds or drift.
        public int Offset(int frame, FrameInput input, MotionVector worldPosition, MotionVector? velocity = null)
        {
            int offset = frame - Frame;
            if (!Ready || offset < 0 || offset >= Points.Length || input.X != Input.X || input.Y != Input.Y || input.Held != Input.Held ||
                offset == 0 && !TrajectoryRequest.Same(input, Input) ||
                offset > 0 && input.ExactEdges && (input.Press | input.Release) != 0) return -1;
            var p = Points[offset].Motion.Position;
            var v = Points[offset].Motion.Velocity;
            if (velocity.HasValue && (Math.Abs(v.X - velocity.Value.X) > .1f || Math.Abs(v.Y - velocity.Value.Y) > .1f)) return -1;
            return Math.Abs(p.X - worldPosition.X) <= .05f && Math.Abs(p.Y - worldPosition.Y) <= .05f ? offset : -1;
        }
    }
}
