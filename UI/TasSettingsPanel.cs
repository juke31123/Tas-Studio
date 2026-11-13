using System;
using System.Collections.Generic;
using Igtap.Tas.Core;
using Igtap.Tas.Runtime;
using Recharge.ModApi;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;

namespace Igtap.Tas.UI
{
    // A real Recharge settings panel, parented to the game's menu canvas.
    internal sealed class TasSettingsPanel : MonoBehaviour
    {
        private TasSession _session;
        private TasLauncher _launcher;
        private Func<bool> _enabled;
        private Func<string> _toggleLabel;
        private TMP_FontAsset _font;
        private TMP_Text _status, _selected, _filePage;
        private TMP_InputField _name;
        private TMP_InputField _vmanSeconds;
        private GameObject _startDropdown;
        private string _mapChoicesKey;
        private bool _startDropdownOpen;
        private double _nextMapScan;
        private Button _toggle, _create, _load, _replay, _start, _desktop, _omni, _dashCount;
        private readonly GameObject[] _pages = new GameObject[4];
        private readonly Button[] _bindingButtons = new Button[Keybinds.Titles.Length];
        private TMP_Text _shortcutHelp, _bindingHelp;
        private int _capture = -1;
        private readonly Button[] _fileButtons = new Button[5];
        private IReadOnlyList<MovieFile> _files = Array.Empty<MovieFile>();
        private readonly TasStartSetup _setup = new TasStartSetup();
        private int _fileOffset;
        private double _nextRefresh;
        private string _error = "";
        private RectTransform _panel;
        private Canvas _canvas;
        private bool _hasFrameRates;
        private float _maximumScale;

