using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

/// <summary>One-time placement repair for preserved residential lots during a FULL coastal build. Never called by
/// incremental publishing: an authored road edit must report conflicts instead of silently rearranging the town.</summary>
public static class CoastalParcelPlacement
{
    const float CandidateStep = 4f;
    const int MaxCandidatesPerLot = 512;
    const int MaxRoadSamples = 50000;

    sealed class Lot
    {
        public CityRoadAnchor anchor;
        public Transform building;
        public string district;
        public Bounds buildingBounds;
        public HashSet<Collider> colliders;
    }
    struct Candidate
    {
        public string road;
        public float u;
        public int side;
    }
    struct Original
    {
        public CityRoadAnchor anchor;
        public string road;
        public float u, lateral, yaw;
        public bool fromEdge;
    }
    struct Envelope
    {
        public string road;
        public Vector3 center, half;
        public Quaternion rotation;
        public Bounds bounds;
    }

    public static string Repair(Transform town)
        => RepairWithFixedLots(town, null);

    public static string RepairWithFixedLots(Transform town, ISet<string> fixedLots)
    {
        if (Application.isPlaying) throw new InvalidOperationException("Parcel placement is an Edit Mode full-build step only.");
        if (!CoastalCityLayout.IsCoastal(town)) throw new ArgumentException("Parcel placement requires the generated coastal town.", nameof(town));
        Physics.SyncTransforms();
        var lots = new List<Lot>();
        foreach (var anchor in town.GetComponentsInChildren<CityRoadAnchor>(true).OrderBy(a => a.stableId, StringComparer.Ordinal))
        {
            var route = anchor.GetComponentInChildren<CoastalHouseRoute>(true);
            var stop = route != null ? route.GetComponent<PickupStop>() : null;
            if (stop == null || route.buildingRoot == null) continue;
            lots.Add(new Lot { anchor = anchor, building = route.buildingRoot, district = stop.District.id,
                buildingBounds = ModelBounds(route.buildingRoot),
                colliders = new HashSet<Collider>(anchor.GetComponentsInChildren<Collider>(true).Where(c => c.enabled && !c.isTrigger && c.gameObject.activeInHierarchy)) });
        }
        var envelopes = RoadEnvelopes();
        var hits = new Collider[512];
        var moved = new List<Original>();
        var report = new StringBuilder();
        int inspected = 0, tried = 0;
        try
        {
            foreach (var lot in lots)
            {
                if (!PreservedDistrict(lot.district) || lot.anchor.stableId.StartsWith("cc-", StringComparison.Ordinal) ||
                    lot.anchor.stableId.StartsWith("st-", StringComparison.Ordinal)) continue;
                inspected++;
                if (Clear(lot, lots, envelopes, hits, out _)) continue;
                if (fixedLots != null && fixedLots.Contains(lot.anchor.stableId))
                    throw new InvalidOperationException("Residential block household " + lot.anchor.stableId +
                        " is obstructed. Adjust ResidentialBlocks.json; authored block spacing will not be silently changed.");
                var anchor = lot.anchor;
                var original = new Original { anchor = anchor, road = anchor.roadId, u = anchor.u, lateral = anchor.lateral,
                    yaw = anchor.yawOffset, fromEdge = anchor.lateralFromRoadEdge };
                var before = new Pose(anchor.transform.position, anchor.transform.rotation);
                bool solved = false; string lastReason = "no authored candidate"; Candidate accepted = default;
                try
                {
                    foreach (var candidate in Candidates(lot))
                    {
                        tried++;
                        var road = CoastalCityLayout.Road(candidate.road);
                        var pose = CityRoadAnchor.Resolve(road, candidate.u, candidate.side * (road.halfWidth + .8f), anchor.height, candidate.side > 0 ? -90 : 90);
                        // Preview only transforms. World-coordinate NPC metadata is changed once, on commit below.
                        anchor.transform.SetPositionAndRotation(pose.position, pose.rotation);
                        lot.buildingBounds = ModelBounds(lot.building);
                        if (!BuildingsClear(lot, lots, out lastReason)) continue;
                        Physics.SyncTransforms();
                        if (!RoadClear(lot, envelopes, hits, out lastReason)) continue;
                        accepted = candidate; solved = true; break;
                    }
                }
                finally
                {
                    anchor.transform.SetPositionAndRotation(before.position, before.rotation);
                    lot.buildingBounds = ModelBounds(lot.building);
                    Physics.SyncTransforms();
                }
                if (!solved)
                    throw new InvalidOperationException("No safe full-build parcel placement for " + anchor.stableId + " in " + lot.district +
                        ". Last conflict: " + lastReason + ". Widen the spacing/range of that district's blueprint lotStrips or move the conflicting road; no lot was discarded.");
                anchor.roadId = accepted.road; anchor.u = accepted.u;
                anchor.lateralFromRoadEdge = true; anchor.lateral = accepted.side * .8f; anchor.yawOffset = accepted.side > 0 ? -90 : 90;
                moved.Add(original);
                if (!anchor.Apply()) throw new InvalidOperationException("Could not commit repaired parcel " + anchor.stableId);
                lot.buildingBounds = ModelBounds(lot.building);
                Physics.SyncTransforms();
                report.AppendLine(anchor.stableId + ": " + original.road + " @ " + original.u.ToString("0.000") + " -> " + anchor.roadId + " @ " + anchor.u.ToString("0.000"));
            }
            foreach (var lot in lots)
                if (PreservedDistrict(lot.district) && !Clear(lot, lots, envelopes, hits, out var reason))
                    throw new InvalidOperationException("Full-build parcel remains obstructed: " + lot.anchor.stableId + ": " + reason);
        }
        catch
        {
            // Fail the build without leaving a partly repaired authored layout behind.
            for (int i = moved.Count - 1; i >= 0; i--)
            {
                var old = moved[i]; var anchor = old.anchor;
                anchor.roadId = old.road; anchor.u = old.u; anchor.lateral = old.lateral; anchor.yawOffset = old.yaw; anchor.lateralFromRoadEdge = old.fromEdge;
                anchor.Apply();
            }
            Physics.SyncTransforms();
            throw;
        }
        return "[Coastal parcels] Checked " + inspected + " preserved homes; moved " + moved.Count + "; tried " + tried + " bounded candidates.\n" + report;
    }

