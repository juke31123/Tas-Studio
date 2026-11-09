# External simulation and section input search

The current release target is the narrower
[section bruteforce scope](SECTION-SEARCH-RELEASE.md). The broader ideas below
are background design options. Whole-run planning, suffix validation, mandatory
input chains, via/avoid regions and new-glitch discovery are not release gates.

Status: proposed implementation plan; no solver or external physics backend is implemented by this document.

Implementation note, 0.11.0: the dedicated window, camera goal placement, job/worker
pipeline, finite chain enumeration, parallel exploratory search and native
goal-verification/import loop now exist. The initial static-platform simulator is
experimental. The planned retargeted-game host, full Unity/Box2D fidelity,
complete world snapshots, broader mechanics and glitch support remain future work.
See [current coverage](BRUTEFORCE.md).

Development note: the local game-code host now supports both search strategies
when explicitly registered, native-prefix validation and tested refill interactions.
Native export, differential traces and branch restore checks are implemented.
Full physics fidelity and broader mechanic completion gates remain open. See
[simulator development](SIMULATOR-DEVELOPMENT.md) for commands and known gaps.

Target: Tas Studio after 0.10.0. Research pinned to ChandlerFerry/IGTAPTasMod commit `134d96c6011dd729e75f86f95d6fc16382538aff`, checked on 2026-11-07.

## 1. Intended result

Select a section of a movie in the Frame Editor, specify which input chains may change and what counts as success, and search for better inputs in a separate, headless process. The process must execute without a running Unity instance. It reads an exported world, reconstructs the section's start, simulates candidates, and returns ranked input patches and predicted paths. Tas Studio then verifies selected candidates through the real game before they are accepted.

The existing Studio and Frame Editor windows remain in the game process. The solver is a new computational executable, launched hidden by the mod or run directly against an exported job. Rendering and searching have different runtime needs; this worker is not a second desktop UI. A user can close the game after export and let a search continue. Verification waits until the game is available again.

The supplied diagram's Capture -> Search -> Verify loop fits this design. Its route seeds, mutations, archive cells, technique grafts, via/avoid boxes and shared best are useful components. The quoted tick rate, thread count and technique count describe that implementation; they are not performance promises for Tas Studio.

The first supported workflow is a section in the existing gameplay scene. Full-game economic planning, route ordering and cross-scene transitions are later extensions. Objects and side effects that affect the selected section must already be simulated correctly; an unsupported component causes an explicit unsupported result.

## 2. What the research establishes

| Upstream component | Relevant finding | Tas Studio decision |
|---|---|---|
| [Engine host overview][up-overview] | The worker runs retargeted installed-game code with a Unity shim and a customised Box2D backend. | Use this architecture as the primary reference rather than expand our approximate movement predictor into another handwritten controller. |
| [Assembly preparation][up-prep] | Mono.Cecil retargets engine references, applies explicit hooks/cuts and audits mutable static state. | Generate a local worker-only copy from the user's installed game; maintain a versioned list of supported methods and state. |
| [Physics backend][up-physics] and [backend notes][up-box2d] | Unity compatibility needs shapes, filtering, queries, contact/callback order, interpolation and numeric details. A generic Box2D integration is insufficient. | A physics compatibility layer with measured parity is a major deliverable, not a small dependency substitution. |
| [Simulation][up-simulation] | Tracks physics, script fields, timers, coroutines, world toggles, interpolation, RNG and economy in branch snapshots. | Build complete offline snapshots before relying on cheap branching. |
| [OfflineSolver][up-search] | Per-worker simulations combine mutation, novelty archives and route splicing. | Add broad search after the single-worker simulator and constrained chain enumerator pass validation. |
| [SegmentSearch][up-segments] | Goal-window searches reconstruct a prefix or load an offline snapshot, with fixed work shares and worker seeds. | Use immutable section jobs and support a reproducible evaluation-budget mode. |
| [Determinism harness][up-determinism] | Tests shuffled execution, restores, fresh simulations and other processes. | A candidate's result must not depend on whichever candidate ran before it. |
| [Fidelity investigations][up-fidelity] | Real problems included shared statics, contact order, rebasing, RNG, moving platforms and transform write timing. | Build a differential trace corpus and first-divergence reporting alongside the simulator. |

The upstream scheduler and input format cannot be adopted unchanged. Its documented turbo/offline path is organised around physics/input ticks and a 280 FPS interpolation grid; its probe documentation describes a limitation below 50 FPS. Tas Studio must retain 30 FPS, potentially multiple PUs in one frame, and arbitrary input changes on every movie frame at all nine supported FPS values. Its category-specific canonical starts also cannot replace the actual state of an arbitrary selected section.

The local TatsuyaTas implementation was checked as a secondary reference. `SolverUpdate`, `EmulatePerTick` and its Physics2D queries operate around live Unity objects. It offers UI/search ideas, but it does not meet this request's external-engine requirement. GitHub EngineSim is the relevant architectural source.

