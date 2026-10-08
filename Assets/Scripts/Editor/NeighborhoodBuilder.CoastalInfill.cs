using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;

public static partial class NeighborhoodBuilder
{
    /// <summary>Full-build decorative street tissue, authored separately from the 98 gameplay collection sites.
    /// Every cluster keeps one stable road anchor and a reserved footprint. The same recipe remains tweakable
    /// in the blueprint without embedding town positions or adding household/network identities here.</summary>
    static void ClearCoastalInfill(Ctx c, HashSet<string> onlyRoads)
    {
        var group = c.root.Find("CoastalInfill");
        if (group == null) return;
        if (onlyRoads == null) { CityEditTransaction.DestroyGenerated(group.gameObject); return; }
        foreach (var spec in CoastalCityLayout.Data.infillSites ?? Array.Empty<CoastalCityLayout.InfillSite>())
            if (onlyRoads.Contains(spec.roadId) && group.Find(spec.id) is Transform old)
                CityEditTransaction.DestroyGenerated(old.gameObject);
    }

    static string BuildCoastalInfill(Ctx c, HashSet<string> onlyRoads = null)
    {
        ClearCoastalInfill(c, onlyRoads);
        var group = c.root.Find("CoastalInfill") ?? Group(c.root, "CoastalInfill");
        var report = new StringBuilder(); var hits = new Collider[256];
        int built = 0, skipped = 0;
        Physics.SyncTransforms();
        foreach (var spec in CoastalCityLayout.Data.infillSites ?? Array.Empty<CoastalCityLayout.InfillSite>())
        {
            if (onlyRoads != null && !onlyRoads.Contains(spec.roadId)) continue;
            var root = Group(group, spec.id);
            var anchor = root.gameObject.AddComponent<CityRoadAnchor>();
            anchor.stableId = "infill/" + spec.id; anchor.roadId = spec.roadId; anchor.u = spec.u;
            anchor.lateralFromRoadEdge = true; anchor.lateral = spec.side * (3f + spec.setback);
            anchor.yawOffset = spec.side * 90f; anchor.Apply();
            var site = root.gameObject.AddComponent<CoastalInfillSite>();
            site.siteId = spec.id; site.kind = spec.type; site.width = spec.width; site.depth = spec.depth;
            string conflict = site.Conflict(c.root, hits);
            if (conflict != null)
            {
                report.AppendLine("SKIP " + spec.id + " (" + spec.roadId + " @ " + spec.u.ToString("0.000") + "): " + conflict);
                CityEditTransaction.DestroyGenerated(root.gameObject); skipped++; continue;
            }
            switch (spec.type)
            {
                case "storefront": InfillStorefront(c, site); break;
                case "workyard": InfillWorkyard(c, site); break;
                case "courtyard": InfillCourtyard(c, site); break;
                case "grove": InfillGrove(c, site); break;
                case "estate": InfillGarden(c, site, true); break;
                default: InfillGarden(c, site, false); break;
            }
            built++; Physics.SyncTransforms();
        }
        return "[Coastal infill] Built " + built + " coherent decorative parcels; skipped " + skipped + ". No collection stops added.\n" + report;
    }

    static GameObject InfillBox(Ctx c, CoastalInfillSite s, string name, Vector3 p, Vector3 size, string material, bool solid = false)
        => OBox(c, s.transform, name, s.transform.TransformPoint(p), s.transform.rotation, size, c.M(material),
            solid ? LayerWalls : LayerDefault, solid);

    static void InfillFloor(Ctx c, CoastalInfillSite s, string material)
    {
        // Terrain is the collider: a thin, non-colliding finish cannot create lip traps at the sidewalk.
        InfillBox(c, s, "Parcel paving", new Vector3(0, .018f, s.depth * .5f), new Vector3(s.width, .025f, s.depth), material);
    }

