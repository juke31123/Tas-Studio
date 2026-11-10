# Tas Studio 0.17.1

New in 0.17.1: the in-game Mods entry and Tas Studio menu title show the
installed DLL version. Loader and desktop version labels use the same value.
Release downloads are named `Tas-Studio-<version>.zip`.

New in 0.17.0: optional frame targets with a configurable allowance (default
1000 frames), per-checkpoint search time and future-frame lookahead scoring for
Beam, Wave and chain searches. Set numbered goals in the checkpoint map, drag
existing points to reposition them, and pan with the middle mouse button.
Free-camera and in-game goal placement have been removed. See
`docs/BRUTEFORCE.md` for the budget rules and controls.

New in 0.16.1: inputs are stored as change blocks with `StartFrame` and `Duration`.
Their durations and per-frame verification count must match the total
`FrameCount`; gaps, overlaps and missing frames are rejected. Loading expands
them into the normal editable timeline. Exact presses/releases and per-frame
hashes remain intact. Existing 0.16.0 compact files still load.

New in 0.16.0: run files use a compact layout, typically about 85% smaller in the
tested recordings. Inputs, movement hashes, PU timing and world verification are
preserved. Existing supported format-7 files load and become compact when saved;
the previous save is retained as `.bak`. The large starting-world check is now
compressed. No extra dependency is needed. See `docs/FILE-STORAGE.md`.

New in 0.15.0: open **Bruteforce > Checkpoint map...** to place and reorder
numbered goals. Click the map to add a point, middle-drag to pan and use the wheel
to zoom. Set each point's coordinates/radius with **Apply point**. Searches must
visit the numbered circles in order and finish at the last; the default whole-route
mode optimizes the section together. Native verification checks the same route.
Clearing the route requires a new finish before starting another search.
Local CashPerLoop single purchases
are now supported by the game-code simulator; other unvalidated purchases remain
guarded. Extract the complete package, including its updated Bruteforce folder.

New in 0.14.0: simulator body-pose and timer corrections, lower observation and
profile-write overhead, streamed saves, improved Beam refinement, and optional
**Experimental work stealing (Beam only)**. Affinity remains the default; the
experimental setting lets idle workers take queued work while retaining their
own simulators. More workers may increase memory and reconstruction costs.

Since 0.13.0: Beam search adds a bounded archive, mixed input holds, route
refinement and worker-owned snapshot caches. Single-frame inputs remain available
at every start; goals are checked after every frame. **Input hold hint** guides
longer candidates without forcing all edits onto a fixed frame grid. The window
shows archive cells, refinement/splice attempts, cache hits and restored frames.
Recordings retain the selected FPS with 50 Hz physics; native verification and
Save edits still control acceptance.

New in 0.12.2: the search choices are **Beam search**, **Exhaustive input chain**
and **Wave search**. The label cleanup does not change their search behaviour.

The portable release includes the accurate external simulator and its .NET
runtime. Extract the complete ZIP into `Recharge/Mods` and use Bruteforce normally.
On first search, **Preparing simulator** creates its local game-code copy from
your installed game, then starts the search. No Git, SDK, manual registration or
separate .NET installation is needed. Cancel also cancels preparation.
Your installed game assemblies are read only; the generated copy stays inside
the mod's `Bruteforce/Host` directory. A changed game version requires a matching
Tas Studio package. Keep the entire `Bruteforce` folder when installing/updating.

New in 0.12.0: select **Wave search**, **Beam search**, or
**Exhaustive input chain** in the Bruteforce window. The archive option runs
the pinned upstream solver through our movie-frame adapter; it needs the local
game-code host described below. Original chain and beam modes remain available.
Recorder gameplay ZIPs can now be imported and replayed with their recorded
variable timing for simulator validation; see `docs/GLITCH-VALIDATION.md`.

Since 0.11.0: **Run > Open Bruteforce window**. Choose an already executed start
frame and use the checkpoint map to set goals. Choose allowed abilities, workers and
a budget, then search externally. Verify a candidate in game, Accept as draft,
and use Save edits to replace all inputs from the start frame onward.
The portable game-code host supports tested refill, zipper and spring interactions. Other mechanics
and technique policies remain limited; see `docs/BRUTEFORCE.md` for coverage.
Developers can also register a separately prepared game-code host for all three
search modes. The generated game assembly is built from a local installation and excluded from
the release ZIP. See `docs/SIMULATOR-DEVELOPMENT.md` for setup and current limits.

