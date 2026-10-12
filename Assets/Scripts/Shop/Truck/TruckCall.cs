using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// "Call my truck": what Esc &gt; Unstuck does for a player on foot (Docs/UIPass.md §5.5). The Scrapazon Prime Tow drone
/// (<see cref="TruckRecovery"/>) lifts the player's own truck from wherever it is and sets it down on the nearest clear,
/// drivable ground beside them.
/// <list type="bullet">
/// <item><b>Whose truck:</b> <see cref="TruckFor"/>. One world: the truck of the lot the player works for (their employer's,
/// while they are on someone's payroll) or else owns. Solo and the legacy flow: the one truck in the loaded scenes.</item>
/// <item><b>Where:</b> <see cref="Pick"/> searches rings around the player, nearest first: the truck's body box must be
/// clear (<see cref="IDropWorld.Clear"/>), its wheels on one level of dry ground, and at least <see cref="MinGap"/> from the
/// player. Roads win over pavement over grass (a few metres' worth each), a spot the player can see wins over one behind a
/// fence, and on a road the truck faces along it, the way the player faces. The caller's machine does this search
/// (so a player with nowhere to put it hears about it straight away) and the host checks the answer again
/// (<see cref="ValidateDrop"/>) with its own physics.</item>
/// <item><b>Who may:</b> the host decides (<see cref="ServerCall"/>): the caller's truck, not driven by anyone, not already on
/// a job, <see cref="Permissions.CanDrive"/>. It is posted to the caller's business at the tow's price (−$25 through the
/// Ledger, free with truck_handling L1+), then the host's <see cref="TruckRecovery"/> flies the truck. A guest's request
/// travels as one rate-limited RPC (<see cref="DepotRowNet.RequestTruckCall"/>) and the answer comes back as a toast;
/// the other machines see the drone through <see cref="TruckRecovery.ObserveTow"/> and the truck through its own NetworkTransform.</item>
/// </list>
/// </summary>
public static class TruckCall
{
    /// <summary>How far from the player the truck may land.</summary>
    public const float MaxReach = 60f;
    /// <summary>Clear ground between the player and the truck's body (the capsule is 0.3 m).</summary>
    public const float MinGap = 1.4f;
    /// <summary>An upright truck this close is "right there": no drone, no fee.</summary>
    public const float RightHere = 3.2f;
    /// <summary>The truck root stands this high over the ground when set down (it then settles onto its wheels).</summary>
    public const float RootLift = 0.15f;

    const int GroundMask = 1 << 6;
    /// <summary>Everything that can be in the truck's way except bags and items (Interactable) and the Ignore Raycast layer.</summary>
    const int ObstacleMask = ~((1 << 2) | (1 << Carryable.InteractableLayer));
    const float WaterY = -0.9f;          // the river and the sea sit at -1.1; their beds are lower still
    const float StreetWindow = 1.6f;     // a surface this far from the town's own ground height is a roof or a river bed
    const float FlatTolerance = 0.7f;    // wheels on a kerb are fine; half over a drop is not
    const float WheelLift = 0.3f;        // the body box starts this far over the ground: kerbs and slabs are rolled over
    const float MinUpNormal = 0.85f;     // ground steeper than about 30° is a wall, not somewhere to park
    const float LeftCost = 0.2f, BackwardsCost = 0.4f, SightCost = 8f;

    // ------------------------------------------------------------------ who, which truck

    /// <summary>The truck <paramref name="client"/> would call, or null (none in the loaded scenes, none spawned for their lot).</summary>
    public static TruckControllerV2 TruckFor(ulong client)
    {
        if (DepotRowWorld.Active)
        {
            var net = DepotRowNet.Instance;
            if (net == null) return null;
            // On someone's payroll the work truck is the employer's; otherwise the player's own business's.
            int lot = Payroll.TryGetJobOf(client, out var job) ? Payroll.EmployerLot(job) : -1;
            if (lot < 0) lot = net.LotOwnedBy(client);
            return lot >= 0 ? net.TruckOf(lot) : null;
        }
        // Solo and the legacy flow: the one truck.
        var all = TruckControllerV2.All;
        for (int i = 0; i < all.Count; i++)
            if (all[i] != null && all[i].isActiveAndEnabled) return all[i];
        return null;
    }

