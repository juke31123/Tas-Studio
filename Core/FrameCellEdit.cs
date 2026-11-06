using System;
namespace Igtap.Tas.Core
{
    public enum InputColumn { Up, Down, Left, Right, Jump, Dash, Reset, X, Y, Menu }
    public static class FrameCellEdit
    {
        public static FrameInput Apply(FrameInput input, InputColumn column, bool held, float value = 0)
        {
            if (column == InputColumn.Jump || column == InputColumn.Dash || column == InputColumn.Reset || column == InputColumn.Menu)
            { input.ExactEdges = false; input.Press = input.Release = Buttons.None; }
            switch (column)
            {
                case InputColumn.Up: input.Y = held ? 1 : input.Y > 0 ? 0 : input.Y; break;
                case InputColumn.Down: input.Y = held ? -1 : input.Y < 0 ? 0 : input.Y; break;
                case InputColumn.Left: input.X = held ? -1 : input.X < 0 ? 0 : input.X; break;
                case InputColumn.Right: input.X = held ? 1 : input.X > 0 ? 0 : input.X; break;
                case InputColumn.Jump: input.Held = Set(input.Held, Buttons.Jump | Buttons.Jump2, Buttons.Jump, held); break;
                case InputColumn.Dash: input.Held = Set(input.Held, Buttons.Dash | Buttons.Dash2, Buttons.Dash, held); break;
                case InputColumn.Reset: input.Held = Set(input.Held, Buttons.Reset, Buttons.Reset, held); break;
                case InputColumn.Menu: input.Held = Set(input.Held, Buttons.Menu, Buttons.Menu, held); break;
                case InputColumn.X: Validate(value); input.X = value; break;
                case InputColumn.Y: Validate(value); input.Y = value; break;
                default: throw new ArgumentOutOfRangeException(nameof(column));
            }
            return input;
        }
        private static Buttons Set(Buttons current, Buttons mask, Buttons canonical, bool held) => held ? (current & mask) != 0 ? current : current | canonical : current & ~mask;
        private static void Validate(float value) { if (float.IsNaN(value) || float.IsInfinity(value) || Math.Abs(value) > 1) throw new ArgumentException("Axis values must be between -1 and 1."); }
        public static void Paint(Movie movie, int start, int count, InputColumn column, bool held, float value)
        {
            if (start < 0 || count < 1 || start > movie.Frames.Count || count > Movie.MaxFrames - start) throw new ArgumentOutOfRangeException(nameof(start));
            Apply(default, column, held, value);
            int end = start + count;
            while (movie.Frames.Count < end) movie.Frames.Add(new MovieFrame());
            for (int i = start; i < end; i++) movie.Frames[i].Input = Apply(movie.Frames[i].Input, column, held, value);
            Timeline.Invalidate(movie, start);
        }
    }
}
