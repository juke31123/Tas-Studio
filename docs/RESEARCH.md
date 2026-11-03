# Research and implementation boundary

Research date: 2026-11-03. The installed game's assemblies were inspected locally for integration details. Decompiled game source stays in the ignored `.research/` directory and is not part of the distributable mod.

## What a TAS requires

A reusable TAS movie needs a reproducible starting state, an input stream associated with simulation frames, control over time, rerecording/editing, and replay verification. Pause/frame advance and save/load states are standard TAS authoring tools. A savestate must preserve the state required for subsequent inputs to produce the same result. [TASVideos basic tools](https://tasvideos.org/TasingGuide/BasicTools), [TASVideos creation workflow](https://tasvideos.org/TASingGuide/TASHowTo).

Recharge provides mod lifecycle hooks, data directories, config storage and Update/FixedUpdate/LateUpdate callbacks. Its normal callbacks are implemented by a shared MonoBehaviour; they do not establish execution order relative to every game component. This implementation therefore installs explicit PlayerLoop boundary markers and in-memory method patches. [IRechargeHost source](https://github.com/SumDumIdiut/recharge/blob/master/loader/ModApi/IRechargeHost.cs), [RechargeTicker source](https://github.com/SumDumIdiut/recharge/blob/master/loader/Runtime/RechargeTicker.cs), [Unity PlayerLoop API](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/LowLevel.PlayerLoop.html).

Unity's capture timestep fixes advancement of scaled time, but does not control unscaled time. Input System also has distinct update modes; manually updated input must be pumped explicitly. These details matter when pausing, advancing and reproducing the game's dash stop. [Time.captureDeltaTime](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Time-captureDeltaTime.html), [InputSystem API](https://docs.unity.cn/Packages/com.unity.inputsystem%401.7/api/UnityEngine.InputSystem.InputSystem.html).

Harmony permits in-memory prefixes and IL transpilers while preserving the original game methods. The implementation uses it to substitute movement input reads and unscaled clock reads, and to gate game update/iterator methods during TAS pause. [Harmony patching](https://harmony.pardeike.net/v2/articles/patching), [Harmony prefix behavior](https://harmony.pardeike.net/v2/articles/patching-prefix.html).

## Findings from the installed IGTAP build

These are local code observations, not claims that the game has been tested with the new mod:

- The installed build settings identify `Assets/Scenes/MainMenu.unity`, `Overworld.unity` and `OverworldHard.unity`. Inspection of serialized scene data found Normal courses 1–10 (areas 1 and 2) and B-side courses 1–5 (area 1). Each gameplay course has a startGate checkpoint reference. B-side course 2 leaves courseScript.resetPoint unset, so the gate's checkpoint is authoritative. The read-only inspection helper is `.research/inspect-courses.ps1`; runtime resolves actual scene references rather than storing guessed coordinates.
- The release selector deliberately excludes the unused OverworldHard scene. Per the user's level naming, levels 11–15 are overgrown courses 1–5. Inspection of ZoneDisableLoadList shows native zone 1 enables courses 1–5 and native zone 2 enables 6–10; switching to native zone 2 disables the first five. Therefore the overgrown visit uses zone 1 with `currentA1State = Overgrown`. OvergrowthLoadScript applies its alternate terrain when that zone loads. The in-memory `ZoneLoader.activeZone` is the same value Movement.load assigns from the save's `_currentZone`.
- JiggleDropScript uses the shared `unlockJiggleDrops` and `moreRefreshOrbs` upgrades to enable both jump and dash refill objects. Its activation rule is upgrade sum > ActivationSequence. PlatformMover's touch-activated zip-mover coroutine is gated by `zipMoversUnlocked`. These are world upgrades, independent of the player's dash/jump ability flags.

- `Movement.Start` resolves `Move`, `Jump`, `Jump2`, `Dash`, `Dash2`, `QuickReset` from the Input System action asset.
- `Movement.Start` also starts `ZoneLoader.LoadZone(force: true)`. Its iterator yields twice before activating the zone, then waits 1.5 scaled seconds before hiding the loading text. Player initialization alone therefore does not establish zone readiness. Version 0.1.5 allows this specific loader through the startup freeze, converts its startup wait to real time and waits for iterator completion plus two initialization frames before warmup.
- `Movement.Update` reads press and release edges, sets jump/dash buffers, schedules buffer cancellation with `Invoke`, and handles QuickReset.
- Test5 frame 18 supplied direct evidence of a delayed-callback mismatch: its reported actual fields reconstruct the actual movement hash exactly; changing only `recentlyJumped` from True to False reconstructs the recorded hash exactly. Position, velocity and fixed-tick count match. Movement schedules `endRecentJump` with a 0.03-second Invoke. Version 0.1.6 replaces Movement's one-shot native timers with session-local scaled timers at a fixed simulation phase; the native timer discrepancy was not merely suppressed in the comparator. Evidence is retained locally under `.research/test5-frame18/`.
- `Movement.FixedUpdate` reads movement, changes cooldowns, applies movement state and drives physics.
- Either jump action's release can trigger jump cutting. Combining both into a single jump boolean would lose information.
- `Movement.TimeStop` starts an iterator that changes `Time.timeScale`, waits for end of frame and accumulates `Time.unscaledDeltaTime`.
- Respawning resets only some player state. It does not recreate moving platforms, world progression, pending invokes, all coroutine state, or physics contacts.
- `Saveloader` loads persistent objects from `Savedata` or `Savedatahard`, with backup fallback. Its normal save format is progress serialization, not a complete live-world savestate.
- The game also tracks timer/course/incremental progression. Player position alone cannot verify all of it.

The design consequence is to start from a scene reload and replay the prefix for seeking. A position-only checkpoint is deliberately not presented as a full savestate.

## Implemented timing contract

Version 0.1.1 adds a loading barrier. Unity's `LoadScene` call does not finish immediately; completion occurs in a following frame. The original implementation incorrectly let simulation run while awaiting readiness and its test double hid this by completing loads immediately. The barrier now holds Physics2D in script mode and gates game ticks until both sceneLoaded and player initialization have occurred, then opens at a frame end. [Unity LoadScene API](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/SceneManagement.SceneManager.LoadScene.html).

The position probe uses the physics body's position, and scene-body interpolation is disabled during the session. Transform interpolation can smooth rendered positions between physics updates, so those coordinates are inappropriate as the sole simulation checkpoint. The user's observed 0.98-unit vertical discrepancy is consistent with startup/phase differences, but attribution between interpolation and uncounted physics needs the real-game bootstrap trace. [Unity Rigidbody2D interpolation](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Rigidbody2D-interpolation.html).

At the end of EarlyUpdate, the session pumps manual Input System processing and selects one logical input snapshot. Both player Update and FixedUpdate consume that snapshot through patched reads. At the end of PostLateUpdate, the session commits one movie frame and compares movement state plus the number of player FixedUpdates.

While paused, game-assembly Update/FixedUpdate/LateUpdate methods and iterator MoveNext bodies are gated, 2D physics switches to script mode without calls to Simulate, and scaled time is stopped. Paused iterators return `true` to remain alive instead of terminating. Unscaled clock reads inside managed game methods use the movie clock. TAS UI and input polling still run.

Pause/resume transitions occur at frame ends. Single-step resumes for the next complete frame and pauses at its end. Viewing speed changes the render target, leaving the capture and physics timesteps unchanged.

## Limits and remaining work

1. **Runtime verification is outstanding.** The build and fake-boundary tests do not establish Unity PlayerLoop ordering, Harmony compatibility, exact physics catch-up behavior or successful game playback. Complete the acceptance checklist before using long recordings.
2. **Single scene, movement only.** Scene transitions stop the session. Shop clicks, menu navigation, upgrades, mouse input and other actions are not movie events. A full-game run needs these actions plus explicit scene-transition events and bootstrap rules.
3. **No complete world savestates.** Replay-based seek is available. Instant rewind would require explicit serialization for platforms, triggers, courses, economy/progression, block swaps, RNG sources, pending invokes/coroutines and relevant physics state, or a process-level checkpoint solution.
4. **Not proven cross-machine deterministic.** Unity physics, native engine systems, floating-point math and other mods can differ. Game/Unity/mod/save fingerprints reject known environment differences, and probes report many divergences, but neither proves entire-world equality.
5. **Probe coverage is partial.** Player position, velocity, momentum, resources and selected internal movement fields are checked. RNG evolution, moving-platform phases, floating origin, currency and full course/world state need additional probe providers. Internal floats are rounded to three decimal places for the movement hash.
6. **Unscaled hooks are scoped.** Reads of `unscaledDeltaTime`, `unscaledTime` and `unscaledTimeAsDouble` in managed game methods are replaced. Native systems, realtimeSinceStartup, WaitForSecondsRealtime, third-party assemblies and external clocks are not all virtualized. UI animation, audio and external mod coroutines can still behave differently while paused.
7. **Input is frame-sampled.** Press+release completed entirely between sampled frames is not recorded. This is intentional for TAS authoring; use authored input or step recording for precise one-frame pulses.
8. **Starting progress is external.** The movie stores hashes, not your save files. Preserve the original save backup yourself. Active TAS sessions suppress the ordinary Saveloader entry points; this is not complete save/Steam-achievement isolation. Normal game progression can still award achievements. Stop reloads the saved scene to discard practice world changes; unload/error restores control and may leave the current practice world in memory.
9. **Reset completeness is not yet established.** Reloading the scene may leave static state or objects owned by other mods alive. A starting-probe match does not prove that those objects were reset. Treat the current start procedure as a practical candidate to validate, not a guarantee.

## Expansion order

First validate a short static-ground replay, then jumps/releases, then dash hitstop, pauses, moving platforms and course completion. Next extend world probes, add explicit supported-game-version checks and isolate a practice save profile. After that add a full action/event schema for purchases and scene transitions. Complete world-state providers should come before claiming instant rewind or portable full-game determinism.
