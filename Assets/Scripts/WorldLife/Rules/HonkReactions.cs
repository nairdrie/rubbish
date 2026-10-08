using System.Collections.Generic;
using UnityEngine;

/// <summary>What the horn does to one body (pure; <see cref="HonkReactions.Classify"/>).</summary>
public enum HornReaction : byte { None = 0, Startle = 1, PullOver = 2, HonkBack = 3, Bark = 4 }

/// <summary>
/// WorldLife package E (Docs/WorldLife.md §8.5): the horn is a tool. Within the horn radius
/// (<see cref="WorldLifeTuning.Horn.Radius"/>: 12 / 25 / 40 m with the air-horn upgrades):
/// <list type="bullet">
/// <item><b>people</b> ahead of the truck, or within 6 m in any direction, <b>startle</b>: a 0.4 m hop, "AAH!", then 2–3 s
/// stepping off its line, then a glare (C presents <see cref="NpcHitKind.Startle"/>);</item>
/// <item><b>cars ahead in the same lane</b> pull to the kerb and stop for 3 s; <b>other cars</b> honk back (D presents
/// <see cref="NpcHitKind.PullOver"/> / <see cref="NpcHitKind.Startle"/>);</item>
/// <item><b>dogs</b> bark. Raccoons scatter as they always have (CritterSpawner).</item>
/// </list>
/// Pure presentation, deterministic per NPC from (id, honk tick), so every machine shows the same scatter once F relays
/// the horn. 0 B per honk after warm-up.
/// </summary>
public sealed class HonkReactions
{
    readonly List<ILifeBody> found = new List<ILifeBody>(48);

    /// <summary>The geometry of §8.5 for one body (pure). <paramref name="rel"/> = body − truck centre (flat),
    /// <paramref name="fwd"/> / <paramref name="bodyFwd"/> unit flat forwards. Distances are measured from the truck's body
    /// box (<paramref name="boxHalf"/> = its half width and half length; zero = from the centre point): fixer pass, so the
    /// 12 m horn reaches 12 m past the nose (from the centre it reached only ~9 m past it, and the cab is where it sounds).</summary>
    public static HornReaction Classify(LifeBodyKind kind, Vector3 rel, Vector3 fwd, Vector3 bodyFwd, float radius, bool movingCar, Vector2 boxHalf = default)
    {
        rel.y = 0f;
        Vector3 right = new Vector3(fwd.z, 0f, -fwd.x);
        float lx = Vector3.Dot(rel, right), lz = Vector3.Dot(rel, fwd);
        float dx = Mathf.Max(0f, Mathf.Abs(lx) - boxHalf.x), dz = Mathf.Max(0f, Mathf.Abs(lz) - boxHalf.y);
        float d = Mathf.Sqrt(dx * dx + dz * dz);
        if (d > radius) return HornReaction.None;
        float ahead = lz;
        switch (kind)
        {
            case LifeBodyKind.Person:
                return ahead > 0f || d <= WorldLifeTuning.Horn.AnyDirectionRadius ? HornReaction.Startle : HornReaction.None;
            case LifeBodyKind.Dog:
                return HornReaction.Bark;
            case LifeBodyKind.Car:
            {
                if (!movingCar) return HornReaction.None;
                bool sameLane = ahead > 0f && Mathf.Abs(lx) <= RulesTuning.SameLaneHalfWidth
                                && Vector3.Dot(bodyFwd, fwd) >= RulesTuning.SameLaneHeadingDot;
                return sameLane ? HornReaction.PullOver : HornReaction.HonkBack;
            }
        }
        return HornReaction.None;
    }

    /// <summary>Scatter everyone in range of this honk (presentation on this machine).</summary>
    public void Scatter(TruckControllerV2 truck, int tick, out int startles, out int pullOvers, out int honkBacks)
    {
        startles = pullOvers = honkBacks = 0;
        var rb = truck.Body;
        Quaternion rot = rb != null ? rb.rotation : truck.transform.rotation;
        Vector3 centre = (rb != null ? rb.position : truck.transform.position) + rot * truck.ModelBodyBoxMetres.center;
        Vector3 fwd = rot * Vector3.forward; fwd.y = 0f;
        fwd = fwd.sqrMagnitude > 1e-6f ? fwd.normalized : Vector3.forward;
        Vector3 vel = rb != null ? rb.linearVelocity : Vector3.zero;
        float radius = WorldLifeTuning.Horn.Radius;
        byte actor = WorldLifeAuthority.SlotOf(truck);
        var dir = ResidentDirector.Instance;
        double hours = GameClock.TotalHours;
        var bb = truck.ModelBodyBoxMetres;
        var boxHalf = new Vector2(bb.extents.x, bb.extents.z);

        found.Clear();
        LifeBodies.Query(centre, radius + boxHalf.magnitude, found, LifeBodyKind.All, true);
        for (int i = 0; i < found.Count; i++)
        {
            var b = found[i];
            Vector3 rel = b.Position - centre;
            Vector3 bf = b.Rotation * Vector3.forward; bf.y = 0f;
            bf = bf.sqrMagnitude > 1e-6f ? bf.normalized : Vector3.forward;
            bool moving = b is CarAgent ca && !ca.parkedShell && ca.path != null;
            var r = Classify(b.BodyKind, rel, fwd, bf, radius, moving, boxHalf);
            if (r == HornReaction.None) continue;
            if (r == HornReaction.Bark)
            {
                if (b is DogAgent dog && dir != null) dog.Bark(dir);
                continue;
            }
            var hit = new NpcHit
            {
                target = b.Id, atHours = hours, tick = tick, point = b.Position, truckVelocity = vel, actorSlot = actor, truck = truck,
                seed = WorldLifeAuthority.Seed(b.Id, tick),
                kind = r == HornReaction.PullOver ? NpcHitKind.PullOver : NpcHitKind.Startle,
                launch = WorldLifeAuthority.SideToward(truck, b.Position, new Vector3(vel.x, 0f, vel.z)),
            };
            WorldLifeAuthority.Present(b, in hit);
            if (r == HornReaction.Startle) startles++;
            else if (r == HornReaction.PullOver) pullOvers++;
            else honkBacks++;
        }
        found.Clear();
    }
}

