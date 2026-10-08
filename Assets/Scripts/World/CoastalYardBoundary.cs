using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Authored property boundaries, not gameplay ownership. Rebuilt explicitly with the city, never at runtime.</summary>
[DisallowMultipleComponent]
public sealed class CoastalYardBoundary : MonoBehaviour
{
    public string[] stopIds = Array.Empty<string>();
    public string roadId = "";
    public string sourceFingerprint = "";
    public string layoutFingerprint = "";
    public Vector3[] ownerPositions = Array.Empty<Vector3>();
    public Quaternion[] ownerRotations = Array.Empty<Quaternion>();
    [Tooltip("Endpoint pairs in this object's local space; shared boundaries occur only once.")]
    public Vector3[] boundaryPoints = Array.Empty<Vector3>();

    public static string SettingsFingerprint()
    {
        var settings = Resources.Load<TextAsset>("City/CoastalYards");
        return settings != null ? Hash128.Compute(settings.text).ToString() : "";
    }

    public static string LayoutFingerprint()
    {
        var value = new System.Text.StringBuilder();
        foreach (var road in CoastalCityLayout.Roads) value.Append(road.Fingerprint).Append('|');
        return Hash128.Compute(value.ToString()).ToString();
    }

    /// <summary>Read-only authoring validation; moving an owner/road or changing settings requires explicit regeneration.</summary>
    public static List<string> Validate(Transform town)
    {
        var errors = new List<string>();
        if (!CoastalCityLayout.IsCoastal(town)) return errors;
        string settings = SettingsFingerprint(), layout = LayoutFingerprint();
        var owners = new Dictionary<string, CityRoadAnchor>(StringComparer.Ordinal);
        foreach (var anchor in TownAuthoringScope.Components<CityRoadAnchor>(town))
            if (!string.IsNullOrEmpty(anchor.stableId) && !owners.ContainsKey(anchor.stableId)) owners.Add(anchor.stableId, anchor);
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var yard in TownAuthoringScope.Components<CoastalYardBoundary>(town))
        {
            string label = "Backyard " + yard.roadId;
            if (yard.sourceFingerprint != settings || yard.layoutFingerprint != layout)
                errors.Add(label + ": stale road/settings source; Apply Blueprint Changes to rebuild boundaries.");
            if (yard.transform.localPosition.sqrMagnitude > .0001f || Quaternion.Angle(yard.transform.localRotation, Quaternion.identity) > .1f ||
                Vector3.Distance(yard.transform.localScale, Vector3.one) > .001f)
                errors.Add(label + ": generated boundary group was manually moved/scaled; regenerate it from its property anchors.");
            if (yard.stopIds == null || yard.ownerPositions == null || yard.ownerRotations == null ||
                yard.stopIds.Length != yard.ownerPositions.Length || yard.stopIds.Length != yard.ownerRotations.Length)
            { errors.Add(label + ": incomplete property-owner pose stamp."); continue; }
            for (int i = 0; i < yard.stopIds.Length; i++)
            {
                string id = yard.stopIds[i] ?? "";
                if (!claimed.Add(id)) errors.Add(label + ": duplicate boundary ownership for " + id);
                if (!owners.TryGetValue(id, out var owner)) { errors.Add(label + ": missing property owner " + id); continue; }
                if (owner.roadId != yard.roadId || Vector3.Distance(owner.transform.position, yard.ownerPositions[i]) > .03f ||
                    Quaternion.Angle(owner.transform.rotation, yard.ownerRotations[i]) > .1f)
                    errors.Add(label + ": owner " + id + " moved after boundary generation; Apply Blueprint Changes.");
            }
            var points = yard.boundaryPoints;
            var boxes = yard.GetComponentsInChildren<BoxCollider>(true);
            if (points == null || points.Length % 2 != 0 || boxes.Length != points.Length / 2)
            { errors.Add(label + ": fence collider inventory differs from authored endpoint pairs."); continue; }
            for (int i = 0; i < points.Length; i += 2)
            {
                var a = yard.transform.TransformPoint(points[i]); var b = yard.transform.TransformPoint(points[i + 1]);
                bool found = false;
                foreach (var box in boxes)
                {
                    var c = box.transform.TransformPoint(new Vector3(box.center.x, 0, box.center.z - box.size.z * .5f));
                    var d = box.transform.TransformPoint(new Vector3(box.center.x, 0, box.center.z + box.size.z * .5f));
                    if (Vector3.Distance(a, c) < .03f && Vector3.Distance(b, d) < .03f ||
                        Vector3.Distance(a, d) < .03f && Vector3.Distance(b, c) < .03f) { found = box.enabled; break; }
                }
                if (!found) errors.Add(label + ": a boundary run is missing, moved or disabled.");
            }
        }
        return errors;
    }
}

