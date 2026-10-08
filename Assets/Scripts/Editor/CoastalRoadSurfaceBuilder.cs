using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Pure planar road union. Shared corners have bounded bevels; pavement is the outer envelope minus
/// every carriageway, never a second raised ribbon laid across an intersection. No scene or asset writes.</summary>
public static class CoastalRoadSurfaceBuilder
{
    public const int SurfaceRevision = CityGeneratedRoad.SurfaceRevision;
    public const float AsphaltHeight = .02f, WalkHeight = .055f, WalkWidth = 3f;
    const float PointEpsilon = .00005f;
    const double AreaEpsilon = .000001;

    public sealed class Surface
    {
        public readonly List<Vector3> vertices = new List<Vector3>();
        public readonly List<int> triangles = new List<int>();
    }
    public sealed class RoadSurface
    {
        public string roadId;
        public readonly Surface asphalt = new Surface(), leftWalk = new Surface(), rightWalk = new Surface();
    }
    sealed class Polygon
    {
        public readonly List<Vector2> points;
        public readonly Vector2 min, max;
        public readonly int owner;
        public Polygon(List<Vector2> points, int owner)
        {
            this.points = points; this.owner = owner;
            min = max = points[0];
            foreach (var point in points) { min = Vector2.Min(min, point); max = Vector2.Max(max, point); }
        }
        public bool Overlaps(Polygon other) => min.x <= other.max.x && max.x >= other.min.x && min.y <= other.max.y && max.y >= other.min.y;
    }
    sealed class Join
    {
        public int owner;
        public Vector2 point;
        public readonly List<Vector2> asphalt = new List<Vector2>(), outer = new List<Vector2>();
        public readonly List<Leg> legs = new List<Leg>();
    }
    struct Leg { public int owner; public Vector2 direction; public float width, length; }
    // Only local neighbours can clip one another. Deterministic sorted candidates avoid all-pairs work on
    // the winding districts, and fixed clipping order keeps repeated bakes byte-for-byte reproducible.
    sealed class Index
    {
        const float Cell = 32f;
        readonly Dictionary<Vector2Int, List<int>> cells = new Dictionary<Vector2Int, List<int>>();
        public readonly List<Polygon> polygons = new List<Polygon>();
        public void Add(Polygon polygon)
        {
            int id = polygons.Count; polygons.Add(polygon);
            foreach (var cell in Cells(polygon))
            {
                if (!cells.TryGetValue(cell, out var list)) cells[cell] = list = new List<int>();
                list.Add(id);
            }
        }
        public List<Polygon> Query(Polygon polygon)
        {
            var ids = new HashSet<int>();
            foreach (var cell in Cells(polygon)) if (cells.TryGetValue(cell, out var values)) foreach (int id in values) ids.Add(id);
            var sorted = new List<int>(ids); sorted.Sort(); var result = new List<Polygon>();
            foreach (int id in sorted) if (polygon.Overlaps(polygons[id])) result.Add(polygons[id]);
            return result;
        }
        static IEnumerable<Vector2Int> Cells(Polygon polygon)
        {
            int x0 = Mathf.FloorToInt(polygon.min.x / Cell), x1 = Mathf.FloorToInt(polygon.max.x / Cell);
            int y0 = Mathf.FloorToInt(polygon.min.y / Cell), y1 = Mathf.FloorToInt(polygon.max.y / Cell);
            for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) yield return new Vector2Int(x, y);
        }
    }

    public static List<RoadSurface> Build(IReadOnlyList<CoastalCityLayout.RoadDef> roads)
    {
        if (roads == null) throw new ArgumentNullException(nameof(roads));
        var ordered = new List<CoastalCityLayout.RoadDef>(roads);
        ordered.Sort((a, b) => string.CompareOrdinal(a?.id, b?.id));
        var result = new List<RoadSurface>(); var asphalt = new List<Polygon>(); var outer = new List<Polygon>();
        var joins = new Dictionary<Vector2, Join>(); var joinOrder = new List<Join>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int owner = 0; owner < ordered.Count; owner++)
        {
            var road = ordered[owner];
            if (road == null || string.IsNullOrEmpty(road.id) || !ids.Add(road.id) || !Finite(road.halfWidth) || road.halfWidth <= 0 || road.points == null || road.points.Length < 2)
                throw new ArgumentException("Road surfaces require unique IDs, positive widths and at least two finite points.", nameof(roads));
            result.Add(new RoadSurface { roadId = road.id });
            foreach (var point in road.points) if (!Finite(point.x) || !Finite(point.z)) throw new ArgumentException("Non-finite road point: " + road.id, nameof(roads));
            for (int i = 1; i < road.points.Length; i++)
            {
                var a = XZ(road.points[i - 1]); var b = XZ(road.points[i]); var delta = b - a;
                if (delta.sqrMagnitude < PointEpsilon * PointEpsilon) continue;
                var normal = new Vector2(-delta.y, delta.x).normalized;
                AddPolygon(asphalt, Rectangle(a, b, normal * road.halfWidth), owner);
                AddPolygon(outer, Rectangle(a, b, normal * (road.halfWidth + WalkWidth)), owner);
                foreach (var point in new[] { a, b })
                {
                    if (!joins.TryGetValue(point, out var join)) { joins[point] = join = new Join { owner = owner, point = point }; joinOrder.Add(join); }
                    join.asphalt.Add(point + normal * road.halfWidth); join.asphalt.Add(point - normal * road.halfWidth);
                    join.outer.Add(point + normal * (road.halfWidth + WalkWidth)); join.outer.Add(point - normal * (road.halfWidth + WalkWidth));
                    join.legs.Add(new Leg { owner = owner, direction = (point == a ? delta : -delta).normalized, width = road.halfWidth, length = delta.magnitude });
                }
            }
        }
        // Convex hulls of incident edge endpoints make bounded bevel joins, including across different road
        // IDs and the repeated first/last point of a loop. Unlike a normalised average tangent they never
        // pinch the approaches; unlike an unbounded miter they cannot send acute corners across a parcel.
        foreach (var join in joinOrder)
        {
            AddPolygon(asphalt, Hull(join.asphalt), join.owner);
            AddPolygon(outer, Hull(join.outer), join.owner);
            AddWidthTransition(join, asphalt, outer);
        }
        var asphaltIndex = new Index();
        foreach (var polygon in asphalt)
        {
            var pieces = Subtract(new List<Polygon> { polygon }, asphaltIndex.Query(polygon));
            foreach (var piece in pieces) Emit(result[piece.owner].asphalt, piece, AsphaltHeight);
            asphaltIndex.Add(polygon);
        }
        var outerIndex = new Index();
        foreach (var polygon in outer)
        {
            // (new outer envelope - old outer envelopes) - all asphalt. Assigning each planar region once
            // removes z-fighting as well as sidewalk collision strips through a junction.
            var pieces = Subtract(new List<Polygon> { polygon }, outerIndex.Query(polygon));
            pieces = Subtract(pieces, asphaltIndex.Query(polygon));
            foreach (var piece in pieces)
            {
                var road = ordered[piece.owner]; var surface = result[piece.owner];
                Emit(IsLeft(piece, road) ? surface.leftWalk : surface.rightWalk, piece, WalkHeight);
            }
            outerIndex.Add(polygon);
        }
        return result;
    }

    static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    static void AddWidthTransition(Join join, List<Polygon> asphalt, List<Polygon> outer)
    {
        // A bridge's wider deck should taper into its two-legged approach instead of leaving a one-metre
        // shoulder step. Keep genuine intersections and sharp corners untouched, and stay within half of
        // the adjacent segment so the expansion cannot reach the next junction or alter its centreline.
        if (join.legs.Count != 2 || Vector2.Dot(join.legs[0].direction, join.legs[1].direction) >= -.95f ||
            Mathf.Abs(join.legs[0].width - join.legs[1].width) <= .05f) return;
        var narrow = join.legs[0].width < join.legs[1].width ? join.legs[0] : join.legs[1];
        var wide = join.legs[0].width < join.legs[1].width ? join.legs[1] : join.legs[0];
        float length = Mathf.Min(8f, narrow.length * .5f);
        var end = join.point + narrow.direction * length; var normal = new Vector2(-narrow.direction.y, narrow.direction.x);
        AddPolygon(asphalt, Taper(join.point, end, normal, wide.width, narrow.width), narrow.owner);
        AddPolygon(outer, Taper(join.point, end, normal, wide.width + WalkWidth, narrow.width + WalkWidth), narrow.owner);
    }
    static List<Vector2> Taper(Vector2 a, Vector2 b, Vector2 normal, float widthA, float widthB) =>
        new List<Vector2> { a - normal * widthA, b - normal * widthB, b + normal * widthB, a + normal * widthA };
    static Vector2 XZ(Vector3 p) => new Vector2(p.x, p.z);
    static List<Vector2> Rectangle(Vector2 a, Vector2 b, Vector2 offset) => new List<Vector2> { a - offset, b - offset, b + offset, a + offset };
    static double Cross(Vector2 a, Vector2 b) => (double)a.x * b.y - (double)a.y * b.x;
    static double Area(List<Vector2> p)
    {
        double area = 0; for (int i = 1; i < p.Count - 1; i++) area += Cross(p[i] - p[0], p[i + 1] - p[0]);
        return area * .5;
    }
    static void AddPolygon(List<Polygon> target, List<Vector2> points, int owner)
    {
        points = Clean(points);
        if (points.Count >= 3 && Area(points) > AreaEpsilon) target.Add(new Polygon(points, owner));
    }
    static List<Vector2> Hull(List<Vector2> input)
    {
        var p = new List<Vector2>(input); p.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
        var result = new List<Vector2>();
        foreach (var point in p)
        {
            while (result.Count >= 2 && Cross(result[result.Count - 1] - result[result.Count - 2], point - result[result.Count - 1]) <= AreaEpsilon) result.RemoveAt(result.Count - 1);
            result.Add(point);
        }
        int lower = result.Count;
        for (int i = p.Count - 2; i >= 0; i--)
        {
            while (result.Count > lower && Cross(result[result.Count - 1] - result[result.Count - 2], p[i] - result[result.Count - 1]) <= AreaEpsilon) result.RemoveAt(result.Count - 1);
            result.Add(p[i]);
        }
        if (result.Count > 0) result.RemoveAt(result.Count - 1);
        return result;
    }
    static List<Polygon> Subtract(List<Polygon> pieces, List<Polygon> clips)
    {
        foreach (var clip in clips)
        {
            var next = new List<Polygon>();
            foreach (var piece in pieces)
            {
                if (!piece.Overlaps(clip)) { next.Add(piece); continue; }
                var remaining = piece.points;
                // At each edge, retain the outside part and carry only the inside part to the next edge.
                // Outputs are disjoint convex polygons; the final inside remainder belongs to the clip.
                for (int i = 0; i < clip.points.Count && remaining.Count >= 3; i++)
                {
                    var a = clip.points[i]; var b = clip.points[(i + 1) % clip.points.Count];
                    AddPolygon(next, HalfPlane(remaining, a, b, false), piece.owner);
                    remaining = HalfPlane(remaining, a, b, true);
                }
            }
            pieces = next;
            if (pieces.Count == 0) break;
        }
        return pieces;
    }
    static List<Vector2> HalfPlane(List<Vector2> polygon, Vector2 a, Vector2 b, bool inside)
    {
        var result = new List<Vector2>(); if (polygon.Count == 0) return result;
        var previous = polygon[polygon.Count - 1]; double oldDistance = Cross(b - a, previous - a);
        bool oldKeep = inside ? oldDistance >= 0 : oldDistance <= 0;
        foreach (var point in polygon)
        {
            double distance = Cross(b - a, point - a); bool keep = inside ? distance >= 0 : distance <= 0;
            if (oldKeep != keep)
            {
                double t = oldDistance / (oldDistance - distance);
                result.Add(new Vector2((float)(previous.x + (point.x - previous.x) * t), (float)(previous.y + (point.y - previous.y) * t)));
            }
            if (keep) result.Add(point);
            previous = point; oldDistance = distance; oldKeep = keep;
        }
        return Clean(result);
    }
    static List<Vector2> Clean(List<Vector2> input)
    {
        var result = new List<Vector2>();
        foreach (var point in input) if (result.Count == 0 || (point - result[result.Count - 1]).sqrMagnitude > PointEpsilon * PointEpsilon) result.Add(point);
        if (result.Count > 1 && (result[0] - result[result.Count - 1]).sqrMagnitude <= PointEpsilon * PointEpsilon) result.RemoveAt(result.Count - 1);
        return result;
    }
    static bool IsLeft(Polygon polygon, CoastalCityLayout.RoadDef road)
    {
        Vector2 centre = Vector2.zero; foreach (var point in polygon.points) centre += point; centre /= polygon.points.Count;
        float nearest = float.MaxValue; double side = 0;
        for (int i = 1; i < road.points.Length; i++)
        {
            var a = XZ(road.points[i - 1]); var delta = XZ(road.points[i]) - a;
            float t = Mathf.Clamp01(Vector2.Dot(centre - a, delta) / Mathf.Max(.00000001f, delta.sqrMagnitude));
            float distance = (centre - a - delta * t).sqrMagnitude;
            if (distance < nearest) { nearest = distance; side = Cross(delta, centre - a); }
        }
        return side >= 0;
    }
    static void Emit(Surface surface, Polygon polygon, float y)
    {
        int start = surface.vertices.Count;
        foreach (var point in polygon.points) surface.vertices.Add(new Vector3(point.x, y, point.y));
        for (int i = 1; i < polygon.points.Count - 1; i++)
        {
            if (Cross(polygon.points[i] - polygon.points[0], polygon.points[i + 1] - polygon.points[0]) <= AreaEpsilon * 2) continue;
            // XZ CCW is -Y; reverse the triangle for a driveable, upward-facing mesh/collider.
            surface.triangles.Add(start); surface.triangles.Add(start + i + 1); surface.triangles.Add(start + i);
        }
    }
}
