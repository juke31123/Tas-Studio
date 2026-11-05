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
            public bool Native;
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
        // Observe Unity-owned Invoke timers without executing or expiring them.
        // Their callbacks stay in Unity's original player-loop phase.
        public void ObserveNative(object owner, string method, double delay, double now)
        {
            if (owner == null || string.IsNullOrEmpty(method) || double.IsNaN(delay) || double.IsInfinity(delay) || double.IsNaN(now) || double.IsInfinity(now))
                throw new ArgumentException("Invalid native timer observation.");
            _pending.Add(new Pending { Owner = owner, Method = method, Due = now + Math.Max(0, delay), Native = true });
        }
        public void NativeCalled(object owner, string method)
        {
            var pending = _pending.FirstOrDefault(p => p.Native && ReferenceEquals(p.Owner, owner) && p.Method == method);
            if (pending != null) _pending.Remove(pending);
        }
        public bool Contains(object owner, string method = null) => _pending.Exists(p => ReferenceEquals(p.Owner, owner) && (method == null || p.Method == method));
        public double Remaining(object owner, string method)
        {
            double remaining = double.PositiveInfinity;
            foreach (var timer in _pending)
                if (ReferenceEquals(timer.Owner, owner) && timer.Method == method) remaining = Math.Min(remaining, Math.Max(0, timer.Due - Time));
            return remaining;
        }
        public void Advance(double scaledSeconds)
        {
            if (double.IsNaN(scaledSeconds) || double.IsInfinity(scaledSeconds) || scaledSeconds < 0) throw new ArgumentOutOfRangeException(nameof(scaledSeconds));
            if (scaledSeconds == 0) return; // Hitstop does not expire scaled timers.
            Time += scaledSeconds;
            // Stable ordering for equal deadlines. A snapshot prevents a callback
            // scheduling itself at zero delay from looping in this same frame.
            var due = _pending.Where(p => !p.Native && p.Due <= Time + 1e-7).OrderBy(p => p.Due).ToArray();
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
