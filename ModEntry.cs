using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace RunCheckpoints;

[ModInitializer(nameof(Initialize))]
public static class ModEntry
{
    public static void Initialize()
    {
        RuntimeHelpers.RunClassConstructor(typeof(RewindService).TypeHandle);
        new Harmony("james.sts2.run-checkpoints").PatchAll(typeof(ModEntry).Assembly);
    }
}