    static void InfillStorefront(Ctx c, CoastalInfillSite s)
    {
        InfillFloor(c, s, "NB_Concrete");
        int bays = Mathf.Max(1, Mathf.RoundToInt(s.width / 11f));
        float bayWidth = (s.width - .5f) / bays;
        float front = Mathf.Min(3f, s.depth * .19f), buildingDepth = s.depth - front - 2.2f;
        uint pick = (uint)PickupVisuals.StableHash(s.siteId);
        string[] colors = { "NB_WallMint", "NB_WallPeach", "NB_WallCream", "NB_WallLilac", "NB_WallSky" };
        string[] signs = { "LITTLE BIG GROCER", "ODDS & ENDS", "CRUMB & CO.", "SECOND CHANCE", "PETAL TO THE METAL", "THE CORNER CUP" };
        for (int i = 0; i < bays; i++)
        {
            float x = -s.width * .5f + .25f + bayWidth * (i + .5f);
            float height = 7.2f + ((pick + (uint)i) % 3) * .55f;
            float width = bayWidth - .12f;
            InfillBox(c, s, "Attached shop body", new Vector3(x, height * .5f, front + buildingDepth * .5f), new Vector3(width, height, buildingDepth), colors[(pick + (uint)i) % colors.Length], true);
            InfillBox(c, s, "Shop plinth", new Vector3(x, .35f, front + buildingDepth * .5f), new Vector3(width + .1f, .7f, buildingDepth + .1f), "NB_Stone");
            InfillBox(c, s, "Shop parapet", new Vector3(x, height, front + buildingDepth * .5f), new Vector3(width + .1f, .34f, buildingDepth + .1f), "NB_White");
            InfillBox(c, s, "Shop recessed roof", new Vector3(x, height + .19f, front + buildingDepth * .5f), new Vector3(width - .5f, .07f, buildingDepth - .6f), "NB_DoorNavy");
            float glassWidth = (width - 2.9f) * .5f;
            foreach (int side in new[] { -1, 1 })
            {
                InfillBox(c, s, "Display window", new Vector3(x + side * (1.15f + glassWidth * .5f), 1.7f, front - .04f), new Vector3(glassWidth, 2.25f, .12f), "NB_Glass");
                InfillBox(c, s, "Upper sash", new Vector3(x + side * width * .26f, 5.85f, front - .04f), new Vector3(width * .28f, 1.45f, .12f), "NB_Glass");
                InfillBox(c, s, "Upper sill", new Vector3(x + side * width * .26f, 5.1f, front - .1f), new Vector3(width * .31f, .12f, .28f), "NB_White");
                InfillBox(c, s, "Party-wall pilaster", new Vector3(x + side * width * .49f, 3.4f, front - .07f), new Vector3(.3f, 6.8f, .3f), "NB_Stone");
            }
            InfillBox(c, s, "Recessed glazed entrance", new Vector3(x, 1.5f, front - .06f), new Vector3(1.7f, 3f, .14f), "NB_DoorNavy");
            InfillBox(c, s, "Entrance glass", new Vector3(x, 1.9f, front - .15f), new Vector3(1.25f, 1.8f, .05f), "NB_Glass");
            InfillBox(c, s, "Continuous shop canopy", new Vector3(x, 3.25f, front - .85f), new Vector3(width, .25f, 1.9f), i % 2 == 0 ? "NB_SignGreen" : "NB_Ribbon");
            InfillBox(c, s, "Canopy valance", new Vector3(x, 3.05f, front - 1.8f), new Vector3(width, .4f, .08f), i % 2 == 0 ? "NB_SignGreen" : "NB_Ribbon");
            SignBoard(c, s.transform, "Local business fascia", s.transform.TransformPoint(new Vector3(x, 4.15f, front - .1f)),
                s.transform.rotation * Yaw(180), new Vector3(width - .65f, .95f, .18f), c.M("NB_DoorNavy"), signs[(pick + (uint)i) % signs.Length], Color.white, false);
            InfillBox(c, s, "Rear delivery door", new Vector3(x, 1.25f, front + buildingDepth + .05f), new Vector3(2, 2.5f, .1f), "NB_Metal");
        }
        InfillPlanter(c, s, -s.width * .5f + .9f, .8f, 1.3f, 1.1f);
        if (s.width > 17) InfillBench(c, s, s.width * .5f - 2.2f, .9f);
    }

