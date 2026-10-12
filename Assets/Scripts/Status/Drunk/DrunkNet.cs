using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>One player's drunk level as everyone sees it: the client and the level in steps of <see cref="DrunkNet.Step"/>.</summary>
public struct DrunkRec : INetworkSerializable, IEquatable<DrunkRec>
{
    public ulong client;
    public byte level;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref client);
        serializer.SerializeValue(ref level);
    }

    public bool Equals(DrunkRec o) => client == o.client && level == o.level;
    public override bool Equals(object obj) => obj is DrunkRec o && Equals(o);
    public override int GetHashCode() => unchecked((int)client * 397 ^ level);
}

/// <summary>
/// What the others see of your drinking (Docs/Casino.md §5): a host-owned list of {client, level}. Each machine reports its
/// own player's level (quantised, only when it changes, at most once a second; the host rate-limits and clamps it), and
/// <see cref="DrunkLook"/> on every puppet reads <see cref="LevelOf"/>. Presentation only: nothing is decided from this
/// list, the symptoms themselves run on the drunk player's own machine (<see cref="DrunkStatus"/>).
///
/// It is a session component, so the prefab gets it when Rubbish/Setup/Coop is run. Until then <see cref="Instance"/> is
/// null and everything degrades to "nobody else looks drunk": the local effects do not depend on it.
/// </summary>
[NetSessionComponent(Order = 46)]
public sealed class DrunkNet : NetworkBehaviour
{
    public static DrunkNet Instance { get; private set; }

    public const string MsgReport = "rb.drunk.report";
    /// <summary>Level per list step (0.02: 60 steps to the maximum, a change every few seconds while sobering).</summary>
    public const float Step = 0.02f;
    static readonly byte MaxSteps = (byte)Mathf.RoundToInt(DrunkRules.MaxLevel / Step);

    NetworkList<DrunkRec> levels;
    byte lastSent;
    float nextReport;
    bool hooked;

    void Awake() => levels = new NetworkList<DrunkRec>();

    public override void OnNetworkSpawn()
    {
        Instance = this;
        lastSent = 0;
        nextReport = 0f;
        if (IsServer)
        {
            SessionService.SetGuard(MsgReport, new NetGuard(2f, 6, 16));
            if (NetworkManager != null) { NetworkManager.OnClientDisconnectCallback += ClientLeft; hooked = true; }
        }
    }

    public override void OnNetworkDespawn()
    {
        Unhook();
        if (Instance == this) Instance = null;
    }

    public override void OnDestroy()
    {
        Unhook();
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    void Unhook()
    {
        if (!hooked) return;
        hooked = false;
        if (NetworkManager != null) NetworkManager.OnClientDisconnectCallback -= ClientLeft;
    }

    static byte Quantise(float level) => (byte)Mathf.Clamp(Mathf.RoundToInt(DrunkRules.Clamp(level) / Step), 0, MaxSteps);

    /// <summary>A player's drunk level as last reported (0 when unknown, sober, or the session has no DrunkNet).</summary>
    public static float LevelOf(ulong client)
    {
        var n = Instance;
        if (n == null || !n.IsSpawned || n.levels == null) return 0f;
        var list = n.levels;
        for (int i = 0; i < list.Count; i++)
            if (list[i].client == client) return list[i].level * Step;
        return 0f;
    }

    void Update()
    {
        if (!IsSpawned || NetworkManager == null) return;
        if (NetSession.IsSolo || SessionService.SoloOnly) return; // nobody to show
        float now = Time.unscaledTime;
        if (now < nextReport) return;
        nextReport = now + 0.5f;
        byte q = Quantise(DrunkStatus.Level);
        if (q == lastSent) return;
        lastSent = q;
        nextReport = now + 1f;
        if (IsServer) HostSet(NetworkManager.LocalClientId, q);
        else ReportRpc(q);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ReportRpc(byte steps, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!SessionService.GuardFor(MsgReport).Allow(sender, 1, Time.realtimeSinceStartupAsDouble)) return;
        HostSet(sender, steps);
    }

    /// <summary>Host: the one writer of the list. Clamped; a sober player has no record.</summary>
    void HostSet(ulong client, byte steps)
    {
        if (!IsServer) return;
        if (steps > MaxSteps) steps = MaxSteps;
        int idx = -1;
        for (int i = 0; i < levels.Count; i++) if (levels[i].client == client) { idx = i; break; }
        if (steps == 0)
        {
            if (idx >= 0) levels.RemoveAt(idx);
            return;
        }
        var rec = new DrunkRec { client = client, level = steps };
        if (idx < 0) levels.Add(rec);
        else if (!levels[idx].Equals(rec)) levels[idx] = rec;
    }

    void ClientLeft(ulong client)
    {
        if (!IsServer || !IsSpawned) return;
        for (int i = levels.Count - 1; i >= 0; i--)
            if (levels[i].client == client) levels.RemoveAt(i);
    }
}
