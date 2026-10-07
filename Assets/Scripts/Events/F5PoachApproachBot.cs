using System;
using System.Collections.Generic;
using Unity.Netcode.Components;
using UnityEngine;

/// <summary>
/// Focused two-bot F5 acceptance check. The host's truck approaches an unvisited curb from 50 m away; the
/// guest first pays a bag from another street into its own truck, then takes a bag from the approached street.
/// The host must see exactly the latter POACHED callout while still more than 22 m from that curb.
/// Run --brain f5poachvictim,f5poachthief with two fresh businesses.
/// </summary>
public sealed class F5PoachVictimBrain : IBotBrain
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Register() => BotBrains.Register("f5poachvictim", () => new F5PoachVictimBrain());

    TruckControllerV2 truck, guestTruck;
    PickupStop stop;
    Vector3 start, direction;
    string street;
    bool supplied, staged;
    int targetCallouts, otherCallouts, restages;
    float calloutDistance = -1f;

    public void Begin(BotContext c)
    {
        c.Stop();
        PoachingNet.Poached += OnPoached;
    }

    public void End(BotContext c)
    {
        PoachingNet.Poached -= OnPoached;
        if (truck != null) truck.InputOverride = null;
        bool pass = staged && targetCallouts == 1 && otherCallouts == 0 &&
                    calloutDistance > PoachingNet.TruckRadius && calloutDistance <= PoachingNet.ApproachRadius;
        NetAudit.Event("botf5poach", new Dictionary<string, object>
        {
            { "phase", "victim-end" }, { "pass", pass }, { "street", street ?? "" },
            { "targetCallouts", targetCallouts }, { "otherCallouts", otherCallouts },
            { "metresAtCallout", Math.Round(calloutDistance, 1) }, { "restages", restages },
        });
        c.Stop();
    }

    void OnPoached(PoachingNet.PoachInfo p)
    {
        if (p.victimId != Businesses.LocalId) return;
        float metres = stop != null && truck != null ? Flat(stop.transform.position - truck.transform.position).magnitude : -1f;
        bool target = p.street == street;
        if (target) { targetCallouts++; calloutDistance = metres; }
        else otherCallouts++;
        NetAudit.Event("botf5poach", new Dictionary<string, object>
        {
            { "phase", "shown" }, { "street", p.street }, { "expected", street ?? "" },
            { "target", target }, { "metres", Math.Round(metres, 1) },
        });
    }

    public void Tick(BotContext c, float dt)
    {
        c.Stop();
        if (!c.IsHost || c.Player == null || c.Elapsed < 12f || Businesses.OnlineCount < 2) return;
        if (!supplied)
        {
            supplied = true;
            CurbTrashSpawner.DebugPutOut(12);
        }
        if (!staged)
        {
            if (!TryStage()) return;
            staged = true;
            NetAudit.Event("botf5poach", new Dictionary<string, object>
            {
                { "phase", "approach" }, { "street", street }, { "stop", stop.stopId },
                { "metres", Math.Round(Flat(stop.transform.position - start).magnitude, 1) },
            });
        }
        if (targetCallouts > 0 || truck == null || stop == null || truck.Body == null) return;
        float metres = Flat(stop.transform.position - truck.transform.position).magnitude;
        if (metres < 33f)
        {
            SetTruck(truck, start, Quaternion.LookRotation(direction), direction * 1.0f);
            restages++;
        }
        else truck.Body.linearVelocity = direction * 1.0f;
        truck.Body.angularVelocity = Vector3.zero;
    }

    bool TryStage()
    {
        var depot = DepotRowNet.Instance;
        var mine = Businesses.Local;
        if (depot == null || !mine.IsValid || mine.lot < 0) return false;
        truck = depot.TruckOf(mine.lot);
        if (truck == null || truck.Body == null) return false;
        guestTruck = null;
        foreach (var business in Businesses.All)
            if (business.IsValid && business.Id != mine.Id && business.lot >= 0)
            {
                guestTruck = depot.TruckOf(business.lot);
                if (guestTruck != null && guestTruck.Body != null) break;
            }
        if (guestTruck == null || guestTruck.Body == null) return false;

        foreach (var s in PickupStop.All)
        {
            if (s == null || !s.HasTrash || !HasUsableBag(s)) continue;
            Vector3 f = Flat(s.transform.forward).normalized;
            Vector3 r = Flat(s.transform.right).normalized;
            Vector3[] dirs = { f, -f, r, -r };
            foreach (var dir in dirs)
            {
                Vector3 at = s.transform.position - dir * 50f;
                if (DepotRowLayout.PadAt(at) >= 0 || DepotRowLayout.InteriorAt(at) >= 0) continue;
                if (!PoachingNet.Approaching(at, dir, s.transform.position)) continue;
                PickupStop closest = null;
                float closestD2 = float.MaxValue;
                foreach (var other in PickupStop.All)
                {
                    if (other == null || !other.HasTrash || !PoachingNet.Approaching(at, dir, other.transform.position)) continue;
                    float d2 = Flat(other.transform.position - at).sqrMagnitude;
                    if (d2 < closestD2) { closestD2 = d2; closest = other; }
                }
                if (closest == null || PoachingNet.StreetOf(closest.stopId) != PoachingNet.StreetOf(s.stopId)) continue;
                bool hasOtherStreet = false;
                foreach (var other in PickupStop.All)
                    if (other != null && HasUsableBag(other) && PoachingNet.StreetOf(other.stopId) != PoachingNet.StreetOf(s.stopId) &&
                        Flat(other.transform.position - at).magnitude > PoachingNet.ApproachRadius + 10f)
                    { hasOtherStreet = true; break; }
                if (!hasOtherStreet) continue;
                if (Physics.Raycast(at + Vector3.up * 8f, Vector3.down, out var hit, 20f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                    at.y = hit.point.y + 0.15f;
                stop = s;
                street = PoachingNet.StreetOf(s.stopId);
                start = at;
                direction = dir;
                truck.InputOverride = () => Vector2.zero;
                SetTruck(truck, at, Quaternion.LookRotation(dir), dir * 1.0f);
                // Park the guest's server-owned truck near the route. The guest moves itself and the bags;
                // parked trucks return to server authority, so a guest-side truck teleport would be ignored.
                Vector3 otherAt = s.transform.position + Flat(s.transform.forward).normalized * 10f;
                if (Physics.Raycast(otherAt + Vector3.up * 8f, Vector3.down, out var otherHit, 20f,
                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) otherAt.y = otherHit.point.y + 0.15f;
                SetTruck(guestTruck, otherAt, Quaternion.LookRotation(-Flat(s.transform.forward).normalized), Vector3.zero);
                return true;
            }
        }
        return false;
    }

    static bool HasUsableBag(PickupStop s)
    {
        foreach (var b in s.SpawnedBags)
            if (b != null && b.IsCurbBag && b.State == CarryState.Resting && !b.HasBurst && BagTells.WeightClass(b) != BagWeightClass.Heavy)
                return true;
        return false;
    }

    internal static Vector3 Flat(Vector3 p) { p.y = 0f; return p; }

    internal static void SetTruck(TruckControllerV2 t, Vector3 p, Quaternion q, Vector3 velocity)
    {
        if (t == null) return;
        var nt = t.GetComponent<NetworkTransform>();
        if (nt != null && nt.IsSpawned && nt.CanCommitToTransform) nt.Teleport(p, q, t.transform.localScale);
        else t.transform.SetPositionAndRotation(p, q);
        if (t.Body != null)
        {
            t.Body.position = p;
            t.Body.rotation = q;
            t.Body.linearVelocity = velocity;
            t.Body.angularVelocity = Vector3.zero;
        }
    }
}

public sealed class F5PoachThiefBrain : IBotBrain
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Register() => BotBrains.Register("f5poachthief", () => new F5PoachThiefBrain());

    TruckControllerV2 hostTruck, ownTruck;
    TruckHopper hopper;
    TrashBag bag;
    string targetStreet, bagId, bagStreet;
    int phase, deliveries;
    float phaseAt;
    bool otherDelivered, targetDelivered;

    public void Begin(BotContext c) => c.Stop();

    public void End(BotContext c)
    {
        bool pass = otherDelivered && targetDelivered && deliveries == 2;
        NetAudit.Event("botf5poach", new Dictionary<string, object>
        {
            { "phase", "thief-end" }, { "pass", pass }, { "targetStreet", targetStreet ?? "" },
            { "otherDelivered", otherDelivered }, { "targetDelivered", targetDelivered }, { "deliveries", deliveries },
        });
        c.Stop();
    }

    public void Tick(BotContext c, float dt)
    {
        c.Stop();
        if (!c.IsGuest || c.Player == null || c.Elapsed < 16f || phase == 9) return;
        var carrier = TrashCarrier.Local;
        if (carrier == null || !FindTrucks()) return;
        if (phase == 0)
        {
            if (!FindTargetStreet(out targetStreet)) return;
            var other = FindBag(b => PoachingNet.StreetOf(b.SourceStop.stopId) != targetStreet &&
                                     F5PoachVictimBrain.Flat(b.transform.position - hostTruck.transform.position).magnitude > PoachingNet.ApproachRadius + 10f);
            if (other == null) return;
            StartDelivery(c, other, 1);
            return;
        }
        if (phase == 5)
        {
            var target = FindBag(b => PoachingNet.StreetOf(b.SourceStop.stopId) == targetStreet);
            if (target == null) return;
            StartDelivery(c, target, 6);
            return;
        }
        if (Time.time - phaseAt > 14f)
        {
            NetAudit.Event("botf5poach", new Dictionary<string, object> { { "phase", "timeout" }, { "step", phase }, { "bag", bagId ?? "" } });
            phase = 9;
            return;
        }
        if (phase == 1 || phase == 6)
        {
            if (Time.time - phaseAt < 0.6f) return;
            if (bag == null || bag.State != CarryState.Resting || carrier.IsCarrying) return;
            if (!carrier.PickUp(bag)) return;
            phase++;
            phaseAt = Time.time;
            return;
        }
        if (phase == 2 || phase == 7)
        {
            if (!carrier.IsCarrying) return;
            if (carrier.Held.NetId != 0 && TrashCarrier.NetCanRelease != null && !TrashCarrier.NetCanRelease(carrier)) return;
            Vector3 p = hopper.TossTargetPosition - hopper.transform.forward * 2.2f;
            HandsBots.Teleport(c.Player.gameObject, HandsBots.Ground(p), hopper.transform.forward);
            phase++;
            phaseAt = Time.time;
            return;
        }
        if (phase == 3 || phase == 8)
        {
            if (Time.time - phaseAt < 0.6f || !carrier.IsCarrying || !hopper.CanAccept(carrier.Held, out _)) return;
            if (TrashCarrier.NetCanRelease != null && !TrashCarrier.NetCanRelease(carrier)) return;
            carrier.TossInto(hopper);
            phase = phase == 3 ? 4 : 10;
            phaseAt = Time.time;
            NetAudit.Event("botf5poach", new Dictionary<string, object> { { "phase", "toss" }, { "bag", bagId }, { "street", bagStreet } });
            return;
        }
        if (phase == 4 || phase == 10)
        {
            if (GameSession.FindCurbBag(bagId) != null) return;
            deliveries++;
            bool target = phase == 10;
            if (target) targetDelivered = true; else otherDelivered = true;
            NetAudit.Event("botf5poach", new Dictionary<string, object>
            {
                { "phase", "delivered" }, { "bag", bagId }, { "target", target }, { "street", targetStreet },
            });
            phase = target ? 9 : 5;
            phaseAt = Time.time;
            bag = null;
        }
    }

    bool FindTrucks()
    {
        var depot = DepotRowNet.Instance;
        var local = Businesses.Local;
        var host = Businesses.Host;
        if (depot == null || !local.IsValid || !host.IsValid || local.Id == host.Id || local.lot < 0 || host.lot < 0) return false;
        ownTruck = depot.TruckOf(local.lot);
        hostTruck = depot.TruckOf(host.lot);
        hopper = ownTruck != null ? ownTruck.GetComponentInChildren<TruckHopper>() : null;
        return ownTruck != null && hostTruck != null && hopper != null;
    }

    bool FindTargetStreet(out string street)
    {
        street = null;
        // A non-owning truck copy may have a kinematic Rigidbody with zero linearVelocity.
        // Its networked facing is enough to locate the curb the host staged ahead of it.
        Vector3 facing = F5PoachVictimBrain.Flat(hostTruck.transform.forward).normalized;
        if (facing.sqrMagnitude < 0.01f) return false;
        PickupStop nearest = null;
        float best = float.MaxValue;
        foreach (var s in PickupStop.All)
        {
            if (s == null || !s.HasTrash || !PoachingNet.Approaching(hostTruck.transform.position, facing, s.transform.position)) continue;
            float d2 = F5PoachVictimBrain.Flat(s.transform.position - hostTruck.transform.position).sqrMagnitude;
            if (d2 < best) { best = d2; nearest = s; }
        }
        if (nearest == null) return false;
        street = PoachingNet.StreetOf(nearest.stopId);
        return true;
    }

    static TrashBag FindBag(Func<TrashBag, bool> filter)
    {
        TrashBag best = null;
        foreach (var s in PickupStop.All)
        {
            if (s == null) continue;
            foreach (var b in s.SpawnedBags)
            {
                if (b == null || b.SourceStop == null || !b.IsCurbBag || b.State != CarryState.Resting || b.HasBurst ||
                    BagTells.WeightClass(b) == BagWeightClass.Heavy || !filter(b)) continue;
                if (best == null || string.CompareOrdinal(b.Contents.bagId, best.Contents.bagId) < 0) best = b;
            }
        }
        return best;
    }

    void StartDelivery(BotContext c, TrashBag b, int pickupPhase)
    {
        bag = b;
        bagId = b.Contents.bagId;
        bagStreet = PoachingNet.StreetOf(b.SourceStop.stopId);
        Vector3 outwards = F5PoachVictimBrain.Flat(b.SourceStop.transform.forward).normalized;
        if (outwards.sqrMagnitude < 0.01f) outwards = Vector3.forward;
        HandsBots.Teleport(c.Player.gameObject, HandsBots.Ground(b.transform.position - outwards * 1.1f), outwards);
        phase = pickupPhase;
        phaseAt = Time.time;
        NetAudit.Event("botf5poach", new Dictionary<string, object>
        {
            { "phase", pickupPhase == 6 ? "target-start" : "other-start" }, { "bag", bagId },
            { "street", bagStreet },
        });
    }
}
