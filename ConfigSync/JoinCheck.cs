using System;
using System.Globalization;
using System.Text;
using Unity.Netcode;

namespace LethalConfigSync.ConfigSync;

/// <summary>
/// "Everyone needs LethalConfigSync", enforced by the host itself.
///
/// A joining player's game sends a connection payload: "gameVersion" on LAN, "gameVersion,steamId" on
/// Steam. The game only reads the first field, and the second one only on Steam, so a trailing field is
/// ignored by the game and by hosts that do not run this mod. Every client with LethalConfigSync appends
/// "LethalConfigSync/&lt;protocol&gt;". A host that has LethalConfigSync refuses anyone whose payload lacks the
/// marker, or carries a different protocol, and the player sees the reason in the main menu.
/// </summary>
internal static class JoinCheck
{
    private const string MarkerPrefix = "LethalConfigSync/";

    /// <summary>Client side: add our marker to the connection data the game has just built.</summary>
    internal static void AppendMarker()
    {
        try
        {
            NetworkManager? nm = NetworkManager.Singleton;
            if (nm == null)
                return;

            string payload = Encoding.ASCII.GetString(nm.NetworkConfig.ConnectionData ?? Array.Empty<byte>());
            if (payload.Contains(MarkerPrefix))
                return;

            nm.NetworkConfig.ConnectionData = Encoding.ASCII.GetBytes(payload + "," + MarkerPrefix + ConfigSyncNetwork.ProtocolVersion);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"Could not add the LethalConfigSync marker to the connection data: {ex}");
        }
    }

    /// <summary>
    /// Host side, after the game's own approval logic has run. Only ever turns an approval into a refusal.
    /// If anything here throws, the connection is left as the game decided: a bug must not lock players out.
    /// </summary>
    internal static void Enforce(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
    {
        try
        {
            NetworkManager? nm = NetworkManager.Singleton;
            if (nm == null || request.ClientNetworkId == nm.LocalClientId)
                return; // the host's own connection

            if (!response.Approved)
                return; // the game already refused this player

            string reason = CheckPayload(request.Payload);
            if (reason.Length == 0)
                return;

            response.Approved = false;
            response.Reason = reason;
            Plugin.Logger.LogWarning($"Refused client {request.ClientNetworkId}: {reason}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"Join check failed, leaving the connection as the game decided: {ex}");
        }
    }

    /// <summary>An empty string when the payload is fine, otherwise the reason to show the player.</summary>
    private static string CheckPayload(byte[]? payloadBytes)
    {
        string payload = Encoding.ASCII.GetString(payloadBytes ?? Array.Empty<byte>());
        foreach (string field in payload.Split(','))
        {
            if (!field.StartsWith(MarkerPrefix, StringComparison.Ordinal))
                continue;

            bool parsed = int.TryParse(field.Substring(MarkerPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int theirProtocol);
            if (parsed && theirProtocol == ConfigSyncNetwork.ProtocolVersion)
                return string.Empty;

            return $"LethalConfigSync version mismatch. The host has {MyPluginInfo.PLUGIN_VERSION}; update yours to match.";
        }

        return $"This lobby requires the LethalConfigSync mod (version {MyPluginInfo.PLUGIN_VERSION}).";
    }
}
