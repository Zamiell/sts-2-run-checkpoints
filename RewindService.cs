using System.Text;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Runs;

namespace RunCheckpoints;

internal sealed record RewindContext(int Profile, RunIdentity Identity, CheckpointStore Store, byte[] LiveState);

internal static class RewindService
{
    internal static bool Busy { get; private set; }
    private static readonly AccessTools.FieldRef<NRun, RunState> State =
        AccessTools.FieldRefAccess<NRun, RunState>("_state");

    public static bool Available => NRun.Instance != null && RunManager.Instance.IsInProgress
        && !RunManager.Instance.IsCleaningUp && RunManager.Instance.NetService.Type == NetGameType.Singleplayer;

    public static string SavePath(int profile) => ProjectSettings.GlobalizePath(
        UserDataPathProvider.GetProfileScopedPath(profile, "saves/current_run.save"));

    public static RewindContext Context()
    {
        if (!Available)
        {
            throw new InvalidOperationException("Rewind is available only in an open singleplayer run.");
        }
        var bytes = Encoding.UTF8.GetBytes(JsonSerializationUtility.ToJson(RunManager.Instance.ToSave(null)));
        var profile = SaveManager.Instance.CurrentProfileId;
        return new(profile, CheckpointStore.Identify(bytes),
            new CheckpointStore(CheckpointStore.DirectoryForSave(SavePath(profile))), bytes);
    }

    public static async Task Restore(RewindContext context, CheckpointInfo checkpoint)
    {
        if (Busy)
        {
            throw new InvalidOperationException("A rewind is already in progress.");
        }
        EnsureContext(context);
        if (checkpoint.Run != context.Identity)
        {
            throw new InvalidDataException("The selected checkpoint belongs to a different run.");
        }
        if (NGame.Instance!.Transition.InTransition || NRun.Instance!.GlobalUi.MapScreen.IsTraveling
            || RunManager.Instance.ActionExecutor.CurrentlyRunningAction != null)
        {
            throw new InvalidOperationException("Wait for the current action or room transition to finish before rewinding.");
        }
        Busy = true;
        var tornDown = false;
        string? backup = null;
        RewindJournal? originalJournal = null;
        try
        {
            if (SaveManager.Instance.CurrentRunSaveTask is { } pending)
            {
                await pending;
            }
            EnsureContext(context);
            var bytes = context.Store.ReadVerified(checkpoint);
            var save = CheckpointPresentation.Parse(bytes);
            if (save.SchemaVersion != SaveManager.Instance.GetLatestSchemaVersion<SerializableRun>())
            {
                throw new InvalidDataException("Checkpoint uses a different save schema. Use the same game version.");
            }
            if (save.Players[0].CurrentHp <= 0)
            {
                throw new InvalidDataException("This checkpoint contains a dead player and cannot be resumed.");
            }
            var state = RunState.FromSerializable(save);
            // Do not serialize a live fight: only the last normal save is resumable.
            var path = SavePath(context.Profile);
            byte[] previous;
            if (File.Exists(path))
            {
                previous = File.ReadAllBytes(path);
            }
            else
            {
                var head = context.Store.ReadJournal(context.Identity).Head;
                var previousPath = head != null ? Path.Combine(context.Store.DirectoryPath, head)
                    : context.Store.List(context.Identity, (file, error) =>
                        Log.Error($"[RunCheckpoints] Cannot read older checkpoint {file}: {error}"))
                        .FirstOrDefault()?.Path;
                if (previousPath == null)
                {
                    throw new InvalidDataException("No resumable save is available to preserve before rewinding.");
                }
                previous = File.ReadAllBytes(previousPath);
            }
            if (CheckpointStore.Identify(previous) != context.Identity)
            {
                throw new InvalidDataException("The current save belongs to a different run. Rewind was cancelled.");
            }
            var previousSave = CheckpointPresentation.Parse(previous);
            if (previousSave.Players[0].CurrentHp <= 0
                || previousSave.SchemaVersion != save.SchemaVersion)
            {
                throw new InvalidDataException("The pre-rewind save is not resumable with this game version.");
            }
            backup = context.Store.Write(previous, "Before rewind (last resumable save, not live combat)");
            originalJournal = context.Store.ReadJournal(context.Identity);
            context.Store.MarkLoaded(checkpoint);

            NAudioManager.Instance?.StopMusic();
            tornDown = true;
            await Load(save, state);
            var journal = context.Store.ReadJournal(context.Identity);
            context.Store.WriteJournal(context.Identity, journal with
            {
                LoadedStateFingerprint = CheckpointStore.Fingerprint(Context().LiveState)
            });
            Log.Info($"[RunCheckpoints] Rewound to {checkpoint.Path}; previous save: {backup}");
        }
        catch (Exception exception)
        {
            Log.Error($"[RunCheckpoints] Rewind failed. Backup: {backup ?? "not created"}. {exception}");
            if (tornDown && backup != null && originalJournal != null)
            {
                try
                {
                    context.Store.WriteJournal(context.Identity, originalJournal with { ProtectSave = true });
                    var recovery = CheckpointPresentation.Parse(File.ReadAllBytes(backup));
                    var recoveryState = RunState.FromSerializable(recovery);
                    await Load(recovery, recoveryState);
                }
                catch (Exception recoveryError)
                {
                    Log.Error($"[RunCheckpoints] Recovery failed; preserved backup: {backup}. {recoveryError}");
                    throw new AggregateException($"Rewind and recovery failed. Your backup is at {backup}. Restart the game before continuing.",
                        exception, recoveryError);
                }
                throw new InvalidOperationException("Rewind failed; your previous resumable save has been reloaded. "
                    + exception.Message, exception);
            }
            else if (originalJournal != null)
            {
                context.Store.WriteJournal(context.Identity, originalJournal);
            }
            throw;
        }
        finally
        {
            Busy = false;
        }
    }

    private static async Task Load(SerializableRun save, RunState state)
    {
        var game = NGame.Instance!;
        await game.Transition.FadeOut();
        RunManager.Instance.CleanUp();
        // Remove the old run before setup, without starting the main menu's asynchronous callbacks.
        game.RootSceneContainer.SetCurrentScene(new Control());
        await RunManager.Instance.SetUpSavedSingleplayer(state, save);
        game.ReactionContainer.InitializeNetworking(new NetSingleplayerGameService());
        await game.LoadRun(state, save.PreFinishedRoom);
        await game.Transition.FadeIn();
    }

    private static void EnsureContext(RewindContext context)
    {
        if (!Available || SaveManager.Instance.CurrentProfileId != context.Profile
            || State(NRun.Instance!).Rng.StringSeed != context.Identity.Seed
            || Context().Identity != context.Identity)
        {
            throw new InvalidOperationException("The active run changed. Open the checkpoint picker again.");
        }
    }
}
