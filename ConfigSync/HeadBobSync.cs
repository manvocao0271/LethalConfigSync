using System;

namespace LethalConfigSync.ConfigSync;

/// <summary>
/// Syncs the game's own "head bobbing" setting (the one in the in-game settings menu) from the host.
///
/// It is separate from SyncedEntries because it is not a BepInEx config entry. The host sends one of three
/// states along with its rules: 0 = not syncing, 1 = off, 2 = on. A client applies it to the game's live
/// settings only. Nothing is saved, so the player's own saved setting is untouched, and their own value is
/// put back when the host stops syncing it or the player leaves the lobby.
/// </summary>
internal static class HeadBobSync
{
    private const byte NotSynced = 0;
    private const byte Off = 1;
    private const byte On = 2;

    private static bool? _original;
    private static bool _lastApplied;

    /// <summary>Host side: what to tell clients about head bobbing.</summary>
    internal static byte HostState()
    {
        if (!SyncSettings.SyncHeadBobbing)
            return NotSynced;

        try
        {
            IngamePlayerSettings? game = IngamePlayerSettings.Instance;
            if (game == null || game.settings == null)
            {
                Plugin.Logger.LogWarning("SyncHeadBobbing is on, but the game's settings are not available, so head bobbing was not synced.");
                return NotSynced;
            }

            return game.settings.headBobbing ? On : Off;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"Could not read the game's head bobbing setting: {ex.Message}");
            return NotSynced;
        }
    }

    /// <summary>Client side: applies what the host sent.</summary>
    internal static void Apply(byte state)
    {
        if (state == NotSynced)
        {
            Restore();
            return;
        }

        try
        {
            IngamePlayerSettings? game = IngamePlayerSettings.Instance;
            if (game == null || game.settings == null)
            {
                Plugin.Logger.LogWarning("The host syncs head bobbing, but the game's settings are not available yet.");
                return;
            }

            bool wanted = state == On;
            bool current = game.settings.headBobbing;

            // Remember the player's own value the first time, and again if they changed it themselves since.
            if (_original == null || current != _lastApplied)
                _original = current;

            if (current != wanted)
            {
                game.settings.headBobbing = wanted;
                Plugin.Logger.LogInfo($"Head bobbing set by the host: {(wanted ? "on" : "off")} (yours was {(current ? "on" : "off")}).");
            }

            _lastApplied = wanted;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"Could not apply the host's head bobbing setting: {ex.Message}");
        }
    }

    /// <summary>Puts the player's own value back, unless they changed it themselves meanwhile. Safe any time.</summary>
    internal static void Restore()
    {
        if (_original == null)
            return;

        try
        {
            IngamePlayerSettings? game = IngamePlayerSettings.Instance;
            if (game != null && game.settings != null && game.settings.headBobbing == _lastApplied)
                game.settings.headBobbing = _original.Value;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"Could not restore your head bobbing setting: {ex.Message}");
        }
        finally
        {
            _original = null;
        }
    }
}