// Owner: park agent. Contract: design/CONTRACT.md.
using System;
using System.Collections.Generic;
using UnityEngine;
using Kit = TownLifeKit;
using Occ = TownLifeOccupancy;

/// <summary>Pickle Park (DESIGN section 8 'Park'). Group: ctx.Group("Park").
/// The layout is data (the Plan block below); everything is derived from the block rect, the promenade axis (the two
/// guarded crossings) and the surviving playground pen, so it follows the scene if those move a little.</summary>
public static class TownLifePark
{
    /// <summary>Module name used in options, reports and RNG keys.</summary>
    public const string Name = "park";

    const string LampPrefab = "Assets/Suburbs Town Pack/PREFABS/Props/SM_streetlamp_03";
    const string Playground = "Assets/Suburbs Town Pack/PREFABS/Playground/";
    const float GrassY = TownLifeContext.GrassY, LawnY = TownLifeContext.LawnY, SoilY = TownLifeContext.SoilY, PavingY = TownLifeContext.PavingY, MarkingY = TownLifeContext.MarkingY;
    const string TagTree = "tree.park", TagShrub = "shrub.park", TagFurn = "furniture.park", TagStruct = "structure.park";

    /// <summary>Builds this module into ctx.</summary>
    public static void Build(TownLifeContext ctx)
    {
        if (!ctx.options.District(TownLifeScene.Acres)) return;
        if (ctx.scene.park == null) { ctx.Error(Name, "no park in the scene snapshot"); return; }
        new Builder(ctx).Run();
    }

    // =================================================================================================== fallback props
    // The stock kit library (DESIGN section 10) is delivered by the kit agent. Until an id exists the park draws a
    // simple stand-in with the same footprint so the module always runs.

    static float PanelLength(string kitId) { return kitId == "kit.panel.rail.post" ? 2.5f : 2f; }

    static string K(string id)
    {
        if (Kit.HasProp(id)) return id;
        string fb = "park.fb." + id.Substring(id.IndexOf('.') + 1);
        if (!Kit.HasProp(fb)) DefineFallback(fb, id);
        return fb;
    }

    static Vector3 V(float x, float y, float z) { return new Vector3(x, y, z); }
    static Vector3 V(float x, float z) { return new Vector3(x, 0f, z); }

    static void DefineFallback(string fb, string id)
    {
        var p = id.Split('.'); string kind = p.Length > 1 ? p[1] : "", sub = p.Length > 2 ? p[2] : "", sz = p.Length > 3 ? p[3] : "";
        var r = Kit.DefineProp(fb);
        int seed = TownLifeContext.StableHash(id) & 0xffff;
        if (kind == "tree")
        {
            float h = sz == "s" ? 3.6f : sz == "l" ? 9f : sz == "xl" ? 12f : 6f;
            string leaf = sub == "maple" ? "leaf.autumn" : sub == "blossom" ? "leaf.blossom" : sub == "birch" ? "leaf.lime" : sub == "oak" ? "leaf.dark" : "leaf.mid";
            string leaf2 = sub == "maple" ? "paint.yellow" : sub == "blossom" ? "paint.white" : sub == "oak" ? "leaf.mid" : sub == "birch" ? "leaf.mid" : "leaf.dark";
            string bark = sub == "birch" ? "bark.birch" : "bark";
            float tr = Mathf.Max(0.16f, h * 0.035f);
            if (sub == "pine")
            {
                r.Cyl("bark", Vector3.zero, tr, h * 0.3f, 7, tr * 0.7f);
                r.Cone("leaf.dark", V(0, h * 0.2f, 0), h * 0.26f, h * 0.4f, 8).Cone("leaf.dark", V(0, h * 0.42f, 0), h * 0.2f, h * 0.36f, 8).Cone("leaf.mid", V(0, h * 0.64f, 0), h * 0.13f, h * 0.36f, 8);
            }
            else if (sub == "pickle")
            {
                r.Cyl("bark", Vector3.zero, tr, h * 0.2f, 7);
                r.Blob("park.pickle", V(0, h * 0.52f, 0), V(h * 0.15f, h * 0.4f, h * 0.15f), seed, 0.1f, 8, 6).Cone("park.pickle.dark", V(0, h * 0.8f, 0), h * 0.09f, h * 0.24f, 8);
            }
            else if (sub == "willow")
            {
                r.Cyl("bark", Vector3.zero, tr * 1.2f, h * 0.5f, 7, tr * 0.8f);
                r.Blob("leaf.lime", V(0, h * 0.68f, 0), V(h * 0.42f, h * 0.24f, h * 0.42f), seed, 0.12f, 10, 5);
                for (int i = 0; i < 9; i++) { float a = i * Mathf.PI * 2f / 9f; r.Blob("leaf.lime", V(Mathf.Cos(a) * h * 0.36f, h * 0.42f, Mathf.Sin(a) * h * 0.36f), V(h * 0.1f, h * 0.3f, h * 0.1f), seed + i, 0.15f, 6, 4); }
            }
            else
            {
                float wide = sub == "oak" ? 0.42f : sub == "elm" ? 0.3f : sub == "birch" ? 0.22f : 0.33f, tall = sub == "elm" ? 0.34f : sub == "birch" ? 0.3f : 0.27f;
                r.Cyl(bark, Vector3.zero, tr, h * 0.5f, 7, tr * 0.6f);
                r.Blob(leaf, V(0, h * 0.7f, 0), V(h * wide, h * tall, h * wide), seed);
                r.Blob(leaf2, V(h * 0.16f, h * 0.56f, h * 0.1f), V(h * wide * 0.62f, h * tall * 0.6f, h * wide * 0.62f), seed + 1);
                r.Blob(leaf, V(-h * 0.15f, h * 0.6f, -h * 0.12f), V(h * wide * 0.6f, h * tall * 0.62f, h * wide * 0.6f), seed + 2);
                if (sub == "fruit") for (int i = 0; i < 8; i++) { float a = i * 2.4f; r.Sphere("paint.red", V(Mathf.Cos(a) * h * 0.3f, h * (0.55f + 0.03f * (i % 4)), Mathf.Sin(a) * h * 0.3f), Vector3.one * 0.14f, 6, 4); }
            }
            r.ColliderTrunk(Mathf.Max(0.3f, tr * 1.3f), 3f);
        }
        else if (kind == "shrub")
        {
            if (sub == "cone") r.Cone("leaf.dark", Vector3.zero, 0.55f, 1.7f, 8);
            else if (sub == "flower")
            {
                r.Blob("leaf.mid", V(0, 0.45f, 0), V(0.7f, 0.55f, 0.7f), seed);
                for (int i = 0; i < 9; i++) { float a = i * 2.4f, rr = 0.25f + 0.05f * (i % 4); r.Sphere(Petal(sz), V(Mathf.Cos(a) * rr * 2f, 0.6f + 0.35f * Mathf.Cos(i * 0.9f) * 0.5f + 0.2f, Mathf.Sin(a) * rr * 2f), Vector3.one * 0.14f, 5, 3); }
            }
            else { float s = sub == "round" && sz == "s" ? 0.7f : 1.1f; r.Blob("leaf.dark", V(0, 0.5f * s, 0), V(0.75f * s, 0.6f * s, 0.75f * s), seed); r.Blob("leaf.mid", V(0.3f * s, 0.6f * s, 0.2f * s), V(0.45f * s, 0.4f * s, 0.45f * s), seed + 3); }
            r.NoShadows();
        }
        else if (kind == "flowers")
        {
            r.Dome("leaf.mid", Vector3.zero, V(0.6f, 0.18f, 0.6f), 8, 2);
            for (int i = 0; i < 9; i++) { float a = i * 2.4f, rr = 0.12f + 0.055f * i; r.Sphere(Petal(sub == "mixed" ? new[] { "red", "yellow", "pink", "purple", "white" }[i % 5] : sub), V(Mathf.Cos(a) * rr, 0.26f + 0.05f * (i % 3), Mathf.Sin(a) * rr), V(0.11f, 0.08f, 0.11f), 5, 3); }
            r.NoShadows();
        }
        else if (kind == "grass") { for (int i = 0; i < 6; i++) { float a = i * 1.05f; r.Cone("leaf.lime", V(Mathf.Cos(a) * 0.14f, 0, Mathf.Sin(a) * 0.14f), 0.07f, 0.4f + 0.08f * (i % 3), 4, V(Mathf.Sin(a) * 14f, 0, -Mathf.Cos(a) * 14f)); } r.NoShadows(); }
        else if (kind == "reeds")
        {
            for (int i = 0; i < 7; i++) { float a = i * 2.4f, rr = 0.1f + 0.05f * i, hh = 1.0f + 0.12f * (i % 4); var b = V(Mathf.Cos(a) * rr, 0, Mathf.Sin(a) * rr); r.Cyl("leaf.lime", b, 0.03f, hh, 4); if (i % 2 == 0) r.Cyl("wood.dark", b + Vector3.up * (hh - 0.05f), 0.055f, 0.26f, 6); }
            r.NoShadows();
        }
        else if (kind == "lilypad") { r.Cyl("leaf.mid", Vector3.zero, 0.38f, 0.025f, 9).Cyl("leaf.lime", V(0.5f, 0, 0.3f), 0.26f, 0.025f, 8).Sphere("leaf.blossom", V(0.05f, 0.08f, 0), V(0.11f, 0.08f, 0.11f), 6, 3).NoShadows(); }
        else if (kind == "panel")
        {
            float L = PanelLength(id);
            if (sub == "hedge")
            {
                float h = sz == "low" ? 0.7f : sz == "tall" ? 1.8f : 1.2f;
                r.Box("park.hedge", V(0, h * 0.48f, L * 0.5f), V(0.72f, h * 0.96f, L));
                r.Blob("park.hedge.top", V(0, h * 0.92f, L * 0.27f), V(0.4f, 0.16f, L * 0.3f), seed, 0.25f, 6, 3).Blob("park.hedge.top", V(0.03f, h * 0.93f, L * 0.75f), V(0.4f, 0.17f, L * 0.28f), seed + 1, 0.25f, 6, 3);
            }
            else if (sub == "flowerborder")
            {
                r.Box("soil", V(0, 0.05f, L * 0.5f), V(0.72f, 0.1f, L));
                var cols = new[] { "red", "yellow", "pink", "purple", "white" };
                for (int i = 0; i < 7; i++)
                {
                    var c = V(((i * 37) % 5 - 2) * 0.12f, 0, (i + 0.5f) * L / 7f);
                    r.Dome("leaf.mid", c + Vector3.up * 0.08f, V(0.2f, 0.2f, 0.17f), 6, 2);
                    r.Sphere(Petal(sz == "mixed" ? cols[i % 5] : sz), c + V(0.04f, 0.34f, 0), V(0.13f, 0.1f, 0.13f), 5, 3);
                    r.Sphere(Petal(sz == "mixed" ? cols[(i + 2) % 5] : sz), c + V(-0.12f, 0.27f, 0.08f), V(0.1f, 0.08f, 0.1f), 5, 3);
                }
                r.NoShadows();
            }
            else if (sub == "rail") { float h = sz == "knee" ? 0.45f : 1f; r.Cyl("wood.dark", Vector3.zero, 0.06f, h, 4).Bar("wood.warm", V(0, h - 0.06f, 0), V(0, h - 0.06f, L), 0.09f); if (sz != "knee") r.Bar("wood.warm", V(0, h * 0.5f, 0), V(0, h * 0.5f, L), 0.08f); r.NoShadows(); }
            else if (sub == "kerb") r.Box("stone.light", V(0, 0.1f, L * 0.5f), V(0.25f, 0.2f, L)).NoShadows();
            else if (sub == "wall") r.Box(sz == "brick" ? "paint.red" : "stone.light", V(0, sz == "brick" ? 0.5f : 0.3f, L * 0.5f), V(0.35f, sz == "brick" ? 1f : 0.6f, L));
            else { r.Bar("paint.white", V(0, 0.75f, 0), V(0, 0.75f, L), 0.07f).Bar("paint.white", V(0, 0.35f, 0), V(0, 0.35f, L), 0.07f); for (int i = 0; i < 6; i++) r.Box("paint.white", V(0.03f, 0.55f, (i + 0.5f) * L / 6f), V(0.03f, 1.1f, 0.12f)); r.NoShadows(); }
        }
        else if (kind == "post") r.Cyl(sub == "white" ? "paint.white" : sub == "stone" ? "stone.light" : "wood.dark", Vector3.zero, sub == "stone" ? 0.18f : 0.08f, sub == "gate" ? 1.6f : 1.1f, 4).NoShadows();
        else if (kind == "bench")
        {
            for (int i = 0; i < 3; i++) r.Box("wood.warm", V(0, 0.46f, -0.16f + i * 0.17f), V(1.8f, 0.05f, 0.14f));
            for (int i = 0; i < 2; i++) r.Box("wood.warm", V(0, 0.68f + i * 0.2f, -0.3f), V(1.8f, 0.14f, 0.05f), V(-10, 0, 0));
            foreach (float x in new[] { -0.75f, 0.75f }) r.Box("metal.dark", V(x, 0.23f, -0.05f), V(0.07f, 0.46f, 0.5f)).Box("metal.dark", V(x, 0.7f, -0.33f), V(0.07f, 0.55f, 0.07f));
            r.ColliderBox(V(0, 0.45f, -0.05f), V(1.8f, 0.9f, 0.55f)).NoShadows();
        }
        else if (kind == "picnic")
        {
            r.Box("wood.warm", V(0, 0.76f, 0), V(1.9f, 0.07f, 0.8f));
            foreach (float z in new[] { -0.72f, 0.72f }) r.Box("wood.warm", V(0, 0.45f, z), V(1.9f, 0.06f, 0.28f));
            foreach (float x in new[] { -0.7f, 0.7f }) r.Bar("wood.dark", V(x, 0, -0.75f), V(x, 0.76f, 0.2f), 0.09f).Bar("wood.dark", V(x, 0, 0.75f), V(x, 0.76f, -0.2f), 0.09f).Bar("wood.dark", V(x, 0.42f, -0.8f), V(x, 0.42f, 0.8f), 0.08f);
            r.ColliderBox(V(0, 0.4f, 0), V(1.9f, 0.8f, 1.6f));
        }
        else if (kind == "bin") r.Cyl("park.sign", Vector3.zero, 0.3f, 0.85f, 8, 0.34f).Cyl("metal.dark", V(0, 0.85f, 0), 0.36f, 0.08f, 8).ColliderBox(V(0, 0.45f, 0), V(0.6f, 0.9f, 0.6f)).NoShadows();
        else if (kind == "lamp") r.Cyl("metal.dark", Vector3.zero, 0.07f, 3.2f, 6).Sphere("paint.white", V(0, 3.4f, 0), Vector3.one * 0.26f).NoShadows();
        else if (kind == "boulder") { float s = sub == "s" ? 0.45f : sub == "l" ? 1.3f : 0.8f; r.Blob("stone.dark", V(0, s * 0.45f, 0), V(s, s * 0.6f, s * 0.85f), seed, 0.22f, 7, 4); }
        else if (kind == "planter") r.Cyl("wood.warm", Vector3.zero, 0.5f, 0.5f, 8, 0.6f).Blob("leaf.mid", V(0, 0.65f, 0), V(0.55f, 0.3f, 0.55f), seed).Sphere(Petal("red"), V(0.2f, 0.9f, 0.1f), Vector3.one * 0.13f, 5, 3).Sphere(Petal("yellow"), V(-0.2f, 0.88f, -0.1f), Vector3.one * 0.13f, 5, 3).Sphere(Petal("pink"), V(0, 0.93f, -0.25f), Vector3.one * 0.13f, 5, 3).NoShadows();
        else if (kind == "bollard") r.Cyl("stone.dark", Vector3.zero, 0.13f, 0.85f, 8).Dome("paint.white", V(0, 0.85f, 0), V(0.15f, 0.1f, 0.15f)).NoShadows();
        else if (kind == "signpost") r.Cyl("wood.dark", Vector3.zero, 0.05f, 1.5f, 4).Box("paint.white", V(0, 1.3f, 0), V(0.7f, 0.4f, 0.05f)).NoShadows();
        else if (kind == "birdbath") r.Cyl("stone.light", Vector3.zero, 0.12f, 0.8f, 8).Cyl("stone.light", V(0, 0.8f, 0), 0.2f, 0.12f, 10, 0.45f).Cyl("water", V(0, 0.9f, 0), 0.4f, 0.03f, 10).NoShadows();
        else if (kind == "gnome") r.Cyl("paint.blue", Vector3.zero, 0.14f, 0.3f, 7, 0.1f).Sphere("leaf.blossom", V(0, 0.38f, 0), Vector3.one * 0.11f).Cone("paint.red", V(0, 0.44f, 0), 0.12f, 0.3f, 7).NoShadows();
        else if (kind == "duck")
        {
            r.Sphere("paint.white", V(0, 0.16f, 0), V(0.17f, 0.15f, 0.26f), 8, 5).Sphere("paint.white", V(0, 0.36f, 0.17f), Vector3.one * 0.11f, 8, 5)
             .Cone("leaf.autumn", V(0, 0.34f, 0.26f), 0.05f, 0.14f, 5, V(90, 0, 0)).Cone("paint.white", V(0, 0.2f, -0.22f), 0.08f, 0.16f, 5, V(-60, 0, 0))
             .Sphere("eye.black", V(0.07f, 0.4f, 0.23f), Vector3.one * 0.028f, 5, 3).Sphere("eye.black", V(-0.07f, 0.4f, 0.23f), Vector3.one * 0.028f, 5, 3).NoShadows();
        }
        else if (kind == "googly")
        {
            foreach (float x in new[] { -0.27f, 0.27f }) r.Sphere("eye.white", V(x, 0, 0), Vector3.one * 0.25f, 10, 6, true).Sphere("eye.black", V(x * 1.1f, x * 0.2f, 0.17f), Vector3.one * 0.12f, 8, 5, true);
            r.NoShadows();
        }
        else r.Blob("leaf.dark", V(0, 0.5f, 0), V(0.7f, 0.6f, 0.7f), seed).NoShadows();
    }

