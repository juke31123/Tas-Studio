# Review of the November 12 profiles

The user's COLDTest recording CSV selects 100 FPS at speed 1 with VSync off.
Its 11 continuous samples average 94.85 FPS, with a minimum sample of 88.90 FPS
and a worst sampled frame of 56.21 ms. These samples are throughput observations,
not a measurement of a single mod subsystem.

The longer playback profile contains 1,038 frames. The median interval is
10.004 ms, close to the requested 100 FPS, but its maximum interval is
3,645.49 ms. The old profiler did not measure trajectory capture, request
creation or rendering, so it cannot attribute this long gap. A synchronous
world export is a possible contributor, not a demonstrated cause from this
report alone. Pausing and external game/render work can also affect intervals.

The 14 world-witness captures average 19.15 ms and peak at 24.27 ms. These can
produce visible spikes, but their replay checks are preserved. The separate
eight-frame recording profile is too short to characterize sustained speed.
It includes frequent world checks during stepping and a 101.25 ms paused
journal operation; neither proves the cause of the longer playback stall.

Version 0.19.1 fixes the independently reproduced checkbox acknowledgment race
and removes repeated automatic capture/restart attempts after preview failure.
It also caches player/origin lookups, retains renderer vertex storage and skips
unchanged paused path uploads. New Trajectory, TrajectoryCapture and
TrajectoryDraw timing sections cover the previously unmeasured work. A new
user profile is required to quantify the effect on COLDTest throughput; no
before/after FPS improvement is claimed from the old profile.

A controlled COLD test at 100 FPS reproduces costly synchronous captures:
two requested captures take 6,752.81 ms total (3,376.40 ms average and
3,391.32 ms maximum). After subtracting the inclusive capture time, the
remaining trajectory controller work totals 38.16 ms across 929 calls.
This establishes that capture can produce a stall of the observed size, but
does not retrospectively identify every gap in the old report. The fix prevents
repeated failed captures; it does not remove the initial capture/explicit-refresh
cost. These headless, paused/stepped checks are not a sustained FPS comparison.
