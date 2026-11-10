# Section bruteforce release scope

The release target is a short-to-medium section optimizer, not a whole-run
planner. The author supplies the start frame, a world-space point and goal
radius (or an ordered list of circular checkpoints), and allowed movement abilities. The job uses the current movie's FPS
and vanilla 50 Hz physics. Time, horizon and worker limits bound the search.

The author records the lead-in, searches from its end, accepts a result, and
continues recording from the result's endpoint. Accepting replaces the old
suffix through the normal draft / Save edits workflow. Numbered checkpoints
constrain one jointly optimized section, with the last point as its finish.
Testing an old suffix, whole-run shopping/route planning, avoid regions and
discovering new glitches are not release requirements.

The optional input-chain mode describes a prescribed sequence such as holding
right, jumping, then dashing with variable durations. It remains available for
specialized timing experiments; normal bruteforce does not require a chain.
Beam and Wave search generate their own inputs from the allowed abilities.

## Release checks

1. A clean installation discovers and prepares the accurate external host from
   the user's installed game. No development-machine paths, Git checkout, SDK,
   manually compiled host or separately installed .NET runtime are required by
   the portable package. Generated game assemblies stay on that user's PC.
2. Start frame, point/radius goal and ability limits work through search,
   cancellation, native candidate playback, draft acceptance and Save edits.
   The recording can continue from the accepted endpoint.
3. Native prefix comparison and worker-owned branches reproduce the supported
   mechanics at the movie's FPS. Unsupported interactions report their cause;
   a portable accurate-host failure must not silently select the approximate
   development solver.
4. Native candidate playback checks the searched section, ordered checkpoints, alive goal arrival
   and ability restrictions. It does not replay a discarded old suffix.
5. Ordinary movement plus validated zipper, refill, spring and local CashPerLoop single-purchase interactions form
   the initial coverage. Further mechanics need focused native fixtures. Use
   existing recorder data first; ask the author for the specific missing
   interaction or context when it is needed.
6. Measure end-to-end search costs and worker scaling on representative short
   and medium sections. Report best found within the budget; unrestricted
   per-frame brute force is not claimed to be exhaustively optimal.

Known-glitch support may grow as the relevant native mechanics are validated.
An automatic technique library, new-glitch discovery, search resumption and
whole-run planning are separate later improvements.
