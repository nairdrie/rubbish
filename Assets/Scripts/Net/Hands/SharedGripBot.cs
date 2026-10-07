using System.Collections.Generic;
using UnityEngine;

/// <summary>Two real peers: shared heavy walk/run, either pull winner, rip conservation, relaxing and voluntary handoff.
/// Runs through player input and the normal grab/NGO paths. Editor peers can run it without making a player build.</summary>
public sealed class SharedGripBot : IBotBrain
{
    public static readonly List<string> Results = new List<string>();
    public const int Rounds = 11;
    readonly int firstRound;
    static readonly Vector3 Centre = new Vector3(1000f, 15.55f, 1000f);
    int round = -1;
    bool spawned, grabbed, checkedRound, released, sawShared, busyRejected, cleanupReleased;
    float maxStrain, nextJoin;
    double lastClock = -1;
    Vector3 startPosition;
    uint itemId;
    GameObject floor;

    public SharedGripBot(int firstRound = 0) { this.firstRound = firstRound; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Register()
    {
        Results.Clear();
        BotBrains.Register("sharedgrip", () => new SharedGripBot());
        BotBrains.Register("sharedgrip-edges", () => new SharedGripBot(7));
    }

    public void Begin(BotContext c) { c.Stop(); }
    public void End(BotContext c)
    {
        c.Stop();
        if (floor != null) Object.Destroy(floor);
        bool pass = Results.Count == Rounds - firstRound && Results.TrueForAll(x => x.StartsWith("PASS"));
        Debug.Log("[SharedGripBot] END " + (pass ? "PASS" : "FAIL") + " " + string.Join(" | ", Results));
    }

    public void Tick(BotContext c, float dt)
    {
        c.Stop();
        var net = HandsNet.Instance;
        if (net == null || !net.Active || c.Player == null || c.Input == null || !DepotRowWorld.Active) return;
        if (floor == null)
        {
            floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Shared grip test platform";
            floor.transform.position = new Vector3(1000f, 15f, 1000f);
            floor.transform.localScale = new Vector3(80f, 1f, 80f);
        }
        double clock = net.ServerNow - 20.0;
        if (clock < 0) return;
        // NGO can correct the guest clock backwards under loss. Never repeat a setup/teleport or a completed assertion.
        clock = System.Math.Max(lastClock, clock);
        lastClock = clock;
        int r = firstRound + (int)(clock / 18.0);
        if (r >= Rounds) return;
        float t = (float)(clock - (r - firstRound) * 18.0);
        bool firstHolder = r == 6 ? c.IsGuest : c.IsHost;
        var carrier = c.Player.GetComponent<TrashCarrier>();
        if (r != round)
        {
            if (carrier.IsCarrying) carrier.Drop();
            round = r;
            spawned = grabbed = checkedRound = released = sawShared = busyRejected = cleanupReleased = false;
            nextJoin = 1.7f;
            maxStrain = 0f;
            itemId = 0;
            var pos = Centre + Vector3.right * (c.IsHost ? -0.75f : 0.75f);
            HandsBots.Teleport(c.Player.gameObject, pos, c.IsHost ? Vector3.right : Vector3.left);
            startPosition = pos;
        }
        // A single coroutine-frame teleport can be overwritten by a controller/physics update in that frame.
        // Settle the test fixture before any pickup, never during the actual interaction being measured.
        if (t < 0.65f)
            HandsBots.Teleport(c.Player.gameObject, startPosition, c.IsHost ? Vector3.right : Vector3.left);
        bool heavy = r == 0 || r == 6;
        string key = "gripcheck/" + r + (heavy ? "#tell=heavy" : "#tell=none");
        if (c.IsHost && !spawned && t > 0.3f)
        {
            var content = new BagContents { bagId = key, foundAt = "shared grip regression", bagMassKg = heavy ? 18f : 2f };
            for (int n = 0; n < 3; n++)
                content.items.Add(new ItemInstance { uid = key + "/item/" + n, defId = "newspaper", massKg = 0.3f, condition = 1f });
            var bag = TrashBag.Spawn(content, Centre + Vector3.up * 0.4f, Quaternion.identity);
            net.HostPublishNow(bag);
            if (r == 9)
            {
                var extra = TrashBag.Spawn(new BagContents { bagId = key + "/extra", bagMassKg = 2f },
                    Centre + Vector3.forward, Quaternion.identity);
                net.HostPublishNow(extra);
            }
            spawned = true;
        }
        NetCarry item = null;
        foreach (var nc in net.Items.Values)
            if (nc != null && nc.Desc.key.ToString() == key) { item = nc; itemId = nc.Id; break; }
        if (firstHolder && !grabbed && t > 0.9f && item != null && item.St.Phase == HandsPhase.Rest)
            grabbed = carrier.PickUp(item.Item);
        if (!firstHolder && t > nextJoin && t < 3f && item != null && item.St.Phase == HandsPhase.Held &&
            item.St.holder != net.LocalId && !item.St.Has(CarryNetState.FlagHeave))
        {
            if (heavy) BagTellsNet.Instance.RequestHelp(item.Id);
            else net.LocalSnatch(item);
            nextJoin = t + 0.6f;
        }

        var shared = net.TugOf(net.LocalId);
        if (shared.HasValue)
        {
            sawShared = true;
            maxStrain = Mathf.Max(maxStrain, (shared.Value.strainH + shared.Value.strainC) / shared.Value.ripAt);
            if (t > 3f && t < 11f)
            {
                Vector3 direction = Vector3.zero;
                bool sprint = true;
                if (r == 0 || r == 6) { direction = Vector3.forward; sprint = t > 6f; }
                if (r == 1 && c.IsGuest) direction = Vector3.right;
                if (r == 2 && c.IsHost) direction = Vector3.left;
                if (r == 3 || r == 4 && t < 4f) direction = c.IsHost ? Vector3.left : Vector3.right;
                if (direction != Vector3.zero)
                {
                    var cam = Camera.main;
                    Vector3 local = Quaternion.Euler(0f, -(cam != null ? cam.transform.eulerAngles.y : c.Player.transform.eulerAngles.y), 0f) * direction;
                    c.Input.move = new Vector2(local.x, local.z);
                    c.Input.sprint = sprint;
                }
            }
            if (r == 5 && c.IsGuest && t > 5f && !released) { net.LocalLetGo(shared.Value.item); released = true; }
            if (r == 8 && c.IsGuest && t > 5f && !released)
            { HandsBots.Teleport(c.Player.gameObject, Centre + Vector3.right * 12f, Vector3.left); released = true; }
            if (r == 9 && t > 5f && !released)
            {
                var extra = HandsBots.Find(net, key + "/extra");
                busyRejected = extra != null && !carrier.PickUp(extra.Item);
                if (extra != null) net.LocalSnatch(extra); // also exercise the host's rejection, not only the local guard
                released = true;
            }
            if (r == 10 && c.IsGuest && t > 5f && !released) { net.LocalFlop(true); released = true; }
        }
        // The first release may already have reached the other peer. Still send their G, just as normal held-item G would.
        if (r == 7 && t > 5f && !released && item != null) { net.LocalLetGo(item.Id); released = true; }
        if (!checkedRound && t > 13f)
        {
            checkedRound = true;
            bool pass = sawShared;
            string detail;
            if (r == 0 || r == 6)
            {
                float moved = Vector3.Distance(startPosition, c.Player.transform.position);
                pass &= shared.HasValue && moved > 1f && maxStrain < 0.1f &&
                    c.Player.GetComponent<StarterAssets.ThirdPersonController>().enabled &&
                    c.Player.GetComponent<PlayerTells>().LiftedTogether;
                detail = "heavy carry moved=" + moved.ToString("F2") + " strain=" + maxStrain.ToString("F2");
            }
            else if (r == 3)
            {
                int count = 0;
                foreach (var nc in net.Items.Values) if (nc != null && nc.Item is LooseItem li &&
                    li.Item != null && li.Item.uid.StartsWith(key + "/item/")) count++;
                pass &= item == null && !shared.HasValue && count == 3;
                detail = "rip contents=" + count;
            }
            else if (r == 4)
            {
                pass &= shared.HasValue && maxStrain > 0.1f &&
                    shared.Value.strainH + shared.Value.strainC < 1f;
                detail = "relaxed strain peak=" + maxStrain.ToString("F2");
            }
            else if (r == 7 || r == 8)
            {
                pass &= !shared.HasValue && item != null &&
                    (item.St.Phase == HandsPhase.Rest || item.St.Phase == HandsPhase.Flight) && !carrier.IsCarrying;
                detail = (r == 7 ? "both let go" : "teleport interrupts safely") + " phase=" + item?.St.Phase;
            }
            else if (r == 9)
            {
                var extra = HandsBots.Find(net, key + "/extra");
                pass &= shared.HasValue && busyRejected && extra != null && extra.St.Phase == HandsPhase.Rest;
                detail = "busy hands reject a second item on client and host";
            }
            else
            {
                bool hostWins = r == 2 || r == 5 || r == 10;
                bool localWins = hostWins == c.IsHost;
                pass &= item != null && !shared.HasValue && item.St.Phase == HandsPhase.Held &&
                    (item.St.holder == net.LocalId) == localWins;
                detail = "winner=" + (item != null ? item.St.holder.ToString() : "missing") + " voluntary=" + (r == 5);
            }
            if (!pass) detail += " sawShared=" + sawShared + " active=" + shared.HasValue +
                " carrier=" + carrier.IsCarrying + " grabbed=" + grabbed + " released=" + released;
            string result = (pass ? "PASS" : "FAIL") + " round=" + r + " " + detail;
            Results.Add(result);
            NetAudit.Event("gripcheck", new Dictionary<string, object> { { "round", r }, { "pass", pass }, { "detail", detail } });
            Debug.Log("[SharedGripBot] " + result);
        }
        if (t > 15f && shared.HasValue && !cleanupReleased)
        { net.LocalLetGo(shared.Value.item); cleanupReleased = true; }
        if (t > 16f && carrier.IsCarrying) carrier.Drop();
    }
}
