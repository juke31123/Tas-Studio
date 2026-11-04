# Version 0.6.1 validation

The settings panel now fits the actual canvas/camera viewport instead of using its authored size as its final screen size. The 0.6.0 screenshots showed vertical clipping and overflowing help text; the fix includes centered viewport scaling, text autosizing and shorter help labels. Live Unity layout at the reported resolution and after window resizing still needs visual acceptance.

# Version 0.6.0 validation

All 405 workspace checks pass, including 16 settings-launcher lifecycle checks and 75 native desktop checks. Coverage verifies no startup windows, settings entry versus enable/disable, queued creation/loading, disabled and missing-file rejection, preserved start options, readiness before desktop handoff, paused frame zero for recording/replay, single handoff, and removal of duplicate native launch/overlay controls. Existing draft editing, buffering, checkpoints and replay checks pass. The Unity settings panel compiles against the installed game; actual panel layout, Back/Close navigation, resolution scaling, focus and scene reload behavior still require in-game acceptance.

# Version 0.5.2 validation

All 387 workspace checks pass, including 74 native desktop/session checks. New native rendering coverage verifies the completed offscreen bitmap, no grid invalidation for identical snapshots or status-only changes, redraws after input/cursor changes, stable GDI resource counts across repeated resizing, and buffer cleanup/recreation on close/reopen. Existing editing, manual-save rebuild, replay and checkpoint tests pass. These checks use real Win32 windows with a fake Unity boundary; visual confirmation in the running game is still required.

# Version 0.5.1 validation

All 378 workspace checks pass, including 65 native desktop/session checks. New coverage verifies draft-only painting and undo, no automatic rebuild, explicit native Save edits/Discard buttons, stepping blocked before commit, drafts surviving window closure, and saving an inline value still being typed. Existing replay, checkpoint and state-rebuild checks pass. Native layout inspection is separate from live Unity/Mono acceptance.

# Version 0.5.0 validation

All 365 workspace checks pass: the prior 299 core/session/stats checks, 52 native desktop/session integration checks and 14 frame-cell model checks. Native tests now click the second window's grid, drag a column, type an analog value in-place, rebuild/undo earlier edits, preserve planned future rows, save prefix checkpoints and replay an edited movie to completion. Both windows are rendered for layout inspection. The Unity boundary is faked; native UI inside Unity/Mono still needs acceptance testing. Production builds must have zero warnings/errors. See [FRAME-EDITOR.md](FRAME-EDITOR.md).

# Version 0.4.2 validation

The external application and named-pipe transport have been replaced with a native Win32 window in the hosting process. All 329 workspace checks pass, including 30 native-window/command checks. These create real hidden native windows and verify their process ownership, command queue bounds/copying, selected-input stepping through the native button, configured creation, checkpoints, close/reopen, thread shutdown and background-setting restoration. The game boundary is faked; Unity/Mono acceptance remains separate. All four native pages were rendered and inspected. Production builds have zero warnings/errors. See [DESKTOP-STUDIO.md](DESKTOP-STUDIO.md).

# Version 0.4.1 validation

The desktop experiment adds seven bounded-message framing checks and 24 command/connection checks, bringing the workspace suite to 330 checks. These run the production named-pipe bridge and desktop client with both JSON serializers, using fake Unity boundaries. Coverage includes input-before-step ordering, main-menu creation, invalid speed/input/edit/replay rejection, checkpoint saving, disconnect pausing and background-setting restoration. The Windows form is rendered separately for visual layout inspection. Both production projects must build without warnings/errors. Live Unity IPC, focus changes and in-game acceptance remain to be verified; see [DESKTOP-STUDIO.md](DESKTOP-STUDIO.md).

# Version 0.4.0 validation

The player-stats addition has 43 pure prediction-model checks and 13 monitor/lifecycle checks, bringing the workspace suite to 299 passing checks. The standalone source repository includes the 43 model checks in `Tests/Motion.Tests.csproj`. The Release build targets the installed game and must pass without warnings/errors. Native Unity movement prediction and the F10 window still require the acceptance pass in [PLAYER-STATS.md](PLAYER-STATS.md).

# Version 0.3.0 validation

The Release build passes against the installed game with zero warnings/errors. All 243 automated checks pass, including 36 new savestate checks for paused/frame-zero capture, storage, corruption and path validation, immutable names, original-timeline backups, compatibility rejection before reset, repeated restores, held-input edges, world mismatch diagnostics, cancel, session recreation, and playback of a completed rerecord. Native Unity boundaries are faked in these tests. Real game checkpoint restoration and the new window still require the acceptance checks in SAVESTATES.md.

# In-game acceptance checklist

