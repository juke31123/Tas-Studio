using System;
using System.Collections.Generic;
using System.Reflection;
using Igtap.Tas.Core;
using UnityEngine;

namespace Igtap.Tas.Runtime
{
    // The only prediction layer that knows the installed game's field names.
    // Reading a snapshot never calls game movement, physics or input-update methods.
    internal static class PlayerTelemetry
    {
        private static readonly Dictionary<string, FieldInfo> Fields = new Dictionary<string, FieldInfo>();
        private static T Read<T>(Movement player, string name)
        {
            if (!Fields.TryGetValue(name, out var field))
            {
                field = typeof(Movement).GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? throw new NotSupportedException("Player stats field missing: " + name);
                Fields.Add(name, field);
            }
            return (T)field.GetValue(player);
        }
        private static MotionVector Vector(Vector2 v) => new MotionVector(v.x, v.y);
        public static PlayerMotion Capture(Movement p, SimulationTimers timers, out MovementParameters parameters)
        {
            var body = p.GetComponent<Rigidbody2D>();
            if (body == null) throw new InvalidOperationException("The player's physics body is not ready.");
            parameters = new MovementParameters {
                RunSpeed = p.runSpeed, FramesToTopSpeed = Read<float>(p, "framesToReachTopSpeed"),
                Gravity = p.gravity, FastFallMultiplier = Read<float>(p, "fastFallMod"),
                MaxDownwardsSpeed = p.maxDownwardsSpeed, TerminalDrag = p.PostMaxDownwardsSpeedDrag,
                ApexAntiGravity = Read<float>(p, "jumpApexAntiGrav"), ApexSpeedBonus = Read<float>(p, "jumpApexSpeedBonus"),
                JumpForce = p.jumpForce, AirJumpForce = p.airJumpForce, MaxWallCoyoteFrames = p.maxWallCoyoteFrames,
                MomentumDecay = Read<float>(p, "momentumDecay"), OppositeMomentumDecay = Read<float>(p, "MovingOppositeMomentumDecay"),
                FlatMomentumDecay = Read<float>(p, "flatMomentumDecay"), DashSpeed = Read<float>(p, "dashSpeed"),
                DashFrames = Read<float>(p, "dashFrames"), DashDroopBaseline = p.DashDroopBaseline, DashDroopIntensity = p.DashDroopIntensityOverTime,
                WallJumpLockFrames = p.wallJumpMovementLockFrames, DoubleJumpUnlocked = p.doubleJumpUnlocked, DashUnlocked = p.dashUnlocked,
                NativeGravity = body.bodyType == RigidbodyType2D.Dynamic ? Vector(Physics2D.gravity * body.gravityScale) : default,
                LinearDamping = body.bodyType == RigidbodyType2D.Dynamic ? body.linearDamping : 0
            };
            var snapshot = new PlayerMotion {
                Position = Vector(body.position), Velocity = Vector(body.linearVelocity), ControlVelocity = Vector(p.Velocity),
                PhysicsSimulated = body.simulated,
                Momentum = Vector(p.momentum), PlatformVelocity = Vector(p.movingPlatformVelocity),
                Grounded = p.onGround, OnWall = p.OnWall, Dashing = p.dashActive, Dead = Read<bool>(p, "isDead"),
                FacingRight = p.facingRight, RecentlyJumped = Read<bool>(p, "recentlyJumped"), NormalJump = Read<bool>(p, "MostRecentUpwardsMoveWasNormalJump"),
                JumpBuffered = Read<bool>(p, "jumpBuffer"), JumpCutBuffered = Read<bool>(p, "jumpCutBuffer"), DashBuffered = Read<bool>(p, "dashBuffer"),
                OnMovingPlatform = p.currentlyTouchedMovingPlatform != null || Read<float>(p, "movingPlatformCoyote") > 0,
                FinalWallCling = Read<bool>(p, "isFinalWallClingActive"), Cutscene = (int)p.cutsceneMode,
                AirJumps = p.airJumpsLeft, AirDashes = p.airDashesLeft, DashBounceFrames = Read<int>(p, "DashBounceFrames"),
                DashFramesRemaining = Read<float>(p, "dashFramesRemaining"), FramesSinceDash = p.framesSinceLastDash, DashCooldown = p.dashCooldown,
                DashDirection = Vector(Read<Vector2>(p, "dashDirection")), WallJumpLock = p.wallJumpMovementLockRemaining,
                WallJumpDirection = p.wallJumpDirection, WallCoyoteFrames = Read<float>(p, "wallCoyoteFrames")
            };
            if (timers != null)
            {
                snapshot.JumpBufferExpires = timers.Remaining(p, "cancelJumpBuffer");
                snapshot.JumpCutExpires = timers.Remaining(p, "cancelJumpCutBuffer");
                snapshot.DashBufferExpires = timers.Remaining(p, "cancelDashBuffer");
                snapshot.RecentJumpExpires = timers.Remaining(p, "endRecentJump");
            }
            return snapshot;
        }
    }
}
