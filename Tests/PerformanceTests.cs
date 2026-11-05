using System;
using System.IO;
using Igtap.Tas.Core;

internal static class PerformanceTests
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool result, string name) { if (!result) throw new Exception(name); checks++; }
        var folder = Path.Combine(Path.GetTempPath(), "tas-fps-" + Guid.NewGuid().ToString("N"));
        var p = new PerformanceTrace(folder);
        void Frame(double now, int cursor, bool paused = false, string mode = "Recording", float speed = 1) =>
            p.Observe(now, mode, "FPS test", cursor, 280, cursor / 6, paused, speed, (int)(280 * speed), 0, 1, true);
        try
        {
            Frame(1, 0, true, "Loading");
            Check(!Directory.Exists(folder), "Loading creates no performance file");
            Frame(2, 0, true);
            Frame(102, 0, true);
            Check(!p.HasSample, "Paused frames do not count as zero throughput");
            Frame(103, 0);
            for (int f = 1; f <= 140; f++) Frame(103 + f / 140.0, f, f == 140);
            Check(Math.Abs(p.ActualFps - 140) < .001, "Measures actual 140 FPS against selected 280");
            Check(Math.Abs(p.AchievedSpeed - .5) < .00001, "Reports half-speed without changing movie time");
            string recorded = p.ReportPath;
            Check(File.ReadAllText(recorded).Contains("ActualFPS,AchievedSpeed"), "CSV available while paused");
            Check(File.ReadAllText(recorded).Contains("\"FPS test\",Recording,280,1,280"), "Report identifies movie, mode and targets");
            Frame(1000, 140, true);
            Check(Math.Abs(p.ActualFps - 140) < .001, "Long editor pause does not lower last measured FPS");
            Frame(1001, 140);
            Frame(1001 + 1.0 / 280, 141, true);
            Check(Math.Abs(p.ActualFps - 280) < .001, "Single step excludes the paused interval");
            Frame(1002, 141, true, "Stopping");
            Check(File.ReadAllText(recorded).Contains("total: Stopping"), "Stopping flushes totals");
            Frame(1003, 0, false, "Playback", .5f);
            Check(p.ReportPath != recorded && !p.HasSample, "Replay gets a fresh report and sample");
            Frame(1003 + 1.0 / 140, 1, true, "Complete", .5f);
            Check(p.HasSample && Math.Abs(p.ActualFps - 140) < .001, "Final playback frame is counted after Complete");
            Check(File.ReadAllText(p.ReportPath).Contains("total: Complete"), "EOF saves a replay total");
            Check(Directory.GetFiles(folder, "*.csv").Length == 2, "Recording and replay reports are retained separately");
            string blocked = Path.Combine(folder, "not-a-directory"); File.WriteAllText(blocked, "file");
            var bad = new PerformanceTrace(blocked);
            bad.Observe(0, "Recording", "test", 0, 280, 0, false, 1, 280, 0, 1, true);
            bad.Observe(1, "Complete", "test", 1, 280, 0, true, 1, 280, 0, 1, true);
            Check(bad.Error.Length > 0 && bad.HasSample, "Report IO failure does not interrupt measurement/gameplay");
        }
        finally { p.Finish("test cleanup"); if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        Console.WriteLine("PASS: " + checks + " real-time FPS diagnostics checks.");
    }
}