Since 0.10.0: 200-level input Undo/Redo, flushed draft recovery, periodic
world replay checks, isolated atom-door progression, and a **Checks** tab for
performance profiling and continuous/frame-stepped replay verification.
Use **Recover draft** in the Frame Editor from the main menu, then **Save edits**.
Matching still-live checkpoints can avoid unnecessary rebuilds. Older states
still reconstruct through replay; arbitrary backward memory restore is not available.
Movie format 7 requires new recordings. Old files are left untouched.
See `docs/REPLAY-RELIABILITY.md` for controls and validation limits.

Use **Step PU** in Studio or the Frame Editor, or **F11** by default, to advance
complete frames until the first frame containing a physics update, then pause.
The shortcut is configurable under Mods > Tas Studio > Keybinds. A frame can
contain multiple PUs at low FPS; the stepper never splits a vanilla frame.
It crosses temporary hitstop, stops if the game menu opens, and pauses safely if
no PU occurs within five seconds of movie frames. Use Step frame to close the menu.

PU rows stay red after edits where their previous timing is known; **~** marks
timing awaiting re-execution. Replaying/recording those rows replaces the old
markings with observed timing. Unchanged live inputs preserve future PU markings.

The game's Pause action (normally Escape) is now recorded and replayed. You can
also paint **Menu** cells in the Frame Editor and use Save edits to apply them.
A press opens the game menu; release it before the next press to close the menu.
This is separate from TAS pause: movie frames continue while the menu freezes PUs.
Clicking the game's Resume button queues a recorded menu press on the next frame;
if the TAS is paused, step or resume it to apply that click. Other menu navigation
and settings changes are not recorded TAS inputs. Movie format is now 7; create
new runs with this version. Existing saved files are preserved but incompatible.

Fixes physical jump/dash/reset presses and releases lost while frame advancing.
Hold the intended input and step: changes since the last simulated frame now reach
the game even after Unity's original input-event flags have expired during pause.
Native same-frame taps still retain both edges. Physics remains at 50 PUs per second.
Record a new run; this update does not repair missing events in existing recordings.

Open **Mods > Tas Studio > Keybinds** to change the seven TAS shortcuts.
Click a binding, then press F1-F12, A-Z or 0-9. Escape cancels; Delete unbinds.
Changes save automatically to `data/keybinds.json`; Restore defaults resets all seven.
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

Requires IGTAP on Windows x64 with Recharge installed. Studio, Frame Editor and Bruteforce controls run inside the game process. Search computation uses a hidden external executable. The portable release supplies its runtime. Smaller developer packages without the `Bruteforce` folder require .NET 10 for the approximate Solver, or a separately prepared game-code host.

1. Close IGTAP.
2. Back up your existing Tas.Studio folder and its data outside Recharge/Mods. Do not leave duplicate installations in Mods.
3. Extract the ZIP into Recharge/Mods. The manifest must be at Recharge/Mods/Tas.Studio/mod.json. Preserve your data folder when updating an existing installation.
4. Start IGTAP and open the game's settings / Mods page, then click **Tas Studio**. No TAS window appears automatically at game startup.
5. Choose **Create New Tas**, **Load Tas**, or **Start Replay**. Creation keeps all stage, area, ability and world-upgrade options. Load selects a saved movie; Start Replay starts it separately. Creation/loading/replay launch require the main menu.
6. After the game finishes loading and validates its starting state, the recording/replay pauses at frame zero and opens the desktop Studio and Frame Editor. Press F6 to begin or F7 to step.

The settings page also contains Enable/Disable and Open desktop editing windows. Clicking the mod's entry opens this page; it no longer toggles the mod directly. Disabling an active run saves/stops it before removing its runtime hooks.

F5 reopens both desktop windows. F9 opens the desktop States page; F10 opens desktop player stats. F6 pauses/resumes, F7 steps, and F8 stops/reloads. All old floating in-game windows and the status banner have been removed. Closing the desktop Studio pauses an advancing run and closes the Frame Editor; reopening preserves the session and draft edits.

Edit cells directly in the Frame Editor. Save edits applies the draft and starts any necessary rebuild; Discard cancels it. Named checkpoints reconstruct state by replay, not instant memory restoration. Predictions do not simulate collisions or triggers.

This package contains the mod, simulator tools/runtimes, dependency notices and documentation. Personal recordings, states, saves and game assemblies are excluded. Existing data is preserved, but recordings from other mod builds are incompatible under the current fingerprint checks.

See docs/DESKTOP-STUDIO.md, docs/FRAME-EDITOR.md, docs/SAVESTATES.md and docs/PLAYER-STATS.md.
