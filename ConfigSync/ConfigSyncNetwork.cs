using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Unity.Collections;
using Unity.Netcode;

namespace LethalConfigSync.ConfigSync;

/// <summary>
/// Host -> client config sync over Netcode named messages (no prefab or NetworkObject to register).
///
/// When a client joins, the host sends two kinds of message:
///
///   Rules (always, even when empty): the host's SyncedEntries list, so the client can show it, plus the
///   state of the game's head bobbing setting (0 = not synced, 1 = off, 2 = on; see HeadBobSync).
///     int version, string rules, byte head bobbing state
///
///   Config (only when the list selects something): the selected settings, one mod at a time, split into
///   small chunks so each message stays well under Unity Transport's default payload limit (LAN lobbies).
///     int version, string mod GUID, int chunk index, int chunk count, int entry count,
///     entry count x { string section, string key, string serialized value }
///
/// A client applies exactly what the host sends. Delivery is reliable and sequenced and the host sends one
/// mod after another, so a mod's chunks arrive in order and the client can save each touched config file
/// once, after that mod's last chunk.
/// </summary>
internal static class ConfigSyncNetwork
{
    private const string ConfigMessageName = "bobabulkerENTERPRISE.LethalConfigSync.ConfigSync";
    private const string RulesMessageName = "bobabulkerENTERPRISE.LethalConfigSync.Rules";
    internal const int ProtocolVersion = 2;
    private const int MaxEntriesPerChunk = 4096;
    private const int ChunkBudgetBytes = 4096;

    private sealed class PendingMod
    {
        public PendingMod(string guid) => Guid = guid;

        public string Guid { get; }
        public bool Found { get; set; }
        public int Changed { get; set; }
        public int Rejected { get; set; }
        public HashSet<ConfigFile> ChangedFiles { get; } = new();
    }

    private static PendingMod? _pending;
    private static bool _warnedProtocol;

    /// <summary>Forgets any half-received mod. Called when a session ends.</summary>
    internal static void ResetSession() => _pending = null;

    // ---------------------------------------------------------------- host

    /// <summary>Host only: tells one joining client the rules, then sends the settings those rules select.</summary>
    internal static void SendToClient(ulong clientId)
    {
        NetworkManager? nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null)
            return;

        // The host's own connect callback also lands here; there is nobody to send to.
        if (clientId == nm.LocalClientId)
            return;

