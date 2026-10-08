using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>One streamed critter (12 bytes): id, kind, state, flags, position (4 cm / 2 cm), yaw (1.4°).</summary>
public struct CritterSnap : INetworkSerializable
{
    public const byte KindRaccoon = 0, KindGoblin = 1, KindKing = 2;
    public ushort id;
    public byte kind, state, flags;
    public short x, y, z;
    public byte yaw;

    public Vector3 Pos => new Vector3(x * 0.04f, y * 0.02f, z * 0.04f);
    public float Yaw => yaw * (360f / 256f);

    public static CritterSnap Of(Critter c, byte kind)
    {
        Vector3 p = c.transform.position;
        return new CritterSnap
        {
            id = c.NetId, kind = kind, state = c.NetState, flags = c.NetFlags,
            x = (short)Mathf.Clamp(Mathf.RoundToInt(p.x / 0.04f), short.MinValue, short.MaxValue),
            y = (short)Mathf.Clamp(Mathf.RoundToInt(p.y / 0.02f), short.MinValue, short.MaxValue),
            z = (short)Mathf.Clamp(Mathf.RoundToInt(p.z / 0.04f), short.MinValue, short.MaxValue),
            yaw = (byte)(Mathf.RoundToInt(Mathf.Repeat(c.transform.eulerAngles.y, 360f) / (360f / 256f)) & 0xFF),
        };
    }

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref id);
        s.SerializeValue(ref kind);
        s.SerializeValue(ref state);
        s.SerializeValue(ref flags);
        s.SerializeValue(ref x);
        s.SerializeValue(ref y);
        s.SerializeValue(ref z);
        s.SerializeValue(ref yaw);
    }
}

/// <summary>A player's rabies, written by the host and keyed by PlayerKey so it survives a reconnect (§4.10, M6 gate).</summary>
public struct RabiesRec : INetworkSerializable, IEquatable<RabiesRec>
{
    public const byte FlagRepelled = 1;
    public FixedString64Bytes key;
    /// <summary>The client this key is connected as right now (<see cref="ulong.MaxValue"/> = away).</summary>
    public ulong client;
    /// <summary>GameClock.TotalHours of the bite, or −1 (healthy, record kept for flags).</summary>
    public double infectedAt;
    public byte flags;

    public bool Infected => infectedAt >= 0;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref key);
        s.SerializeValue(ref client);
        s.SerializeValue(ref infectedAt);
        s.SerializeValue(ref flags);
    }

    public bool Equals(RabiesRec o) => key.Equals(o.key) && client == o.client && infectedAt.Equals(o.infectedAt) && flags == o.flags;
    public override bool Equals(object obj) => obj is RabiesRec o && Equals(o);
    public override int GetHashCode() => key.GetHashCode() ^ client.GetHashCode();
}

/// <summary>
/// Package F4 "Night shift together" on <c>NetSession.prefab</c> (Docs/Multiplayer.md §14.4, §8.2 M6). The host simulates every
/// critter (raccoons, goblins, the Raccoon King) and streams them here; guests see puppets (<see cref="Critter.NetPuppet"/>) that
/// play the same looks.
/// <list type="bullet">
/// <item><b>Snapshots</b>: 5 Hz, unreliable, 12 bytes a critter (≈0.7 KB/s per guest for 8 critters). State changes play their
/// juice on every machine (the puppet enters the same state). Loot and anchors (stowaway on a truck, latched on a player) go
/// reliable as they change and every 2 s.</item>
/// <item><b>Hits</b>: a critter's bite on a guest is sent to that guest's machine, which knocks itself back (§4.12 #5: the victim
/// moves itself) and applies the host's rabies roll. A guest's throw that bonks a puppet asks the host's critter to take it.</item>
/// <item><b>Rabies per player</b>: a host-written list keyed by PlayerKey; each player reports their own infection and cure, and
/// a rejoining player gets theirs back. Everyone sees everyone's foam. <b>Rabies Tag</b> (host Town rule, on by default).</item>
/// <item>Latch, pokes, the horn, traps, roof spotlights and the Raccoon King go through here too (partials).</item>
/// </list>
/// Solo never activates it: every static helper falls back to the local path.
/// </summary>
[NetSessionComponent(Order = 40)]
public partial class NightNet : NetworkBehaviour
{
    public static NightNet Instance { get; private set; }

