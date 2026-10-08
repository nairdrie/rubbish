// TownLife kit, part 2: prop recipes (lists of primitive parts merged into ONE mesh with a submesh per material),
// the primitive generators, and the stock palette / stock prop library shared by all modules.
// Owner: kit agent. The architect implemented the generators and a tiny stock set; the kit agent grows the library.
using System;
using System.Collections.Generic;
using UnityEngine;

public static partial class TownLifeKit
{
    /// <summary>Collider attached to every instance of a prop (prop-local metres).</summary>
    public struct ColliderSpec { public bool capsule; public Vector3 center, size; }   // capsule: size.x = radius, size.y = height

    /// <summary>A prop recipe. Local frame: origin = ground contact point, +Y up, +Z = the prop's front, metres.
    /// Every method returns the recipe so calls chain. A recipe is frozen at its first Spawn.</summary>
    public sealed class Recipe
    {
        public readonly string id;
        internal readonly List<Action<MeshData>> parts = new List<Action<MeshData>>();
        internal readonly List<ColliderSpec> colliders = new List<ColliderSpec>();
        internal bool castShadows = true, occluder, frozen, fitBox; internal float fitShrink; internal int layer = LayerDefault;
        internal Recipe(string id) { this.id = id; }

        internal Recipe Part2(Action<MeshData> a) { return Part(a); }
        Recipe Part(Action<MeshData> a) { if (frozen) throw new InvalidOperationException("Prop '" + id + "' was already spawned; define it completely first."); parts.Add(a); return this; }

        /// <summary>Box centred at <paramref name="center"/>, rotated by euler degrees about its centre.</summary>
        public Recipe Box(string mat, Vector3 center, Vector3 size, Vector3 euler = default) { return Part(d => GenBox(d, mat, Matrix4x4.TRS(center, Quaternion.Euler(euler), size))); }
        /// <summary>Cylinder / frustum / n-gon prism standing on <paramref name="baseCenter"/> (axis +Y before euler).
        /// topRadius &lt; 0 = same as radius; 0 = cone. sides 4 = square post, 6 = hexagon, 8 = octagon.</summary>
        public Recipe Cyl(string mat, Vector3 baseCenter, float radius, float height, int sides = 10, float topRadius = -1f, Vector3 euler = default, bool smooth = false)
        { return Part(d => GenCyl(d, mat, Matrix4x4.TRS(baseCenter, Quaternion.Euler(euler), Vector3.one), radius, topRadius < 0f ? radius : topRadius, height, sides, smooth)); }
        /// <summary>Cone standing on its base.</summary>
        public Recipe Cone(string mat, Vector3 baseCenter, float radius, float height, int sides = 10, Vector3 euler = default) { return Cyl(mat, baseCenter, radius, height, sides, 0f, euler); }
        /// <summary>Ellipsoid centred at <paramref name="center"/> with the given radii.</summary>
        public Recipe Sphere(string mat, Vector3 center, Vector3 radii, int lon = 10, int lat = 6, bool smooth = false, Vector3 euler = default)
        { return Part(d => GenBlob(d, mat, Matrix4x4.TRS(center, Quaternion.Euler(euler), radii), lon, lat, 0, 0f, smooth, false)); }
        /// <summary>Upper half of an ellipsoid sitting on <paramref name="baseCenter"/> (closed underneath).</summary>
        public Recipe Dome(string mat, Vector3 baseCenter, Vector3 radii, int lon = 10, int lat = 4, bool smooth = false)
        { return Part(d => GenBlob(d, mat, Matrix4x4.TRS(baseCenter, Quaternion.identity, radii), lon, lat, 0, 0f, smooth, true)); }
        /// <summary>Faceted lumpy ellipsoid (tree crowns, bushes, boulders, clouds). Deterministic for a given seed.</summary>
        public Recipe Blob(string mat, Vector3 center, Vector3 radii, int seed, float jitter = 0.18f, int lon = 8, int lat = 5, Vector3 euler = default)
        { return Part(d => GenBlob(d, mat, Matrix4x4.TRS(center, Quaternion.Euler(euler), radii), lon, lat, seed, jitter, false, false)); }
        /// <summary>Gable (triangular) prism standing on <paramref name="baseCenter"/>: size.x = span, size.y = rise,
        /// size.z = ridge length (ridge along local Z). Closed on all sides.</summary>
        public Recipe Gable(string mat, Vector3 baseCenter, Vector3 size, Vector3 euler = default) { return Part(d => GenGable(d, mat, Matrix4x4.TRS(baseCenter, Quaternion.Euler(euler), size), 0f)); }
        /// <summary>Wedge / ramp standing on <paramref name="baseCenter"/>: full height at local -X, zero at +X.</summary>
        public Recipe Wedge(string mat, Vector3 baseCenter, Vector3 size, Vector3 euler = default) { return Part(d => GenGable(d, mat, Matrix4x4.TRS(baseCenter, Quaternion.Euler(euler), size), -0.5f)); }
        /// <summary>Torus (tyre, life ring, basin rim) in the local XZ plane before euler.</summary>
        public Recipe Torus(string mat, Vector3 center, float ringRadius, float tubeRadius, int segments = 14, int sides = 6, Vector3 euler = default)
        { return Part(d => GenTorus(d, mat, Matrix4x4.TRS(center, Quaternion.Euler(euler), Vector3.one), ringRadius, tubeRadius, segments, sides)); }
        /// <summary>Square-section beam between two points (rails, ropes, chains, washing lines, branches).</summary>
        public Recipe Bar(string mat, Vector3 a, Vector3 b, float thickness)
        {
            return Part(d =>
            {
                var dir = b - a; float len = dir.magnitude; if (len < 1e-5f) return;
                var up = Mathf.Abs(Vector3.Dot(dir / len, Vector3.up)) > 0.99f ? Vector3.forward : Vector3.up;
                GenBox(d, mat, Matrix4x4.TRS((a + b) * 0.5f, Quaternion.LookRotation(dir / len, up), new Vector3(thickness, thickness, len)));
            });
        }
        /// <summary>Flat panel visible from BOTH sides (flags, bunting, laundry, leaves, signs). Corners in order.</summary>
        public Recipe Sheet(string mat, Vector3 a, Vector3 b, Vector3 c, Vector3 d4) { return Part(d => { d.Quad(mat, a, b, c, d4); d.Quad(mat, d4, c, b, a); }); }
        /// <summary>Bakes another already-defined prop into this one (offset, yaw degrees, scale). Colliders are not copied.</summary>
        public Recipe Add(string otherPropId, Vector3 offset, float yawDeg = 0f, float scale = 1f)
        { return Part(d => d.Append(BuiltOf(otherPropId).data, Matrix4x4.TRS(offset, Quaternion.Euler(0f, yawDeg, 0f), Vector3.one * scale))); }
        /// <summary>Free-form geometry: the callback appends triangles to the prop's MeshData (prop-local).</summary>
        public Recipe Geometry(Action<MeshData> build) { return Part(build); }

        /// <summary>Tapered round limb between two points (trunks, branches, legs, hanging fronds). caps: 0 none,
        /// 1 at <paramref name="a"/>, 2 at <paramref name="b"/>, 3 both.</summary>
        public Recipe Limb(string mat, Vector3 a, Vector3 b, float radiusA, float radiusB, int sides = 6, int caps = 3)
        {
            return Part(d =>
            {
                var dir = b - a; float len = dir.magnitude; if (len < 1e-5f) return;
                GenCyl(d, mat, Matrix4x4.TRS(a, Quaternion.FromToRotation(Vector3.up, dir / len), Vector3.one), radiusA, radiusB, len, sides, false, caps);
            });
        }
        /// <summary>Solid of revolution about +Y through <paramref name="baseCenter"/>. profile = (radius, height) pairs
        /// from bottom to top; a radius of 0 closes to a point, otherwise the end is capped. jitter &gt; 0 makes it lumpy
        /// (deterministic for a seed) and staggers alternate rings for a faceted look.</summary>
        public Recipe Lathe(string mat, Vector3 baseCenter, IList<Vector2> profile, int sides = 8, int seed = 0, float jitter = 0f)
        { var copy = new List<Vector2>(profile); return Part(d => GenLathe(d, mat, baseCenter, copy, sides, seed, jitter)); }
        /// <summary>Three-sided spike from a base point to a tip (grass blades, reeds, thorns, stems). No base cap.</summary>
        public Recipe Spike(string mat, Vector3 basePoint, Vector3 tip, float radius) { return Part(d => GenSpike(d, mat, basePoint, tip, radius)); }
        /// <summary>Constant cross-section (x, y) swept along +Z from z0 to z1 (kerbs, walls, beams). The profile is a
        /// closed outline; both ends are capped (convex or star-shaped about its centroid).</summary>
        public Recipe Prism(string mat, IList<Vector2> profile, float z0, float z1)
        { var copy = new List<Vector2>(profile).ToArray(); return Part(d => GenPrism(d, mat, copy, z0, copy, z1, true, true)); }
        /// <summary>Adds a capsule collider anywhere in the prop (axis +Y; second trunks of clumps, lamp posts).</summary>
        public Recipe ColliderCapsule(Vector3 center, float radius, float height) { colliders.Add(new ColliderSpec { capsule = true, center = center, size = new Vector3(radius, height, 0f) }); return this; }
        /// <summary>Adds a BoxCollider (prop-local centre and size).</summary>
        public Recipe ColliderBox(Vector3 center, Vector3 size) { colliders.Add(new ColliderSpec { center = center, size = size }); return this; }
        /// <summary>Adds one BoxCollider fitted to the whole mesh, shrunk by <paramref name="shrink"/> metres on x and z.</summary>
        public Recipe ColliderFit(float shrink = 0f) { fitBox = true; fitShrink = shrink; return this; }
        /// <summary>Adds the one-capsule trunk collider trees get (axis +Y, standing on the origin).</summary>
        public Recipe ColliderTrunk(float radius, float height) { colliders.Add(new ColliderSpec { capsule = true, center = new Vector3(0f, height * 0.5f, 0f), size = new Vector3(radius, height, 0f) }); return this; }
        /// <summary>Small props and anything flat: no shadow casting.</summary>
        public Recipe NoShadows() { castShadows = false; return this; }
        /// <summary>Big opaque solids (sheds, pavilions, hills): also an occluder.</summary>
        public Recipe Occluder() { occluder = true; return this; }
        /// <summary>Puts instances on layer Ground (only for things the player must stand on; give them a collider).</summary>
        public Recipe Walkable() { layer = LayerGround; return this; }
    }

    internal sealed class BuiltProp { public Recipe recipe; public MeshData data; public Mesh mesh; public Material[] materials; public Bounds bounds; }
    static readonly Dictionary<string, Recipe> recipes = new Dictionary<string, Recipe>();
    static readonly Dictionary<string, BuiltProp> builtProps = new Dictionary<string, BuiltProp>();

    static void ResetProps() { recipes.Clear(); builtProps.Clear(); }
    static void ResetBuiltProps() { builtProps.Clear(); foreach (var r in recipes.Values) r.frozen = false; }

    /// <summary>Starts (or replaces, if never spawned) the recipe for a prop id. Ids are global: prefix module-private
    /// props with the module name ("park.bench"). The mesh asset is "Props/TL_P_&lt;id&gt;".</summary>
    public static Recipe DefineProp(string id)
    {
        RequireActive();
        if (builtProps.ContainsKey(id)) throw new InvalidOperationException("Prop '" + id + "' was already spawned and cannot be redefined.");
        var r = new Recipe(id); recipes[id] = r; return r;
    }
    /// <summary>True when a recipe with this id exists in the current session.</summary>
    public static bool HasProp(string id) { return recipes.ContainsKey(id); }
    /// <summary>Local-space bounds of a prop's mesh (builds the mesh if needed).</summary>
    public static Bounds PropBounds(string id) { return BuiltOf(id).bounds; }
    /// <summary>Every defined prop id (for catalogue / contact-sheet tools).</summary>
    public static IEnumerable<string> PropIds => recipes.Keys;

