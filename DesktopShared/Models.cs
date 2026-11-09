using System;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Search;
namespace Igtap.Tas.DesktopModel
{
    // Detached view models shared between the game and native window threads.
    public sealed class Command
    {
        public string Id = "", Action = "", Name = "", Speed = "1";
        public int Start, Count = 1, Course, Dashes = 1;
        public int Fps = 50;
        public int Column, Revision, Sequence;
        public bool CellHeld;
        public float Value;
        public float X, Y;
        public bool Jump, Dash, Reset, Menu, Authored;
        public bool WallJump, AirJump, UnlockDash, OmniDash, BlockSwap, Light, Orbs, Zippers;
        public SearchOptions SearchOptions;
        internal Command Copy() { var copy = (Command)MemberwiseClone(); copy.SearchOptions = SearchOptions?.Copy(); return copy; }
    }
    public sealed class Snapshot
    {
        public Keybinds Bindings = new Keybinds();
        public int Version = 1;
        public string ModVersion = "0.12.1", Mode = "", Status = "", Name = "", Ack = "", Error = "";
        public int Cursor, Frames, Fps, Rerecords, Page, Revision, CompletedSequence;
        public bool CanEditTimeline, TimelineInput, PendingEdits;
        public int UndoCount, RedoCount;
        public bool CanRecoverDraft;
        public bool AuditRunning;
        public string AuditResult = "";
        public float Speed;
        public bool HasPerformanceSample;
        public double ActualFps, AchievedSpeed;
        public string PerformanceError = "";
        public bool Active, Paused, MainMenu, CanEdit, CanState, CanTransport, Dirty, Authored;
        public string[] Movies = Array.Empty<string>(), States = Array.Empty<string>();
        public FrameRow[] Timeline = Array.Empty<FrameRow>();
        public FrameRow SelectedInput = new FrameRow();
        public StatRow[] Stats = Array.Empty<StatRow>();
        public string Prediction = "", InputSource = "";
        public int FramesUntilPu = -1;
        public long Pus;
        public SearchView Search = new SearchView();
    }
    public sealed class FrameRow
    {
        public int Frame;
        public int Pus = -1;
        public bool PuTimingPending;
        public float X, Y;
        public bool Jump, Dash, Reset, Menu, Verified;
    }
    public sealed class StatRow { public string Name = "", Current = "", Next = ""; }
    public sealed class SearchView
    {
        public SearchOptions Options = new SearchOptions();
        public SearchResult Result = new SearchResult();
        public GoalCameraBindings CameraBindings = new GoalCameraBindings();
        public bool Busy, PlacingGoal, CanStart, CanAccept, Preview;
    }

}
