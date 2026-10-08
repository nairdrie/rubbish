// Owner: meadows agent. Contract: design/CONTRACT.md.
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Minivan Meadows commons (DESIGN section 8 'Meadows'): the two planted court islands, Meadows Common inside
/// the ring (soccer pitch, paths, picnic spots, groves, one themed corner per lobe), hedgerow planting along every
/// rear fence that faces common land, hedge buffers along the Mile and the beach-access gap of the Seaview row.
/// Everything is derived from ctx.plan / ctx.scene / ctx.occupancy: the common land is rasterised on a 2 m grid, each
/// lobe (plan common region inside the ring) gets the biggest feature that fits it. Group: TL_Meadows.</summary>
public static class TownLifeMeadows
{
    /// <summary>Module name used in options, reports and RNG keys.</summary>
    public const string Name = "meadows";

    /// <summary>Builds this module into ctx. Deterministic, physics-free, feeds ctx.occupancy.</summary>
    public static void Build(TownLifeContext ctx)
    {
        if (!ctx.options.District(TownLifeScene.Meadows)) { ctx.Log(Name, "skipped (district filter)"); return; }
        new Job(ctx).Run();
    }

    const float Cell = 2f;
    const string LampPrefab = "Assets/Suburbs Town Pack/PREFABS/Props/SM_streetlamp_03";
    const float GrassY = TownLifeContext.GrassY, LawnY = TownLifeContext.LawnY, SoilY = TownLifeContext.SoilY, PavingY = TownLifeContext.PavingY;
    static readonly string[] Petals = { "meadows.petal.pink", "meadows.petal.yellow", "meadows.petal.white", "meadows.petal.purple", "meadows.petal.red", "meadows.petal.orange" };

    sealed class Lobe { public int id, cells; public int best; public float bestClear; public string theme = "grove"; public Vector3 site; public List<int> list = new List<int>(); }

    sealed class Job
    {
        readonly TownLifeContext ctx; readonly TownLifeOccupancy occ; readonly TownLifeScene scene; readonly TownLifePlan.Result plan;
        Transform root; TownLifeScene.RoadInfo mile; Vector3[] ring;
        // 2 m raster of the common land inside the ring
        float gx0, gz0; int nx, nz; bool[] open, blocked, pathCell; int[] region; float[] clear;
        readonly List<Lobe> lobes = new List<Lobe>();
        readonly List<string> notes = new List<string>(); readonly List<string> missing = new List<string>();
        int trees, runs;
        readonly List<Vector3> picnicHints = new List<Vector3>();

        public Job(TownLifeContext c) { ctx = c; occ = c.occupancy; scene = c.scene; plan = c.plan; }

        // ------------------------------------------------------------------------------------------------ entry
        public void Run()
        {
            mile = scene.Road("meadow-loop");
            if (mile == null) { ctx.Error(Name, "road meadow-loop not found"); return; }
            ring = mile.points;
            root = ctx.Group("Meadows");
            Palette(); Props();
            int before = TownLifeKit.SpawnCount;

            foreach (var court in scene.courts) Island(court);
            Raster();
            Themes();
            foreach (var lobe in lobes) if (lobe.theme == "pitch") Pitch(lobe);
            foreach (var lobe in lobes) if (lobe.theme == "dogrun") DogRun(lobe);
            foreach (var lobe in lobes) if (lobe.theme == "orchard") Orchard(lobe);
            foreach (var lobe in lobes) if (lobe.theme == "wildflower") Wildflower(lobe);
            InfillTies();
            MouthBeds();
            MileBuffers();
            Hedgerows();
            Picnics();
            LaneVerges();
            Groves();
            BeachAccess();

            // layout signature (same inputs -> same number): lets anyone verify determinism from two reports
            uint sig = 2166136261u;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                var p = t.position; string line = t.name + "|" + Mathf.RoundToInt(p.x * 100f) + "|" + Mathf.RoundToInt(p.y * 100f) + "|" + Mathf.RoundToInt(p.z * 100f) + "|" + Mathf.RoundToInt(t.eulerAngles.y);
                var mf = t.GetComponent<MeshFilter>(); if (mf != null && mf.sharedMesh != null) line += "|" + mf.sharedMesh.vertexCount;
                foreach (char ch in line) { sig ^= ch; sig *= 16777619u; }
            }
            string themes = ""; foreach (var l in lobes) themes += (themes.Length > 0 ? ", " : "") + "region " + l.id + " " + (l.cells * 4) + " m2 -> " + l.theme;
            ctx.Log(Name, (TownLifeKit.SpawnCount - before) + " objects; sig " + sig.ToString("x8") + "; " + scene.courts.Length + " islands; lobes: " + themes + "; " + trees + " trees, " + runs + " runs"
                + (missing.Count > 0 ? "; stock props missing (fallbacks used): " + string.Join(",", missing) : "")
                + (notes.Count > 0 ? "; notes: " + string.Join(" | ", notes) : ""));
        }

        // ---------------------------------------------------------------------------------------------- palette
        void Palette()
        {
            TownLifeKit.Define("meadows.lawn.light", new Color(0.50f, 0.70f, 0.31f));
            TownLifeKit.Define("meadows.lawn.dark", new Color(0.37f, 0.58f, 0.25f));
            TownLifeKit.Define("meadows.grass", new Color(0.42f, 0.60f, 0.28f));
            TownLifeKit.Define("meadows.tallgrass", new Color(0.56f, 0.68f, 0.30f));
            TownLifeKit.Define("meadows.sand", new Color(0.94f, 0.85f, 0.62f));
            TownLifeKit.Define("meadows.dirt", new Color(0.74f, 0.62f, 0.44f));
            TownLifeKit.Define("meadows.petal.pink", new Color(1.00f, 0.50f, 0.70f));
            TownLifeKit.Define("meadows.petal.yellow", new Color(1.00f, 0.86f, 0.20f));
            TownLifeKit.Define("meadows.petal.white", new Color(0.98f, 0.98f, 0.95f));
            TownLifeKit.Define("meadows.petal.purple", new Color(0.66f, 0.45f, 0.95f));
            TownLifeKit.Define("meadows.petal.red", new Color(0.95f, 0.25f, 0.22f));
            TownLifeKit.Define("meadows.petal.orange", new Color(1.00f, 0.60f, 0.15f));
            TownLifeKit.Define("meadows.juice.box", new Color(1.00f, 0.80f, 0.22f), 0.35f);
            TownLifeKit.Define("meadows.juice.band", new Color(0.36f, 0.75f, 0.25f), 0.35f);
            TownLifeKit.Define("meadows.juice.spill", new Color(1.00f, 0.72f, 0.15f), 0.85f);
            TownLifeKit.Define("meadows.apple", new Color(0.90f, 0.16f, 0.16f), 0.4f);
            TownLifeKit.Define("meadows.net", new Color(0.90f, 0.91f, 0.93f));
            TownLifeKit.Define("meadows.cone", new Color(1.00f, 0.45f, 0.08f), 0.2f);
            TownLifeKit.Define("meadows.tunnel", new Color(0.20f, 0.62f, 0.95f), 0.3f);
            TownLifeKit.Define("meadows.dog", new Color(0.86f, 0.66f, 0.40f));
            TownLifeKit.Define("meadows.hive", new Color(0.99f, 0.90f, 0.55f));
            TownLifeKit.Define("meadows.straw", new Color(0.93f, 0.80f, 0.42f));
        }

        // ------------------------------------------------------------------------------------------------ props
        string Stock(string id, string fallback)
        {
            if (TownLifeKit.HasProp(id)) return id;
            if (!missing.Contains(id)) missing.Add(id);
            return fallback;
        }
        static Vector3 V(float x, float y, float z) { return new Vector3(x, y, z); }
        static string F(Vector3 p) { return "(" + p.x.ToString("0.0") + "," + p.z.ToString("0.0") + ")"; }

