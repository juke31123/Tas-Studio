using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Igtap.Tas.Core.Search
{
    // Only filesystem/process work belongs here. The mod runs preparation on
    // a background task without accessing any live Unity objects.
    public sealed class EngineHostPackage
    {
        public sealed class PackageFile { public string Path = "", Sha256 = ""; }
        public sealed class PackageManifest
        {
            public int Version = 1, FrameAdapter = 1;
            public string ReferenceCommit = "", GameSha256 = "";
            public PackageFile[] Files = Array.Empty<PackageFile>();
        }
        private sealed class PreparedGame
        {
            public string PackageHash = "", GameSha256 = "", AssemblySha256 = "", ManifestSha256 = "";
        }
        private readonly string _directory, _dataDirectory, _managedDirectory;
        public string HostPath => Path.Combine(_directory, "Host", "TasStudio.EngineHost.exe");
        public EngineHostPackage(string directory, string dataDirectory, string managedDirectory)
        {
            _directory = Path.GetFullPath(directory);
            _dataDirectory = Path.GetFullPath(dataDirectory);
            _managedDirectory = Path.GetFullPath(managedDirectory);
        }
        public static string BundledPath(string dataDirectory) =>
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dataDirectory)), "Bruteforce");
        public static bool Exists(string dataDirectory) => Directory.Exists(BundledPath(dataDirectory));

        public PackageManifest Validate(CancellationToken cancellation)
        {
            var manifest = SearchFiles.Read<PackageManifest>(Path.Combine(_directory, "package.json"));
            if (manifest == null || manifest.Version != 1 || manifest.FrameAdapter != 1 ||
                !IsHash(manifest.GameSha256) || manifest.ReferenceCommit != "134d96c6011dd729e75f86f95d6fc16382538aff" ||
                manifest.Files == null || manifest.Files.Length < 1 || manifest.Files.Length > 2000)
                throw new InvalidDataException("Unsupported Bruteforce package. Install the complete current Tas Studio ZIP.");
            string game = Path.Combine(_managedDirectory, "Assembly-CSharp.dll");
            if (!File.Exists(game) || !SameHash(HashFile(game), manifest.GameSha256))
                throw new InvalidDataException("This Bruteforce package was built for a different game version. Install a matching Tas Studio update.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in manifest.Files)
            {
                cancellation.ThrowIfCancellationRequested();
                if (file == null || string.IsNullOrEmpty(file.Path) || !IsHash(file.Sha256) ||
                    file.Path.IndexOf('\\') >= 0 || file.Path.IndexOf(':') >= 0 || file.Path.StartsWith("/") ||
                    file.Path.EndsWith("/") || Array.Exists(file.Path.Split('/'), p => p == ".." || p == "." || p.Length == 0) ||
                    !names.Add(file.Path) || string.Equals(Path.GetFileName(file.Path), "Assembly-CSharp.dll", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Invalid file entry in Bruteforce package.");
                string path = Path.Combine(_directory, file.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path) || !SameHash(HashFile(path), file.Sha256))
                    throw new InvalidDataException("Missing or changed Bruteforce file: " + file.Path + ". Extract the complete ZIP again.");
            }
            foreach (string required in new[] {
                "Host/TasStudio.EngineHost.exe", "Host/TasStudio.EngineHost.dll", "Host/IGTAP.UnityShim.dll",
                "Host/IGTAP.EngineSim.Runtime.dll", "Host/IGTAP.Box2D.dll", "Host/hostpolicy.dll", "Host/coreclr.dll",
                "Prepare/IGTAP.EngineSim.Prep.exe", "Prepare/Mono.Cecil.dll", "Prepare/hostpolicy.dll", "Prepare/coreclr.dll",
                "cuts.txt", "hooks.txt", "static-allow.txt" })
                if (!names.Contains(required)) throw new InvalidDataException("Incomplete portable Bruteforce package: " + required);
            return manifest;
        }
        public string Prepare(CancellationToken cancellation)
        {
            var manifest = Validate(cancellation);
            string packageHash = SearchFiles.Hash(manifest), host = Path.GetDirectoryName(HostPath);
            string marker = Path.Combine(host, "prepared-game.json"), assembly = Path.Combine(host, "Assembly-CSharp.dll");
            try
            {
                if (File.Exists(marker) && File.Exists(assembly) && File.Exists(Path.Combine(host, "engine-host.manifest.json")))
                {
                    var ready = SearchFiles.Read<PreparedGame>(marker);
                    if (ready != null && ready.PackageHash == packageHash && SameHash(ready.GameSha256, manifest.GameSha256) &&
                        SameHash(ready.AssemblySha256, HashFile(assembly)) && SameHash(ready.ManifestSha256, HashFile(Path.Combine(host, "engine-host.manifest.json")))) return HostPath;
                }
            }
            catch (Exception error) when (error is IOException || error is Newtonsoft.Json.JsonException) { }
            cancellation.ThrowIfCancellationRequested();
            string staging = Path.Combine(_dataDirectory, "engine-setup", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            // The installed game is read only. Prep writes into this private
            // staging directory; publish the generated assembly only on success.
            RunPreparation(Path.Combine(_directory, "Prepare", "IGTAP.EngineSim.Prep.exe"), new[] {
                "retarget", _managedDirectory, staging, Path.Combine(_directory, "cuts.txt"), Path.Combine(_directory, "hooks.txt"),
                Path.Combine(host, "IGTAP.UnityShim.dll"), Path.Combine(_directory, "static-allow.txt") }, cancellation);
            string generated = Path.Combine(staging, "Assembly-CSharp.dll");
            if (!File.Exists(generated)) throw new InvalidDataException("Bruteforce setup did not generate the local game assembly.");
            cancellation.ThrowIfCancellationRequested();
            string temporary = Path.Combine(host, "Assembly-CSharp.dll.setup-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.Copy(generated, temporary);
                if (File.Exists(assembly)) File.Replace(temporary, assembly, null); else File.Move(temporary, assembly);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            SearchFiles.Write(Path.Combine(host, "engine-host.manifest.json"), new {
                manifest.ReferenceCommit, manifest.GameSha256, manifest.FrameAdapter, Redistributable = false });
            SearchFiles.Write(marker, new PreparedGame { PackageHash = packageHash, GameSha256 = manifest.GameSha256, AssemblySha256 = HashFile(assembly),
                ManifestSha256 = HashFile(Path.Combine(host, "engine-host.manifest.json")) });
            // Keep failed setup output for diagnosis. A successful staging
            // directory only holds generated files, so remove its files by name.
            foreach (var file in Directory.GetFiles(staging)) File.Delete(file);
            Directory.Delete(staging);
            return HostPath;
        }
        private static void RunPreparation(string executable, string[] arguments, CancellationToken cancellation)
        {
            var output = new StringBuilder(); var guard = new object();
            Action<string> record = value => { if (value != null) lock (guard) { if (output.Length < 24000) output.AppendLine(value); } };
            using (var process = new Process { StartInfo = new ProcessStartInfo {
                FileName = executable, Arguments = string.Join(" ", Array.ConvertAll(arguments, Quote)),
                WorkingDirectory = Path.GetDirectoryName(executable), UseShellExecute = false,
                CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true } })
            {
                process.OutputDataReceived += (sender, line) => record(line.Data);
                process.ErrorDataReceived += (sender, line) => record(line.Data);
                if (!process.Start()) throw new IOException("Could not start local Bruteforce setup.");
                try
                {
                    process.BeginOutputReadLine(); process.BeginErrorReadLine(); var time = Stopwatch.StartNew();
                    while (!process.WaitForExit(100))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (time.Elapsed.TotalMinutes > 5) throw new TimeoutException("Local Bruteforce setup timed out.");
                    }
                    process.WaitForExit(); cancellation.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0) throw new InvalidDataException("Local Bruteforce setup failed: " + output);
                }
                catch { if (!process.HasExited) { process.Kill(); process.WaitForExit(2000); } throw; }
            }
        }
        private static string Quote(string value)
        {
            if (value.IndexOf('"') >= 0) throw new ArgumentException("Invalid setup path.");
            return "\"" + value.TrimEnd('\\') + "\"";
        }
        public static string HashFile(string path)
        { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""); }
        private static bool SameHash(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        private static bool IsHash(string value) => value != null && value.Length == 64 && Array.TrueForAll(value.ToCharArray(), c => Uri.IsHexDigit(c));
    }
}
