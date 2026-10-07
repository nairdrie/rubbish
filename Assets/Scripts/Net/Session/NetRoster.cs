using System;
using System.Text;
using Unity.Collections;
using Unity.Netcode;

/// <summary>One connected player as every machine sees them (C1 contract; B1 fills the host side).</summary>
public struct RosterEntry : INetworkSerializable, IEquatable<RosterEntry>
{
    /// <summary><see cref="flags"/> bit: this entry is the host.</summary>
    public const byte FlagHost = 1;

    public ulong clientId;
    public FixedString64Bytes playerKey;
    /// <summary>Live from Steam (or the dev name); never saved.</summary>
    public FixedString32Bytes displayName;
    public byte company, slot, flags;

    public bool IsHost => (flags & FlagHost) != 0;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref clientId);
        serializer.SerializeValue(ref playerKey);
        serializer.SerializeValue(ref displayName);
        serializer.SerializeValue(ref company);
        serializer.SerializeValue(ref slot);
        serializer.SerializeValue(ref flags);
    }

    public bool Equals(RosterEntry o) =>
        clientId == o.clientId && playerKey.Equals(o.playerKey) && displayName.Equals(o.displayName) &&
        company == o.company && slot == o.slot && flags == o.flags;

    public override bool Equals(object obj) => obj is RosterEntry o && Equals(o);
    public override int GetHashCode() => clientId.GetHashCode() ^ (slot << 8) ^ (company << 16);
    public override string ToString() => $"{playerKey} client {clientId} company {company} slot {slot}";
}

/// <summary>
/// The replicated roster. Lives on <c>NetSession.prefab</c> (built by <c>Setup_Coop</c> from
/// <see cref="NetSessionComponentAttribute"/>; the host spawns it when a session starts). The host writes
/// <see cref="Entries"/> from <see cref="SessionService"/>'s seats (B1); everyone reads it (B2's <c>RosterView</c> picks the
/// Dumpling variant from <see cref="RosterEntry.slot"/>).
/// </summary>
[NetSessionComponent(Order = 0)]
public class NetRoster : NetworkBehaviour
{
    public static NetRoster Instance { get; private set; }

    NetworkList<RosterEntry> entries;
    bool listening;

    /// <summary>Host-written, read everywhere.</summary>
    public NetworkList<RosterEntry> Entries => entries;

    /// <summary>The roster changed (any add, remove or edit), or this copy just spawned.</summary>
    public event Action Changed;

    void Awake() => entries = new NetworkList<RosterEntry>();

    public override void OnNetworkSpawn()
    {
        Instance = this;
        entries.OnListChanged += OnListChanged;
        if (IsServer)
        {
            SessionService.PlayerJoined += OnPlayerJoined;
            SessionService.PlayerLeft += OnPlayerLeft;
            NetworkManager.OnClientConnectedCallback += OnClientSynced;
            listening = true;
            Rebuild();
        }
        RaiseChanged();
    }

    public override void OnNetworkDespawn()
    {
        entries.OnListChanged -= OnListChanged;
        StopListening();
        if (Instance == this) Instance = null;
    }

    public override void OnDestroy()
    {
        StopListening();
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    void StopListening()
    {
        if (!listening) return;
        SessionService.PlayerJoined -= OnPlayerJoined;
        SessionService.PlayerLeft -= OnPlayerLeft;
        if (NetworkManager != null) NetworkManager.OnClientConnectedCallback -= OnClientSynced;
        listening = false;
    }

    public bool TryGetByClient(ulong clientId, out RosterEntry e)
    {
        if (entries != null)
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].clientId == clientId) { e = entries[i]; return true; }
        e = default;
        return false;
    }

    /// <summary>One line for logs and bot audits: <c>3 [dev:bot-1@0 host, dev:bot-2@1, dev:bot-3@2]</c>.</summary>
    public string Describe()
    {
        var sb = new StringBuilder(64);
        int n = entries?.Count ?? 0;
        sb.Append(n).Append(" [");
        for (int i = 0; i < n; i++)
        {
            var e = entries[i];
            if (i > 0) sb.Append(", ");
            sb.Append(e.playerKey.ToString()).Append('@').Append(e.slot);
            if (e.IsHost) sb.Append(" host");
        }
        return sb.Append(']').ToString();
    }

    // ---- host side ----
    // A guest's entry is added only once NGO reports them connected, i.e. synchronized. Added at approval time, the entry
    // would reach the joiner twice (in their initial snapshot and again as the pending delta) and their list would be one
    // longer than the host's.

    void Rebuild()
    {
        entries.Clear();
        foreach (var (key, clientId, company, slot) in SessionService.Seated())
            if (Synced(clientId)) entries.Add(Make(key, clientId, company, slot));
        Audit();
    }

    bool Synced(ulong clientId) =>
        clientId == NetworkManager.ServerClientId || (NetworkManager != null && NetworkManager.ConnectedClients.ContainsKey(clientId));

    void OnPlayerJoined(PlayerKey key, ulong clientId, int slot)
    {
        if (!IsServer || entries == null) return;
        RemoveWhere(clientId, key);
        if (clientId != NetworkManager.ServerClientId) { Audit(); return; } // added by OnClientSynced
        entries.Add(Make(key, clientId, SessionService.Slots.TryGet(key, out int company, out _) ? company : 0, slot));
        Audit();
    }

    void OnClientSynced(ulong clientId)
    {
        if (!IsServer || entries == null || clientId == NetworkManager.ServerClientId) return;
        if (!SessionService.TryGetPlayer(clientId, out var key) || !SessionService.Slots.TryGet(key, out int company, out int slot)) return;
        RemoveWhere(clientId, key);
        entries.Add(Make(key, clientId, company, slot));
        Audit();
    }

    void OnPlayerLeft(PlayerKey key, ulong clientId)
    {
        if (!IsServer || entries == null) return;
        RemoveWhere(clientId, default);
        Audit();
    }

    void RemoveWhere(ulong clientId, PlayerKey key)
    {
        string k = key.IsValid ? key.Value : null;
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var e = entries[i];
            if (e.clientId == clientId || (k != null && e.playerKey.ToString() == k)) entries.RemoveAt(i);
        }
    }

    void Audit() => NetAudit.Event("roster", Describe());

    static RosterEntry Make(PlayerKey key, ulong clientId, int company, int slot)
    {
        string name = null;
        try { name = SessionService.DisplayNameFor?.Invoke(key); }
        catch (Exception e) { UnityEngine.Debug.LogException(e); }
        return new RosterEntry
        {
            clientId = clientId,
            playerKey = Fixed64(key.Value ?? ""),
            displayName = Fixed32(SessionEndReason.Clean(name, 64)),
            company = (byte)Math.Max(0, Math.Min(255, company)),
            slot = (byte)Math.Max(0, Math.Min(255, slot)),
            flags = clientId == NetworkManager.ServerClientId ? RosterEntry.FlagHost : (byte)0,
        };
    }

    static FixedString64Bytes Fixed64(string s)
    {
        var f = new FixedString64Bytes();
        f.CopyFromTruncated(s);
        return f;
    }

    static FixedString32Bytes Fixed32(string s)
    {
        var f = new FixedString32Bytes();
        f.CopyFromTruncated(s);
        return f;
    }

    void OnListChanged(NetworkListEvent<RosterEntry> change) => RaiseChanged();

    void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { UnityEngine.Debug.LogException(ex); }
    }
}
