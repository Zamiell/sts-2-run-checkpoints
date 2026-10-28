using System.Text;
using System.Text.Json;
using RunCheckpoints;

namespace Godot
{
    public sealed class Control;
    public static class ProjectSettings
    {
        public static string GlobalizePath(string path) => path;
    }
}

namespace MegaCrit.Sts2.Core.Logging
{
    public static class Log
    {
        public static void Info(string text) { }
        public static void Error(string text) { }
    }
}

namespace MegaCrit.Sts2.Core.Multiplayer.Game
{
    public enum NetGameType { Singleplayer, Host, Client }
    public sealed class NetService
    {
        public NetGameType Type { get; set; }
    }
}

namespace MegaCrit.Sts2.Core.Multiplayer
{
    public sealed class NetSingleplayerGameService;
}

namespace MegaCrit.Sts2.Core.Saves
{
    public sealed class SerializableRun(byte[] bytes)
    {
        public byte[] Bytes { get; } = bytes;
        public int SchemaVersion { get; set; } = 10;
        public List<SerializablePlayer> Players { get; } = [new()];
        public object? PreFinishedRoom => null;
    }
    public sealed class SerializablePlayer
    {
        public int CurrentHp { get; set; } = 70;
    }
    public static class JsonSerializationUtility
    {
        public static string ToJson(SerializableRun save) => Encoding.UTF8.GetString(save.Bytes);
    }
    public static class UserDataPathProvider
    {
        public static string Root { get; set; } = "";
        public static string GetProfileScopedPath(int profile, string path) =>
            Path.Combine(Root, $"profile{profile}", path);
    }
    public sealed class SaveManager
    {
        public static SaveManager Instance { get; } = new();
        public int CurrentProfileId { get; set; } = 1;
        public Task? CurrentRunSaveTask { get; set; }
        public int GetLatestSchemaVersion<T>() => 10;
    }
}

namespace MegaCrit.Sts2.Core.Runs
{
    public sealed class RunState(Saves.SerializableRun save)
    {
        public RunRngSet Rng { get; } = new(CheckpointStore.Identify(save.Bytes).Seed);
        public static RunState FromSerializable(Saves.SerializableRun save) => new(save);
    }
    public sealed record RunRngSet(string StringSeed);
    public sealed class ActionExecutor
    {
        public object? CurrentlyRunningAction { get; set; }
    }
    public sealed class RunManager
    {
        public static RunManager Instance { get; } = new();
        public bool IsInProgress { get; set; }
        public bool IsCleaningUp { get; set; }
        public Multiplayer.Game.NetService NetService { get; } = new();
        public ActionExecutor ActionExecutor { get; } = new();
        public Saves.SerializableRun Save { get; set; } = null!;
        public Saves.SerializableRun ToSave(object? room) => Save;
        public void CleanUp() => IsInProgress = false;
        public Task SetUpSavedSingleplayer(RunState state, Saves.SerializableRun save)
        {
            if (IsInProgress)
            {
                throw new InvalidOperationException("Run was not cleaned up before loading");
            }
            Save = save;
            IsInProgress = true;
            var path = RewindService.SavePath(Saves.SaveManager.Instance.CurrentProfileId);
            CheckpointStore.AtomicWrite(path, save.Bytes);
            new CheckpointStore(CheckpointStore.DirectoryForSave(path)).Write(save.Bytes);
            return Task.CompletedTask;
        }
    }
}

namespace MegaCrit.Sts2.Core.Nodes.Audio
{
    public sealed class NAudioManager
    {
        public static NAudioManager? Instance => null;
        public void StopMusic() { }
    }
}

namespace MegaCrit.Sts2.Core.Nodes
{
    public sealed class NRun(Runs.RunState state)
    {
        private readonly Runs.RunState _state = state;
        public Runs.RunState State => _state;
        public static NRun? Instance { get; set; }
        public GlobalUi GlobalUi { get; } = new();
    }
    public sealed class GlobalUi
    {
        public MapScreen MapScreen { get; } = new();
    }
    public sealed class MapScreen
    {
        public bool IsTraveling { get; set; }
    }
    public sealed class Transition
    {
        public bool InTransition { get; set; }
        public Task FadeOut() => Task.CompletedTask;
        public Task FadeIn() => Task.CompletedTask;
    }
    public sealed class ReactionContainer
    {
        public void InitializeNetworking(Multiplayer.NetSingleplayerGameService service) { }
    }
    public sealed class NGame
    {
        public static NGame Instance { get; } = new();
        public Transition Transition { get; } = new();
        public ReactionContainer ReactionContainer { get; } = new();
        public SceneContainer RootSceneContainer { get; } = new();
        public int FailLoads { get; set; }
        public int MenuReturns { get; set; }
        public Task LoadRun(Runs.RunState state, object? room)
        {
            if (FailLoads > 0)
            {
                FailLoads--;
                throw new InvalidOperationException("Simulated load failure");
            }
            NRun.Instance = new NRun(state);
            return Task.CompletedTask;
        }
        public sealed class SceneContainer
        {
            public void SetCurrentScene(Godot.Control scene)
            {
                NGame.Instance.MenuReturns++;
                NRun.Instance = null;
            }
        }
    }
}

namespace RunCheckpoints
{
    internal static class CheckpointPresentation
    {
        public static MegaCrit.Sts2.Core.Saves.SerializableRun Parse(byte[] bytes)
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var save = new MegaCrit.Sts2.Core.Saves.SerializableRun(bytes)
            {
                SchemaVersion = root.GetProperty("schema_version").GetInt32()
            };
            save.Players[0].CurrentHp = root.GetProperty("players")[0].GetProperty("current_hp").GetInt32();
            return save;
        }
    }
}