/// <summary>Pure geometry used by authoring and tests. No Physics, scene searches, RNG or gameplay side effects.</summary>
public static class CoastalYardRules
{
    /// <summary>CoastalHouseRoute bends are stored from the house door toward the sidewalk, never in reverse.</summary>
    public static float PathDistance(Vector3 a, Vector3 b, Vector3 door, Vector3[] bends, Vector3 pathEnd)
    {
        float closest = float.PositiveInfinity;
        Vector3 previous = door;
        foreach (var point in bends ?? Array.Empty<Vector3>())
        { closest = Mathf.Min(closest, SegmentDistance(a, b, previous, point)); previous = point; }
        return Mathf.Min(closest, SegmentDistance(a, b, previous, pathEnd));
    }

    public static float SegmentDistance(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        Vector2 ab = new Vector2(b.x - a.x, b.z - a.z), cd = new Vector2(d.x - c.x, d.z - c.z);
        Vector2 ac = new Vector2(c.x - a.x, c.z - a.z);
        float Cross(Vector2 p, Vector2 q) => p.x * q.y - p.y * q.x;
        float denominator = Cross(ab, cd);
        if (Mathf.Abs(denominator) > .00001f)
        {
            float t = Cross(ac, cd) / denominator, u = Cross(ac, ab) / denominator;
            if (t >= 0 && t <= 1 && u >= 0 && u <= 1) return 0;
        }
        return Mathf.Min(DistanceToSegment(a, c, d), DistanceToSegment(b, c, d),
            DistanceToSegment(c, a, b), DistanceToSegment(d, a, b));
    }

    public static float DistanceToSegment(Vector3 point, Vector3 a, Vector3 b)
    {
        point.y = a.y = b.y = 0;
        Vector3 delta = b - a;
        float t = delta.sqrMagnitude > .000001f ? Mathf.Clamp01(Vector3.Dot(point - a, delta) / delta.sqrMagnitude) : 0;
        return Vector3.Distance(point, a + delta * t);
    }

    /// <summary>Remove an already-built collinear divider, keeping all non-overlapping tails. Crossing lines are not coverage.</summary>
    public static Vector3[] UncoveredSegments(Vector3 a, Vector3 b, Vector3[] coverage)
    {
        var delta = b - a; delta.y = 0;
        float length = delta.magnitude;
        if (length < .05f) return Array.Empty<Vector3>();
        var axis = delta / length;
        var intervals = new List<Vector2> { new Vector2(0, length) };
        for (int i = 0; coverage != null && i + 1 < coverage.Length; i += 2)
        {
            var ca = coverage[i] - a; ca.y = 0;
            var cb = coverage[i + 1] - a; cb.y = 0;
            // Infinite-line distance, not clamped segment distance: longer coverage must also remove a short line.
            if (Vector3.Cross(ca, axis).magnitude > .06f || Vector3.Cross(cb, axis).magnitude > .06f) continue;
            float l = Vector3.Dot(ca, axis), r = Vector3.Dot(cb, axis);
            if (r < l) (l, r) = (r, l);
            var next = new List<Vector2>();
            foreach (var span in intervals)
            {
                if (r <= span.x || l >= span.y) { next.Add(span); continue; }
                if (l > span.x) next.Add(new Vector2(span.x, l));
                if (r < span.y) next.Add(new Vector2(r, span.y));
            }
            intervals = next;
        }
        var result = new List<Vector3>();
        foreach (var span in intervals)
            if (span.y - span.x >= .05f) { result.Add(a + axis * span.x); result.Add(a + axis * span.y); }
        return result.ToArray();
    }

    /// <summary>True only for an actual X intersection; joined corners/T junctions and a shared divider are intentional.</summary>
    public static bool CrossesBoundary(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        Vector2 aa = new Vector2(a.x, a.z), bb = new Vector2(b.x, b.z), cc = new Vector2(c.x, c.z), dd = new Vector2(d.x, d.z);
        float Cross(Vector2 p, Vector2 q) => p.x * q.y - p.y * q.x;
        Vector2 ab = bb - aa, cd = dd - cc;
        float denom = Cross(ab, cd);
        if (Mathf.Abs(denom) < .0001f) return false;
        float t = Cross(cc - aa, cd) / denom, u = Cross(cc - aa, ab) / denom;
        const float tolerance = .025f;
        return t > tolerance && t < 1 - tolerance && u > tolerance && u < 1 - tolerance;
    }
}
