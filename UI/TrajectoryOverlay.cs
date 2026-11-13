using Igtap.Tas.Core;
using Igtap.Tas.Runtime;
using UnityEngine;

namespace Igtap.Tas.UI
{
    // Presentation only. No colliders, scripts or ghost player are cloned into
    // the gameplay world. Hidden objects are excluded from simulator exports.
    internal sealed class TrajectoryOverlay : MonoBehaviour
    {
        private TrajectoryController _preview;
        private TasSession _session;
        private LineRenderer _path, _next, _end;
        private Material _material;
        private FloatingOrigin _origin;
        private Vector3[] _positions;
        private TrajectoryResult _drawn;
        private int _drawnOffset;
        private Vector3 _drawnOrigin;
        private float _drawnWidth, _drawnZ;
        private double _nextDraw;
        private Movement _player;
        private Rigidbody2D _body;
        private Camera _camera;
        internal void Initialize(TrajectoryController preview, TasSession session)
        {
            _preview = preview; _session = session;
            var shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default") ?? Shader.Find("Sprites/Default");
            if (shader != null) _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _path = Line("Trajectory", new Color(.1f, .9f, 1, .85f));
            _next = Line("Next frame", Color.white); _end = Line("Trajectory end", Color.yellow);
        }
        private LineRenderer Line(string name, Color color)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            go.transform.SetParent(transform, false);
            var line = go.AddComponent<LineRenderer>(); line.sharedMaterial = _material;
            line.useWorldSpace = true; line.startColor = line.endColor = color;
            line.numCapVertices = 3; line.numCornerVertices = 2; line.sortingOrder = 32760;
            line.positionCount = 0; return line;
        }
        private Vector3 At(MotionVector p, float z)
        {
            Vector3 shift = _origin != null ? _origin.currentOrigin : Vector3.zero;
            return new Vector3(p.X + shift.x, p.Y + shift.y, z);
        }
        private void LateUpdate()
        {
            if (_path == null) return;
            using var timing = _session.Profiler.Measure(ProfileSection.TrajectoryDraw);
            var result = _preview.Visible;
            var player = _session.Adapter.Player;
            if (!_preview.Enabled || result == null || player == null || !_session.Active)
            { Clear(); return; }
            if (_origin == null) _origin = Object.FindFirstObjectByType<FloatingOrigin>();
            if (_camera == null) _camera = Camera.main;
            var camera = _camera;
            // Keep lines approximately two screen pixels wide at any zoom.
            float width = camera != null && camera.orthographic ? 4 * camera.orthographicSize / Mathf.Max(1, camera.pixelHeight) : 2;
            if (_player != player) { _player=player; _body=player.GetComponent<Rigidbody2D>(); }
            var body = _body;
            var shift = _origin != null ? _origin.currentOrigin : Vector3.zero;
            // End() commits the cursor after LateUpdate. While running the body
            // already represents this open frame; draw the following trajectory.
            int frame = _session.Cursor + (_session.FrameOpen ? 1 : 0);
            int offset = result.Offset(frame, _session.PreviewInput(out _),
                new MotionVector(body.position.x - shift.x, body.position.y - shift.y), new MotionVector(body.linearVelocity.x, body.linearVelocity.y));
            if (offset < 0) { Clear(); return; }
            float z = player.transform.position.z;
            int count = result.Points.Length - offset;
            _next.widthMultiplier = width * 1.5f;
            // The immediate next-frame marker stays current even while the
            // longer line is reused between 30 Hz geometry uploads.
            if (count > 1) Cross(_next, At(result.Points[offset + 1].Motion.Position, z), width * 3);
            else _next.positionCount = 0;
            bool same = ReferenceEquals(_drawn, result) && _drawnOrigin == shift && _drawnWidth == width && _drawnZ == z;
            if (same && (_drawnOffset == offset || !_session.Frozen && Time.realtimeSinceStartupAsDouble < _nextDraw)) return;
            _nextDraw=Time.realtimeSinceStartupAsDouble + 1.0/30;
            _drawn = result; _drawnOffset = offset; _drawnOrigin = shift; _drawnWidth = width; _drawnZ = z;
            _path.widthMultiplier = width; _next.widthMultiplier = _end.widthMultiplier = width * 1.5f;
            // SetPositions ignores entries beyond positionCount. Keep capacity
            // while the path shrinks instead of allocating every moving frame.
            if (_positions == null || _positions.Length < count) _positions = new Vector3[count];
            int used=0;
            for (int i = 0; i < count; i++)
            {
                var at=At(result.Points[offset+i].Motion.Position,z);
                // Non-PU frames often share a body position. They still exist
                // in the result/stats, but need no duplicate drawing vertices.
                if (used==0 || at!=_positions[used-1]) _positions[used++]=at;
            }
            _path.positionCount = used;
            _path.SetPositions(_positions);
            var color = result.End == "death" ? Color.red : result.End == "unsupported" ? new Color(1, .6f, .1f) : Color.yellow;
            _end.startColor = _end.endColor = color;
            Cross(_end, At(result.Points[result.Points.Length - 1].Motion.Position, z), width * 4);
        }
        private static void Cross(LineRenderer line, Vector3 at, float size)
        {
            line.positionCount = 5;
            line.SetPosition(0, at + new Vector3(-size, -size)); line.SetPosition(1, at + new Vector3(size, size));
            line.SetPosition(2, at); line.SetPosition(3, at + new Vector3(-size, size)); line.SetPosition(4, at + new Vector3(size, -size));
        }
        private void Clear() { _drawn = null; if (_path != null) _path.positionCount = _next.positionCount = _end.positionCount = 0; }
        private void OnDisable() => Clear();
        private void OnDestroy() { if (_material != null) Object.Destroy(_material); }
    }
}
