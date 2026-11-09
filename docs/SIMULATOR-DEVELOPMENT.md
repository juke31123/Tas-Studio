# External game-code host

The experimental game-code host runs chain, beam and Wave searches.
Portable player packages prepare it automatically on first search. Development
builds can instead register a locally prepared host; without a portable bundle
or registration, the included static-platform worker remains the default.
This is not a completed or globally certified simulator: native prefix comparison,
unsupported-mechanic guards and native candidate verification remain mandatory.

Each simulation/search job uses exactly the current TAS recording's saved FPS.
All candidates and workers in that job share it; PUs remain at 50 Hz of scaled
game time. Validation and caches are specific to that FPS and timing state.
Support or a failing regression at another FPS does not gate a job at this FPS.
Multi-rate runs are optional developer regression tests.

## Implemented

- A pinned local build of the reference engine's assembly preparation, Unity
  compatibility layer and Box2D backend, using the installed game assembly.
- A Tas Studio frame adapter that executes the retargeted game's
  `Movement.Update` and `Movement.FixedUpdate`. It does not use the handwritten
  movement predictor or the reference mod's physics-tick input injection.
- Separate input frames and 50 Hz PUs, including multiple PUs in a 30 FPS frame,
  secondary input actions and press/release lifetimes between dynamic updates.
- A development scheduler with scaled deadlines and separate frame, fixed and
  end-of-frame coroutine phases. Unknown yields and unsafe iterator snapshots
  fail explicitly.
- Native world/component/reference/shape/material export at a frozen boundary.
  Inactive tilemap/composite geometry is baked in an isolated 2D physics scene
  using collider/grid-only copies. Live colliders are never enabled and live
  physics is never advanced. Bakes are checked against active native geometry;
  unsupported shapes and source combinations fail explicitly.
- Optional native interpolation-history observation. It is disabled by default
  and is enabled by the validation probe before bootstrap.
- SHA-256 identities for the local game and the exact world file bytes.
- Native/offline start, before-PU, after-PU and frame-end samples. Float and
  double values are compared by bits; the report identifies the first divergent
  frame, PU, phase and field. These include player fields, body/render poses,
  clocks, RNG state, currencies and global upgrades. Platform script fields,
  world/local/render and physical body poses, velocity and sleeping state are
  also included. They are not a complete
  digest of every scene component.
- Worker-owned offline snapshots and an A/B/A test: run A, restore, run a
  different B, restore, and require A to reproduce exactly.
- Player animation asset/event export and a candidate-driven animation timeline.
  Footsteps execute the original retargeted event receiver and its RNG calls.
  The supported controller has one integer parameter, one layer and instantaneous
  transitions. Blends, other event functions and StateMachineBehaviours fail
  explicitly rather than silently dropping their effects.
- The original dash-hitstop coroutine, native pause-menu Update and reset
  action, including the recording's checkpoint-respawn setting.
- Explicit interpolation invalidation after scripted transform writes, native
  Invoke clock anchoring and rendered-frame trigger-pair continuity.
- Course/clone economy integration from the exported live fields. Course payout
  text's six random draws are retained in payout order. Untested economy and
  presentation RNG consumers remain a completion gate.
- Coroutine snapshots copy nested compiler-generated closures, preserve shared
  aliases across routines and reject unknown mutable objects. Course path lists,
  animation time/speed and interpolation validity are included in branch state.
- Active-state restore checks, replay without PU observers, shuffled parallel
  worker comparisons and a separate offline replay/allocation benchmark.
- Both search strategies use worker-owned worlds and bounded local snapshot
  caches. Shared beam nodes contain immutable input histories, never another
  worker's Unity/shim objects or snapshots.
- Every worker reconstructs the native bootstrap and unchanged input prefix,
  comparing all captured frame-end fields before evaluating candidates. Jobs
  bind the trace, start, geometry and animation files by SHA-256.
- Refill-orb cooldown, usability, upgrade sequence and dash/jump refill flags
  are compared directly. Active cooldowns participate in branch restore checks.
- Platform reset iterators still waiting at bootstrap are observed without
  advancing or replacing them. The known fixed-phase reset continuation is
  imported with its actual iterator state; unknown or unobserved platform waits
  reject capture. Branch snapshots also copy iterator arrays while preserving
  shared references, cycles, dimensions and lower bounds.