    public const float SnapHz = 5f;
    const string MsgBonk = "rb.night.bonk", MsgPoke = "rb.night.poke", MsgHonk = "rb.night.honk", MsgShake = "rb.night.shake",
                 MsgRabies = "rb.night.rabies", MsgTrap = "rb.night.trap", MsgSpot = "rb.night.spot", MsgShot = "rb.night.shot";
    /// <summary>Per-message overhead we count on top of the payload (NGO header + transport), for the bandwidth audit.</summary>
    const int MsgOverhead = 32;

    readonly NetworkVariable<bool> rabiesTag = new NetworkVariable<bool>(true);
    NetworkList<RabiesRec> rabies;

    bool active;
    /// <summary>A co-op session is streaming critters (false solo and in menus).</summary>
    public static bool Streaming => Instance != null && Instance.IsSpawned && Instance.active;
    public bool RabiesTagOn => rabiesTag.Value;
    public ulong LocalId => NetworkManager != null ? NetworkManager.LocalClientId : 0;

    static bool Solo => NetSession.IsSolo || SessionService.SoloOnly;

    void Awake()
    {
        rabies = new NetworkList<RabiesRec>();
        AwakeExtras();
    }

    public override void OnNetworkSpawn()
    {
        Instance = this;
        // The host's saved preference is the initial authoritative Town rule. NGO replicates it to guests on join/rejoin.
        if (IsServer) rabiesTag.Value = GameSettings.Values.townRules.rabiesTag;
        if (Solo) return;
        active = true;
        HandsNet.HostKnocked -= OnHostKnocked;
        if (IsServer) HandsNet.HostKnocked += OnHostKnocked;
        GameSession.RabiesChanged -= OnLocalRabiesChanged;
        GameSession.RabiesChanged += OnLocalRabiesChanged;
        SpawnExtras();
        NightAudit.Event("spawn", new Dictionary<string, object> { { "server", IsServer } });
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
        if (!active) return;
        active = false;
        HandsNet.HostKnocked -= OnHostKnocked;
        GameSession.RabiesChanged -= OnLocalRabiesChanged;
        foreach (var kv in puppets) if (kv.Value != null) kv.Value.RemoveNow();
        puppets.Clear();
        DespawnExtras();
    }

    void Update()
    {
        if (!active || !IsSpawned) return;
        if (IsServer) { HostStream(); HostRabiesTick(); }
        else GuestTick();
        LocalRabiesTick();
        ExtrasTick();
    }

    // ==================================================================================== critter stream (host)

    readonly Dictionary<ushort, Critter> hostKnown = new Dictionary<ushort, Critter>();
    readonly List<CritterSnap> snapBuf = new List<CritterSnap>(16);
    readonly List<ushort> goneBuf = new List<ushort>(8);
    ushort nextCritterId = 1;
    ushort snapSeq;
    float nextSnap, nextInfo, bwWindowStart;
    long bwBytes;
    int bwMaxCritters;

    static byte KindOf(Critter c) => c is RaccoonKing ? CritterSnap.KindKing : c is PileGoblin ? CritterSnap.KindGoblin : CritterSnap.KindRaccoon;

    bool AnyGuest => NetworkManager != null && NetworkManager.ConnectedClientsIds.Count > 1;

    void HostStream()
    {
        float now = Time.unscaledTime;
        if (now < nextSnap) return;
        nextSnap = now + 1f / SnapHz;
        snapBuf.Clear();
        goneBuf.Clear();
        var all = Critter.All;
        for (int i = 0; i < all.Count; i++)
        {
            var c = all[i];
            if (c == null || c.NetPuppet) continue;
            if (c.NetId == 0)
            {
                c.NetId = nextCritterId++;
                if (nextCritterId == 0) nextCritterId = 1;
                hostKnown[c.NetId] = c;
                // The King is announced when he spawns, before the first stream tick assigns an ID.
                // Publish again now so guests can resolve the boss bar to his puppet immediately.
                if (c is RaccoonKing king) RaccoonKingEvent.HostChanged(king);
            }
            if (c.Despawning) continue;
            snapBuf.Add(CritterSnap.Of(c, KindOf(c)));
        }
        foreach (var kv in hostKnown)
            if (kv.Value == null || kv.Value.Despawning) goneBuf.Add(kv.Key);
        for (int i = 0; i < goneBuf.Count; i++) hostKnown.Remove(goneBuf[i]);
        if (!AnyGuest) return;

        if (goneBuf.Count > 0)
        {
            GoneRpc(goneBuf.ToArray());
            CountBytes(goneBuf.Count * 2 + 4);
        }
        snapSeq++;
        SnapRpc(snapBuf.ToArray(), snapSeq);
        CountBytes(snapBuf.Count * 12 + 6);
        bwMaxCritters = Mathf.Max(bwMaxCritters, snapBuf.Count);

        if (now >= nextInfo)
        {
            nextInfo = now + 2f;
            for (int i = 0; i < all.Count; i++)
            {
                var c = all[i];
                if (c is Raccoon r && !r.NetPuppet && !r.Despawning && (r.LootDefId != null || r.HasAnchor)) SendInfo(r);
            }
        }
    }