Direct source reuse needs a provenance check first: GitHub reports no repository-wide licence at the reviewed commit, while the vendored Box2D subtree has explicit notices. Obtain permission or applicable terms for upstream custom code before copying methods. Where reusable, pin its commit, preserve notices and record local changes. This plan copies no upstream runtime methods. Game assemblies and generated derivatives stay local build inputs/cache rather than being included in the public source/release package.

## 3. Existing Tas Studio pieces and gaps

| Existing piece | Reuse | Missing work |
|---|---|---|
| `Core/FrameTiming.cs`, `Runtime/ClockLease.cs`, `LoopHooks.cs` | Recording FPS, 50 Hz PU schedule, phase, input boundary and native pacing reference. | Share a Unity-free timing contract with the worker, including the float/re-anchoring behaviour used by native playback. |
| `Core/Movie.cs` / `FrameInput` | Per-frame axes, held actions and exact press/release edges. | A worker adapter that preserves every edge and the preceding frame at section boundaries. |
| `WorldCheckpoint`, `WorldWitness` | Comparison fields, stable hierarchy identification and world divergence reports. | Reconstructable object graphs, exact collider geometry/material/filter data, lifecycle order, scheduler state and physics backend state. A witness/digest is not a restorable world export. |
| Replay savestates and `RebuildCheckpoints` | Reconstruct and verify the native prefix. | Independently captured offline branch snapshots. The live-witness cache must not be relabelled as an engine snapshot. |
| `ReplayAudit` | Continuous/stepped native verification and report flow. | Compare offline predictions with native execution, verify candidate patches and their suffixes, and compare relevant behaviour against unmodded play. |
| `EditHistory`, `DraftJournal`, explicit Save edits | Safe input patch staging and recovery. | Revision-bound solver result import, preview/diff and explicit acceptance. |
| `MovementPredictor` | Keep as a clearly labelled lightweight stats estimate. | It has no collision world and declines several critical transitions; it is not accurate enough to evaluate or prune solver candidates. A validated simulator can later implement the preview backend. |
| `FrameProfiler` | Identify mod overhead during capture/verification. | Worker-specific simulation, restore, allocation, search-quality and scaling benchmarks. |

## 4. Components and project layout

Proposed additions inside the standalone `recharge-tas` repository:

```text
SimulationContracts/            Unity-free schemas, input/timing contracts, IDs
Runtime/SimulationExport/       Native capture, detailed traces, verification jobs
EnginePrep/                    Local assembly audit and retargeting tool
UnityShim/                     Reachable Unity API compatibility surface
ExternalPhysics/               Pinned physics implementation and compatibility code
SimulationRuntime/             World, scheduler, lifecycle, StepFrame, snapshots
Solver/                        Chains, mutations, archives, objectives, techniques
Solver.Cli/                    export replay/check/search/bench command entry points
UI/NativeSearchWindow.cs        Section/job configuration and candidate review
Tests/Simulation/              Pure tests and differential corpus runner
docs/simulation/               Schema, phase order, coverage and provenance records
```

Use namespaces and boundaries that prevent the native mod's Unity assemblies from being loaded into the worker. The shim's Unity type identities belong only to the worker assembly load graph. Communication contains data and IDs, never live Unity references, delegates or native pointers.

The mod remains compatible with its current Unity/.NET Standard environment. The worker can target the .NET runtime used for our tools and be published for Windows x64 with a bundled runtime if practical. Pin the runtime/architecture in fidelity reports: JIT and floating-point behaviour can affect correctness. A CLI and a file-based job queue come first; optional local named-pipe progress/control can be added without requiring a web service.

Conceptual interfaces, with concrete types decided during implementation:

```csharp
ISimulationSession Create(WorldDefinition world, StartState start);
FrameResult StepFrame(FrameInput input);
OfflineSnapshot Capture();
void Restore(OfflineSnapshot snapshot);
StateFingerprint Fingerprint();

ISearchStrategy.Run(SearchJob job, ISimulationFactory factory, CancellationToken stop);
ITechniqueProvider.Generate(ContactContext context, SearchPolicy policy);
CandidatePatch VerifyAndStage(SearchResult result, MovieRevision currentMovie);
```

`OfflineSnapshot` is private to the simulator that supports its schema/backend version. Cross-worker transfer uses a serialised portable snapshot with object IDs or an input prefix; passing a snapshot with references to another worker's objects is forbidden.

## 5. Capture and export

Capture runs on Unity's main thread at a complete frame boundary while the TAS is paused. No worker reads Unity objects. Export must produce one internally consistent frozen generation; geometry and state cannot come from different frames.

An export bundle contains:

