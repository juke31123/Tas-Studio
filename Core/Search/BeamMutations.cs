using System;
using System.Linq;

namespace Igtap.Tas.Core.Search
{
    public sealed partial class SectionSearch
    {
        private static Node Resume(Route route, int firstChanged)
        {
            Node resume = route.Nodes[0];
            foreach (var node in route.Nodes) { if (node.Depth > firstChanged) break; resume = node; }
            return resume;
        }
        // Preserve explicit taps/retaps while recomputing ordinary transitions
        // at an edited join. Unchanged rows retain their original exact edges.
        internal static FrameInput RebaseInput(FrameInput row, FrameInput oldPrevious, FrameInput newPrevious, Buttons edited = Buttons.None)
        {
            if (!row.ExactEdges) return row;
            if (edited == Buttons.None && oldPrevious.Held == newPrevious.Held) return row;
            Buttons tap = row.Press & row.Release;
            Buttons press = ((row.Press & ~(row.Held & ~oldPrevious.Held)) | tap) & ~edited;
            Buttons release = ((row.Release & ~(oldPrevious.Held & ~row.Held)) | tap) & ~edited;
            if ((press | release) == 0) { row.ExactEdges = false; row.Press = row.Release = 0; return row; }
            row.Press = (row.Held & ~newPrevious.Held) | press;
            row.Release = (newPrevious.Held & ~row.Held) | release;
            return row;
        }
        private static Candidate Mutate(Route route, int serial, Random random, SearchJob job, Archive archive)
        {
            int length = route.Inputs.Length;
            if (length == 0) return null;
            var rows = (FrameInput[])route.Inputs.Clone(); int first = 0;
            if (serial < 9)
            {
                for (int i = 0; i < rows.Length; i++)
                {
                    var row = rows[i];
                    if (serial < 3) row.Y = serial - 1;
                    else if (serial < 6) row.X = serial == 3 ? 0 : serial == 4 ? -1 : 1;
                    else { row.X = serial == 6 ? 0 : serial == 7 ? 1 : -1; row.Y = -1; }
                    rows[i] = row;
                }
            }
            else if (serial < 33)
            {
                // Try ordinary input changes near blocked movement before
                // spending every refinement on uniformly random timestamps.
                var contacts = route.Nodes.Where(n => n.Depth > 0 && n.Depth < length &&
                    (n.State.Player.OnWall || Math.Abs(n.State.Player.Velocity.X) < 5 && Math.Abs(route.Inputs[n.Depth].X) > .5f)).ToArray();
                if (contacts.Length == 0) return Mutate(route, serial + 33, random, job, archive);
                int choice = serial - 9;
                var contact = contacts[(choice / 4) % contacts.Length];
                first = Math.Max(0, contact.Depth - (choice % 3 + 1));
                int duration = Math.Min(length - first, 1 << (3 + choice % 4));
                float x = (choice & 1) == 0 ? 0 : -Math.Sign(route.Inputs[first].X);
                for (int i = first; i < first + duration; i++) { var row = rows[i]; row.X = x; if (choice % 3 == 0) row.Y = -1; rows[i] = row; }
            }
            else if(serial < 65)
            {
                // Systematic coarse-to-fine cuts find long idle/contact waits
                // without relying on randomly choosing the right timestamp.
                int choice=serial-33,span=Math.Max(1,length>>(2+choice/8));
                first=Math.Min(length-span,(choice%8)*length/8);
                rows=route.Inputs.Take(first).Concat(route.Inputs.Skip(first+span)).ToArray();
                if(first<rows.Length)rows[first]=RebaseInput(rows[first],route.Inputs[first+span-1],first==0?job.Start.Previous:rows[first-1]);
            }
            else if(serial < 97)
            {
                var boundaries=Enumerable.Range(1,length-1).Where(i => route.Inputs[i].Held!=route.Inputs[i-1].Held ||
                    route.Inputs[i].X!=route.Inputs[i-1].X || route.Inputs[i].Y!=route.Inputs[i-1].Y).ToArray();
                if(boundaries.Length==0)return Mutate(route,serial+64,random,job,archive);
                int choice=serial-65,boundary=boundaries[(choice/8)%boundaries.Length],shift=(choice%4)+1;
                bool earlier=(choice/4)%2==0;
                first=earlier?Math.Max(0,boundary-shift):boundary;
                int end=earlier?boundary:Math.Min(length,boundary+shift);
                var replacement=route.Inputs[earlier?boundary:boundary-1];
                for(int i=first;i<end;i++)rows[i]=new FrameInput{X=replacement.X,Y=replacement.Y,Held=replacement.Held};
                if(end<length)rows[end]=RebaseInput(rows[end],route.Inputs[end-1],rows[end-1]);
            }
            else if (serial < 225)
            {
                // The coarse cuts above can miss a one-frame saving on a
                // long route. Sweep fine cuts too, restarting on every new
                // best so several independent waits can be removed in turn.
                int choice = serial - 97;
                first = (int)((long)choice * (length - 1) / 127);
                rows = route.Inputs.Take(first).Concat(route.Inputs.Skip(first + 1)).ToArray();
                if (first < rows.Length) rows[first] = RebaseInput(rows[first], route.Inputs[first], first == 0 ? job.Start.Previous : rows[first - 1]);
            }
            else
            {
                first = random.Next(length);
                int span = Math.Min(length - first, 1 << random.Next(0, 7));
                switch (serial % 6)
                {
                    case 0:
                        float x = random.Next(-1, 2), y = random.Next(-1, 2);
                        for (int i = first; i < first + span; i++) { var row = rows[i]; row.X = x; row.Y = y; rows[i] = row; }
                        break;
                    case 1:
                        if (span >= length) return null;
                        rows = route.Inputs.Take(first).Concat(route.Inputs.Skip(first + span)).ToArray();
                        if (first < rows.Length) rows[first] = RebaseInput(rows[first], route.Inputs[first + span - 1], first == 0 ? job.Start.Previous : rows[first - 1]);
                        break;
                    case 2:
                        Buttons allowed = ((job.Options.Abilities & SearchAbilities.Jump) != 0 ? Buttons.Jump : 0) | ((job.Options.Abilities & SearchAbilities.Dash) != 0 ? Buttons.Dash : 0);
                        if (allowed == 0) { var row = rows[first]; row.X = random.Next(-1, 2); row.Y = random.Next(-1, 2); rows[first] = row; break; }
                        Buttons button = (allowed & Buttons.Dash) != 0 && ((allowed & Buttons.Jump) == 0 || random.Next(2) == 0) ? Buttons.Dash : Buttons.Jump;
                        int end = Math.Min(length, first + Math.Min(span, Math.Max(1, job.Fps / 10)));
                        bool hold = !rows[first].Down(button);
                        for (int i = first; i < end; i++)
                        {
                            var row = rows[i]; row.Held = hold ? row.Held | button : row.Held & ~button;
                            // Explicit events for other buttons survive this edit.
                            if (row.ExactEdges)
                            {
                                row.Press &= ~button; row.Release &= ~button;
                                var previous = i == 0 ? job.Start.Previous : rows[i - 1];
                                row.Press |= row.Held & ~previous.Held & button; row.Release |= previous.Held & ~row.Held & button;
                            }
                            rows[i] = row;
                        }
                        if (end < length) rows[end] = RebaseInput(rows[end], route.Inputs[end - 1], rows[end - 1]);
                        break;
                    case 3:
                        int extra = Math.Min(job.Options.FrameLimit - length, Math.Min(span, 8));
                        if (extra <= 0) return null;
                        var coast = new FrameInput { X = rows[first].X, Y = rows[first].Y, Held = rows[first].Held };
                        rows = route.Inputs.Take(first).Concat(Enumerable.Repeat(coast, extra)).Concat(route.Inputs.Skip(first)).ToArray();
                        rows[first + extra] = RebaseInput(rows[first + extra], first == 0 ? job.Start.Previous : route.Inputs[first - 1], rows[first + extra - 1]);
                        break;
                    case 4:
                        int join = random.Next(1, Math.Max(2, length));
                        if (join >= length) return null;
                        var shortcut = archive.Shortcut(route.Path[join], join, random,archive.CheckpointAt(route.Path,join));
                        if (shortcut == null) return null;
                        var prefix = BuildRoute(shortcut).Inputs;
                        rows = prefix.Concat(route.Inputs.Skip(join)).ToArray();
                        rows[prefix.Length] = RebaseInput(rows[prefix.Length], route.Inputs[join - 1], prefix[prefix.Length - 1]);
                        return new Candidate { Inputs = rows, Resume = shortcut, Splice = true };
                    default:
                        // Move a run boundary by one to four frames; this can
                        // change an input on any frame, including between PUs.
                        if (first == 0) first = 1;
                        if (first >= length) return null;
                        int count = Math.Min(length - first, random.Next(1, 5));
                        var earlier = route.Inputs[first - 1];
                        for (int i = first; i < first + count; i++) rows[i] = new FrameInput { X = earlier.X, Y = earlier.Y, Held = earlier.Held };
                        if (first + count < length) rows[first + count] = RebaseInput(rows[first + count], route.Inputs[first + count - 1], rows[first + count - 1]);
                        break;
                }
            }
            while (first < Math.Min(rows.Length, route.Inputs.Length) && SameRow(rows[first], route.Inputs[first])) first++;
            if (first == rows.Length && rows.Length == route.Inputs.Length) return null;
            return new Candidate { Inputs = rows, Resume = Resume(route, first) };
        }
    }
}
