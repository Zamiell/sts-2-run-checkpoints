using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RunCheckpoints;

internal sealed record RunIdentity(long StartTime, string Seed, string Character)
{
    public string Key => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes($"{StartTime}\n{Seed}\n{Character}")));

    public static RunIdentity FromJson(JsonElement root) => new(
        root.GetProperty("start_time").GetInt64(),
        root.GetProperty("rng").GetProperty("seed").GetString()
            ?? throw new InvalidDataException("Save has no seed."),
        root.GetProperty("players")[0].GetProperty("character_id").GetString()
            ?? throw new InvalidDataException("Save has no character."));
}

internal sealed record CheckpointInfo(
    string Path, RunIdentity Run, string Fingerprint, DateTime SavedUtc, string? Parent, string? Label);

internal sealed record CheckpointLink(string? Parent, string? Label);
internal sealed record RewindJournal(string? Head = null, string? LastLoaded = null,
    bool ProtectSave = false, string? LoadedStateFingerprint = null);

internal sealed class CheckpointStore(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);

    public static string DirectoryForSave(string source) => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(source)!, "..", "run-checkpoints", Path.GetFileNameWithoutExtension(source)));

    public static RunIdentity Identify(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        return RunIdentity.FromJson(document.RootElement);
    }

    public static string Fingerprint(byte[] bytes)
    {
        var root = JsonNode.Parse(bytes) as JsonObject
            ?? throw new InvalidDataException("Checkpoint must contain a JSON object.");
        // Loading and wall-clock time do not make a new rewind destination.
        foreach (var name in new[] { "save_time", "run_time", "num_reloads" })
        {
            root.Remove(name);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(root))));
    }

    private static string Canonical(JsonNode? node) => node switch
    {
        JsonObject obj => "{" + string.Join(",", obj.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => JsonSerializer.Serialize(pair.Key) + ":" + Canonical(pair.Value))) + "}",
        JsonArray array => "[" + string.Join(",", array.Select(Canonical)) + "]",
        _ => node?.ToJsonString() ?? "null"
    };

    public string Write(byte[] bytes, string? label = null)
    {
        var run = Identify(bytes);
        var journal = ReadJournal(run);
        var filename = $"{DateTime.UtcNow:yyyyMMdd'T'HHmmss.fffffff'Z'}-{Guid.NewGuid():N}.save";
        var destination = Path.Combine(DirectoryPath, filename);
        AtomicWrite(destination, bytes);
        WriteJson(destination + ".json", new CheckpointLink(journal.Head, label));
        WriteJournal(run, journal with { Head = filename });
        return destination;
    }

    public IReadOnlyList<CheckpointInfo> List(RunIdentity run, Action<string, Exception> onError)
    {
        if (!Directory.Exists(DirectoryPath))
        {
            return [];
        }
        var result = new List<CheckpointInfo>();
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.save"))
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var identity = Identify(bytes);
                if (identity != run)
                {
                    continue;
                }
                var link = File.Exists(path + ".json")
                    ? JsonSerializer.Deserialize<CheckpointLink>(File.ReadAllText(path + ".json"))
                        ?? throw new InvalidDataException("Empty checkpoint metadata.")
                    : null;
                result.Add(new(path, identity, Fingerprint(bytes), File.GetLastWriteTimeUtc(path),
                    link?.Parent, link?.Label));
            }
            catch (Exception exception) when (IsFileError(exception))
            {
                onError(path, exception);
            }
        }
        return result.OrderByDescending(item => item.SavedUtc)
            .ThenByDescending(item => item.Path, StringComparer.Ordinal).ToArray();
    }

    public CheckpointInfo? Previous(IReadOnlyList<CheckpointInfo> checkpoints, RunIdentity run, string currentFingerprint)
    {
        var journal = ReadJournal(run);
        var head = journal.Head;
        if (journal.LoadedStateFingerprint == currentFingerprint && journal.LastLoaded != null)
        {
            currentFingerprint = checkpoints.FirstOrDefault(item =>
                Path.GetFileName(item.Path) == journal.LastLoaded)?.Fingerprint ?? currentFingerprint;
        }
        var current = head == null ? checkpoints.FirstOrDefault()
            : checkpoints.FirstOrDefault(item => Path.GetFileName(item.Path) == head);
        var visited = new HashSet<string>();
        while (current != null && visited.Add(current.Path))
        {
            if (current.Fingerprint != currentFingerprint)
            {
                return current;
            }
            if (current.Parent != null)
            {
                current = checkpoints.FirstOrDefault(item => Path.GetFileName(item.Path) == current.Parent);
            }
            else
            {
                // Archives from v1.0 have no parent metadata.
                current = File.Exists(current.Path + ".json") ? null
                    : checkpoints.SkipWhile(item => item != current).Skip(1).FirstOrDefault();
            }
        }
        return null;
    }

    public RewindJournal ReadJournal(RunIdentity run)
    {
        var path = JournalPath(run);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<RewindJournal>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Empty rewind journal.")
            : new();
    }

    public void WriteJournal(RunIdentity run, RewindJournal journal) => WriteJson(JournalPath(run), journal);

    public void MarkLoaded(CheckpointInfo checkpoint) => WriteJournal(checkpoint.Run,
        new(Path.GetFileName(checkpoint.Path), Path.GetFileName(checkpoint.Path), ProtectSave: true));

    public byte[] ReadVerified(CheckpointInfo checkpoint)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(checkpoint.Path)) != DirectoryPath)
        {
            throw new InvalidDataException("Checkpoint is outside this profile's archive.");
        }
        var bytes = File.ReadAllBytes(checkpoint.Path);
        if (Identify(bytes) != checkpoint.Run || Fingerprint(bytes) != checkpoint.Fingerprint)
        {
            throw new InvalidDataException("Checkpoint changed after the picker was opened. Open it again.");
        }
        return bytes;
    }

    private string JournalPath(RunIdentity run) => Path.Combine(DirectoryPath, "state", run.Key + ".json");

    internal static bool IsFileError(Exception exception) => exception is IOException
        or UnauthorizedAccessException or JsonException or InvalidOperationException
        or KeyNotFoundException or IndexOutOfRangeException or FormatException;

    private static void WriteJson<T>(string path, T value) => AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(value));

    internal static void AtomicWrite(string destination, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
