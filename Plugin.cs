using BepInEx;
using BepInEx.Logging;
using LethalConfigSync.ConfigSync;
using HarmonyLib;

namespace LethalConfigSync;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
// LethalConfig's list of settings is what gets synced, so it has to be loaded first.
[BepInDependency(LethalConfigSource.PluginGuid, BepInDependency.DependencyFlags.HardDependency)]
public class Plugin : BaseUnityPlugin
{
    public static Plugin Instance { get; private set; } = null!;
    internal new static ManualLogSource Logger { get; private set; } = null!;
    internal static Harmony? Harmony { get; set; }

    private void Awake()
    {
        Logger = base.Logger;
        Instance = this;

        SyncSettings.Init(Config);
        if (!SyncSettings.HasRules)
            Logger.LogInfo("SyncedEntries is empty, so as a host LethalConfigSync will not sync anything. Set it in the config to choose what to sync.");

        Patch();

        Logger.LogInfo($"{MyPluginInfo.PLUGIN_GUID} v{MyPluginInfo.PLUGIN_VERSION} [BETA] has loaded!");
    }

    internal static void Patch()
    {
        Harmony ??= new Harmony(MyPluginInfo.PLUGIN_GUID);

        Logger.LogDebug("Patching...");

        Harmony.PatchAll();

        Logger.LogDebug("Finished patching!");
    }

    internal static void Unpatch()
    {
        Logger.LogDebug("Unpatching...");

        Harmony?.UnpatchSelf();

        Logger.LogDebug("Finished unpatching!");
    }
}
