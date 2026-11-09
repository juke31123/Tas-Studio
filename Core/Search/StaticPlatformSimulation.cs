using System;

namespace Igtap.Tas.Core.Search
{
    // Exploratory controller for static, axis-aligned platform geometry. This is
    // NOT the planned Unity/Box2D parity backend. Native verification is mandatory.
    // Unsupported contacts stop a rollout rather than silently disappearing.
    public sealed class StaticPlatformSimulation : ISearchSimulation
    {
        private readonly SearchJob _job;
        private readonly StaticCollisionWorld _collision;
        public SearchState State { get; private set; }
        public StaticPlatformSimulation(SearchJob job) { if(job.Simulator!=SearchJob.Backend) throw new NotSupportedException("Game-code jobs cannot run in the static model."); _job = job; _collision = new StaticCollisionWorld(job); Restore(job.Start); }
        public void Restore(SearchState snapshot) => State = snapshot.Copy();
        private static float Approach(float value, float target, float amount) => value < target ? Math.Min(target, value + amount) : Math.Max(target, value - amount);
        private static float Clamp(float value, float min, float max) => Math.Max(min, Math.Min(max, value));
        public bool Step(FrameInput input, SearchAbilities abilities, out string unsupported)
        {
            unsupported = null;
            var s = State; var p = s.Player; var tuning = _job.Parameters;
            if (p.Dead || !p.PhysicsSimulated) return false;
            if (input.Down(Buttons.Menu | Buttons.Reset)) { unsupported = "Menu/reset simulation"; return false; }
            if (p.OnMovingPlatform || p.PlatformVelocity.Magnitude > .001f || p.FinalWallCling)
            { unsupported = "Moving platform/final wall cling"; return false; }
            float frame = 1f / _job.Fps;
            if (s.FreezeRemaining > 0)
            {
                s.FreezeRemaining = Math.Max(0, s.FreezeRemaining - frame); InputEdges(input, abilities); s.Previous = input; return true;
            }
            double total = s.Phase + frame * s.Scale;
            int count = (int)Math.Floor(total / FrameTiming.PuSeconds + 1e-10);
            s.Phase = Math.Max(0, total - count * FrameTiming.PuSeconds);
            Expire(ref p.JumpBufferExpires, frame, ref p.JumpBuffered);
            Expire(ref p.JumpCutExpires, frame, ref p.JumpCutBuffered);
            Expire(ref p.DashBufferExpires, frame, ref p.DashBuffered);
            Expire(ref p.RecentJumpExpires, frame, ref p.RecentlyJumped);
            var move = MovementPredictor.NormalizeInput(s.Previous.X, s.Previous.Y);
            for (int i = 0; i < count; i++)
            {
                s.Pus++; s.Time += .02;
                var before = p.Position;
                p.DashCooldown -= .02f; p.FramesSinceDash++;
                if (p.WallJumpLock >= 0) p.WallJumpLock--;
                if (p.Dashing)
                {
                    if (--p.DashFramesRemaining <= 0)
                    {
                        // End-dash behaviour has interactions not covered by this
                        // experimental controller. Let a native check reject it.
                        p.Dashing = false; p.Cutscene = 0; p.Momentum += p.ControlVelocity * .5f;
                        p.ControlVelocity = default;
                    }
                    else p.ControlVelocity = p.DashDirection * (tuning.DashSpeed * 10);
                }
                else if (p.DashBuffered && (abilities & SearchAbilities.Dash) != 0 && tuning.DashUnlocked && p.AirDashes > 0 && p.DashCooldown <= 0)
                {
                    var direction = move;
                    if (!_job.OmniDashUnlocked) direction = new MotionVector(p.FacingRight ? 1 : -1, 0);
                    if (direction.Magnitude < .01f) direction = new MotionVector(p.FacingRight ? 1 : -1, 0);
                    direction *= 1 / direction.Magnitude;
                    p.DashDirection = direction; p.ControlVelocity = direction * (tuning.DashSpeed * 10);
                    p.Dashing = true; p.Cutscene = 1; p.DashFramesRemaining = Math.Max(1, tuning.DashFrames);
                    p.DashBuffered = false; p.AirDashes--; p.DashCooldown = .2f; s.FreezeRemaining = .05f;
                }
                else
                {
                    float top = tuning.RunSpeed * 10, acceleration = top / Math.Max(1, tuning.FramesToTopSpeed);
                    if (p.WallJumpLock > 0) p.ControlVelocity.X = p.WallJumpDirection * top;
                    else
                    {
                        float target = move.X * top;
                        p.ControlVelocity.X = Approach(p.ControlVelocity.X, target, acceleration * (p.ControlVelocity.X * move.X < 0 ? 2 : 1));
                    }
                    if (p.Grounded && !p.RecentlyJumped) p.ControlVelocity.Y = 0;
                    else p.ControlVelocity.Y -= 50 * .02f * tuning.Gravity * (p.ControlVelocity.Y < -75 ? tuning.FastFallMultiplier : 1);
                    p.ControlVelocity.Y = Math.Max(-tuning.MaxDownwardsSpeed * 8, p.ControlVelocity.Y);
                    if (p.JumpBuffered && (abilities & SearchAbilities.Jump) != 0)
                    {
                        bool wall = !p.Grounded && p.OnWall && _job.WallJumpUnlocked && (abilities & SearchAbilities.WallJump) != 0;
                        bool air = !p.Grounded && !wall && tuning.DoubleJumpUnlocked && p.AirJumps > 0 &&
                            (abilities & SearchAbilities.AirJump) != 0 && p.ControlVelocity.Y < (p.NormalJump ? 200 : 700);
                        if (p.Grounded || wall || air)
                        {
                            p.ControlVelocity.Y = air ? tuning.AirJumpForce + Math.Max(0, p.ControlVelocity.Y) * .5f : tuning.JumpForce;
                            if (air) p.AirJumps--;
                            if (wall) { p.WallJumpDirection = p.FacingRight ? -1 : 1; p.ControlVelocity.X = p.WallJumpDirection * top; p.WallJumpLock = tuning.WallJumpLockFrames; }
                            p.JumpBuffered = false; p.NormalJump = true; p.Grounded = false; p.RecentlyJumped = true; p.RecentJumpExpires = .03;
                            p.Position.Y += 5;
                        }
                    }
                    if (p.NormalJump && p.JumpCutBuffered && p.ControlVelocity.Y > 150 && p.ControlVelocity.Y < 580)
                    { p.ControlVelocity.Y = 50; p.JumpCutBuffered = false; }
                }
                p.Momentum *= 1 - Clamp(.02f * tuning.MomentumDecay, 0, 1);
                p.Velocity = p.ControlVelocity + p.Momentum;
                p.Velocity = (p.Velocity + tuning.NativeGravity * .02f) * (1 / (1 + Math.Max(0, tuning.LinearDamping) * .02f));
                var next = p.Position + p.Velocity * .02f;
                if (!_collision.Move(p, next, out unsupported)) return false;
                if (p.Grounded) { p.AirDashes = _job.MaxDashes; p.AirJumps = _job.MaxAirJumps; }
                if (move.X != 0) p.FacingRight = move.X > 0;
                if (!Movie.Finite(next.X) || !Movie.Finite(next.Y) || Math.Abs(next.X) > 1e8 || Math.Abs(next.Y) > 1e8) return false;
            }
            InputEdges(input, abilities); s.Previous = input; return true;
        }
        private void InputEdges(FrameInput input, SearchAbilities allowed)
        {
            var p = State.Player;
            if ((allowed & SearchAbilities.Jump) != 0 && input.Pressed(Buttons.Jump, State.Previous))
            { p.JumpBuffered = true; p.JumpBufferExpires = p.Dashing ? .2 : .12; p.JumpCutBuffered = false; }
            if (input.Released(Buttons.Jump, State.Previous)) { p.JumpCutBuffered = true; p.JumpCutExpires = .26; }
            if ((allowed & SearchAbilities.Dash) != 0 && input.Pressed(Buttons.Dash, State.Previous)) { p.DashBuffered = true; p.DashBufferExpires = .12; }
        }
        private static void Expire(ref double deadline, float seconds, ref bool state) { deadline -= seconds; if (deadline <= 0) state = false; }
    }
}