    void CountBytes(int payload)
    {
        bwBytes += payload + MsgOverhead;
        float now = Time.unscaledTime;
        if (bwWindowStart <= 0f) bwWindowStart = now;
        float span = now - bwWindowStart;
        if (span < 10f) return;
        NightAudit.Event("bw", new Dictionary<string, object>
        {
            { "bytesPerSecPerGuest", Math.Round(bwBytes / span, 1) }, { "maxCritters", bwMaxCritters },
            { "critters", snapBuf.Count }, { "guests", NetworkManager.ConnectedClientsIds.Count - 1 },
        });
        bwBytes = 0;
        bwMaxCritters = 0;
        bwWindowStart = now;
    }

    [Rpc(SendTo.NotServer, Delivery = RpcDelivery.Unreliable)]
    void SnapRpc(CritterSnap[] snaps, ushort seq)
    {
        if (IsServer || snaps == null) return;
        if (haveSeq && (short)(seq - lastSeq) <= 0) return; // late or duplicate
        haveSeq = true;
        lastSeq = seq;
        float now = Time.unscaledTime;
        float interval = lastSnapAt > 0f ? Mathf.Clamp(now - lastSnapAt, 0.05f, 0.6f) : 1f / SnapHz;
        lastSnapAt = now;
        for (int i = 0; i < snaps.Length; i++) ApplySnap(snaps[i], interval, now);
    }

    [Rpc(SendTo.NotServer)]
    void GoneRpc(ushort[] ids)
    {
        if (IsServer || ids == null) return;
        foreach (var id in ids)
        {
            if (puppets.TryGetValue(id, out var c) && c != null) c.Poof();
            puppets.Remove(id);
            seenAt.Remove(id);
        }
    }

    // ==================================================================================== puppets (guest)

    readonly Dictionary<ushort, Critter> puppets = new Dictionary<ushort, Critter>();
    readonly Dictionary<ushort, float> seenAt = new Dictionary<ushort, float>();
    readonly List<ushort> stale = new List<ushort>();
    ushort lastSeq;
    bool haveSeq;
    float lastSnapAt;

    /// <summary>Guest: our copy of the host's critter <paramref name="id"/>.</summary>
    public Critter Puppet(ushort id) => puppets.TryGetValue(id, out var c) ? c : null;

    void ApplySnap(CritterSnap s, float interval, float now)
    {
        if (!puppets.TryGetValue(s.id, out var c) || c == null)
        {
            if (!CritterWorld.InNeighborhood && !DepotRowWorld.Active) return;
            c = s.kind == CritterSnap.KindGoblin ? PileGoblin.SpawnPuppet(s.id, s.Pos, s.Yaw, s.state, s.flags)
              : s.kind == CritterSnap.KindKing ? (Critter)RaccoonKing.SpawnPuppet(s.id, s.Pos, s.Yaw, s.state)
              : Raccoon.SpawnPuppet(s.id, (s.flags & 1) != 0, s.Pos, s.Yaw, s.state);
            puppets[s.id] = c;
            if ((s.flags & 6) != 0) InfoAskRpc(s.id); // carrying loot or riding something: ask what
        }
        seenAt[s.id] = now;
        c.NetSnapshot(s.Pos, s.Yaw, interval);
        c.ApplyNetState(s.state, s.flags);
    }

    void GuestTick()
    {
        float now = Time.unscaledTime;
        stale.Clear();
        foreach (var kv in puppets)
            if (kv.Value == null || (seenAt.TryGetValue(kv.Key, out float t) && now - t > 3f)) stale.Add(kv.Key);
        foreach (var id in stale)
        {
            if (puppets.TryGetValue(id, out var c) && c != null) c.Poof();
            puppets.Remove(id);
            seenAt.Remove(id);
        }
    }

    // ==================================================================================== loot / anchors

    const byte AnchorNone = 0, AnchorTruck = 1, AnchorPlayer = 2;