    internal static BuiltProp BuiltOf(string id)
    {
        RequireActive();
        if (builtProps.TryGetValue(id, out var b)) return b;
        if (!recipes.TryGetValue(id, out var recipe)) throw new KeyNotFoundException("TownLife prop '" + id + "' was never defined (TownLifeKit.DefineProp).");
        var data = new MeshData();
        foreach (var part in recipe.parts) part(data);
        if (data.vertices.Count == 0) throw new InvalidOperationException("Prop '" + id + "' has no geometry.");
        recipe.frozen = true;
        b = new BuiltProp { recipe = recipe, data = data, bounds = data.Bounds() };
        if (recipe.fitBox)
        {
            var size = b.bounds.size; size.x = Mathf.Max(0.05f, size.x - recipe.fitShrink * 2f); size.z = Mathf.Max(0.05f, size.z - recipe.fitShrink * 2f);
            recipe.colliders.Add(new ColliderSpec { center = b.bounds.center, size = size }); recipe.fitBox = false;
        }
        b.mesh = StoreMesh("Props/" + Prefix + "P_" + Sanitize(id), data);
        b.materials = new Material[data.materials.Count];
        for (int i = 0; i < b.materials.Length; i++) b.materials[i] = Mat(data.materials[i]);
        builtProps[id] = b;
        return b;
    }

    // ------------------------------------------------------------------------------------ primitive generators
    // Each takes a matrix from the primitive's unit frame to prop-local space. All faces are wound to face outwards.

    static void GenBox(MeshData d, string mat, Matrix4x4 m)
    {
        var c = new Vector3[8];
        for (int i = 0; i < 8; i++) c[i] = m.MultiplyPoint3x4(new Vector3((i & 1) == 0 ? -0.5f : 0.5f, (i & 2) == 0 ? -0.5f : 0.5f, (i & 4) == 0 ? -0.5f : 0.5f));
        var o = m.MultiplyPoint3x4(Vector3.zero);
        d.QuadOut(mat, o, c[0], c[1], c[3], c[2]); d.QuadOut(mat, o, c[4], c[5], c[7], c[6]);
        d.QuadOut(mat, o, c[0], c[1], c[5], c[4]); d.QuadOut(mat, o, c[2], c[3], c[7], c[6]);
        d.QuadOut(mat, o, c[0], c[2], c[6], c[4]); d.QuadOut(mat, o, c[1], c[3], c[7], c[5]);
    }

    static void GenCyl(MeshData d, string mat, Matrix4x4 m, float r0, float r1, float h, int sides, bool smooth, int caps = 3)
    {
        sides = Mathf.Max(3, sides);
        var bottom = new Vector3[sides]; var top = new Vector3[sides]; var nrm = new Vector3[sides];
        float phase = sides == 4 ? Mathf.PI * 0.25f : 0f;                    // square posts are axis aligned
        float rs = sides == 4 ? 1.41421356f : 1f;                           // ... and `radius` is then the half side
        for (int i = 0; i < sides; i++)
        {
            float a = phase + i * Mathf.PI * 2f / sides; float cx = Mathf.Cos(a), cz = Mathf.Sin(a);
            bottom[i] = m.MultiplyPoint3x4(new Vector3(cx * r0 * rs, 0f, cz * r0 * rs)); top[i] = m.MultiplyPoint3x4(new Vector3(cx * r1 * rs, h, cz * r1 * rs));
            nrm[i] = m.MultiplyVector(new Vector3(cx * h, r0 - r1, cz * h)).normalized;
        }
        Vector3 cb = m.MultiplyPoint3x4(Vector3.zero), ct = m.MultiplyPoint3x4(new Vector3(0f, h, 0f)), mid = (cb + ct) * 0.5f;
        for (int i = 0; i < sides; i++)
        {
            int j = (i + 1) % sides;
            if (smooth)
            {
                bool flip = Vector3.Dot(Vector3.Cross(top[i] - bottom[i], bottom[j] - bottom[i]), bottom[i] - mid) < 0f;
                if (r1 > 1e-5f) { if (flip) { d.Tri(mat, bottom[i], top[j], top[i], nrm[i], nrm[j], nrm[i]); d.Tri(mat, bottom[i], bottom[j], top[j], nrm[i], nrm[j], nrm[j]); } else { d.Tri(mat, bottom[i], top[i], top[j], nrm[i], nrm[i], nrm[j]); d.Tri(mat, bottom[i], top[j], bottom[j], nrm[i], nrm[j], nrm[j]); } }
                else { if (flip) d.Tri(mat, bottom[i], bottom[j], top[i], nrm[i], nrm[j], nrm[i]); else d.Tri(mat, bottom[i], top[i], bottom[j], nrm[i], nrm[i], nrm[j]); }
            }
            else if (r1 > 1e-5f) d.QuadOut(mat, mid, bottom[i], top[i], top[j], bottom[j]);
            else d.TriOut(mat, mid, bottom[i], top[i], bottom[j]);
            if (r0 > 1e-5f && (caps & 1) != 0) d.TriOut(mat, mid, cb, bottom[i], bottom[j]);
            if (r1 > 1e-5f && (caps & 2) != 0) d.TriOut(mat, mid, ct, top[i], top[j]);
        }
    }

    static void GenBlob(MeshData d, string mat, Matrix4x4 m, int lon, int lat, int seed, float jitter, bool smooth, bool dome)
    {
        lon = Mathf.Max(3, lon); lat = Mathf.Max(2, lat);
        var rng = new System.Random(seed);
        var grid = new Vector3[lat + 1, lon]; var nrm = new Vector3[lat + 1, lon];
        var nm = m.inverse.transpose;
        for (int r = 0; r <= lat; r++)
        {
            float t = r / (float)lat, polar = dome ? t * Mathf.PI * 0.5f : t * Mathf.PI;     // 0 = top
            float poleJitter = jitter > 0f ? 1f + ((float)rng.NextDouble() - 0.5f) * jitter : 1f;
            for (int s = 0; s < lon; s++)
            {
                float az = s * Mathf.PI * 2f / lon + (r % 2) * Mathf.PI / lon * (jitter > 0f ? 1f : 0f);
                bool pole = r == 0 || (!dome && r == lat);
                float k = pole ? poleJitter : jitter > 0f ? 1f + ((float)rng.NextDouble() - 0.5f) * 2f * jitter : 1f;
                var unit = new Vector3(Mathf.Sin(polar) * Mathf.Cos(az), Mathf.Cos(polar), Mathf.Sin(polar) * Mathf.Sin(az));
                grid[r, s] = m.MultiplyPoint3x4(unit * k); nrm[r, s] = nm.MultiplyVector(unit).normalized;
            }
        }
        var o = m.MultiplyPoint3x4(dome ? new Vector3(0f, 0.3f, 0f) : Vector3.zero);
        void Emit(Vector3 p, Vector3 q, Vector3 w, Vector3 np, Vector3 nq, Vector3 nw)
        {
            if (!smooth) { d.TriOut(mat, o, p, q, w); return; }
            if (Vector3.Cross(q - p, w - p).sqrMagnitude < 1e-12f) return;
            if (Vector3.Dot(Vector3.Cross(q - p, w - p), (p + q + w) / 3f - o) < 0f) d.Tri(mat, p, w, q, np, nw, nq); else d.Tri(mat, p, q, w, np, nq, nw);
        }
        for (int r = 0; r < lat; r++)
            for (int s = 0; s < lon; s++)
            {
                int j = (s + 1) % lon;
                if (r > 0) Emit(grid[r, s], grid[r, j], grid[r + 1, j], nrm[r, s], nrm[r, j], nrm[r + 1, j]);                       // upper pole ring has no top edge
                if (r < lat - 1 || dome) Emit(grid[r, s], grid[r + 1, j], grid[r + 1, s], nrm[r, s], nrm[r + 1, j], nrm[r + 1, s]);   // lower pole ring has no bottom edge
            }
        if (dome)
        {
            var bc = m.MultiplyPoint3x4(Vector3.zero);
            for (int s = 0; s < lon; s++) d.TriOut(mat, o, bc, grid[lat, s], grid[lat, (s + 1) % lon]);
        }
    }

    /// <summary>apexX = 0: symmetric gable; -0.5: wedge with the high edge at local -X.</summary>
    static void GenGable(MeshData d, string mat, Matrix4x4 m, float apexX)
    {
        Vector3 P(float x, float y, float z) { return m.MultiplyPoint3x4(new Vector3(x, y, z)); }
        Vector3 a0 = P(-0.5f, 0, -0.5f), b0 = P(0.5f, 0, -0.5f), t0 = P(apexX, 1, -0.5f);
        Vector3 a1 = P(-0.5f, 0, 0.5f), b1 = P(0.5f, 0, 0.5f), t1 = P(apexX, 1, 0.5f);
        var o = P(0f, 0.3f, 0f);
        d.TriOut(mat, o, a0, b0, t0); d.TriOut(mat, o, a1, b1, t1);
        d.QuadOut(mat, o, a0, t0, t1, a1); d.QuadOut(mat, o, b0, t0, t1, b1); d.QuadOut(mat, o, a0, b0, b1, a1);
    }

    static void GenLathe(MeshData d, string mat, Vector3 origin, List<Vector2> profile, int sides, int seed, float jitter)
    {
        sides = Mathf.Max(3, sides); int n = profile.Count; if (n < 2) return;
        var rng = new System.Random(seed); var ring = new Vector3[n, sides];
        for (int i = 0; i < n; i++)
            for (int s = 0; s < sides; s++)
            {
                float a = (s + (jitter > 0f && (i & 1) == 1 ? 0.5f : 0f)) * Mathf.PI * 2f / sides;
                float k = jitter > 0f ? 1f + ((float)rng.NextDouble() - 0.5f) * 2f * jitter : 1f;
                float r = profile[i].x * k;
                ring[i, s] = origin + new Vector3(Mathf.Cos(a) * r, profile[i].y + (jitter > 0f && i > 0 && i < n - 1 ? ((float)rng.NextDouble() - 0.5f) * jitter * profile[i].x : 0f), Mathf.Sin(a) * r);
            }
        for (int i = 0; i < n - 1; i++)
        {
            var inside = origin + new Vector3(0f, (profile[i].y + profile[i + 1].y) * 0.5f, 0f);
            for (int s = 0; s < sides; s++)
            {
                int j = (s + 1) % sides;
                d.TriOut(mat, inside, ring[i, s], ring[i + 1, s], ring[i + 1, j]); d.TriOut(mat, inside, ring[i, s], ring[i + 1, j], ring[i, j]);
            }
        }
        var mid = origin + new Vector3(0f, (profile[0].y + profile[n - 1].y) * 0.5f, 0f);
        for (int s = 0; s < sides; s++)
        {
            int j = (s + 1) % sides;
            if (profile[0].x > 1e-5f) d.TriOut(mat, mid, origin + new Vector3(0f, profile[0].y, 0f), ring[0, s], ring[0, j]);
            if (profile[n - 1].x > 1e-5f) d.TriOut(mat, mid, origin + new Vector3(0f, profile[n - 1].y, 0f), ring[n - 1, s], ring[n - 1, j]);
        }
    }

    static void GenSpike(MeshData d, string mat, Vector3 a, Vector3 tip, float radius)
    {
        var dir = tip - a; if (dir.sqrMagnitude < 1e-10f) return;
        var q = Quaternion.FromToRotation(Vector3.up, dir.normalized); var p = new Vector3[3];
        for (int i = 0; i < 3; i++) { float ang = i * Mathf.PI * 2f / 3f; p[i] = a + q * new Vector3(Mathf.Cos(ang) * radius, 0f, Mathf.Sin(ang) * radius); }
        var inside = a + dir * 0.25f;
        for (int i = 0; i < 3; i++) d.TriOut(mat, inside, p[i], tip, p[(i + 1) % 3]);
    }

