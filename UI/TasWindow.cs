using System;
using System.Collections.Generic;
using System.Globalization;
using Igtap.Tas.Core;
using Igtap.Tas.Runtime;
using UnityEngine;

namespace Igtap.Tas.UI
{
    // IMGUI remains usable while the game's Update methods are paused.
    // Keep every action routed through TasSession so UI can later be replaced.
    internal sealed class TasWindow : MonoBehaviour
    {
        private TasSession _session;
        private RepeatabilityCheck _check;
        private Rect _rect = new Rect(20, 40, 550, 650);
        private Vector2 _scroll;
        private Vector2 _fileScroll;
        private IReadOnlyList<MovieFile> _files = Array.Empty<MovieFile>();
        private double _nextFileRefresh;
        private string _start = "0", _count = "1";
        private string _speedText = "1";
        private FrameInput _edit;
        private Movie _undo;
        private string _error = "";
        private int _mainPage; // 0 home, 1 saved runs, 2 creation
        private readonly TasStartSetup _creation = new TasStartSetup();
        private readonly SavestateWindow _savestates = new SavestateWindow();
        public void Initialize(TasSession session, RepeatabilityCheck check) { _session = session; _check = check; }
        private void OnGUI()
        {
            if (_session == null) return;
            GUI.depth = -100;
            _savestates.Draw(_session);
            if (!_session.ShowWindow)
            {
                GUI.Box(new Rect(10, 10, 450, 26), "TAS  " + _session.Mode + "  " + _session.Cursor + "/" + _session.Movie.Frames.Count + "  | F5 controls");
                return;
            }
            if (Time.realtimeSinceStartupAsDouble >= _nextFileRefresh)
            {
                RefreshFiles(); _nextFileRefresh = Time.realtimeSinceStartupAsDouble + 2;
            }
            _rect.width = Math.Min(550, Screen.width - 20);
            _rect.height = Math.Min(720, Screen.height - 40);
            _rect = GUI.Window(0x544153, _rect, Draw, "Tas Studio 0.3.3 — F5 show/hide");
        }
        private void Draw(int id)
        {
            if (_session.IsMainMenu) { DrawMainMenu(); return; }
            _scroll = GUILayout.BeginScrollView(_scroll);
            GUILayout.Label(_session.Mode + (_session.Frozen ? " — PAUSED" : "") + "   Frame " + _session.Cursor + " / " + _session.Movie.Frames.Count);
            GUILayout.Label("Movie time: " + (_session.Cursor / (double)_session.Movie.FrameRate).ToString("F3") + " s | " + _session.Movie.FrameRate + " fps | Rerecords " + _session.Movie.Rerecords);
            GUILayout.Label(_session.Status, GUILayout.MaxWidth(510));
            if (!string.IsNullOrEmpty(_error)) GUILayout.Label(_error);
            GUILayout.Space(8);
            GUILayout.BeginHorizontal(); GUILayout.Label("Save as", GUILayout.Width(85));
            _session.FileName = GUILayout.TextField(_session.FileName, 60); GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            Button("Save" + (_session.Dirty ? " *" : ""), _session.Save);
            if (_session.Mode != SessionMode.Recording)
                Button("Open movies folder", () => Application.OpenURL(new Uri(_session.Store.DirectoryPath + System.IO.Path.DirectorySeparatorChar).AbsoluteUri));
            GUILayout.EndHorizontal();
            if (_session.Mode != SessionMode.Recording) DrawSavedRuns();
            if (_session.Mode == SessionMode.Recording)
                if (GUILayout.Button("F9 Savestates")) _session.ShowSavestates = !_session.ShowSavestates;
            // Replay, saved-scene recording, seeking and repeatability diagnostics
            // remain runtime APIs; the current UI exposes creation and states.
            GUILayout.BeginHorizontal();
            Enabled(_session.Active && (_session.Mode == SessionMode.Playback || _session.Mode == SessionMode.Recording), () => {
                Button("F6 Pause / Resume", _session.TogglePause);
                Button("F7 Step", _session.Step);
            });
            Enabled(_session.Active, () => Button("F8 Stop + reload", _session.Stop));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Speed (0.1–1.0)", GUILayout.Width(110));
            _speedText = GUILayout.TextField(_speedText, 16, GUILayout.Width(75));
            Button("Apply", () => {
                if (!PlaybackSpeed.TryParse(_speedText, out var speed)) throw new ArgumentException(PlaybackSpeed.Error);
                _session.SetSpeed(speed);
                _speedText = speed.ToString("0.######", CultureInfo.InvariantCulture);
            });
            Button("Normal (1x)", () => { _session.SetSpeed(1); _speedText = "1"; });
            GUILayout.EndHorizontal();
            GUILayout.Label("Applied: " + _session.Speed.ToString("0.######", CultureInfo.InvariantCulture) + "x. Dot or comma accepted. Maximum: normal speed.");
            GUILayout.Space(8);
            GUILayout.Label("Frame input (used for edits and optional step recording)");
            DrawInput();
            _session.UseAuthoredInput = GUILayout.Toggle(_session.UseAuthoredInput, "Record the selected input instead of physical controls");
            _session.AuthoredInput = _edit;
            GUILayout.BeginHorizontal(); GUILayout.Label("Start", GUILayout.Width(40)); _start = GUILayout.TextField(_start, 10);
            GUILayout.Label("Count", GUILayout.Width(40)); _count = GUILayout.TextField(_count, 10); GUILayout.EndHorizontal();
            Enabled(_session.CanEdit, () => {
                GUILayout.BeginHorizontal();
                Button("Replace range", () => Edit((m, a, n) => Timeline.Replace(m, a, n, _edit)));
                Button("Insert", () => Edit((m, a, n) => Timeline.Insert(m, a, n, _edit)));
                Button("Delete", () => Edit(Timeline.Delete));
                Enabled(_undo != null, () => Button("Undo edit", () => { _session.RestoreEdit(_undo); _undo = null; }));
                GUILayout.EndHorizontal();
            });
            GUILayout.Label("Edits invalidate state checks from the changed frame onward.");
            GUILayout.Space(8);
            int start = Math.Max(0, Math.Min(_session.Cursor - 3, _session.Movie.Frames.Count - 12));
            GUILayout.Label("Timeline — zero-based frames, held inputs");
            for (int i = start; i < Math.Min(start + 12, _session.Movie.Frames.Count); i++)
            {
                var f = _session.Movie.Frames[i]; int frame = i;
                if (GUILayout.Button((i == _session.Cursor ? "> " : "   ") + i.ToString("D6") + "   (" + f.Input.X.ToString("0.##") + ", " + f.Input.Y.ToString("0.##") + ")  " + f.Input.Held + (f.Expected == null ? "   [edited]" : "")))
                { _start = frame.ToString(); _edit = f.Input; }
            }
            GUILayout.Label("Create New Tas is available from the main menu. During recording, F9 opens named replay checkpoints; pause with F6 to save or load one.");
            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0, 0, 10000, 22));
        }
        private void DrawMainMenu()
        {
            _scroll = GUILayout.BeginScrollView(_scroll);
            GUILayout.Label(_session.Status, GUILayout.MaxWidth(510));
            if (!string.IsNullOrEmpty(_error)) GUILayout.Label(_error);
            Enabled(!_session.Active, () => {
                if (_mainPage == 2) DrawCreation();
                else
                {
                    GUILayout.Label(_session.Movie.Frames.Count == 0 ? "No TAS loaded." : "Loaded: " + _session.FileName + " (" + _session.Movie.Frames.Count + " frames)");
                    if (GUILayout.Button("Load Tas")) { _mainPage = _mainPage == 1 ? 0 : 1; RefreshFiles(); }
                    Enabled(_session.Movie.Frames.Count > 0 && !_check.Running, () => Button("Start Replay", () => _session.StartPlayback()));
                    if (GUILayout.Button("Create New Tas")) _mainPage = 2;
                    if (_mainPage == 1) DrawSavedRuns();
                }
            });
            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0, 0, 10000, 22));
        }
        private void DrawCreation()
        {
            GUILayout.Label("Create New Tas");
            GUILayout.Label("Name");
            _session.FileName = GUILayout.TextField(_session.FileName, 60);
            _creation.Course = Math.Min(_creation.Course, _creation.CourseCount);
            var starts = new string[_creation.CourseCount + 1]; starts[0] = "New game (Area 1)";
            for (int i = 1; i < starts.Length; i++) starts[i] = "Level " + i + " - Area " + (i <= 5 ? "1" : "2") + (i >= 11 ? " (overgrown)" : "");
            GUILayout.Label("Start position");
            _creation.Course = GUILayout.SelectionGrid(_creation.Course, starts, 2);
            GUILayout.Space(8);
            GUILayout.Label("Unlocked abilities");
            _creation.WallJump = GUILayout.Toggle(_creation.WallJump, "Wall jump");
            _creation.DoubleJump = GUILayout.Toggle(_creation.DoubleJump, "One extra air jump (maximum)");
            _creation.Dash = GUILayout.Toggle(_creation.Dash, "Dash");
            if (_creation.Dash)
            {
                GUILayout.Label("Air dashes");
                _creation.AirDashes = 1 + GUILayout.SelectionGrid(_creation.AirDashes - 1, new[] { "1", "2 (maximum)" }, 2);
                _creation.OmniDash = GUILayout.Toggle(_creation.OmniDash, "Omnidirectional dash");
            }
            else _creation.OmniDash = false;
            _creation.BlockSwap = GUILayout.Toggle(_creation.BlockSwap, "Block swap");
            _creation.Light = GUILayout.Toggle(_creation.Light, "Player light");
            GUILayout.Label("Level upgrades");
            _creation.RefillOrbs = GUILayout.Toggle(_creation.RefillOrbs, "Dash + air-jump refill orbs (all groups)");
            _creation.Zippers = GUILayout.Toggle(_creation.Zippers, "Zippers (touch-activated moving platforms)");
            GUILayout.Label("Starts a fresh game state with these abilities. Your saved progress is kept. Recording begins paused after loading; press F6 to begin.");
            Button("Start Tas", () => {
                _session.StartPaused = true;
                _session.StartConfiguredRecording(_creation);
                _mainPage = 0;
            });
            if (GUILayout.Button("Back")) _mainPage = 0;
        }
        private void DrawSavedRuns()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Saved runs (" + _files.Count + ")");
            if (GUILayout.Button("Refresh", GUILayout.Width(65))) RefreshFiles();
            GUILayout.EndHorizontal();
            _fileScroll = GUILayout.BeginScrollView(_fileScroll, GUILayout.Height(125));
            if (_files.Count == 0) GUILayout.Label("No saved .tas.json runs found.");
            foreach (var file in _files)
            {
                var selectedName = file.Name;
                GUILayout.BeginHorizontal();
                GUILayout.Label(file.Name + "\n" + file.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + " | " + (file.Bytes / 1024.0).ToString("0.#") + " KiB", GUILayout.MinWidth(280));
                Enabled(!_session.Active && !_check.Running, () => Button("Load", () => { _session.LoadNamed(selectedName); _mainPage = 0; }));
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }
        private void RefreshFiles()
        {
            try { _files = _session.Store.ListMovies(); }
            catch (Exception error) { _error = "Could not list saved runs: " + error.Message; }
        }
        private void DrawInput()
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Left")) _edit.X = -1;
            if (GUILayout.Button("X neutral")) _edit.X = 0;
            if (GUILayout.Button("Right")) _edit.X = 1;
            if (GUILayout.Button("Down")) _edit.Y = -1;
            if (GUILayout.Button("Y neutral")) _edit.Y = 0;
            if (GUILayout.Button("Up")) _edit.Y = 1;
            GUILayout.EndHorizontal();
            GUILayout.Label("Move: " + _edit.X.ToString("0.###") + ", " + _edit.Y.ToString("0.###"));
            _edit.X = GUILayout.HorizontalSlider(_edit.X, -1, 1);
            _edit.Y = GUILayout.HorizontalSlider(_edit.Y, -1, 1);
            GUILayout.BeginHorizontal();
            foreach (Buttons button in new[] { Buttons.Jump, Buttons.Dash, Buttons.Reset })
            {
                bool held = GUILayout.Toggle(_edit.Down(button), button.ToString());
                if (held) _edit.Held |= button; else _edit.Held &= ~button;
            }
            if (GUILayout.Button("Clear")) _edit = default;
            GUILayout.EndHorizontal();
        }
        private void Edit(Action<Movie, int, int> edit)
        {
            var candidate = MovieStore.Clone(_session.Movie);
            edit(candidate, Parse(_start), Parse(_count)); candidate.Validate();
            _undo = MovieStore.Clone(_session.Movie); _session.RestoreEdit(candidate);
        }
        private static int Parse(string text) => int.TryParse(text, out var value) && value >= 0 ? value : throw new ArgumentException("Use a nonnegative whole frame number.");
        private void Button(string label, Action action)
        {
            if (GUILayout.Button(label)) _session.Queue(() => {
                try { _error = ""; action(); }
                catch (Exception e) { _error = e.Message; throw; }
            });
        }
        private static void Enabled(bool condition, Action draw)
        {
            bool previous = GUI.enabled; GUI.enabled = previous && condition;
            try { draw(); } finally { GUI.enabled = previous; }
        }
    }
}
