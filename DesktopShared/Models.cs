using System;
namespace Igtap.Tas.DesktopModel
{
    // Detached view models shared between the game and native window threads.
    public sealed class Command
    {
        public string Id = "", Action = "", Name = "", Speed = "1";
        public int Start, Count = 1, Course, Dashes = 1;
        public int Column, Revision, Sequence;
        public bool CellHeld;
        public float Value;
        public float X, Y;
        public bool Jump, Dash, Reset, Authored;
        public bool WallJump, AirJump, UnlockDash, OmniDash, BlockSwap, Light, Orbs, Zippers;
        internal Command Copy() => (Command)MemberwiseClone();
    }
    public sealed class Snapshot
    {
        public int Version = 1;
        public string ModVersion = "0.6.1", Mode = "", Status = "", Name = "", Ack = "", Error = "";
        public int Cursor, Frames, Fps, Rerecords, Page, Revision, CompletedSequence;
        public bool CanEditTimeline, TimelineInput, PendingEdits;
        public float Speed;
        public bool Active, Paused, MainMenu, CanEdit, CanState, CanTransport, Dirty, Authored;
        public string[] Movies = Array.Empty<string>(), States = Array.Empty<string>();
        public FrameRow[] Timeline = Array.Empty<FrameRow>();
        public FrameRow SelectedInput = new FrameRow();
        public StatRow[] Stats = Array.Empty<StatRow>();
        public string Prediction = "", InputSource = "";
    }
    public sealed class FrameRow
    {
        public int Frame;
        public float X, Y;
        public bool Jump, Dash, Reset, Verified;
    }
    public sealed class StatRow { public string Name = "", Current = "", Next = ""; }

}
