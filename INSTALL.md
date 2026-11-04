# Tas Studio 0.6.1

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
