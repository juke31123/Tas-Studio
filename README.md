# Tas Studio

TAS authoring tools for **IGTAP: an Incremental Game That's Also a Platformer**, using the Recharge mod loader.

Author: **Juke**. Current version: **0.3.3**. Mod ID: `Tas.Studio`. Entry assembly: `TasStudio.dll`.

## Features

- Create a TAS from a selected level and ability setup.
- Record movement inputs and replay them through the game's simulation.
- Pause, advance a frame, and select recording/playback speed from 0.1x to 1x.
- Edit recorded inputs and detect movement desynchronization.
- Save named replay checkpoints while recording is paused. Loading reconstructs the saved frame by replaying, then resumes paused recording; these are not instant memory snapshots.

See [installation and controls](INSTALL.md), [checkpoint details](docs/SAVESTATES.md), and [implementation limits](docs/RESEARCH.md).

## Build from source

This folder is a standalone source repository. It does not require the surrounding Tas Tools workspace or the example mod.

Requirements:

- Windows PowerShell and a .NET SDK with .NET Standard 2.1 targeting support.
- A local IGTAP installation patched with Recharge. Its `IGTAPfullGame_Data/Managed` directory supplies the game, Unity and Recharge references; those assemblies are not included in this repository.
- Harmony 2.3.3. The build script downloads and checks the pinned package on first use.

From this folder, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -ManagedDir "C:\Program Files (x86)\Steam\steamapps\common\IGTAP an Incremental Game That's Also a Platformer\IGTAPfullGame_Data\Managed"
```

Use `-Offline` after downloading Harmony. To use an existing dependency without downloading it, supply both `-HarmonyPath` (the net472 fat-build DLL) and `-HarmonyLicensePath` (its LICENSE file).

The build produces `artifacts/Tas.Studio/` and `artifacts/Tas-Studio-0.3.3-clean.zip`. Follow `INSTALL.md` to install the package. Build outputs and downloaded dependencies are ignored by Git.

## Source layout

- `Core/`: movie format, input editing, storage and timers.
- `Runtime/`: game integration, recording/replay sessions and simulation control.
- `UI/`: TAS windows and Recharge menu integration.
- `TasMod.cs`: mod lifecycle.
- `mod.json`: loader metadata.

## GitHub updates

Initialize Git in this folder. Commit source changes, then push them to your repository. Attach installation ZIPs to GitHub Releases. The source repository excludes compiled outputs, local dependencies, runtime data and the rest of the Tas Tools workspace.
