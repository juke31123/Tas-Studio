# Version 0.19.4 next-frame action readiness

Both action rows now populate **Next Frame** from the predicted future state.
The fallback evaluates its detached next motion with its unchanged-contact
assumptions. The game-code preview exports immutable readiness alongside each
future trajectory point, using future unlocks, wall-jump charges, hyper/menu/
hitstop flags and native movement fields. This metadata is separate from
serialized movie/search movement state. Missing or unavailable forecast metadata
is shown as `--`; current readiness is never substituted for a missing forecast.

Tests cover air-jump charge consumption, rising-speed eligibility changes,
cooldown expiry, zero-PU frames, JSON transport, current/future context separation,
stale-value clearing and desktop table delivery. The fallback withholds menu
transitions rather than claiming that the next frame remains controllable.

The persistent game-code host matches future action readiness reconstructed from
native trace fields for 190 frames across 13 forecasts at 50 FPS, with branch
rollback, prefix rewinds and wrong-state rejection. A separate origin-only
16-frame forecast at 280 FPS matches readiness and motion through zero-PU frames
and passes rollback/wrong-state checks. The older longer 280 FPS fixture still
rejects a historical `clock/capture-ticks` witness during prefix requests; its
full history is not reported as passing this update's checks.

# Version 0.19.3 jump and dash readiness

The player stats table adds current **Can jump** and **Can dash** rows. Jump
readiness labels the resource/type, including the extra air jump. The evaluation
reads the existing motion sample and separate UI-only unlock/menu/wall context;
it does not add fields to serialized movement/search state. The next-frame
readiness cells deliberately remain unavailable. Small desktop layouts use the
existing compact font to fit the additional rows.

All 655 standalone and 2,967 workspace checks pass. Pure checks cover
ground/wall/air priority, exhausted wall attempts that block
air-jump fallback, internal-velocity ascent boundaries at 200/700, strict dash
cooldown and wall-lock boundaries, queued-dash priority, hyper/dash-jump gates,
unlock/charge limits and menu/hitstop/death/physics locks. Workspace checks cover
desktop row delivery and clearing/loading/paused lifecycle behavior.

A temporary native observer checks 86 calls to the game's normal input-consumer
method, including one ground jump, one extra air jump and one dash. Readiness and
the reported air-jump resource agree with actual input/charge consumption. A
separate replay without the observers reproduces all 100 recorded frame probes;
all 293 phase samples match its external trace, and restore/shuffled-worker
checks pass. This is distinct from the initial stepped fixture's stricter phase
comparison, which reports a body-position difference before PU 69; that initial
fixture is not claimed as full external parity. All 32 save files remain
unchanged after both controlled processes exit. Wall/hyper readiness is covered
by source-based pure checks here, not a new native wall/hyper validation claim.

# Version 0.19.2 Cold search-prefix reconstruction

The user's `Cold Bruteforce test` records 401 frames at 100 FPS. Five failed
search jobs stopped at frame 35, independently of search algorithm or selected
start. A temporary native probe identifies a composite-contact order difference:
the first native contact is diagonal while the external host reports an axial
ceiling contact, changing `Movement.OnCollisionStay2D`'s vertical-velocity clear.
Custom worlds now use newest-first world-contact visitation. The vanilla-world
policy is unchanged. Dependency edits are reproduced by the pinned preparation
script, not maintained only in the ignored build cache.

The 386-frame captured search prefix reaches the native anchor with all fields
passing the normal start-state checks. Two samples at frames 85-86 have a
remaining transient contact clear that resolves at the following PU. The new
prefix validator accepts only this zero-versus-one-gravity-step case without a
recent jump or vertical momentum, while requiring solid contact, matching
positions throughout and the usual complete final witness. Intermediate
vertical contact-solver remainders have a separate 0.001 bound. No simulated
state is overwritten from the witness. Mutation checks reject position, RNG,
currency, unrelated movement, jump/momentum, missing-contact, duration, schedule,
incomplete-witness and final-anchor differences. This is a limited recovery
policy, not a claim of exact custom-map contact simulation.

A controlled native replay reproduces the full 401-frame source recording.
Separate searches on frames 120-140 of that recording run Beam and Wave through
the real controller and rebuilt host. Both candidates pass native verification;
acceptance stages a draft and does not modify the source movie. The existing
strict trace comparator still reports custom-map body float residuals, starting
at frame 31; this is not reported as full external trace parity. Branch restore
passes. All 32 saved files remain unchanged after the controlled processes exit.

