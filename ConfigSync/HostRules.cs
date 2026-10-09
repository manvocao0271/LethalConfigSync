using BepInEx.Configuration;

namespace LethalConfigSync.ConfigSync;

/// <summary>
/// Client side: shows the host's <c>SyncedEntries</c> in this player's own setting, read-only.
///
/// The host's list is written into the setting in memory only. SaveOnConfigSet is switched off for as long
/// as it is shown, so it never reaches the player's config file. The player's own value is put back when
/// they leave the lobby. LethalConfig's control for the setting is locked while it is shown.
///
/// What a client actually applies does not depend on this: it applies what the host sends. This only makes
/// the host's choice visible and keeps the player from editing a value that has no effect on them.
/// </summary>
internal static class HostRules
{
    private static string _localValue = string.Empty;
    private static bool _originalSaveFlag;
    private static bool _lockInstalled;

    /// <summary>True while a host's rules are being shown.</summary>
    internal static bool IsActive { get; private set; }

    internal static void Apply(string hostValue)
    {
        ConfigEntry<string> entry = SyncSettings.SyncedEntriesEntry;
        ConfigFile file = entry.ConfigFile;

        if (!IsActive)
        {
            _localValue = entry.Value ?? string.Empty;
            _originalSaveFlag = file.SaveOnConfigSet;
            file.SaveOnConfigSet = false;
            IsActive = true;
        }

        entry.Value = hostValue;

        if (!_lockInstalled)
            _lockInstalled = LethalConfigSource.TryLock(entry, "Set by the host.", () => IsActive);
    }

    /// <summary>Puts the player's own value back. Safe to call at any time.</summary>
    internal static void Clear()
    {
        if (!IsActive)
            return;

        ConfigEntry<string> entry = SyncSettings.SyncedEntriesEntry;
        entry.Value = _localValue;
        entry.ConfigFile.SaveOnConfigSet = _originalSaveFlag;
        IsActive = false;
    }
}
