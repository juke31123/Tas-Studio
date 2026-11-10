using System;

namespace Igtap.Tas.Core
{
    // Read the installed DLL so menu, desktop and loader versions cannot drift.
    public static class StudioVersion
    {
        public static readonly Version Number = Read();
        public static string Text => Number.ToString();

        private static Version Read()
        {
            var version = typeof(StudioVersion).Assembly.GetName().Version;
            return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        }
    }
}