    /// <summary>Skin between two (x, y) outlines with the same point count at z0 and z1, optional end caps (fans about
    /// the outline centroid). Faces point away from the centroid axis.</summary>
    static void GenPrism(MeshData d, string mat, Vector2[] p0, float z0, Vector2[] p1, float z1, bool cap0, bool cap1, string capMat = null)
    {
        int n = p0.Length; Vector2 c0 = Vector2.zero, c1 = Vector2.zero;
        for (int i = 0; i < n; i++) { c0 += p0[i]; c1 += p1[i]; }
        c0 /= n; c1 /= n;
        var inside = new Vector3((c0.x + c1.x) * 0.5f, (c0.y + c1.y) * 0.5f, (z0 + z1) * 0.5f);
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            Vector3 a = new Vector3(p0[i].x, p0[i].y, z0), b = new Vector3(p0[j].x, p0[j].y, z0), e = new Vector3(p1[j].x, p1[j].y, z1), f = new Vector3(p1[i].x, p1[i].y, z1);
            d.TriOut(mat, inside, a, b, e); d.TriOut(mat, inside, a, e, f);
            if (cap0) d.TriOut(capMat ?? mat, inside, new Vector3(c0.x, c0.y, z0), a, b);
            if (cap1) d.TriOut(capMat ?? mat, inside, new Vector3(c1.x, c1.y, z1), f, e);
        }
    }

    static void GenTorus(MeshData d, string mat, Matrix4x4 m, float ring, float tube, int segments, int sides)
    {
        segments = Mathf.Max(3, segments); sides = Mathf.Max(3, sides);
        var grid = new Vector3[segments, sides]; var centre = new Vector3[segments];
        for (int i = 0; i < segments; i++)
        {
            float a = i * Mathf.PI * 2f / segments; var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            centre[i] = m.MultiplyPoint3x4(dir * ring);
            for (int k = 0; k < sides; k++)
            {
                float b = k * Mathf.PI * 2f / sides;
                grid[i, k] = m.MultiplyPoint3x4(dir * (ring + Mathf.Cos(b) * tube) + Vector3.up * (Mathf.Sin(b) * tube));
            }
        }
        for (int i = 0; i < segments; i++)
            for (int k = 0; k < sides; k++)
            {
                int j = (i + 1) % segments, l = (k + 1) % sides;
                d.QuadOut(mat, (centre[i] + centre[j]) * 0.5f, grid[i, k], grid[j, k], grid[j, l], grid[i, l]);
            }
    }
}

// ============================================================================================================
// Stock palette and the stock prop library ("kit.*"). Everything below is content, not machinery.
// ============================================================================================================
public static partial class TownLifeKit
{
    static Vector3 V(float x, float y, float z) { return new Vector3(x, y, z); }
    static Color C(float r, float g, float b) { return new Color(r, g, b); }

    /// <summary>Colours every module may use without defining them. Extend freely, never repurpose a key.</summary>
    static void DefineStockPalette()
    {
        // foliage
        Define("leaf.mid", C(0.30f, 0.62f, 0.24f));
        Define("leaf.dark", C(0.20f, 0.50f, 0.23f));
        Define("leaf.lime", C(0.55f, 0.78f, 0.27f));
        Define("leaf.light", C(0.42f, 0.71f, 0.27f));
        Define("leaf.olive", C(0.40f, 0.54f, 0.22f));
        Define("leaf.oak", C(0.27f, 0.56f, 0.19f));
        Define("leaf.deep", C(0.15f, 0.43f, 0.24f));
        Define("leaf.elm", C(0.36f, 0.66f, 0.30f));
        Define("leaf.yellow", C(0.74f, 0.82f, 0.26f));
        Define("leaf.pine", C(0.17f, 0.54f, 0.38f));
        Define("leaf.pine.dark", C(0.12f, 0.43f, 0.33f));
        Define("leaf.pickle", C(0.38f, 0.60f, 0.14f));
        Define("leaf.pickle.light", C(0.56f, 0.74f, 0.20f));
        Define("leaf.pickle.dark", C(0.26f, 0.47f, 0.12f));
        Define("leaf.willow", C(0.50f, 0.72f, 0.30f));
        Define("leaf.willow.light", C(0.66f, 0.82f, 0.36f));
        Define("leaf.willow.dark", C(0.36f, 0.60f, 0.26f));
        Define("leaf.blossom", C(0.98f, 0.66f, 0.78f));
        Define("leaf.blossom.light", C(1.00f, 0.84f, 0.90f));
        Define("leaf.blossom.deep", C(0.94f, 0.46f, 0.66f));
        Define("leaf.autumn", C(0.95f, 0.56f, 0.18f));
        Define("leaf.autumn.red", C(0.87f, 0.31f, 0.15f));
        Define("leaf.autumn.gold", C(0.99f, 0.76f, 0.20f));
        Define("hedge", C(0.18f, 0.49f, 0.23f));
        Define("hedge.light", C(0.31f, 0.63f, 0.27f));
        Define("hedge.dark", C(0.13f, 0.40f, 0.21f));
        Define("grass.tuft", C(0.60f, 0.75f, 0.34f));
        Define("grass.dry", C(0.80f, 0.78f, 0.45f));
        Define("lawn.light", C(0.47f, 0.70f, 0.31f), 0.02f);
        Define("lawn.dark", C(0.37f, 0.60f, 0.27f), 0.02f);
        Define("reed", C(0.48f, 0.64f, 0.28f));
        Define("reed.head", C(0.47f, 0.30f, 0.16f));
        Define("lily", C(0.24f, 0.60f, 0.32f), 0.3f);
        // flowers and fruit
        Define("flower.red", C(0.93f, 0.20f, 0.22f), 0.2f);
        Define("flower.yellow", C(1.00f, 0.85f, 0.18f), 0.2f);
        Define("flower.pink", C(1.00f, 0.52f, 0.72f), 0.2f);
        Define("flower.purple", C(0.62f, 0.40f, 0.90f), 0.2f);
        Define("flower.white", C(0.98f, 0.98f, 0.95f), 0.2f);
        Define("flower.orange", C(1.00f, 0.58f, 0.16f), 0.2f);
        Define("flower.blue", C(0.35f, 0.58f, 0.98f), 0.2f);
        Define("fruit.red", C(0.90f, 0.14f, 0.14f), 0.45f);
        // wood, bark
        Define("bark", C(0.45f, 0.31f, 0.20f));
        Define("bark.dark", C(0.36f, 0.25f, 0.18f));
        Define("bark.birch", C(0.93f, 0.92f, 0.88f));
        Define("wood.warm", C(0.72f, 0.48f, 0.27f));
        Define("wood.dark", C(0.45f, 0.29f, 0.17f));
        Define("wood.pale", C(0.86f, 0.70f, 0.47f));
        Define("wood.grey", C(0.66f, 0.62f, 0.55f));
        // paint
        Define("paint.white", C(0.96f, 0.96f, 0.94f), 0.2f);
        Define("paint.red", C(0.88f, 0.22f, 0.20f), 0.25f);
        Define("paint.yellow", C(0.99f, 0.82f, 0.22f), 0.25f);
        Define("paint.blue", C(0.25f, 0.55f, 0.92f), 0.25f);
        Define("paint.green", C(0.22f, 0.60f, 0.36f), 0.25f);
        Define("paint.orange", C(0.98f, 0.55f, 0.18f), 0.25f);
        Define("paint.pink", C(0.98f, 0.60f, 0.74f), 0.25f);
        Define("paint.teal", C(0.20f, 0.68f, 0.68f), 0.25f);
        Define("paint.cream", C(0.98f, 0.93f, 0.78f), 0.2f);
        Define("paint.purple", C(0.58f, 0.42f, 0.82f), 0.25f);
        Define("skin", C(0.98f, 0.78f, 0.62f), 0.15f);
        // stone, masonry, ground
        Define("stone.light", C(0.80f, 0.78f, 0.72f), 0.1f);
        Define("stone.dark", C(0.52f, 0.52f, 0.54f), 0.1f);
        Define("stone.warm", C(0.74f, 0.66f, 0.54f), 0.1f);
        Define("stone.moss", C(0.50f, 0.58f, 0.40f), 0.05f);
        Define("brick", C(0.74f, 0.36f, 0.26f), 0.05f);
        Define("brick.dark", C(0.56f, 0.26f, 0.20f), 0.05f);
        Define("terracotta", C(0.84f, 0.46f, 0.28f), 0.1f);
        Define("paving.warm", C(0.86f, 0.76f, 0.60f), 0.05f);
        Define("paving.grey", C(0.72f, 0.72f, 0.70f), 0.05f);
        Define("paving.red", C(0.78f, 0.45f, 0.36f), 0.05f);
        Define("gravel", C(0.80f, 0.75f, 0.64f), 0.03f);
        Define("sand", C(0.94f, 0.86f, 0.62f), 0.02f);
        Define("soil", C(0.42f, 0.30f, 0.20f), 0.02f);
        Define("mulch", C(0.52f, 0.36f, 0.24f), 0.02f);
        Define("rubber.red", C(0.78f, 0.34f, 0.28f), 0.05f);
        Define("rubber.blue", C(0.28f, 0.52f, 0.72f), 0.05f);
        Define("court.green", C(0.24f, 0.56f, 0.44f), 0.05f);
        Define("water", C(0.30f, 0.66f, 0.88f), 0.9f);
        Define("water.deep", C(0.20f, 0.52f, 0.80f), 0.9f);
        // metal, misc
        Define("metal.dark", C(0.16f, 0.17f, 0.19f), 0.4f, 0.5f);
        Define("metal.light", C(0.72f, 0.74f, 0.77f), 0.55f, 0.6f);
        Define("metal.green", C(0.13f, 0.34f, 0.26f), 0.4f, 0.3f);
        Define("roof.red", C(0.76f, 0.30f, 0.24f), 0.1f);
        Define("roof.slate", C(0.34f, 0.42f, 0.52f), 0.1f);
        Define("roof.brown", C(0.48f, 0.33f, 0.24f), 0.1f);
        Define("glass", C(0.66f, 0.86f, 0.94f), 0.9f);
        Define("eye.white", C(1f, 1f, 1f), 0.85f);
        Define("eye.black", C(0.02f, 0.02f, 0.03f), 0.9f);
        Define("duck.yellow", C(1.00f, 0.86f, 0.22f), 0.35f);
        Define("duck.white", C(0.97f, 0.97f, 0.95f), 0.35f);
        Define("duck.orange", C(1.00f, 0.55f, 0.12f), 0.35f);
        DefineEmissive("lamp.glow", C(1.00f, 0.95f, 0.72f), C(1.00f, 0.86f, 0.50f) * 1.6f);
    }

    /// <summary>Tree builder: design coordinates are for the full-size tree; positions scale by <c>s</c>, limb radii by
    /// s^0.75 (small trees keep sturdy toy trunks). mirror flips X; (ox, oz) shifts the whole tree (clumps).</summary>
    sealed class TB
    {
        readonly Recipe r; readonly float s, rs, ox, oz; readonly bool mirror; readonly float yaw;
        public TB(Recipe r, float s, bool mirror = false, float ox = 0f, float oz = 0f, float yaw = 0f) { this.r = r; this.s = s; rs = 1.15f * Mathf.Pow(s, 0.75f); this.mirror = mirror; this.ox = ox; this.oz = oz; this.yaw = yaw; }
        public Vector3 P(float x, float y, float z) { var p = Quaternion.Euler(0f, yaw, 0f) * new Vector3(mirror ? -x : x, y, z) * s; p.x += ox; p.z += oz; return p; }
        public TB Limb(string mat, float ax, float ay, float az, float bx, float by, float bz, float ra, float rb, int sides = 6, int caps = 3) { r.Limb(mat, P(ax, ay, az), P(bx, by, bz), ra * rs, rb * rs, sides, caps); return this; }
        public TB Leaf(string mat, float x, float y, float z, float rx, float ry, float rz, int seed, int lon = 7, int lat = 4, float jitter = 0.2f) { r.Blob(mat, P(x, y, z), new Vector3(rx, ry, rz) * s, seed, jitter, lon, lat, new Vector3(0f, seed * 37f, 0f)); return this; }
        public TB Skirt(string mat, float y, float r0, float r1, float h, int sides, float turn, float x = 0f, float z = 0f, int caps = 1) { r.Part2(d => GenCyl(d, mat, Matrix4x4.TRS(P(x, y, z), Quaternion.Euler(0f, turn, 0f), Vector3.one), r0 * s, r1 * s, h * s, sides, false, caps)); return this; }
        public TB Dot(string mat, float x, float y, float z, float radius) { r.Sphere(mat, P(x, y, z), Vector3.one * (radius * rs), 4, 2); return this; }
        public float S => s; public float RS => rs;
    }

    // ---------------------------------------------------------------------------------------------- species

