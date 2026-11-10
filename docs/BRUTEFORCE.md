# Bruteforce window (0.17.0)

## Numbered checkpoints

Open **Checkpoint map...** in the Bruteforce window while paused. **Refresh map**
captures the currently loaded collider outlines; this is a frozen planning map,
not a prediction of future moving objects. Blue outlines are solids, red are
hazards and amber marks other interactions. The player is the cross.

Click empty map space to append a checkpoint; the list number is the required
visit order. Drag an existing numbered point to move it. Release inside the map
to apply the change; Escape or release outside the map cancels it. Only the final
drop is sent to the game thread. Middle-drag pans (right-drag also works), and
the wheel zooms. **Fit goals** fits the player and route;
**Center player** centers the last captured player position. Select a list entry,
edit X/Y/radius and click **Apply point**. **Move up/down** changes its number;
**Delete point** removes it. Radii accept decimal points or commas, from 1 to
1000 world units. Up to 64 points are supported.

**Add current player** captures the actual paused position. All checkpoint editing
is in this map window; free-camera and in-game placement controls have been removed.
Placement does not move the player or record input. The final checkpoint is the
finish. **Clear route** removes the points; place a new finish before starting
another search.

All three search modes carry the next required checkpoint in each trajectory,
branch snapshot and archive state. Touching point 3 before point 2 does not count;
it must be visited again after point 2. Overlapping consecutive circles may count
on the same movie-frame boundary. By default, a search optimizes the whole route
in one time budget. Optional per-checkpoint budgets commit one arrival at a time,
retaining its velocity, input history, resources and full simulated world state.
Progress reports the furthest ordered checkpoint count.
This remains a budgeted search for the best candidate found, not a proof of the
globally fastest route. Native verification checks each movie-frame endpoint,
requires every checkpoint in order and a living player inside the final circle.

## Frame targets, checkpoint budgets and lookahead

- **Frame target**, beside **Maximum frames**, treats that number as a target and
  enables **Extra frames allowed** (default **1000**). The hard ceiling is target
  plus allowance, across the complete section. Shorter valid routes remain better;
  reaching the target is not a requirement. With the toggle off, the original
  maximum is the hard limit. The combined ceiling is at most 20,000 frames.
- **Per checkpoint**, beside search seconds, gives each remaining checkpoint that
  many seconds. Four pending checkpoints at 60 seconds receive up to 240 seconds
  of search time, plus setup and selected-route reconstruction. At each deadline,
  the best arrival is chosen and the next stage starts with a fresh timer. If a
  stage finds no arrival, the search ends without an acceptable partial candidate.
  Cancellation leaves the source movie unchanged. Already-reached/overlapping
  checkpoints need no extra stage. Finite input-chain enumeration can finish early
  once every combination has been tried; the chain is applied anew at each stage.
- **Lookahead frames** is available only with per-checkpoint time enabled. Set X
  to test up to X more frames after each arrival toward the next circle. Workers
  compare holding current controls, steering toward the next point, and coasting.
  The score combines arrival frames, preview frames and remaining distance
  converted into an estimated travel time. This can prefer a slightly later
  arrival with better momentum. Preview frames count as search work, respect
  ability restrictions and the remaining hard frame ceiling, and are restored
  afterward: they do not become part of the selected segment. The last checkpoint
  uses arrival time alone. This is a heuristic estimate, not a guarantee of the
  best possible continuation. It costs extra simulation work within each budget.

These controls work with Beam, Wave and exhaustive input-chain search. Progress
includes the current checkpoint, its timer, committed frames and lookahead work.
Whole-route search remains available by leaving **Per checkpoint** off.

## CashPerLoop purchases

Local CashPerLoop single purchases are enabled in normal play with new native
purchase witnesses. Currency, local upgrade levels, box count/cost/cooldown and
active/cap state participate in validation. The game-code backend keeps its
economy model and original local-upgrade game objects synchronized after buying
and restoring a branch. Buy-max, Vman purchases, global CashPerLoop and other
upgrade types still stop unsupported rollouts. A blocked rollout is not an actual
failed in-game purchase; its reason appears in the search report. Older captures
without purchase witnesses need recapturing through the updated mod.

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

The solver selector offers **Beam search**, **Exhaustive input chain**,
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

The exhaustive chain mode remains useful for timing a specific input sequence.
Beam mode remains available for controlled comparisons and independent
development. Selecting the archive option never silently falls back to beam.

