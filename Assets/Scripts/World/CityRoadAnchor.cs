using System;
using UnityEngine;

/// <summary>
/// Authored placement of a complete lot/fixture relative to a stable city road. Only explicit Apply calls move it:
/// loading a save, enabling a shop or joining a session must never rearrange the live town automatically.
/// </summary>
[DisallowMultipleComponent]
public sealed class CityRoadAnchor : MonoBehaviour
{
    [Tooltip("Persistent authored identity; never regenerated when the road or its coordinates change.")]
    public string stableId = "";
    public string roadId = "";
    [Range(0f, 1f)] public float u;
    [Tooltip("Life-sized metres to the road's right (negative = left), or signed clearance beyond the curb when Lateral From Road Edge is enabled.")]
    public float lateral;
    [Tooltip("Keep this lot's signed lateral clearance beyond the road edge when the road width changes. Disabled preserves original centreline-relative anchors.")]
    public bool lateralFromRoadEdge;
    [Tooltip("World-up metres above the road anchor.")]
    public float height;
    [Tooltip("Local yaw relative to the road tangent, in degrees.")]
    public float yawOffset;

    /// <summary>An unresolved/invalid road leaves the existing authored placement intact.</summary>
    public Pose DesiredPose
    {
        get
        {
            var road = CoastalCityLayout.Road(roadId);
            return CanResolveAnchor(road)
                ? Resolve(road)
                : new Pose(transform.position, transform.rotation);
        }
    }

    /// <summary>Pure placement calculation. Distances are metres, not blueprint units or building scale.</summary>
    public static Pose Resolve(CoastalCityLayout.RoadDef road, float u, float lateral, float height, float yawOffset)
    {
        if (!CanResolve(road, u, lateral, height, yawOffset))
            throw new ArgumentException("A road anchor requires a finite, nonzero road and finite placement values.");
        var pose = road.AtNormalized(u);
        pose.position += pose.rotation * Vector3.right * lateral + Vector3.up * height;
        pose.rotation *= Quaternion.Euler(0f, yawOffset, 0f);
        return pose;
    }

    /// <summary>Resolve this anchor including its curb-relative mode. Zero clearance selects the right curb;
    /// a negative clearance selects the left. Road width never scales the lot or its clearance.</summary>
    public Pose Resolve(CoastalCityLayout.RoadDef road)
    {
        if (!CanResolveAnchor(road))
            throw new ArgumentException("A road anchor requires a finite, nonzero road and finite placement values; curb-relative anchors also require a positive finite road half-width.");
        float offset = lateralFromRoadEdge ? Mathf.Sign(lateral) * (road.halfWidth + Mathf.Abs(lateral)) : lateral;
        return Resolve(road, u, offset, height, yawOffset);
    }

    /// <summary>Apply the current blueprint road explicitly; false means nothing moved.</summary>
    public bool Apply() => Apply(CoastalCityLayout.Road(roadId));

    /// <summary>
    /// Explicit-road overload for authoring previews/tests. The supplied road must have this anchor's roadId;
    /// applying a different road never silently reassigns a stable lot to it.
    /// </summary>
    public bool Apply(CoastalCityLayout.RoadDef road)
    {
        if (road == null || !string.Equals(road.id, roadId, StringComparison.Ordinal) ||
            !CanResolveAnchor(road)) return false;
        var next = Resolve(road);
        var before = new Pose(transform.position, transform.rotation);
        var turn = next.rotation * Quaternion.Inverse(before.rotation);
        // Transform children follow the root themselves. These fields are WORLD coordinates and do not;
        // update them from the same old/new rigid frame, including metadata on inactive buildings/yards.
        foreach (var route in GetComponentsInChildren<CoastalHouseRoute>(true))
        {
            route.door = MovePoint(route.door, before, next, turn);
            route.pathEnd = MovePoint(route.pathEnd, before, next, turn);
            route.mailbox = MovePoint(route.mailbox, before, next, turn);
            route.ramp = MovePoint(route.ramp, before, next, turn);
            route.top = MovePoint(route.top, before, next, turn);
            if (route.bends != null)
                for (int i = 0; i < route.bends.Length; i++) route.bends[i] = MovePoint(route.bends[i], before, next, turn);
            route.car0 = MovePose(route.car0, before, next, turn);
            route.car1 = MovePose(route.car1, before, next, turn);
            route.butlerPost = MovePose(route.butlerPost, before, next, turn);
        }
        foreach (var pen in GetComponentsInChildren<KidPen>(true))
        {
            pen.center = MovePoint(pen.center, before, next, turn);
            if (pen.spots != null)
                for (int i = 0; i < pen.spots.Length; i++) pen.spots[i] = MovePoint(pen.spots[i], before, next, turn);
            pen.yaw = (turn * Quaternion.Euler(0f, pen.yaw, 0f)).eulerAngles.y;
        }
        transform.SetPositionAndRotation(next.position, next.rotation);
        return true;
    }

    static Vector3 MovePoint(Vector3 value, Pose before, Pose next, Quaternion turn) =>
        next.position + turn * (value - before.position);

    static Pose MovePose(Pose value, Pose before, Pose next, Quaternion turn) =>
        new Pose(MovePoint(value.position, before, next, turn), turn * value.rotation);

    bool CanResolveAnchor(CoastalCityLayout.RoadDef road) =>
        CanResolve(road, u, lateral, height, yawOffset) &&
        (!lateralFromRoadEdge || (Finite(road.halfWidth) && road.halfWidth > 0f && Finite(road.halfWidth + Mathf.Abs(lateral))));

    static bool CanResolve(CoastalCityLayout.RoadDef road, float u, float lateral, float height, float yawOffset)
    {
        if (road?.points == null || road.points.Length < 2 || !Finite(u) || !Finite(lateral) || !Finite(height) || !Finite(yawOffset)) return false;
        bool segment = false;
        for (int i = 0; i < road.points.Length; i++)
        {
            var p = road.points[i];
            if (!Finite(p.x) || !Finite(p.y) || !Finite(p.z)) return false;
            // RoadDef.At uses the segment at u's boundary. Zero-length segments would create a zero LookRotation.
            if (i > 0)
            {
                if ((p - road.points[i - 1]).sqrMagnitude < .000001f) return false;
                segment = true;
            }
        }
        return segment;
    }

    static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
