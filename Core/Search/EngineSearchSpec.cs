using System;
using System.IO;

namespace Igtap.Tas.Core.Search
{
    // File identities only. Engine objects and live worker snapshots never
    // cross the process boundary or appear in a TAS/search JSON file.
    public sealed class EngineSearchSpec
    {
        public const string Backend="game-code-experimental-v1";
        public string TraceHash="", StartHash="", WorldHash="", AnimationHash="";
        public void Validate()
        {
            foreach(string hash in new[] {TraceHash,StartHash,WorldHash,AnimationHash})
            {
                byte[] bytes;
                try { bytes=Convert.FromBase64String(hash ?? ""); } catch(FormatException) { throw new InvalidDataException("Invalid engine capture hash."); }
                if(bytes.Length!=32) throw new InvalidDataException("Missing engine capture identity.");
            }
        }
    }
}
