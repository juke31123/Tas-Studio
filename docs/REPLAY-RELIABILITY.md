# Replay verification, profiling and checkpoint reuse (0.10.0)

Movie format 7 adds initial and periodic world witnesses and the session's atom-door preference. Old formats are rejected without conversion. Existing files are preserved.

## World checks

Recording captures a complete initial witness and periodic witnesses every movie second, beginning at frame 0. Replay compares currencies, upgrades, RNG state, floating origin, zone, movers, refill objects, course state, bodies, colliders, timers and frame/PU clocks. Bulk geometry and course histories are digested in periodic witnesses to avoid storing a growing full course path every second. Other fields retain their values for useful first-divergence reports. User savestates retain full witnesses.

Edits invalidate downstream witnesses. Rebuilding a recording creates new observations for changed frames; it does not prove those observations independently. Playback does not invent missing reference witnesses. Desync reports include expected and actual world witnesses. Periodic checks cannot detect every transient divergence between checkpoints.

`OpenA2Door` is a progression flag stored in PlayerPrefs outside Savedata. Configured TAS starts use a locked door; saved-scene recording captures the existing value. Calls from the game assembly are redirected to session-local HasKey/GetInt/SetInt/DeleteKey wrappers for this key. Replay uses its recorded starting value. The persistent value is never changed by those redirected calls; other preferences retain native behaviour. Calls made by unrelated mods are outside this isolation.

## Verify replay

Load a fully recorded movie at the main menu. Open desktop controls with F5, select **Checks**, and click **Verify replay**. It runs one continuous pass and one frame-stepped pass, both at the movie's chosen FPS with native 50 Hz physics. Each verifies the recording's player, world and timing references. Stop cancels. The previous viewing speed is restored, and a `replay-audit-*.json` report is saved under `data/movies/`. Desync remains paused for inspection.

For an acceptance corpus, record separate movies at 30, 50, 60, 100, 120, 144, 165, 244 and 280 FPS. Include jump/release between PUs, wall clips, dashes, moving platforms/zippers, refill objects, currency purchases, floating-origin crossings, death/reset, pause-menu transitions and hitstop. Run Verify replay for each movie. These checks compare mod recording against mod playback; a separate unmodded-game baseline is still needed to establish vanilla equivalence.

## Performance profile

The existing FPS CSV remains available. A bounded 4,096-frame profiler additionally records frame interval percentiles, PU/non-PU intervals, Gen0 collections during measured work, and section timings for session handling, input, player/world probes, desktop commands/snapshots, player stats and journaling. Frame intervals include rendering and pacing; named sections cover their own work only. Section times are inclusive and must not be summed as independent totals. Paused-editor operations have separate aggregate counters. There is no guessed allocation-byte measurement.

Reports are saved when an advancing run pauses or releases its clock; **Save profile** also exports on demand to `data/performance/*-profile.json`. The final frame's interval may be unavailable until the next loop begins. Profiling is observational and never changes the movie clock. Measure before claiming an optimisation reaches a particular FPS.

## Checkpoint cache boundary

The bounded cache retains up to 32 world witnesses. Saving edits compares the actual executed input prefix and startup configuration. An unchanged prefix with a matching still-live paused world can be reused immediately, including edits undone before saving. Loading a savestate at the same live frame can also reuse the world after checking both witnesses. This preserves Unity's native contact, interpolation and coroutine history because the engine state never moves.

Changing an executed input, seeking to an older frame, an environment mismatch or failed live verification falls back to scene reload and native replay. Arbitrary backward checkpoint restoration is **not implemented**: witnesses do not capture Unity's hidden native contact caches and coroutine execution state. They must not be written back as if they were a complete memory snapshot. A future restore provider needs native-state coverage and live replay validation before that optimisation can be enabled.

The implementation was informed by review of ChandlerFerry/IGTAPTasMod's profiling, editor and fidelity investigations at commit `134d96c6011dd729e75f86f95d6fc16382538aff`; its runtime code was not copied into this project.
