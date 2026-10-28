# Run Checkpoints

A Slay the Spire 2 mod that archives run saves and lets you rewind a singleplayer run from the pause or game-over screen.

Each checkpoint contains the exact bytes supplied to a successful local write of `current_run.save` or `current_run_mp.save`, including the seed and RNG state. Async saves are archived only after the write succeeds. Settings, progression, history, backup, and temporary files are not checkpointed.

## Rewind UI

Open the pause menu or game-over screen and select **Rewind...**. The scrollable picker shows only the current run's checkpoints, newest first, with act/floor, room boundary, HP, gold, and save time. Selecting an entry shows its act name, character, seed, deck, and relics. The last loaded checkpoint is highlighted.

Choose **Rewind here**, then **Confirm rewind**. **Cancel** or Escape backs out of confirmation; Close or Escape closes the picker. The **Rewind to previous checkpoint** button opens the confirmation directly. The picker also has an opt-in **Ctrl+Z** shortcut; it always asks for confirmation. Its preference is saved in `user://run-checkpoints/settings.json`.

Quick rewind follows the current branch and skips duplicate reload snapshots. Immediately after a rewind it steps farther back, not into the abandoned future. If you have changed the live state, it can first restart from the latest save on the branch. All other checkpoints remain selectable in the full picker.

## Checkpoint location

Checkpoints are stored beside each profile's `saves` directory:

```text
profile1/
  saves/
    current_run.save
  run-checkpoints/
    current_run/
      <UTC timestamp>-<unique ID>.save
      <UTC timestamp>-<unique ID>.save.json
      state/
    current_run_mp/
      <UTC timestamp>-<unique ID>.save
```

For a modded Windows Steam profile, this is typically:

```text
%APPDATA%/SlayTheSpire2/steam/<Steam ID>/modded/profile1/run-checkpoints/
```

Accounts, profiles, and singleplayer/multiplayer saves remain separated. Runs are identified by their start time, seed, and character, not seed alone. The `.save.json` sidecars track checkpoint ancestry and labels; `state/` tracks the current branch and last-loaded checkpoint. Keep these alongside the saves.

Checkpoints use unique names and are published only after their contents have been flushed to disk. Successful copies and archive failures are recorded in the game log. An archive I/O failure does not interrupt the original game save.

Checkpoints are local only and are never automatically pruned. They survive the game deleting the current run on death or victory; remove unwanted checkpoints manually to reclaim space. Local writes from cloud synchronization can also create checkpoints.

## Restoration

Before every rewind, the mod archives the last resumable save as **Before rewind**. This is not a snapshot of unsaved combat actions. If the game has already deleted the current save on death, the last archived save is preserved instead. A failed backup cancels the rewind before unloading the run.

Restoration waits for pending saves, validates the selected checkpoint, cleans up the old run, and uses the game's singleplayer load flow. The restored save is written through the game's normal save/cloud backend. If loading fails, the mod attempts to reload the preserved pre-rewind save and reports the error. If recovery also fails, the error includes the backup path; restart the game before continuing.

Checkpoints capture normal room boundaries, not arbitrary mid-combat states. **Before combat** restarts the fight; **After combat / rewards** resumes the saved post-combat state. Unknown map nodes are labeled as unknown rather than guessing which encounter will be regenerated.

Multiplayer checkpoints are still archived, but restoring them is disabled. Active actions and room transitions must finish before restoration. Save schemas must match; keep the same game version and mods.

Explicitly restored saves are protected from the game's startup cleanup of runs already in history, so a death-screen rewind can survive a restart. This protection is cleared when the run ends again. Uninstalling the mod removes the protection.

Account progression, unlocks, statistics, and existing run history are **not rolled back**. Replaying rooms can affect them again, and finishing the same run again can update its history entry. The mod is marked as affecting gameplay.

## Build and install

Requires a .NET SDK supporting `net9.0` and a local game installation.

```sh
dotnet build -c Release
./install.sh
```

Default Steam paths are supported on Windows, macOS, and WSL. Override with `-p:GameDir="/path/to/Slay the Spire 2"` or `-p:GameDataDir="/path/to/data_sts2_platform"`.

Alternatively, copy `RunCheckpoints.dll` and `RunCheckpoints.json` from `dist/RunCheckpoints/` into the game's `mods/RunCheckpoints/` directory. On macOS, `mods/` is inside `SlayTheSpire2.app/Contents/MacOS/`.

Restart the game after installing. Removing `mods/RunCheckpoints/` uninstalls the mod without deleting archived saves.

## Development checks

The checks require the .NET 9 runtime, matching the game's Harmony library.

```sh
dotnet run --project tests/Behavior
dotnet run --project tests/Restore
dotnet run --project tests/Compatibility -- \
  "/path/to/data_sts2_platform" \
  "dist/RunCheckpoints/RunCheckpoints.dll"
```

Behavior checks patch a stub save backend and exercise archive integrity, branch selection, and duplicate handling. Restore checks cover backup-before-load, rollback, death-screen restoration, and safety guards with game stubs. All file-writing checks use isolated temporary directories. Compatibility checks apply all patches to the installed game's assembly without touching game saves.

Native UI layout, input focus, and an in-game save/restore round trip still require an in-game check.
