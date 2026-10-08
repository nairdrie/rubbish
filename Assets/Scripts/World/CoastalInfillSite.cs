using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>A decorative, road-attached parcel. It never adds collection stops, household IDs or trash production.
/// Its local origin is the frontage centre; positive Z runs into the parcel. The complete footprint is reserved,
/// including open courtyard space, so later road edits cannot drive through its furnishings.</summary>
[DisallowMultipleComponent]
public sealed class CoastalInfillSite : MonoBehaviour
{
    public string siteId, kind;
    public float width, depth;

    public static bool KnownType(string type) => type == "storefront" || type == "workyard" ||
        type == "courtyard" || type == "garden" || type == "estate" || type == "grove";

    public Bounds WorldBounds
    {
        get
        {
            var result = new Bounds(transform.TransformPoint(new Vector3(-width * .5f, 0, 0)), Vector3.zero);
            foreach (float x in new[] { -width * .5f, width * .5f })
                foreach (float z in new[] { 0f, depth }) result.Encapsulate(transform.TransformPoint(new Vector3(x, 0, z)));
            result.Expand(new Vector3(0, 30, 0));
            return result;
        }
    }

    public static List<string> Validate(Transform town)
    {
        var errors = new List<string>();
        if (town == null) return errors;
        var hits = new Collider[256];
        foreach (var site in town.GetComponentsInChildren<CoastalInfillSite>(true))
        {
            string conflict = site.Conflict(town, hits);
            if (conflict != null) errors.Add("Decorative frontage " + site.siteId + ": " + conflict);
        }
        return errors;
    }

    /// <summary>Read-only full-build and incremental-edit clearance check. Solid scenery, driveways, household
    /// paths, pickup space, sidewalks and depot aprons win over decorative density.</summary>
    public string Conflict(Transform town, Collider[] hits)
    {
        if (!KnownType(kind) || !Finite(width) || width < 8 || width > 160 || !Finite(depth) || depth < 8 || depth > 100)
            return "invalid type or footprint";
        if ((transform.lossyScale - Vector3.one).sqrMagnitude > .00001f) return "parcel must retain life-sized scale";
        foreach (var road in CoastalCityLayout.Roads)
            for (int i = 1; i < road.points.Length; i++)
                if (IntersectsSegment(road.points[i - 1], road.points[i], road.halfWidth + 3.1f))
                    return "overlaps road/sidewalk " + road.id;
        foreach (var route in town.GetComponentsInChildren<CoastalHouseRoute>(true))
        {
            var stop = route.GetComponent<PickupStop>();
            string label = stop != null ? stop.stopId : route.name;
            if (stop != null && IntersectsSegment(stop.transform.position, stop.transform.position, 3f))
                return "overlaps collection space " + label;
            Vector3 previous = route.door;
            foreach (var bend in route.bends ?? Array.Empty<Vector3>())
            {
                if (IntersectsSegment(previous, bend, 1.4f)) return "overlaps garden path " + label;
                previous = bend;
            }
            if (IntersectsSegment(previous, route.pathEnd, 1.4f)) return "overlaps garden path " + label;
            if (route.hasDrive && IntersectsSegment(route.ramp, route.top, 2.4f)) return "overlaps driveway " + label;
        }
        for (int lot = 0; lot < 4; lot++)
            if (IntersectsRect(CoastalCityLayout.DepotPad(lot), 1f)) return "overlaps depot apron " + lot;
        foreach (var other in town.GetComponentsInChildren<CoastalInfillSite>(true))
        {
            if (other == this) continue;
            var corners = other.Corners();
            for (int i = 0; i < corners.Length; i++)
                if (IntersectsSegment(corners[i], corners[(i + 1) % corners.Length], .3f)) return "overlaps frontage " + other.siteId;
            if (other.IntersectsSegment(transform.position, transform.position, 0)) return "overlaps frontage " + other.siteId;
        }
        var river = CoastalCityLayout.River;
        for (int i = 1; i < river.Length; i++)
            if (IntersectsSegment(river[i - 1], river[i], 26f)) return "overlaps river bank";
        foreach (var point in Corners())
        {
            if (!CoastalCityLayout.BoundsXZ.Contains(new Vector2(point.x, point.z))) return "outside town bounds";
            var coast = CoastalCityLayout.Coast;
            for (int i = 1; i < coast.Length; i++)
                if (point.z >= Mathf.Min(coast[i - 1].z, coast[i].z) && point.z <= Mathf.Max(coast[i - 1].z, coast[i].z) &&
                    point.x > Mathf.Lerp(coast[i - 1].x, coast[i].x, Mathf.InverseLerp(coast[i - 1].z, coast[i].z, point.z)) - 14f)
                    return "overlaps coastal bank";
        }
        var center = transform.TransformPoint(new Vector3(0, 15.4f, depth * .5f));
        int count = Physics.OverlapBoxNonAlloc(center, new Vector3(width * .5f, 15f, depth * .5f), hits,
            transform.rotation, ~0, QueryTriggerInteraction.Ignore);
        if (count == hits.Length) return "collider query saturated; footprint is not verified";
        for (int i = 0; i < count; i++)
        {
            var hit = hits[i];
            if (!hit || !hit.transform.IsChildOf(town) || hit.transform.IsChildOf(transform) || hit.attachedRigidbody != null ||
                hit.bounds.max.y <= transform.position.y + .3f) continue;
            var anchor = hit.GetComponentInParent<CityRoadAnchor>();
            return "occupied by " + (anchor != null ? anchor.stableId : hit.name);
        }
        return null;
    }

    public Vector3[] Corners() => new[] {
        transform.TransformPoint(new Vector3(-width * .5f, 0, 0)), transform.TransformPoint(new Vector3(width * .5f, 0, 0)),
        transform.TransformPoint(new Vector3(width * .5f, 0, depth)), transform.TransformPoint(new Vector3(-width * .5f, 0, depth)) };

    public bool IntersectsRect(Rect rect, float margin)
    {
        var points = new[] { new Vector3(rect.xMin, 0, rect.yMin), new Vector3(rect.xMax, 0, rect.yMin),
            new Vector3(rect.xMax, 0, rect.yMax), new Vector3(rect.xMin, 0, rect.yMax) };
        for (int i = 0; i < 4; i++) if (IntersectsSegment(points[i], points[(i + 1) % 4], margin)) return true;
        return rect.Contains(new Vector2(transform.position.x, transform.position.z));
    }

    /// <summary>Segment versus an expanded oriented footprint, including containment and zero-length probes.</summary>
    public bool IntersectsSegment(Vector3 worldA, Vector3 worldB, float margin)
    {
        var a = transform.InverseTransformPoint(worldA); var b = transform.InverseTransformPoint(worldB);
        float lo = 0, hi = 1;
        return Clip(a.x, b.x - a.x, -width * .5f - margin, width * .5f + margin, ref lo, ref hi) &&
            Clip(a.z, b.z - a.z, -margin, depth + margin, ref lo, ref hi);
    }
    static bool Clip(float start, float delta, float min, float max, ref float lo, ref float hi)
    {
        if (Mathf.Abs(delta) < .00001f) return start >= min && start <= max;
        float a = (min - start) / delta, b = (max - start) / delta;
        lo = Mathf.Max(lo, Mathf.Min(a, b)); hi = Mathf.Min(hi, Mathf.Max(a, b));
        return lo <= hi;
    }
    static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
}