1. **Manifest:** game assembly and relevant Unity assembly hashes, game/Unity/mod versions, timing/bootstrap protocol, scene identity, settings, schema and exporter versions, and a hash for each payload. Include the effects of gameplay-changing mods; unsupported modifications make the bundle ineligible for verified simulation.
2. **World definition:** all relevant objects/components, parent relationships, execution ordering, layers/tags, transforms, enabled/active state, component references and dynamic spawn/despawn definitions. Include dependencies in other loaded scenes or persistent objects when they influence gameplay.
3. **Physics definition:** exact baked shape paths, edge adjacency/radius, offsets, composite source ownership, materials, friction/bounce, trigger state, body type/mass/centre/inertia, damping/gravity scale, constraints, interpolation, collision detection mode, velocities, sleep/activation state and query/contact/layer masks. Capture Physics2D tuning and query settings as well.
4. **Gameplay state:** all movement parameters and transient fields; current and previous input; abilities/resources; moving-platform paths and phases; orb cooldowns; checkpoints; course timing/rewards; currencies/upgrades; block swap; zones/overgrowth; floating origin; RNG; progression preferences and other reachable gameplay state.
5. **Time and scheduling:** movie frame, PU count/remainder, scaled/unscaled clocks, native float/double values, game timeScale, time-stop state, pending Invoke/InvokeRepeating calls, coroutine wait types/deadlines and supported iterator state. The existing movement-timer witness covers only part of this.
6. **Reconstruction data:** the movie's deterministic start/setup, warmup schedule, full input prefix up to the section, and detailed reference traces. Include enough startup history to establish contacts and coroutine history before branching.

Record stable export IDs separately from native instance IDs. Preserve observed execution/callback ordering where it matters; sorting everything by stable ID can silently change behaviour. References are resolved through IDs on import; unknown types or missing references fail validation with the owning component/field named.

Export inactive geometry too. `GetShapes` can yield no usable geometry for inactive or unloaded colliders. Use a capture-specific strategy for each collider type: source paths/assets or temporary isolated geometry probes that cannot participate in the live world's collisions/callbacks/RNG. Composite/tilemap geometry needs explicit validation and cannot be replaced by an axis-aligned bounding box. An incomplete export is marked unsupported.

World-space, scene-space and capture-space coordinates must have distinct types. Include origin offsets and rebase state; do not shift all values into one coordinate system and assume every gameplay field uses it.

### Arbitrary section starts

The default reliable path is: initialise a captured deterministic base, reproduce its bootstrap, then replay the unchanged movie prefix offline to section start. This establishes the external backend's contacts, timers and interpolation history. Cache that resulting offline snapshot only after its trace matches native execution.

Capture a base early enough to reconstruct the physics history, not just player fields after a contact has already formed. Add a bootstrap capture hook if the current post-warmup capture is too late. Imported midair native snapshots are not assumed to contain Unity's hidden contact/cache state. A supported history reconstruction or measured initialisation procedure is required; otherwise that starting point is unsupported. Avoid an extra unrecorded physics step merely to create contacts, because it can change the scenario.

Exports are immutable and content-addressed. Search jobs refer to their hashes. Changing a movie prefix, start setup, FPS, relevant mod, game build or backend invalidates dependent section snapshots and results.

## 6. External game execution

Recommended backend: a locally retargeted copy of the installed game's managed gameplay code plus a Unity shim, inspired by the GitHub EngineSim architecture. A large handwritten movement rewrite would multiply the risk of changing the bugs we later want to search for.

Preparation steps:

1. Discover reachable gameplay methods from player Update/FixedUpdate, lifecycle/collision/trigger handlers, timers, coroutine resumes and world/economy behaviour. Include private/nested and generated iterator types and dynamic calls.
2. Enumerate their Unity/third-party dependencies and mutable statics. Produce a machine-readable supported-surface report.
3. Retarget known types/calls to shim implementations in a separate generated cache directory. Never patch the user's installed game assembly for this purpose.
4. Strip only proven presentation work. A sound/animation method that consumes gameplay's RNG or enables another component needs its side effects reproduced even if no sound/image is produced. Log every cut/hook and its reason.
5. Make clock, RNG, object registry, PlayerPrefs and mutable gameplay statics worker-local; explicitly initialise them for every worker world. Static readonly collections can still be mutable and require auditing.
6. Generate fast delegates/accessors at load time where allowed. Reflection and graph discovery do not belong in the per-frame loop.
7. Reject an unrecognised game build or reachable unimplemented API with an actionable coverage report. Never silently replace gameplay with a default/no-op.

The shim needs Transform/GameObject/component semantics, activation and lifecycle callbacks, InputAction behaviour, scaled/unscaled Time, Mathf/vector numeric behaviour, RNG, PlayerPrefs, Physics2D queries and scheduler/coroutine operations. Rendering is absent, but a minimal camera/viewport model may be necessary when on-screen checks affect clone spawning, rewards or RNG. Preserve those dependencies.

