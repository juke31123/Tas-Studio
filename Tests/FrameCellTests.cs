using System;
using Igtap.Tas.Core;
internal static class FrameCellTests
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool result, string why) { if (!result) throw new Exception(why); checks++; }
        var input = new FrameInput { X = -.4f, Y = .3f, Held = Buttons.Jump2 | Buttons.Reset };
        var right = FrameCellEdit.Apply(input, InputColumn.Right, true);
        Check(right.X == 1 && right.Y == input.Y && right.Held == input.Held, "Direction cell preserves unrelated inputs");
        Check(FrameCellEdit.Apply(input, InputColumn.Right, false).X == -.4f, "Clearing inactive opposite direction preserves analog value");
        Check(FrameCellEdit.Apply(input, InputColumn.Left, false).X == 0, "Clearing active direction neutralizes axis");
        Check(FrameCellEdit.Apply(input, InputColumn.Jump, false).Held == Buttons.Reset, "Clear jump clears aliases without clearing reset");
        Check(FrameCellEdit.Apply(input, InputColumn.Jump, true).Held == input.Held, "Holding existing alias does not invent new input edges");
        Check(FrameCellEdit.Apply(input, InputColumn.X, false, .25f).X == .25f, "Analog value applies directly");
        var menu = FrameCellEdit.Apply(input, InputColumn.Menu, true);
        Check(menu.Down(Buttons.Menu) && menu.X == input.X && menu.Down(Buttons.Jump2), "Menu cell preserves movement inputs");
        Check(menu.Pressed(Buttons.Menu, input) && !menu.Pressed(Buttons.Menu, menu), "Menu press toggles once across a held stroke");
        Check(!FrameCellEdit.Apply(menu, InputColumn.Menu, false).Down(Buttons.Menu), "Clearing menu releases its action");
        var exact = FrameCellEdit.Apply(new FrameInput { ExactEdges = true }, InputColumn.Menu, true);
        Check(!exact.ExactEdges && exact.Pressed(Buttons.Menu, default), "Painted menu cell generates a press from recorded exact input");
        var movie = new Movie();
        for (int i = 0; i < 5; i++) movie.Frames.Add(new MovieFrame { Input = input, Expected = new StateProbe(), FixedTicks = 1 });
        FrameCellEdit.Paint(movie, 1, 2, InputColumn.Dash, true, 0);
        Check(movie.Frames[1].Input.Down(Buttons.Dash) && movie.Frames[2].Input.Down(Buttons.Dash) && !movie.Frames[3].Input.Down(Buttons.Dash), "Stroke covers inclusive rows only");
        Check(movie.Frames[0].Expected != null && movie.Frames[1].Expected == null && movie.Frames[4].FixedTicks == -1, "Only state suffix is invalidated");
        Check(movie.Frames[2].Input.X == -.4f && movie.Frames[2].Input.Down(Buttons.Jump2), "Painting does not replace whole frame");
        FrameCellEdit.Paint(movie, 5, 2, InputColumn.Up, true, 0);
        Check(movie.Frames.Count == 7 && movie.Frames[6].Input.Y == 1, "Painting at end appends planned inputs");
        foreach (var value in new[] { float.NaN, float.PositiveInfinity, 2f })
        {
            try { FrameCellEdit.Paint(movie, 7, 1, InputColumn.X, false, value); throw new Exception("Invalid axis accepted"); }
            catch (ArgumentException) { Check(movie.Frames.Count == 7, "Rejected axis does not extend movie"); }
        }
        try { FrameCellEdit.Paint(movie, 8, 1, InputColumn.Dash, true, 0); throw new Exception("Gap accepted"); }
        catch (ArgumentOutOfRangeException) { checks++; }
        Console.WriteLine("PASS: " + checks + " frame-cell editing checks.");
    }
}
