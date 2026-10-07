using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>Host-owned depot compaction. Clients send intent, never amounts, inventories or victim lists.</summary>
[NetSessionComponent(Order = 27)]
public sealed class CompactorNet : NetworkBehaviour
{
    public static CompactorNet Instance { get; private set; }
    readonly NetworkVariable<byte> busyLots = new NetworkVariable<byte>();
    const string RequestGuard = "rb.compactor.press";

    public override void OnNetworkSpawn()
    {
        Instance = this;
        if (IsServer) SessionService.SetGuard(RequestGuard, new NetGuard(4f, 8, 32));
    }
    public override void OnNetworkDespawn() { if (Instance == this) Instance = null; }
    public override void OnDestroy() { if (Instance == this) Instance = null; base.OnDestroy(); }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Instance = null;

    public static bool IsBusy(int lot) => Instance != null && Instance.IsSpawned && lot >= 0 && lot < DepotRowLayout.LotCount && (Instance.busyLots.Value & 1 << lot) != 0;
    public void SetBusy(int lot, bool busy)
    {
        if (!IsServer || !IsSpawned || lot < 0 || lot >= DepotRowLayout.LotCount) return;
        busyLots.Value = (byte)(busy ? busyLots.Value | 1 << lot : busyLots.Value & ~(1 << lot));
    }
    static Compactor ForLot(int lot)
    {
        if (lot >= 0)
        {
            var depot = DepotRowScene.Lot(lot);
            return depot != null && depot.interior != null ? depot.interior.GetComponentInChildren<Compactor>(true) : null;
        }
        foreach (var c in Object.FindObjectsByType<Compactor>(FindObjectsSortMode.None))
            if (c.gameObject.scene.name == "ShopScene") return c; // coop:allow(scene) legacy single shop only
        return null;
    }
    static int Serial(int lot) => DepotRowNet.Instance != null && DepotRowNet.Instance.TryGetLot(lot, out var state) ? state.plopSerial : 0;

