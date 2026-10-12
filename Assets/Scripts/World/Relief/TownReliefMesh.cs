using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Lays flat-town geometry onto the relief. Every vertex moves straight up by the ground height under it, so walls
/// stay vertical; a triangle that reaches across terrain facets is first cut along them, so a lawn, a verge or a road
/// lies exactly ON the terrain instead of bridging its creases. Pure: no scene, no assets.
/// </summary>
public static class TownReliefMesh
{
    /// <summary>Triangles narrower than this (in plan) are only moved, never cut: the error is under a millimetre.</summary>
    public const float CutAbove = 0.75f;

    public sealed class Data
    {
        public List<Vector3> vertices = new List<Vector3>(), normals = new List<Vector3>();
        public List<Vector2> uv;                                    // null when the source has none
        public List<Vector2> uv2;                                   // water only: x = metres from the waterline
        public List<List<int>> triangles = new List<List<int>>();   // one list per submesh
        public int VertexCount => vertices.Count;
    }

    struct V { public Vector3 p, n; public Vector2 uv; }

    static V Mix(V a, V b, float t) => new V { p = Vector3.LerpUnclamped(a.p, b.p, t), n = Vector3.LerpUnclamped(a.n, b.n, t), uv = Vector2.LerpUnclamped(a.uv, b.uv, t) };

    /// <summary>Keeps the part of a convex polygon where sign * (ax * x + az * z - c) >= 0.</summary>
    static void Clip(List<V> source, List<V> result, float ax, float az, float c, float sign)
    {
        result.Clear(); int count = source.Count; if (count == 0) return;
        V previous = source[count - 1]; float dp = sign * (ax * previous.p.x + az * previous.p.z - c);
        for (int i = 0; i < count; i++)
        {
            V current = source[i]; float dc = sign * (ax * current.p.x + az * current.p.z - c);
            if (dc >= 0f)
            {
                if (dp < 0f) result.Add(Mix(previous, current, dp / (dp - dc)));
                result.Add(current);
            }
            else if (dp >= 0f) result.Add(Mix(previous, current, dp / (dp - dc)));
            previous = current; dp = dc;
        }
    }

    static Vector3 Tilt(Vector3 n, Vector2 gradient)
    {
        // inverse transpose of the shear y' = y + gx * x + gz * z
        var t = new Vector3(n.x - gradient.x * n.y, n.y, n.z - gradient.y * n.y);
        float m = t.magnitude; return m > 1e-6f ? t / m : Vector3.up;
    }

