using System;
using System.Collections.Generic;
using System.Linq;

namespace Igtap.Tas.Core
{
    // History contains input differences only; old simulation results must never
    // become trusted again simply because an edit was undone.
    public sealed class InputEdit
    {
        public int Start;
        public FrameInput[] Before, After;
        public static bool Same(FrameInput a, FrameInput b) => a.X == b.X && a.Y == b.Y && a.Held == b.Held &&
            a.ExactEdges == b.ExactEdges && a.Press == b.Press && a.Release == b.Release;
        public static InputEdit Between(Movie before, Movie after)
        {
            int start = 0, a = before.Frames.Count, b = after.Frames.Count;
            while (start < a && start < b && Same(before.Frames[start].Input, after.Frames[start].Input)) start++;
            if (start == a && start == b) return null;
            while (a > start && b > start && Same(before.Frames[a - 1].Input, after.Frames[b - 1].Input)) { a--; b--; }
            return new InputEdit { Start = start,
                Before = before.Frames.Skip(start).Take(a - start).Select(f => f.Input).ToArray(),
                After = after.Frames.Skip(start).Take(b - start).Select(f => f.Input).ToArray() };
        }
        public Movie Apply(Movie source, bool undo = false)
        {
            var remove = undo ? After : Before; var insert = undo ? Before : After;
            if (remove == null || insert == null || Start < 0 || Start > source.Frames.Count || remove.Length > source.Frames.Count - Start ||
                insert.Length > Movie.MaxFrames - (source.Frames.Count - remove.Length)) throw new InvalidOperationException("Invalid edit range.");
            for (int i = 0; i < remove.Length; i++)
                if (!Same(source.Frames[Start + i].Input, remove[i])) throw new InvalidOperationException("Edit history no longer matches this timeline.");
            var result = source.CopyForEdit();
            // Keep historical PU colours on rows whose inputs are replaced.
            var rows = new List<MovieFrame>(insert.Length);
            for (int i = 0; i < insert.Length; i++) rows.Add(new MovieFrame { Input = insert[i],
                PreviousFixedTicks = i < remove.Length ? source.Frames[Start + i].FixedTicks >= 0 ?
                    source.Frames[Start + i].FixedTicks : source.Frames[Start + i].PreviousFixedTicks : -1 });
            result.Frames.RemoveRange(Start, remove.Length); result.Frames.InsertRange(Start, rows);
            Timeline.Invalidate(result, Start); result.Validate(); return result;
        }
    }

    public sealed class EditHistory
    {
        private readonly List<InputEdit> _undo = new List<InputEdit>(), _redo = new List<InputEdit>();
        public const int Capacity = 200, MaxStoredInputs = 1_000_000;
        public int UndoCount => _undo.Count;
        public int RedoCount => _redo.Count;
        public void Clear() { _undo.Clear(); _redo.Clear(); }
        public void Push(InputEdit edit)
        {
            if (edit == null) return;
            _undo.Add(edit); _redo.Clear();
            int size = _undo.Sum(e => e.Before.Length + e.After.Length);
            while (_undo.Count > Capacity || size > MaxStoredInputs)
            { size -= _undo[0].Before.Length + _undo[0].After.Length; _undo.RemoveAt(0); }
        }
        public InputEdit Peek(bool redo)
        {
            var list = redo ? _redo : _undo;
            if (list.Count == 0) throw new InvalidOperationException(redo ? "Nothing to redo." : "Nothing to undo.");
            return list[list.Count - 1];
        }
        // Commit only after staging and durable journaling both succeed.
        public void Commit(bool redo)
        {
            var from = redo ? _redo : _undo; var to = redo ? _undo : _redo;
            var edit = Peek(redo); from.RemoveAt(from.Count - 1); to.Add(edit);
        }
    }
}
