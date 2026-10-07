using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A flung rider's flight (package F1, §14.1): a fixed-step (60 Hz) ballistic body that every machine runs from the same
/// start and velocity, so the flight, the bowled-over pedestrians and the knocked props are the same everywhere without
/// streaming the flight (rule 13: hits are by rule, against the static world only).
/// <list type="bullet">
/// <item>Sphere-casts against the static world; trucks, players, bags and triggers are ignored.</item>
/// <item>Knockable props it passes through are knocked over (<see cref="KnockableDirector.DebugKnock"/>, local cosmetics on
/// every machine); people it passes through get WorldLife's thrown-hit rule (stumble / short flop).</item>
/// <item><b>Hedges and bushes catch</b> (soft landing, no knockout); <b>trampolines bounce</b> (up to 3 times).</item>
/// <item>Never longer than <see cref="RideTuning.MaxAir"/> s (§4.5: a fling is ≤ 2 s helpless).</item>
/// </list>
/// </summary>
public sealed class RideFlight
{
    public Vector3 Pos, PrevPos, Vel, Start;
    public float T;
    public bool Landed, Soft;
    public int Bounces, Knocks, People;
    /// <summary>Set by <see cref="Step"/> for the caller's effects ("BOING!", "caught by a hedge").</summary>
    public string Event;

    readonly bool hitPeople;
    readonly HashSet<uint> hitBodies = new HashSet<uint>();

    static readonly RaycastHit[] hits = new RaycastHit[24];
    static readonly Collider[] overlaps = new Collider[32];
    static readonly List<ILifeBody> found = new List<ILifeBody>(8);
    static readonly Dictionary<int, byte> softCache = new Dictionary<int, byte>(256);
    const byte NotSoft = 1, Hedge = 2, Trampoline = 3;

    static int Mask => Physics.DefaultRaycastLayers & ~(1 << 2) & ~(1 << Carryable.InteractableLayer);

    public RideFlight(Vector3 start, Vector3 vel, bool hitPeople)
    {
        Pos = PrevPos = Start = start;
        Vel = vel;
        this.hitPeople = hitPeople;
    }

    /// <summary>Run the flight forward by <paramref name="seconds"/> (a machine that heard about it late).</summary>
    public void FastForward(float seconds)
    {
        int n = Mathf.Min(240, Mathf.FloorToInt(seconds / RideTuning.FlightDt));
        for (int i = 0; i < n && !Landed; i++) Step();
        PrevPos = Pos;
    }

    public void Step()
    {
        if (Landed) return;
        Event = null;
        float dt = RideTuning.FlightDt;
        PrevPos = Pos;
        T += dt;
        Vel.y -= RideTuning.Gravity * dt;
        Vector3 centre = Pos + Vector3.up * 0.75f;

        // Props and people in the way go over first (so a lamp post we hit is knocked, not a wall).
        int n = Physics.OverlapSphereNonAlloc(centre, 0.7f, overlaps, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var k = Knockable.ForCollider(overlaps[i]);
            if (k == null || k.IsDown) continue;
            var kd = KnockableDirector.Instance;
            if (kd != null && kd.DebugKnock(k, new Vector3(Vel.x, 0f, Vel.z) * 1.2f)) Knocks++;
        }
        if (hitPeople)
        {
            found.Clear();
            LifeBodies.Query(centre, 0.9f, found, LifeBodyKind.Person, true);
            for (int i = 0; i < found.Count; i++)
            {
                var b = found[i];
                if (b == null || !hitBodies.Add(b.Id.Packed)) continue;
                Vector3 v = Vel;
                if (v.magnitude < 11.5f) v = v.normalized * 11.5f; // a whole flying garbage man: always a flop
                var hit = WorldLifeAuthority.Instance.ClaimThrownHit(b, v, 0, WorldLifeRunner.FixedTickCount);
                if (hit.kind != NpcHitKind.None) People++;
            }
        }

        Vector3 d = Vel * dt;
        float len = d.magnitude;
        if (len > 1e-5f)
        {
            int m = Physics.SphereCastNonAlloc(centre, RideTuning.FlightRadius, d / len, hits, len + 0.02f, Mask, QueryTriggerInteraction.Ignore);
            int best = -1;
            float bestD = float.MaxValue;
            for (int i = 0; i < m; i++)
            {
                var h = hits[i];
                if (h.collider == null || Ignore(h.collider)) continue;
                if (h.distance <= 0f && h.point == Vector3.zero) continue; // started inside it: let it go
                if (h.distance < bestD) { bestD = h.distance; best = i; }
            }
            if (best >= 0)
            {
                var h = hits[best];
                Pos += d / len * Mathf.Max(0f, h.distance - 0.01f);
                byte soft = SoftKind(h.collider);
                if (soft == Trampoline && Bounces < 3 && Vel.y < 0f)
                {
                    Bounces++;
                    Vel = new Vector3(Vel.x * 0.7f, Mathf.Abs(Vel.y) * 0.85f + 3f, Vel.z * 0.7f);
                    Event = "BOING!";
                }
                else if (soft == Hedge)
                {
                    Landed = true;
                    Soft = true;
                    Event = "caught by a hedge!";
                    Vel = Vector3.zero;
                }
                else if (h.normal.y > 0.55f)
                {
                    Landed = true;
                    Vel = Vector3.zero;
                }
                else
                {
                    // A wall: splat and slide down it.
                    Vector3 nrm = h.normal; nrm.y = 0f;
                    if (nrm.sqrMagnitude > 1e-4f)
                    {
                        nrm.Normalize();
                        Vector3 flat = new Vector3(Vel.x, 0f, Vel.z);
                        flat = Vector3.Reflect(flat, nrm) * 0.3f;
                        Vel = new Vector3(flat.x, Mathf.Min(Vel.y, 1f), flat.z);
                    }
                    Event = "SPLAT!";
                }
            }
            else Pos += d;
        }

        if (!Landed && (T >= RideTuning.MaxAir || Pos.y < Carryable.KillPlaneY))
        {
            Landed = true;
            Vel = Vector3.zero;
            if (Physics.Raycast(Pos + Vector3.up * 2f, Vector3.down, out var g, 60f, Mask, QueryTriggerInteraction.Ignore)) Pos = g.point;
        }
    }

    static bool Ignore(Collider c)
    {
        if (c.isTrigger) return true;
        if (Knockable.ForCollider(c) != null) return true;
        if (c.attachedRigidbody != null && !c.attachedRigidbody.isKinematic) return true; // trucks, bags, debris
        if (c.GetComponentInParent<TruckControllerV2>() != null) return true;
        if (c.GetComponentInParent<PlayerController>() != null) return true;
        return false;
    }

    static byte SoftKind(Collider c)
    {
        int id = c.GetInstanceID();
        if (softCache.TryGetValue(id, out byte k)) return k;
        k = NotSoft;
        var t = c.transform;
        for (int i = 0; i < 4 && t != null; i++, t = t.parent)
        {
            string nm = t.name;
            if (nm.IndexOf("Trampoline", System.StringComparison.OrdinalIgnoreCase) >= 0) { k = Trampoline; break; }
            if (nm.IndexOf("Hedge", System.StringComparison.OrdinalIgnoreCase) >= 0 || nm.IndexOf("Bush", System.StringComparison.OrdinalIgnoreCase) >= 0) { k = Hedge; break; }
        }
        if (softCache.Count > 4096) softCache.Clear();
        softCache[id] = k;
        return k;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => softCache.Clear();
}
