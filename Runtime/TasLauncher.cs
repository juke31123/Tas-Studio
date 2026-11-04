using System;
using Igtap.Tas.Core;

namespace Igtap.Tas.Runtime
{
    // Settings-menu actions use the same frame-boundary queue as desktop commands.
    internal sealed class TasLauncher
    {
        private readonly TasSession _session;
        private readonly Func<bool> _enabled;
        private readonly Action _openDesktop;
        private bool _waitingForRun;
        public bool Busy { get; private set; }
        public string Error { get; private set; } = "";
        public bool CanLaunch => _enabled() && !Busy && !_waitingForRun && !_session.Active && !_session.HasPendingEdits && _session.IsMainMenu;
        public TasLauncher(TasSession session, Func<bool> enabled, Action openDesktop)
        { _session = session; _enabled = enabled; _openDesktop = openDesktop; }
        private void Submit(Action action)
        {
            if (!CanLaunch) { Error = "Return to the main menu and enable Tas Studio before loading or starting a run."; return; }
            Busy = true; Error = "";
            if (!_session.TryQueue(() => {
                try { if (!_enabled()) throw new InvalidOperationException("Tas Studio is disabled."); action(); }
                catch (Exception e) { Error = e.Message; }
                finally { Busy = false; }
            })) { Busy = false; Error = "Another action is pending. Try again."; }
        }
        public void Load(string name) => Submit(() => _session.LoadNamed(name));
        public void Create(string name, TasStartSetup setup)
        {
            var selected = setup.Clone();
            Submit(() => {
                selected.Validate(); _session.FileName = MovieStore.NormalizeName(name);
                _session.StartPaused = true; _session.StartConfiguredRecording(selected); _waitingForRun = true;
            });
        }
        public void Replay() => Submit(() => {
            // Stop at the verified start before handing controls to the desktop.
            _session.StartPlayback(0); _waitingForRun = true;
        });
        public void Tick()
        {
            if (!_waitingForRun) return;
            if (_session.Mode == SessionMode.Recording || _session.Mode == SessionMode.Playback)
            {
                _waitingForRun = false;
                try { _openDesktop(); } catch (Exception e) { Error = "Could not open desktop controls: " + e.Message; }
            }
            else if (!_session.Active || _session.Mode == SessionMode.Desync)
            { _waitingForRun = false; Error = _session.Status; }
        }
    }
}
