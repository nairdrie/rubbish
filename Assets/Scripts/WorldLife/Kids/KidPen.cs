using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// WorldLife kids (Docs/WorldLife.md §3.1, §12.K): one fenced play area where kids cheer the garbage truck: the inner-park
/// playground, or a front yard. NeighborhoodBuilder places one per pen after the street graph is built (no RNG, so the
/// town, HouseDirectory and StreetGraph stay byte-stable): a static GameObject whose BoxColliders ARE the fence (1.6 m
/// high and 0.9 m thick, so not even a 20 m/s truck on discrete physics gets through), the picket panels as children, and
/// this placement data. <see cref="KidDirector"/> reads every enabled pen of the anchor's scene.
/// Kids are presentation only: never an <see cref="ILifeBody"/>, never in <see cref="LifeBodies"/>, never hit.
/// <para>Frame: local +Z points at the street (the "front" the kids face while they play), +X along the front. The fence
/// line is the rectangle ±<see cref="half"/> around <see cref="center"/>, turned by <see cref="yaw"/>; the run loop is
/// that rectangle inset by <see cref="inset"/>.</para>
/// </summary>
[DisallowMultipleComponent]
public sealed class KidPen : MonoBehaviour
{
    [Tooltip("Stable id: \"playground\", or the house's stopId for a yard pen (kid looks and the daily fan moment hash it).")]
    public string penId = "";
    [Tooltip("The household of a yard pen; empty for the playground.")]
    public string stopId = "";
    [Tooltip("District byte (WorldLifeIds.DistrictIndex).")]
    public byte district;
    [Tooltip("How many kids play here.")]
    [Range(1, 8)] public int kids = 2;
    [Tooltip("Fence rectangle centre (world; y = the floor the kids stand on: sand or lawn).")]
    public Vector3 center;
    [Tooltip("Yaw (deg) of the pen frame: local +Z faces the street.")]
    public float yaw;
    [Tooltip("Half size of the fence rectangle (local x, z), metres.")]
    public Vector2 half = new Vector2(2f, 1.5f);
    [Tooltip("The run loop is the fence rectangle inset by this (m): kids run along it to follow a truck.")]
    public float inset = 0.7f;
    [Tooltip("Home spots (world), one per kid: where they play while no truck is near.")]
    public Vector3[] spots = new Vector3[0];
    [Tooltip("One kid here holds a cardboard sign.")]
    public bool sign = true;

    public Quaternion Rotation => Quaternion.Euler(0f, yaw, 0f);

    /// <summary>World → pen frame (x along the front, z toward the street; y unchanged).</summary>
    public Vector3 ToLocal(Vector3 w)
    {
        Vector3 d = w - center;
        float r = -yaw * Mathf.Deg2Rad, cs = Mathf.Cos(r), sn = Mathf.Sin(r);
        return new Vector3(d.x * cs + d.z * sn, d.y, -d.x * sn + d.z * cs);
    }

    /// <summary>Pen frame → world (on the pen's floor).</summary>
    public Vector3 ToWorld(float x, float z)
    {
        float r = yaw * Mathf.Deg2Rad, cs = Mathf.Cos(r), sn = Mathf.Sin(r);
        return new Vector3(center.x + x * cs + z * sn, center.y, center.z - x * sn + z * cs);
    }

    /// <summary>Half extents of the run loop.</summary>
    public Vector2 LoopHalf => new Vector2(Mathf.Max(0.05f, half.x - inset), Mathf.Max(0.05f, half.y - inset));
    /// <summary>Length of the run loop (m).</summary>
    public float LoopLength { get { var h = LoopHalf; return 4f * (h.x + h.y); } }

    /// <summary>The loop point at arc length <paramref name="s"/>: counter-clockwise (seen from above) from the back-left
    /// corner (−x, −z): back side, right side, front side, left side.</summary>
    public Vector3 LoopPoint(float s)
    {
        var h = LoopHalf;
        float ax = h.x, az = h.y, p = 4f * (ax + az);
        s = Mathf.Repeat(s, p);
        float x, z;
        if (s < 2f * ax) { x = -ax + s; z = -az; }
        else if (s < 2f * ax + 2f * az) { x = ax; z = -az + (s - 2f * ax); }
        else if (s < 4f * ax + 2f * az) { x = ax - (s - 2f * ax - 2f * az); z = az; }
        else { x = -ax; z = az - (s - 4f * ax - 2f * az); }
        return ToWorld(x, z);
    }

