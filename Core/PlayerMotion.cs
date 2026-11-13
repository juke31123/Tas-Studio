using System;

namespace Igtap.Tas.Core
{
    public struct MotionVector
    {
        public float X, Y;
        public MotionVector(float x, float y) { X = x; Y = y; }
        public float Magnitude => (float)Math.Sqrt(X * X + Y * Y);
        public static MotionVector operator +(MotionVector a, MotionVector b) => new MotionVector(a.X + b.X, a.Y + b.Y);
        public static MotionVector operator *(MotionVector a, float scale) => new MotionVector(a.X * scale, a.Y * scale);
    }

    public enum PlayerMotionState { Grounded, Jumping, Falling, Airborne, WallSliding, Dashing, Dead }

    // Detached values only: future previews can advance a copy without touching
    // the live player, Unity's physics world, input actions, timers or RNG.
    public sealed class PlayerMotion
    {
        public MotionVector Position, Velocity, ControlVelocity, Momentum, PlatformVelocity, DashDirection;
        public bool Grounded, OnWall, Dashing, Dead, FacingRight, RecentlyJumped, NormalJump;
        public bool JumpBuffered, JumpCutBuffered, DashBuffered, OnMovingPlatform, FinalWallCling;
        public bool PhysicsSimulated = true;
        public int Cutscene, AirJumps, AirDashes, DashBounceFrames;
        public float DashFramesRemaining, FramesSinceDash, DashCooldown, WallJumpLock, WallJumpDirection, WallCoyoteFrames;
        public double JumpBufferExpires = double.PositiveInfinity, JumpCutExpires = double.PositiveInfinity;
        public double DashBufferExpires = double.PositiveInfinity, RecentJumpExpires = double.PositiveInfinity;
        public PlayerMotion Copy() => (PlayerMotion)MemberwiseClone();
        public PlayerMotionState State => Dead || Cutscene == 2 ? PlayerMotionState.Dead
            : Dashing || Cutscene == 1 ? PlayerMotionState.Dashing
            : OnWall ? PlayerMotionState.WallSliding
            : Grounded ? PlayerMotionState.Grounded
            : Velocity.Y > .01f ? PlayerMotionState.Jumping
            : Velocity.Y < -.01f ? PlayerMotionState.Falling : PlayerMotionState.Airborne;
    }

    public sealed class MovementParameters
    {
        public float RunSpeed, FramesToTopSpeed, Gravity, FastFallMultiplier, MaxDownwardsSpeed, TerminalDrag;
        public float ApexAntiGravity, ApexSpeedBonus, JumpForce, AirJumpForce, MaxWallCoyoteFrames;
        public float MomentumDecay, OppositeMomentumDecay, FlatMomentumDecay;
        public float DashSpeed, DashFrames, DashDroopBaseline, DashDroopIntensity, WallJumpLockFrames;
        public bool DoubleJumpUnlocked, DashUnlocked;
        public MotionVector NativeGravity;
        public float LinearDamping;
    }

    public sealed class MotionPrediction
    {
        public PlayerMotion Next { get; }
        public bool Available => Next != null;
        public string Explanation { get; }
        public int PhysicsTicks { get; }
        public float FrameSeconds { get; }
        public PlayerActionAvailability Actions { get; }
        public MotionPrediction(PlayerMotion next, string explanation, int ticks, float seconds, PlayerActionAvailability actions = null)
        { Next = next; Explanation = explanation; PhysicsTicks = ticks; FrameSeconds = seconds; Actions = next == null ? null : actions; }
    }

    public interface IMotionPredictor
    {
        MotionPrediction Predict(PlayerMotion current, MovementParameters parameters, FrameInput input,
            FrameInput previousInput, float frameSeconds, float fixedSeconds, float fixedRemainder);
    }
}
