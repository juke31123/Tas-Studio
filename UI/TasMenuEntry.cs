using System;
using Recharge.ModApi;

namespace Igtap.Tas.UI
{
    // Recharge recreates the pause menu with each scene. Register against the
    // current menu and keep our own event delegate so unload can unsubscribe.
    internal sealed class TasMenuEntry : IDisposable
    {
        private readonly IRechargeHost _host;
        private readonly Action<object> _sceneLoaded;
        private readonly Func<string> _label;
        private Action _toggle;

        public TasMenuEntry(IRechargeHost host, Func<string> label, Action toggle)
        {
            _host = host; _label = label; _toggle = toggle;
            _sceneLoaded = _ => Install(PauseMenuHelper.FindMenu());
            Install(host.PauseMenu);
            host.Events.On("recharge.scene_loaded", _sceneLoaded);
        }
        public void Refresh() => Install(PauseMenuHelper.FindMenu());
        private void Install(pauseMenuScript menu)
        {
            if (menu == null || _toggle == null) return;
            // AddRow upserts by name, preventing duplicates after a scene reload.
            PauseMenuHelper.AddRow(menu, "IgtapTasStudio", _label(), () => {
                try { _toggle?.Invoke(); }
                finally
                {
                    // Recharge assumes each row opens a panel and hides mainBit
                    // before invoking us. A toggle must keep this menu visible.
                    if (menu != null) PauseMenuHelper.MainBit(menu)?.SetActive(true);
                }
            });
        }
        public void Dispose()
        {
            _toggle = null; // Any cached menu callback becomes harmless.
            _host.Events.Off("recharge.scene_loaded", _sceneLoaded);
        }
    }
}
