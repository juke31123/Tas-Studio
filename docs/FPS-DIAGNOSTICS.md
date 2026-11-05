# FPS diagnostics (0.8.2)

Diagnostics run automatically during recording and playback. Studio's header now
distinguishes the movie's target FPS, measured actual FPS, requested speed and
achieved speed. At 280 movie FPS, 140 actual FPS means approximately 0.50x speed.
The last measurement remains visible while paused.

Create a new short TAS at 280 FPS and speed 1. Record about 10 seconds of movement,
then stop/save and replay it to completion at speed 1. Reports are saved separately
for recording and playback in `Recharge/Mods/Tas.Studio/data/performance/`.
Each CSV filename contains its timestamp and mode; its rows identify the TAS name.
For performance comparisons, note whether your separate overlay mods are enabled.

Measurements use Unity's real-time clock, not the movie clock or patched game FPS
counters. They count completed TAS frame intervals and exclude loading, warmup and
time spent editor-paused. The interval includes work between frame boundaries,
including the engine's frame limiter. Small samples after a single step are noisy.
Game-driven dash hitstop still counts as running frames; it can lower PUs per real
second without lowering actual frame throughput.

One small CSV row is written approximately each second, and partial samples are
flushed on pause/stop/completion. Totals cover all measured running intervals.
Columns include actual FPS, achieved speed, real PUs/second, longest frame interval,
requested speed, target FPS, VSync, focus and mean game time scale.
The former built-in Hitboxes column was removed with the viewer in 0.8.2;
the diagnostics cannot determine whether a separate mod's overlay is enabled.
Context columns on a total row describe the last context; use individual sample
rows when changing settings. Report write failures appear in Studio and do not
stop recording or playback. No frame skipping or simulation timing changes are
introduced by the diagnostics.

The tests verify known FPS/speed ratios, pause exclusion, single steps, final-frame
accounting, independent reports and nonfatal file errors. Your new recording/replay
provides the live performance evidence needed to investigate the slowdown.
