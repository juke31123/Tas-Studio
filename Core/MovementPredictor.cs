using System;

namespace Igtap.Tas.Core
{
    // Lightweight telemetry fallback with unchanged contact assumptions. The
    // optional game-code trajectory backend supplies collision-aware TAS stats.
    public sealed class MovementPredictor : IMotionPredictor
    {
        public MotionPrediction Predict(PlayerMotion current, MovementParameters p, FrameInput input,
            FrameInput previous, float frameSeconds, float fixedSeconds, float fixedRemainder)
        {
            MotionPrediction Unavailable(string reason, int ticks = 0) => new MotionPrediction(null, reason, ticks, frameSeconds);
            if (current == null || p == null) return Unavailable("Player data is unavailable.");
            if (!current.PhysicsSimulated) return Unavailable("Player physics is disabled.");
            if (!Finite(current) || !Movie.Finite(input.X) || !Movie.Finite(input.Y)) return Unavailable("Player/input data contains non-finite values.");
            if (!Movie.Finite(frameSeconds) || !Movie.Finite(fixedSeconds) || !Movie.Finite(fixedRemainder) ||
                frameSeconds <= 0 || fixedSeconds <= 0 || fixedRemainder < 0 || fixedRemainder >= fixedSeconds)
                return Unavailable("No advancing frame or unsupported physics timing.");
            if (!Movie.Finite(p.FramesToTopSpeed) || p.FramesToTopSpeed <= 0) return Unavailable("Invalid movement parameters.");
            double tickCount = Math.Floor((fixedRemainder + (double)frameSeconds + 1e-9) / fixedSeconds);
            if (tickCount > 16) return Unavailable("Too many physics ticks for a one-frame preview.");
            int ticks = (int)tickCount;
            if (input.Pressed(Buttons.Reset, previous)) return Unavailable("Respawn prediction is not available yet.", ticks);
            if (input.Pressed(Buttons.Menu, previous)) return Unavailable("Game-menu transitions require the game-code forecast.", ticks);
            if (current.Dead || current.Cutscene == 2) return Unavailable("Death/respawn transition is not predicted.", ticks);
            if (current.Cutscene != 0 && current.Cutscene != 1) return Unavailable("This special movement mode is not predicted yet.", ticks);
            if (current.OnWall || current.FinalWallCling) return Unavailable("Wall interactions are not predicted yet.", ticks);
            if (current.OnMovingPlatform || current.PlatformVelocity.Magnitude > .001f)
                return Unavailable("Moving-platform interactions are not predicted yet.", ticks);

            var s = current.Copy();
            Expire(ref s.JumpBufferExpires, frameSeconds, ref s.JumpBuffered);
            Expire(ref s.JumpCutExpires, frameSeconds, ref s.JumpCutBuffered);
            Expire(ref s.DashBufferExpires, frameSeconds, ref s.DashBuffered);
            Expire(ref s.RecentJumpExpires, frameSeconds, ref s.RecentlyJumped);
            // Dynamic input is processed after this frame's PUs. Those PUs use
            // the previous dynamic input; frame actions below use the new input.
            MotionVector move = NormalizeInput(previous.X, previous.Y);
            for (int i = 0; i < ticks; i++)
            {
                string unsupported = Tick(s, p, move, fixedSeconds);
                if (unsupported != null) return Unavailable(unsupported, ticks);
            }
            // Unity runs FixedUpdate before Update. New jump/dash edges become
            // buffers after this frame's physics, for a subsequent physics tick.
            if (input.Pressed(Buttons.Jump, previous) || input.Pressed(Buttons.Jump2, previous))
            {
                s.JumpBuffered = true; s.JumpBufferExpires = s.Cutscene == 1 ? .2 : .12;
                s.JumpCutBuffered = false; s.JumpCutExpires = double.PositiveInfinity;
            }
            if (input.Released(Buttons.Jump, previous) || input.Released(Buttons.Jump2, previous))
            { s.JumpCutBuffered = true; s.JumpCutExpires = .26; }
            if (input.Pressed(Buttons.Dash, previous) || input.Pressed(Buttons.Dash2, previous))
            { s.DashBuffered = true; s.DashBufferExpires = .12; }
            if (!Finite(s)) return Unavailable("Prediction produced a non-finite value.", ticks);
            return new MotionPrediction(s, "Estimate: unchanged contacts; collisions and triggers are not simulated.", ticks, frameSeconds);
        }

