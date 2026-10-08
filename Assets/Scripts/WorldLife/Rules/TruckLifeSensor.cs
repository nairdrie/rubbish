using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// WorldLife package E (Docs/WorldLife.md §6.3 step 2, §8.1 – §8.3): the truck's life sensor. It rides on every truck
/// that THIS machine simulates, added at runtime with no prefab or TruckControllerV2 edit: the W0 runner builds each such
/// truck's predictive box once per FixedUpdate (body box stretched along the travel by |v| × 2 × dt + 0.3 m) and hands it
/// here before the physics step, together with B's knockable client, so WorldLife.Sensor is billed in one place.
///
/// Every hittable person, dog or car inside the box (padded 0.35 m) is claimed through
/// <see cref="WorldLifeRunner.Authority"/>; the authority decides and presents (rule 13: nothing here depends on two
/// rigidbodies touching; people and dogs have no colliders, and a car's collider goes off the same step it's shunted).
/// A decided shunt also hits back at the truck (§8.3): −35 % speed, a 6°/s yaw kick, a shake for the seated driver and
/// "CRUNCH!". 0 B per step after warm-up.
/// </summary>
public sealed class TruckLifeSensor : ITruckSensorClient
{
    public static readonly TruckLifeSensor Instance = new TruckLifeSensor();

    readonly List<ILifeBody> found = new List<ILifeBody>(16);
    readonly Dictionary<uint, int> lastClaimTick = new Dictionary<uint, int>(64);

    public int Sweeps { get; private set; }
    public int Claimed { get; private set; }
    public int TruckBumps { get; private set; }
    /// <summary>The truck's speed just before / after the last shunt bump (tests).</summary>
    public float LastBumpFrom { get; private set; }
    public float LastBumpTo { get; private set; }

    public void Sense(in TruckSweep sweep)
    {
        Sweeps++;
        found.Clear();
        LifeBodies.QueryBox(sweep.center, sweep.rotation, sweep.halfExtents, found, LifeBodyKind.All, WorldLifeTuning.Hits.TruckBoxPad, true);
        if (found.Count == 0) return;
        if (lastClaimTick.Count > 512) lastClaimTick.Clear();
        var auth = WorldLifeRunner.Authority;
        byte actor = WorldLifeAuthority.SlotOf(sweep.truck);
        for (int i = 0; i < found.Count; i++)
        {
            var b = found[i];
            uint key = b.Id.Packed;
            if (lastClaimTick.TryGetValue(key, out int last) && sweep.tick - last < RulesTuning.ReclaimGuardTicks && sweep.tick >= last) continue;
            lastClaimTick[key] = sweep.tick;
            var claim = new NpcHitClaim { target = b.Id, tick = sweep.tick, truckVelocity = sweep.velocity, actorSlot = actor, truck = sweep.truck };
            Claimed++;
            // Co-op cars are confirmed by the host and shown once everywhere; no second, local-only shunt.
            if (b.BodyKind == LifeBodyKind.Car && HandsNet.Instance != null && HandsNet.Instance.ReportCarImpact(sweep.truck, b.Id, b.Position)) continue;
            // Crossing actors use the same host-confirmed cartoon rules, but never the resident household fines.
            // The explicit solo path also avoids Commit: these event actors do not own a resident's ledger/timetable.
            if (b.Id.kind == LifeKind.Crossing)
            {
                if (HandsNet.Instance != null && HandsNet.Instance.ReportCrossingImpact(sweep.truck, b.Id, b.Position)) continue;
                long serial = CrossingGuardDirector.EventSerial(b.Id);
                if (serial >= 0 && WorldLifeAuthority.Instance.Decide(in claim, b, RulesBooks.Active, out var crossingHit, out _))
                {
                    CrossingGuardDirector.PresentHit(b.Id, serial, in crossingHit);
                    WorldLifeEvents.RaiseHitDecided(in crossingHit);
                }
                continue;
            }
            var hit = auth is WorldLifeAuthority wa ? wa.ClaimNpcHit(in claim, b) : auth.ClaimNpcHit(in claim);
            if (hit.kind == NpcHitKind.Shunt) BumpTruck(in sweep, b);
        }
        found.Clear();
    }

    /// <summary>§8.3, the truck side of a shunt (the machine that simulates the truck).</summary>
    void BumpTruck(in TruckSweep sweep, ILifeBody car)
        => BumpTruck(sweep.truck, car.Position);

    /// <summary>A confirmed collision hits back only on the truck's current physics owner.</summary>
    public void BumpTruck(TruckControllerV2 truck, Vector3 carPosition)
    {
        var rb = truck != null ? truck.Body : null;
        if (rb == null || rb.isKinematic || (truck.IsSpawned && !truck.IsOwner)) return;
        Vector3 v = rb.linearVelocity;
        float keep = 1f - WorldLifeTuning.Hits.ShuntTruckSpeedLoss;
        LastBumpFrom = new Vector3(v.x, 0f, v.z).magnitude;
        rb.linearVelocity = new Vector3(v.x * keep, v.y, v.z * keep);
        LastBumpTo = LastBumpFrom * keep;
        // Kick the nose away from the car.
        float sign = Vector3.Dot(carPosition - rb.position, rb.rotation * Vector3.right) >= 0f ? -1f : 1f;
        rb.angularVelocity += Vector3.up * (sign * Mathf.Lerp(.1f, .3f, Mathf.InverseLerp(2.5f, 20f, LastBumpFrom)));
        rb.AddForce(Vector3.up * Mathf.Clamp((LastBumpFrom - 4f) * .02f, 0f, .35f), ForceMode.VelocityChange);
        TruckBumps++;
        if (truck != null && truck.IsLocalPlayerSeated) Juice.Shake(0.22f, 0.2f);
        Vector3 at = carPosition + Vector3.up * 2.2f;
        KnockSfx.Play("crunch", "crunch", at, 0.9f, 1f);
        GameHUD.FloatingText(at, "CRUNCH!", new Color(1f, 0.6f, 0.3f), 1.2f);
    }

    public void Clear() { lastClaimTick.Clear(); found.Clear(); }

    public void ResetStats() { Sweeps = Claimed = TruckBumps = 0; LastBumpFrom = LastBumpTo = 0f; }
}