        void Props()
        {
            // --- fallbacks for stock ids that are not delivered yet (never used when the kit has them)
            TownLifeKit.DefineProp("meadows.fb.hedge").Box("leaf.dark", V(0, 0.5f, 1f), V(0.8f, 1f, 2f));
            TownLifeKit.DefineProp("meadows.fb.rail").Box("wood.warm", V(0, 0.9f, 1.25f), V(0.06f, 0.1f, 2.5f)).Box("wood.warm", V(0, 0.5f, 1.25f), V(0.06f, 0.1f, 2.5f)).NoShadows();
            TownLifeKit.DefineProp("meadows.fb.post").Cyl("wood.dark", Vector3.zero, 0.07f, 1.1f, 4).NoShadows();
            TownLifeKit.DefineProp("meadows.fb.bench").Box("wood.warm", V(0, 0.45f, 0), V(1.8f, 0.08f, 0.45f)).Box("wood.warm", V(0, 0.8f, -0.22f), V(1.8f, 0.4f, 0.06f))
                .Box("metal.dark", V(-0.8f, 0.22f, 0), V(0.08f, 0.44f, 0.4f)).Box("metal.dark", V(0.8f, 0.22f, 0), V(0.08f, 0.44f, 0.4f)).NoShadows();
            TownLifeKit.DefineProp("meadows.fb.table").Box("wood.warm", V(0, 0.75f, 0), V(1.8f, 0.07f, 0.8f)).Box("wood.warm", V(0, 0.45f, 0.75f), V(1.8f, 0.06f, 0.28f)).Box("wood.warm", V(0, 0.45f, -0.75f), V(1.8f, 0.06f, 0.28f))
                .Bar("wood.dark", V(-0.7f, 0, 0.8f), V(-0.7f, 0.75f, -0.2f), 0.08f).Bar("wood.dark", V(-0.7f, 0, -0.8f), V(-0.7f, 0.75f, 0.2f), 0.08f)
                .Bar("wood.dark", V(0.7f, 0, 0.8f), V(0.7f, 0.75f, -0.2f), 0.08f).Bar("wood.dark", V(0.7f, 0, -0.8f), V(0.7f, 0.75f, 0.2f), 0.08f).NoShadows();
            TownLifeKit.DefineProp("meadows.fb.bin").Cyl("leaf.dark", Vector3.zero, 0.3f, 0.9f, 8, 0.36f).NoShadows();
            TownLifeKit.DefineProp("meadows.fb.boulder").Blob("stone.dark", V(0, 0.35f, 0), V(0.8f, 0.55f, 0.65f), 5, 0.25f);

            // --- shrub border panel (4 m, along +Z): lumpy mixed shrubs with flower puffs, no collider
            TownLifeKit.DefineProp("meadows.border")
                .Blob("leaf.dark", V(0.05f, 0.55f, 0.7f), V(0.75f, 0.7f, 0.85f), 31)
                .Blob("leaf.mid", V(-0.15f, 0.7f, 2.0f), V(0.85f, 0.9f, 0.95f), 32)
                .Blob("leaf.lime", V(0.1f, 0.5f, 3.3f), V(0.7f, 0.6f, 0.8f), 33)
                .Blob("meadows.petal.pink", V(0.45f, 0.6f, 1.35f), V(0.4f, 0.35f, 0.45f), 34, 0.3f)
                .Blob("meadows.petal.white", V(0.5f, 0.45f, 2.8f), V(0.35f, 0.3f, 0.4f), 35, 0.3f).NoShadows();

            // --- Carpool Court hero: the Old Oak with its ring bench, tyre swing and a sleepy face
            var oak = TownLifeKit.DefineProp("meadows.oak")
                .Cyl("bark", Vector3.zero, 1.25f, 1.2f, 10, 0.95f).Cyl("bark", V(0, 1.2f, 0), 0.95f, 4.2f, 10, 0.75f);
            for (int i = 0; i < 6; i++) { float a = i * 60f + 20f; var d = TownLifeGeo.Dir(a); oak.Cone("bark", d * 0.9f, 0.55f, 1.3f, 6, V(d.z * 38f, 0, -d.x * 38f)); }
            oak.Bar("bark", V(0, 4.6f, 0), V(4.4f, 7.2f, 1.6f), 0.55f).Bar("bark", V(0, 4.8f, 0), V(-3.8f, 7.6f, -1.2f), 0.55f)
                .Bar("bark", V(0, 5.0f, 0), V(0.8f, 7.8f, -3.9f), 0.5f).Bar("bark", V(0, 5.0f, 0), V(-1.4f, 7.6f, 3.8f), 0.5f)
                .Blob("leaf.dark", V(0, 10.2f, 0), V(6.2f, 3.9f, 6.2f), 41, 0.2f, 10, 6)
                .Blob("leaf.mid", V(5.2f, 8.3f, 1.8f), V(3.8f, 2.8f, 3.6f), 42).Blob("leaf.dark", V(-4.9f, 8.6f, -1.6f), V(3.9f, 2.9f, 3.7f), 43)
                .Blob("leaf.mid", V(0.9f, 8.7f, -5.0f), V(3.7f, 2.7f, 3.6f), 44).Blob("leaf.mid", V(-1.6f, 8.4f, 5.0f), V(3.8f, 2.8f, 3.7f), 45)
                .Blob("leaf.lime", V(1.2f, 12.6f, 0.8f), V(3.6f, 2.2f, 3.4f), 46).Blob("leaf.lime", V(-3.4f, 11.2f, 3.0f), V(2.6f, 1.8f, 2.6f), 47)
                // face (front = +Z)
                .Sphere("eye.white", V(-0.42f, 3.0f, 0.86f), V(0.34f, 0.34f, 0.16f)).Sphere("eye.white", V(0.42f, 3.05f, 0.86f), V(0.38f, 0.38f, 0.16f))
                .Sphere("eye.black", V(-0.36f, 2.92f, 0.99f), V(0.15f, 0.15f, 0.08f)).Sphere("eye.black", V(0.47f, 2.93f, 0.99f), V(0.17f, 0.17f, 0.08f))
                .Sphere("wood.dark", V(0f, 2.25f, 0.9f), V(0.28f, 0.2f, 0.12f))
                // tyre swing off the east limb
                .Bar("paint.white", V(3.3f, 6.5f, 1.2f), V(3.3f, 1.55f, 1.2f), 0.05f).Torus("metal.dark", V(3.3f, 1.1f, 1.2f), 0.45f, 0.17f, 12, 6, V(90f, 20f, 0))
                // ring bench
                .Cyl("wood.warm", V(0, 0.42f, 0), 2.45f, 0.1f, 8).Cyl("wood.dark", V(0, 0f, 0), 2.05f, 0.42f, 8).Cyl("wood.warm", V(0, 0.52f, 0), 1.5f, 0.45f, 8, 1.25f)
                .ColliderTrunk(1.2f, 5.2f).ColliderBox(V(0, 0.26f, 0), V(4.2f, 0.52f, 4.2f)).Occluder();

            // --- Juice Box Court hero: Big Juicy (front = +Z; spawned tilted)
            var jb = TownLifeKit.DefineProp("meadows.juicebox")
                .Box("meadows.juice.box", V(0, 2.3f, 0), V(2.6f, 4.6f, 1.8f))
                .Box("meadows.juice.band", V(0, 0.3f, 0), V(2.66f, 0.6f, 1.86f)).Box("meadows.juice.band", V(0, 4.3f, 0), V(2.66f, 0.6f, 1.86f))
                .Gable("paint.white", V(0, 4.6f, 0), V(2.6f, 0.4f, 1.8f))
                .Cyl("paint.white", V(0, 1.55f, 0.9f), 0.78f, 0.05f, 18, -1f, V(90f, 0, 0))
                .Sphere("meadows.apple", V(0, 1.5f, 0.97f), V(0.5f, 0.48f, 0.08f)).Sphere("meadows.juice.band", V(0.18f, 2.02f, 0.97f), V(0.2f, 0.1f, 0.05f), 8, 4, false, V(0, 0, 30f))
                .Sphere("eye.white", V(-0.6f, 3.35f, 0.9f), V(0.48f, 0.48f, 0.2f)).Sphere("eye.white", V(0.6f, 3.4f, 0.9f), V(0.55f, 0.55f, 0.2f))
                .Sphere("eye.black", V(-0.5f, 3.22f, 1.06f), V(0.2f, 0.2f, 0.1f)).Sphere("eye.black", V(0.52f, 3.2f, 1.06f), V(0.24f, 0.24f, 0.1f))
                .Bar("eye.black", V(-0.42f, 2.62f, 0.92f), V(-0.2f, 2.5f, 0.92f), 0.08f).Bar("eye.black", V(-0.2f, 2.5f, 0.92f), V(0.2f, 2.5f, 0.92f), 0.08f).Bar("eye.black", V(0.2f, 2.5f, 0.92f), V(0.42f, 2.62f, 0.92f), 0.08f)
                // bendy straw
                .Cyl("paint.white", V(0.75f, 4.6f, -0.35f), 0.13f, 1.9f, 8)
                .Bar("paint.white", V(0.75f, 6.5f, -0.35f), V(1.75f, 7.15f, -0.35f), 0.24f);
            for (int i = 0; i < 4; i++) jb.Cyl("paint.red", V(0.75f, 4.85f + i * 0.4f, -0.35f), 0.145f, 0.18f, 8);
            for (int i = 0; i < 3; i++) jb.Torus("paint.red", V(0.75f + i * 0.05f, 6.3f + i * 0.1f, -0.35f), 0.15f, 0.05f, 10, 5);
            jb.ColliderBox(V(0, 2.3f, 0), V(2.6f, 4.6f, 1.8f)).Occluder();
            TownLifeKit.DefineProp("meadows.fruitpile")
                .Sphere("meadows.apple", V(0, 0.5f, 0), V(0.55f, 0.5f, 0.55f)).Cyl("bark", V(0, 0.95f, 0), 0.04f, 0.22f, 5).Sphere("leaf.mid", V(0.16f, 1.1f, 0), V(0.2f, 0.06f, 0.1f))
                .Sphere("meadows.petal.orange", V(1.0f, 0.42f, 0.5f), V(0.45f, 0.42f, 0.45f)).Sphere("meadows.apple", V(0.5f, 0.38f, -0.85f), V(0.4f, 0.38f, 0.4f))
                .Sphere("meadows.petal.yellow", V(-0.8f, 0.3f, 0.6f), V(0.5f, 0.3f, 0.3f), 10, 6, false, V(0, 30f, 15f))
                .Sphere("eye.white", V(-0.16f, 0.62f, 0.5f), V(0.13f, 0.13f, 0.07f)).Sphere("eye.white", V(0.16f, 0.62f, 0.5f), V(0.13f, 0.13f, 0.07f))
                .Sphere("eye.black", V(-0.14f, 0.6f, 0.56f), V(0.06f, 0.06f, 0.04f)).Sphere("eye.black", V(0.18f, 0.6f, 0.56f), V(0.06f, 0.06f, 0.04f)).NoShadows();

            // --- soccer
            var goal = TownLifeKit.DefineProp("meadows.goal")
                .Cyl("paint.white", V(-2.5f, 0, 0), 0.07f, 2.0f, 6).Cyl("paint.white", V(2.5f, 0, 0), 0.07f, 2.0f, 6).Bar("paint.white", V(-2.57f, 2.0f, 0), V(2.57f, 2.0f, 0), 0.14f)
                .Bar("paint.white", V(-2.5f, 2.0f, 0), V(-2.5f, 0.04f, -1.5f), 0.07f).Bar("paint.white", V(2.5f, 2.0f, 0), V(2.5f, 0.04f, -1.5f), 0.07f).Bar("paint.white", V(-2.5f, 0.04f, -1.5f), V(2.5f, 0.04f, -1.5f), 0.07f);
            for (int i = 1; i < 10; i++) goal.Bar("meadows.net", V(-2.5f + i * 0.5f, 2.0f, 0), V(-2.5f + i * 0.5f, 0.04f, -1.5f), 0.03f);
            for (int i = 1; i < 4; i++) { float t = i / 4f; goal.Bar("meadows.net", V(-2.5f, 2.0f - 1.96f * t, -1.5f * t), V(2.5f, 2.0f - 1.96f * t, -1.5f * t), 0.03f); }
            for (int s = -1; s <= 1; s += 2) for (int i = 1; i < 3; i++) { float t = i / 3f; goal.Bar("meadows.net", V(s * 2.5f, 2.0f * (1 - t), 0), V(s * 2.5f, 2.0f - 1.96f * t, -1.5f * t), 0.03f); }
            var bl = TownLifeKit.DefineProp("meadows.bleacher");
            for (int i = 0; i < 3; i++) bl.Box("wood.warm", V(0, 0.45f + 0.42f * i, -0.3f - 0.75f * i), V(7f, 0.08f, 0.5f)).Box("wood.dark", V(0, 0.12f + 0.42f * i, 0.1f - 0.75f * i), V(7f, 0.06f, 0.35f));
            foreach (float x in new[] { -3.3f, 0f, 3.3f }) bl.Bar("metal.dark", V(x, 0.04f, 0.3f), V(x, 0.04f, -2.3f), 0.09f).Bar("metal.dark", V(x, 0, -2.3f), V(x, 2.0f, -2.3f), 0.09f).Bar("metal.dark", V(x, 0.25f, 0.3f), V(x, 1.35f, -2.3f), 0.09f);
            bl.Bar("metal.dark", V(-3.5f, 2.0f, -2.3f), V(3.5f, 2.0f, -2.3f), 0.08f).Bar("metal.dark", V(-3.5f, 1.65f, -2.3f), V(3.5f, 1.65f, -2.3f), 0.06f)
                .Box("paint.red", V(-2.2f, 0.56f, -0.3f), V(0.5f, 0.12f, 0.4f)).Box("paint.blue", V(1.4f, 0.98f, -1.05f), V(0.5f, 0.12f, 0.4f)).Box("paint.yellow", V(2.6f, 1.4f, -1.8f), V(0.45f, 0.12f, 0.4f))
                .ColliderBox(V(0, 0.75f, -1.05f), V(7f, 1.5f, 2.6f));
            TownLifeKit.DefineProp("meadows.cornerflag").Cyl("paint.white", Vector3.zero, 0.035f, 1.5f, 5).Sheet("paint.red", V(0, 1.5f, 0), V(0, 1.1f, 0), V(0.5f, 1.3f, 0), V(0.5f, 1.3f, 0)).NoShadows();
            var cones = TownLifeKit.DefineProp("meadows.cones");
            for (int i = 0; i < 7; i++)
            {
                float x = -3.6f + i * 1.2f;
                if (i == 5) cones.Cone("meadows.cone", V(x - 0.2f, 0.17f, 0.1f), 0.17f, 0.48f, 8, V(0, 0, -90f)).Box("meadows.cone", V(x - 0.2f, 0.19f, 0.1f), V(0.03f, 0.38f, 0.38f));
                else cones.Cone("meadows.cone", V(x, 0.02f, (i % 2) * 0.12f), 0.17f, 0.48f, 8).Box("meadows.cone", V(x, 0.015f, (i % 2) * 0.12f), V(0.38f, 0.03f, 0.38f)).Cyl("paint.white", V(x, 0.2f, (i % 2) * 0.12f), 0.108f, 0.08f, 8, 0.082f);
            }
            cones.NoShadows();
            TownLifeKit.DefineProp("meadows.ball").Sphere("paint.white", V(0, 0.3f, 0), V(0.3f, 0.3f, 0.3f), 10, 6)
                .Sphere("eye.black", V(0, 0.58f, 0), V(0.12f, 0.04f, 0.12f)).Sphere("eye.black", V(0.27f, 0.36f, 0.08f), V(0.04f, 0.11f, 0.11f)).Sphere("eye.black", V(-0.25f, 0.3f, -0.12f), V(0.05f, 0.11f, 0.11f)).Sphere("eye.black", V(0.05f, 0.25f, -0.28f), V(0.11f, 0.11f, 0.04f))
                .Sphere("eye.white", V(-0.1f, 0.38f, 0.27f), V(0.09f, 0.09f, 0.05f)).Sphere("eye.white", V(0.1f, 0.38f, 0.27f), V(0.09f, 0.09f, 0.05f))
                .Sphere("eye.black", V(-0.09f, 0.37f, 0.31f), V(0.04f, 0.04f, 0.03f)).Sphere("eye.black", V(0.11f, 0.37f, 0.31f), V(0.04f, 0.04f, 0.03f)).NoShadows();
            var stop = TownLifeKit.DefineProp("meadows.ballstop");
            foreach (float x in new[] { -7f, -2.33f, 2.33f, 7f }) stop.Cyl("metal.dark", V(x, 0, 0), 0.07f, 5.2f, 6);
            for (int i = 0; i <= 5; i++) stop.Bar("meadows.net", V(-7f, 0.2f + i * 1.0f, 0), V(7f, 0.2f + i * 1.0f, 0), 0.035f);
            for (int i = 1; i < 14; i++) stop.Bar("meadows.net", V(-7f + i, 0.2f, 0), V(-7f + i, 5.2f, 0), 0.03f);
            stop.NoShadows();
            TownLifeKit.DefineProp("meadows.blanket").Geometry(d =>
            {
                for (int i = 0; i < 6; i++) for (int j = 0; j < 4; j++)
                {
                    float x0 = -1.2f + i * 0.4f, z0 = -0.8f + j * 0.4f;
                    d.QuadOut((i + j) % 2 == 0 ? "paint.red" : "paint.white", V(0, -5f, 0), V(x0, 0.03f, z0), V(x0 + 0.4f, 0.03f, z0), V(x0 + 0.4f, 0.03f, z0 + 0.4f), V(x0, 0.03f, z0 + 0.4f));
                }
            }).Box("wood.warm", V(0.6f, 0.17f, 0.2f), V(0.5f, 0.3f, 0.35f), V(0, 25f, 0)).Torus("wood.dark", V(0.6f, 0.32f, 0.2f), 0.2f, 0.025f, 10, 4, V(90f, 25f, 0))
              .Sphere("meadows.apple", V(-0.3f, 0.12f, -0.2f), V(0.09f, 0.09f, 0.09f), 6, 4).Sphere("meadows.petal.orange", V(-0.5f, 0.12f, 0.1f), V(0.09f, 0.09f, 0.09f), 6, 4)
              .Cyl("paint.blue", V(0.1f, 0.03f, -0.4f), 0.07f, 0.22f, 6).NoShadows();
            TownLifeKit.DefineProp("meadows.fb.bollard").Cyl("wood.dark", Vector3.zero, 0.12f, 0.8f, 6).Cyl("paint.white", V(0, 0.8f, 0), 0.13f, 0.1f, 6).NoShadows();
            var bunt = TownLifeKit.DefineProp("meadows.bunting").Cyl("wood.dark", V(-5f, 0, 0), 0.07f, 3.5f, 6).Cyl("wood.dark", V(5f, 0, 0), 0.07f, 3.5f, 6);
            for (int i = 0; i < 10; i++)
            {
                float x0 = -5f + i, x1 = x0 + 1f; float y0 = 3.4f - 0.7f * (1f - (x0 / 5f) * (x0 / 5f)), y1 = 3.4f - 0.7f * (1f - (x1 / 5f) * (x1 / 5f));
                bunt.Bar("paint.white", V(x0, y0, 0), V(x1, y1, 0), 0.03f);
                var tip = V((x0 + x1) * 0.5f, (y0 + y1) * 0.5f - 0.55f, 0);
                bunt.Sheet(Petals[i % Petals.Length], V(x0 + 0.12f, y0 + (y1 - y0) * 0.12f, 0), V(x1 - 0.12f, y1 - (y1 - y0) * 0.12f, 0), tip, tip);
            }
            bunt.NoShadows();
            TownLifeKit.DefineProp("meadows.teamkit")
                .Box("paint.white", V(0, 0.72f, 0), V(1.4f, 0.05f, 0.6f)).Bar("metal.dark", V(-0.6f, 0, -0.22f), V(-0.6f, 0.7f, 0.22f), 0.04f).Bar("metal.dark", V(-0.6f, 0, 0.22f), V(-0.6f, 0.7f, -0.22f), 0.04f)
                .Bar("metal.dark", V(0.6f, 0, -0.22f), V(0.6f, 0.7f, 0.22f), 0.04f).Bar("metal.dark", V(0.6f, 0, 0.22f), V(0.6f, 0.7f, -0.22f), 0.04f)
                .Cyl("meadows.cone", V(-0.35f, 0.745f, 0), 0.2f, 0.38f, 10).Cyl("paint.white", V(-0.35f, 1.125f, 0), 0.21f, 0.06f, 10)
                .Cyl("paint.white", V(0.15f, 0.745f, 0.1f), 0.04f, 0.1f, 6).Cyl("paint.white", V(0.3f, 0.745f, -0.08f), 0.04f, 0.1f, 6).Cyl("paint.white", V(0.45f, 0.745f, 0.12f), 0.04f, 0.1f, 6)
                .Sphere("paint.blue", V(1.3f, 0.2f, 0.1f), V(0.42f, 0.2f, 0.24f)).Sphere("paint.red", V(1.5f, 0.18f, -0.5f), V(0.36f, 0.18f, 0.22f), 10, 6, false, V(0, 40f, 0))
                .Sphere("paint.white", V(-1.2f, 0.22f, 0.2f), V(0.22f, 0.22f, 0.22f)).Sphere("paint.white", V(-1.55f, 0.22f, -0.1f), V(0.22f, 0.22f, 0.22f)).Sphere("paint.yellow", V(-1.3f, 0.22f, -0.35f), V(0.22f, 0.22f, 0.22f)).NoShadows();
            TownLifeKit.DefineProp("meadows.signposts").Cyl("wood.dark", V(-2.05f, 0, 0), 0.09f, 3.0f, 6).Cyl("wood.dark", V(2.05f, 0, 0), 0.09f, 3.0f, 6).NoShadows();
            TownLifeKit.DefineProp("meadows.signpost1").Cyl("wood.dark", Vector3.zero, 0.08f, 2.2f, 6).NoShadows();

            // --- bark park
            TownLifeKit.DefineProp("meadows.agility")
                // jump
                .Cyl("paint.white", V(-6.4f, 0, -0.9f), 0.06f, 1.0f, 6).Cyl("paint.white", V(-6.4f, 0, 0.9f), 0.06f, 1.0f, 6).Bar("paint.red", V(-6.4f, 0.55f, -0.9f), V(-6.4f, 0.55f, 0.9f), 0.09f).Bar("paint.yellow", V(-6.4f, 0.3f, -0.9f), V(-6.4f, 0.3f, 0.9f), 0.07f)
                // tunnel (along x)
                .Cyl("meadows.tunnel", V(-4.6f, 0.5f, 0.6f), 0.55f, 2.6f, 10, -1f, V(0, 0, -90f)).Cyl("eye.black", V(-4.62f, 0.5f, 0.6f), 0.45f, 2.64f, 10, -1f, V(0, 0, -90f))
                // weave poles
                .Cyl("paint.red", V(-0.9f, 0, -1.2f), 0.04f, 1.0f, 5).Cyl("paint.white", V(-0.3f, 0, -1.2f), 0.04f, 1.0f, 5).Cyl("paint.red", V(0.3f, 0, -1.2f), 0.04f, 1.0f, 5).Cyl("paint.white", V(0.9f, 0, -1.2f), 0.04f, 1.0f, 5).Cyl("paint.red", V(1.5f, 0, -1.2f), 0.04f, 1.0f, 5).Cyl("paint.white", V(2.1f, 0, -1.2f), 0.04f, 1.0f, 5)
                // A-frame
                .Box("paint.yellow", V(3.55f, 0.55f, 0.9f), V(1.7f, 0.07f, 1.0f), V(0, 0, 38f)).Box("paint.blue", V(4.85f, 0.55f, 0.9f), V(1.7f, 0.07f, 1.0f), V(0, 0, -38f))
                // tyre hoop
                .Cyl("wood.warm", V(6.3f, 0, -0.75f), 0.06f, 1.5f, 5).Cyl("wood.warm", V(6.3f, 0, 0.75f), 0.06f, 1.5f, 5).Bar("wood.warm", V(6.3f, 1.5f, -0.8f), V(6.3f, 1.5f, 0.8f), 0.08f).Torus("metal.dark", V(6.3f, 0.85f, 0), 0.42f, 0.12f, 12, 6, V(0, 0, 90f))
                // hydrant (the dog-park joke)
                .Cyl("paint.red", V(1.2f, 0, 1.6f), 0.16f, 0.6f, 8).Dome("paint.red", V(1.2f, 0.6f, 1.6f), V(0.17f, 0.14f, 0.17f)).Cyl("paint.red", V(1.2f, 0.36f, 1.6f), 0.07f, 0.52f, 6, -1f, V(0, 0, 90f)).Cyl("paint.red", V(1.46f, 0.36f, 1.6f), 0.07f, 0.01f, 6)
                .NoShadows();
            TownLifeKit.DefineProp("meadows.dog")
                .Box("meadows.dog", V(0, 0.42f, 0), V(0.36f, 0.34f, 0.8f)).Sphere("meadows.dog", V(0, 0.72f, 0.5f), V(0.26f, 0.24f, 0.28f)).Box("meadows.dog", V(0, 0.64f, 0.78f), V(0.16f, 0.13f, 0.2f))
                .Sphere("eye.black", V(0, 0.68f, 0.89f), V(0.05f, 0.04f, 0.04f)).Box("wood.dark", V(-0.25f, 0.7f, 0.45f), V(0.06f, 0.26f, 0.16f), V(0, 0, 12f)).Box("wood.dark", V(0.25f, 0.7f, 0.45f), V(0.06f, 0.26f, 0.16f), V(0, 0, -12f))
                .Cyl("meadows.dog", V(-0.12f, 0, 0.3f), 0.06f, 0.3f, 5).Cyl("meadows.dog", V(0.12f, 0, 0.3f), 0.06f, 0.3f, 5).Cyl("meadows.dog", V(-0.12f, 0, -0.3f), 0.06f, 0.3f, 5).Cyl("meadows.dog", V(0.12f, 0, -0.3f), 0.06f, 0.3f, 5)
                .Bar("meadows.dog", V(0, 0.55f, -0.4f), V(0, 0.95f, -0.6f), 0.07f).Sphere("paint.red", V(0, 0.52f, 0.92f), V(0.05f, 0.09f, 0.02f))
                .Sphere("eye.white", V(-0.11f, 0.84f, 0.7f), V(0.09f, 0.09f, 0.06f)).Sphere("eye.white", V(0.11f, 0.85f, 0.7f), V(0.1f, 0.1f, 0.06f))
                .Sphere("eye.black", V(-0.1f, 0.83f, 0.75f), V(0.04f, 0.04f, 0.03f)).Sphere("eye.black", V(0.13f, 0.83f, 0.75f), V(0.045f, 0.045f, 0.03f)).NoShadows();

            // --- orchard and meadow dressing
            TownLifeKit.DefineProp("meadows.scarecrow")
                .Cyl("wood.dark", Vector3.zero, 0.06f, 2.1f, 5).Bar("wood.dark", V(-0.95f, 1.5f, 0), V(0.95f, 1.5f, 0), 0.08f)
                .Box("paint.red", V(0, 1.25f, 0), V(0.62f, 0.75f, 0.3f)).Box("paint.red", V(-0.6f, 1.5f, 0), V(0.6f, 0.2f, 0.22f)).Box("paint.red", V(0.6f, 1.5f, 0), V(0.6f, 0.2f, 0.22f))
                .Box("paint.blue", V(0, 0.72f, 0), V(0.56f, 0.4f, 0.28f)).Sphere("meadows.straw", V(0, 1.95f, 0), V(0.3f, 0.3f, 0.28f))
                .Cone("meadows.straw", V(0, 2.16f, 0), 0.52f, 0.1f, 10).Cone("wood.warm", V(0, 2.2f, 0), 0.26f, 0.4f, 8)
                .Sphere("eye.white", V(-0.11f, 2.0f, 0.24f), V(0.1f, 0.1f, 0.06f)).Sphere("eye.white", V(0.12f, 2.02f, 0.24f), V(0.12f, 0.12f, 0.06f))
                .Sphere("eye.black", V(-0.1f, 1.98f, 0.29f), V(0.045f, 0.045f, 0.03f)).Sphere("eye.black", V(0.14f, 1.99f, 0.29f), V(0.05f, 0.05f, 0.03f))
                .Cone("meadows.petal.orange", V(0, 1.9f, 0.26f), 0.05f, 0.22f, 5, V(90f, 0, 0)).NoShadows();
            var crates = TownLifeKit.DefineProp("meadows.crates")
                .Box("wood.warm", V(0, 0.2f, 0), V(0.8f, 0.4f, 0.55f)).Box("wood.warm", V(0.9f, 0.2f, 0.1f), V(0.8f, 0.4f, 0.55f), V(0, 12f, 0)).Box("wood.warm", V(0.4f, 0.6f, 0.05f), V(0.8f, 0.4f, 0.55f), V(0, -8f, 0))
                // ladder
                .Bar("wood.dark", V(-1.2f, 0, 0.5f), V(-1.2f, 2.6f, -0.3f), 0.06f).Bar("wood.dark", V(-1.7f, 0, 0.5f), V(-1.7f, 2.6f, -0.3f), 0.06f);
            for (int i = 0; i < 6; i++) { float t = (i + 0.7f) / 7f; crates.Bar("wood.dark", V(-1.2f, 2.6f * t, 0.5f - 0.8f * t), V(-1.7f, 2.6f * t, 0.5f - 0.8f * t), 0.045f); }
            for (int i = 0; i < 5; i++) crates.Sphere("meadows.apple", V(0.15f + i * 0.14f, 0.84f, -0.1f + (i % 2) * 0.2f), V(0.1f, 0.1f, 0.1f), 6, 4);
            for (int i = 0; i < 4; i++) crates.Sphere("meadows.apple", V(0.65f + i * 0.14f, 0.44f, 0f + (i % 2) * 0.2f), V(0.1f, 0.1f, 0.1f), 6, 4);
            crates.NoShadows();
            var hive = TownLifeKit.DefineProp("meadows.hives");
            for (int i = 0; i < 3; i++)
            {
                float x = -1.3f + i * 1.3f; float h = i == 1 ? 1.15f : 0.85f;
                hive.Box("wood.dark", V(x, 0.15f, 0), V(0.7f, 0.3f, 0.7f)).Box(i == 1 ? "paint.white" : "meadows.hive", V(x, 0.3f + (h - 0.3f) * 0.5f, 0), V(0.62f, h - 0.3f, 0.62f))
                    .Gable("paint.blue", V(x, h, 0), V(0.8f, 0.22f, 0.8f)).Box("eye.black", V(x, 0.36f, 0.31f), V(0.2f, 0.04f, 0.02f));
            }
            hive.NoShadows();
            var sun = TownLifeKit.DefineProp("meadows.sunflower")
                .Cyl("leaf.mid", Vector3.zero, 0.13f, 4.0f, 6, 0.1f).Sphere("leaf.mid", V(0.55f, 1.6f, 0), V(0.6f, 0.08f, 0.3f), 8, 4, false, V(0, 0, 25f)).Sphere("leaf.mid", V(-0.55f, 2.3f, 0.1f), V(0.6f, 0.08f, 0.3f), 8, 4, false, V(0, 0, -25f))
                .Cyl("wood.dark", V(0, 4.25f, 0.05f), 0.62f, 0.2f, 12, -1f, V(78f, 0, 0));
            for (int i = 0; i < 12; i++) { float a = i * 30f * Mathf.Deg2Rad; sun.Sphere("meadows.petal.yellow", V(Mathf.Cos(a) * 0.95f, 4.3f + Mathf.Sin(a) * 0.93f, 0.12f - Mathf.Sin(a) * 0.2f), V(0.42f, 0.2f, 0.07f), 6, 4, false, V(-12f, 0, i * 30f)); }
            sun.Sphere("eye.white", V(-0.2f, 4.45f, 0.26f), V(0.17f, 0.17f, 0.08f)).Sphere("eye.white", V(0.2f, 4.47f, 0.26f), V(0.2f, 0.2f, 0.08f))
                .Sphere("eye.black", V(-0.18f, 4.42f, 0.33f), V(0.07f, 0.07f, 0.04f)).Sphere("eye.black", V(0.23f, 4.42f, 0.33f), V(0.08f, 0.08f, 0.04f))
                .Bar("eye.black", V(-0.18f, 4.12f, 0.24f), V(0f, 4.05f, 0.24f), 0.05f).Bar("eye.black", V(0f, 4.05f, 0.24f), V(0.18f, 4.12f, 0.24f), 0.05f);
            TownLifeKit.DefineProp("meadows.surfboard")
                .Sphere("paint.yellow", V(0, 1.15f, 0), V(0.3f, 1.25f, 0.05f), 10, 6, false, V(0, 0, 8f)).Sphere("paint.red", V(0.02f, 1.2f, 0.035f), V(0.07f, 1.0f, 0.03f), 6, 4, false, V(0, 0, 8f))
                .Sphere("paint.blue", V(0.9f, 1.0f, 0.1f), V(0.27f, 1.1f, 0.05f), 10, 6, false, V(0, 0, -11f)).NoShadows();
            TownLifeKit.DefineProp("meadows.fb.beachfence").Box("meadows.straw", V(0, 0.55f, 0.25f), V(0.03f, 1.1f, 0.12f), V(0, 0, 5f)).Box("meadows.straw", V(0, 0.5f, 0.7f), V(0.03f, 1.0f, 0.12f), V(0, 0, -6f))
                .Box("meadows.straw", V(0, 0.55f, 1.2f), V(0.03f, 1.1f, 0.12f), V(0, 0, 3f)).Box("meadows.straw", V(0, 0.52f, 1.7f), V(0.03f, 1.05f, 0.12f), V(0, 0, -4f))
                .Box("wood.dark", V(0, 0.75f, 1f), V(0.02f, 0.03f, 2f)).Box("wood.dark", V(0, 0.35f, 1f), V(0.02f, 0.03f, 2f)).NoShadows();
        }

