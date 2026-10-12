using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>One Depot Row lot as every machine sees it (index = lot, 0 = "Lot 1", the host's).</summary>
public struct DepotLotState : INetworkSerializable, IEquatable<DepotLotState>
{
    public const byte FlagClaimed = 1, FlagOnline = 2;

    public byte lot, flags, color;
    public ulong ownerClientId;
    /// <summary>NetworkObjectId of the business truck (0 = none spawned).</summary>
    public ulong truckId;
    /// <summary>Bumped by the host every time the lot is claimed by a new business: clients play the plop when it changes.</summary>
    public int plopSerial;
    public FixedString64Bytes businessKey, ownerName, businessName;

    public bool Claimed => (flags & FlagClaimed) != 0;
    public bool Online => (flags & FlagOnline) != 0;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref lot);
        s.SerializeValue(ref flags);
        s.SerializeValue(ref color);
        s.SerializeValue(ref ownerClientId);
        s.SerializeValue(ref truckId);
        s.SerializeValue(ref plopSerial);
        s.SerializeValue(ref businessKey);
        s.SerializeValue(ref ownerName);
        s.SerializeValue(ref businessName);
    }

    public bool Equals(DepotLotState o) =>
        lot == o.lot && flags == o.flags && color == o.color && ownerClientId == o.ownerClientId && truckId == o.truckId &&
        plopSerial == o.plopSerial && businessKey.Equals(o.businessKey) && ownerName.Equals(o.ownerName) && businessName.Equals(o.businessName);

    public override bool Equals(object obj) => obj is DepotLotState o && Equals(o);
    public override int GetHashCode() => lot ^ (flags << 8) ^ (plopSerial << 16);
    public override string ToString() =>
        $"lot{lot + 1} {(Claimed ? (Online ? "open" : "BACK SOON") : "for lease")} owner={ownerClientId} '{ownerName}' '{businessName}' colour={color} truck={truckId}";
}

/// <summary>
/// Depot Row's replicated state and host logic (Docs/Multiplayer.md §13.2–§13.4, package N3). Lives on
/// <c>NetSession.prefab</c> (<c>Setup_Coop</c> adds every <see cref="NetSessionComponentAttribute"/>), so it exists in solo
/// too. There it hands out no lots: it only keeps <c>DepotRow</c> loaded next to the town (<see cref="ServerEnsureLegacyStreet"/>)
/// so the solo shop stands on lot 1 (presented by <see cref="DepotRowScene"/>, <see cref="DepotFlowRules"/>).
/// <list type="bullet">
/// <item><b>Host:</b> loads the town (Single) + <c>DepotRow</c> (Additive) through NGO, so everyone is in one world and
/// nobody is ever moved by someone else's door. Assigns each business the lowest free lot (host = lot 1; a rejoin gets
/// the same lot), spawns one truck per business at its lot after the plop, parks it inside when the owner leaves,
/// and validates renames.</item>
/// <item><b>Everyone:</b> reads <see cref="Lots"/>; <see cref="DepotLot"/> (in the DepotRow scene) presents it (plop,
/// sign, shutters, colours).</item>
/// </list>
/// Pattern: ChessController (§9 rule 10): NetworkVariables/List written by the host, Rpc requests validated on the host.
/// The messages of "call my truck" (Esc &gt; Unstuck on foot, <see cref="TruckCall"/>) live in <c>DepotRowNet.TruckCall.cs</c>.
/// </summary>
[NetSessionComponent(Order = 20)]
public partial class DepotRowNet : NetworkBehaviour
{
    public static DepotRowNet Instance { get; private set; }

    /// <summary>Raised on every machine when any lot changes (or this copy spawned).</summary>
    public static event Action Changed;

    NetworkList<DepotLotState> lots;
    readonly NetworkVariable<bool> oneWorld = new NetworkVariable<bool>(false);

    /// <summary>The co-op one world is on (Neighborhood + DepotRow loaded together, doors teleport).</summary>
    public bool OneWorld => oneWorld.Value;

    public NetworkList<DepotLotState> Lots => lots;

    // ---- host-only bookkeeping
    readonly Dictionary<string, int> lotByKey = new Dictionary<string, int>(StringComparer.Ordinal);
    readonly Dictionary<string, string> nameByKey = new Dictionary<string, string>(StringComparer.Ordinal);
    readonly float[] truckDueAt = new float[DepotRowLayout.LotCount];
    bool listening, worldLoading, syncQueued, legacyLoading, legacyMissingWarned;
    NetGuard renameGuard;

