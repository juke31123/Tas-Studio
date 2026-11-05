# Tas Studio 0.9.2

Fixes physical jump/dash/reset presses and releases lost while frame advancing.
Hold the intended input and step: changes since the last simulated frame now reach
the game even after Unity's original input-event flags have expired during pause.
Native same-frame taps still retain both edges. Physics remains at 50 PUs per second.
Record a new run; this update does not repair missing events in existing recordings.

Open **Mods > Tas Studio > Keybinds** to change the six TAS shortcuts.
Click a binding, then press F1-F12, A-Z or 0-9. Escape cancels; Delete unbinds.
Changes save automatically to `data/keybinds.json`; Restore defaults resets all six.
Duplicate bindings are rejected. Avoid keys used for game actions. Shortcuts are
inactive while this settings menu is open or while typing into desktop fields.
The shortcuts below describe the defaults; desktop labels follow your bindings.

The desktop Studio and Frame Editor use a shared dark theme inspired by TatsuyaTas:
teal controls, colored status badges, grouped panels and aligned player stats.

FPS diagnostics are automatic. Studio shows target and measured FPS plus achieved
speed. Recording/replay CSV reports are saved to `data/performance/`.
See docs/FPS-DIAGNOSTICS.md for the short test procedure.

Create New Tas now includes the game's FPS choices. Recording and replay lock
that FPS while physics stays at 50 PUs per game second. PU frames appear red in
the editor, and stats show frames until the next PU. Record new runs: earlier
movie formats are incompatible. See docs/FRAME-TIMING.md.

The built-in hitbox viewer was removed in 0.8.2. Use your separate hitbox mod for outlines.

Requires IGTAP on Windows with Recharge installed. Both desktop windows run inside the game process; no separate executable or Desktop Runtime is needed.

1. Close IGTAP.
2. Back up your existing Tas.Studio folder and its data outside Recharge/Mods. Do not leave duplicate installations in Mods.
3. Extract the ZIP into Recharge/Mods. The manifest must be at Recharge/Mods/Tas.Studio/mod.json. Preserve your data folder when updating an existing installation.
4. Start IGTAP and open the game's settings / Mods page, then click **Tas Studio**. No TAS window appears automatically at game startup.
5. Choose **Create New Tas**, **Load Tas**, or **Start Replay**. Creation keeps all stage, area, ability and world-upgrade options. Load selects a saved movie; Start Replay starts it separately. Creation/loading/replay launch require the main menu.
6. After the game finishes loading and validates its starting state, the recording/replay pauses at frame zero and opens the desktop Studio and Frame Editor. Press F6 to begin or F7 to step.

The settings page also contains Enable/Disable and Open desktop editing windows. Clicking the mod's entry opens this page; it no longer toggles the mod directly. Disabling an active run saves/stops it before removing its runtime hooks.

F5 reopens both desktop windows. F9 opens the desktop States page; F10 opens desktop player stats. F6 pauses/resumes, F7 steps, and F8 stops/reloads. All old floating in-game windows and the status banner have been removed. Closing the desktop Studio pauses an advancing run and closes the Frame Editor; reopening preserves the session and draft edits.

Edit cells directly in the Frame Editor. Save edits applies the draft and starts any necessary rebuild; Discard cancels it. Named checkpoints reconstruct state by replay, not instant memory restoration. Predictions do not simulate collisions or triggers.

This package contains only the mod, Harmony dependency/license, and documentation. Recordings, states, saves, source, tools and game assemblies are excluded. Existing data is preserved, but recordings from other mod builds are incompatible under the current fingerprint checks.

See docs/DESKTOP-STUDIO.md, docs/FRAME-EDITOR.md, docs/SAVESTATES.md and docs/PLAYER-STATS.md.
