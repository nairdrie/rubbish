using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Town Life for the two rich districts, Snootington Hills and Billionaire Bluffs (Docs/CityAuthoring.md). The base
/// builder gives them 14 and 8 estates on enormous bare lawns; this fills the frontage between them:
/// <list type="bullet">
/// <item><b>Scenery estates</b> along every road wherever a house and its grounds fit: a pack house (a two-wing pair in
/// the Bluffs), a front walk, a drive with a car, a hedge or stone wall along the pavement with gate piers, trees,
/// topiary and beds. They are scenery: no pickup stop, no household, nothing on the route or in the economy.</item>
/// <item><b>Set pieces</b> on the open lawns: tennis courts, pools, fountains, gazebos, topiary gardens, putting greens,
/// and in the Bluffs private helipads and a gold statue or two.</item>
/// <item><b>Avenue trees</b> along the roads and groves on what is left.</item>
/// </list>
/// Like the Commons module it keeps its own occupancy map (the shared one stops at the river), seeded from the roads,
/// the real estates' whole grounds, the decorative sites and the nature trees. Deterministic from stable keys.
/// </summary>
public static class TownLifeEstates
{
    public const string Name = "estates";
    public const string Hills = "snootington_hills", Bluffs = "billionaire_bluffs";
    const string Buildings = "Assets/Suburbs Town Pack/PREFABS/Buildings/", Cars = "Assets/Suburbs Town Pack/PREFABS/Cars/";

    public static void Build(TownLifeContext ctx)
    {
        bool hills = ctx.options.District(Hills), bluffs = ctx.options.District(Bluffs);
        if (!hills && !bluffs) return;
        var job = new Job(ctx);
        job.Prepare();
        if (hills) job.District(Hills);
        if (bluffs) job.District(Bluffs);
        job.Report();
    }

    /// <summary>A pack house: half-width, half-depth and where its front door is (front = +Z), at scale 1.</summary>
    struct House { public string prefab; public float halfW, halfD, doorX, doorZ; }
    static readonly House[] Houses =
    {
        new House { prefab = "SM_house_suburb_004", halfW = 8.45f, halfD = 6.85f, doorX = -2.95f, doorZ = 3.15f },
        new House { prefab = "SM_house_suburb_005", halfW = 5.46f, halfD = 7.79f, doorX = 0f, doorZ = 4f },
        new House { prefab = "SM_house_suburb_006", halfW = 8.95f, halfD = 9.42f, doorX = 0f, doorZ = 5.9f },
        new House { prefab = "SM_house_suburb_007", halfW = 8.95f, halfD = 9.42f, doorX = -5.1f, doorZ = 5.9f },
    };

    sealed class Job
    {
        readonly TownLifeContext ctx;
        readonly TownLifeOccupancy occ;
        Transform root;
        readonly Dictionary<string, int> counts = new Dictionary<string, int>();
        int serial;

        public Job(TownLifeContext ctx)
        {
            this.ctx = ctx;
            occ = new TownLifeOccupancy(new Rect(20f, 545f, 815f, 375f));
        }

        Vector3 G(Vector3 p) { p.y = ctx.scene.GroundY(p); return p; }
        static Vector3 At(Vector3 p, float y) { p.y = y; return p; }
        static float R(System.Random rng, float a, float b) => TownLifeContext.Range(rng, a, b);
        void Count(string what, int n = 1) { counts.TryGetValue(what, out int v); counts[what] = v + n; }
        bool Dry(Vector3 p) => ctx.scene.GroundY(p) > -0.14f;
        // The town is still flat while this runs; the relief bake comes after. Ask the designed land where the cliff
        // and the steep banks WILL be, so nothing that needs level ground is planned onto them.
        readonly TownReliefDesign.Input relief = TownReliefDesign.FromLayout();
        bool Gentle(Vector3 p, float limit = 0.16f, float reach = 6f) => TownReliefDesign.Steepness(relief, p.x, p.z, reach) < limit;
        /// <summary>Not on the shoulder of the cliff either: the land 16 m away in every direction is about as high.</summary>
        bool Settled(Vector3 p) => Gentle(p, 0.1f, 16f);

        public void Report()
        {
            var parts = new List<string>();
            foreach (var key in new[] { Hills + " estates", Bluffs + " estates", "set pieces", "avenue trees", "grove trees" })
                if (counts.TryGetValue(key, out int n)) parts.Add(n + " " + key);
            int refused = 0;
            foreach (var kv in counts) if (kv.Key.StartsWith("no:")) refused += kv.Value;
            parts.Add(refused + " estate sites tried and refused (grounds not free)");
            ctx.Log(Name, string.Join(", ", parts));
        }

        // ------------------------------------------------------------------------------------------ setup