    void SendInfo(Raccoon r)
    {
        byte kind = AnchorNone;
        ulong reference = 0;
        if (r.Boarded && r.StowTruck != null && r.StowTruck.NetworkObject != null && r.StowTruck.NetworkObject.IsSpawned)
        { kind = AnchorTruck; reference = r.StowTruck.NetworkObject.NetworkObjectId; }
        else if (r.LatchVictim != ulong.MaxValue) { kind = AnchorPlayer; reference = r.LatchVictim; }
        var loot = new FixedString64Bytes();
        loot.CopyFromTruncated(r.LootDefId ?? "");
        InfoRpc(r.NetId, loot, kind, reference);
        CountBytes(64 + 11);
    }

    [Rpc(SendTo.NotServer)]
    void InfoRpc(ushort id, FixedString64Bytes loot, byte anchorKind, ulong reference)
    {
        if (IsServer || !(Puppet(id) is Raccoon r)) return;
        r.SetLootLook(loot.ToString());
        if (anchorKind == AnchorTruck)
        {
            var t = TruckByNetId(reference);
            if (t != null && r.AnchorTransform != t.transform) r.SetAnchor(t.transform, Raccoon.StowLocal, Quaternion.Euler(0f, 180f, 0f));
        }
        else if (anchorKind == AnchorPlayer)
        {
            var pc = NightPlayers.ForClient(reference);
            r.PuppetLatch(reference);
            if (pc != null && r.AnchorTransform != pc.transform) r.SetAnchor(pc.transform, Raccoon.LatchLocal, Quaternion.identity);
        }
        else if (r.HasAnchor) r.ClearAnchor();
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void InfoAskRpc(ushort id, RpcParams rp = default)
    {
        if (!SessionService.GuardFor("rb.night.info").Allow(rp.Receive.SenderClientId, 4, Time.realtimeSinceStartupAsDouble)) return;
        if (hostKnown.TryGetValue(id, out var c) && c is Raccoon r && r != null) SendInfo(r);
    }

    static TruckControllerV2 TruckByNetId(ulong id)
    {
        var all = TruckControllerV2.All;
        for (int i = 0; i < all.Count; i++)
        {
            var t = all[i];
            if (t != null && t.NetworkObject != null && t.NetworkObject.IsSpawned && t.NetworkObject.NetworkObjectId == id) return t;
        }
        return null;
    }

    /// <summary>Host: this raccoon's loot or anchor changed (tell the guests now).</summary>
    public static void HostCritterChanged(Raccoon r)
    {
        var n = Instance;
        if (!Streaming || !n.IsServer || r == null || r.NetId == 0 || !n.AnyGuest) return;
        n.SendInfo(r);
    }

    // ==================================================================================== words and toasts

    static readonly Color[] SayColors =
    {
        new Color(0.85f, 0.85f, 0.85f), new Color(1f, 0.85f, 0.5f), new Color(1f, 0.55f, 0.3f), new Color(1f, 0.3f, 0.25f),
        new Color(0.55f, 1f, 0.45f),
    };

    /// <summary>A floating word everyone sees (host-side events: noms, shoos, latches).</summary>
    public static void Say(Vector3 pos, string text, byte color)
    {
        GameHUD.FloatingText(pos, text, SayColors[Mathf.Clamp(color, 0, SayColors.Length - 1)], 1.1f);
        var n = Instance;
        if (!Streaming || !n.IsServer || !n.AnyGuest) return;
        var f = new FixedString64Bytes();
        f.CopyFromTruncated(text ?? "");
        n.SayRpc(pos, f, color);
    }

    [Rpc(SendTo.NotServer)]
    void SayRpc(Vector3 pos, FixedString64Bytes text, byte color)
    {
        if (IsServer) return;
        GameHUD.FloatingText(pos, text.ToString(), SayColors[Mathf.Clamp(color, 0, SayColors.Length - 1)], 1.1f);
    }

    /// <summary>A toast for every player within <paramref name="radius"/> m of <paramref name="pos"/> (host events).</summary>
    public static void HostToastNear(Vector3 pos, float radius, string text, ToastStyle style = ToastStyle.Info)
    {
        ToastIfNear(pos, radius, text, style);
        var n = Instance;
        if (!Streaming || !n.IsServer || !n.AnyGuest) return;
        var f = new FixedString128Bytes();
        f.CopyFromTruncated(text ?? "");
        n.ToastRpc(pos, radius, f, (byte)style);
    }

    static void ToastIfNear(Vector3 pos, float radius, string text, ToastStyle style)
    {
        var me = NightPlayers.LocalPlayer;
        if (me == null) return;
        Vector3 at = me.IsDriving && me.currentTruck != null ? me.currentTruck.transform.position : me.transform.position;
        if (CritterWorld.Flat(at - pos).magnitude <= radius) GameHUD.Toast(text, style, 3f);
    }

    [Rpc(SendTo.NotServer)]
    void ToastRpc(Vector3 pos, float radius, FixedString128Bytes text, byte style)
    {
        if (IsServer) return;
        ToastIfNear(pos, radius, text.ToString(), (ToastStyle)style);
    }

    // ==================================================================================== hits on guests

    static readonly string[] SfxIds = { "bite", "thud", "squeak", "hiss" };

    /// <summary>Host: a critter hit a remote player. Their machine plays it (and applies <paramref name="infect"/>). Optimistic.</summary>
    public static bool HostHitRemote(ulong victim, Vector3 dir, float dist, float hop, string word, Color color, string sfx, float shake,
                                     Vector3 src, bool infect)
    {
        var n = Instance;
        if (!Streaming || !n.IsServer) return false;
        var w = new FixedString32Bytes();
        w.CopyFromTruncated(word ?? "");
        byte s = (byte)Mathf.Max(0, Array.IndexOf(SfxIds, sfx));
        n.HitRpc(dir, dist, hop, w, color, s, shake, src, infect, n.RpcTarget.Single(victim, RpcTargetUse.Temp));
        NightAudit.Event("hit", new Dictionary<string, object> { { "victim", (long)victim }, { "word", word ?? "" }, { "infect", infect } });
        return true;
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void HitRpc(Vector3 dir, float dist, float hop, FixedString32Bytes word, Color color, byte sfx, float shake, Vector3 src, bool infect,
                RpcParams rp = default)
    {
        bool ok = CritterKnockback.HitLocalPlayer(dir, dist, hop, word.ToString(), color, SfxIds[Mathf.Clamp(sfx, 0, SfxIds.Length - 1)], shake, src);
        if (ok && infect && !GameSession.HasRabies) GameSession.Infect(GameClock.TotalHours);
        NightAudit.Event("hurt", new Dictionary<string, object> { { "ok", ok }, { "infect", infect }, { "rabid", GameSession.HasRabies } });
    }

    // ==================================================================================== guest requests

    /// <summary>A throw on this machine bonked our puppet of a host critter.</summary>
    public static void RequestBonk(Critter c, Vector3 vel, ulong by)
    {
        var n = Instance;
        if (c == null) return;
        if (!Streaming || n.IsServer) { c.NetBonk(vel, by); return; }
        n.BonkRpc(c.NetId, vel);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void BonkRpc(ushort id, Vector3 vel, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!SessionService.GuardFor(MsgBonk).Allow(sender, 16, Time.realtimeSinceStartupAsDouble)) return;
        if (!hostKnown.TryGetValue(id, out var c) || c == null || c.Despawning) return;
        var pc = NightPlayers.ForClient(sender);
        if (pc != null && (pc.transform.position - c.transform.position).sqrMagnitude > 40f * 40f) return; // a throw can't reach that far
        if (vel.magnitude > 40f) vel = vel.normalized * 40f;
        c.NetBonk(vel, sender);
        NightAudit.Event("bonk", new Dictionary<string, object> { { "critter", id }, { "by", (long)sender } });
    }

    /// <summary>E on a raccoon (shoo a stowaway, bonk one off a friend).</summary>
    public static void RequestPoke(Raccoon r)
    {
        var n = Instance;
        if (r == null) return;
        if (!Streaming || n.IsServer) { r.Poke(Streaming ? n.LocalId : 0); return; }
        n.PokeRpc(r.NetId);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void PokeRpc(ushort id, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!SessionService.GuardFor(MsgPoke).Allow(sender, 4, Time.realtimeSinceStartupAsDouble)) return;
        if (!hostKnown.TryGetValue(id, out var c) || !(c is Raccoon r) || r == null) return;
        var pc = NightPlayers.ForClient(sender);
        if (pc == null || (pc.transform.position - r.transform.position).sqrMagnitude > 6f * 6f) return;
        r.Poke(sender);
    }

    /// <summary>A guest honked: the host's critters hear it.</summary>
    public static void RequestHonk(Vector3 pos)
    {
        var n = Instance;
        if (!Streaming || n.IsServer) return;
        n.HonkRpc(pos);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void HonkRpc(Vector3 pos, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!SessionService.GuardFor(MsgHonk).Allow(sender, 12, Time.realtimeSinceStartupAsDouble)) return;
        var pc = NightPlayers.ForClient(sender);
        if (pc != null && (pc.transform.position - pos).sqrMagnitude > 15f * 15f) return;
        CritterSpawner.Honk(pos);
    }

    // ==================================================================================== latch

    /// <summary>Host: a raccoon latched onto / let go of <paramref name="victim"/>. Their machine scrambles their controls.</summary>
    public static void HostLatch(ulong victim, Raccoon r, bool on, bool infect)
    {
        var n = Instance;
        bool local = !Streaming || victim == n.LocalId;
        if (local) RaccoonLatch.OnLatch(on, r, infect);
        else n.LatchRpc(on, r != null ? r.NetId : (ushort)0, infect, n.RpcTarget.Single(victim, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void LatchRpc(bool on, ushort critter, bool infect, RpcParams rp = default)
    {
        RaccoonLatch.OnLatch(on, Puppet(critter) as Raccoon, infect);
    }

    /// <summary>The latched player mashed free.</summary>
    public static void RequestLatchShake()
    {
        var n = Instance;
        if (!Streaming || n.IsServer) { NightLatch.HostShake(Streaming ? n.LocalId : 0); return; }
        n.ShakeRpc();
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ShakeRpc(RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!SessionService.GuardFor(MsgShake).Allow(sender, 4, Time.realtimeSinceStartupAsDouble)) return;
        NightLatch.HostShake(sender);
    }

    // ==================================================================================== rabies per player

    string myKey;
    bool restoredRabies;
    float nextRabiesCheck, nextHostRabiesCheck;
    bool lastRepelled;

    /// <summary>This machine's PlayerKey (once the roster has it).</summary>
    string MyKey
    {
        get
        {
            if (!string.IsNullOrEmpty(myKey)) return myKey;
            var roster = NetRoster.Instance;
            if (roster != null && roster.TryGetByClient(LocalId, out var e)) myKey = e.playerKey.ToString();
            return myKey;
        }
    }

    public static bool TryGetRabies(ulong client, out RabiesRec rec)
    {
        rec = default;
        var n = Instance;
        if (!Streaming) return false;
        var list = n.rabies;
        for (int i = 0; i < list.Count; i++) if (list[i].client == client) { rec = list[i]; return true; }
        return false;
    }

    /// <summary>The infection time of a player (local: GameSession; others: the host's list), or −1.</summary>
    public static double InfectedAt(ulong client)
    {
        var n = Instance;
        if (!Streaming || client == n.LocalId) return GameSession.HasRabies ? GameSession.RabiesInfectedAtHours : -1;
        return TryGetRabies(client, out var r) ? r.infectedAt : -1;
    }

    /// <summary>Showing symptoms (past incubation).</summary>
    public static bool Symptomatic(ulong client)
    {
        double at = InfectedAt(client);
        return at >= 0 && GameClock.AgeHours(at) >= Economy.RabiesIncubationHours;
    }

    /// <summary>Is a remote player wearing repellent (their machine reports it)?</summary>
    public static bool IsRepelled(ulong client) => TryGetRabies(client, out var r) && (r.flags & RabiesRec.FlagRepelled) != 0;

    // A reconnect must read the host's record before reporting the local (often healthy) default.
    void OnLocalRabiesChanged(bool infected)
    {
        if (restoredRabies) ReportRabies();
    }

    void ReportRabies()
    {
        double at = GameSession.HasRabies ? GameSession.RabiesInfectedAtHours : -1;
        byte flags = (byte)(CritterRepellent.IsActive ? RabiesRec.FlagRepelled : 0);
        if (IsServer) HostUpsertRabies(LocalId, MyKey, at, flags);
        else RabiesRpc(at, flags);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void RabiesRpc(double infectedAt, byte flags, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!SessionService.GuardFor(MsgRabies).Allow(sender, 12, Time.realtimeSinceStartupAsDouble)) return;
        string key = NetRoster.Instance != null && NetRoster.Instance.TryGetByClient(sender, out var e) ? e.playerKey.ToString() : null;
        HostUpsertRabies(sender, key, infectedAt, flags);
    }

    void HostUpsertRabies(ulong client, string key, double at, byte flags)
    {
        if (string.IsNullOrEmpty(key)) key = "client:" + client;
        var k = new FixedString64Bytes();
        k.CopyFromTruncated(key);
        int idx = -1;
        for (int i = 0; i < rabies.Count; i++) if (rabies[i].key.Equals(k)) { idx = i; break; }
        var rec = new RabiesRec { key = k, client = client, infectedAt = at < 0 ? -1 : at, flags = flags };
        bool was = idx >= 0 && rabies[idx].Infected;
        if (idx < 0) rabies.Add(rec);
        else if (!rabies[idx].Equals(rec)) rabies[idx] = rec;
        if (was != rec.Infected)
            NightAudit.Event("rabies", new Dictionary<string, object> { { "client", (long)client }, { "key", key }, { "infected", rec.Infected }, { "at", Math.Round(at, 3) } });
    }

    /// <summary>Host: keep each record's client id current (a reconnect gets a new id).</summary>
    void HostRabiesTick()
    {
        if (Time.unscaledTime < nextHostRabiesCheck) return;
        nextHostRabiesCheck = Time.unscaledTime + 1f;
        var roster = NetRoster.Instance;
        if (roster == null) return;
        for (int i = 0; i < rabies.Count; i++)
        {
            var r = rabies[i];
            ulong now = ulong.MaxValue;
            foreach (var id in NetworkManager.ConnectedClientsIds)
                if (roster.TryGetByClient(id, out var e) && e.playerKey.Equals(r.key)) { now = id; break; }
            if (now != r.client) { r.client = now; rabies[i] = r; }
        }
        // A new player needs an explicit "no saved infection" record. Until that arrives,
        // their client cannot tell whether its old rabies record is still in flight.
        foreach (var id in NetworkManager.ConnectedClientsIds)
        {
            if (!roster.TryGetByClient(id, out var e)) continue;
            bool have = false;
            for (int i = 0; i < rabies.Count; i++)
                if (rabies[i].key.Equals(e.playerKey)) { have = true; break; }
            if (!have) rabies.Add(new RabiesRec { key = e.playerKey, client = id, infectedAt = -2, flags = 0 });
        }
    }

    /// <summary>Every machine: report our own rabies and repellent; get it back after a rejoin.</summary>
    void LocalRabiesTick()
    {
        if (Time.unscaledTime < nextRabiesCheck) return;
        nextRabiesCheck = Time.unscaledTime + 1f;
        string key = MyKey;
        if (string.IsNullOrEmpty(key)) return;
        RabiesRec mine = default;
        bool have = false;
        for (int i = 0; i < rabies.Count; i++) if (rabies[i].key.ToString() == key) { mine = rabies[i]; have = true; break; }
        if (!have) return; // wait for the host's existing record or its explicit new-player marker
        if (!restoredRabies)
        {
            restoredRabies = true;
            if (mine.Infected && !GameSession.HasRabies)
            {
                // Rejoined while rabid: the host remembered (§8.2 M6 gate: the timer survives a reconnect).
                GameSession.RestoreRabies(mine.infectedAt);
                GameHUD.Toast("Welcome back. You're still rabid. Any shop's Rabies Shot fixes that.", ToastStyle.Bad, 3.5f);
                NightAudit.Event("rabies.restore", new Dictionary<string, object> { { "at", Math.Round(mine.infectedAt, 3) }, { "ok", GameSession.HasRabies } });
                return;
            }
            if (mine.Infected && GameSession.HasRabies && mine.infectedAt < GameSession.RabiesInfectedAtHours)
                GameSession.RestoreRabies(mine.infectedAt);
        }
        bool rep = CritterRepellent.IsActive;
        double at = GameSession.HasRabies ? GameSession.RabiesInfectedAtHours : -1;
        bool differs = !have ? (at >= 0 || rep) : (Math.Abs(mine.infectedAt - at) > 1e-6 || ((mine.flags & RabiesRec.FlagRepelled) != 0) != rep);
        if (differs || rep != lastRepelled) { lastRepelled = rep; ReportRabies(); }
    }

    /// <summary>Host: Rabies Tag, and a friend's bonk knocks a latched raccoon off.</summary>
    void OnHostKnocked(ulong victim, ulong attacker, HitKind kind)
    {
        if (attacker == victim || attacker == HandsNet.None) return;
        var latched = NightLatch.On(victim);
        if (latched != null) latched.EndLatch("friend", attacker, false);
        if (!rabiesTag.Value || kind == HitKind.Stagger) return;
        if (!Symptomatic(attacker) || InfectedAt(victim) >= 0) return;
        bool pass = NightDice.Host.Chance(NightShift.RabiesTagChance);
        NightAudit.Event("rabiestag", new Dictionary<string, object> { { "attacker", (long)attacker }, { "victim", (long)victim }, { "pass", pass } });
        if (!pass) return;
        HostInfect(victim);
        Say(NightPlayers.ForClient(victim) is PlayerController v ? v.transform.position + Vector3.up * 2.2f : Vector3.zero, "TAGGED! (rabies)", 4);
    }

    /// <summary>Host: give <paramref name="victim"/> rabies (Rabies Tag).</summary>
    void HostInfect(ulong victim)
    {
        if (victim == LocalId) { if (!GameSession.HasRabies) GameSession.Infect(GameClock.TotalHours); return; }
        InfectRpc(RpcTarget.Single(victim, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void InfectRpc(RpcParams rp = default)
    {
        if (!GameSession.HasRabies) GameSession.Infect(GameClock.TotalHours);
    }

    /// <summary>Host only: set Rabies Tag for the session.</summary>
    public void HostSetRabiesTag(bool on)
    {
        if (!IsServer) return;
        rabiesTag.Value = on;
        NightAudit.Event("rabiestag.toggle", new Dictionary<string, object> { { "on", on } });
    }

    // ==================================================================================== a friend's Rabies Shot

    /// <summary>Host/solo: cure an already paid-for patient. Guest: request a host-verified, host-charged shot (RabiesJab).</summary>
    public static void ShotFor(ulong target)
    {
        var n = Instance;
        if (!Streaming) { RabiesStatus.CureWithShot(); return; }
        if (!n.IsServer) { n.ShotForRpc(target); return; }
        if (target == n.LocalId) RabiesStatus.CureWithShot();
        else n.CureRpc(true, n.RpcTarget.Single(target, RpcTargetUse.Temp));
    }

    readonly Dictionary<ulong, float> shotPendingUntil = new Dictionary<ulong, float>();

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ShotForRpc(ulong target, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!SessionService.GuardFor(MsgShot).Allow(sender, 8, Time.realtimeSinceStartupAsDouble)) return;
        var payer = NightPlayers.ForClient(sender);
        if (payer == null || payer.IsDriving || payer.IsPlayingChess) return;
        // No first-aid shelf any more: a jab happens where the patient lies (carried, or down within reach of the payer).
        if (InfectedAt(target) < 0 || (shotPendingUntil.TryGetValue(target, out float until) && Time.unscaledTime < until)) return;
        if (target != sender)
        {
            var hands = HandsNet.Instance;
            var patient = PlayerBody.For(target);
            if (hands == null || !hands.Active || patient == null) return;
            bool carrying = hands.BodyOf(sender).carrying == target;
            bool downNearby = (patient.transform.position - payer.transform.position).sqrMagnitude <= 9f &&
                              (patient.State == BodyState.Flopped || patient.State == BodyState.Carried || patient.State == BodyState.KnockedOut);
            if (!carrying && !downNearby) return;
        }
        if (!Businesses.TryGetForClient(sender, out var business)) return;
        bool paid;
        using (Ledger.ToBusiness(business.Id))
            paid = Ledger.Spend(NightShift.RabiesShotPrice, target == sender ? "Rabies Shot" : $"Rabies Shot for {HandsNet.NameOf(target)}", NightShift.LedgerShot);
        if (!paid)
        {
            ShotResultRpc(false, target, RpcTarget.Single(sender, RpcTargetUse.Temp));
            return;
        }
        shotPendingUntil[target] = Time.unscaledTime + 2f;
        NightAudit.Event("jab", new Dictionary<string, object> { { "for", (long)target }, { "self", target == sender } });
        NightAudit.Event("shot", new Dictionary<string, object> { { "by", (long)sender }, { "for", (long)target } });
        if (target == LocalId) RabiesStatus.CureWithShot();
        else CureRpc(target != sender, RpcTarget.Single(target, RpcTargetUse.Temp));
        if (target != sender) ShotResultRpc(true, target, RpcTarget.Single(sender, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void CureRpc(bool byFriend, RpcParams rp = default)
    {
        if (RabiesStatus.CureWithShot() && byFriend) GameHUD.Toast("A friend jabbed you with a Rabies Shot. Cured!", ToastStyle.Good, 3f);
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void ShotResultRpc(bool paid, ulong target, RpcParams rp = default)
    {
        if (!paid) GameHUD.Toast($"Not enough cash for a Rabies Shot ({Economy.Money(NightShift.RabiesShotPrice)}).", ToastStyle.Bad, 2.5f);
        else
        {
            GameHUD.Toast($"You jabbed {HandsNet.NameOf(target)} with a Rabies Shot. What a friend.", ToastStyle.Good, 2.5f);
            Sfx.Play("squeak", null, 0.8f, 0.6f);
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Instance = null;
}