    static void InfillWorkyard(Ctx c, CoastalInfillSite s)
    {
        InfillFloor(c, s, "NB_Gravel");
        float width = s.width * .72f, depth = Mathf.Min(9f, s.depth * .43f), front = s.depth - depth - .6f;
        float height = 5.2f;
        InfillBox(c, s, "Workshop hall", new Vector3(0, height * .5f, front + depth * .5f), new Vector3(width, height, depth), "NB_WallSage", true);
        InfillBox(c, s, "Workshop roof", new Vector3(0, height, front + depth * .5f), new Vector3(width + .6f, .28f, depth + .6f), "NB_DoorNavy");
        for (int side = -1; side <= 1; side += 2)
        {
            float x = side * width * .25f;
            InfillBox(c, s, "Roller door", new Vector3(x, 1.7f, front - .04f), new Vector3(Mathf.Min(4.1f, width * .4f), 3.4f, .12f), "NB_Metal");
            for (int row = 0; row < 6; row++)
                InfillBox(c, s, "Roller door rib", new Vector3(x, .45f + row * .5f, front - .115f), new Vector3(Mathf.Min(4f, width * .39f), .045f, .035f), "NB_DoorNavy");
            InfillBox(c, s, "Workshop clerestory", new Vector3(x, 4.35f, front - .04f), new Vector3(Mathf.Min(4.1f, width * .4f), .65f, .1f), "NB_Glass");
        }
        SignBoard(c, s.transform, "Workshop name", s.transform.TransformPoint(new Vector3(0, 5.8f, front + .2f)), s.transform.rotation * Yaw(180),
            new Vector3(Mathf.Min(11, width), 1f, .2f), c.M("NB_DoorNavy"), "DEPOT WORKS · REPAIRS", Color.white, false);
        InfillFence(c, s, false);
        // One organised material rack and planter, rather than arbitrary loose debris beside the road.
        float rackX = -s.width * .5f + 1.9f, rackZ = Mathf.Min(front * .55f, 5f);
        InfillBox(c, s, "Covered storage rack", new Vector3(rackX, 2, rackZ), new Vector3(2.7f, .18f, 3.5f), "NB_Metal");
        foreach (int side in new[] { -1, 1 })
            InfillBox(c, s, "Rack upright", new Vector3(rackX + side * 1.15f, 1, rackZ), new Vector3(.15f, 2, 3.15f), "NB_Metal");
        for (int i = 0; i < 3; i++)
            InfillBox(c, s, "Stacked timber", new Vector3(rackX, .3f + .38f * i, rackZ), new Vector3(2.3f, .27f, 2.8f), "NB_Wood");
        InfillPlanter(c, s, s.width * .5f - 1.5f, 1.6f, 1.6f, 2.1f);
    }

    static void InfillCourtyard(Ctx c, CoastalInfillSite s)
    {
        InfillFloor(c, s, "NB_ParkPath");
        float wing = Mathf.Min(7, s.width * .22f), wingDepth = s.depth * .7f;
        foreach (int side in new[] { -1, 1 })
        {
            float x = side * (s.width * .5f - wing * .5f - .3f);
            InfillBox(c, s, "Residential podium wing", new Vector3(x, 3.65f, s.depth - wingDepth * .5f - .4f), new Vector3(wing, 7.3f, wingDepth), "NB_WallIvory", true);
            InfillBox(c, s, "Podium cornice", new Vector3(x, 7.4f, s.depth - wingDepth * .5f - .4f), new Vector3(wing + .25f, .3f, wingDepth + .25f), "NB_Stone");
            for (float z = s.depth - wingDepth + 1.2f; z < s.depth - 1; z += 3.2f)
            {
                for (int floor = 0; floor < 2; floor++)
                    InfillBox(c, s, "Court-facing window", new Vector3(x - side * (wing * .5f + .04f), 1.8f + floor * 3.2f, z), new Vector3(.1f, 1.7f, 1.8f), "NB_Glass");
            }
            InfillBox(c, s, "Podium entrance", new Vector3(x, 1.55f, s.depth - wingDepth - .46f), new Vector3(2f, 3.1f, .14f), "NB_DoorNavy");
            InfillBox(c, s, "Entrance canopy", new Vector3(x, 3.3f, s.depth - wingDepth - 1.1f), new Vector3(wing - .5f, .18f, 1.5f), "NB_Stone");
            InfillPlanter(c, s, side * (s.width * .5f - wing - 2.2f), s.depth * .58f, 2.4f, s.depth * .28f);
            InfillBench(c, s, side * s.width * .2f, s.depth * .17f);
        }
        InfillBox(c, s, "Shared courtyard path", new Vector3(0, .035f, s.depth * .5f), new Vector3(3.1f, .025f, s.depth - .25f), "NB_Concrete");
        if (s.width > 22 && s.depth > 16)
        {
            InfillTree(c, s, -s.width * .17f, s.depth * .32f, .65f);
            InfillTree(c, s, s.width * .17f, s.depth * .78f, .65f);
        }
    }