        public void Prepare()
        {
            root = ctx.Group("Estates");
            TownLifeKit.Define("estates.gold", new Color(1f, 0.78f, 0.22f), 0.85f, 0.9f);
            TownLifeKit.Define("estates.marble", new Color(0.93f, 0.92f, 0.9f), 0.5f);
            TownLifeKit.Define("estates.drive", new Color(0.8f, 0.74f, 0.64f), 0.1f);
            TownLifeKit.Define("estates.clay", new Color(0.72f, 0.38f, 0.26f), 0.1f);
            TownLifeKit.Define("estates.tile", new Color(0.3f, 0.66f, 0.78f), 0.5f);
            TownLifeKit.Define("estates.lounger", new Color(0.96f, 0.96f, 0.94f), 0.3f);
            TownLifeKit.Define("estates.asphalt", new Color(0.2f, 0.2f, 0.22f), 0.12f);

            TownLifeKit.DefineProp("estates.fountain")
                .Cyl("estates.marble", Vector3.zero, 2.3f, 0.5f, 12, 2.1f)
                .Cyl("water", new Vector3(0f, 0.42f, 0f), 2f, 0.06f, 12)
                .Cyl("estates.marble", new Vector3(0f, 0.4f, 0f), 0.28f, 1.1f, 8, 0.2f)
                .Cyl("estates.marble", new Vector3(0f, 1.5f, 0f), 0.4f, 0.14f, 10, 1.05f)
                .Cyl("water", new Vector3(0f, 1.6f, 0f), 0.95f, 0.04f, 10)
                .Cyl("estates.marble", new Vector3(0f, 1.6f, 0f), 0.12f, 0.7f, 8, 0.08f)
                .Sphere("estates.gold", new Vector3(0f, 2.45f, 0f), new Vector3(0.2f, 0.2f, 0.2f), 8, 5)
                .ColliderCapsule(new Vector3(0f, 0.6f, 0f), 2.2f, 1.2f);
            TownLifeKit.DefineProp("estates.gazebo")
                .Cyl("estates.marble", Vector3.zero, 2.6f, 0.25f, 8)
                .Cyl("estates.marble", new Vector3(1.9f, 0.25f, 1.9f) * 0.92f, 0.11f, 2.5f, 6)
                .Cyl("estates.marble", new Vector3(-1.9f, 0.25f, 1.9f) * 0.92f, 0.11f, 2.5f, 6)
                .Cyl("estates.marble", new Vector3(1.9f, 0.25f, -1.9f) * 0.92f, 0.11f, 2.5f, 6)
                .Cyl("estates.marble", new Vector3(-1.9f, 0.25f, -1.9f) * 0.92f, 0.11f, 2.5f, 6)
                .Cyl("estates.marble", new Vector3(0f, 2.75f, 0f), 2.75f, 0.14f, 8)
                .Cone("roof.slate", new Vector3(0f, 2.89f, 0f), 2.9f, 1.3f, 8)
                .Sphere("estates.gold", new Vector3(0f, 4.3f, 0f), new Vector3(0.16f, 0.16f, 0.16f), 6, 4)
                .Box("wood.pale", new Vector3(0f, 0.6f, -1.5f), new Vector3(1.6f, 0.08f, 0.45f))
                .ColliderBox(new Vector3(0f, 0.12f, 0f), new Vector3(4.6f, 0.25f, 4.6f)).Walkable();
            // The owner, in gold, holding a bag of money and pointing at nothing
            TownLifeKit.DefineProp("estates.statue")
                .Cyl("estates.marble", Vector3.zero, 1.5f, 0.4f, 8)
                .Box("estates.marble", new Vector3(0f, 1.3f, 0f), new Vector3(1.5f, 1.8f, 1.5f))
                .Cyl("estates.marble", new Vector3(0f, 2.2f, 0f), 1.1f, 0.14f, 8)
                .Blob("estates.gold", new Vector3(0f, 3.5f, 0f), new Vector3(0.75f, 1.05f, 0.6f), 9, 0.1f)
                .Sphere("estates.gold", new Vector3(0f, 4.95f, 0.05f), new Vector3(0.55f, 0.58f, 0.55f), 10, 6)
                .Cyl("estates.gold", new Vector3(0f, 5.4f, 0.05f), 0.5f, 0.1f, 10)
                .Cyl("estates.gold", new Vector3(0f, 5.5f, 0.05f), 0.32f, 0.5f, 10)
                .Limb("estates.gold", new Vector3(0.6f, 3.9f, 0.1f), new Vector3(1.5f, 4.6f, 0.9f), 0.2f, 0.14f)
                .Limb("estates.gold", new Vector3(-0.6f, 3.9f, 0.1f), new Vector3(-1f, 3.1f, 0.5f), 0.2f, 0.16f)
                .Blob("estates.gold", new Vector3(-1.1f, 2.75f, 0.6f), new Vector3(0.42f, 0.5f, 0.42f), 4, 0.12f)
                .Limb("estates.gold", new Vector3(0.3f, 2.3f, 0f), new Vector3(0.34f, 2.9f, 0.05f), 0.24f, 0.26f)
                .Limb("estates.gold", new Vector3(-0.3f, 2.3f, 0f), new Vector3(-0.34f, 2.9f, 0.05f), 0.24f, 0.26f)
                .ColliderBox(new Vector3(0f, 1.2f, 0f), new Vector3(2.4f, 2.4f, 2.4f));
            TownLifeKit.DefineProp("estates.net")
                .Cyl("metal.dark", new Vector3(-5.6f, 0f, 0f), 0.06f, 1.1f, 6)
                .Cyl("metal.dark", new Vector3(5.6f, 0f, 0f), 0.06f, 1.1f, 6)
                .Box("paint.white", new Vector3(0f, 1.0f, 0f), new Vector3(11.2f, 0.07f, 0.03f))
                .Box("metal.dark", new Vector3(0f, 0.55f, 0f), new Vector3(11.2f, 0.85f, 0.02f)).NoShadows();
            TownLifeKit.DefineProp("estates.lounger")
                .Box("estates.lounger", new Vector3(0f, 0.3f, -0.2f), new Vector3(0.65f, 0.08f, 1.4f))
                .Box("estates.lounger", new Vector3(0f, 0.52f, 0.72f), new Vector3(0.65f, 0.08f, 0.6f), new Vector3(-42f, 0f, 0f))
                .Box("wood.pale", new Vector3(0f, 0.14f, -0.2f), new Vector3(0.55f, 0.28f, 1.2f)).NoShadows();
            TownLifeKit.DefineProp("estates.parasol")
                .Cyl("wood.pale", Vector3.zero, 0.04f, 2.3f, 6)
                .Cone("paint.white", new Vector3(0f, 2.05f, 0f), 1.5f, 0.55f, 8);
            TownLifeKit.DefineProp("estates.flag")
                .Cyl("metal.light", Vector3.zero, 0.035f, 2.2f, 6)
                .Sheet("paint.red", new Vector3(0f, 2.2f, 0f), new Vector3(0.6f, 2.05f, 0f), new Vector3(0f, 1.9f, 0f), new Vector3(0f, 1.9f, 0f)).NoShadows();
            TownLifeKit.DefineProp("estates.windsock")
                .Cyl("metal.light", Vector3.zero, 0.05f, 3.2f, 6)
                .Cyl("paint.orange", new Vector3(0.1f, 3.05f, 0f), 0.2f, 1f, 8, 0.1f, new Vector3(0f, 0f, -82f));
            TownLifeKit.DefineProp("estates.gatelamp")
                .Add("kit.post.stone", Vector3.zero, 0f, 1.25f)
                .Sphere("eye.white", new Vector3(0f, 1.95f, 0f), new Vector3(0.2f, 0.22f, 0.2f), 8, 5)
                .ColliderBox(new Vector3(0f, 0.8f, 0f), new Vector3(0.6f, 1.6f, 0.6f));

            var E = TownLifeOccupancy.Block.Everything;
            foreach (var road in CoastalCityLayout.Roads)
            {
                bool near = false;
                foreach (var p in road.points) if (TownLifeGeo.RectContains(occ.area, p, 0f)) { near = true; break; }
                if (!near) continue;
                occ.ReservePolyline(road.points, road.halfWidth + 3f, TownLifeTags.Sidewalk, E);
                occ.ReserveCircle(road.points[0], 13f, "junction");
                occ.ReserveCircle(road.points[road.points.Length - 1], 13f, "junction");
                if (road.service || road.bridge) continue;
                for (float s = 18f; s < road.Length; s += 42f)
                {
                    var c = TownLifeGeo.PointAt(road.points, s, out var t);
                    for (int side = -1; side <= 1; side += 2) occ.ReserveCircle(c + TownLifeGeo.Right(t) * side * (road.halfWidth + 2.4f), 1.3f, "lamp");
                }
            }
            // Every real estate keeps its whole grounds: house, drive, pool, courts, hedges
            foreach (var stop in ctx.town.GetComponentsInChildren<PickupStop>(true))
            {
                if (stop == null || !TownLifeGeo.RectContains(occ.area, stop.transform.position, 0f)) continue;
                var lot = stop.transform.parent != null ? stop.transform.parent : stop.transform;
                // an oriented box in the lot's own frame (most lots sit at an angle; a world-aligned box wastes half the lawn)
                Vector3 o = stop.transform.position, lr = stop.transform.right, lf = stop.transform.forward;
                lr.y = 0f; lf.y = 0f; lr.Normalize(); lf.Normalize();
                float x0 = 0f, x1 = 0f, z0 = 0f, z1 = 0f;
                foreach (var r in lot.GetComponentsInChildren<Renderer>(true))
                {
                    var rb = r.bounds;
                    for (int k = 0; k < 4; k++)
                    {
                        Vector3 corner = new Vector3(k % 2 == 0 ? rb.min.x : rb.max.x, 0f, k < 2 ? rb.min.z : rb.max.z) - o;
                        float x = Vector3.Dot(corner, lr), z = Vector3.Dot(corner, lf);
                        x0 = Mathf.Min(x0, x); x1 = Mathf.Max(x1, x); z0 = Mathf.Min(z0, z); z1 = Mathf.Max(z1, z);
                    }
                }
                occ.ReserveBox(o + lr * (x0 + x1) * 0.5f + lf * (z0 + z1) * 0.5f, TownLifeGeo.Yaw(lf), (x1 - x0) * 0.5f, (z1 - z0) * 0.5f, "lot", E, 2f);
            }
            foreach (var site in ctx.town.GetComponentsInChildren<CoastalInfillSite>(true))
            {
                var b = site.WorldBounds;
                if (TownLifeGeo.RectContains(occ.area, b.center, 0f)) occ.ReserveRect(new Rect(b.min.x, b.min.z, b.size.x, b.size.z), "infill", E, 1f);
            }
            var nature = ctx.town.Find("Nature");
            if (nature != null)
                foreach (Transform t in nature)
                    if (TownLifeGeo.RectContains(occ.area, t.position, 0f)) occ.ReserveCircle(t.position, 3.5f, "tree.nature");
            var authored = TownAuthoringScope.AuthoredRoot(ctx.town);
            if (authored != null)
                foreach (var r in authored.GetComponentsInChildren<Renderer>(true))
                    if (TownLifeGeo.RectContains(occ.area, r.bounds.center, 0f))
                        occ.ReserveRect(new Rect(r.bounds.min.x, r.bounds.min.z, r.bounds.size.x, r.bounds.size.z), "authored", E, 0.6f);
            foreach (var gate in CoastalCityLayout.Gates) occ.ReserveCircle(gate.position, 10f, "gate", E);
        }