    /// <summary>
    /// Why <paramref name="truck"/> can't be called by <paramref name="client"/> right now (a short sentence that also fits
    /// the tape on the disabled button), or null when it can.
    /// </summary>
    public static string WhyNotCallable(TruckControllerV2 truck, ulong client)
    {
        if (truck == null || !truck.isActiveAndEnabled) return UiText.Pause.TruckNone;
        if (truck.HasDriver) return UiText.Pause.TruckInUse;
        if (!Permissions.CanDrive(client, truck)) return Permissions.NotYourTruck;
        var rec = truck.GetComponent<TruckRecovery>();
        if (rec != null && rec.Busy) return UiText.Pause.TruckInTransit;
        return null;
    }

    /// <summary>The tow is free (truck_handling L1+ on the truck's own business).</summary>
    public static bool IsFree(TruckControllerV2 truck) =>
        truck != null && UpgradeEffects.HandlingAtLevel(BusinessUpgradeNet.LevelForTruck(truck, ShopIds.TruckHandling)).freeUnflip;

    /// <summary>The truck is upright and within <see cref="RightHere"/> of <paramref name="player"/>: nothing to call.</summary>
    public static bool IsRightHere(TruckControllerV2 truck, Vector3 player)
    {
        if (truck == null) return false;
        var t = truck.transform;
        if (Vector3.Dot(t.up, Vector3.up) < 0.8f || Mathf.Abs(player.y - t.position.y) > 4f) return false;
        return GapToBox(new Vector2(player.x, player.z), new Vector2(t.position.x, t.position.z), t.eulerAngles.y,
                        new DropBox(truck.ModelBodyBoxMetres)) <= RightHere;
    }

    // ------------------------------------------------------------------ the caller's machine

    /// <summary>
    /// Find where <paramref name="truck"/> can land beside the player at <paramref name="player"/> (this machine's physics).
    /// <paramref name="callerRoot"/> is the player's own object, which never counts as being in the way.
    /// </summary>
    public static bool TryFindDrop(TruckControllerV2 truck, Vector3 player, float playerYawDeg, Transform callerRoot,
                                   out Vector3 root, out float yawDeg)
    {
        root = default;
        yawDeg = playerYawDeg;
        if (truck == null) return false;
        var box = new DropBox(truck.ModelBodyBoxMetres);
        return Pick(new PhysicsDrop(truck.transform, callerRoot, box), box, player, playerYawDeg, out root, out yawDeg);
    }

    /// <summary>
    /// Send the request: the host (and solo) runs it right here, a guest sends one RPC to the host. Either way the answer
    /// comes back through <see cref="Unstuck.OnCallReply"/> (a toast; a refusal gives the cooldown back).
    /// </summary>
    public static void Send(Vector3 player, Vector3 root, float yawDeg)
    {
        var nm = NetworkManager.Singleton;
        if (nm != null && nm.IsListening && !nm.IsServer)
        {
            // A guest: the host moves the truck, never this machine's copy of it.
            var net = DepotRowNet.Instance;
            if (net != null && net.IsSpawned) net.RequestTruckCall(player, root, yawDeg);
            else Unstuck.OnCallReply(false, UiText.Pause.TruckOffline);
            return;
        }
        bool ok = ServerCall(Permissions.LocalClientId, player, root, yawDeg, out string reply);
        Unstuck.OnCallReply(ok, reply);
    }

    // ------------------------------------------------------------------ the host

