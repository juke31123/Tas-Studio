namespace Igtap.Tas.Core
{
    public enum PlayerJumpKind { None, Ground, Wall, Air, Dash, Hyper }

    // Extra live flags used only by the stats display. Keeping them separate
    // avoids changing the serialized movement/search state or movie hashes.
    public struct PlayerActionContext
    {
        public bool MenuOpen, GamePaused, WallJumpUnlocked, CanHyper;
        public int WallJumps;
    }

    public sealed class PlayerActionAvailability
    {
        public PlayerJumpKind Jump { get; }
        [Newtonsoft.Json.JsonIgnore] public bool CanJump => Jump != PlayerJumpKind.None;
        [Newtonsoft.Json.JsonIgnore] public bool UsesAirJump => Jump == PlayerJumpKind.Air;
        public bool CanDash { get; }
        public string JumpText { get; }
        public string DashText { get; }

        [Newtonsoft.Json.JsonConstructor]
        internal PlayerActionAvailability(PlayerJumpKind jump, string jumpText, bool canDash, string dashText)
        { Jump=jump; JumpText=jumpText; CanDash=canDash; DashText=dashText; }

        // Readiness for a fresh button press under the sampled contact/state
        // conditions, not a forecast of the next PU's raycasts or timer expiry.
        // In particular this remains useful while TAS frame advance is paused.
        public static PlayerActionAvailability Evaluate(PlayerMotion s, MovementParameters p, PlayerActionContext context)
        {
            PlayerActionAvailability Blocked(string reason) => new PlayerActionAvailability(PlayerJumpKind.None,reason,false,reason);
            if(s==null || p==null) return Blocked("--");
            if(s.Dead || s.Cutscene==2) return Blocked("No (dead)");
            if(!s.PhysicsSimulated) return Blocked("No (physics)");
            if(context.MenuOpen) return Blocked("No (menu)");
            if(context.GamePaused) return Blocked("No (hitstop)");
            if(!Movie.Finite(s.ControlVelocity.Y) || !Movie.Finite(s.WallCoyoteFrames) || !Movie.Finite(p.MaxWallCoyoteFrames) ||
                !Movie.Finite(s.DashCooldown) || !Movie.Finite(s.WallJumpLock) || !Movie.Finite(p.WallJumpLockFrames))
                return Blocked("--");
            if(s.Cutscene!=0 && s.Cutscene!=1) return Blocked(s.Cutscene==4 ? "No (spring)" : "No (cutscene)");

            bool dashGate=p.DashUnlocked && s.DashCooldown<0 && s.WallJumpLock<p.WallJumpLockFrames-5;
            bool dash=s.Cutscene==0 && dashGate && s.AirDashes>0;
            string dashText=dash ? "Yes" : s.Cutscene==1 ? "No (dashing)" : !p.DashUnlocked ? "No (locked)" :
                s.AirDashes<=0 ? "No (empty)" : s.DashCooldown>=0 ? "No (cooldown)" : "No (wall lock)";
            var jump=PlayerJumpKind.None;
            string jumpText;
            if(s.Cutscene==1)
            {
                if(s.Grounded && s.DashBounceFrames<=0 && (!context.CanHyper || s.FramesSinceDash>=4))
                    jump=context.CanHyper ? PlayerJumpKind.Hyper : PlayerJumpKind.Dash;
                jumpText="No (dashing)";
            }
            else if(s.DashBuffered && dashGate)
                // The native dash branch returns before jump handling even
                // when it has no charge. Do not advertise an air jump here.
                jumpText="No (dash)";
            else if(s.Grounded) { jump=PlayerJumpKind.Ground; jumpText=""; }
            else if(s.OnWall || s.WallCoyoteFrames>0)
            {
                if(context.WallJumpUnlocked && context.WallJumps>0) jump=PlayerJumpKind.Wall;
                // This branch consumes the attempt; native code does not fall
                // through to the extra air jump when wall jumps are exhausted.
                jumpText="No (wall)";
            }
            else if(!p.DoubleJumpUnlocked) jumpText="No (locked)";
            else if(s.AirJumps<=0) jumpText="No (empty)";
            else if(s.WallCoyoteFrames>=p.MaxWallCoyoteFrames) jumpText="No (wall)";
            else if(s.ControlVelocity.Y>=(s.NormalJump ? 200 : 700)) jumpText="No (rising)";
            else { jump=PlayerJumpKind.Air; jumpText=""; }
            if(jump!=PlayerJumpKind.None) jumpText="Yes ("+jump.ToString().ToLowerInvariant()+")";
            return new PlayerActionAvailability(jump,jumpText,dash,dashText);
        }
    }
}
