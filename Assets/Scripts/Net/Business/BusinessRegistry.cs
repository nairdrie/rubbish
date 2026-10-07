using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// The host's record of every business in the Town (Docs/Multiplayer.md §13.1, package N1), on <c>NetSession.prefab</c>
/// (<see cref="NetSessionComponentAttribute"/>; Setup_Coop builds it). Read it through <see cref="Businesses"/>.
/// <list type="number">
/// <item><b>Host business:</b> registered when the session starts, from the host's own save (GameSession); its cash follows
/// <see cref="GameSession.CashChanged"/>.</item>
/// <item><b>Guest businesses:</b> after joining, a guest picks one of its saved businesses or starts a new one
/// (<see cref="BusinessSlots"/>, <see cref="BusinessPicker"/>) and declares it here (<see cref="Declare"/>). The host keeps the
/// authoritative cash from then on; the guest's GameSession mirrors it (<see cref="GameSession.NetMirror"/>). When the guest
/// leaves, the entry closes and the guest's own game saves the business back to its own files (the host never writes it).</item>
/// <item><b>Money</b> for another business comes through <see cref="Ledger"/> (<see cref="ServerApplyCash"/>); a guest's own
/// Ledger calls arrive as requests (<see cref="LedgerRequestRpc"/>: rate- and size-limited, own business only).</item>
/// </list>
/// Every message from a guest goes through <see cref="SessionService.GuardFor"/>.
/// </summary>
[NetSessionComponent(Order = 5)]
public partial class BusinessRegistry : NetworkBehaviour
{
    public static BusinessRegistry Instance { get; private set; }

    /// <summary>Largest single cash change a guest may ask for ($50,000): a rare find sold on ebae fits, a minted million doesn't.</summary>
    public const long MaxRequestCents = 5_000_000;
    const long MaxDeclaredCents = 100_000_000_00; // $100M: anything bigger is a broken save
    const string MsgDeclare = "rb.biz.declare", MsgLedger = "rb.biz.ledger", MsgCapacity = "rb.biz.capacity";
    const float MaxTruckCapacityKg = 100000f;
    const float CapacitySendInterval = 0.25f;

    static readonly string[] Kinds = { Ledger.KindCredit, Ledger.KindCharge, Ledger.KindSpend, Ledger.KindRep };

    NetworkList<BusinessInfo> list;
    bool hostHooked, guestHooked;
    string pendingBusinessId, acceptedBusinessId;
    float declaredCapacityKg, lastCapacitySentAt;
    bool awaitingDeclaration, capacityPending;

    // host only
    readonly Dictionary<string, List<BagContents>> cargo = new Dictionary<string, List<BagContents>>(StringComparer.Ordinal);
    readonly Dictionary<string, int> rememberedLots = new Dictionary<string, int>(StringComparer.Ordinal);
    readonly Dictionary<string, long> openingCents = new Dictionary<string, long>(StringComparer.Ordinal);
    readonly Dictionary<string, long> postedCents = new Dictionary<string, long>(StringComparer.Ordinal);

    /// <summary>Host-written, read everywhere (use <see cref="Businesses"/>).</summary>
    public NetworkList<BusinessInfo> List => list;

    /// <summary>Guest: the host accepted (true) or refused (false, with a sentence) the business this game declared.</summary>
    public static event Action<bool, string> DeclareAnswered;

    /// <summary>Host: a guest's business is about to close (its owner left): (business id, owner client id). Raised while the
    /// entry still exists, so N4 payroll can settle final wages through the Ledger first.</summary>
    public static event Action<string, ulong> Closing;

    void Awake() { list = new NetworkList<BusinessInfo>(); AwakeWorkshop(); }

    public override void OnNetworkSpawn()
    {
        Instance = this;
        list.OnListChanged += OnListChanged;
        if (IsServer)
        {
            RegisterHostBusiness();
            GameSession.CashChanged += OnHostCashChanged;
            GameSession.Changed += OnHostChanged;
            SessionService.PlayerLeft += OnPlayerLeft;
            hostHooked = true;
        }
        else
        {
            GameSession.Changed += OnGuestChanged;
            guestHooked = true;
            BusinessSlots.OnJoinedTown(this);
        }
        SpawnWorkshop();
        Businesses.RaiseChanged();
    }

