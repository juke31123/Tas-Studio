# Bruteforce window (0.12.1 experimental)

Open **Studio > Run > Open Bruteforce window** during a paused recording or replay.
The controls run in a third native window inside IGTAP. Search evaluation runs
outside Unity. The portable release includes a self-contained game-code host in
`Bruteforce/Host`, automatically prepared from the installed game on first search.
It takes priority over a saved development registration. It requires neither an
SDK nor a separate runtime. An explicit `TAS_STUDIO_ENGINE_HOST` environment
override still takes priority for development. Missing, altered or incompatible
portable files report an error rather than silently using the approximate solver.
See [host setup and validation](SIMULATOR-DEVELOPMENT.md).

Development packages without `Bruteforce` use `Solver/TasStudio.Solver.exe` and
need .NET 10, unless a game-code host is registered. Install the whole package.
The scope is section search: start frame, goal point/radius and ability limits.
Continue authoring from the accepted endpoint; input chains are optional. See
[release scope](SECTION-SEARCH-RELEASE.md).

Each search uses the current TAS recording's saved frame rate. Every candidate
and worker runs at that one FPS, with 50 Hz physics; the search does not explore
other frame rates. Candidate verification and playback use the same recorded FPS.

## Workflow

The solver selector offers **Our beam search**, **Our exhaustive input chain**,
and **Wave search**. Only Wave search requires the local
game-code host. It runs the original upstream `OfflineSolver` from the pinned
dependency, using our frame clock and full worker-owned snapshots. Its shared
archive, random exploration, route mutations, plateau walking, segment splicing,
compressed input history and bounded state caches remain upstream code.

