# Gameplay captures and glitch validation

The supplied `tests/test Tas` data contains five passive gameplay captures and
the 7,740-frame `Springs.tas.json` at 280 FPS. The source files remain unchanged.
Captured game data and native build outputs are local test inputs, not release
assets or source files to commit.

## Passive capture audit

The captures are usable simulator inputs. `capture-replay` now imports and
continuously executes them with their own recorded frame durations, per-PU
inputs, clocks, pauses and press/release edges:

```powershell
.\Engine.Host\bin\Release\net10.0\TasStudio.EngineHost.exe capture-replay `
  'C:\captures\example-validation.zip' 'C:\reports\example-replay'
```

The output contains imported world/start snapshots and `comparison.json`, with
the first strict difference and bounded diagnostics. Source ZIPs are read-only.
Expected samples are never written back into the running simulation. Exit 0
means all scoped player observations match, 1 means a strict comparison differs,
and 2 means the import or execution failed. A differing game-build hash is
reported but does not prevent a diagnostic replay.

All 847 supplied frames and 690 PUs run through this importer. Pauselift matches
all 615 player samples and Pauselesslift all 555. The three inwall captures match
every scoped observation after their initial frame: inwall 794/796 overall,
reverse inwall 858/860, straight inwall 936/938. Their only differences are the
initial contact's tiny body-Y velocity residual and its next pre-PU observation.
`Passed` remains false for those strict full-trace comparisons;
`PostInitialFramePassed` separately reports the exact later-frame agreement.
The missing initial native contact cache remains a coverage limit.

The importer reconstructs scene hierarchy/sibling order for collider creation,
which fixes the reverse-inwall run's two simultaneous upgrade-box contacts.
Fixed-origin Invoke callbacks are eligible after physics; Update-origin callbacks
remain in the dynamic phase. Those distinctions preserve the jump-buffer and
recent-jump windows in the passive captures. These changes are confined to the
observed-timing path; the standard fixed-FPS TAS clock remains separate.

Recorder 1.1 accidentally omitted Unity arrays such as the afterimage palette.
The importer supplies presentation-only placeholder colors when that palette is
absent, preserving afterimage bookkeeping. Recorder 1.1.1 exports those arrays,
layer names and completed part metadata correctly. Existing recordings need not
be repeated to use this importer. Animation/RNG/economy parity is outside this
player-state comparison; it includes the player collider sizes, offsets and masks.

The prepared host can inspect a Recorder 1.1 archive without extracting it:

```powershell
.\Engine.Host\bin\Release\net10.0\TasStudio.EngineHost.exe capture-audit `
  'C:\captures\example-validation.zip' 'C:\reports\example.json'
```

The importer verifies the payload hashes, file whitelist, journal ordering,
frame/PU counts and input masks. It preserves native variable frame deltas,
exact press/release edges and frames containing zero or several PUs. Reports
include the input-change frames and movement/body fields that changed between
the pre-input observation and frame end. These changes include later callbacks
and are observations, not a causal attribution to a particular input.
`FramesWithDifferentPhysicsInput` separately counts frames whose FixedUpdate
observed different action state from the later Movement.Update observation.

| Recording | Frames | PUs | Frames without a PU | Input changes without a PU |
| --- | ---: | ---: | ---: | ---: |
| inwall super | 170 | 152 | 40 | 3 |
| Pauselesslift | 111 | 111 | 21 | 4 |
| Pauselift | 174 | 89 | 106 | 4 |
| reverse inwall super | 190 | 160 | 49 | 6 |
| straight inwall super | 202 | 178 | 45 | 3 |

Pauselift records Menu presses at capture frames 33 and 115. Dash is pressed
on frame 28, a frame with no physics update. The inwall recordings contain the
separate dash/jump presses and collision observations needed for further
mechanic checks. A recording's name alone does not certify the technique.

These archives target 280 FPS but contain variable frame durations, including
large first-frame catch-up updates. Do not convert them into 280-Hz TAS movies
by assigning one input row per observed frame. Doing that changes the PU grid
and input windows. A manual TAS still uses its selected fixed recording rate.

All five manifests identify game assembly SHA-256
`A439DB24DB7ACE31A93C139F9C5918BBB5F53EC57987309D4FD6796C2897A874`.
The local prepared host and Springs TAS identify
`106548ED1092EF4F39ECAB82BF59A6937C1AF8512567B303626B21E40B98B274`.
The audit reports this mismatch explicitly. The captures also lack complete
native contact caches, elapsed sleep state and pending native timer deadlines.
Passing `capture-audit` means archive integrity and observation consistency;
`SimulatorValidated` stays false. It is not a full `restore-check` result.

The original 1.1 recorder accidentally exported an empty `parts` metadata list.
The audit identifies parts from the hashed journal entries and reports the gap.
The recorder source now writes the completed part metadata into its manifest.

## Spring sync phase

The supplied spring TAS replays against its recorded states in the native game.
Its first offline divergence was at frame 2870, after a physics update: spring
contact moved the player Transform by 10 units while the native Rigidbody still
reported its pre-write pose. The adapter synchronised that Transform write too
early, during the after-physics phase.

The adapter now leaves contact-callback Transform writes pending until the
native synchronisation phase. The original spring code, forces and timers still
execute unchanged. After this correction the complete original 10,473-sample
native trace matches exactly. New exports additionally include each spring's
scalar state, including cooldown, and checkpoints exercise active spring locks
and cooldown restoration. Search spring contacts require those state witnesses.

The complete run also matches with the spring witnesses enabled. Restoring an
active spring lock or cooldown at frame 2871 matches each 380-sample continuation.
Both chain and beam searches from frame 2864 through spring contact produced
candidates that passed real-game playback verification. Acceptance staged a
draft, and discarding it preserved the original input suffix. Two-worker shuffled
branch checks also match the complete native trace. This validates the exercised
spring route; it does not certify every orientation or technique.

## Additional spring directions

`Springsmoredirections-20261109-185555-652-b2c3a9-validation.zip` imports all
9,998 observed frames and 1,753 PUs using the matching installed game build.
24,701 of 25,255 scoped player-state samples match. The 554 differing samples
are before frame 239; their first differing field is body Y velocity, with the
small opposite-sign residual `359a3db2` / `b59a3db2`. The full comparison remains
failed, without rounding, snapping or applying later expected state.

The first recorded spring state is frame 2260. Every one of the 19,548 scoped
player samples from that contact through the end matches exactly, including
the additional spring directions supplied by the author. This strengthens
continuous spring trajectory coverage; it does not certify animation, RNG,
economy, arbitrary initial contact caches or offline branching for every spring
orientation. The passive report now records the last mismatch, first-difference
field counts and a separate post-first-spring comparison so its first 100
diagnostics cannot conceal later divergence.

## Remaining glitch work

1. Expand the implemented passive replay path to animation, RNG/economy and
   additional moving-world mechanics. Preserve its separate native timing path.
2. Resolve initial-state gaps and game-build identity before claiming full
   trajectory parity. Do not initialise hidden state from later expected samples
   or accept a position-only comparison as proof.
3. Add native fixed-FPS TAS fixtures for pauselift, pauselesslift and each inwall
   variant, including branch restoration and worker isolation.
4. Enable explicit search technique policies only after their applicable checks
   pass. Search currently rejects Menu actions and does not expose a validated
   glitch policy. Manual editing already retains per-frame Jump, Dash and Menu
   inputs, but that does not certify every glitch at every recording FPS.

The approximate static solver is not a replacement for these checks. Native
candidate playback and explicit Save edits remain necessary.