    static bool PreservedDistrict(string id) => id == Districts.MildlyPleasant || id == Districts.MinivanMeadows ||
        id == Districts.SnootingtonHills || id == Districts.BillionaireBluffs;

    static List<Candidate> Candidates(Lot lot)
    {
        int currentSide = lot.anchor.lateral < 0 ? -1 : 1;
        var strips = CoastalCityLayout.Data.lotStrips.Where(s => s.districtId == lot.district &&
            CoastalCityLayout.Road(s.roadId) is CoastalCityLayout.RoadDef r && !r.bridge && !r.service).ToArray();
        var candidates = new List<Candidate>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        void Add(string road, float u, int side)
        {
            string key = road + "/" + side + "/" + Mathf.RoundToInt(u * 100000f);
            if (candidates.Count < MaxCandidatesPerLot && keys.Add(key)) candidates.Add(new Candidate { road = road, u = u, side = side });
        }
        foreach (var strip in strips.Where(s => s.roadId == lot.anchor.roadId && s.side == currentSide))
        {
            var road = CoastalCityLayout.Road(strip.roadId);
            float start = Mathf.Clamp(lot.anchor.u, strip.start, strip.end), step = CandidateStep / Mathf.Max(1, road.Length);
            Add(road.id, start, strip.side);
            int count = Mathf.Min(MaxCandidatesPerLot, Mathf.CeilToInt((strip.end - strip.start) / step));
            for (int i = 1; i <= count; i++)
            {
                if (start + i * step <= strip.end) Add(road.id, start + i * step, strip.side);
                if (start - i * step >= strip.start) Add(road.id, start - i * step, strip.side);
            }
        }
        foreach (var strip in strips)
        {
            var road = CoastalCityLayout.Road(strip.roadId);
            int count = Mathf.Max(1, Mathf.CeilToInt(road.Length * (strip.end - strip.start) / CandidateStep));
            count = Mathf.Min(count, MaxCandidatesPerLot);
            for (int i = 0; i <= count; i++) Add(road.id, Mathf.Lerp(strip.start, strip.end, i / (float)count), strip.side);
        }
        return candidates;
    }

