using System;
using System.IO;
using Igtap.Tas.Core.Search;

namespace Igtap.Tas.Runtime
{
    internal static class EngineHostLocation
    {
        private sealed class Registration { public string Executable=""; }
        public static string Find(string dataDirectory)
        {
            string path=Environment.GetEnvironmentVariable("TAS_STUDIO_ENGINE_HOST");
            if(string.IsNullOrWhiteSpace(path))
            {
                // A portable release takes precedence over a stale developer
                // registration copied alongside an existing data directory.
                if (EngineHostPackage.Exists(dataDirectory))
                    return Path.Combine(EngineHostPackage.BundledPath(dataDirectory), "Host", "TasStudio.EngineHost.exe");
                string file=Path.Combine(dataDirectory,"engine-host.json");
                if(!File.Exists(file)) return null;
                path=SearchFiles.Read<Registration>(file)?.Executable;
            }
            if(string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || !string.Equals(Path.GetFileName(path),"TasStudio.EngineHost.exe",StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The local engine-host registration needs the absolute TasStudio.EngineHost.exe path.");
            return Path.GetFullPath(path);
        }
    }
}