    static string Petal(string colour)
    {
        switch (colour) { case "red": case "yellow": case "pink": case "purple": case "white": return "park.petal." + colour; default: return "park.petal.pink"; }
    }

    // ================================================================================================== mesh helpers

    static void FlatQuad(Kit.MeshData d, string mat, Vector3 a, Vector3 b, Vector3 c, Vector3 e, float y)
    {
        a.y = b.y = c.y = e.y = y; d.QuadOut(mat, (a + c) * 0.5f + Vector3.down * 50f, a, b, c, e);
    }
    /// <summary>A flat painted line (open polyline) of the given width.</summary>
    static void Line(Kit.MeshData d, string mat, IList<Vector3> pts, float width, float y, bool closed = false)
    {
        int n = pts.Count; int count = closed ? n : n - 1;
        for (int i = 0; i < count; i++)
        {
            Vector3 a = pts[i], b = pts[(i + 1) % n]; var dir = b - a; dir.y = 0; if (dir.sqrMagnitude < 1e-6f) continue; dir.Normalize();
            var s = new Vector3(dir.z, 0, -dir.x) * (width * 0.5f); var e = dir * (width * 0.5f);
            FlatQuad(d, mat, a - s - e, a + s - e, b + s + e, b - s + e, y);
        }
    }
    static List<Vector3> Arc(Vector3 c, float radius, float deg0, float deg1, int segments)
    {
        var list = new List<Vector3>();
        for (int i = 0; i <= segments; i++) { float a = Mathf.Lerp(deg0, deg1, i / (float)segments) * Mathf.Deg2Rad; list.Add(new Vector3(c.x + Mathf.Cos(a) * radius, 0, c.z + Mathf.Sin(a) * radius)); }
        return list;
    }
    static List<Vector3> RoundRect(float x0, float x1, float z0, float z1, float r, int seg = 6)
    {
        var l = new List<Vector3>();
        l.AddRange(Arc(V(x1 - r, z0 + r), r, -90, 0, seg)); l.AddRange(Arc(V(x1 - r, z1 - r), r, 0, 90, seg));
        l.AddRange(Arc(V(x0 + r, z1 - r), r, 90, 180, seg)); l.AddRange(Arc(V(x0 + r, z0 + r), r, 180, 270, seg));
        return l;
    }
    static void RingWall(Kit.MeshData d, string mat, float rIn, float rOut, float y0, float y1, int seg)
    {
        for (int i = 0; i < seg; i++)
        {
            float a = i * Mathf.PI * 2f / seg, b = (i + 1) * Mathf.PI * 2f / seg;
            Vector3 ca = V(Mathf.Cos(a), Mathf.Sin(a)), cb = V(Mathf.Cos(b), Mathf.Sin(b));
            var mid = (ca + cb) * 0.5f * (rIn + rOut) * 0.5f + Vector3.up * (y0 + y1) * 0.5f;
            Vector3 U(Vector3 v, float rr, float y) { return v * rr + Vector3.up * y; }
            d.QuadOut(mat, mid, U(ca, rOut, y0), U(cb, rOut, y0), U(cb, rOut, y1), U(ca, rOut, y1));
            d.QuadOut(mat, mid, U(ca, rIn, y0), U(cb, rIn, y0), U(cb, rIn, y1), U(ca, rIn, y1));
            d.QuadOut(mat, mid, U(ca, rIn, y1), U(cb, rIn, y1), U(cb, rOut, y1), U(ca, rOut, y1));
        }
    }
    static float YawTo(Vector3 from, Vector3 to) { return Mathf.Atan2(to.x - from.x, to.z - from.z) * Mathf.Rad2Deg; }

    // ===================================================================================================== the Pickle

