# Simulator fidelity work plan

The current task is to finish the native-game-code backend in small, verified
steps. Each job retains its TAS recording FPS and 50 Hz PUs. Candidate acceptance
still requires native playback verification and explicit Save edits.

## 1. Isolate the first Test30 collision difference (completed)

- Reproduce the frame 584 landing with native colliders in a private 2D scene.
- Run the same bodies, shapes and velocity assignments in the offline Box2D host.
- Compare contact normals, mass, impulses, CCD and repeated resting contacts.
- Identify the first arithmetic or configuration difference. Do not add expected
  trace values, zero tiny velocities, or loosen the bit comparator.

Native and offline micro-tests reproduce both velocity patterns solely by changing
collider registration order. The loaded-scene adapter registered some terrain
after the player. Instance-ID sorting is not a reliable replacement and was rejected.

## 2. Correct the physics adapter (completed for the reference routes)

- Keep changes in the reproducible dependency preparation/compatibility code.
- Add regression checks based on the isolated native measurements.
- Require full Test24, Test30 and Test31 native differential comparisons.

Registering static terrain first while retaining actor and callback order fixes
the landing. Hierarchy rounding, mover interpolation, native sleep history and
broad-phase pair lifetime are also represented. Full Test30 now matches all
6,375 samples, including mover fields, render/local/body poses and sleeping flags.
Fresh Test31 matches all 3,617 samples with the same stronger witnesses.
Fresh Test24 also matches all 9,877 samples, including root and active-state
restores and replay without PU observers.

## 3. Close zipper state and branch restoration gaps (completed for Test30)

- Compare platform body/transform rounding and coroutine phases directly.
- Test snapshots during initial wait, motion, cooldown and momentum inheritance.
- Enable zipper search only after the applicable native checks pass.

Root A/B/A, initial mover wait, moving-platform and observer-free checks pass.
The two-worker shuffled A/B/A comparisons also match the complete native trace.
Only the tested zipMover type is allowed; generic movers remain guarded,
including contacts already present when a search starts.

## 4. Verify search and delivery (completed for the validated scope)

- Exercise chain and beam searches across the validated mechanics.
- Preserve ability restrictions, cancellation, worker isolation and draft-only
  acceptance. Verify candidates in the real game.
- Run workspace and protocol checks, update coverage documentation, and retain
  explicit guards for mechanics still lacking native validation.
- Generated game assemblies and captured game data remain local build inputs.

Test30 chain and beam searches from frame 980 through a zipper ride each produced
a 48-frame candidate that verified in the real game. Acceptance staged a draft;
discard preserved the original recording. Four protocol checks pass: modified
geometry, mismatched FPS, missing mover witnesses and cancellation. Search jobs
now require mover witnesses plus sleep/reset-continuation capture. All 2,575
workspace checks and 14 prepared-host compatibility checks pass. Streaming JSON
I/O retains byte identity, strict parsing and atomic replacement; diagnostic
traces use a separate bounded import size. Documentation records remaining
mechanic gaps rather than declaring complete world coverage.

## Next coverage work

Follow the agreed [section-search release scope](SECTION-SEARCH-RELEASE.md).
The immediate goal is start frame + point/radius + ability restrictions, native
candidate playback and continuing to record from the accepted endpoint.
Whole-run planning and validation of a discarded suffix are outside that scope.

The new spring TAS and passive glitch archives are tracked in
[GLITCH-VALIDATION.md](GLITCH-VALIDATION.md). The spring fixture exposed an early
after-physics Transform sync; deferring that write restores full trace parity.
Passive archives have a separate integrity/timing audit and are not treated as
fixed-FPS TAS files or proof of complete offline glitch simulation.

- Extend spring orientation/technique coverage and add native differential fixtures for generic moving doors, purchases,
  teleporters, active clone/economy effects and zone transitions before enabling
  those mechanics. Existing guards remain in place.
- Extend branch and worker checks to each new fixture, retaining the current
  recording FPS, native prefix verification and native candidate playback.
- Measure complete search costs, including prefix construction and snapshots,
  before promising throughput or widening worker counts. Long full-field traces
  still have substantial decoded-memory costs.