The upstream MonoFloat pass demonstrates a numeric compatibility risk between Unity/Mono and a modern .NET worker. Measure our installed runtime before choosing precision rules. Do not blindly promote all floats to doubles, enable fast-math, reorder expressions, use SIMD/FMA, or apply the upstream pass without differential evidence. Preserve rounding at stores, signed zero and branch-sensitive thresholds where relevant.

## 7. Two clocks and the frame stepping contract

The search unit is **one movie frame**, never one PU. A job has exactly one recording FPS: 30, 50, 60, 100, 120, 144, 165, 244 or 280. Its physics interval stays 0.02 game seconds. Viewing speed changes wall-clock pacing only and has no place in an offline rollout.

The job inherits that FPS from the current TAS recording. It does not search over
FPS values or require the other recording rates to be validated first. Native
parity, coverage eligibility and cached section snapshots are checked for that
job's FPS. Cross-rate development regressions remain optional.

Examples: a 280 FPS rollout has many frames with no PU; a 30 FPS frame may contain multiple PUs. Dash hitstop or an open in-game pause menu may produce input/Update frames while scaled physics is stopped. Those frames still exist and may change game state.

`StepFrame` must reproduce the actual native loop's phases and writes:

1. Begin the frame using the previously prepared capture delta and game scale; advance the clocks using the same precision and PU remainder rules as native playback.
2. Execute every due fixed phase in order. PUs before dynamic input see the prior sampled input. Reproduce script FixedUpdate, transform synchronisation, physics, collision/trigger callbacks, fixed-yield resumes and native timer phases in their measured order.
3. Process the new frame input at Tas Studio's existing dynamic-input boundary. Honour axes, held state, exact edges, same-frame press+release, and press/release lifetime.
4. Apply the native interpolation/read/write sequence and run relevant Update callbacks, game-menu updates, world/economy steps, Invoke callbacks, coroutine resumes, LateUpdate and end-of-frame effects in the captured/measured order.
5. Finalise observed body/transform/interpolation state and prepare the next frame's delta/PU schedule, including game-driven timeScale changes.

This is the required phase list, not an assertion that every Unity callback's precise relative position is already known. Detailed traces must settle ordering ambiguities before the implementation is certified. Reuse the timing maths through a Unity-free shared contract after parity tests; do not merge or rewrite the current native loop merely to accommodate the worker.

Trace both PU boundaries and frame boundaries. Check native capture delta rounding/re-anchoring, not just an ideal `1/FPS` accumulator. Preserve the transform's interpolation endpoints, current blend, write-dirty state and when writes reach Rigidbody2D. A step-up between PUs can change the body later; simply making transform.position equal body.position removes real behaviour.

Implement Invoke, InvokeRepeating and supported coroutine waits according to their measured phases and clocks. Mapping every delay to a rounded number of PUs can move input buffers or respawn events to a different frame. Menu time and dash hitstop can use different scaled/unscaled paths. Tests must cover both.

## 8. Physics compatibility and offline branching

Box2D is a starting point. Pin a backend version and adapt it to the installed Unity behaviour using the GitHub backend and its fidelity notes as references. Required coverage includes boxes/capsules/circles/polygons/edges/composites, one-way/platform effectors where reachable, overlap/raycast/shape casts, triggers, include/exclude masks, starting inside geometry, contact normals/order, continuous collision handling, sleeping/activation, transform sync, friction/restitution, moving platforms and origin shifts.

Do not correct a wall penetration, clamp an unusual speed or normalise contact state just because it looks invalid. Match whatever the supported native game does. Glitches are often consequences of these details.

Offline snapshots must restore:

- Gameplay fields, object reference graphs, stateful collections and active/enabled/layer/shape changes.
- Both inputs, clock values/PU phase, pending timers and coroutine states, RNG and progression/economy state.
- Body poses/velocities/forces/sleep state, interpolation endpoints and dirty-transform flags.
- Physics contacts/manifolds, warm-start impulses, broadphase proxies/order, filtering, trigger pair history and any backend caches that influence future results.
- Goal/via progress and any state accumulated by search-relevant mechanics.

Implement complete offline snapshots because we control that backend's memory and state model. This is different from restoring Unity's opaque native world. Initially keep snapshots local to a worker; add a portable serializer only after restore parity is proven. Body/fixture creation, destruction, reshaping and zone changes must be supported explicitly or invalidate an incompatible snapshot with prefix replay as the fallback. The upstream Box2D snapshot API itself documents topology restrictions, so copying only its SaveState call would be insufficient.

Verify uninterrupted replay equals capture/restore/continue, including alternating two unrelated snapshots, crossing a rebase, toggling geometry and running arbitrary other candidates between restores. Snapshot cost and cache memory must be measured before choosing the capture interval.

## 9. Section and input-chain configuration

The Frame Editor gains **Search selected section**. Selection is a half-open range `[startFrame, endFrame)`; show clear inclusive labels in the UI to avoid off-by-one confusion. A job freezes the base movie revision and references a validated export. Inputs outside its editable range are locked unless the user explicitly expands it.

