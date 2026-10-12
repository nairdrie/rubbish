using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Small faceted meshes for the Hoverhog's model (lathed hulls, duct rings, bent tubes, flat prisms), so it reads as the
/// same low-poly world as the houses and trees instead of a stack of Unity primitives. Every triangle is wound to face
/// away from a point inside its solid, so no shape depends on getting the winding right by hand.
/// </summary>
public static class HogMesh
{
    sealed class Builder
    {
        readonly List<Vector3> verts = new List<Vector3>();

        public void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 inside)
        {
            Vector3 n = Vector3.Cross(b - a, c - a);
            if (n.sqrMagnitude < 1e-12f) return;
            if (Vector3.Dot(n, (a + b + c) / 3f - inside) < 0f) { var t = b; b = c; c = t; }
            verts.Add(a); verts.Add(b); verts.Add(c);
        }

        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 inside)
        {
            Tri(a, b, c, inside);
            Tri(a, c, d, inside);
        }

        public Mesh Build(string name)
        {
            var tris = new int[verts.Count];
            for (int i = 0; i < tris.Length; i++) tris[i] = i;
            var mesh = new Mesh { name = name };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }

    /// <summary>
    /// A hull turned around the Z axis. <paramref name="profile"/> is (z, radius) from back to front; Y is scaled by
    /// <paramref name="squashY"/>. <paramref name="fromDeg"/>..<paramref name="toDeg"/> (0 = +X, 90 = up) cuts a
    /// lengthwise slice, for two-tone paint.
    /// </summary>
    public static Mesh Lathe(string name, Vector2[] profile, int sides, bool capBack, bool capFront, float squashY = 1f,
                             float fromDeg = 0f, float toDeg = 360f)
    {
        var b = new Builder();
        Vector3 P(int i, int s)
        {
            float a = Mathf.Deg2Rad * Mathf.Lerp(fromDeg, toDeg, s / (float)sides);
            return new Vector3(Mathf.Cos(a) * profile[i].y, Mathf.Sin(a) * profile[i].y * squashY, profile[i].x);
        }
        for (int i = 0; i < profile.Length - 1; i++)
        {
            Vector3 inside = new Vector3(0f, 0f, (profile[i].x + profile[i + 1].x) * 0.5f);
            for (int s = 0; s < sides; s++) b.Quad(P(i, s), P(i, s + 1), P(i + 1, s + 1), P(i + 1, s), inside);
        }
        if (capBack)
        {
            Vector3 c = new Vector3(0f, 0f, profile[0].x);
            for (int s = 0; s < sides; s++) b.Tri(c, P(0, s), P(0, s + 1), c + Vector3.forward);
        }
        if (capFront)
        {
            int last = profile.Length - 1;
            Vector3 c = new Vector3(0f, 0f, profile[last].x);
            for (int s = 0; s < sides; s++) b.Tri(c, P(last, s), P(last, s + 1), c + Vector3.back);
        }
        return b.Build(name);
    }

    /// <summary>A ring lying flat (around the Y axis): ring radius, tube radius.</summary>
    public static Mesh Torus(string name, float ring, float tube, int segments, int sides, float fromDeg = 0f, float toDeg = 360f)
    {
        var b = new Builder();
        Vector3 Centre(float u) => new Vector3(Mathf.Cos(u) * ring, 0f, Mathf.Sin(u) * ring);
        Vector3 P(int i, int s)
        {
            float u = Mathf.Deg2Rad * Mathf.Lerp(fromDeg, toDeg, i / (float)segments);
            float v = Mathf.PI * 2f * s / sides;
            float r = ring + Mathf.Cos(v) * tube;
            return new Vector3(Mathf.Cos(u) * r, Mathf.Sin(v) * tube, Mathf.Sin(u) * r);
        }
        for (int i = 0; i < segments; i++)
        {
            float mid = Mathf.Deg2Rad * Mathf.Lerp(fromDeg, toDeg, (i + 0.5f) / segments);
            Vector3 inside = Centre(mid);
            for (int s = 0; s < sides; s++) b.Quad(P(i, s), P(i, s + 1), P(i + 1, s + 1), P(i + 1, s), inside);
        }
        return b.Build(name);
    }

    /// <summary>A pipe along <paramref name="path"/> with a radius per point (one value = the same everywhere), capped.</summary>
    public static Mesh Tube(string name, Vector3[] path, float[] radii, int sides)
    {
        var b = new Builder();
        var rings = new Vector3[path.Length][];
        Vector3 up = Vector3.up;
        for (int i = 0; i < path.Length; i++)
        {
            Vector3 tangent = (path[Mathf.Min(i + 1, path.Length - 1)] - path[Mathf.Max(i - 1, 0)]).normalized;
            if (Mathf.Abs(Vector3.Dot(tangent, up)) > 0.95f) up = Vector3.right;
            Vector3 side = Vector3.Cross(up, tangent).normalized;
            up = Vector3.Cross(tangent, side).normalized;
            float r = radii[Mathf.Min(i, radii.Length - 1)];
            rings[i] = new Vector3[sides + 1];
            for (int s = 0; s <= sides; s++)
            {
                float a = Mathf.PI * 2f * s / sides;
                rings[i][s] = path[i] + (side * Mathf.Cos(a) + up * Mathf.Sin(a)) * r;
            }
        }
        for (int i = 0; i < path.Length - 1; i++)
        {
            Vector3 inside = (path[i] + path[i + 1]) * 0.5f;
            for (int s = 0; s < sides; s++) b.Quad(rings[i][s], rings[i][s + 1], rings[i + 1][s + 1], rings[i + 1][s], inside);
        }
        int last = path.Length - 1;
        for (int s = 0; s < sides; s++)
        {
            b.Tri(path[0], rings[0][s], rings[0][s + 1], path[1]);
            b.Tri(path[last], rings[last][s], rings[last][s + 1], path[last - 1]);
        }
        return b.Build(name);
    }

    /// <summary>A flat convex outline (in XY) given thickness along Z: ears, fins, blades, flags.</summary>
    public static Mesh Prism(string name, Vector2[] outline, float thickness)
    {
        var b = new Builder();
        Vector3 centre = Vector3.zero;
        foreach (var p in outline) centre += (Vector3)p;
        centre /= outline.Length;
        float h = thickness * 0.5f;
        for (int i = 0; i < outline.Length; i++)
        {
            Vector3 p = outline[i], q = outline[(i + 1) % outline.Length];
            Vector3 pf = p + Vector3.forward * h, pb = p + Vector3.back * h, qf = q + Vector3.forward * h, qb = q + Vector3.back * h;
            b.Quad(pb, pf, qf, qb, centre);
            b.Tri(centre + Vector3.forward * h, pf, qf, centre);
            b.Tri(centre + Vector3.back * h, pb, qb, centre);
        }
        return b.Build(name);
    }

    /// <summary>An open cone, apex at the origin, widening downwards; drawn on both faces (the tractor beam).</summary>
    public static Mesh Cone(string name, float topRadius, float bottomRadius, float height, int sides)
    {
        var b = new Builder();
        Vector3 axis = Vector3.down * height * 0.5f;
        for (int s = 0; s < sides; s++)
        {
            float a0 = Mathf.PI * 2f * s / sides, a1 = Mathf.PI * 2f * (s + 1) / sides;
            Vector3 t0 = new Vector3(Mathf.Cos(a0) * topRadius, 0f, Mathf.Sin(a0) * topRadius);
            Vector3 t1 = new Vector3(Mathf.Cos(a1) * topRadius, 0f, Mathf.Sin(a1) * topRadius);
            Vector3 b0 = new Vector3(Mathf.Cos(a0) * bottomRadius, -height, Mathf.Sin(a0) * bottomRadius);
            Vector3 b1 = new Vector3(Mathf.Cos(a1) * bottomRadius, -height, Mathf.Sin(a1) * bottomRadius);
            b.Quad(t0, t1, b1, b0, axis);
            b.Quad(t0, t1, b1, b0, axis + (t0 + b0) * 2f); // the inside face
        }
        return b.Build(name);
    }
}
