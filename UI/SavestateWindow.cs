using System;
using System.Collections.Generic;
using Igtap.Tas.Core;
using Igtap.Tas.Runtime;
using UnityEngine;

namespace Igtap.Tas.UI
{
    internal sealed class SavestateWindow
    {
        private Rect _rect = new Rect(585, 45, 460, 470);
        private Vector2 _scroll;
        private string _label = "checkpoint-1", _error = "";
        private double _refreshAt;
        private IReadOnlyList<MovieFile> _files = Array.Empty<MovieFile>();
        public void Draw(TasSession session)
        {
            if (!session.ShowSavestates || !(session.Mode == SessionMode.Recording || session.RestoringState || session.Mode == SessionMode.Desync)) return;
            if (Time.realtimeSinceStartupAsDouble >= _refreshAt)
            {
                try { _files = session.States.List(); }
                catch (Exception error) { _error = error.Message; }
                _refreshAt = Time.realtimeSinceStartupAsDouble + 2;
            }
            _rect.width = Math.Min(460, Screen.width - 20);
            _rect.height = Math.Min(470, Screen.height - 40);
            _rect.x = Math.Max(0, Math.Min(_rect.x, Screen.width - _rect.width));
            _rect.y = Math.Max(0, Math.Min(_rect.y, Screen.height - _rect.height));
            _rect = GUI.Window(0x544154, _rect, id => Contents(session), "Savestates - F9 show/hide");
        }
        private void Contents(TasSession session)
        {
            GUILayout.Label(session.FileName + " | Frame " + session.Cursor + (session.Frozen ? " | PAUSED" : ""));
            GUILayout.Label("Named replay checkpoints: loading rebuilds the game up to this frame, then pauses recording. Loading is not instant.");
            GUILayout.Label(session.Status);
            if (_error.Length > 0) GUILayout.Label(_error);
            if (session.RestoringState) GUILayout.Label("Restoring: " + session.Cursor + "/" + session.Movie.Frames.Count + " frames. F8 cancels.");
            else if (!session.CanSaveState) GUILayout.Label("Press F6 to pause recording before saving or loading a state.");
            GUILayout.BeginHorizontal();
            GUILayout.Label("State name", GUILayout.Width(90)); _label = GUILayout.TextField(_label, 60);
            GUILayout.EndHorizontal();
            GUILayout.Label("File: " + session.FileName + "--" + _label + ".state.json");
            bool enabled = GUI.enabled;
            GUI.enabled = enabled && session.CanSaveState;
            if (GUILayout.Button("Save current state"))
            {
                string label = _label;
                Execute(session, () => session.SaveState(label));
            }
            GUI.enabled = enabled;
            GUILayout.Label("Saved states (all TAS runs)");
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(90));
            if (_files.Count == 0) GUILayout.Label("No saved states yet.");
            foreach (var file in _files)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(file.Name + "\n" + file.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), GUILayout.MaxWidth(330));
                GUI.enabled = enabled && session.CanSaveState;
                string name = file.Name;
                if (GUILayout.Button("Load", GUILayout.Width(55))) Execute(session, () => session.LoadState(name));
                GUI.enabled = enabled;
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            if (GUILayout.Button("Close")) session.ShowSavestates = false;
            GUI.DragWindow(new Rect(0, 0, 10000, 22));
        }
        private void Execute(TasSession session, Action command)
        {
            session.Queue(() => {
                try { _error = ""; command(); _refreshAt = 0; }
                catch (Exception error) { _error = error.Message; throw; }
            });
        }
    }
}