        // ------------------------------------------------------------------------------------------ one district

        public void District(string id)
        {
            bool bluffs = id == Bluffs;
            var bounds = CoastalCityLayout.DistrictBounds(id);
            var g = TownLifeKit.Group(root, "TL_Estates_" + (bluffs ? "Bluffs" : "Hills"));
            var roads = new List<CoastalCityLayout.RoadDef>();
            foreach (var road in CoastalCityLayout.Roads) if (road.districtId == id && !road.bridge) roads.Add(road);
            roads.Sort((a, b) => string.CompareOrdinal(a.id, b.id));

            // 1. Scenery estates along the roads
            foreach (var road in roads)
                for (int side = -1; side <= 1; side += 2)
                {
                    float next = 26f;
                    int index = 0;
                    for (float s = 26f; s < road.Length - 26f; s += 4f)
                    {
                        if (s < next) continue;
                        var rng = ctx.Rng("estates/site/" + road.id + "/" + side + "/" + index++);
                        float span = TryEstate(g, road, s, side, bluffs, rng);
                        if (span > 0f) next = s + span + R(rng, 8f, 15f);
                    }
                }

            // 2. Set pieces on the open lawns
            string[] pieces = bluffs
                ? new[] { "helipad", "statue", "pool", "tennis", "fountain", "putting", "helipad", "gazebo", "pool", "statue", "topiary", "tennis" }
                : new[] { "tennis", "gazebo", "topiary", "fountain", "putting", "pool", "gazebo", "tennis", "topiary", "fountain" };
            for (int i = 0; i < pieces.Length; i++) SetPiece(g, id, bounds, pieces[i], i);

            // 3. Avenue trees along the roads
            string[] avenue = bluffs ? new[] { "kit.tree.round.l", "kit.tree.pine.l" } : new[] { "kit.tree.elm.l", "kit.tree.oak.l", "kit.tree.maple.l" };
            foreach (var road in roads)
            {
                string kind = avenue[(int)((uint)TownLifeContext.StableHash(road.id) % (uint)avenue.Length)];
                int i = 0;
                foreach (float s in TownLifeGeo.Stations(road.Length, 17f, 12f, 12f))
                {
                    var rng = ctx.Rng("estates/avenue/" + road.id + "/" + i);
                    int side = i++ % 2 == 0 ? 1 : -1;
                    var c = TownLifeGeo.PointAt(road.points, s, out var t);
                    var p = c + TownLifeGeo.Right(t) * side * (road.halfWidth + 4.6f);
                    if (!Dry(p) || !Gentle(p, 0.4f) || !occ.TryReserve(p, 1f, TownLifeTags.Tree + ".estates")) continue;
                    TownLifeKit.Spawn(g, kind, G(p), R(rng, 0f, 360f), R(rng, 0.95f, 1.2f));
                    Count("avenue trees");
                }
            }

            // 4. Groves on whatever lawn is left
            string[] grove = { "kit.clump.mixed.5", "kit.clump.mixed.3", "kit.clump.pine.3", "kit.tree.oak.xl", "kit.tree.elm.l", "kit.clump.birch.3", "kit.tree.willow.l" };
            for (float x = bounds.xMin + 8f; x < bounds.xMax - 8f; x += 17f)
                for (float z = bounds.yMin + 8f; z < bounds.yMax - 8f; z += 17f)
                {
                    var rng = ctx.Rng("estates/grove/" + id + "/" + Mathf.RoundToInt(x) + "/" + Mathf.RoundToInt(z));
                    if (rng.NextDouble() > 0.62) continue;
                    var p = new Vector3(x + R(rng, -6f, 6f), 0f, z + R(rng, -6f, 6f));
                    string kind = TownLifeContext.Pick(rng, grove);
                    if (!Dry(p) || !Gentle(p, 0.4f) || CoastalCityLayout.DistanceToRoad(p) < 11f) continue;
                    if (!occ.TryReserve(p, kind.Contains("clump") ? 6f : 4.5f, TownLifeTags.Tree + ".estates")) continue;
                    TownLifeKit.Spawn(g, kind, G(p), R(rng, 0f, 360f), R(rng, 0.95f, 1.25f));
                    Count("grove trees");
                }
        }