    /// <summary>
    /// Host / solo: <paramref name="client"/> asks for their truck to land at <paramref name="root"/> facing
    /// <paramref name="yawDeg"/>, beside <paramref name="player"/>. Checks everything again, posts the fee and starts the
    /// drone. On a refusal <paramref name="reply"/> is the sentence to show them; on success it is empty.
    /// </summary>
    public static bool ServerCall(ulong client, Vector3 player, Vector3 root, float yawDeg, out string reply)
    {
        reply = null;
        if (!Finite(player) || !Finite(root) || float.IsNaN(yawDeg) || float.IsInfinity(yawDeg)) { reply = UiText.Pause.TruckNoRoom; return false; }
        if (DepotRowLayout.InteriorAt(player) >= 0 || CasinoLayout.Contains(player)) { reply = UiText.Pause.TruckIndoors; return false; }
        var truck = TruckFor(client);
        string why = WhyNotCallable(truck, client);
        if (why != null) { reply = why; return false; }
        var no = truck.GetComponent<NetworkObject>();
        if (no != null && no.IsSpawned && !no.IsOwner) { reply = UiText.Pause.TruckRolling; return false; } // the host simulates a parked truck; a driver who just got out still has it
        var rec = truck.GetComponent<TruckRecovery>();
        if (rec == null) rec = truck.gameObject.AddComponent<TruckRecovery>();
        if (!ValidateDrop(truck, player, root, yawDeg, PlayerRootOf(client), out Vector3 landing)) { reply = UiText.Pause.TruckNoRoom; return false; }

        bool free = IsFree(truck);
        var net = DepotRowNet.Instance;
        bool shown = net != null && net.IsSpawned && net.IsServer;
        if (!rec.StartDelivery(landing, Quaternion.Euler(0f, yawDeg, 0f), free,
                started: at => { if (shown) net.BroadcastTow(truck, true, free, at); },
                finished: at => { RememberParking(truck); if (shown) net.BroadcastTow(truck, false, free, at); },
                keepGoing: () => StillWanted(client, truck),
                out string stop))
        {
            reply = stop;
            return false;
        }
        if (!free) Charge(client, truck);
        NetAudit.Event("unstuck", $"truck call by client {client} to ({root.x:0.0},{root.y:0.0},{root.z:0.0}) {(free ? "free" : "fee")}");
        return true;
    }

    /// <summary>
    /// The host's check of a landing the caller proposed: not indoors, within reach of the player and not on top of them,
    /// on dry ground, clear of everything, wheels level. (The caller ran the same physics; this is the other machine's word.)
    /// <paramref name="landing"/> is the spot at the host's own ground height, which is what the drone uses.
    /// </summary>
    public static bool ValidateDrop(TruckControllerV2 truck, Vector3 player, Vector3 root, float yawDeg, Transform callerRoot, out Vector3 landing)
    {
        landing = root;
        if (truck == null) return false;
        if (DepotRowLayout.InteriorAt(root) >= 0 || CasinoLayout.Contains(root)) return false;
        var box = new DropBox(truck.ModelBodyBoxMetres);
        var player2 = new Vector2(player.x, player.z);
        var root2 = new Vector2(root.x, root.z);
        if (Vector2.Distance(player2, root2) > MaxReach + 25f) return false;
        if (GapToBox(player2, root2, yawDeg, box) < MinGap * 0.7f) return false;
        var world = new PhysicsDrop(truck.transform, callerRoot, box);
        if (!world.Surface(root.x, root.z, root.y, false, out float ground, out _, out _)) return false;
        if (Mathf.Abs(root.y - (ground + RootLift)) > 0.6f) return false;
        landing = new Vector3(root.x, ground + RootLift, root.z);
        return world.Clear(landing, yawDeg) && world.Flat(landing, yawDeg);
    }

    /// <summary>
    /// Solo and the legacy flow keep the one truck's parking spot in <see cref="GameSession"/> (the scene flow puts it back
    /// there after a trip through the garage): the drone's drop-off is where it is parked now.
    /// </summary>
    static void RememberParking(TruckControllerV2 truck)
    {
        if (truck == null || DepotRowWorld.Active || string.IsNullOrEmpty(GameSession.TruckSceneName)) return;
        FlowUtil.RecordTruckPose(GameSession.TruckSceneName, truck.transform.position, truck.transform.rotation);
    }

    /// <summary>The caller is still here and the truck is still theirs to call (they didn't leave, so Depot Row parked the truck in its bay).</summary>
    static bool StillWanted(ulong client, TruckControllerV2 truck)
    {
        if (truck == null) return false;
        var nm = NetworkManager.Singleton;
        if (nm != null && nm.IsListening && client != NetworkManager.ServerClientId && !nm.ConnectedClients.ContainsKey(client)) return false;
        return TruckFor(client) == truck;
    }

    /// <summary>The tow's price goes to the caller's business (a guest's through the host; none known = nobody to bill).</summary>
    static void Charge(ulong client, TruckControllerV2 truck)
    {
        string business = null;
        if (Businesses.TryGetForClient(client, out var b)) business = b.Id;
        else if (client != NetworkManager.ServerClientId) return;
        using (Ledger.ToBusiness(business)) // null = this machine's own (the host's) business
            Ledger.Charge(Economy.TowFee, $"{Scrapazon.Name} Prime Tow", LedgerIds.Tow, truck.transform.position + Vector3.up * 3f);
    }