    static void TreeRound(string id, float s, int seed, bool mirror = false)
    {
        var r = DefineProp(id); var t = new TB(r, s, mirror);
        t.Limb("bark", 0, 0, 0, 0, 0.6f, 0, 0.46f, 0.30f).Limb("bark", 0, 0.5f, 0, 0.18f, 2.3f, 0.05f, 0.30f, 0.26f, 6, 0).Limb("bark", 0.18f, 2.3f, 0.05f, -0.1f, 4.4f, 0, 0.26f, 0.17f, 6, 0);
        t.Limb("bark", 0.1f, 2.6f, 0, 1.6f, 4.6f, 0.5f, 0.15f, 0.08f, 5, 0).Limb("bark", 0.1f, 2.4f, 0, -1.5f, 4.3f, -0.6f, 0.15f, 0.08f, 5, 0);
        t.Leaf("leaf.mid", 0, 6.3f, 0, 3.1f, 2.6f, 3.0f, seed, 8, 5);
        t.Leaf("leaf.dark", -1.8f, 5.0f, 1.0f, 1.9f, 1.5f, 1.9f, seed + 1);
        t.Leaf("leaf.light", 1.9f, 5.3f, -0.8f, 2.0f, 1.6f, 1.9f, seed + 2);
        if (s > 0.5f) t.Leaf("leaf.light", 0.5f, 8.0f, 0.4f, 1.7f, 1.2f, 1.7f, seed + 3);
        r.ColliderTrunk(0.36f * t.RS, Mathf.Max(2f, 3.2f * s));
    }

    static void TreeElm(string id, float s, int seed)
    {
        var r = DefineProp(id); var t = new TB(r, s);
        t.Limb("bark", 0, 0, 0, 0, 0.6f, 0, 0.50f, 0.33f).Limb("bark", 0, 0.5f, 0, 0.05f, 3.4f, 0, 0.33f, 0.27f, 6, 0);
        t.Limb("bark", 0.05f, 3.2f, 0, 2.3f, 6.6f, 0.7f, 0.22f, 0.10f, 5, 0).Limb("bark", 0.05f, 3.2f, 0, -2.2f, 6.5f, 0.6f, 0.22f, 0.10f, 5, 0).Limb("bark", 0.05f, 3.2f, 0, 0.2f, 6.7f, -2.3f, 0.22f, 0.10f, 5, 0);
        t.Leaf("leaf.elm", 0, 8.0f, 0, 3.7f, 2.1f, 3.5f, seed, 9, 4, 0.16f);
        t.Leaf("leaf.light", 2.4f, 7.1f, 0.8f, 2.0f, 1.5f, 1.9f, seed + 1);
        t.Leaf("leaf.mid", -2.4f, 7.0f, 0.7f, 2.0f, 1.5f, 2.0f, seed + 2);
        t.Leaf("leaf.dark", 0.2f, 7.0f, -2.4f, 2.0f, 1.2f, 1.9f, seed + 3);
        t.Leaf("leaf.light", -0.2f, 9.5f, 0.2f, 2.4f, 1.1f, 2.3f, seed + 4, 7, 3);
        r.ColliderTrunk(0.38f * t.RS, Mathf.Max(2f, 3.4f * s));
    }

    static void TreeOak(string id, float s, int seed)
    {
        var r = DefineProp(id); var t = new TB(r, s);
        t.Limb("bark.dark", 0, 0, 0, 0, 0.8f, 0, 0.85f, 0.52f, 7).Limb("bark.dark", 0, 0.7f, 0, 0.1f, 2.7f, 0, 0.52f, 0.44f, 7, 0);
        t.Limb("bark.dark", 0.1f, 2.4f, 0, 3.0f, 4.4f, 0.6f, 0.30f, 0.14f, 5, 0).Limb("bark.dark", 0.1f, 2.4f, 0, -2.9f, 4.2f, -0.9f, 0.30f, 0.14f, 5, 0).Limb("bark.dark", 0.1f, 2.4f, 0, 0.3f, 4.5f, 2.7f, 0.28f, 0.14f, 5, 0);
        t.Leaf("leaf.dark", 0, 5.7f, 0, 3.7f, 2.5f, 3.6f, seed, 8, 5);
        t.Leaf("leaf.oak", 3.1f, 4.9f, 0.6f, 2.3f, 1.7f, 2.2f, seed + 1);
        t.Leaf("leaf.mid", -3.0f, 4.7f, -0.9f, 2.4f, 1.7f, 2.3f, seed + 2);
        t.Leaf("leaf.deep", 0.3f, 4.8f, 2.9f, 2.2f, 1.6f, 2.1f, seed + 3);
        t.Leaf("leaf.oak", -0.5f, 7.4f, -0.4f, 2.5f, 1.5f, 2.4f, seed + 4);
        r.ColliderTrunk(0.58f * t.RS, Mathf.Max(2f, 3f * s));
    }

    static void TreeMaple(string id, float s, int seed)
    {
        var r = DefineProp(id); var t = new TB(r, s);
        t.Limb("bark", 0, 0, 0, 0, 0.6f, 0, 0.46f, 0.30f).Limb("bark", 0, 0.5f, 0, -0.1f, 3.2f, 0.05f, 0.30f, 0.22f, 6, 0);
        t.Limb("bark", -0.05f, 2.4f, 0, 1.4f, 4.0f, 0.8f, 0.14f, 0.08f, 5, 0).Limb("bark", -0.05f, 2.4f, 0, -1.4f, 4.2f, -0.7f, 0.14f, 0.08f, 5, 0);
        t.Leaf("leaf.autumn", 0, 5.7f, 0, 2.9f, 3.0f, 2.8f, seed, 8, 5);
        t.Leaf("leaf.autumn.red", 1.7f, 4.3f, 1.0f, 1.7f, 1.4f, 1.6f, seed + 1);
        t.Leaf("leaf.autumn.gold", -1.7f, 4.7f, -0.9f, 1.7f, 1.5f, 1.7f, seed + 2);
        t.Leaf("leaf.autumn.red", 0.3f, 8.0f, 0.2f, 1.6f, 1.3f, 1.6f, seed + 3);
        if (s > 0.8f) t.Leaf("leaf.autumn.gold", -0.6f, 6.6f, 1.9f, 1.3f, 1.1f, 1.2f, seed + 4, 6, 3);
        r.ColliderTrunk(0.36f * t.RS, Mathf.Max(2f, 3.2f * s));
    }

    static void TreeBirch(string id, float s, int seed)
    {
        var r = DefineProp(id); var t = new TB(r, s);
        t.Limb("bark.birch", 0, 0, 0, 0.28f, 4.0f, 0.1f, 0.24f, 0.17f, 6, 1).Limb("bark.birch", 0.28f, 4.0f, 0.1f, 0.05f, 7.9f, 0, 0.17f, 0.07f, 6, 0);
        t.Limb("bark.birch", 0.14f, 2.0f, 0.05f, -1.4f, 6.4f, 0.6f, 0.15f, 0.06f, 5, 0);
        t.Limb("bark.birch", 0.26f, 3.6f, 0.1f, 1.0f, 6.5f, 0.6f, 0.10f, 0.05f, 4, 0);
        // the dark birch bands
        float[] bands = { 0.9f, 1.75f, 2.8f, 4.6f, 5.6f };
        foreach (float y in bands) { float k = y / 4f; float x = y < 4f ? 0.28f * k : Mathf.Lerp(0.28f, 0.05f, (y - 4f) / 3.9f); float rad = Mathf.Lerp(0.24f, 0.11f, y / 6f) + 0.012f; t.Limb("bark.dark", x, y, 0.1f * Mathf.Min(1f, k), x + 0.005f, y + 0.13f, 0.1f * Mathf.Min(1f, k), rad, rad, 6, 0); }
        t.Leaf("leaf.lime", 0.05f, 8.3f, 0, 1.5f, 1.5f, 1.4f, seed);
        t.Leaf("leaf.light", 1.1f, 6.8f, 0.7f, 1.3f, 1.2f, 1.3f, seed + 1);
        t.Leaf("leaf.lime", -1.5f, 6.8f, 0.7f, 1.5f, 1.4f, 1.4f, seed + 2);
        t.Leaf("leaf.yellow", -0.3f, 5.6f, -1.0f, 1.2f, 1.0f, 1.2f, seed + 3, 6, 3);
        t.Leaf("leaf.lime", 0.9f, 5.0f, -0.6f, 1.0f, 0.9f, 1.0f, seed + 4, 6, 3);
        r.ColliderTrunk(0.26f * t.RS, Mathf.Max(2f, 3.4f * s));
    }

    static void TreeBlossom(string id, float s, int seed)
    {
        var r = DefineProp(id); var t = new TB(r, s);
        t.Limb("bark.dark", 0, 0, 0, 0, 0.4f, 0, 0.34f, 0.22f).Limb("bark.dark", 0, 0.3f, 0, 0.1f, 2.2f, 0, 0.22f, 0.18f, 6, 0);
        t.Limb("bark.dark", 0.08f, 1.7f, 0, 1.2f, 3.0f, 0.4f, 0.12f, 0.07f, 5, 0).Limb("bark.dark", 0.08f, 1.7f, 0, -1.1f, 3.0f, -0.5f, 0.12f, 0.07f, 5, 0);
        t.Leaf("leaf.blossom", 0, 4.0f, 0, 2.3f, 2.0f, 2.3f, seed, 8, 5);
        t.Leaf("leaf.blossom.light", 1.1f, 5.2f, 0.7f, 1.1f, 0.9f, 1.1f, seed + 1, 6, 3);
        t.Leaf("leaf.blossom.deep", -1.4f, 4.4f, -0.8f, 1.2f, 1.0f, 1.2f, seed + 2, 6, 3);
        t.Leaf("leaf.blossom.light", -0.5f, 3.2f, 1.7f, 1.0f, 0.8f, 1.0f, seed + 3, 6, 3);
        t.Leaf("leaf.blossom.deep", 1.5f, 3.2f, -1.2f, 1.1f, 0.9f, 1.0f, seed + 4, 6, 3);
        t.Leaf("leaf.blossom.light", -0.9f, 5.4f, -0.3f, 0.9f, 0.7f, 0.9f, seed + 5, 6, 3);
        r.ColliderTrunk(0.28f * t.RS, Mathf.Max(1.6f, 2.4f * s));
    }

    static void TreePickle(string id, float s, int seed)
    {
        var r = DefineProp(id); var t = new TB(r, s);
        t.Limb("bark", 0, 0, 0, 0, 1.4f, 0, 0.26f, 0.20f, 6, 0);
        var profile = new[] { new Vector2(0.55f, 0.9f), new Vector2(1.25f, 1.7f), new Vector2(1.5f, 3.1f), new Vector2(1.45f, 4.9f), new Vector2(1.15f, 6.6f), new Vector2(0.7f, 8.0f), new Vector2(0f, 9.2f) };
        for (int i = 0; i < profile.Length; i++) profile[i] *= s;
        r.Lathe("leaf.pickle", Vector3.zero, profile, 8, seed, 0.09f);
        // pickle warts: lighter and darker bumps on the skin
        t.Leaf("leaf.pickle.light", 1.0f, 3.2f, 0.9f, 0.6f, 0.75f, 0.6f, seed + 1, 5, 3);
        t.Leaf("leaf.pickle.light", -1.1f, 5.3f, -0.6f, 0.55f, 0.75f, 0.55f, seed + 2, 5, 3);
        t.Leaf("leaf.pickle.dark", -0.8f, 2.4f, -1.0f, 0.6f, 0.7f, 0.6f, seed + 3, 5, 3);
        t.Leaf("leaf.pickle.light", 0.5f, 6.9f, -0.7f, 0.45f, 0.65f, 0.45f, seed + 4, 5, 3);
        t.Leaf("leaf.pickle.dark", 0.9f, 5.0f, 0.9f, 0.5f, 0.7f, 0.5f, seed + 5, 5, 3);
        t.Leaf("leaf.pickle.light", -0.9f, 3.8f, 1.0f, 0.45f, 0.6f, 0.45f, seed + 6, 5, 3);
        r.ColliderTrunk(0.5f * t.RS, Mathf.Max(2f, 3f * s));
    }