All 625 standalone and 2,931 workspace checks pass. A fresh extracted portable
bundle passes first-use preparation without global .NET discovery, cache repair,
wrong-map rejection, and both algorithms on the Cold job. The installed update
replaces only the 401 Bruteforce files, retaining the 0.19.1 runtime and manifest
so the source recording's mod fingerprint stays valid. The full 0.19.2 ZIP is a
separate build for new recordings. TAS data, saved progress and the recorded mod
fingerprint are unchanged by the solver-only installation.

Regression traces remain bit exact: 1,485 zipper samples, 3,617 refill samples,
4,195 spring samples and 3,836 purchase samples. The `prefix-check <job.json>`
developer command validates reconstruction without running a search or writing
over the job's result. Frame-end-only diagnostic witnesses now retain their
sample schedule in trace and restore checks.

# Version 0.19.1 trajectory control stability and profiling

Native-window regression checks delay game-side command processing while old
snapshots refresh repeatedly. Enable/disable clicks remain visible until their
acknowledgment; unapplied length edits survive refreshes, including turning off
with an unfinished edit. The snapshot-refresh command preserves the enabled
preference. The standalone and workspace suites also cover the new profiling
sections and their reset behavior.

Controlled game tests at 30 and 280 FPS inject an unavailable forecast after a
valid preview. For more than 2.5 seconds the feature stays enabled and its capture
count does not increase. Explicit refresh then creates exactly one new capture
and recovers the preview. Movement forecasts, full native traces, restore and
shuffled-worker checks pass afterward. A COLD custom-map test repeats these
checks at 100 FPS: all 33 native samples across 16 frames match exactly.
World-coordinate comparisons in the probe materialize float32 results before
comparing them, avoiding Mono's wider intermediate arithmetic at large map
coordinates; no simulation tolerance is increased. Saved progress stays intact.

The COLD timing result and the limits of the user's old profiler are recorded
in [the profile review](PERFORMANCE-20261112.md). Renderer buffer reuse and
unchanged paused uploads are optimized without reducing replay/world checks.

## Version 0.19.0 trajectory preview

The standalone and workspace suites pass with trajectory protocol, held-input,
timer-deadline, stale-input/position/velocity and next-frame stats checks. The
native controls test checks that the Preview tab and its controls are present.

Controlled in-game tests at 30 and 280 FPS compare held-input forecasts with
native body position and velocity. All three frames before the first input
change at 30 FPS and all 16 at 280 FPS match. The full 16-frame native traces
match all 67 and 23 native samples respectively, including restore and shuffled
worker checks. The hidden in-game LineRenderer contains the forecast geometry.
These are headless checks, not a pixel-level review of the overlay's appearance.

Enabling at frame 30 of a 280 FPS jump, with expensive history collection off
before enabling, forecasts the following 16 frames correctly. Its full 48-frame
trace matches all 67 native samples and passes restore/shuffled-worker checks.
The test compares physics/game fields, movie cursor/frame count and random
state; Unity's interpolated sprite can settle after pausing and is excluded
from the side-effect assertion. All 32 saved files remain unchanged after exit.

A persistent-process test on the native 30 FPS capture checks ten forecasts
across 31 subsequent frames, including jump/dash input changes. Alternate
hypothetical inputs do not alter a repeated forecast, prefix rewinds work, and
an intentionally incorrect live-state witness is rejected without corrupting
the following valid request. Current-frame captures remain explicitly marked
as estimates; these checks do not claim universal mechanic/glitch parity.
A second persistent-process test at 280 FPS checks five forecasts across 98
native frames, including the jump transition and mid-jump prefixes, with the
same rollback, rewind and incorrect-witness rejection checks.

## Version 0.18.2 continuous Beam refinement

All 577 standalone and 2,870 workspace checks pass. Beam regression checks
cover improvements over a seeded route, non-increasing best frame counts,
continued refinement beyond 192 attempts, the full time budget after archive
exhaustion or a one-frame candidate, and cancellation retaining a complete
candidate. Ability limits, exact input edges, snapshot ownership, sequential
checkpoint lookahead and work-stealing cancellation checks continue to pass.

A controlled 280 FPS CortexPistis search uses its full 20-second budget
(20.001 seconds), evaluating 126,568 candidates with 62,076 refinements. Its
five-frame candidate passes verification in the native game, and acceptance
stages only a draft without changing the source movie. All 16 reference frames
match the recording, branch restore passes and all 32 saved files remain
unchanged after process exit. The strict external trace/worker comparison still
reports the previously documented small custom-map body residuals; this is
not reported as bit-exact agreement. The native candidate verification passes
independently of that diagnostic.

## Version 0.18.1 live map discovery and capture identity

All 573 standalone and 2,866 workspace checks pass. New checks cover map
installation after the first catalog scan, loaded-map/revision rejection,
search-job identity, and checkpoint-map geometry refreshing after the loaded
context changes. The Studio, Bruteforce and checkpoint-map title bars update
from the actual loaded context without advancing TAS frames.