    /// <summary>Seconds between a lot plopping and its truck rolling out of the door.</summary>
    public const float TruckRollOutDelay = 2.4f;

    void Awake() => lots = new NetworkList<DepotLotState>();

    public override void OnNetworkSpawn()
    {
        Instance = this;
        legacyLoading = false;
        lots.OnListChanged += OnLotsChanged;
        oneWorld.OnValueChanged += OnOneWorldChanged;
        if (IsServer)
        {
            if (lots.Count != DepotRowLayout.LotCount)
            {
                lots.Clear();
                for (int i = 0; i < DepotRowLayout.LotCount; i++) lots.Add(new DepotLotState { lot = (byte)i, color = (byte)i });
            }
            renameGuard = new NetGuard(0.5f, 3, 256);
            SessionService.PlayerJoined += OnPlayerJoined;
            SessionService.PlayerLeft += OnPlayerLeft;
            NetworkManager.OnClientConnectedCallback += OnClientConnected;
            Businesses.Changed += QueueSync;
            listening = true;
            if (DepotRowWorld.HostWantsOneWorld) ServerBeginOneWorld();
        }
        RaiseChanged();
    }

    public override void OnNetworkDespawn()
    {
        StopListening();
        lots.OnListChanged -= OnLotsChanged;
        oneWorld.OnValueChanged -= OnOneWorldChanged;
        if (Instance == this) Instance = null;
        RaiseChanged();
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
        if (NetworkManager != null) NetworkManager.OnClientConnectedCallback -= OnClientConnected;
        Businesses.Changed -= QueueSync;
        listening = false;
    }

    void OnLotsChanged(NetworkListEvent<DepotLotState> e) => RaiseChanged();
    void OnOneWorldChanged(bool a, bool b) => RaiseChanged();

