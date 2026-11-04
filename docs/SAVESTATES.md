# Savestates and rerecording

Current UI (0.6.0): these controls live in the desktop Studio, opened automatically after starting a run from Mods > Tas Studio. F5 reopens Studio and Frame Editor; F9 selects States and F10 shows player stats. The former floating in-game windows have been removed.


TASVideos describes movie savestates as carrying the input movie and its frame count; loading in recording mode restores that timeline and increments the rerecord count. See [On Savestates](https://tasvideos.org/LawsOfTAS/OnSavestates) and [BizHawk rerecording](https://tasvideos.org/Bizhawk/Rerecording). A position/velocity teleport is insufficient: money, upgrades, object state, input edges, timers, RNG and the simulation's hidden state affect the future.

## Implemented backend: replay-checkpoint-v1

Unity does not expose a complete supported serializer for its running physics world and coroutine execution stacks. This implementation reconstructs those through the existing input replay rather than assigning selected fields and claiming that the entire process was restored. Restoration takes scene loading plus replay time; the existing 0.1–1.0 speed limit applies. No fast-forward bypass is used.

1. Saving is allowed only at a completed, paused recording frame. The state embeds an independent copy of the entire recorded prefix, cursor, original start configuration/environment fingerprints, and a world verification witness. Frame zero is valid. Unverified/edited prefixes are rejected.
2. A new immutable file is written via a temporary file and atomic rename. Names are path-sanitized; duplicate names, unsupported formats, unknown JSON members, invalid frame ranges and oversized files are rejected. The saved-state browser only lists metadata, so corrupt files do not hide good ones.
3. Loading validates the file and game/mod/Unity/save environment before leaving the current recording. It saves the previous input timeline to a uniquely named `movies/before-state-*.tas.json` backup, including unsaved frames.
4. The scene loads through the same startup barrier as ordinary playback. The complete saved prefix runs through normal replay checks. At the requested frame, world verification must match before recording is enabled. The cursor denotes the **next** input frame. Last input state is retained so a held jump does not become a new press.
5. On success, recording is paused and the rerecord counter increments. New frames append to the restored prefix. On failure, recording remains disabled and the session pauses in Desync. F8/disable/unload cancels pending restoration. Saved checkpoint files are never modified.

## Verification coverage and limits

`Runtime/WorldCheckpoint.cs` contains explicit, extensible providers. They read game fields rather than serialize arbitrary Unity graphs or execute arbitrary properties. Dictionaries contain readable names and exact double strings for money/upgrades; physics floats use an absolute 0.0001 comparison tolerance. Existing movement probes still verify every replayed frame.

Providers cover currency and upgrade dictionaries; selected global progress; area/zone and floating origin; platform phase flags and motion; orb availability/cooldowns; course path samples, rewards and tracking; local upgrades; the game timer; active/inactive scene rigidbodies and colliders; Unity RNG; the TAS logical clock, simulation scale and movement callback queue. Stable hierarchy paths/component indices replace process-specific instance IDs.

This is not a full memory snapshot. Animator/audio state, arbitrary mod state, clone/atom internal graphs and native physics contact caches are not serialized or exhaustively compared. They are reconstructed only insofar as the original scene and input history reproduce them. Native Invoke/coroutine timing, cosmetic random calls, offline income and unrecorded UI interactions can still cause divergence. A matching witness is useful verification, not proof of every future outcome. A state mismatch must be fixed in the replay/start/provider implementation; never patch over it by setting recorded money or teleporting the player, which would break replay of the final TAS.

Saved-game starts require unchanged original progress files. The checkpoint does not archive or overwrite the user's save directory. Fresh configured starts are preferred for portable checkpoints. Old mod builds remain incompatible and no migration path is provided.

For a future instant backend, use a new explicit method/version with complete providers for native physics, every game coroutine/timer, spawned/despawned objects, clone/atom graphs, and external mutations. Preserve the timeline, backup, verification and frame-boundary contracts above. Do not silently fall back to partial field restoration.

## In-game acceptance

- Create a new TAS, save a state at frame zero, move/jump, then load it; verify loading completes before paused frame zero.
- Save mid-jump and during a dash; continue, restore, then use F7. Check momentum, input edges and resources. Replay the finished rerecord from the beginning.
- Enable two dashes, the air jump, orbs and zippers; save after an orb pickup and while a platform moves. Compare cooldown/phase behavior after restoration.
- Save after earning currency; load and compare balances exactly. Try a local upgrade only if its interaction is actually represented by recorded inputs.
- Change the state name and create multiple checkpoints. Try an existing name, a corrupt file, an incompatible build, and changed starting save data; the current recording must survive rejected imports.
- Cancel restoration with F8 and disable the mod while restoring; normal save handling and clock settings must recover.
- Check F9 works with F5 hidden, and saving/loading is disabled until recording is paused.
- A mismatch report is evidence for a remaining deterministic-replay issue, not a successful full-state restore. Send `last-state-desync.json`, the `.state.json` file and `last-desync.json` for diagnosis.