1. Pause the recording or replay. Open **Checkpoint map...** and place the finish,
   optionally preceded by intermediate checkpoints. The map can also add the
   current player position. Edit each radius in the map; decimal points and commas
   are supported.
2. Enter **Start frame**, an already executed frame at or before the TAS cursor.
   **Use TAS cursor** copies the current frame. Set the maximum search frames, time
   budget, workers and allowed abilities.
3. Optionally enable a frame target/allowance, per-checkpoint time and lookahead.
   Scroll the window when its lower controls do not fit the display.
4. Click **Start search**. If necessary, the mod reconstructs the selected start
   using the original movie prefix. The game then stays paused while the external
   worker searches. Existing future inputs remain available as a seed and backup.
5. A candidate is **unverified**. Click **Verify candidate in game** to replay the
   original prefix and candidate at the movie FPS. The native player must reach
   the goal alive; observed resource consumption also rejects disabled air-jump,
   wall-jump or dash use. The temporary verification movie cannot overwrite the TAS.
6. **Accept result as draft** stages the replacement from Start frame onward.
   **Save edits** in the Frame Editor applies it, removes the old suffix, rebuilds
   the changed recording and saves it. Undo/Redo and draft recovery remain available.
   **Cancel / discard preview** restores the original inputs and reconstructs their
   state when necessary. Original movies are backed up before reconstruction.

The ability boxes restrict use; they do not grant upgrades or change the recording's
start setup. Dash count and extra-jump count come from the actual selected start.
The checkpoint map never moves the game camera or player. Preparation and native
verification own the TAS transport until they finish or are cancelled.

## Search modes

**Heuristic beam search** keeps a bounded archive of reachable states and expands
small groups with different priorities: goal progress, less explored cells,
velocity and early arrival. Cells retain several contact/resource/momentum and
timing variants; finer cells near the goal protect small goal radii. These labels
are heuristic diversity, never an exact-state equivalence or convergence claim.

Input holds mix single frames, short powers of two and longer bursts. **Input hold
hint** adds a useful duration; it does not constrain every edit to that spacing.
The final movie still contains ordinary frame rows, including changes between PUs.
Goals are checked after every simulated frame, not only at hold endpoints.

The existing suffix seeds the archive and a possible incumbent. Search continues
with input changes, cuts, inserts, button/run retiming and splice attempts. Every
changed join is simulated from its real preceding state; reaching a nearby map
cell does not establish matching hidden state. No unchanged old suffix is required.

Each dedicated worker owns its simulator and snapshot tokens. Native parent
snapshots are reused across siblings, with a 64 MiB allocation-based target for
retained snapshots per worker, excluding the mandatory root, active temporary
tokens, worlds and runtime. Cache misses replay immutable input histories.
Progress includes replay frames and estimated retained cache bytes. Winning
preview arrays are built when a result changes, without progress-triggered replay.

The result is best found, not exhaustive or globally optimal. A finite archive or
refinement neighbourhood may be exhausted before the time limit. Workers remain
configurable from 1 to 32; actual speed and memory depend on the captured world.

Beam tries goal-facing actions earlier while retaining the other directions. Its
refiner also tests systematic coarse-to-fine cuts and shifts input-run boundaries.
These are search-order heuristics, not new movement rules or a shortest-path proof.

**Experimental work stealing (Beam only)** is off by default. Normal affinity
assigns parent batches to their existing worlds. With stealing enabled, an idle
worker can take an unclaimed task from another queue and reconstruct that task's
immutable input history in its own world. Native snapshot tokens never cross
workers. Progress reports the scheduling mode, completed tasks and stolen tasks.
Wave already uses its upstream multithreaded solver; this checkbox affects Beam.
More workers can spend more time rebuilding histories and initializing worlds.
Start with two; compare on the section before increasing the count.

Developers can compare affinity/stealing without starting the game:

```powershell
.\Compare-Search.ps1 -JobPath 'C:\captures\job.json' -Seconds 5 -Workers 1,2,4 -Repeats 2
```

The script copies captured inputs into new artifact folders and leaves the source
job untouched. Its candidates remain unverified until replayed in the game.

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
[GLITCH-VALIDATION.md](GLITCH-VALIDATION.md). Generic movers, other purchases, teleporters
and unrepresented geometry remain guarded. Already-active contacts are checked
when search starts. Every new search export includes mover, spring and local
CashPerLoop purchase state.
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
