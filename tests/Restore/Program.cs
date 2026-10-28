using System.Text;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using RunCheckpoints;

var root = Directory.CreateTempSubdirectory("run-checkpoints-restore-").FullName;
try
{
    var (context, target) = Setup("success");
    var before = File.ReadAllBytes(RewindService.SavePath(1));
    await RewindService.Restore(context, target);
    Assert(File.ReadAllBytes(RewindService.SavePath(1)).SequenceEqual(Bytes(70)), "Selected state not persisted");
    var backup = context.Store.List(context.Identity, (_, error) => throw error).Single(item => item.Label != null);
    Assert(File.ReadAllBytes(backup.Path).SequenceEqual(before), "Before-rewind backup differs from current save");
    Assert(context.Store.ReadJournal(context.Identity).LastLoaded == Path.GetFileName(target.Path), "Loaded marker missing");
    Assert(!RewindService.Busy, "Busy lock not released");

    (context, target) = Setup("recovery");
    NGame.Instance.FailLoads = 1;
    await Expect<InvalidOperationException>(() => RewindService.Restore(context, target));
    Assert(NRun.Instance != null && File.ReadAllBytes(RewindService.SavePath(1)).SequenceEqual(Bytes(40)),
        "Failed load did not recover previous resumable state");
    Assert(context.Store.ReadJournal(context.Identity).ProtectSave, "Recovery not protected from history cleanup");

    (context, target) = Setup("death");
    File.Delete(RewindService.SavePath(1));
    await RewindService.Restore(context, target);
    Assert(File.ReadAllBytes(RewindService.SavePath(1)).SequenceEqual(Bytes(70)), "Death-screen restore failed");

    (context, target) = Setup("legacy-death");
    File.Delete(RewindService.SavePath(1));
    Directory.Delete(Path.Combine(context.Store.DirectoryPath, "state"), recursive: true);
    await RewindService.Restore(context, target);
    Assert(File.ReadAllBytes(RewindService.SavePath(1)).SequenceEqual(Bytes(70)), "Legacy death-screen restore failed");

    (context, target) = Setup("changed");
    File.WriteAllBytes(target.Path, Bytes(69));
    await Expect<InvalidDataException>(() => RewindService.Restore(context, target));
    Assert(NGame.Instance.MenuReturns == 0, "Invalid checkpoint tore down active run");
    Assert(File.ReadAllBytes(RewindService.SavePath(1)).SequenceEqual(Bytes(40)), "Invalid checkpoint changed current save");

    (context, target) = Setup("backup-failure");
    Directory.Delete(Path.Combine(context.Store.DirectoryPath, "state"), recursive: true);
    File.WriteAllText(Path.Combine(context.Store.DirectoryPath, "state"), "blocked");
    await Expect<IOException>(() => RewindService.Restore(context, target));
    Assert(NGame.Instance.MenuReturns == 0, "Backup failure tore down active run");
    Assert(File.ReadAllBytes(RewindService.SavePath(1)).SequenceEqual(Bytes(40)), "Backup failure overwrote current save");

    (context, target) = Setup("wrong-profile");
    SaveManager.Instance.CurrentProfileId = 2;
    await Expect<InvalidOperationException>(() => RewindService.Restore(context, target));
    Assert(NGame.Instance.MenuReturns == 0, "Wrong profile was allowed to rewind");

    (context, target) = Setup("multiplayer");
    RunManager.Instance.NetService.Type = NetGameType.Host;
    await Expect<InvalidOperationException>(() => RewindService.Restore(context, target));
    Assert(NGame.Instance.MenuReturns == 0, "Multiplayer was allowed to rewind");

    (context, target) = Setup("busy-action");
    RunManager.Instance.ActionExecutor.CurrentlyRunningAction = new object();
    await Expect<InvalidOperationException>(() => RewindService.Restore(context, target));
    Assert(NGame.Instance.MenuReturns == 0, "Running action was interrupted");

    (context, target) = Setup("pending-save");
    var pending = new TaskCompletionSource();
    SaveManager.Instance.CurrentRunSaveTask = pending.Task;
    var restore = RewindService.Restore(context, target);
    Assert(!restore.IsCompleted && NGame.Instance.MenuReturns == 0, "Did not wait for pending save");
    await Expect<InvalidOperationException>(() => RewindService.Restore(context, target));
    pending.SetResult();
    await restore;
    Assert(!RewindService.Busy, "Pending save left rewind locked");

    (context, target) = Setup("double-failure");
    NGame.Instance.FailLoads = 2;
    await Expect<AggregateException>(() => RewindService.Restore(context, target));
    Assert(context.Store.List(context.Identity, (_, error) => throw error).Any(item => item.Label != null),
        "Recovery failure lost backup");
    Assert(!RewindService.Busy, "Recovery failure left rewind locked");
    Console.WriteLine("PASS: Restore, pre-rewind preservation, death, rollback, profile/multiplayer guards, and concurrency.");
}
finally
{
    Directory.Delete(root, recursive: true);
}

(RewindContext, CheckpointInfo) Setup(string name)
{
    UserDataPathProvider.Root = Path.Combine(root, name);
    SaveManager.Instance.CurrentProfileId = 1;
    SaveManager.Instance.CurrentRunSaveTask = null;
    RunManager.Instance.NetService.Type = NetGameType.Singleplayer;
    RunManager.Instance.IsInProgress = true;
    RunManager.Instance.ActionExecutor.CurrentlyRunningAction = null;
    RunManager.Instance.Save = CheckpointPresentation.Parse(Bytes(40));
    NRun.Instance = new NRun(RunState.FromSerializable(RunManager.Instance.Save));
    NGame.Instance.FailLoads = 0;
    NGame.Instance.MenuReturns = 0;
    var context = RewindService.Context();
    var selectedPath = context.Store.Write(Bytes(70));
    context.Store.Write(Bytes(40));
    CheckpointStore.AtomicWrite(RewindService.SavePath(1), Bytes(40));
    return (context, context.Store.List(context.Identity, (_, error) => throw error)
        .Single(item => item.Path == selectedPath));
}

static byte[] Bytes(int hp) => Encoding.UTF8.GetBytes(
    $$"""{"schema_version":10,"start_time":123,"rng":{"seed":"SEED"},"players":[{"character_id":"CHARACTER.SILENT","current_hp":{{hp}}}]}""");

static async Task Expect<T>(Func<Task> action) where T : Exception
{
    try
    {
        await action();
    }
    catch (T)
    {
        return;
    }
    throw new Exception($"Expected {typeof(T).Name}");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception(message);
    }
}