    const float PickleH = 5.6f;
    static Vector3 PickleAxis(float t) { return new Vector3(0.5f * Mathf.Sin(Mathf.PI * t) - 0.12f, PickleH * t, 0f); }
    static float PickleRadius(float t, float a)
    {
        float prof = Mathf.Pow(Mathf.Sin(Mathf.PI * (0.07f + 0.86f * t)), 0.42f);
        return 1.22f * prof * (1f + 0.06f * Mathf.Sin(t * 21f + a * 3f) + 0.04f * Mathf.Sin(t * 9f - a * 2f));
    }
    static Vector3 PickleDir(float a) { return new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)); }
    static Vector3 PickleSkin(float t, float a, float proud = 0f) { return PickleAxis(t) + PickleDir(a) * (PickleRadius(t, a) + proud); }

    static void DefinePickle()
    {
        if (Kit.HasProp("park.pickle")) return;
        const int S = 16, R = 14; const float front = Mathf.PI * 0.5f;      // the face looks along prop +Z
        var r = Kit.DefineProp("park.pickle");
        r.Geometry(d =>
        {
            for (int ring = 0; ring < R; ring++)
                for (int s = 0; s < S; s++)
                {
                    float t0 = ring / (float)R, t1 = (ring + 1) / (float)R, a0 = s * Mathf.PI * 2f / S, a1 = (s + 1) * Mathf.PI * 2f / S;
                    string mat = s % 4 == 1 ? "park.pickle.light" : "park.pickle";
                    d.QuadOut(mat, (PickleAxis(t0) + PickleAxis(t1)) * 0.5f, PickleSkin(t0, a0), PickleSkin(t1, a0), PickleSkin(t1, a1), PickleSkin(t0, a1));
                }
        });
        r.Dome("park.pickle", PickleAxis(1f) - Vector3.up * 0.02f, V(0.68f, 0.42f, 0.68f), 16, 3);
        for (int i = 0; i < 26; i++)                                        // warts
        {
            float t = 0.1f + 0.8f * ((i * 0.618034f) % 1f), a = i * 2.39996f;
            float da = Mathf.Abs(Mathf.DeltaAngle(a * Mathf.Rad2Deg, 90f));
            if (da < 75f && t > 0.42f && t < 0.86f) continue;               // keep the face clean
            float w = 0.11f + 0.05f * (i % 3);
            r.Sphere("park.pickle.dark", PickleSkin(t, a, -0.02f), V(w, w * 0.8f, w), 6, 4);
        }
        // googly eyes, wide-set so each park entrance gets one staring at it
        for (int e = 0; e < 2; e++)
        {
            float sign = e == 0 ? -1f : 1f, a = front + sign * 46f * Mathf.Deg2Rad, t = 0.71f + sign * 0.012f;
            var dir = PickleDir(a); var c = PickleSkin(t, a, 0.18f);
            float er = e == 0 ? 0.56f : 0.5f;
            r.Sphere("eye.white", c, Vector3.one * er, 14, 8, true);
            var look = (dir + V(sign * -0.25f, e == 0 ? 0.35f : -0.4f, 0.3f)).normalized;      // one looks up, one looks down
            r.Sphere("eye.black", c + look * (er * 0.72f), Vector3.one * (er * 0.42f), 10, 6, true);
            r.Sphere("eye.white", c + look * (er * 1.0f) + V(0.05f, 0.08f, 0), Vector3.one * (er * 0.1f), 6, 4, true);
        }
        // lopsided smile + cheeks
        Vector3 prev = Vector3.zero;
        for (int i = 0; i <= 8; i++)
        {
            float u = i / 8f, a = front + Mathf.Lerp(-30f, 30f, u) * Mathf.Deg2Rad, t = 0.535f + 0.05f * (2f * u - 1f) * (2f * u - 1f) + 0.012f * u;
            var pt = PickleSkin(t, a, 0.03f);
            if (i > 0) r.Bar("eye.black", prev, pt, 0.1f);
            prev = pt;
        }
        r.Sphere("leaf.blossom", PickleSkin(0.6f, front - 40f * Mathf.Deg2Rad, 0f), V(0.2f, 0.14f, 0.2f), 8, 4);
        r.Sphere("leaf.blossom", PickleSkin(0.61f, front + 40f * Mathf.Deg2Rad, 0f), V(0.2f, 0.14f, 0.2f), 8, 4);
        // stubby arms: one waving, one on the hip
        Vector3 s0 = PickleSkin(0.5f, front - 95f * Mathf.Deg2Rad, -0.1f), s1 = s0 + V(0.75f, 0.95f, 0.15f);
        r.Bar("park.pickle.dark", s0, s1, 0.17f).Sphere("park.pickle", s1, Vector3.one * 0.24f, 8, 5);
        Vector3 h0 = PickleSkin(0.5f, front + 95f * Mathf.Deg2Rad, -0.1f), h1 = h0 + V(-0.7f, -0.35f, 0.2f);
        r.Bar("park.pickle.dark", h0, h1, 0.17f).Sphere("park.pickle", h1, Vector3.one * 0.24f, 8, 5);
        // a tiny top hat, slightly too small, slightly askew
        var top = PickleAxis(1f) + V(0.05f, 0.33f, 0f); var tilt = V(0, 0, -14);
        r.Cyl("metal.dark", top, 0.5f, 0.06f, 12, -1f, tilt).Cyl("metal.dark", top, 0.3f, 0.55f, 12, -1f, tilt).Cyl("paint.red", top + V(0.01f, 0.06f, 0), 0.31f, 0.12f, 12, -1f, tilt);
        r.ColliderBox(V(0.15f, PickleH * 0.5f, 0), V(2.2f, PickleH, 2.2f));

        Kit.DefineProp("park.pickle.plinth")
            .Cyl("stone.dark", Vector3.zero, 1.85f, 0.35f, 4)
            .Cyl("stone.light", V(0, 0.35f, 0), 1.55f, 1.0f, 4)
            .Cyl("stone.dark", V(0, 1.35f, 0), 1.7f, 0.16f, 4)
            .Cyl("park.pickle.dark", V(0, 1.51f, 0), 1.0f, 0.1f, 12)
            .ColliderBox(V(0, 0.75f, 0), V(3.4f, 1.5f, 3.4f)).Occluder();

        Kit.DefineProp("park.pickle.basin")
            .Geometry(d => { RingWall(d, "stone.light", 3.95f, 4.5f, 0f, 0.5f, 24); RingWall(d, "stone.dark", 4.45f, 4.75f, 0f, 0.14f, 24); })
            .Cyl("water", Vector3.zero, 3.97f, 0.36f, 24)
            .Cyl("park.water.deep", V(0, 0.3f, 0), 3.1f, 0.075f, 20)
            .ColliderBox(V(0, 0.25f, 0), V(6.2f, 0.5f, 6.2f));
        // three little spouts + a rubber duck
        var b = Kit.DefineProp("park.pickle.spouts");
        for (int i = 0; i < 3; i++)
        {
            float a = (i * 120f + 60f) * Mathf.Deg2Rad; var o = V(Mathf.Cos(a) * 2.9f, 0.36f, Mathf.Sin(a) * 2.9f);
            b.Cyl("stone.dark", o, 0.14f, 0.3f, 6).Cone("paint.white", o + Vector3.up * 0.3f, 0.11f, 1.1f, 6).Sphere("paint.white", o + Vector3.up * 1.35f, V(0.2f, 0.12f, 0.2f), 6, 3);
        }
        b.Sphere("paint.yellow", V(-3.0f, 0.5f, 1.2f), V(0.26f, 0.22f, 0.34f), 8, 5).Sphere("paint.yellow", V(-3.0f, 0.78f, 1.42f), Vector3.one * 0.17f, 8, 5)
         .Cone("leaf.autumn", V(-3.0f, 0.76f, 1.56f), 0.07f, 0.16f, 5, V(90, 0, 0)).Sphere("eye.black", V(-2.9f, 0.84f, 1.53f), Vector3.one * 0.035f, 5, 3).Sphere("eye.black", V(-3.1f, 0.84f, 1.53f), Vector3.one * 0.035f, 5, 3)
         .NoShadows();
    }

    // ================================================================================================ structure props

    static void DefineStructures()
    {
        if (Kit.HasProp("park.arch.big")) return;
        foreach (bool big in new[] { true, false })
        {
            float half = big ? 5.6f : 3.3f, h = big ? 4.9f : 4.3f, th = big ? 0.46f : 0.38f, bw = big ? 6.4f : 4.6f, bh = big ? 1.15f : 0.9f;
            var r = Kit.DefineProp(big ? "park.arch.big" : "park.arch.small");
            foreach (float x in new[] { -half, half })
            {
                r.Cyl("stone.light", V(x, 0, 0), th * 0.85f, 0.7f, 4).Cyl("wood.warm", V(x, 0.7f, 0), th * 0.5f, h - 0.7f, 4)
                 .Bar("wood.dark", V(x, h - 1.6f, 0), V(x - Mathf.Sign(x) * 1.5f, h - 0.1f, 0), th * 0.45f)
                 .Blob("park.pickle", V(x, h + 0.85f, 0), V(0.3f, 0.6f, 0.3f), 5, 0.1f, 8, 5).Cyl("wood.dark", V(x, h + 0.2f, 0), th * 0.62f, 0.14f, 4)
                 .ColliderBox(V(x, h * 0.5f, 0), V(th, h, th));
            }
            r.Box("wood.warm", V(0, h + 0.06f, 0), V(half * 2f + 1.3f, th * 0.6f, th * 0.8f));
            r.Box("wood.dark", V(0, h + 0.34f, 0), V(half * 2f + 0.5f, 0.14f, th * 1.25f));
            // curved crown made of short beams, with the name board hanging in it
            Vector3 prev = Vector3.zero;
            for (int i = 0; i <= 8; i++) { float u = i / 8f * 2f - 1f; var pt = V(u * (bw * 0.5f + 0.5f), h + 0.4f + (bh + 0.75f) * (1f - u * u), 0); if (i > 0) r.Bar("wood.warm", prev, pt, th * 0.42f); prev = pt; }
            r.Box("park.sign", V(0, h + 0.45f + bh * 0.5f, 0), V(bw, bh, 0.14f)).Box("park.cream", V(0, h + 0.45f + bh * 0.5f, 0), V(bw + 0.16f, bh + 0.16f, 0.08f));
            // bunting
            for (int i = 0; i < (big ? 11 : 7); i++)
            {
                float u = (i + 0.5f) / (big ? 11 : 7), x = Mathf.Lerp(-half + 0.3f, half - 0.3f, u), y = h - 0.22f - 0.45f * (1f - (2f * u - 1f) * (2f * u - 1f)) * -1f - 0.45f;
                y = h - 0.25f - 0.5f * (1f - (2f * u - 1f) * (2f * u - 1f));
                r.Sheet(new[] { "paint.red", "paint.yellow", "paint.blue", "paint.white" }[i % 4], V(x - 0.2f, y, 0), V(x + 0.2f, y, 0), V(x, y - 0.42f, 0), V(x, y - 0.42f, 0));
            }
        }

        // bandstand superstructure (origin = deck top centre; the stepped deck is extruded ground)
        var g = Kit.DefineProp("park.bandstand");
        const float pr = 3.55f, ph = 3.0f;
        for (int i = 0; i < 8; i++)
        {
            float a = (i + 0.5f) * Mathf.PI / 4f, a2 = (i + 1.5f) * Mathf.PI / 4f;
            Vector3 p0 = V(Mathf.Cos(a) * pr, 0, Mathf.Sin(a) * pr), p1 = V(Mathf.Cos(a2) * pr, 0, Mathf.Sin(a2) * pr);
            g.Cyl("paint.white", p0, 0.11f, ph, 8).Cyl("paint.white", p0, 0.18f, 0.3f, 8).ColliderBox(p0 + Vector3.up * 1.5f, V(0.3f, 3f, 0.3f));
            bool open = i == 1;                                             // the bay facing +Z stays open
            if (!open)
            {
                g.Bar("paint.white", p0 + Vector3.up * 0.95f, p1 + Vector3.up * 0.95f, 0.09f).Bar("paint.white", p0 + Vector3.up * 0.2f, p1 + Vector3.up * 0.2f, 0.07f);
                for (int k = 1; k < 6; k++) { var q = Vector3.Lerp(p0, p1, k / 6f); g.Bar("park.roof.red", q + Vector3.up * 0.2f, q + Vector3.up * 0.95f, 0.05f); }
            }
            g.Bar("paint.white", p0 + Vector3.up * (ph - 0.1f), p1 + Vector3.up * (ph - 0.1f), 0.16f);
            // scalloped valance
            for (int k = 0; k < 4; k++) { var q0 = Vector3.Lerp(p0, p1, k / 4f) * 1.17f; var q1 = Vector3.Lerp(p0, p1, (k + 1) / 4f) * 1.17f; g.Sheet(k % 2 == 0 ? "park.cream" : "park.roof.red", q0 + Vector3.up * ph, q1 + Vector3.up * ph, (q0 + q1) * 0.5f + Vector3.up * (ph - 0.45f), (q0 + q1) * 0.5f + Vector3.up * (ph - 0.45f)); }
        }
        g.Geometry(d =>
        {
            var apex = V(0, ph + 2.3f, 0);
            for (int i = 0; i < 16; i++)
            {
                float a = (i * 0.5f + 0.5f) * Mathf.PI / 4f, a2 = (i * 0.5f + 1f) * Mathf.PI / 4f;
                Vector3 e0 = V(Mathf.Cos(a) * 4.5f, ph - 0.05f, Mathf.Sin(a) * 4.5f), e1 = V(Mathf.Cos(a2) * 4.5f, ph - 0.05f, Mathf.Sin(a2) * 4.5f);
                Vector3 m0 = V(Mathf.Cos(a) * 2.2f, ph + 1.05f, Mathf.Sin(a) * 2.2f), m1 = V(Mathf.Cos(a2) * 2.2f, ph + 1.05f, Mathf.Sin(a2) * 2.2f);
                string mat = i % 2 == 0 ? "park.roof.red" : "park.cream";
                d.QuadOut(mat, V(0, ph, 0), e0, e1, m1, m0); d.TriOut(mat, V(0, ph, 0), m0, m1, apex);
                d.TriOut("park.cream", V(0, ph + 3f, 0), e0, e1, V(0, ph + 0.4f, 0));
            }
        });
        g.Cyl("park.gold", V(0, ph + 2.2f, 0), 0.1f, 0.5f, 6).Sphere("park.gold", V(0, ph + 2.85f, 0), Vector3.one * 0.24f, 8, 5)
         .Blob("park.pickle", V(0, ph + 3.4f, 0), V(0.16f, 0.36f, 0.16f), 3, 0.1f, 7, 5);
        // a drum, a tuba-ish horn and two folding chairs so somebody clearly plays here
        g.Cyl("paint.red", V(-1.2f, 0, -1.4f), 0.45f, 0.5f, 10).Cyl("park.cream", V(-1.2f, 0.5f, -1.4f), 0.47f, 0.04f, 10)
         .Cone("park.gold", V(1.3f, 0.75f, -1.2f), 0.42f, 0.7f, 10, V(200, 0, 20)).Cyl("park.gold", V(1.3f, 0, -1.2f), 0.08f, 0.8f, 6);
        foreach (float x in new[] { -0.2f, 0.55f }) g.Box("paint.blue", V(x, 0.45f, -2.2f), V(0.42f, 0.05f, 0.42f)).Box("paint.blue", V(x, 0.75f, -2.4f), V(0.42f, 0.4f, 0.05f)).Box("metal.dark", V(x, 0.22f, -2.2f), V(0.36f, 0.44f, 0.36f));
        g.Occluder();

        Kit.DefineProp("park.hoop")
            .Cyl("metal.dark", Vector3.zero, 0.09f, 3.3f, 6).Bar("metal.dark", V(0, 3.25f, 0), V(0, 3.4f, 0.9f), 0.1f)
            .Box("paint.white", V(0, 3.5f, 0.95f), V(1.8f, 1.1f, 0.06f)).Box("paint.red", V(0, 3.3f, 0.99f), V(0.6f, 0.45f, 0.02f)).Box("paint.white", V(0, 3.3f, 1.0f), V(0.46f, 0.31f, 0.02f))
            .Torus("park.hoop", V(0, 3.05f, 1.3f), 0.25f, 0.03f, 12, 5).Cyl("paint.white", V(0, 2.6f, 1.3f), 0.17f, 0.45f, 8, 0.25f)
            .ColliderBox(V(0, 1.6f, 0), V(0.25f, 3.2f, 0.25f));
        Kit.DefineProp("park.ball").Sphere("park.hoop", V(0, 0.13f, 0), Vector3.one * 0.13f, 8, 5).NoShadows();

        Kit.DefineProp("park.jetty")
            .Box("wood.warm", V(0, 0.34f, 3.2f), V(1.5f, 0.08f, 6.6f))
            .Geometry(d => { for (int i = 0; i < 11; i++) { var c = V(0, 0.385f, 0.3f + i * 0.6f); d.QuadOut("wood.dark", c + Vector3.down, c + V(-0.75f, 0, -0.02f), c + V(0.75f, 0, -0.02f), c + V(0.75f, 0, 0.02f), c + V(-0.75f, 0, 0.02f)); } })
            .Cyl("wood.dark", V(-0.7f, -0.1f, 2.2f), 0.09f, 0.95f, 6).Cyl("wood.dark", V(0.7f, -0.1f, 2.2f), 0.09f, 0.95f, 6)
            .Cyl("wood.dark", V(-0.7f, -0.1f, 4.4f), 0.09f, 0.95f, 6).Cyl("wood.dark", V(0.7f, -0.1f, 4.4f), 0.09f, 1.5f, 6)
            .Cyl("wood.dark", V(-0.7f, -0.1f, 6.4f), 0.09f, 1.5f, 6).Cyl("wood.dark", V(0.7f, -0.1f, 6.4f), 0.09f, 0.95f, 6)
            .Torus("paint.red", V(0.7f, 1.2f, 4.52f), 0.2f, 0.06f, 10, 5, V(90, 0, 0)).ColliderBox(V(0, 0.2f, 3.2f), V(1.5f, 0.4f, 6.6f)).Walkable();
        Kit.DefineProp("park.boat")
            .Sphere("paint.red", V(0, 0.1f, 0), V(0.62f, 0.34f, 1.25f), 8, 4).Cyl("park.cream", V(0, 0.22f, 0), 0.5f, 0.06f, 8, -1f)
            .Box("wood.warm", V(0, 0.32f, 0.2f), V(0.95f, 0.05f, 0.2f)).Box("wood.warm", V(0, 0.32f, -0.45f), V(0.8f, 0.05f, 0.2f))
            .Bar("wood.dark", V(0.5f, 0.38f, 0.1f), V(1.3f, 0.2f, -0.5f), 0.05f).Bar("wood.dark", V(-0.5f, 0.38f, 0.1f), V(-1.3f, 0.2f, -0.5f), 0.05f).NoShadows();
        Kit.DefineProp("park.bbq")
            .Bar("metal.dark", V(-0.25f, 0, -0.2f), V(-0.1f, 0.7f, -0.05f), 0.04f).Bar("metal.dark", V(0.25f, 0, -0.2f), V(0.1f, 0.7f, -0.05f), 0.04f).Bar("metal.dark", V(0, 0, 0.3f), V(0, 0.7f, 0.08f), 0.04f)
            .Sphere("metal.dark", V(0, 0.85f, 0), V(0.38f, 0.26f, 0.38f), 10, 5).Dome("paint.red", V(0, 0.9f, 0), V(0.39f, 0.28f, 0.39f), 10, 3).Sphere("wood.dark", V(0, 1.2f, 0), Vector3.one * 0.05f, 5, 3)
            .Blob("paint.white", V(0.15f, 1.7f, 0), V(0.2f, 0.16f, 0.2f), 4, 0.3f, 6, 4).Blob("paint.white", V(0.3f, 2.1f, 0.1f), V(0.28f, 0.2f, 0.28f), 5, 0.3f, 6, 4)
            .ColliderBox(V(0, 0.55f, 0), V(0.6f, 1.1f, 0.6f)).NoShadows();
        // picnic blanket with a basket, a pie and an abandoned sandwich
        Kit.DefineProp("park.blanket").Geometry(d =>
        {
            for (int i = 0; i < 4; i++) for (int k = 0; k < 4; k++)
            { var c = V(-0.9f + i * 0.6f, 0.03f, -0.9f + k * 0.6f); d.QuadOut((i + k) % 2 == 0 ? "paint.red" : "paint.white", c + Vector3.down, c + V(-0.3f, 0, -0.3f), c + V(0.3f, 0, -0.3f), c + V(0.3f, 0, 0.3f), c + V(-0.3f, 0, 0.3f)); }
        })
            .Box("wood.warm", V(0.5f, 0.18f, 0.4f), V(0.5f, 0.3f, 0.36f)).Torus("wood.dark", V(0.5f, 0.33f, 0.4f), 0.2f, 0.025f, 8, 4, V(90, 0, 0))
            .Cyl("park.cream", V(-0.4f, 0.03f, -0.2f), 0.2f, 0.07f, 9).Cyl("paint.red", V(-0.4f, 0.1f, -0.2f), 0.15f, 0.02f, 9)
            .Box("paint.yellow", V(-0.3f, 0.07f, 0.55f), V(0.2f, 0.07f, 0.2f)).NoShadows();
        var bl = Kit.DefineProp("park.balloons").Cyl("wood.dark", Vector3.zero, 0.05f, 0.9f, 4);
        string[] bc = { "paint.red", "paint.yellow", "paint.blue", "park.petal.pink", "park.pickle.light" };
        for (int i = 0; i < 5; i++)
        {
            float a = i * 1.3f; var c = V(Mathf.Cos(a) * 0.42f, 2.5f + 0.28f * (i % 3), Mathf.Sin(a) * 0.42f);
            bl.Bar("paint.white", V(0, 0.9f, 0), c - Vector3.up * 0.3f, 0.02f).Sphere(bc[i], c, V(0.27f, 0.33f, 0.27f), 8, 5).Cone(bc[i], c - Vector3.up * 0.4f, 0.05f, 0.1f, 5);
        }
        bl.NoShadows();
        Kit.DefineProp("park.signstake").Cyl("wood.dark", V(-0.5f, 0, 0), 0.05f, 1.25f, 4).Cyl("wood.dark", V(0.5f, 0, 0), 0.05f, 1.25f, 4).NoShadows();
        // a kite stuck in a tree: spawned high up next to a crown
        Kit.DefineProp("park.kite")
            .Sheet("paint.red", V(0, 0.7f, 0), V(0.45f, 0, 0), V(0, -0.9f, 0), V(-0.45f, 0, 0)).Sheet("paint.yellow", V(0, 0.7f, 0.01f), V(0.45f, 0, 0.01f), V(0, 0, 0.01f), V(0, 0, 0.01f))
            .Bar("paint.white", V(0, -0.9f, 0), V(0.3f, -1.6f, 0.2f), 0.03f).Bar("paint.white", V(0.3f, -1.6f, 0.2f), V(-0.1f, -2.2f, 0.1f), 0.03f)
            .Sheet("paint.blue", V(0.3f, -1.6f, 0.2f), V(0.5f, -1.5f, 0.2f), V(0.5f, -1.7f, 0.2f), V(0.3f, -1.6f, 0.2f)).NoShadows();
    }

    static void DefinePalette()
    {
        Kit.Define("park.pickle", new Color(0.38f, 0.64f, 0.17f), 0.4f);
        Kit.Define("park.pickle.dark", new Color(0.23f, 0.47f, 0.12f), 0.3f);
        Kit.Define("park.pickle.light", new Color(0.60f, 0.79f, 0.27f), 0.4f);
        Kit.Define("park.lawn", new Color(0.39f, 0.66f, 0.29f), 0.02f);
        Kit.Define("park.lawn.light", new Color(0.52f, 0.78f, 0.36f), 0.02f);
        Kit.Define("park.hedge", new Color(0.16f, 0.43f, 0.19f), 0.03f);
        Kit.Define("park.hedge.top", new Color(0.24f, 0.55f, 0.22f), 0.03f);
        Kit.Define("park.rubber", new Color(0.93f, 0.50f, 0.40f), 0.05f);
        Kit.Define("park.rubber.alt", new Color(0.30f, 0.68f, 0.70f), 0.05f);
        Kit.Define("park.court", new Color(0.27f, 0.60f, 0.70f), 0.05f);
        Kit.Define("park.court.key", new Color(0.93f, 0.56f, 0.36f), 0.05f);
        Kit.Define("park.sand", new Color(0.93f, 0.84f, 0.60f), 0.02f);
        Kit.Define("park.water.deep", new Color(0.20f, 0.52f, 0.82f), 0.9f);
        Kit.Define("park.roof.red", new Color(0.90f, 0.27f, 0.25f), 0.2f);
        Kit.Define("park.cream", new Color(0.99f, 0.94f, 0.80f), 0.1f);
        Kit.Define("park.gold", new Color(1f, 0.78f, 0.2f), 0.6f, 0.4f);
        Kit.Define("park.sign", new Color(0.13f, 0.44f, 0.30f), 0.15f);
        Kit.Define("park.hoop", new Color(0.96f, 0.45f, 0.12f), 0.2f);
        Kit.Define("park.plaza.band", new Color(0.80f, 0.56f, 0.44f), 0.05f);
        Kit.Define("park.plaza.light", new Color(0.96f, 0.90f, 0.78f), 0.05f);
        Kit.Define("park.petal.red", new Color(0.93f, 0.22f, 0.24f), 0.1f);
        Kit.Define("park.petal.yellow", new Color(1f, 0.84f, 0.2f), 0.1f);
        Kit.Define("park.petal.pink", new Color(1f, 0.52f, 0.72f), 0.1f);
        Kit.Define("park.petal.purple", new Color(0.62f, 0.42f, 0.90f), 0.1f);
        Kit.Define("park.petal.white", new Color(0.99f, 0.98f, 0.94f), 0.1f);
    }

    // ========================================================================================================= builder

    sealed class Builder
    {
        readonly TownLifeContext ctx; readonly Occ occ; readonly TownLifeScene.ParkInfo park;
        Transform root, gGround, gEdge, gTrees, gPlants, gFurn, gFeature;
        int trees, skipped, lamps; readonly List<string> skippedWhat = new List<string>();

        // ------------------------------------------------------------------------------------------- THE PLAN (data)
        // Block interior and promenade axis come from the scene; everything else is laid out relative to them.
        float x0, x1, z0, z1, ax;                      // block rect (inside the sidewalks) and the N-S promenade x
        const float HedgeInset = 2.9f;                 // 0..2.4 m inside the block edge stays free for street trees
        const float PromenadeWidth = 5f, CrossWidth = 3.4f, LoopWidth = 2.3f;
        const float GateHalf = 7f, SideGateHalf = 3.6f;// half openings: S/N forecourts (14 m), W/E gates
        Vector3 plaza; const float PlazaR = 11.5f;     // round plaza with the Pickle in the middle, promenade through its west third
        float crossZ;                                  // E-W path / W and E gates
        Rect loop; const float LoopR = 9f;             // stroll loop (rounded rect)
        Vector3 pond; Vector2 pondR;                   // NW: pond
        Rect picnic;                                   // N of the pen, W of the promenade: picnic lawn under oaks
        Rect playExt, parents;                         // W of the pen: open play patch; S of the pen: parents' strip
        Rect sunLawn, court;                           // SE: mown lawn + half court
        Vector3 bandstand; Rect bandLawn;              // NE: bandstand + audience lawn
        Vector3 southArch, northArch, westArch, eastArch;

        public Builder(TownLifeContext ctx) { this.ctx = ctx; occ = ctx.occupancy; park = ctx.scene.park; }

        void Plan()
        {
            var b = park.block; x0 = b.xMin; x1 = b.xMax; z0 = b.yMin; z1 = b.yMax; ax = park.landmark.x;
            float lx = park.landmark.x, lz = park.landmark.z;
            plaza = V(lx + 6.75f, lz + 1.3f); crossZ = plaza.z;
            loop = Rect.MinMaxRect(x0 + 10.5f, z0 + 10.75f, x1 - 10.5f, z1 - 10.5f);
            pond = V(x0 + 23.5f, z1 - 22f); pondR = new Vector2(10.5f, 7.5f);
            picnic = Rect.MinMaxRect(x0 + 38f, crossZ + 4.5f, ax - 5.25f, z1 - 12f);
            playExt = Rect.MinMaxRect(x0 + 13.5f, z0 + 23.25f, x0 + 28.5f, crossZ - 3f);
            parents = Rect.MinMaxRect(x0 + 13f, z0 + 13.25f, ax - 5.5f, z0 + 21.75f);
            sunLawn = Rect.MinMaxRect(ax + 8.2f, z0 + 13.75f, ax + 17.2f, plaza.z - PlazaR - 5.4f);
            court = Rect.MinMaxRect(ax + 20.25f, z0 + 14.25f, ax + 34.25f, z0 + 27.25f);
            bandstand = V(ax + 27.75f, z1 - 21f);
            bandLawn = Rect.MinMaxRect(ax + 8.2f, plaza.z + PlazaR + 5.4f, ax + 19.5f, z1 - 12.5f);
            southArch = V(ax, z0 + 6.5f); northArch = V(ax, z1 - 6.5f); westArch = V(x0 + 4.6f, crossZ); eastArch = V(x1 - 4.6f, crossZ);
        }

        public void Run()
        {
            Plan(); DefinePalette(); DefinePickle(); DefineStructures();
            root = ctx.Group("Park");
            gGround = Kit.Group(root, "TL_ParkGround"); gEdge = Kit.Group(root, "TL_ParkEdge"); gTrees = Kit.Group(root, "TL_ParkTrees");
            gPlants = Kit.Group(root, "TL_ParkPlants"); gFurn = Kit.Group(root, "TL_ParkFurniture"); gFeature = Kit.Group(root, "TL_ParkFeatures");

            Ground();
            ReserveZones();
            Perimeter();
            Gateways();
            PlazaAndPickle();
            PlayArea();
            Pond();
            Picnic();
            Bandstand();
            Court();
            SunLawn();
            Promenade();
            CrossPath();
            Groves();
            PerimeterPlanting();

            ctx.Log(Name, "Pickle Park: " + trees + " trees, " + lamps + " lit lamps, " + skipped + " placements skipped by keep-outs"
                + (skippedWhat.Count > 0 ? " (" + string.Join(", ", skippedWhat.GetRange(0, Mathf.Min(60, skippedWhat.Count))) + ")" : ""));
        }

        // ---------------------------------------------------------------------------------------------- placement
        Vector3 G(Vector3 p) { p.y = GrassY; return p; }
        System.Random Rng(string key) { return ctx.Rng("park/" + key); }

        GameObject Put(Transform parent, string id, Vector3 p, float yaw, float scale, float radius, string tag, bool force = false, float y = GrassY)
        {
            p.y = 0f;
            if (!force && !occ.Free(p, radius)) { skipped++; skippedWhat.Add(id.Substring(id.LastIndexOf('.', Mathf.Max(0, id.Length - 4)) < 0 ? 0 : id.IndexOf('.') + 1) + "@" + Mathf.RoundToInt(p.x) + "," + Mathf.RoundToInt(p.z) + ":" + occ.Blocker(p, radius, Occ.What.Solid)); return null; }
            if (radius > 0f) occ.ReserveCircle(p, radius, tag);
            p.y = y;
            return Kit.Spawn(parent, id, p, yaw, scale);
        }
        bool Tree(string species, string size, float x, float z, string key, bool force = false, float scale = -1f)
        {
            var rng = Rng("tree/" + key);
            float s = scale > 0f ? scale : TownLifeContext.Range(rng, 0.9f, 1.15f);
            var go = Put(gTrees, K("kit.tree." + species + "." + size), V(x, z), TownLifeContext.Range(rng, 0f, 360f), s, 1.1f, TagTree, force);
            if (go != null) trees++;
            return go != null;
        }
        void Shrub(string id, float x, float z, string key, float scale = 1f)
        {
            var rng = Rng("shrub/" + key);
            Put(gPlants, K(id), V(x, z), TownLifeContext.Range(rng, 0f, 360f), scale * TownLifeContext.Range(rng, 0.85f, 1.2f), 0.5f, TagShrub);
        }
        void Bench(float x, float z, float yaw, bool force = false) { Put(gFurn, K("kit.bench.park"), V(x, z), yaw, 1f, 0.9f, TagFurn, force); }
        void Bin(float x, float z, float yaw = 0f) { Put(gFurn, K("kit.bin.park"), V(x, z), yaw, 1f, 0.4f, TagFurn); }
        void Lamp(float x, float z, float yaw = 0f)
        {
            var p = V(x, z);
            if (!occ.Free(p, 0.3f)) { skipped++; skippedWhat.Add("lamp@" + Mathf.RoundToInt(x) + "," + Mathf.RoundToInt(z) + ":" + occ.Blocker(p, 0.3f, Occ.What.Solid)); return; }
            p.y = GrassY;
            var go = Kit.SpawnPrefab(gFurn, LampPrefab, p, yaw, 1f, true, true);
            if (go == null) go = Kit.Spawn(gFurn, K("kit.lamp.park"), p, yaw);
            occ.ReserveCircle(p, 0.6f, TagFurn); lamps++;
        }
        void FlowerRun(string name, IList<Vector3> pts, string colour)
        {
            string id = "kit.panel.flowerborder." + colour;
            Kit.Run(gPlants, name, pts, new Kit.RunSpec { panel = K(id), panelLength = PanelLength(id), y = GrassY, castShadows = false });
            occ.ReservePolyline(pts, 0.4f, TownLifeTags.Bed + ".park", Occ.Block.Marker);
        }
        void Sign(string name, float x, float z, float yaw, string text, float width = 1.5f)
        {
            if (!occ.Free(V(x, z), 0.3f)) { skipped++; return; }
            Kit.Spawn(gFurn, "park.signstake", V(x, GrassY, z), yaw, new Vector3(width / 1.5f, 1f, 1f));
            Kit.SignBoard(gFurn, name, V(x, GrassY + 1.05f, z), yaw, new Vector3(width + 0.3f, 0.62f, 0.06f), "park.sign", text, new Color(1f, 0.93f, 0.55f), true);
            occ.ReserveCircle(V(x, z), 0.5f, TagFurn);
        }

        // ------------------------------------------------------------------------------------------------- ground
        void Ground()
        {
            // one fresher green over the whole block so the park reads as one designed place from above
            Kit.Polygon(gGround, "park_lawn_base", new[] { V(x0, z0), V(x0, z1), V(x1, z1), V(x1, z0) }, "park.lawn", LawnY);

            // stroll loop (gravel, below the paving so crossings never fight)
            var loopPts = RoundRect(loop.xMin, loop.xMax, loop.yMin, loop.yMax, LoopR, 7);
            Kit.Ribbon(gGround, "park_loop", loopPts, LoopWidth, "gravel", SoilY, false, true);

            // E-W path, gate to gate
            Kit.Ribbon(gGround, "park_cross_west", new[] { V(x0, crossZ), V(plaza.x - PlazaR + 0.5f, crossZ) }, CrossWidth, "paving.warm", PavingY, true);
            Kit.Ribbon(gGround, "park_cross_east", new[] { V(plaza.x + PlazaR - 0.5f, crossZ), V(x1, crossZ) }, CrossWidth, "paving.warm", PavingY, true);

            // promenade + the two forecourts in one walkable piece
            float h = PromenadeWidth * 0.5f;
            var prom = new[] {
                V(ax - GateHalf, z0), V(ax + GateHalf, z0), V(ax + GateHalf, z0 + 4.5f), V(ax + h, z0 + 9f),
                V(ax + h, z1 - 9f), V(ax + GateHalf, z1 - 4.5f), V(ax + GateHalf, z1), V(ax - GateHalf, z1), V(ax - GateHalf, z1 - 4.5f), V(ax - h, z1 - 9f),
                V(ax - h, z0 + 9f), V(ax - GateHalf, z0 + 4.5f) };
            Kit.Polygon(gGround, "park_promenade", prom, "paving.warm", PavingY, true);
            // a centre band and edge bands on the promenade (pure paint, on the corridor is fine)
            var m = new Kit.MeshData();
            foreach (float off in new[] { -h + 0.2f, h - 0.2f })
            {
                float dx = ax + off - plaza.x, half = Mathf.Abs(dx) < PlazaR ? Mathf.Sqrt(PlazaR * PlazaR - dx * dx) : 0f;
                Line(m, "park.plaza.band", new[] { V(ax + off, z0 + 9.5f), V(ax + off, plaza.z - half) }, 0.35f, MarkingY);
                Line(m, "park.plaza.band", new[] { V(ax + off, plaza.z + half), V(ax + off, z1 - 9.5f) }, 0.35f, MarkingY);
            }
            for (float z = z0 + 12f; z < z1 - 11f; z += 6f)
            {
                if (Mathf.Abs(z - plaza.z) < PlazaR - 1f) continue;
                FlatQuad(m, "park.plaza.light", V(ax - 0.7f, z), V(ax, z + 0.7f), V(ax + 0.7f, z), V(ax, z - 0.7f), MarkingY);
            }
            // forecourt welcome mats
            foreach (float z in new[] { z0 + 2.4f, z1 - 2.4f })
            { Line(m, "park.plaza.band", new[] { V(ax - GateHalf + 0.6f, z - 1.6f), V(ax + GateHalf - 0.6f, z - 1.6f), V(ax + GateHalf - 0.6f, z + 1.6f), V(ax - GateHalf + 0.6f, z + 1.6f) }, 0.3f, MarkingY, true); }
            // a painted pickle on each forecourt, looking at whoever walks in
            foreach (float z in new[] { z0 + 2.4f, z1 - 2.4f })
            {
                var c = V(ax - 3.2f, z); var body = new List<Vector3>();
                for (int i = 0; i < 18; i++) { float a = i * Mathf.PI * 2f / 18f; body.Add(c + V(Mathf.Cos(a) * 2.3f, Mathf.Sin(a) * 0.85f + 0.25f * Mathf.Cos(a) * Mathf.Cos(a))); }
                Kit.AddPolygon(m, "park.pickle", body, MarkingY);
                foreach (float ex in new[] { 0.9f, 1.55f })
                {
                    var eye = Kit.Circle(c + V(ex, 0.5f), 0.3f, 10); Kit.AddPolygon(m, "eye.white", eye, MarkingY + 0.006f);
                    var pup = Kit.Circle(c + V(ex + 0.08f, 0.45f), 0.13f, 8); Kit.AddPolygon(m, "eye.black", pup, MarkingY + 0.012f);
                }
            }
            Kit.Custom(gGround, "park_promenade_marks", m);

            // spur to the bandstand
            Kit.Ribbon(gGround, "park_band_spur", new[] { V(bandstand.x, crossZ + CrossWidth * 0.5f - 0.1f), V(bandstand.x, bandstand.z - 6.4f) }, 2.6f, "paving.warm", PavingY);
            // little paths: loop -> jetty, loop -> play patch
        }

        void ReserveZones()
        {
            // promenade stays drivable and prop-free; paths are markers; lawns stay open; pond blocks everything
            occ.ReserveLine(V(ax, z0), V(ax, z1), PromenadeWidth * 0.5f + 0.2f, TownLifeTags.PathSurface + ".park.promenade", Occ.Block.Solids);
            occ.ReserveLine(V(x0, crossZ), V(x1, crossZ), CrossWidth * 0.5f + 0.2f, TownLifeTags.PathSurface + ".park.cross", Occ.Block.Solids);
            occ.ReserveCircle(plaza, PlazaR, TownLifeTags.PathSurface + ".park.plaza", Occ.Block.Solids);
            occ.ReservePolyline(RoundRect(loop.xMin, loop.xMax, loop.yMin, loop.yMax, LoopR, 7), LoopWidth * 0.5f + 0.2f, TownLifeTags.PathSurface + ".park.loop", Occ.Block.Solids, true);
            occ.ReserveRect(Rect.MinMaxRect(ax - GateHalf, z0, ax + GateHalf, z0 + 5f), TownLifeTags.PathSurface + ".park.forecourt", Occ.Block.Solids);
            occ.ReserveRect(Rect.MinMaxRect(ax - GateHalf, z1 - 5f, ax + GateHalf, z1), TownLifeTags.PathSurface + ".park.forecourt", Occ.Block.Solids);
            occ.ReserveLine(V(bandstand.x, crossZ), V(bandstand.x, bandstand.z - 6f), 1.5f, TownLifeTags.PathSurface + ".park.spur", Occ.Block.Solids);
            occ.ReserveRect(sunLawn, TownLifeTags.Lawn + ".park.sun", Occ.Block.Solids);
            occ.ReserveRect(bandLawn, TownLifeTags.Lawn + ".park.band", Occ.Block.Solids);
            occ.ReserveRect(court, TownLifeTags.PathSurface + ".park.court", Occ.Block.Solids, 0.5f);
            occ.ReserveRect(playExt, TownLifeTags.PathSurface + ".park.play", Occ.Block.Solids);
            // lemonade stand: 2 m clear
            foreach (var k in park.kept ?? new Transform[0]) if (k != null && k.name.StartsWith("SM_lemonade_shop", StringComparison.Ordinal)) occ.ReserveCircle(k.position, 2.2f, TownLifeTags.Feature + ".park.lemonade", Occ.Block.Solids);
            occ.ReserveCircle(V(ax + 2.75f, z0 + 3.65f), 2.2f, TownLifeTags.Feature + ".park.lemonade", Occ.Block.Solids);
        }

        // ---------------------------------------------------------------------------------------------- perimeter
        void Perimeter()
        {
            float i = HedgeInset, w = 6.2f, sw = 3.2f;              // hedge returns at the gates
            float gl = ax - GateHalf - 0.7f, gr = ax + GateHalf + 0.7f, ga = crossZ - SideGateHalf, gb = crossZ + SideGateHalf;
            var quads = new Dictionary<string, Vector3[]>
            {
                { "sw", new[] { V(gl, z0 + w), V(gl, z0 + i), V(x0 + i, z0 + i), V(x0 + i, ga), V(x0 + i + sw, ga) } },
                { "nw", new[] { V(x0 + i + sw, gb), V(x0 + i, gb), V(x0 + i, z1 - i), V(gl, z1 - i), V(gl, z1 - w) } },
                { "ne", new[] { V(gr, z1 - w), V(gr, z1 - i), V(x1 - i, z1 - i), V(x1 - i, gb), V(x1 - i - sw, gb) } },
                { "se", new[] { V(x1 - i - sw, ga), V(x1 - i, ga), V(x1 - i, z0 + i), V(gr, z0 + i), V(gr, z0 + w) } },
            };
            string hedge = "kit.panel.hedge.mid", rail = "kit.panel.rail.knee";
            foreach (var key in new[] { "sw", "nw", "ne", "se" })
            {
                var pts = quads[key];
                Kit.Run(gEdge, "park_hedge_" + key, pts, new Kit.RunSpec { panel = K(hedge), panelLength = PanelLength(hedge), collider = Kit.RunCollider.Box, colliderHeight = 1.3f, colliderThickness = 0.6f, y = GrassY });
                occ.ReservePolyline(pts, 0.45f, TownLifeTags.Hedge + ".park", Occ.Block.Solids);
                // low rail on the street face of the hedge (street-facing legs only)
                var street = new List<Vector3>();
                for (int k = 1; k < pts.Length - 1; k++) street.Add(Outset(pts[k], 0.62f));
                Kit.Run(gEdge, "park_rail_" + key, street, new Kit.RunSpec { panel = K(rail), panelLength = PanelLength(rail), y = GrassY, castShadows = false });
                // flower border on the park face
                var inner = new List<Vector3>();
                for (int k = 1; k < pts.Length - 1; k++) inner.Add(Outset(pts[k], -0.85f));
                FlowerRun("park_border_" + key, inner, key == "sw" || key == "ne" ? "mixed" : "yellow");
            }
        }
        /// <summary>Moves a perimeter point towards the street (positive) or into the park (negative).</summary>
        Vector3 Outset(Vector3 p, float d)
        {
            float cx = (x0 + x1) * 0.5f, cz = (z0 + z1) * 0.5f; bool nearX = Mathf.Abs(Mathf.Abs(p.x - cx) - (x1 - x0) * 0.5f + HedgeInset) < 0.05f, nearZ = Mathf.Abs(Mathf.Abs(p.z - cz) - (z1 - z0) * 0.5f + HedgeInset) < 0.05f;
            if (nearX) p.x += Mathf.Sign(p.x - cx) * d; if (nearZ) p.z += Mathf.Sign(p.z - cz) * d;
            return p;
        }

        // ----------------------------------------------------------------------------------------------- gateways
        void Gateways()
        {
            var gold = new Color(1f, 0.93f, 0.55f);
            foreach (bool south in new[] { true, false })
            {
                var at = south ? southArch : northArch; float into = south ? 1f : -1f, edge = south ? z0 : z1;
                var arch = Kit.Spawn(gFeature, "park.arch.big", G(at), south ? 180f : 0f);
                foreach (float x in new[] { -5.6f, 5.6f }) occ.ReserveCircle(at + V(x, 0), 0.8f, TagStruct);
                Kit.Label(arch.transform, "Text", "PICKLE PARK", new Vector3(0, 4.9f + 0.45f + 0.575f, 0.085f), Quaternion.Euler(0, 180, 0), new Vector2(6.0f, 1.0f), gold);
                Kit.Label(arch.transform, "TextBack", "COME BACK SOON", new Vector3(0, 4.9f + 0.45f + 0.575f, -0.085f), Quaternion.identity, new Vector2(6.0f, 0.8f), gold);
                // flower beds along the hedge returns facing the forecourt, tubs, bollards
                foreach (float sx in new[] { -1f, 1f })
                {
                    float hx = ax + sx * (GateHalf + 0.7f);
                    string tag = (south ? "s" : "n") + (sx < 0 ? "w" : "e");
                    FlowerRun("park_gatebed_" + tag, new[] { V(hx - sx * 0.85f, edge + into * (HedgeInset + 0.2f)), V(hx - sx * 0.85f, edge + into * 6.1f) }, "red");
                    FlowerRun("park_gatebed2_" + tag, new[] { V(hx + sx * 0.85f, edge + into * (HedgeInset + 0.9f)), V(hx + sx * 0.85f, edge + into * 6.1f), V(hx + sx * 6f, edge + into * 6.1f) }, "pink");
                    Put(gPlants, K("kit.shrub.cone"), V(hx, edge + into * 7.5f), 0, 1.25f, 0.5f, TagShrub);
                    Put(gFurn, K("kit.planter.tub"), V(ax + sx * 4.6f, edge + into * 8.3f), 0, 1.2f, 0.6f, TagFurn);
                }
            }
            foreach (bool west in new[] { true, false })
            {
                var at = west ? westArch : eastArch; float into = west ? 1f : -1f, edge = west ? x0 : x1;
                var arch = Kit.Spawn(gFeature, "park.arch.small", G(at), west ? 270f : 90f);
                foreach (float z in new[] { -3.3f, 3.3f }) occ.ReserveCircle(at + V(0, z), 0.7f, TagStruct);
                Kit.Label(arch.transform, "Text", "PICKLE PARK", new Vector3(0, 4.3f + 0.45f + 0.45f, 0.085f), Quaternion.Euler(0, 180, 0), new Vector2(4.2f, 0.75f), gold);
                Kit.Label(arch.transform, "TextBack", "MIND THE DUCKS", new Vector3(0, 4.3f + 0.45f + 0.45f, -0.085f), Quaternion.identity, new Vector2(4.2f, 0.62f), gold);
                foreach (float sz in new[] { -1f, 1f })
                {
                    string tag = (west ? "w" : "e") + (sz < 0 ? "s" : "n");
                    float gz = crossZ + sz * SideGateHalf;
                    FlowerRun("park_gatebed_" + tag, new[] { V(edge + into * (HedgeInset + 0.3f), gz - sz * 0.85f), V(edge + into * (HedgeInset + 3.2f), gz - sz * 0.85f) }, "red");
                    Put(gPlants, K("kit.shrub.cone"), V(edge + into * (HedgeInset + 4.5f), gz), 0, 1.15f, 0.5f, TagShrub);
                    Put(gFurn, K("kit.bollard"), V(edge + into * 1.2f, crossZ + sz * 2.6f), 0, 1f, 0.25f, TagFurn);
                }
            }
            // lemonade stand deck (the stand itself is legacy and floats at ~0.10)
            Vector3 stand = V(ax + 2.75f, z0 + 3.65f);
            foreach (var k in park.kept ?? new Transform[0]) if (k != null && k.name.StartsWith("SM_lemonade_shop", StringComparison.Ordinal)) stand = V(k.position.x, k.position.z);
            Kit.Extrude(gGround, "park_lemonade_deck", new[] { stand + V(-1.5f, -0.95f), stand + V(-1.5f, 0.95f), stand + V(1.5f, 0.95f), stand + V(1.5f, -0.95f) }, GrassY, 0.1f, "wood.warm", "wood.dark");
        }

        // ------------------------------------------------------------------------------------------ plaza + Pickle
        void PlazaAndPickle()
        {
            Kit.Disc(gGround, "park_plaza", plaza, PlazaR, "paving.warm", PavingY, true, 40);
            var m = new Kit.MeshData();
            void RingBand(string mat, float r0, float r1)
            {
                var a = Kit.Circle(plaza, r0, 40); var b = Kit.Circle(plaza, r1, 40);
                for (int i = 0; i < 40; i++) FlatQuad(m, mat, a[i], b[i], b[(i + 1) % 40], a[(i + 1) % 40], MarkingY);
            }
            RingBand("park.plaza.band", PlazaR - 0.9f, PlazaR - 0.3f);
            RingBand("park.plaza.light", 4.9f, 6.3f);
            RingBand("park.plaza.band", 6.3f, 6.6f);
            for (int i = 0; i < 16; i++)
            {
                float a = (i + 0.5f) * Mathf.PI / 8f; var dir = V(Mathf.Cos(a), Mathf.Sin(a));
                Line(m, i % 2 == 0 ? "park.plaza.band" : "park.plaza.light", new[] { plaza + dir * 6.9f, plaza + dir * (PlazaR - 1.2f) }, 0.4f, MarkingY);
            }
            Kit.Custom(gGround, "park_plaza_marks", m);

            // the Pickle: faces west (the promenade), wide-set eyes catch both gates
            Kit.Spawn(gFeature, "park.pickle.basin", G(plaza), 0f);
            Kit.Spawn(gFeature, "park.pickle.spouts", G(plaza), 0f);
            Kit.Spawn(gFeature, "park.pickle.plinth", G(plaza), 0f);
            Kit.SpawnRotated(gFeature, "park.pickle", V(plaza.x, GrassY + 1.5f, plaza.z), Quaternion.Euler(0f, 270f, 0f) * Quaternion.Euler(3f, 0f, 5f), Vector3.one);
            occ.ReserveCircle(plaza, 4.9f, TownLifeTags.Feature + ".park.pickle", Occ.Block.Everything);
            var ink = new Color(0.20f, 0.22f, 0.20f);
            Kit.SignBoard(gFeature, "park_plaque_w", V(plaza.x - 1.56f, GrassY + 0.86f, plaza.z), 270f, new Vector3(2.5f, 0.78f, 0.05f), "park.gold", "THE PICKLE\n<size=42%>NOBODY KNOWS WHO PUT IT HERE</size>", ink);
            Kit.SignBoard(gFeature, "park_plaque_s", V(plaza.x, GrassY + 0.86f, plaza.z - 1.56f), 180f, new Vector3(2.5f, 0.78f, 0.05f), "park.gold", "THE PICKLE\n<size=42%>PLEASE DO NOT LICK</size>", ink);
            Kit.SignBoard(gFeature, "park_plaque_n", V(plaza.x, GrassY + 0.86f, plaza.z + 1.56f), 0f, new Vector3(2.5f, 0.78f, 0.05f), "park.gold", "THE PICKLE\n<size=42%>EST. A WHILE AGO</size>", ink);

            // flower arcs + blossom trees behind it (east half), benches facing it, lamps
            FlowerRun("park_plaza_bed_ne", Arc(plaza, PlazaR + 0.75f, 12f, 102f, 12), "pink");
            FlowerRun("park_plaza_bed_se", Arc(plaza, PlazaR + 0.75f, 258f, 348f, 12), "pink");
            FlowerRun("park_plaza_bed_ne2", Arc(plaza, PlazaR + 1.5f, 14f, 100f, 12), "purple");
            FlowerRun("park_plaza_bed_se2", Arc(plaza, PlazaR + 1.5f, 260f, 346f, 12), "purple");
            int ti = 0;
            foreach (float deg in new[] { 25f, 50f, 75f, 285f, 310f, 335f })
            { var p = plaza + V(Mathf.Cos(deg * Mathf.Deg2Rad), Mathf.Sin(deg * Mathf.Deg2Rad)) * (PlazaR + 3.6f); Tree("blossom", "m", p.x, p.z, "plaza" + ti++, false, 1.15f); }
            foreach (float deg in new[] { 32f, 60f, 300f, 328f })
            {
                var dir = V(Mathf.Cos(deg * Mathf.Deg2Rad), Mathf.Sin(deg * Mathf.Deg2Rad)); var p = plaza + dir * (PlazaR - 1.7f);
                Bench(p.x, p.z, YawTo(p, plaza), true);
            }
            foreach (float deg in new[] { 14f, 346f, 84f, 276f })
            { var p = plaza + V(Mathf.Cos(deg * Mathf.Deg2Rad), Mathf.Sin(deg * Mathf.Deg2Rad)) * (PlazaR + 0.1f); Put(gFurn, K("kit.planter.tub"), p, 0, 1.25f, 0.6f, TagFurn, true); }
            foreach (float deg in new[] { 45f, 315f })
            { var p = plaza + V(Mathf.Cos(deg * Mathf.Deg2Rad), Mathf.Sin(deg * Mathf.Deg2Rad)) * (PlazaR - 1.6f); LampForced(p.x, p.z); }
            var bp = plaza + V(Mathf.Cos(70f * Mathf.Deg2Rad), Mathf.Sin(70f * Mathf.Deg2Rad)) * (PlazaR - 1.6f); Put(gFurn, K("kit.bin.park"), bp, 0, 1f, 0.4f, TagFurn, true);
            Sign("park_sign_pickle", plaza.x - 4.85f, plaza.z + 2.8f, 240f, "NO CLIMBING\n<size=60%>(the pickle is watching)</size>", 1.6f);
        }
        void LampForced(float x, float z)
        {
            var p = V(x, GrassY, z); var go = Kit.SpawnPrefab(gFurn, LampPrefab, p, 0f, 1f, true, true);
            if (go == null) Kit.Spawn(gFurn, K("kit.lamp.park"), p, 0f);
            occ.ReserveCircle(p, 0.6f, TagFurn); lamps++;
        }

        // ---------------------------------------------------------------------------------------------- play area
        void PlayArea()
        {
            var pen = park.playground;
            if (pen != null)
            {
                // raised safety-surface plinth under and around the legacy pen (which floats ~0.2 above the grass)
                Vector3 a = pen.quad.a, b = pen.quad.b, c = pen.quad.c, e = pen.quad.d, o = (a + b + c + e) * 0.25f;
                Vector3 u = (b - a); float hu = u.magnitude * 0.5f; u.Normalize(); Vector3 v = (e - a); float hv = v.magnitude * 0.5f; v.Normalize();
                Vector3[] Ring(float grow) { return new[] { o - u * (hu + grow) - v * (hv + grow), o + u * (hu + grow) - v * (hv + grow), o + u * (hu + grow) + v * (hv + grow), o - u * (hu + grow) + v * (hv + grow) }; }
                var m = new Kit.MeshData(); const float top = 0.088f;
                var r0 = Ring(0.85f); var r1 = Ring(1.15f); var r2 = Ring(1.75f);
                FlatQuad(m, "park.rubber", r0[0], r0[1], r0[2], r0[3], top);
                for (int i = 0; i < 4; i++)
                {
                    int j = (i + 1) % 4;
                    Vector3 p0 = r0[i], p1 = r0[j], q0 = r1[i], q1 = r1[j], s0 = r2[i], s1 = r2[j];
                    p0.y = p1.y = q0.y = q1.y = top; s0.y = s1.y = GrassY + 0.005f;
                    var inside = (p0 + p1) * 0.5f + Vector3.down * 20f;
                    m.QuadOut("park.cream", inside, p0, p1, q1, q0); m.QuadOut("park.rubber.alt", inside, q0, q1, s1, s0);
                }
                Kit.Custom(gGround, "park_play_plinth", m, true);
                occ.ReserveQuad(new TownLifePlan.Quad { a = r2[0], b = r2[1], c = r2[2], d = r2[3] }, TownLifeTags.PathSurface + ".park.playplinth", Occ.Block.Solids);
            }

            // open play patch west of the pen: more toys on a soft surface
            var patch = RoundRect(playExt.xMin, playExt.xMax, playExt.yMin, playExt.yMax, 3.5f, 5);
            Kit.Polygon(gGround, "park_play_patch", patch, "park.rubber", SoilY);
            var pm = new Kit.MeshData();
            Line(pm, "park.cream", patch, 0.35f, PavingY, true);
            // hopscotch + a painted snail of circles
            Vector3 hs = V(playExt.xMin + 2.6f, playExt.yMin + 2.2f);
            for (int i = 0; i < 7; i++)
            {
                bool dbl = i == 2 || i == 5; float z = hs.z + i * 0.95f;
                string[] cols = { "park.petal.yellow", "park.rubber.alt", "park.petal.white", "park.petal.purple" };
                if (dbl) { for (int k = -1; k <= 1; k += 2) { var cc = V(hs.x + k * 0.47f, z); FlatQuad(pm, cols[(i + k + 4) % 4], cc + V(-0.43f, -0.43f), cc + V(0.43f, -0.43f), cc + V(0.43f, 0.43f), cc + V(-0.43f, 0.43f), PavingY); } }
                else { var cc = V(hs.x, z); FlatQuad(pm, cols[i % 4], cc + V(-0.43f, -0.43f), cc + V(0.43f, -0.43f), cc + V(0.43f, 0.43f), cc + V(-0.43f, 0.43f), PavingY); }
            }
            for (int i = 0; i < 9; i++)
            {
                float a = i * 0.8f, rr = 0.5f + i * 0.28f; var cc = V(playExt.xMax - 4.2f + Mathf.Cos(a) * rr, playExt.yMax - 4.2f + Mathf.Sin(a) * rr);
                var ring = Kit.Circle(cc, 0.42f, 8); for (int k = 0; k < 8; k++) m9(pm, i % 2 == 0 ? "park.rubber.alt" : "park.petal.yellow", cc, ring[k], ring[(k + 1) % 8]);
            }
            Kit.Custom(gGround, "park_play_marks", pm);

            float cx = playExt.center.x, cz = playExt.center.y;
            Prefab("SM_sandbox_playground_01", cx - 2.6f, playExt.yMax - 3.4f, 0f, 1.7f);
            Prefab("SM_climbingrack", cx + 2.8f, cz - 1.2f, 90f, 1.8f);
            Prefab("SM_playground_object_01", cx - 1.2f, cz - 1.6f, 20f, 1.9f);
            Prefab("SM_playground_object_02", cx + 1.5f, playExt.yMin + 2.4f, -15f, 1.9f);
            Put(gFurn, "park.ball", V(cx - 3.5f, cz + 0.5f), 0, 1.6f, 0f, TagFurn, true, SoilY);

            // parents' strip south of the pen: benches looking at the kids, blossom row behind them
            float pz = parents.yMax - 0.9f; int n = 0;
            for (float x = parents.xMin + 15f; x < parents.xMax - 2f; x += 9f) { Bench(x, pz, 0f); n++; if (n % 2 == 0) Bin(x + 1.6f, pz); }
            for (float x = parents.xMin + 10.5f; x < parents.xMax; x += 9f)
            {
                if (Tree("blossom", "s", x, parents.yMax - 4.9f, "parents" + x.ToString("0"), false, 1.2f))
                    FlowerRun("park_parents_ring_" + n++, Arc(V(x, parents.yMax - 4.9f), 1.5f, 0f, 360f, 8), n % 2 == 0 ? "white" : "pink");
            }
            Bench(playExt.xMin + 1.0f, cz + 3.4f, 90f, true); Bench(playExt.xMin + 1.0f, cz + 0.2f, 90f, true);
            Lamp(playExt.xMax - 0.8f, playExt.yMin - 1.3f);
            Lamp(parents.xMax - 6f, pz + 0.3f);
            Kit.Spawn(gFeature, "park.balloons", G(V(playExt.xMax + 2.0f, playExt.yMax + 0.9f)), 30f);
            Sign("park_sign_play", playExt.xMax + 1.2f, playExt.yMax + 0.9f, 180f, "PLAYGROUND\n<size=60%>grown-ups must behave</size>", 1.7f);
        }
        static void m9(Kit.MeshData d, string mat, Vector3 c, Vector3 a, Vector3 b) { c.y = a.y = b.y = PavingY; d.TriOut(mat, c + Vector3.down * 20f, c, a, b); }
        void Prefab(string name, float x, float z, float yaw, float radius)
        {
            var p = V(x, SoilY, z);
            var go = Kit.SpawnPrefab(gFeature, Playground + name, p, yaw, 1f, true);
            if (go == null) { skipped++; skippedWhat.Add("missing prefab " + name); return; }
            occ.ReserveCircle(p, radius, TagStruct);
        }

        // --------------------------------------------------------------------------------------------------- pond
        float PondRadius(float a) { return 1f + 0.13f * Mathf.Sin(a * 2f + 0.6f) + 0.09f * Mathf.Sin(a * 3f + 2.1f) + 0.05f * Mathf.Sin(a * 5f + 1f); }
        Vector3 PondPoint(float a, float k) { float r = PondRadius(a) * k; return V(pond.x + Mathf.Cos(a) * pondR.x * r, pond.z + Mathf.Sin(a) * pondR.y * r); }
        List<Vector3> PondOutline(float k, float grow, int n = 28)
        {
            var l = new List<Vector3>();
            for (int i = 0; i < n; i++) { float a = i * Mathf.PI * 2f / n; var p = PondPoint(a, k); var dir = (p - pond).normalized; l.Add(p + dir * grow); }
            return l;
        }
        void Pond()
        {
            // water lies level, a little down in its hollow, whatever the park's slope does (terrain relief)
            Kit.LevelPad(gGround, "park_pond", pond, new Vector2(pondR.x * 2.6f + 3f, pondR.y * 2.6f + 3f), 0f, 5f, -0.2f);
            Kit.Polygon(gGround, "park_pond_bank", PondOutline(1f, 1.3f), "park.sand", SoilY);
            Kit.Polygon(gGround, "park_pond_water", PondOutline(1f, 0f), "water", PavingY);
            Kit.Polygon(gGround, "park_pond_deep", PondOutline(0.62f, 0f, 18), "park.water.deep", MarkingY);
            occ.ReservePolyline(PondOutline(0.55f, 0f, 14), 4.2f, TownLifeTags.Water2 + ".park", Occ.Block.Everything, true);
            occ.ReserveCircle(pond, 5.5f, TownLifeTags.Water2 + ".park", Occ.Block.Everything);

            var rng = Rng("pond");
            // jetty from the east bank, rowboat tied to it
            float ja = 0.12f; var jb = PondPoint(ja, 1f) + V(1.6f, 0.2f);
            Kit.Spawn(gFeature, "park.jetty", G(jb), 268f);
            Kit.Spawn(gFeature, "park.boat", V(jb.x - 5.2f, PavingY, jb.z + 1.7f), 200f);
            Kit.Ribbon(gGround, "park_path_jetty", new[] { jb + V(-0.4f, 0), V(picnic.xMin + 1f, jb.z + 0.5f) }, 1.7f, "gravel", SoilY);
            // reeds, lilies, boulders round the bank
            int idx = 0;
            foreach (float deg in new[] { 60f, 78f, 96f, 150f, 168f, 200f, 215f, 262f, 280f, 318f })
            {
                float a = deg * Mathf.Deg2Rad; var p = PondPoint(a, 0.96f);
                Kit.Spawn(gPlants, K("kit.reeds"), G(p), TownLifeContext.Range(rng, 0, 360), TownLifeContext.Range(rng, 1.0f, 1.5f));
                if (idx % 2 == 0) Kit.Spawn(gPlants, K("kit.reeds"), G(PondPoint(a + 0.07f, 0.9f)), TownLifeContext.Range(rng, 0, 360), TownLifeContext.Range(rng, 0.9f, 1.3f));
                idx++;
            }
            foreach (float deg in new[] { 40f, 120f, 185f, 240f, 300f, 345f })
            { var p = PondPoint(deg * Mathf.Deg2Rad, 0.72f); Kit.Spawn(gPlants, K("kit.lilypad"), V(p.x, PavingY + 0.012f, p.z), TownLifeContext.Range(rng, 0, 360), TownLifeContext.Range(rng, 1.1f, 1.6f)); }
            foreach (float deg in new[] { 30f, 132f, 232f, 250f, 335f })
            { var p = PondPoint(deg * Mathf.Deg2Rad, 1.08f); Kit.Spawn(gPlants, K(deg > 200f ? "kit.boulder.m" : "kit.boulder.s"), G(p), TownLifeContext.Range(rng, 0, 360), TownLifeContext.Range(rng, 0.8f, 1.2f)); }
            // derpy ducks: a convoy on the water, two loafing on the bank, one marching to the plaza
            Vector3 lead = PondPoint(2.4f, 0.35f);
            for (int i = 0; i < 4; i++) Kit.Spawn(gFeature, K("kit.duck"), V(lead.x + i * 1.15f, PavingY - 0.03f, lead.z - i * 0.5f + (i % 2) * 0.2f), 290f + i * 6f, i == 0 ? 1.9f : 1.25f);
            var bank = PondPoint(4.4f, 1.12f);
            Kit.Spawn(gFeature, K("kit.duck"), G(bank), 150f, 1.6f); Kit.Spawn(gFeature, K("kit.duck"), G(bank + V(0.8f, 0.5f)), 20f, 1.5f);
            // ... and a family crossing the E-W path in single file
            for (int i = 0; i < 5; i++) Kit.Spawn(gFeature, K("kit.duck"), V(pond.x + pondR.x + 6f - i * 0.25f, i < 2 ? PavingY : GrassY, crossZ - 0.9f + i * 0.85f + (i == 0 ? -0.35f : 0f)), 170f + (i % 2) * 14f, i == 0 ? 1.7f : 0.85f);
            // willows + birches round the water, benches looking at it
            Tree("willow", "l", pond.x - pondR.x - 1.4f, pond.z + pondR.y - 0.5f, "pond-w1", true);
            Tree("willow", "l", pond.x + 5f, pond.z + pondR.y + 0.9f, "pond-w2", true);
            Tree("birch", "m", pond.x - pondR.x + 0.2f, pond.z - pondR.y - 1.0f, "pond-b1");
            Tree("birch", "l", pond.x - pondR.x + 3.4f, pond.z - pondR.y - 2.8f, "pond-b2");
            Tree("birch", "m", pond.x + pondR.x + 1.8f, pond.z - pondR.y + 0.2f, "pond-b3");
            foreach (float bx in new[] { -4.5f, 0.5f, 5.5f }) { var p = V(pond.x + bx, pond.z - pondR.y - 3.3f); Bench(p.x, p.z, 0f); }
            Bin(pond.x + 3f, pond.z - pondR.y - 3.3f);
            Lamp(pond.x - 2f, pond.z - pondR.y - 3.6f);
            Sign("park_sign_ducks", pond.x + 8.2f, pond.z - pondR.y - 2.2f, 200f, "DUCK POND\n<size=60%>the ducks have right of way</size>", 1.7f);
            foreach (float deg in new[] { 20f, 100f, 140f, 225f, 290f }) { var p = PondPoint(deg * Mathf.Deg2Rad, 1.2f); Shrub("kit.grass.tuft", p.x, p.z, "pondtuft" + deg, 1.6f); }
        }

        // ------------------------------------------------------------------------------------------------- picnic
        void Picnic()
        {
            Stripes("park_picnic_stripes", picnic, 2.2f, true);
            occ.ReserveRect(Rect.MinMaxRect(picnic.xMin + 5f, picnic.yMin + 6f, picnic.xMax - 5f, picnic.yMax - 5f), TownLifeTags.Lawn + ".park.picnic", Occ.Block.Marker);
            Tree("oak", "xl", picnic.xMin + 3.5f, picnic.yMax - 3.5f, "picnic-o1", true);
            Tree("oak", "l", picnic.xMax - 4.5f, picnic.yMax - 2.2f, "picnic-o2", true);
            Tree("oak", "xl", picnic.xMin + 9.5f, picnic.yMin + 4f, "picnic-o3", true);
            Tree("oak", "l", picnic.xMax - 3.2f, picnic.yMin + 9.5f, "picnic-o4", true);
            float cx = picnic.center.x, cz = picnic.center.y;
            Put(gFurn, K("kit.picnic.table"), V(cx - 5f, cz + 3f), 15f, 1f, 1.3f, TagFurn, true);
            Put(gFurn, K("kit.picnic.table"), V(cx + 1.5f, cz + 5.2f), -20f, 1f, 1.3f, TagFurn, true);
            Put(gFurn, K("kit.picnic.table"), V(cx + 3.8f, cz - 2.2f), 70f, 1f, 1.3f, TagFurn, true);
            Put(gFurn, K("kit.picnic.table"), V(cx - 4.2f, cz - 4.5f), 100f, 1f, 1.3f, TagFurn, true);
            Put(gFurn, "park.bbq", V(cx - 1.2f, cz + 0.4f), 30f, 1.15f, 0.6f, TagFurn, true);
            Put(gFurn, "park.blanket", V(cx + 6.2f, cz + 1.8f), 25f, 1.1f, 1.2f, TagFurn, true, SoilY + 0.03f);
            Bin(cx - 7.5f, cz + 0.5f);
            Sign("park_sign_picnic", picnic.xMax - 1.6f, picnic.yMin + 5f, 110f, "PICNIC LAWN\n<size=60%>ants welcome</size>", 1.6f);
        }

        /// <summary>Mown stripes inside a rect (rounded ends are faked by shortening the outer stripes).</summary>
        void Stripes(string name, Rect r, float width, bool alongX)
        {
            var m = new Kit.MeshData(); float span = alongX ? r.height : r.width; int n = Mathf.Max(2, Mathf.RoundToInt(span / width)); float w = span / n;
            for (int i = 0; i < n; i += 2)
            {
                float t = (i + 0.5f) / n, cut = Mathf.Abs(2f * t - 1f); cut = 0f;
                if (alongX) FlatQuad(m, "park.lawn.light", V(r.xMin + cut, r.yMin + i * w), V(r.xMax - cut, r.yMin + i * w), V(r.xMax - cut, r.yMin + (i + 1) * w), V(r.xMin + cut, r.yMin + (i + 1) * w), SoilY);
                else FlatQuad(m, "park.lawn.light", V(r.xMin + i * w, r.yMin + cut), V(r.xMin + (i + 1) * w, r.yMin + cut), V(r.xMin + (i + 1) * w, r.yMax - cut), V(r.xMin + i * w, r.yMax - cut), SoilY);
            }
            Kit.Custom(gGround, name, m);
        }

        // ---------------------------------------------------------------------------------------------- bandstand
        void Bandstand()
        {
            Kit.Disc(gGround, "park_band_apron", bandstand, 6.6f, "paving.warm", PavingY, false, 28);
            var oct1 = new List<Vector3>(); var oct2 = new List<Vector3>();
            for (int i = 0; i < 8; i++) { float a = (i + 0.5f) * Mathf.PI / 4f; oct1.Add(bandstand + V(Mathf.Cos(a), Mathf.Sin(a)) * 5.0f); oct2.Add(bandstand + V(Mathf.Cos(a), Mathf.Sin(a)) * 4.3f); }
            Kit.Extrude(gGround, "park_band_step1", oct1, GrassY, 0.22f, "stone.light", "stone.dark", true, true);
            Kit.Extrude(gGround, "park_band_step2", oct2, 0.22f, 0.5f, "wood.warm", "park.cream", true, true);
            // open bay faces the audience lawn (west): prop +Z -> world -X
            Kit.Spawn(gFeature, "park.bandstand", V(bandstand.x, 0.5f, bandstand.z), 270f);
            occ.ReserveCircle(bandstand, 5.4f, TagStruct, Occ.Block.Everything);
            occ.ReserveCircle(bandstand, 6.6f, TownLifeTags.PathSurface + ".park.apron", Occ.Block.Solids);

            Stripes("park_band_stripes", bandLawn, 2.25f, false);
            // audience: benches in an arc on the lawn edge, a blanket, flower tubs at the steps
            foreach (float deg in new[] { 152f, 176f, 200f })
            { var p = bandstand + V(Mathf.Cos(deg * Mathf.Deg2Rad), Mathf.Sin(deg * Mathf.Deg2Rad)) * 9.4f; Bench(p.x, p.z, YawTo(p, bandstand), true); }
            Put(gFurn, "park.blanket", V(bandLawn.center.x - 1f, bandLawn.center.y + 1f), -30f, 1.1f, 1.2f, TagFurn, true, SoilY + 0.03f);
            foreach (float deg in new[] { 150f, 210f }) { var p = bandstand + V(Mathf.Cos(deg * Mathf.Deg2Rad), Mathf.Sin(deg * Mathf.Deg2Rad)) * 5.9f; Put(gFurn, K("kit.planter.tub"), p, 0, 1.2f, 0.6f, TagFurn, true); }
            LampForced(bandstand.x - 4.8f, bandstand.z - 5.6f); LampForced(bandstand.x - 4.8f, bandstand.z + 5.6f);
            // backdrop: a horseshoe of pickle trees and flowers behind the bandstand
            int k = 0;
            foreach (float deg in new[] { -70f, -42f, -14f, 14f, 42f, 70f })
            { var p = bandstand + V(Mathf.Cos(deg * Mathf.Deg2Rad), Mathf.Sin(deg * Mathf.Deg2Rad)) * 8.3f; Tree("pickle", k % 2 == 0 ? "l" : "m", p.x, p.z, "band" + k++, true); }
            FlowerRun("park_band_bed", Arc(bandstand, 7.1f, -80f, 80f, 10), "yellow");
            Kit.Spawn(gFeature, "park.balloons", G(V(bandstand.x - 5.9f, bandstand.z - 3.1f)), 200f);
            Sign("park_sign_band", bandstand.x - 6.6f, bandstand.z - 3.3f, 250f, "TONIGHT:\n<size=70%>ONE TUBA, NO REFUNDS</size>", 1.7f);
        }

        // -------------------------------------------------------------------------------------------------- court
        void Court()
        {
            Kit.Polygon(gGround, "park_court", new[] { V(court.xMin, court.yMin), V(court.xMin, court.yMax), V(court.xMax, court.yMax), V(court.xMax, court.yMin) }, "park.court", PavingY);
            var m = new Kit.MeshData(); float cz = court.center.y, bx = court.xMax - 0.6f;      // baseline on the east
            FlatQuad(m, "park.court.key", V(bx - 5.8f, cz - 2.45f), V(bx, cz - 2.45f), V(bx, cz + 2.45f), V(bx - 5.8f, cz + 2.45f), MarkingY - 0.012f);
            Line(m, "paint.white", new[] { V(court.xMin + 0.5f, court.yMin + 0.5f), V(bx, court.yMin + 0.5f), V(bx, court.yMax - 0.5f), V(court.xMin + 0.5f, court.yMax - 0.5f) }, 0.12f, MarkingY, true);
            Line(m, "paint.white", new[] { V(bx, cz - 2.45f), V(bx - 5.8f, cz - 2.45f), V(bx - 5.8f, cz + 2.45f), V(bx, cz + 2.45f) }, 0.12f, MarkingY);
            Line(m, "paint.white", Arc(V(bx - 5.8f, cz), 1.8f, 90f, 270f, 10), 0.12f, MarkingY);
            Line(m, "paint.white", Arc(V(bx - 1.2f, cz), 5.7f, 108f, 252f, 14), 0.12f, MarkingY);
            Kit.Custom(gGround, "park_court_marks", m);
            Kit.Spawn(gFeature, "park.hoop", G(V(court.xMax + 0.35f, cz)), 270f); occ.ReserveCircle(V(court.xMax + 0.35f, cz), 0.5f, TagStruct);
            Kit.Spawn(gFurn, "park.ball", V(bx - 4.1f, PavingY, cz + 3.3f), 0f, 1f);
            string rail = "kit.panel.rail.post";
            var back = new[] { V(court.xMax + 0.9f, court.yMin - 0.3f), V(court.xMax + 0.9f, court.yMax + 0.3f) };
            Kit.Run(gEdge, "park_court_rail", back, new Kit.RunSpec { panel = K(rail), panelLength = PanelLength(rail), y = GrassY, castShadows = false });
            occ.ReservePolyline(back, 0.3f, TagStruct);
            Bench(court.xMin + 3.5f, court.yMax + 1.3f, 180f, true); Bench(court.xMin + 9f, court.yMax + 1.3f, 180f, true);
            Bin(court.xMin + 6.2f, court.yMax + 1.3f);
            LampForced(court.xMin - 0.9f, court.yMax + 0.9f);
            FlowerRun("park_court_bed", new[] { V(court.xMin + 0.5f, court.yMin - 0.9f), V(court.xMax - 0.5f, court.yMin - 0.9f) }, "white");
        }

        void SunLawn()
        {
            Stripes("park_sun_stripes", sunLawn, 2.05f, false);
            float cx = sunLawn.center.x, cz = sunLawn.center.y;
            Put(gFurn, "park.blanket", V(cx - 1.5f, cz + 2.5f), 40f, 1.1f, 1.2f, TagFurn, true, SoilY + 0.03f);
            Put(gFurn, "park.blanket", V(cx + 2.4f, cz - 4.2f), -15f, 1.1f, 1.2f, TagFurn, true, SoilY + 0.03f);
            Put(gFurn, K("kit.gnome"), V(cx + 3.6f, cz + 5.2f), 200f, 1.6f, 0.3f, TagFurn, true);
            Sign("park_sign_lawn", sunLawn.xMin + 1.2f, sunLawn.yMin - 0.9f, 180f, "KEEP ON THE GRASS", 1.9f);
        }

        // ---------------------------------------------------------------------------------- promenade + cross path
        void Promenade()
        {
            float off = PromenadeWidth * 0.5f;
            // avenue of tall elms, lamps and benches both sides; nothing ever inside the paving
            foreach (float side in new[] { -1f, 1f })
            {
                int i = 0;
                for (float z = z0 + 14.25f; z <= z1 - 11f; z += 8f, i++)
                {
                    string key = (side < 0 ? "w" : "e") + i;
                    Tree("elm", "l", ax + side * (off + 2.7f), z, "avenue-" + key, false, 1.1f);
                    float zl = z + 4f; if (Mathf.Abs(zl - loop.yMax) < 2f || Mathf.Abs(zl - loop.yMin) < 2f) zl -= 1.6f;
                    if (i % 2 == 0) Lamp(ax + side * (off + 0.9f), zl, side < 0 ? 90f : 270f);
                    else Bench(ax + side * (off + 1.1f), zl, side < 0 ? 90f : 270f);
                }
            }
            Bin(ax - off - 1.0f, z0 + 21f); Bin(ax + off + 1.0f, z1 - 15f);
        }
        void CrossPath()
        {
            // maples, benches and lamps along the E-W path where the neighbouring zones leave room
            float h = CrossWidth * 0.5f, zn = crossZ + h + 2.4f, zs = crossZ - h - 2.4f;
            Tree("maple", "m", picnic.xMin + 3f, zn, "cross-n0"); Tree("maple", "m", picnic.xMin + 13f, zn, "cross-n1");
            Tree("maple", "m", bandstand.x - 5.5f, zn, "cross-n2"); Tree("maple", "m", bandstand.x + 5.5f, zn, "cross-n3");
            Bench(picnic.xMin + 8f, crossZ + h + 1.3f, 180f);
            Lamp(picnic.xMin - 4f, crossZ + h + 0.9f); Lamp(bandstand.x - 3.2f, crossZ + h + 0.9f); Lamp(x1 - 13f, crossZ - h - 0.9f); Lamp(x0 + 13f, crossZ + h + 0.9f);
        }

        // ------------------------------------------------------------------------------------------------- groves
        void Groves()
        {
            // SE between court and the cross path: a mixed grove with a table in the shade
            float gx = court.center.x, gz = crossZ - 4.4f;
            Tree("maple", "l", gx - 4.2f, gz, "se1"); Tree("round", "l", gx + 5.4f, gz - 0.5f, "se2"); Tree("birch", "m", gx + 0.8f, gz - 3.9f, "se3");
            Put(gFurn, K("kit.picnic.table"), V(gx + 0.6f, gz + 0.2f), 35f, 1f, 1.3f, TagFurn);
            Shrub("kit.shrub.flower.yellow", gx - 2f, gz - 2.6f, "se-s1"); Shrub("kit.shrub.round.m", gx + 3.4f, gz - 2.6f, "se-s2");
            // NE: behind the audience lawn
            Tree("round", "l", bandLawn.xMax + 2.2f, bandLawn.yMax - 0.5f, "ne1");
            // W: between the play patch, the west gate and the loop
            Tree("round", "l", playExt.xMin + 0.4f, parents.yMin + 3.5f, "sw1"); Tree("maple", "m", playExt.xMin + 5.5f, parents.yMin + 2.2f, "sw2"); Tree("round", "m", playExt.xMin + 1.2f, playExt.yMin - 2.6f, "sw3");
            // picnic / pond gap
            Tree("round", "l", picnic.xMin - 1.2f, picnic.yMax - 1.2f, "nw1");
            // flower patches at the feet of things, so no lawn edge is bare
            var fr = Rng("patches"); string[] fc = { "red", "yellow", "pink", "purple", "white", "mixed" };
            var spots = new[] {
                V(picnic.xMin + 1.5f, picnic.yMin + 1.5f), V(picnic.xMax - 6f, picnic.yMax - 0.5f), V(picnic.xMin + 6f, picnic.yMax - 1.5f),
                V(sunLawn.xMin - 0.8f, sunLawn.yMax + 0.8f), V(sunLawn.xMax + 0.9f, sunLawn.yMin + 1f), V(sunLawn.xMax + 0.9f, sunLawn.yMax - 1f),
                V(bandLawn.xMin - 0.8f, bandLawn.yMin - 0.6f), V(bandLawn.xMax + 0.6f, bandLawn.yMin - 0.9f), V(bandLawn.xMin + 2f, bandLawn.yMax + 0.5f),
                V(playExt.xMin - 0.9f, playExt.yMax + 0.5f), V(playExt.xMax - 2f, playExt.yMin - 1.4f), V(parents.xMin + 4f, parents.yMin + 4.5f),
                V(court.xMin - 1.1f, court.yMin + 2f), V(court.xMin - 1.1f, court.yMax - 2f), V(court.xMax + 1.7f, court.yMax + 1.5f),
                V(pond.x + pondR.x + 2.6f, pond.z + 4.5f), V(pond.x - 2f, pond.z - pondR.y - 2.2f), V(pond.x + 7.5f, pond.z - pondR.y - 2.6f),
                V(bandstand.x + 3f, crossZ + 5.2f), V(bandstand.x - 8f, crossZ + 6.2f), V(court.center.x + 1f, crossZ - 9.2f), V(ax - 8.5f, crossZ + 3.6f), V(ax - 9.5f, z0 + 15.5f), V(ax + 6.9f, z1 - 13f) };
            for (int i = 0; i < spots.Length; i++)
            {
                if (!occ.Free(spots[i], 0.5f, Occ.What.Flat)) continue;
                Kit.Spawn(gPlants, K("kit.flowers." + fc[i % fc.Length]), G(spots[i]), TownLifeContext.Range(fr, 0, 360), TownLifeContext.Range(fr, 1.2f, 1.7f));
                Kit.Spawn(gPlants, K("kit.flowers." + fc[(i + 2) % fc.Length]), G(spots[i] + V(TownLifeContext.Range(fr, -1.2f, 1.2f), TownLifeContext.Range(fr, 0.8f, 1.3f))), TownLifeContext.Range(fr, 0, 360), TownLifeContext.Range(fr, 1.0f, 1.4f));
            }
            // a kite that did not make it
            Kit.Spawn(gFeature, "park.kite", V(ax + 5.2f + 1.6f, 6.8f, z0 + 14.25f + 0.6f), 40f, 1.4f);
        }

        // ------------------------------------------------------------------------------------- perimeter planting
        void PerimeterPlanting()
        {
            // tall trees in the band between hedge and loop, species per side, tightened into a rhythm, skipping gates
            float inset = 6.6f; int i = 0;
            void Row(Vector3 a, Vector3 b, string species, string key, float gateAt, float gateHalf)
            {
                float len = Vector3.Distance(a, b); int n = Mathf.Max(2, Mathf.RoundToInt(len / 9.2f));
                for (int k = 0; k <= n; k++)
                {
                    float t = k / (float)n; var p = Vector3.Lerp(a, b, t); float along = Mathf.Abs(a.x - b.x) > 1f ? p.x : p.z;
                    var rng = Rng("edge/" + key + k);
                    if (Mathf.Abs(along - gateAt) < gateHalf) continue;
                    var dir = (b - a).normalized; var inward = new Vector3(-dir.z, 0, dir.x); if (Vector3.Dot(inward, V((x0 + x1) * 0.5f, (z0 + z1) * 0.5f) - p) < 0) inward = -inward;
                    p += dir * TownLifeContext.Range(rng, -1f, 1f) + inward * TownLifeContext.Range(rng, -0.6f, 1.2f);
                    string sp = species, size = "l";
                    if (k % 4 == 2) { sp = "round"; size = "l"; }
                    if (species == "oak" && k % 3 == 0) size = "xl";
                    Tree(sp, size, p.x, p.z, "edge-" + key + k);
                    // understorey between the trees
                    var q = Vector3.Lerp(a, b, (k + 0.5f) / n) + inward * TownLifeContext.Range(rng, -1.6f, 0.4f);
                    if (k < n && Mathf.Abs((Mathf.Abs(a.x - b.x) > 1f ? q.x : q.z) - gateAt) > gateHalf - 2f)
                    {
                        string[] shrubs = { "kit.shrub.round.m", "kit.shrub.flower.pink", "kit.shrub.round.s", "kit.shrub.flower.white", "kit.shrub.round.m", "kit.shrub.flower.purple" };
                        Shrub(shrubs[(i + k) % shrubs.Length], q.x, q.z, "edge-" + key + k, 1.15f);
                        if (k % 2 == 0) Shrub(shrubs[(i + k + 3) % shrubs.Length], q.x + dir.x * 1.5f + inward.x * 0.9f, q.z + dir.z * 1.5f + inward.z * 0.9f, "edge2-" + key + k);
                    }
                }
                i += 2;
            }
            Row(V(x0 + inset, z0 + inset), V(x1 - inset, z0 + inset), "maple", "s", ax, GateHalf + 2.5f);
            Row(V(x0 + inset, z1 - inset), V(x1 - inset, z1 - inset), "birch", "n", ax, GateHalf + 2.5f);
            Row(V(x0 + inset, z0 + inset + 8f), V(x0 + inset, z1 - inset - 8f), "elm", "w", crossZ, SideGateHalf + 2.5f);
            Row(V(x1 - inset, z0 + inset + 8f), V(x1 - inset, z1 - inset - 8f), "pickle", "e", crossZ, SideGateHalf + 2.5f);
            // corner anchors: one big oak in each corner bed
            Tree("oak", "xl", x0 + 9.5f, z0 + 9.5f, "corner-sw"); Tree("oak", "xl", x1 - 9.5f, z0 + 9.5f, "corner-se");
            Tree("oak", "xl", x0 + 9.5f, z1 - 9.5f, "corner-nw"); Tree("oak", "xl", x1 - 9.5f, z1 - 9.5f, "corner-ne");
        }
    }
}
