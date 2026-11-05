# Tas Studio

TAS authoring tools for **IGTAP: an Incremental Game That's Also a Platformer**, using the Recharge mod loader.

Author: **Juke**. Current version: **0.9.2**. Mod ID: `Tas.Studio`. Entry assembly: `TasStudio.dll`.

## Features

- Change TAS shortcuts from **Mods > Tas Studio > Keybinds**. Bindings save automatically, support unbinding and restoring defaults, and appear on the desktop buttons. The F5-F10 shortcuts below are defaults.
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

The build produces `artifacts/Tas.Studio/` and `artifacts/Tas-Studio-0.9.2-clean.zip`. Follow `INSTALL.md` to install the package. Build outputs and downloaded dependencies are ignored by Git.

## Source layout

- `Core/`: movie format, input editing, storage and timers.
- `Runtime/`: game integration, recording/replay sessions and simulation control.
- `UI/`: native Windows editors, the Recharge settings panel and desktop lifecycle host.
- `DesktopShared/`: detached snapshots and a bounded in-process command queue between the native UI and game threads.
- `TasMod.cs`: mod lifecycle.
- `mod.json`: loader metadata.

## GitHub updates

Initialize Git in this folder. Commit source changes, then push them to your repository. Attach installation ZIPs to GitHub Releases. The source repository excludes compiled outputs, local dependencies, runtime data and the rest of the Tas Tools workspace.