A controlled game test opens the real Create New TAS screen, then installs an
owned temporary map that did not exist at startup. The open menu detects it
automatically and retains its previous selection. The new map adds a distant
terrain tile to the prototype. Its actual loaded ID/name, the new tile in the
geometry capture and the search-job ID/hash are checked in game. Capturing with
an intentionally wrong map ID is rejected. All 23 native samples across the
first 16 frames match exactly, with restore and shuffled-worker checks passing.
The temporary map is removed afterward. All 32 saved files remain unchanged
after process exit, with no restorations or removals required.

## Version 0.18.0 custom maps and VMAN

All 566 standalone and 2,858 workspace checks pass. New checks cover custom-map
discovery, stable map/asset identity, changed/missing maps, path validation,
setup persistence, timed/practice VMAN configuration and isolation of persistent
VMAN reward preferences. Custom-map prefix residual tests restrict the allowance
to finite body position/velocity values; ordinary map checks, movement state,
clocks and larger differences remain strict.

Controlled tests loaded all three installed maps through Navigator's real builder:
Cold (1,150 objects), CortexPistis.exe v0.2 (29,795) and Endurance Y emo 2 (6,883).
Endurance's first 16 frames match all 23 native samples exactly, including branch
restore and shuffled workers. Cold's first 48 frames match all 67 native samples
exactly. In timed VMAN, Beam and Wave each find an 18-frame custom-map route and
pass verification in IGTAP. Saved-file reload plus continuous and frame-stepped
native playback pass.

CortexPistis exposes rebuilt-tile native body residuals as small as two float ULPs
in position and about 0.000003 in velocity. Its strict diagnostic trace reports
these differences. With the documented custom-map-only 0.0001 prefix allowance,
Beam and Wave reconstruct frame 8 and each find a five-frame route that passes
native verification. The report marks NativePrefixMatchedExactly=false and counts
the residual fields. Saved-file reload and both native playback modes pass.

A full 280-frame Cold jump trace has eight samples with small body-velocity
residuals, while every other captured field matches. These measurements are not
reported as bit-exact agreement. The simulator never applies the allowance to
movie hashes, movement-state witnesses or game-side candidate verification.

VMAN practice matches all 375 native samples across 280 frames, including its
count-up timer, with A/B/A, pending-invoke, hitstop, active-course and observer
restore checks passing. Timed VMAN's native clock also matches in the Cold tests.
All controlled runs restore the installed DLL and preserve saved progress.
A final isolation test starts with Navigator remembering an older map. Its
save owner is cleared before loading and after stopping; all 32 saved files
remain identical after process shutdown, with zero restorations or removals.
The controlled-test script now backs up Savedata folders and checks after exit
as well as inside the game.
Custom upgrades/group/area triggers and VMAN purchases still require dedicated
adapters/validation; affected search branches are guarded. See CUSTOM-MAPS.md.

## Version 0.17.0 checkpoint budgets and map editing

All 549 standalone and 2,836 workspace checks pass. New coverage includes the
global target-plus-allowance ceiling, a fresh timer for each checkpoint, full
state handoff, lookahead restoring hidden state and physics clocks, and Beam/chain
staged searches. Cancellation before or after a committed checkpoint never
exposes an incomplete route for acceptance. Native-window checks cover the
budget controls, enabling rules, map placement, drag/drop, Escape cancellation
and middle-button panning without advancing or changing the recording.

A controlled 280 FPS Cashloop capture covers frames [0,230). All 313 native
samples match the external simulator; restore and shuffled-worker checks pass.
Beam and Wave each search three ordered checkpoints using three seconds per
checkpoint, 12-frame lookahead, a one-frame target and a 70-frame allowance.
Both produce 59-frame routes which pass in-game verification. Beam performs
36,756 preview frames and Wave 180,375; neither commits preview frames. Accepting
stages a draft while preserving the original movie. The test restores the
installed DLL and confirms the original saved progress is unchanged.

This validates a short three-point section and state restoration, not globally
optimal route choice or every possible map interaction. Lookahead uses three
continuation policies and an estimated-distance score. Per-checkpoint mode makes
greedy stage choices; whole-route search remains available.

## Version 0.16.1 input durations

All 534 standalone and 2,814 workspace checks pass. Input runs carry an explicit
zero-based start, positive duration and full logical sample. FrameCount must
match both the duration sum and per-frame verification count. Tests reject gaps,
overlaps, missing runs/verification rows, non-positive or oversized durations,
missing total counts and per-frame input overrides. Exact press/hold/release
boundaries, repeated same-frame taps, signed-zero axes and authored rows round-trip.

