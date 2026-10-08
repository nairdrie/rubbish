// Owner: edges agent. Contract: design/CONTRACT.md.
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>The town's edge: woodland belts and tree-topped hills to the south and west, the riverside promenade to the
/// north, the coast strip to the east, Depot Row planting, the planted district seam, and a sweep-up that turns every
/// leftover piece of common land into a grove or meadow patch. Group: ctx.Group("Edges").
/// Runs LAST: everything here respects the reservations of all earlier modules.</summary>
public static class TownLifeEdges
{
    /// <summary>Module name used in options, reports and RNG keys.</summary>
    public const string Name = "edges";

    const string TagTree = "tree.edge", TagShrub = "shrub.edge", TagFurniture = "furniture.edge", TagPaving = "paving.edge";
    const string LampPrefab = "Assets/Suburbs Town Pack/PREFABS/Props/SM_streetlamp_02.prefab";

    // ---------------------------------------------------------------------------------------------------- bank tables
    // First non-flat terrain cell, read off the terrain collider (understand/geometry/occupancy_2m.json, 2 m cells,
    // min over a 12 m window). The terrain is an 8 m grid so the bank top steps. 0 / large values = under a bridge.
    const float RiverX0 = 53.75f, CoastZ0 = -50.25f, TableStep = 8f;
    static readonly float[] RiverTop =
    {
        283.75f,281.75f,277.75f,277.75f,277.75f,277.75f,277.75f,277.75f,277.75f,283.75f,283.75f,283.75f,283.75f,283.75f,283.75f,283.75f,283.75f,285.75f,283.75f,283.75f,
        283.75f,283.75f,283.75f,283.75f,283.75f,283.75f,281.75f,279.75f,277.75f,277.75f,277.75f,277.75f,277.75f,277.75f,277.75f,277.75f,277.75f,277.75f,277.75f,279.75f,
        283.75f,283.75f,283.75f,283.75f,283.75f,283.75f,283.75f,283.75f,283.75f,285.75f,285.75f,285.75f,285.75f,285.75f,287.75f,289.75f,289.75f,287.75f,285.75f,285.75f,
        285.75f,285.75f,285.75f,285.75f,283.75f,283.75f,283.75f,283.75f,283.75f,283.75f,283.75f,283.75f,283.75f,283.75f,283.75f,283.75f,283.75f,283.75f,283.75f,283.75f,
        283.75f,285.75f,285.75f,287.75f,287.75f,287.75f,285.75f,285.75f,285.75f,285.75f,285.75f,283.75f,283.75f,283.75f,271.75f,251.75f,249.75f,249.75f,249.75f
    };
    static readonly float[] RiverWater =
    {
        287.75f,285.75f,285.75f,283.75f,281.75f,281.75f,281.75f,283.75f,285.75f,285.75f,285.75f,285.75f,285.75f,287.75f,287.75f,287.75f,287.75f,287.75f,287.75f,287.75f,
        287.75f,287.75f,285.75f,285.75f,285.75f,285.75f,285.75f,285.75f,283.75f,283.75f,283.75f,281.75f,281.75f,281.75f,281.75f,283.75f,283.75f,283.75f,285.75f,285.75f,
        285.75f,287.75f,287.75f,287.75f,285.75f,287.75f,287.75f,287.75f,289.75f,289.75f,291.75f,291.75f,291.75f,293.75f,293.75f,293.75f,293.75f,293.75f,293.75f,291.75f,
        291.75f,291.75f,289.75f,287.75f,287.75f,287.75f,287.75f,285.75f,285.75f,285.75f,285.75f,285.75f,285.75f,285.75f,285.75f,285.75f,285.75f,287.75f,287.75f,287.75f,
        289.75f,291.75f,293.75f,293.75f,293.75f,293.75f,291.75f,291.75f,291.75f,289.75f,289.75f,287.75f,287.75f,287.75f,287.75f,283.75f,267.75f,249.75f,249.75f
    };
    static readonly float[] CoastTop =
    {
        831.75f,827.75f,825.75f,825.75f,823.75f,823.75f,819.75f,817.75f,817.75f,817.75f,817.75f,815.75f,815.75f,815.75f,815.75f,817.75f,817.75f,817.75f,817.75f,819.75f,
        823.75f,823.75f,825.75f,825.75f,825.75f,829.75f,831.75f,831.75f,833.75f,833.75f,833.75f,833.75f,833.75f,831.75f,829.75f,825.75f,823.75f,821.75f,817.75f,815.75f,
        811.75f,805.75f,805.75f
    };

    // far (north) bank: smallest z that is flat land again (max over a 20 m window); 999 = beyond the area
    static readonly float[] RiverFar =
    {
        339.75f,339.75f,337.75f,331.75f,331.75f,331.75f,999f,999f,999f,999f,999f,339.75f,339.75f,339.75f,339.75f,339.75f,339.75f,339.75f,339.75f,339.75f,
        339.75f,339.75f,337.75f,337.75f,337.75f,337.75f,333.75f,331.75f,331.75f,331.75f,331.75f,331.75f,331.75f,331.75f,331.75f,331.75f,331.75f,331.75f,331.75f,331.75f,
        337.75f,337.75f,337.75f,337.75f,339.75f,339.75f,339.75f,339.75f,339.75f,339.75f,339.75f,339.75f,339.75f,339.75f,339.75f,339.75f,339.75f,339.75f,339.75f,339.75f,
        339.75f,339.75f,339.75f,339.75f,339.75f,339.75f,337.75f,337.75f,337.75f,337.75f,335.75f,333.75f,335.75f,337.75f,337.75f,337.75f,335.75f,339.75f,339.75f,339.75f,
        339.75f,339.75f,339.75f,339.75f,339.75f,339.75f,339.75f,339.75f,339.75f,339.75f,339.75f,339.75f,339.75f,339.75f,341.75f,341.75f,341.75f,341.75f,341.75f
    };
    static float Table(float[] t, float origin, float v, bool minimum)
    {
        float f = (v - origin) / TableStep; int i = Mathf.Clamp(Mathf.FloorToInt(f), 0, t.Length - 1), j = Mathf.Min(t.Length - 1, i + 1);
        return minimum ? Mathf.Min(t[i], t[j]) : Mathf.Lerp(t[i], t[j], Mathf.Clamp01(f - i));
    }
    /// <summary>Largest z that is certainly still flat land at this x (river bank top, conservative).</summary>
    static float RiverSafe(float x) { return Mathf.Min(Table(RiverTop, RiverX0, x - 6f, true), Table(RiverTop, RiverX0, x + 6f, true)); }
    /// <summary>z where the terrain is certainly below the water plane.</summary>
    static float RiverWet(State s, float x)
    {
        var r = s.scene.river; float zc = Table(RiverWater, RiverX0, x, false) + 21f;
        for (int i = 1; i < r.Length; i++)
            if ((r[i - 1].x <= x && x <= r[i].x) || (r[i].x <= x && x <= r[i - 1].x)) { zc = Mathf.Lerp(r[i - 1].z, r[i].z, Mathf.InverseLerp(r[i - 1].x, r[i].x, x)); break; }
        return Mathf.Max(zc - 18.6f, Table(RiverWater, RiverX0, x, false) + 1.2f);
    }
    /// <summary>Largest x that is certainly still flat land at this z (coastal bank top, conservative).</summary>
    static float CoastSafe(float z) { return Mathf.Min(Table(CoastTop, CoastZ0, z - 6f, true), Table(CoastTop, CoastZ0, z + 6f, true)); }

    // ---------------------------------------------------------------------------------------------------- state
    sealed class Hill { public Vector3 c; public Vector2 r; public float h; }

    sealed class State
    {
        public TownLifeContext ctx; public TownLifeScene scene; public TownLifePlan.Result plan; public TownLifeOccupancy occ;
        public Transform root, wood, river, coast, depot, hills, seam;
        public int gw, gh; public float cell = 4f; public float[] dist; public bool[] wild;
        public Vector3[] ring; public List<Hill> hillList = new List<Hill>();
        public float density;
        public Dictionary<string, int> counts = new Dictionary<string, int>();
        public void Count(string k, int n = 1) { counts.TryGetValue(k, out int v); counts[k] = v + n; }
    }

    // ---------------------------------------------------------------------------------------------------- prop ids
    /// <summary>Stock id when the kit has it, else a local stand-in so the module always runs.</summary>
    static string P(string id)
    {
        if (TownLifeKit.HasProp(id)) return id;
        if (id.StartsWith("kit.clump")) return "edges.fb.clump";
        if (id.StartsWith("kit.tree")) return "kit.tree.round";
        if (id.StartsWith("kit.panel")) return "edges.fb.panel";
        if (id.StartsWith("kit.post")) return "edges.fb.post";
        if (id.StartsWith("kit.bench")) return "edges.fb.bench";
        if (id.StartsWith("kit.boulder")) return "edges.fb.boulder";
        if (id.StartsWith("kit.duck")) return "edges.fb.duck";
        if (id.StartsWith("kit.reeds") || id.StartsWith("kit.grass")) return "edges.fb.tuft";
        if (id.StartsWith("kit.flowers") || id.StartsWith("kit.shrub.flower")) return "edges.fb.flower";
        if (id.StartsWith("kit.lilypad")) return "edges.fb.lily";
        if (id.StartsWith("kit.signpost") || id.StartsWith("kit.bin") || id.StartsWith("kit.planter") || id.StartsWith("kit.bollard")) return "edges.fb.post";
        return "kit.shrub.round";
    }
    /// <summary>Extra scale that makes the stand-in tree roughly the size the stock id promises.</summary>
    static float FS(string id)
    {
        if (TownLifeKit.HasProp(id) || !id.StartsWith("kit.tree")) return 1f;
        if (id.EndsWith(".xl")) return 1.8f; if (id.EndsWith(".l")) return 1.4f; if (id.EndsWith(".s")) return 0.6f; return 1f;
    }

