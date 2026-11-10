using System;
using System.IO;
using System.Linq;
using Igtap.Tas.Core;
using Newtonsoft.Json.Linq;

internal static class ReliabilityTests
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
        void Throws(Action action, string name) { try { action(); } catch { checks++; return; } throw new Exception("Expected rejection: " + name); }
        Movie Sample() => new Movie { BootstrapVersion = 4, StartState = new StateProbe(), Frames = Enumerable.Range(0, 6)
            .Select(i => new MovieFrame { Input = new FrameInput { X = i / 10f }, FixedTicks = 1, Expected = new StateProbe(),
                World = new WorldWitness { Numbers = { ["moving-platform/x"] = i } } }).ToList() };
        string folder = Path.Combine(Path.GetTempPath(), "tas-reliability-" + Guid.NewGuid().ToString("N"));
        try
        {
            var original = Sample(); var edited = original.CopyForEdit();
            FrameCellEdit.Paint(edited, 2, 2, InputColumn.Jump, true, 0);
            Check(original.Frames[2].Input.Held == 0 && original.Frames[2].Expected != null, "Draft row mutation does not touch live movie");
            Check(edited.Frames[1].World != null && edited.Frames[2].World == null, "Past edit invalidates downstream world references only");
            var history = new EditHistory(); var change = InputEdit.Between(original, edited); history.Push(change);
            Check(change.Start == 2 && change.Before.Length == 2 && change.After.Length == 2, "History stores only changed input range");
            var undone = history.Peek(false).Apply(edited, true); history.Commit(false);
            Check(InputEdit.Between(original, undone) == null && undone.Frames[2].Expected == null, "Undo restores input without reviving old simulation proof");
            var redone = history.Peek(true).Apply(undone); history.Commit(true);
            Check(InputEdit.Between(edited, redone) == null, "Redo restores exact inputs");
            var insert = edited.CopyForEdit(); Timeline.Insert(insert, 3, 2, new FrameInput { Held = Buttons.Menu, ExactEdges = true, Press = Buttons.Menu });
            var insertion = InputEdit.Between(edited, insert);
            Check(InputEdit.Between(insertion.Apply(edited), insert) == null, "Insertion roundtrips explicit input edges");
            Check(InputEdit.Between(insertion.Apply(insert, true), edited) == null, "Insertion undo preserves tail");
            var deletion = insert.CopyForEdit(); Timeline.Delete(deletion, 1, 4);
            var del = InputEdit.Between(insert, deletion);
            Check(InputEdit.Between(del.Apply(deletion, true), insert) == null, "Deletion undo restores removed range");
            Throws(() => change.Apply(edited), "Stale history cannot apply over changed inputs");
            history.Clear(); for (int i = 0; i < 205; i++) history.Push(change);
            Check(history.UndoCount == 200, "History depth is bounded");
            history.Commit(false); history.Push(insertion);
            Check(history.RedoCount == 0, "New edit clears redo branch");

            var journalPath = Path.Combine(folder, "journal"); var journal = new DraftJournal(journalPath);
            journal.Append("recovery-test", original, edited); journal.Append("recovery-test", edited, insert);
            string lockedLog = Directory.GetFiles(journalPath, "edits.jsonl", SearchOption.AllDirectories).Single();
            using (var held = new FileStream(lockedLog, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Throws(() => journal.Append("recovery-test", insert, edited), "Failed durable write rejects edit instead of silently losing recovery");
            var afterCrash = new DraftJournal(journalPath);
            Check(afterCrash.Available && InputEdit.Between(afterCrash.Recover(), insert) == null, "Crash recovery replays durable deltas from baseline");
            string log = Directory.GetFiles(journalPath, "edits.jsonl", SearchOption.AllDirectories).Single();
            File.AppendAllText(log, "{\"Sequence\":3");
            Check(InputEdit.Between(new DraftJournal(journalPath).Recover(), insert) == null, "Truncated last write preserves last complete edit");
            afterCrash.Resolve(); Check(!new DraftJournal(journalPath).Available, "Resolved recovery is not offered again");
            var brokenPath = Path.Combine(folder, "broken"); var broken = new DraftJournal(brokenPath);
            broken.Append("broken", original, edited);
            File.AppendAllText(Directory.GetFiles(brokenPath, "edits.jsonl", SearchOption.AllDirectories).Single(), "garbage\n");
            Throws(() => new DraftJournal(brokenPath).Recover(), "Complete malformed journal entry is rejected");

            var cache = new RebuildCheckpoints(); var witness = new WorldWitness { Numbers = { ["origin/x"] = 20 }, Exact = { ["rng"] = "a" } };
            for (int i = 0; i < 40; i++) cache.Remember(i, witness);
            Check(cache.Count == 32 && cache.LiveDifference(0, witness) != null && cache.LiveDifference(39, witness) == null, "Checkpoint cache bounds and evicts old entries");
            cache.InvalidateFrom(20); Check(cache.LiveDifference(20, witness) == null && cache.LiveDifference(21, witness) != null, "Edit invalidation keeps state before changed input");
            var different = new WorldWitness { Numbers = { ["origin/x"] = 21 }, Exact = { ["rng"] = "a" } };
            Check(cache.LiveDifference(20, different).Contains("expected 20, actual 21"), "Checkpoint mismatch identifies field and both values");
            Check(RebuildCheckpoints.SamePrefix(original, edited, 2) && !RebuildCheckpoints.SamePrefix(original, edited, 3), "Reuse only accepts identical executed input prefix");
            var otherFps = original.CopyForEdit(); otherFps.FrameRate = 280;
            Check(!RebuildCheckpoints.SamePrefix(original, otherFps, 0), "Clock mismatch invalidates checkpoint reuse");
            var otherStart = original.CopyForEdit(); otherStart.StartState = new StateProbe { X = 100 };
            Check(!RebuildCheckpoints.SamePrefix(original, otherStart, 0), "Different startup state invalidates checkpoint reuse");
            var bulk = new WorldWitness { Numbers = { ["/courseScript#0/currentPlayerPath/0/x"] = 3, ["currency"] = 10 } };
            var compact = bulk.CompactForReplay();
            Check(compact.Exact.ContainsKey("digest/course-paths") && compact.Numbers.Count == 1, "Large course history is digested rather than repeated in each movie checkpoint");
            bulk.Numbers["/courseScript#0/currentPlayerPath/0/x"] = 4;
            Check(compact.Difference(bulk.CompactForReplay()).Contains("digest/course-paths"), "Digest still detects changed historical course input");
            var profiler = new FrameProfiler();
            for (int i = 0; i < 4200; i++) { profiler.Begin(true); using (profiler.Measure(ProfileSection.PlayerProbe)) { } profiler.End(i % 2); }
            Check(profiler.Samples == FrameProfiler.Capacity, "Profiler ring remains bounded");
            string profile = Path.Combine(folder, "profile.json"); profiler.Save(profile);
            var report = File.ReadAllText(profile);
            Check(report.Contains("P99Ms") && report.Contains("PlayerProbe") && report.Contains("Gen0Collections"), "Profiler exports percentiles, sections and GC information");
            profiler.Reset(); Check(profiler.Samples == 0, "New session clears rolling profile");
            Check(!profiler.AutoSaveDue(0,true,true) && !profiler.AutoSaveDue(.49,false,true),"Frame-step autosaves wait for idle time.");
            Check(!profiler.AutoSaveDue(.4,true,true) && !profiler.AutoSaveDue(.6,false,true),"Repeated steps coalesce their autosave deadline.");
            Check(profiler.AutoSaveDue(.91,false,true) && !profiler.AutoSaveDue(1,false,true),"An idle pause produces exactly one automatic write.");
            profiler.AutoSaveDue(2,true,true);profiler.AutoSaveDue(2.1,true,false);
            Check(!profiler.AutoSaveDue(3,false,true),"Resuming playback cancels a pending pause write.");
            using (profiler.Measure(ProfileSection.Journal)) { }
            profiler.Save(profile);
            Check(Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(profile))["OperationsIncludingPausedEditor"]["Journal"]["Calls"].Value<long>() == 1,
                "Paused editing costs are measured separately from advancing frames");

            Console.WriteLine("PASS: " + checks + " input history, journal recovery, checkpoint and profiler checks.");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
