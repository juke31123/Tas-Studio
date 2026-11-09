using System;
using System.Collections.Generic;

namespace Igtap.Tas.Core.Search
{
    // Swept AABB against exported convex polygons. Conservative static geometry
    // backend, independent of Unity. A future Box2D provider replaces this class.
    internal sealed class StaticCollisionWorld
    {
        private sealed class Shape
        {
            public SearchBody Source;
            public MotionVector[] Normals;
            public float[] Limits;
        }
        private readonly Shape[] _shapes;
        private readonly float _offsetX, _offsetY;
        private readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();
        private readonly List<int> _large = new List<int>();
        private readonly int[] _seen, _candidates;
        private int _stamp;
        private const float CellSize = 256;
        private static long Cell(int x, int y) => ((long)x << 32) | (uint)y;
        public StaticCollisionWorld(SearchJob job)
        {
            _offsetX = job.OffsetX; _offsetY = job.OffsetY;
            _shapes = new Shape[job.Geometry.Length];
            _seen = new int[_shapes.Length]; _candidates = new int[_shapes.Length];
            for (int i = 0; i < _shapes.Length; i++)
            {
                var source = job.Geometry[i]; var vertices = source.Vertices;
                if (vertices.Length == 0) vertices = new[] { new MotionVector(source.MinX, source.MinY), new MotionVector(source.MaxX, source.MinY),
                    new MotionVector(source.MaxX, source.MaxY), new MotionVector(source.MinX, source.MaxY) };
                double area = 0;
                for (int k = 0; k < vertices.Length; k++) { var a = vertices[k]; var b = vertices[(k + 1) % vertices.Length]; area += a.X * (double)b.Y - b.X * (double)a.Y; }
                var shape = _shapes[i] = new Shape { Source = source, Normals = new MotionVector[vertices.Length + 4], Limits = new float[vertices.Length + 4] };
                for (int k = 0; k < vertices.Length; k++)
                {
                    var a = vertices[k]; var b = vertices[(k + 1) % vertices.Length];
                    var normal = area >= 0 ? new MotionVector(b.Y - a.Y, a.X - b.X) : new MotionVector(a.Y - b.Y, b.X - a.X);
                    float length = normal.Magnitude;
                    if (length < 1e-6f) { shape.Limits[k] = float.MaxValue; continue; }
                    normal *= 1 / length; shape.Normals[k] = normal;
                    shape.Limits[k] = normal.X * a.X + normal.Y * a.Y + job.HalfWidth * Math.Abs(normal.X) + job.HalfHeight * Math.Abs(normal.Y) + source.Radius;
                }
                int n = vertices.Length;
                shape.Normals[n] = new MotionVector(1, 0); shape.Limits[n] = source.MaxX + job.HalfWidth + source.Radius;
                shape.Normals[n + 1] = new MotionVector(-1, 0); shape.Limits[n + 1] = -source.MinX + job.HalfWidth + source.Radius;
                shape.Normals[n + 2] = new MotionVector(0, 1); shape.Limits[n + 2] = source.MaxY + job.HalfHeight + source.Radius;
                shape.Normals[n + 3] = new MotionVector(0, -1); shape.Limits[n + 3] = -source.MinY + job.HalfHeight + source.Radius;
                int x0 = (int)Math.Floor((source.MinX - job.HalfWidth - source.Radius) / CellSize), x1 = (int)Math.Floor((source.MaxX + job.HalfWidth + source.Radius) / CellSize);
                int y0 = (int)Math.Floor((source.MinY - job.HalfHeight - source.Radius) / CellSize), y1 = (int)Math.Floor((source.MaxY + job.HalfHeight + source.Radius) / CellSize);
                if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 4096) _large.Add(i);
                else for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++)
                { long key = Cell(x, y); if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new List<int>(); list.Add(i); }
            }
        }
        private int Candidates(MotionVector from, MotionVector delta)
        {
            if (++_stamp == int.MaxValue) { Array.Clear(_seen, 0, _seen.Length); _stamp = 1; }
            var to = from + delta; int count = 0;
            int x0 = (int)Math.Floor(Math.Min(from.X, to.X) / CellSize), x1 = (int)Math.Floor(Math.Max(from.X, to.X) / CellSize);
            int y0 = (int)Math.Floor(Math.Min(from.Y, to.Y) / CellSize), y1 = (int)Math.Floor(Math.Max(from.Y, to.Y) / CellSize);
            if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 4096)
            { for (int i = 0; i < _shapes.Length; i++) _candidates[count++] = i; return count; }
            void Add(int i) { if (_seen[i] != _stamp) { _seen[i] = _stamp; _candidates[count++] = i; } }
            for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++)
                if (_cells.TryGetValue(Cell(x, y), out var list)) foreach (int i in list) Add(i);
            foreach (int i in _large) Add(i);
            Array.Sort(_candidates, 0, count); // Preserve export order for ties.
            return count;
        }
        private static float Dot(MotionVector a, MotionVector b) => a.X * b.X + a.Y * b.Y;
        private static bool Hit(Shape shape, MotionVector from, MotionVector delta, out float time, out MotionVector normal, out bool penetrating)
        {
            float entry = 0, exit = 1, nearest = float.MinValue; normal = default; bool enters = false; penetrating = true;
            for (int i = 0; i < shape.Normals.Length; i++)
            {
                var n = shape.Normals[i]; float d = Dot(from, n) - shape.Limits[i], speed = Dot(delta, n);
                nearest = Math.Max(nearest, d); if (d >= -.02f) penetrating = false;
                if (Math.Abs(speed) < 1e-8f) { if (d > .001f) { time = 0; return false; } continue; }
                float t = -d / speed;
                if (speed < 0) { if (t >= entry - 1e-6f) { entry = Math.Max(0, t); normal = n; enters = true; } }
                else exit = Math.Min(exit, t);
                if (entry > exit + 1e-6f) { time = 0; return false; }
            }
            time = entry;
            return penetrating || enters && entry <= 1 && exit >= 0 && Dot(delta, normal) < -1e-7f;
        }
        public bool Move(PlayerMotion player, MotionVector wanted, out string unsupported)
        {
            unsupported = null; player.Grounded = player.OnWall = false;
            var from = player.Position + new MotionVector(_offsetX, _offsetY);
            var delta = wanted + new MotionVector(_offsetX, _offsetY) + from * -1;
            for (int pass = 0; pass < 4; pass++)
            {
                float earliest = 1; Shape chosen = null; MotionVector normal = default;
                int candidates = Candidates(from, delta);
                for (int index = 0; index < candidates; index++)
                {
                    var shape = _shapes[_candidates[index]];
                    if (!Hit(shape, from, delta, out float time, out var hitNormal, out bool penetrating)) continue;
                    if (shape.Source.Kind == "unsupported") { unsupported = "Unsupported contact: " + shape.Source.Id; return false; }
                    if (shape.Source.Kind == "hazard") { player.Dead = true; return false; }
                    if (penetrating) { unsupported = "Unmodelled penetration: " + shape.Source.Id; return false; }
                    if (chosen == null || time < earliest) { earliest = time; chosen = shape; normal = hitNormal; }
                }
                if (chosen == null) { from += delta; break; }
                from += delta * earliest;
                delta *= 1 - earliest;
                float into = Dot(delta, normal); if (into < 0) delta += normal * -into;
                float velocityInto = Dot(player.Velocity, normal); if (velocityInto < 0) player.Velocity += normal * -velocityInto;
                float controlInto = Dot(player.ControlVelocity, normal); if (controlInto < 0) player.ControlVelocity += normal * -controlInto;
                if (normal.Y > .5f) player.Grounded = true;
                if (Math.Abs(normal.X) > .5f) player.OnWall = true;
                if (delta.Magnitude < .00001f) break;
            }
            // Contact persistence when resting on a floor with zero vertical speed.
            if (player.Velocity.Y <= 0)
            {
                int candidates = Candidates(from, new MotionVector(0, -.05f));
                for (int index = 0; index < candidates; index++)
                {
                    var shape = _shapes[_candidates[index]];
                    if (shape.Source.Kind == "solid" && Hit(shape, from, new MotionVector(0, -.05f), out _, out var normal, out _) && normal.Y > .5f) player.Grounded = true;
                }
            }
            player.Position = from + new MotionVector(-_offsetX, -_offsetY);
            return true;
        }
    }
}