    public void Request(Compactor compactor, bool bulk)
    {
        int lot = DepotMachineContext.LotOf(compactor);
        if (IsServer) Handle(NetworkManager.LocalClientId, lot, Serial(lot), bulk, 0);
        else PressRpc((sbyte)lot, Serial(lot), bulk, 0);
    }
    public void RequestWholeBag(Compactor compactor, TrashBag bag)
    {
        if (IsServer && bag != null && bag.Holder == TrashCarrier.Local && compactor != null &&
            Permissions.LocalCanWorkAt(compactor.transform.position) &&
            NightPlayers.ForClient(NetworkManager.LocalClientId) is PlayerController player &&
            compactor.ContainsShopPoint(player.transform.position) && Vector3.Distance(player.transform.position, compactor.transform.position) <= 12f)
        { compactor.WholeBagHost(NetworkManager.LocalClientId, bag); return; }
        StartCoroutine(WaitForBag(compactor, bag));
    }
    IEnumerator WaitForBag(Compactor compactor, TrashBag bag)
    {
        float until = Time.unscaledTime + 3f;
        while (bag != null && bag.NetId == 0 && Time.unscaledTime < until) yield return null;
        if (compactor == null || bag == null || bag.State == CarryState.Consumed) yield break;
        if (bag.NetId == 0) { GameHUD.Toast("Bag isn't ready yet. Try again.", ToastStyle.Info); yield break; }
        int lot = DepotMachineContext.LotOf(compactor);
        if (IsServer) Handle(NetworkManager.LocalClientId, lot, Serial(lot), false, bag.NetId);
        else PressRpc((sbyte)lot, Serial(lot), false, bag.NetId);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void PressRpc(sbyte lot, int serial, bool bulk, uint bagId, RpcParams rpc = default)
    {
        ulong sender = rpc.Receive.SenderClientId;
        if (!SessionService.GuardFor(RequestGuard).Allow(sender, 16, Time.realtimeSinceStartupAsDouble)) return;
        Handle(sender, lot, serial, bulk, bagId);
    }

    void Handle(ulong sender, int lot, int serial, bool bulk, uint bagId)
    {
        if (!IsServer || lot < -1 || lot >= DepotRowLayout.LotCount) return;
        if (DepotRowWorld.Active && (lot < 0 || DepotRowNet.Instance == null || !DepotRowNet.Instance.TryGetLot(lot, out var state) ||
            !state.Claimed || !state.Online || state.plopSerial != serial)) return;
        if (!Permissions.CanWorkIn(sender, lot)) { Notify(sender, lot, bulk, false, Permissions.NotYourShop); return; }
        var c = ForLot(lot);
        var player = NightPlayers.ForClient(sender);
        if (c == null || !c.isActiveAndEnabled || player == null || !c.ContainsShopPoint(player.transform.position) ||
            Vector3.Distance(player.transform.position, c.transform.position) > 12f) return;
        if (bagId != 0)
        {
            var nc = HandsNet.Instance != null ? HandsNet.Instance.Get(bagId) : null;
            if (nc == null || !(nc.Item is TrashBag bag) || !nc.HeldBy(sender))
            { Notify(sender, lot, false, false, "Hold that bag first."); return; }
            c.WholeBagHost(sender, bag);
        }
        else c.PressHost(sender, bulk);
    }

    public void Notify(ulong actor, int lot, bool bulk, bool armed, string message)
    {
        if (!IsServer || !IsSpawned) return;
        NoticeRpc((sbyte)lot, bulk, armed, message, RpcTarget.Single(actor, RpcTargetUse.Temp));
    }
    [Rpc(SendTo.SpecifiedInParams)]
    void NoticeRpc(sbyte lot, bool bulk, bool armed, string message, RpcParams rpc = default)
    {
        var c = ForLot(lot);
        if (c != null) c.ApplyNotice(bulk, armed, message);
        else if (!string.IsNullOrEmpty(message)) GameHUD.Toast(message, ToastStyle.Info, 3f);
    }

    public void ShowStart(int lot, bool bulk, uint[] ids)
    {
        if (IsServer && IsSpawned) IntakeRpc((sbyte)lot, bulk, ids);
    }
    [Rpc(SendTo.NotServer)]
    void IntakeRpc(sbyte lot, bool bulk, uint[] ids)
    {
        var c = ForLot(lot);
        if (c == null || !c.isActiveAndEnabled) return;
        var stock = new List<Carryable>();
        foreach (uint id in ids)
        {
            var nc = HandsNet.Instance != null ? HandsNet.Instance.Get(id) : null;
            if (nc != null && nc.Item != null) stock.Add(nc.Item);
        }
        c.StartVisualIntake(bulk, stock);
    }
    public void ShowPress(int lot, bool bulk, float kg, bool hazardBang)
    {
        if (IsServer && IsSpawned) PressFxRpc((sbyte)lot, bulk, kg, hazardBang);
    }
    [Rpc(SendTo.NotServer)]
    void PressFxRpc(sbyte lot, bool bulk, float kg, bool hazardBang)
    {
        var c = ForLot(lot);
        if (c != null && c.isActiveAndEnabled) c.StartCoroutine(c.PresentationPress(bulk, kg, hazardBang));
    }

    public void RecordLoad(int lot, int bags, float kg, int lost, bool bulk)
    {
        if (!IsServer || !bulk) return;
        if (lot < 0) { ProgressionEvents.RaiseLoadCrushed(bags, kg, lost); return; }
        if (!BusinessUpgradeNet.TryOwnerForLot(lot, out string business, out ulong owner)) return;
        LoadFactRpc(business, bags, kg, lost, RpcTarget.Single(owner, RpcTargetUse.Temp));
    }
    [Rpc(SendTo.SpecifiedInParams)]
    void LoadFactRpc(string business, int bags, float kg, int lost, RpcParams rpc = default)
    {
        if (business != GameSession.BusinessId) return;
        if (GameSession.IsConnectedGuest) Progression.ApplyHostLoadCrushed();
        else ProgressionEvents.RaiseLoadCrushed(bags, kg, lost);
    }
}
