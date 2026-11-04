# Native Studio windows (0.6.1)

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
