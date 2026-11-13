# Player stats and prediction (0.4.0)

Current UI (0.8.0): these controls live in the desktop Studio, opened automatically after starting a run from Mods > Tas Studio. F5 reopens Studio and Frame Editor; F9 selects States and F10 shows player stats. The former floating in-game windows have been removed. Stats also shows **Frames until next PU**: 1 means the next game frame contains a physics update. The countdown uses the movie FPS and current game time scale; during dash hitstop it shows unknown until the game resumes. Editor pauses preserve the countdown. See [frame timing](FRAME-TIMING.md).


Press **F10** to open the desktop player stats. It shows scene-space physics-body position, X/Y velocity in game units per second, speed, movement state and remaining air jumps/dashes. Controller velocity and momentum are shown separately because IGTAP combines those with platform motion before setting the body's velocity. Grounded, jumping/rising, falling, airborne/apex, wall sliding, dashing and dead are distinguished. Pause does not erase the player's movement state.

## Action readiness

**Can jump** and **Can dash** show current-state checks and, since 0.19.4,
readiness after the selected next frame.
Jump readiness shows `Yes (ground)`, `Yes (wall)`, `Yes (air)`, `Yes (dash)` or
`Yes (hyper)`; only `Yes (air)` spends the extra air-jump charge. Blocked actions
show a short reason such as `No (rising)`, `No (empty)`, `No (cooldown)` or
`No (wall lock)`. A failed wall-jump branch does not fall through to an air jump.
Native menu/hitstop, death and special control locks are respected; pausing the
TAS editor alone does not disable readiness.

These checks mean a fresh press under the sampled/predicted conditions. Holding
an already-held button is not another press. The **Next Frame** column evaluates
the future state after that frame's selected inputs: a spent air jump or dash
charge, an expired cooldown and a changed rising-velocity threshold can produce
different current/next values. Frames without a PU preserve PU-driven counters.

The game-code trajectory backend exports readiness from each private simulated
frame, including its future unlocks, wall-jump charges, hyper state, menu and
hitstop flags. The lightweight fallback retains its unchanged-contact assumptions
and withholds game-menu transitions. Unavailable forecasts or missing readiness
metadata show `--`; they never copy current readiness into the next column.
Current readiness remains visible independently of forecast availability.

## Next frame estimate

The right column is a calculated estimate, not a saved future frame. It uses selected authored input during authored recording, the next recorded input during playback, or held physical controls otherwise. The input assumption is displayed. TAS pause previews a resumed/stepped frame using the saved simulation scale; changing the viewing speed does not change the simulated timestep.

The predictor estimates how many fixed physics ticks fit in the next rendered frame using the current fixed-clock remainder. Zero and multiple ticks are supported. Outside TAS it assumes the next frame has the same duration as the current one. Hitstop/game pause and completed/desynced runs show no prediction. Unity can vary its physics tick schedule, so the displayed tick count is an estimate.

The first backend models ordinary horizontal acceleration/braking, gravity and fast fall, jump-apex corrections, existing wall-jump movement locks, buffered ground/air jumps and jump cuts, momentum decay, and ongoing dash velocity/droop. Basic native gravity/damping are included. Input press/release edges are applied after physics, matching the game's FixedUpdate-before-Update buffer processing. During TAS, pending movement-buffer expiry is read without invoking callbacks. Native timer expiry outside TAS is not modeled.

**Contact with the current floor is assumed unchanged. No collision or trigger simulation is performed.** The calculation cannot predict landing, walking off a ledge, wall/ceiling impact, slopes, orb pickup or world changes. Wall interactions, moving platforms (including platform-momentum coyote time), dash startup/end hitstop, dash bounces/hypers, death/respawn and other special movement modes withhold the prediction and display the reason. Current stats remain visible. Even a supported free-movement estimate is not proof of a future outcome.

## Extension points

- `Core/PlayerMotion.cs`: detached scalar/vector snapshots, movement parameters, state classification, and `IMotionPredictor` contract.
- `Core/PlayerActions.cs`: side-effect-free current jump/dash readiness. Extra UI-only context stays outside serialized movie/search movement state.
- `Core/MovementPredictor.cs`: pure one-frame calculation. It copies the input snapshot, never changes live state, and returns either an estimated next snapshot or an explicit unsupported reason.
- `Runtime/PlayerTelemetry.cs`: field mappings for the installed game; reads body values and private controller values. Field lookup failures disable optional telemetry rather than aborting recording.
- `Runtime/PlayerStatsMonitor.cs`: captures one sample at the PostLateUpdate boundary after the TAS session, chooses inputs and timing, and caches data for the UI. It does not advance input, physics, timers, RNG or movies.
- `Runtime/DesktopBridge.cs` and `UI/NativeTheme.cs`: detached stats rows and native desktop presentation.

Do not call live `Physics2D.Simulate` or the real player's movement methods to generate previews: they can mutate gameplay, and simulating physics alone does not execute the movement controller. Keep unsupported cases explicit as the backend expands. See Unity's [physics simulation documentation](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Physics2D.Simulate.html) and [fixed-update timing](https://docs.unity3d.com/6000.0/Documentation/Manual/fixed-updates.html).

## Validation

Run the standalone model tests from the source repository:

```powershell
dotnet run --project Tests/Motion.Tests.csproj -p:ManagedDir="C:\Program Files (x86)\Steam\steamapps\common\IGTAP an Incremental Game That's Also a Platformer\IGTAPfullGame_Data\Managed"
```

The model checks cover movement arithmetic, state classification, independent input edges, buffer timing, unsupported transitions, zero/multiple ticks, timer reads and snapshot non-mutation. The workspace's session tests also check opening with F10, pause/slow-speed timing, authored input selection, loading/menu invalidation, telemetry failures and absence of recording/clock side effects. These tests do not run native Unity physics.

In game, compare the current and estimated columns while on static ground and during a plain jump/fall, then step with F7 and compare with the new current values. Test a held input and a new press separately. Test F10 with F5 hidden, scene loads, slow speeds, hitstop, platform/wall interactions, and mod disable/re-enable. Live Unity/UI accuracy still requires this acceptance pass.
