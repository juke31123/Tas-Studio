using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Igtap.Tas.Core.Search;

internal static class EnginePackageTests
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool value, string why) { if (!value) throw new Exception(why); checks++; }
        void Reject(Action action, string why)
        { try { action(); } catch (InvalidDataException) { checks++; return; } throw new Exception(why); }
        string directory = Path.Combine(Path.GetTempPath(), "tas-portable-check-" + Guid.NewGuid().ToString("N"));
        string mod = Path.Combine(directory, "Tas.Studio"), data = Path.Combine(mod, "data"), managed = Path.Combine(directory, "Managed");
        string root = EngineHostPackage.BundledPath(data), manifestPath = Path.Combine(root, "package.json");
        Check(!EngineHostPackage.Exists(data), "A normal developer installation does not pretend to have a portable bundle.");
        Directory.CreateDirectory(root); Directory.CreateDirectory(managed);
        string game = Path.Combine(managed, "Assembly-CSharp.dll"); File.WriteAllText(game, "native game stays unchanged");
        var package = new EngineHostPackage(root, data, managed);
        var files = new List<EngineHostPackage.PackageFile>();
        foreach (var name in new[] { "Host/TasStudio.EngineHost.exe", "Host/TasStudio.EngineHost.dll", "Host/IGTAP.UnityShim.dll",
            "Host/IGTAP.EngineSim.Runtime.dll", "Host/IGTAP.Box2D.dll", "Host/hostpolicy.dll", "Host/coreclr.dll",
            "Prepare/IGTAP.EngineSim.Prep.exe", "Prepare/Mono.Cecil.dll", "Prepare/hostpolicy.dll", "Prepare/coreclr.dll",
            "cuts.txt", "hooks.txt", "static-allow.txt" })
        {
            string path = Path.Combine(root, name); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, name);
            files.Add(new EngineHostPackage.PackageFile { Path = name, Sha256 = EngineHostPackage.HashFile(path) });
        }
        EngineHostPackage.PackageManifest Fresh() => new EngineHostPackage.PackageManifest {
            ReferenceCommit = "134d96c6011dd729e75f86f95d6fc16382538aff", GameSha256 = EngineHostPackage.HashFile(game), Files = files.ToArray() };
        void Save(EngineHostPackage.PackageManifest value) => SearchFiles.Write(manifestPath, value);
        try
        {
            Check(EngineHostPackage.Exists(data), "A partial portable install is detected so it cannot silently select another solver.");
            var manifest = Fresh(); Save(manifest);
            Check(EngineHostPackage.Exists(data), "Portable package discovery is relative to the mod, never a developer absolute path.");
            Check(package.Validate(CancellationToken.None).Files.Length == files.Count, "A complete matching portable package validates.");
            Check(package.HostPath == Path.Combine(root, "Host", "TasStudio.EngineHost.exe"), "Portable executable stays in this mod installation.");
            manifest.GameSha256 = new string('0', 64); Save(manifest);
            Reject(() => package.Validate(CancellationToken.None), "A different native game build must fail before executing preparation.");
            manifest = Fresh(); manifest.ReferenceCommit = new string('0', 40); Save(manifest);
            Reject(() => package.Validate(CancellationToken.None), "An unsupported reference runtime is rejected.");
            manifest = Fresh(); manifest.Files = new[] { files[0] }; Save(manifest);
            Reject(() => package.Validate(CancellationToken.None), "Partial installs cannot silently fall back to the approximate solver.");
            foreach (string bad in new[] { "../outside.dll", "/outside.dll", "C:/outside.dll", "Host/../bad.dll", "Host\\bad.dll", "Host//bad.dll", "Host/Assembly-CSharp.dll" })
            {
                manifest = Fresh(); var entries = new List<EngineHostPackage.PackageFile>(files);
                entries.Add(new EngineHostPackage.PackageFile { Path = bad, Sha256 = new string('0', 64) }); manifest.Files = entries.ToArray(); Save(manifest);
                Reject(() => package.Validate(CancellationToken.None), "Unsafe or generated package entry rejected: " + bad);
            }
            manifest = Fresh(); var duplicate = new List<EngineHostPackage.PackageFile>(files);
            duplicate.Add(new EngineHostPackage.PackageFile { Path = files[0].Path.ToUpperInvariant(), Sha256 = files[0].Sha256 });
            manifest.Files = duplicate.ToArray(); Save(manifest);
            Reject(() => package.Validate(CancellationToken.None), "Windows case-insensitive duplicate entries are rejected.");
            Save(Fresh()); File.AppendAllText(Path.Combine(root, files[0].Path), "tampered");
            Reject(() => package.Validate(CancellationToken.None), "Changing a bundled executable invalidates the package.");
            File.WriteAllText(Path.Combine(root, files[0].Path), files[0].Path);
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                try { package.Prepare(cancelled.Token); throw new Exception("Cancelled setup ran."); }
                catch (OperationCanceledException) { checks++; }
            }
            Check(File.ReadAllText(game) == "native game stays unchanged", "Validation and cancelled setup never modify the installed game.");
            Check(!File.Exists(Path.Combine(root, "Host", "Assembly-CSharp.dll")), "No generated game assembly is published after a cancelled setup.");
        }
        finally
        {
            // The unique test tree contains only files created above.
            foreach (var path in Directory.GetFiles(directory, "*", SearchOption.AllDirectories)) File.Delete(path);
            var directories = Directory.GetDirectories(directory, "*", SearchOption.AllDirectories);
            Array.Sort(directories, (a, b) => b.Length.CompareTo(a.Length));
            foreach (var path in directories) Directory.Delete(path);
            Directory.Delete(directory);
        }
        Console.WriteLine("Portable engine package checks passed: " + checks);
    }
}