- WaitForSeconds keeps its float duration and strict scaled deadline instead of
  rounding an almost-equal wait down to an earlier rendered frame. Repeated
  identity rotation writes do not teleport an interpolated body.

The actual native clock is separate from the ideal movie phase. Unity exposes a
[rational time grid](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Unity.IntegerTime.RationalTime.TicksPerSecond.html)
and a [rational capture interval](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Time-captureDeltaTimeRational.html).
`NativeFrameClock` is a measured compatibility model under development; the
documentation does not establish that every native physics/interpolation
calculation uses the same expression or rounding.

## Preparing a local host

The reference engine is an external source dependency pinned to
`134d96c6011dd729e75f86f95d6fc16382538aff` of
[ChandlerFerry/IGTAPTasMod](https://github.com/ChandlerFerry/IGTAPTasMod/tree/134d96c6011dd729e75f86f95d6fc16382538aff).
Its source is an external build dependency rather than vendored into this
repository. Portable releases compile its preparation/runtime components and
retain attribution and dependency notices. The generated game assembly is a
local build input and must never be shipped with the mod.

## Building the portable player package

Add `-PortableBruteforce -UpstreamRoot 'C:\path\to\IGTAPTasMod'` to `build.ps1`.
`build-bruteforce.ps1` prepares the pinned dependency and publishes the host and
preparation executable with Windows x64 .NET runtimes. It hashes an explicit
package file list and excludes generated game assemblies and debug symbols.
End users only extract the ZIP; no SDK, Git or runtime installation is required.
On first search the mod checks package hashes and the native game identity, runs
the bundled preparation tool on a background task, and publishes the generated
assembly with a completion marker. Failed/cancelled preparation never starts a
search, never edits the native game, and never falls back to the static model.
Subsequent searches reuse the validated local copy. A changed bundle, generated
assembly or completion manifest triggers preparation again; a different native
game build requires a compatible package.

`Tests/PortablePackage.Probe.csproj` checks a freshly extracted portable folder.
Pass the `Bruteforce` folder, a temporary data folder, the native game's Managed
folder and a validated native search job. It disables normal .NET discovery for
child tools, prepares/reuses/repairs the local game copy, runs host compatibility
checks, and runs a Wave job in a private copy. It verifies the native game hash
is unchanged. This complements native candidate playback rather than replacing it.
Use `Test-Simulator.ps1 -EngineHostPath '<prepared-portable-host.exe>'` to run
native differential/restore/worker checks and optional search acceptance tests
against that exact portable executable instead of the development host.

Prepare a clean local checkout of that commit, then run from this repository:

```powershell
.\prepare-engine-host.ps1 -UpstreamRoot 'C:\path\to\IGTAPTasMod'
# Register this local build for the installed mod's Bruteforce window:
.\prepare-engine-host.ps1 -UpstreamRoot 'C:\path\to\IGTAPTasMod' -RegisterForGame
```

An optional `-ManagedDir` selects the installed game's Managed directory. This
requires the .NET 10 SDK and Mono.Cecil 0.11.6. The script checks the source pin,
copies only tracked dependency files into `Engine.Host/obj/reference`, applies
explicit frame-adapter substitutions, and builds the local host. It never
rewrites the installed game assembly. `-RegisterForGame` writes only
`Recharge/Mods/Tas.Studio/data/engine-host.json`, containing the absolute host
executable path. The mod can also use `TAS_STUDIO_ENGINE_HOST`. Remove that
registration/environment override to select the static worker again. No generated
game files are installed in the mod folder. Upstream compiler/restore warnings are reported
separately from the Tas Studio host build.

The generated executable and its local-only dependencies are under
`Engine.Host/bin/Release/net10.0`. Nested `bin` and `obj` directories are excluded
from SDK candidate discovery to prevent the native mod from accidentally linking
against the retargeted game assembly.

## Comparing a capture

With the game closed, the reproducible controlled capture is:

```powershell
.\Test-Simulator.ps1 -TasPath 'C:\path\to\current-run.tas.json'
# Optional developer regression tests across explicitly selected rates:
.\Test-Simulator.ps1 -FrameRates 30,280
```

This builds the native mod and probe, backs up the installed Tas Studio DLL,
temporarily installs the test builds, runs a short configured level-1 fixture at
the supplied TAS file's recording FPS, and restores the installed DLL in `finally`.
`-TasPath` selects the fixture's FPS; it does not replay the supplied movie.
Other rates run only when explicitly requested with `-FrameRates`. The probe checks that
saved progress is unchanged. New test movies and the temporary probe are moved
into the capture directory. The hidden game process it starts is closed at the
end; an already running game is never reused. Reports live under the ignored
`Engine.Host/artifacts/native` directory. Supply `-HarmonyPath` when the fat
Harmony 2.3.3 dependency is stored elsewhere.

The script requires native/source agreement, all restore/observer checks and
shuffled-worker agreement. It exits 1 if any check fails; a passing run still
does not certify dependencies listed in the export's coverage report.

To capture a real recording's configured start and inputs, use reference mode:

```powershell
.\Test-Simulator.ps1 -ReferenceTasPath 'C:\path\to\Test24.tas.json' -ReferenceEndFrame 1200
```

The end frame is exclusive; omit it to capture the full recording. This mode
reconstructs the configured start and drives a separate temporary recording with
the source inputs in continuous time. It checks the saved start, warmup schedule,
recorded player probes, PU counts and phases against the source. It uses the
movie's normal probe comparator; the richer native/offline traces still compare
their float bits exactly. The source file is copied into the ignored capture
directory, never loaded as an editable movie, and its SHA-256 is checked afterward.
The driver currently requires the standard seed and ten-frame bootstrap.

`reference-comparison.json` reports the source reconstruction and the captured
endpoint's world coordinates. `random-events.jsonl` records sound/animation RNG
consumption for diagnosis. These event timestamps are diagnostics, not an input
to candidate simulation. An animation event model must derive events from each
candidate's state rather than replay the reference's timestamps.

Traces identify `step` or `continuous` advance mode. In continuous recording,
Tas Studio has prepared the following capture interval by its frame-end sample;
when frame-stepping, that preparation waits until resume. The offline clock
models that observation without advancing an extra frame.
Search preparation uses `before-pacing`: its frame-end witness is observed before
the next capture interval is prepared. This is an observation boundary, not an
extra frame or PU. Origin rebases during bootstrap invalidate interpolation history
instead of reusing poses from the old coordinate system.

A test directory contains `world.json`, `start.json`, `animation.json` and
`native.trace.json`. The trace binds the animation asset bytes by SHA-256 too.
Native capture must start before bootstrap if interpolation history is needed.
Do not construct fixtures by substituting expected positions into the offline
model or adding an unrecorded physics step to prime contacts.

```powershell
.\Engine.Host\bin\Release\net10.0\TasStudio.EngineHost.exe trace 'C:\path\to\test'
.\Engine.Host\bin\Release\net10.0\TasStudio.EngineHost.exe restore-check 'C:\path\to\test'
.\Engine.Host\bin\Release\net10.0\TasStudio.EngineHost.exe worker-check 'C:\path\to\test'
.\Engine.Host\bin\Release\net10.0\TasStudio.EngineHost.exe benchmark 'C:\path\to\test'
```

Outputs are `offline.trace.json`, `comparison.json`, and, for the second command,
`restore-comparison.json`, `restore-<state>.json` and `observer-comparison.json`.
`restore-summary.json` reports branch/observer/mover restoration independently
of the native parity result; native divergence still makes the command fail.
The worker and benchmark commands write `worker-comparison.json` and
`benchmark.json`. `engine-error.json` records an execution failure; a failing
trace also records a bounded `first-divergence-state.json` diagnostic.
Exit 0 means the requested trace and restore comparison passed, 1 means a
comparison failed, and 2 means setup or execution failed. A passing trace with
coverage gaps does **not** certify the complete world. Do not commit captures containing game data or distribute generated
game assemblies.

`-SearchSmoke` also drives the real native controller through chain search, beam
search, native verification, draft acceptance and discard. The default test
segment is the constant rightward input on frames 100-129. Select another constant
rightward segment (optionally holding Jump/Dash) with `-SearchSmokeStartFrame` and
`-SearchSmokeEndFrame`, the latter exclusive. For example, Test31's refill check:

```powershell
.\Test-Simulator.ps1 -ReferenceTasPath 'C:\path\to\Test31.tas.json' `
  -ReferenceEndFrame 710 -SearchSmoke -SearchSmokeStartFrame 600 -SearchSmokeEndFrame 650
```

The smoke checks preserve the original suffix and prove acceptance only stages
a draft. They write `search-chain-smoke.json`, `search-beam-smoke.json` and archived
job exports under the capture directory. `-PhysicsDiagnosticsEndFrame` optionally
records native/offline mover states and native contact/impulse diagnostics; these
logs are never simulation inputs. A source frame without an existing player probe
is counted in `UnprobedSourceFrames`, not treated as independently verified.

For a captured search job, `Engine.Host/Test-SearchHost.ps1 -JobPath <job.json>`
checks rejection of modified geometry, a mismatched prefix FPS, missing mover
witnesses and cancellation
before prefix reconstruction. It works on private copies under `artifacts/protocol`
and never edits the original job. All four checks pass with the current host.

Trace I/O streams JSON directly to/from files. Diagnostic traces have a separate
1 GiB import bound because all-platform before/after-PU observations can exceed
the 256 MiB job/result bound. That ordinary import bound is unchanged. Very long
traces still require substantial memory for their decoded samples; this is not
an unlimited streaming simulation or a search-throughput measurement.

## Remaining completion gates

1. Expand native mechanic fixtures at the current recording's FPS. Test24 and
   Test31 pass, but they do not establish fidelity for every route, moving platform,
   spring, active clone, purchase, death or zone transition.
   Validation at all nine rates is not a prerequisite for a same-FPS job.
   Render pose is gameplay state; do not hide drift with a position tolerance.
2. Extend the passing Test30 zipper fixture to additional layouts, contact
   approaches and phase combinations. The tested `zipMover` type is enabled
   after full native trace, snapshot and chain/beam playback checks. Generic
   platform movers still fail explicitly, including already-active contacts.
3. Extend world scheduling and geometry coverage. Springs, purchases, teleporters,
   unrepresented collider activation/movement and mixed composite source combinations
   fail closed. Validate active clones, additional economy/RNG effects and zone transitions.
4. Complete worker isolation and full mutable world/static snapshot coverage.
   A/B/A agreement in the tested section does not establish these globally.
5. Extend the passing two-worker shuffle and replay/allocation benchmark to
   representative native mechanic traces, more workers and verified section
   import. Snapshot costs must be included when measuring search throughput.
6. Broaden the passing native chain/beam import checks to additional mechanics
   and routes. Preserve native verification, ability-use policy and explicit
   Save edits on acceptance; no worker result may edit the source movie directly.

Glitch search remains a later extension. The host keeps the original movement
code rather than replacing wall-clipping or transform-write behaviour with a
sanitised movement controller.

## Test24 reference, 2026-11-08

Test24 is 7,424 frames at 280 FPS with a configured New Game start and the main
abilities/refills/zippers enabled. Its first menu input is frame 6271 and its reset
is frame 7255. All 7,424 inputs were captured at 280 FPS; all saved player probe
and PU schedule checks matched the original recording. The source file and saved
progress were unchanged, and the original installed mod was restored.

This reference exposed and fixed the continuous frame-end capture observation
and the interpolation divisor: native blends use the difference of the public
double clocks divided by the public float timestep. Using an integer tick ratio
caused one-ULP pose differences.

The final offline comparison matches **all 9,877 native samples exactly**,
including RNG, movement fields, body/render poses, clocks, currencies and global
upgrades. Candidate-dependent animation events, the original hitstop coroutine,
course callback order, menu Update before Movement.Update, course payout RNG,
trigger continuity and interpolation invalidation are included. No reference
positions or event timestamps are injected into candidates.

| Check | Result |
|---|---|
| Full native/offline trace | 9,877 exact samples |
| Root A/B/A restore | 9,877 exact samples |
| Pending Invoke, next frame 227 | 380 exact samples |
| Pending transform write, next frame 413 | 374 exact samples |
| Active hitstop, next frame 590 | 366 exact samples |
| Active course, next frame 1627 | 380 exact samples |
| Open pause menu, next frame 6272 | 314 exact samples |
| Replay without PU/contact diagnostics | 7,425 exact start/frame-end samples |
| Two concurrent workers, A/B/A and B/A/B | Same A hash as native; identical B hashes |

A is the full reference. B changes horizontal input on frames 20–39 and runs a
1,200-frame prefix; B establishes worker determinism, not native parity for that
new input sequence. Workers remain at the recording's 280 FPS.

A local three-run sequential benchmark replayed 22,272 frames / 3,678 PUs in
about 0.50 seconds: approximately 44,900 frames/s and 7,410 PUs/s on this machine.
Allocation was about 530 bytes/frame after removing empty-scheduler allocations
and caching the animation callback. A capture/restore pair cost about 1.22 ms and
135 KB at the tested endpoint. These are development replay measurements,
excluding loading, trace observers, search overhead and parallel scaling;
they are not a brute-force candidates-per-second claim.

The latest geometry export recovers **445 inactive shapes**, with **18/18** active
native geometry comparisons exact and no failed bakes. Coverage entries dropped
from 440 to **1**, the native contact/coroutine boundary. This does not certify
arbitrary mid-run snapshots or the entire world. Search reconstructs from frame
zero and verifies the prefix on every worker instead of loading a player-only snapshot.

Both native chain and beam searches starting at frame 100 found a 30-frame
candidate, passed real in-game verification and staged only a draft. The original
7,424-frame recording remained intact through acceptance and discard.

## Test31 refill reference, 2026-11-08

All **2,690 frames / 3,617 native samples** at the recording's **280 FPS** match
exactly, now including all exported refill-orb fields. A dash-only refill enters
cooldown at frame 626; a combined dash/air-jump refill enters cooldown at frame
1505. Root A/B/A, active course/hitstop/invoke/transform checks, an active refill
cooldown restore (next frame 627), observer-free replay and shuffled two-worker
checks pass. Original movie bytes and saved progress are unchanged.

Chain and beam searches from frame 600 across the first refill also passed native
verification and draft-only acceptance. This validates the tested interactions,
not all camera/visibility, refill-upgrade or route combinations.

## Test30 zipper reference, 2026-11-08

The full **4,750-frame Course 10 / 6,375-sample** recording now matches exactly,
including platform script fields, physical/body velocity, world/local/render
poses and sleeping flags. The final source frame has no saved player probe and
is counted separately in the original-recording check.

The frame 584 collision mismatch came from initial fixture registration order.
Native isolated landing tests reproduce both velocity bit patterns by changing
only terrain/actor creation order. The host now registers terrain first while
preserving actor and callback order. Instance-ID sorting was tested and rejected.
Six native cases, three shape representations and eight PUs each pass; opposite
creation-order controls differ as expected. Run `collision-check <capture-root>`
to compare an exported `collision-fixture.json` independently of gameplay.

Additional fixes preserve pending reset iterators, float wait deadlines, full
ancestor transform conversion, no-op transform writes, mover interpolation and
sleep history. An isolated native wake test proves that a new non-touching solid
pair can wake a platform. Retaining predictive bounds while the actual AABB is
contained preserves those pairs; the newer shrink heuristic changed their
lifetime and woke the first zipper too early. These measured rules also appear
in the upstream [Box2D contact manager](https://github.com/erincatto/box2d/blob/v2.3.1/Box2D/Box2D/Dynamics/b2ContactManager.cpp)
and [dynamic tree](https://github.com/erincatto/box2d/blob/v2.3.1/Box2D/Box2D/Collision/b2DynamicTree.cpp).
They do not establish the version or every behaviour of Unity's physics fork.

Root A/B/A (6,375 samples), active mover-wait at next frame 865 (372 samples),
motion at next frame 880 (374 samples), other active-state checks and replay
without PU observers (4,751 samples) pass. Shuffled A/B/A runs on two worker
threads match the full native witness. Hierarchy/sleep/broad-phase compatibility
checks also run separately with `self-check`.

Native chain and beam searches from frame 980 during the zipper ride both
returned 48-frame candidates that passed real-game playback verification.
Acceptance staged a draft; discard retained the original recording. The host
now permits the tested `zipMover` type, checks existing contacts when guards
start, and requires mover witnesses and sleep/reset-continuation capture.
Generic movers, purchases and teleporters remain guarded. Subsequent spring
coverage and the passive glitch-capture audit are documented in
[GLITCH-VALIDATION.md](GLITCH-VALIDATION.md); spring contacts require captured
cooldown witnesses in addition to the player state.