    static void TreePine(string id, float s, int seed)
    {
        var r = DefineProp(id); var t = new TB(r, s);
        t.Limb("bark.dark", 0, 0, 0, 0, 0.5f, 0, 0.42f, 0.27f).Limb("bark.dark", 0, 0.4f, 0, 0, 4.0f, 0, 0.27f, 0.12f, 6, 0);
        t.Skirt("leaf.pine.dark", 1.7f, 2.7f, 0.9f, 2.5f, 8, 0f);
        t.Skirt("leaf.pine", 3.5f, 2.2f, 0.7f, 2.4f, 8, 22.5f, 0.1f, 0.05f);
        t.Skirt("leaf.pine.dark", 5.3f, 1.65f, 0.5f, 2.2f, 7, 10f, -0.05f, 0.05f);
        t.Skirt("leaf.pine", 7.0f, 1.15f, 0f, 2.6f, 7, 30f);
        r.ColliderTrunk(0.32f * t.RS, Mathf.Max(2f, 3f * s));
    }

    static void TreeWillow(string id, float s, int seed)
    {
        var r = DefineProp(id); var t = new TB(r, s); var rng = new System.Random(seed);
        t.Limb("bark", 0, 0, 0, 0, 0.7f, 0, 0.80f, 0.50f, 7).Limb("bark", 0, 0.6f, 0, 0.45f, 2.9f, 0, 0.50f, 0.40f, 7, 0);
        t.Limb("bark", 0.4f, 2.6f, 0, 2.0f, 5.4f, 0.5f, 0.26f, 0.12f, 5, 0).Limb("bark", 0.4f, 2.6f, 0, -1.6f, 5.6f, -0.7f, 0.26f, 0.12f, 5, 0).Limb("bark", 0.4f, 2.6f, 0, 0.3f, 5.6f, 1.9f, 0.24f, 0.12f, 5, 0);
        t.Leaf("leaf.willow", 0.2f, 6.7f, 0, 4.0f, 2.0f, 3.8f, seed, 9, 4, 0.14f);
        t.Leaf("leaf.willow.light", -0.5f, 7.9f, 0.3f, 2.5f, 1.3f, 2.4f, seed + 1);
        string[] mats = { "leaf.willow", "leaf.willow.dark", "leaf.willow.light" };
        int fronds = 17;
        for (int i = 0; i < fronds; i++)
        {
            bool inner = i % 3 == 2;
            float a = (i + (float)rng.NextDouble() * 0.4f) * Mathf.PI * 2f / fronds; float rad = inner ? 2.3f + (float)rng.NextDouble() * 0.4f : 3.25f + (float)rng.NextDouble() * 0.45f;
            float tip = (inner ? 2.0f : 1.2f) + (float)rng.NextDouble() * 1.5f, top = 6.4f + (float)rng.NextDouble() * 0.4f;
            float cx = 0.2f + Mathf.Cos(a) * rad, cz = Mathf.Sin(a) * rad * 0.95f;
            t.Limb(mats[i % 3], cx * 1.04f, tip, cz * 1.04f, cx * 0.93f, top, cz * 0.93f, 0.2f, 0.78f + (float)rng.NextDouble() * 0.25f, 4, 1);
        }
        r.ColliderTrunk(0.55f * t.RS, 3f * s);
    }

    static void TreeFruit(string id, float s, int seed)
    {
        var r = DefineProp(id); var t = new TB(r, s); var rng = new System.Random(seed);
        t.Limb("bark", 0, 0, 0, 0, 0.3f, 0, 0.26f, 0.17f).Limb("bark", 0, 0.25f, 0, 0.08f, 1.7f, 0, 0.17f, 0.13f, 6, 0);
        t.Limb("bark", 0.06f, 1.3f, 0, 0.9f, 2.1f, 0.3f, 0.09f, 0.05f, 5, 0).Limb("bark", 0.06f, 1.3f, 0, -0.8f, 2.1f, -0.4f, 0.09f, 0.05f, 5, 0);
        t.Leaf("leaf.mid", 0, 2.7f, 0, 1.6f, 1.25f, 1.6f, seed, 8, 5, 0.16f);
        t.Leaf("leaf.light", 0.8f, 3.3f, 0.4f, 0.95f, 0.75f, 0.95f, seed + 1, 6, 3);
        t.Leaf("leaf.dark", -1.0f, 2.4f, -0.6f, 0.9f, 0.7f, 0.9f, seed + 2, 6, 3);
        for (int i = 0; i < 11; i++)
        {
            float a = (i * 0.618f * 2f + (float)rng.NextDouble() * 0.2f) * Mathf.PI, el = -0.35f + (float)rng.NextDouble() * 1.1f;
            var dir = new Vector3(Mathf.Cos(a) * Mathf.Cos(el), Mathf.Sin(el), Mathf.Sin(a) * Mathf.Cos(el));
            t.Dot("fruit.red", dir.x * 1.62f, 2.7f + dir.y * 1.28f, dir.z * 1.62f, 0.27f);
        }
        r.ColliderTrunk(0.2f * t.RS, Mathf.Max(1.5f, 1.8f * s));
    }

    // cheap trees for clumps (one object holds 3-5 of them)
    static void MiniRound(Recipe r, float ox, float oz, float s, int seed, string a, string b, string bark = "bark")
    {
        var t = new TB(r, s, false, ox, oz, seed * 71f);
        t.Limb(bark, 0, 0, 0, 0.15f, 4.2f, 0.05f, 0.34f, 0.2f, 5, 0);
        t.Leaf(a, 0, 6.3f, 0, 3.4f, 2.8f, 3.3f, seed, 7, 4);
        t.Leaf(b, 1.9f, 5.0f, 1.1f, 2.2f, 1.7f, 2.2f, seed + 1, 6, 3);
        t.Leaf(b, -1.6f, 7.7f, -0.6f, 1.9f, 1.4f, 1.9f, seed + 2, 6, 3);
    }
    static void MiniPine(Recipe r, float ox, float oz, float s, int seed)
    {
        var t = new TB(r, s, false, ox, oz, seed * 53f);
        t.Limb("bark.dark", 0, 0, 0, 0, 3f, 0, 0.3f, 0.14f, 5, 0);
        t.Skirt("leaf.pine.dark", 1.7f, 2.6f, 0.8f, 2.6f, 7, 0f).Skirt("leaf.pine", 3.8f, 2.0f, 0.5f, 2.6f, 7, 25f).Skirt("leaf.pine.dark", 6.0f, 1.4f, 0f, 3.4f, 6, 10f);
    }
    static void MiniBirch(Recipe r, float ox, float oz, float s, int seed)
    {
        var t = new TB(r, s, false, ox, oz, seed * 97f);
        t.Limb("bark.birch", 0, 0, 0, 0.25f, 4.0f, 0.1f, 0.22f, 0.15f, 5, 0).Limb("bark.birch", 0.25f, 4.0f, 0.1f, 0f, 7.6f, 0, 0.15f, 0.07f, 5, 0);
        t.Limb("bark.dark", 0.1f, 1.6f, 0.04f, 0.11f, 1.74f, 0.04f, 0.215f, 0.215f, 5, 0).Limb("bark.dark", 0.2f, 3.2f, 0.08f, 0.21f, 3.33f, 0.08f, 0.185f, 0.185f, 5, 0);
        t.Leaf("leaf.lime", 0, 8.0f, 0, 1.6f, 1.5f, 1.5f, seed, 6, 4);
        t.Leaf("leaf.light", 1.1f, 6.4f, 0.6f, 1.4f, 1.2f, 1.4f, seed + 1, 6, 3);
        t.Leaf("leaf.yellow", -1.2f, 6.2f, -0.4f, 1.4f, 1.2f, 1.3f, seed + 2, 6, 3);
    }
    static void Under(Recipe r, float x, float z, float rad, int seed, string mat = "hedge.dark") { r.Blob(mat, V(x, rad * 0.55f, z), V(rad, rad * 0.75f, rad), seed, 0.22f, 6, 3); }

    static void DefineTrees()
    {
        TreeRound("kit.tree.round.s", 0.42f, 101); TreeRound("kit.tree.round.m", 0.67f, 111, true); TreeRound("kit.tree.round.l", 1f, 121);
        TreeElm("kit.tree.elm.m", 0.68f, 131); TreeElm("kit.tree.elm.l", 1f, 141);
        TreeOak("kit.tree.oak.l", 1f, 151); TreeOak("kit.tree.oak.xl", 1.36f, 161);
        TreeMaple("kit.tree.maple.m", 0.67f, 171); TreeMaple("kit.tree.maple.l", 1f, 181);
        TreeBirch("kit.tree.birch.m", 0.67f, 191); TreeBirch("kit.tree.birch.l", 1f, 201);
        TreeBlossom("kit.tree.blossom.s", 0.6f, 211); TreeBlossom("kit.tree.blossom.m", 1f, 221);
        TreePickle("kit.tree.pickle.m", 0.67f, 231); TreePickle("kit.tree.pickle.l", 1f, 241);
        TreePine("kit.tree.pine.m", 0.65f, 251); TreePine("kit.tree.pine.l", 1f, 261);
        TreeWillow("kit.tree.willow.l", 1f, 271);
        TreeFruit("kit.tree.fruit.s", 1f, 281);
        // legacy fallback id used by early module drafts
        DefineProp("kit.tree.round").Add("kit.tree.round.m", Vector3.zero).ColliderTrunk(0.3f, 2.4f);

        var c = DefineProp("kit.clump.mixed.3");
        MiniRound(c, 0f, 0f, 1.12f, 301, "leaf.mid", "leaf.light"); MiniRound(c, 4.6f, 2.4f, 0.86f, 302, "leaf.dark", "leaf.oak", "bark.dark"); MiniBirch(c, -3.6f, 3.2f, 0.95f, 303);
        Under(c, 2.2f, -2.6f, 1.3f, 304); Under(c, -2.8f, -1.4f, 1.0f, 305, "leaf.olive");
        c.ColliderCapsule(V(0f, 1.6f, 0f), 0.4f, 3.2f).ColliderCapsule(V(4.6f, 1.5f, 2.4f), 0.35f, 3f);

        c = DefineProp("kit.clump.mixed.5");
        MiniRound(c, 0f, 0f, 1.2f, 311, "leaf.dark", "leaf.mid", "bark.dark"); MiniRound(c, 5.4f, 1.0f, 0.9f, 312, "leaf.mid", "leaf.light"); MiniPine(c, -4.6f, 2.6f, 1.05f, 313);
        MiniRound(c, 1.6f, 5.6f, 0.8f, 314, "leaf.olive", "leaf.elm"); MiniBirch(c, -2.6f, -4.6f, 0.92f, 315);
        Under(c, 3.4f, -3.2f, 1.4f, 316); Under(c, -5.6f, -1.6f, 1.1f, 317, "leaf.olive"); Under(c, 5.4f, 5.0f, 1.0f, 318, "leaf.dark");
        c.ColliderCapsule(V(0f, 1.6f, 0f), 0.42f, 3.2f).ColliderCapsule(V(5.4f, 1.5f, 1f), 0.35f, 3f);

        c = DefineProp("kit.clump.pine.3");
        MiniPine(c, 0f, 0f, 1.2f, 321); MiniPine(c, 3.9f, 2.2f, 0.9f, 322); MiniPine(c, -3.0f, 3.0f, 1.02f, 323);
        Under(c, 1.6f, -2.4f, 1.0f, 324, "leaf.deep");
        c.ColliderCapsule(V(0f, 1.6f, 0f), 0.36f, 3.2f).ColliderCapsule(V(3.9f, 1.5f, 2.2f), 0.3f, 3f);

        c = DefineProp("kit.clump.pine.5");
        MiniPine(c, 0f, 0f, 1.25f, 331); MiniPine(c, 4.4f, 1.4f, 0.95f, 332); MiniPine(c, -4.0f, 2.2f, 1.05f, 333); MiniPine(c, 1.2f, 5.0f, 0.85f, 334); MiniPine(c, -1.8f, -4.4f, 0.9f, 335);
        Under(c, 3.2f, -3.0f, 1.1f, 336, "leaf.deep"); Under(c, -5.0f, -2.0f, 0.9f, 337, "leaf.olive");
        c.ColliderCapsule(V(0f, 1.6f, 0f), 0.36f, 3.2f).ColliderCapsule(V(4.4f, 1.5f, 1.4f), 0.3f, 3f);

        c = DefineProp("kit.clump.birch.3");
        MiniBirch(c, 0f, 0f, 1.15f, 341); MiniBirch(c, 3.0f, 1.8f, 0.9f, 342); MiniBirch(c, -2.4f, 2.4f, 1.0f, 343);
        Under(c, 1.2f, -1.8f, 0.9f, 344, "leaf.light"); Under(c, -2.6f, -0.8f, 0.7f, 345, "leaf.olive");
        c.ColliderCapsule(V(0f, 1.6f, 0f), 0.28f, 3.2f).ColliderCapsule(V(3f, 1.5f, 1.8f), 0.24f, 3f);
    }

