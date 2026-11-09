using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Igtap.Tas.Core.Validation
{
    public sealed class GameplayInputWitness
    {
        public string Part = "";
        public int Frame, PhysicsUpdates;
        public FrameInput Input;
        public bool MenuOpen;
        public string[] ChangedFields = Array.Empty<string>();
    }

    public sealed class GameplayCaptureReport
    {
        public string Name = "", GameHash = "";
        public bool IntegrityPassed, SimulatorValidated;
        public bool? PreparedGameMatches;
        public int Parts, Frames, PhysicsUpdates, FramesWithoutPhysics, InputChangesWithoutPhysics, MenuPresses, SpringCalls, FramesWithDifferentPhysicsInput;
        public double UnscaledSeconds, MinFrameSeconds = double.MaxValue, MaxFrameSeconds;
        public string[] Warnings = Array.Empty<string>();
        public List<GameplayInputWitness> Inputs = new List<GameplayInputWitness>();
    }

    // Reads observations in place. In particular, this does not turn native
    // variable frame timing into a fixed-FPS movie or extract archive paths.
    public static class PassiveCaptureAudit
    {
        private const long MaxDecodedBytes = 512L * 1024 * 1024;
        private static readonly Regex Allowed = new Regex(@"^(environment\.json|part-[0-9]{4}/(world\.json|starting-state\.json|animation\.json|gameplay\.frames\.jsonl\.gz|contacts\.jsonl\.gz|contacts-status\.json))$", RegexOptions.CultureInvariant);
        public static GameplayCaptureReport Read(string path, string preparedGameHash = null)
        {
            using (var zip = ZipFile.OpenRead(path))
            {
                if (zip.Entries.Count > 450) throw new InvalidDataException("Too many capture entries.");
                var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
                long entryBytes = 0;
                foreach (var entry in zip.Entries)
                    if ((entryBytes += entry.Length) > MaxDecodedBytes || entry.Length < 0 ||
                        (entry.FullName != "capture.json" && !Allowed.IsMatch(entry.FullName)) || !entries.TryAdd(entry.FullName, entry))
                        throw new InvalidDataException("Unexpected or duplicate capture entry: " + entry.FullName);
                if (!entries.TryGetValue("capture.json", out var capture)) throw new InvalidDataException("Missing capture manifest.");
                var metadata = Json(ReadText(capture, 1024 * 1024));
                if ((int?)metadata["schema"] != 2 || (string)metadata["mode"] != "passive-gameplay" || (bool?)metadata["complete"] != true)
                    throw new InvalidDataException("A complete passive gameplay capture is required.");
                var files = metadata["files"] as JObject ?? throw new InvalidDataException("Missing capture hashes.");
                if (files.Count != entries.Count - 1) throw new InvalidDataException("Capture file manifest differs.");
                foreach (var file in files.Properties())
                {
                    if (!entries.TryGetValue(file.Name, out var entry) || (long?)file.Value["bytes"] != entry.Length)
                        throw new InvalidDataException("Capture size differs: " + file.Name);
                    using (var stream = entry.Open()) using (var hash = SHA256.Create())
                        if (Convert.ToBase64String(hash.ComputeHash(stream)) != (string)file.Value["sha256"])
                            throw new InvalidDataException("Capture hash differs: " + file.Name);
                }
                var report = new GameplayCaptureReport { Name = (string)metadata["name"] ?? "", GameHash = (string)metadata["gameHash"] ?? "" };
                var warnings = new List<string> { "Integrity and observed input effects are checked; complete offline simulation is not certified.",
                    "Initial native contact caches, elapsed sleep time and remaining Invoke/coroutine waits are not fully captured." };
                if (preparedGameHash != null)
                {
                    report.PreparedGameMatches = preparedGameHash == report.GameHash;
                    if (report.PreparedGameMatches == false) warnings.Add("The capture game assembly differs from the prepared simulator; this data cannot certify that build's replay parity.");
                }
                var parts = entries.Keys.Where(n => n.EndsWith("/gameplay.frames.jsonl.gz", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                if (parts.Length == 0 || parts.Length > 64) throw new InvalidDataException("No gameplay parts, or too many parts.");
                if (!(metadata["parts"] is JArray declared) || declared.Count != parts.Length)
                    warnings.Add("Part metadata is incomplete; part boundaries were read from the hashed journals.");
                long decoded = 0;
                int expected = 0, previousEngineFrame = -1;
                foreach (string journal in parts)
                {
                    string part = journal.Substring(0, journal.IndexOf('/'));
                    foreach (string required in new[] { "world.json", "starting-state.json", "animation.json", "contacts-status.json", "contacts.jsonl.gz" })
                        if (!entries.ContainsKey(part + "/" + required)) throw new InvalidDataException("Incomplete gameplay part: " + part);
                    var status = Json(ReadText(entries[part + "/contacts-status.json"], 1024 * 1024));
                    if ((int?)status["DroppedAtLimit"] != 0 || !string.IsNullOrEmpty((string)status["Error"])) warnings.Add(part + ": contact events were dropped or failed.");
                    FrameInput? previous = null;
                    ForLines(entries[journal], line =>
                    {
                        var frame = Json(line);
                        int index = RequiredInt(frame, "Frame"), engineFrame = RequiredInt(frame, "EngineFrame"), pus = RequiredInt(frame, "PhysicsUpdates");
                        var samples = frame["Samples"] as JArray;
                        if (index != expected++ || engineFrame <= previousEngineFrame || pus < 0 || pus > 42 || samples == null || samples.Count == 0 || samples.Count > 128)
                            throw new InvalidDataException("Invalid gameplay frame sequence.");
                        previousEngineFrame = engineFrame;
                        JObject update = null, end = null;
                        int before = 0, after = 0;
                        foreach (JObject sample in samples)
                        {
                            string phase = (string)sample["Phase"];
                            int pu = RequiredInt(sample, "Pu");
                            if (!(sample["Fields"] is JObject fields) || fields.Count > 20000) throw new InvalidDataException("Invalid observation fields.");
                            foreach (var field in fields.Properties()) if (field.Value.Type != JTokenType.String) throw new InvalidDataException("Invalid observation value.");
                            if (phase == "before-fixed-update") { if (before != after || pu != before + 1) throw new InvalidDataException("Invalid fixed-update order."); }
                            else if (phase == "before-physics") { if (before != after || pu != ++before) throw new InvalidDataException("Invalid before-physics order."); }
                            else if (phase == "after-physics") { if (before != after + 1 || pu != ++after) throw new InvalidDataException("Invalid after-physics order."); }
                            else if (phase == "before-input-update") { if (update != null || after != pus || pu != pus) throw new InvalidDataException("Invalid input-update order."); update = sample; }
                            else if (phase == "frame-end") { if (end != null || !ReferenceEquals(sample, samples.Last) || after != pus || pu != pus) throw new InvalidDataException("Invalid frame end."); end = sample; }
                            else throw new InvalidDataException("Unknown native observation phase: " + phase);
                            ReadInput(sample);
                        }
                        if (before != pus || after != pus || update == null || end == null) throw new InvalidDataException("Missing native observations.");
                        double dt = Float((string)end["Fields"]["clock/unscaled-delta"]);
                        if (dt < 0 || dt > 60) throw new InvalidDataException("Invalid native frame duration.");
                        report.UnscaledSeconds += dt; report.MinFrameSeconds = Math.Min(report.MinFrameSeconds, dt); report.MaxFrameSeconds = Math.Max(report.MaxFrameSeconds, dt);
                        report.Frames++; report.PhysicsUpdates += pus; if (pus == 0) report.FramesWithoutPhysics++;
                        var input = ReadInput(update);
                        if (samples.OfType<JObject>().Where(s => (string)s["Phase"] == "before-fixed-update").Any(s => !SameInput(ReadInput(s), input))) report.FramesWithDifferentPhysicsInput++;
                        bool changed = previous == null || input.X != previous.Value.X || input.Y != previous.Value.Y || input.Held != previous.Value.Held || input.Press != 0 || input.Release != 0;
                        if (changed)
                        {
                            if (pus == 0 && previous.HasValue) report.InputChangesWithoutPhysics++;
                            if ((input.Press & Buttons.Menu) != 0) report.MenuPresses++;
                            if (report.Inputs.Count >= 10000) throw new InvalidDataException("Too many input change witnesses.");
                            var startFields = (JObject)update["Fields"]; var endFields = (JObject)end["Fields"];
                            report.Inputs.Add(new GameplayInputWitness { Part = part, Frame = index, PhysicsUpdates = pus, Input = input, MenuOpen = (string)endFields["menu/open"] == "True",
                                ChangedFields = startFields.Properties().Where(p => (p.Name.StartsWith("movement/", StringComparison.Ordinal) || p.Name.StartsWith("body/", StringComparison.Ordinal)) && !JToken.DeepEquals(p.Value, endFields[p.Name])).Select(p => p.Name).ToArray() });
                        }
                        previous = input;
                    }, ref decoded);
                    int events = 0;
                    ForLines(entries[part + "/contacts.jsonl.gz"], line => { var e = Json(line); events++; if (((string)e["Method"] ?? "").StartsWith("Movement.", StringComparison.Ordinal) && ((string)e["Method"]).IndexOf("Spring", StringComparison.OrdinalIgnoreCase) >= 0) report.SpringCalls++; }, ref decoded);
                    if (events != (int?)status["Events"]) throw new InvalidDataException("Contact event count differs.");
                }
                if (report.Frames == 0 || report.Frames != (int?)metadata["frames"] || report.PhysicsUpdates != (int?)metadata["pus"]) throw new InvalidDataException("Capture totals differ.");
                report.Parts = parts.Length; report.Warnings = warnings.ToArray(); report.IntegrityPassed = true;
                return report;
            }
        }
        public static float Float(string bits)
        {
            if (bits == null || bits.Length != 8 || !uint.TryParse(bits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value)) throw new InvalidDataException("Missing or invalid float bits.");
            float number = BitConverter.ToSingle(BitConverter.GetBytes(value), 0);
            if (float.IsNaN(number) || float.IsInfinity(number)) throw new InvalidDataException("Nonfinite float.");
            return number;
        }
        public static double Double(string bits)
        {
            if (bits == null || bits.Length != 16 || !ulong.TryParse(bits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong value)) throw new InvalidDataException("Missing or invalid double bits.");
            double number=BitConverter.ToDouble(BitConverter.GetBytes(value),0);
            if(double.IsNaN(number) || double.IsInfinity(number)) throw new InvalidDataException("Nonfinite double.");
            return number;
        }
        public static GameplayCaptureReport Visit(string path, Action<string,JObject,JObject> beginPart, Action<string,JObject> frame)
        {
            using(var pinned=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
            var report=Read(path); // Hash and bound the entire input before loading game objects.
            using(var zip=new ZipArchive(pinned,ZipArchiveMode.Read,true))
            {
                long decoded=0;
                foreach(var entry in zip.Entries.Where(e=>e.FullName.EndsWith("/gameplay.frames.jsonl.gz",StringComparison.Ordinal)).OrderBy(e=>e.FullName,StringComparer.Ordinal))
                {
                    string part=entry.FullName.Substring(0,entry.FullName.IndexOf('/'));
                    beginPart(part,Json(ReadText(zip.GetEntry(part+"/world.json"),64L*1024*1024)),Json(ReadText(zip.GetEntry(part+"/starting-state.json"),4L*1024*1024)));
                    ForLines(entry,line=>frame(part,Json(line)),ref decoded);
                }
            }
            return report;
            }
        }
        private static FrameInput ReadInput(JObject sample)
        {
            var source = sample["Input"] as JObject ?? throw new InvalidDataException("Missing logical input.");
            var input = source.ToObject<FrameInput>();
            if (!Movie.Finite(input.X) || !Movie.Finite(input.Y) || Math.Abs(input.X) > 1 || Math.Abs(input.Y) > 1 || !input.ExactEdges || (((int)input.Held | (int)input.Press | (int)input.Release) & ~63) != 0)
                throw new InvalidDataException("Invalid logical action input.");
            return input;
        }
        private static bool SameInput(FrameInput a, FrameInput b) => a.X == b.X && a.Y == b.Y && a.Held == b.Held && a.Press == b.Press && a.Release == b.Release;
        private static int RequiredInt(JObject o, string key) => o[key]?.Type == JTokenType.Integer ? (int)o[key] : throw new InvalidDataException("Missing integer: " + key);
        private static JObject Json(string text) => JObject.Parse(text, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        private static string ReadText(ZipArchiveEntry entry, long limit)
        {
            if (entry.Length > limit) throw new InvalidDataException("Metadata too large.");
            using (var reader = new StreamReader(entry.Open(), Encoding.UTF8, true)) return reader.ReadToEnd();
        }
        private static void ForLines(ZipArchiveEntry entry, Action<string> action, ref long total)
        {
            if (entry.Length == 0) return; // An empty contact log has no gzip header.
            using (var gzip = new GZipStream(entry.Open(), CompressionMode.Decompress))
            using (var reader = new StreamReader(gzip, Encoding.UTF8, true, 8192))
            {
                var line = new StringBuilder(); var buffer = new char[8192]; int n;
                while ((n = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += n; if (total > MaxDecodedBytes) throw new InvalidDataException("Decoded capture exceeds the import limit.");
                    for (int i = 0; i < n; i++)
                        if (buffer[i] == '\n') { if (line.Length == 0) throw new InvalidDataException("Empty journal line."); action(line.ToString()); line.Clear(); }
                        else { line.Append(buffer[i]); if (line.Length > 8 * 1024 * 1024) throw new InvalidDataException("Observation frame too large."); }
                }
                if (line.Length != 0) throw new InvalidDataException("Truncated gameplay journal.");
            }
        }
    }
}
