# Tas Studio

The portable release includes the external game-code simulator. Its game code
is prepared from the player's installed game on first search, without requiring
Git, an SDK or a separate .NET installation. It supports beam and input-chain searches and
Wave search, with verified native prefixes and tested refill,
zipper and spring interactions. Recorder ZIPs have a variable-timing replay importer.
See [section-search release scope](docs/SECTION-SEARCH-RELEASE.md) and
[simulator development](docs/SIMULATOR-DEVELOPMENT.md).

TAS authoring tools for **IGTAP: an Incremental Game That's Also a Platformer**, using the Recharge mod loader.

Author: **Juke**. Current version: **0.17.1**. Mod ID: `Tas.Studio`. Entry assembly: `TasStudio.dll`.

## Features

- The in-game **Mods** entry and Tas Studio menu title show the installed version, shared with the loader and desktop labels.
- **Compact run files** save input changes with explicit start frames/durations, checked against the total frame count. Related state values are grouped, movement/world changes are stored and the starting-world witness is compressed. Inputs, per-frame movement hashes, timing and replay checks remain intact. See [file storage](docs/FILE-STORAGE.md).
- **Checkpoint map** in Bruteforce: place and drag numbered circular goals, middle-drag to pan, edit coordinates/radii and reorder points. Beam, Wave and chain searches retain ordered checkpoint progress in every branch and offer whole-route or per-checkpoint time budgets with optional lookahead. Native playback must visit every point in order and finish at the last before acceptance.
- Local **CashPerLoop** single purchases are supported by the game-code search, with native currency, price, cooldown and local-upgrade witnesses. Other upgrades, buy-max and Vman purchases remain guarded until validated.
- Dedicated **Bruteforce** window: choose a start frame, goal point/radius and allowed abilities, then use **Beam search** or **Wave search**. An optional **Exhaustive input chain** mode times a prescribed action sequence. Progress includes archive cells, splices, plateau changes and cached-state replays. Optional frame targets allow a configurable overrun (default 1000 frames). Goal placement happens in the checkpoint map. **Native verification required.** See [coverage and controls](docs/BRUTEFORCE.md).
- **Beam search** keeps diverse reachable states, explores mixed input holds including single frames, and improves routes with cuts, retiming and splices. Dedicated workers reuse their own native snapshots; progress reports cache and refinement work without re-simulating the preview.
- Optional **Experimental work stealing (Beam only)** lets idle workers take queued branches while preserving private worlds and snapshots. Affinity remains the default. More threads can increase reconstruction costs; compare on the current section.
- Desktop stats sample at the display's 10 Hz cadence, timeline pages use direct indexing, world checks reuse per-capture paths, saves stream JSON, and repeated frame steps coalesce automatic profiler writes. Replay witnesses and explicit Save edits remain intact.
- Multi-level **Undo/Redo**, durable draft recovery, and verified live-checkpoint reuse. Edits still apply only when you click **Save edits**.
- **Checks** tab: export detailed performance profiles or verify a recorded movie with continuous and frame-stepped playback. Periodic world checks cover more than player movement, and atom-door progression stays local to the TAS. See [replay reliability](docs/REPLAY-RELIABILITY.md).
- **Step PU / F11** advances to the next frame with a physics update and pauses. Available in both desktop windows with a configurable keybind. Red **~** rows retain previous PU timing after edits until re-execution updates them.
- Record/replay the game's Pause action or edit it in the **Menu** frame column. Game-menu pause freezes physics while TAS frames continue; TAS pause/step remains separate. Requires new format-7 movies.
- Change TAS shortcuts from **Mods > Tas Studio > Keybinds**. Bindings save automatically, support unbinding and restoring defaults, and appear on the desktop buttons. The F5-F11 shortcuts below are defaults.
- Choose the game's recording FPS while physics stays at **50 PUs per game second**. Replay locks the recorded FPS. Frames between PUs retain frame-based inputs; PU rows are red and Stats shows the countdown to the next PU. See [frame and physics timing](docs/FRAME-TIMING.md).
- Separate native **Frame Editor** with clickable/paintable input columns, in-cell analog editing, row insertion/deletion/clearing and undo. Edits remain pending until Save edits; saving past-frame changes rebuilds state while preserving future inputs. See [Frame Editor controls](docs/FRAME-EDITOR.md).
- **Mods > Tas Studio** opens the in-game settings page for creating, loading and replaying runs. Recording/replay opens Studio and Frame Editor after loading, paused at frame zero. F5 reopens them. No floating in-game panels remain. See [Desktop Studio](docs/DESKTOP-STUDIO.md).
- Create a TAS from a selected level and ability setup.
- Record movement inputs and replay them through the game's simulation.
- Pause, advance a frame, and select recording/playback speed from 0.1x to 1x.
- Edit recorded inputs and detect movement desynchronization.
- Save named replay checkpoints while recording is paused. Loading reconstructs the saved frame by replaying, then resumes paused recording; these are not instant memory snapshots.
- Inspect live player position, velocity, speed and movement state in the **F10 Player stats** window, with a next-frame estimate for supported movement. Predictions assume unchanged contacts; collisions, triggers and special transitions are not simulated. See [prediction coverage](docs/PLAYER-STATS.md).

See [installation and controls](INSTALL.md), [checkpoint details](docs/SAVESTATES.md), and [implementation limits](docs/RESEARCH.md).

## Build from source

This folder is a standalone source repository. It does not require the surrounding Tas Tools workspace or the example mod.

Requirements:

- Windows PowerShell and a .NET SDK with .NET Standard 2.1 targeting support (tested with .NET 10). Players do not need a separate Desktop Runtime; the native window runs in Unity's existing runtime.
- A local IGTAP installation patched with Recharge. Its `IGTAPfullGame_Data/Managed` directory supplies the game, Unity and Recharge references; those assemblies are not included in this repository.
- Harmony 2.3.3. The build script downloads and checks the pinned package on first use.

From this folder, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -ManagedDir "C:\Program Files (x86)\Steam\steamapps\common\IGTAP an Incremental Game That's Also a Platformer\IGTAPfullGame_Data\Managed"
```

Use `-Offline` after downloading Harmony. To use an existing dependency without downloading it, supply both `-HarmonyPath` (the net472 fat-build DLL) and `-HarmonyLicensePath` (its LICENSE file).

For the portable player release, also pass `-PortableBruteforce -UpstreamRoot
"C:\path\to\IGTAPTasMod"` with the reference checkout pinned as described in
[simulator development](docs/SIMULATOR-DEVELOPMENT.md). This builds both tools
with bundled .NET runtimes and excludes the generated game assembly. The default
build without that switch is the smaller development package; its static solver
still needs .NET 10 or a separately registered host.

The build produces `artifacts/Tas.Studio/` and `artifacts/Tas-Studio-0.17.1.zip`. Follow `INSTALL.md` to install the package. Build outputs and downloaded dependencies are ignored by Git.

## Source layout

- `Core/`: movie format, input editing, storage and timers.
- `Runtime/`: game integration, recording/replay sessions and simulation control.
- `UI/`: native Windows editors, the Recharge settings panel and desktop lifecycle host.
- `DesktopShared/`: detached snapshots and a bounded in-process command queue between the native UI and game threads.
- `TasMod.cs`: mod lifecycle.
- `mod.json`: loader metadata.

## GitHub updates

Initialize Git in this folder. Commit source changes, then push them to your repository. Attach installation ZIPs to GitHub Releases. The source repository excludes compiled outputs, local dependencies, runtime data and the rest of the Tas Tools workspace.
