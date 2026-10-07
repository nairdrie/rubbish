using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Two-bot regression for offscreen depot sorting physics. The guest drops a real networked loose item over its
/// table. Once HandsNet transfers Rest authority to the host, the host checks that the item remains on the table
/// in the guest-owned depot even though that depot's visible interior is switched off on the host.
/// Run with -botbrain tableproxy on two bots in the isolated bot build.
/// </summary>
public sealed class DepotTableProxyBot : IBotBrain
{
    bool spawned, checkedOnce, checkedFinal;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Register() => BotBrains.Register("tableproxy", () => new DepotTableProxyBot());

    public void Begin(BotContext c) { }
    public void End(BotContext c) { }

    public void Tick(BotContext c, float dt)
    {
        if (!DepotRowWorld.Active || DepotRowScene.Instance == null || HandsNet.Instance == null) return;

        if (c.IsGuest && !spawned && c.Elapsed >= 15f)
        {
            int lot = DepotRowWorld.LocalLot;
            var depot = DepotRowScene.Lot(lot);
            var table = depot != null && depot.interior != null
                ? depot.interior.GetComponentInChildren<SortingTable>(true) : null;
            if (lot < 0 || table == null || !depot.InteriorOn) return;
            var item = new ItemInstance
            {
                uid = Key(lot), defId = "banana_peel", massKg = 0.2f, condition = 1f
            };
            LooseItem.Spawn(item, table.TopPoint(0f, 0f) + Vector3.up * 0.45f,
                Quaternion.identity, Vector3.down * 0.5f);
            spawned = true;
            Log("guest-spawn", true, lot, table.TopWorldY, 0f, "local item above own table");
        }

        if (c.Elapsed >= 26f && !checkedOnce)
        {
            checkedOnce = true;
            Check(c, "first-rest");
        }
        if (c.Elapsed >= 36f && !checkedFinal)
        {
            checkedFinal = true;
            Check(c, "stable-rest");
        }
    }

    static string Key(int lot) => $"bot-table-proxy-lot-{lot + 1}";

    static void Check(BotContext c, string phase)
    {
        int lot = -1;
        if (c.IsHost)
        {
            var net = DepotRowNet.Instance;
            for (int i = 0; i < DepotRowLayout.LotCount; i++)
                if (i != DepotRowWorld.LocalLot && net != null && net.TryGetLot(i, out var s) && s.Claimed)
                { lot = i; break; }
        }
        else lot = DepotRowWorld.LocalLot;

        var depot = DepotRowScene.Lot(lot);
        var table = depot != null && depot.interior != null
            ? depot.interior.GetComponentInChildren<SortingTable>(true) : null;
        NetCarry found = null;
        var hands = HandsNet.Instance;
        if (hands != null)
            foreach (var kv in hands.Items)
                if (kv.Value != null && kv.Value.Item is LooseItem li && li.Item != null && li.Item.uid == Key(lot))
                { found = kv.Value; break; }

        var loose = found != null ? found.Item as LooseItem : null;
        float top = table != null ? table.TopWorldY : 0f;
        float delta = loose != null ? loose.transform.position.y - top : -999f;
        bool support = depot != null && (c.IsHost ? depot.TableRestColliderOn && !depot.InteriorOn
            : depot.InteriorOn && !depot.TableRestColliderOn);
        bool rest = found != null && found.St.Phase == HandsPhase.Rest;
        bool authority = found != null && found.LocalSim == c.IsHost;
        bool bodyMatches = loose != null && loose.Body != null && loose.Body.isKinematic != c.IsHost;
        bool onTable = table != null && loose != null && table.IsOnTop(loose.transform.position)
            && delta >= -0.12f && delta <= 0.24f;
        bool pass = lot >= 0 && support && rest && authority && bodyMatches && onTable;
        Log(phase, pass, lot, top, delta,
            $"host={c.IsHost} support={support} rest={rest} authority={authority} bodyMatches={bodyMatches} onTable={onTable} netId={(found != null ? found.Id : 0)}");
    }

    static void Log(string phase, bool pass, int lot, float top, float delta, string detail)
    {
        NetAudit.Event("tableproxy", new Dictionary<string, object>
        {
            { "phase", phase }, { "pass", pass }, { "lot", lot + 1 },
            { "topY", top }, { "itemAboveTop", delta }, { "detail", detail }
        });
        Debug.Log($"[DepotTableProxyBot] {phase}: {(pass ? "PASS" : "FAIL")} lot={lot + 1} {detail}");
    }
}

/// <summary>
/// Employee variant for the reported case: a guest is hired into the host's depot, enters it, slams a one-item
/// bag on that table and watches the spilled item through guest Flight to host-owned Rest. This also checks that
/// opening the employer interior on the employee machine does not leave its table uncollidable.
/// Run with -botbrain tableproxyemployee on two bots.
/// </summary>
public sealed class DepotTableProxyEmployeeBot : IBotBrain
{
    const string ItemKey = "bot-table-employee-item";
    bool employee, offered, entered, slammed, firstChecked, finalChecked;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Register() => BotBrains.Register("tableproxyemployee", () => new DepotTableProxyEmployeeBot());

    public void Begin(BotContext c)
    {
        employee = c.IsGuest || (c.Options.label != null && c.Options.label.EndsWith("bot-2"));
        if (employee) Payroll.OfferReceived += OnOffer;
    }

    public void End(BotContext c)
    {
        if (employee) Payroll.OfferReceived -= OnOffer;
    }

    void OnOffer(ulong employer, int wageCents, string label)
    {
        Payroll.Answer(true);
        Log("offer-accepted", true, 0, 0f, $"employer={employer} wage={wageCents}");
    }