All nine current-format named files from 0.16.0 retain identical expanded
canonical fingerprints after conversion to duration storage. Their combined size
falls from 12,530,246 to 11,626,219 bytes, another 7.2% smaller. The substantial
recordings save 7-8%; empty/single-frame movies gain a few bytes of count metadata.
Cashloop stores 5,694 logical inputs as 78 runs, whose durations sum to 5,694.
Every per-frame verification row and movement hash is retained.

The native driver reads the duration-encoded Cashloop reference and matches all
1,087 samples across the first 800 frames, with branch and shuffled-worker checks
passing. A saved v2 movie reloads with the same canonical fingerprint. Continuous
and frame-stepped native playback each match 800 frames and 144 PUs. The installed
DLL is restored afterward and saved game progress is unchanged. The in-memory
timeline and playback/physics code continue to consume ordinary individual frames.

## Version 0.16.0 compact movie storage

All 515 standalone and 2,795 workspace checks pass. Storage tests retain exact
edges/retaps, inferred inputs, authored rows, all player flags, distinct body and
render velocities, movement-field changes/removals, world changes/removals,
negative zero, subnormal values and finite float extremes. Invalid layouts,
missing hashes, malformed compressed witnesses and trailing JSON are rejected.
Native engine snapshots/traces keep their existing fields.

All nine supported named movies in the local movies folder round-trip to compact
copies with identical expanded canonical fingerprints. Sources remain untouched
during comparison. Combined size falls from 86,928,433 to 12,530,246 bytes
(85.6% smaller). The five substantial recordings measure:

| Run | Before MiB | After MiB | Typical lines/frame | Maximum |
| --- | ---: | ---: | ---: | ---: |
| Cashloop | 15.75 | 2.27 | 11 | 13 |
| Springs | 20.92 | 3.21 | 12 | 14 |
| Test24 | 20.13 | 3.01 | 11 | 14 |
| Test30 | 13.25 | 1.98 | 12 | 13 |
| Test31 | 8.08 | 1.14 | 11 | 13 |

The starting-world payload is retained, gzip-compressed instead of expanded into
thousands of path/value lines. Related values share tuples; repeated movement
fields and periodic world values use deltas. Loading restores their full values
and the existing comparison tolerances. Every per-frame movement hash and its
movement-field inputs remains intact. Canonical source/cache fingerprints sort
dictionary keys, avoiding culture/order differences, and stream JSON into SHA-256
without allocating a full expanded JSON string and UTF-8 copy.

The native driver reads a converted Cashloop reference, records 2,825 frames and
matches all 3,836 before/after-PU/frame samples, with branch and shuffled-worker
checks passing. Beam and Wave each find a 28-frame three-checkpoint candidate,
pass native verification and stage a draft. A freshly saved compact movie is then
reloaded with the same canonical fingerprint and audited in the real game:
continuous and frame-stepped passes both complete 2,826 frames and 506 PUs, with
all player and periodic world checks matching. The one additional saved frame is
the frame completed before the capture driver's queued pause. The controlled
driver restores the installed DLL and confirms game saves are unchanged.

This validates the storage roundtrip and tested playback/search paths; it does
not expand the simulator's mechanic coverage. See [file storage](FILE-STORAGE.md).

## Version 0.15.0 ordered checkpoints and CashPerLoop

All 487 standalone and 2,767 workspace checks pass. New tests cover ordered and
out-of-order circle visits, overlapping goals, checkpoint branch ownership and
restore, turnback routes, Beam refinement, native start-boundary counting and
the same-process checkpoint map's edit/reorder controls without recording input.
Prepared-host compatibility checks pass all 27 assertions.

The supplied `Cashloop.tas.json` recording at 280 FPS matches all 7,729 native
samples across 5,694 frames, including CashPerLoop purchases at frames 2812 and
5349. Witnesses include local upgrades, mutable CashPerLoop box state, currencies
and RNG. A stale local-upgrade dictionary in the adapter was exposed and fixed;
restored branches resynchronize the game objects from the branchable economy.
Sleeping player bodies retain their physical pose instead of receiving redundant
interpolation. No expected trace values are injected to obtain agreement.

The post-purchase snapshot at frame 2813 also restores after an intervening
movement branch and matches all 380 continuation samples through frame 3092.

A fresh 2,825-frame native capture matches all 3,836 samples, with original movie
witnesses, A/B/A restore and shuffled-worker checks passing. Chain, Beam and Wave
searches through that purchase section all visit three numbered circles in order,
pass native playback and stage a draft while retaining the original input suffix.
Chain finds 36 frames; Beam and Wave find 28 within their 20-second search budgets.
The controlled driver restores the installed DLL and confirms saved progress is
unchanged. This is a focused fixture, not certification of all upgrade types.
Only normal local CashPerLoop single purchases are enabled; buy-max, Vman,
global CashPerLoop and other upgrades remain guarded.

