using System;
using System.Collections.Generic;
using System.Linq;

namespace Igtap.Tas.Core
{
    // A session-local clock: never reads Unity's absolute time or wall time.
    // Advance once at the beginning of each complete simulation frame.
    public sealed class SimulationTimers
    {
        private sealed class Pending
        {
            public object Owner;
            public string Method;
            public double Due;
            public Action Callback;
        }
        private readonly List<Pending> _pending = new List<Pending>();
        public double Time { get; private set; }
        public void Schedule(object owner, string method, double delay, Action callback)
        {
            if (owner == null || string.IsNullOrEmpty(method) || callback == null) throw new ArgumentException("A timer requires an owner, method and callback.");
            if (double.IsNaN(delay) || double.IsInfinity(delay)) throw new ArgumentOutOfRangeException(nameof(delay));
            _pending.Add(new Pending { Owner = owner, Method = method, Due = Time + Math.Max(0, delay), Callback = callback });
        }
        public void Cancel(object owner, string method = null) => _pending.RemoveAll(p => ReferenceEquals(p.Owner, owner) && (method == null || p.Method == method));
        public bool Contains(object owner, string method = null) => _pending.Exists(p => ReferenceEquals(p.Owner, owner) && (method == null || p.Method == method));
        public void Advance(double scaledSeconds)
        {
            if (double.IsNaN(scaledSeconds) || double.IsInfinity(scaledSeconds) || scaledSeconds < 0) throw new ArgumentOutOfRangeException(nameof(scaledSeconds));
            if (scaledSeconds == 0) return; // Hitstop does not expire scaled timers.
            Time += scaledSeconds;
            // Stable ordering for equal deadlines. A snapshot prevents a callback
            // scheduling itself at zero delay from looping in this same frame.
            var due = _pending.Where(p => p.Due <= Time + 1e-7).OrderBy(p => p.Due).ToArray();
            foreach (var timer in due)
                if (_pending.Remove(timer)) timer.Callback(); // An earlier callback may cancel it.
        }
        public void Clear() { _pending.Clear(); Time = 0; }
        public void Capture(WorldWitness witness)
        {
            witness.Numbers["timers/time"] = Time;
            witness.Exact["timers/count"] = _pending.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            for (int i = 0; i < _pending.Count; i++)
            {
                witness.Exact["timers/" + i + "/method"] = _pending[i].Method;
                witness.Numbers["timers/" + i + "/due"] = _pending[i].Due;
            }
        }
    }
}