    static void DefineAll()
    {
        TownLifeKit.Define("edges.sand", new Color(0.93f, 0.85f, 0.62f), 0.03f);
        TownLifeKit.Define("edges.hill", new Color(0.36f, 0.60f, 0.27f), 0.02f);
        TownLifeKit.Define("edges.hill.far", new Color(0.30f, 0.54f, 0.30f), 0.02f);
        TownLifeKit.Define("edges.driftwood", new Color(0.82f, 0.78f, 0.70f), 0.05f);
        TownLifeKit.Define("edges.reed", new Color(0.47f, 0.62f, 0.25f), 0.05f);
        TownLifeKit.Define("edges.petal", new Color(0.98f, 0.55f, 0.72f), 0.1f);
        TownLifeKit.Define("edges.duck", new Color(0.99f, 0.86f, 0.25f), 0.2f);

        // ---- stand-ins (only used while the stock library lacks an id)
        TownLifeKit.DefineProp("edges.fb.clump")
            .Add("kit.tree.round", new Vector3(0f, 0f, 0f), 0f, 1.6f).Add("kit.tree.round", new Vector3(3.6f, 0f, 1.4f), 130f, 1.25f)
            .Add("kit.tree.round", new Vector3(-2.6f, 0f, 3.0f), 250f, 1.4f).Add("kit.tree.round", new Vector3(-1.2f, 0f, -3.4f), 60f, 1.15f)
            .ColliderTrunk(0.5f, 4f);
        TownLifeKit.DefineProp("edges.fb.panel")
            .Bar("wood.warm", new Vector3(0f, 0.85f, 0f), new Vector3(0f, 0.85f, 2.5f), 0.09f)
            .Bar("wood.warm", new Vector3(0f, 0.45f, 0f), new Vector3(0f, 0.45f, 2.5f), 0.09f).NoShadows();
        TownLifeKit.DefineProp("edges.fb.post").Cyl("wood.dark", Vector3.zero, 0.08f, 1.05f, 4).NoShadows();
        TownLifeKit.DefineProp("edges.fb.bench")
            .Box("wood.warm", new Vector3(0f, 0.45f, 0f), new Vector3(1.8f, 0.08f, 0.5f))
            .Box("wood.warm", new Vector3(0f, 0.8f, -0.24f), new Vector3(1.8f, 0.4f, 0.07f))
            .Box("metal.dark", new Vector3(-0.8f, 0.22f, 0f), new Vector3(0.08f, 0.44f, 0.5f)).Box("metal.dark", new Vector3(0.8f, 0.22f, 0f), new Vector3(0.08f, 0.44f, 0.5f)).NoShadows();
        TownLifeKit.DefineProp("edges.fb.boulder").Blob("stone.dark", new Vector3(0f, 0.3f, 0f), new Vector3(0.7f, 0.5f, 0.6f), 5).NoShadows();
        TownLifeKit.DefineProp("edges.fb.duck").Sphere("edges.duck", new Vector3(0f, 0.16f, 0f), new Vector3(0.2f, 0.16f, 0.3f)).Sphere("edges.duck", new Vector3(0f, 0.4f, 0.2f), new Vector3(0.12f, 0.12f, 0.12f)).NoShadows();
        TownLifeKit.DefineProp("edges.fb.tuft").Cone("edges.reed", Vector3.zero, 0.25f, 0.9f, 5).Cone("edges.reed", new Vector3(0.25f, 0f, 0.1f), 0.2f, 0.7f, 5).NoShadows();
        TownLifeKit.DefineProp("edges.fb.flower").Blob("leaf.mid", new Vector3(0f, 0.2f, 0f), new Vector3(0.6f, 0.25f, 0.6f), 3).Blob("edges.petal", new Vector3(0f, 0.38f, 0f), new Vector3(0.4f, 0.12f, 0.4f), 4).NoShadows();
        TownLifeKit.DefineProp("edges.fb.lily").Cyl("leaf.lime", Vector3.zero, 0.3f, 0.02f, 8).NoShadows();

        // ---- woodland masses: several stock clumps baked into ONE object (the belts must be dense AND cheap)
        var c5 = TownLifeKit.PropBounds(P("kit.clump.mixed.5")).size; float w = Mathf.Clamp(Mathf.Max(c5.x, c5.z), 6f, 14f) * 0.62f;
        TownLifeKit.DefineProp("edges.wood.a")
            .Add(P("kit.clump.mixed.5"), new Vector3(0f, 0f, 0f), 0f, 1.1f).Add(P("kit.clump.mixed.3"), new Vector3(w, 0f, w * 0.3f), 120f, 1f)
            .Add(P("kit.clump.mixed.5"), new Vector3(-w * 0.5f, 0f, w), 205f, 1.2f).Add(P("kit.clump.pine.3"), new Vector3(w * 0.4f, 0f, -w), 300f, 1.1f)
            .Add(P("kit.shrub.round.m"), new Vector3(w * 1.1f, 0f, -w * 0.6f), 0f, 1.6f).Add(P("kit.shrub.round.m"), new Vector3(-w * 1.1f, 0f, -w * 0.2f), 80f, 1.4f)
            .ColliderBox(new Vector3(0f, 2f, 0f), new Vector3(1.2f, 4f, 1.2f)).ColliderBox(new Vector3(-w * 0.5f, 2f, w), new Vector3(1.2f, 4f, 1.2f));
        TownLifeKit.DefineProp("edges.wood.b")
            .Add(P("kit.clump.mixed.5"), new Vector3(0f, 0f, 0f), 70f, 1.25f).Add(P("kit.clump.birch.3"), new Vector3(-w, 0f, -w * 0.4f), 10f, 1.1f)
            .Add(P("kit.clump.mixed.3"), new Vector3(w * 0.8f, 0f, -w * 0.7f), 160f, 1.15f).Add(P("kit.clump.mixed.5"), new Vector3(w * 0.3f, 0f, w), 250f, 1f)
            .Add(P("kit.shrub.round.m"), new Vector3(-w * 0.9f, 0f, w * 0.8f), 0f, 1.5f)
            .ColliderBox(new Vector3(0f, 2f, 0f), new Vector3(1.2f, 4f, 1.2f)).ColliderBox(new Vector3(w * 0.3f, 2f, w), new Vector3(1.2f, 4f, 1.2f));
        TownLifeKit.DefineProp("edges.wood.c")
            .Add(P("kit.clump.pine.5"), new Vector3(0f, 0f, 0f), 30f, 1.15f).Add(P("kit.clump.mixed.5"), new Vector3(w, 0f, w * 0.5f), 200f, 1.1f)
            .Add(P("kit.clump.mixed.3"), new Vector3(-w * 0.8f, 0f, w * 0.7f), 100f, 1.2f).Add(P("kit.clump.pine.3"), new Vector3(-w * 0.3f, 0f, -w), 310f, 1f)
            .ColliderBox(new Vector3(0f, 2f, 0f), new Vector3(1.2f, 4f, 1.2f)).ColliderBox(new Vector3(w, 2f, w * 0.5f), new Vector3(1.2f, 4f, 1.2f));
        TownLifeKit.DefineProp("edges.wood.pine")
            .Add(P("kit.clump.pine.5"), new Vector3(0f, 0f, 0f), 0f, 1.1f).Add(P("kit.clump.pine.3"), new Vector3(w * 0.9f, 0f, w * 0.4f), 140f, 1.2f)
            .Add(P("kit.clump.pine.3"), new Vector3(-w * 0.6f, 0f, w * 0.8f), 260f, 0.95f)
            .Add(P("kit.grass.tuft"), new Vector3(w * 0.5f, 0f, -w * 0.8f), 0f, 1.6f).Add(P("kit.grass.tuft"), new Vector3(-w, 0f, -w * 0.3f), 90f, 1.4f)
            .ColliderBox(new Vector3(0f, 2f, 0f), new Vector3(1.2f, 4f, 1.2f));
        // a hill-crest line of woods, dropping at both ends to follow the dome
        var ridge = TownLifeKit.DefineProp("edges.ridge");
        for (int i = -3; i <= 3; i++)
        {
            float drop = -0.45f * i * i;
            ridge.Add(P(i % 2 == 0 ? "kit.clump.mixed.5" : (i == 1 ? "kit.clump.pine.5" : "kit.clump.mixed.3")), new Vector3(i * w * 1.05f, drop, (i % 2 == 0 ? 0.25f : -0.3f) * w), i * 77f, 1.25f - 0.05f * Mathf.Abs(i));
        }

        // ---- understorey and ground clusters (one object each, no colliders)
        TownLifeKit.DefineProp("edges.under.a")
            .Add(P("kit.shrub.round.m"), new Vector3(0f, 0f, 0f), 0f, 1.25f).Add(P("kit.shrub.round.s"), new Vector3(1.5f, 0f, 0.5f), 90f, 1.1f)
            .Add(P("kit.shrub.round.s"), new Vector3(-1.1f, 0f, 1.2f), 200f, 1f).Add(P("kit.flowers.mixed"), new Vector3(0.4f, 0f, -1.5f), 0f, 1.1f)
            .Add(P("kit.boulder.s"), new Vector3(-1.6f, 0f, -0.7f), 40f, 1f).Add(P("kit.grass.tuft"), new Vector3(1.9f, 0f, -0.9f), 0f, 1f).NoShadows();
        TownLifeKit.DefineProp("edges.under.b")
            .Add(P("kit.shrub.flower.pink"), new Vector3(0f, 0f, 0f), 0f, 1.2f).Add(P("kit.shrub.round.s"), new Vector3(-1.4f, 0f, 0.3f), 30f, 1.2f)
            .Add(P("kit.flowers.yellow"), new Vector3(1.3f, 0f, 0.9f), 0f, 1f).Add(P("kit.grass.tuft"), new Vector3(0.8f, 0f, -1.3f), 70f, 1.1f)
            .Add(P("kit.grass.tuft"), new Vector3(-0.9f, 0f, -1.2f), 190f, 0.9f).Add(P("kit.shrub.flower.white"), new Vector3(1.9f, 0f, -0.4f), 0f, 0.9f).NoShadows();
        TownLifeKit.DefineProp("edges.under.c")
            .Add(P("kit.boulder.m"), new Vector3(0f, 0f, 0f), 20f, 1.1f).Add(P("kit.boulder.s"), new Vector3(1.3f, 0f, 0.6f), 140f, 1f)
            .Add(P("kit.shrub.flower.purple"), new Vector3(-1.3f, 0f, 0.7f), 0f, 1f).Add(P("kit.flowers.purple"), new Vector3(0.3f, 0f, -1.4f), 0f, 1f)
            .Add(P("kit.grass.tuft"), new Vector3(-1.0f, 0f, -0.9f), 0f, 1.2f).Add(P("kit.shrub.round.s"), new Vector3(0.6f, 0f, 1.6f), 0f, 1.1f).NoShadows();
        string[] fl = { "kit.flowers.red", "kit.flowers.yellow", "kit.flowers.white", "kit.flowers.purple", "kit.flowers.pink", "kit.flowers.mixed" };
        for (int v = 0; v < 2; v++)
        {
            var r = TownLifeKit.DefineProp("edges.wildflowers." + (v == 0 ? "a" : "b")); var rng = new System.Random(TownLifeContext.StableHash("edges/wildflowers/" + v));
            for (int i = 0; i < 9; i++)
            {
                float a = i * 2.4f + v, d = 0.6f + 2.6f * (float)rng.NextDouble();
                r.Add(P(fl[(i + v * 2) % fl.Length]), new Vector3(Mathf.Cos(a) * d, 0f, Mathf.Sin(a) * d), rng.Next(360), 0.9f + 0.4f * (float)rng.NextDouble());
            }
            r.Add(P("kit.grass.tuft"), new Vector3(0.5f, 0f, 0.4f), 0f, 1.1f).Add(P("kit.grass.tuft"), new Vector3(-1.8f, 0f, -1.1f), 60f, 1f).NoShadows();
        }
        var reed = TownLifeKit.DefineProp("edges.reedbed");
        { var rng = new System.Random(TownLifeContext.StableHash("edges/reedbed")); for (int i = 0; i < 8; i++) reed.Add(P("kit.reeds"), new Vector3(-3.2f + i * 0.9f, 0f, -0.7f + 1.4f * (float)rng.NextDouble()), rng.Next(360), 0.9f + 0.5f * (float)rng.NextDouble()); }
        reed.NoShadows();
        for (int v = 0; v < 2; v++)
        {
            var r = TownLifeKit.DefineProp("edges.dune." + (v == 0 ? "a" : "b")); var rng = new System.Random(TownLifeContext.StableHash("edges/dune/" + v));
            for (int i = 0; i < 12; i++) { float a = i * 2.1f, d = 0.3f + 3.0f * (float)rng.NextDouble(); r.Add(P("kit.grass.tuft"), new Vector3(Mathf.Cos(a) * d, 0f, Mathf.Sin(a) * d * 1.3f), rng.Next(360), 1.2f + 0.9f * (float)rng.NextDouble()); }
            if (v == 1) r.Add(P("kit.boulder.s"), new Vector3(0.4f, 0f, 0.2f), 0f, 0.9f);
            r.NoShadows();
        }
        TownLifeKit.DefineProp("edges.driftwood")
            .Cyl("edges.driftwood", new Vector3(-1.4f, 0.2f, 0f), 0.2f, 2.8f, 7, 0.12f, new Vector3(0f, 0f, -90f))
            .Cyl("edges.driftwood", new Vector3(0.2f, 0.25f, 0f), 0.09f, 1.1f, 5, 0.05f, new Vector3(50f, 0f, -20f))
            .Cyl("edges.driftwood", new Vector3(-0.6f, 0.25f, 0f), 0.08f, 0.8f, 5, 0.04f, new Vector3(-55f, 0f, 15f)).NoShadows();

        // ---- fishing jetty: origin = landward end on the grass, +Z out over the water, deck top 0.10 above the origin
        var jetty = TownLifeKit.DefineProp("edges.jetty")
            .Box("wood.warm", new Vector3(0f, 0.02f, 8f), new Vector3(2.4f, 0.16f, 16f));
        for (int i = 0; i < 16; i++) jetty.Box("wood.dark", new Vector3(0f, 0.105f, 0.5f + i), new Vector3(2.4f, 0.012f, 0.06f));
        foreach (float z in new[] { 3.2f, 6.4f, 9.6f, 12.8f, 15.8f })
            foreach (float x in new[] { -1.1f, 1.1f })
                jetty.Cyl("wood.dark", new Vector3(x, -5.4f, z), 0.13f, z > 9f ? 6.45f : 5.4f, 6);
        foreach (float x in new[] { -1.1f, 1.1f })
        {
            jetty.Bar("wood.warm", new Vector3(x, 0.95f, 9.6f), new Vector3(x, 0.95f, 15.8f), 0.09f);
            jetty.Bar("wood.warm", new Vector3(x, 0.55f, 9.6f), new Vector3(x, 0.55f, 15.8f), 0.07f);
        }
        jetty.Bar("wood.dark", new Vector3(0.95f, 0.9f, 15.4f), new Vector3(1.7f, 2.7f, 17.6f), 0.05f)           // a forgotten fishing rod
            .Bar("paint.white", new Vector3(1.7f, 2.7f, 17.6f), new Vector3(1.7f, -1.0f, 17.6f), 0.02f)
            .Sphere("paint.red", new Vector3(1.7f, -0.98f, 17.6f), new Vector3(0.12f, 0.12f, 0.12f))
            .Box("paint.blue", new Vector3(-0.6f, 0.27f, 14.6f), new Vector3(0.5f, 0.34f, 0.36f))                   // tackle box
            .ColliderBox(new Vector3(0f, 0.02f, 8f), new Vector3(2.4f, 0.16f, 16f)).Walkable();
    }

