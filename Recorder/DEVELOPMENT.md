# Passive gameplay recorder

Version 1.1.0 replaces the configured TAS recorder with a native-gameplay observer.
Its build excludes TasSession, ClockLease, GamePatches, ReplayAudit, desktop windows
and external solvers. It has no frame-step, PU-step, pause/resume or replay transport.

`PassiveHooks` installs void observation prefixes on Movement.Update/FixedUpdate
and markers around the native physics step and end of frame. Game methods still
execute normally. `LiveWorldCapture` reads current state without scene loading,
physics simulation, collider enabling, clock writes or RNG seeding. The snapshot
checks that its reads preserve player body state, RNG and pacing settings.

`GameplayJournal` compresses complete frame packets on a background worker with a
bounded queue. Native frame deltas and multiple/zero PUs are retained; there is no
synthetic recording FPS. `PassiveBundle` hashes and packages an explicit file list.
Schema 2 data is intentionally distinct from fixed-FPS TAS movies/traces.

Build from the repository root:

```powershell
.\Recorder\build.ps1 -ManagedDir '<Game>\IGTAPfullGame_Data\Managed' -HarmonyPath '<Harmony 2.3.3>\lib\net472\0Harmony.dll'
```

Pure tests cover frame/PU ordering, variable deltas, bounded capture, background
flush, segmented frame numbering, archive contents and payload hashes.
`Test-Native.ps1` opens normal saved gameplay, captures keyboard inputs, changes
the game's target FPS during capture, checks F7/F11 do not step/freeze anything,
and confirms no recorder patches block native saves. The test harness backs up
and restores the specific save folders and existing game preferences; the mod
itself leaves normal saves enabled. Probes and test data are not distributed.

The 1.1.0 native check captured 101 ordinary frames and 102 native PUs, followed
a target-FPS change to 60, retained the original capture/fixed-step/time-scale
settings, and passed save-method/transport-runtime checks. Thirteen additional
pure passive-journal/archive checks pass with the existing workspace suite.