/// <summary>
/// WorldLife package E (§8.5): night honks. A honk within 30 m of any house between 22:00 and 06:00 is a noise complaint:
/// Rep −1 (Progression caps it at 3 a night per district; this keeps the same count so the letter agrees), a shout from
/// the nearest door, and one batched letter in the morning. Host only.
/// </summary>
public static class NightNoise
{
    static readonly int[] tonight = new int[Districts.All.Count];
    static int pendingNight = int.MinValue;
    static float nextShout;
    static string[] keys;

    public static int Complaints { get; private set; }
    public static int Capped { get; private set; }
    public static int Letters { get; private set; }

    /// <summary>Is this hour of the day in the noise window (22:00–06:00)?</summary>
    public static bool IsQuietHours(float hourOfDay) =>
        hourOfDay >= WorldLifeTuning.Horn.NightFromHour || hourOfDay < WorldLifeTuning.Horn.NightToHour;

    /// <summary>Host: a honk at <paramref name="at"/>. True when it was a (counted) noise complaint.</summary>
    public static bool Check(IRulesBooks b, StreetGraph g, Vector3 at)
    {
        if (b == null || !b.IsHost || g == null || g.houses == null) return false;
        float hod = (float)(b.Hours - System.Math.Floor(b.Hours / 24.0) * 24.0);
        if (!IsQuietHours(hod)) return false;
        int nearest = -1; float best = WorldLifeTuning.Horn.NoiseRadius * WorldLifeTuning.Horn.NoiseRadius;
        for (int i = 0; i < g.houses.Length; i++)
        {
            var h = g.houses[i];
            if (h.door >= g.nodes.Length) continue;
            Vector3 d = g.nodes[h.door].pos - at; d.y = 0f;
            float d2 = d.sqrMagnitude;
            if (d2 < best) { best = d2; nearest = i; }
        }
        if (nearest < 0) return false;
        var house = g.houses[nearest];
        byte district = (byte)Mathf.Clamp(house.district, 0, Districts.All.Count - 1);
        string did = WorldLifeIds.DistrictId(district);
        Vector3 door = g.nodes[house.door].pos;
        if (Time.time >= nextShout)
        {
            nextShout = Time.time + RulesTuning.NoiseShoutInterval;
            GameHUD.FloatingText(door + Vector3.up * 3f, Shouts[(Complaints + Capped) % Shouts.Length], new Color(1f, 0.55f, 0.4f), 1f);
        }
        EnsureKeys();
        int night = RulesBooks.NightOf(b.Hours);
        int n = RulesBooks.DailyGet(b, keys[district], night);
        if (n >= RepValues.NoiseComplaintsPerNight) { Capped++; return false; }
        RulesBooks.DailyAdd(b, keys[district], night, 1);
        b.Incident(new NeighborIncidentFact
        {
            kind = NeighborIncidentKind.NoiseComplaint, stopId = house.stopId ?? "", districtId = did, pos = at, escalation = 1f,
        });
        if (pendingNight != night) { System.Array.Clear(tonight, 0, tonight.Length); pendingNight = night; }
        tonight[district]++;
        Complaints++;
        return true;
    }

    static readonly string[] Shouts = { "SOME OF US ARE SLEEPING!", "IT'S 2 AM!!", "KEEP IT DOWN!", "*lights flick on*" };

    static void EnsureKeys()
    {
        if (keys != null) return;
        keys = new string[Districts.All.Count];
        for (int i = 0; i < keys.Length; i++) keys[i] = "wl_noise:" + WorldLifeIds.DistrictId((byte)i);
    }

    /// <summary>Host, every in-game hour: the morning after a noisy night (06:00 or later), send the one letter.</summary>
    public static bool MaybeSendMorningLetter(IRulesBooks b)
    {
        if (pendingNight == int.MinValue || b == null || !b.IsHost) return false;
        double morning = pendingNight * 24.0 + 12.0 + 18.0;   // the night starts at noon; 06:00 is 18 h later
        if (b.Hours < morning) return false;
        NeighborLetters.NoiseMorning(b, tonight);
        System.Array.Clear(tonight, 0, tonight.Length);
        pendingNight = int.MinValue;
        Letters++;
        return true;
    }

    /// <summary>Complaints waiting for the morning letter (tests).</summary>
    public static int Pending { get { int s = 0; foreach (var n in tonight) s += n; return pendingNight == int.MinValue ? 0 : s; } }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        System.Array.Clear(tonight, 0, tonight.Length);
        pendingNight = int.MinValue; nextShout = 0f; keys = null; Complaints = Capped = Letters = 0;
    }

    /// <summary>Tests: forget tonight's pending letter and the stats.</summary>
    public static void ResetForTests() => ResetStatics();
}
