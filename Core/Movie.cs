using System;
using System.Collections.Generic;

namespace Igtap.Tas.Core
{
    [Flags]
    public enum Buttons { None = 0, Jump = 1, Jump2 = 2, Dash = 4, Dash2 = 8, Reset = 16, Menu = 32 }

    // Logical actions make movies independent of the keyboard/controller bindings.
    // Keep the two jump actions separate: releasing either triggers the game's jump cut.
    public struct FrameInput
    {
        public float X;
        public float Y;
        public Buttons Held;
        public bool ExactEdges;
        public Buttons Press, Release;
        public bool Down(Buttons button) => (Held & button) != 0;
        public bool Pressed(Buttons button, FrameInput previous) => ExactEdges ? (Press & button) != 0 : Down(button) && !previous.Down(button);
        public bool Released(Buttons button, FrameInput previous) => ExactEdges ? (Release & button) != 0 : !Down(button) && previous.Down(button);
    }

    public sealed class StateProbe
    {
        public float X, Y, VelocityX, VelocityY, MomentumX, MomentumY;
        public float RenderX, RenderY, BodyVelocityX, BodyVelocityY;
        public bool Grounded, OnWall, Dashing, FacingRight;
        public bool MenuOpen, GamePaused;
        public int AirJumps, AirDashes, Cutscene;
        public string MovementHash = "";
        // Recorded values provide actionable field-level differences.
        public SortedDictionary<string, string> MovementFields;
    }

    public sealed class MovieFrame
    {
        public FrameInput Input;
        // Null means the frame has been authored/edited and is not yet verified.
        public StateProbe Expected;
        public int FixedTicks = -1;
        // Editor-only history. Never used for replay verification or persisted as fact.
        [Newtonsoft.Json.JsonIgnore] public int PreviousFixedTicks = -1;
        public double PuPhase = -1;
        public WorldWitness World;
    }

    public sealed class Movie
    {
        public const int CurrentVersion = 7;
        public const int MaxFrames = 2_000_000;
        public int FormatVersion = CurrentVersion;
        public string Name = "Untitled";
        public string CreatedUtc = DateTime.UtcNow.ToString("O");
        public string Adapter = "igtap-movement-v1";
        public string GameHash = "";
        public string UnityVersion = "";
        public string ModSetHash = "";
        public string SaveHash = "";
        public string Scene = "";
        public int Difficulty;
        public TasStartSetup StartSetup;
        public int Seed = 12345;
        public int FrameRate = 50;
        public float FixedDeltaTime = 0.02f;
        public int TimingProtocol = FrameTiming.Protocol;
        public double StartPuPhase;
        public int WarmupFrames = 10;
        // An absent protocol stays invalid; only explicitly recorded protocol 4 is accepted.
        public int BootstrapVersion;
        public List<int> WarmupFixedTicks = new List<int>();
        public int Rerecords;
        public StateProbe StartState;
        public WorldWitness StartWorld;
        public bool AtomDoorPresent;
        public int AtomDoorValue;
        public string Notes = "";
        public List<MovieFrame> Frames = new List<MovieFrame>();

        // Probes/witnesses are immutable after capture. Clone the mutable rows,
        // not thousands of dictionaries through a JSON roundtrip on every click.
        public Movie CopyForEdit()
        {
            var copy = (Movie)MemberwiseClone();
            copy.Frames = Frames.ConvertAll(f => new MovieFrame { Input = f.Input, Expected = f.Expected,
                FixedTicks = f.FixedTicks, PreviousFixedTicks = f.PreviousFixedTicks, PuPhase = f.PuPhase, World = f.World });
            copy.WarmupFixedTicks = new List<int>(WarmupFixedTicks);
            copy.StartSetup = StartSetup?.Clone();
            return copy;
        }

