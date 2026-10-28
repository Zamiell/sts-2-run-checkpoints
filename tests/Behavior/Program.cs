using System.Text;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Saves;
using RunCheckpoints;

new Harmony("run-checkpoints.behavior").PatchAll(typeof(CheckpointArchive).Assembly);
var root = Directory.CreateTempSubdirectory("run-checkpoints-tests-").FullName;
try
{
    var profile = Path.Combine(root, "profile1");
    var store = new GodotFileIo(Path.Combine(profile, "saves"));
    var archive = Path.Combine(profile, "run-checkpoints", "current_run");
    byte[] first = Encoding.UTF8.GetBytes(Save("FIRST"));
    store.WriteFile("current_run.save", first);
    Assert(Files(archive).Length == 1, "Sync save missing");
    Assert(File.ReadAllBytes(Files(archive).Single()).SequenceEqual(first), "Save bytes changed");
    store.WriteFile("current_run.save", first);
    Assert(Files(archive).Length == 2, "Repeated save overwrote a checkpoint");
    store.WriteFile("current_run.save", Save("STRING"));
    Assert(Files(archive).Length == 3, "String overload archived more than once");

    foreach (var name in new[] { "progress.save", "settings.save", "current_run.save.backup", "current_run.save.tmp", "123.run" })
    {
        store.WriteFile(name, first);
        await store.WriteFileAsync(name, first);
    }
    Assert(Files(archive).Length == 3, "Non-run saves were archived");

    var completion = new TaskCompletionSource();
    store.PendingWrite = completion.Task;
    var write = store.WriteFileAsync("current_run.save", Save("ASYNC"));
    Assert(!write.IsCompleted && Files(archive).Length == 3, "Async checkpoint written too early");
    completion.SetResult();
    await write;
    Assert(Files(archive).Length == 4, "Async checkpoint missing");

    store.FailSyncWrite = true;
    try
    {
        store.WriteFile("current_run.save", first);
        throw new Exception("Original sync failure was swallowed");
    }
    catch (IOException) { }
    store.FailSyncWrite = false;
    store.PendingWrite = Task.FromException(new IOException("Simulated async failure"));
    try
    {
        await store.WriteFileAsync("current_run.save", first);
        throw new Exception("Original async failure was swallowed");
    }
    catch (IOException) { }
    store.PendingWrite = Task.FromCanceled(new CancellationToken(canceled: true));
    try
    {
        await store.WriteFileAsync("current_run.save", first);
        throw new Exception("Original cancellation was swallowed");
    }
    catch (OperationCanceledException) { }
    Assert(Files(archive).Length == 4, "Failed/cancelled save created a checkpoint");

    // Completing a later save first must not replace the earlier write's snapshot.
    var older = new TaskCompletionSource();
    store.PendingWrite = older.Task;
    var olderWrite = store.WriteFileAsync("current_run.save", Save("OLDER"));
    store.PendingWrite = Task.CompletedTask;
    await store.WriteFileAsync("current_run.save", Save("NEWER"));
    older.SetResult();
    await olderWrite;
    var contents = Files(archive).Select(File.ReadAllText).ToArray();
    Assert(contents.Contains(Save("OLDER")) && contents.Contains(Save("NEWER")),
        "Overlapping writes lost their individual payloads");

    await store.WriteFileAsync("current_run_mp.save", first);
    Assert(Files(Path.Combine(profile, "run-checkpoints", "current_run_mp")).Length == 1,
        "Multiplayer save was not isolated");
    var secondProfile = Path.Combine(root, "profile2");
    var otherStore = new GodotFileIo(Path.Combine(secondProfile, "saves"));
    otherStore.WriteFile("current_run.save", first);
    Assert(Files(Path.Combine(secondProfile, "run-checkpoints", "current_run")).Length == 1,
        "Profile saves were mixed");

    var blocked = Path.Combine(root, "blocked-profile");
    Directory.CreateDirectory(blocked);
    File.WriteAllText(Path.Combine(blocked, "run-checkpoints"), "not a directory");
    var blockedStore = new GodotFileIo(Path.Combine(blocked, "saves"));
    blockedStore.WriteFile("current_run.save", first);
    await blockedStore.WriteFileAsync("current_run.save", first);
    Assert(Log.Errors.Count == 2 && blockedStore.LastWrite == first,
        "Archive failures must be logged without failing game saves");
    CheckCatalog(Path.Combine(root, "catalog"));
    Assert(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(),
        "Temporary checkpoint files leaked");
    Console.WriteLine("PASS: Exact bytes, sync/async writes, isolation, overlap, filtering, and failure handling.");
}
finally
{
    Directory.Delete(root, recursive: true);
}

