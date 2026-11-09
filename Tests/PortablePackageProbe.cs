using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Igtap.Tas.Core.Search;
using Newtonsoft.Json.Linq;

internal static class PortablePackageProbe
{
    private static int Main(string[] arguments)
    {
        if (arguments.Length != 4) { Console.Error.WriteLine("Usage: <clean-Bruteforce-folder> <temporary-data-folder> <native-Managed-folder> <native-search-job.json>"); return 2; }
        try
        {
            string root = Path.GetFullPath(arguments[0]), data = Path.GetFullPath(arguments[1]), managed = Path.GetFullPath(arguments[2]);
            string game = Path.Combine(managed, "Assembly-CSharp.dll"), gameHash = EngineHostPackage.HashFile(game);
            string host = Path.Combine(root, "Host"), assembly = Path.Combine(host, "Assembly-CSharp.dll");
            if (File.Exists(assembly)) throw new Exception("Use a fresh extracted bundle without generated game code.");
            // These are process-local settings. Child tools must use the
            // bundled runtimes even when normal .NET discovery is unavailable.
            Environment.SetEnvironmentVariable("DOTNET_ROOT", Path.Combine(data, "no-global-dotnet"));
            Environment.SetEnvironmentVariable("DOTNET_ROOT_X64", Path.Combine(data, "no-global-dotnet"));
            Environment.SetEnvironmentVariable("DOTNET_MULTILEVEL_LOOKUP", "0");
            foreach (string file in new[] { "Host/TasStudio.EngineHost.runtimeconfig.json", "Prepare/IGTAP.EngineSim.Prep.runtimeconfig.json" })
            {
                var config = JObject.Parse(File.ReadAllText(Path.Combine(root, file)));
                if (config.SelectToken("runtimeOptions.includedFrameworks") == null || config.SelectToken("runtimeOptions.framework") != null)
                    throw new Exception("Executable is not self-contained: " + file);
            }
            var package = new EngineHostPackage(root, data, managed);
            string executable = package.Prepare(CancellationToken.None);
            Console.WriteLine("PASS: first-use preparation with global runtime discovery disabled.");
            string generatedHash = EngineHostPackage.HashFile(assembly); var written = File.GetLastWriteTimeUtc(assembly);
            package.Prepare(CancellationToken.None);
            if (written != File.GetLastWriteTimeUtc(assembly)) throw new Exception("An unchanged installation rebuilt the game.");
            Console.WriteLine("PASS: subsequent setup reuses the validated generated assembly.");
            File.AppendAllText(assembly, "corrupted local cache");
            package.Prepare(CancellationToken.None);
            if (EngineHostPackage.HashFile(assembly) != generatedHash) throw new Exception("Generated game assembly was not repaired.");
            File.WriteAllText(Path.Combine(host, "engine-host.manifest.json"), "damaged completion manifest");
            package.Prepare(CancellationToken.None);
            Console.WriteLine("PASS: changed generated code and completion manifest trigger repair.");
            Run(executable, "self-check", host);
            Console.WriteLine("PASS: isolated portable host compatibility checks.");

            string source = Path.GetDirectoryName(Path.GetFullPath(arguments[3])), copy = Path.Combine(data, "search-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(copy, "engine"));
            foreach (string name in new[] { "native.trace.json", "start.json", "world.json", "animation.json" })
                File.Copy(Path.Combine(source, "engine", name), Path.Combine(copy, "engine", name));
            var job = SearchFiles.Read<SearchJob>(Path.Combine(source, "job.json"));
            job.Options.Mode = "wave"; job.Options.Seconds = 5; job.Options.Workers = 2;
            SearchFiles.Write(Path.Combine(copy, "job.json"), job);
            Run(executable, "solve \"" + Path.Combine(copy, "job.json") + "\"", host);
            var result = SearchFiles.Read<SearchResult>(Path.Combine(copy, "result.json"));
            if (!result.Finished || result.Simulator != EngineSearchSpec.Backend || result.Inputs.Length == 0)
                throw new Exception("Isolated search produced no candidate: " + result.Status + " / " + result.Message);
            if (EngineHostPackage.HashFile(game) != gameHash) throw new Exception("Native game assembly changed.");
            SearchFiles.Write(Path.Combine(data, "portable-validation.json"), new { Passed = true, Algorithm = result.Algorithm,
                CandidateFrames = result.Inputs.Length, result.Evaluated, result.SimulatedFrames, result.Pus, result.Seconds, NativeGameUnchanged = true,
                Scope = "Portable setup, cache repair and external search. Does not replace in-game candidate verification.", SearchDirectory = copy });
            Console.WriteLine("PASS: isolated " + result.Algorithm + " search found " + result.Inputs.Length + " input frames; native files unchanged.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Run(string executable, string arguments, string workingDirectory)
    {
        using var process = Process.Start(new ProcessStartInfo { FileName = executable, Arguments = arguments, WorkingDirectory = workingDirectory,
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }) ?? throw new IOException("Portable executable did not start.");
        if (!process.WaitForExit(120000)) { process.Kill(); throw new TimeoutException("Portable executable timed out."); }
        if (process.ExitCode != 0) throw new IOException("Portable executable failed: " + arguments + " (" + process.ExitCode + ").");
    }
}