        // ------------------------------------------------------------------------------------------ small helpers
        Vector3 G(Vector3 p) { p.y = scene.GroundY(p); return p; }
        static Vector3 At(Vector3 p, float y) { p.y = y; return p; }
        bool InRing(Vector3 p) { return TownLifeGeo.PolygonContains(ring, p); }
        static float R(System.Random rng, float a, float b) { return TownLifeContext.Range(rng, a, b); }
        static float R(float a, float b, System.Random rng) { return TownLifeContext.Range(rng, a, b); }

        string TreeId(string id, out float scale)
        {
            scale = 1f; if (TownLifeKit.HasProp(id)) return id;
            if (!missing.Contains(id)) missing.Add(id);
            scale = id.EndsWith(".xl") ? 1.8f : id.EndsWith(".l") || id.EndsWith(".5") ? 1.4f : id.EndsWith(".s") ? 0.58f : 0.95f;
            return "kit.tree.round";
        }
        bool Tree(Transform parent, string id, Vector3 p, System.Random rng, float scaleMul = 1f, float reserve = 1.2f, float spacing = 0f)
        {
            if (spacing > 0f && occ.Within(p, spacing, TownLifeTags.Tree)) return false;
            if (!occ.TryReserve(p, reserve, TownLifeTags.Tree + ".meadows")) return false;
            string prop = TreeId(id, out float s);
            TownLifeKit.Spawn(parent, prop, G(p), R(rng, 0f, 360f), s * scaleMul * R(rng, 0.9f, 1.15f));
            trees++; return true;
        }
        GameObject Prop(Transform parent, string id, string fallback, Vector3 p, float yaw, float radius, string tag, float scale = 1f)
        {
            if (!occ.TryReserve(p, radius, tag + ".meadows")) return null;
            return TownLifeKit.Spawn(parent, Stock(id, fallback), G(p), yaw, scale);
        }
        string HedgePanel(string size) { return Stock("kit.panel.hedge." + size, "meadows.fb.hedge"); }

