using System;
using System.Collections.Generic;
using Igtap.Tas.Core.Simulation;

internal static class AnimationTimelineTests
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool result, string description) { if (!result) throw new Exception(description); checks++; }
        void Reject(Action action, string description) { try { action(); } catch (ArgumentException) { checks++; return; } throw new Exception(description); }
        SimulationAnimationState[] States() => new[] {
            new SimulationAnimationState {Parameter=0, Hash=10, Length=1, Loop=true},
            new SimulationAnimationState {Parameter=1, Hash=11, Length=1, Loop=true, Events=new[] {
                new SimulationAnimationEvent {Time=.25f, Function="left"}, new SimulationAnimationEvent {Time=.75f, Function="right"}}},
            new SimulationAnimationState {Parameter=2, Hash=12, Length=1, Events=new[] {new SimulationAnimationEvent {Time=.5f, Function="once"}}}
        };
        var timeline = new AnimationTimeline(States(), 0, 0); var events = new List<string>();
        void Emit(SimulationAnimationEvent signal) => events.Add(signal.Function);
        timeline.Step(1, 1, 1, Emit);
        Check(timeline.State.Time == 0 && events.Count == 0, "Entering an instantaneous native state starts at time zero.");
        timeline.Step(1, 1, .5f, Emit);
        Check(events.Count == 1 && events[0] == "left", "Events fire when the candidate's own animation crosses the asset timestamp.");
        var snapshot = timeline.State;
        timeline.Step(1, 1, .5f, Emit);
        Check(events.Count == 2 && events[1] == "right", "Later clip event preserves order.");
        timeline.Step(1, 1, 1f, Emit);
        Check(events.Count == 4 && events[2] == "left" && events[3] == "right", "Loops dispatch both events once per cycle.");
        timeline.Restore(snapshot); events.Clear(); timeline.Step(1, 1, .5f, Emit);
        Check(events.Count == 1 && events[0] == "right", "Restored branch fires the same future event.");
        timeline.Restore(snapshot); events.Clear(); timeline.Step(0, 1, 1, Emit); timeline.Step(0, 1, 1, Emit);
        Check(events.Count == 0, "A different candidate that stops running does not inherit the reference's footstep times.");
        timeline.Restore(snapshot); events.Clear(); timeline.Step(1, 0, 5, Emit); timeline.Step(1, 1, 0, Emit);
        Check(timeline.State.Time == snapshot.Time && events.Count == 0, "Frozen scaled animation time emits no events.");
        timeline.Restore(snapshot); timeline.Step(1, 2, .25f, Emit);
        Check(events.Count == 1 && events[0] == "right", "Candidate animation speed changes the event deadline.");
        timeline.Step(2, 1, 0, Emit); events.Clear(); timeline.Step(2, 1, 3, Emit); timeline.Step(2, 1, 3, Emit);
        Check(events.Count == 1 && events[0] == "once", "Non-looping clips do not repeat events after finishing.");
        var graph = new AnimationTimeline(States(), 0, 0, new[] {
            new SimulationAnimationTransition {FromHash=10,Parameter=1,ToHash=10},
            new SimulationAnimationTransition {FromHash=10,Parameter=2,ToHash=12}});
        graph.Step(1, 1, .5f, Emit);
        Check(graph.StateHash == 10 && graph.State.Time == .5f, "Controller graph can ignore a parameter without resetting its active state.");
        graph.Step(2, 1, .5f, Emit);
        Check(graph.StateHash == 12 && graph.State.Time == 0, "Controller graph selects the declared destination.");
        var bad = States(); bad[1].Events[0].Time = -.1f;
        Reject(() => new AnimationTimeline(bad, 0, 0), "Negative event time must fail.");
        Reject(() => timeline.Restore(new AnimationTimelineState {Parameter=999}), "Unknown snapshot state must fail.");
        Reject(() => timeline.Step(1, float.NaN, 1, Emit), "Nonfinite animation speed must fail.");
        Console.WriteLine("Animation timeline checks passed: " + checks);
    }
}