        try
        {
            SendRules(nm, clientId);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"Failed to send the rules to client {clientId}: {ex}");
        }

        if (!SyncSettings.HasRules)
        {
            Plugin.Logger.LogDebug($"SyncedEntries is empty, so client {clientId} has nothing to sync.");
            return;
        }

        int modCount = 0;
        int entryCount = 0;

        foreach (ModConfig mod in LethalConfigSource.GetAll())
        {
            try
            {
                int sent = SendMod(nm, mod, clientId);
                if (sent > 0)
                {
                    modCount++;
                    entryCount += sent;
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Failed to send {mod.Guid} config to client {clientId}: {ex}");
            }
        }

        if (entryCount == 0)
            Plugin.Logger.LogWarning("SyncedEntries matched no settings in LethalConfig, so nothing was sent. Check the mod GUIDs, sections and keys.");
        else
            Plugin.Logger.LogInfo($"Sent {entryCount} settings from {modCount} mods to client {clientId}.");
    }

    /// <summary>
    /// Host only: sends the rules and the selected settings again to every player already in the lobby.
    /// This is what the SyncNow setting does. Players who join later are still synced on join.
    /// </summary>
    internal static void SendToAllClients()
    {
        NetworkManager? nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer)
        {
            Plugin.Logger.LogInfo("SyncNow only works for the host, in a lobby. Nothing was sent.");
            return;
        }

        var clients = new List<ulong>(nm.ConnectedClientsIds);
        int sent = 0;
        foreach (ulong clientId in clients)
        {
            if (clientId == nm.LocalClientId)
                continue;

            SendToClient(clientId);
            sent++;
        }

        if (sent == 0)
            Plugin.Logger.LogInfo("SyncNow: there are no other players in the lobby.");
    }

    private static void SendRules(NetworkManager nm, ulong clientId)
    {
        string rules = SyncSettings.SyncedEntries;
        byte headBob = HeadBobSync.HostState();
        int size = sizeof(int) + FastBufferWriter.GetWriteSize(rules) + sizeof(byte);

        using var writer = new FastBufferWriter(size, Allocator.Temp);
        int version = ProtocolVersion;
        writer.WriteValueSafe(version);
        writer.WriteValueSafe(rules);
        writer.WriteValueSafe(headBob);

        nm.CustomMessagingManager.SendNamedMessage(RulesMessageName, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
    }

    private static int SendMod(NetworkManager nm, ModConfig mod, ulong clientId)
    {
        var items = new List<(string Section, string Key, string Value)>();
        foreach (ConfigEntryBase entry in mod.Entries)
        {
            if (!SyncSettings.IsSelected(mod.Guid, entry))
                continue;

            items.Add((entry.Definition.Section, entry.Definition.Key, entry.GetSerializedValue()));
        }

        if (items.Count == 0)
            return 0;

        // Split into chunks that each fit the byte budget (a single oversized item still gets its own chunk).
        int headerSize = sizeof(int) * 4 + FastBufferWriter.GetWriteSize(mod.Guid);
        var chunks = new List<(int Start, int End, int Size)>();
        int start = 0;
        while (start < items.Count)
        {
            int size = headerSize;
            int end = start;
            while (end < items.Count && end - start < MaxEntriesPerChunk)
            {
                int itemSize = ItemSize(items[end]);
                if (end > start && size + itemSize > ChunkBudgetBytes)
                    break;

                size += itemSize;
                end++;
            }

            chunks.Add((start, end, size));
            start = end;
        }

        for (int i = 0; i < chunks.Count; i++)
            WriteChunk(nm, clientId, mod.Guid, items, chunks[i].Start, chunks[i].End, chunks[i].Size, i, chunks.Count);

        return items.Count;
    }

    private static int ItemSize((string Section, string Key, string Value) item) =>
        FastBufferWriter.GetWriteSize(item.Section)
        + FastBufferWriter.GetWriteSize(item.Key)
        + FastBufferWriter.GetWriteSize(item.Value);

    private static void WriteChunk(
        NetworkManager nm, ulong clientId, string guid,
        List<(string Section, string Key, string Value)> items,
        int start, int end, int size, int chunkIndex, int chunkCount)
    {
        using var writer = new FastBufferWriter(size, Allocator.Temp);

        int version = ProtocolVersion;
        int count = end - start;
        writer.WriteValueSafe(version);
        writer.WriteValueSafe(guid);
        writer.WriteValueSafe(chunkIndex);
        writer.WriteValueSafe(chunkCount);
        writer.WriteValueSafe(count);
        for (int i = start; i < end; i++)
        {
            writer.WriteValueSafe(items[i].Section);
            writer.WriteValueSafe(items[i].Key);
            writer.WriteValueSafe(items[i].Value);
        }

        nm.CustomMessagingManager.SendNamedMessage(ConfigMessageName, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
    }

    // -------------------------------------------------------------- client

    /// <summary>
    /// Netcode creates a fresh CustomMessagingManager on every NetworkManager.Initialize, so the
    /// handlers must be registered again for every session.
    /// </summary>
    internal static void RegisterHandler(NetworkManager nm)
    {
        nm.CustomMessagingManager?.RegisterNamedMessageHandler(RulesMessageName, OnRulesMessage);
        nm.CustomMessagingManager?.RegisterNamedMessageHandler(ConfigMessageName, OnConfigMessage);
    }

    private static bool VersionMatches(int version)
    {
        if (version == ProtocolVersion)
            return true;

        if (!_warnedProtocol)
        {
            _warnedProtocol = true;
            Plugin.Logger.LogWarning($"Host sent config with protocol {version}; this client speaks {ProtocolVersion}. Update LethalConfigSync so you and the host match.");
        }

        return false;
    }

    private static void OnRulesMessage(ulong senderClientId, FastBufferReader reader)
    {
        // Only the host is allowed to push anything.
        if (senderClientId != NetworkManager.ServerClientId)
            return;

        try
        {
            reader.ReadValueSafe(out int version);
            if (!VersionMatches(version))
                return;

            reader.ReadValueSafe(out string rules);
            reader.ReadValueSafe(out byte headBob);
            HostRules.Apply(rules);
            HeadBobSync.Apply(headBob);

            Plugin.Logger.LogInfo(rules.Length == 0
                ? "The host syncs nothing."
                : $"The host syncs: {rules}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"Failed to process the host's rules: {ex}");
        }
    }

    private static void OnConfigMessage(ulong senderClientId, FastBufferReader reader)
    {
        if (senderClientId != NetworkManager.ServerClientId)
            return;

        try
        {
            reader.ReadValueSafe(out int version);
            if (!VersionMatches(version))
                return;

            reader.ReadValueSafe(out string modGuid);
            reader.ReadValueSafe(out int chunkIndex);
            reader.ReadValueSafe(out int chunkCount);
            reader.ReadValueSafe(out int count);
            if (count < 0 || count > MaxEntriesPerChunk || chunkCount < 1 || chunkIndex < 0 || chunkIndex >= chunkCount)
            {
                Plugin.Logger.LogWarning($"Ignoring malformed config chunk for '{modGuid}'.");
                return;
            }

            var incoming = new Dictionary<(string Section, string Key), string>(count);
            for (int i = 0; i < count; i++)
            {
                reader.ReadValueSafe(out string section);
                reader.ReadValueSafe(out string key);
                reader.ReadValueSafe(out string value);
                incoming[(section, key)] = value;
            }

            ApplyChunk(modGuid, incoming, chunkIndex, chunkCount);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"Failed to process host config sync: {ex}");
        }
    }

    /// <summary>
    /// Overwrites this client's values with the host's. ConfigFile.SaveOnConfigSet is switched off while a
    /// chunk is applied so nothing hits the disk per setting, and every touched file is saved once, after
    /// the mod's last chunk. Host values replace the client's own config on disk.
    /// </summary>
    private static void ApplyChunk(string modGuid, Dictionary<(string Section, string Key), string> incoming, int chunkIndex, int chunkCount)
    {
        // The host never gets to rewrite LethalConfigSync's own settings or LethalConfig's UI preferences.
        if (SyncSettings.IsProtectedMod(modGuid))
        {
            Plugin.Logger.LogDebug($"Ignoring config for protected mod '{modGuid}'.");
            return;
        }

        if (chunkIndex == 0 || _pending == null || !string.Equals(_pending.Guid, modGuid, StringComparison.OrdinalIgnoreCase))
            _pending = new PendingMod(modGuid);

        PendingMod pending = _pending;
        List<ConfigEntryBase> entries = LethalConfigSource.GetEntries(modGuid);
        if (entries.Count > 0)
            pending.Found = true;

        var originalSaveFlags = new Dictionary<ConfigFile, bool>();
        try
        {
            foreach (ConfigEntryBase entry in entries)
            {
                if (!incoming.TryGetValue((entry.Definition.Section, entry.Definition.Key), out string? value))
                    continue;

                // One setting that fails (another mod's SettingChanged handler throwing, for example) must
                // not stop the rest of the chunk, or the files from being saved afterwards.
                try
                {
                    ConfigFile file = entry.ConfigFile;
                    if (!originalSaveFlags.ContainsKey(file))
                    {
                        originalSaveFlags[file] = file.SaveOnConfigSet;
                        file.SaveOnConfigSet = false;
                    }

                    if (entry.GetSerializedValue() == value)
                        continue; // already identical; avoid a pointless SettingChanged

                    // Raises SettingChanged, which is how mods notice a changed value. LethalConfig's own
                    // sliders subscribe to it too.
                    entry.SetSerializedValue(value);

                    if (entry.GetSerializedValue() == value)
                    {
                        pending.Changed++;
                        pending.ChangedFiles.Add(file);
                    }
                    else
                    {
                        pending.Rejected++; // unparseable, or clamped by the entry's acceptable range
                        Plugin.Logger.LogDebug($"[{modGuid}] host value for [{entry.Definition.Section}] {entry.Definition.Key} was not accepted as-is: '{value}'.");
                    }
                }
                catch (Exception ex)
                {
                    // The value may have been stored before the failure; if so, it still needs saving.
                    bool applied = false;
                    try { applied = entry.GetSerializedValue() == value; } catch (Exception) { }

                    if (applied)
                    {
                        pending.Changed++;
                        pending.ChangedFiles.Add(entry.ConfigFile);
                    }
                    else
                    {
                        pending.Rejected++;
                    }

                    Plugin.Logger.LogWarning($"[{modGuid}] error while applying [{entry.Definition.Section}] {entry.Definition.Key}: {ex.Message}");
                }
            }
        }
        finally
        {
            foreach (KeyValuePair<ConfigFile, bool> pair in originalSaveFlags)
                pair.Key.SaveOnConfigSet = pair.Value;
        }

        if (chunkIndex == chunkCount - 1)
            Finish(pending);
    }

    private static void Finish(PendingMod pending)
    {
        foreach (ConfigFile file in pending.ChangedFiles)
        {
            try
            {
                file.Save();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Could not save {file.ConfigFilePath}: {ex.Message}");
            }
        }

        if (!pending.Found)
            Plugin.Logger.LogDebug($"[{pending.Guid}] is not listed in LethalConfig on this client; skipped.");
        else if (pending.Changed > 0 || pending.Rejected > 0)
            Plugin.Logger.LogInfo($"[{pending.Guid}] synced with host: {pending.Changed} changed, {pending.Rejected} rejected.");
        else
            Plugin.Logger.LogDebug($"[{pending.Guid}] already matches the host.");

        _pending = null;
    }
}