        /// One flowering plant (leafy tuft + a blossom) appended to a merged mesh.
        static void Plant(TownLifeKit.MeshData d, Vector3 p, string petal, System.Random rng, float size = 1f)
        {
            float h = R(rng, 0.16f, 0.3f) * size, r = R(rng, 0.18f, 0.27f) * size, a0 = R(rng, 0f, 1.5f);
            var top = p + Vector3.up * h; var inside = p + Vector3.up * (h * 0.3f);
            var b = new Vector3[4]; for (int i = 0; i < 4; i++) { float a = a0 + i * Mathf.PI * 0.5f; b[i] = p + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r); }
            for (int i = 0; i < 4; i++) d.TriOut("leaf.mid", inside, b[i], b[(i + 1) % 4], top);
            float pr = R(rng, 0.12f, 0.18f) * size; var c = top + Vector3.up * (pr * 0.45f);
            var e = new Vector3[4]; for (int i = 0; i < 4; i++) { float a = a0 + 0.7f + i * Mathf.PI * 0.5f; e[i] = c + new Vector3(Mathf.Cos(a) * pr, 0f, Mathf.Sin(a) * pr); }
            Vector3 up = c + Vector3.up * pr * 0.8f, dn = c - Vector3.up * pr * 0.8f;
            for (int i = 0; i < 4; i++) { d.TriOut(petal, c, e[i], e[(i + 1) % 4], up); d.TriOut(petal, c, e[i], e[(i + 1) % 4], dn); }
        }
        static void Annulus(TownLifeKit.MeshData d, string mat, Vector3 c, float r0, float r1, float y, int segs, float a0 = 0f, float a1 = 360f)
        {
            for (int i = 0; i < segs; i++)
            {
                float u = Mathf.Lerp(a0, a1, i / (float)segs) * Mathf.Deg2Rad, v = Mathf.Lerp(a0, a1, (i + 1) / (float)segs) * Mathf.Deg2Rad;
                Vector3 du = new Vector3(Mathf.Sin(u), 0, Mathf.Cos(u)), dv = new Vector3(Mathf.Sin(v), 0, Mathf.Cos(v));
                d.QuadOut(mat, At(c, y - 10f), At(c + du * r0, y), At(c + du * r1, y), At(c + dv * r1, y), At(c + dv * r0, y));
            }
        }
        static void Wall(TownLifeKit.MeshData d, string mat, Vector3 c, float r0, float r1, float y0, float y1, int segs)
        {
            Annulus(d, mat, c, r0, r1, y1, segs);
            for (int i = 0; i < segs; i++)
            {
                float u = i * Mathf.PI * 2f / segs, v = (i + 1) * Mathf.PI * 2f / segs;
                Vector3 du = new Vector3(Mathf.Sin(u), 0, Mathf.Cos(u)), dv = new Vector3(Mathf.Sin(v), 0, Mathf.Cos(v));
                var mid = At(c + (du + dv) * 0.5f * (r0 + r1) * 0.5f, (y0 + y1) * 0.5f);
                d.QuadOut(mat, mid, At(c + du * r1, y0), At(c + dv * r1, y0), At(c + dv * r1, y1), At(c + du * r1, y1));
                d.QuadOut(mat, mid, At(c + du * r0, y0), At(c + dv * r0, y0), At(c + dv * r0, y1), At(c + du * r0, y1));
            }
        }
        static void RectUV(TownLifeKit.MeshData d, string mat, Vector3 c, Vector3 u, Vector3 v, float a0, float a1, float b0, float b1, float y)
        {
            d.QuadOut(mat, At(c, y - 10f), At(c + u * a0 + v * b0, y), At(c + u * a1 + v * b0, y), At(c + u * a1 + v * b1, y), At(c + u * a0 + v * b1, y));
        }
        static void DiscAt(TownLifeKit.MeshData d, string mat, Vector3 c, float rx, float rz, float yawDeg, float y, int segs = 14)
        {
            var f = TownLifeGeo.Dir(yawDeg); var r = TownLifeGeo.Right(f); var pts = new List<Vector3>();
            for (int i = 0; i < segs; i++) { float a = i * Mathf.PI * 2f / segs; pts.Add(c + r * (Mathf.Cos(a) * rx) + f * (Mathf.Sin(a) * rz)); }
            TownLifeKit.AddPolygon(d, mat, pts, y);
        }
        void Sign(Transform parent, string name, Vector3 foot, float yaw, float width, float height, float boardCentreY, string mat, string text, Color textColor, bool twoPosts)
        {
            var p = G(foot);
            if (twoPosts) TownLifeKit.Spawn(parent, "meadows.signposts", p, yaw, new Vector3(width / 4.1f * 1.02f, (boardCentreY + height * 0.5f + 0.1f) / 3f, 1f));
            else TownLifeKit.Spawn(parent, "meadows.signpost1", p, yaw, new Vector3(1f, (boardCentreY + height * 0.5f + 0.05f) / 2.2f, 1f));
            var f = TownLifeGeo.Dir(yaw);
            TownLifeKit.SignBoard(parent, name, p + Vector3.up * boardCentreY + f * (twoPosts ? 0f : 0.1f), yaw, new Vector3(width, height, 0.07f), mat, text, textColor, true);
            occ.ReserveCircle(foot, 0.8f, TownLifeTags.Furniture + ".meadows");
        }

        // ---------------------------------------------------------------------------------------------- islands
        void Island(TownLifeScene.CourtInfo court)
        {
            bool oak = court.roadId.StartsWith("carpool");
            string key = TownLifeKit.Sanitize(court.roadId);
            var g = TownLifeKit.Group(root, "TL_Island_" + key);
            var c = court.centre; c.y = 0f; float rad = court.islandRadius;                 // 14: inner sidewalk starts here
            float kerbOut = rad - 1.05f, kerbIn = kerbOut - 0.45f, bedIn = kerbIn - 2.3f, hedgeR = bedIn - 0.75f;
            var rng = ctx.Rng("meadows/island/" + court.roadId);
            float entYaw = TownLifeGeo.Yaw(court.entrance);

            // ground layers: mown disc, soil bed ring, gravel ring + four spokes, stone kerb
            var flat = new TownLifeKit.MeshData();
            Annulus(flat, "meadows.lawn.light", c, 0f, hedgeR - 0.5f, LawnY, 28);
            Annulus(flat, "soil", c, bedIn, kerbIn + 0.05f, SoilY, 36);
            Annulus(flat, "gravel", c, 4.0f, 5.7f, PavingY, 28);
            for (int k = 0; k < 4; k++) { var u = TownLifeGeo.Dir(entYaw + k * 90f); RectUV(flat, "gravel", c, u, TownLifeGeo.Right(u), 5.6f, kerbIn, -0.85f, 0.85f, PavingY); }
            TownLifeKit.Custom(g, "meadows_island_ground_" + key, flat);
            var kerb = new TownLifeKit.MeshData(); Wall(kerb, "stone.light", c, kerbIn, kerbOut, GrassY - 0.05f, GrassY + 0.3f, 40);
            TownLifeKit.Custom(g, "meadows_island_kerb_" + key, kerb);

            // flower ring: colour blocks between the spokes, one merged mesh
            var fl = new TownLifeKit.MeshData(); int col0 = oak ? 0 : 1;
            for (int i = 0; i < 520; i++)
            {
                float ang = R(rng, 0f, 360f), rr = R(rng, bedIn + 0.25f, kerbIn - 0.2f);
                float rel = Mathf.Repeat(ang - entYaw + 45f, 90f) - 45f; if (Mathf.Abs(rel) * Mathf.Deg2Rad * rr < 1.15f) continue;   // spokes stay clear
                int band = Mathf.FloorToInt(Mathf.Repeat(ang - entYaw, 360f) / 30f);
                Plant(fl, At(c + TownLifeGeo.Dir(ang) * rr, SoilY), Petals[(col0 + band * 2 + (rr > (bedIn + kerbIn) * 0.5f ? 1 : 0)) % Petals.Length], rng, 1.25f);
            }
            TownLifeKit.Custom(g, "meadows_island_flowers_" + key, fl, false, false);

            // low hedge quadrants inside the bed, topiary cones at the gaps
            for (int k = 0; k < 4; k++)
            {
                var arc = new List<Vector3>(); float mid = entYaw + 45f + k * 90f;
                for (int i = 0; i <= 8; i++) arc.Add(c + TownLifeGeo.Dir(mid - 34f + i * 8.5f) * hedgeR);
                TownLifeKit.Run(g, "meadows_island_hedge_" + key + "_" + k, arc, new TownLifeKit.RunSpec { panel = HedgePanel("low"), panelLength = 2f, y = GrassY });
                runs++;
                foreach (float e in new[] { -39f, 39f })
                {
                    string cone = Stock("kit.shrub.cone", "kit.shrub.round");
                    TownLifeKit.Spawn(g, cone, G(c + TownLifeGeo.Dir(mid + e) * hedgeR), R(rng, 0, 360f), 1.15f);
                }
            }

            float heroYaw = entYaw - (oak ? 0f : 38f);      // faces the lane that enters the court (the box turned a little into the afternoon sun)
            if (oak)
            {
                TownLifeKit.Spawn(g, "meadows.oak", G(c), heroYaw, 1.12f);
                foreach (float a in new[] { 60f, 185f, 300f }) TownLifeKit.Spawn(g, Stock("kit.shrub.flower.pink", "kit.shrub.round"), G(c + TownLifeGeo.Dir(entYaw + a) * 7.2f), a, 1.1f);
            }
            else
            {
                TownLifeKit.Mound(g, "meadows_island_mound_" + key, c, new Vector2(3.6f, 3.2f), 0.65f, "meadows.grass", GrassY, 77);
                var spill = new TownLifeKit.MeshData(); var right = TownLifeGeo.Right(court.entrance);
                DiscAt(spill, "meadows.juice.spill", c + right * 2.9f + court.entrance * 1.0f, 2.3f, 1.5f, entYaw + 60f, PavingY + 0.03f, 14);
                DiscAt(spill, "meadows.juice.spill", c + right * 4.6f + court.entrance * 2.4f, 0.8f, 0.6f, entYaw, PavingY + 0.03f, 10);
                DiscAt(spill, "meadows.juice.spill", c + right * 3.6f - court.entrance * 0.9f, 0.5f, 0.45f, entYaw, PavingY + 0.03f, 8);
                TownLifeKit.Custom(g, "meadows_island_spill_" + key, spill);
                TownLifeKit.SpawnRotated(g, "meadows.juicebox", G(c) + Vector3.up * 0.42f, Quaternion.Euler(0f, heroYaw, 0f) * Quaternion.Euler(4f, 0f, -11f), Vector3.one * 1.25f);
                TownLifeKit.Spawn(g, "meadows.fruitpile", G(c - right * 4.9f + court.entrance * 0.6f), entYaw + 20f, 1.2f);
                foreach (float a in new[] { 95f, 265f }) TownLifeKit.Spawn(g, Stock("kit.bench.park", "meadows.fb.bench"), G(c + TownLifeGeo.Dir(entYaw + a) * 6.6f), entYaw + a + 180f);
                foreach (float a in new[] { 140f, 220f }) Tree(g, "kit.tree.blossom.m", c + TownLifeGeo.Dir(entYaw + a) * 7.4f, rng, 1.1f, 0.5f);
            }
            // night lamp beside the entrance spoke
            var lampAt = c + court.entrance * (hedgeR + 1.5f) + TownLifeGeo.Right(court.entrance) * (oak ? 1.9f : -1.9f);
            if (TownLifeKit.SpawnPrefab(g, LampPrefab, G(lampAt), entYaw, 1f, true, true) == null) notes.Add("lamp prefab missing");
            TownLifeKit.Spawn(g, Stock("kit.bin.park", "meadows.fb.bin"), G(c + court.entrance * 6.6f - TownLifeGeo.Right(court.entrance) * 1.6f), entYaw);
            occ.ReserveCircle(c, kerbOut, TownLifeTags.Feature + ".meadows.island");
        }

        // ------------------------------------------------------------------------------------------------ raster
        int Ix(Vector3 p) { int ix = Mathf.FloorToInt((p.x - gx0) / Cell), iz = Mathf.FloorToInt((p.z - gz0) / Cell); return ix < 0 || iz < 0 || ix >= nx || iz >= nz ? -1 : iz * nx + ix; }
        Vector3 Centre(int i) { return new Vector3(gx0 + (i % nx + 0.5f) * Cell, 0f, gz0 + (i / nx + 0.5f) * Cell); }
        bool Open(Vector3 p) { int i = Ix(p); return i >= 0 && open[i]; }
        bool Routable(Vector3 p) { int i = Ix(p); return i >= 0 && open[i] && !blocked[i]; }
        float Clear(Vector3 p) { int i = Ix(p); return i < 0 ? 0f : clear[i]; }
        int Region(Vector3 p) { int i = Ix(p); return i < 0 || !open[i] ? -1 : region[i]; }

        void Raster()
        {
            float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
            foreach (var p in ring) { x0 = Mathf.Min(x0, p.x); x1 = Mathf.Max(x1, p.x); z0 = Mathf.Min(z0, p.z); z1 = Mathf.Max(z1, p.z); }
            gx0 = Mathf.Floor(x0) - 2f; gz0 = Mathf.Floor(z0) - 2f; nx = Mathf.CeilToInt((x1 - gx0 + 2f) / Cell); nz = Mathf.CeilToInt((z1 - gz0 + 2f) / Cell);
            int n = nx * nz; open = new bool[n]; blocked = new bool[n]; pathCell = new bool[n]; region = new int[n]; clear = new float[n];
            for (int i = 0; i < n; i++)
            {
                var p = Centre(i); region[i] = -1;
                int id = plan.CommonAt(p); if (id < 0 || !InRing(p)) continue;
                bool island = false; foreach (var c in scene.courts) if (TownLifeGeo.Dist(c.centre, p) < c.outerRadius) island = true;
                if (island || !occ.Free(p, 1.0f, TownLifeOccupancy.What.Flat)) continue;
                open[i] = true; region[i] = id;
            }
            // chamfer distance to the nearest closed cell
            const float big = 999f, o = Cell, dg = Cell * 1.4142f;
            for (int i = 0; i < n; i++) clear[i] = open[i] ? big : 0f;
            for (int z = 0; z < nz; z++) for (int x = 0; x < nx; x++)
            {
                int i = z * nx + x; if (!open[i]) continue; float m = clear[i];
                if (x == 0 || z == 0 || x == nx - 1) { clear[i] = Mathf.Min(m, o); m = clear[i]; }
                if (x > 0) m = Mathf.Min(m, clear[i - 1] + o); if (z > 0) m = Mathf.Min(m, clear[i - nx] + o);
                if (x > 0 && z > 0) m = Mathf.Min(m, clear[i - nx - 1] + dg); if (x < nx - 1 && z > 0) m = Mathf.Min(m, clear[i - nx + 1] + dg);
                clear[i] = m;
            }
            for (int z = nz - 1; z >= 0; z--) for (int x = nx - 1; x >= 0; x--)
            {
                int i = z * nx + x; if (!open[i]) continue; float m = clear[i];
                if (z == nz - 1) m = Mathf.Min(m, o);
                if (x < nx - 1) m = Mathf.Min(m, clear[i + 1] + o); if (z < nz - 1) m = Mathf.Min(m, clear[i + nx] + o);
                if (x < nx - 1 && z < nz - 1) m = Mathf.Min(m, clear[i + nx + 1] + dg); if (x > 0 && z < nz - 1) m = Mathf.Min(m, clear[i + nx - 1] + dg);
                clear[i] = m;
            }
            var byId = new SortedDictionary<int, Lobe>();
            for (int i = 0; i < n; i++)
            {
                if (!open[i]) continue;
                if (!byId.TryGetValue(region[i], out var l)) byId[region[i]] = l = new Lobe { id = region[i], best = i };
                l.cells++; l.list.Add(i); if (clear[i] > l.bestClear) { l.bestClear = clear[i]; l.best = i; }
            }
            foreach (var kv in byId) if (kv.Value.cells * Cell * Cell >= 300f) { kv.Value.site = Centre(kv.Value.best); lobes.Add(kv.Value); }
        }

        bool RectOpen(Vector3 c, Vector3 u, Vector3 v, float a0, float a1, float b0, float b1, int regionId)
        {
            for (float a = a0; a <= a1 + 0.01f; a += 1.9f)
                for (float b = b0; b <= b1 + 0.01f; b += 1.9f)
                {
                    var p = c + u * Mathf.Min(a, a1) + v * Mathf.Min(b, b1); int i = Ix(p);
                    if (i < 0 || !open[i] || blocked[i] || region[i] != regionId) return false;
                }
            return RectCorner(c + u * a1 + v * b1, regionId) && RectCorner(c + u * a1 + v * b0, regionId) && RectCorner(c + u * a0 + v * b1, regionId);
        }
        bool RectCorner(Vector3 p, int regionId) { int i = Ix(p); return i >= 0 && open[i] && !blocked[i] && region[i] == regionId; }

        /// Largest rectangle from `sizes` (long, short) that fits a lobe at any 15-degree yaw; b-range is asymmetric by `extraB`.
        bool FitRect(Lobe lobe, Vector2[] sizes, float margin, float extraB, float minClear, out Vector3 centre, out float yaw, out Vector2 size)
        {
            centre = lobe.site; yaw = 0f; size = Vector2.zero;
            foreach (var s in sizes)
            {
                float best = -1f;
                foreach (int i in lobe.list)
                {
                    if (clear[i] < Mathf.Min(minClear, s.y * 0.5f)) continue;
                    var c = Centre(i);
                    for (int k = 0; k < 12; k++)
                    {
                        float y = k * 15f; var u = TownLifeGeo.Dir(y); var v = TownLifeGeo.Right(u);
                        for (int flip = -1; flip <= 1; flip += 2)
                        {
                            float b0 = -s.y * 0.5f - margin - (flip < 0 ? extraB : 0f), b1 = s.y * 0.5f + margin + (flip > 0 ? extraB : 0f);
                            if (!RectOpen(c, u, v, -s.x * 0.5f - margin, s.x * 0.5f + margin, b0, b1, lobe.id)) continue;
                            float score = clear[i] + (k % 6 == 0 ? 0.6f : 0f);
                            if (score > best) { best = score; centre = c; yaw = flip > 0 ? y : y + 180f; size = s; }
                        }
                    }
                }
                if (best >= 0f) return true;
            }
            return false;
        }
        void BlockRect(Vector3 c, Vector3 u, Vector3 v, float ha, float hb)
        {
            for (float a = -ha; a <= ha; a += 1f) for (float b = -hb; b <= hb; b += 1f) { int i = Ix(c + u * a + v * b); if (i >= 0) blocked[i] = true; }
        }

        // ------------------------------------------------------------------------------------------------ themes
        static readonly Vector2[] PitchSizes = { new Vector2(44, 28), new Vector2(40, 26), new Vector2(36, 23), new Vector2(32, 21), new Vector2(28, 18), new Vector2(24, 15) };
        Vector3 pitchCentre; float pitchYaw; Vector2 pitchSize;

        void Themes()
        {
            // the pitch goes to the lobe that takes the biggest one; the others get wildflower / orchard / dog run by size
            Lobe pick = null; int rank = int.MaxValue; float bestClear = 0f;
            foreach (var l in lobes)
            {
                if (!FitRect(l, PitchSizes, 4.8f, 3.0f, 12f, out var c, out float yaw, out var size)) continue;
                int r = Array.IndexOf(PitchSizes, size);
                if (r < rank || (r == rank && l.bestClear > bestClear)) { rank = r; bestClear = l.bestClear; pick = l; pitchCentre = c; pitchYaw = yaw; pitchSize = size; }
            }
            if (pick != null) { pick.theme = "pitch"; } else notes.Add("no lobe fits a soccer pitch");
            var rest = new List<Lobe>(); foreach (var l in lobes) if (l != pick) rest.Add(l);
            rest.Sort((a, b) => b.cells != a.cells ? b.cells.CompareTo(a.cells) : a.id.CompareTo(b.id));
            string[] order = { "wildflower", "orchard", "dogrun" };
            for (int i = 0; i < rest.Count; i++) rest[i].theme = i < order.Length ? order[i] : "grove";
        }

        // -------------------------------------------------------------------------------------------- path routing
        List<Vector3> Route(Vector3 from, Vector3 to)
        {
            int s = Snap(from), t = Snap(to); if (s < 0 || t < 0) return null;
            int n = nx * nz; var cost = new float[n]; var prev = new int[n]; for (int i = 0; i < n; i++) { cost[i] = float.MaxValue; prev[i] = -1; }
            var queue = new SortedSet<(float, int)>(); cost[s] = 0f; queue.Add((0f, s));
            int[] dx = { 1, -1, 0, 0, 1, 1, -1, -1 }, dz = { 0, 0, 1, -1, 1, -1, 1, -1 };
            while (queue.Count > 0)
            {
                var top = queue.Min; queue.Remove(top); int i = top.Item2; if (i == t) break;
                int x = i % nx, z = i / nx;
                for (int k = 0; k < 8; k++)
                {
                    int ax = x + dx[k], az = z + dz[k]; if (ax < 0 || az < 0 || ax >= nx || az >= nz) continue;
                    int j = az * nx + ax; if (!open[j] || blocked[j]) continue;
                    if (k >= 4 && (!open[z * nx + ax] || !open[az * nx + x] || blocked[z * nx + ax] || blocked[az * nx + x])) continue;
                    float step = (k < 4 ? Cell : Cell * 1.4142f) * (pathCell[j] ? 0.45f : 1f + 4f / Mathf.Max(clear[j], 1f));
                    float c = cost[i] + step; if (c >= cost[j]) continue;
                    if (cost[j] < float.MaxValue) queue.Remove((cost[j], j));
                    cost[j] = c; prev[j] = i; queue.Add((c, j));
                }
            }
            if (prev[t] < 0 && t != s) return null;
            var cells = new List<Vector3>(); for (int i = t; i >= 0; i = prev[i]) cells.Add(Centre(i)); cells.Reverse();
            // string pulling
            var pulled = new List<Vector3> { cells[0] }; int at = 0;
            while (at < cells.Count - 1)
            {
                int far = at + 1;
                for (int j = Mathf.Min(cells.Count - 1, at + 14); j > at + 1; j--) if (LineRoutable(cells[at], cells[j])) { far = j; break; }
                pulled.Add(cells[far]); at = far;
            }
            // Chaikin
            for (int pass = 0; pass < 2; pass++)
            {
                var sm = new List<Vector3> { pulled[0] };
                for (int i = 0; i < pulled.Count - 1; i++) { sm.Add(Vector3.Lerp(pulled[i], pulled[i + 1], 0.25f)); sm.Add(Vector3.Lerp(pulled[i], pulled[i + 1], 0.75f)); }
                sm.Add(pulled[pulled.Count - 1]); pulled = sm;
            }
            return pulled;
        }
        bool LineRoutable(Vector3 a, Vector3 b)
        {
            float len = TownLifeGeo.Dist(a, b); int n = Mathf.Max(1, Mathf.CeilToInt(len)); var side = TownLifeGeo.Right((b - a).normalized) * 1.1f;
            for (int i = 0; i <= n; i++) { var p = Vector3.Lerp(a, b, i / (float)n); if (!Routable(p) || !Routable(p + side) || !Routable(p - side)) return false; }
            return true;
        }
        int Snap(Vector3 p)
        {
            int i = Ix(p); if (i >= 0 && open[i] && !blocked[i]) return i;
            int best = -1; float bd = 14f * 14f;
            for (int k = 0; k < nx * nz; k++) { if (!open[k] || blocked[k]) continue; float d = (Centre(k) - TownLifeGeo.Flat(p)).sqrMagnitude; if (d < bd) { bd = d; best = k; } }
            return best;
        }
        /// Where a path can leave a road's sidewalk into the given lobe, nearest to `target`. edge = on the sidewalk's back edge.
        bool Mouth(TownLifeScene.RoadInfo road, Vector3 target, int regionId, out Vector3 edge, out Vector3 inner, float avoidRadius = 0f, Vector3 avoid = default)
        {
            edge = inner = Vector3.zero; float best = float.MaxValue;
            if (road == null) return false;
            for (float s = 4f; s < road.length - 4f; s += 2f)
            {
                var c = TownLifeGeo.PointAt(road.points, s, out var tan); var n = TownLifeGeo.Right(tan);
                for (int side = -1; side <= 1; side += 2)
                {
                    var q = c + n * (side * (road.halfWidth + 3f + 2.4f));
                    if (Region(q) != regionId || !Routable(q) || !occ.Free(q, 1.2f)) continue;
                    if (avoidRadius > 0f && TownLifeGeo.Dist(q, avoid) < avoidRadius) continue;
                    float d = TownLifeGeo.Dist(q, target); if (d < best) { best = d; inner = q; edge = c + n * (side * (road.halfWidth + 2.7f)); }
                }
            }
            return best < float.MaxValue;
        }
        int pathCount; readonly List<(Vector3 p, Vector3 dir, float width)> mouths = new List<(Vector3, Vector3, float)>();
        /// A little flower bed each side of every place a path leaves a sidewalk (one merged mesh).
        void MouthBeds()
        {
            if (mouths.Count == 0) return;
            var d = new TownLifeKit.MeshData(); var rng = ctx.Rng("meadows/mouthbeds"); int k = 0;
            foreach (var m in mouths)
            {
                var side = TownLifeGeo.Right(m.dir);
                foreach (float e in new[] { -1f, 1f })
                {
                    var c = m.p + m.dir * 0.3f + side * (e * (m.width * 0.5f + 2.3f)); k++;
                    if (plan.CommonAt(c) < 0 || !occ.Free(c, 1.3f)) continue;
                    DiscAt(d, "soil", c, 1.5f, 1.15f, TownLifeGeo.Yaw(side), SoilY, 12);
                    for (int i = 0; i < 16; i++)
                    {
                        float a = R(rng, 0f, Mathf.PI * 2f), rr = Mathf.Sqrt((float)rng.NextDouble());
                        Plant(d, At(c + side * (Mathf.Cos(a) * 1.3f * rr) + m.dir * (Mathf.Sin(a) * 0.95f * rr), SoilY), Petals[(k + (i % 3 == 0 ? 2 : 0)) % Petals.Length], rng, 1.3f);
                    }
                    occ.ReserveCircle(c, 1.4f, TownLifeTags.Bed + ".meadows", TownLifeOccupancy.Block.Marker);
                }
            }
            TownLifeKit.Custom(root, "meadows_mouth_beds", d, false, false);
        }
        void Path(string name, List<Vector3> pts, float width = 2.2f, string mat = "gravel", float y = PavingY, bool closed = false)
        {
            if (pts == null || pts.Count < 2) return;
            TownLifeKit.Ribbon(root, "meadows_path_" + name, pts, width, mat, y, false, closed); pathCount++;
            occ.ReservePolyline(pts, width * 0.5f + 0.35f, TownLifeTags.PathSurface + ".meadows", TownLifeOccupancy.Block.Solids, closed);
            for (int i = 0; i < pts.Count - (closed ? 0 : 1); i++)
            {
                Vector3 a = pts[i], b = pts[(i + 1) % pts.Count]; int n = Mathf.Max(1, Mathf.CeilToInt(TownLifeGeo.Dist(a, b)));
                for (int k = 0; k <= n; k++) { int c = Ix(Vector3.Lerp(a, b, k / (float)n)); if (c >= 0) pathCell[c] = true; }
            }
        }
        /// Gravel path from a road's sidewalk into a lobe toward `target`.
        bool Link(string name, TownLifeScene.RoadInfo road, Vector3 target, int regionId, float width = 2.2f, string mat = "gravel", float y = PavingY, float avoidRadius = 0f, Vector3 avoid = default)
        {
            if (!Mouth(road, target, regionId, out var edge, out var inner, avoidRadius, avoid)) return false;
            var r = Route(inner, target); if (r == null) return false;
            r.Insert(0, edge); Path(name, r, width, mat, y);
            var dir = (r[1] - r[0]).normalized; var side = TownLifeGeo.Right(dir);
            mouths.Add((inner, dir, width));
            foreach (float e in new[] { -1f, 1f })
            {
                var bpos = inner - dir * 1.4f + side * (e * (width * 0.5f + 0.75f));
                if (occ.TryReserve(bpos, 0.35f, TownLifeTags.Furniture + ".meadows")) TownLifeKit.Spawn(root, Stock("kit.bollard", "meadows.fb.bollard"), G(bpos), TownLifeGeo.Yaw(dir));
            }
            return true;
        }

        void Lamp(Transform parent, Vector3 p, float yaw)
        {
            if (!Open(p) || !occ.TryReserve(p, 0.7f, TownLifeTags.Furniture + ".meadows.lamp")) return;
            TownLifeKit.SpawnPrefab(parent, LampPrefab, G(p), yaw, 1f, true, true);
        }
        void Blanket(Transform parent, Vector3 p, float yaw)
        {
            if (!Open(p) || !occ.TryReserve(p, 1.6f, TownLifeTags.Furniture + ".meadows.blanket", TownLifeOccupancy.What.Solid, TownLifeOccupancy.Block.Solids)) return;
            TownLifeKit.Spawn(parent, "meadows.blanket", G(p), yaw);
        }

        // ------------------------------------------------------------------------------------------------- pitch
        void Pitch(Lobe lobe)
        {
            var g = TownLifeKit.Group(root, "TL_Pitch");
            var c = pitchCentre; float L = pitchSize.x, W = pitchSize.y; var u = TownLifeGeo.Dir(pitchYaw); var v = TownLifeGeo.Right(u);   // +v = bleacher side
            float hl = L * 0.5f, hw = W * 0.5f;
            var grass = new TownLifeKit.MeshData(); const int bands = 10; const float apron = 1.6f;
            for (int i = 0; i < bands; i++)
            {
                float a0 = -hl + i * L / bands - (i == 0 ? apron : 0f), a1 = -hl + (i + 1) * L / bands + (i == bands - 1 ? apron : 0f);
                RectUV(grass, i % 2 == 0 ? "meadows.lawn.light" : "meadows.lawn.dark", c, u, v, a0, a1, -hw - apron, hw + apron, LawnY);
            }
            TownLifeKit.Custom(g, "meadows_pitch_grass", grass);
            var ln = new TownLifeKit.MeshData(); const float t = 0.1f; const string wh = "paint.white";
            RectUV(ln, wh, c, u, v, -hl - t, hl + t, -hw - t, -hw + t, SoilY); RectUV(ln, wh, c, u, v, -hl - t, hl + t, hw - t, hw + t, SoilY);
            RectUV(ln, wh, c, u, v, -hl - t, -hl + t, -hw, hw, SoilY); RectUV(ln, wh, c, u, v, hl - t, hl + t, -hw, hw, SoilY); RectUV(ln, wh, c, u, v, -t, t, -hw, hw, SoilY);
            Annulus(ln, wh, c, 3.6f, 3.8f, SoilY, 28); Annulus(ln, wh, c, 0f, 0.28f, SoilY, 10);
            float pd = Mathf.Min(6f, L * 0.16f), pw = Mathf.Min(7.5f, hw - 2f);
            for (int e = -1; e <= 1; e += 2)
            {
                float g0 = e * hl, g1 = e * (hl - pd);
                RectUV(ln, wh, c, u, v, Mathf.Min(g1 - t, g1 + t), Mathf.Max(g1 - t, g1 + t), -pw, pw, SoilY);
                RectUV(ln, wh, c, u, v, Mathf.Min(g0, g1), Mathf.Max(g0, g1), -pw - t, -pw + t, SoilY); RectUV(ln, wh, c, u, v, Mathf.Min(g0, g1), Mathf.Max(g0, g1), pw - t, pw + t, SoilY);
                Annulus(ln, wh, c + u * (e * (hl - pd * 0.65f)), 0f, 0.2f, SoilY, 8);
            }
            TownLifeKit.Custom(g, "meadows_pitch_lines", ln);
            occ.ReserveQuad(TownLifeGeo.Quad(c, v, u, hw + apron, hl + apron), TownLifeTags.Lawn + ".meadows.pitch", TownLifeOccupancy.Block.Solids);

            TownLifeKit.Spawn(g, "meadows.goal", G(c + u * hl), pitchYaw + 180f); TownLifeKit.Spawn(g, "meadows.goal", G(c - u * hl), pitchYaw);
            for (int a = -1; a <= 1; a += 2) for (int b = -1; b <= 1; b += 2) TownLifeKit.Spawn(g, "meadows.cornerflag", G(c + u * (a * hl) + v * (b * hw)), pitchYaw + (a > 0 ? 180f : 0f) + 45f * a * b);
            TownLifeKit.Spawn(g, "meadows.ball", G(c + u * 3.1f + v * 2.2f), pitchYaw + 200f);

            // loop path round the pitch (rounded rectangle), blocked interior for later routing
            const float off = 3.5f, cr = 3.5f; var loop = new List<Vector3>();
            for (int k = 0; k < 4; k++)
            {
                float sa = k == 0 || k == 3 ? 1f : -1f, sb = k < 2 ? 1f : -1f;
                var corner = c + u * (sa * (hl + off - cr)) + v * (sb * (hw + off - cr));
                float start = k * 90f;      // k0: +a +b
                for (int i = 0; i <= 5; i++)
                {
                    float ang = (start + i * 18f) * Mathf.Deg2Rad;
                    // quadrant order: (+u,+v) -> (-u,+v) -> (-u,-v) -> (+u,-v)
                    Vector3 d0 = k == 0 ? u * Mathf.Cos(ang) + v * Mathf.Sin(ang) : k == 1 ? u * Mathf.Cos(ang) + v * Mathf.Sin(ang) : k == 2 ? u * Mathf.Cos(ang) + v * Mathf.Sin(ang) : u * Mathf.Cos(ang) + v * Mathf.Sin(ang);
                    loop.Add(corner + d0 * cr);
                }
            }
            BlockRect(c, u, v, hl + off - 1.2f, hw + off - 1.2f);
            Path("pitch_loop", loop, 2.0f, "gravel", PavingY, true);

            // bleacher on a gravel pad outside the loop (+v side), cones in front, benches opposite
            var bp = c + v * (hw + off + 1.5f);
            var pad = new TownLifeKit.MeshData(); RectUV(pad, "gravel", c, u, v, -4.4f, 4.4f, hw + off + 0.8f, hw + off + 4.6f, PavingY);
            TownLifeKit.Custom(g, "meadows_pitch_pad", pad);
            TownLifeKit.Spawn(g, "meadows.bleacher", G(bp), pitchYaw - 90f);        // prop +Z (front) -> -v (the pitch)
            occ.ReserveQuad(TownLifeGeo.Quad(bp + v * 1.1f, u, v, 4.2f, 2.1f), TownLifeTags.Structure + ".meadows", TownLifeOccupancy.Block.Solids);
            BlockRect(bp + v * 1.3f, u, v, 4.4f, 2.2f);
            TownLifeKit.Spawn(g, "meadows.cones", G(c + v * (hw + 1.0f) - u * (hl * 0.5f)), pitchYaw - 90f);
            foreach (float a in new[] { -5.5f, 5.5f }) TownLifeKit.Spawn(g, Stock("kit.bench.simple", "meadows.fb.bench"), G(c - v * (hw + 1.25f) + u * a), pitchYaw + 90f);
            // banner + scoreboard on the far touchline, outside the loop, facing the bleacher
            foreach (float e in new[] { -1f, 1f }) TownLifeKit.Spawn(g, "meadows.ballstop", G(c + u * (e * (hl + 2.15f))), pitchYaw + 90f);
            TownLifeKit.Spawn(g, "meadows.teamkit", G(c - v * (hw + 1.35f)), pitchYaw + 90f);
            foreach (float e in new[] { -1f, 1f })
            {
                var q = c + v * (hw + off + 2.1f) + u * (e * 10.4f);
                if (Open(q - u * 5f) && Open(q + u * 5f) && occ.Free(q - u * 5f, 0.3f) && occ.Free(q + u * 5f, 0.3f)) { TownLifeKit.Spawn(g, "meadows.bunting", G(q), pitchYaw); occ.ReserveCircle(q - u * 5f, 0.4f, TownLifeTags.Furniture + ".meadows"); occ.ReserveCircle(q + u * 5f, 0.4f, TownLifeTags.Furniture + ".meadows"); }
            }
            Sign(g, "meadows_sign_goteam", bp + v * 2.55f, pitchYaw - 90f, 5.8f, 1.25f, 3.75f, "paint.yellow", "GO TEAM!", new Color(0.12f, 0.3f, 0.75f), true);
            float so = hw + off + 2.0f;
            foreach (var cand in new[] { c - v * so + u * (hl - 7f), c - v * so - u * (hl - 7f), c + v * so + u * (hl - 5f), c + v * so - u * (hl - 5f), c + u * (hl + off + 2f) - v * (hw - 2f), c - u * (hl + off + 2f) - v * (hw - 2f) })
            {
                if (!Open(cand) || !occ.Free(cand, 1.3f)) continue;
                Sign(g, "meadows_sign_score", cand, TownLifeGeo.Yaw(c - cand), 3.6f, 1.4f, 2.5f, "metal.dark", "US 0 : 12 THEM", new Color(1f, 0.85f, 0.2f), true); break;
            }
            for (int a = -1; a <= 1; a += 2) for (int b = -1; b <= 1; b += 2) Lamp(g, c + u * (a * (hl + off + 0.2f)) + v * (b * (hw + off + 0.2f)) + (u * a + v * b) * 0.9f, pitchYaw);
            picnicHints.Add(c + u * (hl + off + 6f)); picnicHints.Add(c - u * (hl + off + 6f));

            // links: both lanes and the Mile, wherever they touch this lobe
            int links = 0;
            foreach (var id in new[] { "carpool", "juicebox" }) if (Link("pitch_" + id, scene.Road(id), NearestOn(loop, RoadNearest(scene.Road(id), c)), lobe.id)) links++;
            if (Link("pitch_mile", mile, NearestOn(loop, RoadNearest(mile, c)), lobe.id)) links++;
            if (Mouth(mile, NearestOn(loop, RoadNearest(mile, c)), lobe.id, out _, out var mm))
            {
                TownLifeGeo.Project(mile.points, mm, out float ms, out _); TownLifeGeo.PointAt(mile.points, ms, out var mt); mt = TownLifeGeo.Flat(mt).normalized;
                foreach (float e in new[] { 5.5f, -5.5f, 8f, -8f })
                {
                    var q = mm + mt * e; if (!Open(q) || !occ.Free(q, 1.6f)) continue;
                    Sign(g, "meadows_sign_common", q, TownLifeGeo.Yaw(RoadNearest(mile, q) - q), 4.4f, 1.5f, 2.0f, "leaf.dark", "MEADOWS COMMON\n<size=60%>home of the Fighting Minivans</size>", new Color(1f, 0.95f, 0.8f), true); break;
                }
            }
            if (links == 0) notes.Add("pitch has no path link");
            pitchLoop = loop; pitchLobe = lobe;
        }
        List<Vector3> pitchLoop; Lobe pitchLobe;
        static Vector3 NearestOn(List<Vector3> pts, Vector3 p) { Vector3 best = pts[0]; float bd = float.MaxValue; foreach (var q in pts) { float d = TownLifeGeo.Dist(p, q); if (d < bd) { bd = d; best = q; } } return best; }
        static Vector3 RoadNearest(TownLifeScene.RoadInfo road, Vector3 p) { if (road == null) return p; TownLifeGeo.Project(road.points, p, out float s, out _); return TownLifeGeo.PointAt(road.points, s, out _); }

        // --------------------------------------------------------------------------------------- infill tie-ins
        void InfillTies()
        {
            foreach (var inf in scene.infill)
            {
                var ic = TownLifeGeo.Centre(inf.quad); if (!InRing(ic)) continue;
                var corners = TownLifeGeo.Corners(inf.quad); string key = TownLifeKit.Sanitize(inf.id);
                var rng = ctx.Rng("meadows/infill/" + inf.id);
                // a path from the side of the parcel that looks at the pitch, to the pitch loop
                if (pitchLoop != null)
                {
                    Vector3 best = Vector3.zero; float bd = float.MaxValue; var target = NearestOn(pitchLoop, ic);
                    for (int i = 0; i < 4; i++)
                    {
                        var mid = (corners[i] + corners[(i + 1) % 4]) * 0.5f; var outp = mid + (mid - ic).normalized * 2.6f;
                        if (!Routable(outp) || Region(outp) != pitchLobe.id) continue;
                        float d = TownLifeGeo.Dist(outp, target); if (d < bd) { bd = d; best = outp; }
                    }
                    if (bd < float.MaxValue) { var r = Route(best, NearestOn(pitchLoop, best)); if (r != null) { r.Insert(0, best + (ic - best).normalized * 1.9f); Path("infill_" + key, r, 2.0f); } }
                }
                // flower drifts hugging the outside of the footprint, and a tree off each free corner
                var fl = new TownLifeKit.MeshData(); int planted = 0;
                for (int i = 0; i < 4; i++)
                {
                    Vector3 a = corners[i], b = corners[(i + 1) % 4]; var outward = ((a + b) * 0.5f - ic).normalized; float len = TownLifeGeo.Dist(a, b);
                    for (int k = 0; k < (int)(len * 2.2f); k++)
                    {
                        var p = Vector3.Lerp(a, b, R(rng, 0.03f, 0.97f)) + outward * R(rng, 0.9f, 2.4f);
                        if (plan.CommonAt(p) < 0 || !occ.Free(p, 0.3f)) continue;
                        Plant(fl, At(p, GrassY), Petals[(i + (k % 3 == 0 ? 1 : 0)) % Petals.Length], rng, 1.2f); planted++;
                    }
                    var cp = a + ((a - ic).normalized) * 3.4f;
                    if (Open(cp)) Tree(root, i % 2 == 0 ? "kit.tree.blossom.m" : "kit.tree.round.m", cp, rng, 1.1f, 1.2f, 6f);
                }
                if (planted > 0) TownLifeKit.Custom(root, "meadows_infill_flowers_" + key, fl, false, false);
            }
        }

        // ------------------------------------------------------------------------------------------------ dog run
        void DogRun(Lobe lobe)
        {
            var sizes = new[] { new Vector2(30, 17), new Vector2(26, 15), new Vector2(22, 13), new Vector2(18, 12), new Vector2(15, 10), new Vector2(12, 9) };
            if (!FitRect(lobe, sizes, 3.0f, 0f, 7f, out var c, out float yaw, out var size)) { lobe.theme = "grove"; notes.Add("dog run does not fit region " + lobe.id); return; }
            var g = TownLifeKit.Group(root, "TL_BarkPark");
            var u = TownLifeGeo.Dir(yaw); var v = TownLifeGeo.Right(u); float hl = size.x * 0.5f, hw = size.y * 0.5f;
            var rng = ctx.Rng("meadows/dogrun");
            // gate on the long side that faces the nearest Mile mouth
            Mouth(mile, c, lobe.id, out _, out var mouthIn);
            float gs = Vector3.Dot(mouthIn - c, v) >= 0f ? 1f : -1f; var gate = c + v * (gs * hw);
            var ground = new TownLifeKit.MeshData();
            RectUV(ground, "meadows.lawn.light", c, u, v, -hl, hl, -hw, hw, LawnY);
            DiscAt(ground, "meadows.dirt", c, hl * 0.78f, hw * 0.62f, yaw + 90f, SoilY, 18);
            DiscAt(ground, "meadows.dirt", gate - v * (gs * 0.5f), 1.9f, 1.6f, yaw, SoilY, 10);
            TownLifeKit.Custom(g, "meadows_dogrun_ground", ground);
            var line = new List<Vector3> { gate + u * 1.1f, c + u * hl + v * (gs * hw), c + u * hl - v * (gs * hw), c - u * hl - v * (gs * hw), c - u * hl + v * (gs * hw), gate - u * 1.1f };
            TownLifeKit.Run(g, "meadows_dogrun_fence", line, new TownLifeKit.RunSpec { panel = Stock("kit.panel.rail.post", "meadows.fb.rail"), panelLength = 2.5f, post = Stock("kit.post.wood", "meadows.fb.post"), collider = TownLifeKit.RunCollider.Box, colliderHeight = 1.0f, colliderThickness = 0.15f, castShadows = false });
            runs++;
            occ.ReservePolyline(line, 0.5f, TownLifeTags.Structure + ".meadows.dogrun", TownLifeOccupancy.Block.Solids);
            occ.ReserveQuad(TownLifeGeo.Quad(c, v, u, hw, hl), TownLifeTags.Lawn + ".meadows.dogrun", TownLifeOccupancy.Block.Solids);
            BlockRect(c, u, v, hl + 0.5f, hw + 0.5f);
            float k = Mathf.Min(1f, (size.x - 4f) / 14f);
            TownLifeKit.Spawn(g, "meadows.agility", G(c - v * (gs * hw * 0.15f)), yaw - 90f, k);     // prop +X -> u
            TownLifeKit.Spawn(g, "meadows.dog", G(c + u * (hl * 0.25f) + v * (gs * hw * 0.45f)), yaw + gs * 120f, 1.5f);
            TownLifeKit.Spawn(g, "meadows.dog", G(c - u * (hl * 0.62f) - v * (gs * hw * 0.5f)), yaw + 60f, 1.2f);
            TownLifeKit.Spawn(g, Stock("kit.bench.park", "meadows.fb.bench"), G(c + u * (hl - 1.0f) + v * (gs * hw * 0.2f)), yaw - 90f);
            Sign(g, "meadows_sign_barkpark", gate + u * 2.6f + v * (gs * 1.0f), TownLifeGeo.Yaw(v * gs), 2.6f, 1.0f, 2.0f, "paint.blue", "BARK PARK", Color.white, false);
            var outside = gate + v * (gs * 2.4f);
            if (!Link("dogrun_mile", mile, outside, lobe.id)) notes.Add("dog run has no path");
            foreach (var id in new[] { "carpool", "juicebox" }) Link("dogrun_" + id, scene.Road(id), outside, lobe.id);
            Tree(g, "kit.tree.round.l", c - u * (hl + 2.6f) - v * (gs * hw * 0.3f), rng, 1f, 1.2f); Tree(g, "kit.tree.birch.m", c + u * (hl + 2.4f) - v * (gs * (hw + 1.5f)), rng, 1f, 1.2f);
            picnicHints.Add(outside + u * 7f); picnicHints.Add(outside - u * 9f + v * (gs * 5f));
            Lamp(g, gate + v * (gs * 1.6f) - u * 2.4f, yaw);
        }

        // ------------------------------------------------------------------------------------------------ orchard
        void Orchard(Lobe lobe)
        {
            var g = TownLifeKit.Group(root, "TL_Orchard");
            var c0 = lobe.site; var rng = ctx.Rng("meadows/orchard");
            TownLifeGeo.Project(mile.points, c0, out float st, out _); TownLifeGeo.PointAt(mile.points, st, out var tan);
            var u = TownLifeGeo.Flat(tan).normalized; var v = TownLifeGeo.Right(u); const float du = 5.6f, dv = 6.6f;
            var cand = new List<(float d, int i, int j)>();
            for (int i = -5; i <= 5; i++) for (int j = -4; j <= 4; j++)
            {
                var p = c0 + u * ((i + 0.5f) * du) + v * ((j + 0.5f) * dv);
                if (Region(p) != lobe.id || Clear(p) < 3.9f || !Routable(p)) continue;
                cand.Add((TownLifeGeo.Dist(p, c0), i, j));
            }
            cand.Sort((a, b) => a.d != b.d ? a.d.CompareTo(b.d) : a.i != b.i ? a.i.CompareTo(b.i) : a.j.CompareTo(b.j));
            var rows = new SortedDictionary<int, Vector2>(); int planted = 0;
            var strips = new TownLifeKit.MeshData();
            foreach (var cnd in cand)
            {
                if (planted >= 26) break;
                var p = c0 + u * ((cnd.i + 0.5f) * du) + v * ((cnd.j + 0.5f) * dv);
                if (!Tree(g, "kit.tree.fruit.s", p, rng, 1.3f, 1.2f)) continue;
                planted++; DiscAt(strips, "soil", p, 1.0f, 1.0f, 0f, SoilY, 10);
                float a = (cnd.i + 0.5f) * du;
                rows[cnd.j] = rows.TryGetValue(cnd.j, out var e) ? new Vector2(Mathf.Min(e.x, a), Mathf.Max(e.y, a)) : new Vector2(a, a);
            }
            if (planted < 4) { notes.Add("orchard too small in region " + lobe.id); }
            foreach (var kv in rows) RectUV(strips, "meadows.lawn.light", c0, u, v, kv.Value.x - 2.4f, kv.Value.y + 2.4f, (kv.Key + 0.5f) * dv - 1.5f, (kv.Key + 0.5f) * dv + 1.5f, LawnY);
            TownLifeKit.Custom(g, "meadows_orchard_ground", strips);
            occ.ReserveCircle(c0, 3f, TownLifeTags.Lawn + ".meadows.orchard", TownLifeOccupancy.Block.Marker);
            // dressing in the aisle through the centre
            var cr = c0 + u * 1.2f; if (occ.Free(cr, 1.3f)) { TownLifeKit.Spawn(g, "meadows.crates", G(cr), TownLifeGeo.Yaw(v) + 15f); occ.ReserveCircle(cr, 1.3f, TownLifeTags.Furniture + ".meadows"); }
            var sc = c0 - u * du + v * 0.4f; if (occ.Free(sc, 0.8f)) { TownLifeKit.Spawn(g, "meadows.scarecrow", G(sc), TownLifeGeo.Yaw(-v) + 12f, 1.25f); occ.ReserveCircle(sc, 0.8f, TownLifeTags.Furniture + ".meadows"); }
            bool linked = Link("orchard_mile", mile, c0, lobe.id);
            foreach (var id in new[] { "carpool", "juicebox" }) linked |= Link("orchard_" + id, scene.Road(id), c0, lobe.id);
            if (!linked) notes.Add("orchard has no path");
            if (Mouth(mile, c0, lobe.id, out _, out var m))
            {
                var sp = m + (c0 - m).normalized * 2.5f + TownLifeGeo.Right((c0 - m).normalized) * 2.4f;
                if (occ.Free(sp, 0.8f)) Sign(g, "meadows_sign_orchard", sp, TownLifeGeo.Yaw(m - c0), 2.8f, 1.1f, 1.9f, "paint.red", "PICK YOUR OWN\n(one each!)", Color.white, false);
            }
            picnicHints.Add(c0 + v * (dv * 0.5f) + u * du * 2f);
            Lamp(g, c0 + v * 2.2f - u * 1.5f, 0f);
        }

        // --------------------------------------------------------------------------------------------- wildflower
        void Wildflower(Lobe lobe)
        {
            var g = TownLifeKit.Group(root, "TL_WildflowerMeadow");
            var rng = ctx.Rng("meadows/wildflower");
            // mown path: from the Mile through the middle of the lobe and back out at its far end
            int e1 = lobe.best; float far = -1f; foreach (int i in lobe.list) { float d = TownLifeGeo.Dist(Centre(i), lobe.site); if (clear[i] >= 4f && d > far) { far = d; e1 = i; } }
            int e2 = lobe.best; far = -1f; foreach (int i in lobe.list) { float d = TownLifeGeo.Dist(Centre(i), Centre(e1)); if (clear[i] >= 4f && d > far) { far = d; e2 = i; } }
            var mown = new List<Vector3>();
            if (Mouth(mile, Centre(e1), lobe.id, out var edge1, out var in1))
            {
                var a = Route(in1, Centre(e1)); var b = Route(Centre(e1), lobe.site); var cc = Route(lobe.site, Centre(e2));
                if (a != null) { mown.Add(edge1); mown.AddRange(a); }
                if (b != null) mown.AddRange(b.GetRange(1, b.Count - 1));
                if (cc != null) mown.AddRange(cc.GetRange(1, cc.Count - 1));
                if (Mouth(mile, Centre(e2), lobe.id, out var edge2, out var in2, 30f, in1)) { var d = Route(Centre(e2), in2); if (d != null) { mown.AddRange(d.GetRange(1, d.Count - 1)); mown.Add(edge2); } }
            }
            if (mown.Count >= 2) Path("wildflower_mown", mown, 2.6f, "meadows.lawn.light", SoilY);
            else notes.Add("wildflower meadow has no mown path");

            // drifts: tall-grass patches full of flowers
            var order = new List<int>(lobe.list); for (int i = order.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); int t = order[i]; order[i] = order[j]; order[j] = t; }
            var tint = new TownLifeKit.MeshData(); var fl = new TownLifeKit.MeshData(); var centres = new List<Vector3>(); int flowers = 0;
            foreach (int i in order)
            {
                if (clear[i] < 4f || blocked[i]) continue; var p = Centre(i) + new Vector3(R(rng, -0.8f, 0.8f), 0, R(rng, -0.8f, 0.8f));
                bool near = false; foreach (var q in centres) if (TownLifeGeo.Dist(p, q) < 8.5f) { near = true; break; }
                if (near) continue;
                centres.Add(p);
                float rx = Mathf.Min(clear[i] - 0.5f, R(rng, 4.5f, 7.5f)), rz = rx * R(rng, 0.6f, 0.85f), yaw = R(rng, 0f, 180f);
                DiscAt(tint, "meadows.tallgrass", p, rx, rz, yaw, LawnY, 14);
                var f = TownLifeGeo.Dir(yaw); var rt = TownLifeGeo.Right(f);
                int c1 = rng.Next(Petals.Length), c2 = rng.Next(Petals.Length); int count = Mathf.Min(70, (int)(rx * rz * 2.2f));
                for (int k = 0; k < count; k++)
                {
                    float ang = R(rng, 0f, Mathf.PI * 2f), rr = Mathf.Sqrt((float)rng.NextDouble());
                    var q = p + rt * (Mathf.Cos(ang) * rx * rr) + f * (Mathf.Sin(ang) * rz * rr);
                    if (!Open(q) || !occ.Free(q, 0.25f)) continue;
                    Plant(fl, At(q, GrassY), Petals[k % 4 == 0 ? c2 : c1], rng, R(rng, 1.2f, 1.9f)); flowers++;
                }
                if (centres.Count >= 34) break;
            }
            TownLifeKit.Custom(g, "meadows_wildflower_tint", tint);
            TownLifeKit.Custom(g, "meadows_wildflower_flowers", fl, false, false);

            // bee corner at the heart of the meadow
            var site = lobe.site; var dir = mown.Count >= 2 ? (NearestOn(mown, site) - site) : Vector3.forward; if (dir.sqrMagnitude < 0.01f) dir = Vector3.forward; dir.Normalize();
            if (occ.TryFindFree(site - dir * 4.5f, 2.2f, TownLifeOccupancy.What.Solid, 6f, out var hp) && Open(hp))
            {
                TownLifeKit.Spawn(g, "meadows.hives", G(hp), TownLifeGeo.Yaw(dir)); occ.ReserveCircle(hp, 2.2f, TownLifeTags.Furniture + ".meadows");
                var sp = hp + TownLifeGeo.Right(dir) * 3.2f + dir * 1.2f;
                if (occ.Free(sp, 0.8f)) Sign(g, "meadows_sign_bees", sp, TownLifeGeo.Yaw(dir), 2.4f, 1.0f, 1.7f, "paint.yellow", "BEES AT WORK\n(be nice)", new Color(0.15f, 0.12f, 0.08f), false);
            }
            // the Sunflower Family: three giant googly sunflowers looking at the Mile
            if (occ.TryFindFree(site + dir * 0.5f + TownLifeGeo.Right(dir) * 7f, 2.6f, TownLifeOccupancy.What.Solid, 8f, out var sf) && Open(sf))
            {
                var toRoad = (RoadNearest(mile, sf) - sf).normalized; float fy = TownLifeGeo.Yaw(toRoad); var sr = TownLifeGeo.Right(toRoad);
                occ.ReserveCircle(sf, 2.6f, TownLifeTags.Feature + ".meadows.sunflowers");
                TownLifeKit.Spawn(g, "meadows.sunflower", G(sf), fy + 8f, 1.75f); TownLifeKit.Spawn(g, "meadows.sunflower", G(sf + sr * 2.1f - toRoad * 0.5f), fy - 14f, 1.3f); TownLifeKit.Spawn(g, "meadows.sunflower", G(sf - sr * 1.9f + toRoad * 0.6f), fy + 22f, 0.95f);
            }
            // benches along the mown path
            if (mown.Count > 8)
                foreach (float t in new[] { 0.3f, 0.7f })
                {
                    int k = Mathf.Clamp((int)(mown.Count * t), 1, mown.Count - 2); var tn = (mown[k + 1] - mown[k - 1]).normalized; var side = TownLifeGeo.Right(tn);
                    var bp = mown[k] + side * 2.4f; if (!Open(bp)) { bp = mown[k] - side * 2.4f; side = -side; }
                    if (Open(bp)) Prop(g, "kit.bench.park", "meadows.fb.bench", bp, TownLifeGeo.Yaw(-side), 1.0f, TownLifeTags.Furniture);
                }
            picnicHints.Add(site + dir * 5f);
            if (mown.Count > 12) { Blanket(g, mown[mown.Count / 2] - TownLifeGeo.Right((mown[mown.Count / 2 + 1] - mown[mown.Count / 2]).normalized) * 3.6f, 40f); Blanket(g, mown[mown.Count / 5] + TownLifeGeo.Right((mown[mown.Count / 5 + 1] - mown[mown.Count / 5]).normalized) * 3.8f, 110f); }
            // airy birches so the meadow has verticals without closing the view from the Mile
            int placed = 0;
            foreach (int i in order)
            {
                if (placed >= 9) break; if (clear[i] < 5f || plan.RoadEdgeDistance(Centre(i)) < 10f) continue;
                if (Tree(g, placed % 3 == 0 ? "kit.clump.birch.3" : placed % 3 == 1 ? "kit.tree.birch.l" : "kit.tree.blossom.m", Centre(i), rng, 1.1f, 1.4f, 13f)) placed++;
            }
        }

        // ------------------------------------------------------------------------------------- hedgerows / buffers
        /// Shrub border + trees on the common side of every lot line that faces common land inside the ring.
        void Hedgerows()
        {
            var g = TownLifeKit.Group(root, "TL_Hedgerows"); int n = 0;
            string[] kinds = { "kit.tree.blossom.m", "kit.tree.round.m", "kit.tree.birch.m", "kit.tree.maple.m", "kit.tree.round.l", "kit.tree.elm.m" };
            foreach (var e in plan.edges)
            {
                if (e.kind != TownLifePlan.EdgeKind.ParcelCommon || e.points == null || e.points.Length < 2) continue;
                var mid = TownLifeGeo.PointAt(e.points, TownLifeGeo.PolylineLength(e.points) * 0.5f, out var tan);
                if (!InRing(mid)) continue;
                float sign = 0f; var nrm = TownLifeGeo.Right(tan);
                foreach (float s in new[] { 1f, -1f }) if (plan.CommonAt(mid + nrm * (s * 2.5f)) >= 0) { sign = s; break; }
                if (sign == 0f) continue;
                var rng = ctx.Rng("meadows/hedgerow/" + n);
                float len = TownLifeGeo.PolylineLength(e.points);
                // shrub border in free stretches, 1.5 m off the lot line
                var stretch = new List<Vector3>(); int piece = 0;
                void Flush()
                {
                    if (stretch.Count >= 2 && TownLifeGeo.PolylineLength(stretch) >= 3.5f)
                    {
                        TownLifeKit.Run(g, "meadows_hedgerow_" + n + "_" + piece++, stretch, new TownLifeKit.RunSpec { panel = "meadows.border", panelLength = 4f, y = GrassY, castShadows = false });
                        occ.ReservePolyline(stretch, 0.8f, TownLifeTags.Shrub + ".meadows", TownLifeOccupancy.Block.Marker); runs++;
                    }
                    stretch = new List<Vector3>();
                }
                for (float s = 1f; s <= len - 1f; s += 2f)
                {
                    var c = TownLifeGeo.PointAt(e.points, s, out var t2); var p = c + TownLifeGeo.Right(t2) * (sign * 1.6f);
                    if (plan.CommonAt(p) >= 0 && occ.Free(p, 0.75f)) { if (stretch.Count == 0 || TownLifeGeo.Dist(stretch[stretch.Count - 1], p) > 0.8f) stretch.Add(p); } else Flush();
                }
                Flush();
                // trees every ~11 m, 3 m off the line
                for (float s = R(rng, 3f, 7f); s < len - 2f; s += R(rng, 9.5f, 12.5f))
                {
                    var c = TownLifeGeo.PointAt(e.points, s, out var t2); var p = c + TownLifeGeo.Right(t2) * (sign * R(rng, 2.8f, 3.6f));
                    if (plan.CommonAt(p) < 0) continue;
                    Tree(g, kinds[rng.Next(kinds.Length)], p, rng, 1.05f, 1.2f, 6f);
                }
                n++;
            }
        }

        /// Clipped hedge between the Mile and the lot rears wherever only a strip of common land separates them.
        void MileBuffers()
        {
            var g = TownLifeKit.Group(root, "TL_MileBuffers"); int piece = 0;
            var stretch = new List<Vector3>();
            void Flush()
            {
                if (stretch.Count >= 2 && TownLifeGeo.PolylineLength(stretch) >= 5f)
                {
                    TownLifeKit.Run(g, "meadows_buffer_" + piece++, stretch, new TownLifeKit.RunSpec { panel = HedgePanel("mid"), panelLength = 2f, y = GrassY });
                    occ.ReservePolyline(stretch, 0.55f, TownLifeTags.Hedge + ".meadows", TownLifeOccupancy.Block.Solids); runs++;
                }
                stretch = new List<Vector3>();
            }
            float gap = 0f;
            for (float s = 0f; s < mile.length; s += 2f)
            {
                var c = TownLifeGeo.PointAt(mile.points, s, out var tan); var n = TownLifeGeo.Right(tan);
                var probe = c + n * (mile.halfWidth + 4f); if (!InRing(probe)) { n = -n; probe = c + n * (mile.halfWidth + 4f); }
                float depth = 0f; for (float d = 0.5f; d <= 24f; d += 1f) { if (plan.CommonAt(c + n * (mile.halfWidth + 3f + d)) < 0) break; depth = d; }
                bool want = depth >= 5.5f && depth <= 19f;
                var p = c + n * (mile.halfWidth + 3f + Mathf.Clamp(depth * 0.5f, 3.4f, 5.5f));
                // a breather every ~26 m so it reads as clipped hedge lengths, not a wall
                gap += 2f; bool breather = gap > 26f; if (gap > 29f) gap = 0f;
                if (want && !breather && occ.Free(p, 0.7f)) stretch.Add(p); else Flush();
            }
            Flush();
        }

        // ---------------------------------------------------------------------------------------------- picnics
        void Picnics()
        {
            var g = TownLifeKit.Group(root, "TL_Picnic"); int made = 0;
            // one more hint per lobe: its roomiest spot that is still free
            foreach (var lobe in lobes)
            {
                int bestCell = -1; float bc = 0f;
                foreach (int i in lobe.list) { if (blocked[i] || clear[i] <= bc || clear[i] < 4f) continue; var q = Centre(i); if (!occ.Free(q, 4f) || occ.Within(q, 6f, TownLifeTags.Lawn) || occ.Within(q, 2.5f, TownLifeTags.PathSurface)) continue; bc = clear[i]; bestCell = i; }
                if (bestCell >= 0) picnicHints.Add(Centre(bestCell));
            }
            for (int h = 0; h < picnicHints.Count && made < 7; h++)
            {
                var rng = ctx.Rng("meadows/picnic/" + h);
                if (!occ.TryFindFree(picnicHints[h], 3.6f, TownLifeOccupancy.What.Solid, 9f, out var p, 1.5f) || !Open(p) || Clear(p) < 3.5f) continue;
                if (occ.Within(p, 14f, TownLifeTags.Furniture + ".meadows.picnic")) continue;
                float yaw = R(rng, 0f, 180f); var f = TownLifeGeo.Dir(yaw); var r = TownLifeGeo.Right(f);
                occ.ReserveCircle(p, 3.6f, TownLifeTags.Furniture + ".meadows.picnic");
                TownLifeKit.Spawn(g, Stock("kit.picnic.table", "meadows.fb.table"), G(p - r * 1.5f), yaw + R(rng, -12f, 12f));
                TownLifeKit.Spawn(g, Stock("kit.picnic.table", "meadows.fb.table"), G(p + r * 1.6f + f * 0.4f), yaw + 90f + R(rng, -12f, 12f));
                TownLifeKit.Spawn(g, Stock("kit.bin.park", "meadows.fb.bin"), G(p + f * 2.9f), yaw);
                var tp = p - f * 3.4f; string id = made % 2 == 0 ? "kit.tree.oak.l" : "kit.tree.round.l";
                string prop = TreeId(id, out float sc);
                if (Open(tp)) { TownLifeKit.Spawn(g, prop, G(tp), R(rng, 0, 360f), sc * 1.1f); occ.ReserveCircle(tp, 1.2f, TownLifeTags.Tree + ".meadows"); trees++; }
                Blanket(g, p + f * 5.2f + r * 2.5f, yaw + 30f);
                made++;
            }
        }

        // ------------------------------------------------------------------------------------------ lane verges
        /// A ribbon of verge flowers behind the sidewalks of the two lanes wherever they pass common land.
        void LaneVerges()
        {
            foreach (var id in new[] { "carpool", "juicebox" })
            {
                var road = scene.Road(id); if (road == null) continue;
                var rng = ctx.Rng("meadows/verge/" + id); var fl = new TownLifeKit.MeshData(); int count = 0;
                for (float s = 13f; s < road.length; s += 0.3f)
                {
                    var c = TownLifeGeo.PointAt(road.points, s, out var tan); var n = TownLifeGeo.Right(tan);
                    for (int side = -1; side <= 1; side += 2)
                    {
                        var p = c + n * (side * (road.halfWidth + 3f + R(rng, 0.45f, 1.9f)));
                        if (Region(p) < 0 || !occ.Free(p, 0.3f)) continue;
                        int block = (int)(s / 9f) + (side > 0 ? 3 : 0);
                        Plant(fl, At(p, GrassY), Petals[block % Petals.Length], rng, 1.35f); count++;
                    }
                }
                if (count > 0) TownLifeKit.Custom(root, "meadows_verge_" + TownLifeKit.Sanitize(id), fl, false, false);
            }
        }

        // ------------------------------------------------------------------------------------------------ groves
        void Groves()
        {
            var g = TownLifeKit.Group(root, "TL_Groves");
            foreach (var lobe in lobes)
            {
                var rng = ctx.Rng("meadows/grove/" + lobe.id);
                int want = lobe.theme == "pitch" ? 18 : lobe.theme == "wildflower" ? 0 : lobe.theme == "orchard" ? 11 : lobe.theme == "dogrun" ? 14 : 12;
                string[] kinds = lobe.theme == "pitch" ? new[] { "kit.tree.round.l", "kit.tree.oak.l", "kit.clump.mixed.3", "kit.tree.blossom.m", "kit.tree.elm.l" }
                    : lobe.theme == "orchard" ? new[] { "kit.tree.maple.l", "kit.tree.round.l", "kit.clump.mixed.3" } : new[] { "kit.tree.elm.l", "kit.clump.birch.3", "kit.tree.round.l", "kit.tree.maple.m" };
                var order = new List<int>(lobe.list); for (int i = order.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); int t = order[i]; order[i] = order[j]; order[j] = t; }
                int placed = 0;
                foreach (int i in order)
                {
                    if (placed >= want) break; if (clear[i] < 3f || blocked[i]) continue;
                    var p = Centre(i) + new Vector3(R(rng, -0.8f, 0.8f), 0, R(rng, -0.8f, 0.8f));
                    if (occ.Within(p, 3.2f, TownLifeTags.PathSurface) || occ.Within(p, 5f, TownLifeTags.Lawn) || occ.Within(p, 4f, TownLifeTags.Furniture)) continue;
                    if (plan.RoadEdgeDistance(p) < 11f) continue;            // leave the verge to the Streets module's tree row
                    if (!Tree(g, kinds[rng.Next(kinds.Length)], p, rng, 1.1f, 1.4f, 7.5f)) continue;
                    placed++;
                    if (rng.Next(100) < 60)
                    {
                        string[] under = { "kit.shrub.round.m", "kit.shrub.flower.pink", "kit.shrub.flower.yellow", "kit.boulder.m", "kit.shrub.flower.white", "kit.shrub.round.s", "kit.shrub.flower.purple" };
                        var q = p + TownLifeGeo.Dir(R(rng, 0f, 360f)) * R(1.9f, 2.9f, rng);
                        if (Open(q) && occ.TryReserve(q, 0.6f, TownLifeTags.Shrub + ".meadows")) TownLifeKit.Spawn(g, Stock(under[rng.Next(under.Length)], "kit.shrub.round"), G(q), R(rng, 0f, 360f), R(rng, 0.9f, 1.25f));
                    }
                }
            }
        }

        // ------------------------------------------------------------------------------------------ beach access
        void BeachAccess()
        {
            var a = plan.ParcelOf("mm-juicebox-210"); var b = plan.ParcelOf("mm-juicebox-224");
            if (a == null || b == null) { notes.Add("beach access: Seaview parcels not found"); return; }
            float s0 = Mathf.Min(a.house.station, b.house.station), s1 = Mathf.Max(a.house.station, b.house.station);
            // widest stretch of common frontage between the two lots
            float bestA = -1f, bestB = -1f, runA = -1f;
            for (float s = s0; s <= s1 + 0.01f; s += 1f)
            {
                var c = TownLifeGeo.PointAt(mile.points, s, out var tan); var n = TownLifeGeo.Right(tan); if (InRing(c + n * (mile.halfWidth + 6f))) n = -n;
                bool common = plan.LabelAt(c + n * (mile.halfWidth + 6f)) == TownLifePlan.Common;
                if (common && runA < 0f) runA = s;
                if ((!common || s + 1f > s1) && runA >= 0f) { if (s - runA > bestB - bestA) { bestA = runA; bestB = s; } runA = -1f; }
            }
            if (bestB - bestA < 6f) { notes.Add("beach access: no common gap between the Seaview lots"); return; }
            var g = TownLifeKit.Group(root, "TL_BeachAccess"); var rng = ctx.Rng("meadows/beach");
            var c0 = TownLifeGeo.PointAt(mile.points, (bestA + bestB) * 0.5f, out var t0); var n0 = TownLifeGeo.Right(t0); if (InRing(c0 + n0 * (mile.halfWidth + 6f))) n0 = -n0;
            var tn = TownLifeGeo.Flat(t0).normalized;
            // march outward, re-centring in the gap
            var centre = new List<Vector3>(); var half = new List<float>(); float lat = 0f;
            for (float d = 0f; d <= 44f; d += 2f)
            {
                var basep = c0 + n0 * (mile.halfWidth + 3f + d);
                float lo = lat, hi = lat; bool ok = plan.LabelAt(basep + tn * lat) == TownLifePlan.Common;
                if (!ok && d > 2f) break;
                while (lo > lat - 22f && plan.LabelAt(basep + tn * (lo - 0.5f)) == TownLifePlan.Common) lo -= 0.5f;
                while (hi < lat + 22f && plan.LabelAt(basep + tn * (hi + 0.5f)) == TownLifePlan.Common) hi += 0.5f;
                if (d > 6f && hi - lo > 40f) break;                                    // past the rear of the lots: the coast strip (Edges)
                if (d > 6f && plan.WaterDistance(basep) < 4f) break;
                lat = Mathf.Lerp(lat, (lo + hi) * 0.5f, 0.5f); centre.Add(basep + tn * lat); half.Add((hi - lo) * 0.5f);
            }
            if (centre.Count < 4) { notes.Add("beach access: gap too short"); return; }
            // sandy path with a gentle wobble
            var sand = new List<Vector3> { c0 + n0 * (mile.halfWidth + 2.7f) + tn * 0f };
            for (int i = 0; i < centre.Count; i++) sand.Add(centre[i] + tn * (Mathf.Sin(i * 0.6f) * Mathf.Min(1.2f, half[i] * 0.15f)));
            var halfS = new List<float> { half[0] }; halfS.AddRange(half);
            for (int pass = 0; pass < 2; pass++)
            {
                var sm = new List<Vector3> { sand[0] }; var hs = new List<float> { halfS[0] };
                for (int i = 0; i < sand.Count - 1; i++) { sm.Add(Vector3.Lerp(sand[i], sand[i + 1], 0.25f)); sm.Add(Vector3.Lerp(sand[i], sand[i + 1], 0.75f)); hs.Add(Mathf.Min(halfS[i], halfS[i + 1])); hs.Add(Mathf.Min(halfS[i], halfS[i + 1])); }
                sm.Add(sand[sand.Count - 1]); hs.Add(halfS[halfS.Count - 1]); sand = sm; halfS = hs;
            }
            Path("beach_access", sand, 3.4f, "meadows.sand", SoilY);
            var fan = new TownLifeKit.MeshData(); DiscAt(fan, "meadows.sand", sand[sand.Count - 1], 4.2f, 3.2f, TownLifeGeo.Yaw(tn), SoilY, 14); DiscAt(fan, "meadows.sand", sand[1], 2.6f, 2.2f, 0f, SoilY, 12);
            for (int i = 6; i < sand.Count - 4; i += 9) DiscAt(fan, "meadows.sand", sand[i] + tn * R(rng, -1.3f, 1.3f), R(rng, 1.6f, 2.2f), R(rng, 1.2f, 1.6f), R(rng, 0, 180f), SoilY, 10);
            TownLifeKit.Custom(g, "meadows_beach_sand", fan);
            // wonky beach fence both sides
            for (int side = -1; side <= 1; side += 2)
            {
                var line = new List<Vector3>();
                for (int i = 6; i < sand.Count; i += 2)
                {
                    var p = sand[i] + tn * (side * Mathf.Min(3.3f, halfS[i] - 2.2f));
                    if (occ.Free(p, 0.5f)) line.Add(p); else if (line.Count > 0) break;
                }
                if (line.Count >= 2)
                {
                    TownLifeKit.Run(g, "meadows_beach_fence_" + (side < 0 ? "a" : "b"), line, new TownLifeKit.RunSpec { panel = Stock("kit.panel.beachfence", "meadows.fb.beachfence"), panelLength = 2f, y = GrassY, castShadows = false });
                    occ.ReservePolyline(line, 0.4f, TownLifeTags.Structure + ".meadows", TownLifeOccupancy.Block.Solids); runs++;
                }
            }
            // sign, pines, surfboards, tufts
            foreach (var cand in new[] { 3.6f, -3.6f, 5f, -5f, 7f, -7f })
            {
                var sp = c0 + n0 * (mile.halfWidth + 3f + 2.2f) + tn * cand;
                if (plan.LabelAt(sp) != TownLifePlan.Common || !occ.Free(sp, 0.8f)) continue;
                Sign(g, "meadows_sign_beach", sp, TownLifeGeo.Yaw(-n0), 3.0f, 1.2f, 2.0f, "paint.blue", "TO THE BEACH!\n(sand in shoes: free)", Color.white, false); break;
            }
            int pines = 0;
            for (int i = 2; i < centre.Count && pines < 4; i += 3)
            {
                int side = (i / 3) % 2 == 0 ? 1 : -1; float off = Mathf.Min(half[i] - 2.4f, R(rng, 5.6f, 7.2f)); if (off < 4.6f) continue;
                if (Tree(g, pines % 2 == 0 ? "kit.tree.pine.l" : "kit.tree.pine.m", centre[i] + tn * (side * off), rng, 1.1f, 1.2f, 5f)) pines++;
            }
            var bp = sand[sand.Count - 2] - tn * 4.4f; if (occ.Free(bp, 1f)) { TownLifeKit.Spawn(g, "meadows.surfboard", G(bp), TownLifeGeo.Yaw(-n0) + 20f); occ.ReserveCircle(bp, 1f, TownLifeTags.Furniture + ".meadows"); }
            var tufts = new TownLifeKit.MeshData();
            for (int i = 0; i < 200; i++)
            {
                int k = rng.Next(1, centre.Count); float side = rng.Next(2) == 0 ? -1f : 1f; var p = centre[k] + tn * (side * R(rng, 2.2f, Mathf.Max(2.4f, half[k] - 1f))) + n0 * R(rng, -1f, 1f);
                if (plan.LabelAt(p) != TownLifePlan.Common || !occ.Free(p, 0.3f)) continue;
                float h = R(rng, 0.6f, 1.15f), r = R(rng, 0.25f, 0.42f); var top = At(p, GrassY + h) + new Vector3(R(rng, -0.15f, 0.15f), 0, R(rng, -0.15f, 0.15f));
                for (int q = 0; q < 3; q++) { float a0 = q * 2.094f, a1 = (q + 1) * 2.094f; tufts.TriOut(i % 3 == 0 ? "meadows.straw" : "meadows.tallgrass", At(p, GrassY + h * 0.3f), At(p + new Vector3(Mathf.Cos(a0) * r, 0, Mathf.Sin(a0) * r), GrassY), At(p + new Vector3(Mathf.Cos(a1) * r, 0, Mathf.Sin(a1) * r), GrassY), top); }
            }
            TownLifeKit.Custom(g, "meadows_beach_tufts", tufts);
        }
    }
}
