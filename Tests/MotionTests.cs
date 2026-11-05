using System;
using Igtap.Tas.Core;

internal static class MotionTests
{
    private static int _checks;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); _checks++; }
    private static bool Near(float a, float b) => Math.Abs(a - b) < .0001f;
    private static PlayerMotion State() => new PlayerMotion {
        Velocity = new MotionVector(0, 100), ControlVelocity = new MotionVector(0, 100),
        WallJumpLock = -1, WallCoyoteFrames = -1, DashFramesRemaining = -1, DashBounceFrames = -1, NormalJump = true
    };
    private static MovementParameters Parameters() => new MovementParameters {
        RunSpeed = 20, FramesToTopSpeed = 2, Gravity = 10, FastFallMultiplier = 2, MaxDownwardsSpeed = 100,
        JumpForce = 300, AirJumpForce = 250, MaxWallCoyoteFrames = 3, DoubleJumpUnlocked = true, DashUnlocked = true,
        DashSpeed = 60, DashFrames = 10, DashDroopBaseline = .5f, DashDroopIntensity = .1f, WallJumpLockFrames = 10
    };
    public static void Run()
    {
        var engine = new MovementPredictor();
        var p = Parameters(); var s = State();
        var right = new FrameInput { X = 1 };
        var jump = new FrameInput { Held = Buttons.Jump };
        var result = engine.Predict(s, p, right, right, .02f, .02f, 0);
        Check(result.Available && result.PhysicsTicks == 1, "one normal 50 Hz frame");
        Check(Near(result.Next.Position.X, 2) && Near(result.Next.Position.Y, 1.8f), "position integrates predicted movement velocity");
        Check(Near(result.Next.Velocity.X, 100) && Near(result.Next.Velocity.Y, 90), "game gravity and horizontal acceleration applied");
        Check(s.Position.X == 0 && s.ControlVelocity.Y == 100 && p.Gravity == 10, "prediction does not mutate input snapshots or parameters");
        Check(result.Next.State == PlayerMotionState.Jumping, "upward motion classified as jumping");
        s.ControlVelocity.Y = s.Velocity.Y = -100;
        result = engine.Predict(s, p, default, default, .02f, .02f, 0);
        Check(Near(result.Next.Velocity.Y, -120) && result.Next.State == PlayerMotionState.Falling, "fast fall and falling state");
        result = engine.Predict(s, p, new FrameInput { Y = -1 }, new FrameInput { Y = -1 }, .02f, .02f, 0);
        Check(Near(result.Next.Velocity.Y, -128), "down input increases fall gravity");
        s = State(); s.Grounded = true; s.ControlVelocity.Y = s.Velocity.Y = 0;
        result = engine.Predict(s, p, default, default, .02f, .02f, 0);
        Check(result.Next.State == PlayerMotionState.Grounded && result.Next.Position.Y == 0, "unchanged floor assumption holds body at ground");
        result = engine.Predict(s, p, jump, default, .02f, .02f, 0);
        Check(result.Next.Grounded && result.Next.JumpBuffered, "new jump edge buffers after current frame's physics");
        var second = engine.Predict(result.Next, p, jump, jump, .02f, .02f, 0);
        Check(second.Next.State == PlayerMotionState.Jumping && Near(second.Next.Position.Y, 11) && second.Next.Velocity.Y == 300, "buffered ground jump applies impulse and five-unit lift");
        result = engine.Predict(s, p, jump, jump, .02f, .02f, 0);
        Check(!result.Next.JumpBuffered && result.Next.Grounded, "held jump does not generate another press");
        s = State(); s.AirJumps = 1; s.JumpBuffered = true;
        result = engine.Predict(s, p, jump, jump, .02f, .02f, 0);
        Check(result.Next.AirJumps == 0 && Near(result.Next.Velocity.Y, 295), "air jump uses remaining charge and current vertical velocity");
        s = State(); s.ControlVelocity.Y = s.Velocity.Y = 300;
        result = engine.Predict(s, p, default, jump, .02f, .02f, 0);
        Check(result.Next.JumpCutBuffered && result.Next.Velocity.Y == 290, "release buffers jump cut after physics");
        second = engine.Predict(result.Next, p, default, default, .02f, .02f, 0);
        Check(second.Next.Velocity.Y == 50, "buffered jump cut uses game's velocity window");
        result = engine.Predict(s, p, jump, new FrameInput { Held = Buttons.Jump | Buttons.Jump2 }, .02f, .02f, 0);
        Check(result.Next.JumpCutBuffered, "alternate jump release stays independent");
        s = State(); s.Grounded = true; s.JumpBuffered = true; s.JumpBufferExpires = .01;
        result = engine.Predict(s, p, default, default, .02f, .02f, 0);
        Check(result.Next.Grounded && !result.Next.JumpBuffered, "expired buffered jump cannot fire");
        s = State(); s.ControlVelocity.X = s.Velocity.X = 200;
        result = engine.Predict(s, p, default, default, .02f, .02f, 0);
        Check(result.Next.Velocity.X == 100, "neutral input decelerates horizontally");
        result = engine.Predict(s, p, new FrameInput { X = -1 }, new FrameInput { X = -1 }, .02f, .02f, 0);
        Check(result.Next.Velocity.X == 0, "reverse input applies stronger braking");
        s = State(); s.WallJumpLock = 2; s.WallJumpDirection = -1;
        result = engine.Predict(s, p, right, right, .02f, .02f, 0);
        Check(result.Next.Velocity.X == -200 && result.Next.WallJumpLock == 1, "existing wall-jump movement lock overrides input");
        s = State(); s.ControlVelocity.Y = s.Velocity.Y = 5;
        p.ApexAntiGravity = 2; p.ApexSpeedBonus = 1;
        result = engine.Predict(s, p, right, right, .02f, .02f, 0);
        Check(Near(result.Next.Velocity.Y, -3) && Near(result.Next.Velocity.X, 110), "jump-apex gravity and speed corrections");
        p = Parameters(); p.MomentumDecay = 5; p.FlatMomentumDecay = 2;
        s = State(); s.Momentum = new MotionVector(100, 50);
        result = engine.Predict(s, p, default, default, .02f, .02f, 0);
        Check(Near(result.Next.Momentum.X, 88) && Near(result.Next.Momentum.Y, 44.96f), "momentum decay uses game's per-tick and time-scaled terms");
        Check(Near(result.Next.Velocity.Y, 134.96f), "displayed total velocity includes momentum");
        p = Parameters(); s = State(); s.Dashing = true; s.Cutscene = 1;
        s.DashFramesRemaining = 5; s.FramesSinceDash = 2; s.DashDirection = new MotionVector(1, 0);
        result = engine.Predict(s, p, default, default, .02f, .02f, 0);
        Check(result.Next.State == PlayerMotionState.Dashing && result.Next.Velocity.X == 600 && result.Next.Position.X == 12, "ongoing horizontal dash prediction");
        Check(result.Next.DashFramesRemaining == 4 && result.Next.FramesSinceDash == 3, "dash counters advance one physics tick");
        s.DashDirection = new MotionVector(0, 1); s.FramesSinceDash = 9;
        result = engine.Predict(s, p, default, default, .02f, .02f, 0);
        Check(Near(result.Next.Velocity.Y, 300), "upward dash droop is modeled");
        s.DashFramesRemaining = 0;
        Check(!engine.Predict(s, p, default, default, .02f, .02f, 0).Available, "dash-end transition is explicitly unsupported");
        s = State(); s.DashBuffered = true; s.DashCooldown = -1; s.AirDashes = 1;
        Check(!engine.Predict(s, p, default, default, .02f, .02f, 0).Available, "dash startup is explicitly unsupported");
        s = State(); s.OnWall = true;
        Check(s.State == PlayerMotionState.WallSliding && !engine.Predict(s, p, default, default, .02f, .02f, 0).Available, "wall state stays visible without fabricated prediction");
        s = State(); s.OnMovingPlatform = true;
        Check(!engine.Predict(s, p, default, default, .02f, .02f, 0).Available, "platform transition unsupported");
        s = State(); s.Dead = true;
        Check(s.State == PlayerMotionState.Dead && !engine.Predict(s, p, default, default, .02f, .02f, 0).Available, "death state available, respawn prediction withheld");
        s = State();
        Check(!engine.Predict(s, p, new FrameInput { Held = Buttons.Reset }, default, .02f, .02f, 0).Available, "reset input does not fabricate teleport");
        result = engine.Predict(s, p, right, right, .01f, .02f, 0);
        Check(result.PhysicsTicks == 0 && result.Next.Position.Y == s.Position.Y && result.Next.Velocity.Y == s.Velocity.Y, "zero physics ticks preserve motion");
        result = engine.Predict(s, p, right, right, .01f, .02f, .015f);
        Check(result.PhysicsTicks == 1 && result.Next.Position.X == 2, "fractional fixed accumulator included");
        result = engine.Predict(s, p, right, right, .02f, .02f, .01999998f);
        Check(result.PhysicsTicks == 1, "near-boundary accumulator does not invent an extra physics tick");
        result = engine.Predict(s, p, right, right, .04f, .02f, 0);
        Check(result.PhysicsTicks == 2 && result.Next.Position.X == 6 && Near(result.Next.Position.Y, 3.4f), "multi-tick frame advances full movement per tick");
        Check(!engine.Predict(s, p, default, default, 0, .02f, 0).Available, "hitstop/paused game time is not treated as a moving frame");
        Check(!engine.Predict(s, p, default, default, 1, .02f, 0).Available, "excessive frame duration bounded");
        Check(!engine.Predict(s, p, default, default, float.NaN, .02f, 0).Available, "nonfinite frame timing rejected");
        Check(!engine.Predict(s, p, default, default, .02f, .02f, -.01f).Available, "negative accumulator rejected");
        s.PhysicsSimulated = false;
        Check(!engine.Predict(s, p, default, default, .02f, .02f, 0).Available, "disabled native physics cannot yield a prediction");
        var normalized = MovementPredictor.NormalizeInput(.5f, .35f);
        Check(normalized.X == 1 && normalized.Y == 0, "direction normalization matches game's diagonal thresholds");
        var timers = new SimulationTimers(); object owner = new object(); bool fired = false;
        timers.Schedule(owner, "callback", .1, () => fired = true);
        Check(Math.Abs(timers.Remaining(owner, "callback") - .1) < 1e-6 && !fired && timers.Time == 0, "reading pending timer is side-effect-free");
        timers.Advance(.02);
        Check(Math.Abs(timers.Remaining(owner, "callback") - .08) < 1e-6 && double.IsPositiveInfinity(timers.Remaining(new object(), "callback")), "timer read respects elapsed time and owner");
        Console.WriteLine("PASS: " + _checks + " player motion/prediction checks (pure model, no Unity simulation).");
    }
}
