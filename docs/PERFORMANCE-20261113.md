# Trajectory performance and native checks - 2026-11-13

Version 0.19.5 keeps simulation in the bundled external EngineHost process.
Native snapshot/witness reads and drawing necessarily remain in Unity. The
transport thread streams immutable input-history chunks and uses a typed,
bounded binary protocol. Repeated input spans retain their explicit tap edges.
The worker uses one committed cursor and bounded prefix checkpoints instead of
the search node/cache machinery. Obsolete forecasts yield to newer controls.

## Continuous recording

The controlled native 280 FPS test recorded 562 movie frames, with the preview
enabled through 560 observed advancing frames. Its witness checks did not fail;
26 requests were issued and a valid cached path was visible on 341 frames.
Changing controls or hitstop can temporarily hide a path while a new one is
calculated. This is not a claim that a forecast is visible on every frame.

| Measured game-thread section | Mean per recorded frame | P99 | Maximum |
| --- | ---: | ---: | ---: |
| Trajectory controller | 0.0731 ms | 1.4889 ms | 2.2453 ms |
| Trajectory drawing | 0.00394 ms | 0.0385 ms | 0.0480 ms |

Initial capture was separately measured at 3166.3 ms while paused. The longest
observed external forecast was 43.768 ms. These are one controlled fixture's
measurements, not a before/after benchmark or a sustained 280 FPS guarantee.
The test also captures diagnostic witnesses every PU/frame, adding work that
normal recording does not perform. Existing world replay witnesses can still
take tens of milliseconds; they were not removed to improve a benchmark.

Evidence: `Engine.Host/artifacts/native/20261113-002328-481/fps-280/`.

## Stepping, Reset and hazards

- Mixed frame and PU stepping: 80 frames, 70 visible path frames, 17 requests;
  no preview witness failure. All 111 native diagnostic samples matched, and
  restore/parallel-worker checks passed. Evidence: `20261113-003801-269`.
- Reset on frame 4 at 280 FPS had zero PUs on that frame. All 97 native samples
  matched. Persistent forecast tests compared 156 future frames, including
  action readiness, rewinds and A/B/A rollback. Evidence:
  `20261113-002439-864` and `artifacts/trajectory-live-20261113/reset-280`.
- A new spike with default grace killed the native player on the third PU:
  Enter, first Stay, then fatal second Stay. At 280 FPS the fatal frame was 12.
  Initially the simulator killed one PU early because geometric overlaps were
  primed as existing contacts. Exporting actual native trigger pairs corrected
  this; the 280 FPS trace matches all 35 samples. Forecast checks also match the
  grace window at 50 FPS. Evidence: `20261113-002849-802` and the `spike-*`
  forecast reports under `artifacts/trajectory-live-20261113`.
- Dash into the same spike killed on the second PU (first Stay), at native frame
  15 in this fixture with dash hitstop. All 49 diagnostic samples matched, as did
  restore and worker checks. Forecasts include the fatal position rather than
  ending one sample before it. Evidence: `20261113-003307-204`.
- Capturing mid-contact at frame 7 retained the spent grace counter and Stay
  phase. Six future native frames matched through death; all 23 complete trace
  samples matched. Live player/movie/RNG were unchanged by the preview.
  Evidence: `20261113-004027-395`.

The general 50 FPS death fixture still diverges in a distant mover's sleeping
witness at frame 15, after the forecast's fatal endpoint. That witness remains
strict; arbitrary post-death histories are not certified by these tests.
Forecasts stop at death. An unsupported or mismatching actual prefix hides the
path and requires an explicit paused refresh, as before.

The test driver restored the installed DLL and verified all 32 pre-existing
save files after each controlled game run. These are owned prototype fixtures,
not modifications of the user's recordings. No gameplay step is performed in
the live world to compute a forecast.

## Protocol and regression coverage

Checks cover all movement fields and timer infinities, future jump/dash
readiness, binary/RLE round trips, immutable branch histories, file locks,
truncated/wrong-kind messages, input retaps, stale positions/velocities, rejected
witness recovery, obsolete requests, prefix rewinds and snapshot rollback.
Continuous End and the last paused frame of a frame/PU step use separate
per-frame pacing metadata in diagnostic traces; the capture-tick comparison was
not relaxed. The movie format, PU rate and search ability policy are unchanged.

Initial inspection still requires a paused initialized TAS. Long horizons cost
more worker time; actual input history remains bounded to 20,000 frames per
capture. Unknown scripts and unvalidated mechanics retain their existing
guards. These tests establish the listed interactions, not complete game parity.