Offline replay of all nine existing FPS regression fixtures still matches all
2,040 native samples. Checkpoint completion is evaluated at movie-frame
boundaries, including frames without PUs; numbered circles constrain one joint
search rather than separate greedy legs. Results are best found within the
budget, not proofs of a globally fastest route.

A fresh extracted portable bundle passes first-use game preparation with global
.NET discovery disabled, setup reuse, generated-cache repair and host checks.
Its Wave search visits all three Cashloop checkpoints and finds a 28-frame
candidate. The installed game assembly remains unchanged. The clean archive has
one `Tas.Studio` root and excludes recordings, local captures and generated game
assemblies.

## Version 0.14.0 simulation, performance and experimental scheduling

All 444 standalone and 2,720 workspace checks pass, including the native checkbox
binding through the fake Unity boundary. Prepared-host checks add strict timer
deadlines, stable callback ordering, cancellation, repeating invokes and reentrant
dispatch. Snapshot tests retain aliases, cycles, signed zero and value-array
isolation. Experimental queues execute each task once, respect simulator-thread
ownership and shut down on cancellation.

Native captures at all nine game FPS values (30, 50, 60, 100, 120, 144, 165, 244,
280) match all 2,040 samples. Each passes branch restore and shuffled-worker
checks. The initial run exposed platform body/render rounding at 100/165/244 FPS
and an early Invoke at 50 FPS. Independent exported body poses and strict timer
deadlines correct these mismatches; expected observations are never injected
during replay. These are short movement/jump/dash fixtures, not all-mechanic
certification at every FPS.

A four-worker experimental Beam run executes 14,586 tasks, steals 841 and
produces a 377-frame candidate for the existing 395-frame section. It passes
native playback, draft acceptance and endpoint rebuilding. Its 1,600-frame
reference matches all 2,167 samples. The controlled test restores the installed
DLL and confirms saved progress is unchanged. Work stealing remains optional.

The complete supplied spring recording additionally matches all 10,473 samples
across 7,740 frames. Restores cover pending invokes, dash hitstop, a tracked
course, active spring lock/cooldown and a pending Transform write at frame 4386.
Four-worker Beam and Wave each verify an 18-frame spring section from a fresh
portable package. Native cache/ownership checks include disabled same-frame
jump/dash taps and detached-node isolation.

One same-frame native measurement reduces world-check time from 24.04 to 20.30 ms
per call, with identical player and world witness hashes. Stats now sample when
the 10 Hz desktop snapshot is due, rather than at every game frame. Profiler
autosaves debounce repeated steps; explicit export and session teardown remain
immediate. Timeline paging avoids walking the skipped prefix, movie saves stream
JSON, and live search progress omits repeated input/path arrays. Final results
and movie writes retain durable atomic replacement.

Live progress is optional: transient I/O or JSON-read failures retain the last
metrics while the worker runs. Final results remain strictly parsed. A local
controller failure is saved to `controller-error.json` and cannot be overwritten
by the worker's subsequent cancellation report. Tests exercise truncated live
progress, malformed final results and original-error preservation. One native
startup cancellation was observed before this hardening; its original cause was
overwritten, so that cause is not claimed as established. The diagnostic Wave
rerun and subsequent four-worker Beam/Wave repeat both pass.

The 9,300-frame offline replay benchmark reduces measured allocation from 568.2
to 360.9 bytes/frame. This excludes world loading, trace observers and search
snapshots. The native Mono allocation counter returns no usable data, so native
byte estimates are null rather than reported as zero. Timings depend on hardware
and workload and do not imply that the game will sustain every selected FPS.

Reusing only the adapter's working state reduces its measured 327-frame replay
allocation from 1,112.9 to 712.9 bytes/frame. Detached archive nodes retain their
own motion values and immutable histories; native cache checks explicitly verify
that later steps cannot mutate them. Float witnesses also avoid a temporary
byte array while preserving signed zero, infinity and NaN payload bits.

In the five-second 280 FPS fixture at Beam width 128, two repeated runs each at
one, two, four and eight workers produce a 327-frame candidate with one to four
workers, and 371 frames with eight. Two/four-worker stealing evaluates roughly
10% more branches than affinity in this sample, with more reconstruction work;
the candidate length is unchanged. The unseeded goal remains unsolved within
five seconds. These local measurements support keeping affinity and two workers
as defaults; they are not a universal scaling or route-quality guarantee.

All six passive ZIPs retain their previous scoped results: both lift captures
match completely; the inwall captures differ only at their initial contact, and
the spring-directions capture matches all 19,548 samples from the first spring
onward. Its earlier tiny body-Y residual remains a strict failure, with 554
differing samples. No tolerance or observed-state overwrite conceals that gap.