static string[] Files(string directory) => Directory.Exists(directory)
    ? Directory.GetFiles(directory, "*.save")
    : [];

static string Save(string seed, int hp = 70, long start = 123) =>
    $$"""{"start_time":{{start}},"rng":{"seed":"{{seed}}"},"players":[{"character_id":"CHARACTER.SILENT","current_hp":{{hp}}}]}""";

static void CheckCatalog(string directory)
{
    var store = new CheckpointStore(directory);
    byte[] Bytes(int hp) => Encoding.UTF8.GetBytes(Save("BRANCH", hp));
    var identity = CheckpointStore.Identify(Bytes(70));
    IReadOnlyList<CheckpointInfo> List() => store.List(identity, (_, error) => throw error);
    var first = store.Write(Bytes(70));
    var second = store.Write(Bytes(50));
    var abandonedFuture = store.Write(Bytes(30));
    var checkpoints = List();
    Assert(checkpoints.Count == 3 && checkpoints[0].Path == abandonedFuture, "Catalog is not newest first");
    var previous = store.Previous(checkpoints, identity, CheckpointStore.Fingerprint(Bytes(30)));
    Assert(previous?.Path == second, "Quick rewind did not skip current state");
    store.Write(Encoding.UTF8.GetBytes(Save("BRANCH", 70, start: 456)));
    Assert(List().Count == 3, "Runs sharing a seed were mixed");
    var selected = checkpoints.Single(item => item.Path == second);
    store.Write(Bytes(30), "Before rewind");
    store.MarkLoaded(selected);
    var reloaded = store.Write(Bytes(50));
    var journal = store.ReadJournal(identity);
    store.WriteJournal(identity, journal with { LoadedStateFingerprint = "live-after-load" });
    previous = store.Previous(List(), identity, "live-after-load");
    Assert(previous?.Path == first, "Quick rewind followed abandoned future or reload duplicate");
    Assert(List().Any(item => item.Path == abandonedFuture), "Rewind deleted the abandoned future");
    Assert(store.ReadJournal(identity).LastLoaded == Path.GetFileName(second), "Loaded marker was lost on save");
    Assert(store.ReadJournal(identity).Head == Path.GetFileName(reloaded), "Branch head did not advance");
    Assert(store.ReadJournal(identity).ProtectSave, "History cleanup protection was lost");
    Assert(List().Any(item => item.Label == "Before rewind"), "Before-rewind backup is not labeled");
    Assert(store.ReadVerified(selected).SequenceEqual(Bytes(50)), "Read changed original bytes");
    File.WriteAllBytes(second, Bytes(49));
    try
    {
        store.ReadVerified(selected);
        throw new Exception("Changed checkpoint accepted");
    }
    catch (InvalidDataException) { }
    var malformed = Path.Combine(directory, "broken.save");
    File.WriteAllText(malformed, "{broken");
    var errors = 0;
    store.List(identity, (_, _) => errors++);
    Assert(errors == 1, "Corrupt checkpoint was not explicitly reported");
    var reordered = Encoding.UTF8.GetBytes(
        """{"num_reloads":9,"save_time":42,"run_time":55,"players":[{"current_hp":70,"character_id":"CHARACTER.SILENT"}],"rng":{"seed":"BRANCH"},"start_time":123}""");
    Assert(CheckpointStore.Fingerprint(Bytes(70)) == CheckpointStore.Fingerprint(reordered),
        "Time/reload counters or property ordering created a fake rewind destination");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception(message);
    }
}
