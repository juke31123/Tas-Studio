using System;
using System.Collections.Generic;

namespace Igtap.Tas.Core.Simulation
{
    internal sealed class SimulationAnimationEvent
    {
        public float Time { get; set; }
        public string Function { get; set; }
    }
    internal sealed class SimulationAnimationState
    {
        public int Parameter { get; set; }
        public int Hash { get; set; }
        public float Length { get; set; }
        public float Speed { get; set; } = 1;
        public bool Loop { get; set; }
        public SimulationAnimationEvent[] Events { get; set; } = Array.Empty<SimulationAnimationEvent>();
    }
    internal struct AnimationTimelineState
    {
        public int Parameter;
        public float Time;
    }
    // A declared one-layer, instantaneous integer-state controller. Definitions
    // come from native assets. Inputs are the candidate's own Animator writes.
    internal sealed class SimulationAnimationTransition
    {
        public int FromHash { get; set; }
        public int Parameter { get; set; }
        public int ToHash { get; set; }
    }
    internal sealed class AnimationTimeline
    {
        private readonly Dictionary<int, SimulationAnimationState> _states = new Dictionary<int, SimulationAnimationState>();
        private readonly Dictionary<int, SimulationAnimationState> _hashes = new Dictionary<int, SimulationAnimationState>();
        private readonly Dictionary<(int, int), int> _transitions = new Dictionary<(int, int), int>();
        private AnimationTimelineState _state;
        public AnimationTimelineState State => _state;
        public int StateHash => _states[_state.Parameter].Hash;
        public float NormalizedTime => _state.Time / _states[_state.Parameter].Length;
        public AnimationTimeline(SimulationAnimationState[] states, int initialParameter, float initialNormalizedTime, SimulationAnimationTransition[] transitions = null)
        {
            if (states == null || states.Length == 0 || !Finite(initialNormalizedTime) || initialNormalizedTime < 0) throw new ArgumentException("Invalid animation definition.");
            foreach (var state in states)
            {
                if (state == null || !Finite(state.Length) || state.Length <= 0 || !Finite(state.Speed) || state.Speed < 0 || state.Events == null || _states.ContainsKey(state.Parameter))
                    throw new ArgumentException("Invalid animation state.");
                float previous = -1;
                foreach (var signal in state.Events)
                {
                    if (signal == null || !Finite(signal.Time) || signal.Time < 0 || signal.Time < previous || signal.Time > state.Length || string.IsNullOrEmpty(signal.Function))
                        throw new ArgumentException("Invalid animation event.");
                    previous = signal.Time;
                }
                _states.Add(state.Parameter, state);
                _hashes.Add(state.Hash, state);
            }
            if (transitions != null)
                foreach (var transition in transitions)
                {
                    if (transition == null || !_hashes.ContainsKey(transition.FromHash) || !_hashes.ContainsKey(transition.ToHash) || !_states.ContainsKey(transition.Parameter))
                        throw new ArgumentException("Invalid animation transition.");
                    _transitions.Add((transition.FromHash, transition.Parameter), transition.ToHash);
                }
            if (!_states.TryGetValue(initialParameter, out var initial)) throw new ArgumentException("Unknown initial animation.");
            _state = new AnimationTimelineState { Parameter = initialParameter, Time = initialNormalizedTime * initial.Length };
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        public void Restore(AnimationTimelineState state)
        {
            if (!_states.ContainsKey(state.Parameter) || !Finite(state.Time) || state.Time < 0) throw new ArgumentException("Invalid animation snapshot.");
            _state = state;
        }
        public void Step(int parameter, float speed, float delta, Action<SimulationAnimationEvent> emit)
        {
            if (!_states.TryGetValue(parameter, out var target)) throw new NotSupportedException("Unexported animation parameter: " + parameter);
            int currentHash = _states[_state.Parameter].Hash;
            if (_transitions.Count != 0)
            {
                if (!_transitions.TryGetValue((currentHash, parameter), out int hash)) throw new NotSupportedException("Unexported animation transition.");
                target = _hashes[hash];
            }
            if (!Finite(delta) || !Finite(speed) || delta < 0 || speed < 0) throw new ArgumentException("Invalid animation clock.");
            if (_states[_state.Parameter].Hash != target.Hash)
            {
                // Unity evaluates the outgoing clip's final event interval even
                // when an instantaneous transition enters its destination at
                // time zero. Dropping that interval loses a footstep (and the
                // shared RNG draws it makes) when a jump interrupts running.
                Advance(_states[_state.Parameter], speed, delta, emit);
                _state = new AnimationTimelineState { Parameter = target.Parameter };
                foreach (var signal in target.Events) if (signal.Time == 0) emit?.Invoke(signal);
                return; // Native zero-duration transitions enter at time zero.
            }
            _state.Parameter = target.Parameter;
            _state.Time = Advance(target, speed, delta, emit);
        }
        private float Advance(SimulationAnimationState target, float speed, float delta, Action<SimulationAnimationEvent> emit)
        {
            float before = _state.Time, advance = delta * speed * target.Speed;
            float after = before + advance;
            if (!Finite(after)) throw new InvalidOperationException("Animation clock overflow.");
            if (after > before && target.Events.Length != 0)
            {
                int start = target.Loop ? (int)Math.Floor(before / target.Length) : 0;
                int end = target.Loop ? (int)Math.Floor(after / target.Length) : 0;
                if (end - start > 10000) throw new InvalidOperationException("Animation event interval exceeds safety limit.");
                for (int cycle = start; cycle <= end; cycle++)
                    foreach (var signal in target.Events)
                    {
                        double due = cycle * (double)target.Length + signal.Time;
                        if (due > before && due <= after) emit?.Invoke(signal);
                    }
            }
            return after;
        }
    }
}