        private static string Tick(PlayerMotion s, MovementParameters p, MotionVector move, float dt)
        {
            if (s.DashCooldown > -1) s.DashCooldown -= dt;
            if (s.DashBounceFrames > -1) s.DashBounceFrames--;
            // A stable floor is an assumption, not a raycast or collision result.
            if (s.Grounded && !s.RecentlyJumped) s.ControlVelocity.Y = 0;
            if (!s.Grounded) s.WallCoyoteFrames -= 50 * dt;
            float down = move.Y < -.6f && s.Velocity.Y < 300 ? 1.4f : 1;
            float fall = s.ControlVelocity.Y < -75 && !s.Grounded ? p.FastFallMultiplier : 1;
            s.ControlVelocity.Y -= 50 * dt * p.Gravity * fall * down;
            float terminal = -p.MaxDownwardsSpeed * 8 * down;
            if (s.ControlVelocity.Y < terminal) s.ControlVelocity.Y = Lerp(s.ControlVelocity.Y, terminal, p.TerminalDrag);
            bool apex = s.ControlVelocity.Y < 200 && s.ControlVelocity.Y > -50 && !s.Grounded;
            if (apex) s.ControlVelocity.Y += 50 * dt * p.ApexAntiGravity;
            s.FramesSinceDash++;

            if (s.Dashing && s.DashFramesRemaining <= 0) return "Dash-ending momentum/hitstop transition is not predicted yet.";
            if (s.DashFramesRemaining > -1) s.DashFramesRemaining--;
            if (s.Cutscene == 1)
            {
                if (s.Grounded && (s.DashDirection.Y < 0 || s.JumpBuffered))
                    return "Dash bounce/hyper-jump transition is not predicted yet.";
                var direction = s.DashDirection;
                if (direction.Y > 0) direction.Y = Lerp(0, direction.Y, p.DashDroopBaseline + p.DashDroopIntensity * (p.DashFrames - s.FramesSinceDash));
                s.ControlVelocity = direction * (p.DashSpeed * 10);
            }
            else
            {
                if (s.DashBuffered && s.DashCooldown < 0 && p.DashUnlocked && s.WallJumpLock < p.WallJumpLockFrames - 5)
                {
                    if (s.AirDashes > 0) return "Dash startup/hitstop transition is not predicted yet.";
                    // The game returns before horizontal/jump logic even without a charge.
                }
                else
                {
                    Horizontal(s, p, move.X, apex);
                    if (s.NormalJump && s.JumpCutBuffered && s.ControlVelocity.Y < 580 && s.ControlVelocity.Y > 150)
                    {
                        s.ControlVelocity.Y = 50;
                        if (s.WallJumpLock > 0) s.WallJumpLock = 0;
                    }
                    if (s.JumpBuffered && s.Grounded)
                    {
                        if (s.DashBounceFrames > 0) return "Dash bounce jump is not predicted yet.";
                        s.JumpBuffered = false; s.Grounded = false; s.NormalJump = true;
                        s.Position.Y += 5; s.ControlVelocity.Y = p.JumpForce;
                        s.RecentlyJumped = true; s.RecentJumpExpires = .03;
                    }
                    else if (s.JumpBuffered && s.WallCoyoteFrames > 0) return "Wall-coyote jump is not predicted yet.";
                    else if (s.JumpBuffered && p.DoubleJumpUnlocked && s.WallCoyoteFrames < p.MaxWallCoyoteFrames && s.AirJumps > 0 &&
                        s.ControlVelocity.Y < (s.NormalJump ? 200 : 700))
                    {
                        s.JumpBuffered = false; s.NormalJump = true; s.AirJumps--;
                        s.ControlVelocity.Y = p.AirJumpForce + Math.Max(0, s.ControlVelocity.Y) * .5f;
                    }
                }
            }
            if (s.ControlVelocity.X != 0) s.FacingRight = s.ControlVelocity.X > 0;
            float decay = 1 - Clamp01(dt * p.MomentumDecay);
            s.Momentum = s.Momentum * decay;
            s.Momentum.X = TowardZero(s.Momentum.X, p.FlatMomentumDecay);
            s.Momentum.Y = TowardZero(s.Momentum.Y, p.FlatMomentumDecay * dt);
            s.Velocity = s.ControlVelocity + s.Momentum;
            s.Velocity = (s.Velocity + p.NativeGravity * dt) * (1 / (1 + Math.Max(0, p.LinearDamping) * dt));
            if (s.Grounded && s.Velocity.Y < 0) s.Velocity.Y = 0;
            s.Position += s.Velocity * dt;
            return null;
        }

        private static void Horizontal(PlayerMotion s, MovementParameters p, float x, bool apex)
        {
            float top = p.RunSpeed * 10, acceleration = top / p.FramesToTopSpeed;
            if (s.WallJumpLock >= 0)
            { s.ControlVelocity.X = s.WallJumpDirection * top; s.WallJumpLock--; return; }
            s.WallJumpDirection = 0;
            if (s.Momentum.X > 0 && x < 0) s.Momentum.X -= p.OppositeMomentumDecay;
            else if (s.Momentum.X < 0 && x > 0) s.Momentum.X += p.OppositeMomentumDecay;
            if (x != 0)
            {
                s.ControlVelocity.X += x * acceleration;
                if ((s.ControlVelocity.X >= 0) != (x >= 0)) s.ControlVelocity.X += x * acceleration;
                if (apex) s.ControlVelocity.X += p.ApexSpeedBonus * x * 10;
            }
            else
            {
                s.ControlVelocity.X -= Math.Sign(s.ControlVelocity.X) * acceleration;
                if (Math.Abs(s.ControlVelocity.X) < acceleration) s.ControlVelocity.X = 0;
            }
            s.ControlVelocity.X = Math.Max(-top, Math.Min(top, s.ControlVelocity.X));
        }

        public static MotionVector NormalizeInput(float x, float y)
        {
            if (x > .8 || (x > .4 && Math.Abs(y) > .3)) x = 1;
            else if (x < -.8 || (x < -.4 && Math.Abs(y) > .3)) x = -1;
            else if (Math.Abs(x) < .3) x = 0;
            y = y > .4 ? 1 : y < -.4 ? -1 : 0;
            return new MotionVector(x, y);
        }
        private static void Expire(ref double time, float dt, ref bool value)
        { time -= dt; if (time <= 1e-7) value = false; }
        private static float TowardZero(float value, float step) => value > 0 ? Math.Max(0, value - step) : Math.Min(0, value + step);
        private static float Clamp01(float value) => Math.Max(0, Math.Min(1, value));
        private static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
        private static bool Finite(PlayerMotion s) => Movie.Finite(s.Position.X) && Movie.Finite(s.Position.Y) && Movie.Finite(s.Velocity.X) && Movie.Finite(s.Velocity.Y);
    }
}
