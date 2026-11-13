# Trajectory preview (0.19.7)

Version 0.19.7 preserves the outgoing clip's last event interval when Unity's
instantaneous animation transition enters its destination at time zero. In
Test34, frame 32117 changes running to jumping exactly across a footstep event.
Skipping that event loses two shared random draws and later produces
`random/s0` native `1413645022`, simulated `854872060`. The simulator now emits
that event before entering the jump clip; no random-state comparison is ignored
or overwritten. This correction also applies to search simulations.

The Test34 regression passes native replay of all 32,400 source frames. A fresh
snapshot at frame 30,000 matches all 3,259 full native samples through frame
32,399, including random state; restore and shuffled-worker checks pass too.
The live preview runs for those 2,400 frames with 62 requests and no witness
failure. A separate persistent-worker test compares 40 forecasts / 723 native
frames, including prefix rewinds, hypothetical branch rollback, supersession
and rejection of deliberately wrong live state. All 3,028 workspace checks pass.

For an existing 0.19.6 recording, update only the packaged `Bruteforce` directory
to retain its strict mod fingerprint. The complete 0.19.7 package is for a full
installation and new recordings; replacing the main DLL/manifest changes that
fingerprint. The backend update does not migrate or rewrite TAS files.

In the desktop controls, open **Preview** while paused in an initialized TAS.
Enable **Show in-game trajectory**. Set **Preview length** to 1-2000 TAS frames
(default 280), then **Apply preview settings**. At 280 FPS, 280 frames is one
second of game frames; at 50 FPS it is 5.6 seconds. Viewing speed does not change
the predicted simulation interval. The preview is off by default.

Checkbox clicks remain visible while the game processes them; older snapshots
cannot undo a pending click. Unapplied length edits are also retained. If the
simulator reports a failure, the checkbox stays enabled and the path hides with
an explanation. Pause and click **Refresh snapshot (paused)** to retry. A failed
forecast no longer causes repeated automatic world captures. Scene, player and
TAS changes still invalidate the old capture normally.

The in-game display uses a cyan path, a white next-frame cross and a yellow
endpoint. Red means the simulation stopped at death; orange means an unsupported
interaction. The death marker includes the native fatal frame's position;
unsupported interactions stop at the preceding trusted point. These markers
do not identify an exact collision surface. A frame
without a PU can have the same position as the current frame: that is expected.

The controls window's next-frame position, velocity, state and resources come
from the same simulation while this feature is enabled. With the feature off,
the existing lightweight free-movement estimate remains available. Outside a
TAS, only that lightweight estimate is supported.

## Inputs and timing

The first predicted frame uses the next selected input at the current cursor:
the next playback row, the current frame-list row in timeline-input mode, the
authored input, or the current physical controls. All following frames hold its
axes and buttons. Explicit taps/retaps are applied once; they are not repeated
on every future frame. A physical press already consumed by the current frame
is not replayed as another press. Future changes in the movie are not followed.

The private simulator uses the recording's FPS, the current PU phase and the
vanilla 50 Hz physics schedule. It runs the same game-code backend as Bruteforce,
including supported collisions, jumps, wall movement, dash/hitstop transitions,
refills, zippers and springs. No live player is advanced or restored to produce
a forecast. Hypothetical movement is rolled back only inside the private world.

Reset uses the original game's rendered-frame action, including respawn point,
course-reset point and checkpoint settings; it is no longer rejected by the
search action allowlist. Search itself keeps its existing ability restrictions.
The preview's first Reset press is applied once, rather than retapped on every
future frame. Actual prefix reconstruction can include dead/respawn frames;
each hypothetical path ends on its first fatal frame.

