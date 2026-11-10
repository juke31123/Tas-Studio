# Compact TAS files (0.16.1)

The storage layout is `tas-studio-compact-v2`. It changes how verification data is
written, independently of gameplay format 7 and its frame/50 Hz PU protocol.
Files remain `.tas.json`. Existing supported format-7 files load normally; saving
writes the compact layout and keeps the previous file as `.bak`. Earlier gameplay
formats remain unsupported. Conversion does not change mod/game compatibility
metadata or bypass its checks.

The previous compact-v1 layout from 0.16.0 also loads normally. New saves use v2.

## Input durations

Inputs are stored only when the complete logical sample changes, in `InputRuns`.
Each entry has a zero-based `StartFrame`, a positive `Duration`, and its `Input`.
For example, holding right for 120 frames with ordinary inferred button edges:

```json
{"StartFrame":0,"Duration":120,"Input":[1,0,0]}
```

The next run must start at frame 120. A file-wide `FrameCount` must equal both the
sum of all durations and the number of verification rows in `Frames`. The loader
rejects gaps, overlaps, missing runs, zero/negative durations, oversized counts
and missing verification rows. It never silently fills a damaged input timeline.

Run comparison includes axes, held buttons, edge mode and exact press/release
bits. An exact jump press, steady held jump and release therefore form separate
runs; expanding a steady hold does not repeat the initial press. Deliberate
same-frame taps on consecutive frames remain taps on every frame. Analog values
and signed-zero axes are preserved bit for bit.

The file retains per-frame hashes, state/PU timing and periodic world witnesses.
Those observations can change while inputs stay held. On load, runs expand into
the usual editable per-frame timeline, so playback, undo/redo, savestates and
bruteforce consume the same logical frames as before.

## What stays

- Every input axis, button and exact press/release, including taps between PUs.
- Player position, control velocity, physics velocity, momentum, movement flags,
  resources, menu state and interpolated position used for desync checks.
- Every per-frame movement hash, with the same movement fields as hash inputs.
- The PU count and phase. These detect timing drift and power the editor's PU rows.
- Starting settings, seed, difficulty, warmup PU schedule and game/mod identity.
- Complete starting-world verification and the same periodic world checkpoints.

No movement-hash fields were dropped simply because they stayed constant in one
recording. Upgrades can change movement settings during a run; timers, buffers,
respawn data and other internal fields affect later frames and glitches.

## What no longer repeats

Related numbers share one line. `Input` is `[x, y, held]` for inferred button
edges, or `[x, y, held, pressed, released]` for exact edges. A rare sixth `false`
entry preserves unused edge bits on an inferred-edge row for lossless conversion.
Buttons use the existing mask: jump 1, jump2 2, dash 4, dash2 8, reset 16, menu 32.

`Position`, `Velocity` and `Momentum` are two-number tuples. `RenderPosition` and
`PhysicsVelocity` appear only when they differ from position/control velocity;
the loader reconstructs the omitted values exactly. The reserved string `"-0"`
preserves negative zero where ordinary JSON number parsing would erase its sign.
Other coordinates remain JSON numbers, including analog/subnormal values.

`State` is `[flags, airJumps, airDashes, cutscene]`. Flags are grounded 1, wall 2,
dashing 4, facing-right 8, menu-open 16 and game-paused 32. `Expected: null` marks
an authored frame without verification data.

`Movement` contains changes from the preceding stored movement dictionary. The
start dictionary is written once; unchanged values such as movement settings and
unlocks are inherited. `MovementRemoved` deletes keys. Loading reconstructs full
dictionaries, retaining field-specific desync messages. Each observed frame still
has its own unchanged `MovementHash`.

## The large header

The header describes how to recreate and verify the start: selected level and
abilities, FPS, seed, warmup, initial player state, and game/mod/save fingerprints.
`StartWorld` checks currencies, upgrades, RNG, platforms, colliders, course state
and session timers. It is a replay witness, not a restorable engine-memory dump.

The old header expanded every object path and scalar into readable JSON. The
compact header stores that complete witness as a gzip/base64 string. The sample
world payload is over 1 MiB unpacked. It is retained for reliable starting-state
checks, while compressed on disk. Periodic `World` strings contain compressed
changes from the preceding witness, initially based on the compact replay view
of `StartWorld`. All keys/values are restored before comparison, with the existing
numeric tolerances intact. Savestates retain their outer full-world witness too.

The codec streams frame rows and bounds both compressed and expanded imports.
Source/cache fingerprints stream the expanded logical data and sort dictionary
keys ordinally. Compression output and dictionary iteration order are not hash
inputs; movement hashes themselves are unchanged.

Bruteforce's source/recovery movie copies use the same compact codec. Its detailed
native engine snapshots and validation traces retain their fields; those captures
serve a different purpose from normal run files.

## Developer validation

Create verified compact copies without overwriting any source file:

```powershell
dotnet run --project Tests/MovieStorage.Probe.csproj -c Release -p:ManagedDir="<Game>/IGTAPfullGame_Data/Managed" -- "<new output folder>" "<movie.tas.json>"
```

The report compares expanded canonical fingerprints, confirms source file hashes
are unchanged, and measures bytes and actual frame-line counts. The native driver
also supports `Test-Simulator.ps1 -ReferenceTasPath <movie> -VerifyCompactMovie`.
It saves/reloads a fresh recording, then checks continuous and frame-stepped
playback in the installed game while preserving game saves and restoring its DLL.
