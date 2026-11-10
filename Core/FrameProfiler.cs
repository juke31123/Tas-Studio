using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;

namespace Igtap.Tas.Core
{
    public enum ProfileSection { Session, Input, PlayerProbe, WorldProbe, DesktopCommands, DesktopSnapshot, PlayerStats, Journal }

    // Fixed storage, no per-frame lists or disk I/O. Section times are inclusive:
    // PlayerProbe is part of Session and must not be added to it a second time.
    public sealed class FrameProfiler
    {
        public const int Capacity = 4096;
        private readonly double[,] _sections = new double[Capacity, 8];
        private readonly double[] _intervals = new double[Capacity];
        private readonly bool[] _intervalKnown = new bool[Capacity];
        private readonly int[] _pus = new int[Capacity], _gc = new int[Capacity];
        private readonly double[] _current = new double[8];
        private readonly double[] _operationTotal = new double[8], _operationMax = new double[8];
        private readonly long[] _operationCount = new long[8];
        private long _start;
        private int _pendingInterval = -1;
        private int _count, _write, _gcStart;
        private bool _open;
        private double _autoSaveAt=double.PositiveInfinity;
        // Coalesce repeated frame steps into one idle write. Explicit export
        // and session teardown still save immediately through Save().
        public bool AutoSaveDue(double now,bool advancing,bool paused)
        {
            if(advancing){_autoSaveAt=paused?now+.5:double.PositiveInfinity;return false;}
            if(!paused){_autoSaveAt=double.PositiveInfinity;return false;}
            if(now<_autoSaveAt)return false;
            _autoSaveAt=double.PositiveInfinity;return true;
        }
        public int Samples => _count;
        public string Error { get; private set; } = "";
        public void Begin(bool advancing)
        {
            long now = Stopwatch.GetTimestamp();
            if (_pendingInterval >= 0)
            {
                _intervals[_pendingInterval] = Milliseconds(now - _start);
                _intervalKnown[_pendingInterval] = true; _pendingInterval = -1;
            }
            _open = advancing;
            if (!advancing) return;
            _start = now; _gcStart = GC.CollectionCount(0);
            Array.Clear(_current, 0, _current.Length);
        }
        public Scope Measure(ProfileSection section) => new Scope(this, section);
        public readonly struct Scope : IDisposable
        {
            private readonly FrameProfiler _owner; private readonly ProfileSection _section; private readonly long _start;
            internal Scope(FrameProfiler owner, ProfileSection section) { _owner = owner; _section = section; _start = Stopwatch.GetTimestamp(); }
            public void Dispose()
            {
                if (_owner == null) return;
                int section = (int)_section; double elapsed = Milliseconds(Stopwatch.GetTimestamp() - _start);
                if (_owner._open) _owner._current[section] += elapsed;
                _owner._operationTotal[section] += elapsed; _owner._operationCount[section]++;
                _owner._operationMax[section] = Math.Max(_owner._operationMax[section], elapsed);
            }
        }
        private static double Milliseconds(long ticks) => ticks * (1000.0 / Stopwatch.Frequency);
        public void End(int pus)
        {
            if (!_open) return;
            _open = false;
            _intervalKnown[_write] = false; _pendingInterval = _write;
            _pus[_write] = pus; _gc[_write] = GC.CollectionCount(0) - _gcStart;
            for (int s = 0; s < 8; s++) _sections[_write, s] = _current[s];
            _write = (_write + 1) % Capacity; _count = Math.Min(Capacity, _count + 1);
        }
        private object Distribution(Func<int, double> value, Func<int, bool> include = null)
        {
            var values = new double[_count]; int count = 0; double total = 0;
            for (int i = 0; i < _count; i++) if (include == null || include(i)) { values[count++] = value(i); total += value(i); }
            Array.Sort(values, 0, count);
            return new { Samples = count, MeanMs = count == 0 ? 0 : total / count,
                P50Ms = count == 0 ? 0 : values[(count - 1) / 2], P99Ms = count == 0 ? 0 : values[(int)((count - 1) * .99)], MaxMs = count == 0 ? 0 : values[count - 1] };
        }
        public object Report()
        {
            var sections = new System.Collections.Generic.Dictionary<string, object>();
            var operations = new System.Collections.Generic.Dictionary<string, object>();
            for (int s = 0; s < 8; s++) { int index = s; sections[((ProfileSection)s).ToString()] = Distribution(i => _sections[i, index]); }
            for (int s = 0; s < 8; s++) operations[((ProfileSection)s).ToString()] = new {
                Calls = _operationCount[s], TotalMs = _operationTotal[s], MeanMs = _operationCount[s] == 0 ? 0 : _operationTotal[s] / _operationCount[s], MaxMs = _operationMax[s] };
            int collections = 0; for (int i = 0; i < _count; i++) collections += _gc[i];
            return new { Samples = _count, Capacity, Gen0CollectionsDuringWork = collections,
                FrameIntervals = Distribution(i => _intervals[i], i => _intervalKnown[i]),
                PuFrameIntervals = Distribution(i => _intervals[i], i => _intervalKnown[i] && _pus[i] > 0),
                NonPuFrameIntervals = Distribution(i => _intervals[i], i => _intervalKnown[i] && _pus[i] == 0), Sections = sections,
                OperationsIncludingPausedEditor = operations,
                Note = "Rolling samples. Section timings are inclusive. Frame intervals include pacing/rendering; sections measure only named work. No allocation-byte estimate." };
        }
        public void Save(string path)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, JsonConvert.SerializeObject(Report(), Formatting.Indented)); Error = ""; }
            catch (Exception e) { Error = e.Message; }
        }
        public void Reset()
        {
            _count = _write = 0; _pendingInterval = -1; _open = false; Error = "";
            _autoSaveAt=double.PositiveInfinity;
            Array.Clear(_operationCount, 0, 8); Array.Clear(_operationTotal, 0, 8); Array.Clear(_operationMax, 0, 8);
        }
    }
}