Spike grace follows the captured script's `graceFrames`, `counter`, dash-kill
flag and actual Enter/Stay callbacks. It is not a hardcoded rendered-frame delay.
With the default grace of 1, the owned native fixture survives Enter and the
first Stay, then dies on the second Stay. A dash can kill on the first Stay.
At 280 FPS these span multiple rendered frames between PUs. Capture reads
initial trigger pairs using `Collider2D.IsTouching`, whose
[last-physics-update semantics](https://docs.unity.com/en-us/engine/6000.0/script-reference/unityengine/rigidbody2d/istouching)
distinguish a new overlap from an existing contact.
`GetContacts` cannot supply these pairs because
[Unity excludes trigger contacts](https://docs.unity.com/en-us/engine/6000.0/script-reference/unityengine/physics2d/getcontacts).

## Capture and accuracy

Pause once to capture the live world. One persistent background process loads
that geometry and reuses simulated input history and private snapshots. It
accepts only the latest pending request. Changed live controls request at up to
30 Hz; paused edits and frame/PU steps bypass that throttle. Matching forecasts
are reused until their remaining horizon needs renewal. Newer requests cancel
obsolete forecasts between batches. No simulator step runs on Unity's main thread. The initial
native world inspection still runs on the main thread and can briefly stall a
large map. Once detached, snapshot serialization and file hashing run on the
transport worker; no live Unity object is read from that worker.
The complete package includes the host and runtime; no SDK installation is
required. Bruteforce suspends the preview so their capture work does not overlap.

The strongest starting point is enabling at frame 0. Enabling later captures
the current state, player interpolation endpoints and exact pending player
Invoke deadlines, including their originating phase. Such a snapshot is marked
as an **estimate**: native contact caches, pre-existing unsupported coroutines
and unknown mod behaviours cannot all be exported. A matching starting witness
does not prove every future interaction is exact.

New requests reconstruct the actual recorded prefix and compare the current
native witness before forecasting. Mismatched history hides the path; pausing
and using **Refresh snapshot** creates a fresh snapshot. Input, map, player and scene changes, rewinds, detected
position/velocity drift and exhausted forecast horizons invalidate old paths.
Long histories are bounded to 20,000 frames per capture; pause again to recapture
after that limit. While running, a matching cached forecast follows the player;
it disappears immediately when its current controls or position/velocity no
longer match. Game menus and active hitstop suspend the overlay.

The existing custom-map prefix body tolerance remains 0.0001 world units; other
witnessed fields stay strict. Unsupported mechanics stop forecasts just as they
stop unsupported search branches. The feature is a visual authoring aid, not
native verification or a guarantee that every collision/glitch is exact.

Turning the feature off releases its worker and temporary capture. Closing the
game also ends the worker. The movie format and saved game progress are unchanged.

## Performance diagnostics

The profiler separately measures **Trajectory**, **TrajectoryCapture** and
**TrajectoryDraw**, including operations while paused. Capture is included in
Trajectory's total, so do not add both totals together. The renderer retains its
vertex buffer as the remaining path shrinks and skips uploading an unchanged
paused path. During continuous play, the long line is trimmed/uploaded at up to
30 Hz (a short already-traversed tail can remain between uploads). The white
next-frame cross updates every game frame; paused steps redraw immediately.
Duplicate body positions on non-PU frames need no duplicate drawing vertices.
Oversized buffers are supported by
[Unity's SetPositions API](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/LineRenderer.SetPositions.html).
Physics and replay validation checks remain intact.

Only native snapshot reads, witness sampling and drawing remain on the game
thread. Input history uses immutable chunks; the transport thread streams and
validates requests. Binary messages encode repeated input spans and typed motion
values, avoiding JSON property graphs for every forecast point. The private
worker retains a root plus at most seven history checkpoints and one committed
cursor. Forecast steps allocate no search-history nodes or search branch cache.
Scene-wide mover discovery is cached rather than repeated every PU.

See [measured validation and limits](PERFORMANCE-20261113.md). Long horizons can
still delay a forecast; the default 280-frame horizon is a useful starting point
for live recording. The initial world inspection remains a paused operation.