    static void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception e) { Debug.LogException(e); }
    }

    // =====================================================================
    // Queries (every machine)
    // =====================================================================

    public bool TryGetLot(int lot, out DepotLotState s)
    {
        if (lots != null && lot >= 0 && lot < lots.Count) { s = lots[lot]; return true; }
        s = default;
        return false;
    }

    /// <summary>The lot owned (claimed, online) by <paramref name="clientId"/>, or −1.</summary>
    public int LotOwnedBy(ulong clientId)
    {
        if (lots == null) return -1;
        for (int i = 0; i < lots.Count; i++)
            if (lots[i].Claimed && lots[i].Online && lots[i].ownerClientId == clientId) return i;
        return -1;
    }

    /// <summary>The lot whose business truck this is, or −1.</summary>
    public int LotOfTruck(ulong networkObjectId)
    {
        if (lots == null || networkObjectId == 0) return -1;
        for (int i = 0; i < lots.Count; i++)
            if (lots[i].truckId == networkObjectId) return i;
        return -1;
    }

    public string Describe()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(OneWorld ? "oneWorld " : "legacy ");
        if (lots != null) for (int i = 0; i < lots.Count; i++) sb.Append('[').Append(lots[i]).Append("] ");
        return sb.ToString();
    }

    // =====================================================================
    // Host: begin the one world
    // =====================================================================

    /// <summary>Host: turn on the one world (load town + DepotRow through NGO, then hand out lots).</summary>
    public void ServerBeginOneWorld()
    {
        if (!IsServer || worldLoading) return;
        oneWorld.Value = true;
        worldLoading = true;
        StartCoroutine(LoadWorld());
    }

    IEnumerator LoadWorld()
    {
        var nm = NetworkManager;
        string town = DepotRowWorld.TownSceneName;
        float giveUp = Time.realtimeSinceStartup + 60f;

        // The host started in the garage (ShopScene) or the menu's first scene: swap to the town (players respawn there).
        if (!string.IsNullOrEmpty(town) && !DepotRowWorld.IsSceneLoaded(town))
        {
            var st = nm.SceneManager.LoadScene(town, LoadSceneMode.Single);
            if (st != SceneEventProgressStatus.Started) Debug.LogError($"[DepotRow] Couldn't load the town '{town}': {st}");
            while (Time.realtimeSinceStartup < giveUp && !DepotRowWorld.IsSceneLoaded(town)) yield return null;
            yield return new WaitForSecondsRealtime(0.3f);
        }

        if (!DepotRowWorld.IsSceneLoaded(DepotRowLayout.SceneName))
        {
            if (!Application.CanStreamedLevelBeLoaded(DepotRowLayout.SceneName))
            {
                Debug.LogError("[DepotRow] DepotRow.unity isn't in the build settings (run Rubbish/Setup/DepotRow). Staying in the legacy flow.");
                oneWorld.Value = false;
                worldLoading = false;
                yield break;
            }
            SceneEventProgressStatus st;
            // NGO refuses a new scene event while the last one is still finishing: retry for a little while.
            do
            {
                st = nm.SceneManager.LoadScene(DepotRowLayout.SceneName, LoadSceneMode.Additive);
                if (st == SceneEventProgressStatus.Started) break;
                yield return new WaitForSecondsRealtime(0.25f);
            } while (Time.realtimeSinceStartup < giveUp);
            while (Time.realtimeSinceStartup < giveUp && !DepotRowWorld.IsSceneLoaded(DepotRowLayout.SceneName)) yield return null;
        }
        worldLoading = false;
        Debug.Log("[DepotRow] One world is up: town + Depot Row loaded.");
        NetAudit.Event("depotrow", "one world loaded");
        ServerSync(teleportPresent: true);
    }

    // =====================================================================
    // Host: the legacy flow's street (solo)
    // =====================================================================

    /// <summary>
    /// Host, legacy scene flow (solo / <c>-depotrow-off</c>): whenever the town is the loaded world, load <c>DepotRow</c>
    /// next to it through NGO, exactly like the one world does. Nobody claims a lot; <see cref="DepotRowScene"/> presents
    /// lot 1 as the shop and its door swaps to ShopScene. The next Single load (back into the garage) unloads it again.
    /// </summary>
    void ServerEnsureLegacyStreet()
    {
        if (legacyLoading || !DepotRowWorld.HostWantsLegacyStreet) return;
        if (SceneTransitionData.IsPending) return; // a door transition is mid-load: the town may be on its way out
        if (!DepotRowWorld.IsSceneLoaded(DepotRowWorld.TownSceneName) || DepotRowWorld.IsSceneLoaded(DepotRowLayout.SceneName)) return;
        if (!Application.CanStreamedLevelBeLoaded(DepotRowLayout.SceneName))
        {
            if (!legacyMissingWarned)
            {
                legacyMissingWarned = true;
                Debug.LogError("[DepotRow] DepotRow.unity isn't in the build settings (run Rubbish/Setup/DepotRow): the town has no shop building.");
            }
            return;
        }
        StartCoroutine(LoadLegacyStreet());
    }

    IEnumerator LoadLegacyStreet()
    {
        legacyLoading = true;
        var nm = NetworkManager;
        string town = DepotRowWorld.TownSceneName;
        float giveUp = Time.realtimeSinceStartup + 30f;
        bool started = false;
        // NGO refuses a new scene event while the town's own load is still finishing: retry for a little while.
        while (Time.realtimeSinceStartup < giveUp && nm != null && nm.SceneManager != null && !OneWorld &&
               DepotRowWorld.IsSceneLoaded(town) && !DepotRowWorld.IsSceneLoaded(DepotRowLayout.SceneName))
        {
            if (!started)
            {
                started = nm.SceneManager.LoadScene(DepotRowLayout.SceneName, LoadSceneMode.Additive) == SceneEventProgressStatus.Started;
                if (!started) { yield return new WaitForSecondsRealtime(0.1f); continue; }
            }
            yield return null;
        }
        if (DepotRowWorld.IsSceneLoaded(DepotRowLayout.SceneName))
        {
            Debug.Log("[DepotRow] Legacy flow: Depot Row's street side is up (lot 1 is the shop).");
            NetAudit.Event("depotrow", "legacy street loaded");
        }
        else if (DepotRowWorld.IsSceneLoaded(town)) Debug.LogWarning("[DepotRow] Legacy flow: couldn't load Depot Row next to the town.");
        legacyLoading = false;
    }

    // =====================================================================
    // Host: lots
    // =====================================================================

    void OnPlayerJoined(PlayerKey key, ulong clientId, int slot) => QueueSync();
    void OnPlayerLeft(PlayerKey key, ulong clientId) => QueueSync();
    void OnClientConnected(ulong clientId) => QueueSync();

    void QueueSync()
    {
        if (!IsServer) return;
        syncQueued = true;
    }

    void Update()
    {
        if (!IsSpawned || !IsServer) return;
        if (syncQueued && !worldLoading)
        {
            syncQueued = false;
            ServerSync(teleportPresent: false);
        }
        if (!OneWorld && !worldLoading) ServerEnsureLegacyStreet();
        if (!OneWorld || worldLoading) return;
        for (int i = 0; i < truckDueAt.Length; i++)
        {
            if (truckDueAt[i] <= 0f || Time.time < truckDueAt[i]) continue;
            truckDueAt[i] = 0f;
            ServerRollOutTruck(i);
        }
    }

    /// <summary>
    /// Host: make <see cref="Lots"/> match who is connected. Every connected player has a business; a business keeps
    /// its lot for the whole session (a rejoin gets it back). <paramref name="teleportPresent"/>: also walk players who
    /// were already spawned to their lot (the first load).
    /// </summary>
    public void ServerSync(bool teleportPresent)
    {
        if (!IsServer || !OneWorld || worldLoading) return;
        var nm = NetworkManager;

        // Who is here (host first, so the host always gets lot 1).
        var present = new List<(ulong clientId, string key)>();
        foreach (var (pk, clientId, _, _) in SessionService.Seated())
        {
            if (clientId != NetworkManager.ServerClientId && !nm.ConnectedClients.ContainsKey(clientId)) continue;
            present.Add((clientId, DepotBusinesses.KeyFor(clientId)));
        }
        if (!present.Exists(p => p.clientId == NetworkManager.ServerClientId) && nm.IsHost)
            present.Add((NetworkManager.ServerClientId, DepotBusinesses.KeyFor(NetworkManager.ServerClientId)));
        present.Sort((a, b) => a.clientId.CompareTo(b.clientId));

        var onlineKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in present) onlineKeys.Add(p.key);

        // 1) Owners who left: shutters down, truck parked inside.
        for (int i = 0; i < lots.Count; i++)
        {
            var s = lots[i];
            if (!s.Claimed || !s.Online) continue;
            if (onlineKeys.Contains(s.businessKey.ToString())) continue;
            s.flags = DepotLotState.FlagClaimed;
            lots[i] = s;
            truckDueAt[i] = 0f;
            ServerParkTruckInside(i);
            Debug.Log($"[DepotRow] {s.ownerName} left: lot {i + 1} shows BACK SOON.");
            NetAudit.Event("depotrow", $"lot {i + 1} back soon ({s.businessKey})");
        }

        // 2) Everyone present has a lot.
        foreach (var (clientId, key) in present)
        {
            int lot = FindLotForKey(key);
            bool fresh = false;
            if (lot < 0)
            {
                lot = LowestFreeLot();
                if (lot < 0)
                {
                    Debug.LogWarning($"[DepotRow] No free lot for {key} (Town is full).");
                    continue;
                }
                fresh = true;
                lotByKey[key] = lot;
            }
            var s = lots[lot];
            bool wasOnline = s.Claimed && s.Online && s.ownerClientId == clientId;
            if (wasOnline && !fresh && s.businessKey.ToString() == key)
            {
                // Still here: refresh what N1's registry may have learned since (display name, business name, colour).
                var before = s;
                s.ownerName = Fixed(DepotBusinesses.OwnerNameFor(clientId));
                s.businessName = Fixed(BusinessNameFor(key, clientId));
                s.color = (byte)DepotBusinesses.ColorIndexFor(clientId, lot);
                if (!s.Equals(before)) lots[lot] = s;
                TellRegistry(clientId, lot);
                if (teleportPresent) SendToLot(clientId, lot);
                continue;
            }
            bool reclaim = s.Claimed && s.businessKey.ToString() == key; // rejoin: same business, same lot
            s.lot = (byte)lot;
            s.flags = DepotLotState.FlagClaimed | DepotLotState.FlagOnline;
            s.ownerClientId = clientId;
            s.businessKey = Fixed(key);
            s.ownerName = Fixed(DepotBusinesses.OwnerNameFor(clientId));
            s.color = (byte)DepotBusinesses.ColorIndexFor(clientId, lot);
            s.businessName = Fixed(BusinessNameFor(key, clientId));
            if (!reclaim)
            {
                s.plopSerial++;
                s.truckId = 0;
            }
            lots[lot] = s;
            TellRegistry(clientId, lot);
            Debug.Log($"[DepotRow] {(reclaim ? "Welcome back" : "Plop!")} {s.ownerName} -> lot {lot + 1} ({s.businessName}, {DepotColors.Names[s.color % 4]}).");
            NetAudit.Event("depotrow", $"lot {lot + 1} {(reclaim ? "reopened" : "claimed")} by {key} client {clientId}");

            if (reclaim && TruckAlive(s.truckId)) ServerBringTruckOut(lot);
            else truckDueAt[lot] = Time.time + (reclaim ? 0.5f : TruckRollOutDelay);

            // Players already standing somewhere (the host on the first load, a guest whose lot just reopened) walk over.
            if (teleportPresent || reclaim || nm.SpawnManager.GetPlayerNetworkObject(clientId) != null) SendToLot(clientId, lot);
        }
    }

    /// <summary>A rename made here wins; else N1's registry name; else the default.</summary>
    string BusinessNameFor(string key, ulong clientId)
    {
        if (nameByKey.TryGetValue(key, out var renamed)) return renamed;
        return DepotBusinesses.NameFor(clientId) ?? DepotBusinesses.DefaultName;
    }

    /// <summary>N1's registry remembers each business's lot (its rejoin, other packages' lookups).</summary>
    void TellRegistry(ulong clientId, int lot)
    {
        string id = DepotBusinesses.BusinessIdFor(clientId);
        if (string.IsNullOrEmpty(id)) return;
        try { Businesses.AssignLot(id, lot); }
        catch (Exception e) { Debug.LogException(e); }
    }

    int FindLotForKey(string key)
    {
        if (lotByKey.TryGetValue(key, out int lot) && lot >= 0 && lot < lots.Count && lots[lot].businessKey.ToString() == key) return lot;
        for (int i = 0; i < lots.Count; i++) if (lots[i].Claimed && lots[i].businessKey.ToString() == key) return i;
        return -1;
    }

    int LowestFreeLot()
    {
        for (int i = 0; i < lots.Count; i++) if (!lots[i].Claimed) return i;
        // Every lot is held by someone: hand out the lowest lot whose owner is away (they get a fresh lot if they return).
        for (int i = 0; i < lots.Count; i++)
        {
            if (lots[i].Online) continue;
            var old = lots[i].businessKey.ToString();
            lotByKey.Remove(old);
            ServerDespawnTruck(i);
            var s = lots[i];
            s.flags = 0;
            lots[i] = s;
            return i;
        }
        return -1;
    }

    void SendToLot(ulong clientId, int lot)
    {
        try { GoToLotRpc((byte)lot, RpcTarget.Single(clientId, RpcTargetUse.Temp)); }
        catch (Exception e) { Debug.LogException(e); }
    }

    // =====================================================================
    // Host: trucks
    // =====================================================================

    bool TruckAlive(ulong id) => id != 0 && NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(id, out var no) && no != null;

    public TruckControllerV2 TruckOf(int lot)
    {
        if (!TryGetLot(lot, out var s) || s.truckId == 0 || NetworkManager == null) return null;
        return NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(s.truckId, out var no) && no != null ? no.GetComponent<TruckControllerV2>() : null;
    }

    NetworkObject TruckPrefab()
    {
        var list = NetworkManager.NetworkConfig.Prefabs?.Prefabs;
        if (list == null) return null;
        foreach (var p in list)
            if (p?.Prefab != null && p.Prefab.GetComponent<TruckControllerV2>() != null) return p.Prefab.GetComponent<NetworkObject>();
        return null;
    }

    void ServerRollOutTruck(int lot)
    {
        var s = lots[lot];
        if (!s.Claimed || !s.Online) return;
        if (TruckAlive(s.truckId)) { ServerBringTruckOut(lot); return; }
        var prefab = TruckPrefab();
        if (prefab == null) { Debug.LogError("[DepotRow] No truck prefab in the NetworkManager's prefab list."); return; }
        var pose = DepotRowLayout.StreetTruckRollout(lot);
        var inst = Instantiate(prefab, pose.position, pose.rotation);
        inst.DontDestroyWithOwner = true;
        inst.Spawn(destroyWithScene: true);
        s.truckId = inst.NetworkObjectId;
        lots[lot] = s;
        var rb = inst.GetComponent<Rigidbody>();
        if (rb != null && !rb.isKinematic) rb.linearVelocity = DepotRowLayout.StreetFacing * Vector3.forward * 3.2f;
        Debug.Log($"[DepotRow] Lot {lot + 1}'s truck rolls out (id {s.truckId}).");
        NetAudit.Event("depotrow", $"lot {lot + 1} truck {s.truckId}");
    }

    /// <summary>Host: the lot's truck comes back out onto its apron (a reopened lot).</summary>
    void ServerBringTruckOut(int lot)
    {
        var t = TruckOf(lot);
        if (t == null) return;
        var p = DepotRowLayout.StreetTruck(lot);
        t.ServerForceReleaseDriver();
        DepotRowWorld.TeleportTruck(t, p.position + Vector3.up * 0.2f, p.rotation);
    }

    void ServerParkTruckInside(int lot)
    {
        var t = TruckOf(lot);
        if (t == null) return;
        t.ServerForceReleaseDriver();
        var bay = DepotRowWorld.InteriorTruckPose(lot);
        DepotRowWorld.TeleportTruck(t, bay.position + Vector3.up * 0.2f, bay.rotation);
    }

    void ServerDespawnTruck(int lot)
    {
        var t = TruckOf(lot);
        var s = lots[lot];
        s.truckId = 0;
        lots[lot] = s;
        if (t == null) return;
        t.ServerForceReleaseDriver();
        var no = t.GetComponent<NetworkObject>();
        if (no != null && no.IsSpawned) no.Despawn(true);
    }

    // =====================================================================
    // Rename (owner → host)
    // =====================================================================

    /// <summary>Owner: ask the host to rename your business (≤ 24 characters, word filter).</summary>
    public void RequestRename(string name)
    {
        var f = new FixedString128Bytes();
        f.CopyFromTruncated(name ?? "");
        RenameRpc(f);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void RenameRpc(FixedString128Bytes name, RpcParams p = default)
    {
        ulong sender = p.Receive.SenderClientId;
        if (renameGuard != null && !renameGuard.Allow(sender, name.Length, Time.realtimeSinceStartupAsDouble)) return;
        int lot = LotOwnedBy(sender);
        if (lot < 0 || !Permissions.CanRename(sender, lot))
        {
            RenameResultRpc(false, Fixed("Only the owner can rename the business."), RpcTarget.Single(sender, RpcTargetUse.Temp));
            return;
        }
        string clean = DepotBusinesses.Clean(name.ToString());
        if (clean == null)
        {
            RenameResultRpc(false, Fixed("The sign guy won't paint that. Try another name."), RpcTarget.Single(sender, RpcTargetUse.Temp));
            return;
        }
        var s = lots[lot];
        s.businessName = Fixed(clean);
        lots[lot] = s;
        nameByKey[s.businessKey.ToString()] = clean;
        NetAudit.Event("depotrow", $"lot {lot + 1} renamed '{clean}'");
        RenameResultRpc(true, Fixed(clean), RpcTarget.Single(sender, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void RenameResultRpc(bool ok, FixedString64Bytes text, RpcParams p = default)
    {
        if (ok)
        {
            GameHUD.Toast($"New sign: {text}", ToastStyle.Good, 2.5f);
            Sfx.Play("ding");
            DepotBusinesses.RaiseLocalNameChanged(text.ToString());
        }
        else
        {
            GameHUD.Toast(text.ToString(), ToastStyle.Bad, 2.5f);
            Sfx.Play("buzz");
        }
    }

    // =====================================================================
    // Client presentation requests
    // =====================================================================

    /// <summary>Host → one player: walk over to your lot's apron (first load / reopened shop).</summary>
    [Rpc(SendTo.SpecifiedInParams)]
    void GoToLotRpc(byte lot, RpcParams p = default) => DepotRowWorld.LocalGoToStreet(lot, spawnSpot: true);

    static FixedString64Bytes Fixed(string s)
    {
        var f = new FixedString64Bytes();
        f.CopyFromTruncated(s ?? "");
        return f;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Instance = null;
        Changed = null;
    }
}