        // ------------------------------------------------------------------------------------------ a scenery estate

        /// <summary>Try to stand an estate beside <paramref name="road"/> at <paramref name="station"/>. Returns the
        /// length of frontage it took, or 0.</summary>
        float TryEstate(Transform parent, CoastalCityLayout.RoadDef road, float station, int side, bool bluffs, System.Random rng)
        {
            var c = TownLifeGeo.PointAt(road.points, station, out var tangent);
            Vector3 along = TownLifeGeo.Dir(TownLifeGeo.Yaw(tangent)), away = TownLifeGeo.Right(tangent) * side;
            float edge = road.halfWidth + 3f, yaw = TownLifeGeo.Yaw(-away);
            // Try the grandest version first, then smaller ones, each a little nearer and a little further from the road
            bool pair = false;
            House a = default, b = Houses[3];
            float scale = 1f, halfW = 0f, halfD = 0f, setback = 0f;
            Vector3 centre = default;
            bool found = false;
            int first = rng.Next(Houses.Length);
            for (int attempt = bluffs ? -1 : 0; attempt < Houses.Length && !found; attempt++)
            {
                pair = attempt < 0;
                a = pair ? Houses[2] : Houses[bluffs ? 3 - attempt : (first + attempt) % Houses.Length];
                scale = bluffs ? (pair ? 1.45f : 1.5f) : 1.28f;
                halfW = (pair ? a.halfW + b.halfW - 0.3f : a.halfW) * scale;
                halfD = a.halfD * scale;
                foreach (float back in bluffs ? new[] { 20f, 15f, 27f } : new[] { 14f, 11f, 19f })
                {
                    setback = back;
                    centre = c + away * (edge + setback + halfD);
                    if (Fits(c, centre, along, away, yaw, halfW, halfD, setback, edge)) { found = true; break; }
                }
            }
            if (!found) return 0f;

            string key = (bluffs ? "bluffs" : "hills") + "_" + (++serial);
            var g = TownLifeKit.Group(parent, "TL_Estate_" + key);
            // local frame: x along the road (as seen facing the road), z towards the road
            Vector3 right = TownLifeGeo.Right(-away), front = -away;
            Vector3 L(float x, float z) => centre + right * x + front * z;

            // The house (two wings in the Bluffs)
            float doorX, doorZ = a.doorZ * scale;
            if (pair)
            {
                float split = (a.halfW - b.halfW) * 0.5f * scale;
                TownLifeKit.SpawnPrefab(g, Buildings + a.prefab, G(L(split - (b.halfW - 0.15f) * scale, 0f)), yaw, scale, true);
                TownLifeKit.SpawnPrefab(g, Buildings + b.prefab, G(L(split + (a.halfW - 0.15f) * scale, 0f)), yaw, scale, true);
                doorX = split - (b.halfW - 0.15f) * scale + a.doorX * scale;
            }
            else
            {
                TownLifeKit.SpawnPrefab(g, Buildings + a.prefab, G(centre), yaw, scale, true);
                doorX = a.doorX * scale;
            }
            occ.ReserveBox(centre, yaw, halfW + 0.6f, halfD + 0.6f, "building.estates", TownLifeOccupancy.Block.Everything);

            // Front walk from the door to the pavement, a drive down one side with a car on it
            float toKerb = halfD + setback;
            var walk = new List<Vector3> { L(doorX, doorZ - 0.5f), L(doorX, toKerb + 0.2f) };
            TownLifeKit.Ribbon(g, "estates_walk_" + key, walk, 2f, bluffs ? "estates.marble" : "paving.warm", TownLifeContext.PavingY);
            occ.ReservePolyline(walk, 1.5f, TownLifeTags.PathSurface + ".estates", TownLifeOccupancy.Block.Solids);
            float driveX = (doorX > 0f ? -1f : 1f) * (halfW - 2.4f);
            var drive = new List<Vector3> { L(driveX, toKerb + 3.1f), L(driveX, doorZ + 1f), L(driveX, -halfD * 0.3f) };
            TownLifeKit.Ribbon(g, "estates_drive_" + key, drive, 4f, "estates.drive", TownLifeContext.PavingY);
            occ.ReservePolyline(drive, 2.6f, TownLifeTags.PathSurface + ".estates", TownLifeOccupancy.Block.Solids);
            string[] cars = { "SM_car_town_01", "SM_car_town_02", "SM_car_town_03", "SM_car_town_04" };
            TownLifeKit.SpawnPrefab(g, Cars + TownLifeContext.Pick(rng, cars), At(L(driveX, doorZ + R(rng, 2f, 5f)), TownLifeContext.PavingY), yaw + (rng.Next(2) == 0 ? 0f : 180f) + R(rng, -3f, 3f), 1f, true);
            if (bluffs) TownLifeKit.SpawnPrefab(g, Cars + TownLifeContext.Pick(rng, cars), At(L(driveX + (driveX > 0f ? -0.2f : 0.2f), doorZ + R(rng, 8f, 10f)), TownLifeContext.PavingY), yaw + R(rng, -3f, 3f), 1f, true);

            // The frontage: a tall hedge (Hills) or a stone wall (Bluffs) along the pavement, open at the walk and the drive
            float frontZ = toKerb - 1.1f, w = halfW + 2.6f;
            var gaps = new List<Vector2> { new Vector2(doorX - 1.5f, doorX + 1.5f), new Vector2(driveX - 2.6f, driveX + 2.6f) };
            gaps.Sort((p, q) => p.x.CompareTo(q.x));
            float from = -w;
            int run = 0;
            var spec = new TownLifeKit.RunSpec
            {
                panel = bluffs ? "kit.panel.wall.stone" : "kit.panel.hedge.tall", panelLength = 2f, post = bluffs ? "kit.post.stone" : null,
                collider = TownLifeKit.RunCollider.Box, colliderHeight = bluffs ? 0.5f : 1.6f, colliderThickness = bluffs ? 0.4f : 0.8f, y = TownLifeContext.GrassY,
            };
            foreach (var gap in gaps)
            {
                if (gap.x - from > 2.2f) TownLifeKit.Run(g, "estates_front_" + key + "_" + run++, new List<Vector3> { L(from, frontZ), L(gap.x, frontZ) }, spec);
                TownLifeKit.Spawn(g, "estates.gatelamp", G(L(gap.x, frontZ)), yaw);
                TownLifeKit.Spawn(g, "estates.gatelamp", G(L(gap.y, frontZ)), yaw);
                from = gap.y;
            }
            if (w - from > 2.2f) TownLifeKit.Run(g, "estates_front_" + key + "_" + run, new List<Vector3> { L(from, frontZ), L(w, frontZ) }, spec);
            occ.ReserveLine(L(-w, frontZ), L(w, frontZ), 1f, TownLifeTags.Hedge + ".estates");

            // The garden: topiary down the walk, beds under the windows, shade trees at the corners, something in the lawn
            for (float z = doorZ + 2.5f; z < toKerb - 3f; z += 3.2f)
                for (int s = -1; s <= 1; s += 2)
                    if (occ.TryReserve(L(doorX + s * 1.9f, z), 0.5f, TownLifeTags.Shrub + ".estates")) TownLifeKit.Spawn(g, "kit.shrub.cone", G(L(doorX + s * 1.9f, z)), R(rng, 0f, 360f), bluffs ? 1.5f : 1.25f);
            string[] flowers = { "kit.flowers.red", "kit.flowers.pink", "kit.flowers.white", "kit.flowers.purple", "kit.flowers.yellow" };
            string bloom = TownLifeContext.Pick(rng, flowers);
            for (float x = -halfW + 1.2f; x <= halfW - 1.2f; x += 1.6f)
            {
                if (Mathf.Abs(x - doorX) < 2f || Mathf.Abs(x - driveX) < 3f) continue;
                TownLifeKit.Spawn(g, rng.Next(4) == 0 ? "kit.shrub.round.s" : bloom, G(L(x, halfD + 1f)), R(rng, 0f, 360f), R(rng, 0.9f, 1.2f));
            }
            string[] shade = bluffs ? new[] { "kit.tree.round.l", "kit.tree.blossom.m" } : new[] { "kit.tree.oak.l", "kit.tree.maple.l", "kit.tree.blossom.m" };
            foreach (var spot in new[] { new Vector2(-halfW - 1.6f, toKerb - 5f), new Vector2(halfW + 1.6f, toKerb - 5.5f), new Vector2(-halfW - 2f, -halfD - 2.5f), new Vector2(halfW + 2f, -halfD - 2.8f) })
                if (occ.TryReserve(L(spot.x, spot.y), 1.4f, TownLifeTags.Tree + ".estates"))
                    TownLifeKit.Spawn(g, TownLifeContext.Pick(rng, shade), G(L(spot.x, spot.y)), R(rng, 0f, 360f), R(rng, 0.95f, 1.2f));
            Vector3 lawn = L(-doorX * 0.5f + (doorX > 0f ? -3.5f : 3.5f), (doorZ + toKerb) * 0.5f);
            if (occ.TryReserve(lawn, 2.6f, TownLifeTags.Feature + ".estates"))
                TownLifeKit.Spawn(g, bluffs ? "estates.fountain" : rng.Next(2) == 0 ? "kit.birdbath" : "estates.fountain", G(lawn), R(rng, 0f, 360f), bluffs ? 1f : 0.7f);

            occ.ReserveBox(c + away * (edge + setback * 0.5f), yaw, halfW + 3f, setback * 0.5f, "lawn.estates", TownLifeOccupancy.Block.Solids);
            occ.ReserveBox(centre, yaw, halfW + 4f, halfD + 4.5f, "lawn.estates", TownLifeOccupancy.Block.Solids);
            Count((bluffs ? Bluffs : Hills) + " estates");
            return halfW * 2f + 8f;
        }