Prior validation for 0.2.1: Release build against the installed game passes with zero warnings/errors. All 207 automated checks pass: 94 core/storage checks, 90 production session/clock lifecycle checks, 12 mod-toggle lifecycle checks and 11 configured-start checks with fake Unity/Recharge boundaries. Configured-start checks exercise serialized setup roundtrips, course/ability validation, the real checkpoint-resolution/ability-application code against fake components, recording/replay from the menu, saved-progress independence, difficulty restoration and safe return after setup errors. Release-selector checks cover all sixteen start choices, overgrown/native-zone mapping, legal ability limits, native world-upgrade flags, orb initialization and clearing unselected unlocks. The main-menu UI, orbs, zippers and all course starts still require in-game acceptance. Toggle checks cover menu visibility, removing/reinstalling hooks and UI, disabled scene reloads, saving and finishing active runs before disabling, retaining the selected movie, canceling a pending disable and unload cleanup. These include decimal/comma speed input, the 0.1–1 range, restoring TAS pacing after game FPS/VSync changes, unchanged simulation timesteps at slow speeds, and rejection of unsupported or missing startup protocols on import and different mod builds on playback. Incompatible runs are labeled on load and their files remain untouched. Existing deferred-load, warmup, repeatability, file-browser and edit-preservation checks remain covered. Loading checks cover delayed zone completion after player Start, overlapping loaders, canceled readiness, two initialization frames, and no movie/physics advancement while waiting. Timer checks cover the 30ms jump callback, hitstop, pause, cancellation from callbacks, stable order, deferred zero-delay callbacks, reset/stop cleanup and named movement-field differences. In-game acceptance remains separate; native coroutine behavior, patched timer calls and wall-clock playback speed require game verification.

The installed 0.1.1 startup log confirmed that 68 runtime hooks loaded successfully. The real-game repeatability test has not yet produced a result; 0.1.2 adds a saved-run browser to remove the manual filename obstacle.

The 0.1.0 user report confirmed a starting-state desync at frame 0: expected Y -307.494873, actual Y -308.4735, with equal movement velocity. That version allowed simulation during deferred scene initialization and probed rendered transforms. Version 0.1.1 holds simulation until initialization completes and uses Rigidbody2D positions with interpolation disabled during a session.

Run **Run reset + idle replay test (3 runs)** in the TAS window to exercise the real Unity runtime. It records 120 neutral frames, reloads and replays them three times at different viewing speeds, including pauses in two passes. The test uses its own movie name and writes `repeatability-result.json`, `last-bootstrap.json`, and, on a mismatch, `last-desync.json` in the movies folder. A pass covers this idle reset/replay scenario, not all game mechanics.

Use a copy of the starting save and begin with a 2–5 second route. Keep identical game files, mods and saves across recording/replay.

- [ ] Recharge loads the mod and logs the runtime hook count without exceptions.
- [ ] F5 opens/closes controls; window controls remain responsive during TAS pause.
- [ ] Record neutral → right → neutral; Stop saves; Play reproduces the same positions and completes with all recorded probes matching.
- [ ] Repeat playback twice after Stop/reload, including after a game restart.
- [ ] Hold Jump for multiple frames; verify one press and one release, with matching jump-cut behavior.
- [ ] Test Jump2 independently and release one jump button while the other remains held.
- [ ] Test Dash, Dash2, directional dash, neutral dash direction selection and dash hitstop.
- [ ] Pause during normal movement and during dash hitstop; wait several seconds; verify the cursor and gameplay state remain unchanged.
- [ ] F7 advances exactly one movie frame and returns to pause. Check that zero-physics hitstop frames are counted correctly.
- [ ] Record the same test at 0.5x and play at 1x/0.5x/0.1x; checkpoints and fixed-tick counts still match.
- [ ] Enter both `0.5` and `0,5`, then Apply; reject values below 0.1 or above 1 without changing the current speed.
- [ ] With the game's FPS setting at 100, replay a 50 Hz movie at 1x and verify 50 movie frames take approximately one second, excluding loading/pauses. Repeat after a scene reload and at 0.5x (approximately two seconds).
- [ ] Seek to frame N and confirm the state matches uninterrupted playback at N.
- [ ] Branch at N; verify prefix preservation, future truncation, backup creation and new inputs on playback.
- [ ] Edit an input range; verify downstream probes are invalidated and playback says those frames lacked reference probes.
- [ ] QuickReset and death/respawn replay correctly.
- [ ] Moving platforms, springs, wall jumps, block swaps and floating-origin movement either replay successfully or stop with a clear first-desync report.
- [ ] Modified starting save or mod/game DLL is rejected before playback begins.
- [ ] Stop, runtime failure and mod unload restore input mode, physics simulation, capture/fixed time and render pacing.
- [ ] No ordinary autosave is written during active recording/playback. Verify Stop reloads original saved progress.
- [ ] Scene transitions stop clearly; no corrupted continuation is appended to the movie.

When reporting a desync, retain the `.tas.json`, its `.bak`, the original starting save, installed mod versions and Recharge/Unity Player.log. Record the first frame reported and whether it involved pause, stepping, dash hitstop, scene loading or a moving platform.
