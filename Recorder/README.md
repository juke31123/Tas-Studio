# Tas Studio Gameplay Recorder 1.1.0

A small Recharge mod that observes the player during **normal gameplay**.

## Use

1. Close IGTAP and extract `Tas.Studio.Recorder` into `<Game>/Recharge/Mods/`.
   Replace the previous recorder DLL/manifest if updating; keep its `data` folder.
2. Play the game normally. Press **F8** to start recording from your current state.
3. Press **F8** again to stop and export. **F5** shows/hides a small control window
   with Record, Stop/export, optional name/notes and Open export folder.
4. Wait for "Gameplay ZIP ready", then send the `*-validation.zip` from
   `Tas.Studio.Recorder/data/exports/`.

The window hides while recording and reappears on stop. Use the game's own pause
menu. There are no frame/PU stepping controls, TAS creation settings or replay
verification controls. F6, F7 and F11 have no recorder function.

The recorder does not reload the game, select a level, change abilities, inject
inputs, lock FPS, change physics timing or disable saving. Your normal progress
continues to be saved. FPS changes and variable frame deltas are recorded as they
happen. Recording adds some observation overhead; the actual timing is included.

For clean baseline data, use Recharge and this recorder without gameplay-changing
mods or an active Tas Studio session. Mod versions/hashes are included in exports.
Nothing is uploaded automatically. No separate application or solver is needed.

## Data

Each validation ZIP contains environment metadata and one or more scene/area parts:

- Current world, player/body, geometry, abilities/economy, animation and RNG state.
- A compressed frame journal with native timestamps/deltas, sampled game inputs,
  position, velocity, movement flags, native physics-update observations, RNG,
  currencies and nearby platforms/refill orbs.
- Player contact, purchase and spring-helper events.
- Capture status and file-size/SHA-256 identities (Base64).

Scene/player/area changes start a new part automatically. Loading/main-menu gaps
are skipped; recording can resume when gameplay returns. Compression and file
writing use a bounded background queue. Capture stops at its diagnostic size or
queue limit rather than pausing gameplay or silently discarding frames.

These are **passive gameplay observations**, not `.tas.json` movies. An arbitrary
starting point, variable frame times, native contact caches and existing timed
callbacks prevent promising deterministic TAS replay from this data alone. The
data is useful for simulator development and diagnostics; it does not certify
simulation parity. The prepared host's `capture-replay <capture.zip> <output-dir>`
imports these ZIPs and runs a continuous player-state comparison using their
recorded timings. See `docs/GLITCH-VALIDATION.md` in Tas Studio's source. The TAS
`restore-check` command uses the separate fixed-FPS movie fixture format.

Only generated gameplay data is bundled, not game DLLs or raw save files. Stop
and wait for packing before closing the game. If interrupted, keep the relevant
`data/captures` folder for diagnostics.
