# Frames and physics updates (0.8.0)

Choose Recording FPS on Create New Tas. The choices come from the installed
game's FPS dropdown, not a guessed list or the PlayerPrefs option index. A movie
locks that rate for its whole lifetime. Playback uses the movie rate and restores
the user's graphics settings afterward. Physics retains a 0.02-second timestep
(50 PUs per scaled game second). Game-driven time stops still stop physics.

Each editor row is one game frame, including frames with no PU. Frames containing
observed PUs have a red background; held cells use a brighter red. Zero-PU rows
retain the usual colors. Unknown future rows and edited suffixes are not labelled
as observed PUs until executed. Save edits still explicitly starts rebuilding.

Player stats show Frames until next PU, inclusive: 1 means the next frame contains
a PU. The countdown assumes the current game time scale continues. During a game
freeze its resumption is not predicted, so the display shows unknown. An editor
pause uses the saved resumption scale. The total PU count includes startup warmup.

## Timing ownership

- `Core/FrameTiming.cs` describes the independent frame and 50 Hz PU clocks.
- `Runtime/ClockLease.cs` owns capture timing, wall-time pacing, startup alignment,
  pause/resume and restoration. It adjusts the next capture step against Unity's
  native accumulator so rounding does not drift across a PU boundary. Alignment
  happens with gameplay and physics gated, before neutral warmup begins.
- `Runtime/LoopHooks.cs` opens a frame in EarlyUpdate, samples its inputs immediately
  before ScriptRunBehaviourUpdate (after Unity's dynamic input update), and closes
  it at PostLateUpdate. PUs before that input sample see the previous input.
- Unity owns FixedUpdate, simulation, contacts, interpolation and Invoke callbacks.
  No frame manually calls Physics2D.Simulate or adds a PU for an input action.
  Movement's native timers are observed for telemetry/checkpoints, not dispatched
  by the mod. Other scripts reading the same gameplay InputActions also receive
  the movie's input.
- Frame-based code, including step-up and dash-direction selection, continues
  between PUs. Player Rigidbody interpolation is preserved. Physics-body and
  interpolated positions are both checked during replay.
- Deterministic unscaled time replaces wall time during TAS playback. FixedUpdate
  readers receive fixed-step time; frame readers receive movie-frame time. Dash
  freeze coroutines keep advancing through game frames; an editor pause gates them.
- Footstep animation events are gated during loading/editor pauses, including
  Animator initialization events that can occur at timeScale=0. During active
  frames their native behavior and RNG draws are unchanged.

Viewing speed (0.1x-1x) only paces the frames. It never scales the physics timestep
or the game's timeScale. F7 advances one complete game frame, which may contain
zero, one or multiple PUs. A slow computer may take longer to present the run;
it does not skip input rows to catch up.

## Movie and checkpoint data

Format 5, bootstrap 4, timing protocol 1. Old files are rejected without conversion.
The file stores recording FPS, start PU phase, each row's observed PU count and
phase, inputs and state probes. Physical inputs retain exact press/release edges,
including a quick press and release during a single input update. Button painting
authors transitions from the held-state cells instead. Existing alternate action
bindings remain independently recorded, as their releases can affect jump cuts.

Replay, savestate reconstruction and committed edit rebuilding use this same
clock. A changed input invalidates later PU observations as well as state probes:
for example, a new dash can introduce a time stop. Savestate witnesses also include
the clock's PU phase and count. Desync reports include both native and planned phase.

## Validation boundary

Automated tests cover mixed frame/PU ratios, zero-PU and multi-PU frames, native
clock alignment, game freezes, pauses, single steps, slow replay, saved checkpoints,
edited reconstruction, exact edges and red Win32 row rendering. The clock boundary
in those session tests is simulated. A separate in-game probe successfully recorded
82-frame movement/jump/dash sequences, restored checkpoints, rebuilt saved edits
and replayed at every installed option: 30, 50, 60, 100, 120, 144, 165, 244 and
280 FPS. A 100 FPS run also passed longer editor pauses, single-step and 0.1x replay.
See [VALIDATION.md](VALIDATION.md). In-game acceptance must additionally compare
wallclips, step-ups, jump cuts, native timer deadlines, interpolation through long
editor pauses, moving platforms and refills with unmodded play at each selected FPS.
Repeatable playback alone is not proof of vanilla equivalence. The next-frame
motion predictor remains an estimate without collision or interpolation modelling.

## Research references

- [Unity fixed updates](https://docs.unity3d.com/6000.0/Documentation/Manual/fixed-updates.html)
- [Unity captureDeltaTime](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Time-captureDeltaTime.html)
- [Unity unscaledDeltaTime](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Time-unscaledDeltaTime.html)
- [ChandlerFerry timing grid](https://github.com/ChandlerFerry/IGTAPTasMod/blob/main/MacroEngine.cs)
- [ChandlerFerry pacing](https://github.com/ChandlerFerry/IGTAPTasMod/blob/main/TasPlugin.Playback.cs)
- Local reference: `tests/TatsuyaTas/Plugin.cs`, RecUpdate, StartFrameMovie and
  FrameMovieLate. Its duration/phase recording informed the investigation; its
  solver's approximated interpolation lag is not used here.