    public override void OnNetworkDespawn()
    {
        list.OnListChanged -= OnListChanged;
        Unhook();
        if (IsServer) AuditAll("session end");
        if (Instance == this) Instance = null;
        Businesses.RaiseChanged();
    }

    public override void OnDestroy()
    {
        Unhook();
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    void Unhook()
    {
        UnhookWorkshop();
        if (hostHooked)
        {
            GameSession.CashChanged -= OnHostCashChanged;
            GameSession.Changed -= OnHostChanged;
            SessionService.PlayerLeft -= OnPlayerLeft;
            Ledger.PostedAnyBusiness -= OnPostedAny;
            hostHooked = false;
        }
        if (guestHooked)
        {
            GameSession.Changed -= OnGuestChanged;
            guestHooked = false;
        }
        pendingBusinessId = acceptedBusinessId = null;
        awaitingDeclaration = capacityPending = false;
    }

    void OnListChanged(NetworkListEvent<BusinessInfo> e)
    {
        if (!IsServer && BusinessSlots.Declared)
        {
            int local = IndexOf(GameSession.BusinessId);
            if (local >= 0) MunicipalEconomy.SaveLocalHours(list[local].municipalHours);
        }
        Businesses.RaiseChanged();
    }

    int IndexOf(string id)
    {
        if (string.IsNullOrEmpty(id)) return -1;
        for (int i = 0; i < list.Count; i++) if (list[i].id.ToString() == id) return i;
        return -1;
    }

    int IndexOfClient(ulong clientId)
    {
        for (int i = 0; i < list.Count; i++) if (list[i].ownerClientId == clientId) return i;
        return -1;
    }

    // ------------------------------------------------------------------ host: the host's own business

    void RegisterHostBusiness()
    {
        var key = NetSession.LocalPlayerKey;
        int color = SessionService.Slots.TryGet(key, out _, out int slot) ? slot : 0;
        var b = new BusinessInfo
        {
            ownerClientId = NetworkManager.ServerClientId,
            cashCents = GameSession.CashCents,
            color = (byte)Mathf.Clamp(color, 0, BusinessColors.Count - 1),
            lot = -1,
            flags = BusinessInfo.FlagHost,
            truckKg = GameSession.TruckLoadKg,
            truckCapKg = GameSession.TruckCapacityKg,
            municipalHours = MunicipalEconomy.LocalHours,
        };
        b.id.CopyFromTruncated(GameSession.EnsureBusinessId());
        b.ownerKey.CopyFromTruncated(key.Value ?? "");
        b.name.CopyFromTruncated(GameSession.BusinessName);
        b.ownerName.CopyFromTruncated(DisplayName(key));
        int i = IndexOf(b.Id);
        if (i >= 0) list[i] = b;
        else list.Add(b);
        openingCents[b.Id] = b.cashCents;
        municipalSince[b.Id] = GameClock.TotalHours;
        postedCents[b.Id] = 0;
        Ledger.PostedAnyBusiness -= OnPostedAny;
        Ledger.PostedAnyBusiness += OnPostedAny;
        NetAudit.Event("business", new Dictionary<string, object>
        {
            { "op", "open" }, { "id", b.Id }, { "name", b.Name }, { "owner", key.Value }, { "host", true }, { "cash", b.cashCents }, { "color", color },
        });
    }

    void OnHostCashChanged(float delta, string reason, Vector3? pos) => SyncHostEntry();

    void OnHostChanged() => SyncHostEntry();

    void SyncHostEntry()
    {
        if (!IsServer || !IsSpawned) return;
        int i = IndexOf(GameSession.BusinessId);
        if (i < 0) return;
        var b = list[i];
        var n = b;
        n.cashCents = GameSession.CashCents;
        n.name.CopyFromTruncated(GameSession.BusinessName);
        n.truckKg = Mathf.Round(GameSession.TruckLoadKg * 10f) / 10f;
        n.truckCapKg = GameSession.TruckCapacityKg;
        if (!n.Equals(b)) list[i] = n;
    }

    static string DisplayName(PlayerKey key)
    {
        try { return SessionService.DisplayNameFor?.Invoke(key) ?? ""; }
        catch (Exception e) { Debug.LogException(e); return ""; }
    }

    // ------------------------------------------------------------------ host: guests' businesses

    /// <summary>Guest: tell the host which business this player brought (BusinessSlots calls it after the pick).</summary>
    public void Declare(string id, string name, long cashCents, float truckCapKg)
    {
        if (IsServer) return;
        // A pick can load another local business before the old declaration's answer arrives.
        // Only the accepted pick may subsequently change its own registered capacity.
        pendingBusinessId = id;
        acceptedBusinessId = null;
        awaitingDeclaration = true;
        capacityPending = false;
        declaredCapacityKg = truckCapKg;
        var fid = new FixedString64Bytes();
        fid.CopyFromTruncated(id ?? "");
        var fname = new FixedString64Bytes();
        fname.CopyFromTruncated(BusinessColors.CleanName(name));
        DeclareRpc(fid, fname, cashCents, truckCapKg, MunicipalEconomy.LocalHours);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void DeclareRpc(FixedString64Bytes id, FixedString64Bytes name, long cashCents, float truckCapKg, float municipalHours, RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        if (!Allowed(MsgDeclare, sender, 160)) return;
        string sid = id.ToString();
        if (!SessionService.TryGetPlayer(sender, out var key)) { Answer(sender, false, "The host doesn't know you yet. Try again."); return; }
        if (!BusinessIds.IsValid(sid)) { Answer(sender, false, "The host couldn't read that business."); return; }
        int other = IndexOf(sid);
        if (other >= 0 && list[other].ownerClientId != sender)
        {
            Answer(sender, false, "That business is already open in this Town. Pick another one or start a new one.");
            return;
        }
        int mine = IndexOfClient(sender);
        if (mine >= 0 && list[mine].Id == sid)
        {
            // A retransmitted declaration must not restore old cash or reset earned startup-protection hours.
            Answer(sender, true, sid);
            return;
        }
        if (mine >= 0 && list[mine].Id != sid)
        {
            AuditClose(list[mine], "re-picked");
            list.RemoveAt(mine);
        }
        int color = SessionService.TryGetSlot(sender, out int slot) ? slot : 0;
        var b = new BusinessInfo
        {
            ownerClientId = sender,
            cashCents = Math.Max(-MaxDeclaredCents, Math.Min(MaxDeclaredCents, cashCents)),
            color = (byte)Mathf.Clamp(color, 0, BusinessColors.Count - 1),
            lot = (sbyte)(rememberedLots.TryGetValue(sid, out int lot) ? lot : -1),
            truckCapKg = float.IsNaN(truckCapKg) || truckCapKg <= 0f ? Economy.StartingTruckCapacityKg : Mathf.Min(truckCapKg, MaxTruckCapacityKg),
            municipalHours = MunicipalEconomy.SanitizeHours(municipalHours),
        };
        b.id = id;
        b.ownerKey.CopyFromTruncated(key.Value ?? "");
        b.name.CopyFromTruncated(BusinessColors.CleanName(name.ToString()));
        b.ownerName.CopyFromTruncated(DisplayName(key));
        int at = IndexOf(sid);
        if (at >= 0) list[at] = b;
        else list.Add(b);
        if (!cargo.ContainsKey(sid)) cargo[sid] = new List<BagContents>();
        openingCents[sid] = b.cashCents;
        municipalSince[sid] = GameClock.TotalHours;
        postedCents[sid] = 0;
        Debug.Log($"[Businesses] {key} opened {b.Name} ({sid}) in {BusinessColors.Name(color)}, ${b.Cash:0.00}.");
        NetAudit.Event("business", new Dictionary<string, object>
        {
            { "op", "open" }, { "id", sid }, { "name", b.Name }, { "owner", key.Value }, { "host", false }, { "cash", b.cashCents }, { "color", color },
        });
        Answer(sender, true, sid);
    }

    void Answer(ulong client, bool ok, string text)
    {
        var t = new FixedString128Bytes();
        t.CopyFromTruncated(text ?? "");
        if (!ok) Debug.Log($"[Businesses] Refused client {client}'s business: {text}");
        DeclareResultRpc(ok, t, RpcTarget.Single(client, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void DeclareResultRpc(bool ok, FixedString128Bytes text, RpcParams rpcParams)
    {
        if (IsServer) return;
        string answer = text.ToString();
        bool acceptedCurrent = ok && awaitingDeclaration && answer == pendingBusinessId && answer == GameSession.BusinessId;
        if (acceptedCurrent)
        {
            acceptedBusinessId = answer;
            awaitingDeclaration = false;
        }
        else if (!ok && awaitingDeclaration) awaitingDeclaration = false;
        BusinessSlots.OnDeclareAnswer(ok, answer);
        if (acceptedCurrent) QueueGuestCapacity(); // catches an upgrade made while the declaration was in flight
        try { DeclareAnswered?.Invoke(ok, answer); }
        catch (Exception e) { Debug.LogException(e); }
    }

    /// <summary>Guest: send only a changed, valid capacity after its own business declaration is accepted.</summary>
    void OnGuestChanged() => QueueGuestCapacity();

    static bool ValidTruckCapacity(float kg) => !float.IsNaN(kg) && !float.IsInfinity(kg) && kg > 0f && kg <= MaxTruckCapacityKg;

    void QueueGuestCapacity()
    {
        if (!IsSpawned || IsServer || awaitingDeclaration || !BusinessSlots.Declared ||
            string.IsNullOrEmpty(acceptedBusinessId) || GameSession.BusinessId != acceptedBusinessId) return;
        float cap = GameSession.TruckCapacityKg;
        if (ValidTruckCapacity(cap) && !Mathf.Approximately(cap, declaredCapacityKg)) capacityPending = true;
    }

    void Update()
    {
        TickMunicipalHours();
        TickWorkshop();
        if (!capacityPending || !IsSpawned || IsServer || awaitingDeclaration || !BusinessSlots.Declared ||
            GameSession.BusinessId != acceptedBusinessId || Time.realtimeSinceStartup < lastCapacitySentAt + CapacitySendInterval) return;
        float cap = GameSession.TruckCapacityKg;
        if (!ValidTruckCapacity(cap)) return;
        capacityPending = false;
        if (Mathf.Approximately(cap, declaredCapacityKg)) return;
        var id = new FixedString64Bytes();
        id.CopyFromTruncated(acceptedBusinessId);
        CapacityRpc(id, cap);
        declaredCapacityKg = cap;
        lastCapacitySentAt = Time.realtimeSinceStartup;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void CapacityRpc(FixedString64Bytes id, float capacityKg, RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        if (!Allowed(MsgCapacity, sender, 80) || !ValidTruckCapacity(capacityKg)) return;
        int i = IndexOfClient(sender);
        if (i < 0 || list[i].IsHostBusiness || !list[i].id.Equals(id)) return;
        var b = list[i];
        if (Mathf.Approximately(b.truckCapKg, capacityKg)) return;
        b.truckCapKg = capacityKg;
        list[i] = b; // preserve the existing cargo, cash, lot and business identity
    }

    /// <summary>Guest: this game's business was renamed (Businesses.RenameLocal).</summary>
    internal void SendRename(string name)
    {
        if (IsServer || !BusinessSlots.Declared) return;
        var f = new FixedString64Bytes();
        f.CopyFromTruncated(BusinessColors.CleanName(name));
        RenameRpc(f);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void RenameRpc(FixedString64Bytes name, RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        if (!Allowed(MsgDeclare, sender, 64)) return;
        int i = IndexOfClient(sender);
        if (i < 0) return;
        var b = list[i];
        b.name.CopyFromTruncated(BusinessColors.CleanName(name.ToString()));
        if (!b.Equals(list[i])) list[i] = b;
    }

    void OnPlayerLeft(PlayerKey key, ulong clientId)
    {
        if (!IsServer || !IsSpawned) return;
        int i = IndexOfClient(clientId);
        if (i < 0 || list[i].IsHostBusiness) return;
        try { Closing?.Invoke(list[i].Id, clientId); }
        catch (Exception e) { Debug.LogException(e); }
        i = IndexOfClient(clientId);
        if (i < 0) return;
        var b = list[i];
        if (b.lot >= 0) rememberedLots[b.Id] = b.lot;
        AuditClose(b, "left");
        cargo.Remove(b.Id);
        list.RemoveAt(i);
        Debug.Log($"[Businesses] {b.Name} ({b.Id}) closed: {key} went home with it (${b.Cash:0.00}).");
    }

    // ------------------------------------------------------------------ host: lots, trucks, cargo

    internal bool ServerAssignLot(string id, int lot)
    {
        int i = IndexOf(id);
        if (i < 0 || lot < -1 || lot >= NetConstants.MaxCompanies) return false;
        var b = list[i];
        if (b.lot == lot) return true;
        b.lot = (sbyte)lot;
        list[i] = b;
        if (lot >= 0) rememberedLots[id] = lot;
        return true;
    }

    internal List<BagContents> ServerCargo(string id)
    {
        if (string.IsNullOrEmpty(id) || IndexOf(id) < 0) return null;
        if (id == GameSession.BusinessId) return null; // the host's own truck is GameSession.TruckBags
        if (!cargo.TryGetValue(id, out var c)) cargo[id] = c = new List<BagContents>();
        return c;
    }

    internal void ServerSetTruck(string id, float kg, float capacityKg)
    {
        int i = IndexOf(id);
        if (i < 0) return;
        var b = list[i];
        float k = Mathf.Round(kg * 10f) / 10f;
        if (Mathf.Approximately(b.truckKg, k) && Mathf.Approximately(b.truckCapKg, capacityKg)) return;
        b.truckKg = k;
        b.truckCapKg = capacityKg;
        list[i] = b;
    }

    // ------------------------------------------------------------------ host: money

    internal bool ServerApplyCash(string id, string kind, long cents, string reason, string ledgerId, Vector3? pos, string districtId, bool allowOverdraw)
    {
        if (cents == 0) return false;
        if (id == GameSession.BusinessId)
        {
            // The host's own business (a scope naming it explicitly): the normal local path.
            using (Ledger.ToBusiness(null))
            {
                if (kind == Ledger.KindSpend) return Ledger.Spend(-cents / 100f, reason, ledgerId);
                if (cents > 0) Ledger.Credit(cents / 100f, reason, ledgerId, pos, districtId);
                else Ledger.Charge(-cents / 100f, reason, ledgerId, pos, districtId);
                return true;
            }
        }
        int i = IndexOf(id);
        if (i < 0)
        {
            Debug.LogWarning($"[Businesses] No business {id} in this Town: \"{reason}\" ({cents}c) dropped.");
            return false;
        }
        var b = list[i];
        if (kind == Ledger.KindSpend && b.cashCents + cents < 0 && !allowOverdraw) return false;
        b.cashCents += cents;
        list[i] = b;
        SendCash(b, kind, cents, reason, ledgerId, pos, districtId);
        Ledger.PostApplied(kind, ledgerId, cents, reason, districtId, pos, id, local: false);
        return true;
    }

    internal bool ServerApplyRep(string id, float applied, string repId, string districtId, Vector3? pos)
    {
        int i = IndexOf(id);
        if (i < 0 || id == GameSession.BusinessId) return false;
        var b = list[i];
        RepRpc(applied, Fixed32(repId), Fixed32(districtId), pos ?? Vector3.zero, pos.HasValue,
               RpcTarget.Single(b.ownerClientId, RpcTargetUse.Temp));
        Ledger.PostAppliedRep(repId, applied, districtId, pos, id);
        return true;
    }

    void SendCash(BusinessInfo b, string kind, long cents, string reason, string ledgerId, Vector3? pos, string districtId)
    {
        var r = new FixedString128Bytes();
        r.CopyFromTruncated(reason ?? "");
        CashRpc(b.cashCents, cents, KindByte(kind), r, Fixed32(ledgerId), Fixed32(districtId), pos ?? Vector3.zero, pos.HasValue,
                RpcTarget.Single(b.ownerClientId, RpcTargetUse.Temp));
    }

    /// <summary>Owner (guest): the host applied a change to this game's business.</summary>
    [Rpc(SendTo.SpecifiedInParams)]
    void CashRpc(long balanceCents, long deltaCents, byte kind, FixedString128Bytes reason, FixedString32Bytes ledgerId,
                 FixedString32Bytes districtId, Vector3 pos, bool hasPos, RpcParams rpcParams)
    {
        if (IsServer) return;
        Vector3? at = hasPos ? pos : (Vector3?)null;
        string why = reason.ToString(), district = Str(districtId);
        // NPC ebae sales remove the held item in the SAME callback as payment, before a guest can
        // disconnect/checkpoint. A second sale RPC is only an idempotent fallback, not half a sale.
        if (!EbaeNeighbours.TryApplyNpcCash(balanceCents, why))
            GameSession.NetMirror.SetCashCents(balanceCents, deltaCents, why, at);
        Ledger.PostApplied(KindName(kind), Str(ledgerId), deltaCents, why, district, at, Businesses.LocalId, local: true);
    }

    /// <summary>Owner (guest): Rep the host awarded this game's business.</summary>
    [Rpc(SendTo.SpecifiedInParams)]
    void RepRpc(float applied, FixedString32Bytes repId, FixedString32Bytes districtId, Vector3 pos, bool hasPos, RpcParams rpcParams)
    {
        if (IsServer || float.IsNaN(applied) || Mathf.Abs(applied) > 100000f) return;
        Vector3? at = hasPos ? pos : (Vector3?)null;
        string id = Str(repId), district = Str(districtId);
        GameSession.AddRep(applied, id, district, at);
        Ledger.PostAppliedRep(id, applied, district, at, Businesses.LocalId);
    }

    /// <summary>Guest: send one of this player's own Ledger calls to the host.</summary>
    internal void SendLedgerRequest(string kind, long cents, string reason, string ledgerId, Vector3? pos, string districtId)
    {
        if (!BusinessSlots.Declared)
        {
            Debug.Log($"[Businesses] \"{reason}\" ({cents}c) dropped: pick your business first.");
            return;
        }
        var r = new FixedString128Bytes();
        r.CopyFromTruncated(reason ?? "");
        LedgerRequestRpc(KindByte(kind), cents, r, Fixed32(ledgerId), Fixed32(districtId), pos ?? Vector3.zero, pos.HasValue);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void LedgerRequestRpc(byte kind, long cents, FixedString128Bytes reason, FixedString32Bytes ledgerId, FixedString32Bytes districtId,
                          Vector3 pos, bool hasPos, RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        if (!Allowed(MsgLedger, sender, 220)) return;
        int i = IndexOfClient(sender);
        string k = KindName(kind);
        bool signOk = k == Ledger.KindCredit ? cents > 0 : (k == Ledger.KindCharge || k == Ledger.KindSpend) && cents < 0;
        if (i < 0 || !signOk || Math.Abs(cents) > MaxRequestCents || list[i].IsHostBusiness)
        {
            NetAudit.Event("biz-refused", new Dictionary<string, object> { { "client", sender }, { "kind", k }, { "cents", cents }, { "reason", reason.ToString() } });
            return;
        }
        string id = list[i].Id;
        // A guest's spend already happened on its machine (it checked its balance): applied even if a race overdraws it.
        ServerApplyCash(id, k, cents, reason.ToString(), Str(ledgerId), hasPos ? pos : (Vector3?)null, Str(districtId), allowOverdraw: true);
    }

    // ------------------------------------------------------------------ audit

    void OnPostedAny(LedgerEntry e)
    {
        if (e.kind == Ledger.KindRep || string.IsNullOrEmpty(e.businessId)) return;
        postedCents[e.businessId] = (postedCents.TryGetValue(e.businessId, out long p) ? p : 0) + e.cents;
        if (e.businessId != GameSession.BusinessId)
            NetAudit.Event("bizledger", new Dictionary<string, object>
            {
                { "business", e.businessId }, { "op", e.kind }, { "id", e.id }, { "cents", e.cents }, { "reason", e.reason },
                { "balance", IndexOf(e.businessId) >= 0 ? list[IndexOf(e.businessId)].cashCents : 0 },
            });
    }

    void AuditClose(BusinessInfo b, string why)
    {
        string id = b.Id;
        long open = openingCents.TryGetValue(id, out long o) ? o : 0;
        long posted = postedCents.TryGetValue(id, out long p) ? p : 0;
        bool balanced = open + posted == b.cashCents;
        NetAudit.Event("business", new Dictionary<string, object>
        {
            { "op", "close" }, { "why", why }, { "id", id }, { "name", b.Name }, { "owner", b.ownerKey.ToString() }, { "opening", open },
            { "posted", posted }, { "cash", b.cashCents }, { "balanced", balanced },
        });
        if (!balanced) Debug.LogError($"[Businesses] Ledger doesn't balance for {b.Name} ({id}): opened {open}c + posted {posted}c != {b.cashCents}c.");
    }

    void AuditAll(string why)
    {
        for (int i = 0; i < list.Count; i++) AuditClose(list[i], why);
    }

    // ------------------------------------------------------------------ helpers

    static bool Allowed(string msg, ulong sender, int bytes)
    {
        if (SessionService.GuardFor(msg).Allow(sender, bytes, Time.realtimeSinceStartupAsDouble)) return true;
        NetAudit.Event("guard", $"{msg} from client {sender} over its limit");
        return false;
    }

    static byte KindByte(string kind)
    {
        int i = Array.IndexOf(Kinds, kind);
        return (byte)(i < 0 ? 0 : i);
    }

    static string KindName(byte kind) => kind < Kinds.Length ? Kinds[kind] : Ledger.KindCredit;

    static FixedString32Bytes Fixed32(string s)
    {
        var f = new FixedString32Bytes();
        f.CopyFromTruncated(s ?? "");
        return f;
    }

    static string Str(FixedString32Bytes f) => f.Length == 0 ? null : f.ToString();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Instance = null;
        DeclareAnswered = null;
        Closing = null;
    }

    // NetAuditNet resets its probe table at SubsystemRegistration: register after that.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void RegisterProbe() => NetAuditNet.RegisterProbe("business", BusinessProbe);

    /// <summary>NetAudit hash domain "business": every machine must agree on the business list (id, cash, colour, lot, name).</summary>
    static bool BusinessProbe(NetworkManager nm, ulong forClient, out ulong hash, out int count)
    {
        hash = WorldHash.Seed;
        count = 0;
        var r = Instance;
        if (r == null || !r.IsSpawned) return false;
        var rows = new List<BusinessInfo>(r.list.Count);
        for (int i = 0; i < r.list.Count; i++) rows.Add(r.list[i]);
        rows.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        foreach (var b in rows)
        {
            hash = WorldHash.Fnv(hash, b.Id);
            hash = WorldHash.Fnv(hash, b.cashCents);
            hash = WorldHash.Fnv(hash, b.color);
            hash = WorldHash.Fnv(hash, b.lot);
            hash = WorldHash.Fnv(hash, b.Name);
            hash = WorldHash.Fnv(hash, unchecked((long)b.ownerClientId));
        }
        count = rows.Count;
        return true;
    }
}
