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
