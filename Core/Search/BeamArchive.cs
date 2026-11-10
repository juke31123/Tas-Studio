using System;
using System.Collections.Generic;
using System.Linq;

namespace Igtap.Tas.Core.Search
{
    public sealed partial class SectionSearch
    {
        // Quantized diversity, not a cache key for reusing simulation results.
        private readonly struct Variant : IEquatable<Variant>
        {
            private readonly int _x, _y, _vx, _vy, _mx, _my, _dash, _jump, _wall, _mode, _cooldown, _lock, _coyote, _dashTime, _ix, _iy, _held, _edges, _scale, _epoch, _phaseGroup, _checkpoint;
            private readonly long _phase;
            internal Variant(Node node, float quantum, float velocityQuantum)
            {
                var s = node.State; var p = s.Player;
                _x = (int)Math.Floor(p.Position.X / quantum); _y = (int)Math.Floor(p.Position.Y / quantum);
                _vx = (int)Math.Floor(p.Velocity.X / velocityQuantum); _vy = (int)Math.Floor(p.Velocity.Y / velocityQuantum);
                _mx = (int)Math.Floor(p.Momentum.X / 250); _my = (int)Math.Floor(p.Momentum.Y / 250);
                _dash = p.AirDashes; _jump = p.AirJumps; _wall = s.WallJumps;
                _mode = (p.Grounded ? 1 : 0) | (p.OnWall ? 2 : 0) | (p.Dashing ? 4 : 0) | (p.FacingRight ? 8 : 0) |
                    (p.JumpBuffered ? 16 : 0) | (p.DashBuffered ? 32 : 0) | (p.JumpCutBuffered ? 64 : 0) | (p.RecentlyJumped ? 128 : 0) | (p.Cutscene << 8);
                _cooldown = (int)Math.Floor(p.DashCooldown * 100); _lock = (int)Math.Floor(p.WallJumpLock);
                _coyote = (int)Math.Floor(p.WallCoyoteFrames); _dashTime = (int)Math.Floor(p.DashFramesRemaining);
                _ix = BitConverter.SingleToInt32Bits(s.Previous.X); _iy = BitConverter.SingleToInt32Bits(s.Previous.Y);
                _held = (int)s.Previous.Held; _edges = (int)s.Previous.Press | ((int)s.Previous.Release << 8) | (s.Previous.ExactEdges ? 1 << 16 : 0);
                _scale = BitConverter.SingleToInt32Bits(s.Scale); _phase = BitConverter.DoubleToInt64Bits(s.Phase); _epoch = node.Depth / 32;
                _phaseGroup = (int)Math.Round(s.Phase * 1000000);
                _checkpoint=s.Checkpoint;
            }
            internal bool SameMotion(Variant other) => _mode == other._mode && _dash == other._dash && _jump == other._jump && _wall == other._wall &&
                _mx == other._mx && _my == other._my && Math.Sign(_vy) == Math.Sign(other._vy) && _phaseGroup == other._phaseGroup && _scale == other._scale && _checkpoint==other._checkpoint;
            public bool Equals(Variant o) => _x == o._x && _y == o._y && _vx == o._vx && _vy == o._vy && _mx == o._mx && _my == o._my &&
                _dash == o._dash && _jump == o._jump && _wall == o._wall && _mode == o._mode && _cooldown == o._cooldown && _lock == o._lock &&
                _coyote == o._coyote && _dashTime == o._dashTime && _ix == o._ix && _iy == o._iy && _held == o._held && _edges == o._edges &&
                _scale == o._scale && _phase == o._phase && _epoch == o._epoch && _checkpoint==o._checkpoint;
            public override bool Equals(object value) => value is Variant other && Equals(other);
            public override int GetHashCode()
            {
                unchecked { int h = _x * 397 ^ _y; h = h * 31 + _vx; h = h * 31 + _vy; h = h * 31 + _mx; h = h * 31 + _my;
                    h = h * 31 + _dash; h = h * 31 + _jump; h = h * 31 + _wall; h = h * 31 + _mode; h = h * 31 + _cooldown;
                    h = h * 31 + _lock; h = h * 31 + _coyote; h = h * 31 + _dashTime; h = h * 31 + _ix; h = h * 31 + _iy;
                    h = h * 31 + _held; h = h * 31 + _edges; h = h * 31 + _scale; h = h * 31 + _phase.GetHashCode(); h=h*31+_checkpoint; return h * 31 + _epoch; }
            }
        }
        private sealed class Archive
        {
            private sealed class Cell { internal readonly List<Entry> Entries = new List<Entry>(); internal int Expansions; }
            private sealed class Entry { internal Node Node; internal Variant Variant; internal bool Expanded; internal Cell Cell; }
            private readonly Dictionary<(int, int, bool, int), Cell> _cells = new Dictionary<(int, int, bool, int), Cell>();
            private readonly float _cellSize;
            private readonly SearchJob _job;
            private readonly CheckpointRoute _route;
            private readonly int _capacity, _width;
            private int _count;
            internal int Cells => _cells.Count;
            internal int Pending => _cells.Values.Sum(c => c.Entries.Count(e => !e.Expanded));
            internal Archive(SearchJob job)
            {
                _job = job;
                _route=new CheckpointRoute(job.Options);
                _cellSize = Math.Max(4, Math.Max(job.HalfWidth, job.HalfHeight) * 2);
                _capacity = Math.Max(512, Math.Min(8192, job.Options.BeamWidth * 32)); _width = job.Options.BeamWidth;
            }
            private static double Retention(Node node)
            {
                var p = node.State.Player;
                // Arrival time breaks ties in the exact diversity label. A
                // time penalty here would discard all slow forward progress.
                return node.Score - node.Depth * .015 - p.AirDashes * 8 - p.AirJumps * 5 - Math.Sqrt(p.Momentum.X * p.Momentum.X + p.Momentum.Y * p.Momentum.Y) * .015;
            }
            internal void Offer(Node node)
            {
                var p = node.State.Player;
                if (!Movie.Finite(p.Position.X) || !Movie.Finite(p.Position.Y) || p.Dead) return;
                bool near = _route.DistanceToNext(node.State) <= _cellSize * 2;
                float size = near ? Math.Min(_cellSize, Math.Max(2, _route.Next(node.State.Checkpoint).Radius * 2)) : _cellSize;
                var key = ((int)Math.Floor(p.Position.X / size), (int)Math.Floor(p.Position.Y / size), near,node.State.Checkpoint);
                if (!_cells.TryGetValue(key, out var cell)) _cells.Add(key, cell = new Cell());
                var variant = new Variant(node, size / 6, near ? 25 : 100);
                var same = cell.Entries.FirstOrDefault(e => e.Variant.Equals(variant));
                if (same != null)
                {
                    // Quantization must not arrest slow motion before the next
                    // position bucket. Permit a genuinely closer continuation
                    // even when it arrives later than the existing representative.
                    bool progresses = node.Depth > same.Node.Depth && Distance(_route, node.State) + .00001 < Distance(_route, same.Node.State);
                    if (progresses || node.Depth < same.Node.Depth || node.Depth == same.Node.Depth && node.Score < same.Node.Score)
                    { same.Node = node; same.Expanded = false; }
                    return;
                }
                if (cell.Entries.Count >= 8)
                {
                    bool newMotion = !cell.Entries.Any(e => e.Variant.SameMotion(variant));
                    var replaceable = newMotion ? cell.Entries.Where(e => cell.Entries.Count(o => o.Variant.SameMotion(e.Variant)) > 1).ToArray() : cell.Entries.ToArray();
                    if (replaceable.Length == 0) replaceable = cell.Entries.ToArray();
                    var worst = replaceable.OrderByDescending(e => Retention(e.Node)).ThenByDescending(e => e.Node.Id).First();
                    if (!newMotion && Retention(node) >= Retention(worst.Node)) return;
                    cell.Entries.Remove(worst); _count--;
                }
                cell.Entries.Add(new Entry { Node = node, Variant = variant, Cell = cell }); _count++;
                if (_count > _capacity + 64) Trim();
            }
            private void Trim()
            {
                var entries = _cells.Values.SelectMany(c => c.Entries)
                    .OrderByDescending(e => (e.Expanded ? _cellSize * 4 : 0) + Retention(e.Node)).ThenByDescending(e => e.Node.Id).ToArray();
                foreach (var entry in entries)
                { if (_count <= _capacity) break; entry.Cell.Entries.Remove(entry); _count--; }
                foreach (var key in _cells.Where(p => p.Value.Entries.Count == 0).Select(p => p.Key).ToArray()) _cells.Remove(key);
            }
            internal List<Node> Select(int count, int round, int beforeFrame)
            {
                double Priority(Entry e)
                {
                    var n = e.Node; var p = n.State.Player;
                    switch (round % 4)
                    {
                        case 1: return e.Cell.Expansions * _cellSize * 2 + n.Score * .15 + n.Depth * .01;
                        case 2: return n.Score - Math.Sqrt(p.Velocity.X * p.Velocity.X + p.Velocity.Y * p.Velocity.Y) * .06;
                        case 3: return n.Score * .4 + n.Depth * .15;
                        default: return n.Score;
                    }
                }
                var ordered = _cells.Values.SelectMany(c => c.Entries).Where(e => !e.Expanded && e.Node.Depth < beforeFrame)
                    .OrderBy(Priority).ThenBy(e => e.Node.Id).Take(Math.Max(_width, count) * 4).ToArray();
                var used = new Dictionary<Cell, int>(); var chosen = new List<Node>(count);
                foreach (var entry in ordered)
                {
                    used.TryGetValue(entry.Cell, out int picked); if (picked >= 2) continue;
                    used[entry.Cell] = picked + 1; entry.Expanded = true; entry.Cell.Expansions++; chosen.Add(entry.Node);
                    if (chosen.Count == count) break;
                }
                return chosen;
            }
            internal Node Shortcut(MotionVector point, int before, Random random,int checkpoint)
            {
                double DistanceTo(Node n) { double x = n.State.Player.Position.X - point.X, y = n.State.Player.Position.Y - point.Y; return x * x + y * y; }
                double radius = Math.Max(4, _cellSize * .6);
                var choices = _cells.Values.SelectMany(c => c.Entries).Select(e => e.Node).Where(n => n.State.Checkpoint==checkpoint && n.Depth > 0 && n.Depth < before && DistanceTo(n) <= radius * radius)
                    .OrderBy(n => n.Depth).ThenBy(DistanceTo).ThenBy(n => n.Id).Take(8).ToArray();
                return choices.Length == 0 ? null : choices[random.Next(choices.Length)];
            }
            internal int CheckpointAt(MotionVector[] path,int frame)
            {int progress=0;for(int i=0;i<=frame;i++)progress=_route.Advance(progress,path[i]);return progress;}
        }
    }
}