    // ---------------------------------------------------------------------------------------------------- build
    public static void Build(TownLifeContext ctx)
    {
        var s = new State { ctx = ctx, scene = ctx.scene, plan = ctx.plan, occ = ctx.occupancy };
        s.density = ctx.options.Number("edges.density", 1f);
        DefineAll();
        s.root = ctx.Group("Edges");
        s.wood = TownLifeKit.Group(s.root, "TL_Edges_Woodland"); s.river = TownLifeKit.Group(s.root, "TL_Edges_Riverside");
        s.coast = TownLifeKit.Group(s.root, "TL_Edges_Coast"); s.depot = TownLifeKit.Group(s.root, "TL_Edges_DepotRow");
        s.hills = TownLifeKit.Group(s.root, "TL_Edges_Hills"); s.seam = TownLifeKit.Group(s.root, "TL_Edges_Seam");
        var loop = s.scene.Road("meadow-loop"); s.ring = loop != null ? loop.points : new Vector3[0];

        BuildGrid(s);
        if (ctx.options.Flag("edges.river", true)) Riverside(s);
        if (ctx.options.Flag("edges.coast", true)) Coast(s);
        if (ctx.options.Flag("edges.depot", true)) DepotRow(s);
        if (ctx.options.Flag("edges.seam", true)) Gates(s);
        if (ctx.options.Flag("edges.vistas", true)) VistaEnds(s);
        if (ctx.options.Flag("edges.hills", true)) Hills(s);
        if (ctx.options.Flag("edges.wild", true)) Wild(s);

        if (ctx.options.Flag("edges.debug", false))
            foreach (var c in s.plan.commons) ctx.Log(Name, "common " + c.id + " area " + c.area.ToString("0") + " c " + c.centroid.x.ToString("0") + "," + c.centroid.z.ToString("0") + " bounds " + c.touchesBounds + " water " + c.touchesWater + " rect " + c.bounds);
        var keys = new List<string>(s.counts.Keys); keys.Sort(StringComparer.Ordinal);
        var sb = new System.Text.StringBuilder(); int total = 0;
        foreach (var k in keys) { sb.Append(k).Append(' ').Append(s.counts[k]).Append(", "); total += s.counts[k]; }
        ctx.Log(Name, total + " objects: " + sb.ToString().TrimEnd(',', ' '));
    }

    static bool On(State s, Vector3 p) { return s.ctx.options.District(p.x < 476f ? TownLifeScene.Acres : TownLifeScene.Meadows); }
    static Vector3 G(State s, Vector3 p) { p.y = s.scene.GroundY(p); return p; }
    static Vector3 V(float x, float z) { return new Vector3(x, 0f, z); }
    static float R(System.Random rng, float a, float b) { return TownLifeContext.Range(rng, a, b); }

    /// <summary>Land that is nobody's: common (or mis-labelled water setback that is really dry land), dry, in the area.</summary>
    static bool OpenLand(State s, Vector3 p, float bankMargin = 1.5f)
    {
        if (!TownLifeGeo.RectContains(s.occ.area, p)) return false;
        int l = s.plan.LabelAt(p);
        if (l != TownLifePlan.Common && l != TownLifePlan.Water) return false;
        if (p.z > RiverSafe(p.x) - bankMargin || p.x > CoastSafe(p.z) - bankMargin) return false;
        return true;
    }
    static bool RiverZone(Vector3 p) { return p.x > 128f && p.x < 716f && p.z > 261f && p.z > RiverSafe(p.x) - 24f; }
    static bool CoastZone(Vector3 p) { return p.x > 760f && p.x > CoastSafe(p.z) - 14f; }

