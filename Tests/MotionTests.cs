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
        ActionChecks();
        Console.WriteLine("PASS: " + _checks + " player motion/prediction checks (pure model, no Unity simulation).");
    }
    private static void ActionChecks()
    {
        var s=State();var p=Parameters();var context=new PlayerActionContext {WallJumpUnlocked=true,WallJumps=2};
        PlayerActionAvailability Read()=>PlayerActionAvailability.Evaluate(s,p,context);
        s.Grounded=true;s.AirJumps=1;s.AirDashes=1;s.DashCooldown=-.01f;
        Check(Read().Jump==PlayerJumpKind.Ground && !Read().UsesAirJump && Read().CanDash,"Ground readiness does not claim to consume the available air jump.");
        s.Grounded=false;
        Check(Read().UsesAirJump && Read().JumpText=="Yes (air)","An available midair jump is explicitly labeled air.");
        s.ControlVelocity.Y=200;s.Velocity.Y=-20;
        Check(!Read().CanJump && Read().JumpText=="No (rising)","The strict 200 air-jump boundary uses internal control velocity, not body velocity.");
        s.ControlVelocity.Y=199.999f;Check(Read().UsesAirJump,"Normal-jump ascent below the threshold permits an air jump.");
        s.NormalJump=false;s.ControlVelocity.Y=699.999f;Check(Read().UsesAirJump,"Other upward movement uses the native 700 air-jump threshold.");
        s.ControlVelocity.Y=700;Check(!Read().CanJump,"The alternative 700 threshold is also strict.");
        s.ControlVelocity.Y=-50;s.AirJumps=0;Check(!Read().CanJump,"No air-jump charge blocks a midair jump.");
        s.AirJumps=1;p.DoubleJumpUnlocked=false;Check(!Read().CanJump,"A stored charge cannot bypass the air-jump unlock.");p.DoubleJumpUnlocked=true;
        s.OnWall=true;
        Check(Read().Jump==PlayerJumpKind.Wall && !Read().UsesAirJump,"A wall jump has priority over an air jump.");
        context.WallJumps=0;Check(!Read().CanJump,"An exhausted wall-jump branch cannot fall through to consume an air jump.");
        s.OnWall=false;s.WallCoyoteFrames=1;context.WallJumps=1;
        Check(Read().Jump==PlayerJumpKind.Wall,"Wall-coyote readiness uses the wall jump.");
        context.WallJumpUnlocked=false;Check(!Read().CanJump,"A wall-coyote attempt still requires the wall-jump unlock.");
        s.WallCoyoteFrames=0;Check(Read().UsesAirJump,"An air jump becomes eligible after the wall window clears.");
        s.Grounded=true;s.AirJumps=0;p.DoubleJumpUnlocked=false;
        Check(Read().CanJump,"Ground jumping is independent of extra-air-jump unlocks and charges.");
        s.DashCooldown=0;Check(!Read().CanDash,"Dash cooldown must be strictly negative.");
        s.DashCooldown=-.001f;s.WallJumpLock=p.WallJumpLockFrames-5;
        Check(!Read().CanDash,"The wall-jump dash lock boundary is strict.");
        s.WallJumpLock-=.001f;Check(Read().CanDash,"Dash readiness returns after the wall-jump lock.");
        s.AirDashes=0;Check(!Read().CanDash,"No dash charge blocks dash.");
        s.DashBuffered=true;Check(!Read().CanJump,"An eligible queued dash blocks jump handling even with no dash charge.");
        s.DashBuffered=false;s.AirDashes=1;p.DashUnlocked=false;Check(!Read().CanDash,"Dash requires its unlock even when a charge is stored.");p.DashUnlocked=true;
        context.MenuOpen=true;Check(!Read().CanJump && !Read().CanDash,"An open game menu blocks both actions.");context.MenuOpen=false;
        context.GamePaused=true;Check(!Read().CanJump && !Read().CanDash,"Native hitstop blocks new button presses.");context.GamePaused=false;
        s.Cutscene=1;s.Dashing=true;s.DashBounceFrames=0;
        Check(Read().Jump==PlayerJumpKind.Dash && !Read().CanDash,"Grounded dash-jump readiness is distinct from another dash.");
        context.CanHyper=true;s.FramesSinceDash=3;Check(!Read().CanJump,"The early hyper window cannot activate a jump yet.");
        s.FramesSinceDash=4;Check(Read().Jump==PlayerJumpKind.Hyper && !Read().UsesAirJump,"A ready hyper jump does not consume the air jump.");
        s.DashBounceFrames=1;Check(!Read().CanJump,"Dash bounce frames block the dash super-jump branch.");
        s.Cutscene=4;Check(!Read().CanJump && !Read().CanDash,"Spring control lock does not advertise an immediate jump or dash.");
        s.Cutscene=0;s.Dashing=false;s.Dead=true;Check(!Read().CanJump && !Read().CanDash,"Dead players cannot act.");s.Dead=false;
        s.PhysicsSimulated=false;Check(!Read().CanJump && !Read().CanDash,"Disabled player physics reports no readiness.");s.PhysicsSimulated=true;
        s.DashCooldown=float.NaN;Check(!Read().CanJump && !Read().CanDash && Read().JumpText=="--","Non-finite state is unavailable, not a positive action claim.");
        var predictor=new MovementPredictor();s=State();p=Parameters();context=default;
        s.Grounded=true;s.AirDashes=1;s.DashCooldown=.01f;s.WallJumpLock=-100;
        var next=predictor.Predict(s,p,default,default,.02f,.02f,0);
        Check(!Read().CanDash && PlayerActionAvailability.Evaluate(next.Next,p,context).CanDash,"The next PU can expire a dash cooldown while current readiness remains blocked.");
        next=predictor.Predict(s,p,default,default,1f/280,.02f,0);
        Check(next.PhysicsTicks==0 && !PlayerActionAvailability.Evaluate(next.Next,p,context).CanDash,"A frame without a PU must not expire the dash cooldown.");
        s=State();s.AirJumps=1;s.ControlVelocity.Y=s.Velocity.Y=210;p.Gravity=27;
        next=predictor.Predict(s,p,default,default,.02f,.02f,0);
        Check(!Read().CanJump && PlayerActionAvailability.Evaluate(next.Next,p,context).UsesAirJump,"Predicted vertical velocity can make an air jump available on the next frame.");
        s.ControlVelocity.Y=s.Velocity.Y=100;s.JumpBuffered=true;
        next=predictor.Predict(s,p,default,default,.02f,.02f,0);
        Check(Read().UsesAirJump && next.Next.AirJumps==0 && !PlayerActionAvailability.Evaluate(next.Next,p,context).CanJump,"A predicted air jump consumes its charge before next-frame readiness is evaluated.");
        Check(!predictor.Predict(s,p,new FrameInput {ExactEdges=true,Press=Buttons.Menu},default,.02f,.02f,0).Available,"The contact-assumption fallback cannot invent readiness across a game-menu transition.");
    }
}
