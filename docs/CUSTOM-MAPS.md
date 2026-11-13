# Custom maps and VMAN (0.18.0)

Open **Mods > Tas Studio > Create New Tas**. The start-position list includes
installed Navigator maps as **Custom: <map name>**, after the built-in levels.
The list refreshes each time you open Create New Tas and once per second while
that screen is open. Updates wait until the dropdown closes and preserve your
selected map by its folder ID when other maps are installed or removed.
Navigator must be enabled;
Studio supports both Recharge's built-in Maps module and the older Navigator mod.

Select a map, abilities, recording FPS and run mode, then **Start Tas**. The map
uses its main spawn. Studio calls Navigator's map builder, waits for its objects
and tile colliders, and performs the final native spawn placement before TAS
warmup. The selected abilities override the map's ability defaults. Currency and
progress start fresh; this does not continue the player's existing map save.
Navigator's spawn-switcher keys are suppressed while the TAS owns the game.

The movie stores the map folder ID, name and SHA256 fingerprint, as well as the
VMAN settings. Playback, rebuilds and search verification recreate the same map.
Install the same map on another machine. Missing or changed map documents/assets
are rejected before playback. Hub metadata does not affect the fingerprint.
Map files are read in place; they are not copied into the TAS or release ZIP.
Both the normal save and Navigator's map-save paths are isolated while the TAS
is active. Stopping returns to the main menu.

The in-game settings and desktop window titles show the map Navigator actually
loaded. A map change invalidates the checkpoint-map geometry; it refreshes
automatically once the TAS is initialized and paused. A capture must match the
TAS map ID and loaded revision. Each external search job also stores this map
identity, which is checked against its exported world before simulation.
Geometry comes from the loaded scene, so new maps need no hand-authored geometry
file or entry in Studio's source code. Installing a map does not switch an
already running TAS to it; select it when creating a new TAS.

## Run mode

- **Normal** uses ordinary gameplay.
- **Timed VMAN** enables VMAN and accepts a duration in whole seconds (default
  60, range 1–86,400). The native VMAN timer advances with physics updates.
- **VMAN practice** enables the game's practice mode and its count-up clock.

The selected player abilities apply in all three modes. VMAN mode, time and score
are included in the simulation capture; time/score participate in replay world
checks. The timed duration starts during game setup, so warmup physics also
consumes time. The simulator supports clock advancement and branch restoration.
A search section cannot cross timed VMAN's expiry/scene transition. VMAN shop
purchases remain guarded until their purchase semantics are validated.

## Bruteforce

Use the existing Bruteforce and checkpoint-map windows. Their coordinates come
from the loaded map and floating origin. Beam and Wave use exported live collider
geometry and native game scripts at the TAS recording's FPS. The selected map
is also rebuilt when preparing an earlier start frame or verifying a candidate.
No custom-map assembly is required by the external worker for ordinary geometry.

Navigator can add extra behaviours such as custom upgrade boxes, group switches,
area switches and course-specific checkpoints. Their unsupported contact scripts
are reported and stop affected search branches; they are not silently simulated
as ordinary empty triggers. Manual recording/replay executes Navigator's real
behaviours. A map's available mechanics determine which sections can be searched.

Rebuilt tile contacts can leave small differences between Unity's native Box2D
and the external physics backend. For custom-map search-prefix reconstruction,
body position/velocity differences up to **0.0001** world units are allowed.
Version 0.19.2 corrects custom-map contact visitation order. It also permits one
specific transient during prefix reconstruction: a solid contact clearing the
internal vertical velocity to zero instead of leaving one gravity step. This
must resolve by the next PU, with no recent jump or vertical momentum. The
almost-zero vertical body-velocity remainder at these contacts has a separate
intermediate limit of **0.001**; this does not increase the position limit.
The final search-start witness uses the normal checks, including the original
**0.0001** body limit and exact internal movement fields. No witnessed values are
written into the simulation to force agreement. Persistent differences, other
movement changes, RNG, currencies, flags and clocks still reject preparation.
Built-in prefixes retain their exact checks. The search report counts recovered
contact samples and explicitly marks these prefixes as not bit exact. Movie
hashes and in-game replay checks are unchanged. Every candidate still needs
**Verify candidate in game**, then **Accept result as draft** and **Save edits**.

The strict diagnostic trace comparer continues to report every float-bit
difference, including smaller residuals accepted during custom-map preparation.
This support is not a claim that all custom interactions or glitch thresholds
have exact external emulation.

Navigator lifecycle references:
[MapManager](https://github.com/SumDumIdiut/recharge/blob/master/next/src/Recharge.Module.Maps/MapManager.cs),
[MapSpawn](https://github.com/SumDumIdiut/recharge/blob/master/next/src/Recharge.Module.Maps/MapSpawn.cs),
[MapSaves](https://github.com/SumDumIdiut/recharge/blob/master/next/src/Recharge.Module.Maps/MapSaves.cs).
