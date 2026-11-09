using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Igtap.Tas.Core
{
    // Witness cache, not a native-memory dump. Unity has no supported contact/
    // coroutine snapshot API. Only a matching STILL-LIVE checkpoint can be reused;
    // an older checkpoint remains a replay verification target, never a teleport.
    public sealed class RebuildCheckpoints
    {
        public const int Capacity = 32;
        private readonly SortedDictionary<int, WorldWitness> _worlds = new SortedDictionary<int, WorldWitness>();
        public int Count => _worlds.Count;
        public void Clear() => _worlds.Clear();
        public void Remember(int frame, WorldWitness world)
        {
            world.Validate(); _worlds[frame] = world;
            while (_worlds.Count > Capacity) { using var keys = _worlds.Keys.GetEnumerator(); keys.MoveNext(); _worlds.Remove(keys.Current); }
        }
        public void InvalidateFrom(int frame)
        {
            var remove = new List<int>();
            foreach (int key in _worlds.Keys) if (key > frame) remove.Add(key);
            foreach (int key in remove) _worlds.Remove(key);
        }
        public string LiveDifference(int frame, WorldWitness actual) => _worlds.TryGetValue(frame, out var expected) ? expected.Difference(actual) : "No live checkpoint";
        public static bool SamePrefix(Movie live, Movie edited, int frame)
        {
            if (frame < 0 || frame > live.Frames.Count || frame > edited.Frames.Count ||
                live.FormatVersion != edited.FormatVersion || live.FrameRate != edited.FrameRate || live.FixedDeltaTime != edited.FixedDeltaTime ||
                live.TimingProtocol != edited.TimingProtocol || live.BootstrapVersion != edited.BootstrapVersion || live.WarmupFrames != edited.WarmupFrames ||
                live.GameHash != edited.GameHash || live.ModSetHash != edited.ModSetHash || live.UnityVersion != edited.UnityVersion || live.SaveHash != edited.SaveHash ||
                live.Scene != edited.Scene || live.Difficulty != edited.Difficulty || live.Seed != edited.Seed || live.StartPuPhase != edited.StartPuPhase ||
                live.AtomDoorPresent != edited.AtomDoorPresent || live.AtomDoorValue != edited.AtomDoorValue ||
                live.WarmupFixedTicks.Count != edited.WarmupFixedTicks.Count ||
                JsonConvert.SerializeObject(live.StartState) != JsonConvert.SerializeObject(edited.StartState) ||
                JsonConvert.SerializeObject(live.StartWorld) != JsonConvert.SerializeObject(edited.StartWorld) ||
                JsonConvert.SerializeObject(live.StartSetup) != JsonConvert.SerializeObject(edited.StartSetup)) return false;
            for (int i = 0; i < live.WarmupFixedTicks.Count; i++) if (live.WarmupFixedTicks[i] != edited.WarmupFixedTicks[i]) return false;
            for (int i = 0; i < frame; i++) if (!InputEdit.Same(live.Frames[i].Input, edited.Frames[i].Input)) return false;
            return true;
        }
    }
}