    // ---------------------------------------------------------------------------------------------------- distance grid
    static void BuildGrid(State s)
    {
        var area = s.occ.area; s.gw = Mathf.CeilToInt(area.width / s.cell); s.gh = Mathf.CeilToInt(area.height / s.cell);
        s.dist = new float[s.gw * s.gh]; s.wild = new bool[s.gw * s.gh];
        var beach = new Rect(766f, 139f, 42f, 27f); var park = s.scene.park != null ? s.scene.park.kerb : new Rect(0, 0, 0, 0);
        for (int iz = 0; iz < s.gh; iz++)
            for (int ix = 0; ix < s.gw; ix++)
            {
                var p = new Vector3(area.xMin + (ix + 0.5f) * s.cell, 0f, area.yMin + (iz + 0.5f) * s.cell); int i = iz * s.gw + ix;
                int l = s.plan.LabelAt(p);
                bool seed = l >= 0 || l == TownLifePlan.RoadReserve || l == TownLifePlan.Reserved || s.plan.RoadEdgeDistance(p) < 3f;
                if (!seed) foreach (var pad in s.scene.depotPads) if (TownLifeGeo.RectContains(pad, p, 1f)) { seed = true; break; }
                if (!seed) foreach (var inf in s.scene.infill) if (TownLifeGeo.QuadContains(inf.quad, p, 1f)) { seed = true; break; }
                bool foreign = TownLifeGeo.RectContains(park, p) || TownLifeGeo.RectContains(beach, p) || (s.ring.Length > 2 && TownLifeGeo.PolygonContains(s.ring, p));
                if (foreign) seed = true;
                s.wild[i] = !seed && OpenLand(s, p) && !RiverZone(p);
                s.dist[i] = seed ? 0f : 1e6f;
            }
        // two-pass chamfer distance (metres) from the town
        float a = s.cell, b = s.cell * 1.4142f;
        for (int iz = 0; iz < s.gh; iz++)
            for (int ix = 0; ix < s.gw; ix++)
            {
                int i = iz * s.gw + ix; float d = s.dist[i];
                if (ix > 0) d = Mathf.Min(d, s.dist[i - 1] + a);
                if (iz > 0) { d = Mathf.Min(d, s.dist[i - s.gw] + a); if (ix > 0) d = Mathf.Min(d, s.dist[i - s.gw - 1] + b); if (ix < s.gw - 1) d = Mathf.Min(d, s.dist[i - s.gw + 1] + b); }
                s.dist[i] = d;
            }
        for (int iz = s.gh - 1; iz >= 0; iz--)
            for (int ix = s.gw - 1; ix >= 0; ix--)
            {
                int i = iz * s.gw + ix; float d = s.dist[i];
                if (ix < s.gw - 1) d = Mathf.Min(d, s.dist[i + 1] + a);
                if (iz < s.gh - 1) { d = Mathf.Min(d, s.dist[i + s.gw] + a); if (ix > 0) d = Mathf.Min(d, s.dist[i + s.gw - 1] + b); if (ix < s.gw - 1) d = Mathf.Min(d, s.dist[i + s.gw + 1] + b); }
                s.dist[i] = d;
            }
    }
    /// <summary>Common land enclosed by the town: touches neither the area bounds nor the water.</summary>
    static bool Inner(State s, Vector3 p)
    {
        int id = s.plan.CommonAt(p); if (id < 0) return false;
        foreach (var c in s.plan.commons) if (c.id == id) return !c.touchesBounds && !c.touchesWater;
        return false;
    }
    static bool WildAt(State s, Vector3 p, out float townDist)
    {
        var area = s.occ.area; townDist = 0f;
        int ix = Mathf.FloorToInt((p.x - area.xMin) / s.cell), iz = Mathf.FloorToInt((p.z - area.yMin) / s.cell);
        if (ix < 0 || iz < 0 || ix >= s.gw || iz >= s.gh) return false;
        int i = iz * s.gw + ix; if (!s.wild[i]) return false;
        townDist = s.dist[i] - s.cell * 0.5f;
        return OpenLand(s, p) && !RiverZone(p);
    }

