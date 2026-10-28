using HarmonyLib;
using MegaCrit.Sts2.Core.Saves;

namespace RunCheckpoints;

[HarmonyPatch(typeof(GodotFileIo), nameof(GodotFileIo.WriteFile),
    [typeof(string), typeof(byte[])])]
internal static class SyncSaveWritePatch
{
    private static void Postfix(GodotFileIo __instance, string path, byte[] bytes)
    {
        if (CheckpointArchive.IsRunSave(path))
        {
            CheckpointArchive.Capture(__instance, path, bytes);
        }
    }
}

[HarmonyPatch(typeof(GodotFileIo), nameof(GodotFileIo.WriteFileAsync),
    [typeof(string), typeof(byte[])])]
internal static class AsyncSaveWritePatch
{
    private static void Postfix(GodotFileIo __instance, string path, byte[] bytes, ref Task __result)
    {
        if (CheckpointArchive.IsRunSave(path))
        {
            __result = CaptureAfterWrite(__result, __instance, path, bytes);
        }
    }

    private static async Task CaptureAfterWrite(Task write, GodotFileIo store, string path, byte[] bytes)
    {
        // A postfix on an async method runs before its write finishes.
        await write;
        CheckpointArchive.Capture(store, path, bytes);
    }
}