        public void Build(GameObject panel, TasSession session, TasLauncher launcher, Func<string> toggleLabel,
            Action toggle, Action openDesktop, Func<bool> enabled)
        {
            _session = session; _launcher = launcher; _toggleLabel = toggleLabel; _enabled = enabled;
            _font = panel.GetComponentInChildren<TMP_Text>(true)?.font;
            PanelLayout.Apply(panel, new Vector2(850, 800));
            PauseMenuHelper.NormalizePanelLayout(panel, 365, -365);
            _panel = panel.GetComponent<RectTransform>();
            _panel.pivot = new Vector2(.5f, .5f);
            _canvas = panel.GetComponentInParent<Canvas>()?.rootCanvas;
            _maximumScale = Mathf.Min(Mathf.Abs(_panel.localScale.x), Mathf.Abs(_panel.localScale.y));
            for (int i = 0; i < _pages.Length; i++)
            {
                var page = new GameObject("TasPage" + i, typeof(RectTransform)); page.transform.SetParent(panel.transform, false);
                ((RectTransform)page.transform).sizeDelta = new Vector2(800, 660);
                PauseMenuHelper.IgnoreLayout(page); _pages[i] = page;
            }
            _status = Label(panel.transform, 0, -315, 770, 65, "");
            var home = _pages[0].transform;
            _selected = Label(home, 0, 280, 750, 65, "");
            _create = Button(home, 0, 185, 500, "Create New Tas", () => ShowPage(1));
            _load = Button(home, 0, 125, 500, "Load Tas", () => { RefreshFiles(); ShowPage(2); });
            _replay = Button(home, 0, 65, 500, "Start Replay", _launcher.Replay);
            _desktop = Button(home, 0, -20, 500, "Open desktop editing windows (F5)", openDesktop);
            Button(home, 0, -80, 500, "Keybinds", () => ShowPage(3));
            _toggle = Button(home, 0, -140, 500, "", toggle);
            _shortcutHelp = Label(home, 0, -230, 750, 85, "");

            var keys = _pages[3].transform;
            Label(keys, 0, 290, 750, 40, "Tas Studio keybinds");
            for (int i = 0; i < _bindingButtons.Length; i++)
            {
                int action = i;
                Label(keys, -150, 220 - i * 50, 420, 42, Keybinds.Titles[i]);
                _bindingButtons[i] = Button(keys, 235, 220 - i * 50, 220, "", () => { _capture = action; });
            }
            _bindingHelp = Label(keys, 0, -155, 750, 80, "");
            Button(keys, -195, -235, 370, "Restore defaults", () => { _capture = -1; _session.SaveBindings(new Keybinds()); });
            Button(keys, 195, -235, 370, "Back", () => ShowPage(0));

            var create = _pages[1].transform;
            Label(create, -280, 300, 180, 36, "Tas name");
            _name = PanelWidgets.CreateInputField(create, _font, new Vector2(80, 300), new Vector2(520, 38), "my-run");
            _name.text = session.FileName; _name.characterLimit = 60; PauseMenuHelper.IgnoreLayout(_name.gameObject);
            Label(create, -280, 240, 180, 36, "Start position");
            Choice(create, -195, 170, "Wall jump", () => _setup.WallJump, v => _setup.WallJump = v);
            Choice(create, 195, 170, "Extra air jump (max 1)", () => _setup.DoubleJump, v => _setup.DoubleJump = v);
            Choice(create, -195, 115, "Dash", () => _setup.Dash, v => _setup.Dash = v);
            _omni = Choice(create, 195, 115, "Omnidirectional dash", () => _setup.OmniDash, v => _setup.OmniDash = v);
            _dashCount = Button(create, -195, 60, 370, "Air dashes: 1 (max 2)", () => {
                _setup.AirDashes = _setup.AirDashes == 1 ? 2 : 1;
                PauseMenuHelper.SetButtonLabel(_dashCount.gameObject, "Air dashes: " + _setup.AirDashes + " (max 2)");
            });
            Choice(create, 195, 60, "Block swap", () => _setup.BlockSwap, v => _setup.BlockSwap = v);
            Choice(create, -195, 5, "Player light", () => _setup.Light, v => _setup.Light = v);
            Choice(create, 195, 5, "Dash + air-jump orbs", () => _setup.RefillOrbs, v => _setup.RefillOrbs = v);
            Choice(create, -195, -50, "Zippers", () => _setup.Zippers, v => _setup.Zippers = v);
            var rates = GameFrameRates.Available();
            _hasFrameRates = rates.Length > 0;
            int selectedRate = Array.IndexOf(rates, Application.targetFrameRate);
            if (selectedRate < 0) selectedRate = 0;
            if (_hasFrameRates) _setup.FrameRate = rates[selectedRate];
            Label(create,-290,-105,140,36,"Run mode");
            PanelWidgets.CreateDropdown(create,_font,new Vector2(-60,-105),new Vector2(280,36),new[]{"Normal","Timed VMAN","VMAN practice"},0,value=>_setup.VmanMode=value);
            Label(create,170,-105,110,36,"Seconds");
            _vmanSeconds=PanelWidgets.CreateInputField(create,_font,new Vector2(290,-105),new Vector2(150,36),"60");_vmanSeconds.text="60";_vmanSeconds.characterLimit=5;PauseMenuHelper.IgnoreLayout(_vmanSeconds.gameObject);
            Label(create, 0, -165, 760, 55, "Custom maps use the selected abilities and main spawn.\nLevels 11-15 are overgrown. Saved progress is preserved.");
            _start = Button(create, -195, -225, 370, "Start Tas", () => {
                var setup = _setup.Clone(); if (!setup.Dash) setup.OmniDash = false;
                if(setup.VmanMode==1){if(!int.TryParse(_vmanSeconds.text,out setup.VmanSeconds))throw new InvalidOperationException("Enter a VMAN duration in whole seconds.");}
                _launcher.Create(_name.text, setup);
            });
            Button(create, 195, -225, 370, "Back", () => ShowPage(0));
            // Create the dropdown last so its expanded list draws above the ability controls.
            RefreshStartSelection();
            Label(create, 85, -50, 120, 36, "FPS / 50 PUs");
            if (_hasFrameRates)
                PanelWidgets.CreateDropdown(create, _font, new Vector2(270, -50), new Vector2(200, 38),
                    Array.ConvertAll(rates, fps => fps.ToString()), selectedRate, value => _setup.FrameRate = rates[value]);
            else Label(create, 255, -50, 240, 36, "Game FPS options unavailable");

            var files = _pages[2].transform;
            _filePage = Label(files, 0, 290, 740, 40, "");
            for (int i = 0; i < _fileButtons.Length; i++)
            {
                int slot = i;
                _fileButtons[i] = Button(files, 0, 220 - i * 65, 750, "", () => {
                    int index = _fileOffset + slot;
                    if (index < _files.Count) { _launcher.Load(_files[index].Name); ShowPage(0); }
                });
            }
            Button(files, -260, -135, 230, "Previous", () => { _fileOffset = Math.Max(0, _fileOffset - 5); RefreshFileRows(); });
            Button(files, 0, -135, 230, "Refresh", RefreshFiles);
            Button(files, 260, -135, 230, "Next", () => { if (_fileOffset + 5 < _files.Count) _fileOffset += 5; RefreshFileRows(); });
            Button(files, 0, -225, 500, "Back", () => ShowPage(0));
            ShowPage(0); RefreshState(); panel.SetActive(false);
        }
        private TMP_Text Label(Transform parent, float x, float y, float width, float height, string text)
        {
            var label = PanelWidgets.CreateLabel(parent, _font, new Vector2(x, y), new Vector2(width, height), text, fontSize: 17);
            label.textWrappingMode = TextWrappingModes.Normal; label.enableAutoSizing = true;
            label.fontSizeMin = 12; label.fontSizeMax = 17; label.overflowMode = TextOverflowModes.Ellipsis;
            PauseMenuHelper.IgnoreLayout(label.gameObject); return label;
        }
        private Button Button(Transform parent, float x, float y, float width, string label, Action action)
        {
            var go = PanelWidgets.CreateButton(parent, _font, "Tas_" + label, new Vector2(x, y), new Vector2(width, 42), label, 18);
            PauseMenuHelper.IgnoreLayout(go);
            var text = go.GetComponentInChildren<TMP_Text>(true);
            if (text != null)
            {
                text.enableAutoSizing = true; text.fontSizeMin = 12; text.fontSizeMax = 18;
                text.textWrappingMode = TextWrappingModes.NoWrap; text.overflowMode = TextOverflowModes.Ellipsis;
            }
            var button = go.GetComponent<Button>(); button.onClick.AddListener(() => {
                try { _error = ""; action(); } catch (Exception e) { _error = e.Message; }
                RefreshState();
            }); return button;
        }
        private Button Choice(Transform parent, float x, float y, string title, Func<bool> get, Action<bool> set)
        {
            Button button = null;
            string Text() => (get() ? "[X] " : "[ ] ") + title;
            button = Button(parent, x, y, 370, Text(), () => { set(!get()); PauseMenuHelper.SetButtonLabel(button.gameObject, Text()); });
            return button;
        }
        private void OnEnable() { if (_session != null) { _session.SuppressHotkeys = true; ShowPage(0); RefreshState(); } }
        private void OnDisable() { _capture = -1; if (_session != null) _session.SuppressHotkeys = false; }
        private void LateUpdate()
        {
            if (_panel == null || _canvas == null || !(_panel.parent is RectTransform parent)) return;
            // CanvasScaler and ancestor transforms change the actual on-screen size.
            // Convert the visible viewport into parent units rather than treating pixels
            // as UI units. Apply after canvas updates and repeat when the window resizes.
            var camera = _canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : _canvas.worldCamera;
            var viewport = Screen.safeArea;
            if (camera != null)
            {
                var pixels = camera.pixelRect;
                viewport = Rect.MinMaxRect(Mathf.Max(viewport.xMin, pixels.xMin), Mathf.Max(viewport.yMin, pixels.yMin),
                    Mathf.Min(viewport.xMax, pixels.xMax), Mathf.Min(viewport.yMax, pixels.yMax));
            }
            if (viewport.width <= 0 || viewport.height <= 0 || _panel.rect.width <= 0 || _panel.rect.height <= 0) return;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, viewport.min, camera, out var minimum) ||
                !RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, viewport.max, camera, out var maximum)) return;
            float scale = Mathf.Min(_maximumScale, Mathf.Min(Mathf.Abs(maximum.x - minimum.x) * .9f / _panel.rect.width,
                Mathf.Abs(maximum.y - minimum.y) * .88f / _panel.rect.height));
            if (scale <= 0) return;
            var desiredScale = new Vector3(scale, scale, _panel.localScale.z);
            var center = (minimum + maximum) * .5f;
            var desiredPosition = new Vector3(center.x, center.y, _panel.localPosition.z);
            if ((_panel.localScale - desiredScale).sqrMagnitude > .000001f) _panel.localScale = desiredScale;
            if ((_panel.localPosition - desiredPosition).sqrMagnitude > .000001f) _panel.localPosition = desiredPosition;
        }
        private void Update()
        {
            if (_session != null) _session.SuppressHotkeys = true;
            CaptureBinding();
            if (_session == null || Time.realtimeSinceStartupAsDouble < _nextRefresh) return;
            _nextRefresh = Time.realtimeSinceStartupAsDouble + .2; RefreshState();
            if(_pages[1].activeSelf&&!_startDropdownOpen&&Time.realtimeSinceStartupAsDouble>=_nextMapScan)
            {_nextMapScan=Time.realtimeSinceStartupAsDouble+1;RefreshStartSelection();}
        }
        private void ShowPage(int page)
        { _capture = -1;if(page==1)RefreshStartSelection(true); for (int i = 0; i < _pages.Length; i++) _pages[i].SetActive(i == page); }
        private void RefreshStartSelection(bool force=false)
        {
            var maps=CustomMaps.Available();
            string key=string.Join("\n",Array.ConvertAll(maps,m=>m.Id+"\t"+m.Name));
            if(!force&&key==_mapChoicesKey)return;
            _mapChoicesKey=key;_startDropdownOpen=false;
            if(_startDropdown!=null){_startDropdown.SetActive(false);UnityEngine.Object.Destroy(_startDropdown);}
            var starts=new string[_setup.CourseCount+1+maps.Length];starts[0]="New game - Area 1";
            for(int i=1;i<=_setup.CourseCount;i++)starts[i]="Level "+i+" - Area "+(i<=5?"1":"2")+(i>=11?" (overgrown)":"");
            int selected=_setup.Course;
            for(int i=0;i<maps.Length;i++){starts[_setup.CourseCount+1+i]="Custom: "+maps[i].Name;if(maps[i].Id==_setup.CustomMapId)selected=_setup.CourseCount+1+i;}
            if(_setup.IsCustomMap&&selected<=_setup.CourseCount){_setup.CustomMapId=_setup.CustomMapName=_setup.CustomMapHash="";_setup.Course=0;selected=0;}
            _startDropdown=PanelWidgets.CreateDropdown(_pages[1].transform,_font,new Vector2(80,240),new Vector2(520,38),starts,selected,value=>{
                bool custom=value>_setup.CourseCount;_setup.Course=custom?0:value;
                _setup.CustomMapId=custom?maps[value-_setup.CourseCount-1].Id:"";_setup.CustomMapName=custom?maps[value-_setup.CourseCount-1].Name:"";_setup.CustomMapHash="";
            },open=>_startDropdownOpen=open);
        }
        private void CaptureBinding()
        {
            var keyboard = Keyboard.current;
            if (_capture < 0 || keyboard == null) return;
            foreach (var key in keyboard.allKeys)
            {
                if (!key.wasPressedThisFrame) continue;
                if (key.keyCode == Key.Escape) { _capture = -1; _error = ""; RefreshState(); return; }
                try
                {
                    if (TasHotkeys.Modified(keyboard)) throw new ArgumentException("Choose a single key without Ctrl, Shift, Alt or Windows.");
                    var bindings = _session.Bindings.Copy();
                    bindings.Assign((TasShortcut)_capture, key.keyCode == Key.Delete ? "" : key.keyCode.ToString());
                    _session.SaveBindings(bindings); _capture = -1; _error = "";
                }
                catch (Exception e) { _error = e.Message; }
                RefreshState(); return;
            }
        }
        private void RefreshFiles()
        {
            _files = _session.Store.ListMovies();
            _fileOffset = Math.Min(_fileOffset, Math.Max(0, (_files.Count - 1) / 5 * 5)); RefreshFileRows();
        }
        private void RefreshFileRows()
        {
            _filePage.text = _files.Count == 0 ? "No saved .tas.json runs found." : "Saved runs " + (_fileOffset + 1) + "-" + Math.Min(_fileOffset + 5, _files.Count) + " / " + _files.Count;
            for (int i = 0; i < _fileButtons.Length; i++)
            {
                bool exists = _fileOffset + i < _files.Count;
                _fileButtons[i].gameObject.SetActive(exists);
                if (exists) PauseMenuHelper.SetButtonLabel(_fileButtons[i].gameObject, "Load: " + _files[_fileOffset + i].Name);
            }
        }
        private void RefreshState()
        {
            if (_status == null || _toggle == null) return;
            var bindings = _session.Bindings;
            PauseMenuHelper.SetButtonLabel(_desktop.gameObject, "Open desktop editing windows (" + bindings.Label(TasShortcut.Editors) + ")");
            _shortcutHelp.text = "Create, load and replay from the main menu.\nRecording opens desktop editors after loading.\n" +
                bindings.Label(TasShortcut.Pause) + " pause/resume | " + bindings.Label(TasShortcut.Step) + " step | " + bindings.Label(TasShortcut.Stop) + " stop\n" +
                bindings.Label(TasShortcut.Savestates) + " savestates | " + bindings.Label(TasShortcut.Stats) + " player stats | " + bindings.Label(TasShortcut.StepPu) + " step PU";
            for (int i = 0; i < _bindingButtons.Length; i++)
                PauseMenuHelper.SetButtonLabel(_bindingButtons[i].gameObject, _capture == i ? "Press a key..." : bindings.Label((TasShortcut)i));
            _bindingHelp.text = _capture >= 0 ? "Press F1-F12, A-Z or 0-9. Escape cancels; Delete clears.\n" + Keybinds.Titles[_capture] :
                "Click a binding to change it. Changes save automatically.\nShortcuts work in the game and desktop editors.\nAvoid keys used for game actions. Ctrl+S and editor keys stay unchanged.";
            _selected.text = "Selected: " + _session.FileName + "  |  " + _session.Movie.Frames.Count + " frames\nLoaded: "+_session.Adapter.LoadedMapLabel;
            _create.interactable = _load.interactable = _start.interactable = _launcher.CanLaunch;
            _start.interactable &= _hasFrameRates;
            _replay.interactable = _launcher.CanLaunch && _session.Movie.Frames.Count > 0;
            _desktop.interactable = _enabled();
            _omni.interactable = _dashCount.interactable = _setup.Dash;
            _vmanSeconds.interactable=_setup.VmanMode==1;
            if (!_setup.Dash) { _setup.OmniDash = false; PauseMenuHelper.SetButtonLabel(_omni.gameObject, "[ ] Omnidirectional dash"); }
            foreach (var button in _fileButtons) if (button != null) button.interactable = _launcher.CanLaunch;
            PauseMenuHelper.SetButtonLabel(_toggle.gameObject, _toggleLabel());
            _status.text = _error.Length > 0 ? _error : _session.KeybindError.Length > 0 ? _session.KeybindError : _launcher.Error.Length > 0 ? _launcher.Error : _session.Status;
        }
    }
}
