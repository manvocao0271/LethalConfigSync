using BepInEx.Configuration;

namespace LethalConfigSync.ConfigSync;

/// <summary>
/// Client side: shows the host's <c>SyncedEntries</c> in a separate, display-only setting (HostSyncedEntries,
/// in the "Host" section). The player's own <c>SyncedEntries</c> is never touched, so there is nothing to
/// restore when they leave and nothing of the host's can end up in it.
///
/// The display setting is never read by anything. It is cleared when the lobby ends and again when the game
/// starts, so a stale value left in the file by a crash is harmless. LethalConfig's control for it is locked
/// when possible, but that is only for show: editing it would change nothing.
///
/// What a client actually applies does not depend on this: it applies what the host sends.
/// </summary>
internal static class HostRules
{
    private const string NothingText = "(the host syncs nothing)";

    private static bool _lockInstalled;

    /// <summary>True while a host's rules are being shown.</summary>
    internal static bool IsActive { get; private set; }

    internal static void Apply(string hostValue)
    {
        ConfigEntry<string> entry = SyncSettings.HostSyncedEntriesEntry;
        entry.Value = string.IsNullOrWhiteSpace(hostValue) ? NothingText : hostValue;
        IsActive = true;

        if (!_lockInstalled)
            _lockInstalled = LethalConfigSource.TryLock(entry, "Display only. This is the host's list.", () => true);
    }

    /// <summary>Empties the display setting. Safe to call at any time.</summary>
    internal static void Clear()
    {
        if (!IsActive)
            return;

        SyncSettings.HostSyncedEntriesEntry.Value = string.Empty;
        IsActive = false;
    }
}