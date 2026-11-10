# Beam search improvements

Keep the existing section controls: start frame, point/radius goal, abilities,
time/frame limits and workers. Every candidate remains normal movie-frame input
at the movie's FPS with vanilla 50 Hz physics. Native verification and Save
edits remain mandatory for accepting a result.

Work in order:

1. Dedicated worker threads, worker-owned branch snapshots, memory-budgeted
   native caches and cached winning previews. Count reconstruction work.
2. Bounded archive of diverse reachable states, preserving contact/resource,
   momentum and frame/PU phase distinctions as heuristic diversity labels.
   Archive labels are not exact-state equivalence or convergence proofs.
3. Mixed input hold lengths including one frame. Check goals on every frame.
4. Continue improving candidates through cuts, input changes, retiming and
   splices, replaying every changed join through the same simulator.
5. Regression tests for detours, single-frame windows, exact input edges,
   cancellation, snapshot ownership and refinement; compare with the existing
   seeded/unseeded native fixture and validate candidates in the real game.

Baseline on the existing 397-frame horizon, 280 FPS, two workers and a five-second
budget: Beam keeps a 394-frame seed and expands only ten new frames. Wave finds
a 327-frame candidate. Neither solves the unseeded case in that short run.
This is a single offline comparison, not a general performance claim.

The five implementation steps above are complete in 0.13.0. Beam keeps its own
implementation; the archive/explore/refine approach draws on the same design
ideas as Wave, whose upstream attribution remains in [BRUTEFORCE.md](BRUTEFORCE.md).
The suite adds 29 Beam checks, including slow motion within an archive bucket.
On a repeated five-second run, Beam reduced the seeded route from 394 to 327
frames and explored to depth 228 instead of 10 without a seed. The unseeded
goal was not solved within that budget. These are offline candidates and a
single fixture, not a general ranking of the solvers.
Native cache checks, candidate playback and portable installation are covered in
[VALIDATION.md](VALIDATION.md). Native verification remains mandatory before a
candidate can be accepted. Wave remains a separate selectable solver.
