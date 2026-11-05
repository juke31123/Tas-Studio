using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Igtap.Tas.Core
{
    // Passive end-of-frame measurements. Call with a real monotonic clock, never
    // deltaTime or the movie clock. Pauses/loading are excluded from throughput.
    public sealed class PerformanceTrace
    {
        private readonly string _directory;
        private bool _created;
        private string _mode, _name;
        private int _fps, _cursor, _frames, _totalFrames, _target, _vsync;
        private long _pus, _windowPus, _totalPus;
        private double _last, _seconds, _totalSeconds, _maxMs, _totalMaxMs, _scaleSum;
        private float _requested, _scale;
        private bool _anchor, _focused;
        public double ActualFps { get; private set; }
        public double AchievedSpeed { get; private set; }
        public bool HasSample { get; private set; }
        public string ReportPath { get; private set; } = "";
        public string Error { get; private set; } = "";

        public PerformanceTrace(string directory) { _directory = directory; }

        public void Observe(double now, string mode, string name, int cursor, int fps, long pus,
            bool paused, float requested, int target, int vsync, float scale, bool focused)
        {
            bool running = mode == "Recording" || mode == "Playback";
            bool finishing = mode == "Complete" || mode == "Desync";
            if (_mode != null && ((running && (mode != _mode || fps != _fps)) || cursor < _cursor || (!running && !finishing)))
                Finish(mode);
            if (_mode == null && !running) return;
            if (_mode == null)
            {
                _mode = mode; _name = name; _fps = fps;
                _totalFrames = 0; _totalSeconds = _totalMaxMs = 0; _totalPus = 0;
                HasSample = false; ActualFps = AchievedSpeed = 0; Error = "";
                _created = false;
                ReportPath = Path.Combine(_directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + mode.ToLowerInvariant() + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".csv");
            }
            if (_anchor && cursor == _cursor + 1 && now > _last)
            {
                double elapsed = now - _last;
                _seconds += elapsed; _totalSeconds += elapsed;
                _frames++; _totalFrames++;
                long count = Math.Max(0, pus - _pus);
                _windowPus += count; _totalPus += count;
                _scaleSum += _scale;
                _maxMs = Math.Max(_maxMs, elapsed * 1000); _totalMaxMs = Math.Max(_totalMaxMs, elapsed * 1000);
            }
            // End the old sample before changing its pacing/context labels.
            if (_seconds >= 1 || paused || !running || requested != _requested || target != _target ||
                focused != _focused || vsync != _vsync) FlushSample(paused ? "paused" : "sample");
            _anchor = running && !paused;
            _last = now; _cursor = cursor; _pus = pus;
            _requested = requested; _target = target; _vsync = vsync; _scale = scale;
            _focused = focused;
            if (!running) Finish(mode);
        }

        private void FlushSample(string reason)
        {
            if (_frames == 0) return;
            ActualFps = _frames / _seconds;
            AchievedSpeed = ActualFps / _fps;
            HasSample = true;
            Write(reason, _frames, _seconds, _windowPus, _maxMs, _scaleSum / _frames);
            _frames = 0; _seconds = _maxMs = _scaleSum = 0; _windowPus = 0;
        }

        public void Finish(string reason)
        {
            if (_mode == null) return;
            FlushSample(reason);
            if (_totalFrames > 0) Write("total: " + reason, _totalFrames, _totalSeconds, _totalPus, _totalMaxMs, double.NaN);
            _mode = null; _anchor = false;
        }

        private void Write(string reason, int frames, double seconds, long pus, double maxMs, double scale)
        {
            if (Error.Length > 0) return;
            try
            {
                Directory.CreateDirectory(_directory);
                using var writer = new StreamWriter(new FileStream(ReportPath, _created ? FileMode.Append : FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
                if (!_created) writer.WriteLine("Event,Movie,Mode,MovieFPS,RequestedSpeed,TargetFPS,VSync,Focused,Frames,WallSeconds,ActualFPS,AchievedSpeed,PUCount,RealPUsPerSecond,MaxFrameMs,MeanGameTimeScale");
                _created = true;
                writer.WriteLine(string.Join(",", Quote(reason), Quote(_name), _mode, N(_fps), N(_requested), N(_target), N(_vsync),
                    _focused ? "true" : "false", N(frames), N(seconds), N(frames / seconds),
                    N(frames / seconds / _fps), N(pus), N(pus / seconds), N(maxMs), double.IsNaN(scale) ? "" : N(scale)));
                // Close after each small row so reports can be opened while paused.
            }
            catch (Exception e) { Error = e.Message; }
        }
        private static string N(double n) => n.ToString("0.######", CultureInfo.InvariantCulture);
        private static string Quote(string text) => "\"" + (text ?? "").Replace("\"", "\"\"") + "\"";
    }
}
