using System.Runtime.CompilerServices;
using System.Text;

namespace Godot
{
    public static class ProjectSettings
    {
        public static string GlobalizePath(string path) => Path.GetFullPath(path);
    }
}

namespace MegaCrit.Sts2.Core.Logging
{
    public static class Log
    {
        public static List<string> Errors { get; } = [];
        public static void Info(string text) { }
        public static void Error(string text) => Errors.Add(text);
    }
}

namespace MegaCrit.Sts2.Core.Saves
{
    public sealed class GodotFileIo(string saveDir)
    {
        public Task PendingWrite { get; set; } = Task.CompletedTask;
        public bool FailSyncWrite { get; set; }
        public byte[]? LastWrite { get; private set; }

        public string GetFullPath(string path) => Path.Combine(saveDir, path);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void WriteFile(string path, byte[] bytes)
        {
            if (FailSyncWrite)
            {
                throw new IOException("Simulated game write failure");
            }
            LastWrite = bytes;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public async Task WriteFileAsync(string path, byte[] bytes)
        {
            await PendingWrite;
            LastWrite = bytes;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void WriteFile(string path, string content) => WriteFile(path, Encoding.UTF8.GetBytes(content));

        [MethodImpl(MethodImplOptions.NoInlining)]
        public Task WriteFileAsync(string path, string content) => WriteFileAsync(path, Encoding.UTF8.GetBytes(content));
    }
}
