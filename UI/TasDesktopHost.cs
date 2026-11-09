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
        public void Initialize(TasSession session) { _session = session; _session.Search.GoalPlacement = new GoalPlacement(session); }
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
            using (_session.Profiler.Measure(Core.ProfileSection.PlayerStats)) _stats.Refresh(_session, _desktop?.Connected == true);
            _desktop?.Publish(_stats);
        }
        private void OnDisable()
        {
            _session?.Search.GoalPlacement?.End();
            _desktop?.Dispose(); _desktop = null;
        }
    }
}
