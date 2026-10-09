using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using BepInEx.Configuration;

namespace LethalConfigSync.ConfigSync;

/// <summary>
/// LethalConfigSync's one setting, and the rule that decides what the host syncs.
///
/// It is an allowlist: only settings that match <c>SyncedEntries</c> are synced, and the default is empty,
/// which syncs nothing. It is the host's list that counts. The host sends it to every client (see
/// <see cref="HostRules"/>), and a client applies exactly what the host sends, so nothing here is a
/// per-player choice. The setting lives in a normal BepInEx config file and travels with a profile code.
/// </summary>
internal static class SyncSettings
{
    /// <summary>Never synced, whatever the list says: our own settings and LethalConfig's UI preferences.</summary>
    private static readonly HashSet<string> ProtectedMods = new(StringComparer.OrdinalIgnoreCase)
    {
        MyPluginInfo.PLUGIN_GUID,
        LethalConfigSource.PluginGuid,
    };

    private static ConfigEntry<string> _syncedEntries = null!;
    private static ConfigEntry<bool> _syncNow = null!;

    private static string? _compiledFrom;
    private static List<EntryPattern> _patterns = new();

    internal static ConfigEntry<string> SyncedEntriesEntry => _syncedEntries;

    internal static string SyncedEntries => _syncedEntries.Value ?? string.Empty;

    /// <summary>False when the list is empty (or holds no usable pattern): the host syncs nothing.</summary>
    internal static bool HasRules => GetPatterns().Count > 0;

    internal static void Init(ConfigFile config)
    {
        _syncedEntries = config.Bind(
            "Sync", "SyncedEntries", "",
            "Mods and settings the host syncs to players who join. Empty syncs nothing. " +
            "Separate entries with ';'. Use a mod GUID, or 'GUID|section|setting' for a single setting. " +
            "Example: com.seeya.cameraoverhaul; LethalHUD; MrHat.EladsLethalHUD; ShaosilGaming.GeneralImprovements|Mechanics|AddHealthRechargeStation");

        _syncNow = config.Bind(
            "Sync", "SyncNow", false,
            "Host only. Check this and apply it to send your current settings to the players already in the lobby. It unchecks itself afterwards. Players who join later are synced automatically.");

        // A leftover 'true' (a crash between checking and unchecking) must not stay checked.
        if (_syncNow.Value)
            _syncNow.Value = false;

        _syncNow.SettingChanged += OnSyncNowChanged;
    }

    private static void OnSyncNowChanged(object sender, EventArgs e)
    {
        if (!_syncNow.Value)
            return;

        try
        {
            ConfigSyncNetwork.SendToAllClients();
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"SyncNow failed: {ex}");
        }
        finally
        {
            _syncNow.Value = false;
        }
    }

    internal static bool IsProtectedMod(string modGuid) => ProtectedMods.Contains(modGuid);

    /// <summary>Host side: true if this setting is on the allowlist.</summary>
    internal static bool IsSelected(string modGuid, ConfigEntryBase entry)
    {
        if (IsProtectedMod(modGuid))
            return false;

        string section = entry.Definition.Section;
        string key = entry.Definition.Key;
        foreach (EntryPattern pattern in GetPatterns())
        {
            if (pattern.Guid.IsMatch(modGuid) && pattern.Section.IsMatch(section) && pattern.Key.IsMatch(key))
                return true;
        }

        return false;
    }

    private sealed class EntryPattern
    {
        public EntryPattern(Regex guid, Regex section, Regex key)
        {
            Guid = guid;
            Section = section;
            Key = key;
        }

        public Regex Guid { get; }
        public Regex Section { get; }
        public Regex Key { get; }
    }

    private static List<EntryPattern> GetPatterns()
    {
        string raw = SyncedEntries;
        if (string.Equals(raw, _compiledFrom, StringComparison.Ordinal))
            return _patterns;

        var compiled = new List<EntryPattern>();
        foreach (string part in raw.Split(';'))
        {
            string text = part.Trim();
            if (text.Length == 0)
                continue;

            string[] fields = text.Split('|');
            if (fields.Length > 3)
            {
                Plugin.Logger.LogWarning($"Ignoring SyncedEntries pattern '{text}': use at most 'guid|section|key'.");
                continue;
            }

            string guid = Field(fields, 0);
            string section = Field(fields, 1);
            string key = Field(fields, 2);
            compiled.Add(new EntryPattern(Wildcard(guid), Wildcard(section), Wildcard(key)));
        }

        _patterns = compiled;
        _compiledFrom = raw;
        return _patterns;
    }

    // Parts are read left to right; a missing or empty part matches everything.
    private static string Field(string[] fields, int index)
    {
        if (index >= fields.Length)
            return "*";

        string value = fields[index].Trim();
        return value.Length == 0 ? "*" : value;
    }

    private static Regex Wildcard(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
}