    /// <summary>
    /// Drapes a mesh. <paramref name="flatToWorld"/> is where the object stood in the flat town; the result is
    /// expressed in <paramref name="worldToFinal"/>'s space (the object's transform after the bake moved it; the same
    /// matrix inverted when it did not move). Vertices keep their order; cut triangles append new ones.
    /// </summary>
    public static Data Drape(TownReliefField field, IList<Vector3> vertices, IList<Vector3> normals, IList<Vector2> uv,
        IList<IList<int>> submeshes, Matrix4x4 flatToWorld, Matrix4x4 worldToFinal)
    {
        var data = new Data(); bool hasUv = uv != null && uv.Count == vertices.Count; if (hasUv) data.uv = new List<Vector2>(vertices.Count);
        bool hasNormals = normals != null && normals.Count == vertices.Count;
        var normalToWorld = flatToWorld.inverse.transpose; var normalToFinal = worldToFinal.inverse.transpose;
        int count = vertices.Count; var world = new V[count];
        for (int i = 0; i < count; i++)
        {
            world[i].p = flatToWorld.MultiplyPoint3x4(vertices[i]);
            world[i].n = hasNormals ? normalToWorld.MultiplyVector(normals[i]).normalized : Vector3.up;
            world[i].uv = hasUv ? uv[i] : Vector2.zero;
        }
        // Pieces cut from neighbouring triangles meet in the same points: those are stored once (to a tenth of a
        // millimetre), which keeps a cut road or lawn a few times smaller than a loose pile of triangles would be.
        var welded = new Dictionary<(long, long, long, long), int>();
        int Emit(V v, Vector2 gradient, bool weld)
        {
            var p = v.p; p.y += field.Height(p.x, p.z);
            var n = Tilt(v.n, gradient);
            (long, long, long, long) key = default;
            if (weld)
            {
                key = ((long)Mathf.RoundToInt(p.x * 10000f) << 32 ^ (uint)Mathf.RoundToInt(p.z * 10000f), (long)Mathf.RoundToInt(p.y * 10000f) << 32 ^ (uint)Mathf.RoundToInt(n.y * 2000f),
                       (long)Mathf.RoundToInt(n.x * 2000f) << 32 ^ (uint)Mathf.RoundToInt(n.z * 2000f), (long)Mathf.RoundToInt(v.uv.x * 20000f) << 32 ^ (uint)Mathf.RoundToInt(v.uv.y * 20000f));
                if (welded.TryGetValue(key, out int known)) return known;
            }
            int index = data.vertices.Count;
            data.vertices.Add(worldToFinal.MultiplyPoint3x4(p));
            data.normals.Add(normalToFinal.MultiplyVector(n).normalized);
            if (hasUv) data.uv.Add(v.uv);
            if (weld) welded.Add(key, index);
            return index;
        }
        for (int i = 0; i < count; i++) Emit(world[i], field.Gradient(world[i].p.x, world[i].p.z), false);

        var polygon = new List<V>(8); var column = new List<V>(8); var cellPiece = new List<V>(8); var half = new List<V>(8); var scratch = new List<V>(8);
        float cell = field.cell; var corner = new List<int>(8);
        foreach (var source in submeshes)
        {
            var target = new List<int>(source.Count); data.triangles.Add(target);
            for (int t = 0; t + 2 < source.Count; t += 3)
            {
                int i0 = source[t], i1 = source[t + 1], i2 = source[t + 2];
                Vector3 a = world[i0].p, b = world[i1].p, c = world[i2].p;
                float minX = Mathf.Min(a.x, Mathf.Min(b.x, c.x)), maxX = Mathf.Max(a.x, Mathf.Max(b.x, c.x));
                float minZ = Mathf.Min(a.z, Mathf.Min(b.z, c.z)), maxZ = Mathf.Max(a.z, Mathf.Max(b.z, c.z));
                if (maxX - minX <= CutAbove && maxZ - minZ <= CutAbove) { target.Add(i0); target.Add(i1); target.Add(i2); continue; }
                int cx0 = Mathf.FloorToInt((minX - field.x0) / cell), cx1 = Mathf.FloorToInt((maxX - field.x0) / cell);
                int cz0 = Mathf.FloorToInt((minZ - field.z0) / cell), cz1 = Mathf.FloorToInt((maxZ - field.z0) / cell);
                polygon.Clear(); polygon.Add(world[i0]); polygon.Add(world[i1]); polygon.Add(world[i2]);
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    float xa = field.x0 + cx * cell, xb = xa + cell;
                    Clip(polygon, scratch, 1f, 0f, xa, 1f); Clip(scratch, column, 1f, 0f, xb, -1f);
                    if (column.Count < 3) continue;
                    for (int cz = cz0; cz <= cz1; cz++)
                    {
                        float za = field.z0 + cz * cell, zb = za + cell;
                        Clip(column, scratch, 0f, 1f, za, 1f); Clip(scratch, cellPiece, 0f, 1f, zb, -1f);
                        if (cellPiece.Count < 3) continue;
                        // the cell's two facets meet on its diagonal: (z - za) = (x - xa), or the other one
                        bool flipped = TownReliefField.Flipped(cx, cz);
                        for (int side = 0; side < 2; side++)
                        {
                            if (flipped) Clip(cellPiece, half, 1f, 1f, xa + za + cell, side == 0 ? 1f : -1f);
                            else Clip(cellPiece, half, -1f, 1f, za - xa, side == 0 ? 1f : -1f);
                            if (half.Count < 3) continue;
                            Vector3 centre = Vector3.zero; foreach (var v in half) centre += v.p; centre /= half.Count;
                            var gradient = field.Gradient(centre.x, centre.z);
                            corner.Clear();
                            foreach (var v in half) corner.Add(Emit(v, gradient, true));
                            for (int k = 1; k + 1 < half.Count; k++)
                            {
                                Vector3 p0 = half[0].p, p1 = half[k].p, p2 = half[k + 1].p;
                                if (Vector3.Cross(p1 - p0, p2 - p0).sqrMagnitude < 1e-10f) continue;
                                if (corner[0] == corner[k] || corner[k] == corner[k + 1] || corner[0] == corner[k + 1]) continue;
                                target.Add(corner[0]); target.Add(corner[k]); target.Add(corner[k + 1]);
                            }
                        }
                    }
                }
            }
        }
        return data;
    }

    /// <summary>Highest and lowest ground under an axis-aligned plan rectangle, and the best-fit plane through it.</summary>
    public static void Sample(TownReliefField field, Bounds bounds, out float min, out float max, out float centre, out Vector2 slope, out float residual)
    {
        const int N = 5;
        float sx = 0, sz = 0, sh = 0, sxx = 0, szz = 0, sxh = 0, szh = 0; min = float.MaxValue; max = float.MinValue;
        var hs = new float[N * N];
        for (int i = 0; i < N; i++) for (int j = 0; j < N; j++)
        {
            float u = i / (N - 1f) - 0.5f, v = j / (N - 1f) - 0.5f;
            float h = field.Height(bounds.center.x + u * bounds.size.x, bounds.center.z + v * bounds.size.z);
            hs[i * N + j] = h; min = Mathf.Min(min, h); max = Mathf.Max(max, h);
            float x = u * bounds.size.x, z = v * bounds.size.z;
            sx += x; sz += z; sh += h; sxx += x * x; szz += z * z; sxh += x * h; szh += z * h;
        }
        int n = N * N; centre = sh / n;
        // the sample grid is symmetric, so the two slopes separate
        slope = new Vector2(sxx > 1e-6f ? sxh / sxx : 0f, szz > 1e-6f ? szh / szz : 0f);
        residual = 0f;
        for (int i = 0; i < N; i++) for (int j = 0; j < N; j++)
        {
            float x = (i / (N - 1f) - 0.5f) * bounds.size.x, z = (j / (N - 1f) - 0.5f) * bounds.size.z;
            residual = Mathf.Max(residual, Mathf.Abs(hs[i * N + j] - (centre + slope.x * x + slope.y * z)));
        }
    }
}
