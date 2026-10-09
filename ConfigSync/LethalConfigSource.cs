using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;

namespace LethalConfigSync.ConfigSync;

/// <summary>One mod's settings, exactly as LethalConfig lists them.</summary>
internal sealed class ModConfig
{
    public ModConfig(string guid, IReadOnlyList<ConfigEntryBase> entries)
    {
        Guid = guid;
        Entries = entries;
    }

    /// <summary>The mod's BepInEx plugin GUID.</summary>
    public string Guid { get; }

    public IReadOnlyList<ConfigEntryBase> Entries { get; }
}

/// <summary>
/// Reads "everything LethalConfig sees" through reflection, so there is no compile-time dependency on
/// LethalConfig and no per-mod code here.
///
/// LethalConfig keeps every mod in the internal static dictionary <c>LethalConfigManager.Mods</c>. Each
/// <c>Mod</c> has a public <c>ConfigItems</c> list, and each item points at its real BepInEx
/// <see cref="ConfigEntryBase"/>. That covers mods that registered items explicitly (Camera_Overhaul does,
/// with its own private ConfigFile) as well as mods LethalConfig auto-generated items for (int, float, bool,
/// string and enum entries of every plugin's normal Config).
/// </summary>
internal static class LethalConfigSource
{
    internal const string PluginGuid = "ainavt.lc.lethalconfig";

    private static bool _resolved;
    private static bool _available;
    private static bool _prepared;

    private static PropertyInfo? _modsProperty;     // LethalConfigManager.Mods            (internal static)
    private static MethodInfo? _generateMissing;    // LethalConfigManager.AutoGenerateMissingConfigsIfNeeded()
    private static MethodInfo? _runLate;            // LethalConfigManager.RunLateAutoGeneration()
    private static FieldInfo? _configItemsField;    // Mod.ConfigItems                     (public)
    private static FieldInfo? _modInfoField;        // Mod.ModInfo                         (public)
    private static PropertyInfo? _guidProperty;     // ModInfo.Guid                        (internal)
    private static PropertyInfo? _entryProperty;    // BaseConfigItem.BaseConfigEntry      (internal)
    private static PropertyInfo? _optionsProperty;  // BaseConfigItem.Options              (internal)

    /// <summary>Every mod LethalConfig lists, with its entries. Used by the host.</summary>
    internal static List<ModConfig> GetAll()
    {
        var result = new List<ModConfig>();
        foreach ((string guid, object mod) in ListMods())
        {
            List<ConfigEntryBase> entries = ReadEntries(mod);
            if (entries.Count > 0)
                result.Add(new ModConfig(guid, entries));
        }

        return result;
    }

    /// <summary>The entries LethalConfig lists for one mod. Empty if the mod isn't listed. Used by clients.</summary>
    internal static List<ConfigEntryBase> GetEntries(string guid)
    {
        var entries = new List<ConfigEntryBase>();
        foreach ((string modGuid, object mod) in ListMods())
        {
            if (string.Equals(modGuid, guid, StringComparison.OrdinalIgnoreCase))
                entries.AddRange(ReadEntries(mod));
        }

        return entries;
    }