    // ---------------------------------------------------------------------------------------------------- hills
    static float HillY(State s, Vector3 p)
    {
        float best = 0f;
        foreach (var h in s.hillList)
        {
            float dx = (p.x - h.c.x) / h.r.x, dz = (p.z - h.c.z) / h.r.y, r2 = dx * dx + dz * dz;
            if (r2 < 0.96f) best = Mathf.Max(best, h.h * Mathf.Sqrt(1f - r2) * 0.86f - 0.5f);
        }
        return best;
    }
    static void AddHill(State s, string name, Vector3 c, Vector2 r, float h, string mat, int seed)
    {
        if (!On(s, new Vector3(Mathf.Clamp(c.x, 60f, 830f), 0f, c.z))) return;
        TownLifeKit.Mound(s.hills, "edges_hill_" + name, c, r, h, mat, TownLifeContext.GrassY - 0.02f, seed, false, 22, 5);
        s.hillList.Add(new Hill { c = c, r = r, h = h }); s.Count("hill");
    }
    static void Hills(State s)
    {
        var rng = s.ctx.Rng("edges/hills");
        // back row: bigger and darker, mostly beyond the city wall (z -52); front row: lower foothills
        int n = 0;
        for (float x = 10f; x < 900f; x += R(rng, 78f, 104f), n++)
        {
            float rz = R(rng, 34f, 44f), rx = R(rng, 62f, 84f), h = R(rng, 23f, 31f);
            AddHill(s, "back_" + n, V(x, -47f - rz + R(rng, -6f, 2f)), new Vector2(rx, rz), h, "edges.hill.far", 100 + n);
        }
        n = 0;
        for (float x = 50f; x < 880f; x += R(rng, 70f, 96f), n++)
        {
            float rz = R(rng, 20f, 28f), rx = R(rng, 36f, 52f), h = R(rng, 8f, 13f);
            AddHill(s, "front_" + n, V(x, -38f - rz + R(rng, -3f, 5f)), new Vector2(rx, rz), h, "edges.hill", 200 + n);
        }
        // west: behind the depot woodland
        // (Home Run Road dead-ends at x 45.5, z 58.5: it runs out through a valley between two hills, closed by a third)
        float[] wz = { 3f, 114f, 196f, 251f }, wr = { 38f, 39f, 33f, 26f };
        for (int i = 0; i < wz.Length; i++)
            AddHill(s, "west_" + i, V(R(rng, 14f, 22f), wz[i]), new Vector2(R(rng, 30f, 36f), wr[i]), R(rng, 14f, 22f), i % 2 == 0 ? "edges.hill.far" : "edges.hill", 300 + i);
        AddHill(s, "west_valley", V(-8f, 58.5f), new Vector2(27f, 34f), 24f, "edges.hill.far", 310);

        // tree tops: a crest line and two woods on the town-facing slope of every hill
        int k = 0;
        foreach (var h in s.hillList)
        {
            var hr = s.ctx.Rng("edges/hilltrees/" + k++);
            bool west = h.c.x < 40f && h.c.z > -20f;
            float yaw = west ? 90f : 0f;
            var crest = h.c; crest.y = TownLifeContext.GrassY + h.h * 0.86f - 0.8f;
            bool back = !west && h.c.z - h.r.y * 0.5f < -60f;
            if (h.h > 13f) { TownLifeKit.Spawn(s.hills, "edges.ridge", crest, yaw + R(hr, -12f, 12f), R(hr, 0.95f, 1.2f)); s.Count("hill.wood"); }
            for (int j = 0; j < 2 && !back; j++)
            {
                float u = (j == 0 ? -1f : 1f) * R(hr, 0.25f, 0.5f), v = R(hr, 0.42f, 0.62f);
                var p = west ? new Vector3(h.c.x + v * h.r.x, 0f, h.c.z + u * h.r.y) : new Vector3(h.c.x + u * h.r.x, 0f, h.c.z + v * h.r.y);
                if (p.z < -51f && !west && h.h < 13f) continue;
                p.y = TownLifeContext.GrassY + HillY(s, p);
                TownLifeKit.Spawn(s.hills, hr.Next(3) == 0 ? "edges.wood.c" : (hr.Next(2) == 0 ? "edges.wood.a" : "edges.wood.b"), p, hr.Next(360), R(hr, 0.9f, 1.15f)); s.Count("hill.wood");
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------- wild land
    static void Wild(State s)
    {
        var area = s.occ.area; var occ = s.occ;
        string[] megas = { "edges.wood.a", "edges.wood.b", "edges.wood.c" };
        string[] clumps = { "kit.clump.mixed.5", "kit.clump.mixed.5", "kit.clump.mixed.3", "kit.clump.mixed.3", "kit.clump.birch.3", "kit.clump.pine.3", "kit.clump.mixed.5" };
        string[] specimens = { "kit.tree.oak.l", "kit.tree.maple.l", "kit.tree.blossom.m", "kit.tree.birch.l", "kit.tree.round.l", "kit.tree.oak.xl", "kit.tree.elm.l" };
        string[] unders = { "edges.under.a", "edges.under.b", "edges.under.c" };

        // sweep-up: commons enclosed by the town (no bounds, no water) become groves, not leftover lawn
        float step = 7.5f;
        for (int iz = 0; iz * step < area.height; iz++)
            for (int ix = 0; ix * step < area.width; ix++)
            {
                var rng = s.ctx.Rng("edges/wild/s/" + ix + "/" + iz);
                var p = V(area.xMin + (ix + 0.5f + ((iz & 1) == 0 ? 0f : 0.5f)) * step + R(rng, -2.5f, 2.5f), area.yMin + (iz + 0.5f) * step + R(rng, -2.5f, 2.5f));
                if (!WildAt(s, p, out float d) || d < 4f || !On(s, p) || !Inner(s, p)) continue;
                if (s.occ.Within(p, 5f, "tree") || !occ.Free(p, 3.4f)) continue;
                occ.ReserveCircle(p, 2.6f, TagTree);
                string id = new[] { "kit.clump.mixed.3", "kit.clump.birch.3", "kit.clump.mixed.5", "kit.tree.blossom.m", "kit.tree.maple.l" }[rng.Next(5)];
                TownLifeKit.Spawn(s.wood, P(id), G(s, p), rng.Next(360), FS(id) * R(rng, 0.95f, 1.2f)); s.Count("grove");
            }
        // pass A: deep woodland masses
        step = 20f / Mathf.Sqrt(s.density);
        for (int iz = 0; iz * step < area.height; iz++)
            for (int ix = 0; ix * step < area.width; ix++)
            {
                var rng = s.ctx.Rng("edges/wild/a/" + ix + "/" + iz);
                var p = V(area.xMin + (ix + 0.5f + ((iz & 1) == 0 ? 0f : 0.5f)) * step + R(rng, -3.5f, 3.5f), area.yMin + (iz + 0.5f) * step + R(rng, -3.5f, 3.5f));
                if (!WildAt(s, p, out float d) || d < 16f || !On(s, p)) continue;
                bool coast = CoastZone(p) || (p.x > 770f && rng.Next(2) == 0);
                if (coast && rng.Next(3) == 0) continue;
                if (!occ.Free(p, 6.5f)) continue;
                occ.ReserveCircle(p, 5f, TagTree);
                var q = G(s, p); q.y += HillY(s, p);
                TownLifeKit.Spawn(s.wood, coast ? "edges.wood.pine" : megas[rng.Next(megas.Length)], q, rng.Next(360), R(rng, 1.15f, 1.45f)); s.Count("wood");
            }
        // pass B: stock clumps along the inner edge of the belt
        step = 12.5f / Mathf.Sqrt(s.density);
        for (int iz = 0; iz * step < area.height; iz++)
            for (int ix = 0; ix * step < area.width; ix++)
            {
                var rng = s.ctx.Rng("edges/wild/b/" + ix + "/" + iz);
                var p = V(area.xMin + (ix + 0.5f + ((iz & 1) == 0 ? 0f : 0.5f)) * step + R(rng, -3f, 3f), area.yMin + (iz + 0.5f) * step + R(rng, -3f, 3f));
                if (!WildAt(s, p, out float d) || d < 7.5f || !On(s, p)) continue;
                bool coast = CoastZone(p) || p.x > 785f;
                if (rng.NextDouble() > (coast ? 0.4 : 0.76)) continue;
                if (!occ.Free(p, 4.2f)) continue;
                occ.ReserveCircle(p, 3.2f, TagTree);
                string id = coast ? (rng.Next(2) == 0 ? "kit.clump.pine.3" : "kit.clump.pine.5") : clumps[rng.Next(clumps.Length)];
                var q = G(s, p); q.y += HillY(s, p);
                TownLifeKit.Spawn(s.wood, P(id), q, rng.Next(360), R(rng, 1.05f, 1.4f)); s.Count("clump");
            }
        // pass C: the front edge: specimen trees, understorey, wildflowers; open grass in between
        step = 6.5f / Mathf.Sqrt(s.density);
        for (int iz = 0; iz * step < area.height; iz++)
            for (int ix = 0; ix * step < area.width; ix++)
            {
                var rng = s.ctx.Rng("edges/wild/c/" + ix + "/" + iz);
                var p = V(area.xMin + (ix + 0.5f) * step + R(rng, -2.4f, 2.4f), area.yMin + (iz + 0.5f) * step + R(rng, -2.4f, 2.4f));
                if (!WildAt(s, p, out float d) || d < 3.2f || d > 11f || !On(s, p)) continue;
                bool coast = CoastZone(p);
                double roll = rng.NextDouble(); var q = G(s, p);
                if (coast)
                {
                    if (roll < 0.10 && !occ.Within(p, 6f, "tree") && occ.TryReserve(p, 1.2f, TagTree)) { string id = rng.Next(2) == 0 ? "kit.tree.pine.l" : "kit.tree.pine.m"; TownLifeKit.Spawn(s.wood, P(id), q, rng.Next(360), FS(id) * R(rng, 0.9f, 1.2f)); s.Count("specimen"); }
                    else if (roll < 0.5 && occ.TryReserve(p, 1.5f, TagShrub, TownLifeOccupancy.What.Solid, TownLifeOccupancy.Block.Marker)) { TownLifeKit.Spawn(s.wood, rng.Next(2) == 0 ? "edges.dune.a" : "edges.dune.b", q, rng.Next(360), R(rng, 0.9f, 1.3f)); s.Count("dune"); }
                    continue;
                }
                if (roll < 0.16)
                {
                    if (occ.Within(p, 7f, "tree") || !occ.TryReserve(p, 1.3f, TagTree)) continue;
                    string id = specimens[rng.Next(specimens.Length)];
                    TownLifeKit.Spawn(s.wood, P(id), q, rng.Next(360), FS(id) * R(rng, 0.9f, 1.2f)); s.Count("specimen");
                }
                else if (roll < 0.40)
                {
                    if (!occ.TryReserve(p, 2.2f, TagShrub, TownLifeOccupancy.What.Solid, TownLifeOccupancy.Block.Marker)) continue;
                    TownLifeKit.Spawn(s.wood, unders[rng.Next(unders.Length)], q, rng.Next(360), R(rng, 0.9f, 1.25f)); s.Count("under");
                }
                else if (roll < 0.50)
                {
                    if (!occ.TryReserve(p, 2.5f, TagShrub, TownLifeOccupancy.What.Solid, TownLifeOccupancy.Block.Marker)) continue;
                    TownLifeKit.Spawn(s.wood, rng.Next(2) == 0 ? "edges.wildflowers.a" : "edges.wildflowers.b", q, rng.Next(360), R(rng, 0.9f, 1.3f)); s.Count("flowers");
                }
            }
    }

    // ---------------------------------------------------------------------------------------------------- riverside
    static void Riverside(State s)
    {
        // promenade runs: sample the bank every 6 m; break where there is no room, no common land, or a bridge
        var runs = new List<List<Vector3>>(); List<Vector3> cur = null;
        for (float x = 132f; x <= 716f; x += 6f)
        {
            float top = Mathf.Min(RiverSafe(x - 6f), RiverSafe(x), RiverSafe(x + 6f));
            var rail = V(x, top - 1.0f); var path = V(x, top - 3.6f);
            bool ok = top < 292f && On(s, rail);
            if (ok) { int l = s.plan.LabelAt(path); ok = l == TownLifePlan.Common || l == TownLifePlan.Water; }
            if (ok) ok = s.scene.RoadEdgeDistance(V(x, top - 5.5f), out _) > 3.6f && s.scene.RoadEdgeDistance(rail, out _) > 6f;
            if (ok) foreach (var r in s.scene.roads) if (r.bridge && PolyDist(r.points, rail) < 11f) { ok = false; break; }
            if (!ok) { if (cur != null && cur.Count >= 3) runs.Add(cur); cur = null; continue; }
            if (cur == null) cur = new List<Vector3>();
            cur.Add(rail);
        }
        if (cur != null && cur.Count >= 3) runs.Add(cur);

        // jetties: one on the Birch reach, one on the Mile reach
        float[] jettyX = { 226f, 622f }; var jetties = new List<Vector3>();
        foreach (float jx in jettyX)
            foreach (var run in runs)
                if (jx > run[0].x + 8f && jx < run[run.Count - 1].x - 8f) jetties.Add(V(jx, RailZ(run, jx)));

        int runIndex = 0;
        foreach (var run in runs)
        {
            string id = "x" + Mathf.RoundToInt(run[0].x);
            // --- rail (broken at jetties), merged, one collider per straight piece
            var pieces = new List<List<Vector3>>(); var piece = new List<Vector3>();
            for (int i = 0; i < run.Count; i++)
            {
                foreach (var j in jetties)
                    if (i > 0 && run[i - 1].x < j.x && j.x <= run[i].x + 3f && piece.Count > 0 && piece[piece.Count - 1].x < j.x - 1.7f)
                    { piece.Add(V(j.x - 1.7f, RailZ(run, j.x - 1.7f))); if (piece.Count >= 2) pieces.Add(piece); piece = new List<Vector3> { V(j.x + 1.7f, RailZ(run, j.x + 1.7f)) }; }
                bool gap = false; foreach (var j in jetties) if (Mathf.Abs(run[i].x - j.x) < 1.9f) gap = true;
                if (!gap && (piece.Count == 0 || run[i].x > piece[piece.Count - 1].x + 0.2f)) piece.Add(run[i]);
            }
            if (piece.Count >= 2) pieces.Add(piece);
            int pi = 0;
            foreach (var pc in pieces)
            {
                var simple = Simplify(pc);
                TownLifeKit.Run(s.river, "edges_riverrail_" + id + "_" + pi++, simple, new TownLifeKit.RunSpec
                {
                    panel = P("kit.panel.rail.post"), panelLength = 2.5f, post = P("kit.post.wood"), collider = TownLifeKit.RunCollider.Box,
                    colliderHeight = 1.0f, colliderThickness = 0.2f, castShadows = false
                });
                s.Count("river.rail");
            }
            // --- gravel path
            var pathPts = new List<Vector3>(); foreach (var p in run) pathPts.Add(V(p.x, p.z - 2.6f));
            var pathSimple = Simplify(pathPts);
            TownLifeKit.Ribbon(s.river, "edges_riverpath_" + id, pathSimple, 2.4f, "gravel", TownLifeContext.PavingY);
            s.occ.ReservePolyline(pathSimple, 1.2f, TagPaving, TownLifeOccupancy.Block.Solids); s.Count("river.path");
            // short spurs from the sidewalk at both ends and the middle
            int spur = 0;
            foreach (float t in new[] { 0.05f, 0.35f, 0.65f, 0.95f })
                foreach (float shift in new[] { 0f, 3f, -3f, 6f, -6f, 9f, -9f })
                {
                    float x = Mathf.Clamp(Mathf.Lerp(run[0].x, run[run.Count - 1].x, t) + shift, run[0].x + 1f, run[run.Count - 1].x - 1f); var a = V(x, RailZ(run, x) - 2.6f);
                    Vector3 b = a; bool found = false;
                    for (float dz = 2f; dz < 26f; dz += 0.25f) { b = V(x, a.z - dz); if (s.scene.RoadEdgeDistance(b, out _) < 3.3f) { found = true; break; } }
                    if (!found || !s.occ.FreeLine(a + V(0f, -2.2f), b + V(0f, 0.9f), 0.7f)) continue;
                    TownLifeKit.Ribbon(s.river, "edges_riverspur_" + id + "_" + spur++, new[] { a + V(0f, 0.5f), b + V(0f, -0.25f) }, 2.0f, "gravel", TownLifeContext.PavingY);
                    s.occ.ReserveLine(a, b, 1.2f, TagPaving, TownLifeOccupancy.Block.Solids); s.Count("river.path");
                    break;
                }
            // --- furniture and trees, walking the run
            float x0 = run[0].x + 3f, x1 = run[run.Count - 1].x - 3f; int n = 0;
            for (float x = x0; x <= x1; x += 6.5f, n++)
            {
                var rng = s.ctx.Rng("edges/river/" + runIndex + "/" + n);
                float rz = RailZ(run, x); bool nearJetty = false; foreach (var j in jetties) if (Mathf.Abs(x - j.x) < 4f) nearJetty = true;
                int slot = n % 6;
                // south edge of the path: bench / lamp / bin rhythm
                var edge = V(x, rz - 5.0f);
                if (slot == 1 || (slot == 4 && (n / 6) % 2 == 1))
                {
                    if (s.occ.TryReserve(edge, 1.0f, TagFurniture)) { TownLifeKit.Spawn(s.river, P("kit.bench.park"), G(s, edge), R(rng, -4f, 4f)); s.Count("river.bench"); }
                }
                else if (slot == 0 && (n / 6) % 2 == 0)
                {
                    var lp = V(x, rz - 4.75f);
                    if (s.occ.TryReserve(lp, 0.6f, TagFurniture))
                    {
                        var lamp = TownLifeKit.SpawnPrefab(s.river, LampPrefab, G(s, lp), 0f, 0.8f, true, true);
                        if (lamp == null) TownLifeKit.Spawn(s.river, P("kit.lamp.park"), G(s, lp), 0f);
                        s.Count("river.lamp");
                    }
                }
                else if (slot == 3 && (n / 6) % 3 == 1)
                {
                    var bp = V(x, rz - 4.7f);
                    if (s.occ.TryReserve(bp, 0.6f, TagFurniture)) { TownLifeKit.Spawn(s.river, P("kit.bin.park"), G(s, bp), 0f); s.Count("river.bin"); }
                }
                // trees behind the path
                if (slot == 2 || slot == 5)
                {
                    var tp = V(x + R(rng, -1.2f, 1.2f), rz - R(rng, 7.2f, 8.6f));
                    for (int k = 0; k < 8 && s.scene.RoadEdgeDistance(tp, out _) < 4.9f; k++) tp.z += 0.4f;
                    if (tp.z > rz - 6.2f) tp.z = -999f;
                    string tid = slot == 2 ? "kit.tree.willow.l" : (rng.Next(3) == 0 ? "kit.tree.blossom.m" : "kit.tree.birch.l");
                    if (tp.z > 0f && !s.occ.Within(tp, 5.5f, "tree") && s.occ.TryReserve(tp, 1.2f, TagTree)) { TownLifeKit.Spawn(s.river, P(tid), G(s, tp), rng.Next(360), FS(tid) * R(rng, 0.9f, 1.15f)); s.Count("river.tree"); }
                    // deeper strips get a second, looser row
                    var tp2 = V(x + R(rng, 2f, 4f), rz - R(rng, 12.5f, 15f));
                    if (s.scene.RoadEdgeDistance(tp2, out _) > 5.5f && OpenLand(s, tp2) && !s.occ.Within(tp2, 6f, "tree") && s.occ.TryReserve(tp2, 2.4f, TagTree))
                    { TownLifeKit.Spawn(s.river, P(rng.Next(2) == 0 ? "kit.clump.birch.3" : "kit.clump.mixed.3"), G(s, tp2), rng.Next(360), R(rng, 0.9f, 1.1f)); s.Count("river.tree"); }
                }
                // flowers between path and rail
                if (slot == 3 || slot == 0)
                {
                    var fp = V(x + R(rng, -1f, 1f), rz - 6.4f);
                    if (s.scene.RoadEdgeDistance(fp, out _) > 4f && s.occ.TryReserve(fp, 1.8f, TagShrub, TownLifeOccupancy.What.Solid, TownLifeOccupancy.Block.Marker))
                    { TownLifeKit.Spawn(s.river, rng.Next(2) == 0 ? "edges.under.b" : "edges.wildflowers.a", G(s, fp), rng.Next(360), 0.8f); s.Count("river.flowers"); }
                }
                // reeds in the shallows
                if (!nearJetty && rng.NextDouble() < 0.46)
                {
                    float rx = x + R(rng, -2f, 2f); var rp = new Vector3(rx, TownLifeContext.WaterY - 0.12f, RiverWet(s, rx) + R(rng, -0.4f, 0.8f));
                    TownLifeKit.Spawn(s.river, "edges.reedbed", rp, R(rng, -14f, 14f), R(rng, 0.9f, 1.3f)); s.Count("river.reeds");
                }
            }
            runIndex++;
        }

        // --- the far bank: a loose line of trees so the river has two sides (stays inside the working area)
        int fn = 0;
        for (float x = 64f; x < 800f; x += 24f, fn++)
        {
            var rng = s.ctx.Rng("edges/farbank/" + fn); float fx = x + R(rng, -6f, 6f);
            int fi = Mathf.Clamp(Mathf.RoundToInt((fx - RiverX0) / TableStep), 1, RiverFar.Length - 2);
            float top = Mathf.Max(RiverFar[fi - 1], RiverFar[fi], RiverFar[fi + 1]);
            var p = V(fx, Mathf.Min(top + R(rng, 1.4f, 4f), s.occ.area.yMax - 0.6f));
            if (top > 400f || p.z < top + 1f || !On(s, p) || rng.Next(6) == 0) continue;
            if (s.scene.RoadEdgeDistance(p, out _) < 9f) continue;
            bool clear = true;
            foreach (var r in s.scene.roads) if (r.bridge && PolyDist(r.points, p) < 13f) clear = false;
            foreach (var inf in s.scene.infill) if (TownLifeGeo.QuadContains(inf.quad, p, 4f)) clear = false;
            foreach (var w in s.scene.riverWalk) if (TownLifeGeo.Dist(w, p) < 5f) clear = false;
            if (!clear) continue;
            string id = new[] { "kit.tree.willow.l", "kit.clump.mixed.3", "kit.clump.birch.3", "kit.tree.willow.l", "kit.clump.mixed.5" }[rng.Next(5)];
            TownLifeKit.Spawn(s.river, P(id), G(s, p), rng.Next(360), FS(id) * R(rng, 1.05f, 1.35f)); s.Count("river.farbank");
        }

        // --- jetties, ducks, lily pads
        int jn = 0;
        foreach (var j in jetties)
        {
            var rng = s.ctx.Rng("edges/jetty/" + jn);
            var at = new Vector3(j.x, TownLifeContext.GrassY, j.z - 1.6f);
            TownLifeKit.Spawn(s.river, "edges.jetty", at, 0f); s.Count("river.jetty");
            s.occ.ReserveBox(V(j.x, j.z + 6f), 0f, 1.4f, 8.5f, "structure.edge");
            float wet = RiverWet(s, j.x);
            for (int d = 0; d < 3; d++)
            {
                var dp = new Vector3(j.x + (d - 1) * 4.5f + R(rng, -1f, 1f), TownLifeContext.WaterY - 0.03f, wet + 2.5f + R(rng, 0f, 7f));
                TownLifeKit.Spawn(s.river, P("kit.duck"), dp, rng.Next(360), 1.3f); s.Count("river.duck");
            }
            for (int d = 0; d < 2; d++)
            {
                var lp = new Vector3(j.x + (d == 0 ? -3.6f : 3.4f), TownLifeContext.WaterY + 0.02f, wet + 1.5f + d);
                TownLifeKit.Spawn(s.river, P("kit.lilypad"), lp, rng.Next(360), 1.6f); s.Count("river.lily");
                TownLifeKit.Spawn(s.river, "edges.reedbed", new Vector3(j.x + (d == 0 ? -6f : 6f), TownLifeContext.WaterY - 0.12f, wet + 0.2f), R(rng, -10f, 10f), 1.2f); s.Count("river.reeds");
            }
            jn++;
        }
    }
    static float PolyDist(Vector3[] pts, Vector3 p) { float d = float.MaxValue; for (int i = 1; i < pts.Length; i++) d = Mathf.Min(d, TownLifeGeo.DistToSegment(p, pts[i - 1], pts[i])); return d; }
    static float RailZ(List<Vector3> run, float x)
    {
        for (int i = 1; i < run.Count; i++) if (x <= run[i].x) return Mathf.Lerp(run[i - 1].z, run[i].z, Mathf.InverseLerp(run[i - 1].x, run[i].x, x));
        return run[run.Count - 1].z;
    }
    /// <summary>Drops collinear points (keeps every corner): fewer straight pieces = fewer colliders.</summary>
    static List<Vector3> Simplify(List<Vector3> pts)
    {
        var o = new List<Vector3> { pts[0] };
        for (int i = 1; i < pts.Count - 1; i++)
        {
            var a = o[o.Count - 1]; var b = pts[i]; var c = pts[i + 1];
            if (TownLifeGeo.DistToSegment(b, a, c) > 0.05f) o.Add(b);
        }
        o.Add(pts[pts.Count - 1]); return o;
    }

    // ---------------------------------------------------------------------------------------------------- coast
    static void Coast(State s)
    {
        if (!s.ctx.options.District(TownLifeScene.Meadows)) return;
        // a sandy dune band along the bank top: the strip reads as coast from the street and from above
        int n = 0; var band = new List<Vector3>(); int bandIndex = 0;
        for (float z = -46f; z <= 266f; z += 8f)
        {
            var p = V(CoastSafe(z) - 3.7f, z);
            bool ok = OpenLand(s, p, 0.2f) && s.scene.RoadEdgeDistance(p, out _) > 7f;
            if (ok) band.Add(p);
            if ((!ok || z + 8f > 266f) && band.Count > 0)
            {
                if (band.Count >= 2) { TownLifeKit.Ribbon(s.coast, "edges_coast_sandband_" + bandIndex++, Simplify(band), 6.6f, "edges.sand", TownLifeContext.SoilY); s.Count("coast.sand"); }
                band = new List<Vector3>();
            }
        }
        // pines in a loose row behind the dunes
        for (float z = -40f; z < 262f; z += 12.5f, n++)
        {
            var rng = s.ctx.Rng("edges/coast/pines/" + n);
            var p = V(CoastSafe(z) - R(rng, 9.5f, 15f), z + R(rng, -3f, 3f));
            if (rng.Next(5) == 0 || !OpenLand(s, p, 2f) || s.plan.LabelAt(p) != TownLifePlan.Common || TownLifeGeo.RectContains(new Rect(796f, 140f, 44f, 25f), p)) continue;
            if (s.scene.RoadEdgeDistance(p, out _) < 6f || s.occ.Within(p, 5f, "tree") || !s.occ.TryReserve(p, 2.4f, TagTree)) continue;
            string id = rng.Next(3) == 0 ? "kit.tree.pine.l" : (rng.Next(2) == 0 ? "kit.clump.pine.3" : "kit.clump.pine.5");
            TownLifeKit.Spawn(s.coast, P(id), G(s, p), rng.Next(360), FS(id) * R(rng, 0.95f, 1.25f)); s.Count("coast.pine");
        }
        // beach fence: staggered runs just behind the bank top
        n = 0;
        for (float z = -30f; z < 262f; z += 19f, n++)
        {
            var rng = s.ctx.Rng("edges/coast/fence/" + n);
            float len = R(rng, 8f, 12f); float za = z + R(rng, -2f, 2f), zb = za + len;
            float c = Mathf.Min(CoastSafe(za), CoastSafe(zb)) - 1.6f;
            var a = V(c - R(rng, 0f, 1.2f), za); var b = V(c - R(rng, 0f, 1.2f), zb);
            if (!OpenLand(s, a, 1f) || !OpenLand(s, b, 1f) || TownLifeGeo.RectContains(new Rect(800f, 144f, 40f, 17f), (a + b) * 0.5f)) continue;
            if (s.scene.RoadEdgeDistance(a, out _) < 5f || s.scene.RoadEdgeDistance(b, out _) < 5f) continue;
            TownLifeKit.Run(s.coast, "edges_beachfence_" + n, new[] { a, b }, new TownLifeKit.RunSpec { panel = P("kit.panel.beachfence"), panelLength = 2f, post = P("kit.post.wood"), castShadows = false });
            s.occ.ReserveLine(a, b, 0.5f, "fence.edge"); s.Count("coast.fence");
        }
        // dune grass thick along the bank top
        n = 0;
        for (float z = -44f; z < 266f; z += 4.2f, n++)
        {
            var rng = s.ctx.Rng("edges/coast/dune/" + n);
            if (rng.NextDouble() > 0.8) continue;
            float c = CoastSafe(z); var p = V(c - R(rng, 0.6f, 7.5f), z + R(rng, -1.5f, 1.5f));
            if (!OpenLand(s, p, 0.3f) || s.scene.RoadEdgeDistance(p, out _) < 4.5f) continue;
            if (!s.occ.TryReserve(p, 1.2f, TagShrub, TownLifeOccupancy.What.Solid, TownLifeOccupancy.Block.Marker)) continue;
            TownLifeKit.Spawn(s.coast, rng.Next(2) == 0 ? "edges.dune.a" : "edges.dune.b", G(s, p), rng.Next(360), R(rng, 0.9f, 1.4f)); s.Count("coast.dune");
        }
        // sea-view benches on sand patches, a pine pair and driftwood beside each
        n = 0;
        foreach (float z in new[] { 34f, 82f, 124f, 190f, 232f })
        {
            var rng = s.ctx.Rng("edges/coast/bench/" + n);
            float c = CoastSafe(z) - 5.2f; var p = V(c, z);
            if (!OpenLand(s, p, 2f) || !s.occ.Free(p, 2.6f)) { n++; continue; }
            s.occ.ReserveCircle(p, 1.2f, TagFurniture);
            TownLifeKit.Spawn(s.coast, P("kit.bench.park"), G(s, p), 90f + R(rng, -8f, 8f)); s.Count("coast.bench");
            var dw = V(c + 2.4f, z + R(rng, 2.5f, 4f));
            TownLifeKit.Spawn(s.coast, "edges.driftwood", G(s, dw), rng.Next(360), R(rng, 0.9f, 1.3f)); s.Count("coast.driftwood");
            foreach (float off in new[] { -6.5f, 7.5f })
            {
                var tp = V(c - R(rng, 2f, 5f), z + off); string tid = rng.Next(2) == 0 ? "kit.tree.pine.l" : "kit.tree.pine.m";
                if (OpenLand(s, tp, 2f) && !s.occ.Within(tp, 4f, "tree") && s.occ.TryReserve(tp, 1.2f, TagTree)) { TownLifeKit.Spawn(s.coast, P(tid), G(s, tp), rng.Next(360), FS(tid) * R(rng, 0.95f, 1.25f)); s.Count("coast.pine"); }
            }
            n++;
        }
        // lookout where the beach-access path arrives (the access strip itself, x 769..804, is Meadows')
        {
            float z = 152.5f, c = CoastSafe(z) - 6.5f; var p = V(c, z); var rng = s.ctx.Rng("edges/coast/lookout");
            if (OpenLand(s, p, 2f))
            {
                TownLifeKit.Ribbon(s.coast, "edges_coast_lookout_path", new[] { V(806f, z), V(c - 3f, z) }, 3f, "edges.sand", TownLifeContext.SoilY);
                TownLifeKit.Disc(s.coast, "edges_coast_lookout", p, 6f, "edges.sand", TownLifeContext.SoilY + 0.006f, false, 18);
                s.occ.ReserveCircle(p, 6f, TagPaving, TownLifeOccupancy.Block.Marker); s.Count("coast.lookout", 2);
                foreach (float off in new[] { -2.6f, 2.6f })
                { var bp = V(c + 2.2f, z + off); s.occ.ReserveCircle(bp, 1f, TagFurniture); TownLifeKit.Spawn(s.coast, P("kit.bench.park"), G(s, bp), 90f - off * 5f); s.Count("coast.bench"); }
                TownLifeKit.Spawn(s.coast, P("kit.signpost"), G(s, V(c - 4.5f, z - 3.4f)), 60f); s.Count("coast.sign");
                TownLifeKit.Spawn(s.coast, "edges.driftwood", G(s, V(c + 3.4f, z - 6.5f)), 30f, 1.3f);
                TownLifeKit.Spawn(s.coast, "edges.driftwood", G(s, V(c + 1.4f, z + 7.5f)), 250f, 1.0f); s.Count("coast.driftwood", 2);
                for (int i = 0; i < 2; i++)
                {
                    float za = z + (i == 0 ? -13f : 6.5f); var a = V(c + 4.4f, za); var b = V(c + 4.4f, za + 6.5f);
                    TownLifeKit.Run(s.coast, "edges_beachfence_lookout_" + i, new[] { a, b }, new TownLifeKit.RunSpec { panel = P("kit.panel.beachfence"), panelLength = 2f, post = P("kit.post.wood"), castShadows = false });
                    s.Count("coast.fence");
                }
                foreach (float off in new[] { -9.5f, 10f })
                { var tp = V(c - R(rng, 0f, 3f), z + off); if (OpenLand(s, tp, 2f) && s.occ.TryReserve(tp, 1.2f, TagTree)) { TownLifeKit.Spawn(s.coast, P("kit.tree.pine.l"), G(s, tp), rng.Next(360), FS("kit.tree.pine.l") * R(rng, 1f, 1.2f)); s.Count("coast.pine"); } }
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------- Depot Row
    static void DepotRow(State s)
    {
        if (!s.ctx.options.District(TownLifeScene.Acres)) return;
        var pads = new List<Rect>(s.scene.depotPads); pads.Sort((a, b) => a.yMin.CompareTo(b.yMin));
        if (pads.Count == 0) return;
        // strips: between consecutive pads, plus one south of the first and one north of the last when there is room
        var strips = new List<Vector2>();   // zMin, zMax
        float xMin = float.MaxValue, xMax = float.MinValue; foreach (var p in pads) { xMin = Mathf.Min(xMin, p.xMin); xMax = Mathf.Max(xMax, p.xMax); }
        for (int i = 1; i < pads.Count; i++) if (pads[i].yMin - pads[i - 1].yMax > 6f) strips.Add(new Vector2(pads[i - 1].yMax, pads[i].yMin));
        int n = 0;
        foreach (var st in strips)
        {
            float zc = (st.x + st.y) * 0.5f, half = (st.y - st.x) * 0.5f; var rng = s.ctx.Rng("edges/depot/strip/" + n);
            // low hedges framing the strip on both pad edges
            foreach (float side in new[] { -1f, 1f })
            {
                var a = V(xMin + 2f, zc + side * (half - 1.4f)); var b = V(xMax - 6.5f, zc + side * (half - 1.4f));
                if (!s.occ.FreeLine(a, b, 0.5f)) continue;
                TownLifeKit.Run(s.depot, "edges_depot_hedge_" + n + (side < 0 ? "_s" : "_n"), new[] { a, b }, new TownLifeKit.RunSpec { panel = P("kit.panel.hedge.mid"), panelLength = 2f, castShadows = true });
                s.occ.ReserveLine(a, b, 0.6f, "hedge.edge"); s.Count("depot.hedge");
            }
            // a row of big round trees down the middle, flower border at the street end
            int t = 0;
            for (float x = xMax - 9f; x > xMin + 1f; x -= 8.4f, t++)
            {
                var p = V(x + R(rng, -0.6f, 0.6f), zc + R(rng, -0.5f, 0.5f)); string tid = t % 2 == 0 ? "kit.tree.round.l" : "kit.tree.elm.l";
                if (s.occ.TryReserve(p, 1.2f, TagTree)) { TownLifeKit.Spawn(s.depot, P(tid), G(s, p), rng.Next(360), FS(tid) * R(rng, 0.95f, 1.15f)); s.Count("depot.tree"); }
                var f = V(x + 4.2f, zc + R(rng, -1.2f, 1.2f));
                if (t > 0 && s.occ.TryReserve(f, 1.6f, TagShrub, TownLifeOccupancy.What.Solid, TownLifeOccupancy.Block.Marker)) { TownLifeKit.Spawn(s.depot, t % 2 == 0 ? "edges.under.b" : "edges.under.a", G(s, f), rng.Next(360), 0.9f); s.Count("depot.under"); }
            }
            var fa = V(xMax - 5.6f, zc - half + 2.6f); var fb = V(xMax - 5.6f, zc + half - 2.6f);
            if (s.occ.FreeLine(fa, fb, 0.5f))
            {
                TownLifeKit.Run(s.depot, "edges_depot_border_" + n, new[] { fa, fb }, new TownLifeKit.RunSpec { panel = P("kit.panel.flowerborder.mixed"), panelLength = 2f, castShadows = false });
                s.occ.ReserveLine(fa, fb, 0.6f, "bed.edge", TownLifeOccupancy.Block.Marker); s.Count("depot.border");
            }
            n++;
        }
        // a tall hedge screen right behind the pads, so the yards have a back wall before the woods
        {
            float zs = pads[0].yMin, ze = pads[pads.Count - 1].yMax; var a = V(xMin - 2.2f, zs); var b = V(xMin - 2.2f, ze);
            if (s.occ.FreeLine(a, b, 0.5f))
            {
                TownLifeKit.Run(s.depot, "edges_depot_backhedge", new[] { a, b }, new TownLifeKit.RunSpec { panel = P("kit.panel.hedge.tall"), panelLength = 2f });
                s.occ.ReserveLine(a, b, 0.7f, "hedge.edge"); s.Count("depot.hedge");
            }
            // tall trees over the hedge: the back wall of every depot yard
            int t = 0;
            for (float z = zs + 4f; z < ze; z += 9.5f, t++)
            {
                var rng = s.ctx.Rng("edges/depot/back/" + t); var p = V(xMin - 6.5f + R(rng, -1.2f, 1.2f), z + R(rng, -1.5f, 1.5f));
                string tid = t % 3 == 0 ? "kit.clump.pine.3" : (t % 3 == 1 ? "kit.clump.mixed.5" : "kit.clump.mixed.3");
                if (OpenLand(s, p) && s.occ.TryReserve(p, 2.6f, TagTree)) { TownLifeKit.Spawn(s.depot, P(tid), G(s, p), rng.Next(360), R(rng, 1.05f, 1.3f)); s.Count("depot.tree"); }
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------- gates / seam
    static void Gates(State s)
    {
        int n = 0;
        foreach (var g in s.scene.gates)
        {
            if (!TownLifeGeo.RectContains(s.occ.area, g.position) || g.districtId != TownLifeScene.Meadows) { n++; continue; }
            if (!s.ctx.options.District(TownLifeScene.Acres) && !s.ctx.options.District(TownLifeScene.Meadows)) continue;
            var fwd = TownLifeGeo.Flat(g.forward).normalized; var right = TownLifeGeo.Right(fwd); var rng = s.ctx.Rng("edges/gate/" + g.id);
            foreach (float side in new[] { -1f, 1f })
            {
                // hedge wing square to the road, from just outside the keep-out, as far as common land allows (max 16 m)
                float d0 = 9.6f, d1 = d0;
                for (float d = d0; d <= 26f; d += 0.5f)
                {
                    var p = g.position + right * side * d;
                    if (s.plan.LabelAt(p) != TownLifePlan.Common || !s.occ.Free(p, 0.55f)) break;
                    d1 = d;
                }
                var a = g.position + right * side * d0; var b = g.position + right * side * d1; a.y = b.y = 0f;
                if (d1 - d0 >= 2.5f)
                {
                    TownLifeKit.Run(s.seam, "edges_gatehedge_" + TownLifeKit.Sanitize(g.id) + (side < 0 ? "_l" : "_r"), new[] { a, b }, new TownLifeKit.RunSpec { panel = P("kit.panel.hedge.tall"), panelLength = 2f });
                    s.occ.ReserveLine(a, b, 0.7f, "hedge.edge"); s.Count("seam.hedge");
                }
                // a matched pair of columnar trees flanking the gate, and flowers at their feet
                foreach (float along in new[] { -2.8f, 2.8f })
                {
                    var tp = g.position + right * side * (d0 + 1.6f) + fwd * along; tp.y = 0f; string tid = "kit.tree.pickle.l";
                    if (s.plan.LabelAt(tp) == TownLifePlan.Common && s.occ.TryReserve(tp, 1.1f, TagTree)) { TownLifeKit.Spawn(s.seam, P(tid), G(s, tp), rng.Next(360), FS(tid)); s.Count("seam.tree"); }
                }
                var fp = g.position + right * side * (d0 + 1.2f) - fwd * 6.2f; fp.y = 0f;
                if (s.plan.LabelAt(fp) == TownLifePlan.Common && s.occ.TryReserve(fp, 1.6f, TagShrub, TownLifeOccupancy.What.Solid, TownLifeOccupancy.Block.Marker)) { TownLifeKit.Spawn(s.seam, "edges.under.b", G(s, fp), rng.Next(360), 1f); s.Count("seam.under"); }
            }
            n++;
        }
    }

    // ---------------------------------------------------------------------------------------------------- vista ends
    /// <summary>Where a north-south street dead-ends on Home Run Road, the far side gets a hero tree and a bench when
    /// that land is common (otherwise it is somebody's lot and Yards owns it).</summary>
    static void VistaEnds(State s)
    {
        if (!s.ctx.options.District(TownLifeScene.Acres)) return;
        var home = s.scene.Road("home"); if (home == null) return;
        foreach (string id in new[] { "depot", "elm", "park", "oak" })
        {
            var road = s.scene.Road(id); if (road == null) continue;
            var end = road.points[0].z < road.points[road.points.Length - 1].z ? road.points[0] : road.points[road.points.Length - 1];
            var want = V(end.x, end.z - 18f); var rng = s.ctx.Rng("edges/vista/" + id);
            Vector3 at = want; bool ok = false;
            for (int ring = 0; ring < 9 && !ok; ring++)
                for (int k = 0; k < (ring == 0 ? 1 : 8) && !ok; k++)
                {
                    var c = want + TownLifeGeo.Dir(k * 45f) * ring * 1.5f;
                    if (s.plan.LabelAt(c) == TownLifePlan.Common && s.scene.RoadEdgeDistance(c, out _) > 7f && s.occ.Free(c, 2.2f)) { at = c; ok = true; }
                }
            if (!ok) continue;
            s.occ.ReserveCircle(at, 1.6f, TagTree);
            TownLifeKit.Spawn(s.wood, P("kit.tree.oak.xl"), G(s, at), rng.Next(360), FS("kit.tree.oak.xl") * 1.1f); s.Count("vista.tree");
            foreach (float off in new[] { -5.5f, 5.5f })
            {
                var bp = V(at.x + off, at.z + 5.5f);
                if (s.plan.LabelAt(bp) == TownLifePlan.Common && s.scene.RoadEdgeDistance(bp, out _) > 3.8f && s.occ.TryReserve(bp, 1f, TagFurniture)) { TownLifeKit.Spawn(s.wood, P("kit.bench.park"), G(s, bp), 0f); s.Count("vista.bench"); }
                var fp = V(at.x + off * 1.5f, at.z + 1.5f);
                if (s.plan.LabelAt(fp) == TownLifePlan.Common && s.occ.TryReserve(fp, 2f, TagShrub, TownLifeOccupancy.What.Solid, TownLifeOccupancy.Block.Marker)) { TownLifeKit.Spawn(s.wood, off < 0 ? "edges.wildflowers.a" : "edges.under.a", G(s, fp), rng.Next(360), 1.1f); s.Count("vista.flowers"); }
            }
        }
    }
}
