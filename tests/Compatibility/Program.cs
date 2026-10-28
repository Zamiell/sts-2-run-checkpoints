using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

if (args.Length != 2)
{
    throw new ArgumentException("Usage: Compatibility <game-data-directory> <RunCheckpoints.dll>");
}

var gameData = Path.GetFullPath(args[0]);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var path = Path.Combine(gameData, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};

var mod = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[1]));
RuntimeHelpers.RunClassConstructor(mod.GetType("RunCheckpoints.RewindService", throwOnError: true)!.TypeHandle);
var harmonyAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(gameData, "0Harmony.dll"));
var harmonyType = harmonyAssembly.GetType("HarmonyLib.Harmony", throwOnError: true)!;
var harmony = Activator.CreateInstance(harmonyType, "run-checkpoints.compatibility")!;
harmonyType.GetMethod("PatchAll", [typeof(Assembly)])!.Invoke(harmony, [mod]);

var game = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(gameData, "sts2.dll"));
var store = game.GetType("MegaCrit.Sts2.Core.Saves.GodotFileIo", throwOnError: true)!;
foreach (var name in new[] { "WriteFile", "WriteFileAsync" })
{
    var target = store.GetMethod(name, [typeof(string), typeof(byte[])])!;
    if (harmonyType.GetMethod("GetPatchInfo")!.Invoke(null, [target]) == null)
    {
        throw new InvalidOperationException($"Harmony did not patch GodotFileIo.{name}.");
    }
}
foreach (var (typeName, methodName) in new[]
{
    ("MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu.NPauseMenu", "Initialize"),
    ("MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen.NGameOverScreen", "_Ready"),
    ("MegaCrit.Sts2.Core.Nodes.NGame", "_Input"),
    ("MegaCrit.Sts2.Core.Saves.SaveManager", "CleanupStaleCurrentRunSaveForProfile"),
    ("MegaCrit.Sts2.Core.Saves.SaveManager", "DeleteCurrentRun")
})
{
    var target = game.GetType(typeName, throwOnError: true)!
        .GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
    if (harmonyType.GetMethod("GetPatchInfo")!.Invoke(null, [target]) == null)
    {
        throw new InvalidOperationException($"Harmony did not patch {typeName}.{methodName}.");
    }
}
Console.WriteLine("PASS: Save-write, pause/death UI, shortcut, and restored-save cleanup patches bind to the installed game.");
