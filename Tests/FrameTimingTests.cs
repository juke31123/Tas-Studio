using System;
using Igtap.Tas.Core;

internal static class FrameTimingTests
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool value, string text) { if (!value) throw new Exception(text); checks++; }
        foreach (int fps in new[] { 30, 50, 60, 75, 100, 120, 144, 165, 240, 280, 360 })
        {
            var clock = new FrameTiming(fps);
            double start = clock.Phase;
            int zero = 0, multiple = 0;
            for (int i = 0; i < fps * 100; i++)
            {
                int wait = FrameTiming.FramesUntilPu(clock.Phase, fps, 1);
                int pus = clock.Advance(1);
                if ((wait == 1) != (pus > 0)) throw new Exception("PU countdown disagrees with frame at " + fps + " FPS");
                if (pus == 0) zero++; if (pus > 1) multiple++;
            }
            Check(clock.Pus == 5000 && Math.Abs(clock.Phase - start) < 1e-8, fps + " FPS preserves 50 Hz across non-integer ratios");
            Check(fps <= 50 || zero > 0, fps + " FPS includes frames between PUs");
            Check(fps >= 50 || multiple > 0, fps + " FPS permits multiple PUs per frame");
            for (int i = 0; i < 100; i++) clock.Advance(0);
            Check(clock.Pus == 5000 && Math.Abs(clock.Phase - start) < 1e-8 && FrameTiming.FramesUntilPu(clock.Phase, fps, 0) == -1,
                "Hitstop retains phase and has no invented countdown");
        }
        var slow = new FrameTiming(100);
        for (int i = 0; i < 100; i++) slow.Advance(.5);
        Check(slow.Pus == 25, "Game timeScale is respected independently of viewing speed");
        var tap = new FrameInput { ExactEdges = true, Press = Buttons.Jump, Release = Buttons.Jump };
        Check(tap.Pressed(Buttons.Jump, default) && tap.Released(Buttons.Jump, default) && !tap.Down(Buttons.Jump), "A press and release in one input update survives capture");
        var movie = new Movie { BootstrapVersion = 4, FrameRate = 280, StartPuPhase = .001 };
        movie.Frames.Add(new MovieFrame { Input = tap, FixedTicks = 0, PuPhase = .002 });
        var copy = MovieStore.Clone(movie);
        Check(copy.FrameRate == 280 && copy.Frames[0].Input.ExactEdges && copy.Frames[0].FixedTicks == 0 && copy.Frames[0].PuPhase == .002, "Movie round trip retains high FPS, edges and phase");
        Timeline.Replace(copy, 0, 1, default);
        Check(copy.Frames[0].FixedTicks == -1 && copy.Frames[0].PuPhase == -1, "Edits invalidate PU observations");
        movie.FixedDeltaTime = .01f;
        try { movie.Validate(); throw new Exception("Non-50 Hz movie accepted"); } catch (InvalidOperationException) { checks++; }
        var observed = new SimulationTimers(); var owner = new object();
        observed.ObserveNative(owner, "buffer", .03, .015);
        observed.Advance(.04);
        Check(Math.Abs(observed.Remaining(owner, "buffer") - .005) < 1e-9, "Native deadline uses scheduling PU time, not the end of its render frame");
        observed.Advance(.02);
        Check(observed.Contains(owner, "buffer"), "Observation never dispatches or expires a Unity-owned callback");
        observed.NativeCalled(owner, "buffer");
        Check(!observed.Contains(owner), "Unity callback clears its observation");
        var predictor = new MovementPredictor();
        var motion = new PlayerMotion { PhysicsSimulated = true, WallJumpLock = -1, DashFramesRemaining = -1, DashBounceFrames = -1 };
        var parameters = new MovementParameters { FramesToTopSpeed = 2, RunSpeed = 20 };
        var prediction = predictor.Predict(motion, parameters, new FrameInput { X = 1 }, default, .02f, .02f, 0);
        Check(prediction.Available && prediction.Next.Velocity.X == 0, "New dynamic axis cannot affect an earlier PU in the same frame");
        Check(ProbeComparer.Difference(new StateProbe(), new StateProbe { RenderX = float.NaN }) == "Non-finite player state", "Replay rejects invalid interpolated positions");
        Check(ProbeComparer.Difference(new StateProbe(), new StateProbe { BodyVelocityY = float.PositiveInfinity }) == "Non-finite player state", "Replay rejects invalid native velocities");
        Console.WriteLine("PASS: " + checks + " frame/PU timing and format checks.");
    }
}