Configuration:

| Setting | Meaning |
|---|---|
| Section and horizon | First mutable frame, last mutable frame, maximum rollout length and suffix/check region. |
| Input permissions | Mutable buttons/axes, immutable cells, allowed directions/analog samples, reset/menu permission, minimum/maximum holds. |
| Input chain | Ordered actions/events with time windows, duration ranges, precedence, optional branches/repeats and conditions. |
| Goal | Reach a world-space region/gate, complete a course, reach a state/event, or match a rejoin condition. |
| Objective | Earliest movie frame by default; optionally native course time, exit speed/direction, or lexicographic time then input changes. |
| Constraints | Ordered via regions, avoid regions, death/reset allowance, required resources/abilities, permitted transitions. |
| Search | Exhaustive chain enumeration or heuristic search; work/time/RAM budgets, seed, workers, retained candidates. |
| Technique policy | Permitted known techniques, unknown-behaviour policy and supported simulator features. |

Example chain, expressed as UI concepts rather than a final file schema:

```text
Section: [1200, 1280) at the movie's 280 FPS
Locked: Right held throughout; Menu and Reset disallowed
Jump: press 0..8 frames after section start; hold for 1..6 frames; then release
Dash: press 2..12 frames after Jump; select Right or Up+Right
Goal: enter the chosen exit region, alive, moving right, with at least 1 dash
Continuation: replay the original suffix through the next checkpoint
Rank: earliest accepted exit, then highest horizontal exit velocity
```

An event-based condition such as 'after touching the wall' is a compiled chain automaton driven by simulator events. The resulting candidate is still flattened to concrete per-frame inputs for replay. Each chain must have finite repeat/duration/horizon bounds for exhaustive mode.

Default controls use the existing single visible Jump/Dash actions. Preserve the movie format's distinct underlying actions and exact edge semantics; do not merge them in simulation if their release behaviour differs. Candidate generation must produce input combinations the recording/replay action model can represent, with provenance for same-frame taps. Direct writes to position, ability counts, timeScale or internal game fields are not candidate actions.

Analog axes require an explicit finite search set, initially existing values plus neutral/cardinal/diagonal choices, with optional local refinement. Exhaustive mode proves coverage only for its stated finite alphabet and bounds, never every representable controller trajectory.

## 10. Search algorithms

### A. Exhaustive constrained chain search

Compile chain choices into a finite grammar/automaton. Enumerate press frames, release frames, holds, directions and optional branches lazily. Compute candidate count in advance where feasible, and show an estimate or range when event-dependent branches prevent an exact count.

Use a prefix tree: simulate each common input prefix once, snapshot at useful branch points, then restore and evaluate alternatives. Partition independent subtrees across workers. Apply only proven-safe pruning: invalid input grammar, explicit avoid/goal violations and valid objective bounds. 'It moved away from the target' is a heuristic, not a proof that a branch is useless.

Completion status includes evaluated count and coverage. 'Optimal within configured chain' is allowed only after every admissible combination has been evaluated or safely bounded under a validated simulator. If cancelled, budget-limited or interrupted by unsupported behaviour, report best found and incomplete coverage. In-game verification confirms replayability, not a proof of global optimality.

Even a basic alphabet of 9 directions x Jump on/off x Dash on/off gives 36 choices per frame; 60 unconstrained frames have about `2.39e93` sequences. Multi-threading does not make unrestricted enumeration feasible.

### B. Longer-section heuristic search

Use a hybrid inspired by the diagram and `OfflineSolver`:

1. Seed with the original section, saved alternatives and previous verified improvements.
2. Mutate action timing, hold/release duration, direction runs and coordinated jump/dash combinations. Preserve chain locks.
3. Explore short random/structured bursts from underexplored archived states.
4. Keep a bounded diversity archive and promote better candidates through a coordinator.
5. Splice/graft useful input segments, then simulate from the correct preceding snapshot through the join and continuation.
6. Add optional beam/contact-event planning for difficult gaps and later technique grafts.
7. Continue until budget, cancellation or a satisfied search target; persist resumable progress.

An archive cell should include more than position: velocity/momentum bands, contact class, resources, movement mode, goal/via progress and frame/PU phase. Relevant mover phase, RNG/economy state and progression must influence equivalence or remain distinct labels. The diagram's 'earliest arrival per 48x48 cell' is a useful heuristic but can discard a later arrival with more speed, a usable dash, or a better platform phase. Start with several Pareto/diversity representatives per cell and configurable budgets.

Keep coarse archive grouping separate from exact state identity. Use exact fingerprints plus collision checks to reuse a deterministic rollout. Never infer identical future behaviour from matching position alone, a spatial cell, or a rounded hash. Any prefix-dependent objective/chain progress is part of the state key.

An A*/Dijkstra distance field can guide exploration around terrain but is not necessarily an admissible lower bound, particularly when clipping or velocity boosts are allowed. Use it as a preference in heuristic mode. A known-tech-only or shortest-geometry-path search must not be the only exploration mechanism.

