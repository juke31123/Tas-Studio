using Igtap.Tas.Runtime;
using UnityEngine;

namespace Igtap.Tas.UI
{
    // Persistent lifecycle host only. All run-editing UI lives in native windows.
    internal sealed class TasDesktopHost : MonoBehaviour
    {
        private TasSession _session;
        private DesktopBridge _desktop;
        private readonly PlayerStatsMonitor _stats = new PlayerStatsMonitor();
        private TrajectoryController _trajectory;
        private TrajectoryOverlay _overlay;
        public void Initialize(TasSession session)
        {
            _session = session; _trajectory = new TrajectoryController(session); session.Trajectory = _trajectory;
            _overlay = gameObject.AddComponent<TrajectoryOverlay>(); _overlay.Initialize(_trajectory, session); _overlay.enabled = false;
        }
        public void OpenDesktop(int page = 0)
        {
            if (_desktop == null) _desktop = new DesktopBridge(_session);
            _desktop.Open(page);
        }
        public void PumpDesktop()
        {
            if (_session.DesktopPageRequest >= 0)
            {
                int page = _session.DesktopPageRequest; _session.DesktopPageRequest = -1;
                try { OpenDesktop(page); }
                catch (System.Exception e) { Debug.LogError("Tas Studio desktop: " + e.Message); }
            }
            _desktop?.Early();
        }
        public void RefreshPlayerStats()
        {
            using (_session.Profiler.Measure(Core.ProfileSection.Trajectory)) _trajectory?.Tick();
            if (_overlay != null && _overlay.enabled != (_trajectory.Enabled && _trajectory.Visible != null))
                _overlay.enabled = _trajectory.Enabled && _trajectory.Visible != null;
            if (_desktop?.SampleDue != true) return;
            using (_session.Profiler.Measure(Core.ProfileSection.PlayerStats)) _stats.Refresh(_session, true);
            _desktop.Publish(_stats);
        }
        private void OnDisable()
        {
            _desktop?.Dispose(); _desktop = null;
            _trajectory?.Configure(false, _trajectory.Horizon);
        }
        private void OnDestroy() { _trajectory?.Dispose(); if (_session != null) _session.Trajectory = null; }
    }
}
