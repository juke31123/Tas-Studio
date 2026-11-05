# Native Studio windows (0.9.1)

**Mods > Tas Studio > Keybinds** configures open-editors, pause/resume, step,
stop/reload, savestates and stats. Click a binding and press F1-F12, A-Z or 0-9;
Escape cancels and Delete unbinds. Changes persist immediately in `data/keybinds.json`,
with a backup when replacing an existing file. Restore defaults resets all six.
Duplicates and unsupported keys are rejected; corrupt settings fall back to defaults
with an error shown in the menu, preserving the file until a successful explicit save.
The shortcuts listed below are defaults. Both desktop windows show current bindings.

`Core/Keybinds.cs` stores platform-independent settings; `Runtime/TasHotkeys.cs`
maps these to Unity Input System keys. The desktop receives detached copies through
its existing snapshot channel and maps them to local Win32 key events. No global
keyboard hooks are installed. Modified keys, repeated native keydown events, desktop
text entry and the open in-game settings page suppress TAS shortcuts. The game also
ignores shortcuts when unfocused to avoid duplicate commands from the desktop.
Ctrl+S, Ctrl+Z and other frame-editing keys retain their existing behavior. Game
movement bindings are unchanged; choose TAS keys that do not overlap game controls.

The external windows adapt the visual design of TatsuyaTas's in-game UI: dark
rounded cards, teal accents, muted labels, aligned values, status badges and a
progress strip. Recording uses red, paused state amber, playback/completion green,
and idle/loading blue. Unsaved frame edits are labelled explicitly. PU rows retain
their red backgrounds and all cells remain directly editable.

`UI/NativeTheme.cs` owns the shared palette, cached GDI brushes/fonts and owner-drawn
controls. Rendering stays on the native UI thread. Native buttons retain keyboard
focus/activation; the timeline retains its offscreen buffer and unchanged-grid
checks. Stats use separate columns rather than spaces in one text block. At smaller
window sizes secondary help is hidden and long prediction explanations ellipsize.
The main window minimum remains 820 by 680 logical pixels.

Visual reference: local `tests/TatsuyaTas/Plugin.cs`, `UiStyles`, `UiRow`, `UiBox`
and `OnGUI`. This is a desktop adaptation; its simulation and solver are not used.
Native owner drawing follows [Microsoft's DRAWITEMSTRUCT contract](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-drawitemstruct).

The in-game settings panel scales to fit within 90% of the visible viewport width and 88% of its height, including the title and Close button. It centers in the camera viewport/safe area and refits when window or canvas scaling changes. Text wraps or shrinks within its controls.

Start in the game's **Settings / Mods > Tas Studio** page. It offers Create New Tas, a paged saved-movie browser, and Start Replay. Creation includes new game, all fifteen level starts, the area/overgrown setup, abilities, up to two air dashes and one extra air jump, refill orbs and zippers. Nothing opens automatically at game startup.

After startup loading and warmup complete, the recording or replay pauses at frame zero and opens two desktop windows: Studio and Frame Editor. Both run inside IGTAP on a dedicated native UI thread. There is no companion executable, web server or Desktop Runtime dependency. No floating in-game editor, savestate/stats windows or TAS banner remain.

## Controls


- **Frame Editor:** click/drag input cells or double-click X/Y for inline analog values. Save edits commits the draft and starts any necessary rebuild. See [FRAME-EDITOR.md](FRAME-EDITOR.md).
- **Run:** pause/resume, step, stop/reload, save, and speed from 0.1 to 1.0 (dot/comma accepted). Record inputs from frame list consumes planned rows during recording.
- **States:** save/load named checkpoints during paused recording. Loading reconstructs the saved frame by replay.
- **Player stats:** current position, velocity, speed, movement state and charges beside next-frame estimates. See [PLAYER-STATS.md](PLAYER-STATS.md).

Create/load/replay launch controls now live exclusively in the game's mod settings page. That page also contains Enable/Disable and a button to reopen the desktop windows. Opening the page does not change mod enablement.

F5 in IGTAP reopens Studio and Frame Editor. F9 selects States; F10 selects Run with player stats. F6/F7/F8 work in the game and the native windows. Closing Studio closes Frame Editor and pauses an advancing run at a boundary. Closing only Frame Editor leaves Studio open. Reopening retains the session and pending edits. Disabling/unloading the mod closes both windows.

IGTAP temporarily runs in the background while the editor is open; its previous setting is restored on closure or failure. Use windowed mode if exclusive fullscreen minimizes the game on focus changes. There is no embedded game capture.

## Architecture and validation

TasMenuEntry registers a real Recharge panel with AddPanelRow. TasSettingsPanel builds its Unity UI controls. TasLauncher queues session actions, waits for load/warmup readiness, and requests the desktop handoff once. TasDesktopHost owns the desktop bridge and telemetry lifecycle without drawing in-game UI.

NativeStudioWindow and NativeFrameEditor own their native HWNDs and GDI resources. DesktopBridge exchanges detached snapshots/commands through StudioChannel and applies mutations on the game thread. Frame rendering uses a reusable offscreen buffer and skips unchanged grids.

Automated checks cover menu registration, enable/disable, deferred launcher actions, loading failures, configured setup, frame-zero handoff, native controls, editing, rebuilds and replay. Unity boundaries are faked; actual settings layout, navigation, scene reloads and window focus need in-game acceptance.
