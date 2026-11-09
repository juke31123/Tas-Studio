using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Igtap.Tas.Core.Simulation;
using UnityEngine;

namespace IGTAP.EngineSim
{
    public sealed partial class Simulation
    {
        private AnimationTimeline _studioAnimation;
        private Action _studioFootstep;
        private Action<SimulationAnimationEvent> _studioAnimationSignal;
        public int StudioAnimationHash => _studioAnimation?.StateHash ?? 0;
        public float StudioAnimationNormalizedTime => _studioAnimation?.NormalizedTime ?? 0;
        public void ConfigureStudioAnimation(JsonElement definition)
        {
            if (definition.GetProperty("version").GetInt32() != 1 || definition.GetProperty("updateMode").GetInt32() != 0 || definition.GetProperty("cullingMode").GetInt32() != 0)
                throw new NotSupportedException("Unsupported player Animator update/culling mode.");
            if (!definition.GetProperty("enabled").GetBoolean() || !definition.GetProperty("fireEvents").GetBoolean())
                throw new NotSupportedException("Disabled player Animator/event mode requires explicit support.");
            if (definition.GetProperty("layerCount").GetInt32() != 1 || definition.GetProperty("parameters").EnumerateArray().Any(p => p.GetProperty("name").GetString() != "Animation" || p.GetProperty("type").GetInt32() != 3))
                throw new NotSupportedException("Only the exported single integer animation controller is supported.");
            var clips = definition.GetProperty("clips").EnumerateArray().ToDictionary(c => c.GetProperty("id").GetInt32());
            var states = new List<SimulationAnimationState>();
            foreach (var source in definition.GetProperty("states").EnumerateArray())
            {
                var linked = source.GetProperty("clips").EnumerateArray().ToArray();
                if (linked.Length != 1 || linked[0].GetProperty("weight").GetSingle() != 1 || source.GetProperty("transition").GetBoolean())
                    throw new NotSupportedException("Animation blending requires explicit support.");
                var clip = clips[linked[0].GetProperty("id").GetInt32()];
                var signals = new List<SimulationAnimationEvent>();
                foreach (var signal in clip.GetProperty("events").EnumerateArray())
                {
                    string function = signal.GetProperty("function").GetString();
                    if (function != "RelayFootstep" || signal.GetProperty("hasObject").GetBoolean() || signal.GetProperty("float").GetSingle() != 0 || signal.GetProperty("int").GetInt32() != 0 || signal.GetProperty("string").GetString() != "")
                        throw new NotSupportedException("Unsupported animation callback: " + function);
                    signals.Add(new SimulationAnimationEvent { Time = signal.GetProperty("time").GetSingle(), Function = function });
                }
                states.Add(new SimulationAnimationState { Parameter = source.GetProperty("parameter").GetInt32(), Hash = source.GetProperty("stateHash").GetInt32(),
                    Length = clip.GetProperty("length").GetSingle(), Loop = clip.GetProperty("loop").GetBoolean(),
                    Speed = source.GetProperty("stateSpeed").GetSingle() * source.GetProperty("speedMultiplier").GetSingle(), Events = signals.ToArray() });
            }
            var transitions = new List<SimulationAnimationTransition>();
            foreach (var transition in definition.GetProperty("transitions").EnumerateArray())
            {
                int target = transition.GetProperty("to").GetInt32();
                if (transition.GetProperty("transition").GetBoolean()) throw new NotSupportedException("Player animation transitions blend.");
                int actual = transition.GetProperty("stateHash").GetInt32();
                if (actual != transition.GetProperty("lateStateHash").GetInt32() || transition.GetProperty("lateTransition").GetBoolean())
                    throw new NotSupportedException("Time-dependent animation transitions require explicit support.");
                int from = states.Single(s => s.Parameter == transition.GetProperty("from").GetInt32()).Hash;
                if (actual != from && actual != states.Single(s => s.Parameter == target).Hash) throw new NotSupportedException("Player animation transition selects an unmodelled intermediate state.");
                transitions.Add(new SimulationAnimationTransition { FromHash = from, Parameter = target, ToHash = actual });
            }
            var initial = definition.GetProperty("initial");
            if (initial.GetProperty("transition").GetBoolean()) throw new NotSupportedException("Cannot import an active animation blend.");
            int parameter = initial.GetProperty("parameter").GetInt32();
            if (states.Single(s => s.Parameter == parameter).Hash != initial.GetProperty("stateHash").GetInt32()) throw new NotSupportedException("Player animation parameter has a pending transition at capture.");
            _studioAnimation = new AnimationTimeline(states.ToArray(), parameter, initial.GetProperty("normalizedTime").GetSingle(), transitions.ToArray());
            Player.animator.SetInteger("Animation", parameter); Player.animator.speed = initial.GetProperty("speed").GetSingle();
            var proxy = Player.animator.GetComponent<PlayerAnimationEventProxy>();
            if (proxy == null) throw new NotSupportedException("Animation event receiver is missing from the exported world.");
            var method = typeof(PlayerAnimationEventProxy).GetMethod("RelayFootstep", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null)
                ?? throw new MissingMethodException("PlayerAnimationEventProxy.RelayFootstep");
            _studioFootstep = (Action)Delegate.CreateDelegate(typeof(Action), proxy, method);
            _studioAnimationSignal = _ => _studioFootstep();
        }
        private void StepStudioAnimation(float delta) => _studioAnimation?.Step(Player.animator.GetInteger("Animation"), Player.animator.speed, delta, _studioAnimationSignal);
    }
}
