# Frame Editor (0.10.0)

**Step PU (default F11)** advances through complete input frames and pauses after
the first frame containing one or more PUs. It uses observed physics callbacks,
not a rounded FPS ratio, so game hitstop is preserved. Frames at 30 FPS can contain
two PUs; both execute. Use normal Step to close an open game menu. A PU step stops
if the menu opens and has a five-second movie-frame limit if physics never resumes.

**Menu** is the game's Pause action, separate from the TAS Pause / Run controls.
Paint a Menu press to open the in-game pause menu; leave at least one released row
before another press to close it. A continuous hold toggles only on its first row.
As with other edits, use Save edits to apply/rebuild. While the game menu is open,
movie frames and menu inputs still run but native game time and physics stop.
Physical recording captures the game's configured Pause binding (normally Escape).
Clicks on the native Resume button become a recorded press on the next advancing
frame. Playback ignores live Resume clicks. Menu navigation/settings are not part
of the input schema; use the recorded Menu action for reproducible open/close.

Each row is one game frame at the movie's chosen FPS. A red background marks a
frame with observed physics updates (PUs). Brighter red marks held inputs on those
rows. After editing, prior PU timing remains visible with a **~** frame marker.
This is historical timing, not a prediction or verification reference. Re-executing
a row replaces it with actual timing, including removing red if it no longer has
a PU. The historical display cache is session-only; saved files contain only actual
timing observations, so unexecuted edited rows can have unknown timing after reload. The
player stats show an inclusive countdown to the next PU (1 = next frame).

Create a run from **Mods > Tas Studio**; Studio and Frame Editor open after loading completes. **F5** reopens both windows. Their UI runs inside IGTAP. The optional Bruteforce window launches a separate hidden search worker; see [Bruteforce](BRUTEFORCE.md). Studio keeps run controls, savestates and player stats. Creation/loading/replay launch live in the game settings page.

## Editing directly in the list

- **Click U/D/L/R, Jump, Dash, Reset or Menu** to toggle that input on a frame. Setting one direction replaces the opposite direction on the same axis. Other inputs on the frame stay unchanged.
- **Drag vertically in a button column** to paint a hold or release over the visible rows. The first cell determines the value for the stroke; the entire stroke becomes one undoable draft edit on mouse release.
- **Double-click X or Y** to type an analog value directly into that cell, from -1 to 1. Dot and comma are accepted. Enter stages the value; Escape cancels the cell edit. Save edits also accepts a value still being typed. Function-key stepping is suppressed while typing in the cell.
- Click a **frame number** to select a row; **Shift-click another frame number** to select a range. Insert adds neutral rows before the selection, Delete removes selected rows, and Clear neutralizes their inputs. Ins/Del also work when the grid has focus. Space paints the last selected button column over the selected rows.
- **Undo / Ctrl+Z**, **Redo / Ctrl+Y** (or Ctrl+Shift+Z) navigate up to 200 edits. History stores input differences, with a total budget of one million stored input values. A new edit clears redo. These operations remain staged until Save edits.
- The final row marked **+** is the next empty frame. Painting it appends a planned input frame.
- Use the scrollbar, mouse wheel, **Go to frame**, or **Follow cursor** to navigate the movie. The viewport requests a bounded slice of the timeline; large movies are not copied to the UI in full.

The grid uses zero-based frame numbers. `>` marks the next frame to execute; `*` marks a frame with no recorded state reference. Held inputs are highlighted. Jump and Dash each have a single visible column.

## Recording and replay

Edit while the run is **paused or stopped**. Changes stay in an unsaved draft: the live movie, game state and on-disk movie remain unchanged. Click **Save edits** (or Ctrl+S with the grid focused) to apply and save the whole batch. **Discard** removes the draft without changing the run. Undo also stays in the draft until Save edits. Running/loading/rebuilding sessions reject edits on both the UI and runtime sides.

Saving edits to future rows does not advance the game. **Record inputs from frame list** is enabled when you save edits: recording consumes those planned rows instead of appending duplicates, and records their resulting state. Uncheck it on the main Run page to use physical controls instead. The checkbox controls recording only; replay always consumes the movie's inputs. Once the planned list ends, recording uses the normal selected/physical input source.

Only clicking **Save edits** starts reconstruction if the batch changes a frame before the current cursor. If all executed inputs still match (for example after undoing an edit), the mod checks the live world against its paused checkpoint and reuses it without reloading. Otherwise it saves a uniquely named recovery movie, reloads the start and replays the edited prefix to the current cursor, then pauses again. This can take time. Future rows are preserved. F8 cancels through the usual stop/reload path. Older cached witnesses are verification targets, not restorable Unity memory snapshots.

Affected state/tick references are invalidated from the edit onward. When rebuilding a recording, the changed prefix receives fresh recording observations; these are not independent verification of the edit. A later replay can check those references. Playback rebuilds leave edited references unverified.

Savestates capture only the executed prefix up to the current cursor. Planned future rows remain in the live movie; saving a checkpoint does not remove them. Loading a checkpoint retains the existing branching behavior.

While a draft exists, stepping/resuming, movie switching and saving/loading checkpoints are blocked until Save edits or Discard. Every accepted input edit is flushed to a separate journal under `data/recovery/`: a baseline followed by input differences. A failed journal write rejects the edit. An interrupted final journal entry is ignored on recovery; earlier complete entries remain recoverable. At the main menu, open the desktop windows with F5 and click **Recover draft** in the Frame Editor, then **Save edits**. Recovery does not start playback. The latest unresolved draft is offered first; resolving it exposes older unresolved drafts. Resolved journals remain on disk for manual cleanup.

The draft also survives closing/reopening either native window; closing a window never applies it. On game unload it is additionally written as recovery-pending-edits. Closing only the Frame Editor leaves the main studio open. Closing the main studio closes both native windows, pauses an advancing run and keeps gameplay clear.

## Implementation and validation

`NativeFrameEditor` handles native rendering, selection, cell editing and drag gestures. `FrameCellEdit` changes only the requested input component and invalidates the affected state suffix. `DesktopBridge` rejects stale timeline revisions and queues edits on the main thread. `TasSession.ApplyTimelineEdit` handles paused edits, reconstruction and planned-row recording. The window never calls Unity from the native UI thread.

Version 0.5.2 introduced drawing the grid into a reusable GDI bitmap and copies the completed image to the window, avoiding visible clearing between rows. Identical timeline snapshots do not invalidate the grid; status labels update only when their text changes. The buffer is recreated on resize and released on close. This follows the [Win32 memory device context](https://learn.microsoft.com/en-us/windows/win32/gdi/memory-device-contexts) drawing approach.

Tests exercise real native cell/drag/inline-edit messages, stale revisions, unchanged inputs, undo/rebuild, future-row preservation, checkpoint prefixes and replay to completion using a fake Unity boundary. The main studio and frame grid are rendered for visual inspection. Live Unity/Mono behavior still needs in-game testing.

The interaction is inspired by the [BizHawk TAStudio piano-roll editor](https://tasvideos.org/Bizhawk), rather than a reproduction of its emulator-specific savestate/greenzone system.