    static void InfillGarden(Ctx c, CoastalInfillSite s, bool estate)
    {
        InfillFloor(c, s, "NB_Grass");
        InfillBox(c, s, "Garden entrance path", new Vector3(0, .037f, s.depth * .5f), new Vector3(estate ? 2.8f : 1.7f, .03f, s.depth - .3f), "NB_ParkPath");
        InfillBox(c, s, "Shared garden cross path", new Vector3(0, .038f, s.depth * .55f), new Vector3(s.width - .4f, .03f, 1.5f), "NB_ParkPath");
        foreach (int side in new[] { -1, 1 })
        {
            float x = side * s.width * .28f;
            InfillPlanter(c, s, x, s.depth * .3f, Mathf.Max(1.4f, s.width * .2f), Mathf.Max(1.7f, s.depth * .25f));
            InfillBench(c, s, x, s.depth * .63f);
            bool hasShedHere = !estate && s.width > 20 && s.depth > 15 && side > 0;
            if (s.width > 14 && s.depth > 12 && !hasShedHere) InfillTree(c, s, x, s.depth * .81f, estate ? .95f : .65f);
        }
        InfillFence(c, s, estate);
        if (!estate && s.width > 20 && s.depth > 15)
        {
            float x = s.width * .28f, z = s.depth * .82f;
            InfillBox(c, s, "Garden tool shed", new Vector3(x, 1.4f, z), new Vector3(3.4f, 2.8f, 2.7f), "NB_WallSage", true);
            InfillBox(c, s, "Shed roof", new Vector3(x, 2.9f, z), new Vector3(3.8f, .22f, 3.1f), "NB_DoorNavy");
            InfillBox(c, s, "Shed door", new Vector3(x, 1, z - 1.39f), new Vector3(1.1f, 2, .1f), "NB_Wood");
        }
    }

    static void InfillFence(Ctx c, CoastalInfillSite s, bool estate)
    {
        string material = estate ? "NB_Stone" : "NB_Wood";
        float height = estate ? .85f : 1.35f;
        InfillBox(c, s, "Connected rear boundary", new Vector3(0, height * .5f, s.depth - .15f), new Vector3(s.width - .3f, height, .22f), material, true);
        foreach (int side in new[] { -1, 1 })
        {
            InfillBox(c, s, "Connected side boundary", new Vector3(side * (s.width * .5f - .15f), height * .5f, s.depth * .5f), new Vector3(.22f, height, s.depth - .3f), material, true);
            float opening = Mathf.Min(s.kind == "workyard" ? 7f : 3f, s.width * .5f), run = (s.width - opening) * .5f;
            InfillBox(c, s, "Front boundary with entrance", new Vector3(side * (opening * .5f + run * .5f), height * .5f, .15f), new Vector3(run - .15f, height, .22f), material, true);
        }
        // Regular posts give the enclosure an intentional joined perimeter, not floating fence fragments.
        int count = Mathf.Max(2, Mathf.CeilToInt(s.width / 3.5f));
        for (int i = 0; i <= count; i++)
            InfillBox(c, s, "Boundary post", new Vector3(Mathf.Lerp(-s.width * .5f + .15f, s.width * .5f - .15f, i / (float)count), height * .5f + .05f, s.depth - .15f), new Vector3(.28f, height + .1f, .28f), material);
    }

