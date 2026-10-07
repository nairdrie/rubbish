using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// <c>-botbrain depot</c> (package N3 test rig): a headless bot that checks Depot Row on its own machine and writes what it
/// saw to the NetAudit log (<c>kind = depot</c>), then wanders like <c>walk</c>.
/// <list type="number">
/// <item>t 6 s: its lot, colour, truck and every lot's state;</item>
/// <item>t 10/16 s: walks into its own shop and back out (on foot, local fade + teleport);</item>
/// <item>t 22/30 s: visits a neighbour's shop: may look, may not work there (pick up / station / laptop) or drive their truck;</item>
/// <item>t 34–48 s: gets in its own truck, drives into its garage and back out, gets out.</item>
/// </list>
/// </summary>
public sealed class DepotBotBrain : IBotBrain
{
    readonly IBotBrain walk = BotBrains.Create("walk");
    readonly HashSet<int> done = new HashSet<int>();
    int other = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Register() => BotBrains.Register("depot", () => new DepotBotBrain());

    public void Begin(BotContext c) => walk.Begin(c);
    public void End(BotContext c) => walk.End(c);

    bool Step(BotContext c, int at)
    {
        if (c.Elapsed < at || done.Contains(at)) return false;
        done.Add(at);
        return true;
    }

    public void Tick(BotContext c, float dt)
    {
        var pc = c.Player != null ? c.Player.GetComponent<PlayerController>() : null;
        if (pc == null || !DepotRowWorld.Active)
        {
            if (c.Elapsed > 50f) walk.Tick(c, dt);
            if (Step(c, 20) && !DepotRowWorld.Active) Log("not in the one world", null);
            return;
        }
        var net = DepotRowNet.Instance;
        int lot = DepotRowWorld.LocalLot;
        var me = Permissions.LocalClientId;

        if (Step(c, 6))
        {
            var truck = net.TruckOf(lot);
            var d = new Dictionary<string, object>
            {
                { "phase", "state" }, { "lot", lot + 1 }, { "pos", V(pc.transform.position) }, { "lots", net.Describe() },
                { "truck", truck != null ? (object)truck.NetworkObjectId : "none" },
                { "truckPaint", truck != null && truck.GetComponent<DepotTruckPaint>() != null ? truck.GetComponent<DepotTruckPaint>().Lot + 1 : 0 },
            };
            NetAudit.Event("depot", d);
            Debug.Log("[DepotBot] " + net.Describe());
        }
        if (lot < 0) { if (c.Elapsed > 50f) walk.Tick(c, dt); return; }

        if (Step(c, 10)) Log("enter own shop", DepotRowWorld.UseDoor(lot, false, pc.gameObject));
        if (Step(c, 14)) Log("inside own shop", DepotRowWorld.LocalInterior == lot, ("interior", DepotRowWorld.LocalInterior + 1));
        if (Step(c, 16)) Log("leave own shop", DepotRowWorld.UseDoor(lot, true, pc.gameObject));
        if (Step(c, 20)) Log("back on the street", DepotRowWorld.LocalInterior < 0, ("pos", V(pc.transform.position)));

        if (Step(c, 22))
        {
            for (int i = 0; i < DepotRowLayout.LotCount; i++)
                if (i != lot && net.TryGetLot(i, out var s) && s.Claimed) { other = i; break; }
            if (other >= 0) Log("visit neighbour", DepotRowWorld.UseDoor(other, false, pc.gameObject), ("lot", other + 1));
            else Log("no neighbour to visit", null);
        }
        if (Step(c, 26) && other >= 0)
        {
            var inside = DepotRowWorld.LocalInterior;
            var theirTruck = net.TruckOf(other);
            var myTruck = net.TruckOf(lot);
            Log("in neighbour's shop", inside == other,
                ("canWork", Permissions.LocalCanWorkAt(pc.transform.position)),
                ("canLaptop", Permissions.LocalCanUseLaptopAt(pc.transform.position)),
                ("canDriveTheirs", theirTruck == null ? (object)"no truck" : Permissions.CanDrive(me, theirTruck)),
                ("canDriveMine", myTruck == null ? (object)"no truck" : Permissions.CanDrive(me, myTruck)),
                ("canWorkOwn", Permissions.CanWorkIn(me, lot)));
        }
        if (Step(c, 30) && other >= 0) Log("leave neighbour's shop", DepotRowWorld.UseDoor(other, true, pc.gameObject));

        if (Step(c, 34))
        {
            var t = net.TruckOf(lot);
            if (t != null)
            {
                DepotRowWorld.TeleportPlayer(pc, t.transform.position + t.transform.right * -2.5f, pc.transform.rotation);
                t.OnInteract(pc.gameObject);
            }
            Log("get in own truck", t != null && t.IsLocalPlayerSeated);
        }
        if (Step(c, 37)) Log("drive into own garage", pc.IsDriving && DepotRowWorld.UseDoor(lot, false, pc.gameObject));
        if (Step(c, 41))
        {
            var t = net.TruckOf(lot);
            Log("truck in the bay", t != null && DepotRowLayout.InteriorAt(t.transform.position) == lot, ("truckPos", t != null ? V(t.transform.position) : "-"));
        }
        if (Step(c, 43)) Log("drive out", pc.IsDriving && DepotRowWorld.UseDoor(lot, true, pc.gameObject));
        if (Step(c, 47))
        {
            var t = net.TruckOf(lot);
            bool outside = t != null && DepotRowLayout.InteriorAt(t.transform.position) < 0;
            if (t != null && t.IsLocalPlayerSeated) t.ForceExit();
            Log("truck back on the street", outside, ("truckPos", t != null ? V(t.transform.position) : "-"));
        }
        if (c.Elapsed > 50f) walk.Tick(c, dt);
    }

    static string V(Vector3 p) => $"{p.x:0.0},{p.y:0.0},{p.z:0.0}";

    static void Log(string phase, bool? ok, params (string k, object v)[] extra)
    {
        var d = new Dictionary<string, object> { { "phase", phase }, { "ok", ok.HasValue ? (object)ok.Value : "n/a" } };
        foreach (var (k, v) in extra) d[k] = v;
        NetAudit.Event("depot", d);
        Debug.Log($"[DepotBot] {phase}: {(ok.HasValue ? (ok.Value ? "OK" : "FAIL") : "-")}");
    }
}