        /// <summary>Is there room for a house with a garden round it and a front between it and the pavement?</summary>
        bool Fits(Vector3 c, Vector3 centre, Vector3 along, Vector3 away, float yaw, float halfW, float halfD, float setback, float edge)
        {
            if (!occ.FreeBox(centre, yaw, halfW + 4f, halfD + 4.5f)) { Count("no:" + occ.Blocker(centre, halfD)); return false; }
            if (!occ.FreeBox(c + away * (edge + setback * 0.5f + 0.4f), yaw, halfW + 3f, setback * 0.5f - 0.6f)) { Count("no:front"); return false; }
            foreach (var corner in new[] { new Vector2(-1f, -1f), new Vector2(1f, -1f), new Vector2(-1f, 1f), new Vector2(1f, 1f) })
            {
                var at = centre + along * corner.x * (halfW + 4f) + away * corner.y * (halfD + 4f);
                if (!Dry(at)) { Count("no:wet"); return false; }
                if (!Gentle(at)) { Count("no:steep"); return false; }
            }
            if (!Gentle(centre) || !Settled(centre)) { Count("no:steep"); return false; }
            // Town Life's own walkable pieces may still hold last bake's draped shape here (the relief bake drapes them in
            // place), so they are left to the occupancy map; this is the backstop for everything else in the scene.
            foreach (var hit in Physics.OverlapBox(G(centre) + Vector3.up * 3.2f, new Vector3(halfW + 1f, 2.6f, halfD + 1f), Quaternion.Euler(0f, yaw, 0f), ~0, QueryTriggerInteraction.Ignore))
                if (!hit.transform.IsChildOf(ctx.root)) { Count("no:physics"); return false; }
            return true;
        }