    static void InfillGrove(Ctx c, CoastalInfillSite s)
    {
        // Woodland grows from the existing terrain: no rectangular lawn mat, perimeter fence or formal beds.
        // A modest winding trail separates two deliberately clustered stands; trunks never occupy the trail.
        var path = new[] { new Vector3(0, 0, .55f), new Vector3(s.width * .10f, 0, s.depth * .29f),
            new Vector3(-s.width * .08f, 0, s.depth * .70f), new Vector3(0, 0, s.depth - .55f) };
        var seats = new[] { new Vector3(s.width * .1f + 2.6f, 0, s.depth * .3f),
            new Vector3(-s.width * .08f - 2.6f, 0, s.depth * .7f) };
        for (int i = 1; i < path.Length; i++)
        {
            var a = s.transform.TransformPoint(path[i - 1]); var b = s.transform.TransformPoint(path[i]);
            var rotation = Quaternion.LookRotation((b - a).normalized);
            OBox(c, s.transform, "Woodland through path", (a + b) * .5f + Vector3.up * .028f, rotation,
                new Vector3(1.65f, .027f, Vector3.Distance(a, b) + .12f), c.M("NB_ParkPath"), LayerDefault, false);
        }
        var random = new System.Random(PickupVisuals.StableHash(s.siteId) ^ 93137);
        var trees = new System.Collections.Generic.List<Vector3>();
        int target = Mathf.Clamp(Mathf.RoundToInt(s.width * s.depth / 92f), 6, 18);
        string[] types = { "SM_tree_town_01", "SM_tree_town_02", "SM_tree_town_03" };
        for (int attempt = 0; attempt < 240 && trees.Count < target; attempt++)
        {
            int side = attempt % 2 == 0 ? -1 : 1;
            float x = side * Mathf.Lerp(5.7f, s.width * .5f - 5.4f, (float)random.NextDouble());
            float z = Mathf.Lerp(5.6f, s.depth - 5.6f, (float)random.NextDouble());
            var local = new Vector3(x, 0, z); bool clear = true;
            for (int i = 1; i < path.Length; i++)
                if (CoastalCityLayout.DistanceToSegment(local, path[i - 1], path[i]) < 4.6f) clear = false;
            foreach (var other in trees) if (Vector3.Distance(local, other) < 5.4f) clear = false;
            foreach (var seat in seats) if (Vector3.Distance(local, seat) < 2.3f) clear = false;
            if (!clear) continue;
            float scale = .98f + (float)random.NextDouble() * .25f;
            Tree(c, s.transform, types[trees.Count % types.Length], s.transform.TransformPoint(local), scale);
            trees.Add(local);
            if (trees.Count % 3 == 0)
            {
                // Understory belongs to each grove, not random roadside scatter. It has no blocking collider.
                var shrub = Place(c, Sub + "Nature/SM_bush_02", s.transform,
                    s.transform.TransformPoint(local + new Vector3(side * .7f, 0, .8f)), s.transform.rotation,
                    1.5f, false, LayerDefault);
                if (shrub != null) DisableColliders(shrub);
            }
        }
        foreach (var seat in seats) InfillBench(c, s, seat.x, seat.z);
    }

    static void InfillPlanter(Ctx c, CoastalInfillSite s, float x, float z, float width, float depth)
    {
        InfillBox(c, s, "Raised stone planter", new Vector3(x, .22f, z), new Vector3(width, .44f, depth), "NB_Stone");
        InfillBox(c, s, "Planted hedge", new Vector3(x, .7f, z), new Vector3(width - .18f, .7f, depth - .18f), "NB_Hedge");
    }

    static void InfillBench(Ctx c, CoastalInfillSite s, float x, float z)
    {
        InfillBox(c, s, "Bench seat", new Vector3(x, .47f, z), new Vector3(2.35f, .16f, .65f), "NB_Wood");
        InfillBox(c, s, "Bench back", new Vector3(x, .87f, z + .32f), new Vector3(2.35f, .65f, .12f), "NB_Wood");
        foreach (int side in new[] { -1, 1 })
            InfillBox(c, s, "Bench leg", new Vector3(x + side * .8f, .2f, z), new Vector3(.16f, .4f, .5f), "NB_Iron");
    }

    static void InfillTree(Ctx c, CoastalInfillSite s, float x, float z, float scale)
        => Tree(c, s.transform, "SM_tree_town_03", s.transform.TransformPoint(new Vector3(x, 0, z)), scale);
}