### C. Boundaries and the unchanged suffix

Support two explicit section modes:

- **Fixed length:** replace the same number of frames; optimise endpoint/progress/speed or goal arrival within the window. Frame numbers outside the selection stay fixed, but the changed terminal state still requires suffix checking.
- **Variable length:** allow frames to be inserted/deleted; the suffix shifts and must be re-simulated with the resulting clock/world phase. An earlier local exit is not automatically a faster valid full TAS.

At a splice, compare current/previous action state so a held button is not accidentally turned into a fresh press or release. Preserve explicit edges; do not simply paste boolean holds and hope the boundary works.

For a truly exact rejoin, compare the complete future-relevant state, including PU remainder, contacts, timers, platform phases and RNG. Exact rejoins may be rare or impossible after shortening a section. Otherwise replay the original continuation from the candidate's actual state to the configured checkpoint/goal or end of movie. Rank 'section-only improvement' separately from 'continuation passed'. Multi-section searches must rebuild downstream starts after an earlier section changes.

## 11. Parallelism and performance

First make one worker correct. Initially allow one isolated worker process per simulation if necessary to avoid shared game statics. Then move to a single external host with dedicated worker threads after static-state and backend audits pass. A process mode remains useful for isolation and diagnostics.

Each thread owns its world, physics backend, object graph, scheduler, game statics, RNG, pools and snapshots for its whole lifetime. Share only immutable world definitions/geometry and immutable candidate messages. With ThreadStatic-based hosting, a Simulation belongs to its owner thread; do not schedule its steps on arbitrary TaskPool threads or interleave another world that steals the thread's shim hooks. Initialise thread-local fields explicitly, including collections normally created in static constructors.

Parallelise candidate rollouts, not the internals of a single contact solver. Changing contact iteration order to exploit threads can change outcomes. Work stealing moves candidate descriptors/prefixes, not live engine objects.

Use a coordinator for immutable global best publication and batched archive updates. Avoid a shared lock or shared dictionary insertion on every simulated frame. Workers can search different seeds/strategies or disjoint chain subtrees. Cap thread count by measured throughput and RAM as well as CPU count; offer a preset that leaves CPU capacity for the game/UI.

Hot-loop requirements:

- Preload and resolve the world once per worker; no JSON, disk I/O, reflection discovery or Unity IPC per rollout/frame.
- Compact input runs for storage and bounded buffers/pools; retain exact expanded edges when running candidates.
- Restore the nearest valid offline snapshot before the earliest changed input; do not replay the entire TAS for every mutation.
- Share immutable snapshot chunks only after proving no mutation aliases; bound cache bytes and evict by measured replay cost.
- Avoid snapshot-per-frame by default; benchmark intervals and branch/event-based captures.
- Keep full traces off during broad search; collect them for baseline/parity/debug and promising candidates.
- Reuse broadphase/query buffers and remove measured per-step allocations while preserving numeric/callback order.
- Limit UI progress to a few updates per second and persist bests/checkpoints asynchronously at safe boundaries.

Provide two scheduling modes:

1. **Reproducible:** fixed candidate IDs, seeds and evaluation/step budgets; deterministic batches, tie breaks and publication. Re-evaluating a given candidate must always match. Exact search trajectories across different worker counts require explicit deterministic partition/merge rules.
2. **Throughput:** asynchronous discovery/publication; same seed alone does not guarantee the same search trajectory because workers see improvements at different times. Save the exact winning input patch and all job/version metadata.

Benchmark candidates/s, simulated frames/s, PUs/s, snapshot capture/restore cost, allocation bytes, memory peak, worker CPU utilisation and verification rejection rate. Report frames and PUs separately; a 280 FPS frame with no PU costs differently from a PU frame. Test 1/2/4/8/... workers on representative geometry and compare solution quality at equal budgets. The screenshot's roughly 1.7 million ticks/s on 32 threads is a reference claim, not our target guarantee. GPU acceleration is not the first implementation: stateful branching/contact code and per-frame dependencies favour CPU workers.

## 12. UI, jobs and result acceptance

A new native **Section Search** window is opened from the Frame Editor. It contains range/chain configuration, goals/via/avoid regions, a worker/budget selector, export readiness, progress and a ranked candidate list. Reuse the detached command/snapshot pattern; no Unity access from the window thread.

First deliver numeric region fields and 'use current player bounds/selected gate' helpers. Add a detached 2D map of exported geometry for drawing goal/via/avoid boxes later. Show predicted paths there and in frame-list/stat previews; do not require restoring the removed in-game hitbox viewer or cluttering gameplay.

Job files contain a schema version, immutable export/movie/prefix hashes, selected range, FPS, chain/objective/policy definitions, seeds and budgets. Results contain concrete input patches, variable-length changes, achieved goal frame/PU/time, terminal state fingerprint, trace/version metadata, coverage or search count, unsupported events and validation status. Worker snapshots/caches use their own versions and stay separate from `.state.json` replay savestates.

