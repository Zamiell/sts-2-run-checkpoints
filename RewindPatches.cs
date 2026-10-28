using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Saves;

namespace RunCheckpoints;

[HarmonyPatch(typeof(NPauseMenu), nameof(NPauseMenu.Initialize))]
internal static class PauseRewindPatch
{
    private static void Postfix(NPauseMenu __instance) => RewindUi.AddButtons(__instance);
}

[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.DeleteCurrentRun))]
internal static class EndedRunProtectionPatch
{
    private static void Prefix(out RunIdentity? __state)
    {
        __state = null;
        try
        {
            var path = RewindService.SavePath(SaveManager.Instance.CurrentProfileId);
            if (File.Exists(path))
            {
                __state = CheckpointStore.Identify(File.ReadAllBytes(path));
            }
        }
        catch (Exception exception) when (CheckpointStore.IsFileError(exception))
        {
            Log.Error($"[RunCheckpoints] Could not identify the ending run: {exception}");
        }
    }

    private static void Postfix(RunIdentity? __state)
    {
        if (__state == null)
        {
            return;
        }
        try
        {
            var path = RewindService.SavePath(SaveManager.Instance.CurrentProfileId);
            var store = new CheckpointStore(CheckpointStore.DirectoryForSave(path));
            var journal = store.ReadJournal(__state);
            if (journal.ProtectSave)
            {
                store.WriteJournal(__state, journal with { ProtectSave = false });
            }
        }
        catch (Exception exception) when (CheckpointStore.IsFileError(exception))
        {
            Log.Error($"[RunCheckpoints] Could not clear ended-run protection: {exception}");
        }
    }
}

[HarmonyPatch(typeof(NGameOverScreen), nameof(NGameOverScreen._Ready))]
internal static class DeathRewindPatch
{
    private static void Postfix(NGameOverScreen __instance) => RewindUi.AddButtons(__instance);
}

[HarmonyPatch(typeof(NGame), nameof(NGame._Input))]
internal static class RewindShortcutPatch
{
    private static bool Prefix(NGame __instance, InputEvent inputEvent)
    {
        if (RewindService.Busy || RewindUi.IsOpen)
        {
            __instance.GetViewport().SetInputAsHandled();
            return false;
        }
        if (inputEvent is InputEventKey
            {
                Pressed: true, Echo: false, CtrlPressed: true, ShiftPressed: false,
                AltPressed: false, MetaPressed: false, Keycode: Key.Z
            } && RewindService.Available)
        {
            try
            {
                if (RewindUi.ShortcutEnabled)
                {
                    __instance.GetViewport().SetInputAsHandled();
                    RewindUi.Open(true);
                    return false;
                }
            }
            catch (Exception exception) when (CheckpointStore.IsFileError(exception))
            {
                Log.Error($"[RunCheckpoints] Cannot read shortcut preference: {exception}");
            }
        }
        return true;
    }
}

// Keep explicitly restored runs resumable even when a prior death is already in run history.
[HarmonyPatch(typeof(SaveManager), "CleanupStaleCurrentRunSaveForProfile")]
internal static class RestoredSaveCleanupPatch
{
    private static bool Prefix(int profileId, string runSaveFileName)
    {
        if (runSaveFileName != "current_run.save")
        {
            return true;
        }
        try
        {
            var path = RewindService.SavePath(profileId);
            if (!File.Exists(path))
            {
                path += ".backup";
                if (!File.Exists(path))
                {
                    return true;
                }
            }
            var identity = CheckpointStore.Identify(File.ReadAllBytes(path));
            var store = new CheckpointStore(CheckpointStore.DirectoryForSave(RewindService.SavePath(profileId)));
            return !store.ReadJournal(identity).ProtectSave;
        }
        catch (Exception exception) when (CheckpointStore.IsFileError(exception))
        {
            Log.Error($"[RunCheckpoints] Could not inspect rewind protection for profile {profileId}: {exception}");
            // Do not delete a potentially restored save when its protection cannot be read.
            return false;
        }
    }
}