    /// <summary>Props every module may spawn. Ids are "kit.*" (list in DESIGN section 10).</summary>
    static void DefineStockProps()
    {
        DefineTrees();
        DefinePlants();
        DefinePanels();
        DefineFurniture();
    }
}
public static partial class TownLifeKit
{
    static readonly string[] FlowerNames = { "red", "yellow", "pink", "purple", "white", "mixed" };
    static readonly string[] MixedFlowers = { "flower.red", "flower.yellow", "flower.pink", "flower.purple", "flower.white", "flower.orange", "flower.blue" };
    static string FlowerMat(string name, int i) { return name == "mixed" ? MixedFlowers[i % MixedFlowers.Length] : (i % 5 == 4 ? (name == "white" ? "flower.yellow" : name == "yellow" ? "flower.orange" : "flower.white") : "flower." + name); }
    /// <summary>One chunky toy flower head (an 8-triangle gem) on a green stem.</summary>
    static void Flower(Recipe r, string mat, float x, float z, float h, float size, bool stem = true)
    {
        if (stem) r.Spike("leaf.light", V(x, 0f, z), V(x, h, z), 0.035f);
        r.Sphere(mat, V(x, h, z), V(size, size * 0.8f, size), 5, 2, false, V(0f, x * 400f + z * 250f, 0f));
    }

    static void DefinePlants()
    {
        // ---- shrubs
        DefineProp("kit.shrub.round.s").Blob("hedge", V(0f, 0.32f, 0f), V(0.55f, 0.42f, 0.55f), 21, 0.2f, 7, 4).Blob("leaf.light", V(0.18f, 0.5f, 0.1f), V(0.32f, 0.26f, 0.32f), 22, 0.2f, 6, 3).NoShadows();
        DefineProp("kit.shrub.round.m").Blob("leaf.dark", V(0f, 0.55f, 0f), V(0.95f, 0.72f, 0.9f), 23, 0.2f, 7, 4).Blob("leaf.mid", V(0.35f, 0.85f, 0.2f), V(0.6f, 0.5f, 0.6f), 24, 0.2f, 6, 3).Blob("hedge", V(-0.5f, 0.4f, -0.3f), V(0.55f, 0.42f, 0.55f), 25, 0.2f, 6, 3).NoShadows();
        DefineProp("kit.shrub.round").Add("kit.shrub.round.m", Vector3.zero).NoShadows();           // legacy fallback id
        string[] cols = { "pink", "yellow", "white", "purple" };
        for (int c = 0; c < cols.Length; c++)
        {
            var r = DefineProp("kit.shrub.flower." + cols[c]).Blob(c % 2 == 0 ? "leaf.mid" : "hedge", V(0f, 0.42f, 0f), V(0.72f, 0.55f, 0.7f), 31 + c, 0.18f, 7, 4);
            var rng = new System.Random(900 + c);
            for (int i = 0; i < 9; i++)
            {
                float a = i * 2.4f + (float)rng.NextDouble() * 0.4f, el = 0.15f + (float)rng.NextDouble() * 1.15f;
                var dir = V(Mathf.Cos(a) * Mathf.Cos(el), Mathf.Sin(el), Mathf.Sin(a) * Mathf.Cos(el));
                r.Sphere(i % 4 == 3 ? (cols[c] == "white" ? "flower.yellow" : "flower.white") : "flower." + cols[c], V(dir.x * 0.72f, 0.42f + dir.y * 0.57f, dir.z * 0.7f), V(0.2f, 0.16f, 0.2f), 5, 2, false, V(0f, i * 50f, 0f));
            }
            r.NoShadows();
        }
        // topiary: a clipped cone with a ball on top, in a little terracotta pot
        DefineProp("kit.shrub.cone").Cyl("terracotta", Vector3.zero, 0.26f, 0.28f, 8, 0.32f).Cyl("soil", V(0f, 0.27f, 0f), 0.29f, 0.02f, 8)
            .Lathe("hedge", Vector3.zero, new[] { new Vector2(0.26f, 0.29f), new Vector2(0.47f, 0.45f), new Vector2(0.36f, 0.9f), new Vector2(0.17f, 1.3f), new Vector2(0.05f, 1.5f) }, 8)
            .Blob("hedge.light", V(0f, 1.63f, 0f), V(0.21f, 0.21f, 0.21f), 41, 0.1f, 6, 3).NoShadows();
        // ---- ground plants
        {
            var r = DefineProp("kit.grass.tuft"); var rng = new System.Random(51);
            for (int i = 0; i < 9; i++)
            {
                float a = i * 2.4f, rad = 0.05f + (float)rng.NextDouble() * 0.16f, lean = 0.12f + (float)rng.NextDouble() * 0.3f, h = 0.4f + (float)rng.NextDouble() * 0.45f;
                var b = V(Mathf.Cos(a) * rad, 0f, Mathf.Sin(a) * rad);
                r.Spike(i % 3 == 0 ? "grass.dry" : i % 3 == 1 ? "grass.tuft" : "leaf.lime", b, b + V(Mathf.Cos(a) * lean, h, Mathf.Sin(a) * lean), 0.07f);
            }
            r.NoShadows();
        }
        {
            var r = DefineProp("kit.reeds"); var rng = new System.Random(52);
            for (int i = 0; i < 9; i++)
            {
                float a = i * 2.4f, rad = 0.08f + (float)rng.NextDouble() * 0.38f, h = 1.0f + (float)rng.NextDouble() * 0.9f, lean = ((float)rng.NextDouble() - 0.3f) * 0.3f;
                Vector3 b = V(Mathf.Cos(a) * rad, 0f, Mathf.Sin(a) * rad), tip = b + V(Mathf.Cos(a) * lean, h, Mathf.Sin(a) * lean);
                if (i % 2 == 0) { r.Spike("reed", b, tip, 0.035f); r.Limb("reed.head", Vector3.Lerp(b, tip, 0.72f), Vector3.Lerp(b, tip, 0.95f), 0.055f, 0.05f, 5); }
                else r.Spike(i % 4 == 1 ? "reed" : "leaf.lime", b, b + V(Mathf.Cos(a) * (lean + 0.3f), h * 0.75f, Mathf.Sin(a) * (lean + 0.3f)), 0.07f);
            }
            r.NoShadows();
        }
        foreach (var name in FlowerNames)
        {
            var r = DefineProp("kit.flowers." + name).Blob("leaf.mid", V(0f, 0.06f, 0f), V(0.6f, 0.2f, 0.58f), 61, 0.25f, 7, 3); var rng = new System.Random(62);
            for (int i = 0; i < 10; i++)
            {
                float a = i * 2.4f, rad = i == 0 ? 0f : 0.16f + 0.4f * Mathf.Sqrt(i / 10f);
                Flower(r, FlowerMat(name, i), Mathf.Cos(a) * rad, Mathf.Sin(a) * rad, 0.3f + (float)rng.NextDouble() * 0.2f, 0.15f + (float)rng.NextDouble() * 0.04f);
            }
            r.NoShadows();
        }
        // lily pad: stands on the water surface (spawn at the water level)
        DefineProp("kit.lilypad").Geometry(d =>
        {
            int n = 9; var c = V(0f, 0.025f, 0f);
            for (int i = 0; i < n - 1; i++)
            {
                float a0 = i * Mathf.PI * 2f / n + 0.3f, a1 = (i + 1) * Mathf.PI * 2f / n + 0.3f;
                d.TriOut("lily", V(0f, -100f, 0f), c, V(Mathf.Cos(a0) * 0.36f, 0.025f, Mathf.Sin(a0) * 0.36f), V(Mathf.Cos(a1) * 0.36f, 0.025f, Mathf.Sin(a1) * 0.36f));
            }
        }).Sphere("flower.pink", V(0.08f, 0.1f, -0.05f), V(0.1f, 0.09f, 0.1f), 5, 2).Sphere("flower.white", V(0.08f, 0.16f, -0.05f), V(0.055f, 0.06f, 0.055f), 4, 2).NoShadows();
    }

    // ------------------------------------------------------------------------------------------- run panels
    // All panels: along +Z from 0 to their length, centred on x = 0, base y = 0. Ends are flush and identical so
    // stretched copies tile without a seam.

    static Vector2[] BoxProfile(float halfW, float y0, float y1) { return new[] { new Vector2(-halfW, y0), new Vector2(-halfW, y1), new Vector2(halfW, y1), new Vector2(halfW, y0) }; }

    static void HedgePanel(string id, float len, float width, float height, int seed)
    {
        var recipe = DefineProp(id).Geometry(d =>
        {
            int n = 5; var rng = new System.Random(seed); float hw = width * 0.5f, ch = Mathf.Min(0.18f, height * 0.22f);
            Vector2[] Station(bool end)
            {
                float j() { return end ? 0f : ((float)rng.NextDouble() - 0.5f); }
                float top = height + j() * 0.1f, l = -hw - j() * 0.09f, rr = hw + j() * 0.09f, belly = height * 0.3f;
                // tucked in at the ground, belly out, chamfered shoulders
                return new[] { new Vector2(l * 0.93f, 0f), new Vector2(l * 1.04f, belly + j() * 0.06f), new Vector2(l * 0.97f, top - ch), new Vector2(l + ch, top + j() * 0.04f),
                               new Vector2(rr - ch, top + j() * 0.04f), new Vector2(rr * 0.97f, top - ch), new Vector2(rr * 1.04f, belly + j() * 0.06f), new Vector2(rr * 0.93f, 0f) };
            }
            var prev = Station(true);
            for (int i = 1; i <= n; i++)
            {
                var next = Station(i == n); float z0 = len * (i - 1) / n, z1 = len * i / n;
                var inside = V(0f, height * 0.5f, (z0 + z1) * 0.5f);
                for (int k = 0; k < 7; k++)
                {
                    string mat = k >= 2 && k <= 4 ? "hedge.light" : k == 0 || k == 6 ? "hedge.dark" : "hedge";
                    Vector3 a = V(prev[k].x, prev[k].y, z0), b = V(prev[k + 1].x, prev[k + 1].y, z0), e = V(next[k + 1].x, next[k + 1].y, z1), f = V(next[k].x, next[k].y, z1);
                    if ((i + k) % 2 == 0) { d.TriOut(mat, inside, a, b, e); d.TriOut(mat, inside, a, e, f); } else { d.TriOut(mat, inside, a, b, f); d.TriOut(mat, inside, b, e, f); }
                }
                prev = next;
            }
            // end caps (hidden inside a run, close the two run ends)
            var cap = Station(true);
            for (int k = 0; k < 8; k++)
            {
                int k1 = (k + 1) % 8;       // k == 7 closes the fan along the ground edge
                d.TriOut("hedge", V(0f, height * 0.5f, len * 0.5f), V(0f, height * 0.5f, 0f), V(cap[k].x, cap[k].y, 0f), V(cap[k1].x, cap[k1].y, 0f));
                d.TriOut("hedge", V(0f, height * 0.5f, len * 0.5f), V(0f, height * 0.5f, len), V(cap[k].x, cap[k].y, len), V(cap[k1].x, cap[k1].y, len));
            }
        });
        // a few leafy tufts poking out so it reads as a plant, not a green wall
        float h2 = width * 0.5f;
        recipe.Blob("leaf.mid", V(h2 * 0.92f, height * 0.6f, len * 0.3f), V(0.15f, 0.2f, 0.3f), seed + 1, 0.2f, 6, 3)
              .Blob("hedge.light", V(-h2 * 0.92f, height * 0.5f, len * 0.72f), V(0.15f, 0.2f, 0.3f), seed + 2, 0.2f, 6, 3);
    }