    public void Tick(BotContext c, float dt)
    {
        c.Stop();
        if (!DepotRowWorld.Active || DepotRowScene.Instance == null || !Payroll.Available || HandsNet.Instance == null) return;

        if (c.IsHost && !offered && c.Elapsed >= 12f)
        {
            ulong guest = ulong.MaxValue;
            foreach (var b in Businesses.All)
                if (b.IsValid && (b.OwnerName.EndsWith("bot-2", System.StringComparison.Ordinal) ||
                    b.ownerKey.ToString().EndsWith("bot-2", System.StringComparison.Ordinal)))
                { guest = b.ownerClientId; break; }
            if (guest != ulong.MaxValue && Payroll.WhyNotHire(Payroll.LocalClientId, guest) == null)
            {
                Payroll.Offer(guest, Payroll.MinWageCents);
                offered = true;
                Log("offer-sent", true, DepotRowWorld.LocalLot, 0f, $"guest={guest}");
            }
        }

        if (c.IsGuest && !entered && c.Elapsed >= 20f && c.Player != null &&
            Payroll.TryGetLocalJob(out var job))
        {
            int bossLot = Payroll.EmployerLot(job);
            bool allowed = bossLot >= 0 && bossLot != DepotRowWorld.LocalLot &&
                Permissions.CanWorkIn(Payroll.LocalClientId, bossLot);
            if (allowed && DepotRowWorld.UseDoor(bossLot, false, c.Player.gameObject))
            {
                entered = true;
                Log("employee-enter", true, bossLot, 0f, "non-owner entered employer depot");
            }
        }

        if (c.IsGuest && entered && !slammed && c.Elapsed >= 25f &&
            Payroll.TryGetLocalJob(out var activeJob))
        {
            int bossLot = Payroll.EmployerLot(activeJob);
            var depot = DepotRowScene.Lot(bossLot);
            var table = depot != null && depot.interior != null
                ? depot.interior.GetComponentInChildren<SortingTable>(true) : null;
            if (DepotRowWorld.LocalInterior == bossLot && depot != null && depot.InteriorOn && table != null)
            {
                var bag = new BagContents
                {
                    bagId = "bot-table-employee-bag", foundAt = "Employee Table Probe", bagMassKg = 0.3f,
                    sackVariant = 0,
                    items = new List<ItemInstance> { new ItemInstance
                    {
                        uid = ItemKey, defId = "banana_peel", massKg = 0.2f, condition = 1f
                    } }
                };
                var physical = TrashBag.Spawn(bag, table.TopPoint(0f, 0f) + Vector3.up * 0.8f, Quaternion.identity);
                bool started = table.Slam(physical);
                slammed = true;
                Log("employee-slam", started && Permissions.LocalCanWorkAt(table.transform.position), bossLot,
                    table.TopWorldY, $"stationInterior={DepotRowWorld.LocalInterior + 1} tableOn={depot.InteriorOn}");
            }
        }

        if (!firstChecked && c.Elapsed >= 38f)
        {
            firstChecked = true;
            Check(c, "first-rest");
        }
        if (!finalChecked && c.Elapsed >= 48f)
        {
            finalChecked = true;
            Check(c, "stable-rest");
        }
    }

    static void Check(BotContext c, string phase)
    {
        var net = DepotRowNet.Instance;
        int bossLot = net != null ? net.LotOwnedBy(Unity.Netcode.NetworkManager.ServerClientId) : -1;
        var depot = DepotRowScene.Lot(bossLot);
        var table = depot != null && depot.interior != null
            ? depot.interior.GetComponentInChildren<SortingTable>(true) : null;
        NetCarry found = null;
        if (HandsNet.Instance != null)
            foreach (var kv in HandsNet.Instance.Items)
                if (kv.Value != null && kv.Value.Item is LooseItem li && li.Item != null && li.Item.uid == ItemKey)
                { found = kv.Value; break; }
        var loose = found != null ? found.Item as LooseItem : null;
        float top = table != null ? table.TopWorldY : 0f;
        float delta = loose != null ? loose.transform.position.y - top : -999f;
        bool job = c.IsHost ? Payroll.EmployeesOf(Unity.Netcode.NetworkManager.ServerClientId).Count > 0
            : Payroll.TryGetLocalJob(out var j) && Payroll.EmployerLot(j) == bossLot &&
                Permissions.CanWorkIn(Payroll.LocalClientId, bossLot);
        bool interior = depot != null && depot.InteriorOn && !depot.TableRestColliderOn;
        bool rest = found != null && found.St.Phase == HandsPhase.Rest;
        bool authority = found != null && found.LocalSim == c.IsHost;
        bool bodyMatches = loose != null && loose.Body != null && loose.Body.isKinematic != c.IsHost;
        bool onTable = table != null && loose != null && table.IsOnTop(loose.transform.position)
            && delta >= -0.12f && delta <= 0.24f;
        bool pass = bossLot >= 0 && job && interior && rest && authority && bodyMatches && onTable;
        Log(phase, pass, bossLot, top,
            $"host={c.IsHost} job={job} interior={interior} rest={rest} authority={authority} bodyMatches={bodyMatches} onTable={onTable} delta={delta:0.000} netId={(found != null ? found.Id : 0)}");
    }

    static void Log(string phase, bool pass, int lot, float top, string detail)
    {
        NetAudit.Event("tableproxyemployee", new Dictionary<string, object>
        {
            { "phase", phase }, { "pass", pass }, { "lot", lot + 1 },
            { "topY", top }, { "detail", detail }
        });
        Debug.Log($"[DepotTableProxyEmployeeBot] {phase}: {(pass ? "PASS" : "FAIL")} lot={lot + 1} {detail}");
    }
}
