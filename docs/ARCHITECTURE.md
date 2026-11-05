# Architecture and extension points

## Data and authoring

`Movie` owns format version, adapter identity, game/save/mod fingerprints, clock parameters, starting probe and ordered frames. A frame stores held logical input, optional exact press/release edges, expected state, PU count and PU phase. Frame rate is chosen from the installed game options; the physics timestep stays 0.02 seconds. Index N means the input for the Nth complete simulation frame; the cursor is the index of the next frame to execute.

`Timeline` mutates only the movie data. Editing at N invalidates every expected state from N onward. `MovieStore` validates before writing, writes to a temporary file, flushes and replaces the old file while keeping a backup. Unknown JSON fields and future schema versions fail rather than being silently misinterpreted. Type-name deserialization is disabled.

Movie format 5 contains an optional `TasStartSetup` with level ID (zero means new game), release-only difficulty, ability choices/counts, refill-orb and zipper unlocks. It is cloned when recording starts and reused on replay. Levels 11–15 map to course checkpoints 1–5 with the Overgrown terrain state and native zone 1. The logical Area 2 label denotes the overgrown visit; native zone 2 disables these course objects. Levels 6–10 use native zone 2; New game and levels 1–5 use native zone 1. `ConfiguredStart` resolves start-gate checkpoints from the loaded scene, including inactive objects, before Movement.Start invokes zone loading. It initializes ability resources, player/body position and camera together. A missing/ambiguous checkpoint fails explicitly.

World unlocks use the native `zipMoversUnlocked`, `unlockJiggleDrops` and `moreRefreshOrbs` upgrade values. Dash and jump orbs share one unlock. The last orb activation group is discovered from scene objects so the selected orb unlock enables all groups, including overgrown groups. After zone initialization, orb usability/visuals are refreshed before warmup. Unselected upgrades are explicitly zeroed. The maximum is one extra air jump and two dashes, enforced at movie validation as well as in the UI.

Configured starts suppress SaveableObject.load during the fresh reset, allowing Awake/Start initialization to build a fresh world without reading progress. SaveableObject.save and Saveloader saving remain blocked throughout every TAS session. Stop re-enables normal loading while retaining save suppression until the main menu finishes loading. Difficulty and temporary game-start flags are restored. Ordinary saved-scene recordings retain their save-hash preflight; configured runs instead reproduce the serialized setup. The setup and difficulty are included in bootstrap/desync reports.

## Runtime ownership

`TasMod` owns one `TasSession`, one `GamePatches`, one `LoopHooks`, and one persistent UI object. Its unload path releases global timing/input settings, removes only its loop markers and unpatches only its Harmony owner ID.

The Recharge menu entry opens a native game settings panel. TasLauncher queues create/load/replay actions at frame boundaries and opens desktop editing after readiness. Enable/Disable is a button inside the panel. Disabling saves/stops an active run before removing runtime hooks. The settings panel remains available while disabled; scene reloads re-register it without duplicate construction. TasDesktopHost owns desktop/telemetry lifecycle and never draws an in-game overlay.

`TasSession` processes queued UI commands at an early loop boundary. Mode transitions are Idle → Loading → Warmup → Recording/Playback → Complete/Desync → Stopping → Idle. Pause is orthogonal to mode. Completion/desync retains a paused session so the offending/final state can be inspected. Loading and Stopping hold both game ticks and native physics through sceneLoaded and Movement.Start. Binding only arms readiness; the barrier opens at the end of that frame, and warmup starts on the next whole frame. Stop retains save suppression until the replacement scene/player has initialized.

Bootstrap protocol 4 waits for scene activation, Movement.Start and every startup ZoneLoader iterator to finish. Only these loader iterators may advance while the startup barrier holds gameplay/physics frozen. Their WaitForSeconds yields become real-time waits during loading, so the final loading-screen delay can finish at timeScale=0; normal in-run zone transitions keep their original waits. Two complete initialization frames follow the last loader completion before physics synchronization, native clock alignment and warmup. A newly started loader cancels pending readiness. Warmup physics counts are still recorded and checked. Unsupported or absent protocol versions fail validation when loading a file.

`ClockLease` is the sole owner of temporary changes to capture timestep, fixed timestep, time scale, target frame rate, vSync, Input System update mode and Physics2D simulation mode. Errors and unload restore the previous values. It preserves the game's current time scale when pausing, including a zero scale during dash hitstop.

`PlaybackSpeed` parses dot/comma decimals and validates the inclusive 0.1–1 range for both UI and runtime callers. `ClockLease` limits rendering to the movie FPS multiplied by speed, rounded down to a whole FPS. Session frame boundaries reassert target FPS and disabled VSync because the game's `SettingsScript.Awake` overwrites them during scene loading. Speed never changes capture/fixed timesteps or movie inputs.

Playback requires an exact match of the entire mod-set fingerprint, including the TAS DLL. Different builds are marked incompatible on load and rejected before starting playback. No old-run conversion or migration code is provided.

`GamePatches` scans managed game methods for unscaled clock calls and patches game tick/iterator methods. Input replacement leaves the original action objects, bindings and UI actions intact; all game consumers of the player's known action reads use the movie snapshot. Unknown actions fall back to their original methods. `IgtapAdapter` is the build-specific seam for action lookup and movement probes.

`SimulationTimers` observes Movement's native one-shot Invoke deadlines for checkpoint verification. Unity dispatches the actual callbacks in their original phase. Cancellation and callback completion update the observer; it never fires a native callback itself. Timing, dynamic input sampling after FixedUpdate, interpolation preservation and frame/PU data are detailed in [FRAME-TIMING.md](FRAME-TIMING.md).

## Adding features

- **Another gameplay action:** add a stable bit or typed event in the movie schema, bump the format version when necessary, map its real gameplay consumption point, add recording and replay support together, then add edge tests.
- **More verification:** extend `StateProbe` with a versioned world-provider payload or introduce `IStateProbeProvider`. Capture sorted stable identifiers, not Unity instance IDs. Start with floating origin, platforms, block colors, course timers, upgrades and RNG state.
- **Full savestates:** define explicit `Capture`, `Restore`, `Validate` providers with coordinated ownership of pending timers/coroutines. Restoring only public fields or transforms is insufficient. Retain replay-from-start as the correctness reference.
- **Cross-scene movies:** introduce scene-transition events and a loading phase with a recorded bootstrap contract. Do not simply remove the current scene-change stop.
- **New UI:** invoke the existing queued session commands. Keep editor changes transactional using a cloned movie so a rejected edit leaves the original intact.
- **New game version:** inspect the input consumers, timing and state fields; update the adapter identifier and fail with a readable compatibility error when assumptions no longer hold.

## Testing boundary

Core tests exercise movie persistence and edits. Session tests compile the production session and clock classes with fake Unity/input/scene/adapter boundaries, exercising pause, single-step, replay EOF, desync, branches and cleanup. They intentionally do not mock the implementation of those state transitions.

The real Unity phase order, patched IL, native physics and coroutine timing require in-game acceptance tests. Keep those results separate from unit-test and compilation success.
