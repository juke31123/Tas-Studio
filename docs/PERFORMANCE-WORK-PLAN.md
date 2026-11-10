# Simulator and performance work

Preserve movie-frame inputs, native 50 Hz physics, exact witness comparisons,
worker-owned worlds and mandatory native candidate verification. Improvements
must not replace game behaviour with expected observations or skip non-PU frames.

1. Measure offline replay/snapshot costs and paused native player/world checks.
2. Reduce redundant desktop sampling and repeated world-check metadata work.
3. Reduce scheduler allocation while preserving callback ordering, cancellation,
   timer deadlines and snapshot continuation.
4. Improve Beam refinements and add selectable experimental work stealing with
   fixed worker-owned worlds. Compare cache locality and throughput with affinity.
5. Exercise current recordings, native candidate playback, multiple FPS/worker
   counts, cancellation and a fresh portable installation. Record remaining gaps.

The passive initial-contact residual remains a strict mismatch until a measured
physics correction is available; a tolerance or injected observation is not a fix.

Steps 1-5 are implemented in 0.14.0. See [validation evidence](VALIDATION.md).
The audit found and corrected two additional simulator issues: physical bodies
must load independently of interpolated render poses, and Invoke deadlines
must not have an early-fire epsilon. All nine FPS fixtures now match exactly.

The experimental scheduling option is selectable in the Bruteforce window and
defaults off. Worker scaling and unseeded tests show why more threads are not
automatically preferable. Independent worlds, native verification and explicit
Save edits remain required. Existing unsupported-mechanic guards stay enabled.

Further coverage needs native differential fixtures for generic moving doors,
teleporters, clone/economy effects and additional glitch setups. Initial native
contact caches are still incomplete in passive captures. Those boundaries are
recorded in [GLITCH-VALIDATION.md](GLITCH-VALIDATION.md), without accepting a
tolerance or writing later observations into the simulator.
