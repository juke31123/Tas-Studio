# Performance changes in 0.19.6

This update reduces periodic verification and desktop overhead without reducing
the observed state, changing digest bytes, lowering check frequency or changing
TAS FPS/50 Hz physics. Geometry and movement algorithms remain unchanged.

## Measured world verification

A preserved copy of the previous implementation runs inside the controlled
native test mod, against the same paused scene/state as the optimized version.
Twelve warm observations per path were measured without forced garbage collection.
The old periodic path required full capture followed by compaction; the new
path builds the same compact witness directly.

| Path | Mean |
| --- | ---: |
| Previous full capture | 19.4074 ms |
| Previous compaction | 17.1172 ms |
| Previous periodic total | 36.5246 ms |
| New direct periodic capture | 20.9457 ms |

This fixture shows about a **43% reduction** in periodic world-check cost. Other
runs measured roughly 38 ms versus 22 ms. This is not a sustained game-FPS
benchmark or a guarantee that every map improves by that amount.

Both full captures retained exactly 2,998 exact fields and 9,246 numeric fields;
compact captures retained 775 exact entries and 264 numbers, including the same
bulk digests. Full and compact hashes match the reference. Native mutation checks
cover creation, renaming, scaling, enable/disable, reparenting, adding/removing
components and inactive objects. Core checks include Unicode, several locales,
duplicate writes, signed zero, changed values, removed history/object keys,
cache reset/abandonment and non-finite values.

Evidence: `Engine.Host/artifacts/native/20261113-014830-735/fps-280/runtime-performance.json`.

## What changed

- Scene enumeration resolves relevant component providers in one fresh inventory.
  Hierarchy/name/sibling/component order is re-read each time. Immutable key
  strings are reused only while that identity matches, and stale entries are
  removed. Caches clear on scene changes and simulation release.
- Periodic captures avoid building and discarding a full sorted geometry tree.
  Every field is supplied again. Unchanged bulk values reuse their canonical
  UTF-8 encoding and sorted layout; all bytes are hashed again with SHA-256.
  Changes, removals and new keys invalidate the appropriate encoding/layout.
- Full savestates/checkpoints keep their original fields and floating-point
  checks. A non-finite bulk value cannot disappear inside a digest unchecked.
- Editor snapshots reuse unchanged immutable frame pages and copy only changed
  rows. Published older snapshots cannot be changed by a later PU observation.
  The offscreen PU/pending-timing regression is covered by desktop tests.
- Native controls cache read-only label text and enabled states. Editable text
  still reads the actual control contents, preserving unsaved user input.
- Export discovers each GameObject's components once and caches reflection
  schemas, assembly names and vector representation metadata. Trajectory export
  hands detached primitive/JToken data to its transport worker for disk writes
  and hashes; the worker never reads live Unity objects.

## Initial capture and remaining limits

The controlled 300-frame live trajectory run passed native trace, restore and
parallel-worker checks. After background serialization, its paused trajectory
capture took **2,408 ms**; an earlier 0.19.5 fixture measured 3,166 ms. The runs
are not a controlled before/after FPS comparison. Native geometry inspection is
still the dominant paused cost, and serialization still consumes background CPU.

Profiler sections now separate CaptureAnimation, CaptureDiscovery,
CaptureGeometry, CaptureMetadata and CaptureWrite. Geometry can be nested in
metadata capture, so inclusive totals must not be added together. Background
trajectory writes are not charged to the main-thread CaptureWrite section.

Private-hierarchy reuse and a tile-data memoization experiment were measured and
discarded: they did not improve this workload. All 857 compared native geometry
results matched the reference, but correctness alone did not justify keeping
additional complexity. The existing collider-only isolated baker remains in use.

The saved native recording was loaded and audited in continuous and single-frame
playback after the changes. All 403 native diagnostic samples matched. Evidence:
`Engine.Host/artifacts/native/20261113-015346-596/`.

Recorded inputs, replay checks, savestates, UI commands and search verification
remain in place. Existing unvalidated-mechanic and post-death simulator limits
still apply; this update does not claim complete simulator parity. Test drivers
restore the installed DLL and verify preservation of all existing save files.
