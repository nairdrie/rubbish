// Setup_Casino, part 2: the modelling kit. A small sibling of TownLifeKit for the casino. TownLifeKit itself cannot be
// used here: it owns Assets/Materials/Generated/TownLife (a town dependency: writing there would make the baked town
// stale), its meshes carry no UVs and its materials no textures. Same rules, though: parts merge into ONE mesh per
// object with a submesh per material; every mesh is a named sub-asset of one container asset; every material and
// texture is an asset under Assets/Materials/Generated/Casino; unchanged content is refilled in place (stable ids).
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static partial class Setup_Casino
{
    public const string AssetDir = "Assets/Materials/Generated/Casino";
    const string MeshAsset = AssetDir + "/CasinoMeshes.asset";
    const string MatDir = AssetDir + "/Mat";
    const string TexDir = AssetDir + "/Tex";
    const int LayerDefault = 0, LayerGround = 6, LayerWalls = 7;

    // ------------------------------------------------------------------------------------------------ mesh builder

    /// <summary>Triangle soup with UVs and a submesh per material key, built through a transform stack.</summary>
    sealed class MB
    {
        public readonly List<Vector3> v = new List<Vector3>(), n = new List<Vector3>();
        public readonly List<Vector2> uv = new List<Vector2>();
        public readonly List<string> mats = new List<string>();
        public readonly List<List<int>> tris = new List<List<int>>();
        Matrix4x4 m = Matrix4x4.identity;
        readonly Stack<Matrix4x4> stack = new Stack<Matrix4x4>();
        /// <summary>Box / panel UVs are metres times this.</summary>
        public float uvScale = 1f;
        /// <summary>Swap U and V on boxes (wood grain along a horizontal member).</summary>
        public bool grainAlong;

        public MB Push(Vector3 pos, Vector3 euler = default, float scale = 1f)
        {
            stack.Push(m);
            m = m * Matrix4x4.TRS(pos, Quaternion.Euler(euler), Vector3.one * scale);
            return this;
        }
        public MB Push(Vector3 pos, Quaternion rot) { stack.Push(m); m = m * Matrix4x4.TRS(pos, rot, Vector3.one); return this; }
        public MB Pop() { m = stack.Pop(); return this; }

        List<int> Sub(string mat)
        {
            int i = mats.IndexOf(mat);
            if (i < 0) { mats.Add(mat); tris.Add(new List<int>()); i = mats.Count - 1; }
            return tris[i];
        }

        public void Tri(string mat, Vector3 a, Vector3 b, Vector3 c, Vector3 na, Vector3 nb, Vector3 nc, Vector2 ua, Vector2 ub, Vector2 uc)
        {
            var t = Sub(mat); int k = v.Count;
            v.Add(m.MultiplyPoint3x4(a)); v.Add(m.MultiplyPoint3x4(b)); v.Add(m.MultiplyPoint3x4(c));
            n.Add(m.MultiplyVector(na).normalized); n.Add(m.MultiplyVector(nb).normalized); n.Add(m.MultiplyVector(nc).normalized);
            uv.Add(ua); uv.Add(ub); uv.Add(uc);
            t.Add(k); t.Add(k + 1); t.Add(k + 2);
        }
        void TriR(string mat, Vector3 a, Vector3 b, Vector3 c, Vector3 na, Vector3 nb, Vector3 nc, Vector2 ua, Vector2 ub, Vector2 uc)
        {
            Tri(mat, a, c, b, na, nc, nb, ua, uc, ub);
        }
        void QuadSmoothR(string mat, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 na, Vector3 nb, Vector3 nc, Vector3 nd, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud)
        {
            TriR(mat, a, b, c, na, nb, nc, ua, ub, uc); TriR(mat, a, c, d, na, nc, nd, ua, uc, ud);
        }
        /// <summary>Flat triangle, clockwise seen from outside.</summary>
        public void Tri(string mat, Vector3 a, Vector3 b, Vector3 c, Vector2 ua, Vector2 ub, Vector2 uc)
        {
            var nn = Vector3.Cross(b - a, c - a); if (nn.sqrMagnitude < 1e-14f) return; nn.Normalize();
            Tri(mat, a, b, c, nn, nn, nn, ua, ub, uc);
        }
        /// <summary>Flat quad, corners clockwise seen from outside (bottom-left, top-left, top-right, bottom-right), UV 0..1
        /// inside <paramref name="uvRect"/>.</summary>
        public void Quad(string mat, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Rect? uvRect = null)
        {
            var r = uvRect ?? new Rect(0, 0, 1, 1);
            Vector2 u0 = new Vector2(r.xMin, r.yMin), u1 = new Vector2(r.xMin, r.yMax), u2 = new Vector2(r.xMax, r.yMax), u3 = new Vector2(r.xMax, r.yMin);
            Tri(mat, a, b, c, u0, u1, u2); Tri(mat, a, c, d, u0, u2, u3);
        }
        public void QuadSmooth(string mat, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 na, Vector3 nb, Vector3 nc, Vector3 nd, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud)
        {
            Tri(mat, a, b, c, na, nb, nc, ua, ub, uc); Tri(mat, a, c, d, na, nc, nd, ua, uc, ud);
        }

        /// <summary>A wall-mounted picture: a quad of size w x h centred at <paramref name="c"/>, facing <paramref name="normal"/>.</summary>
        public MB Panel(string mat, Vector3 c, Vector3 normal, float w, float h, Rect? uvRect = null, Vector3? upHint = null)
        {
            Vector3 up = upHint ?? (Mathf.Abs(normal.y) > 0.9f ? Vector3.forward : Vector3.up);
            Vector3 right = Vector3.Cross(up, normal).normalized; up = Vector3.Cross(normal, right).normalized;
            // seen from the front (looking along -normal) "right" must be on the viewer's right
            right = -right;
            Vector3 a = c - right * (w * .5f) - up * (h * .5f), b = c - right * (w * .5f) + up * (h * .5f),
                    cc = c + right * (w * .5f) + up * (h * .5f), d = c + right * (w * .5f) - up * (h * .5f);
            if (Vector3.Dot(Vector3.Cross(b - a, cc - a), normal) < 0f) Quad(mat, d, cc, b, a, FlipU(uvRect));
            else Quad(mat, a, b, cc, d, uvRect);
            return this;
        }
        static Rect? FlipU(Rect? r) { var q = r ?? new Rect(0, 0, 1, 1); return new Rect(q.xMax, q.yMin, -q.width, q.height); }

        [Flags] public enum Face { None = 0, XN = 1, XP = 2, YN = 4, YP = 8, ZN = 16, ZP = 32, All = 63, NoBottom = 59, Sides = 51 }

        public MB Box(string mat, Vector3 center, Vector3 size, Vector3 euler = default, Face faces = Face.All)
        {
            Push(center, euler);
            Vector3 h = size * .5f; float s = uvScale;
            Vector2 U(float a, float b) => grainAlong ? new Vector2(b * s, a * s) : new Vector2(a * s, b * s);
            void F(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float w, float t)
            {
                Tri(mat, a, b, c, U(0, 0), U(0, t), U(w, t)); Tri(mat, a, c, d, U(0, 0), U(w, t), U(w, 0));
            }
            if ((faces & Face.ZN) != 0) F(new Vector3(-h.x, -h.y, -h.z), new Vector3(-h.x, h.y, -h.z), new Vector3(h.x, h.y, -h.z), new Vector3(h.x, -h.y, -h.z), size.x, size.y);
            if ((faces & Face.ZP) != 0) F(new Vector3(h.x, -h.y, h.z), new Vector3(h.x, h.y, h.z), new Vector3(-h.x, h.y, h.z), new Vector3(-h.x, -h.y, h.z), size.x, size.y);
            if ((faces & Face.XN) != 0) F(new Vector3(-h.x, -h.y, h.z), new Vector3(-h.x, h.y, h.z), new Vector3(-h.x, h.y, -h.z), new Vector3(-h.x, -h.y, -h.z), size.z, size.y);
            if ((faces & Face.XP) != 0) F(new Vector3(h.x, -h.y, -h.z), new Vector3(h.x, h.y, -h.z), new Vector3(h.x, h.y, h.z), new Vector3(h.x, -h.y, h.z), size.z, size.y);
            if ((faces & Face.YP) != 0) F(new Vector3(-h.x, h.y, -h.z), new Vector3(-h.x, h.y, h.z), new Vector3(h.x, h.y, h.z), new Vector3(h.x, h.y, -h.z), size.x, size.z);
            if ((faces & Face.YN) != 0) F(new Vector3(-h.x, -h.y, h.z), new Vector3(-h.x, -h.y, -h.z), new Vector3(h.x, -h.y, -h.z), new Vector3(h.x, -h.y, h.z), size.x, size.z);
            return Pop();
        }

        /// <summary>A surface of revolution about +Y: <paramref name="profile"/> is (radius, y) from bottom to top.
        /// Smooth around; smooth along unless two consecutive points repeat (a hard edge). Radius 0 ends close it.</summary>
        public MB Lathe(string mat, Vector3 basePos, IList<Vector2> profile, int sides = 16, Vector3 euler = default, float arc = 360f)
        {
            Push(basePos, euler);
            int np = profile.Count;
            // per-segment outward normals in the (r, y) plane, then averaged at shared points unless marked hard
            var segN = new Vector2[np - 1];
            for (int i = 0; i < np - 1; i++)
            {
                Vector2 d = profile[i + 1] - profile[i];
                segN[i] = d.sqrMagnitude < 1e-12f ? Vector2.zero : new Vector2(d.y, -d.x).normalized;
            }
            for (int i = 0; i < np - 1; i++)
            {
                if (segN[i] == Vector2.zero) continue;
                Vector2 p0 = profile[i], p1 = profile[i + 1];
                Vector2 n0 = segN[i], n1 = segN[i];
                if (i > 0 && segN[i - 1] != Vector2.zero && Vector2.Dot(segN[i - 1], segN[i]) > 0.5f) n0 = (segN[i - 1] + segN[i]).normalized;
                if (i < np - 2 && segN[i + 1] != Vector2.zero && Vector2.Dot(segN[i + 1], segN[i]) > 0.5f) n1 = (segN[i + 1] + segN[i]).normalized;
                for (int s = 0; s < sides; s++)
                {
                    float a0 = s * arc / sides * Mathf.Deg2Rad, a1 = (s + 1) * arc / sides * Mathf.Deg2Rad;
                    Vector3 P(Vector2 p, float a) => new Vector3(Mathf.Sin(a) * p.x, p.y, Mathf.Cos(a) * p.x);
                    Vector3 N(Vector2 q, float a) => new Vector3(Mathf.Sin(a) * q.x, q.y, Mathf.Cos(a) * q.x);
                    float u0 = s / (float)sides, u1 = (s + 1) / (float)sides, v0 = i / (float)(np - 1), v1 = (i + 1) / (float)(np - 1);
                    Vector3 A = P(p0, a0), B = P(p1, a0), C = P(p1, a1), D = P(p0, a1);
                    if (p1.x > 1e-6f) TriR(mat, A, B, C, N(n0, a0), N(n1, a0), N(n1, a1), new Vector2(u0, v0), new Vector2(u0, v1), new Vector2(u1, v1));
                    if (p0.x > 1e-6f) TriR(mat, A, C, D, N(n0, a0), N(n1, a1), N(n0, a1), new Vector2(u0, v0), new Vector2(u1, v1), new Vector2(u1, v0));
                }
            }
            return Pop();
        }

        /// <summary>Cylinder / cone frustum standing on <paramref name="basePos"/> along +Y (capped).</summary>
        public MB Cyl(string mat, Vector3 basePos, float r, float h, int sides = 14, float topR = -1f, Vector3 euler = default)
        {
            if (topR < 0f) topR = r;
            var prof = new List<Vector2> { new Vector2(0, 0), new Vector2(r, 0), new Vector2(r, 0), new Vector2(topR, h), new Vector2(topR, h), new Vector2(0, h) };
            return Lathe(mat, basePos, prof, sides, euler);
        }

        public MB Sphere(string mat, Vector3 c, Vector3 radii, int lon = 14, int lat = 8, float fromLat = -90f, float toLat = 90f)
        {
            Push(c);
            for (int i = 0; i < lat; i++)
            {
                float t0 = Mathf.Lerp(fromLat, toLat, i / (float)lat) * Mathf.Deg2Rad, t1 = Mathf.Lerp(fromLat, toLat, (i + 1) / (float)lat) * Mathf.Deg2Rad;
                for (int s = 0; s < lon; s++)
                {
                    float a0 = s * Mathf.PI * 2f / lon, a1 = (s + 1) * Mathf.PI * 2f / lon;
                    Vector3 D(float t, float a) => new Vector3(Mathf.Cos(t) * Mathf.Sin(a), Mathf.Sin(t), Mathf.Cos(t) * Mathf.Cos(a));
                    Vector3 P(Vector3 d) => Vector3.Scale(d, radii);
                    Vector3 Nn(Vector3 d) => new Vector3(d.x / radii.x, d.y / radii.y, d.z / radii.z).normalized;
                    Vector3 d00 = D(t0, a0), d10 = D(t1, a0), d11 = D(t1, a1), d01 = D(t0, a1);
                    Vector2 uu = Vector2.zero;
                    if (Mathf.Cos(t1) > 1e-5f) TriR(mat, P(d00), P(d10), P(d11), Nn(d00), Nn(d10), Nn(d11), uu, uu, uu);
                    if (Mathf.Cos(t0) > 1e-5f) TriR(mat, P(d00), P(d11), P(d01), Nn(d00), Nn(d11), Nn(d01), uu, uu, uu);
                }
            }
            return Pop();
        }

        /// <summary>Torus in the XZ plane (axis +Y) unless rotated.</summary>
        public MB Torus(string mat, Vector3 c, float ring, float tube, int segments = 24, int sides = 8, Vector3 euler = default, float arc = 360f)
        {
            Push(c, euler);
            for (int i = 0; i < segments; i++)
            {
                float a0 = i * arc / segments * Mathf.Deg2Rad, a1 = (i + 1) * arc / segments * Mathf.Deg2Rad;
                for (int s = 0; s < sides; s++)
                {
                    float b0 = s * Mathf.PI * 2f / sides, b1 = (s + 1) * Mathf.PI * 2f / sides;
                    Vector3 Nn(float a, float b) => new Vector3(Mathf.Sin(a) * Mathf.Cos(b), Mathf.Sin(b), Mathf.Cos(a) * Mathf.Cos(b));
                    Vector3 P(float a, float b) => new Vector3(Mathf.Sin(a) * ring, 0, Mathf.Cos(a) * ring) + Nn(a, b) * tube;
                    QuadSmoothR(mat, P(a0, b0), P(a0, b1), P(a1, b1), P(a1, b0), Nn(a0, b0), Nn(a0, b1), Nn(a1, b1), Nn(a1, b0),
                        Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
                }
            }
            return Pop();
        }

        /// <summary>A round tube swept along a polyline (neon, rails, ropes), with flat end caps.</summary>
        public MB Tube(string mat, IList<Vector3> path, float radius, int sides = 6, bool closed = false)
        {
            int np = path.Count; if (np < 2) return this;
            var tan = new Vector3[np];
            for (int i = 0; i < np; i++)
            {
                Vector3 prev = path[closed ? (i - 1 + np) % np : Mathf.Max(i - 1, 0)], next = path[closed ? (i + 1) % np : Mathf.Min(i + 1, np - 1)];
                tan[i] = (next - prev).normalized;
                if (tan[i].sqrMagnitude < 1e-8f) tan[i] = Vector3.forward;
            }
            var right = new Vector3[np]; var up = new Vector3[np];
            Vector3 r0 = Vector3.Cross(Mathf.Abs(tan[0].y) > 0.95f ? Vector3.forward : Vector3.up, tan[0]).normalized;
            right[0] = r0; up[0] = Vector3.Cross(tan[0], r0).normalized;
            for (int i = 1; i < np; i++)
            {
                var q = Quaternion.FromToRotation(tan[i - 1], tan[i]);
                right[i] = (q * right[i - 1]).normalized; up[i] = (q * up[i - 1]).normalized;
            }
            int segs = closed ? np : np - 1;
            for (int i = 0; i < segs; i++)
            {
                int j = (i + 1) % np;
                for (int s = 0; s < sides; s++)
                {
                    float b0 = s * Mathf.PI * 2f / sides, b1 = (s + 1) * Mathf.PI * 2f / sides;
                    Vector3 Nn(int k, float b) => right[k] * Mathf.Cos(b) + up[k] * Mathf.Sin(b);
                    Vector3 n00 = Nn(i, b0), n01 = Nn(i, b1), n10 = Nn(j, b0), n11 = Nn(j, b1);
                    QuadSmoothR(mat, path[i] + n00 * radius, path[j] + n10 * radius, path[j] + n11 * radius, path[i] + n01 * radius,
                        n00, n10, n11, n01, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
                }
            }
            if (!closed)
                for (int e = 0; e < 2; e++)
                {
                    int k = e == 0 ? 0 : np - 1; Vector3 nn = e == 0 ? -tan[0] : tan[np - 1];
                    for (int s = 0; s < sides; s++)
                    {
                        float b0 = s * Mathf.PI * 2f / sides, b1 = (s + 1) * Mathf.PI * 2f / sides;
                        Vector3 p0 = path[k] + (right[k] * Mathf.Cos(b0) + up[k] * Mathf.Sin(b0)) * radius, p1 = path[k] + (right[k] * Mathf.Cos(b1) + up[k] * Mathf.Sin(b1)) * radius;
                        if (e == 0) Tri(mat, path[k], p1, p0, nn, nn, nn, Vector2.zero, Vector2.zero, Vector2.zero);
                        else Tri(mat, path[k], p0, p1, nn, nn, nn, Vector2.zero, Vector2.zero, Vector2.zero);
                    }
                }
            return this;
        }

        /// <summary>An extruded outline in the XZ plane (any winding, may be concave): top at y1 (UV mapped over
        /// <paramref name="uvBounds"/> or metres), sides down to y0, optional bottom.</summary>
        public MB Prism(string topMat, string sideMat, IList<Vector2> outline, float y0, float y1, Rect? uvBounds = null, bool bottom = false)
        {
            var pts = new List<Vector2>(outline);
            if (Area(pts) < 0f) pts.Reverse(); // counter-clockwise seen from above (x right, z up)
            Vector2 UVof(Vector2 p) => uvBounds.HasValue
                ? new Vector2((p.x - uvBounds.Value.xMin) / uvBounds.Value.width, (p.y - uvBounds.Value.yMin) / uvBounds.Value.height)
                : p * uvScale;
            foreach (var t in Triangulate(pts))
            {
                Vector3 a = new Vector3(pts[t[0]].x, y1, pts[t[0]].y), b = new Vector3(pts[t[1]].x, y1, pts[t[1]].y), c = new Vector3(pts[t[2]].x, y1, pts[t[2]].y);
                // counter-clockwise in XZ seen from above = clockwise in Unity's view from +Y? fix by the normal
                if (Vector3.Cross(b - a, c - a).y < 0f) Tri(topMat, a, c, b, UVof(pts[t[0]]), UVof(pts[t[2]]), UVof(pts[t[1]]));
                else Tri(topMat, a, b, c, UVof(pts[t[0]]), UVof(pts[t[1]]), UVof(pts[t[2]]));
                if (bottom)
                {
                    Vector3 a0 = new Vector3(a.x, y0, a.z), b0 = new Vector3(b.x, y0, b.z), c0 = new Vector3(c.x, y0, c.z);
                    if (Vector3.Cross(b0 - a0, c0 - a0).y > 0f) Tri(sideMat, a0, c0, b0, Vector2.zero, Vector2.zero, Vector2.zero);
                    else Tri(sideMat, a0, b0, c0, Vector2.zero, Vector2.zero, Vector2.zero);
                }
            }
            float run = 0f;
            for (int i = 0; i < pts.Count; i++)
            {
                Vector2 p = pts[i], q = pts[(i + 1) % pts.Count];
                float len = (q - p).magnitude;
                Vector3 a = new Vector3(p.x, y0, p.y), b = new Vector3(p.x, y1, p.y), c = new Vector3(q.x, y1, q.y), d = new Vector3(q.x, y0, q.y);
                Vector3 outward = new Vector3((q - p).y, 0f, -(q - p).x);
                var r = new Rect(run * uvScale, 0f, len * uvScale, (y1 - y0) * uvScale);
                if (Vector3.Dot(Vector3.Cross(b - a, c - a), outward) < 0f) Quad(sideMat, d, c, b, a, r); else Quad(sideMat, a, b, c, d, r);
                run += len;
            }
            return this;
        }

        public MB Disc(string mat, Vector3 c, float radius, int sides = 24, bool up = true)
        {
            for (int s = 0; s < sides; s++)
            {
                float a0 = s * Mathf.PI * 2f / sides, a1 = (s + 1) * Mathf.PI * 2f / sides;
                Vector3 d0 = new Vector3(Mathf.Sin(a0), 0, Mathf.Cos(a0)), d1 = new Vector3(Mathf.Sin(a1), 0, Mathf.Cos(a1));
                Vector2 U(Vector3 d) => new Vector2(0.5f + d.x * 0.5f, 0.5f + d.z * 0.5f);
                Vector3 nn = up ? Vector3.up : Vector3.down;
                if (up) Tri(mat, c, c + d0 * radius, c + d1 * radius, nn, nn, nn, new Vector2(.5f, .5f), U(d0), U(d1));
                else Tri(mat, c, c + d1 * radius, c + d0 * radius, nn, nn, nn, new Vector2(.5f, .5f), U(d1), U(d0));
            }
            return this;
        }

        public Bounds Bounds()
        {
            if (v.Count == 0) return new Bounds();
            var b = new Bounds(v[0], Vector3.zero); foreach (var p in v) b.Encapsulate(p); return b;
        }
    }

    static float Area(IList<Vector2> p)
    {
        float a = 0f; for (int i = 0; i < p.Count; i++) { var u = p[i]; var w = p[(i + 1) % p.Count]; a += u.x * w.y - w.x * u.y; }
        return a * 0.5f;
    }

    /// <summary>Ear clipping of a counter-clockwise simple polygon.</summary>
    static List<int[]> Triangulate(IList<Vector2> p)
    {
        var res = new List<int[]>(); var idx = new List<int>(); for (int i = 0; i < p.Count; i++) idx.Add(i);
        float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
        int guard = p.Count * p.Count + 16;
        while (idx.Count > 3 && guard-- > 0)
        {
            bool clipped = false;
            for (int i = 0; i < idx.Count; i++)
            {
                int i0 = idx[(i - 1 + idx.Count) % idx.Count], i1 = idx[i], i2 = idx[(i + 1) % idx.Count];
                if (Cross(p[i0], p[i1], p[i2]) <= 1e-9f) continue;
                bool ear = true;
                foreach (int k in idx)
                {
                    if (k == i0 || k == i1 || k == i2) continue;
                    if (Cross(p[i0], p[i1], p[k]) >= 0 && Cross(p[i1], p[i2], p[k]) >= 0 && Cross(p[i2], p[i0], p[k]) >= 0) { ear = false; break; }
                }
                if (!ear) continue;
                res.Add(new[] { i0, i1, i2 }); idx.RemoveAt(i); clipped = true; break;
            }
            if (!clipped) idx.RemoveAt(0);
        }
        if (idx.Count == 3) res.Add(new[] { idx[0], idx[1], idx[2] });
        return res;
    }

    // ------------------------------------------------------------------------------------------------ mesh assets

    static Mesh containerMain;
    static readonly Dictionary<string, Mesh> meshByName = new Dictionary<string, Mesh>();
    static readonly HashSet<string> meshUsed = new HashSet<string>();
    static int meshCount, triCount;

    static void BeginAssets()
    {
        EnsureFolder(AssetDir); EnsureFolder(MatDir); EnsureFolder(TexDir);
        meshByName.Clear(); meshUsed.Clear(); mats.Clear(); matDefs.Clear(); texCache.Clear(); meshCount = triCount = 0;
        containerMain = null;
        if (File.Exists(MeshAsset))
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(MeshAsset))
            {
                var me = o as Mesh; if (me == null) continue;
                if (AssetDatabase.IsMainAsset(me)) containerMain = me;
                else if (!meshByName.ContainsKey(me.name)) meshByName.Add(me.name, me);
            }
        if (containerMain == null)
        {
            containerMain = new Mesh { name = "CasinoMeshes" };
            AssetDatabase.CreateAsset(containerMain, MeshAsset);
        }
    }

    static void EndAssets()
    {
        // drop meshes of an earlier build that nothing made this time
        foreach (var o in AssetDatabase.LoadAllAssetsAtPath(MeshAsset))
        {
            var me = o as Mesh; if (me == null || me == containerMain) continue;
            if (meshUsed.Contains(me.name) && meshByName.TryGetValue(me.name, out var keep) && keep == me) continue;
            AssetDatabase.RemoveObjectFromAsset(me); UnityEngine.Object.DestroyImmediate(me, true);
        }
        EditorUtility.SetDirty(containerMain);
        AssetDatabase.SaveAssets();
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }

    static string Sanitize(string s)
    {
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length; i++) if (!(char.IsLetterOrDigit(chars[i]) || chars[i] == '-' || chars[i] == '_')) chars[i] = '_';
        return new string(chars);
    }

    static Mesh StoreMesh(string name, MB d)
    {
        string baseName = "CS_" + Sanitize(name), unique = baseName; int k = 1;
        while (!meshUsed.Add(unique)) unique = baseName + "_" + (++k);
        meshByName.TryGetValue(unique, out var mesh);
        if (mesh == null)
        {
            mesh = new Mesh { name = unique };
            AssetDatabase.AddObjectToAsset(mesh, containerMain);
            meshByName[unique] = mesh;
        }
        mesh.Clear();
        mesh.indexFormat = d.v.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.SetVertices(d.v); mesh.SetNormals(d.n); mesh.SetUVs(0, d.uv);
        mesh.subMeshCount = Mathf.Max(1, d.tris.Count);
        for (int i = 0; i < d.tris.Count; i++) { mesh.SetTriangles(d.tris[i], i, false); triCount += d.tris[i].Count / 3; }
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        EditorUtility.SetDirty(mesh);
        meshCount++;
        return mesh;
    }

    /// <summary>One GameObject for a finished soup: mesh sub-asset, a material per submesh, static flags, shadows.</summary>
    static GameObject Emit(Transform parent, string name, MB d, Vector3 localPos = default, float yaw = 0f, bool isStatic = true, bool shadows = false, int layer = LayerDefault)
    {
        var go = new GameObject(name) { layer = layer };
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos; go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
        if (d.v.Count > 0)
        {
            go.AddComponent<MeshFilter>().sharedMesh = StoreMesh(name, d);
            var mr = go.AddComponent<MeshRenderer>();
            var arr = new Material[d.mats.Count]; for (int i = 0; i < arr.Length; i++) arr[i] = Mat(d.mats[i]);
            mr.sharedMaterials = arr;
            mr.shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            mr.receiveShadows = true;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }
        if (isStatic) GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);
        return go;
    }

    static Transform Node(Transform parent, string name, Vector3 localPos = default, Vector3 euler = default)
    {
        var go = new GameObject(name); go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos; go.transform.localRotation = Quaternion.Euler(euler);
        return go.transform;
    }

    static BoxCollider ColBox(Transform parent, string name, Vector3 localCenter, Vector3 size, int layer = LayerDefault, float yaw = 0f)
    {
        var go = new GameObject(name) { layer = layer }; go.transform.SetParent(parent, false);
        go.transform.localPosition = localCenter; go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
        var c = go.AddComponent<BoxCollider>(); c.size = size;
        GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);
        return c;
    }

    static CapsuleCollider ColCapsule(Transform parent, string name, Vector3 localCenter, float radius, float height)
    {
        var go = new GameObject(name) { layer = LayerDefault }; go.transform.SetParent(parent, false);
        go.transform.localPosition = localCenter;
        var c = go.AddComponent<CapsuleCollider>(); c.radius = radius; c.height = height; c.direction = 1;
        return c;
    }

    // ------------------------------------------------------------------------------------------------ materials

    enum Blend { Opaque, Alpha, Additive }
    sealed class MatDef
    {
        public Color color = Color.white, emission = Color.black; public float smooth = 0.2f, metal; public string tex, emissionTex;
        public Vector2 tiling = Vector2.one; public Blend blend; public bool unlit, twoSided, particle;
        /// <summary>No GPU instancing: a material whose renderers are tinted at run time with property blocks (CasinoButton, the timer lamp, the result board). Instanced copies of one shared mesh mixed their blocks up (diagonal stripes).</summary>
        public bool noInstancing;
    }
    static readonly Dictionary<string, MatDef> matDefs = new Dictionary<string, MatDef>();
    static readonly Dictionary<string, Material> mats = new Dictionary<string, Material>();

    static MatDef Def(string key, Color color, float smooth = 0.2f, float metal = 0f)
    {
        var d = new MatDef { color = color, smooth = smooth, metal = metal };
        matDefs[key] = d; return d;
    }
    static MatDef Glow(string key, Color color, Color emission, float smooth = 0.4f)
    {
        var d = Def(key, color, smooth); d.emission = emission; return d;
    }
    static Color C(float r, float g, float b, float a = 1f) => new Color(r, g, b, a);

    static Material Mat(string key)
    {
        if (mats.TryGetValue(key, out var cached) && cached) return cached;
        if (!matDefs.TryGetValue(key, out var d)) throw new KeyNotFoundException("Casino material '" + key + "' was never defined.");
        var shader = Shader.Find(d.particle ? "Universal Render Pipeline/Particles/Unlit" : d.unlit ? "Universal Render Pipeline/Unlit" : "Universal Render Pipeline/Lit");
        if (shader == null) throw new InvalidOperationException("URP shader not found.");
        string path = MatDir + "/CS_" + Sanitize(key) + ".mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null) { m = new Material(shader) { name = "CS_" + Sanitize(key) }; AssetDatabase.CreateAsset(m, path); }
        else if (m.shader != shader) m.shader = shader;
        m.SetColor("_BaseColor", d.color); m.color = d.color;
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", d.smooth);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", d.metal);
        var tex = d.tex != null ? LoadTex(d.tex) : null;
        m.SetTexture("_BaseMap", tex); m.mainTexture = tex;
        m.SetTextureScale("_BaseMap", d.tiling); m.mainTextureScale = d.tiling;
        if (!d.unlit)
        {
            bool emissive = d.emission.maxColorComponent > 0.0001f;
            if (emissive)
            {
                m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", d.emission);
                m.SetTexture("_EmissionMap", d.emissionTex != null ? LoadTex(d.emissionTex) : null);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;
            }
            else
            {
                m.DisableKeyword("_EMISSION"); m.SetColor("_EmissionColor", Color.black); m.SetTexture("_EmissionMap", null);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            }
        }
        if (d.blend != Blend.Opaque)
        {
            m.SetFloat("_Surface", 1f); m.SetFloat("_Blend", d.blend == Blend.Additive ? 2f : 0f);
            m.SetOverrideTag("RenderType", "Transparent");
            m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)(d.blend == Blend.Additive ? BlendMode.One : BlendMode.OneMinusSrcAlpha));
            m.SetInt("_SrcBlendAlpha", (int)BlendMode.One);
            m.SetInt("_DstBlendAlpha", (int)(d.blend == Blend.Additive ? BlendMode.One : BlendMode.OneMinusSrcAlpha));
            m.SetInt("_ZWrite", 0); m.SetFloat("_ZWrite", 0f);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.SetShaderPassEnabled("ShadowCaster", false); m.SetShaderPassEnabled("DepthOnly", false);
            m.renderQueue = (int)RenderQueue.Transparent;
        }
        else
        {
            m.SetFloat("_Surface", 0f); m.SetOverrideTag("RenderType", "Opaque");
            m.SetInt("_SrcBlend", (int)BlendMode.One); m.SetInt("_DstBlend", (int)BlendMode.Zero); m.SetInt("_ZWrite", 1);
            m.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.SetShaderPassEnabled("ShadowCaster", true); m.SetShaderPassEnabled("DepthOnly", true);
            m.renderQueue = -1;
        }
        m.SetFloat("_Cull", d.twoSided ? 0f : 2f);
        m.enableInstancing = !d.noInstancing;
        EditorUtility.SetDirty(m);
        mats[key] = m; return m;
    }

    // ------------------------------------------------------------------------------------------------- textures

    static readonly Dictionary<string, Texture2D> texCache = new Dictionary<string, Texture2D>();

    static Texture2D LoadTex(string name)
    {
        if (texCache.TryGetValue(name, out var t) && t) return t;
        t = AssetDatabase.LoadAssetAtPath<Texture2D>(TexDir + "/" + name + ".png");
        if (t == null) throw new FileNotFoundException("Casino texture '" + name + "' was not generated.");
        texCache[name] = t; return t;
    }

    /// <summary>Writes a generated picture as a PNG asset (only when its bytes changed) and sets its import settings.</summary>
    static void SaveTex(string name, Canvas c, bool repeat, bool alpha = false, int maxSize = 2048)
    {
        string path = TexDir + "/" + name + ".png";
        var tex = new Texture2D(c.w, c.h, TextureFormat.RGBA32, false);
        tex.SetPixels(c.px); tex.Apply(false);
        byte[] png = tex.EncodeToPNG();
        UnityEngine.Object.DestroyImmediate(tex);
        bool same = false;
        if (File.Exists(path))
        {
            var old = File.ReadAllBytes(path);
            same = old.Length == png.Length;
            if (same) for (int i = 0; i < old.Length; i++) if (old[i] != png[i]) { same = false; break; }
        }
        if (!same) { File.WriteAllBytes(path, png); AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate); }
        var imp = AssetImporter.GetAtPath(path) as TextureImporter;
        if (imp == null) return;
        var wrap = repeat ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
        if (imp.wrapMode != wrap || imp.alphaIsTransparency != alpha || imp.maxTextureSize != maxSize || imp.anisoLevel != 8 || !imp.mipmapEnabled || !imp.sRGBTexture
            || imp.textureCompression != TextureImporterCompression.CompressedHQ)
        {
            imp.textureType = TextureImporterType.Default; imp.sRGBTexture = true;
            imp.wrapMode = wrap; imp.alphaIsTransparency = alpha; imp.maxTextureSize = maxSize; imp.anisoLevel = 8; imp.mipmapEnabled = true;
            imp.textureCompression = TextureImporterCompression.CompressedHQ;
            imp.SaveAndReimport();
        }
    }

    /// <summary>A tiny software painter: anti-aliased discs, boxes, lines and polygons into an RGBA float buffer.</summary>
    sealed class Canvas
    {
        public readonly int w, h; public readonly Color[] px;
        public bool wrap;
        public Canvas(int w, int h, Color fill) { this.w = w; this.h = h; px = new Color[w * h]; for (int i = 0; i < px.Length; i++) px[i] = fill; }

        public void Blend(int x, int y, Color c, float a)
        {
            if (wrap) { x = ((x % w) + w) % w; y = ((y % h) + h) % h; }
            else if (x < 0 || y < 0 || x >= w || y >= h) return;
            a *= c.a; if (a <= 0f) return;
            int i = y * w + x; var o = px[i];
            float oa = o.a + a * (1f - o.a);
            px[i] = new Color(Mathf.Lerp(o.r, c.r, a), Mathf.Lerp(o.g, c.g, a), Mathf.Lerp(o.b, c.b, a), oa);
        }
        /// <summary>Signed-distance fill: <paramref name="sd"/> returns pixels outside (positive) / inside (negative).</summary>
        public void Sdf(float x0, float y0, float x1, float y1, Color c, Func<float, float, float> sd, float soft = 1f)
        {
            int ix0 = Mathf.FloorToInt(x0 - soft - 1), ix1 = Mathf.CeilToInt(x1 + soft + 1), iy0 = Mathf.FloorToInt(y0 - soft - 1), iy1 = Mathf.CeilToInt(y1 + soft + 1);
            for (int y = iy0; y <= iy1; y++)
                for (int x = ix0; x <= ix1; x++)
                {
                    float d = sd(x + .5f, y + .5f);
                    float a = Mathf.Clamp01(0.5f - d / soft);
                    if (a > 0f) Blend(x, y, c, a);
                }
        }
        public void Rect(float x0, float y0, float x1, float y1, Color c)
        {
            Sdf(x0, y0, x1, y1, c, (x, y) => Mathf.Max(Mathf.Max(x0 - x, x - x1), Mathf.Max(y0 - y, y - y1)));
        }
        public void Frame(float x0, float y0, float x1, float y1, float t, Color c)
        {
            Rect(x0, y0, x1, y0 + t, c); Rect(x0, y1 - t, x1, y1, c); Rect(x0, y0 + t, x0 + t, y1 - t, c); Rect(x1 - t, y0 + t, x1, y1 - t, c);
        }
        public void Disc(float cx, float cy, float r, Color c, float soft = 1f)
        {
            Sdf(cx - r, cy - r, cx + r, cy + r, c, (x, y) => Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - r, soft);
        }
        public void Ellipse(float cx, float cy, float rx, float ry, Color c, float soft = 1f)
        {
            float rm = Mathf.Min(rx, ry);
            Sdf(cx - rx, cy - ry, cx + rx, cy + ry, c, (x, y) => (Mathf.Sqrt((x - cx) * (x - cx) / (rx * rx) + (y - cy) * (y - cy) / (ry * ry)) - 1f) * rm, soft);
        }
        public void Ring(float cx, float cy, float r, float t, Color c)
        {
            Sdf(cx - r - t, cy - r - t, cx + r + t, cy + r + t, c, (x, y) => Mathf.Abs(Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - r) - t * .5f);
        }
        public void Line(float ax, float ay, float bx, float by, float t, Color c)
        {
            float dx = bx - ax, dy = by - ay, l2 = Mathf.Max(1e-6f, dx * dx + dy * dy);
            Sdf(Mathf.Min(ax, bx) - t, Mathf.Min(ay, by) - t, Mathf.Max(ax, bx) + t, Mathf.Max(ay, by) + t, c, (x, y) =>
            {
                float u = Mathf.Clamp01(((x - ax) * dx + (y - ay) * dy) / l2);
                float qx = ax + dx * u - x, qy = ay + dy * u - y;
                return Mathf.Sqrt(qx * qx + qy * qy) - t * .5f;
            });
        }
        public void Poly(Color c, params float[] xy)
        {
            int np = xy.Length / 2; float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (int i = 0; i < np; i++) { x0 = Mathf.Min(x0, xy[i * 2]); x1 = Mathf.Max(x1, xy[i * 2]); y0 = Mathf.Min(y0, xy[i * 2 + 1]); y1 = Mathf.Max(y1, xy[i * 2 + 1]); }
            bool Inside(float x, float y)
            {
                bool inside = false;
                for (int i = 0, j = np - 1; i < np; j = i++)
                {
                    float xi = xy[i * 2], yi = xy[i * 2 + 1], xj = xy[j * 2], yj = xy[j * 2 + 1];
                    if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
                }
                return inside;
            }
            for (int y = Mathf.FloorToInt(y0); y <= Mathf.CeilToInt(y1); y++)
                for (int x = Mathf.FloorToInt(x0); x <= Mathf.CeilToInt(x1); x++)
                {
                    int hits = 0;
                    if (Inside(x + .25f, y + .25f)) hits++; if (Inside(x + .75f, y + .25f)) hits++;
                    if (Inside(x + .25f, y + .75f)) hits++; if (Inside(x + .75f, y + .75f)) hits++;
                    if (hits > 0) Blend(x, y, c, hits * .25f);
                }
        }
        /// <summary>Multiply every pixel by a function of (x, y) (grain, wear, vignettes).</summary>
        public void Each(Func<int, int, Color, Color> f)
        {
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) px[y * w + x] = f(x, y, px[y * w + x]);
        }
    }

    /// <summary>Tileable value noise 0..1 (period in cells).</summary>
    static float Noise(float x, float y, int period, int seed) => Noise(x, y, period, period, seed);
    static float Noise(float x, float y, int periodX, int periodY, int seed)
    {
        int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y); float fx = x - x0, fy = y - y0;
        fx = fx * fx * (3f - 2f * fx); fy = fy * fy * (3f - 2f * fy);
        float H(int ix, int iy)
        {
            ix = ((ix % periodX) + periodX) % periodX; iy = ((iy % periodY) + periodY) % periodY;
            uint hsh = (uint)(ix * 374761393 + iy * 668265263 + seed * 362437); hsh = (hsh ^ (hsh >> 13)) * 1274126177u; hsh ^= hsh >> 16;
            return (hsh & 0xFFFFFF) / 16777215f;
        }
        return Mathf.Lerp(Mathf.Lerp(H(x0, y0), H(x0 + 1, y0), fx), Mathf.Lerp(H(x0, y0 + 1), H(x0 + 1, y0 + 1), fx), fy);
    }
    static float Hash01(int a, int b)
    {
        uint hsh = (uint)(a * 374761393 + b * 668265263); hsh = (hsh ^ (hsh >> 13)) * 1274126177u; hsh ^= hsh >> 16;
        return (hsh & 0xFFFFFF) / 16777215f;
    }
}