Result lifecycle:

```text
Exported -> Fidelity checked -> Searching -> Offline candidate
         -> Native verification queued -> Verified / Rejected / Unsupported
         -> Stage selected patch -> Save edits -> Rebuild as needed
```

Native verification uses a copy of the movie: reconstruct its real start/prefix and execute the candidate plus required suffix at the recorded FPS with normal 50 Hz physics. Compare predicted frame/PU events, player/world state and success metrics. Initially use ordinary native pacing; a future turbo judge must prove it matches native playback before substituting for it. Verification never writes predicted state into Unity to repair divergence.

Verify a bounded shortlist, not every enumerated candidate. Queue checks and report status; repeated native failures are simulation bugs to diagnose, not valid speed records. A closed or busy game leaves results waiting on disk.

Before staging, check that the current movie's revision/prefix still matches the job. Otherwise require a new export/search or explicit revalidation. Back up the original, stage only the selected patch through `StageTimelineEdit`, and keep rebuilding tied to **Save edits**. Undo/Redo and draft recovery must work on imported patches. A predicted result is never silently substituted into the live recording.

## 13. Glitch support from the start

Accurate vanilla execution is the prerequisite: a glitch is valid when native execution produces it from representable inputs. The simulator must not add an invented 'wallclip' action or teleport to the other side of a wall.

Separate three concepts:

1. **Simulation capability:** can this backend reproduce the underlying event/objects at this FPS?
2. **Search policy:** does the user permit a known technique/category or an unclassified result?
3. **Search generator:** does a technique library supply useful input patterns near an event?

Technique definitions carry ID/version, prerequisites, contact/context matcher, frame-relative action template with parameter ranges, optional chaining, classifier/detector, and verified examples/counterexamples. Useful events include wall/ledge contact changes, landings, dash start/end, buffer expiry, spike touches, platform attachment, orb refill and rebase. Each template generates normal frame inputs that pass through the same simulator and native judge.

Keep raw-frame mutation/enumeration available outside known templates, including changes between PUs and during time stops if allowed by the real game. A library should help discover faster patterns, not make unknown glitches unreachable. Do not import the upstream mod's human-input restrictions or category definitions as universal Tas Studio rules.

Policies may include 'known forbidden techniques', 'known allowed techniques', 'allow unknown verified behaviour', and 'strict classification required'. Detector absence means unknown; it does not prove glitchless. Unsupported simulation is also distinct from an unknown but faithfully reproduced technique. Exact-mode pruning/deduplication must preserve phase, contact and momentum differences that could create a glitch.

Develop an isolated lab suite with exported real-game collider arrangements and very small frame windows. Add a known between-PU wallclip only when offline and native traces agree. Persist each discovered glitch case as a regression, with FPS, input chain, full start and trace. Future learning/mining can propose templates from validated cases.

## 14. Validation gates

Testing grows with implementation. Existing fake-Unity tests remain useful for UI/session logic but do not establish external physics parity.

| Gate | Evidence required |
|---|---|
| Export completeness | Required types, references, inactive geometry, settings and scheduling state are present. Unsupported content is named and rejected. Export does not perturb native state/RNG/progression. |
| Clock/input parity | All nine FPS values; frame-only input changes; multi-PU 30 FPS frames; edges after long editor pauses; same-frame taps; scale changes; menu/hitstop; frame/PU counts and event order. |
| Free movement parity | Native vs offline jump/release/dash/gravity/momentum paths at frame and PU boundaries. |
| Contact/world parity | Corners, high speed, multiple contacts, query edge cases, triggers, moving platforms, refill orbs, block swaps, death/reset, rebasing, zone activation and relevant progression/economy. |
| Snapshot independence | Uninterrupted vs restore/continue; A/B/A restore order; random candidate order; worker/process/cross-worker serialisation; topology changes; no stale shared state. |
| Chain correctness | Every generated input obeys locks/grammar; a tiny known finite search is fully enumerated; cancellation reports incomplete coverage; no heuristic pruning disguised as exhaustive proof. |
| Section continuation | Locked prefix and input boundaries preserved; fixed/variable-length patches; suffix effects; no position-only rejoin acceptance; stale result rejection. |
| Native verification | Candidate and suffix pass at recorded FPS from a reconstructed start; offline/native objective and success events agree. Compare with unmodded gameplay for the mechanics claimed to be vanilla-equivalent. |
| Performance | Benchmarks include correctness digests and unchanged corpus outcomes. Speedups never waive a failed fidelity gate. |

Native comparison uses documented numeric tolerances only where justified; discrete states, PU schedules, event ordering and hit/miss decisions must agree. Record bit/ULP differences for diagnosis rather than hide them behind a broad position tolerance. Offline restore/worker determinism should be exact on the supported runtime. No rounding, snapping or copying expected state to make tests pass.

