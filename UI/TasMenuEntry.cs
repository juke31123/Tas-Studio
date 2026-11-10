using System;
using System.Collections.Generic;
using Igtap.Tas.Core;
using Recharge.ModApi;
using UnityEngine;

namespace Igtap.Tas.UI
{
    // Recharge recreates the pause menu with each scene. Register against the
    // current menu and keep our own event delegate so unload can unsubscribe.
    internal sealed class TasMenuEntry : IDisposable
    {
        private readonly IRechargeHost _host;
        private readonly Action<object> _sceneLoaded;
        private readonly Action<GameObject> _build;
        private readonly List<GameObject> _panels = new List<GameObject>();

        public TasMenuEntry(IRechargeHost host, Action<GameObject> build)
        {
            _host = host; _build = build;
            _sceneLoaded = _ => Install(PauseMenuHelper.FindMenu());
            Install(host.PauseMenu);
            host.Events.On("recharge.scene_loaded", _sceneLoaded);
        }
        public void Refresh() => Install(PauseMenuHelper.FindMenu());
        private void Install(pauseMenuScript menu)
        {
            if (menu == null) return;
            var panel = PauseMenuHelper.AddPanelRow(menu, "IgtapTasStudio", "Tas Studio v" + StudioVersion.Text);
            _panels.RemoveAll(p => p == null);
            if (panel == null || _panels.Contains(panel)) return;
            _panels.Add(panel); _build(panel);
        }
        public void Dispose()
        {
            _host.Events.Off("recharge.scene_loaded", _sceneLoaded);
            foreach (var panel in _panels) if (panel != null) UnityEngine.Object.Destroy(panel);
            _panels.Clear();
        }
    }
}
