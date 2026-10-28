using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Saves;

namespace RunCheckpoints;

internal static class CheckpointArchive
{
    internal static bool IsRunSave(string path) =>
        Path.GetFileName(path) is "current_run.save" or "current_run_mp.save";

    internal static void Capture(GodotFileIo store, string path, byte[] bytes)
    {
        try
        {
            var source = ProjectSettings.GlobalizePath(store.GetFullPath(path));
            var destination = new CheckpointStore(CheckpointStore.DirectoryForSave(source)).Write(bytes);
            Log.Info($"[RunCheckpoints] Saved checkpoint: {destination}");
        }
        catch (Exception exception) when (CheckpointStore.IsFileError(exception))
        {
            // A checkpoint failure must not turn a successful game save into a failed save.
            Log.Error($"[RunCheckpoints] Failed to archive {path}; the game's save is intact: {exception}");
        }
    }
}