# Version 0.13.0 Beam search improvements

All 413 standalone checks and 2,681 workspace checks pass. The workspace session
checks use fake Unity boundaries. New Beam checks cover detours away from the
goal, slow motion within archive position buckets, single-frame jump windows
between PUs, exact same-frame press/release events, route refinement, preview
reuse, cancellation and worker ownership. The gameplay Recorder still builds.

The native host additionally passes 36 cache checks with a deliberately small
256 KiB retained budget: eviction/reconstruction, full captured-state A/B/A
restoration, identical continuation, repeated-history restoration and rejection
of foreign simulator/thread snapshot tokens. The normal target is 64 MiB of
estimated retained snapshots per worker, excluding roots, active temporary
tokens, histories and the worlds/runtime. This is not a whole-process RAM limit.

In the actual Unity/Mono game at the recording's 280 FPS and 50 Hz physics,
Beam found a 377-frame candidate for the supplied 395-frame section. Native
playback reached the goal with the selected ability limits. Acceptance staged
a draft and retained the original inputs; Save edits rebuilt the endpoint.
The reference capture matched all 2,167 samples across 1,600 frames, and native
branch restoration and shuffled-worker checks passed. The controlled test
restored the installed mod DLL and confirmed saved progress was unchanged.

A second native run starts from a fresh extracted portable package and exercises
the mod's background first-use preparation. Beam and Wave both produce
18-frame spring-contact candidates that pass native playback and draft
acceptance. The 3,100-frame reference has 4,195 matching samples. This checks
the new snapshot path against an interaction rather than only free movement.

The same offline comparison fixture used before the change was repeated with
280 FPS, a 397-frame horizon, two workers and a five-second budget. Seeded Beam
improved from the previous 394-frame result to 327 frames; Wave found 332 in
that run. Without a seed, Beam explored to depth 228 rather than the previous
10; neither solver reached this goal within five seconds. The 327-frame result
is an offline candidate, separate from the native-verified results above.
Wall-time budgets and parallel exploration vary between runs. This one fixture
does not show that Beam always beats Wave or establishes a general speedup.

A fresh portable ZIP passes all 400 declared tool-file hashes and excludes
generated game assemblies and personal data. First-use setup, reuse and cache
repair work with global .NET discovery disabled; host compatibility and Wave
search also pass. These checks establish packaging and the exercised sections,
not universal mechanic parity, exhaustive search or global route optimality.

# Version 0.12.2 search label cleanup

The search selector and current documentation use **Beam search**,
**Exhaustive input chain** and **Wave search**. This patch changes display text
and package version metadata. Algorithm identifiers, input generation, movie
timing and simulator behaviour are unchanged. All 384 standalone checks and
2,652 workspace checks pass.

# Version 0.12.1 portable section-search validation

The workspace suite passes 2,652 checks, including portable-package integrity,
game-build identity, cancellation before preparation, unsafe file entries and
continued recording immediately after Save edits accepts a searched section.
The session integration checks use a fake Unity boundary.

A freshly extracted Windows x64 portable package prepares the installed game's
simulator copy without normal .NET discovery, reuses it on subsequent searches,
repairs altered generated files and runs host compatibility checks and Wave
search. The installed game assembly remains unchanged. The ZIP excludes game
assemblies, personal recordings and save data.

The actual Unity/Mono game also exercised the mod's background first-use setup
from a fresh portable package. At the supplied Spring TAS's 280 FPS and 50 Hz
physics, 3,100 frames / 4,195 samples match, branch restores and shuffled workers
pass, and Wave finds an 18-frame section which passes native playback. Accept
stages a draft while preserving the original until Save edits. The controlled
test restores the installed DLL and confirms original saved progress is unchanged.

A second native search checks a 395-frame movement-only section. Wave finds a
254-frame candidate reaching the same goal with the selected ability restrictions;
native playback and draft acceptance pass. Its 1,600-frame reference capture has
2,167 matching samples. This saves 141 movie frames on that particular section;
it does not establish optimality or predict speedups on other routes.

The additional spring-direction passive capture has exact agreement on all
19,548 scoped player samples from the first spring onward. Its full comparison
still fails on small starting Y-velocity differences before frame 239; see
[spring validation and limits](GLITCH-VALIDATION.md). This is not universal
mechanic/glitch certification or a claim of globally optimal routes. Dependency
builds may report upstream nullable warnings or unavailable NuGet audit data;
the Tas Studio mod and host compile without compiler warnings.

# Version 0.11.0 experimental validation