    static Transform PlayerRootOf(ulong client)
    {
        var nm = NetworkManager.Singleton;
        var player = nm != null && nm.SpawnManager != null ? nm.SpawnManager.GetPlayerNetworkObject(client) : null;
        return player != null ? player.transform : null;
    }

    static bool Finite(Vector3 p) => !(float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z) ||
                                       float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z));

    // ------------------------------------------------------------------ the search (pure; the world is asked through IDropWorld)

    /// <summary>What a truck is being set down on: it decides how much a spot is worth.</summary>
    public enum DropSurface : byte { Road, Drive, Walk, Ground, Path }

    /// <summary>The truck's footprint relative to its root, in metres, and how tall it is (rotation only, like
    /// <see cref="TruckControllerV2.BodyBoxMetres"/>): x to the right, z forward.</summary>
    public readonly struct DropBox
    {
        public readonly float cx, cz, hx, hz, bottom, top;
        public DropBox(Bounds metres)
        {
            cx = metres.center.x; cz = metres.center.z;
            hx = metres.extents.x; hz = metres.extents.z;
            bottom = metres.min.y; top = metres.max.y;
        }
        public DropBox(float cx, float cz, float hx, float hz, float bottom, float top)
        {
            this.cx = cx; this.cz = cz; this.hx = hx; this.hz = hz; this.bottom = bottom; this.top = top;
        }
        /// <summary>Farthest reach of the body from the root on the ground plane.</summary>
        public float Radius => Mathf.Sqrt((hx + Mathf.Abs(cx)) * (hx + Mathf.Abs(cx)) + (hz + Mathf.Abs(cz)) * (hz + Mathf.Abs(cz)));
    }

    /// <summary>The world as the search sees it: the game's physics, or a made-up street in a test.</summary>
    public interface IDropWorld
    {
        /// <summary>The surface a truck can stand on at (x, z), the highest one near street level and above water:
        /// its height, kind and (on a road) the direction along it. False = none.</summary>
        bool Surface(float x, float z, float nearY, bool wantAlong, out float y, out DropSurface kind, out Vector2 along);
        /// <summary>The body box, standing at <paramref name="root"/> facing <paramref name="yawDeg"/>, touches nothing solid.</summary>
        bool Clear(Vector3 root, float yawDeg);
        /// <summary>All four wheel corners are on dry ground at one level.</summary>
        bool Flat(Vector3 root, float yawDeg);
        /// <summary>Nothing solid between the two points (a fence or a wall would).</summary>
        bool Open(Vector3 from, Vector3 to);
    }

    /// <summary>Directions to try around the player, in degrees from where they face (+ = their right): the sides first.</summary>
    static readonly float[] Spokes = BuildSpokes();

    static float[] BuildSpokes()
    {
        var list = new List<float>(24);
        for (int i = 0; i < 24; i++) list.Add(-165f + i * 15f);
        list.Sort((a, b) =>
        {
            int c = Mathf.Abs(Mathf.Abs(a) - 90f).CompareTo(Mathf.Abs(Mathf.Abs(b) - 90f));
            return c != 0 ? c : b.CompareTo(a);
        });
        return list.ToArray();
    }

    /// <summary>Cost of standing on a kind of surface, in metres of walking: a road within 4 m beats grass.</summary>
    public static float SurfaceCost(DropSurface kind)
    {
        switch (kind)
        {
            case DropSurface.Road: return 0f;
            case DropSurface.Drive: return 1.5f;
            case DropSurface.Walk: return 3f;
            case DropSurface.Path: return 7f;
            default: return 4f;
        }
    }

    /// <summary>Yaw in degrees of a ground-plane direction (0 = +Z, 90 = +X, like Unity's).</summary>
    public static float YawOf(Vector2 dir) => Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg;

    /// <summary>Distance on the ground plane from <paramref name="point"/> to the body box of a truck rooted at
    /// <paramref name="root"/> facing <paramref name="yawDeg"/> (0 when the point is inside it).</summary>
    public static float GapToBox(Vector2 point, Vector2 root, float yawDeg, DropBox box)
    {
        Vector2 d = point - BoxCentre(root, yawDeg, box, out Vector2 right, out Vector2 fwd);
        float u = Mathf.Max(Mathf.Abs(Vector2.Dot(d, right)) - box.hx, 0f);
        float v = Mathf.Max(Mathf.Abs(Vector2.Dot(d, fwd)) - box.hz, 0f);
        return Mathf.Sqrt(u * u + v * v);
    }

    /// <summary>The point of the body box nearest <paramref name="point"/> (on the ground plane).</summary>
    public static Vector2 NearestOnBox(Vector2 point, Vector2 root, float yawDeg, DropBox box)
    {
        Vector2 centre = BoxCentre(root, yawDeg, box, out Vector2 right, out Vector2 fwd);
        Vector2 d = point - centre;
        return centre + right * Mathf.Clamp(Vector2.Dot(d, right), -box.hx, box.hx) + fwd * Mathf.Clamp(Vector2.Dot(d, fwd), -box.hz, box.hz);
    }

    static Vector2 BoxCentre(Vector2 root, float yawDeg, DropBox box, out Vector2 right, out Vector2 fwd)
    {
        float r = yawDeg * Mathf.Deg2Rad, s = Mathf.Sin(r), c = Mathf.Cos(r);
        fwd = new Vector2(s, c);
        right = new Vector2(c, -s);
        return root + right * box.cx + fwd * box.cz;
    }

    /// <summary>
    /// Nearest-first search for a place to set the truck down beside the player. Rings of spokes around them; a spot's price
    /// is its gap to the player plus <see cref="SurfaceCost"/>, a little more on the player's left (the driver's door is
    /// on the truck's left, so a truck on their right shows it) and facing against them, and <c>8</c> when it is behind
    /// a fence. The cheapest valid spot wins. <paramref name="player"/> is their feet.
    /// </summary>
    public static bool Pick(IDropWorld world, DropBox box, Vector3 player, float playerYawDeg, out Vector3 root, out float yawDeg) =>
        Pick(world, box, player, playerYawDeg, out root, out yawDeg, out _);

    /// <summary><see cref="Pick(IDropWorld, DropBox, Vector3, float, out Vector3, out float)"/>, also giving the winner's price (tests).</summary>
    public static bool Pick(IDropWorld world, DropBox box, Vector3 player, float playerYawDeg, out Vector3 root, out float yawDeg, out float cost)
    {
        root = default;
        yawDeg = playerYawDeg;
        cost = float.MaxValue;
        var player2 = new Vector2(player.x, player.z);
        float pr = playerYawDeg * Mathf.Deg2Rad;
        Vector2 fwd = new Vector2(Mathf.Sin(pr), Mathf.Cos(pr)), right = new Vector2(fwd.y, -fwd.x);
        float best = float.MaxValue, slack = box.Radius + 0.1f;
        bool found = false;
        for (float ring = 2.8f; ring <= MaxReach; ring += ring < 9f ? 0.5f : ring * 0.2f)
        {
            if (found && ring - slack > best) break; // nothing farther out can still win
            for (int i = 0; i < Spokes.Length; i++)
            {
                float a = Spokes[i] * Mathf.Deg2Rad;
                Vector2 at = player2 + (fwd * Mathf.Cos(a) + right * Mathf.Sin(a)) * ring;
                if (!world.Surface(at.x, at.y, player.y, true, out float ground, out var kind, out Vector2 along)) continue;
                float spokeCost = SurfaceCost(kind) + (Spokes[i] < 0f ? LeftCost : 0f);
                if (ring - slack + spokeCost >= best) continue;
                Vector2 heading = along.sqrMagnitude > 0.01f ? along.normalized : fwd;
                for (int h = 0; h < 2; h++)
                {
                    Vector2 dir = h == 0 ? heading : -heading;
                    float yaw = YawOf(dir);
                    float score = GapToBox(player2, at, yaw, box);
                    if (score < MinGap) continue;
                    score += spokeCost + (Vector2.Dot(dir, fwd) < 0f ? BackwardsCost : 0f);
                    if (score >= best) continue;
                    var spot = new Vector3(at.x, ground + RootLift, at.y);
                    if (!world.Clear(spot, yaw) || !world.Flat(spot, yaw)) continue;
                    Vector2 near = NearestOnBox(player2, at, yaw, box);
                    if (!world.Open(player + Vector3.up, new Vector3(near.x, ground + 1f, near.y))) score += SightCost;
                    if (score >= best) continue;
                    best = score;
                    root = spot;
                    yawDeg = yaw;
                    found = true;
                }
            }
        }
        cost = best;
        return found;
    }

    // ------------------------------------------------------------------ roads (plan data)

    /// <summary>
    /// The nearest point on a road centreline to <paramref name="from"/> (on the ground plane), which way the road runs
    /// there (a unit direction, x/z) and the road's half-width. False when none is within <paramref name="maxDistance"/>.
    /// </summary>
    public static bool TryRoadFrame(Vector3 from, IReadOnlyList<CoastalCityLayout.RoadDef> roads, float maxDistance,
                                    out Vector3 point, out Vector2 along, out float halfWidth, out float distance)
    {
        point = from;
        along = Vector2.zero;
        halfWidth = 0f;
        distance = float.MaxValue;
        if (roads == null || float.IsNaN(maxDistance) || maxDistance < 0f || !Finite(from)) return false;
        float best = maxDistance * maxDistance;
        bool found = false;
        foreach (var road in roads)
        {
            if (road?.points == null || road.points.Length < 2) continue;
            for (int i = 1; i < road.points.Length; i++)
            {
                Vector3 a = road.points[i - 1], b = road.points[i];
                if (!Finite(a) || !Finite(b)) continue;
                Vector2 delta = new Vector2(b.x - a.x, b.z - a.z);
                float len2 = delta.sqrMagnitude;
                if (len2 < .000001f) continue;
                float u = Mathf.Clamp01(((from.x - a.x) * delta.x + (from.z - a.z) * delta.y) / len2);
                Vector3 candidate = Vector3.Lerp(a, b, u);
                float dx = candidate.x - from.x, dz = candidate.z - from.z;
                float d2 = dx * dx + dz * dz;
                if (d2 > best || (found && d2 >= best)) continue;
                best = d2;
                point = candidate;
                along = delta / Mathf.Sqrt(len2);
                halfWidth = road.halfWidth;
                found = true;
            }
        }
        if (found) distance = Mathf.Sqrt(best);
        return found;
    }

    // ------------------------------------------------------------------ the game's physics

    /// <summary>
    /// <see cref="IDropWorld"/> over Unity's physics. The town's ground layer holds everything solid and flat (roads, pavements,
    /// terrain) AND the houses, so a surface is the highest upward-facing hit near street level, and "in the way" is anything
    /// that is not a known surface, not low enough for a wheel to roll over, and not a bag or critter the drop shoves aside.
    /// </summary>
    sealed class PhysicsDrop : IDropWorld
    {
        static readonly RaycastHit[] rays = new RaycastHit[32];
        static readonly Collider[] overlaps = new Collider[96];
        readonly Transform truckRoot, callerRoot;
        readonly DropBox box;
        readonly IReadOnlyList<CoastalCityLayout.RoadDef> roads;

        public PhysicsDrop(Transform truckRoot, Transform callerRoot, DropBox box)
        {
            this.truckRoot = truckRoot;
            this.callerRoot = callerRoot;
            this.box = box;
            // The coastal blueprint knows which way each road runs; a legacy town's slabs are read off their bounds.
            if (WorldLifeAnchor.Latest != null && WorldLifeAnchor.Latest.Graph.layoutId == CoastalCityLayout.LayoutId)
            {
                try { roads = CoastalCityLayout.Roads; }
                catch (InvalidOperationException) { roads = null; }
            }
        }

        public bool Surface(float x, float z, float nearY, bool wantAlong, out float y, out DropSurface kind, out Vector2 along)
        {
            y = 0f;
            kind = DropSurface.Ground;
            along = Vector2.zero;
            float street = TownRelief.Height(x, z); // the town's own ground here (0 when flat); roofs and river beds are far from it
            var origin = new Vector3(x, Mathf.Max(nearY, street) + 6f, z);
            int n = Physics.RaycastNonAlloc(origin, Vector3.down, rays, 40f, GroundMask, QueryTriggerInteraction.Ignore);
            Collider top = null;
            float topY = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                var h = rays[i];
                if (h.collider == null || h.normal.y < MinUpNormal || Mathf.Abs(h.point.y - street) > StreetWindow) continue;
                if (h.point.y > topY) { topY = h.point.y; top = h.collider; }
            }
            if (top == null || topY < WaterY) return false;
            y = topY;
            kind = Classify(top.name);
            if (wantAlong && kind == DropSurface.Road) along = RoadAlong(x, z, top);
            return true;
        }

        Vector2 RoadAlong(float x, float z, Collider road)
        {
            if (roads != null && TryRoadFrame(new Vector3(x, 0f, z), roads, 16f, out _, out Vector2 along, out float half, out float d) && d <= half + 2f)
                return along;
            var b = road.bounds; // an old axis-aligned slab runs along its long side
            return b.size.x >= b.size.z ? Vector2.right : Vector2.up;
        }

        public bool Clear(Vector3 root, float yawDeg)
        {
            var rot = Quaternion.Euler(0f, yawDeg, 0f);
            float ground = root.y - RootLift;
            float bottom = ground + WheelLift, top = ground + (box.top - box.bottom) - 0.05f;
            if (top <= bottom) return true;
            Vector3 centre = root + rot * new Vector3(box.cx, 0f, box.cz);
            centre.y = (bottom + top) * 0.5f;
            var half = new Vector3(Mathf.Max(0.1f, box.hx - 0.05f), (top - bottom) * 0.5f, Mathf.Max(0.1f, box.hz - 0.05f));
            int n = Physics.OverlapBoxNonAlloc(centre, half, overlaps, rot, ObstacleMask, QueryTriggerInteraction.Ignore);
            if (n >= overlaps.Length) return false; // too crowded to vouch for
            for (int i = 0; i < n; i++)
                if (Blocks(overlaps[i])) return false;
            return true;
        }

        public bool Flat(Vector3 root, float yawDeg)
        {
            var rot = Quaternion.Euler(0f, yawDeg, 0f);
            float ground = root.y - RootLift;
            float ix = Mathf.Max(0.1f, box.hx - 0.2f), iz = Mathf.Max(0.1f, box.hz - 0.2f);
            for (int k = 0; k < 4; k++)
            {
                Vector3 p = root + rot * new Vector3(box.cx + ((k & 1) == 0 ? -ix : ix), 0f, box.cz + ((k & 2) == 0 ? -iz : iz));
                if (!Surface(p.x, p.z, ground, false, out float y, out _, out _) || Mathf.Abs(y - ground) > FlatTolerance) return false;
            }
            return true;
        }

        public bool Open(Vector3 from, Vector3 to)
        {
            Vector3 d = to - from;
            float len = d.magnitude;
            if (len < 0.05f) return true;
            int n = Physics.RaycastNonAlloc(from, d / len, rays, len, ObstacleMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                if (rays[i].collider != null && Blocks(rays[i].collider)) return false;
            return true;
        }

        bool Blocks(Collider c)
        {
            if (c == null || c.isTrigger) return false;
            if (truckRoot != null && c.transform.IsChildOf(truckRoot)) return false;
            if (callerRoot != null && c.transform.IsChildOf(callerRoot)) return false;
            if (IsSurfaceName(c.name)) return false;                                  // what the truck stands on
            var rb = c.attachedRigidbody;
            if (rb != null && !rb.isKinematic && rb.mass < 40f) return false;        // bags, bins, critters: the drop shoves them aside
            if (TownRelief.TopAboveGround(c) <= 0.3f) return false;                    // low enough for a wheel to roll over
            return true;
        }
    }

    /// <summary>What kind of ground a collider is, by the names the town builders give them (Road_*, Sidewalk_*, DepotPad_*...).</summary>
    public static DropSurface Classify(string name)
    {
        if (string.IsNullOrEmpty(name)) return DropSurface.Ground;
        if (name.StartsWith("Road", StringComparison.Ordinal) || name.StartsWith("DepotPad", StringComparison.Ordinal)) return DropSurface.Road;
        if (name.StartsWith("Drive", StringComparison.Ordinal) || name.StartsWith("CurbRamp", StringComparison.Ordinal)) return DropSurface.Drive;
        if (name.StartsWith("Sidewalk", StringComparison.Ordinal) || name.StartsWith("Curb", StringComparison.Ordinal)) return DropSurface.Walk;
        if (name.StartsWith("River walk", StringComparison.Ordinal) || name.StartsWith("Entry path", StringComparison.Ordinal) ||
            name.IndexOf("_path_", StringComparison.Ordinal) >= 0) return DropSurface.Path; // SM_path_garden: flat stones, not SM_pathlight
        return DropSurface.Ground;
    }

    /// <summary>A collider the town's ground is made of (so it is never "in the way" of something standing on it).</summary>
    public static bool IsSurfaceName(string name) =>
        Classify(name) != DropSurface.Ground || name.StartsWith("Relief terrain", StringComparison.Ordinal) ||
        name == "Coastal terrain" || name.StartsWith("Grass", StringComparison.Ordinal);
}