    static Bounds ModelBounds(Transform root)
    {
        bool any = false; Bounds result = default;
        // Estates designate their Hall as buildingRoot, but their substantial house wings are siblings of it.
        // Include those authored structural models without inflating the footprint to fences, pools or driveways.
        var structures = new List<Transform> { root };
        var anchor = root.GetComponentInParent<CityRoadAnchor>();
        if (anchor != null)
            foreach (var child in anchor.GetComponentsInChildren<Transform>(true))
                if (child != root && (child.name.StartsWith("SM_house", StringComparison.Ordinal) || child.name == "Hall"))
                    structures.Add(child);
        var seen = new HashSet<Renderer>();
        foreach (var structure in structures)
        foreach (var renderer in structure.GetComponentsInChildren<Renderer>(true))
        {
            if (!seen.Add(renderer) || !renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer is ParticleSystemRenderer) continue;
            if (!any) { result = renderer.bounds; any = true; } else result.Encapsulate(renderer.bounds);
        }
        if (!any) throw new InvalidOperationException("Preserved building has no visible bounds: " + root.name);
        return result;
    }

    static List<Envelope> RoadEnvelopes() => RoadEnvelopes(CoastalCityLayout.Roads);

    static List<Envelope> RoadEnvelopes(IReadOnlyList<CoastalCityLayout.RoadDef> roads)
    {
        var result = new List<Envelope>();
        foreach (var road in roads)
        {
            // Preserve the full-width curb envelope, including shoulders outside the three checked lanes.
            AddSamples(road, 2f, 1.05f);
            // The final validator uses different stations and a longer nose. A denser sample interval alone
            // is not a superset around bends/endpoints: it missed a house 1.05–1.2 m beyond Pinky-Up Place.
            // At every exact validator pose this box contains ALL three of its lane boxes. Keep this
            // mirrored contract covered by CoastalParcelPlacementTests if CityRoadClearance changes.
            AddSamples(road, 2.5f, 1.25f);
        }
        return result;

        void AddSamples(CoastalCityLayout.RoadDef road, float spacing, float forwardHalf)
        {
            int count = Mathf.Max(1, Mathf.CeilToInt(road.Length / spacing));
            if (result.Count + count + 1 > MaxRoadSamples) throw new InvalidOperationException("City exceeds the bounded parcel clearance sample budget.");
            float laneEnvelopeHalf = Mathf.Min(StreetGraph.LaneOffset, road.halfWidth * .5f) + .85f;
            for (int i = 0; i <= count; i++)
            {
                var pose = road.At(road.Length * i / count);
                var half = new Vector3(Mathf.Max(laneEnvelopeHalf, road.halfWidth - .12f), 1.55f, forwardHalf);
                var x = pose.rotation * Vector3.right; var z = pose.rotation * Vector3.forward;
                var aabb = new Vector3(Mathf.Abs(x.x) * half.x + Mathf.Abs(z.x) * half.z, half.y,
                    Mathf.Abs(x.z) * half.x + Mathf.Abs(z.z) * half.z);
                var center = pose.position + Vector3.up * 1.8f;
                result.Add(new Envelope { road = road.id, center = center, half = half, rotation = pose.rotation, bounds = new Bounds(center, aabb * 2f) });
            }
        }
    }

    static bool Clear(Lot lot, List<Lot> lots, List<Envelope> envelopes, Collider[] hits, out string reason) =>
        BuildingsClear(lot, lots, out reason) && RoadClear(lot, envelopes, hits, out reason);

    static bool BuildingsClear(Lot lot, List<Lot> lots, out string reason)
    {
        foreach (var other in lots)
        {
            if (other == lot) continue;
            var min = Vector3.Max(lot.buildingBounds.min, other.buildingBounds.min);
            var max = Vector3.Min(lot.buildingBounds.max, other.buildingBounds.max);
            if (max.x - min.x > .15f && max.z - min.z > .15f && max.y - min.y > .15f)
            { reason = "building overlaps " + other.anchor.stableId; return false; }
        }
        reason = null; return true;
    }

    static bool RoadClear(Lot lot, List<Envelope> envelopes, Collider[] hits, out string reason)
    {
        bool any = false; Bounds all = default;
        foreach (var collider in lot.colliders)
        {
            if (!any) { all = collider.bounds; any = true; } else all.Encapsulate(collider.bounds);
        }
        if (!any) { reason = null; return true; }
        foreach (var envelope in envelopes)
        {
            if (!envelope.bounds.Intersects(all)) continue;
            int count = Physics.OverlapBoxNonAlloc(envelope.center, envelope.half, hits, envelope.rotation, ~0, QueryTriggerInteraction.Ignore);
            if (count == hits.Length) { reason = "road overlap query saturated on " + envelope.road; return false; }
            for (int i = 0; i < count; i++)
                if (lot.colliders.Contains(hits[i]) && hits[i].bounds.max.y > envelope.center.y - envelope.half.y)
                { reason = hits[i].name + " blocks " + envelope.road; return false; }
        }
        reason = null; return true;
    }
}