The production mod and external solver compile against the installed references
with zero warnings/errors. The workspace suite contains 2,480 checks, including
native window opening/reopening, camera rebinding/goal precision, bounded chain
enumeration, parallel independent states, cancellation, polygon hazard outlines,
all-rate frame/PU scheduling and real external-worker integration. The standalone
repository suite contains 214 checks.

The search integration tests use a fake Unity boundary for session behaviour.
They verify that predicted success is rejected when the native model misses the
goal or consumes a disabled ability, and that accepting a verified candidate
creates a draft, deletes the suffix only after Save edits, and preserves backups.

A temporary mod additionally ran the actual installed Unity/Mono game at 50 FPS.
It recorded a short Level 1 route, set the final player position as the goal,
reconstructed frame 0, exported 5,795 collider shapes and searched on two external
workers. The candidate passed native goal verification, was accepted/rebuilt/saved,
and the resulting 16-frame movie replayed with matching native probes. Goal-camera
panning/restoration preserved body position, velocity, movie, frame and RNG. The
saved-progress hash before/after the test matched. The probe and its test movies
were archived outside Mods; they are excluded from the release.

Earlier native runs exposed broad bounding boxes for spike outlines and precision
loss on thin edge geometry; the export now uses individual shapes/segments and
precision-safe thickness. This experimental backend still approximates movement,
contact and timing details. Native verification proves goal reachability for a
candidate, not Unity-equivalent simulation or global optimality. Full world,
moving-platform, economy, coroutine and glitch parity remain future work. See
BRUTEFORCE.md and EXTERNAL-SIMULATION-PLAN.md.

# Version 0.10.0 validation

All 2,392 workspace checks pass, including 181 new core/runtime reliability
checks and native Redo-button integration. The standalone source repository
suite contains 158 checks. The production build compiles against the installed
game assemblies with no warnings or errors.

Coverage includes bounded multi-level input history, exact-edge undo/redo,
stale-history rejection, branch invalidation, flushed draft recovery, interrupted
final writes, damaged complete entries, rejected writes while the journal is
locked, recovery without automatic replay, bounded profiling, and startup/world
checkpoint matching. All nine game FPS choices exercise continuous and stepped
replay audit, same-frame savestate reuse, edited-then-undone prefix reuse, and
fallback reconstruction when the live world no longer matches its checkpoint.
Currency-only divergence is detected even when the player probe matches.

The installed Assembly-CSharp IL was inspected: OpenA2Door is used by
upgradeBox.Start, upgradeBox.buyUpgrade and pauseMenuScript.deleteSaveFunction.
The session-local preference tests verify captured values, configured starts,
write/delete isolation, and replay after the machine's preference changes.

Offscreen native Win32 windows were rendered and inspected, including the Frame
Editor and Checks tab. Session tests use a simulated Unity boundary: real gameplay,
native coroutine/contact fidelity, runtime patch execution and performance at
280 FPS still require in-game acceptance. Verify replay is provided for that work;
it does not independently prove equivalence to an unmodded game.

Arbitrary backward native-memory restoration is not implemented. Only matching
still-live checkpoints are reused; changed/older prefixes reconstruct by replay.

# Version 0.9.4 validation

All 2,208 workspace checks pass. PU stepping is exercised at all nine game FPS
options, through hitstop and replay, with observed-frame boundaries and unchanged
50 Hz physics. Tests cover an open menu, a five-second no-PU limit, unchanged live
inputs preserving future timing, edited playback restoring PU observations without
inventing movement probes, and repeated draft clones retaining display history.
Real offscreen Win32 tests verify red rows with Follow disabled and the current
frame offscreen, pending timing markings, and removal when re-observation finds
zero PUs. Both buttons and F11 dispatch the same command. Existing six-key settings
retain their bindings; an occupied F11 leaves the new shortcut unbound.
Session checks use a simulated Unity boundary; live in-game acceptance is pending.

# Version 0.9.3 validation

All 1,045 automated checks pass, including 80 menu session checks at 30/50/280 FPS.
The tests cover physical input after editor pause, held input without repeat toggles,
zero PUs and stable phase while the game menu is open, recorded Resume clicks,
savestate reconstruction with an open menu, and serialized replay with menu/guard
verification. Native desktop tests exercise painting/discarding the Menu column,
its held color and repaint invalidation. The production build compiles against the
installed game assemblies without warnings/errors. Native pauseMenuScript.Update
and its dynamic-input query were inspected in the installed game's decompiled code.
Session tests use a fake Unity boundary; live in-game menu acceptance is still pending.

# Version 0.9.2 validation

Test20 exposed a missing physical input edge: Jump became held at zero-based frame
128 but ExactEdges was true with Press=0. It remained held until frame 240; the
release at frame 241 also had Release=0. The game requires a press to set its jump
buffer. Unity had processed those events during editor pause before the next step.