        // ------------------------------------------------------------------------------------------ set pieces

        /// <summary>The free rectangle (half extents plus a margin) nearest a road in the district, on a 7 m grid.</summary>
        bool FindLawn(Rect bounds, float halfW, float halfD, System.Random rng, out Vector3 centre)
        {
            centre = default;
            float best = float.MaxValue;
            for (float x = bounds.xMin + 10f; x < bounds.xMax - 10f; x += 7f)
                for (float z = bounds.yMin + 10f; z < bounds.yMax - 10f; z += 7f)
                {
                    var p = new Vector3(x, 0f, z);
                    float road = CoastalCityLayout.DistanceToRoad(p);
                    float need = Mathf.Max(halfW, halfD) + 9f;
                    if (road < need || road > need + 34f) continue;
                    if (!Dry(p) || !Dry(p + new Vector3(halfW, 0f, halfD)) || !Dry(p - new Vector3(halfW, 0f, halfD))) continue;
                    if (!Settled(p) || !Gentle(p) || !Gentle(p + new Vector3(halfW + 3f, 0f, halfD + 3f)) || !Gentle(p - new Vector3(halfW + 3f, 0f, halfD + 3f)) ||
                        !Gentle(p + new Vector3(halfW + 3f, 0f, -halfD - 3f)) || !Gentle(p + new Vector3(-halfW - 3f, 0f, halfD + 3f))) continue;
                    if (!occ.FreeBox(p, 0f, halfW + 4f, halfD + 4f)) continue;
                    float score = road + (float)rng.NextDouble() * 18f;
                    if (score < best) { best = score; centre = p; }
                }
            return best < float.MaxValue;
        }

