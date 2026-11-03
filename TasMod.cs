using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Igtap.Tas.Runtime;
using Igtap.Tas.UI;
using Recharge.ModApi;
using UnityEngine;

namespace Igtap.Tas
{
    public sealed class TasMod : IRechargeMod
    {
        public string Id => "Tas.Studio";
        public string DisplayName => "Tas Studio";
        public Version Version => new Version(0, 3, 3);
        private TasSession _session;
        private GamePatches _patches;
        private LoopHooks _loop;
        private GameObject _ui;
        private TasMenuEntry _menuEntry;
        private IRechargeHost _host;
        private RepeatabilityCheck _check;
        private bool _enabled, _disablePending;

        public void OnLoad(IRechargeHost host)
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveDependency;
            try { Initialize(host); }
            catch { OnUnload(); throw; }
        }
        // Delay JIT of code referencing Harmony until the sibling-DLL resolver exists.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Initialize(IRechargeHost host)
        {
            _host = host;
            _session = new TasSession(host, Id);
            _check = new RepeatabilityCheck(_session);
            _ui = new GameObject("Tas Studio");
            UnityEngine.Object.DontDestroyOnLoad(_ui);
            _ui.AddComponent<TasWindow>().Initialize(_session, _check);
            EnableRuntime();
            _menuEntry = new TasMenuEntry(host, () => _disablePending ? "Tas Studio (stopping...)" : _enabled ? "Tas Studio (enabled)" : "Tas Studio (disabled)", ToggleEnabled);
            host.Log("Tas Studio loaded; " + _patches.PatchedMethods + " runtime hooks installed. F5 shows controls; the Recharge menu entry enables/disables the mod.");
        }
        private void ToggleEnabled()
        {
            if (_disablePending) _disablePending = false;
            else if (_enabled) _disablePending = true;
            else EnableRuntime();
            _menuEntry?.Refresh();
        }
        private void EnableRuntime()
        {
            // Keep the session/movie while disabled, but remove every runtime
            // hook and loop callback. Only the Recharge menu subscription remains.
            try
            {
                _patches = new GamePatches(); _patches.Install(_session);
                _loop = new LoopHooks(Early, End);
                _enabled = true; _disablePending = false;
                _session.ShowWindow = true; _ui.SetActive(true);
            }
            catch { DisableRuntime(); throw; }
        }
        private void Early()
        {
            _session.Early();
            if (!_disablePending) return;
            try
            {
                // Execute at the loop boundary, not inside a menu click. Stop
                // saves the run and retains guards through the full scene reload.
                if (_check.Running) _check.Cancel();
                if (_session.Active) _session.Stop();
            }
            catch (Exception error)
            {
                _disablePending = false;
                _host.LogError("Could not disable Tas Studio safely: " + error.Message);
                _menuEntry?.Refresh();
            }
        }
        private void End()
        {
            _session.End();
            if (_disablePending)
            {
                if (!_session.Active) { DisableRuntime(); _menuEntry?.Refresh(); }
                return;
            }
            _check.Tick();
        }
        private void DisableRuntime()
        {
            try { _loop?.Dispose(); }
            finally
            {
                try { _patches?.Dispose(); }
                finally
                {
                    _loop = null; _patches = null;
                    _enabled = _disablePending = false;
                    if (_ui != null) _ui.SetActive(false);
                }
            }
        }
        private static Assembly ResolveDependency(object sender, ResolveEventArgs args)
        {
            if (new AssemblyName(args.Name).Name != "0Harmony") return null;
            var path = Path.Combine(Path.GetDirectoryName(typeof(TasMod).Assembly.Location), "0Harmony.dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }
        public void OnUnload()
        {
            try
            {
                try { _menuEntry?.Dispose(); }
                finally { _session?.Dispose(); }
            }
            finally
            {
                try { DisableRuntime(); }
                finally
                {
                    if (_ui != null) UnityEngine.Object.Destroy(_ui);
                    AppDomain.CurrentDomain.AssemblyResolve -= ResolveDependency;
                    _session = null; _ui = null; _menuEntry = null; _check = null; _host = null;
                }
            }
        }
    }
}
