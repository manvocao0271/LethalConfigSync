using LethalConfigSync.ConfigSync;
using HarmonyLib;
using Unity.Netcode;

namespace LethalConfigSync.Patches;

[HarmonyPatch]
internal static class NetworkPatches
{
    /// <summary>
    /// NetworkManager.Initialize(bool server) builds a new CustomMessagingManager for every
    /// StartClient / StartHost / StartServer, so this is the earliest point a handler can be registered.
    /// The host never receives config, so only the client path (server == false) registers. A new session
    /// always starts from the player's own rules.
    /// </summary>
    [HarmonyPatch(typeof(NetworkManager), "Initialize", new[] { typeof(bool) })]
    [HarmonyPostfix]
    private static void RegisterHandlerPostfix(NetworkManager __instance, bool server)
    {
        HostRules.Clear();
        HeadBobSync.Restore();
        ConfigSyncNetwork.ResetSession();

        if (server)
            return;

        ConfigSyncNetwork.RegisterHandler(__instance);
    }

    /// <summary>
    /// The game calls NetworkManager.Shutdown when a player leaves a lobby. Put the player's own rules back
    /// (the host's were only ever shown in memory) and drop any half-received mod.
    /// </summary>
    [HarmonyPatch(typeof(NetworkManager), nameof(NetworkManager.Shutdown))]
    [HarmonyPostfix]
    private static void ShutdownPostfix()
    {
        HostRules.Clear();
        HeadBobSync.Restore();
        ConfigSyncNetwork.ResetSession();
    }

    /// <summary>
    /// Host only (the original returns early on non-servers, and SendToClient re-checks). Runs once for
    /// every client that connects, which is what gives "re-sync on every join", late joiners included.
    /// </summary>
    [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.OnClientConnect))]
    [HarmonyPostfix]
    private static void SendConfigPostfix(ulong clientId)
    {
        ConfigSyncNetwork.SendToClient(clientId);
    }

    /// <summary>
    /// Client side. SetConnectionDataBeforeConnecting is the only place the game builds the connection
    /// payload, and it runs right before the connection starts. Add our marker to it.
    /// </summary>
    [HarmonyPatch(typeof(GameNetworkManager), nameof(GameNetworkManager.SetConnectionDataBeforeConnecting))]
    [HarmonyPostfix]
    private static void AppendMarkerPostfix()
    {
        JoinCheck.AppendMarker();
    }

    /// <summary>
    /// Host side. Runs after the game's own checks (version, lobby full, kicked) and after other mods that
    /// patch it, so it only ever turns an approval into a refusal.
    /// </summary>
    [HarmonyPatch(typeof(GameNetworkManager), "ConnectionApproval")]
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void ConnectionApprovalPostfix(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
    {
        JoinCheck.Enforce(request, response);
    }
}