        void SetPiece(Transform parent, string district, Rect bounds, string kind, int index)
        {
            var rng = ctx.Rng("estates/piece/" + district + "/" + kind + "/" + index);
            float halfW = 4f, halfD = 4f;
            switch (kind)
            {
                case "tennis": halfW = 9.5f; halfD = 18f; break;
                case "pool": halfW = 9f; halfD = 6.5f; break;
                case "helipad": halfW = 8f; halfD = 8f; break;
                case "topiary": halfW = 7f; halfD = 7f; break;
                case "putting": halfW = 8f; halfD = 6f; break;
                case "statue": halfW = 4.5f; halfD = 4.5f; break;
                case "gazebo": halfW = 5f; halfD = 5f; break;
                case "fountain": halfW = 5f; halfD = 5f; break;
            }
            if (!FindLawn(bounds, halfW, halfD, rng, out var c)) return;
            occ.ReserveBox(c, 0f, halfW + 1f, halfD + 1f, TownLifeTags.Feature + ".estates", TownLifeOccupancy.Block.Everything);
            string key = TownLifeKit.Sanitize(district.Substring(0, 5) + "_" + kind + "_" + index);
            var g = TownLifeKit.Group(parent, "TL_Piece_" + key);
            // a court, a pool, a pad and a green are level whatever the hill does; the small pieces just stand on it
            if (kind == "tennis" || kind == "pool" || kind == "helipad" || kind == "putting" || kind == "topiary")
                TownLifeKit.LevelPad(g, key, c, new Vector2(halfW * 2f + 1f, halfD * 2f + 1f), 0f, 4f);
            float y = TownLifeContext.PavingY, ym = TownLifeContext.MarkingY, lift = y - TownLifeContext.GrassY;
            var d = new TownLifeKit.MeshData();
            switch (kind)
            {
                case "tennis":
                {
                    Rect4(d, "court.green", c, 8.5f, 17f, y);
                    Rect4(d, "estates.clay", c, 5.5f, 11.9f, ym);
                    float l = ym + TownLifeContext.DecalLift;
                    foreach (float x in new[] { -5.5f, -4.1f, 4.1f, 5.5f }) Rect4(d, "paint.white", c + new Vector3(x, 0f, 0f), 0.05f, 11.9f, l);
                    foreach (float z in new[] { -11.9f, -6.4f, 6.4f, 11.9f }) Rect4(d, "paint.white", c + new Vector3(0f, 0f, z), z * z > 100f ? 5.5f : 4.1f, 0.05f, l);
                    Rect4(d, "paint.white", c, 0.05f, 6.4f, l);
                    TownLifeKit.Custom(g, "estates_tennis_" + key, d);
                    TownLifeKit.Spawn(g, "estates.net", G(c) + Vector3.up * lift, 0f);
                    TownLifeKit.Run(g, "estates_tennis_fence_" + key, new List<Vector3>
                    {
                        c + new Vector3(-8.9f, 0f, -17.4f), c + new Vector3(8.9f, 0f, -17.4f), c + new Vector3(8.9f, 0f, 17.4f), c + new Vector3(-8.9f, 0f, 17.4f), c + new Vector3(-8.9f, 0f, -12f),
                    }, new TownLifeKit.RunSpec { panel = "kit.panel.hedge.mid", panelLength = 2f, collider = TownLifeKit.RunCollider.Box, colliderHeight = 1.1f, colliderThickness = 0.7f, y = TownLifeContext.GrassY });
                    TownLifeKit.Spawn(g, "kit.bench.park", G(c + new Vector3(7.4f, 0f, 0f)) + Vector3.up * lift, -90f);
                    break;
                }
                case "pool":
                {
                    Rect4(d, "estates.marble", c, 8.5f, 6f, y);
                    Rect4(d, "estates.tile", c + new Vector3(-1.5f, 0f, 0f), 5.5f, 3.6f, ym);
                    Rect4(d, "water", c + new Vector3(-1.5f, 0f, 0f), 5.2f, 3.3f, ym + TownLifeContext.DecalLift);
                    TownLifeKit.Custom(g, "estates_pool_" + key, d);
                    for (int i = 0; i < 3; i++) TownLifeKit.Spawn(g, "estates.lounger", G(c + new Vector3(6.4f, 0f, -3.4f + i * 3.2f)) + Vector3.up * lift, -90f + R(rng, -8f, 8f));
                    TownLifeKit.Spawn(g, "estates.parasol", G(c + new Vector3(6.6f, 0f, -1.8f)) + Vector3.up * lift, 0f);
                    TownLifeKit.Spawn(g, "estates.parasol", G(c + new Vector3(6.6f, 0f, 1.5f)) + Vector3.up * lift, 0f);
                    foreach (var corner in new[] { new Vector3(-8f, 0f, -5.4f), new Vector3(-8f, 0f, 5.4f), new Vector3(8f, 0f, -5.4f), new Vector3(8f, 0f, 5.4f) })
                        TownLifeKit.Spawn(g, "kit.shrub.cone", G(c + corner) + Vector3.up * lift, 0f, 1.4f);
                    break;
                }
                case "helipad":
                {
                    TownLifeKit.AddPolygon(d, "estates.asphalt", TownLifeKit.Circle(c, 6.5f, 28), y);
                    Ring(d, "paint.yellow", c, 5.6f, 6f, ym);
                    float l = ym;
                    Rect4(d, "paint.white", c + new Vector3(-1.5f, 0f, 0f), 0.35f, 2.4f, l);
                    Rect4(d, "paint.white", c + new Vector3(1.5f, 0f, 0f), 0.35f, 2.4f, l);
                    Rect4(d, "paint.white", c, 1.5f, 0.35f, l);
                    TownLifeKit.Custom(g, "estates_helipad_" + key, d);
                    TownLifeKit.Spawn(g, "estates.windsock", G(c + new Vector3(7.2f, 0f, 5.6f)), R(rng, 0f, 360f));
                    for (int i = 0; i < 6; i++) TownLifeKit.Spawn(g, "kit.shrub.round.s", G(c + Quaternion.Euler(0f, i * 60f + 20f, 0f) * new Vector3(7.4f, 0f, 0f)), 0f);
                    break;
                }
                case "topiary":
                {
                    Rect4(d, "gravel", c, 6.5f, 0.9f, y);
                    Rect4(d, "gravel", c, 0.9f, 6.5f, y);
                    Ring(d, "gravel", c, 5.6f, 6.6f, y);
                    TownLifeKit.Custom(g, "estates_topiary_" + key, d);
                    TownLifeKit.Spawn(g, "kit.birdbath", G(c) + Vector3.up * lift, 0f, 1.3f);
                    foreach (int sx in new[] { -1, 1 })
                        foreach (int sz in new[] { -1, 1 })
                        {
                            TownLifeKit.Spawn(g, "kit.shrub.cone", G(c + new Vector3(sx * 2.6f, 0f, sz * 2.6f)), 0f, 1.7f);
                            TownLifeKit.Spawn(g, "kit.shrub.round.m", G(c + new Vector3(sx * 4.2f, 0f, sz * 1.9f)), R(rng, 0f, 360f));
                            TownLifeKit.Spawn(g, "kit.flowers.mixed", G(c + new Vector3(sx * 1.9f, 0f, sz * 4.2f)), R(rng, 0f, 360f), 1.3f);
                        }
                    break;
                }
                case "putting":
                {
                    TownLifeKit.AddPolygon(d, "lawn.light", Oval(c, 7.5f, 5.5f, 20), TownLifeContext.LawnY);
                    TownLifeKit.AddPolygon(d, "sand", Oval(c + new Vector3(4.8f, 0f, 3.2f), 2.2f, 1.5f, 12), TownLifeContext.SoilY);
                    TownLifeKit.AddPolygon(d, "metal.dark", Oval(c + new Vector3(-2.5f, 0f, -1f), 0.12f, 0.12f, 8), TownLifeContext.SoilY);
                    TownLifeKit.Custom(g, "estates_putting_" + key, d);
                    TownLifeKit.Spawn(g, "estates.flag", G(c + new Vector3(-2.5f, 0f, -1f)), R(rng, 0f, 360f));
                    TownLifeKit.Spawn(g, "kit.bench.park", G(c + new Vector3(-7.4f, 0f, 3f)), 110f);
                    break;
                }
                case "statue":
                    TownLifeKit.AddPolygon(d, "estates.marble", TownLifeKit.Circle(c, 4f, 20), y);
                    TownLifeKit.Custom(g, "estates_statue_" + key, d);
                    TownLifeKit.Spawn(g, "estates.statue", G(c) + Vector3.up * lift, TownLifeGeo.Yaw(NearestRoadPoint(c) - c));
                    for (int i = 0; i < 4; i++) TownLifeKit.Spawn(g, "kit.shrub.cone", G(c + Quaternion.Euler(0f, i * 90f + 45f, 0f) * new Vector3(3.6f, 0f, 0f)), 0f, 1.5f);
                    break;
                case "gazebo":
                    TownLifeKit.Spawn(g, "estates.gazebo", G(c), R(rng, 0f, 45f));
                    for (int i = 0; i < 6; i++) TownLifeKit.Spawn(g, i % 2 == 0 ? "kit.shrub.flower.pink" : "kit.shrub.round.s", G(c + Quaternion.Euler(0f, i * 60f, 0f) * new Vector3(3.9f, 0f, 0f)), R(rng, 0f, 360f));
                    break;
                default:
                    TownLifeKit.AddPolygon(d, "gravel", TownLifeKit.Circle(c, 4.4f, 20), y);
                    TownLifeKit.Custom(g, "estates_fountain_" + key, d);
                    TownLifeKit.Spawn(g, "estates.fountain", G(c) + Vector3.up * lift, 0f, 1.25f);
                    for (int i = 0; i < 4; i++) TownLifeKit.Spawn(g, "kit.bench.park", G(c + Quaternion.Euler(0f, i * 90f, 0f) * new Vector3(0f, 0f, 4f)) + Vector3.up * lift, i * 90f + 180f);
                    break;
            }
            Count("set pieces");
        }