    static void DefinePanels()
    {
        HedgePanel("kit.panel.hedge.low", 2f, 0.62f, 0.7f, 71);
        HedgePanel("kit.panel.hedge.mid", 2f, 0.78f, 1.2f, 72);
        HedgePanel("kit.panel.hedge.tall", 2f, 0.92f, 1.8f, 73);

        foreach (var name in FlowerNames)
        {
            var r = DefineProp("kit.panel.flowerborder." + name);
            r.Prism("soil", new[] { new Vector2(-0.35f, 0f), new Vector2(-0.3f, 0.1f), new Vector2(0.3f, 0.1f), new Vector2(0.35f, 0f) }, 0f, 2f);
            r.Blob("leaf.mid", V(-0.05f, 0.12f, 0.42f), V(0.27f, 0.17f, 0.36f), 81, 0.2f, 6, 3).Blob("hedge", V(0.06f, 0.12f, 1.02f), V(0.26f, 0.2f, 0.32f), 82, 0.2f, 6, 3).Blob("leaf.light", V(-0.04f, 0.12f, 1.6f), V(0.27f, 0.16f, 0.34f), 83, 0.2f, 6, 3);
            var rng = new System.Random(84);
            for (int i = 0; i < 10; i++)
            {
                float z = 0.1f + 0.2f * i, x = (i % 2 == 0 ? -0.15f : 0.15f) + ((float)rng.NextDouble() - 0.5f) * 0.14f;
                Flower(r, FlowerMat(name, i), x, z, 0.33f + (float)rng.NextDouble() * 0.17f, 0.15f + (float)rng.NextDouble() * 0.035f, i % 2 == 0);
            }
            r.NoShadows();
        }

        // white picket: two rails and eight pointed pales per 2 m (posts: kit.post.white)
        DefineProp("kit.panel.picket.white").Box("paint.white", V(-0.02f, 0.38f, 1f), V(0.04f, 0.08f, 2f)).Box("paint.white", V(-0.02f, 0.88f, 1f), V(0.04f, 0.08f, 2f)).Geometry(d =>
        {
            for (int i = 0; i < 8; i++)
            {
                float zc = (i + 0.5f) * 0.25f, hw = 0.062f, x0 = 0f, x1 = 0.03f, yb = 0.08f, ys = 1.02f, yt = 1.14f; var inside = V(0.015f, 0.6f, zc);
                Vector3[] f = { V(x1, yb, zc - hw), V(x1, ys, zc - hw), V(x1, yt, zc), V(x1, ys, zc + hw), V(x1, yb, zc + hw) }, b = new Vector3[5];
                for (int k = 0; k < 5; k++) { b[k] = f[k]; b[k].x = x0; }
                d.TriOut("paint.white", inside, f[0], f[1], f[3]); d.TriOut("paint.white", inside, f[0], f[3], f[4]); d.TriOut("paint.white", inside, f[1], f[2], f[3]);
                d.TriOut("paint.white", inside, b[0], b[1], b[3]); d.TriOut("paint.white", inside, b[0], b[3], b[4]); d.TriOut("paint.white", inside, b[1], b[2], b[3]);
                for (int k = 0; k < 4; k++) d.QuadOut("paint.white", inside, f[k], f[k + 1], b[k + 1], b[k]);
            }
        }).NoShadows();

        // park knee rail: a diamond-set timber rail on a stub post at the start of every panel (end post: kit.post.knee)
        DefineProp("kit.panel.rail.knee").Box("wood.warm", V(0f, 0.4f, 1f), V(0.1f, 0.1f, 2f), V(0f, 0f, 45f)).Cyl("wood.dark", Vector3.zero, 0.065f, 0.44f, 4).NoShadows();
        // two-rail timber fence (posts: kit.post.wood at every joint)
        DefineProp("kit.panel.rail.post").Box("wood.warm", V(0.02f, 0.86f, 1.25f), V(0.05f, 0.13f, 2.5f)).Box("wood.warm", V(0.02f, 0.46f, 1.25f), V(0.05f, 0.13f, 2.5f)).NoShadows();

        // low dry-stone wall with irregular cap stones
        {
            var r = DefineProp("kit.panel.wall.stone").Prism("stone.dark", new[] { new Vector2(-0.22f, 0f), new Vector2(-0.19f, 0.48f), new Vector2(0.19f, 0.48f), new Vector2(0.22f, 0f) }, 0f, 2f);
            float[] hs = { 0.13f, 0.1f, 0.15f, 0.11f }; string[] ms = { "stone.light", "stone.warm", "stone.light", "stone.moss" };
            for (int i = 0; i < 4; i++) r.Box(ms[i], V(i % 2 == 0 ? 0.01f : -0.01f, 0.48f + hs[i] * 0.5f, 0.25f + i * 0.5f), V(0.46f, hs[i], 0.5f));
            r.Box("stone.warm", V(0.205f, 0.2f, 0.45f), V(0.04f, 0.16f, 0.36f)).Box("stone.light", V(0.2f, 0.34f, 1.35f), V(0.04f, 0.14f, 0.42f)).Box("stone.light", V(-0.205f, 0.2f, 1.5f), V(0.04f, 0.16f, 0.36f)).Box("stone.warm", V(-0.2f, 0.34f, 0.6f), V(0.04f, 0.14f, 0.42f));
        }
        // garden brick wall with a stone coping (piers: kit.post.stone)
        DefineProp("kit.panel.wall.brick").Box("brick.dark", V(0f, 0.07f, 1f), V(0.3f, 0.14f, 2f)).Box("brick", V(0f, 0.52f, 1f), V(0.24f, 0.76f, 2f)).Box("brick.dark", V(0f, 0.6f, 1f), V(0.25f, 0.06f, 2f))
            .Prism("stone.light", new[] { new Vector2(-0.17f, 0.9f), new Vector2(-0.17f, 0.96f), new Vector2(-0.08f, 1.02f), new Vector2(0.08f, 1.02f), new Vector2(0.17f, 0.96f), new Vector2(0.17f, 0.9f) }, 0f, 2f);
        // stone kerb: two chunky bevelled stones per panel
        DefineProp("kit.panel.kerb.stone").Prism("stone.light", new[] { new Vector2(-0.14f, 0f), new Vector2(-0.12f, 0.16f), new Vector2(-0.07f, 0.21f), new Vector2(0.07f, 0.21f), new Vector2(0.12f, 0.16f), new Vector2(0.14f, 0f) }, 0f, 1f)
            .Prism("stone.warm", new[] { new Vector2(-0.14f, 0f), new Vector2(-0.12f, 0.14f), new Vector2(-0.07f, 0.185f), new Vector2(0.07f, 0.185f), new Vector2(0.12f, 0.14f), new Vector2(0.14f, 0f) }, 1f, 2f).NoShadows();
        // beach / dune fence: wonky grey slats on two wires
        {
            var r = DefineProp("kit.panel.beachfence"); var rng = new System.Random(91);
            for (int i = 0; i < 8; i++)
            {
                float z = (i + 0.5f) * 0.25f, h = 0.85f + (float)rng.NextDouble() * 0.3f, lean = ((float)rng.NextDouble() - 0.5f) * 12f;
                r.Box(i % 3 == 0 ? "wood.pale" : "wood.grey", V(0f, h * 0.5f, z), V(0.03f, h, 0.1f), V(lean, 0f, ((float)rng.NextDouble() - 0.5f) * 6f));
            }
            r.Box("metal.dark", V(0.02f, 0.3f, 1f), V(0.015f, 0.02f, 2f)).Box("metal.dark", V(0.02f, 0.72f, 1f), V(0.015f, 0.02f, 2f)).NoShadows();
        }

        // posts
        DefineProp("kit.post.wood").Cyl("wood.dark", Vector3.zero, 0.075f, 1.08f, 4).Cone("wood.dark", V(0f, 1.08f, 0f), 0.075f, 0.1f, 4).NoShadows();
        DefineProp("kit.post.knee").Cyl("wood.dark", Vector3.zero, 0.065f, 0.44f, 4).Cone("wood.dark", V(0f, 0.44f, 0f), 0.065f, 0.06f, 4).NoShadows();
        DefineProp("kit.post.white").Cyl("paint.white", Vector3.zero, 0.06f, 1.2f, 4).Cyl("paint.white", V(0f, 1.2f, 0f), 0.08f, 0.04f, 4).Cone("paint.white", V(0f, 1.24f, 0f), 0.07f, 0.1f, 4).NoShadows();
        DefineProp("kit.post.stone").Cyl("stone.warm", Vector3.zero, 0.26f, 0.12f, 4).Cyl("brick", V(0f, 0.12f, 0f), 0.21f, 0.98f, 4).Cyl("stone.light", V(0f, 1.1f, 0f), 0.27f, 0.09f, 4).Cone("stone.light", V(0f, 1.19f, 0f), 0.2f, 0.12f, 4).Sphere("stone.light", V(0f, 1.38f, 0f), V(0.11f, 0.11f, 0.11f), 6, 4);
        DefineProp("kit.post.gate").Cyl("wood.dark", Vector3.zero, 0.1f, 1.75f, 4).Cyl("wood.warm", V(0f, 1.75f, 0f), 0.13f, 0.06f, 4).Cone("wood.warm", V(0f, 1.81f, 0f), 0.1f, 0.1f, 4).Sphere("wood.pale", V(0f, 1.97f, 0f), V(0.09f, 0.09f, 0.09f), 6, 4);
    }

    // --------------------------------------------------------------------------------------------- furniture