    /// <summary>
    /// Locks the LethalConfig control for one of our own settings: while <paramref name="isLocked"/> returns
    /// true it cannot be changed, and the player sees <paramref name="reason"/>. Best effort: returns false if
    /// the control is not there yet or LethalConfig's internals are not what we expect.
    /// </summary>
    internal static bool TryLock(ConfigEntryBase target, string reason, Func<bool> isLocked)
    {
        if (!Resolve() || _optionsProperty == null)
            return false;

        bool locked = false;
        try
        {
            foreach ((string guid, object mod) in ListMods())
            {
                if (!string.Equals(guid, MyPluginInfo.PLUGIN_GUID, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (_configItemsField!.GetValue(mod) is not IEnumerable items)
                    continue;

                foreach (object? item in items)
                {
                    if (item == null || !ReferenceEquals(_entryProperty!.GetValue(item), target))
                        continue;

                    object? options = _optionsProperty.GetValue(item);
                    PropertyInfo? callback = options == null ? null : AccessTools.Property(options.GetType(), "CanModifyCallback");
                    Delegate? handler = callback == null ? null : BuildCanModify(callback.PropertyType, reason, isLocked);
                    if (options == null || callback == null || handler == null)
                        continue;

                    callback.SetValue(options, handler);
                    locked = true;
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogDebug($"Could not lock the LethalConfig control: {ex.Message}");
        }

        return locked;
    }

    // LethalConfig's CanModifyDelegate takes no arguments and returns a CanModifyResult, which has
    // static True() and False(string reason). Build   () => isLocked() ? False(reason) : True()   for it.
    private static Delegate? BuildCanModify(Type delegateType, string reason, Func<bool> isLocked)
    {
        MethodInfo? invoke = delegateType.GetMethod("Invoke");
        if (invoke == null || invoke.GetParameters().Length != 0)
            return null;

        Type resultType = invoke.ReturnType;
        MethodInfo? whenLocked = resultType.GetMethod("False", new[] { typeof(string) });
        MethodInfo? whenFree = resultType.GetMethod("True", Type.EmptyTypes);
        if (whenLocked == null || whenFree == null)
            return null;

        Expression body = Expression.Condition(
            Expression.Invoke(Expression.Constant(isLocked)),
            Expression.Call(whenLocked, Expression.Constant(reason)),
            Expression.Call(whenFree));

        return Expression.Lambda(delegateType, body).Compile();
    }

    private static List<(string Guid, object Mod)> ListMods()
    {
        var list = new List<(string Guid, object Mod)>();
        if (!Resolve())
            return list;

        Prepare();

        try
        {
            if (_modsProperty!.GetValue(null) is not IDictionary mods)
                return list;

            foreach (object? mod in mods.Values)
            {
                if (mod == null)
                    continue;

                try
                {
                    object? info = _modInfoField!.GetValue(mod);
                    string? guid = info == null ? null : _guidProperty!.GetValue(info) as string;
                    if (!string.IsNullOrEmpty(guid))
                        list.Add((guid!, mod));
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogDebug($"Skipping a LethalConfig mod entry that could not be read: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"Could not read LethalConfig's mod list: {ex}");
        }

        return list;
    }

    private static List<ConfigEntryBase> ReadEntries(object mod)
    {
        var entries = new List<ConfigEntryBase>();
        if (_configItemsField!.GetValue(mod) is not IEnumerable items)
            return entries;

        foreach (object? item in items)
        {
            if (item == null)
                continue;

            try
            {
                if (_entryProperty!.GetValue(item) is ConfigEntryBase entry)
                    entries.Add(entry);
            }
            catch (Exception)
            {
                // Items with no underlying entry (buttons, for example) are simply skipped.
            }
        }

        return entries;
    }

    /// <summary>
    /// LethalConfig creates items for mods that never registered any lazily, from ConfigMenu.Awake and
    /// StartOfRound.Start. A message can arrive before either has run, so make sure it has happened. Both
    /// methods are idempotent on LethalConfig's side; we call them once.
    /// </summary>
    private static void Prepare()
    {
        if (_prepared)
            return;

        _prepared = true;
        try
        {
            _generateMissing?.Invoke(null, null);
            _runLate?.Invoke(null, null);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogDebug($"LethalConfig auto-generation could not be triggered early: {ex.Message}");
        }
    }

    private static bool Resolve()
    {
        if (_resolved)
            return _available;

        _resolved = true;

        try
        {
            if (!Chainloader.PluginInfos.TryGetValue(PluginGuid, out var info) || info.Instance == null)
            {
                Plugin.Logger.LogWarning("LethalConfig is not loaded; there is nothing to sync.");
                return false;
            }

            Assembly assembly = info.Instance.GetType().Assembly;
            Type? manager = assembly.GetType("LethalConfig.LethalConfigManager");
            Type? mod = assembly.GetType("LethalConfig.Mods.Mod");
            Type? modInfo = assembly.GetType("LethalConfig.Mods.ModInfo");
            Type? item = assembly.GetType("LethalConfig.ConfigItems.BaseConfigItem");

            if (manager == null || mod == null || modInfo == null || item == null)
            {
                Plugin.Logger.LogWarning("LethalConfig's internal layout was not recognised (a different version?); config sync is disabled.");
                return false;
            }

            _modsProperty = AccessTools.Property(manager, "Mods");
            _generateMissing = AccessTools.Method(manager, "AutoGenerateMissingConfigsIfNeeded");
            _runLate = AccessTools.Method(manager, "RunLateAutoGeneration");
            _configItemsField = AccessTools.Field(mod, "ConfigItems");
            _modInfoField = AccessTools.Field(mod, "ModInfo");
            _guidProperty = AccessTools.Property(modInfo, "Guid");
            _entryProperty = AccessTools.Property(item, "BaseConfigEntry");
            _optionsProperty = AccessTools.Property(item, "Options"); // optional: only needed to lock our own setting

            _available = _modsProperty != null && _configItemsField != null && _modInfoField != null
                         && _guidProperty != null && _entryProperty != null;

            if (!_available)
                Plugin.Logger.LogWarning("LethalConfig's internal layout was not recognised (a different version?); config sync is disabled.");

            return _available;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"Could not inspect LethalConfig: {ex}");
            return false;
        }
    }
}