The session now merges physical held-state changes since the last simulated input
into native press/release flags before recording and delivering them to the game.
Native same-frame press/release taps are retained. Authored and replay inputs are
consumed as stored; no physics scheduling or input sampling phase changed.

A regression using Test20's held-input sequence failed before the fix. It now
passes at 30/50/60/100/120/144/165/244/280 FPS, with pauses before every step,
exactly one press/release across the held interval, an additional same-frame tap,
and matching edge-consumer probes through serialized replay. All 957 checks pass;
the production build has zero warnings/errors. These regression tests simulate the
Unity boundary; an actual in-game jump acceptance test remains necessary.

# Version 0.9.1 validation

The release builds against the installed game assemblies with zero warnings/errors.
All 867 automated checks pass, including 74 new keybind checks: persistence and
backup, invalid/duplicate assignments, clearing/defaults, corrupted settings fallback,
detached snapshot copies, Unity focus/modifier/menu suppression, and Win32 key routing.
The native-window tests confirm updated labels in both editors, rebound pause/step,
and suppression of repeated keys, previous bindings and typing in a text field.
The new Unity settings page compiles against the real game UI assemblies; its visual
layout and interactive key capture still need an in-game acceptance check.

# Version 0.9.0 validation

The native windows share a new theme and grouped layout. Real Win32 previews were
rendered and inspected for the Run page, savestates, frame editor and minimum-size
Studio. All 793 workspace checks pass and the production build has no warnings or
errors. The existing native command, frame painting, repaint suppression, resize
resource and lifecycle checks remain in place. These previews run outside Unity;
appearance and focus on the user's game setup still need live acceptance.

# Version 0.8.2 validation

The built-in hitbox renderer, settings, native tab and associated tests are removed.
All 793 remaining workspace checks pass, including a native UI check confirming
that the removed controls are absent. Frame/PU timing and FPS diagnostics remain
covered. This removes the built-in overlay's cost; 280 FPS is not guaranteed by
this change alone.

# Version 0.8.1 validation

All 825 workspace checks pass, including 14 new real-time FPS diagnostics checks.
These verify actual versus target FPS, achieved speed, pause exclusion, frame-step
intervals, EOF accounting, separate CSV reports and nonfatal IO errors. Measurements
are passive; live slowdown diagnosis awaits the user's short recording/replay.

# Version 0.8.0 validation

All **811 workspace checks** pass. This includes 55 pure timing/format checks and
315 session checks using a simulated native clock, covering independent frame/PU
rates, alignment from different starting phases, game freezes, pauses, stepping,
slow playback, checkpoint reconstruction and rebuilding edited input. The standalone
repository tests pass 137 checks. The production build has zero warnings/errors.
Native Win32 previews were visually inspected for red PU rows and the stats countdown.

Separate **live Unity/Mono tests** used a temporary Recharge probe and the actual
installed game's FPS dropdown: **30, 50, 60, 100, 120, 144, 165, 244, 280**. At each
rate an 82-frame recording included horizontal movement, a jump/release and a dash.
Named checkpoint reconstruction, explicit saved-edit rebuilding and subsequent full
replay passed. State probes, observed PU counts/phases and checkpoint world witnesses
(including RNG) remained enabled. An additional 100 FPS test passed two-second editor
pauses, one-frame stepping and playback at 0.1x. Evidence remains locally in
`.research/fps-probe/result.log`; the temporary probe and generated test runs are
excluded from the release.

The first-load checkpoint test initially exposed random-state divergence caused by
footstep animation events while gameplay was frozen. The startup/pause event gate
fix passed the previously failing first-load case and the subsequent full matrix.
The probe now treats rejected commands as failures and requires an observed reload
before accepting reconstruction; an earlier false-positive test result is not counted.

These are short, controlled runs, not proof of whole-game or cross-machine determinism.
Specific wallclips, long routes, moving-platform/refill interactions and equality with
unmodded play at the same FPS still need targeted in-game comparison. The next-frame
movement predictor remains an estimate without collision/interpolation simulation.

# Version 0.7.0 validation

All 438 workspace checks pass, including 25 hitbox geometry/preferences checks and 83 native desktop/session checks. New tests cover circles, capsules, rounded polygon winding, display filters, dot/comma parsing, RGB validation, atomic preferences, corrupted settings, detached native commands, checkbox/Apply controls and changes during paused recording without movie mutation. The new native Hitboxes tab is rendered and visually inspected. The production overlay builds against the installed Unity APIs. Unity collider extraction, camera alignment, shader behavior and renderer lifecycle still require in-game acceptance; this historical feature was removed in 0.8.2.

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