    static void DefineFurniture()
    {
        // googly eye pair: origin between the eyes, looking along +Z, ~0.55 m wide at scale 1. Bake onto anything.
        DefineProp("kit.googly")
            .Sphere("eye.white", V(-0.14f, 0f, 0.02f), V(0.135f, 0.135f, 0.07f), 8, 4, true).Sphere("eye.white", V(0.14f, 0.01f, 0.02f), V(0.15f, 0.15f, 0.075f), 8, 4, true)
            .Sphere("eye.black", V(-0.105f, -0.045f, 0.075f), V(0.062f, 0.062f, 0.03f), 6, 2, true).Sphere("eye.black", V(0.185f, 0.05f, 0.085f), V(0.07f, 0.07f, 0.03f), 6, 2, true).NoShadows();

        // park bench: green iron ends, timber slats. Sitters face +Z.
        {
            var r = DefineProp("kit.bench.park");
            foreach (float x in new[] { -0.82f, 0.82f })
            {
                r.Box("metal.green", V(x, 0.22f, 0.2f), V(0.06f, 0.44f, 0.06f)).Bar("metal.green", V(x, 0f, -0.2f), V(x, 0.95f, -0.34f), 0.06f)
                 .Bar("metal.green", V(x, 0.42f, -0.24f), V(x, 0.42f, 0.24f), 0.05f).Bar("metal.green", V(x, 0.64f, -0.3f), V(x, 0.64f, 0.26f), 0.05f).Box("metal.green", V(x, 0.53f, 0.22f), V(0.05f, 0.2f, 0.05f));
            }
            for (int i = 0; i < 3; i++) r.Box(i == 1 ? "wood.pale" : "wood.warm", V(0f, 0.46f, -0.14f + i * 0.16f), V(1.8f, 0.045f, 0.13f));
            r.Box("wood.warm", V(0f, 0.66f, -0.285f), V(1.8f, 0.13f, 0.04f), V(-8f, 0f, 0f)).Box("wood.pale", V(0f, 0.84f, -0.31f), V(1.8f, 0.13f, 0.04f), V(-8f, 0f, 0f));
            r.ColliderBox(V(0f, 0.45f, -0.05f), V(1.8f, 0.9f, 0.6f));
        }
        DefineProp("kit.bench.simple").Box("wood.warm", V(0f, 0.45f, 0f), V(1.7f, 0.08f, 0.42f)).Box("wood.dark", V(-0.62f, 0.2f, 0f), V(0.12f, 0.42f, 0.36f)).Box("wood.dark", V(0.62f, 0.2f, 0f), V(0.12f, 0.42f, 0.36f))
            .ColliderBox(V(0f, 0.25f, 0f), V(1.7f, 0.5f, 0.42f));
        // picnic table with attached benches
        {
            var r = DefineProp("kit.picnic.table");
            for (int i = 0; i < 3; i++) r.Box(i == 1 ? "wood.pale" : "wood.warm", V(0f, 0.76f, -0.27f + i * 0.27f), V(1.9f, 0.05f, 0.25f));
            foreach (float z in new[] { -0.72f, 0.72f }) r.Box("wood.warm", V(0f, 0.44f, z), V(1.9f, 0.05f, 0.28f));
            foreach (float x in new[] { -0.68f, 0.68f })
                r.Bar("wood.dark", V(x, 0f, -0.62f), V(x, 0.74f, -0.22f), 0.08f).Bar("wood.dark", V(x, 0f, 0.62f), V(x, 0.74f, 0.22f), 0.08f).Bar("wood.dark", V(x, 0.39f, -0.84f), V(x, 0.39f, 0.84f), 0.08f);
            r.ColliderBox(V(0f, 0.4f, 0f), V(1.9f, 0.8f, 1.7f));
        }
        DefineProp("kit.bin.park").Cyl("metal.dark", Vector3.zero, 0.22f, 0.08f, 8).Cyl("wood.warm", V(0f, 0.08f, 0f), 0.27f, 0.72f, 8, 0.31f).Cyl("metal.green", V(0f, 0.4f, 0f), 0.3f, 0.05f, 8)
            .Cyl("metal.green", V(0f, 0.8f, 0f), 0.34f, 0.07f, 8).Cyl("metal.dark", V(0f, 0.87f, 0f), 0.24f, 0.01f, 8).NoShadows()
            .ColliderBox(V(0f, 0.45f, 0f), V(0.6f, 0.9f, 0.6f));
        // decorative park lamp (not lit by the night system; for real light use SM_streetlamp prefabs)
        DefineProp("kit.lamp.park").Cyl("metal.green", Vector3.zero, 0.17f, 0.12f, 8).Cyl("metal.green", V(0f, 0.12f, 0f), 0.12f, 0.5f, 8, 0.075f).Cyl("metal.green", V(0f, 0.62f, 0f), 0.055f, 2.6f, 6)
            .Cyl("metal.green", V(0f, 3.2f, 0f), 0.1f, 0.06f, 8, 0.17f).Cyl("lamp.glow", V(0f, 3.26f, 0f), 0.15f, 0.4f, 6, 0.23f).Cone("metal.green", V(0f, 3.66f, 0f), 0.33f, 0.2f, 6).Sphere("metal.green", V(0f, 3.9f, 0f), V(0.06f, 0.07f, 0.06f), 6, 4)
            .Bar("metal.green", V(-0.28f, 2.85f, 0f), V(0.28f, 2.85f, 0f), 0.04f)
            .ColliderBox(V(0f, 1.8f, 0f), V(0.3f, 3.6f, 0.3f));
        DefineProp("kit.boulder.s").Blob("stone.dark", V(0f, 0.16f, 0f), V(0.34f, 0.26f, 0.28f), 401, 0.22f, 6, 4).NoShadows().ColliderFit(0.06f);
        DefineProp("kit.boulder.m").Blob("stone.dark", V(0f, 0.3f, 0f), V(0.62f, 0.48f, 0.52f), 402, 0.22f, 7, 4).Blob("stone.light", V(0.3f, 0.2f, 0.32f), V(0.34f, 0.26f, 0.3f), 403, 0.22f, 6, 3).ColliderFit(0.12f);
        DefineProp("kit.boulder.l").Blob("stone.dark", V(0f, 0.55f, 0f), V(1.1f, 0.85f, 0.9f), 404, 0.22f, 8, 5).Blob("stone.light", V(0.75f, 0.32f, 0.5f), V(0.55f, 0.42f, 0.5f), 405, 0.22f, 6, 4).Blob("stone.moss", V(-0.2f, 1.12f, 0f), V(0.6f, 0.2f, 0.5f), 406, 0.2f, 6, 3).ColliderFit(0.25f);
        // half-barrel planter overflowing with flowers
        {
            var r = DefineProp("kit.planter.tub").Cyl("wood.warm", Vector3.zero, 0.4f, 0.46f, 10, 0.5f).Cyl("metal.dark", V(0f, 0.09f, 0f), 0.428f, 0.04f, 10, 0.436f).Cyl("metal.dark", V(0f, 0.32f, 0f), 0.478f, 0.04f, 10, 0.486f)
                .Cyl("soil", V(0f, 0.44f, 0f), 0.46f, 0.03f, 10).Blob("leaf.mid", V(0f, 0.52f, 0f), V(0.5f, 0.2f, 0.5f), 411, 0.2f, 7, 3);
            for (int i = 0; i < 8; i++) { float a = i * 2.4f, rad = i == 0 ? 0f : 0.15f + 0.26f * Mathf.Sqrt(i / 8f); Flower(r, MixedFlowers[i % 7], Mathf.Cos(a) * rad, Mathf.Sin(a) * rad, 0.76f + (i % 3) * 0.07f, 0.16f, false); }
            r.NoShadows().ColliderBox(V(0f, 0.3f, 0f), V(0.9f, 0.6f, 0.9f));
        }
        DefineProp("kit.bollard").Cyl("metal.green", Vector3.zero, 0.15f, 0.1f, 8).Cyl("metal.green", V(0f, 0.1f, 0f), 0.11f, 0.62f, 8, 0.09f).Cyl("paint.white", V(0f, 0.5f, 0f), 0.105f, 0.1f, 8, 0.1f).Cyl("metal.green", V(0f, 0.72f, 0f), 0.13f, 0.05f, 8)
            .Sphere("paint.yellow", V(0f, 0.86f, 0f), V(0.11f, 0.11f, 0.11f), 8, 5).NoShadows().ColliderBox(V(0f, 0.45f, 0f), V(0.3f, 0.9f, 0.3f));
        // finger post: three boards pointing different ways (no text; add a Label if you want one)
        DefineProp("kit.signpost").Cyl("wood.dark", Vector3.zero, 0.06f, 2.7f, 4).Cone("wood.dark", V(0f, 2.7f, 0f), 0.06f, 0.1f, 4).Cyl("stone.light", Vector3.zero, 0.16f, 0.1f, 6)
            .Geometry(d =>
            {
                float[] yaws = { 0f, 115f, 250f }; float[] ys = { 2.42f, 2.14f, 1.86f }; string[] ms = { "paint.white", "paint.cream", "paint.white" };
                for (int i = 0; i < 3; i++)
                {
                    var m = Matrix4x4.TRS(V(0f, ys[i], 0f), Quaternion.Euler(0f, yaws[i], 0f), Vector3.one); var inside = m.MultiplyPoint3x4(V(0f, 0f, 0.5f));
                    Vector3 P(float y, float z, float x) { return m.MultiplyPoint3x4(V(x, y, z)); }
                    float t = 0.025f, h = 0.1f, z0 = 0.05f, z1 = 0.95f, z2 = 1.12f;
                    foreach (float x in new[] { -t, t }) { d.TriOut(ms[i], inside, P(-h, z0, x), P(h, z0, x), P(h, z1, x)); d.TriOut(ms[i], inside, P(-h, z0, x), P(h, z1, x), P(-h, z1, x)); d.TriOut(ms[i], inside, P(-h, z1, x), P(h, z1, x), P(0f, z2, x)); }
                    d.QuadOut(ms[i], inside, P(h, z0, -t), P(h, z1, -t), P(h, z1, t), P(h, z0, t)); d.QuadOut(ms[i], inside, P(-h, z0, -t), P(-h, z1, -t), P(-h, z1, t), P(-h, z0, t));
                    d.QuadOut(ms[i], inside, P(h, z1, -t), P(0f, z2, -t), P(0f, z2, t), P(h, z1, t)); d.QuadOut(ms[i], inside, P(-h, z1, -t), P(0f, z2, -t), P(0f, z2, t), P(-h, z1, t));
                }
            }).ColliderBox(V(0f, 1.3f, 0f), V(0.2f, 2.6f, 0.2f));
        // bird bath with a tiny derpy bird on the rim
        DefineProp("kit.birdbath").Cyl("stone.light", Vector3.zero, 0.3f, 0.08f, 8, 0.26f).Cyl("stone.light", V(0f, 0.08f, 0f), 0.1f, 0.58f, 8, 0.075f).Cyl("stone.light", V(0f, 0.66f, 0f), 0.1f, 0.18f, 10, 0.46f)
            .Cyl("water", V(0f, 0.835f, 0f), 0.39f, 0.012f, 10).Torus("stone.light", V(0f, 0.84f, 0f), 0.43f, 0.04f, 10, 4)
            .Sphere("paint.blue", V(0.38f, 0.96f, 0.1f), V(0.085f, 0.08f, 0.11f), 6, 4).Sphere("paint.blue", V(0.38f, 1.06f, 0.17f), V(0.065f, 0.065f, 0.065f), 6, 4).Spike("duck.orange", V(0.38f, 1.05f, 0.22f), V(0.38f, 1.04f, 0.31f), 0.022f)
            .Spike("paint.blue", V(0.38f, 0.97f, 0.02f), V(0.38f, 1.04f, -0.1f), 0.04f).Add("kit.googly", V(0.38f, 1.09f, 0.215f), 0f, 0.2f)
            .NoShadows().ColliderBox(V(0f, 0.45f, 0f), V(0.7f, 0.9f, 0.7f));
        // garden gnome
        DefineProp("kit.gnome").Box("bark.dark", V(-0.07f, 0.03f, 0.03f), V(0.1f, 0.06f, 0.16f)).Box("bark.dark", V(0.07f, 0.03f, 0.03f), V(0.1f, 0.06f, 0.16f))
            .Cyl("paint.blue", V(0f, 0.05f, 0f), 0.17f, 0.3f, 8, 0.13f).Cyl("bark.dark", V(0f, 0.14f, 0f), 0.165f, 0.04f, 8, 0.16f)
            .Sphere("skin", V(0f, 0.43f, 0f), V(0.125f, 0.12f, 0.12f), 8, 5)
            .Limb("paint.white", V(0f, 0.43f, 0.07f), V(0f, 0.2f, 0.14f), 0.12f, 0.02f, 6, 0)
            .Sphere("skin", V(0f, 0.43f, 0.125f), V(0.04f, 0.04f, 0.045f), 5, 3)
            .Limb("paint.red", V(0f, 0.49f, -0.01f), V(0.03f, 0.92f, -0.06f), 0.155f, 0.0f, 8, 1)
            .Limb("paint.blue", V(-0.15f, 0.31f, 0f), V(-0.21f, 0.14f, 0.05f), 0.045f, 0.04f, 5).Limb("paint.blue", V(0.15f, 0.31f, 0f), V(0.24f, 0.4f, 0.08f), 0.045f, 0.04f, 5)
            .Add("kit.googly", V(0f, 0.475f, 0.105f), 0f, 0.26f).NoShadows();
        // derpy duck: sits on the surface it is spawned on (water level or lawn)
        DefineProp("kit.duck").Sphere("duck.yellow", V(0f, 0.19f, -0.03f), V(0.2f, 0.18f, 0.29f), 8, 5).Spike("duck.yellow", V(0f, 0.24f, -0.24f), V(0f, 0.43f, -0.4f), 0.09f)
            .Sphere("duck.yellow", V(0f, 0.5f, 0.15f), V(0.16f, 0.155f, 0.16f), 8, 5)
            .Sphere("duck.orange", V(0f, 0.455f, 0.33f), V(0.085f, 0.03f, 0.11f), 6, 2).Sphere("duck.orange", V(0f, 0.425f, 0.31f), V(0.07f, 0.025f, 0.08f), 6, 2)
            .Sphere("duck.white", V(-0.2f, 0.2f, -0.05f), V(0.04f, 0.1f, 0.17f), 6, 3).Sphere("duck.white", V(0.2f, 0.2f, -0.05f), V(0.04f, 0.1f, 0.17f), 6, 3)
            .Box("duck.orange", V(-0.08f, 0.012f, 0.06f), V(0.1f, 0.024f, 0.14f)).Box("duck.orange", V(0.08f, 0.012f, 0.06f), V(0.1f, 0.024f, 0.14f))
            .Add("kit.googly", V(0f, 0.57f, 0.27f), 0f, 0.5f).NoShadows();
    }
}
