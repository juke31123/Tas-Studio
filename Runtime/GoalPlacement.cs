using System;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Search;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Igtap.Tas.Runtime
{
    // Free camera only: the player, Rigidbody2D, collisions and movie input never
    // move. Restore the original view on close, cancel, stop, disable or unload.
    internal sealed class GoalPlacement : IGoalPlacement
    {
        private readonly TasSession _session;
        private Camera _camera;
        private Vector3 _position;
        private Quaternion _rotation;
        private float _size;
        private double _last;
        public bool Active { get; private set; }
        public GoalPlacement(TasSession session) { _session = session; }
        public void Begin()
        {
            if (Active) return;
            if (!_session.CanEditTimeline || !_session.Active || !_session.Frozen || _session.IsMainMenu)
                throw new InvalidOperationException("Pause the TAS in gameplay before placing its goal.");
            if (_session.Adapter.Player.pauseMenu?.menuOpen == true || _session.Adapter.Player.GamePaused)
                throw new InvalidOperationException("Close the in-game pause menu before selecting a goal.");
            _camera = Camera.main;
            if (_camera == null || !_camera.orthographic) throw new InvalidOperationException("A gameplay orthographic camera is required.");
            _position = _camera.transform.position; _rotation = _camera.transform.rotation; _size = _camera.orthographicSize;
            _last = Time.realtimeSinceStartupAsDouble; Active = true;
        }
        public void End()
        {
            if (!Active) return;
            Active = false;
            if (_camera != null) { _camera.transform.position = _position; _camera.transform.rotation = _rotation; _camera.orthographicSize = _size; }
            _camera = null;
        }
        public void Pan(float x, float y)
        {
            if (Active && _camera != null) _camera.transform.position += new Vector3(x, y, 0) * (_camera.orthographicSize * .12f);
        }
        public void Tick()
        {
            if (!Active) return;
            if (!_session.Active || !_session.Frozen || _session.WaitingForLoad || _camera == null) { End(); return; }
            double now = Time.realtimeSinceStartupAsDouble; float dt = (float)Math.Max(0, Math.Min(.05, now - _last)); _last = now;
            if (!Application.isFocused || _session.SuppressHotkeys) return;
            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.escapeKey.wasPressedThisFrame) { End(); return; }
                var keys = _session.Search.CameraBindings.Keys;
                bool Held(string name) => Enum.TryParse<Key>(name, out var key) && keyboard[key].isPressed;
                float x = (Held(keys[3]) ? 1 : 0) - (Held(keys[2]) ? 1 : 0), y = (Held(keys[0]) ? 1 : 0) - (Held(keys[1]) ? 1 : 0);
                _camera.transform.position += new Vector3(x, y, 0) * (_camera.orthographicSize * 1.5f * dt);
            }
            var mouse = Mouse.current;
            if (mouse == null) return;
            float wheel = mouse.scroll.ReadValue().y;
            if (wheel != 0) _camera.orthographicSize = Mathf.Clamp(_camera.orthographicSize * (wheel > 0 ? .9f : 1.1f), 20, 5000);
            if (mouse.leftButton.wasPressedThisFrame)
            {
                Vector2 screen = mouse.position.ReadValue();
                if (screen.x < 0 || screen.y < 0 || screen.x > Screen.width || screen.y > Screen.height) return;
                Vector3 world = _camera.ScreenToWorldPoint(new Vector3(screen.x, screen.y, -_camera.transform.position.z));
                var origin = UnityEngine.Object.FindFirstObjectByType<FloatingOrigin>();
                if (origin != null) world -= origin.currentOrigin;
                _session.Search.SetGoal(world.x, world.y); End();
            }
        }
        public void Dispose() => End();
    }
}