    /// <summary>Arc length of the loop point nearest to a world point (inside or outside the pen).</summary>
    public float LoopNearest(Vector3 w)
    {
        var h = LoopHalf;
        float ax = h.x, az = h.y;
        Vector3 l = ToLocal(w);
        float x = Mathf.Clamp(l.x, -ax, ax), z = Mathf.Clamp(l.z, -az, az);
        if (Mathf.Abs(l.x) < ax && Mathf.Abs(l.z) < az)
        {
            // Inside the loop: out to the nearest side.
            float dxm = l.x + ax, dxp = ax - l.x, dzm = l.z + az, dzp = az - l.z;
            float m = Mathf.Min(Mathf.Min(dxm, dxp), Mathf.Min(dzm, dzp));
            if (m == dzm) z = -az; else if (m == dxp) x = ax; else if (m == dzp) z = az; else x = -ax;
        }
        const float e = 1e-4f;
        if (Mathf.Abs(z + az) < e) return x + ax;                                   // back side (corner −x,−z = 0)
        if (Mathf.Abs(x - ax) < e) return 2f * ax + (z + az);                       // right side
        if (Mathf.Abs(z - az) < e) return 2f * ax + 2f * az + (ax - x);             // front side
        return 4f * ax + 2f * az + (az - z);                                         // left side
    }

    /// <summary>The shortest signed way round the loop from <paramref name="from"/> to <paramref name="to"/> (m).</summary>
    public float LoopDelta(float from, float to)
    {
        float p = LoopLength;
        float d = Mathf.Repeat(to - from, p);
        return d > p * 0.5f ? d - p : d;
    }

    /// <summary>Is a world point inside the fence rectangle (grown by <paramref name="pad"/>)?</summary>
    public bool Inside(Vector3 w, float pad = 0f)
    {
        Vector3 l = ToLocal(w);
        return Mathf.Abs(l.x) <= half.x + pad && Mathf.Abs(l.z) <= half.y + pad;
    }

    /// <summary>XZ distance from a world point to the fence rectangle (0 inside).</summary>
    public float DistanceTo(Vector3 w)
    {
        Vector3 l = ToLocal(w);
        float dx = Mathf.Max(0f, Mathf.Abs(l.x) - half.x), dz = Mathf.Max(0f, Mathf.Abs(l.z) - half.y);
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>The kid's home spot (falls back to points spread along the middle when the data has too few).</summary>
    public Vector3 Spot(int k)
    {
        if (spots != null && k >= 0 && k < spots.Length) return spots[k];
        int n = Mathf.Max(1, kids);
        var h = LoopHalf;
        return ToWorld(Mathf.Lerp(-h.x, h.x, (k + 0.5f) / n), 0f);
    }

    // ------------------------------------------------------------------ registry (enabled pens in loaded scenes)

    static readonly List<KidPen> all = new List<KidPen>(8);
    public static IReadOnlyList<KidPen> All => all;
    /// <summary>Bumped whenever a pen enables or disables (the director re-reads its roster).</summary>
    public static int Version { get; private set; }

    void OnEnable() { if (!all.Contains(this)) all.Add(this); Version++; }
    void OnDisable() { all.Remove(this); Version++; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { all.Clear(); Version = 0; }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.8f, 0.2f);
        Vector3 a = ToWorld(-half.x, -half.y), b = ToWorld(half.x, -half.y), c = ToWorld(half.x, half.y), d = ToWorld(-half.x, half.y);
        Gizmos.DrawLine(a, b); Gizmos.DrawLine(b, c); Gizmos.DrawLine(c, d); Gizmos.DrawLine(d, a);
        Gizmos.color = new Color(0.3f, 0.9f, 1f);
        float p = LoopLength;
        Vector3 prev = LoopPoint(0f);
        for (float s = 0.5f; s <= p + 0.01f; s += 0.5f) { var q = LoopPoint(s); Gizmos.DrawLine(prev + Vector3.up * 0.05f, q + Vector3.up * 0.05f); prev = q; }
        Gizmos.color = new Color(1f, 0.4f, 0.7f);
        if (spots != null) foreach (var s in spots) Gizmos.DrawWireSphere(s + Vector3.up * 0.5f, 0.3f);
        Gizmos.DrawLine(center + Vector3.up * 0.2f, ToWorld(0f, half.y + 1.5f) + Vector3.up * 0.2f);   // the front
    }
#endif
}