        public void Validate()
        {
            StartSetup?.Validate();
            if (Difficulty < 0 || Difficulty > 1 || (StartSetup != null && StartSetup.Difficulty != Difficulty))
                throw new InvalidOperationException("Invalid movie difficulty.");
            if (FormatVersion != CurrentVersion) throw new InvalidOperationException("Unsupported TAS format version.");
            if (Adapter != "igtap-movement-v1") throw new InvalidOperationException("Unsupported game adapter.");
            FrameTiming.ValidateFps(FrameRate);
            if (FixedDeltaTime != FrameTiming.NativePuSeconds || TimingProtocol != FrameTiming.Protocol ||
                double.IsNaN(StartPuPhase) || StartPuPhase < 0 || StartPuPhase >= FrameTiming.PuSeconds ||
                (StartSetup != null && StartSetup.FrameRate != FrameRate))
                throw new InvalidOperationException("Invalid movie timing.");
            if (WarmupFrames < 1 || WarmupFrames > 600) throw new InvalidOperationException("Invalid warmup length.");
            if (BootstrapVersion != 4) throw new InvalidOperationException("Incompatible TAS run: unsupported starting-state protocol. Record a new run with the installed version.");
            if (WarmupFixedTicks == null || WarmupFixedTicks.Count > WarmupFrames || WarmupFixedTicks.Exists(n => n < 0 || n > 1000)) throw new InvalidOperationException("Invalid warmup tick schedule.");
            if (Frames == null || Frames.Count > MaxFrames) throw new InvalidOperationException("Invalid movie length.");
            ValidateProbe(StartState);
            StartWorld?.Validate();
            foreach (var frame in Frames)
            {
                if (frame == null || !Finite(frame.Input.X) || !Finite(frame.Input.Y) || Math.Abs(frame.Input.X) > 1 || Math.Abs(frame.Input.Y) > 1 || ((int)frame.Input.Held & ~63) != 0)
                    throw new InvalidOperationException("Invalid input frame.");
                if (frame.FixedTicks < -1 || frame.FixedTicks > 1000) throw new InvalidOperationException("Invalid physics tick count.");
                if ((((int)frame.Input.Press | (int)frame.Input.Release) & ~63) != 0 || double.IsNaN(frame.PuPhase) ||
                    (frame.PuPhase != -1 && (frame.PuPhase < 0 || frame.PuPhase >= FrameTiming.PuSeconds)))
                    throw new InvalidOperationException("Invalid frame timing or button edges.");
                ValidateProbe(frame.Expected);
                frame.World?.Validate();
            }
        }

        private static void ValidateProbe(StateProbe p)
        {
            if (p != null && (!Finite(p.X) || !Finite(p.Y) || !Finite(p.RenderX) || !Finite(p.RenderY) || !Finite(p.BodyVelocityX) || !Finite(p.BodyVelocityY) || !Finite(p.VelocityX) || !Finite(p.VelocityY) || !Finite(p.MomentumX) || !Finite(p.MomentumY)))
                throw new InvalidOperationException("Invalid state probe.");
        }
        public static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    public static class ProbeComparer
    {
        public static string Difference(StateProbe expected, StateProbe actual, float tolerance = 0.01f)
        {
            if (expected == null) return null;
            if (actual == null) return "Player missing";
            if (!Movie.Finite(actual.X) || !Movie.Finite(actual.Y) || !Movie.Finite(actual.RenderX) || !Movie.Finite(actual.RenderY) || !Movie.Finite(actual.BodyVelocityX) || !Movie.Finite(actual.BodyVelocityY) || !Movie.Finite(actual.VelocityX) || !Movie.Finite(actual.VelocityY) || !Movie.Finite(actual.MomentumX) || !Movie.Finite(actual.MomentumY)) return "Non-finite player state";
            if (expected.MenuOpen != actual.MenuOpen || expected.GamePaused != actual.GamePaused) return "Game pause/menu state differs";
            if (Math.Abs(expected.X - actual.X) > tolerance || Math.Abs(expected.Y - actual.Y) > tolerance) return "Position differs";
            if (Math.Abs(expected.RenderX - actual.RenderX) > tolerance || Math.Abs(expected.RenderY - actual.RenderY) > tolerance) return "Interpolated position differs";
            if (Math.Abs(expected.BodyVelocityX - actual.BodyVelocityX) > tolerance || Math.Abs(expected.BodyVelocityY - actual.BodyVelocityY) > tolerance) return "Physics velocity differs";
            if (Math.Abs(expected.VelocityX - actual.VelocityX) > tolerance || Math.Abs(expected.VelocityY - actual.VelocityY) > tolerance) return "Velocity differs";
            if (Math.Abs(expected.MomentumX - actual.MomentumX) > tolerance || Math.Abs(expected.MomentumY - actual.MomentumY) > tolerance) return "Momentum differs";
            if (expected.Grounded != actual.Grounded || expected.OnWall != actual.OnWall || expected.Dashing != actual.Dashing || expected.FacingRight != actual.FacingRight || expected.AirJumps != actual.AirJumps || expected.AirDashes != actual.AirDashes || expected.Cutscene != actual.Cutscene) return "Movement flags/resources differ";
            if (expected.MovementHash != actual.MovementHash)
            {
                if (expected.MovementFields != null && actual.MovementFields != null)
                    foreach (var field in expected.MovementFields)
                        if (!actual.MovementFields.TryGetValue(field.Key, out var value) || value != field.Value)
                            return "Internal movement state differs: " + field.Key + " (expected " + field.Value + ", actual " + (value ?? "missing") + ")";
                return "Internal movement state differs";
            }
            return null;
        }
    }
}