        Vector3 NearestRoadPoint(Vector3 p)
        {
            Vector3 best = p + Vector3.forward;
            float bestD = float.MaxValue;
            foreach (var road in CoastalCityLayout.Roads)
                for (int i = 0; i < road.points.Length; i += 2)
                {
                    float dd = TownLifeGeo.Dist(road.points[i], p);
                    if (dd < bestD) { bestD = dd; best = road.points[i]; }
                }
            return best;
        }

        static void Rect4(TownLifeKit.MeshData d, string mat, Vector3 c, float halfX, float halfZ, float y)
        {
            d.QuadOut(mat, At(c, y - 10f), new Vector3(c.x - halfX, y, c.z - halfZ), new Vector3(c.x + halfX, y, c.z - halfZ),
                new Vector3(c.x + halfX, y, c.z + halfZ), new Vector3(c.x - halfX, y, c.z + halfZ));
        }

        static void Ring(TownLifeKit.MeshData d, string mat, Vector3 c, float inner, float outer, float y, int segments = 28)
        {
            for (int i = 0; i < segments; i++)
            {
                float a0 = i * Mathf.PI * 2f / segments, a1 = (i + 1) * Mathf.PI * 2f / segments;
                Vector3 d0 = new Vector3(Mathf.Cos(a0), 0f, Mathf.Sin(a0)), d1 = new Vector3(Mathf.Cos(a1), 0f, Mathf.Sin(a1));
                d.QuadOut(mat, At(c, y - 10f), At(c + d0 * inner, y), At(c + d1 * inner, y), At(c + d1 * outer, y), At(c + d0 * outer, y));
            }
        }

        static List<Vector3> Oval(Vector3 c, float rx, float rz, int segments)
        {
            var pts = new List<Vector3>();
            for (int i = 0; i < segments; i++) { float a = i * Mathf.PI * 2f / segments; pts.Add(c + new Vector3(Mathf.Cos(a) * rx, 0f, Mathf.Sin(a) * rz)); }
            return pts;
        }
    }
}