Source attribution: Wave search adapts `OfflineSolver` from
[ChandlerFerry/IGTAPTasMod](https://github.com/ChandlerFerry/IGTAPTasMod), pinned
to commit `134d96c6011dd729e75f86f95d6fc16382538aff`.

The upstream macro-tick offsets (`dashJump`, `springDash`, lock/freeze stick and
pause macros) and automatic technique-demo graft library are not enabled in this
first frame adapter. They cannot be applied as movie frames without translation.
Raw Jump/Dash/direction rows still run through the original game code at the TAS
FPS, including frames between PUs. Ability restrictions and native verification
apply to every algorithm. Archive search reports the best found, not an exhaustive
or globally optimal result. Its state cache budget is 64 MiB per worker and its
shared archive budget is 64 MiB, in addition to the game worlds and runtime.
The upstream exact-state convergence shortcut is disabled for frame-adapter runs
until its equality test covers the frame scheduler; native snapshots still restore.

Our exhaustive chain mode remains useful for timing a specific input sequence.
Our beam mode remains available for controlled comparisons and independent
development. Selecting the archive option never silently falls back to beam.

1. Record or replay to the destination. Pause. Opening Bruteforce initially uses the
   player's current world position as its goal. **Goal = current player position**
   refreshes it. X/Y and the goal radius can also be entered directly; decimal point
   and comma are supported.
2. Enter **Start frame**, an already executed frame at or before the TAS cursor.
   **Use TAS cursor** copies the current frame. Set the maximum search frames, time
   budget, workers and allowed abilities.
3. Optionally select **Place goal with free camera**. Focus the game and move the
   camera with the arrow keys, zoom with the mouse wheel, then click the destination.
   Escape cancels. The player/body never moves, and camera navigation is not recorded.
   The original camera position, rotation and zoom are restored when placement ends.
   Closing the window, stopping or unloading also ends placement.
4. Each camera key can be rebound using the four key buttons at the bottom of the
   Bruteforce window. Click one, then press a different arrow, letter, digit or
   function key. Escape cancels rebinding. Settings persist in `data/goal-camera.json`.
   Scroll the window when the lower controls do not fit the display.
5. Click **Start search**. If necessary, the mod reconstructs the selected start
   using the original movie prefix. The game then stays paused while the external
   worker searches. Existing future inputs remain available as a seed and backup.
6. A candidate is **unverified**. Click **Verify candidate in game** to replay the
   original prefix and candidate at the movie FPS. The native player must reach
   the goal alive; observed resource consumption also rejects disabled air-jump,
   wall-jump or dash use. The temporary verification movie cannot overwrite the TAS.
7. **Accept result as draft** stages the replacement from Start frame onward.
   **Save edits** in the Frame Editor applies it, removes the old suffix, rebuilds
   the changed recording and saves it. Undo/Redo and draft recovery remain available.
   **Cancel / discard preview** restores the original inputs and reconstructs their
   state when necessary. Original movies are backed up before reconstruction.

The ability boxes restrict use; they do not grant upgrades or change the recording's
start setup. Dash count and extra-jump count come from the actual selected start.
There is no player noclip/teleport mode: goal placement uses the detached camera.
Transport is blocked during goal placement, preparation and native verification.

## Search modes

**Heuristic beam search** explores allowed direction/button combinations in parallel
and keeps a bounded set of promising states. Input hold is its action block length;
use 1 for changes on every frame. A larger value searches longer actions more cheaply
but can miss narrow timing windows. The result is best found within the budget,
not a globally optimal route. Worker count is configurable from 1 to 32.

**Exhaustive input chain** enumerates duration choices for a bounded action sequence:

```text
R:1-30; R+J:1-6; R:1-40
```

This means hold Right for 1-30 frames, Right+Jump for 1-6 frames, then Right for
1-40 frames. The finite choice count is 7,200 before horizon/unsupported pruning.
An action with one duration, such as `R+Dash:1`, has a fixed length.

Directions are `N`, `R`, `L`, `U`, `D`, `UR`, `UL`, `DR`, `DL`. Add `+J` or `+Dash`
for buttons, subject to the ability restrictions. Durations are movie frames,
not PUs. The goal is checked at the end of the complete chain. Maximum search
frames remains a hard limit. Time/cancelled searches report incomplete coverage;
unsupported branches also prevent an optimality claim. Enumeration completeness
does not establish accuracy of the experimental simulation model.

The window shows search status, depth/horizon, evaluated branches, simulated frames,
PUs, elapsed/budget time, unsupported rollouts, nearest distance, candidate length
and native verification status. Progress/result files are atomically replaced in
`data/search/<job-id>/`. The original movie and immutable job export are stored there.
Jobs can be run directly with `Solver/TasStudio.Solver.exe <job.json>`.

## Current simulation coverage

The smaller development package's fallback is an **exploratory static-platform
simulator**. That fallback does not run the game's retargeted managed code or a
Unity-equivalent Box2D backend. The portable release uses the game-code host
described below; neither backend certifies every game behaviour.

It uses captured movement parameters and a separate frame/50 Hz PU clock, static
convex polygon/edge geometry, swept player bounds, an acceleration/gravity/jump/dash
model and worker-local branch states. A spatial index avoids scanning the full map
for every movement query. This supports short route experiments but is approximate,
including jump/dash transitions, contact responses and interpolation behaviour.

Moving platforms, many special triggers and unsupported shapes stop affected
rollouts. Known course/checkpoint/camera/clone-visibility triggers are omitted from
movement exploration; their economy/camera/RNG effects are not simulated. Native
verification executes those real callbacks. Scene transitions, arbitrary coroutine
state, full economy and glitch simulation are not covered. No glitch toggle is
enabled until a backend can reproduce the relevant behaviours.

Native verification establishes **goal reachability for that input sequence**, not
offline/native state parity or global optimality. A verified result is then rebuilt
to obtain actual recording probes, and its subsequent movie playback checks those
native observations. Rejected candidates preserve the original recording. If a
job repeatedly has no candidate or fails native verification, it may be outside
the initial model's coverage rather than an impossible route.

The optional game-code backend executes the installed game's retargeted movement
code with its frame/PU clock and Box2D world. It reconstructs and compares the
native input prefix on every worker, then searches in worker-owned worlds outside
the game. Chain and beam searches across a tested refill interaction have passed
native verification. Test30 zipper rides now pass full state comparison, branch
restores, two-worker checks and native chain/beam candidate verification. The
tested `zipMover` type is enabled. Spring contacts now require spring-state
witnesses, including cooldown; the supplied spring fixture is documented in
[GLITCH-VALIDATION.md](GLITCH-VALIDATION.md). Generic movers, purchases, teleporters
and unrepresented geometry remain guarded. Already-active contacts are checked
when search starts. Every new search export includes mover and spring state.
It is still experimental and does not certify all game
behaviour or glitches. An invalid registration or rejected prefix reports an
error rather than silently substituting the approximate model.

## Validation

Workspace tests exercise input chains, parallel results, cancellation, independent
branch restores, all nine frame/PU schedules, unknown contacts, polygon outlines,
camera bindings, real worker processes and staged suffix replacement. Session tests
use a fake Unity boundary and do not certify game physics.

A temporary probe also ran the actual installed game at 50 FPS: it recorded a
short Level 1 route, used its final player position as the goal, reconstructed frame
0, exported 5,795 shapes, searched externally on two workers, verified the candidate
in-game, accepted/saved the replacement and replayed the resulting 16-frame movie
with matching native probes. It checked camera restoration and unchanged player,
velocity, movie/frame and RNG during camera panning, plus unchanged saved-progress
hashes after the run. This is one short controlled case, not all-mechanic parity.
The probe is not included in the release.