Trace mismatch reports identify the first frame, PU and phase, input edge, component/field, expected/actual numeric bits, collider pair/query, timer/coroutine event, RNG and origin. Automatically minimise failing input ranges where practical. Add near-boundary/adversarial cases around each fixed mismatch, so a solver cannot repeatedly exploit the same emulator error.

## 15. Delivery sequence

| Stage | Deliverables | Completion condition |
|---|---|---|
| 0. Contracts and feasibility | Provenance decision; game API/call graph/static audit; versioned export, timing, input and section-job contracts; small native trace corpus. | A real selected section exports reproducibly; gaps and code reuse dependencies are explicit. |
| 1. Single-worker host | Local assembly preparation, minimal shim, clocks/input, world loading and CLI replay/check. No broad search yet. | Supported free-movement traces match at the current recording's FPS. Validate other FPS values independently when used; cross-rate regression runs are optional. |
| 2. Physics/world coverage | Baked geometry, query/filter/callback semantics, interpolation, timers/coroutines, movers/orbs and relevant world/economy. | A declared mechanic/FPS coverage matrix passes native differential tests; unsupported paths stop explicitly. |
| 3. Offline snapshots | Complete capture/restore; prefix replay/cache; state fingerprint; memory and restore benchmarks. | Continuations match uninterrupted execution regardless of preceding work and restore order. |
| 4. Useful section search | Finite input-chain grammar, exhaustive enumerator, objectives/constraints, prefix sharing, candidate/suffix validation; CLI then native search window. | A small user-selected chain is fully searched externally and a best found result passes the native judge. |
| 5. Parallel and broad search | Audited dedicated workers/process fallback, bounded archives, mutation/beam exploration, resumable jobs, deterministic and throughput modes. | Measured speedup with identical candidate outcomes, no state leakage and bounded memory. |
| 6. Technique/glitch work | Event hooks/classification, template library, lab cases and known between-PU glitches. | Each enabled technique has native-confirmed examples; raw exploration remains available. |
| 7. Broader planning and previews | Multiple dependent sections, longer economy/zone runs, path preview/hitbox overlays in the detached map, optional route/upgrade planning. | Full supported runs pass suffix/final native verification with cached starts invalidated correctly. |

Add fidelity fixtures as each mechanic becomes supported; do not postpone all validation to the last stage. Publish useful bounded support at Stage 4 with a clear coverage matrix rather than call an incomplete simulator universal.

The main research risks are Unity contact/callback parity, arbitrary-start reconstruction, numeric/runtime differences, game-code/static isolation and the amount of world/economy code reachable from a short section. These decide the schedule. There is no credible fixed throughput or completion-time estimate before Stages 0-2 produce measurements.

## 16. First implementation milestone to approve

Start with **export + trace + one-worker external replay**, including all current FPS values and exact per-frame input semantics. Then add **bounded chain search over a selected section**, with a real native verification/import loop. This yields the requested workflow while the simulator's coverage grows. Parallel heuristic search and technique mining build on that validated foundation.

No production runtime, movie format or installed mod is changed by this plan.

[up-overview]: https://github.com/ChandlerFerry/IGTAPTasMod/blob/134d96c6011dd729e75f86f95d6fc16382538aff/enginesim/README.md
[up-prep]: https://github.com/ChandlerFerry/IGTAPTasMod/blob/134d96c6011dd729e75f86f95d6fc16382538aff/enginesim/Prep/Program.cs
[up-physics]: https://github.com/ChandlerFerry/IGTAPTasMod/blob/134d96c6011dd729e75f86f95d6fc16382538aff/enginesim/Runtime/PhysicsWorld.cs
[up-box2d]: https://github.com/ChandlerFerry/IGTAPTasMod/blob/134d96c6011dd729e75f86f95d6fc16382538aff/enginesim/Box2D/NOTES.md
[up-simulation]: https://github.com/ChandlerFerry/IGTAPTasMod/blob/134d96c6011dd729e75f86f95d6fc16382538aff/enginesim/Runtime/Simulation.cs
[up-search]: https://github.com/ChandlerFerry/IGTAPTasMod/blob/134d96c6011dd729e75f86f95d6fc16382538aff/enginesim/Runtime/OfflineSolver.cs
[up-segments]: https://github.com/ChandlerFerry/IGTAPTasMod/blob/134d96c6011dd729e75f86f95d6fc16382538aff/enginesim/Cli/Runs/SegmentSearch.cs
[up-determinism]: https://github.com/ChandlerFerry/IGTAPTasMod/blob/134d96c6011dd729e75f86f95d6fc16382538aff/enginesim/Cli/Determinism.cs
[up-fidelity]: https://github.com/ChandlerFerry/IGTAPTasMod/blob/134d96c6011dd729e75f86f95d6fc16382538aff/docs/fidelity-log.md
