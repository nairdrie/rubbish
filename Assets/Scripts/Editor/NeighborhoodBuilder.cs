using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>
/// Procedural, deterministic (seeded) builder for the Neighborhood scene: a little morning suburb with
/// a loop of streets the truck can drive around (Maple St / Oak Ave / Birch St / Elm Ave), Depot Rd
/// running south to the RUBBISH CO. depot (the shop's exterior + garage door back to ShopScene), houses
/// on both sides of every street, a park in the middle of the block, and the living trash route: a
/// PickupStop at the curb of EVERY house (stable stopId "{street}-{number}", curb capacity 3–6 by house size,
/// wealth by house size) under one TrashRoute. It also writes Assets/Resources/HouseDirectory.asset (the
/// house list the CurbTrashSpawner uses before the scene is loaded) when building the real scene.
///
/// Everything generated lives under one root ("NeighborhoodGen") that is deleted and rebuilt on every
/// run, so the builder is idempotent. The old hand-made street is moved (not deleted) under a disabled
/// "_OldNeighborhood" root. Layout (world XZ, metres): road centre lines Maple z=60, Birch z=170,
/// Elm x=0, Oak x=160, Depot Rd x=0 from z=12; depot pad x -18..18, z -24..12 with the garage door
/// in the building front at z=-14 facing +Z.
///
/// Levels: asphalt top y=0, grass y=0.10, sidewalks/curbs/driveways y=0.15 (all BoxColliders on
/// layer Ground). Driveways get small ramps at the curb so WheelColliders roll over smoothly.
///
/// DISTRICTS (Docs/Progression.md §5.3): the town is built per district from DistrictSpecs. District 1 (Mildly
/// Pleasant Acres, the loop above) is built FIRST with the original main RNG sequence, so its lots, stopIds, wealth and
/// positions are byte-stable. Then each new district builds with its own RNG (seed ^ StableHash(districtId)), unique
/// street keys and stopId prefix: Minivan Meadows (east, via Minivan Mile from the depot pad, gate x ≈ 206), Snootington
/// Hills (north, Old Money Rd = Elm Ave continued, gate z ≈ 230) and Billionaire Bluffs (far north, Tax Haven Heights Rd
/// continues straight up Old Money Rd, boom barrier z ≈ 460). Each new district is walled in (picket fence / hedge /
/// stone wall) with its DistrictGate as the only road opening. PlayBounds grow to x -60..440, z -40..640; only the
/// nature scatter and the depot fence (a gap for Minivan Mile) change around district 1.
///
/// WORLD LIFE (Docs/WorldLife.md, package A): after the districts and nature, BuildWorldLife adds — on its own RNG
/// (seed ^ StableHash("worldlife")), so every district's sequence and the HouseDirectory are untouched — the three town
/// exits (Old Highway west, Interstate On-Ramp east, Private Airport Rd north: roads, tunnel portals in grassy berms),
/// household cars (the old parked-car draws become CarRec data; one static GameObject per car with a HouseholdCar
/// marker), fire hydrants, road works and lawn flamingos, and marks ~730 street props Knockable (stable ids in build order,
/// primitive rest colliders, one box collider per picket panel). BuildStreetGraph then writes
/// Assets/Resources/StreetGraph.asset (lanes, sidewalks, crosswalks, garden paths, driveways, gates, exits, loops) and a
/// WorldLifeAnchor under NeighborhoodGen points at it. Validate adds the §5.3 graph checks.
///
/// KIDS (Docs/WorldLife.md §12.K): after the graph, BuildKidPens fences the inner-park playground and a few front yards
/// (hard, sunny picket pens with KidPen placement data) for the kids who cheer the truck. No RNG and built after the graph, so
/// the HouseDirectory and StreetGraph.asset are untouched by it.
/// </summary>
public static class NeighborhoodBuilder
{
    public const string DefaultScenePath = "Assets/Scenes/Neighborhood.unity";
    public const int DefaultSeed = 1234;
    public const string RouteName = "Route 1 — Whole Neighborhood";

    [MenuItem("Rubbish/Setup/Neighborhood")]
    public static void Run() => Build(DefaultScenePath, DefaultSeed);

    /// <summary>Skip the town-map re-bake that ends a real-scene build (an agent rebuilding while the map's owner is mid-change
    /// sets this and runs Setup_TownMap later instead). Default false: the bake runs.</summary>
    public static bool SkipTownMapBake;

    // ------------------------------------------------------------------------------------------
    // Constants
    // ------------------------------------------------------------------------------------------

    const string RootName = "NeighborhoodGen";
    const string OldRootName = "_OldNeighborhood";
    const string MatDir = "Assets/Materials/Generated/Neighborhood";
    const string Sub = "Assets/Suburbs Town Pack/PREFABS/";
    const string Gar = "Assets/Garage (Low Poly)/Prefabs/";
    const string SkyboxPath = "Assets/Day-Night Skyboxes/Materials/SkyBrightMorning.mat";
    const string GarageWallMatPath = "Assets/Garage (Low Poly)/Materials/M_wall_a.mat";

    const int LayerDefault = 0, LayerInteractable = 3, LayerGround = 6, LayerWalls = 7;

    const float RoadTop = 0f, GrassTop = 0.10f, WalkTop = 0.15f, SandTop = 0.12f, FarGroundTop = -0.05f, SlabBottom = -0.4f;
    const float RoadHalfWidth = 5f, CurbWidth = 0.3f, WalkWidth = 3f, PropertyLine = 10f;
    const float VergeLampD = 8.7f, VergeTreeD = 9.25f, MailboxD = 8.45f, BinD = 6.15f, StopD = 5.8f;

    const float ElmX = 0f, OakX = 160f, MapleZ = 60f, BirchZ = 170f, DepotRoadStartZ = 12f;
    const float ParkPathX = 80f, DepotFrontZ = -14f;

    static readonly Rect2 DepotPad = new Rect2(-18f, -24f, 18f, 12f);
    /// <summary>The whole town (all four districts). District 1's own area is its old PlayBounds x -60..220, z -40..230.</summary>
    static readonly Rect2 PlayBounds = new Rect2(-60f, -40f, 440f, 640f);
    static readonly Rect2 InnerBlock = new Rect2(ElmX + PropertyLine, MapleZ + PropertyLine, OakX - PropertyLine, BirchZ - PropertyLine);
    static readonly Rect2 ParkRect = new Rect2(35f, 98f, 125f, 132f);

    // ------------------------------------------------------------------------------------------
    // Data types
    // ------------------------------------------------------------------------------------------

    /// <summary>Axis-aligned rectangle on the XZ plane.</summary>
    struct Rect2
    {
        public float x0, z0, x1, z1;
        public Rect2(float ax, float az, float bx, float bz)
        {
            x0 = Mathf.Min(ax, bx); x1 = Mathf.Max(ax, bx);
            z0 = Mathf.Min(az, bz); z1 = Mathf.Max(az, bz);
        }
        public static Rect2 From(Vector3 a, Vector3 b) => new Rect2(a.x, a.z, b.x, b.z);
        public float W => x1 - x0;
        public float D => z1 - z0;
        public Rect2 Expand(float d) => new Rect2(x0 - d, z0 - d, x1 + d, z1 + d);
        public bool Contains(float x, float z) => x >= x0 && x <= x1 && z >= z0 && z <= z1;
        public bool Contains(Rect2 o, float tol) => o.x0 >= x0 - tol && o.x1 <= x1 + tol && o.z0 >= z0 - tol && o.z1 <= z1 + tol;
        public bool Overlaps(Rect2 o, float eps = 0.02f) => x0 < o.x1 - eps && o.x0 < x1 - eps && z0 < o.z1 - eps && o.z0 < z1 - eps;
        public float Distance(float x, float z)
        {
            float dx = Mathf.Max(0f, Mathf.Max(x0 - x, x - x1));
            float dz = Mathf.Max(0f, Mathf.Max(z0 - z, z - z1));
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
        public override string ToString() => $"[x {x0:F1}..{x1:F1}, z {z0:F1}..{z1:F1}]";
    }

    enum Drive { Side, Garage, Front }

    /// <summary>Measured house prefab facts (pivot-relative; houses face local +Z).</summary>
    sealed class HouseDef
    {
        public string prefab;
        public float halfW, zMin, zMax, doorX, doorZ;
        public Drive drive;
        public float garageX, garageZ, garageW;
        public int size; // 0 small, 1 medium, 2 big
        public float weight = 1f;
        public float Depth => zMax - zMin;
    }

    static readonly HouseDef[] Houses =
    {
        new HouseDef { prefab = "SM_house_suburb_001", halfW = 4.12f, zMin = -5.72f, zMax = 5.72f, doorX = 2.15f, doorZ = 2.3f, drive = Drive.Side, size = 0 },
        new HouseDef { prefab = "SM_house_suburb_002", halfW = 4.64f, zMin = -5.62f, zMax = 5.62f, doorX = 2.2f, doorZ = 2.35f, drive = Drive.Side, size = 0 },
        new HouseDef { prefab = "SM_house_suburb_003", halfW = 7.69f, zMin = -5.93f, zMax = 5.93f, doorX = -1.35f, doorZ = 2.3f, drive = Drive.Garage, garageX = 4.05f, garageZ = 2.35f, garageW = 5.1f, size = 1 },
        new HouseDef { prefab = "SM_house_suburb_004", halfW = 8.45f, zMin = -6.85f, zMax = 6.85f, doorX = -2.95f, doorZ = 3.15f, drive = Drive.Garage, garageX = 3.7f, garageZ = 2.6f, garageW = 4.6f, size = 1 },
        new HouseDef { prefab = "SM_house_suburb_005", halfW = 5.46f, zMin = -7.79f, zMax = 7.79f, doorX = 0f, doorZ = 4.0f, drive = Drive.Side, size = 1 },
        new HouseDef { prefab = "SM_house_suburb_006", halfW = 8.95f, zMin = -9.42f, zMax = 9.42f, doorX = 0f, doorZ = 5.9f, drive = Drive.Front, size = 2, weight = 0.8f },
        new HouseDef { prefab = "SM_house_suburb_007", halfW = 8.95f, zMin = -9.42f, zMax = 9.42f, doorX = -5.1f, doorZ = 5.9f, drive = Drive.Front, size = 2, weight = 0.8f },
    };

    /// <summary>One side of one street: a strip of lots plus the verge furniture in front of them.</summary>
    sealed class Frontage
    {
        public string street, key;
        public Vector3 origin, along, normal; // normal points from the road toward the lots
        public float lot0, lot1, furn0, furn1, maxDepth;
        public bool odd, outer;
        public int numberOffset;                       // added to house numbers (two frontages of one street)
        public float lotWidth;                         // new districts: target lot width override (0 = the district's)
        public DistrictSpec district;                  // null = district 1 (legacy pipeline)
        public readonly List<Vector2> reserved = new List<Vector2>(); // along-intervals kept free (park paths)
        public readonly List<Vector2> occupied = new List<Vector2>(); // verge intervals already used

        public Vector3 P(float s, float d, float y) => origin + along * s + normal * d + Vector3.up * y;
        public Quaternion FaceRoad => Quaternion.LookRotation(-normal, Vector3.up);

        public bool VergeFree(float s, float halfWidth)
        {
            foreach (var r in reserved) if (s + halfWidth > r.x && s - halfWidth < r.y) return false;
            foreach (var o in occupied) if (s + halfWidth > o.x && s - halfWidth < o.y) return false;
            return true;
        }
        public void Occupy(float s, float halfWidth) => occupied.Add(new Vector2(s - halfWidth, s + halfWidth));
    }

    sealed class LotInfo
    {
        public Frontage f;
        public int number;
        public HouseDef house;
        public Rect2 lotRect, houseRect;
        public Vector3 stopPos;
        public Quaternion stopRot;
        public string name;
        public Transform bin;                          // the wheelie bin prop (GoldStar sticker at runtime)
        public DistrictSpec district;                  // null = district 1
        public int wealthBand = -1;                    // new districts: index into the spec's wealth bands

        // WorldLife house anchors (recorded while the lot is built; no RNG): along-offsets are relative to the lot centre sc.
        public string stopId;
        public float sc, half, aHouse, doorA, dwC, dwW, dwEnd, frontD, doorD;
        public bool hasDrive, estate;
        public Vector3 mailboxPos;
        public GameObject houseGo;                     // estates: the hall (the door the walk starts from)
        public float walkA;                            // estates: the path runs down the drive's inner edge
        public Pose butlerPost = new Pose(Vector3.zero, Quaternion.identity);
        public readonly List<CarRec> cars = new List<CarRec>();
        public readonly List<HouseholdCar> placedCars = new List<HouseholdCar>();
        public Pose carPose0 = new Pose(Vector3.zero, Quaternion.identity), carPose1 = new Pose(Vector3.zero, Quaternion.identity);
        public string StopId => stopId;
    }

    /// <summary>A household car to park (the old parked-car RNG draws, or the WorldLife post-pass).</summary>
    sealed class CarRec
    {
        public int model;                              // SM_car_town_0{model}
        public string tintKey;                         // c.mats key, null = the prefab's paint
        public Vector3 scale = Vector3.one;
    }

    /// <summary>A prop that becomes Knockable once the town is built (ids are assigned in this order).</summary>
    sealed class KnockRec
    {
        public GameObject go;
        public KnockKind kind;
        public string proxyKey, stopId;
    }

    /// <summary>A town exit (Docs/WorldLife.md §5.4): a short road to the boundary with a tunnel portal 6 m inside it.</summary>
    sealed class ExitSpec
    {
        public string id, name, signText;
        public Vector3 junction, portal, forward;
        public Rect2 road;
        public DistrictSpec district;                  // null = district 1
        public string lampPrefab = "Props/SM_streetlamp_01", treeA = "SM_tree_town_03", treeB = "SM_tree_town_02";
        public float Length => Vector3.Distance(junction, portal);
    }

    enum ColMode { Keep, Box, Pole }

    struct SpawnCheck { public Transform t; public float y; public bool truck; }

    sealed class Ctx
    {
        public Scene scene;
        public System.Random rng;
        public int seed;
        public Transform root, gGround, gMarkings, gLots, gStreet, gPark, gDepot, gNature, gBounds, gSpawns, gRoute;
        public readonly Dictionary<string, Material> mats = new Dictionary<string, Material>();
        public readonly Dictionary<string, GameObject> prefabs = new Dictionary<string, GameObject>();
        public readonly List<Frontage> frontages = new List<Frontage>();
        public readonly List<LotInfo> lots = new List<LotInfo>();
        public readonly List<Rect2> streets = new List<Rect2>();
        public readonly List<Rect2> roads = new List<Rect2>(); // streets + depot pad (asphalt)
        public readonly List<Rect2> developed = new List<Rect2>(); // no scatter nature here
        public readonly List<Vector3> trees = new List<Vector3>();
        public readonly List<PickupStop> stops = new List<PickupStop>();
        public readonly List<SpawnCheck> spawns = new List<SpawnCheck>();
        public readonly HashSet<Transform> dynamicRoots = new HashSet<Transform>();
        public readonly List<string> missing = new List<string>();
        public readonly StringBuilder notes = new StringBuilder();
        public Transform doorZoneT;
        public readonly List<DistrictSpec> districts = new List<DistrictSpec>();
        public readonly List<DistrictGate> gates = new List<DistrictGate>();
        public Transform gDistricts;

        // WorldLife (package A)
        public readonly List<KnockRec> knock = new List<KnockRec>();
        public readonly List<ExitSpec> exits = new List<ExitSpec>();
        public readonly List<Vector3> roadWorks = new List<Vector3>();
        public readonly List<HouseholdCar> householdCars = new List<HouseholdCar>();
        public readonly Dictionary<string, (Mesh mesh, Material[] mats)> propMeshes = new Dictionary<string, (Mesh, Material[])>();
        public Transform gWorldLife;
        public WorldLifeAnchor anchor;
        public StreetGraph graph;
        public readonly StringBuilder graphNotes = new StringBuilder();
        public int gameObjectsBefore = -1;
        public List<HouseDirectory.Entry> directoryBefore;

        public float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);
        public int RI(int a, int bInclusive) => rng.Next(a, bInclusive + 1);
        public bool Chance(float p) => rng.NextDouble() < p;
        public float Sign() => rng.NextDouble() < 0.5 ? -1f : 1f;
        public T Pick<T>(IList<T> list) => list[rng.Next(list.Count)];
        public Material M(string key) => mats[key];
    }

    // ------------------------------------------------------------------------------------------
    // Entry
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Rebuild the suburb in the scene at <paramref name="scenePath"/> (opened additively if needed,
    /// saved, and closed again if it wasn't open). Deterministic for a given seed. Returns the
    /// validation report (also logged).
    /// </summary>
    public static string Build(string scenePath, int seed) => Build(scenePath, seed, null);

    /// <summary>
    /// As <see cref="Build(string,int)"/>; <paramref name="graphAssetPath"/> is where the street graph is written (null:
    /// <see cref="StreetGraph.AssetPath"/> when building the real scene, nowhere for a scratch copy, which still builds,
    /// validates and probes the graph in memory).
    /// </summary>
    public static string Build(string scenePath, int seed, string graphAssetPath)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[NeighborhoodBuilder] Exit play mode first.");
            return "ERROR: play mode";
        }

        var scene = SceneManager.GetSceneByPath(scenePath);
        bool opened = false;
        if (!scene.IsValid() || !scene.isLoaded)
        {
            scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            opened = true;
        }
        var prevActive = SceneManager.GetActiveScene();

        var c = new Ctx { scene = scene, rng = new System.Random(seed), seed = seed };
        string report;
        bool ok = false;
        try
        {
            c.gameObjectsBefore = CountGameObjects(scene);
            c.directoryBefore = SnapshotHouseDirectory();
            SceneManager.SetActiveScene(scene);
            CreateMaterials(c);
            PrepareScene(c);
            DefineDistricts(c);          // pure data (roads, frontages, gates); no RNG
            DefineExits(c);              // pure data (WorldLife: the three town-exit roads); no RNG
            BuildGround(c);
            BuildMarkings(c);
            // ---- district 1 (Mildly Pleasant Acres): the original main-RNG sequence, byte-stable ----
            BuildLots(c);
            BuildStreetFurniture(c);
            BuildPark(c);
            BuildDepot(c);
            BuildRoute(c);
            // ---- districts 2..4: each with its own RNG ----
            BuildDistricts(c);
            BuildNature(c);
            BuildBounds(c);
            // ---- WorldLife (package A): its own RNG, after everything else ----
            BuildWorldLife(c);
            ApplyStatic(c);
            c.graph = BuildStreetGraph(c);
            // ---- WorldLife kids (§12.K): fenced pens, AFTER the graph and with no RNG, so nothing above changes ----
            BuildKidPens(c);
            report = Validate(c);
            string graphPath = graphAssetPath ?? (scenePath == DefaultScenePath ? StreetGraph.AssetPath : null);
            if (graphPath != null)
            {
                var asset = SaveGraphAsset(c.graph, graphPath);
                if (c.anchor != null) c.anchor.graph = asset;
                report += $"[NeighborhoodBuilder] street graph -> {graphPath} ({c.graph.NodeCount} nodes, {c.graph.EdgeCount} edges)\n";
            }
            else report += "[NeighborhoodBuilder] street graph built in memory only (scratch build)\n";
            report += BakeKnockProxies(scene);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            if (scenePath == DefaultScenePath)
                report += Setup_Pickup.WriteHouseDirectory(c.stops, scene.name) + "\n";
            AssetDatabase.SaveAssets();
            // UIPass §10.6 (optional hook): re-bake the town map so the minimap and full map match the rebuilt town
            if (scenePath == DefaultScenePath && SkipTownMapBake)
                report += "[NeighborhoodBuilder] town map: NOT re-baked (SkipTownMapBake): run Rubbish/Setup/Town Map (Setup_TownMap) next\n";
            else if (scenePath == DefaultScenePath)
            {
                try { var mapLog = new StringBuilder(); Setup_TownMap.Bake(mapLog); report += "[NeighborhoodBuilder] town map: " + mapLog.ToString().Replace("\n", " ") + "\n"; }
                catch (Exception mapEx) { report += "[NeighborhoodBuilder] town map bake failed: " + mapEx.Message + "\n"; }
            }
            ok = true;
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            report = "[NeighborhoodBuilder] FAILED: " + e.Message;
        }
        finally
        {
            if (c.graph != null && !AssetDatabase.Contains(c.graph)) Object.DestroyImmediate(c.graph);
            if (prevActive.IsValid() && prevActive.isLoaded && prevActive != scene)
                SceneManager.SetActiveScene(prevActive);
            if (opened) EditorSceneManager.CloseScene(scene, true);
        }

        if (ok)
        {
            if (report.Contains("FAIL")) Debug.LogWarning(report);
            else Debug.Log(report);
        }
        return report;
    }

    // ------------------------------------------------------------------------------------------
    // Scene prep: old content, sun, sky, root
    // ------------------------------------------------------------------------------------------

    static void PrepareScene(Ctx c)
    {
        // Sun: reuse the old directional light (pull it out before the old roots get disabled).
        Light sun = null;
        var sunRoot = FindRoot(c.scene, "Sun");
        if (sunRoot != null) sun = sunRoot.GetComponent<Light>();
        if (sun == null)
        {
            foreach (var r in c.scene.GetRootGameObjects())
            {
                if (r.name == RootName) continue;
                foreach (var l in r.GetComponentsInChildren<Light>(true))
                    if (l.type == LightType.Directional) { sun = l; break; }
                if (sun != null) break;
            }
        }
        if (sun == null)
        {
            var go = new GameObject("Sun");
            sun = go.AddComponent<Light>();
            sun.type = LightType.Directional;
        }
        sun.transform.SetParent(null, true);
        sun.gameObject.name = "Sun";
        sun.gameObject.SetActive(true);
        sun.enabled = true;
        sun.transform.SetPositionAndRotation(new Vector3(80f, 60f, 60f), Quaternion.Euler(38f, -58f, 0f));
        sun.color = new Color(1f, 0.9f, 0.76f);
        sun.intensity = 1.45f;
        sun.shadows = LightShadows.Soft;
        sun.shadowStrength = 0.82f;
        sun.lightmapBakeType = LightmapBakeType.Realtime;

        // Old hand-made street -> disabled holder (kept, not deleted).
        var old = FindRoot(c.scene, OldRootName);
        if (old == null) old = new GameObject(OldRootName);
        foreach (var n in new[] { "ROAD", "Map", "Lighting" })
        {
            var r = FindRoot(c.scene, n);
            if (r != null) r.transform.SetParent(old.transform, true);
        }
        old.SetActive(false);

        // Fresh generated root.
        var existing = FindRoot(c.scene, RootName);
        if (existing != null) Object.DestroyImmediate(existing);
        c.root = new GameObject(RootName).transform;
        c.gGround = Group(c.root, "Ground");
        c.gMarkings = Group(c.root, "Markings");
        c.gLots = Group(c.root, "Lots");
        c.gStreet = Group(c.root, "StreetFurniture");
        c.gPark = Group(c.root, "Park");
        c.gDepot = Group(c.root, "Depot");
        c.gNature = Group(c.root, "Nature");
        c.gBounds = Group(c.root, "Bounds");
        c.gSpawns = Group(c.root, "Spawns");
        c.dynamicRoots.Add(c.gSpawns);

        var atmo = new GameObject("Atmosphere");
        atmo.transform.SetParent(c.root, false);
        atmo.AddComponent<NeighborhoodAtmosphere>();

        // Render settings (they belong to the active scene, which is this one during the build).
        var sky = AssetDatabase.LoadAssetAtPath<Material>(SkyboxPath);
        if (sky == null) c.missing.Add(SkyboxPath);
        RenderSettings.skybox = sky;
        RenderSettings.ambientMode = AmbientMode.Skybox;
        RenderSettings.ambientIntensity = 1f;
        RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
        RenderSettings.reflectionIntensity = 1f;
        RenderSettings.sun = sun;
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = new Color(0.74f, 0.82f, 0.9f);
        RenderSettings.fogStartDistance = 120f;
        RenderSettings.fogEndDistance = 520f;
        // The old bake was a dark night scene; drop it so nothing samples stale lightmaps/probes.
        Lightmapping.lightingDataAsset = null;
        DynamicGI.UpdateEnvironment();
    }

    // ------------------------------------------------------------------------------------------
    // Materials
    // ------------------------------------------------------------------------------------------

    static void CreateMaterials(Ctx c)
    {
        EnsureFolder(MatDir);
        void Add(string key, Color col, float smooth = 0.08f, float metal = 0f) => c.mats[key] = GetOrCreateMat(key, col, smooth, metal);
        Add("NB_Asphalt", new Color(0.21f, 0.22f, 0.24f), 0.12f);
        Add("NB_AsphaltLot", new Color(0.27f, 0.27f, 0.29f), 0.1f);
        Add("NB_Concrete", new Color(0.76f, 0.74f, 0.7f));
        Add("NB_Curb", new Color(0.88f, 0.86f, 0.82f));
        Add("NB_Driveway", new Color(0.64f, 0.62f, 0.59f));
        Add("NB_Grass", new Color(0.42f, 0.64f, 0.27f));
        Add("NB_GrassFar", new Color(0.37f, 0.57f, 0.25f));
        Add("NB_Sand", new Color(0.93f, 0.83f, 0.58f));
        Add("NB_ParkPath", new Color(0.83f, 0.74f, 0.58f));
        Add("NB_LineYellow", new Color(0.98f, 0.8f, 0.16f), 0.2f);
        Add("NB_LineWhite", new Color(0.95f, 0.95f, 0.93f), 0.2f);
        Add("NB_SignYellow", new Color(1f, 0.8f, 0.1f), 0.3f);
        Add("NB_SignGreen", new Color(0.1f, 0.47f, 0.27f), 0.3f);
        Add("NB_Metal", new Color(0.42f, 0.45f, 0.48f), 0.5f, 0.6f);
        Add("NB_Wood", new Color(0.55f, 0.37f, 0.2f));
        Add("NB_EyeWhite", Color.white, 0.85f);
        Add("NB_EyeBlack", new Color(0.02f, 0.02f, 0.03f), 0.9f);
        // District materials (Docs/Progression.md §5.3 looks).
        Add("NB_White", new Color(0.95f, 0.95f, 0.93f), 0.25f);
        Add("NB_Gravel", new Color(0.82f, 0.76f, 0.64f), 0.05f);
        Add("NB_Stone", new Color(0.78f, 0.75f, 0.69f), 0.12f);
        Add("NB_Hedge", new Color(0.18f, 0.42f, 0.17f), 0.05f);
        Add("NB_Water", new Color(0.25f, 0.62f, 0.86f), 0.92f);
        Add("NB_Tennis", new Color(0.22f, 0.5f, 0.38f), 0.15f);
        Add("NB_Gold", new Color(1f, 0.76f, 0.22f), 0.75f, 0.85f);
        Add("NB_GoldRoof", new Color(0.78f, 0.6f, 0.24f), 0.5f, 0.35f);
        Add("NB_Silver", new Color(0.86f, 0.88f, 0.9f), 0.85f, 0.9f);
        Add("NB_Iron", new Color(0.09f, 0.09f, 0.1f), 0.35f, 0.5f);
        Add("NB_Tux", new Color(0.06f, 0.06f, 0.08f), 0.3f);
        Add("NB_Skin", new Color(0.96f, 0.8f, 0.66f), 0.2f);
        Add("NB_Glass", new Color(0.55f, 0.75f, 0.9f), 0.9f, 0.2f);
        Add("NB_Ribbon", new Color(0.9f, 0.08f, 0.14f), 0.45f);
        Add("NB_Orange", new Color(1f, 0.45f, 0.1f), 0.3f);
        Add("NB_Rubber", new Color(0.07f, 0.07f, 0.08f), 0.2f);
        Add("NB_NetBlack", new Color(0.12f, 0.12f, 0.14f), 0.1f);
        Add("NB_TrampBlue", new Color(0.2f, 0.55f, 0.95f), 0.35f);
        Add("NB_DoorNavy", new Color(0.12f, 0.17f, 0.32f), 0.3f);
        Add("NB_VanBeige", new Color(0.84f, 0.76f, 0.6f), 0.62f);
        Add("NB_VanTeal", new Color(0.22f, 0.62f, 0.62f), 0.62f);
        Add("NB_WallPink", new Color(0.98f, 0.76f, 0.82f), 0.12f);
        Add("NB_WallMint", new Color(0.7f, 0.92f, 0.8f), 0.12f);
        Add("NB_WallSky", new Color(0.72f, 0.85f, 0.98f), 0.12f);
        Add("NB_WallLemon", new Color(0.99f, 0.94f, 0.66f), 0.12f);
        Add("NB_WallLilac", new Color(0.85f, 0.78f, 0.96f), 0.12f);
        Add("NB_WallPeach", new Color(1f, 0.84f, 0.7f), 0.12f);
        Add("NB_WallCream", new Color(0.96f, 0.92f, 0.8f), 0.12f);
        Add("NB_WallIvory", new Color(0.97f, 0.96f, 0.91f), 0.12f);
        Add("NB_WallSage", new Color(0.8f, 0.86f, 0.76f), 0.12f);
        Add("NB_WallWhite", new Color(0.97f, 0.97f, 0.96f), 0.2f);
        // WorldLife (package A): exits, household cars, and the derp props (colours = package B's KnockRecipes palette).
        Add("NB_TunnelDark", new Color(0.015f, 0.015f, 0.02f), 0f);
        Add("NB_Berm", new Color(0.38f, 0.6f, 0.25f), 0.05f);
        Add("NB_TownCarBlack", new Color(0.035f, 0.035f, 0.045f), 0.8f, 0.3f);
        Add("NB_LimoWhite", new Color(0.97f, 0.97f, 0.95f), 0.8f, 0.1f);
        Add("NB_KidFence", new Color(0.99f, 0.8f, 0.26f), 0.3f);     // WorldLife kids (§12.K): the sunny pickets of the kid pens
        foreach (var m in KnockRecipes.MaterialNames)
            Add(PropMatKey(m), KnockRecipes.ColorOf(m), m == KnockRecipes.MatMetal ? 0.55f : 0.25f, m == KnockRecipes.MatMetal ? 0.5f : 0f);
        var wall = AssetDatabase.LoadAssetAtPath<Material>(GarageWallMatPath);
        c.mats["DepotWall"] = wall != null ? wall : GetOrCreateMat("NB_DepotWall", new Color(0.55f, 0.57f, 0.6f), 0.1f, 0f);
    }

    static Material GetOrCreateMat(string name, Color col, float smooth, float metal)
    {
        string path = MatDir + "/" + name + ".mat";
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            m = new Material(shader) { name = name };
            AssetDatabase.CreateAsset(m, path);
        }
        else if (shader != null && m.shader != shader) m.shader = shader;
        m.SetColor("_BaseColor", col);
        m.color = col;
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metal);
        m.enableInstancing = true;
        EditorUtility.SetDirty(m);
        return m;
    }

    // ------------------------------------------------------------------------------------------
    // Ground: roads, curbs, sidewalks, grass (cell-grid decomposition, no overlapping tops)
    // ------------------------------------------------------------------------------------------

    static void BuildGround(Ctx c)
    {
        // Streets (asphalt, 10 m wide). Intersections are simply where the rects overlap.
        var maple = new Rect2(ElmX - RoadHalfWidth, MapleZ - RoadHalfWidth, OakX + RoadHalfWidth, MapleZ + RoadHalfWidth);
        var birch = new Rect2(ElmX - RoadHalfWidth, BirchZ - RoadHalfWidth, OakX + RoadHalfWidth, BirchZ + RoadHalfWidth);
        var elm = new Rect2(ElmX - RoadHalfWidth, MapleZ - RoadHalfWidth, ElmX + RoadHalfWidth, BirchZ + RoadHalfWidth);
        var oak = new Rect2(OakX - RoadHalfWidth, MapleZ - RoadHalfWidth, OakX + RoadHalfWidth, BirchZ + RoadHalfWidth);
        var depotRd = new Rect2(ElmX - RoadHalfWidth, DepotRoadStartZ, ElmX + RoadHalfWidth, MapleZ - RoadHalfWidth);
        c.streets.AddRange(new[] { maple, birch, elm, oak, depotRd });
        foreach (var d in c.districts) c.streets.AddRange(d.roads);
        // WorldLife exits are streets too (slabs, sidewalks, corridor checks) but NOT developed land: nature keeps its RNG
        // sequence and BuildWorldLife clears the few trees standing in the corridors afterwards.
        int developedStreets = c.streets.Count;
        foreach (var x in c.exits) c.streets.Add(x.road);
        c.roads.AddRange(c.streets);
        c.roads.AddRange(DepotRowPads());   // Depot Row lot pads (N3); before DepotPad, which Validate expects last
        c.roads.Add(DepotPad);

        // Priority layers: 0 grass < 1 sidewalk < 2 curb < 3 road < 4 depot pad.
        var layers = new List<(Rect2 r, int t)>();
        foreach (var s in c.streets) layers.Add((s.Expand(WalkWidth), 1));
        foreach (var r in c.roads) layers.Add((r.Expand(CurbWidth), 2));
        foreach (var s in c.streets) layers.Add((s, 3));
        layers.Add((DepotPad, 4));
        foreach (var r in DepotRowPads()) layers.Add((r, 4));

        var xs = new SortedSet<float> { PlayBounds.x0, PlayBounds.x1 };
        var zs = new SortedSet<float> { PlayBounds.z0, PlayBounds.z1 };
        foreach (var l in layers)
        {
            xs.Add(Mathf.Clamp(l.r.x0, PlayBounds.x0, PlayBounds.x1)); xs.Add(Mathf.Clamp(l.r.x1, PlayBounds.x0, PlayBounds.x1));
            zs.Add(Mathf.Clamp(l.r.z0, PlayBounds.z0, PlayBounds.z1)); zs.Add(Mathf.Clamp(l.r.z1, PlayBounds.z0, PlayBounds.z1));
        }
        var X = new List<float>(xs);
        var Z = new List<float>(zs);
        int nx = X.Count - 1, nz = Z.Count - 1;
        var grid = new int[nx, nz];
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < nz; j++)
            {
                float cx = 0.5f * (X[i] + X[i + 1]), cz = 0.5f * (Z[j] + Z[j + 1]);
                int t = 0;
                foreach (var l in layers) if (l.t > t && l.r.Contains(cx, cz)) t = l.t;
                grid[i, j] = t;
            }

        // Merge cells: horizontal runs per row, then stack identical runs vertically.
        var rects = new List<(Rect2 r, int t)>();
        var open = new Dictionary<(int, int, int), (int j0, int j1)>();
        for (int j = 0; j < nz; j++)
        {
            var seen = new HashSet<(int, int, int)>();
            int i = 0;
            while (i < nx)
            {
                int t = grid[i, j], i1 = i;
                while (i1 + 1 < nx && grid[i1 + 1, j] == t) i1++;
                var key = (i, i1, t);
                seen.Add(key);
                if (open.TryGetValue(key, out var run) && run.j1 == j - 1) open[key] = (run.j0, j);
                else
                {
                    if (open.TryGetValue(key, out var stale)) rects.Add((new Rect2(X[i], Z[stale.j0], X[i1 + 1], Z[stale.j1 + 1]), t));
                    open[key] = (j, j);
                }
                i = i1 + 1;
            }
            var closed = new List<(int, int, int)>();
            foreach (var kv in open) if (!seen.Contains(kv.Key)) closed.Add(kv.Key);
            foreach (var k in closed)
            {
                var run = open[k];
                rects.Add((new Rect2(X[k.Item1], Z[run.j0], X[k.Item2 + 1], Z[run.j1 + 1]), k.Item3));
                open.Remove(k);
            }
        }
        foreach (var kv in open)
            rects.Add((new Rect2(X[kv.Key.Item1], Z[kv.Value.j0], X[kv.Key.Item2 + 1], Z[kv.Value.j1 + 1]), kv.Key.Item3));

        int n = 0;
        foreach (var (r, t) in rects)
        {
            n++;
            switch (t)
            {
                case 0: Slab(c, c.gGround, "Grass_" + n, r, GrassTop, c.M("NB_Grass")); break;
                case 1: Slab(c, c.gGround, "Sidewalk_" + n, r, WalkTop, c.M("NB_Concrete")); break;
                case 2: Slab(c, c.gGround, "Curb_" + n, r, WalkTop, c.M("NB_Curb")); break;
                case 3: Slab(c, c.gGround, "Road_" + n, r, RoadTop, c.M("NB_Asphalt")); break;
                default: Slab(c, c.gGround, "DepotPad_" + n, r, RoadTop, c.M("NB_AsphaltLot")); break;
            }
        }

        // Horizon ground under everything (below the playable plateau).
        var far = Slab(c, c.gGround, "FarGround", new Rect2(-1420f, -1400f, 1580f, 1600f), FarGroundTop, c.M("NB_GrassFar"));
        far.transform.position = new Vector3(far.transform.position.x, FarGroundTop - 0.5f, far.transform.position.z);
        far.transform.localScale = new Vector3(far.transform.localScale.x, 1f, far.transform.localScale.z);

        // Developed areas (no wild nature): street corridors out to the property line, inner block, depot.
        for (int i = 0; i < developedStreets; i++) c.developed.Add(c.streets[i].Expand(PropertyLine - RoadHalfWidth + 0.5f));
        c.developed.Add(InnerBlock);
        c.developed.Add(DepotPad.Expand(5f));
        c.notes.AppendLine($"ground slabs: {rects.Count}");
    }

    // ------------------------------------------------------------------------------------------
    // Road markings, crosswalks, signs
    // ------------------------------------------------------------------------------------------

    static void BuildMarkings(Ctx c)
    {
        var g = c.gMarkings;
        var skipMaple = new[] { ParkPathX };
        DashLine(c, g, "Maple", new Vector3(ElmX + RoadHalfWidth, 0, MapleZ), new Vector3(OakX - RoadHalfWidth, 0, MapleZ), 5.6f, 5.6f, skipMaple);
        DashLine(c, g, "Birch", new Vector3(ElmX + RoadHalfWidth, 0, BirchZ), new Vector3(OakX - RoadHalfWidth, 0, BirchZ), 5.6f, 5.6f, skipMaple);
        DashLine(c, g, "Elm", new Vector3(ElmX, 0, MapleZ + RoadHalfWidth), new Vector3(ElmX, 0, BirchZ - RoadHalfWidth), 5.6f, 5.6f, null);
        DashLine(c, g, "Oak", new Vector3(OakX, 0, MapleZ + RoadHalfWidth), new Vector3(OakX, 0, BirchZ - RoadHalfWidth), 5.6f, 5.6f, null);
        DashLine(c, g, "DepotRd", new Vector3(ElmX, 0, DepotRoadStartZ), new Vector3(ElmX, 0, MapleZ - RoadHalfWidth), 2f, 5.6f, null);

        // Zebra crossings at every intersection leg + mid-block at the park path.
        const float cw = RoadHalfWidth + 2.2f;
        var crossings = new (Vector3 p, float yaw)[]
        {
            (new Vector3(ElmX + cw, 0, MapleZ), 0f), (new Vector3(ElmX, 0, MapleZ + cw), 90f), (new Vector3(ElmX, 0, MapleZ - cw), 90f),
            (new Vector3(OakX - cw, 0, MapleZ), 0f), (new Vector3(OakX, 0, MapleZ + cw), 90f),
            (new Vector3(OakX, 0, BirchZ - cw), 90f), (new Vector3(OakX - cw, 0, BirchZ), 0f),
            (new Vector3(ElmX + cw, 0, BirchZ), 0f), (new Vector3(ElmX, 0, BirchZ - cw), 90f),
            (new Vector3(ParkPathX, 0, MapleZ), 0f), (new Vector3(ParkPathX, 0, BirchZ), 0f),
        };
        foreach (var (p, yaw) in crossings)
        {
            var z = Place(c, Sub + "Modular/SM_pedestrian_crossing", g, p + Vector3.up * 0.006f, Yaw(yaw), 1f, false, LayerDefault);
            DisableColliders(z);
            NoShadows(z);
        }

        // Stop signs at the T (Depot Rd / Maple St / Elm Ave), facing the approaching traffic.
        var stop = Sub + "Signs/SM_roadsign_011";
        KnockPrefab(c, Place(c, stop, g, new Vector3(ElmX + 6.3f, WalkTop, MapleZ - RoadHalfWidth - 5.1f), Yaw(180f), 1f, false, LayerDefault), KnockKind.Sign, null, ColMode.Box, "stopsign"); // northbound Depot Rd
        KnockPrefab(c, Place(c, stop, g, new Vector3(ElmX + RoadHalfWidth + 5.0f, WalkTop, MapleZ + 6.3f), Yaw(90f), 1f, false, LayerDefault), KnockKind.Sign, null, ColMode.Box, "stopsign");    // westbound Maple
        KnockPrefab(c, Place(c, stop, g, new Vector3(ElmX - 6.3f, WalkTop, MapleZ + RoadHalfWidth + 5.0f), Yaw(0f), 1f, false, LayerDefault), KnockKind.Sign, null, ColMode.Box, "stopsign");    // southbound Elm
        // Stop bars for the three approaches.
        OBox(c, g, "StopBar_DepotRd", new Vector3(ElmX + 2.5f, 0.006f, MapleZ - RoadHalfWidth - 4.4f), Quaternion.identity, new Vector3(4.6f, 0.02f, 0.45f), c.M("NB_LineWhite"), LayerDefault, false);
        OBox(c, g, "StopBar_Maple", new Vector3(ElmX + RoadHalfWidth + 4.4f, 0.006f, MapleZ + 2.5f), Quaternion.identity, new Vector3(0.45f, 0.02f, 4.6f), c.M("NB_LineWhite"), LayerDefault, false);
        OBox(c, g, "StopBar_Elm", new Vector3(ElmX - 2.5f, 0.006f, MapleZ + RoadHalfWidth + 4.4f), Quaternion.identity, new Vector3(4.6f, 0.02f, 0.45f), c.M("NB_LineWhite"), LayerDefault, false);

        // Street name signs on the corners.
        StreetSign(c, g, new Vector3(ElmX + 6.4f, WalkTop, MapleZ + 6.4f), "Maple St", "Elm Ave");
        StreetSign(c, g, new Vector3(ElmX + 6.4f, WalkTop, MapleZ - 6.6f), "Maple St", "Depot Rd");
        StreetSign(c, g, new Vector3(OakX - 6.4f, WalkTop, MapleZ + 6.4f), "Maple St", "Oak Ave");
        StreetSign(c, g, new Vector3(OakX - 6.4f, WalkTop, BirchZ - 6.4f), "Birch St", "Oak Ave");
        StreetSign(c, g, new Vector3(ElmX + 6.4f, WalkTop, BirchZ - 6.4f), "Birch St", "Elm Ave");

        // Derpy warning sign on Depot Rd for traffic heading to the depot (faces northbound... er, you).
        var caution = new GameObject("CautionSign_DepotRd").transform;
        caution.SetParent(g, false);
        var cp = new Vector3(ElmX + RoadHalfWidth + 3.4f, GrassTop, DepotRoadStartZ + 30f);
        caution.position = cp;
        OBox(c, caution, "Post", cp + Vector3.up * 1.2f, Quaternion.identity, new Vector3(0.09f, 2.4f, 0.09f), c.M("NB_Metal"), LayerDefault, true);
        OBox(c, caution, "Board", cp + Vector3.up * 2.5f, Quaternion.Euler(0f, 0f, 45f), new Vector3(1.3f, 1.3f, 0.05f), c.M("NB_SignYellow"), LayerDefault, true);
        Label(caution, "Text", "CAUTION\n<size=55%>GARBAGE TRUCK\n(it's you)</size>", new Vector3(0f, 2.45f, -0.045f), Quaternion.identity, new Vector2(1.2f, 0.75f), Color.black);
        Knock(c, caution.gameObject, KnockKind.Sign, "cautionsign", null);
    }

    static void DashLine(Ctx c, Transform g, string name, Vector3 a, Vector3 b, float marginA, float marginB, float[] skipX)
    {
        Vector3 dir = b - a;
        float len = dir.magnitude;
        dir /= len;
        var rot = Quaternion.LookRotation(dir);
        const float dash = 3f, gap = 3f;
        var mat = c.M("NB_LineYellow");
        for (float t = marginA; t + dash <= len - marginB + 0.01f; t += dash + gap)
        {
            Vector3 p = a + dir * (t + dash * 0.5f);
            bool skip = false;
            if (skipX != null) foreach (var sx in skipX) if (Mathf.Abs(p.x - sx) < 4.5f) skip = true;
            if (skip) continue;
            OBox(c, g, name + "_Dash", p + Vector3.up * 0.005f, rot, new Vector3(0.18f, 0.02f, dash), mat, LayerDefault, false);
        }
    }

    static void StreetSign(Ctx c, Transform g, Vector3 pos, string nameX, string nameZ)
    {
        var root = new GameObject("StreetSign_" + nameX.Split(' ')[0] + "_" + nameZ.Split(' ')[0]).transform;
        root.SetParent(g, false);
        root.position = pos;
        // Post ends under the blades so it never covers the lettering.
        OBox(c, root, "Post", pos + Vector3.up * 1.31f, Quaternion.identity, new Vector3(0.09f, 2.62f, 0.09f), c.M("NB_Metal"), LayerDefault, true);
        OBox(c, root, "Cap", pos + Vector3.up * 2.64f, Quaternion.identity, new Vector3(0.16f, 0.05f, 0.16f), c.M("NB_Metal"), LayerDefault, false);
        SignBoard(c, root, "Board_" + nameX, pos + Vector3.up * 2.85f, Quaternion.identity, new Vector3(1.9f, 0.36f, 0.05f), c.M("NB_SignGreen"), nameX, Color.white, true);
        SignBoard(c, root, "Board_" + nameZ, pos + Vector3.up * 3.22f, Yaw(90f), new Vector3(1.9f, 0.36f, 0.05f), c.M("NB_SignGreen"), nameZ, Color.white, true);
        Knock(c, root.gameObject, KnockKind.Sign, "streetsign", null);
    }

    /// <summary>A flat board (cube) with TMP text on its front (and back if twoSided). Front = +Z of rot.</summary>
    static Transform SignBoard(Ctx c, Transform parent, string name, Vector3 center, Quaternion rot, Vector3 size, Material mat, string text, Color textColor, bool twoSided, int layer = LayerDefault)
    {
        var pivot = new GameObject(name).transform;
        pivot.SetParent(parent, false);
        pivot.SetPositionAndRotation(center, rot);
        var board = OBox(c, pivot, "Board", center, rot, size, mat, layer, true);
        float off = size.z * 0.5f + 0.012f;
        var textSize = new Vector2(size.x * 0.92f, size.y * 0.86f);
        Label(pivot, "Text", text, new Vector3(0, 0, off), Quaternion.Euler(0, 180f, 0), textSize, textColor);
        if (twoSided) Label(pivot, "TextBack", text, new Vector3(0, 0, -off), Quaternion.identity, textSize, textColor);
        return board.transform;
    }

    /// <summary>World-space TMP label. With identity local rotation it reads from local -Z.</summary>
    static TextMeshPro Label(Transform parent, string name, string text, Vector3 localPos, Quaternion localRot, Vector2 size, Color color, FontStyles style = FontStyles.Bold)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localRotation = localRot;
        var t = go.AddComponent<TextMeshPro>();
        t.text = text;
        t.color = color;
        t.fontStyle = style;
        t.alignment = TextAlignmentOptions.Center;
        t.textWrappingMode = TextWrappingModes.NoWrap;
        t.enableAutoSizing = true;
        t.fontSizeMin = 0.5f;
        t.fontSizeMax = 72f;
        t.rectTransform.sizeDelta = size;
        t.ForceMeshUpdate();
        return t;
    }

    // ------------------------------------------------------------------------------------------
    // Lots and houses
    // ------------------------------------------------------------------------------------------

    static void MakeFrontages(Ctx c)
    {
        var X = Vector3.right; var Zf = Vector3.forward;
        Frontage F(string street, string key, Vector3 origin, Vector3 along, Vector3 normal, float l0, float l1, float f0, float f1, float depth, bool odd, bool outer)
        {
            var f = new Frontage { street = street, key = key, origin = origin, along = along, normal = normal, lot0 = l0, lot1 = l1, furn0 = f0, furn1 = f1, maxDepth = depth, odd = odd, outer = outer };
            c.frontages.Add(f);
            return f;
        }
        var parkGap = new Vector2(ParkPathX - 4f, ParkPathX + 4f);
        // Maple St (z = 60): north side is inside the loop (even numbers), south side outside (odd).
        F("Maple St", "maple", new Vector3(ElmX, 0, MapleZ), X, Zf, 10f, 150f, 8.5f, 151.5f, 28f, false, false).reserved.Add(parkGap);
        F("Maple St", "maple", new Vector3(ElmX, 0, MapleZ), X, -Zf, 10f, 156f, 10f, 165f, 32f, true, true);
        // Oak Ave (x = 160): east side outside (even), west side inside (odd, one big lot by the park).
        F("Oak Ave", "oak", new Vector3(OakX, 0, MapleZ), Zf, X, -5f, 115f, -5f, 115f, 34f, false, true);
        F("Oak Ave", "oak", new Vector3(OakX, 0, MapleZ), Zf, -X, 38f, 72f, 10f, 100f, 25f, true, false);
        // Birch St (z = 170): south side inside (odd), north side outside (even).
        F("Birch St", "birch", new Vector3(ElmX, 0, BirchZ), X, -Zf, 10f, 150f, 8.5f, 151.5f, 28f, true, false).reserved.Add(parkGap);
        F("Birch St", "birch", new Vector3(ElmX, 0, BirchZ), X, Zf, 10f, 150f, -5f, 165f, 34f, false, true);
        // Elm Ave (x = 0): west side outside (odd), east side inside (even).
        F("Elm Ave", "elm", new Vector3(ElmX, 0, MapleZ), Zf, -X, -5f, 115f, -5f, 115f, 34f, true, true);
        F("Elm Ave", "elm", new Vector3(ElmX, 0, MapleZ), Zf, X, 38f, 72f, 10f, 100f, 25f, false, false);
        // Depot Rd (x = 0, z 12..55): houses on the west side, verge furniture on both.
        F("Depot Rd", "depot", new Vector3(ElmX, 0, DepotRoadStartZ), Zf, -X, 4f, 40f, 2f, 42f, 34f, true, true);
        F("Depot Rd", "depot", new Vector3(ElmX, 0, DepotRoadStartZ), Zf, X, 0f, 0f, 2f, 38f, 0f, false, true);
    }

    static void BuildLots(Ctx c)
    {
        MakeFrontages(c);
        foreach (var f in c.frontages)
        {
            if (f.lot1 - f.lot0 < 10f) continue;
            // Split the lot range around reserved gaps.
            var segs = new List<Vector2> { new Vector2(f.lot0, f.lot1) };
            foreach (var r in f.reserved)
            {
                var next = new List<Vector2>();
                foreach (var s in segs)
                {
                    if (r.y <= s.x || r.x >= s.y) { next.Add(s); continue; }
                    if (r.x > s.x) next.Add(new Vector2(s.x, r.x));
                    if (r.y < s.y) next.Add(new Vector2(r.y, s.y));
                }
                segs = next;
            }
            HouseDef prev = null;
            foreach (var seg in segs)
            {
                float L = seg.y - seg.x;
                int n = Mathf.Max(1, Mathf.FloorToInt(L / 21.5f));
                float w = L / n;
                for (int i = 0; i < n; i++)
                {
                    var h = PickHouse(c, f, w, prev);
                    if (h == null) continue;
                    prev = h;
                    BuildLot(c, f, seg.x + i * w, seg.x + (i + 1) * w, h, i == n - 1);
                }
            }
        }
    }

    static HouseDef PickHouse(Ctx c, Frontage f, float lotW, HouseDef prev)
    {
        var options = new List<HouseDef>();
        float total = 0f;
        foreach (var h in Houses)
        {
            if (6f + h.Depth + 1.5f > f.maxDepth) continue;
            float need = h.drive == Drive.Side ? 2f * h.halfW + 6.3f : 2f * h.halfW + 2.4f;
            if (need > lotW) continue;
            if (h == prev && Houses.Length > 2) continue;
            options.Add(h);
            total += h.weight;
        }
        if (options.Count == 0) return null;
        float roll = c.R(0f, total);
        foreach (var h in options) { roll -= h.weight; if (roll <= 0f) return h; }
        return options[options.Count - 1];
    }

    static int HouseNumber(Frontage f, float sCenter)
    {
        int n = 2 * Mathf.RoundToInt(sCenter / 5f);
        if (n <= 0) n = 2;
        return (f.odd ? n + 1 : n) + f.numberOffset;
    }

    static void BuildLot(Ctx c, Frontage f, float s0, float s1, HouseDef h, bool lastInSegment)
    {
        float w = s1 - s0, sc = 0.5f * (s0 + s1), half = w * 0.5f;
        int number = HouseNumber(f, sc);
        string stopId = $"{f.key}-{number}";
        var lot = new GameObject($"Lot_{f.key}_{number}").transform;
        lot.SetParent(c.gLots, false);
        lot.position = f.P(sc, PropertyLine, GrassTop);

        bool mirror = c.Chance(0.5f);
        float setback = Mathf.Min(c.R(6f, 8f), f.maxDepth - h.Depth - 1.5f);
        float frontD = PropertyLine + setback;
        float centerD = frontD + h.zMax;
        float backD = frontD + h.Depth;
        float lotBackD = PropertyLine + f.maxDepth;
        Quaternion faceRoad = f.FaceRoad;
        float sx = mirror ? -1f : 1f;
        float alongSign = Mathf.Sign(Vector3.Dot(faceRoad * Vector3.right, f.along)) * sx; // house local +X -> along
        float doorRel = alongSign * h.doorX;

        // --- house position + driveway layout (all along-offsets relative to lot centre) ---
        float aHouse, dwC, dwW, dwEnd;
        Drive mode = h.drive;
        if (mode == Drive.Side)
        {
            float side = Mathf.Abs(doorRel) > 0.3f ? -Mathf.Sign(doorRel) : c.Sign();
            dwW = 3.6f;
            dwC = side * (half - 0.9f - dwW * 0.5f);
            aHouse = dwC - side * (dwW * 0.5f + 0.9f + h.halfW);
            float farEdge = aHouse - side * h.halfW;
            if (Mathf.Abs(farEdge) > half - 1f) aHouse = -side * (half - 1f - h.halfW);
            dwEnd = frontD + Mathf.Min(8f, h.Depth * 0.6f);
            if (Mathf.Abs(dwC - aHouse) < dwW * 0.5f + h.halfW + 0.3f) mode = Drive.Front; // doesn't fit: fall back
        }
        else
        {
            aHouse = 0f; dwC = 0f; dwW = 0f; dwEnd = 0f;
        }
        if (mode != Drive.Side)
        {
            float slack = Mathf.Max(0f, half - h.halfW - 1.2f);
            aHouse = c.R(-1f, 1f) * Mathf.Min(1.5f, slack);
            if (mode == Drive.Garage)
            {
                dwW = h.garageW + 0.6f;
                dwC = aHouse + alongSign * h.garageX;
                dwEnd = centerD - h.garageZ + 0.1f;
            }
            else
            {
                float side = Mathf.Abs(doorRel) > 0.3f ? -Mathf.Sign(doorRel) : c.Sign();
                dwW = 3.8f;
                dwC = aHouse + side * (h.halfW - 3.0f);
                dwEnd = frontD + 0.3f;
            }
        }
        float doorA = aHouse + doorRel;

        // --- house ---
        var housePos = f.P(sc + aHouse, centerD, GrassTop);
        var house = Place(c, Sub + "Buildings/" + h.prefab, lot, housePos, faceRoad, 1f, mirror, LayerGround);
        var hr = Rect2.From(f.P(sc + aHouse - h.halfW, frontD, 0), f.P(sc + aHouse + h.halfW, backD, 0));
        var lr = Rect2.From(f.P(s0, PropertyLine, 0), f.P(s1, lotBackD, 0));

        // --- driveway, curb ramp, garden path ---
        if (dwEnd - 8f > 1.5f)
        {
            float mid = 0.5f * (8f + dwEnd);
            OBox(c, lot, "Driveway", f.P(sc + dwC, mid, 0.5f * (WalkTop + SlabBottom)), faceRoad,
                 new Vector3(dwW, WalkTop - SlabBottom, dwEnd - 8f), c.M("NB_Driveway"), LayerGround, true, false);
            CurbRamp(c, lot, f, sc + dwC, dwW + 1.2f);
            f.Occupy(sc + dwC, dwW * 0.5f + 0.8f);
        }
        float pathEnd = Mathf.Max(frontD + 0.3f, centerD - h.doorZ - 0.2f);
        if (pathEnd - 8f > 1f)
        {
            float mid = 0.5f * (8f + pathEnd);
            var path = Place(c, Sub + "Modular/SM_path_garden", lot, f.P(sc + doorA, mid, GrassTop), faceRoad, 1f, false, LayerGround);
            if (path != null)
            {
                var ls = path.transform.localScale;
                path.transform.localScale = new Vector3(ls.x * 1.15f, ls.y, (pathEnd - 8f) / 6.36f);
                DisableColliders(path);
                NoShadows(path);
                var bc = path.AddComponent<BoxCollider>();
                bc.center = new Vector3(0f, 0f, 0f);
                bc.size = new Vector3(1f, 0.1f / Mathf.Max(0.01f, ls.y), 6.36f);
            }
            f.Occupy(sc + doorA, 1.0f);
        }

        // --- bin + pickup stop spot on the sidewalk, mailbox on the verge ---
        float bs = dwC >= 0f ? -1f : 1f; // toward the lot centre from the driveway
        float binA = dwC + bs * (dwW * 0.5f + 1.0f);
        float stopA = binA + bs * 1.8f;
        if (Mathf.Abs(stopA) > half - 1.2f)
        {
            bs = -bs;
            binA = dwC + bs * (dwW * 0.5f + 1.0f);
            stopA = binA + bs * 1.8f;
        }
        var bin = Place(c, Sub + "Props/SM_garbagecan_0" + c.RI(1, 3), lot, f.P(sc + binA, BinD, WalkTop), faceRoad * Yaw(180f + c.R(-10f, 10f)), 1f, false, LayerDefault);
        KnockPrefab(c, bin, KnockKind.WheelieBin, stopId, ColMode.Box);
        float mbA = dwC - bs * (dwW * 0.5f + 0.7f);
        if (Mathf.Abs(mbA) > half - 0.4f) mbA = dwC + bs * (dwW * 0.5f + 0.7f) + bs * 0.9f;
        KnockPrefab(c, Place(c, Sub + "Props/SM_mailbox_0" + c.RI(1, 2), lot, f.P(sc + mbA, MailboxD, GrassTop), faceRoad, 1f, false, LayerDefault), KnockKind.Mailbox, stopId, ColMode.Box);
        f.Occupy(sc + mbA, 0.7f);

        // --- bushes along the house front ---
        int bushes = c.RI(2, 4);
        var bushAs = new List<float>();
        for (int k = 0, tries = 0; k < bushes && tries < 20; tries++)
        {
            float a = aHouse + c.R(-h.halfW + 0.9f, h.halfW - 0.9f);
            if (Mathf.Abs(a - doorA) < 1.7f) continue;
            if (dwW > 0f && Mathf.Abs(a - dwC) < dwW * 0.5f + 1.1f) continue;
            bool near = false;
            foreach (var b in bushAs) if (Mathf.Abs(b - a) < 1.6f) near = true;
            if (near) continue;
            bushAs.Add(a);
            k++;
            string bush = c.Chance(0.6f) ? "Nature/SM_bush_02" : "Nature/SM_bush_03";
            Place(c, Sub + bush, lot, f.P(sc + a, frontD - c.R(0.7f, 1.3f), GrassTop), Yaw(c.R(0f, 360f)), c.R(0.8f, 1.25f), false, LayerDefault);
        }

        // --- front-yard tree ---
        if (c.Chance(0.35f))
        {
            float side = dwC >= 0f ? -1f : 1f;
            float a = side * (half - c.R(2.8f, 3.8f));
            if (Mathf.Abs(a - doorA) > 3f && (dwW <= 0f || Mathf.Abs(a - dwC) > dwW * 0.5f + 2.5f))
                Tree(c, lot, c.Chance(0.5f) ? "SM_tree_town_03" : "SM_tree_town_02", f.P(sc + a, PropertyLine + setback * c.R(0.35f, 0.55f), GrassTop), c.R(0.55f, 0.7f));
        }

        // --- parked car: the RNG draws stay (byte-stable); BuildHouseholdCars parks it nose-in as a WorldLife household car ---
        CarRec parked = null;
        if (dwW > 0f && dwEnd - 8f >= 7f && c.Chance(0.45f))
        {
            int model = c.RI(1, 4);
            c.Chance(0.5f);              // the old parking-direction roll (household cars park nose-in)
            parked = new CarRec { model = model };
        }

        // --- fences ---
        if (!lastInSegment && c.Chance(0.35f))
        {
            Vector3 a = f.P(s1, frontD + 2.5f, GrassTop), b = f.P(s1, lotBackD - 0.3f, GrassTop);
            FenceLine(c, lot, a, b);
        }
        if (c.Chance(f.outer ? 0.55f : 0.75f))
        {
            Vector3 a = f.P(s0 + 0.3f, lotBackD - 0.25f, GrassTop), b = f.P(s1 - 0.3f, lotBackD - 0.25f, GrassTop);
            FenceLine(c, lot, a, b);
        }

        // --- backyard extras ---
        float yard = lotBackD - backD;
        if (h.size >= 1 && f.outer && yard >= 8.5f && half - Mathf.Abs(aHouse) > 1f && c.Chance(0.3f))
        {
            float a = Mathf.Clamp(aHouse + c.R(-2f, 2f), -half + 4.6f, half - 4.6f);
            Place(c, Sub + "Props/SM_swimming_pool", lot, f.P(sc + a, backD + 1.2f + 2.8f, GrassTop), faceRoad, 1f, false, LayerGround);
        }
        else if (yard >= 4f && c.Chance(0.35f))
        {
            float a = Mathf.Clamp(aHouse + c.R(-3f, 3f), -half + 2.6f, half - 2.6f);
            KnockPrefab(c, Place(c, Sub + "Props/SM_clothes_drying_rack_0" + c.RI(1, 4), lot, f.P(sc + a, backD + 2f, GrassTop), faceRoad, 1f, false, LayerDefault), KnockKind.DryingRack, stopId, ColMode.Box);
        }

        // --- bookkeeping ---
        var info = new LotInfo
        {
            f = f, number = number, house = h, lotRect = lr, houseRect = hr, name = lot.name,
            stopPos = f.P(sc + stopA, StopD, WalkTop), stopRot = faceRoad, bin = bin != null ? bin.transform : null,
            stopId = stopId, sc = sc, half = half, aHouse = aHouse, doorA = doorA, dwC = dwC, dwW = dwW, dwEnd = dwEnd,
            frontD = frontD, doorD = centerD - h.doorZ, hasDrive = dwEnd - 8f > 1.5f, mailboxPos = f.P(sc + mbA, MailboxD, GrassTop),
            houseGo = house,
        };
        if (parked != null) info.cars.Add(parked);
        c.lots.Add(info);
        c.developed.Add(lr.Expand(0.5f));
        _ = house;
        _ = bin;
    }

    /// <summary>Concrete wedge in the road at the curb so wheels roll up onto a driveway.</summary>
    static void CurbRamp(Ctx c, Transform parent, Frontage f, float s, float width)
    {
        const float run = 1.1f, t = 0.25f;
        Vector3 low = f.P(s, RoadHalfWidth - run, RoadTop);
        Vector3 high = f.P(s, RoadHalfWidth, WalkTop);
        Vector3 dir = (high - low).normalized;
        var rot = Quaternion.LookRotation(dir, Vector3.up);
        Vector3 up = rot * Vector3.up;
        Vector3 center = (low + high) * 0.5f - up * (t * 0.5f);
        OBox(c, parent, "CurbRamp", center, rot, new Vector3(width, t, (high - low).magnitude), c.M("NB_Asphalt"), LayerGround, true, false);
    }

    static void FenceLine(Ctx c, Transform parent, Vector3 a, Vector3 b)
    {
        const float segLen = 11.58f;
        Vector3 d = b - a; d.y = 0f;
        float len = d.magnitude;
        if (len < 2f) return;
        Vector3 v = d / len;
        int n = Mathf.Max(1, Mathf.CeilToInt(len / segLen));
        float each = len / n;
        var rot = Quaternion.LookRotation(v) * Yaw(-90f); // local X along the line
        for (int i = 0; i < n; i++)
        {
            var go = Place(c, Sub + "Modular/SM_garden_fence_long", parent, a + v * (each * (i + 0.5f)), rot, 1f, false, LayerGround);
            if (go != null)
            {
                var s = go.transform.localScale;
                go.transform.localScale = new Vector3(s.x * each / segLen, s.y, s.z);
            }
        }
    }

    // ------------------------------------------------------------------------------------------
    // Street furniture: lamps + street trees on the verges
    // ------------------------------------------------------------------------------------------

    static void BuildStreetFurniture(Ctx c)
    {
        FurnishFrontages(c, c.frontages, c.gStreet, "Props/SM_streetlamp_01", "SM_tree_town_03", "SM_tree_town_02", out int lamps, out int trees);
        c.notes.AppendLine($"street lamps: {lamps}, street trees: {trees}");
    }

    /// <summary>Lamps every ~28 m (staggered by side) and street trees every ~13 m on the free verge of each frontage.
    /// District 1 calls this with its original arguments, so its RNG sequence is unchanged.</summary>
    static void FurnishFrontages(Ctx c, IEnumerable<Frontage> list, Transform root, string lampPrefab, string treeA, string treeB,
        out int lamps, out int trees)
    {
        lamps = 0; trees = 0;
        foreach (var f in list)
        {
            var g = Group(root, $"{f.key}_{(f.odd ? "odd" : "even")}_{(f.outer ? "outer" : "inner")}");
            // Lamps every ~28 m, staggered between the two sides of a street.
            float phase = f.odd ? 18f : 4f;
            var lampRot = Quaternion.LookRotation(f.normal) * Yaw(-90f); // arm (-X) reaches over the road
            for (float s = f.furn0 + phase; s <= f.furn1 - 1f; s += 28f)
            {
                float best = float.NaN;
                for (int k = 0; k < 13; k++)
                {
                    float trial = s + (k % 2 == 0 ? 1 : -1) * (k / 2) * 0.8f;
                    if (trial < f.furn0 || trial > f.furn1) continue;
                    if (f.VergeFree(trial, 1.2f)) { best = trial; break; }
                }
                if (float.IsNaN(best)) continue;
                KnockPrefab(c, Place(c, Sub + lampPrefab, g, f.P(best, VergeLampD, GrassTop), lampRot, 1f, false, LayerDefault), KnockKind.Lamp, null, ColMode.Pole);
                f.Occupy(best, 1.6f);
                lamps++;
            }
            // Street trees every ~13 m where the verge is free.
            for (float s = f.furn0 + c.R(3f, 6f); s <= f.furn1 - 2f; s += c.R(9f, 13f))
            {
                if (!f.VergeFree(s, 1.9f)) continue;
                Tree(c, g, c.Chance(0.55f) ? treeA : treeB, f.P(s, VergeTreeD, GrassTop), c.R(0.68f, 0.85f));
                f.Occupy(s, 2.2f);
                trees++;
            }
        }
    }

    // ------------------------------------------------------------------------------------------
    // Park in the middle of the block
    // ------------------------------------------------------------------------------------------

    static void BuildPark(Ctx c)
    {
        var g = c.gPark;
        float pz = 0.5f * (ParkRect.z0 + ParkRect.z1);
        var mPath = c.M("NB_ParkPath");
        var pathNS = new Rect2(ParkPathX - 1.25f, MapleZ + 8f, ParkPathX + 1.25f, BirchZ - 8f);
        var pathW = new Rect2(ParkRect.x0 + 3f, pz - 1.25f, ParkPathX - 1.25f, pz + 1.25f);
        var pathE = new Rect2(ParkPathX + 1.25f, pz - 1.25f, ParkRect.x1 - 3f, pz + 1.25f);
        Slab(c, g, "ParkPath_NS", pathNS, WalkTop, mPath);
        Slab(c, g, "ParkPath_W", pathW, WalkTop, mPath);
        Slab(c, g, "ParkPath_E", pathE, WalkTop, mPath);

        // Playground on a sand patch west of the crossing.
        var sand = new Rect2(46f, 100.5f, 71f, 112f);
        Slab(c, g, "PlaygroundSand", sand, SandTop, c.M("NB_Sand"));
        string P = Sub + "Playground/";
        Place(c, P + "SM_swing", g, new Vector3(51.5f, SandTop, 103.6f), Yaw(0f), 1f, false, LayerDefault);
        Place(c, P + "SM_slide", g, new Vector3(58.5f, SandTop, 103.4f), Yaw(0f), 1f, false, LayerDefault);
        Place(c, P + "SM_climbingrack", g, new Vector3(65.5f, SandTop, 103.4f), Yaw(0f), 1f, false, LayerDefault);
        Place(c, P + "SM_sandbox_playground_0" + c.RI(1, 3), g, new Vector3(50.5f, SandTop, 109.2f), Yaw(0f), 1f, false, LayerDefault);
        Place(c, P + "SM_playground_object_01", g, new Vector3(58f, SandTop, 109.6f), Yaw(0f), 1f, false, LayerDefault);
        Place(c, P + "SM_playground_object_02", g, new Vector3(65.5f, SandTop, 109.6f), Yaw(0f), 1f, false, LayerDefault);

        // Benches along the east-west path, facing it, each with a park bin.
        var benches = new[] { 52f, 64f, 96f, 110f };
        foreach (var bx in benches)
        {
            KnockPrefab(c, Place(c, P + "SM_bench_playground", g, new Vector3(bx, GrassTop, pz + 2.1f), Yaw(180f), 1f, false, LayerDefault), KnockKind.ParkBench, null, ColMode.Box);
            KnockPrefab(c, Place(c, P + "SM_trashbin_park", g, new Vector3(bx + 1.6f, GrassTop, pz + 1.8f), Yaw(180f), 1f, false, LayerDefault), KnockKind.ParkBin, null, ColMode.Box);
        }

        // Entrance dressing on Maple St: a lemonade stand and a derpy park sign.
        KnockPrefab(c, Place(c, Sub + "Props/SM_lemonade_shop", g, new Vector3(ParkPathX + 2.75f, GrassTop, MapleZ + 11.4f), Yaw(180f), 1f, false, LayerDefault), KnockKind.LemonadeStand, null, ColMode.Box);
        ParkSign(c, g, new Vector3(ParkPathX - 2.65f, GrassTop, MapleZ + 11.0f), "PICKLE PARK");
        ParkSign(c, g, new Vector3(ParkPathX + 2.65f, GrassTop, BirchZ - 11.0f), "PICKLE PARK", 0f);

        // Trees + bushes scattered in the park, clear of paths, sand and benches.
        var keepOut = new List<Rect2> { pathNS.Expand(2.8f), pathW.Expand(2.8f), pathE.Expand(2.8f), sand.Expand(2.5f) };
        var placed = new List<Vector3>();
        for (int tries = 0; tries < 400 && placed.Count < 16; tries++)
        {
            float x = c.R(ParkRect.x0 + 2.5f, ParkRect.x1 - 2.5f), z = c.R(ParkRect.z0 + 2.5f, ParkRect.z1 - 2.5f);
            if (InAny(keepOut, x, z)) continue;
            if (TooClose(placed, x, z, 8.5f)) continue;
            var p = new Vector3(x, GrassTop, z);
            placed.Add(p);
            Tree(c, g, c.Chance(0.5f) ? "SM_tree_town_01" : "SM_tree_town_02", p, c.R(0.75f, 1.0f));
        }
        for (int tries = 0, n = 0; tries < 200 && n < 14; tries++)
        {
            bool alongZ = c.Chance(0.5f);
            float x = alongZ ? (c.Chance(0.5f) ? ParkRect.x0 + c.R(0.8f, 2f) : ParkRect.x1 - c.R(0.8f, 2f)) : c.R(ParkRect.x0 + 1f, ParkRect.x1 - 1f);
            float z = alongZ ? c.R(ParkRect.z0 + 1f, ParkRect.z1 - 1f) : (c.Chance(0.5f) ? ParkRect.z0 + c.R(0.8f, 2f) : ParkRect.z1 - c.R(0.8f, 2f));
            if (InAny(keepOut, x, z)) continue;
            n++;
            Place(c, Sub + (c.Chance(0.5f) ? "Nature/SM_bush_02" : "Nature/SM_bush_03"), g, new Vector3(x, GrassTop, z), Yaw(c.R(0f, 360f)), c.R(1f, 1.6f), false, LayerDefault);
        }
        c.developed.Add(pathNS.Expand(1f));
    }

    static void ParkSign(Ctx c, Transform g, Vector3 pos, string text, float yaw = 180f)
    {
        var root = new GameObject("ParkSign").transform;
        root.SetParent(g, false);
        root.SetPositionAndRotation(pos, Yaw(yaw));
        var rot = Yaw(yaw);
        Vector3 right = rot * Vector3.right;
        OBox(c, root, "PostL", pos - right * 1.0f + Vector3.up * 0.75f, rot, new Vector3(0.14f, 1.5f, 0.14f), c.M("NB_Wood"), LayerDefault, true);
        OBox(c, root, "PostR", pos + right * 1.0f + Vector3.up * 0.75f, rot, new Vector3(0.14f, 1.5f, 0.14f), c.M("NB_Wood"), LayerDefault, true);
        // Slightly crooked board, because of course it is.
        SignBoard(c, root, "Board", pos + Vector3.up * 1.35f, rot * Quaternion.Euler(0, 0, 3.5f), new Vector3(2.4f, 0.7f, 0.1f), c.M("NB_SignGreen"), text, new Color(1f, 0.93f, 0.4f), true);
        Knock(c, root.gameObject, KnockKind.Sign, "parksign", null);
    }

    // ------------------------------------------------------------------------------------------
    // Depot (RUBBISH CO. exterior) + garage door + spawn markers
    // ------------------------------------------------------------------------------------------

    static void BuildDepot(Ctx c)
    {
        var g = c.gDepot;
        const float fz = DepotFrontZ;

        // Building: [block][garage entry frame][block], closed bay shell behind the frame.
        var bld = Group(g, "Building");
        Place(c, Gar + "SM_bld_block_01", bld, new Vector3(-6.72f, 0f, fz - 4.10f), Quaternion.identity, 1f, false, LayerGround);
        Place(c, Gar + "SM_bld_block_01", bld, new Vector3(6.72f, 0f, fz - 4.10f), Quaternion.identity, 1f, true, LayerGround);
        Place(c, Gar + "SM_garage_entry_01", bld, new Vector3(0f, 0f, fz), Quaternion.identity, 1f, false, LayerGround);
        OBox(c, bld, "BayShell", new Vector3(0f, 3.915f, fz - 4.2f), Quaternion.identity, new Vector3(7.5f, 7.83f, 8.0f), c.M("DepotWall"), LayerGround, true);
        // Closed roller door scaled into the frame opening (6.07 x 5.31 m).
        var door = Place(c, Gar + "SM_garage_door", bld, new Vector3(-0.025f, 0.16f, fz + 0.02f), Quaternion.identity, 1f, false, LayerGround);
        if (door != null) door.transform.localScale = new Vector3(1.76f, 1.77f, 1f);
        // Tagline plank above the door.
        SignBoard(c, bld, "Tagline", new Vector3(0f, 7.35f, fz + 0.06f), Quaternion.identity, new Vector3(7.2f, 0.8f, 0.08f), c.M("NB_SignGreen"),
                  "we pick up what you put down", new Color(1f, 0.93f, 0.45f), false, LayerGround);

        // Painted wall slogans on the two blocks (text reads from the lot, +Z).
        var paintRed = new Color(0.7f, 0.12f, 0.1f);
        Label(bld, "Slogan_L", "NO DUMPING\n<size=55%>(that's OUR job)</size>", new Vector3(-6.72f, 4.3f, fz + 0.03f), Yaw(180f), new Vector2(5f, 2.2f), paintRed);
        Label(bld, "Slogan_R", "OPEN 6AM-ISH\n<size=55%>every day is garbage day</size>", new Vector3(6.72f, 4.3f, fz + 0.03f), Yaw(180f), new Vector2(5f, 2.2f), paintRed);

        // Roof sign on two posts, a little crooked.
        var sign = Group(bld, "RoofSign");
        OBox(c, sign, "PostL", new Vector3(-3.6f, 8.45f, fz - 0.7f), Quaternion.identity, new Vector3(0.3f, 1.3f, 0.3f), c.M("NB_Metal"), LayerGround, true);
        OBox(c, sign, "PostR", new Vector3(3.6f, 8.45f, fz - 0.7f), Quaternion.identity, new Vector3(0.3f, 1.3f, 0.3f), c.M("NB_Metal"), LayerGround, true);
        SignBoard(c, sign, "Board", new Vector3(0.9f, 10.1f, fz - 0.55f), Quaternion.Euler(0f, 0f, 2.5f), new Vector3(11f, 2.6f, 0.3f), c.M("NB_SignYellow"),
                  "RUBBISH CO.", new Color(0.08f, 0.32f, 0.16f), true, LayerGround);

        // Mascot: a giant trash bag with googly eyes, bobbing on the roof.
        var mascot = new GameObject("Mascot_BigBag").transform;
        mascot.SetParent(bld, false);
        mascot.SetPositionAndRotation(new Vector3(-7.7f, 7.83f, fz - 2.4f), Yaw(14f));
        var wob = mascot.gameObject.AddComponent<DerpyWobble>();
        wob.period = 1.7f; wob.squash = 0.07f; wob.swayDegrees = 5f;
        c.dynamicRoots.Add(mascot);
        var sack = Place(c, Sub + "Props/SM_trash_sack_01", mascot, mascot.position, mascot.rotation, 4.6f, false, LayerDefault);
        if (sack != null) sack.transform.SetParent(mascot, true);
        GooglyEye(c, mascot, new Vector3(-0.5f, 2.35f, 1.12f), 0.62f);
        GooglyEye(c, mascot, new Vector3(0.52f, 2.45f, 1.08f), 0.72f);

        // Props around the lot.
        var props = Group(g, "Props");
        Place(c, Sub + "Props/SM_garbagecan_04", props, new Vector3(-10.5f, 0f, -20.6f), Yaw(90f), 1f, false, LayerGround);
        Place(c, Sub + "Props/SM_garbagecan_05", props, new Vector3(-10.5f, 0f, -18.2f), Yaw(90f), 1f, false, LayerGround);
        Place(c, Sub + "Props/SM_garbagecan_06", props, new Vector3(-10.5f, 0f, -15.9f), Yaw(96f), 1f, false, LayerGround);
        for (int i = 0; i < 3; i++)
            Place(c, Gar + "SM_tire_01", props, new Vector3(11.4f + c.R(-0.08f, 0.08f), 0.37f * i, -21.2f + c.R(-0.08f, 0.08f)), Yaw(c.R(0f, 360f)), 1f, false, LayerDefault);
        Place(c, Gar + "SM_tire_01", props, new Vector3(12.9f, 0f, -19.8f), Yaw(c.R(0f, 360f)), 1f, false, LayerDefault);
        Place(c, Gar + "SM_tire_01", props, new Vector3(13.2f, 0.37f, -19.9f), Quaternion.Euler(12f, c.R(0f, 360f), 0f), 1f, false, LayerDefault);
        Place(c, Gar + "SM_container_01", props, new Vector3(10.6f, 0f, -16.4f), Yaw(c.R(-20f, 20f)), 1f, false, LayerDefault);
        Place(c, Gar + "SM_container_01", props, new Vector3(11.3f, 0f, -16.0f), Yaw(c.R(-20f, 20f)), 1f, false, LayerDefault);
        Place(c, Gar + "SM_large_round_container", props, new Vector3(-15.4f, 0.24f, -21.8f), Yaw(0f), 1f, false, LayerDefault);
        Place(c, Gar + "SM_large_round_container", props, new Vector3(-14.2f, 0.24f, -22.4f), Yaw(40f), 1f, false, LayerDefault);
        Place(c, Gar + "SM_wooden_box", props, new Vector3(14.6f, 0f, -22.3f), Yaw(10f), 1f, false, LayerDefault);
        Place(c, Gar + "SM_wooden_box", props, new Vector3(15.3f, 0f, -21.4f), Yaw(-25f), 1f, false, LayerDefault);
        Place(c, Gar + "SM_wooden_box", props, new Vector3(14.9f, 0.35f, -21.9f), Yaw(30f), 1f, false, LayerDefault);
        var junker = Place(c, Gar + "SM_pickup_truck_01", props, new Vector3(-13.3f, 0f, 3.6f), Yaw(196f), 1f, false, LayerGround);
        if (junker != null)
        {
            var bc = junker.AddComponent<BoxCollider>();
            bc.center = new Vector3(0f, 1.2f, -0.1f);
            bc.size = new Vector3(2.9f, 2.35f, 6.1f);
        }
        // (the south-east lamp sits back from Minivan Mile's mouth so trucks can swing out east)
        foreach (var p in new[] { new Vector3(-17.2f, 0f, 10.8f), new Vector3(17.2f, 0f, 10.8f), new Vector3(-17.2f, 0f, -11f), new Vector3(17.2f, 0f, -16.5f) })
            KnockPrefab(c, Place(c, Sub + "Props/SM_streetlamp_02", props, p, Yaw(0f), 1f, false, LayerDefault), KnockKind.Lamp, null, ColMode.Pole);

        // Fence around the sides and back of the pad (open toward Depot Rd).
        var fence = Group(g, "Fence");
        FencePosts(c, fence, new Vector3(-DepotPad.x1 - 0.6f, 0f, DepotPad.z0 - 0.6f), new Vector3(-DepotPad.x1 - 0.6f, 0f, 4f));
        // East side: a gap where Minivan Mile leaves the pad (road + sidewalks, z -14..2).
        FencePosts(c, fence, new Vector3(DepotPad.x1 + 0.6f, 0f, DepotPad.z0 - 0.6f), new Vector3(DepotPad.x1 + 0.6f, 0f, MileZ - RoadHalfWidth - WalkWidth - 0.4f));
        FencePosts(c, fence, new Vector3(-DepotPad.x1 - 0.6f, 0f, DepotPad.z0 - 0.6f), new Vector3(DepotPad.x1 + 0.6f, 0f, DepotPad.z0 - 0.6f));

        // Truck parking bay (TruckSpawner) on the east side of the lot, facing Depot Rd.
        var truckPark = new Vector3(11.5f, RoadTop, -3f);
        var paint = Group(g, "Paint");
        var white = c.M("NB_LineWhite");
        OBox(c, paint, "Bay_L", truckPark + new Vector3(-1.9f, 0.005f, 0.8f), Quaternion.identity, new Vector3(0.15f, 0.02f, 7.8f), white, LayerDefault, false);
        OBox(c, paint, "Bay_R", truckPark + new Vector3(1.9f, 0.005f, 0.8f), Quaternion.identity, new Vector3(0.15f, 0.02f, 7.8f), white, LayerDefault, false);
        OBox(c, paint, "Bay_Back", truckPark + new Vector3(0f, 0.005f, -3.1f), Quaternion.identity, new Vector3(3.95f, 0.02f, 0.15f), white, LayerDefault, false);
        var baseLabel = Label(paint, "BayText", "TRUCK\n<size=60%>(the big one)</size>", truckPark + new Vector3(0f, 0.02f, 3.7f), Quaternion.Euler(90f, 0f, 0f), new Vector2(3.4f, 1.6f), white.color);
        _ = baseLabel;
        // Yellow "keep clear" box painted in front of the garage door (= the InteractionZone apron).
        var y = c.M("NB_LineYellow");
        OBox(c, paint, "Apron_L", new Vector3(-5f, 0.005f, fz + 2.6f), Quaternion.identity, new Vector3(0.18f, 0.02f, 5f), y, LayerDefault, false);
        OBox(c, paint, "Apron_R", new Vector3(5f, 0.005f, fz + 2.6f), Quaternion.identity, new Vector3(0.18f, 0.02f, 5f), y, LayerDefault, false);
        OBox(c, paint, "Apron_F", new Vector3(0f, 0.005f, fz + 5.1f), Quaternion.identity, new Vector3(10.18f, 0.02f, 0.18f), y, LayerDefault, false);

        // --- Garage door interaction (Flow's GarageDoorController) ---
        var doorGo = new GameObject("DepotGarageDoor");
        doorGo.transform.SetParent(g, false);
        doorGo.transform.SetPositionAndRotation(new Vector3(0f, 0f, fz + 0.2f), Quaternion.identity);
        var gdc = doorGo.AddComponent<GarageDoorController>();
        gdc.targetSceneName = "ShopScene";
        gdc.targetSpawnPointName = "SpawnPoint_FromNeighborhood";
        gdc.targetSpawnPointNameDriving = "SpawnPoint_FromNeighborhood_Truck";
        gdc.promptOnFoot = "Enter Garage";
        gdc.promptDriving = "Drive into Garage";
        var zone = doorGo.AddComponent<InteractionZone>();
        zone.mode = ZoneMode.Either;
        zone.center = new Vector3(0f, 2f, 2.5f);
        zone.size = new Vector3(10f, 6f, 5f);
        zone.score = 1f;
        var col = new GameObject("DoorCollider");
        col.transform.SetParent(doorGo.transform, false);
        col.layer = LayerInteractable;
        var box = col.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.center = new Vector3(0f, 2.8f, 0.1f);
        box.size = new Vector3(6.4f, 5.6f, 0.6f);
        c.dynamicRoots.Add(doorGo.transform);
        c.doorZoneT = doorGo.transform;

        // --- Spawn markers ---
        var onFoot = Marker(c, "SpawnPoint_FromGarage", new Vector3(5.8f, RoadTop, fz + 2f), Yaw(0f), false);
        Marker(c, "SpawnPoint_FromGarage_Truck", new Vector3(0f, RoadTop, fz + 10f), Yaw(0f), true);
        var park = Marker(c, "TruckParking", truckPark, Yaw(0f), true);
        park.tag = "TruckSpawner";
        FixPlayerSpawner(c, onFoot.transform.position, onFoot.transform.rotation);

        c.developed.Add(DepotPad.Expand(5f));
    }

    static void FencePosts(Ctx c, Transform parent, Vector3 a, Vector3 b)
    {
        const float segLen = 3.02f;
        Vector3 d = b - a;
        float len = d.magnitude;
        Vector3 v = d / len;
        int n = Mathf.Max(1, Mathf.RoundToInt(len / segLen));
        float each = len / n;
        var rot = Quaternion.LookRotation(v) * Yaw(-90f);
        for (int i = 0; i < n; i++)
        {
            var go = Place(c, Sub + "Modular/SM_fence_01", parent, a + v * (each * (i + 0.5f)) + Vector3.up * GrassTop, rot, 1f, false, LayerGround);
            if (go != null)
            {
                var s = go.transform.localScale;
                go.transform.localScale = new Vector3(s.x * each / segLen, s.y, s.z);
            }
        }
    }

    static void GooglyEye(Ctx c, Transform parent, Vector3 localPos, float size)
    {
        var eye = new GameObject("GooglyEye").transform;
        eye.SetParent(parent, false);
        eye.localPosition = localPos;
        eye.localRotation = Quaternion.identity;
        var white = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        white.name = "White";
        white.transform.SetParent(eye, false);
        white.transform.localScale = new Vector3(size, size, size * 0.4f);
        white.GetComponent<MeshRenderer>().sharedMaterial = c.M("NB_EyeWhite");
        Object.DestroyImmediate(white.GetComponent<Collider>());
        var pupil = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        pupil.name = "Pupil";
        pupil.transform.SetParent(eye, false);
        pupil.transform.localPosition = new Vector3(c.R(-0.1f, 0.1f) * size, -0.18f * size, size * 0.17f);
        pupil.transform.localScale = new Vector3(size * 0.45f, size * 0.45f, size * 0.2f);
        pupil.GetComponent<MeshRenderer>().sharedMaterial = c.M("NB_EyeBlack");
        Object.DestroyImmediate(pupil.GetComponent<Collider>());
        var gp = eye.gameObject.AddComponent<GooglyPupil>();
        gp.pupil = pupil.transform;
        gp.maxOffset = size * 0.26f;
    }

    static GameObject Marker(Ctx c, string name, Vector3 pos, Quaternion rot, bool truck)
    {
        var go = new GameObject(name);
        go.transform.SetParent(c.gSpawns, false);
        go.transform.SetPositionAndRotation(pos, rot);
        c.spawns.Add(new SpawnCheck { t = go.transform, y = pos.y, truck = truck });
        return go;
    }

    /// <summary>The scene's PlayerSpawner-tagged marker: strip any PlayerSpawner component, move it to the on-foot spawn.</summary>
    static void FixPlayerSpawner(Ctx c, Vector3 pos, Quaternion rot)
    {
        GameObject tagged = null;
        foreach (var r in c.scene.GetRootGameObjects())
        {
            if (r.name == RootName || r.name == OldRootName) continue;
            foreach (var t in r.GetComponentsInChildren<Transform>(true))
                if (t.CompareTag("PlayerSpawner")) { tagged = t.gameObject; break; }
            if (tagged != null) break;
        }
        if (tagged == null)
        {
            tagged = new GameObject("PlayerSpawner") { tag = "PlayerSpawner" };
            c.notes.AppendLine("created PlayerSpawner-tagged marker");
        }
        foreach (var ps in tagged.GetComponents<PlayerSpawner>())
        {
            try { Object.DestroyImmediate(ps); }
            catch (Exception e) { c.notes.AppendLine("  (remove PlayerSpawner: " + e.Message + ")"); }
            if (ps != null) { ps.enabled = false; c.notes.AppendLine("PlayerSpawner component could not be removed; disabled it"); }
            else c.notes.AppendLine("removed PlayerSpawner component from the tagged marker");
        }
        tagged.transform.SetPositionAndRotation(pos, rot);
        c.spawns.Add(new SpawnCheck { t = tagged.transform, y = pos.y, truck = false });
    }

    // ------------------------------------------------------------------------------------------
    // The living route: a PickupStop at every house
    // ------------------------------------------------------------------------------------------

    static void BuildRoute(Ctx c)
    {
        var routeGo = new GameObject("TrashRoute");
        routeGo.transform.SetParent(c.root, false);
        var route = routeGo.AddComponent<TrashRoute>();
        route.routeName = RouteName;
        c.gRoute = routeGo.transform;
        c.dynamicRoots.Add(routeGo.transform);

        // The old fixed Route 1 drew from c.rng here; replay exactly those draws so everything built after
        // this (nature scatter) comes out identical for a given seed.
        ReplayLegacyRouteDraws(c);

        // Every house gets a curb. Per-house numbers come from a private RNG keyed by (seed, stopId), so they
        // are stable per house and never disturb the main sequence.
        var ordered = new List<LotInfo>(c.lots);
        ordered.Sort((a, b) => string.CompareOrdinal(StopIdFor(a), StopIdFor(b)));
        foreach (var l in ordered)
        {
            string id = StopIdFor(l);
            var rng = new System.Random(unchecked(c.seed * 486187739 + PickupVisuals.StableHash(id)));
            float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);

            var go = new GameObject("Stop_" + id);
            go.transform.SetParent(routeGo.transform, false);
            go.transform.SetPositionAndRotation(l.stopPos, l.stopRot);
            var stop = go.AddComponent<PickupStop>();
            stop.stopId = id;
            stop.address = $"{l.number} {l.f.street}";
            // Curb capacity by house size: small 3–4, medium 4–5, big 5–6.
            stop.bagCount = Mathf.Clamp(3 + l.house.size + rng.Next(0, 2), 3, PickupStop.MaxSlots);
            stop.wealth = l.house.size == 0 ? R(0.2f, 0.35f) : l.house.size == 1 ? R(0.45f, 0.6f) : R(0.75f, 0.9f);
            stop.onRoute = true;
            stop.districtId = Districts.MildlyPleasant;
            stop.bin = l.bin;
            c.stops.Add(stop);
        }
    }

    static string StopIdFor(LotInfo l) => $"{(l.district != null ? l.district.prefix : "")}{l.f.key}-{l.number}";

    /// <summary>Consume c.rng exactly like the legacy 9-stop Route 1 did (3 picks each from Maple even/odd and Oak, then 2 rolls per stop).</summary>
    static void ReplayLegacyRouteDraws(Ctx c)
    {
        var mapleEven = new List<LotInfo>(); var mapleOdd = new List<LotInfo>(); var oak = new List<LotInfo>();
        foreach (var l in c.lots)
        {
            if (l.f.key == "maple") (l.f.odd ? mapleOdd : mapleEven).Add(l);
            else if (l.f.key == "oak") oak.Add(l);
        }
        int chosen = TakeRandom(c, mapleEven, 3).Count + TakeRandom(c, mapleOdd, 3).Count + TakeRandom(c, oak, 3).Count;
        for (int i = 0; i < chosen; i++)
        {
            c.RI(1, 3);       // legacy bagCount roll
            c.R(0f, 1f);      // legacy wealth roll
        }
    }

    static List<LotInfo> TakeRandom(Ctx c, List<LotInfo> from, int n)
    {
        var pool = new List<LotInfo>(from);
        var result = new List<LotInfo>();
        while (result.Count < n && pool.Count > 0)
        {
            int i = c.rng.Next(pool.Count);
            result.Add(pool[i]);
            pool.RemoveAt(i);
        }
        return result;
    }

    // ------------------------------------------------------------------------------------------
    // Districts 2..4 (Docs/Progression.md §5.3): per-district specs
    // ------------------------------------------------------------------------------------------

    const float MileZ = -6f;                                   // Minivan Mile / Carpool Ln centre line
    const float MMWestX = 240f, MMEastX = 400f, MMNorthZ = 150f, MMSpurX = 320f, MMBulbAZ = 56f, MMBulbBZ = 88f, MMBulbHalf = 12f;
    const float MMGateX = 206f;
    const float SHSouthZ = 240f, SHNorthZ = 420f, SHEastX = 200f, SHGateZ = 230f, SHTopZ = 460f;
    const float BBAveZ = 550f, BBGateZ = 460f, BBBulbX = 318f;
    const float GateRoadHalf = 5.6f, GateHalf = 8.4f;          // vehicle opening (road + curbs) / full gate width

    enum WallStyle { Picket, Hedge, Stone }

    /// <summary>One district's build recipe: roads, lot frontages, house pool + scale, wealth bands, gate and walls.</summary>
    sealed class DistrictSpec
    {
        public string id, prefix, label;
        public int tier;
        public int seed;
        public Rect2 bounds;                                       // wall rectangle (= Districts.boundsXZ)
        public readonly List<Rect2> roads = new List<Rect2>();     // asphalt (also the lot-corridor checks)
        public readonly List<(string name, Vector3 a, Vector3 b)> lines = new List<(string, Vector3, Vector3)>();
        public readonly List<Frontage> frontages = new List<Frontage>();     // lots (+ verge furniture)
        public readonly List<Frontage> verges = new List<Frontage>();        // verge furniture only (access roads)
        public float lotTarget = 26f;
        public string[] housePool; public float[] houseWeights;
        public float scaleMin = 1f, scaleMax = 1f, setbackMin = 6f, setbackMax = 8f;
        public float[] bandShare; public Vector2[] bandRange;
        public int capMin = 4, capMax = 6;
        public Vector3 gatePos; public float gateYaw;               // +Z of the gate root points into the district
        public WallStyle wallStyle;
        public readonly List<(Vector3 a, Vector3 b)> walls = new List<(Vector3, Vector3)>();
        public string lampPrefab = "Props/SM_streetlamp_01", treeA = "SM_tree_town_03", treeB = "SM_tree_town_02";
        public Transform root, gLots, gStreet, gProps;
        public int lotIndex;
    }

    static Frontage NewFrontage(DistrictSpec d, string street, string key, Vector3 origin, Vector3 along, Vector3 normal,
        float l0, float l1, float f0, float f1, float depth, bool odd, bool outer, int numberOffset = 0, bool lots = true)
    {
        var f = new Frontage
        {
            street = street, key = key, origin = origin, along = along, normal = normal, lot0 = l0, lot1 = l1, furn0 = f0, furn1 = f1,
            maxDepth = depth, odd = odd, outer = outer, numberOffset = numberOffset, district = d,
        };
        (lots ? d.frontages : d.verges).Add(f);
        return f;
    }

    static Rect2 RoadX(float z, float x0, float x1) => new Rect2(x0, z - RoadHalfWidth, x1, z + RoadHalfWidth);
    static Rect2 RoadZ(float x, float z0, float z1) => new Rect2(x - RoadHalfWidth, z0, x + RoadHalfWidth, z1);

    /// <summary>Pure data for the three new districts (no RNG, no objects): roads join the ground decomposition.</summary>
    static void DefineDistricts(Ctx c)
    {
        var X = Vector3.right; var Zf = Vector3.forward;

        // ---------------- Minivan Meadows (east; tier 2) ----------------
        var mm = new DistrictSpec
        {
            id = Districts.MinivanMeadows, prefix = "mm-", label = "Minivan Meadows", tier = 2, lotTarget = 26f,
            bounds = new Rect2(MMGateX, -40f, 440f, 230f),
            housePool = new[] { "SM_house_suburb_003", "SM_house_suburb_004", "SM_house_suburb_005", "SM_house_suburb_006", "SM_house_suburb_007" },
            houseWeights = new[] { 1f, 1f, 1.2f, 0.7f, 0.7f },
            scaleMin = 1.1f, scaleMax = 1.2f, setbackMin = 5.5f, setbackMax = 7f,
            bandShare = new[] { 0.30f, 0.45f, 0.25f },
            bandRange = new[] { new Vector2(0.30f, 0.45f), new Vector2(0.45f, 0.65f), new Vector2(0.65f, 0.80f) },
            capMin = 4, capMax = 6, gatePos = new Vector3(MMGateX, 0f, MileZ), gateYaw = 90f, wallStyle = WallStyle.Picket,
        };
        mm.roads.Add(RoadX(MileZ, DepotPad.x1, MMWestX + RoadHalfWidth));                           // Minivan Mile
        mm.roads.Add(RoadX(MileZ, MMWestX - RoadHalfWidth, MMEastX + RoadHalfWidth));              // Carpool Ln
        mm.roads.Add(RoadZ(MMWestX, MileZ - RoadHalfWidth, MMNorthZ + RoadHalfWidth));             // Trampoline Ct
        mm.roads.Add(RoadZ(MMEastX, MileZ - RoadHalfWidth, MMNorthZ + RoadHalfWidth));             // Soccer Mom Way
        mm.roads.Add(RoadX(MMNorthZ, MMWestX - RoadHalfWidth, MMEastX + RoadHalfWidth));           // Juice Box Blvd
        mm.roads.Add(RoadZ(MMSpurX, MileZ + RoadHalfWidth, MMBulbAZ - MMBulbHalf));                // Gluten-Free Ct (spur)
        mm.roads.Add(new Rect2(MMSpurX - MMBulbHalf, MMBulbAZ - MMBulbHalf, MMSpurX + MMBulbHalf, MMBulbAZ + MMBulbHalf));
        mm.roads.Add(RoadZ(MMSpurX, MMBulbBZ + MMBulbHalf, MMNorthZ - RoadHalfWidth));             // Playdate Pl (spur)
        mm.roads.Add(new Rect2(MMSpurX - MMBulbHalf, MMBulbBZ - MMBulbHalf, MMSpurX + MMBulbHalf, MMBulbBZ + MMBulbHalf));
        mm.lines.Add(("Mile", new Vector3(DepotPad.x1 + 2f, 0, MileZ), new Vector3(MMWestX - RoadHalfWidth - 1f, 0, MileZ)));
        mm.lines.Add(("Carpool", new Vector3(MMWestX + RoadHalfWidth, 0, MileZ), new Vector3(MMEastX - RoadHalfWidth, 0, MileZ)));
        mm.lines.Add(("Trampoline", new Vector3(MMWestX, 0, MileZ + RoadHalfWidth), new Vector3(MMWestX, 0, MMNorthZ - RoadHalfWidth)));
        mm.lines.Add(("SoccerMom", new Vector3(MMEastX, 0, MileZ + RoadHalfWidth), new Vector3(MMEastX, 0, MMNorthZ - RoadHalfWidth)));
        mm.lines.Add(("JuiceBox", new Vector3(MMWestX + RoadHalfWidth, 0, MMNorthZ), new Vector3(MMEastX - RoadHalfWidth, 0, MMNorthZ)));
        var mmGap = new Vector2(MMSpurX - MMWestX - 11f, MMSpurX - MMWestX + 11f);
        NewFrontage(mm, "Carpool Ln", "carpool", new Vector3(MMWestX, 0, MileZ), X, Zf, 10f, 150f, 8f, 152f, 28f, false, false).reserved.Add(mmGap);
        NewFrontage(mm, "Juice Box Blvd", "juicebox", new Vector3(MMWestX, 0, MMNorthZ), X, -Zf, 10f, 150f, 8f, 152f, 28f, true, false).reserved.Add(mmGap);
        NewFrontage(mm, "Trampoline Ct", "trampoline", new Vector3(MMWestX, 0, MileZ), Zf, X, 38f, 118f, 10f, 152f, 25f, false, false);
        NewFrontage(mm, "Soccer Mom Way", "soccermom", new Vector3(MMEastX, 0, MileZ), Zf, -X, 38f, 118f, 10f, 152f, 25f, true, false);
        NewFrontage(mm, "Gluten-Free Ct", "glutenfree", new Vector3(MMSpurX - MMBulbHalf + RoadHalfWidth, 0, MMBulbAZ), Zf, -X, -14f, 14f, -14f, 14f, 28f, true, false, 100);
        NewFrontage(mm, "Gluten-Free Ct", "glutenfree", new Vector3(MMSpurX + MMBulbHalf - RoadHalfWidth, 0, MMBulbAZ), Zf, X, -14f, 14f, -14f, 14f, 28f, false, false, 100);
        NewFrontage(mm, "Playdate Pl", "playdate", new Vector3(MMSpurX - MMBulbHalf + RoadHalfWidth, 0, MMBulbBZ), Zf, -X, -14f, 14f, -14f, 14f, 28f, true, false, 100);
        NewFrontage(mm, "Playdate Pl", "playdate", new Vector3(MMSpurX + MMBulbHalf - RoadHalfWidth, 0, MMBulbBZ), Zf, X, -14f, 14f, -14f, 14f, 28f, false, false, 100);
        var jbOuter = NewFrontage(mm, "Juice Box Blvd", "juicebox", new Vector3(MMWestX, 0, MMNorthZ), X, Zf, 5f, 155f, 0f, 160f, 30f, false, true, 200);
        jbOuter.lotWidth = 37.5f;
        NewFrontage(mm, "Minivan Mile", "mile", new Vector3(DepotPad.x1, 0, MileZ), X, Zf, 0f, 0f, 6f, MMGateX - DepotPad.x1 - 4f, 0f, false, true, 0, false);
        NewFrontage(mm, "Minivan Mile", "mile", new Vector3(DepotPad.x1, 0, MileZ), X, -Zf, 0f, 0f, 6f, MMGateX - DepotPad.x1 - 4f, 0f, true, true, 0, false);
        NewFrontage(mm, "Carpool Ln", "carpool", new Vector3(MMGateX, 0, MileZ), X, -Zf, 0f, 0f, 8f, MMEastX - MMGateX + 2f, 0f, true, true, 0, false);
        // Picket fence: the west side (gate gap on the Mile) and the north side; east/south are the town's edge.
        mm.walls.Add((new Vector3(MMGateX, 0, -40f), new Vector3(MMGateX, 0, MileZ - GateHalf)));
        mm.walls.Add((new Vector3(MMGateX, 0, MileZ + GateHalf), new Vector3(MMGateX, 0, 230f)));
        mm.walls.Add((new Vector3(MMGateX, 0, 230f), new Vector3(440f, 0, 230f)));
        c.districts.Add(mm);

        // ---------------- Snootington Hills (north; tier 3) ----------------
        var sh = new DistrictSpec
        {
            id = Districts.SnootingtonHills, prefix = "sh-", label = "Snootington Hills", tier = 3, lotTarget = 40f,
            bounds = new Rect2(-60f, SHGateZ, 220f, SHTopZ),
            housePool = new[] { "SM_house_suburb_006", "SM_house_suburb_007" }, houseWeights = new[] { 1f, 1f },
            scaleMin = 1.3f, scaleMax = 1.3f, setbackMin = 7f, setbackMax = 9f,
            bandShare = new[] { 0.30f, 0.40f, 0.30f },
            bandRange = new[] { new Vector2(0.40f, 0.60f), new Vector2(0.60f, 0.80f), new Vector2(0.80f, 0.90f) },
            capMin = 6, capMax = 6, gatePos = new Vector3(0f, 0f, SHGateZ), gateYaw = 0f, wallStyle = WallStyle.Hedge,
            lampPrefab = "Props/SM_streetlamp_03", treeA = "SM_tree_town_01", treeB = "SM_tree_town_03",
        };
        sh.roads.Add(RoadZ(ElmX, BirchZ + RoadHalfWidth, SHNorthZ + RoadHalfWidth));               // Old Money Rd (Elm Ave continued)
        sh.roads.Add(RoadX(SHSouthZ, ElmX - RoadHalfWidth, SHEastX + RoadHalfWidth));              // Hedgerow Crescent
        sh.roads.Add(RoadZ(SHEastX, SHSouthZ - RoadHalfWidth, SHNorthZ + RoadHalfWidth));          // Pinky-Up Place
        sh.roads.Add(RoadX(SHNorthZ, ElmX - RoadHalfWidth, SHEastX + RoadHalfWidth));              // Monocle Mews
        sh.lines.Add(("OldMoney", new Vector3(ElmX, 0, BirchZ + RoadHalfWidth + 2f), new Vector3(ElmX, 0, SHNorthZ - RoadHalfWidth)));
        sh.lines.Add(("Hedgerow", new Vector3(ElmX + RoadHalfWidth, 0, SHSouthZ), new Vector3(SHEastX - RoadHalfWidth, 0, SHSouthZ)));
        sh.lines.Add(("PinkyUp", new Vector3(SHEastX, 0, SHSouthZ + RoadHalfWidth), new Vector3(SHEastX, 0, SHNorthZ - RoadHalfWidth)));
        sh.lines.Add(("Monocle", new Vector3(ElmX + RoadHalfWidth, 0, SHNorthZ), new Vector3(SHEastX - RoadHalfWidth, 0, SHNorthZ)));
        NewFrontage(sh, "Hedgerow Crescent", "hedgerow", new Vector3(ElmX, 0, SHSouthZ), X, Zf, 10f, 190f, 8f, 192f, 40f, false, false);
        NewFrontage(sh, "Monocle Mews", "monocle", new Vector3(ElmX, 0, SHNorthZ), X, -Zf, 10f, 190f, 8f, 192f, 40f, true, false);
        NewFrontage(sh, "Old Money Rd", "oldmoney", new Vector3(ElmX, 0, SHSouthZ), Zf, X, 50f, 130f, 10f, 172f, 40f, false, false, 300);
        NewFrontage(sh, "Old Money Rd", "oldmoney", new Vector3(ElmX, 0, SHSouthZ), Zf, -X, 50f, 130f, -5f, 215f, 40f, true, true, 300);
        NewFrontage(sh, "Pinky-Up Place", "pinkyup", new Vector3(SHEastX, 0, SHSouthZ), Zf, -X, 50f, 130f, 10f, 172f, 40f, true, false);
        NewFrontage(sh, "Old Money Rd", "oldmoney", new Vector3(ElmX, 0, BirchZ), Zf, X, 0f, 0f, 14f, SHGateZ - BirchZ - 12f, 0f, false, true, 0, false);
        NewFrontage(sh, "Old Money Rd", "oldmoney", new Vector3(ElmX, 0, BirchZ), Zf, -X, 0f, 0f, 14f, SHGateZ - BirchZ - 12f, 0f, true, true, 0, false);
        // Hedges: south (gate gap on Old Money Rd), east, and the shared north line (Bluffs gate gap; stone, built by the Bluffs).
        sh.walls.Add((new Vector3(-60f, 0, SHGateZ), new Vector3(ElmX - GateHalf, 0, SHGateZ)));
        sh.walls.Add((new Vector3(ElmX + GateHalf, 0, SHGateZ), new Vector3(220f, 0, SHGateZ)));
        sh.walls.Add((new Vector3(220f, 0, SHGateZ), new Vector3(220f, 0, SHTopZ)));
        c.districts.Add(sh);

        // ---------------- Billionaire Bluffs (far north; tier 4) ----------------
        var bb = new DistrictSpec
        {
            id = Districts.BillionaireBluffs, prefix = "bb-", label = "Billionaire Bluffs", tier = 4, lotTarget = 72f,
            bounds = new Rect2(-20f, BBGateZ, 340f, 640f),
            housePool = new[] { "SM_house_suburb_006", "SM_house_suburb_007" }, houseWeights = new[] { 1f, 1f },
            scaleMin = 1.6f, scaleMax = 1.6f, setbackMin = 12f, setbackMax = 16f,
            bandShare = new[] { 0.5f, 0.5f },
            bandRange = new[] { new Vector2(0.60f, 0.85f), new Vector2(0.85f, 1.00f) },
            capMin = 6, capMax = 6, gatePos = new Vector3(0f, 0f, BBGateZ), gateYaw = 0f, wallStyle = WallStyle.Stone,
            lampPrefab = "Props/SM_streetlamp_03", treeA = "SM_tree_town_01", treeB = "SM_tree_town_01",
        };
        bb.roads.Add(RoadZ(ElmX, SHNorthZ + RoadHalfWidth, BBAveZ + RoadHalfWidth));               // Tax Haven Heights Rd
        bb.roads.Add(RoadX(BBAveZ, ElmX - RoadHalfWidth, BBBulbX));                                 // Offshore Account Ave
        bb.roads.Add(new Rect2(BBBulbX - 12f, BBAveZ - 12f, BBBulbX + 12f, BBAveZ + 12f));          // Yacht Turnaround
        bb.lines.Add(("TaxHaven", new Vector3(ElmX, 0, SHNorthZ + RoadHalfWidth + 2f), new Vector3(ElmX, 0, BBAveZ - RoadHalfWidth)));
        bb.lines.Add(("Offshore", new Vector3(ElmX + RoadHalfWidth, 0, BBAveZ), new Vector3(BBBulbX - 13f, 0, BBAveZ)));
        NewFrontage(bb, "Offshore Account Ave", "offshore", new Vector3(ElmX, 0, BBAveZ), X, Zf, 12f, 300f, 10f, 300f, 70f, false, true);
        NewFrontage(bb, "Offshore Account Ave", "offshore", new Vector3(ElmX, 0, BBAveZ), X, -Zf, 12f, 300f, 10f, 300f, 70f, true, true);
        NewFrontage(bb, "Tax Haven Heights Rd", "taxhaven", new Vector3(ElmX, 0, SHNorthZ), Zf, X, 0f, 0f, 10f, BBGateZ - SHNorthZ - 12f, 0f, false, true, 0, false);
        NewFrontage(bb, "Tax Haven Heights Rd", "taxhaven", new Vector3(ElmX, 0, SHNorthZ), Zf, -X, 0f, 0f, 10f, BBGateZ - SHNorthZ - 12f, 0f, true, true, 0, false);
        bb.walls.Add((new Vector3(-60f, 0, BBGateZ), new Vector3(ElmX - GateHalf, 0, BBGateZ)));
        bb.walls.Add((new Vector3(ElmX + GateHalf, 0, BBGateZ), new Vector3(340f, 0, BBGateZ)));
        bb.walls.Add((new Vector3(-20f, 0, BBGateZ), new Vector3(-20f, 0, 640f)));
        bb.walls.Add((new Vector3(340f, 0, BBGateZ), new Vector3(340f, 0, 640f)));
        c.districts.Add(bb);

        foreach (var d in c.districts) d.seed = c.seed ^ PickupVisuals.StableHash(d.id);
    }

    /// <summary>Build every new district (after district 1 is done), each with its own RNG.</summary>
    static void BuildDistricts(Ctx c)
    {
        c.gDistricts = Group(c.root, "Districts");
        // New roads cut through a few district-1 verge spots (Birch St's north verge at Old Money Rd): clear them.
        int cleared = ClearRoadFootprints(c);
        var mainRng = c.rng;
        foreach (var d in c.districts)
        {
            c.rng = new System.Random(d.seed);
            d.root = Group(c.gDistricts, d.label.Replace(" ", ""));
            d.gLots = Group(d.root, "Lots");
            d.gStreet = Group(d.root, "StreetFurniture");
            d.gProps = Group(d.root, "Props");
            BuildDistrictMarkings(c, d);
            int lotsBefore = c.lots.Count;
            foreach (var f in d.frontages) BuildDistrictFrontage(c, d, f);
            var furn = new List<Frontage>(d.frontages);
            furn.AddRange(d.verges);
            FurnishFrontages(c, furn, d.gStreet, d.lampPrefab, d.treeA, d.treeB, out int lamps, out int trees);
            switch (d.tier)
            {
                case 2: DressMeadowsCommons(c, d); break;
                case 3: DressSnootingtonGreen(c, d); break;
                case 4: DressBluffsCommons(c, d); break;
            }
            BuildDistrictStops(c, d, lotsBefore);
            BuildGate(c, d);
            foreach (var w in d.walls) WallLine(c, d.root, w.a, w.b, d.wallStyle);
            c.notes.AppendLine($"district {d.id}: {c.lots.Count - lotsBefore} houses, lamps {lamps}, street trees {trees}");
        }
        c.rng = mainRng;
        if (cleared > 0) c.notes.AppendLine($"cleared {cleared} district-1 verge props from new road footprints");
    }

    /// <summary>Delete district-1 street furniture standing on a new district road (+ its sidewalks).</summary>
    static int ClearRoadFootprints(Ctx c)
    {
        var kill = new List<GameObject>();
        foreach (Transform group in c.gStreet)
            foreach (Transform t in group)
            {
                var p = t.position;
                foreach (var d in c.districts)
                    foreach (var r in d.roads)
                        if (r.Expand(WalkWidth + CurbWidth + 0.6f).Contains(p.x, p.z)) { kill.Add(t.gameObject); goto next; }
                next:;
            }
        foreach (var g in kill) Object.DestroyImmediate(g);
        return kill.Count;
    }

    static void BuildDistrictMarkings(Ctx c, DistrictSpec d)
    {
        var g = Group(d.root, "Markings");
        foreach (var l in d.lines) DashLine(c, g, l.name, l.a, l.b, 2f, 2f, null);
    }

    // ------------------------------------------------------------------------------------------
    // District lots
    // ------------------------------------------------------------------------------------------

    static HouseDef HouseByPrefab(string prefab)
    {
        foreach (var h in Houses) if (h.prefab == prefab) return h;
        return Houses[0];
    }

    static void BuildDistrictFrontage(Ctx c, DistrictSpec d, Frontage f)
    {
        if (f.lot1 - f.lot0 < 10f) return;
        var segs = new List<Vector2> { new Vector2(f.lot0, f.lot1) };
        foreach (var r in f.reserved)
        {
            var next = new List<Vector2>();
            foreach (var s in segs)
            {
                if (r.y <= s.x || r.x >= s.y) { next.Add(s); continue; }
                if (r.x > s.x) next.Add(new Vector2(s.x, r.x));
                if (r.y < s.y) next.Add(new Vector2(r.y, s.y));
            }
            segs = next;
        }
        float target = f.lotWidth > 0f ? f.lotWidth : d.lotTarget;
        foreach (var seg in segs)
        {
            float L = seg.y - seg.x;
            int n = Mathf.Max(1, Mathf.FloorToInt(L / target + 0.001f));
            float w = L / n;
            for (int i = 0; i < n; i++)
            {
                if (d.tier >= 4) BuildEstateLot(c, d, f, seg.x + i * w, seg.x + (i + 1) * w);
                else BuildFancyLot(c, d, f, seg.x + i * w, seg.x + (i + 1) * w);
            }
        }
    }

    /// <summary>Pick a house from the district pool that fits the lot (scaled).</summary>
    static HouseDef PickDistrictHouse(Ctx c, DistrictSpec d, Frontage f, float lotW, float scale)
    {
        var options = new List<HouseDef>(); var weights = new List<float>(); float total = 0f;
        for (int i = 0; i < d.housePool.Length; i++)
        {
            var h = HouseByPrefab(d.housePool[i]);
            if (6f + h.Depth * scale + 1.5f > f.maxDepth) continue;
            if (2f * h.halfW * scale + 6.3f > lotW) continue;
            options.Add(h); weights.Add(d.houseWeights[i]); total += d.houseWeights[i];
        }
        if (options.Count == 0) return null;
        float roll = c.R(0f, total);
        for (int i = 0; i < options.Count; i++) { roll -= weights[i]; if (roll <= 0f) return options[i]; }
        return options[options.Count - 1];
    }

    /// <summary>A Meadows / Snootington lot: scaled house, side driveway (2 minivans or a gravel turnaround), path,
    /// bin + curb spot, mailbox, bushes, and the district's dressing.</summary>
    static void BuildFancyLot(Ctx c, DistrictSpec d, Frontage f, float s0, float s1)
    {
        float w = s1 - s0, sc = 0.5f * (s0 + s1), half = w * 0.5f;
        float scale = c.R(d.scaleMin, d.scaleMax);
        var h = PickDistrictHouse(c, d, f, w, scale);
        if (h == null) { scale = 1f; h = PickDistrictHouse(c, d, f, w, 1f); }
        if (h == null) return;
        int number = HouseNumber(f, sc);
        string stopId = $"{d.prefix}{f.key}-{number}";
        var lot = new GameObject($"Lot_{d.prefix}{f.key}_{number}").transform;
        lot.SetParent(d.gLots, false);
        lot.position = f.P(sc, PropertyLine, GrassTop);

        float hw = h.halfW * scale, depth = h.Depth * scale, zMax = h.zMax * scale;
        bool mirror = c.Chance(0.5f);
        float setback = Mathf.Min(c.R(d.setbackMin, d.setbackMax), f.maxDepth - depth - 1.5f);
        float frontD = PropertyLine + setback, centerD = frontD + zMax, backD = frontD + depth;
        float lotBackD = PropertyLine + f.maxDepth;
        Quaternion faceRoad = f.FaceRoad;
        float sx = mirror ? -1f : 1f;
        float alongSign = Mathf.Sign(Vector3.Dot(faceRoad * Vector3.right, f.along)) * sx;
        float doorRel = alongSign * h.doorX * scale;

        // Side driveway away from the front door (wide enough for two parked minivans in tandem).
        float side = Mathf.Abs(doorRel) > 0.3f ? -Mathf.Sign(doorRel) : c.Sign();
        float dwW = d.tier >= 3 ? 4.6f : 4.0f;
        float dwC = side * (half - 1.0f - dwW * 0.5f);
        float aHouse = dwC - side * (dwW * 0.5f + 1.4f + hw);
        if (Mathf.Abs(aHouse) + hw > half - 0.8f) aHouse = -side * (half - 0.8f - hw);
        bool hasDrive = Mathf.Abs(dwC - aHouse) >= dwW * 0.5f + hw + 0.3f;
        float dwEnd = frontD + Mathf.Min(10f, depth * 0.7f);
        float doorA = aHouse + doorRel;

        var housePos = f.P(sc + aHouse, centerD, GrassTop);
        var house = Place(c, Sub + "Buildings/" + h.prefab, lot, housePos, faceRoad, scale, mirror, LayerGround);
        TintHouse(c, house, d, c.rng.Next(0, 1000));
        var hr = Rect2.From(f.P(sc + aHouse - hw, frontD, 0), f.P(sc + aHouse + hw, backD, 0));
        var lr = Rect2.From(f.P(s0, PropertyLine, 0), f.P(s1, lotBackD, 0));

        if (hasDrive)
        {
            float mid = 0.5f * (8f + dwEnd);
            OBox(c, lot, "Driveway", f.P(sc + dwC, mid, 0.5f * (WalkTop + SlabBottom)), faceRoad,
                 new Vector3(dwW, WalkTop - SlabBottom, dwEnd - 8f), c.M(d.tier >= 3 ? "NB_Gravel" : "NB_Driveway"), LayerGround, true, false);
            CurbRamp(c, lot, f, sc + dwC, dwW + 1.2f);
            f.Occupy(sc + dwC, dwW * 0.5f + 0.8f);
        }
        // Garden path to the door.
        float pathEnd = frontD + 0.3f;
        if (pathEnd - 8f > 1f)
        {
            float mid = 0.5f * (8f + pathEnd);
            OBox(c, lot, "Path", f.P(sc + doorA, mid, 0.5f * (WalkTop + SlabBottom)), faceRoad,
                 new Vector3(1.5f, WalkTop - SlabBottom, pathEnd - 8f), c.M(d.tier >= 3 ? "NB_Stone" : "NB_ParkPath"), LayerGround, true, false);
            f.Occupy(sc + doorA, 1.0f);
        }

        // Bin + curb spot on the sidewalk (bags sit outside the hedge gates in Snootington), mailbox on the verge.
        float bs = dwC >= 0f ? -1f : 1f;
        float binA = dwC + bs * (dwW * 0.5f + 1.0f);
        float stopA = binA + bs * 1.8f;
        if (Mathf.Abs(stopA) > half - 1.2f) { bs = -bs; binA = dwC + bs * (dwW * 0.5f + 1.0f); stopA = binA + bs * 1.8f; }
        var bin = Place(c, Sub + "Props/SM_garbagecan_0" + c.RI(1, 3), lot, f.P(sc + binA, BinD, WalkTop), faceRoad * Yaw(180f + c.R(-10f, 10f)), 1f, false, LayerDefault);
        KnockPrefab(c, bin, KnockKind.WheelieBin, stopId, ColMode.Box);
        float mbA = dwC - bs * (dwW * 0.5f + 0.7f);
        if (Mathf.Abs(mbA) > half - 0.4f) mbA = dwC + bs * (dwW * 0.5f + 0.7f) + bs * 0.9f;
        KnockPrefab(c, Place(c, Sub + "Props/SM_mailbox_0" + c.RI(1, 2), lot, f.P(sc + mbA, MailboxD, GrassTop), faceRoad, 1f, false, LayerDefault), KnockKind.Mailbox, stopId, ColMode.Box);
        f.Occupy(sc + mbA, 0.7f);

        // Bushes along the house front.
        int bushes = c.RI(2, 4);
        for (int k = 0, tries = 0; k < bushes && tries < 16; tries++)
        {
            float a = aHouse + c.R(-hw + 1f, hw - 1f);
            if (Mathf.Abs(a - doorA) < 2f || (hasDrive && Mathf.Abs(a - dwC) < dwW * 0.5f + 1.2f)) continue;
            k++;
            Place(c, Sub + (c.Chance(0.6f) ? "Nature/SM_bush_02" : "Nature/SM_bush_03"), lot, f.P(sc + a, frontD - c.R(0.7f, 1.2f), GrassTop), Yaw(c.R(0f, 360f)), c.R(0.9f, 1.3f), false, LayerDefault);
        }

        var ctxLot = new FancyLot
        {
            lot = lot, f = f, sc = sc, half = half, s0 = s0, s1 = s1, aHouse = aHouse, hw = hw, dwC = dwC, dwW = dwW, dwEnd = dwEnd,
            hasDrive = hasDrive, frontD = frontD, backD = backD, lotBackD = lotBackD, doorA = doorA, faceRoad = faceRoad, h = h, scale = scale,
            house = house, stopId = stopId,
        };
        if (d.tier == 2) DressMeadowsLot(c, d, ctxLot);
        else DressSnootyLot(c, d, ctxLot);

        var info = new LotInfo
        {
            f = f, number = number, house = h, lotRect = lr, houseRect = hr, name = lot.name,
            stopPos = f.P(sc + stopA, StopD, WalkTop), stopRot = faceRoad, bin = bin != null ? bin.transform : null, district = d,
            stopId = stopId, sc = sc, half = half, aHouse = aHouse, doorA = doorA, dwC = dwC, dwW = dwW, dwEnd = dwEnd,
            frontD = frontD, doorD = centerD - h.doorZ * scale, hasDrive = hasDrive, mailboxPos = f.P(sc + mbA, MailboxD, GrassTop),
            houseGo = house,
        };
        info.cars.AddRange(ctxLot.cars);
        c.lots.Add(info);
        c.developed.Add(lr.Expand(0.5f));
        d.lotIndex++;
    }

    /// <summary>Everything a lot dresser needs to know about the lot it decorates.</summary>
    sealed class FancyLot
    {
        public Transform lot; public Frontage f; public float sc, half, s0, s1, aHouse, hw, dwC, dwW, dwEnd, frontD, backD, lotBackD, doorA;
        public bool hasDrive; public Quaternion faceRoad; public HouseDef h; public float scale; public GameObject house;
        public string stopId;
        public readonly List<CarRec> cars = new List<CarRec>();
        public Vector3 P(float along, float d, float y) => f.P(sc + along, d, y);
    }

    /// <summary>Pastel / cream / white-and-gold walls: swap the house's "M_wall*" slot for a district tint (and gold roofs in
    /// the Bluffs). Material variants are assets under Generated/Neighborhood (prefab overrides in the scene).</summary>
    static void TintHouse(Ctx c, GameObject house, DistrictSpec d, int pick)
    {
        if (house == null) return;
        string[] tints = d.tier switch
        {
            2 => new[] { "NB_WallPink", "NB_WallMint", "NB_WallSky", "NB_WallLemon", "NB_WallLilac", "NB_WallPeach" },
            3 => new[] { "NB_WallCream", "NB_WallIvory", "NB_WallCream", "NB_WallSage" },
            _ => new[] { "NB_WallWhite" },
        };
        var wallMat = c.M(tints[pick % tints.Length]);
        foreach (var r in house.GetComponentsInChildren<MeshRenderer>(true))
        {
            var mats = r.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null) continue;
                string n = mats[i].name;
                if (n.StartsWith("M_wall")) { mats[i] = wallMat; changed = true; }
                else if (d.tier >= 4 && n == "M_roof") { mats[i] = c.M("NB_GoldRoof"); changed = true; }
                else if (d.tier >= 3 && (n == "M_doors")) { mats[i] = c.M(d.tier >= 4 ? "NB_Gold" : "NB_DoorNavy"); changed = true; }
            }
            if (changed) r.sharedMaterials = mats;
        }
    }

    // ------------------------------------------------------------------------------------------
    // Minivan Meadows dressing
    // ------------------------------------------------------------------------------------------

    static readonly string[] YardSigns =
    {
        "LIVE LAUGH\nLOAD", "BLESSED\n(by the HOA)", "PROUD PARENT\nof a Soccer Kid", "THIS HOUSE\nRECYCLES\n(sometimes)",
        "NO SOLICITING\n(we have Scrapazon)", "LIVE LAUGH\nLAUNDRY",
    };

    static void DressMeadowsLot(Ctx c, DistrictSpec d, FancyLot L)
    {
        var lot = L.lot;
        // Two stretched "minivans" on the driveway (beige or teal), parked in tandem. The RNG draws stay (byte-stable);
        // BuildHouseholdCars parks them nose-in as WorldLife household cars.
        if (L.hasDrive && L.dwEnd - 8f >= 6.5f)
        {
            int vans = L.dwEnd - 8f >= 11f ? 2 : 1;
            c.Chance(0.5f);              // the old parking-direction roll (household cars park nose-in)
            for (int v = 0; v < vans; v++)
            {
                int model = c.RI(1, 4);
                string tint = c.Chance(0.5f) ? "NB_VanBeige" : "NB_VanTeal";
                L.cars.Add(new CarRec { model = model, tintKey = tint, scale = new Vector3(1.15f, 1.12f, 1.05f) });
            }
        }
        // White picket fence along the property line, with gaps for the driveway and the path.
        PicketFront(c, lot, L, PropertyLine + 0.4f);

        // Backyard: trampoline / pool; driveway hoop; yard sign; the odd lemonade stand on the verge.
        float yard = L.lotBackD - L.backD;
        int roll = c.RI(0, 9);
        if (yard >= 7f && roll < 5)
        {
            float a = Mathf.Clamp(L.aHouse + c.R(-3f, 3f), -L.half + 3.6f, L.half - 3.6f);
            Trampoline(c, lot, L.P(a, L.backD + Mathf.Min(yard - 3.5f, 4.2f), GrassTop), c.R(0f, 360f));
        }
        else if (yard >= 8f && roll < 8)
        {
            float a = Mathf.Clamp(L.aHouse + c.R(-2f, 2f), -L.half + 4.6f, L.half - 4.6f);
            Place(c, Sub + "Props/SM_swimming_pool", lot, L.P(a, L.backD + 4.4f, GrassTop), L.faceRoad, 1f, false, LayerGround);
        }
        if (L.hasDrive && c.Chance(0.45f))
            BasketballHoop(c, lot, L.P(L.dwC + Mathf.Sign(L.dwC) * (L.dwW * 0.5f + 0.25f), L.dwEnd - 0.4f, WalkTop), L.faceRoad, L.stopId);
        if (c.Chance(0.55f))
        {
            float a = L.doorA + (L.doorA > L.aHouse ? 2.4f : -2.4f);
            if (Mathf.Abs(a) < L.half - 1f) YardSign(c, lot, L.P(a, PropertyLine + 2.2f, GrassTop), L.faceRoad, c.Pick(YardSigns), L.stopId);
        }
        if (c.Chance(0.12f))
        {
            float a = Mathf.Clamp(-L.dwC * 0.6f, -L.half + 2f, L.half - 2f);
            if (L.f.VergeFree(L.sc + a, 1.4f))
            {
                KnockPrefab(c, Place(c, Sub + "Props/SM_lemonade_shop", lot, L.P(a, PropertyLine + 1.4f, GrassTop), L.faceRoad * Yaw(180f), 1f, false, LayerDefault), KnockKind.LemonadeStand, L.stopId, ColMode.Box);
            }
        }
        // A front-yard tree now and then.
        if (c.Chance(0.3f))
        {
            float a = -Mathf.Sign(L.dwC) * (L.half - c.R(3f, 4f));
            if (Mathf.Abs(a - L.doorA) > 3f) Tree(c, lot, "SM_tree_town_02", L.P(a, PropertyLine + 2.6f, GrassTop), c.R(0.55f, 0.7f));
        }
    }

    static void TintCar(Ctx c, GameObject car, string matKey)
    {
        foreach (var r in car.GetComponentsInChildren<MeshRenderer>(true))
        {
            var mats = r.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < mats.Length; i++)
                if (mats[i] != null && mats[i].name.StartsWith("M_car_")) { mats[i] = c.M(matKey); changed = true; }
            if (changed) r.sharedMaterials = mats;
        }
    }

    /// <summary>White picket fence along the lot front (property line), leaving the driveway and path open.</summary>
    static void PicketFront(Ctx c, Transform lot, FancyLot L, float d)
    {
        var gaps = new List<Vector2>();
        if (L.hasDrive) gaps.Add(new Vector2(L.dwC - L.dwW * 0.5f - 0.3f, L.dwC + L.dwW * 0.5f + 0.3f));
        gaps.Add(new Vector2(L.doorA - 1.1f, L.doorA + 1.1f));
        var segs = new List<Vector2> { new Vector2(-L.half + 0.3f, L.half - 0.3f) };
        foreach (var gp in gaps)
        {
            var next = new List<Vector2>();
            foreach (var s in segs)
            {
                if (gp.y <= s.x || gp.x >= s.y) { next.Add(s); continue; }
                if (gp.x > s.x) next.Add(new Vector2(s.x, gp.x));
                if (gp.y < s.y) next.Add(new Vector2(gp.y, s.y));
            }
            segs = next;
        }
        foreach (var s in segs)
            if (s.y - s.x > 0.8f) PicketRun(c, lot, L.P(s.x, d, GrassTop), L.P(s.y, d, GrassTop), false, L.stopId);
    }

    /// <summary>A white picket run from a to b: 4 m picket-panel meshes (one shared mesh asset) + one collider for the run.
    /// With <paramref name="knockStopId"/> (lot fronts, WorldLife §6.1) every panel instead gets its own box collider and
    /// becomes Knockable; district perimeter fences (WallLine) stay one hard run.</summary>
    static void PicketRun(Ctx c, Transform parent, Vector3 a, Vector3 b, bool collider = true, string knockStopId = null)
    {
        Vector3 dlt = b - a; dlt.y = 0f;
        float len = dlt.magnitude;
        if (len < 0.5f) return;
        var rot = Quaternion.LookRotation(dlt / len);
        var g = new GameObject("Picket").transform;
        g.SetParent(parent, false);
        g.SetPositionAndRotation(a, rot);
        // Knockable lot fronts use 2 m panels (5 pickets) so a hit breaks off a section, not 4 m of fence.
        float panelLen = knockStopId != null ? KnockPicketPanelLength : PicketPanelLength;
        var mesh = PicketPanelMesh(panelLen);
        int n = Mathf.Max(1, Mathf.RoundToInt(len / panelLen));
        float each = len / n;
        for (int i = 0; i < n; i++)
        {
            var go = new GameObject("Panel");
            go.transform.SetParent(g, false);
            go.transform.localPosition = new Vector3(0f, 0f, each * i);
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = new Vector3(1f, 1f, each / panelLen);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = c.M("NB_White");
            if (knockStopId != null)
            {
                var pc = go.AddComponent<BoxCollider>();
                pc.center = new Vector3(0f, 0.55f, panelLen * 0.5f);
                pc.size = new Vector3(0.12f, 1.1f, panelLen);
                Knock(c, go, KnockKind.PicketPanel, "picket", knockStopId);
            }
        }
        if (!collider) return;
        // One collider for the whole run (people bump into it; trucks don't drive over it).
        var col = new GameObject("Collider");
        col.transform.SetParent(g, false);
        col.transform.localPosition = new Vector3(0f, 0.55f, len * 0.5f);
        col.AddComponent<BoxCollider>().size = new Vector3(0.12f, 1.1f, len);
    }

    const float PicketPanelLength = 4f, KnockPicketPanelLength = 2f;
    static readonly Dictionary<float, Mesh> picketMeshes = new Dictionary<float, Mesh>();

    /// <summary>A white picket-fence panel (a pointed picket every 0.4 m + 2 rails) along local +Z, saved once as a mesh
    /// asset: 4 m = NB_PicketPanel (district fences), 2 m = NB_PicketPanel2 (knockable lot fronts).</summary>
    static Mesh PicketPanelMesh(float length = PicketPanelLength)
    {
        if (picketMeshes.TryGetValue(length, out var cached) && cached != null) return cached;
        string meshName = Mathf.Approximately(length, PicketPanelLength) ? "NB_PicketPanel" : "NB_PicketPanel" + Mathf.RoundToInt(length);
        string meshPath = MatDir + "/" + meshName + ".asset";
        var existing = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
        if (existing != null) { picketMeshes[length] = existing; return existing; }
        int pickets = Mathf.Max(1, Mathf.RoundToInt(length / 0.4f));
        var v = new List<Vector3>(); var t = new List<int>();
        void Box(Vector3 min, Vector3 max)
        {
            Vector3[] p =
            {
                new Vector3(min.x, min.y, min.z), new Vector3(max.x, min.y, min.z), new Vector3(max.x, max.y, min.z), new Vector3(min.x, max.y, min.z),
                new Vector3(min.x, min.y, max.z), new Vector3(max.x, min.y, max.z), new Vector3(max.x, max.y, max.z), new Vector3(min.x, max.y, max.z),
            };
            int[][] faces = { new[] { 0, 3, 2, 1 }, new[] { 4, 5, 6, 7 }, new[] { 0, 4, 7, 3 }, new[] { 1, 2, 6, 5 }, new[] { 3, 7, 6, 2 }, new[] { 0, 1, 5, 4 } };
            foreach (var f in faces)
            {
                int i0 = v.Count;
                foreach (int k in f) v.Add(p[k]);
                t.Add(i0); t.Add(i0 + 1); t.Add(i0 + 2); t.Add(i0); t.Add(i0 + 2); t.Add(i0 + 3);
            }
        }
        void Tip(float z0, float z1, float y, float x)
        {
            // A little pointed roof on a picket (two slanted quads, flat shaded).
            float zm = 0.5f * (z0 + z1);
            Vector3 a0 = new Vector3(-x, y, z0), a1 = new Vector3(x, y, z0), b0 = new Vector3(-x, y, z1), b1 = new Vector3(x, y, z1);
            Vector3 p0 = new Vector3(-x, y + 0.09f, zm), p1 = new Vector3(x, y + 0.09f, zm);
            int i0 = v.Count; v.Add(a0); v.Add(p0); v.Add(p1); v.Add(a1); t.Add(i0); t.Add(i0 + 1); t.Add(i0 + 2); t.Add(i0); t.Add(i0 + 2); t.Add(i0 + 3);
            i0 = v.Count; v.Add(b1); v.Add(p1); v.Add(p0); v.Add(b0); t.Add(i0); t.Add(i0 + 1); t.Add(i0 + 2); t.Add(i0); t.Add(i0 + 2); t.Add(i0 + 3);
            i0 = v.Count; v.Add(a0); v.Add(b0); v.Add(p0); t.Add(i0); t.Add(i0 + 1); t.Add(i0 + 2);
            i0 = v.Count; v.Add(a1); v.Add(p1); v.Add(b1); t.Add(i0); t.Add(i0 + 1); t.Add(i0 + 2);
        }
        Box(new Vector3(-0.025f, 0.28f, 0f), new Vector3(0.025f, 0.35f, length));
        Box(new Vector3(-0.025f, 0.74f, 0f), new Vector3(0.025f, 0.81f, length));
        float pitch = length / pickets;
        for (int i = 0; i < pickets; i++)
        {
            float z0 = pitch * i + (pitch - 0.09f) * 0.5f, z1 = z0 + 0.09f;
            Box(new Vector3(-0.018f, 0f, z0), new Vector3(0.018f, 0.98f, z1));
            Tip(z0, z1, 0.98f, 0.018f);
        }
        var m = new Mesh { name = meshName };
        m.SetVertices(v);
        m.SetTriangles(t, 0);
        m.RecalculateNormals();
        m.RecalculateBounds();
        EnsureFolder(MatDir);
        AssetDatabase.CreateAsset(m, meshPath);
        picketMeshes[length] = m;
        return m;
    }

    static void Trampoline(Ctx c, Transform parent, Vector3 at, float yaw)
    {
        var g = new GameObject("Trampoline").transform;
        g.SetParent(parent, false);
        g.SetPositionAndRotation(at, Yaw(yaw));
        const float r = 1.9f, hgt = 0.85f;
        OPrim(c, g, PrimitiveType.Cylinder, "Rim", at + Vector3.up * hgt, Quaternion.identity, new Vector3(r * 2f + 0.3f, 0.06f, r * 2f + 0.3f), c.M("NB_TrampBlue"), true);
        OPrim(c, g, PrimitiveType.Cylinder, "Mat", at + Vector3.up * (hgt + 0.035f), Quaternion.identity, new Vector3(r * 2f, 0.03f, r * 2f), c.M("NB_Rubber"), false);
        for (int i = 0; i < 6; i++)
        {
            var dir = Quaternion.Euler(0f, yaw + i * 60f, 0f) * Vector3.forward;
            OBox(c, g, "Leg", at + dir * r + Vector3.up * hgt * 0.5f, Quaternion.identity, new Vector3(0.07f, hgt, 0.07f), c.M("NB_Metal"), LayerDefault, false);
            OBox(c, g, "NetPole", at + dir * (r + 0.1f) + Vector3.up * (hgt + 0.9f), Quaternion.identity, new Vector3(0.05f, 1.8f, 0.05f), c.M("NB_TrampBlue"), LayerDefault, false);
        }
        OPrim(c, g, PrimitiveType.Cylinder, "NetTop", at + Vector3.up * (hgt + 1.8f), Quaternion.identity, new Vector3(r * 2f + 0.25f, 0.025f, r * 2f + 0.25f), c.M("NB_NetBlack"), false);
    }

    static void BasketballHoop(Ctx c, Transform parent, Vector3 at, Quaternion faceRoad, string stopId = null)
    {
        var g = new GameObject("Hoop").transform;
        g.SetParent(parent, false);
        g.SetPositionAndRotation(at, faceRoad);
        Vector3 fwd = faceRoad * Vector3.forward;
        OBox(c, g, "Pole", at + Vector3.up * 1.6f, faceRoad, new Vector3(0.12f, 3.2f, 0.12f), c.M("NB_Metal"), LayerDefault, true);
        OBox(c, g, "Board", at + Vector3.up * 3.15f + fwd * 0.35f, faceRoad, new Vector3(1.2f, 0.8f, 0.05f), c.M("NB_White"), LayerDefault, false);
        OBox(c, g, "Square", at + Vector3.up * 3.05f + fwd * 0.38f, faceRoad, new Vector3(0.45f, 0.35f, 0.02f), c.M("NB_Ribbon"), LayerDefault, false);
        OPrim(c, g, PrimitiveType.Cylinder, "Ring", at + Vector3.up * 2.95f + fwd * 0.62f, Quaternion.identity, new Vector3(0.46f, 0.015f, 0.46f), c.M("NB_Orange"), false);
        Knock(c, g.gameObject, KnockKind.BasketballHoop, "hoop", stopId);
    }

    static void YardSign(Ctx c, Transform parent, Vector3 at, Quaternion faceRoad, string text, string stopId = null)
    {
        var g = new GameObject("YardSign").transform;
        g.SetParent(parent, false);
        g.SetPositionAndRotation(at, faceRoad);
        Vector3 rgt = faceRoad * Vector3.right;
        OBox(c, g, "StakeL", at - rgt * 0.32f + Vector3.up * 0.4f, faceRoad, new Vector3(0.03f, 0.8f, 0.03f), c.M("NB_Metal"), LayerDefault, false);
        OBox(c, g, "StakeR", at + rgt * 0.32f + Vector3.up * 0.4f, faceRoad, new Vector3(0.03f, 0.8f, 0.03f), c.M("NB_Metal"), LayerDefault, false);
        SignBoard(c, g, "Board", at + Vector3.up * 0.82f, faceRoad * Yaw(180f), new Vector3(0.95f, 0.6f, 0.03f), c.M("NB_White"), text, new Color(0.35f, 0.25f, 0.5f), true);
        Knock(c, g.gameObject, KnockKind.Sign, "yardsign", stopId);
    }

    /// <summary>The HOA commons between the two cul-de-sacs, the Mile's signs and the HOA's gate dressing.</summary>
    static void DressMeadowsCommons(Ctx c, DistrictSpec d)
    {
        var g = d.gProps;
        // A grass strip and a sign between the two cul-de-sacs that almost touch.
        float midZ = 0.5f * (MMBulbAZ + MMBulbBZ);
        // (faces both cul-de-sacs: along the 2 m grass strip it no longer overhangs their sidewalks — WorldLife walk lanes)
        SignPost(c, g, new Vector3(MMSpurX, GrassTop, midZ), Yaw(0f), "NO THRU TRAFFIC\n<size=60%>(HOA rule 47b)</size>", c.M("NB_White"), new Color(0.7f, 0.12f, 0.1f));
        // Mile signs: one at the depot, one at the gate.
        SignPost(c, g, new Vector3(DepotPad.x1 + 6f, GrassTop, MileZ + RoadHalfWidth + 4.2f), Yaw(90f), "MINIVAN MILE\n<size=55%>Minivan Meadows ->\nDEPOT ROW: shops on your right</size>", c.M("NB_SignGreen"), Color.white);
        SignPost(c, g, new Vector3(MMGateX - 14f, GrassTop, MileZ + RoadHalfWidth + 4.2f), Yaw(90f), "SLOW\n<size=55%>CHILDREN, MINIVANS,\nCHILDREN IN MINIVANS</size>", c.M("NB_SignYellow"), Color.black);
        StreetSign(c, g, new Vector3(MMWestX + 6.4f, WalkTop, MileZ + 6.4f), "Carpool Ln", "Trampoline Ct");
        StreetSign(c, g, new Vector3(MMEastX - 6.4f, WalkTop, MileZ + 6.4f), "Carpool Ln", "Soccer Mom Way");
        StreetSign(c, g, new Vector3(MMWestX + 6.4f, WalkTop, MMNorthZ - 6.4f), "Juice Box Blvd", "Trampoline Ct");
        StreetSign(c, g, new Vector3(MMSpurX + 6.6f, WalkTop, MileZ + 6.4f), "Carpool Ln", "Gluten-Free Ct");
        StreetSign(c, g, new Vector3(MMSpurX + 6.6f, WalkTop, MMNorthZ - 6.4f), "Juice Box Blvd", "Playdate Pl");
        // A few lemonade stands along Juice Box Blvd's verge (it's in the name).
        KnockPrefab(c, Place(c, Sub + "Props/SM_lemonade_shop", g, new Vector3(MMWestX + 12f, GrassTop, MMNorthZ - 8.9f), Yaw(0f), 1f, false, LayerDefault), KnockKind.LemonadeStand, null, ColMode.Box);
        c.developed.Add(new Rect2(MMSpurX - 14f, MMBulbAZ + MMBulbHalf, MMSpurX + 14f, MMBulbBZ - MMBulbHalf));
    }

    static void SignPost(Ctx c, Transform parent, Vector3 at, Quaternion rot, string text, Material board, Color textColor)
    {
        var g = new GameObject("Sign").transform;
        g.SetParent(parent, false);
        g.SetPositionAndRotation(at, rot);
        Vector3 rgt = rot * Vector3.right;
        OBox(c, g, "PostL", at - rgt * 1.1f + Vector3.up * 1.2f, rot, new Vector3(0.12f, 2.4f, 0.12f), c.M("NB_Wood"), LayerDefault, true);
        OBox(c, g, "PostR", at + rgt * 1.1f + Vector3.up * 1.2f, rot, new Vector3(0.12f, 2.4f, 0.12f), c.M("NB_Wood"), LayerDefault, true);
        SignBoard(c, g, "Board", at + Vector3.up * 2.1f, rot, new Vector3(2.6f, 1.3f, 0.08f), board, text, textColor, true);
        Knock(c, g.gameObject, KnockKind.Sign, "signpost", null);
    }

    // ------------------------------------------------------------------------------------------
    // Snootington Hills dressing
    // ------------------------------------------------------------------------------------------

    static void DressSnootyLot(Ctx c, DistrictSpec d, FancyLot L)
    {
        var lot = L.lot;
        // 3 m boxy hedges along the property line, with gaps for the drive and a gate on the path.
        HedgeFront(c, lot, L, PropertyLine + 0.9f);
        // A pillared porch in front of the door.
        Vector3 rgt = L.faceRoad * Vector3.right;
        for (int i = -1; i <= 1; i += 2)
            OPrim(c, lot, PrimitiveType.Cylinder, "PorchPillar", L.P(L.doorA, L.frontD - 1.6f, GrassTop) + rgt * (1.5f * i) + Vector3.up * 2.4f,
                  Quaternion.identity, new Vector3(0.45f, 2.4f, 0.45f), c.M("NB_White"), true);
        OBox(c, lot, "Pediment", L.P(L.doorA, L.frontD - 1.4f, GrassTop) + Vector3.up * 4.9f, L.faceRoad, new Vector3(4.2f, 0.4f, 1.9f), c.M("NB_White"), LayerDefault, false);
        // Topiary poodle on the lawn, a fountain, or both.
        float lawn = L.frontD - PropertyLine - 1.5f;
        if (lawn > 3f)
        {
            float a = -Mathf.Sign(L.dwC) * Mathf.Min(L.half - 3f, Mathf.Abs(L.aHouse) + L.hw * 0.5f + 2f);
            if (c.Chance(0.6f)) TopiaryPoodle(c, lot, L.P(a, PropertyLine + 1.2f + lawn * 0.5f, GrassTop), c.R(0f, 360f));
            else Fountain(c, lot, L.P(a, PropertyLine + 1.2f + lawn * 0.5f, GrassTop), 1f);
        }
        // Gravel turnaround at the top of the drive.
        if (L.hasDrive)
            OPrim(c, lot, PrimitiveType.Cylinder, "Turnaround", L.P(L.dwC - Mathf.Sign(L.dwC) * 1.5f, L.dwEnd - 2f, WalkTop - 0.03f), Quaternion.identity,
                  new Vector3(7f, 0.03f, 7f), c.M("NB_Gravel"), false);
        // Backyard: hot tub or a tennis court.
        float yard = L.lotBackD - L.backD;
        if (yard >= 10f && c.Chance(0.4f)) TennisCourt(c, lot, L.P(Mathf.Clamp(L.aHouse, -L.half + 7f, L.half - 7f), L.backD + yard * 0.5f, GrassTop), L.faceRoad, 0.6f);
        else if (yard >= 4f) Place(c, Sub + "Props/SM_whirlpool", lot, L.P(L.aHouse + c.R(-3f, 3f), L.backD + 2.2f, GrassTop), L.faceRoad, 1.2f, false, LayerGround);
    }

    static void HedgeFront(Ctx c, Transform lot, FancyLot L, float d)
    {
        var gaps = new List<Vector2>();
        if (L.hasDrive) gaps.Add(new Vector2(L.dwC - L.dwW * 0.5f - 0.4f, L.dwC + L.dwW * 0.5f + 0.4f));
        gaps.Add(new Vector2(L.doorA - 1.0f, L.doorA + 1.0f));
        var segs = new List<Vector2> { new Vector2(-L.half + 0.4f, L.half - 0.4f) };
        foreach (var gp in gaps)
        {
            var next = new List<Vector2>();
            foreach (var s in segs)
            {
                if (gp.y <= s.x || gp.x >= s.y) { next.Add(s); continue; }
                if (gp.x > s.x) next.Add(new Vector2(s.x, gp.x));
                if (gp.y < s.y) next.Add(new Vector2(gp.y, s.y));
            }
            segs = next;
        }
        foreach (var s in segs)
        {
            if (s.y - s.x < 0.8f) continue;
            float mid = 0.5f * (s.x + s.y);
            OBox(c, lot, "Hedge", L.P(mid, d, GrassTop) + Vector3.up * 1.5f, L.faceRoad, new Vector3(s.y - s.x, 3f, 1.4f), c.M("NB_Hedge"), LayerDefault, true);
        }
        // The hedge gate on the path: two small stone pillars with ball caps.
        for (int i = -1; i <= 1; i += 2)
        {
            Vector3 p = L.P(L.doorA + 1.05f * i, d, GrassTop);
            OBox(c, lot, "GatePillar", p + Vector3.up * 1.6f, L.faceRoad, new Vector3(0.5f, 3.2f, 0.5f), c.M("NB_Stone"), LayerDefault, true);
            OPrim(c, lot, PrimitiveType.Sphere, "Ball", p + Vector3.up * 3.45f, Quaternion.identity, Vector3.one * 0.5f, c.M("NB_Stone"), false);
        }
    }

    /// <summary>A topiary poodle: sphere clusters (body, head, ears, pompom tail, ankle poms).</summary>
    static void TopiaryPoodle(Ctx c, Transform parent, Vector3 at, float yaw)
    {
        var g = new GameObject("TopiaryPoodle").transform;
        g.SetParent(parent, false);
        g.SetPositionAndRotation(at, Yaw(yaw));
        var hedge = c.M("NB_Hedge");
        Quaternion r = Yaw(yaw);
        Vector3 P(float x, float y, float z) => at + r * new Vector3(x, y, z);
        OBox(c, g, "Plinth", P(0, 0.2f, 0), r, new Vector3(1.6f, 0.4f, 2.4f), c.M("NB_Stone"), LayerDefault, true);
        OPrim(c, g, PrimitiveType.Sphere, "Body", P(0, 1.35f, -0.2f), r, new Vector3(1.1f, 0.95f, 1.4f), hedge, false);
        OPrim(c, g, PrimitiveType.Sphere, "Chest", P(0, 1.5f, 0.55f), r, new Vector3(1.05f, 1.05f, 0.95f), hedge, false);
        OPrim(c, g, PrimitiveType.Sphere, "Head", P(0, 2.35f, 0.75f), r, Vector3.one * 0.8f, hedge, false);
        OPrim(c, g, PrimitiveType.Sphere, "Snout", P(0, 2.25f, 1.2f), r, new Vector3(0.38f, 0.32f, 0.5f), hedge, false);
        OPrim(c, g, PrimitiveType.Sphere, "Topknot", P(0, 2.8f, 0.7f), r, Vector3.one * 0.5f, hedge, false);
        OPrim(c, g, PrimitiveType.Sphere, "EarL", P(-0.42f, 2.2f, 0.7f), r, new Vector3(0.3f, 0.55f, 0.3f), hedge, false);
        OPrim(c, g, PrimitiveType.Sphere, "EarR", P(0.42f, 2.2f, 0.7f), r, new Vector3(0.3f, 0.55f, 0.3f), hedge, false);
        OPrim(c, g, PrimitiveType.Sphere, "Tail", P(0, 2.05f, -0.95f), r, Vector3.one * 0.55f, hedge, false);
        foreach (var lx in new[] { -0.3f, 0.3f })
            foreach (var lz in new[] { -0.6f, 0.45f })
            {
                OBox(c, g, "Leg", P(lx, 0.65f, lz), r, new Vector3(0.12f, 0.6f, 0.12f), c.M("NB_Wood"), LayerDefault, false);
                OPrim(c, g, PrimitiveType.Sphere, "Pom", P(lx, 0.55f, lz), r, Vector3.one * 0.36f, hedge, false);
            }
    }

    static void Fountain(Ctx c, Transform parent, Vector3 at, float scale)
    {
        var g = new GameObject("Fountain").transform;
        g.SetParent(parent, false);
        g.position = at;
        var stone = c.M("NB_Stone");
        OPrim(c, g, PrimitiveType.Cylinder, "Basin", at + Vector3.up * 0.3f * scale, Quaternion.identity, new Vector3(3.6f, 0.3f, 3.6f) * scale, stone, true);
        OPrim(c, g, PrimitiveType.Cylinder, "Water", at + Vector3.up * 0.58f * scale, Quaternion.identity, new Vector3(3.2f, 0.02f, 3.2f) * scale, c.M("NB_Water"), false);
        OPrim(c, g, PrimitiveType.Cylinder, "Column", at + Vector3.up * 1.1f * scale, Quaternion.identity, new Vector3(0.4f, 0.8f, 0.4f) * scale, stone, false);
        OPrim(c, g, PrimitiveType.Cylinder, "Bowl", at + Vector3.up * 1.95f * scale, Quaternion.identity, new Vector3(1.5f, 0.12f, 1.5f) * scale, stone, false);
        OPrim(c, g, PrimitiveType.Sphere, "Spout", at + Vector3.up * 2.35f * scale, Quaternion.identity, new Vector3(0.6f, 0.9f, 0.6f) * scale, c.M("NB_Water"), false);
    }

    static void TennisCourt(Ctx c, Transform parent, Vector3 at, Quaternion rot, float scale)
    {
        var g = new GameObject("TennisCourt").transform;
        g.SetParent(parent, false);
        g.SetPositionAndRotation(at, rot);
        float W = 10.97f * scale, Lg = 23.77f * scale;
        OBox(c, g, "Court", at + Vector3.up * 0.02f, rot * Yaw(90f), new Vector3(W + 2f, 0.04f, Lg + 3f), c.M("NB_Tennis"), LayerDefault, false);
        var white = c.M("NB_LineWhite");
        var rr = rot * Yaw(90f);
        Vector3 Q(float x, float z) => at + rr * new Vector3(x, 0.05f, z);
        OBox(c, g, "LineL", Q(-W * 0.5f, 0), rr, new Vector3(0.08f, 0.01f, Lg), white, LayerDefault, false);
        OBox(c, g, "LineR", Q(W * 0.5f, 0), rr, new Vector3(0.08f, 0.01f, Lg), white, LayerDefault, false);
        OBox(c, g, "LineB", Q(0, -Lg * 0.5f), rr, new Vector3(W, 0.01f, 0.08f), white, LayerDefault, false);
        OBox(c, g, "LineT", Q(0, Lg * 0.5f), rr, new Vector3(W, 0.01f, 0.08f), white, LayerDefault, false);
        OBox(c, g, "LineC", Q(0, 0), rr, new Vector3(0.06f, 0.01f, Lg * 0.55f), white, LayerDefault, false);
        OBox(c, g, "Net", at + rr * new Vector3(0, 0.5f, 0), rr, new Vector3(W + 1f, 0.9f, 0.04f), c.M("NB_NetBlack"), LayerDefault, true);
    }

    /// <summary>"Snootington Green": the private park inside the loop (tennis, a hedge maze, a big fountain, a croquet lawn).</summary>
    static void DressSnootingtonGreen(Ctx c, DistrictSpec d)
    {
        var g = d.gProps;
        var green = new Rect2(ElmX + PropertyLine + 40f, SHSouthZ + PropertyLine + 40f, SHEastX - PropertyLine - 40f, SHNorthZ - PropertyLine - 40f);
        Slab(c, g, "GreenPath_EW", new Rect2(green.x0 + 2f, 0.5f * (green.z0 + green.z1) - 1.3f, green.x1 - 2f, 0.5f * (green.z0 + green.z1) + 1.3f), WalkTop, c.M("NB_Gravel"));
        Fountain(c, g, new Vector3(0.5f * (green.x0 + green.x1), WalkTop, 0.5f * (green.z0 + green.z1)), 1.8f);
        TennisCourt(c, g, new Vector3(green.x0 + 18f, GrassTop, green.z0 + 14f), Yaw(0f), 1f);
        HedgeMaze(c, g, new Vector3(green.x1 - 20f, GrassTop, green.z1 - 18f), 5, 4, 3.4f, c.M("NB_Hedge"));
        SignPost(c, g, new Vector3(green.x0 + 3f, GrassTop, 0.5f * (green.z0 + green.z1) + 3f), Yaw(-90f), "SNOOTINGTON GREEN\n<size=55%>PRIVATE. VERY PRIVATE.\nPinkies up past this sign.</size>", c.M("NB_SignGreen"), new Color(1f, 0.92f, 0.6f));
        for (int i = 0; i < 6; i++)
            TopiaryPoodle(c, g, new Vector3(c.R(green.x0 + 4f, green.x1 - 4f), GrassTop, c.R(green.z0 + 26f, green.z1 - 30f)), c.R(0f, 360f));
        c.developed.Add(green);
        // Birch St becomes a 4-way at Elm Ave / Old Money Rd: a crosswalk and a street sign on the new north leg.
        var zebra = Place(c, Sub + "Modular/SM_pedestrian_crossing", g, new Vector3(ElmX, 0.006f, BirchZ + RoadHalfWidth + 2.2f), Yaw(90f), 1f, false, LayerDefault);
        if (zebra != null) { DisableColliders(zebra); NoShadows(zebra); }
        StreetSign(c, g, new Vector3(ElmX + 6.4f, WalkTop, BirchZ + 6.4f), "Birch St", "Old Money Rd");
        StreetSign(c, g, new Vector3(ElmX + 6.4f, WalkTop, SHSouthZ + 6.4f), "Hedgerow Cres", "Old Money Rd");
        StreetSign(c, g, new Vector3(ElmX + 6.4f, WalkTop, SHNorthZ - 6.4f), "Monocle Mews", "Old Money Rd");
        // Old Money Rd's welcome sign outside the gate.
        SignPost(c, g, new Vector3(ElmX + RoadHalfWidth + 6f, GrassTop, SHGateZ - 14f), Yaw(180f), "SNOOTINGTON HILLS\n<size=50%>Est. 1887 · Please Wipe Your Tires</size>", c.M("NB_DoorNavy"), new Color(1f, 0.88f, 0.5f));
    }

    /// <summary>A little hedge maze: a grid of walls with a few knocked out (deterministic from the district RNG).</summary>
    static void HedgeMaze(Ctx c, Transform parent, Vector3 center, int cols, int rows, float cell, Material hedge)
    {
        var g = new GameObject("HedgeMaze").transform;
        g.SetParent(parent, false);
        g.position = center;
        float W = cols * cell, D = rows * cell;
        Vector3 o = center - new Vector3(W * 0.5f, 0f, D * 0.5f);
        // Outer walls with an entrance and an exit.
        OBox(c, g, "N", o + new Vector3(W * 0.5f, 1.2f, D), Quaternion.identity, new Vector3(W, 2.4f, 0.8f), hedge, LayerDefault, true);
        OBox(c, g, "S_L", o + new Vector3(cell * 0.75f, 1.2f, 0f), Quaternion.identity, new Vector3(cell * 1.5f - 1f, 2.4f, 0.8f), hedge, LayerDefault, true);
        OBox(c, g, "S_R", o + new Vector3(W * 0.5f + cell, 1.2f, 0f), Quaternion.identity, new Vector3(W - cell * 2f, 2.4f, 0.8f), hedge, LayerDefault, true);
        OBox(c, g, "W", o + new Vector3(0f, 1.2f, D * 0.5f), Quaternion.identity, new Vector3(0.8f, 2.4f, D), hedge, LayerDefault, true);
        OBox(c, g, "E", o + new Vector3(W, 1.2f, D * 0.5f - cell * 0.5f), Quaternion.identity, new Vector3(0.8f, 2.4f, D - cell), hedge, LayerDefault, true);
        for (int x = 1; x < cols; x++)
            for (int z = 0; z < rows; z++)
                if (c.Chance(0.55f)) OBox(c, g, "In", o + new Vector3(x * cell, 1.2f, (z + 0.5f) * cell), Quaternion.identity, new Vector3(0.7f, 2.4f, cell), hedge, LayerDefault, true);
        for (int z = 1; z < rows; z++)
            for (int x = 0; x < cols; x++)
                if (c.Chance(0.35f)) OBox(c, g, "In", o + new Vector3((x + 0.5f) * cell, 1.2f, z * cell), Quaternion.identity, new Vector3(cell, 2.4f, 0.7f), hedge, LayerDefault, true);
    }

    // ------------------------------------------------------------------------------------------
    // Billionaire Bluffs: mega-estates
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// A Bluffs mega-estate: a two-wing mansion (006 + 007 side by side at 1.6×, white and gold), a long gravel drive with
    /// a turnaround circle, and 2–3 of: helipad, giant gold statue of the owner (on a moat island), a yacht parked on the
    /// lawn, a hedge maze, an infinity pool, a rocket pad. A butler holds a silver platter at the curb.
    /// </summary>
    static void BuildEstateLot(Ctx c, DistrictSpec d, Frontage f, float s0, float s1)
    {
        float w = s1 - s0, sc = 0.5f * (s0 + s1), half = w * 0.5f;
        int number = HouseNumber(f, sc);
        var lot = new GameObject($"Lot_{d.prefix}{f.key}_{number}").transform;
        lot.SetParent(d.gLots, false);
        lot.position = f.P(sc, PropertyLine, GrassTop);
        const float scale = 1.6f;
        var hA = HouseByPrefab("SM_house_suburb_006");
        var hB = HouseByPrefab("SM_house_suburb_007");
        float wingW = hA.halfW * scale;                      // 14.3
        float depth = hA.Depth * scale;                      // 30.1
        float zMax = hA.zMax * scale;
        float setback = Mathf.Min(c.R(d.setbackMin, d.setbackMax), f.maxDepth - depth - 6f);
        float frontD = PropertyLine + setback, centerD = frontD + zMax, backD = frontD + depth;
        float lotBackD = PropertyLine + f.maxDepth;
        Quaternion faceRoad = f.FaceRoad;
        Vector3 P(float a, float dd, float y) => f.P(sc + a, dd, y);
        float aHouse = c.R(-2f, 2f);

        // Two wings, side by side (the west wing mirrored so the doors face the middle).
        var wingL = Place(c, Sub + "Buildings/" + hA.prefab, lot, P(aHouse - wingW, centerD, GrassTop), faceRoad, scale, false, LayerGround);
        var wingR = Place(c, Sub + "Buildings/" + hB.prefab, lot, P(aHouse + wingW, centerD, GrassTop), faceRoad, scale, true, LayerGround);
        TintHouse(c, wingL, d, 0); TintHouse(c, wingR, d, 0);
        // A gold-trimmed connecting hall between the wings.
        var hall = OBox(c, lot, "Hall", P(aHouse, frontD + depth * 0.45f, GrassTop + 4f), faceRoad, new Vector3(4f, 8f, depth * 0.6f), c.M("NB_WallWhite"), LayerGround, true);
        OBox(c, lot, "HallTrim", P(aHouse, frontD + depth * 0.45f, GrassTop + 8.15f), faceRoad, new Vector3(4.4f, 0.3f, depth * 0.62f), c.M("NB_Gold"), LayerDefault, false);
        var hr = Rect2.From(P(aHouse - 2f * wingW, frontD, 0), P(aHouse + 2f * wingW, backD, 0));
        var lr = Rect2.From(f.P(s0, PropertyLine, 0), f.P(s1, lotBackD, 0));

        // Long gravel drive from the curb to a turnaround circle in front of the hall.
        float dwC = (c.Chance(0.5f) ? -1f : 1f) * (half - 4.5f);
        float dwW = 5f;
        OBox(c, lot, "Drive", P(dwC, 0.5f * (8f + frontD - 2f), 0.5f * (WalkTop + SlabBottom)), faceRoad,
             new Vector3(dwW, WalkTop - SlabBottom, frontD - 2f - 8f), c.M("NB_Gravel"), LayerGround, true, false);
        OBox(c, lot, "DriveLink", P(0.5f * (dwC + aHouse), frontD - 4f, 0.5f * (WalkTop + SlabBottom)), faceRoad,
             new Vector3(Mathf.Abs(dwC - aHouse) + dwW, WalkTop - SlabBottom, 4f), c.M("NB_Gravel"), LayerGround, true, false);
        CurbRamp(c, lot, f, sc + dwC, dwW + 1.4f);
        f.Occupy(sc + dwC, dwW * 0.5f + 0.8f);
        // Gold gate posts at the drive.
        for (int i = -1; i <= 1; i += 2)
        {
            Vector3 gp = P(dwC + i * (dwW * 0.5f + 0.6f), PropertyLine + 0.6f, GrassTop);
            OBox(c, lot, "DrivePost", gp + Vector3.up * 1.4f, faceRoad, new Vector3(0.7f, 2.8f, 0.7f), c.M("NB_Stone"), LayerDefault, true);
            OPrim(c, lot, PrimitiveType.Sphere, "GoldBall", gp + Vector3.up * 3.1f, Quaternion.identity, Vector3.one * 0.7f, c.M("NB_Gold"), false);
        }

        // Bin + curb spot beside the drive; a butler with a silver platter guards the bags.
        float bs = dwC >= 0f ? -1f : 1f;
        float binA = dwC + bs * (dwW * 0.5f + 1.2f);
        float stopA = binA + bs * 1.8f;
        string stopId = $"{d.prefix}{f.key}-{number}";
        var bin = Place(c, Sub + "Props/SM_garbagecan_0" + c.RI(1, 3), lot, P(binA, BinD, WalkTop), faceRoad * Yaw(180f), 1f, false, LayerDefault);
        KnockPrefab(c, bin, KnockKind.WheelieBin, stopId, ColMode.Box);
        // The butler stands between the curb slots' row and the sidewalk lane (WorldLife: people walk past at 7.4 m).
        var butlerPost = new Pose(P(stopA + bs * 2.6f, 6.5f, WalkTop), faceRoad);
        Butler(c, lot, butlerPost.position, faceRoad);

        // Low stone wall with gold caps along the frontage (gap for the drive).
        float gapL = dwC - dwW * 0.5f - 1.4f, gapR = dwC + dwW * 0.5f + 1.4f;
        foreach (var seg in new[] { new Vector2(-half + 0.5f, gapL), new Vector2(gapR, half - 0.5f) })
        {
            if (seg.y - seg.x < 1f) continue;
            float mid = 0.5f * (seg.x + seg.y);
            OBox(c, lot, "EstateWall", P(mid, PropertyLine + 0.5f, GrassTop) + Vector3.up * 0.6f, faceRoad, new Vector3(seg.y - seg.x, 1.2f, 0.6f), c.M("NB_Stone"), LayerDefault, true);
            OBox(c, lot, "EstateWallCap", P(mid, PropertyLine + 0.5f, GrassTop) + Vector3.up * 1.25f, faceRoad, new Vector3(seg.y - seg.x, 0.12f, 0.7f), c.M("NB_Gold"), LayerDefault, false);
        }

        // 2–3 features, cycling so every estate type shows up across the 8 lots.
        int k = d.lotIndex;
        float lawnA = -dwC * 0.55f;                        // the lawn side, away from the drive
        float lawnD = PropertyLine + 2f + (frontD - PropertyLine - 6f) * 0.5f;
        float yardD = backD + (lotBackD - backD) * 0.5f;
        float yardA = aHouse + c.R(-6f, 6f);
        switch (k % 4)
        {
            case 0: GoldStatue(c, lot, P(lawnA, lawnD, GrassTop), faceRoad, true); Helipad(c, lot, P(yardA, yardD, GrassTop)); break;
            case 1: Yacht(c, lot, P(lawnA, lawnD, GrassTop), faceRoad * Yaw(c.R(60f, 120f))); InfinityPool(c, lot, P(yardA, yardD, GrassTop), faceRoad); break;
            case 2: HedgeMaze(c, lot, P(lawnA, lawnD, GrassTop), 4, 3, 3.2f, c.M("NB_Hedge")); RocketPad(c, lot, P(yardA, yardD, GrassTop)); break;
            default: GoldStatue(c, lot, P(lawnA, lawnD, GrassTop), faceRoad, false); Yacht(c, lot, P(yardA, yardD, GrassTop), faceRoad * Yaw(90f)); break;
        }
        if (k >= 4)
        {
            if (k % 2 == 0) InfinityPool(c, lot, P(yardA + (yardA > aHouse ? -18f : 18f), yardD, GrassTop), faceRoad);
            else Helipad(c, lot, P(yardA + (yardA > aHouse ? -18f : 18f), yardD, GrassTop));
        }
        // Tall trees in the corners of the yard.
        Tree(c, lot, "SM_tree_town_01", P(-half + 4f, lotBackD - 4f, GrassTop), c.R(1f, 1.25f));
        Tree(c, lot, "SM_tree_town_01", P(half - 4f, lotBackD - 4f, GrassTop), c.R(1f, 1.25f));

        var info = new LotInfo
        {
            f = f, number = number, house = hA, lotRect = lr, houseRect = hr, name = lot.name,
            stopPos = P(stopA, StopD, WalkTop), stopRot = faceRoad, bin = bin != null ? bin.transform : null, district = d,
            stopId = stopId, sc = sc, half = half, aHouse = aHouse, doorA = aHouse, dwC = dwC, dwW = dwW,
            dwEnd = frontD - 2f, frontD = frontD, doorD = frontD + depth * 0.15f, hasDrive = true, estate = true,
            houseGo = hall, walkA = dwC + bs * 1.6f, butlerPost = butlerPost,
        };
        c.lots.Add(info);
        c.developed.Add(lr.Expand(0.5f));
        d.lotIndex++;
    }

    static void Butler(Ctx c, Transform parent, Vector3 at, Quaternion faceRoad)
    {
        var g = new GameObject("Butler").transform;
        g.SetParent(parent, false);
        g.SetPositionAndRotation(at, faceRoad);
        Vector3 F(float x, float y, float z) => at + faceRoad * new Vector3(x, y, z);
        var tux = c.M("NB_Tux");
        OBox(c, g, "LegL", F(-0.12f, 0.45f, 0), faceRoad, new Vector3(0.18f, 0.9f, 0.2f), tux, LayerDefault, false);
        OBox(c, g, "LegR", F(0.12f, 0.45f, 0), faceRoad, new Vector3(0.18f, 0.9f, 0.2f), tux, LayerDefault, false);
        var body = OPrim(c, g, PrimitiveType.Capsule, "Body", F(0, 1.3f, 0), faceRoad, new Vector3(0.62f, 0.55f, 0.42f), tux, true);
        OBox(c, g, "Shirt", F(0, 1.35f, 0.18f), faceRoad, new Vector3(0.22f, 0.5f, 0.06f), c.M("NB_White"), LayerDefault, false);
        OBox(c, g, "BowTie", F(0, 1.62f, 0.21f), faceRoad, new Vector3(0.2f, 0.07f, 0.04f), tux, LayerDefault, false);
        OPrim(c, g, PrimitiveType.Sphere, "Head", F(0, 1.95f, 0), faceRoad, Vector3.one * 0.42f, c.M("NB_Skin"), false);
        OPrim(c, g, PrimitiveType.Sphere, "Hair", F(0, 2.09f, -0.04f), faceRoad, new Vector3(0.44f, 0.2f, 0.42f), c.M("NB_Tux"), false);
        OPrim(c, g, PrimitiveType.Sphere, "Mustache", F(0, 1.88f, 0.19f), faceRoad, new Vector3(0.2f, 0.05f, 0.06f), c.M("NB_Tux"), false);
        OBox(c, g, "Arm", F(0.34f, 1.45f, 0.22f), faceRoad, new Vector3(0.13f, 0.13f, 0.5f), tux, LayerDefault, false);
        OPrim(c, g, PrimitiveType.Cylinder, "Platter", F(0.34f, 1.53f, 0.5f), faceRoad, new Vector3(0.62f, 0.015f, 0.62f), c.M("NB_Silver"), false);
        OPrim(c, g, PrimitiveType.Sphere, "Cloche", F(0.34f, 1.6f, 0.5f), faceRoad, new Vector3(0.42f, 0.28f, 0.42f), c.M("NB_Silver"), false);
        _ = body;
    }

    static void GoldStatue(Ctx c, Transform parent, Vector3 at, Quaternion faceRoad, bool moat)
    {
        var g = new GameObject("GoldStatue").transform;
        g.SetParent(parent, false);
        g.SetPositionAndRotation(at, faceRoad);
        var gold = c.M("NB_Gold");
        Vector3 F(float x, float y, float z) => at + faceRoad * new Vector3(x, y, z);
        if (moat)
        {
            OPrim(c, g, PrimitiveType.Cylinder, "Moat", at + Vector3.up * 0.02f, Quaternion.identity, new Vector3(12f, 0.02f, 12f), c.M("NB_Water"), false);
            OPrim(c, g, PrimitiveType.Cylinder, "Island", at + Vector3.up * 0.15f, Quaternion.identity, new Vector3(7f, 0.15f, 7f), c.M("NB_Grass"), true);
        }
        OBox(c, g, "Pedestal", F(0, 1.2f, 0), faceRoad, new Vector3(3.2f, 2.4f, 3.2f), c.M("NB_Stone"), LayerDefault, true);
        OBox(c, g, "Plaque", F(0, 1.4f, 1.62f), faceRoad, new Vector3(1.8f, 0.6f, 0.04f), gold, LayerDefault, false);
        OPrim(c, g, PrimitiveType.Capsule, "Body", F(0, 4.6f, 0), faceRoad, new Vector3(1.9f, 2.2f, 1.4f), gold, false);
        OPrim(c, g, PrimitiveType.Sphere, "Head", F(0, 7.4f, 0.1f), faceRoad, new Vector3(1.6f, 1.75f, 1.6f), gold, false);
        OPrim(c, g, PrimitiveType.Sphere, "Chin", F(0, 6.8f, 0.45f), faceRoad, new Vector3(1.1f, 0.6f, 0.8f), gold, false);
        OPrim(c, g, PrimitiveType.Capsule, "ArmUp", F(1.15f, 6.9f, 0.1f), faceRoad * Quaternion.Euler(0, 0, -25f), new Vector3(0.5f, 1.3f, 0.5f), gold, false);
        OPrim(c, g, PrimitiveType.Capsule, "ArmHip", F(-1.05f, 4.9f, 0.1f), faceRoad * Quaternion.Euler(0, 0, 40f), new Vector3(0.45f, 0.9f, 0.45f), gold, false);
        OPrim(c, g, PrimitiveType.Cylinder, "Crown", F(0, 8.35f, 0.1f), faceRoad, new Vector3(1.0f, 0.3f, 1.0f), gold, false);
        for (int i = 0; i < 5; i++)
            OPrim(c, g, PrimitiveType.Sphere, "CrownPoint", F(Mathf.Cos(i * 1.2566f) * 0.45f, 8.7f, 0.1f + Mathf.Sin(i * 1.2566f) * 0.45f), faceRoad, Vector3.one * 0.22f, gold, false);
    }

    static void Helipad(Ctx c, Transform parent, Vector3 at)
    {
        var g = new GameObject("Helipad").transform;
        g.SetParent(parent, false);
        g.position = at;
        OPrim(c, g, PrimitiveType.Cylinder, "Pad", at + Vector3.up * 0.08f, Quaternion.identity, new Vector3(12f, 0.08f, 12f), c.M("NB_AsphaltLot"), true);
        OPrim(c, g, PrimitiveType.Cylinder, "Ring", at + Vector3.up * 0.165f, Quaternion.identity, new Vector3(10f, 0.01f, 10f), c.M("NB_LineYellow"), false);
        OPrim(c, g, PrimitiveType.Cylinder, "Inner", at + Vector3.up * 0.17f, Quaternion.identity, new Vector3(9.2f, 0.01f, 9.2f), c.M("NB_AsphaltLot"), false);
        var white = c.M("NB_LineWhite");
        OBox(c, g, "H_L", at + new Vector3(-1.4f, 0.18f, 0f), Quaternion.identity, new Vector3(0.6f, 0.01f, 4f), white, LayerDefault, false);
        OBox(c, g, "H_R", at + new Vector3(1.4f, 0.18f, 0f), Quaternion.identity, new Vector3(0.6f, 0.01f, 4f), white, LayerDefault, false);
        OBox(c, g, "H_M", at + new Vector3(0f, 0.18f, 0f), Quaternion.identity, new Vector3(2.4f, 0.01f, 0.6f), white, LayerDefault, false);
    }

    static void Yacht(Ctx c, Transform parent, Vector3 at, Quaternion rot)
    {
        var g = new GameObject("YachtOnTheLawn").transform;
        g.SetParent(parent, false);
        g.SetPositionAndRotation(at, rot);
        Vector3 F(float x, float y, float z) => at + rot * new Vector3(x, y, z);
        var white = c.M("NB_White");
        OBox(c, g, "Cradle", F(0, 0.4f, 0), rot, new Vector3(2.2f, 0.8f, 9f), c.M("NB_Wood"), LayerDefault, true);
        OBox(c, g, "Hull", F(0, 1.8f, -0.5f), rot, new Vector3(3.6f, 2f, 10f), white, LayerDefault, true);
        OPrim(c, g, PrimitiveType.Cylinder, "Bow", F(0, 1.8f, 4.6f), rot * Quaternion.Euler(0, 45f, 0), new Vector3(2.55f, 1f, 2.55f), white, false);
        OBox(c, g, "Stripe", F(0, 1.5f, -0.5f), rot, new Vector3(3.65f, 0.25f, 10.02f), c.M("NB_DoorNavy"), LayerDefault, false);
        OBox(c, g, "Cabin", F(0, 3.4f, -1.6f), rot, new Vector3(2.8f, 1.3f, 5f), white, LayerDefault, false);
        OBox(c, g, "Windows", F(0, 3.5f, -1.6f), rot, new Vector3(2.85f, 0.45f, 4.6f), c.M("NB_Glass"), LayerDefault, false);
        OBox(c, g, "Flybridge", F(0, 4.35f, -2.2f), rot, new Vector3(2.4f, 0.6f, 3f), white, LayerDefault, false);
        OBox(c, g, "Mast", F(0, 6f, -2.4f), rot, new Vector3(0.15f, 3.2f, 0.15f), c.M("NB_Metal"), LayerDefault, false);
        foreach (float sgn in new[] { -1f, 1f })
        {
            var name = new GameObject("Name").transform;
            name.SetParent(g, false);
            name.SetPositionAndRotation(F(sgn * 1.83f, 2.05f, -1.2f), rot * Yaw(sgn * 90f));
            Label(name, "Text", "SS TAX WRITE-OFF", Vector3.zero, Quaternion.identity, new Vector2(6f, 0.8f), new Color(0.12f, 0.2f, 0.45f));
        }
    }

    static void InfinityPool(Ctx c, Transform parent, Vector3 at, Quaternion rot)
    {
        var g = new GameObject("InfinityPool").transform;
        g.SetParent(parent, false);
        g.SetPositionAndRotation(at, rot);
        OBox(c, g, "Deck", at + Vector3.up * 0.12f, rot, new Vector3(16f, 0.24f, 8f), c.M("NB_Stone"), LayerDefault, true);
        OBox(c, g, "Water", at + Vector3.up * 0.25f, rot, new Vector3(14f, 0.04f, 6f), c.M("NB_Water"), LayerDefault, false);
        OBox(c, g, "Edge", at + rot * new Vector3(0f, 0.27f, 3.05f), rot, new Vector3(14f, 0.05f, 0.1f), c.M("NB_Gold"), LayerDefault, false);
    }

    static void RocketPad(Ctx c, Transform parent, Vector3 at)
    {
        var g = new GameObject("RocketPad").transform;
        g.SetParent(parent, false);
        g.position = at;
        OPrim(c, g, PrimitiveType.Cylinder, "Pad", at + Vector3.up * 0.15f, Quaternion.identity, new Vector3(8f, 0.15f, 8f), c.M("NB_Concrete"), true);
        OPrim(c, g, PrimitiveType.Cylinder, "Hazard", at + Vector3.up * 0.31f, Quaternion.identity, new Vector3(6f, 0.01f, 6f), c.M("NB_LineYellow"), false);
        OPrim(c, g, PrimitiveType.Cylinder, "Rocket", at + Vector3.up * 4.3f, Quaternion.identity, new Vector3(1.4f, 4f, 1.4f), c.M("NB_White"), true);
        OPrim(c, g, PrimitiveType.Capsule, "Nose", at + Vector3.up * 8.6f, Quaternion.identity, new Vector3(1.4f, 1.2f, 1.4f), c.M("NB_Ribbon"), false);
        OPrim(c, g, PrimitiveType.Sphere, "Window", at + new Vector3(0f, 6.2f, 0.62f), Quaternion.identity, new Vector3(0.6f, 0.6f, 0.3f), c.M("NB_Glass"), false);
        for (int i = 0; i < 3; i++)
        {
            var dir = Quaternion.Euler(0f, i * 120f, 0f) * Vector3.forward;
            OBox(c, g, "Fin", at + dir * 0.95f + Vector3.up * 1.3f, Quaternion.LookRotation(dir), new Vector3(0.1f, 1.6f, 1.1f), c.M("NB_Ribbon"), LayerDefault, false);
        }
    }

    /// <summary>The Bluffs' welcome sign and a gold-trimmed fountain at the turnaround.</summary>
    static void DressBluffsCommons(Ctx c, DistrictSpec d)
    {
        var g = d.gProps;
        Fountain(c, g, new Vector3(BBBulbX, WalkTop, BBAveZ), 1.4f);
        OPrim(c, g, PrimitiveType.Cylinder, "FountainIsland", new Vector3(BBBulbX, 0.08f, BBAveZ), Quaternion.identity, new Vector3(6f, 0.08f, 6f), c.M("NB_Grass"), true);
        SignPost(c, g, new Vector3(ElmX + RoadHalfWidth + 6.5f, GrassTop, BBGateZ + 16f), Yaw(180f), "BILLIONAIRE BLUFFS\n<size=50%>Speed limit: whatever you can afford</size>", c.M("NB_Gold"), new Color(0.1f, 0.08f, 0.05f));
    }

    // ------------------------------------------------------------------------------------------
    // District stops, gates, walls
    // ------------------------------------------------------------------------------------------

    static void BuildDistrictStops(Ctx c, DistrictSpec d, int lotsBefore)
    {
        var route = c.gRoute;
        var mine = new List<LotInfo>();
        for (int i = lotsBefore; i < c.lots.Count; i++) mine.Add(c.lots[i]);
        mine.Sort((a, b) => string.CompareOrdinal(StopIdFor(a), StopIdFor(b)));
        foreach (var l in mine)
        {
            string id = StopIdFor(l);
            var rng = new System.Random(unchecked(c.seed * 486187739 + PickupVisuals.StableHash(id)));
            var go = new GameObject("Stop_" + id);
            go.transform.SetParent(route, false);
            go.transform.SetPositionAndRotation(l.stopPos, l.stopRot);
            var stop = go.AddComponent<PickupStop>();
            stop.stopId = id;
            stop.address = $"{l.number} {l.f.street}";
            stop.bagCount = Mathf.Clamp(d.capMin + rng.Next(0, d.capMax - d.capMin + 1), 1, PickupStop.MaxSlots);
            double roll = rng.NextDouble();
            int band = d.bandShare.Length - 1;
            for (int b = 0; b < d.bandShare.Length; b++) { if (roll < d.bandShare[b]) { band = b; break; } roll -= d.bandShare[b]; }
            var range = d.bandRange[band];
            stop.wealth = range.x + (float)rng.NextDouble() * (range.y - range.x);
            stop.onRoute = true;
            stop.districtId = d.id;
            stop.bin = l.bin;
            stop.setBack = 0.3f;
            l.wealthBand = band;
            c.stops.Add(stop);
        }
    }

    /// <summary>The district's gate on its access road: vehicle barrier (layer Walls), turnstile, pillars, signs, ribbon,
    /// InteractionZone, and a DistrictGate driving it. Root +Z points into the district.</summary>
    static void BuildGate(Ctx c, DistrictSpec d)
    {
        var rot = Yaw(d.gateYaw);
        var root = new GameObject("Gate_" + d.id).transform;
        root.SetParent(d.root, false);
        root.SetPositionAndRotation(d.gatePos, rot);
        c.dynamicRoots.Add(root);
        Vector3 L(float x, float y, float z) => d.gatePos + rot * new Vector3(x, y, z);

        var gate = root.gameObject.AddComponent<DistrictGate>();
        gate.districtId = d.id;
        c.gates.Add(gate);

        // Ribbon line trigger (district side) on the root.
        var trig = root.gameObject.AddComponent<BoxCollider>();
        trig.isTrigger = true;
        trig.center = new Vector3(0f, 1.6f, 3.2f);
        trig.size = new Vector3(GateRoadHalf * 2f, 3.2f, 2f);

        // [E] zone on the approach side.
        var zone = root.gameObject.AddComponent<InteractionZone>();
        zone.mode = ZoneMode.Either;
        zone.center = new Vector3(0f, 2f, -5.5f);
        zone.size = new Vector3(GateHalf * 2f, 6f, 9f);
        zone.score = 1.2f;

        Material pillarMat = d.tier == 2 ? c.M("NB_WallPink") : c.M("NB_Stone");
        // Pillars at the edge of the vehicle opening; the left sidewalk is blocked, the right has the turnstile.
        foreach (float sgn in new[] { -1f, 1f })
        {
            OBox(c, root, "Pillar", L(sgn * (GateRoadHalf + 0.4f), d.tier == 2 ? 3.2f : 2.4f, 0f), rot, new Vector3(0.8f, d.tier == 2 ? 6.4f : 4.8f, 0.8f), pillarMat, LayerWalls, true);
            if (d.tier >= 3) OPrim(c, root, PrimitiveType.Sphere, "PillarBall", L(sgn * (GateRoadHalf + 0.4f), 5.15f, 0f), Quaternion.identity, Vector3.one * 0.8f, d.tier >= 4 ? c.M("NB_Gold") : c.M("NB_Stone"), false);
        }
        OBox(c, root, "SidewalkBlock", L(-(GateRoadHalf + 0.8f + 1.0f), 1.3f, 0f), rot, new Vector3(2.0f, 2.6f, 0.6f), d.tier == 2 ? c.M("NB_White") : d.tier == 3 ? c.M("NB_Hedge") : c.M("NB_Stone"), LayerWalls, true);
        // Turnstile on the right sidewalk: a post and three arms (people squeeze by; a truck never fits).
        Vector3 tp = L(GateRoadHalf + 0.8f + 1.0f, 0f, 0f);
        OBox(c, root, "TurnstilePost", tp + Vector3.up * 0.55f, rot, new Vector3(0.18f, 1.1f, 0.18f), c.M("NB_Metal"), LayerDefault, true);
        for (int i = 0; i < 3; i++)
            OBox(c, root, "TurnstileArm", tp + Vector3.up * 0.95f + rot * (Quaternion.Euler(0f, i * 120f, 0f) * new Vector3(0f, 0f, 0.3f)), rot * Quaternion.Euler(0f, i * 120f, 0f), new Vector3(0.05f, 0.05f, 0.6f), c.M("NB_Metal"), LayerDefault, false);
        OBox(c, root, "TurnstileEnd", L(GateHalf - 0.1f, 1.3f, 0f), rot, new Vector3(0.2f, 2.6f, 0.6f), pillarMat, LayerWalls, true);

        // The barrier collider (enabled while locked) spans the vehicle opening.
        var barrier = new GameObject("GateBarrier");
        barrier.transform.SetParent(root, false);
        barrier.layer = LayerWalls;
        var bc = barrier.AddComponent<BoxCollider>();
        bc.center = new Vector3(0f, 2f, 0f);
        bc.size = new Vector3(GateRoadHalf * 2f, 4f, 0.8f);
        gate.barrierCollider = bc;

        // Moving parts per style.
        if (d.tier == 3)
        {
            // Wrought-iron double gate: two leaves hinged at the pillars, swinging into the district.
            gate.barrierPivot = IronLeaf(c, root, rot, d.gatePos, -GateRoadHalf, 1f);
            gate.barrierPivot2 = IronLeaf(c, root, rot, d.gatePos, GateRoadHalf, -1f);
            gate.closedEuler = Vector3.zero; gate.openEuler = new Vector3(0f, -100f, 0f);
            gate.closedEuler2 = Vector3.zero; gate.openEuler2 = new Vector3(0f, 100f, 0f);
        }
        else
        {
            // Swing arm (Meadows: pastel stripes) / boom barrier (Bluffs: red and white), hinged on the left.
            var pivot = new GameObject("ArmPivot").transform;
            pivot.SetParent(root, false);
            pivot.localPosition = new Vector3(-GateRoadHalf + 0.2f, 1.15f, -0.6f);
            int segs = 6;
            float armLen = GateRoadHalf * 2f - 0.6f;
            for (int i = 0; i < segs; i++)
            {
                var seg = OBox(c, pivot, "ArmSeg", Vector3.zero, Quaternion.identity, new Vector3(armLen / segs, 0.22f, 0.16f),
                               (i % 2 == 0) ? (d.tier == 2 ? c.M("NB_WallMint") : c.M("NB_Ribbon")) : c.M("NB_White"), LayerDefault, false);
                seg.transform.localPosition = new Vector3((i + 0.5f) * armLen / segs, 0f, 0f);
                seg.transform.localRotation = Quaternion.identity;
            }
            gate.barrierPivot = pivot;
            gate.closedEuler = Vector3.zero; gate.openEuler = new Vector3(0f, 0f, 82f);
        }

        // Signage + status board (approach side).
        var status = new GameObject("Status").transform;
        status.SetParent(root, false);
        status.localPosition = new Vector3(-(GateRoadHalf + 0.4f), 2.4f, -0.45f);
        status.localRotation = Quaternion.identity;
        var st = Label(status, "Text", "PERMIT ONLY", new Vector3(0f, 0f, -0.01f), Quaternion.identity, new Vector2(1.6f, 0.9f), Color.white);
        OBox(c, status, "Board", status.position + rot * new Vector3(0f, 0f, 0.03f), rot, new Vector3(1.8f, 1.0f, 0.05f), c.M("NB_Tux"), LayerDefault, false);
        gate.statusText = st;

        switch (d.tier)
        {
            case 2:
                OBox(c, root, "ArchBeam", L(0f, 6.6f, 0f), rot, new Vector3(GateRoadHalf * 2f + 2.4f, 1.0f, 0.7f), c.M("NB_WallMint"), LayerDefault, false);
                SignBoard(c, root, "ArchSignInside", L(0f, 6.6f, 0.4f), rot * Yaw(180f), new Vector3(GateRoadHalf * 2f + 1.6f, 0.9f, 0.08f), c.M("NB_White"),
                          "Drive safe! <size=70%>The HOA is watching. (They have binoculars.)</size>", new Color(0.3f, 0.5f, 0.75f), false);
                SignBoard(c, root, "ArchSign", L(0f, 6.6f, -0.4f), rot, new Vector3(GateRoadHalf * 2f + 1.6f, 0.9f, 0.08f), c.M("NB_White"),
                          "MINIVAN MEADOWS: <size=70%>Pop. 64 minivans</size>", new Color(0.85f, 0.3f, 0.55f), true);
                for (int i = -1; i <= 1; i += 2)
                    OPrim(c, root, PrimitiveType.Sphere, "Balloon", L(i * (GateRoadHalf + 0.4f), 7.6f, 0f), Quaternion.identity, new Vector3(0.7f, 0.85f, 0.7f), i < 0 ? c.M("NB_Ribbon") : c.M("NB_TrampBlue"), false);
                break;
            case 3:
                SignBoard(c, root, "Plaque", L(GateRoadHalf + 0.4f, 3.2f, -0.45f), rot, new Vector3(0.75f, 0.9f, 0.04f), c.M("NB_Gold"),
                          "SNOOTINGTON\nHILLS\n<size=60%>Est. 1887</size>", new Color(0.15f, 0.1f, 0.05f), false);
                break;
            default:
                // Guard booth on the approach side with a bored guard and his catchphrase.
                Vector3 bp = L(-(GateHalf + 2.2f), 0f, -2.5f);
                OBox(c, root, "Booth", bp + Vector3.up * 1.4f, rot, new Vector3(2.4f, 2.8f, 2.4f), c.M("NB_WallWhite"), LayerWalls, true);
                OBox(c, root, "BoothRoof", bp + Vector3.up * 2.95f, rot, new Vector3(2.9f, 0.3f, 2.9f), c.M("NB_Gold"), LayerDefault, false);
                OBox(c, root, "BoothWindow", bp + rot * new Vector3(1.21f, 1.7f, 0f), rot, new Vector3(0.05f, 0.9f, 1.6f), c.M("NB_Glass"), LayerDefault, false);
                Butler(c, root, bp + rot * new Vector3(1.6f, 0f, 0.2f), rot * Yaw(90f));
                var bubble = new GameObject("GuardSays").transform;
                bubble.SetParent(root, false);
                bubble.position = bp + rot * new Vector3(2.2f, 3.6f, -1.6f);
                bubble.rotation = rot;
                OBox(c, bubble, "Bubble", bubble.position + rot * new Vector3(0f, 0f, 0.04f), rot, new Vector3(3.9f, 0.62f, 0.04f), c.M("NB_White"), LayerDefault, false);
                Label(bubble, "Text", "\"Name? ...Garbage? ...Fine.\"", Vector3.zero, Quaternion.identity, new Vector2(3.7f, 0.5f), new Color(0.12f, 0.1f, 0.1f));
                break;
        }

        // The giant red ribbon (shown once unlocked, until the first drive-through snaps it).
        var ribbon = new GameObject("Ribbon").transform;
        ribbon.SetParent(root, false);
        ribbon.localPosition = new Vector3(0f, 1.3f, 2.6f);
        ribbon.localRotation = Quaternion.identity;
        var red = c.M("NB_Ribbon");
        var rl = OBox(c, ribbon, "RibbonL", Vector3.zero, Quaternion.identity, new Vector3(GateRoadHalf, 0.5f, 0.05f), red, LayerDefault, false);
        rl.transform.localPosition = new Vector3(-GateRoadHalf * 0.5f, 0f, 0f); rl.transform.localRotation = Quaternion.identity;
        var rr = OBox(c, ribbon, "RibbonR", Vector3.zero, Quaternion.identity, new Vector3(GateRoadHalf, 0.5f, 0.05f), red, LayerDefault, false);
        rr.transform.localPosition = new Vector3(GateRoadHalf * 0.5f, 0f, 0f); rr.transform.localRotation = Quaternion.identity;
        var bow = new GameObject("Bow").transform;
        bow.SetParent(ribbon, false);
        bow.localPosition = Vector3.zero;
        foreach (float sgn in new[] { -1f, 1f })
        {
            var lobe = OPrim(c, bow, PrimitiveType.Sphere, "Lobe", Vector3.zero, Quaternion.identity, new Vector3(0.9f, 0.6f, 0.25f), red, false);
            lobe.transform.localPosition = new Vector3(sgn * 0.5f, 0.15f, 0f);
            lobe.transform.localRotation = Quaternion.Euler(0f, 0f, sgn * 20f);
        }
        var knot = OPrim(c, bow, PrimitiveType.Sphere, "Knot", Vector3.zero, Quaternion.identity, Vector3.one * 0.35f, red, false);
        knot.transform.localPosition = Vector3.zero;
        var tag = new GameObject("GrandOpening").transform;
        tag.SetParent(bow, false);
        tag.localPosition = new Vector3(0f, -0.65f, -0.06f);
        Label(tag, "Text", "GRAND OPENING!", Vector3.zero, Quaternion.identity, new Vector2(2.6f, 0.45f), new Color(1f, 0.9f, 0.3f));
        gate.ribbon = ribbon.gameObject;
        ribbon.gameObject.SetActive(false);    // DistrictGate shows it at runtime once the permit is owned
    }

    static Transform IronLeaf(Ctx c, Transform root, Quaternion rot, Vector3 gatePos, float hingeX, float dir)
    {
        var pivot = new GameObject("Leaf").transform;
        pivot.SetParent(root, false);
        pivot.localPosition = new Vector3(hingeX, 0f, 0f);
        pivot.localRotation = Quaternion.identity;
        var iron = c.M("NB_Iron");
        float len = GateRoadHalf - 0.05f;
        void Bar(string n, Vector3 lp, Vector3 size)
        {
            var b = OBox(c, pivot, n, Vector3.zero, Quaternion.identity, size, iron, LayerDefault, false);
            b.transform.localPosition = lp; b.transform.localRotation = Quaternion.identity;
        }
        Bar("RailLo", new Vector3(dir * len * 0.5f, 0.35f, 0f), new Vector3(len, 0.1f, 0.08f));
        Bar("RailMid", new Vector3(dir * len * 0.5f, 1.6f, 0f), new Vector3(len, 0.08f, 0.08f));
        Bar("RailHi", new Vector3(dir * len * 0.5f, 3.0f, 0f), new Vector3(len, 0.1f, 0.08f));
        int n = 11;
        for (int i = 1; i < n; i++)
        {
            float x = dir * len * i / n;
            float top = 3.25f + 0.35f * Mathf.Sin(Mathf.PI * i / n);
            Bar("Bar", new Vector3(x, top * 0.5f, 0f), new Vector3(0.05f, top, 0.05f));
            var tip = OPrim(c, pivot, PrimitiveType.Sphere, "Finial", Vector3.zero, Quaternion.identity, new Vector3(0.12f, 0.2f, 0.12f), c.M("NB_Gold"), false);
            tip.transform.localPosition = new Vector3(x, top + 0.05f, 0f);
        }
        return pivot;
    }

    /// <summary>A district perimeter wall from a to b (XZ), styled per district, with real colliders (layer Walls).</summary>
    static void WallLine(Ctx c, Transform parent, Vector3 a, Vector3 b, WallStyle style)
    {
        Vector3 dlt = b - a; dlt.y = 0f;
        float len = dlt.magnitude;
        if (len < 0.5f) return;
        var g = Group(parent, "Wall");
        var rot = Quaternion.LookRotation(dlt / len);
        const float maxSeg = 60f;
        int n = Mathf.Max(1, Mathf.CeilToInt(len / maxSeg));
        float each = len / n;
        for (int i = 0; i < n; i++)
        {
            Vector3 mid = a + dlt / len * (each * (i + 0.5f));
            switch (style)
            {
                case WallStyle.Picket:
                    PicketRun(c, g, a + dlt / len * (each * i), a + dlt / len * (each * (i + 1)));
                    var col = OBox(c, g, "WallCollider", mid + Vector3.up * 1.6f, rot, new Vector3(0.4f, 3.2f, each), c.M("NB_White"), LayerWalls, true);
                    Object.DestroyImmediate(col.GetComponent<MeshRenderer>());
                    Object.DestroyImmediate(col.GetComponent<MeshFilter>());
                    break;
                case WallStyle.Hedge:
                    OBox(c, g, "Hedge", mid + Vector3.up * 1.6f, rot, new Vector3(1.8f, 3.2f, each), c.M("NB_Hedge"), LayerWalls, true);
                    break;
                default:
                    OBox(c, g, "StoneWall", mid + Vector3.up * 1.6f, rot, new Vector3(0.9f, 3.2f, each), c.M("NB_Stone"), LayerWalls, true);
                    OBox(c, g, "StoneCap", mid + Vector3.up * 3.28f, rot, new Vector3(1.1f, 0.18f, each), c.M("NB_Gold"), LayerDefault, false);
                    break;
            }
        }
    }

    /// <summary>A primitive (sphere / cylinder / capsule / cube) with a material, optionally keeping its collider.</summary>
    static GameObject OPrim(Ctx c, Transform parent, PrimitiveType type, string name, Vector3 center, Quaternion rot, Vector3 size, Material m, bool collider)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(center, rot);
        go.transform.localScale = size;
        var mr = go.GetComponent<MeshRenderer>();
        mr.sharedMaterial = m;
        if (size.y < 0.05f) mr.shadowCastingMode = ShadowCastingMode.Off;
        go.layer = LayerDefault;
        if (!collider) Object.DestroyImmediate(go.GetComponent<Collider>());
        return go;
    }

    // ------------------------------------------------------------------------------------------
    // Nature: tree line + scattered woods outside the developed area
    // ------------------------------------------------------------------------------------------

    static void BuildNature(Ctx c)
    {
        var g = c.gNature;
        string[] treeTypes = { "SM_tree_town_01", "SM_tree_town_02", "SM_tree_town_03" };
        var mine = new List<Vector3>();
        int perimeter = 0, scatter = 0, bushes = 0;

        // Perimeter tree line just inside the invisible walls.
        var inset = PlayBounds.Expand(-3f);
        var corners = new[] { new Vector3(inset.x0, 0, inset.z0), new Vector3(inset.x1, 0, inset.z0), new Vector3(inset.x1, 0, inset.z1), new Vector3(inset.x0, 0, inset.z1) };
        for (int e = 0; e < 4; e++)
        {
            Vector3 a = corners[e], b = corners[(e + 1) % 4];
            Vector3 d = b - a; float len = d.magnitude; Vector3 v = d / len;
            Vector3 inward = Vector3.Cross(v, Vector3.up); // corners run CCW seen from above, so this points inside
            for (float t = c.R(0f, 3f); t < len; t += c.R(6.5f, 9f))
            {
                var p = a + v * t + inward * c.R(0f, 4f);
                if (InAny(c.developed, p.x, p.z)) continue;
                if (TooClose(mine, p.x, p.z, 5f)) continue;
                p.y = GrassTop;
                mine.Add(p);
                Tree(c, g, c.Pick(treeTypes), p, c.R(0.9f, 1.3f));
                perimeter++;
            }
        }

        // Scattered woods in the leftover meadows (the count grows with the town: 150 trees for district 1's old area).
        var dom = PlayBounds.Expand(-6f);
        int wantTrees = 330, wantBushes = 170;
        for (int tries = 0; tries < 9000 && scatter < wantTrees; tries++)
        {
            float x = c.R(dom.x0, dom.x1), z = c.R(dom.z0, dom.z1);
            if (InAny(c.developed, x, z, 2.5f)) continue;
            if (TooClose(mine, x, z, 7.5f)) continue;
            var p = new Vector3(x, GrassTop, z);
            mine.Add(p);
            Tree(c, g, c.Pick(treeTypes), p, c.R(0.8f, 1.25f));
            scatter++;
        }
        for (int tries = 0; tries < 3000 && bushes < wantBushes; tries++)
        {
            float x = c.R(dom.x0, dom.x1), z = c.R(dom.z0, dom.z1);
            if (InAny(c.developed, x, z, 1.5f)) continue;
            if (TooClose(mine, x, z, 2f)) continue;
            Place(c, Sub + (c.Chance(0.5f) ? "Nature/SM_bush_02" : "Nature/SM_bush_03"), g, new Vector3(x, GrassTop, z), Yaw(c.R(0f, 360f)), c.R(1.3f, 2.6f), false, LayerDefault);
            bushes++;
        }
        c.notes.AppendLine($"nature: perimeter trees {perimeter}, woods trees {scatter}, bushes {bushes}");
    }

    static void BuildBounds(Ctx c)
    {
        const float h = 30f, t = 2f;
        var b = PlayBounds;
        Wall(c, "Wall_South", new Vector3((b.x0 + b.x1) / 2, h / 2 - 1f, b.z0 - t / 2), new Vector3(b.W + 2 * t, h, t));
        Wall(c, "Wall_North", new Vector3((b.x0 + b.x1) / 2, h / 2 - 1f, b.z1 + t / 2), new Vector3(b.W + 2 * t, h, t));
        Wall(c, "Wall_West", new Vector3(b.x0 - t / 2, h / 2 - 1f, (b.z0 + b.z1) / 2), new Vector3(t, h, b.D + 2 * t));
        Wall(c, "Wall_East", new Vector3(b.x1 + t / 2, h / 2 - 1f, (b.z0 + b.z1) / 2), new Vector3(t, h, b.D + 2 * t));
    }

    static void Wall(Ctx c, string name, Vector3 center, Vector3 size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(c.gBounds, false);
        go.transform.position = center;
        go.layer = LayerWalls;
        var bc = go.AddComponent<BoxCollider>();
        bc.size = size;
    }

    // ==========================================================================================
    // WorldLife (Docs/WorldLife.md, package A): exits, household cars, derp props, knockables, the anchor
    // ==========================================================================================

    const float ExitWestZ = 200f, ExitPortalInset = 6f;
    const float HydrantD = 8.6f, HydrantSpacing = 115f, RoadWorkD = 4.35f, PathEndD = 8.2f, RampD = 5.3f;
    /// <summary>The pack's cars point their nose (headlights) along local +X: yaw from a slot pose (nose = +Z) to the model.</summary>
    const float CarNoseYaw = -90f;

    static string PropMatKey(string recipeMat) => "NB_Knock_" + recipeMat;
    static byte DistrictByte(Vector3 p) => WorldLifeIds.DistrictIndex(Districts.AtPosition(p));
    static Vector3 RightOf(Vector3 d) => new Vector3(d.z, 0f, -d.x);

    /// <summary>The three town exits (§5.4). Pure data; the roads join c.streets in BuildGround.</summary>
    static void DefineExits(Ctx c)
    {
        DistrictSpec mm = null, bb = null;
        foreach (var d in c.districts)
        {
            if (d.id == Districts.MinivanMeadows) mm = d;
            else if (d.id == Districts.BillionaireBluffs) bb = d;
        }
        // West "Old Highway": z = 200 from Old Money Rd (between Birch St and the Snootington gate) to the west boundary.
        c.exits.Add(new ExitSpec
        {
            id = "exit-west", name = "Old Highway", signText = "OLD HIGHWAY ->",
            junction = new Vector3(ElmX, 0f, ExitWestZ), forward = Vector3.left,
            portal = new Vector3(PlayBounds.x0 + ExitPortalInset, 0f, ExitWestZ),
            road = RoadX(ExitWestZ, PlayBounds.x0, ElmX - RoadHalfWidth),
        });
        // East "Interstate On-Ramp": Carpool Ln carries on past Soccer Mom Way to the east boundary (inside the Meadows).
        c.exits.Add(new ExitSpec
        {
            id = "exit-east", name = "Interstate On-Ramp", signText = "INTERSTATE ->", district = mm,
            junction = new Vector3(MMEastX, 0f, MileZ), forward = Vector3.right,
            portal = new Vector3(PlayBounds.x1 - ExitPortalInset, 0f, MileZ),
            road = RoadX(MileZ, MMEastX + RoadHalfWidth, PlayBounds.x1),
        });
        // North "Private Airport Rd": Tax Haven Heights Rd carries on past Offshore Account Ave to the north boundary.
        c.exits.Add(new ExitSpec
        {
            id = "exit-north", name = "Private Airport Rd", signText = "PRIVATE AIRPORT ->\n<size=60%>(billionaires only)</size>", district = bb,
            junction = new Vector3(ElmX, 0f, BBAveZ), forward = Vector3.forward,
            portal = new Vector3(ElmX, 0f, PlayBounds.z1 - ExitPortalInset),
            road = RoadZ(ElmX, BBAveZ + RoadHalfWidth, PlayBounds.z1),
            lampPrefab = "Props/SM_streetlamp_03", treeA = "SM_tree_town_01", treeB = "SM_tree_town_01",
        });
    }

    /// <summary>Everything WorldLife adds to the built town, on its own RNG (seed ^ StableHash("worldlife")).</summary>
    static void BuildWorldLife(Ctx c)
    {
        var mainRng = c.rng;
        c.rng = new System.Random(c.seed ^ PickupVisuals.StableHash("worldlife"));
        c.gWorldLife = Group(c.root, "WorldLife");
        try
        {
            int cleared = ClearExitFootprints(c);
            foreach (var x in c.exits) BuildExit(c, x);
            BuildHouseholdCars(c);
            Physics.SyncTransforms();
            int hydrants = BuildHydrants(c);
            int sites = BuildRoadWorks(c);
            Physics.SyncTransforms();
            int flamingos = BuildFlamingos(c);
            FinishKnockables(c);
            BuildAnchor(c);
            c.notes.AppendLine($"worldlife: exits {c.exits.Count} (cleared {cleared} verge props / trees from their corridors), household cars {c.householdCars.Count}, hydrants {hydrants}, road-work sites {sites}, flamingos {flamingos}");
        }
        finally { c.rng = mainRng; }
    }

    static IEnumerable<Frontage> AllFrontages(Ctx c)
    {
        foreach (var f in c.frontages) yield return f;
        foreach (var d in c.districts)
        {
            foreach (var f in d.frontages) yield return f;
            foreach (var f in d.verges) yield return f;
        }
    }

    static bool IsBulbFrontage(Frontage f) => f.key == "glutenfree" || f.key == "playdate";

    /// <summary>Depot Row's four lot pads (Docs/Multiplayer.md §13.2, package N3; <see cref="DepotRowLayout"/>): asphalt that
    /// cuts Minivan Mile's south sidewalk. The shops on them live in DepotRow.unity (Setup_DepotRow). No RNG.</summary>
    static IEnumerable<Rect2> DepotRowPads()
    {
        for (int k = 0; k < DepotRowLayout.LotCount; k++)
        {
            var r = DepotRowLayout.PadRect(k);
            yield return new Rect2(r.xMin, r.yMin, r.xMax, r.yMax);
        }
    }

    static bool InDepotRowPad(Vector3 p, float margin)
    {
        foreach (var r in DepotRowPads()) if (r.Expand(margin).Contains(p.x, p.z)) return true;
        return false;
    }

    static bool InExitCorridor(Ctx c, Vector3 p, float margin)
    {
        foreach (var x in c.exits) if (x.road.Expand(margin).Contains(p.x, p.z)) return true;
        return false;
    }

    /// <summary>Delete verge furniture, district dressing and nature standing on an exit road (+ sidewalks) or its berm.</summary>
    static int ClearExitFootprints(Ctx c)
    {
        var zones = new List<Rect2>();
        foreach (var x in c.exits)
        {
            zones.Add(x.road.Expand(WalkWidth + CurbWidth + 1.5f));
            Vector3 r = RightOf(x.forward);
            zones.Add(Rect2.From(x.portal - x.forward * 9f + r * 30f, x.portal + x.forward * 12f - r * 30f));
        }
        // Depot Row (N3): the four lot pads on Minivan Mile's south side are cleared of verge props and trees too.
        foreach (var pad in DepotRowPads()) zones.Add(pad.Expand(1.5f));
        var kill = new List<GameObject>();
        void Check(Transform t)
        {
            foreach (var z in zones) if (z.Contains(t.position.x, t.position.z)) { kill.Add(t.gameObject); return; }
        }
        var streetGroups = new List<Transform> { c.gStreet };
        foreach (var d in c.districts) { if (d.gStreet != null) streetGroups.Add(d.gStreet); }
        foreach (var sg in streetGroups)
            foreach (Transform frontage in sg)
                foreach (Transform t in frontage) Check(t);
        foreach (var d in c.districts)
            if (d.gProps != null) foreach (Transform t in d.gProps) Check(t);
        foreach (Transform t in c.gNature) Check(t);
        foreach (var g in kill) Object.DestroyImmediate(g);
        return kill.Count;
    }

    /// <summary>An exit: centre dashes, verge lamps and trees, a crooked sign, guard rails, and the tunnel portal in a
    /// grassy berm (dark mouth, a wall 6 m in, the toast trigger).</summary>
    static void BuildExit(Ctx c, ExitSpec x)
    {
        var g = Group(c.gWorldLife, "Exit_" + x.id);
        Vector3 f = x.forward, r = RightOf(f);
        Quaternion faceOut = Quaternion.LookRotation(f), faceTown = Quaternion.LookRotation(-f);
        Vector3 L(float across, float up, float along) => x.portal + r * across + Vector3.up * up + f * along;
        float len = x.Length;

        DashLine(c, g, "ExitDash", x.junction + f * (RoadHalfWidth + 2f), x.portal - f * 1f, 0f, 0f, null);

        // Verge lamps + street trees on the exit's own two verges (the sign's spot reserved first).
        var verges = new List<Frontage>();
        foreach (float side in new[] { 1f, -1f })
            verges.Add(new Frontage
            {
                street = x.name, key = x.id, origin = x.junction, along = f, normal = r * side,
                furn0 = 12f, furn1 = len - 10f, odd = side < 0f, outer = true, district = x.district,
            });
        float signS = len - 14f;
        verges[0].Occupy(signS, 2.2f);
        FurnishFrontages(c, verges, g, x.lampPrefab, x.treeA, x.treeB, out int lamps, out int trees);

        // The crooked sign on the outbound side: "OLD HIGHWAY ->" to leavers, "WELCOME" to arrivals.
        var sg = new GameObject("ExitSign").transform;
        sg.SetParent(g, false);
        Vector3 sp = x.junction + f * signS + r * 8.7f + Vector3.up * GrassTop;
        sg.SetPositionAndRotation(sp, faceTown);
        Vector3 srgt = faceTown * Vector3.right;
        OBox(c, sg, "PostL", sp - srgt * 1.25f + Vector3.up * 1.3f, faceTown, new Vector3(0.14f, 2.6f, 0.14f), c.M("NB_Wood"), LayerDefault, true);
        OBox(c, sg, "PostR", sp + srgt * 1.25f + Vector3.up * 1.3f, faceTown, new Vector3(0.14f, 2.6f, 0.14f), c.M("NB_Wood"), LayerDefault, true);
        var board = SignBoard(c, sg, "Board", sp + Vector3.up * 2.4f, faceTown * Quaternion.Euler(0f, 0f, 4.5f), new Vector3(3.1f, 1.5f, 0.08f),
            c.M("NB_SignGreen"), x.signText + "\n<size=45%>Now leaving Maple Hollow.\nCome back with your trash!</size>", Color.white, false);
        Label(board.parent, "TextBack", "WELCOME TO\nMAPLE HOLLOW\n<size=55%>Please put your trash out.</size>", new Vector3(0f, 0f, -0.052f), Quaternion.identity,
              new Vector2(2.85f, 1.29f), new Color(1f, 0.93f, 0.4f));
        Knock(c, sg.gameObject, KnockKind.Sign, "exitsign", null);

        // Guard rails along the last 15 m before the mouth (on the curbs).
        var metal = c.M("NB_Metal");
        foreach (float side in new[] { -1f, 1f })
        {
            OBox(c, g, "GuardRail", L(side * 5.6f, 0.72f, -8.5f), faceOut, new Vector3(0.1f, 0.32f, 15f), metal, LayerDefault, true);
            for (int i = 0; i < 4; i++)
                OBox(c, g, "RailPost", L(side * 5.62f, 0.42f, -15.5f + i * 4.6f), faceOut, new Vector3(0.12f, 0.84f, 0.12f), metal, LayerDefault, false);
        }

        // The tunnel: a dark mouth, side walls, a ceiling and the wall 6 m in (layer Walls: the truck bonks it).
        var dark = c.M("NB_TunnelDark");
        OBox(c, g, "TunnelFloor", L(0f, WalkTop + 0.015f, 3f), faceOut, new Vector3(12.8f, 0.02f, 6.2f), dark, LayerDefault, false, false);   // over road + sidewalks
        OBox(c, g, "TunnelWallL", L(-6.7f, 3.1f, 3f), faceOut, new Vector3(0.6f, 6.2f, 6.4f), dark, LayerWalls, true);
        OBox(c, g, "TunnelWallR", L(6.7f, 3.1f, 3f), faceOut, new Vector3(0.6f, 6.2f, 6.4f), dark, LayerWalls, true);
        OBox(c, g, "TunnelCeiling", L(0f, 6.4f, 3f), faceOut, new Vector3(14f, 0.4f, 6.4f), dark, LayerWalls, true);
        OBox(c, g, "TunnelEnd", L(0f, 3.2f, 5.8f), faceOut, new Vector3(13.2f, 6.4f, 0.4f), dark, LayerWalls, true);

        // Concrete portal frame with the road's name on the lintel.
        var concrete = c.M("NB_Concrete");
        OBox(c, g, "PortalPillarL", L(-6.6f, 3.3f, -0.35f), faceOut, new Vector3(1.2f, 6.6f, 0.9f), concrete, LayerWalls, true);
        OBox(c, g, "PortalPillarR", L(6.6f, 3.3f, -0.35f), faceOut, new Vector3(1.2f, 6.6f, 0.9f), concrete, LayerWalls, true);
        OBox(c, g, "PortalLintel", L(0f, 7.3f, -0.35f), faceOut, new Vector3(14.8f, 1.6f, 1f), concrete, LayerWalls, true);
        var nameT = new GameObject("PortalName").transform;
        nameT.SetParent(g, false);
        nameT.SetPositionAndRotation(L(0f, 7.3f, -0.87f), faceOut);           // identity label reads from local -Z = the town side
        Label(nameT, "Text", x.name.ToUpperInvariant(), Vector3.zero, Quaternion.identity, new Vector2(13f, 1.15f), new Color(0.2f, 0.22f, 0.25f));

        // The grassy berm (visual only: the tunnel walls and the town's boundary are the colliders).
        var berm = c.M("NB_Berm");
        // (their inner edges, at ±6.6 m, stay just outside the tunnel walls' inner faces at ±6.4 m)
        OPrim(c, g, PrimitiveType.Sphere, "BermL", L(-17.6f, 0f, 2.5f), faceOut, new Vector3(22f, 15f, 16f), berm, false);
        OPrim(c, g, PrimitiveType.Sphere, "BermR", L(17.6f, 0f, 2.5f), faceOut, new Vector3(22f, 15f, 16f), berm, false);
        // (sits on the lintel and stays clear of the tunnel's ceiling and the portal's name)
        OPrim(c, g, PrimitiveType.Sphere, "BermTop", L(0f, 10f, 5f), faceOut, new Vector3(24f, 7f, 12f), berm, false);

        // The toast trigger inside the mouth (TownExitPortal).
        var trig = new GameObject("ExitPortal_" + x.id);
        trig.transform.SetParent(g, false);
        trig.transform.SetPositionAndRotation(L(0f, 2.6f, 2.6f), faceOut);
        var tb = trig.AddComponent<BoxCollider>();
        tb.isTrigger = true;
        tb.size = new Vector3(12.4f, 5.2f, 5.2f);
        var portal = trig.AddComponent<TownExitPortal>();
        portal.exitId = x.id;
        portal.displayName = x.name;
        c.dynamicRoots.Add(trig.transform);
        c.notes.AppendLine($"exit {x.id} '{x.name}': {len:F0} m, lamps {lamps}, trees {trees}, portal {x.portal}");
    }

    // ---------------- household cars

    /// <summary>The old parked-car draws (district 1, the Meadows vans) plus a WorldLife top-up: 60% of district-1 houses
    /// with a long enough driveway, a black town car per Snootington house and a white limo per Bluffs estate. Each car is a
    /// static GameObject parked nose-in with a HouseholdCar marker (package D drives pooled copies).</summary>
    static void BuildHouseholdCars(Ctx c)
    {
        var g = Group(c.gWorldLife, "HouseholdCars");
        var eligible = new List<LotInfo>();
        int have = 0;
        foreach (var l in c.lots)
            if (l.district == null && l.hasDrive && l.dwW > 0f && l.dwEnd - 8f >= 7f) { eligible.Add(l); if (l.cars.Count > 0) have++; }
        int want = Mathf.CeilToInt(eligible.Count * 0.6f);
        var pool = eligible.FindAll(l => l.cars.Count == 0);
        pool.Sort((a, b) => string.CompareOrdinal(a.stopId, b.stopId));
        while (have < want && pool.Count > 0)
        {
            int i = c.rng.Next(pool.Count);
            pool[i].cars.Add(new CarRec { model = c.RI(1, 4) });
            pool.RemoveAt(i);
            have++;
        }
        foreach (var l in c.lots)
        {
            SetCarPoses(l);
            if (!l.hasDrive) continue;
            int tier = l.district != null ? l.district.tier : 1;
            if (tier == 3 && l.cars.Count == 0) l.cars.Add(new CarRec { model = 4, tintKey = "NB_TownCarBlack", scale = new Vector3(1.2f, 1f, 1f) });
            if (tier == 4 && l.cars.Count == 0) l.cars.Add(new CarRec { model = 2, tintKey = "NB_LimoWhite", scale = new Vector3(1.6f, 1.02f, 1.04f) });
            for (int k = 0; k < l.cars.Count && k < 2; k++) PlaceHouseholdCar(c, g, l, k);
        }
    }

    static void SetCarPoses(LotInfo l)
    {
        var f = l.f;
        var nose = Quaternion.LookRotation(f.normal, Vector3.up);       // nose toward the house
        float d0 = l.estate ? l.frontD - 7.5f : l.dwEnd - 2.9f;
        float d1 = l.estate ? l.frontD - 14.5f : l.dwEnd - 7.9f;
        l.carPose0 = new Pose(f.P(l.sc + l.dwC, Mathf.Max(d0, PathEndD + 0.4f), WalkTop), nose);
        l.carPose1 = new Pose(f.P(l.sc + l.dwC, Mathf.Max(d1, PathEndD + 0.4f), WalkTop), nose);
    }

    static void PlaceHouseholdCar(Ctx c, Transform g, LotInfo l, int slot)
    {
        var rec = l.cars[slot];
        var pose = slot == 0 ? l.carPose0 : l.carPose1;
        string path = Sub + "Cars/SM_car_town_0" + rec.model;
        var go = Place(c, path, g, pose.position, pose.rotation * Yaw(CarNoseYaw), 1f, false, LayerGround);
        if (go == null) return;
        go.transform.localScale = Vector3.Scale(go.transform.localScale, rec.scale);
        if (!string.IsNullOrEmpty(rec.tintKey)) TintCar(c, go, rec.tintKey);
        go.name = $"Car_{l.stopId}_{slot}";
        var box = SwapToPrimitiveCollider(go, ColMode.Box) as BoxCollider;
        var hc = go.AddComponent<HouseholdCar>();
        hc.stopId = l.stopId;
        hc.slot = (byte)slot;
        hc.district = DistrictByte(pose.position);
        hc.prefab = LoadPrefab(c, path);
        hc.tint = string.IsNullOrEmpty(rec.tintKey) ? null : c.M(rec.tintKey);
        hc.scale = go.transform.localScale;
        hc.noseYaw = CarNoseYaw;
        hc.body = go.GetComponent<Renderer>();
        hc.restCollider = box;
        l.placedCars.Add(hc);
        c.householdCars.Add(hc);
    }

    // ---------------- derp props: hydrants, road works, flamingos

    /// <summary>Fire hydrants on the verges, about one every 115 m per side (staggered), where the verge is free.</summary>
    static int BuildHydrants(Ctx c)
    {
        var g = Group(c.gWorldLife, "Hydrants");
        int n = 0;
        foreach (var f in AllFrontages(c))
        {
            if (IsBulbFrontage(f)) continue;
            float phase = f.odd ? HydrantSpacing * 0.5f : 8f;
            for (float s = f.furn0 + phase; s <= f.furn1 - 4f; s += HydrantSpacing)
            {
                float best = float.NaN;
                for (int k = 0; k < 21 && float.IsNaN(best); k++)
                {
                    float trial = s + (k % 2 == 0 ? 1 : -1) * (k / 2) * 0.75f;
                    if (trial < f.furn0 + 2f || trial > f.furn1 - 2f || !f.VergeFree(trial, 0.7f)) continue;
                    if (HydrantSpotOk(c, f.P(trial, HydrantD, GrassTop))) best = trial;
                }
                if (float.IsNaN(best)) continue;
                PlaceProp(c, "hydrant", g, f.P(best, HydrantD, GrassTop), Yaw(c.R(0f, 360f)), "");
                f.Occupy(best, 0.8f);
                n++;
            }
        }
        return n;
    }

    static bool HydrantSpotOk(Ctx c, Vector3 p)
    {
        if (!PlayBounds.Expand(-4f).Contains(p.x, p.z) || InExitCorridor(c, p, 6f) || InDepotRowPad(p, 1.5f)) return false;
        foreach (var s in c.streets) if (s.Expand(WalkWidth + CurbWidth + 0.15f).Contains(p.x, p.z)) return false;   // on some road / sidewalk
        foreach (var d in c.districts) if ((d.gatePos - p).sqrMagnitude < 16f * 16f) return false;
        return !Physics.CheckCapsule(p + Vector3.up * 0.65f, p + Vector3.up * 1.2f, 0.5f, ~(1 << LayerGround), QueryTriggerInteraction.Ignore);
    }

    /// <summary>2–3 road-works sites per district (cones in the road along the curb, two sawhorses, an A-frame sign) on
    /// straight stretches clear of junctions, curb stops, driveways, gates and the park crossings.</summary>
    static int BuildRoadWorks(Ctx c)
    {
        var g = Group(c.gWorldLife, "RoadWorks");
        var cands = new List<(Frontage f, float s)>[4];
        for (int i = 0; i < 4; i++) cands[i] = new List<(Frontage, float)>();
        foreach (var f in AllFrontages(c))
        {
            if (IsBulbFrontage(f)) continue;
            for (float s = f.furn0 + 14f; s <= f.furn1 - 14f; s += 3f)
                if (RoadWorkSpotOk(c, f, s)) cands[DistrictByte(f.P(s, RoadWorkD, 0f))].Add((f, s));
        }
        int sites = 0;
        for (int d = 0; d < 4; d++)
        {
            int want = d == 0 ? 3 : 2 + c.rng.Next(0, 2);
            var list = cands[d];
            int placed = 0;
            for (int tries = 0; tries < 80 && placed < want && list.Count > 0; tries++)
            {
                int i = c.rng.Next(list.Count);
                var (f, s) = list[i];
                list.RemoveAt(i);
                var p = f.P(s, RoadWorkD, 0f);
                bool far = true;
                foreach (var q in c.roadWorks) if ((q - p).sqrMagnitude < 45f * 45f) { far = false; break; }
                if (!far) continue;
                BuildRoadWorkSite(c, g, f, s);
                c.roadWorks.Add(p);
                placed++; sites++;
            }
        }
        return sites;
    }

    static readonly Collider[] probeBuf = new Collider[32];

    static bool RoadWorkSpotOk(Ctx c, Frontage f, float s)
    {
        Vector3 a = f.P(s - 9f, 0f, 0f), b = f.P(s + 9f, 0f, 0f), p = f.P(s, RoadWorkD, 0f);
        // A straight stretch of exactly one street (no junction within 9 m of the site).
        int own = -1;
        for (int i = 0; i < c.streets.Count; i++)
            if (c.streets[i].Contains(a.x, a.z) && c.streets[i].Contains(b.x, b.z)) { own = i; break; }
        if (own < 0) return false;
        // ...and no other street within 17 m of the site centre (the junction's crosswalks sit 7.4 m from its centre).
        Vector3 centre = f.P(s, 0f, 0f);
        for (int i = 0; i < c.streets.Count; i++) if (i != own && c.streets[i].Distance(centre.x, centre.z) < 17f) return false;
        if (InExitCorridor(c, p, 14f) || DepotPad.Expand(16f).Contains(p.x, p.z) || InDepotRowPad(p, 10f)) return false;
        foreach (var d in c.districts) if ((d.gatePos - p).sqrMagnitude < 22f * 22f) return false;
        foreach (var x in new[] { new Vector3(ParkPathX, 0f, MapleZ), new Vector3(ParkPathX, 0f, BirchZ) })
            if ((x - p).sqrMagnitude < 14f * 14f) return false;
        foreach (var st in c.stops) if ((st.transform.position - p).sqrMagnitude < 14f * 14f) return false;
        // Nothing solid along the curb: driveway ramps, posts, parked trucks...
        var rot = Quaternion.LookRotation(f.along);
        int n = Physics.OverlapBoxNonAlloc(f.P(s, RoadWorkD + 0.1f, 0.45f), new Vector3(0.8f, 0.4f, 8.5f), probeBuf, rot, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var col = probeBuf[i];
            if (col.gameObject.scene != c.scene) continue;
            string nm = col.name;
            if (col.gameObject.layer == LayerGround && (nm.StartsWith("Road_") || nm.StartsWith("Curb_") || nm.StartsWith("Sidewalk_"))) continue;
            return false;
        }
        return true;
    }

    static void BuildRoadWorkSite(Ctx c, Transform g, Frontage f, float s)
    {
        var site = Group(g, "RoadWork");
        Vector3 n = f.normal;                                   // road -> lots
        Vector3 travel = new Vector3(-n.z, 0f, n.x);            // the near lane's direction (Right(travel) == n)
        float dir = Mathf.Sign(Vector3.Dot(travel, f.along));
        float U(float k) => s + dir * k;                        // k metres downstream of the site centre
        int cones = 4 + c.rng.Next(0, 3);
        var alongCurb = Quaternion.LookRotation(n);             // sawhorse boards (local X) parallel to the curb
        RoadWorkSign(c, site, f.P(U(-7.4f), RoadWorkD + 0.1f, RoadTop), Quaternion.LookRotation(-travel) * Yaw(c.R(-8f, 8f)));
        PlaceProp(c, "sawhorse", site, f.P(U(-5.5f), RoadWorkD + 0.08f, RoadTop), alongCurb * Yaw(c.R(-6f, 6f)), "");
        for (int i = 0; i < cones; i++)
        {
            float k = -3.6f + 7.2f * i / (cones - 1);
            PlaceProp(c, "cone", site, f.P(U(k) + c.R(-0.15f, 0.15f), RoadWorkD + c.R(-0.1f, 0.1f), RoadTop), Yaw(c.R(0f, 360f)), "");
        }
        PlaceProp(c, "sawhorse", site, f.P(U(5.5f), RoadWorkD + 0.08f, RoadTop), alongCurb * Yaw(c.R(-6f, 6f)), "");
    }

    /// <summary>"ROAD WORK (since 1997)": an A-frame board (one mesh) with its text, facing the oncoming traffic.</summary>
    static void RoadWorkSign(Ctx c, Transform parent, Vector3 at, Quaternion faceTraffic)
    {
        string white = PropMatKey(KnockRecipes.MatWhite), orange = PropMatKey(KnockRecipes.MatOrange), black = PropMatKey(KnockRecipes.MatBlack);
        var parts = new List<(PrimitiveType, Vector3, Vector3, Vector3, string)>
        {
            (PrimitiveType.Cube, new Vector3(0f, 0.55f, 0.115f), new Vector3(-12f, 0f, 0f), new Vector3(0.95f, 1.1f, 0.03f), white),   // front board
            (PrimitiveType.Cube, new Vector3(0f, 0.55f, -0.115f), new Vector3(12f, 0f, 0f), new Vector3(0.95f, 1.1f, 0.03f), orange),  // back board
            (PrimitiveType.Cube, new Vector3(0f, 1.0f, 0.105f), new Vector3(-12f, 0f, 0f), new Vector3(0.96f, 0.14f, 0.032f), orange), // top stripe
            (PrimitiveType.Cube, new Vector3(0f, 0.1f, 0.135f), new Vector3(-12f, 0f, 0f), new Vector3(0.96f, 0.14f, 0.032f), orange), // bottom stripe
            (PrimitiveType.Cube, new Vector3(0f, 1.1f, 0f), Vector3.zero, new Vector3(0.9f, 0.05f, 0.06f), black),                     // hinge
        };
        var (mesh, mats) = CombinedProp(c, "NB_RoadWorkSign", parts);
        var go = new GameObject("RoadWorkSign");
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(at, faceTraffic);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterials = mats;
        var box = go.AddComponent<BoxCollider>();
        box.center = new Vector3(0f, 0.55f, 0f);
        box.size = new Vector3(0.95f, 1.1f, 0.45f);
        // Text on the front board (reads from local +Z, the traffic side), leaning back with it.
        Label(go.transform, "Text", "ROAD\nWORK\n<size=55%>(since 1997)</size>", new Vector3(0f, 0.55f, 0.135f), Quaternion.Euler(-12f, 180f, 0f),
              new Vector2(0.85f, 0.75f), new Color(0.08f, 0.08f, 0.09f));
        Knock(c, go, KnockKind.Sign, "roadworksign", "");
    }

    /// <summary>Pink lawn flamingos in Meadows front yards (1–2 on most lots), clear of the path, drive and dressing.</summary>
    static int BuildFlamingos(Ctx c)
    {
        var g = Group(c.gWorldLife, "Flamingos");
        int n = 0;
        foreach (var l in c.lots)
        {
            if (l.district == null || l.district.tier != 2) continue;
            if (c.rng.NextDouble() >= 0.85) continue;
            int want = c.rng.NextDouble() < 0.5 ? 2 : 1;
            float d0 = PropertyLine + 1.3f, d1 = l.frontD - 1.8f;
            if (d1 < d0 + 0.3f) continue;
            var placed = new List<Vector3>();
            for (int tries = 0; tries < 16 && placed.Count < want; tries++)
            {
                float a = c.R(-l.half + 1.5f, l.half - 1.5f), dd = c.R(d0, d1);
                if (Mathf.Abs(a - l.doorA) < 1.8f) continue;
                if (l.hasDrive && Mathf.Abs(a - l.dwC) < l.dwW * 0.5f + 1.3f) continue;
                var p = l.f.P(l.sc + a, dd, GrassTop);
                bool near = false;
                foreach (var q in placed) if ((q - p).sqrMagnitude < 1.6f * 1.6f) near = true;
                if (near) continue;
                if (Physics.CheckCapsule(p + Vector3.up * 0.45f, p + Vector3.up * 1.1f, 0.45f, ~(1 << LayerGround), QueryTriggerInteraction.Ignore)) continue;
                PlaceProp(c, "flamingo", g, p, Yaw(c.R(0f, 360f)), l.stopId);
                placed.Add(p);
                n++;
            }
        }
        return n;
    }

    /// <summary>A package-B recipe prop (hydrant, cone, sawhorse, flamingo) as ONE static GameObject: the recipe's
    /// primitives combined into a mesh asset (a submesh per material), the recipe's rest box, and a Knockable.</summary>
    static GameObject PlaceProp(Ctx c, string recipe, Transform parent, Vector3 pos, Quaternion rot, string stopId)
    {
        if (!KnockRecipes.TryGet(recipe, out var r))
        {
            if (!c.missing.Contains("KnockRecipes." + recipe)) c.missing.Add("KnockRecipes." + recipe);
            return null;
        }
        var parts = new List<(PrimitiveType, Vector3, Vector3, Vector3, string)>(r.parts.Length);
        foreach (var p in r.parts)
        {
            var prim = p.prim == KnockRecipes.Prim.Cube ? PrimitiveType.Cube : p.prim == KnockRecipes.Prim.Cylinder ? PrimitiveType.Cylinder : PrimitiveType.Sphere;
            parts.Add((prim, p.pos, p.euler, p.size, PropMatKey(p.mat)));
        }
        var (mesh, mats) = CombinedProp(c, "NB_Knock_" + r.name, parts);
        var go = new GameObject(char.ToUpperInvariant(r.name[0]) + r.name.Substring(1));
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(pos, rot);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterials = mats;
        var box = go.AddComponent<BoxCollider>();
        box.center = r.colliderCenter;
        box.size = r.colliderSize;
        go.layer = LayerDefault;
        Knock(c, go, r.kind, r.name, stopId);
        return go;
    }

    /// <summary>Combine primitives into one mesh asset under the builder's material folder (a submesh per material, in
    /// first-use order); idempotent: an existing asset is overwritten in place so scene references survive.</summary>
    static (Mesh mesh, Material[] mats) CombinedProp(Ctx c, string assetName, IList<(PrimitiveType prim, Vector3 pos, Vector3 euler, Vector3 size, string mat)> parts)
    {
        if (c.propMeshes.TryGetValue(assetName, out var cached)) return cached;
        var order = new List<string>();
        var groups = new Dictionary<string, List<CombineInstance>>();
        foreach (var p in parts)
        {
            if (!groups.TryGetValue(p.mat, out var list)) { list = new List<CombineInstance>(); groups[p.mat] = list; order.Add(p.mat); }
            list.Add(new CombineInstance { mesh = BuiltinMesh(p.prim), transform = Matrix4x4.TRS(p.pos, Quaternion.Euler(p.euler), p.size) });
        }
        var temps = new List<Mesh>();
        var subs = new List<CombineInstance>();
        foreach (var k in order)
        {
            var m = new Mesh();
            m.CombineMeshes(groups[k].ToArray(), true, true);
            temps.Add(m);
            subs.Add(new CombineInstance { mesh = m, transform = Matrix4x4.identity });
        }
        var mesh = new Mesh { name = assetName };
        mesh.CombineMeshes(subs.ToArray(), false, false);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        foreach (var t in temps) Object.DestroyImmediate(t);
        EnsureFolder(MatDir);
        string path = MatDir + "/" + assetName + ".asset";
        var asset = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (asset == null) { AssetDatabase.CreateAsset(mesh, path); asset = mesh; }
        else
        {
            EditorUtility.CopySerialized(mesh, asset);
            asset.name = assetName;
            Object.DestroyImmediate(mesh);
            EditorUtility.SetDirty(asset);
        }
        var mats = new Material[order.Count];
        for (int i = 0; i < order.Count; i++) mats[i] = c.M(order[i]);
        var res = (asset, mats);
        c.propMeshes[assetName] = res;
        return res;
    }

    static readonly Dictionary<PrimitiveType, Mesh> builtinMeshes = new Dictionary<PrimitiveType, Mesh>();

    static Mesh BuiltinMesh(PrimitiveType p)
    {
        if (builtinMeshes.TryGetValue(p, out var m) && m != null) return m;
        var go = GameObject.CreatePrimitive(p);
        m = go.GetComponent<MeshFilter>().sharedMesh;
        Object.DestroyImmediate(go);
        builtinMeshes[p] = m;
        return m;
    }

    // ---------------- knockables

    /// <summary>Mark a builder-made prop Knockable (proxy "recipe/&lt;name&gt;"): its own primitive colliders are its
    /// rest colliders. Ids are assigned in this order by FinishKnockables.</summary>
    static void Knock(Ctx c, GameObject go, KnockKind kind, string recipe, string stopId)
    {
        if (go == null) return;
        c.knock.Add(new KnockRec { go = go, kind = kind, proxyKey = WorldLifeIds.KnockRecipePrefix + recipe, stopId = stopId ?? "" });
    }

    /// <summary>Mark a pack prefab instance Knockable (proxy "knock/&lt;prefabName&gt;"), swapping its non-convex
    /// MeshCollider for a primitive: a box round the mesh, or a pole capsule for lamps (§6.3 step 1).</summary>
    static GameObject KnockPrefab(Ctx c, GameObject go, KnockKind kind, string stopId, ColMode mode, string recipeKey = null)
    {
        if (go == null) return null;
        // recipeKey: pack props package B makes no proxy prefab for (the stop sign) fly from B's mesh bake instead.
        string key = recipeKey != null ? WorldLifeIds.KnockRecipePrefix + recipeKey : WorldLifeIds.KnockPrefabPrefix + go.name;
        if (mode != ColMode.Keep) SwapToPrimitiveCollider(go, mode);
        c.knock.Add(new KnockRec { go = go, kind = kind, proxyKey = key, stopId = stopId ?? "" });
        return go;
    }

    static Collider SwapToPrimitiveCollider(GameObject go, ColMode mode)
    {
        var b = LocalMeshBounds(go);
        foreach (var mc in go.GetComponentsInChildren<MeshCollider>(true))
        {
            try { Object.DestroyImmediate(mc); }
            catch (Exception) { mc.enabled = false; }
        }
        if (mode == ColMode.Pole)
        {
            var cap = go.AddComponent<CapsuleCollider>();
            cap.direction = 1;
            cap.radius = 0.15f;
            cap.height = Mathf.Max(0.5f, b.max.y);
            cap.center = new Vector3(0f, cap.height * 0.5f, 0f);
            return cap;
        }
        var box = go.AddComponent<BoxCollider>();
        var size = b.size;
        size.x = Mathf.Max(size.x, 0.15f);
        size.z = Mathf.Max(size.z, 0.15f);
        box.center = b.center;
        box.size = size;
        return box;
    }

    /// <summary>Bounds of every mesh under <paramref name="go"/> in go's local space (its own scale excluded).</summary>
    static Bounds LocalMeshBounds(GameObject go)
    {
        var toLocal = go.transform.worldToLocalMatrix;
        bool any = false;
        var b = new Bounds();
        foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf.sharedMesh == null) continue;
            var mb = mf.sharedMesh.bounds;
            var m = toLocal * mf.transform.localToWorldMatrix;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? mb.min.x : mb.max.x, (i & 2) == 0 ? mb.min.y : mb.max.y, (i & 4) == 0 ? mb.min.z : mb.max.z);
                var p = m.MultiplyPoint3x4(corner);
                if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
            }
        }
        return any ? b : new Bounds(Vector3.up * 0.5f, Vector3.one);
    }

    /// <summary>Give every recorded prop that survived the build its Knockable: id (build order), kind, proxy key, house,
    /// district, rest colliders (enabled primitives) and renderers (TMP text included).</summary>
    static void FinishKnockables(Ctx c)
    {
        ushort id = 0;
        int dropped = 0;
        var cols = new List<Collider>();
        foreach (var k in c.knock)
        {
            if (k.go == null) { dropped++; continue; }
            cols.Clear();
            foreach (var col in k.go.GetComponentsInChildren<Collider>(true))
                if (col.enabled && !col.isTrigger && !(col is MeshCollider)) cols.Add(col);
            var kn = k.go.GetComponent<Knockable>();
            if (kn == null) kn = k.go.AddComponent<Knockable>();
            kn.id = id++;
            kn.kind = k.kind;
            kn.proxyKey = k.proxyKey;
            kn.stopId = k.stopId ?? "";
            kn.district = DistrictByte(k.go.transform.position);
            kn.restColliders = cols.ToArray();
            kn.renderers = k.go.GetComponentsInChildren<Renderer>(true);
        }
        c.notes.AppendLine($"knockables: {id} marked ({dropped} removed with road / exit footprints)");
    }

    static void BuildAnchor(Ctx c)
    {
        var go = new GameObject("WorldLifeAnchor");
        go.SetActive(false);
        go.transform.SetParent(c.gWorldLife, false);
        var a = go.AddComponent<WorldLifeAnchor>();
        a.builderSeed = c.seed;
        go.AddComponent<StreetGraphGizmos>();
        go.SetActive(true);
        c.anchor = a;
        c.dynamicRoots.Add(go.transform);
    }

    // ---------------- the street graph (§5.2)

    /// <summary>Describe the town's roads, houses, gates, exits, park paths and loops to StreetGraphBuilder.</summary>
    static StreetGraph BuildStreetGraph(Ctx c)
    {
        Physics.SyncTransforms();
        var b = new StreetGraphBuilder { builderSeed = c.seed, districtAt = DistrictByte };
        foreach (var rw in c.roadWorks) b.jaywalkKeepOut.Add((rw, 11f));                        // cones in the road
        foreach (var st in c.stops) b.jaywalkKeepOut.Add((st.transform.position, 4.5f));       // curb slots + the wheelie bin
        const float W = StreetGraph.WalkOffset, Ring = MMBulbHalf + StreetGraphBuilder.BulbWalkMargin;
        int J(float x, float z, string name) => b.AddJunction(new Vector3(x, 0f, z), StreetGraphBuilder.EndKind.Junction, 0f, null, name);
        int Bulb(float x, float z, float half, string name) => b.AddJunction(new Vector3(x, 0f, z), StreetGraphBuilder.EndKind.Bulb, half, null, name);
        int Open(float x, float z, string name) => b.AddJunction(new Vector3(x, 0f, z), StreetGraphBuilder.EndKind.Open, 0f, null, name);

        // Junctions: district 1 + the depot, Old Money Rd up to the Bluffs, the Meadows.
        int jDepot = Open(ElmX, DepotRoadStartZ, "depot pad (Depot Rd)");
        int jMapleElm = J(ElmX, MapleZ, "Maple/Elm/Depot"), jMapleOak = J(OakX, MapleZ, "Maple/Oak");
        int jBirchElm = J(ElmX, BirchZ, "Birch/Elm/Old Money"), jBirchOak = J(OakX, BirchZ, "Birch/Oak");
        int jWestT = J(ElmX, ExitWestZ, "Old Money/Old Highway");
        int jHedge = J(ElmX, SHSouthZ, "Old Money/Hedgerow"), jMonocle = J(ElmX, SHNorthZ, "Old Money/Monocle/Tax Haven");
        int jHedgePinky = J(SHEastX, SHSouthZ, "Hedgerow/Pinky-Up"), jMonoPinky = J(SHEastX, SHNorthZ, "Monocle/Pinky-Up");
        int jOffshore = J(ElmX, BBAveZ, "Tax Haven/Offshore/Airport");
        int jYacht = Bulb(BBBulbX, BBAveZ, 12f, "Yacht Turnaround");
        int jMile = Open(DepotPad.x1, MileZ, "depot pad (Minivan Mile)");
        int jMMW = J(MMWestX, MileZ, "Mile/Carpool/Trampoline"), jMMS = J(MMSpurX, MileZ, "Carpool/Gluten-Free"), jMME = J(MMEastX, MileZ, "Carpool/Soccer Mom/Interstate");
        int jMMNW = J(MMWestX, MMNorthZ, "Trampoline/Juice Box"), jMMN = J(MMSpurX, MMNorthZ, "Juice Box/Playdate"), jMMNE = J(MMEastX, MMNorthZ, "Juice Box/Soccer Mom");
        int jGF = Bulb(MMSpurX, MMBulbAZ, MMBulbHalf, "Gluten-Free Ct bulb"), jPD = Bulb(MMSpurX, MMBulbBZ, MMBulbHalf, "Playdate Pl bulb");

        int sDepot = b.AddSegment(jDepot, jMapleElm, "Depot Rd");
        int sMaple = b.AddSegment(jMapleElm, jMapleOak, "Maple St");
        b.AddSegment(jMapleElm, jBirchElm, "Elm Ave");
        b.AddSegment(jMapleOak, jBirchOak, "Oak Ave");
        int sBirch = b.AddSegment(jBirchElm, jBirchOak, "Birch St");
        b.AddSegment(jBirchElm, jWestT, "Old Money Rd (Birch to Old Highway)");
        int sGateSH = b.AddSegment(jWestT, jHedge, "Old Money Rd (Snootington gate)");
        b.SetGate(sGateSH, Districts.SnootingtonHills, SHGateZ - ExitWestZ);
        b.AddSegment(jHedge, jMonocle, "Old Money Rd");
        int sGateBB = b.AddSegment(jMonocle, jOffshore, "Tax Haven Heights Rd");
        b.SetGate(sGateBB, Districts.BillionaireBluffs, BBGateZ - SHNorthZ);
        b.AddSegment(jHedge, jHedgePinky, "Hedgerow Crescent");
        b.AddSegment(jHedgePinky, jMonoPinky, "Pinky-Up Place");
        b.AddSegment(jMonocle, jMonoPinky, "Monocle Mews");
        b.AddSegment(jOffshore, jYacht, "Offshore Account Ave");
        int sMile = b.AddSegment(jMile, jMMW, "Minivan Mile");
        b.SetGate(sMile, Districts.MinivanMeadows, MMGateX - DepotPad.x1);
        b.AddSegment(jMMW, jMMS, "Carpool Ln (west)");
        b.AddSegment(jMMS, jMME, "Carpool Ln (east)");
        b.AddSegment(jMMW, jMMNW, "Trampoline Ct");
        b.AddSegment(jMME, jMMNE, "Soccer Mom Way");
        b.AddSegment(jMMNW, jMMN, "Juice Box Blvd (west)");
        b.AddSegment(jMMN, jMMNE, "Juice Box Blvd (east)");
        b.AddSegment(jMMS, jGF, "Gluten-Free Ct");
        b.AddSegment(jMMN, jPD, "Playdate Pl");
        foreach (var x in c.exits)
        {
            int from = -1;
            for (int j = 0; j < b.junctions.Count; j++) if ((b.junctions[j].p - x.junction).sqrMagnitude < 0.01f) { from = j; break; }
            if (from < 0) throw new InvalidOperationException($"exit {x.id}: no junction at {x.junction}");
            int jx = b.AddJunction(x.portal, StreetGraphBuilder.EndKind.Exit, 0f, x.id, x.name);
            b.AddSegment(from, jx, x.name, false);
        }

        // The depot pad: hand-laid one-way lanes (yield zone, ≤ 2.5 m/s) between Depot Rd and Minivan Mile, clear of the
        // truck bay (x 9.6..13.4, z -6.1..1.7), the door apron and the truck spawn (0, -4) ± 3 m; a walk across its corner.
        b.AddLaneLink(sDepot, false, new[]
        {
            new Vector3(-2.5f, 0f, 9f), new Vector3(-0.8f, 0f, 6f), new Vector3(2.5f, 0f, 4.2f), new Vector3(5.4f, 0f, 2.4f),
            new Vector3(6.3f, 0f, 0f), new Vector3(6.3f, 0f, -3.5f), new Vector3(7.2f, 0f, -6.4f), new Vector3(9.5f, 0f, -8.2f), new Vector3(13f, 0f, -8.5f),
        }, sMile, true, StreetGraph.EdgeFlags.Pad);
        b.AddLaneLink(sMile, false, new[]
        {
            new Vector3(16.8f, 0f, -2.6f), new Vector3(16f, 0f, 0f), new Vector3(15.8f, 0f, 3f), new Vector3(14.4f, 0f, 5f),
            new Vector3(11.5f, 0f, 5.8f), new Vector3(8f, 0f, 6.2f), new Vector3(4.9f, 0f, 7.3f), new Vector3(3.2f, 0f, 9f),
        }, sDepot, true, StreetGraph.EdgeFlags.Pad);
        b.AddPath(StreetGraphBuilder.WalkRef.Seg(sDepot, +1, StreetGraphBuilder.OpenWalkInset), new[] { new Vector3(12.5f, 0f, 8f) },
                  StreetGraphBuilder.WalkRef.Seg(sMile, -1, StreetGraphBuilder.OpenWalkInset), StreetGraph.EdgeKind.Walk, StreetGraph.NodeKind.Walk);
        b.AddCrossing(sMile, StreetGraphBuilder.OpenWalkInset, StreetGraph.EdgeKind.Crosswalk);

        // Pickle Park: the zebra crossings at the park path and the figure-eight of paths (benches on the east-west one).
        float pz = 0.5f * (ParkRect.z0 + ParkRect.z1);
        b.AddCrossing(sMaple, ParkPathX - ElmX, StreetGraph.EdgeKind.Crosswalk);
        b.AddCrossing(sBirch, ParkPathX - ElmX, StreetGraph.EdgeKind.Crosswalk);
        var ns = new List<Vector3>();
        for (float z = MapleZ + 15f; z < BirchZ - 9f; z += 10f) ns.Add(new Vector3(ParkPathX, 0f, z));
        b.AddPath(StreetGraphBuilder.WalkRef.Seg(sMaple, -1, ParkPathX - ElmX), ns.ToArray(), StreetGraphBuilder.WalkRef.Seg(sBirch, +1, ParkPathX - ElmX));
        b.AddPath(StreetGraphBuilder.WalkRef.Near(new Vector3(ParkPathX, 0f, pz)), new[] { new Vector3(70f, 0f, pz), new Vector3(60f, 0f, pz), new Vector3(50f, 0f, pz), new Vector3(ParkRect.x0 + 4f, 0f, pz) }, StreetGraphBuilder.WalkRef.Free);
        b.AddPath(StreetGraphBuilder.WalkRef.Near(new Vector3(ParkPathX, 0f, pz)), new[] { new Vector3(90f, 0f, pz), new Vector3(100f, 0f, pz), new Vector3(110f, 0f, pz), new Vector3(ParkRect.x1 - 4f, 0f, pz) }, StreetGraphBuilder.WalkRef.Free);
        // The Meadows commons: a short path across the grass between the two cul-de-sacs that almost touch.
        b.AddPath(StreetGraphBuilder.WalkRef.Ring(jGF, new Vector3(MMSpurX - 10f, 0f, MMBulbAZ + Ring)), null,
                  StreetGraphBuilder.WalkRef.Ring(jPD, new Vector3(MMSpurX - 10f, 0f, MMBulbBZ - Ring)));

        // Houses: door (probed against the house), garden path, curb slot, driveway, car slots, mailbox, butler post.
        var specs = new List<(LotInfo lot, StreetGraphBuilder.HouseSpec spec)>();
        foreach (var l in c.lots)
        {
            var f = l.f;
            float pathA = l.estate ? l.walkA : l.doorA;
            var spec = new StreetGraphBuilder.HouseSpec
            {
                stopId = l.stopId, size = (byte)Mathf.Clamp(l.house.size, 0, 2), frontPoint = f.P(l.sc, 0f, 0f), normal = f.normal,
                slot = new Vector3(l.stopPos.x, WalkTop, l.stopPos.z), pathEnd = f.P(l.sc + pathA, PathEndD, WalkTop),
                mailbox = l.mailboxPos, butlerPost = l.butlerPost, car0 = l.carPose0, car1 = l.carPose1,
                cars = (byte)l.placedCars.Count, hasDrive = l.hasDrive && l.dwW > 0f,
                ramp = f.P(l.sc + l.dwC, RampD, WalkTop),
            };
            if (l.estate)
                spec.bends = new[] { f.P(l.sc + l.aHouse, l.frontD - 4f, WalkTop), f.P(l.sc + l.walkA, l.frontD - 4f, WalkTop) };
            spec.top = l.placedCars.Count > 0 ? l.carPose0.position : f.P(l.sc + l.dwC, Mathf.Max(PathEndD + 0.4f, l.estate ? l.frontD - 7.5f : l.dwEnd - 2.9f), WalkTop);
            spec.door = ProbeDoor(c, l, f.P(l.sc + l.doorA, l.doorD, GrassTop), l.estate ? spec.bends[0] : spec.pathEnd);
            b.AddHouse(spec);
            specs.Add((l, spec));
        }

        // Stroll loops (§5.2): block rings and figure-eights per district.
        Vector3 P(float x, float z) => new Vector3(x, 0f, z);
        b.AddLoop(0, "Pickle Park block", P(ElmX + W, MapleZ + W), P(OakX - W, MapleZ + W), P(OakX - W, BirchZ - W), P(ElmX + W, BirchZ - W));
        b.AddLoop(0, "Pickle Park west half", P(ElmX + W, MapleZ + W), P(ParkPathX, MapleZ + W), P(ParkPathX, BirchZ - W), P(ElmX + W, BirchZ - W));
        b.AddLoop(0, "Pickle Park east half", P(ParkPathX, MapleZ + W), P(OakX - W, MapleZ + W), P(OakX - W, BirchZ - W), P(ParkPathX, BirchZ - W));
        b.AddLoop(0, "Mildly Pleasant outer ring", P(ElmX + W, MapleZ - W), P(OakX + W, MapleZ - W), P(OakX + W, BirchZ + W), P(ElmX - W, BirchZ + W), P(ElmX - W, MapleZ - W));
        b.AddLoop(1, "Meadows inner ring", P(MMWestX + W, MileZ + W), P(MMEastX - W, MileZ + W), P(MMEastX - W, MMNorthZ - W), P(MMWestX + W, MMNorthZ - W));
        b.AddLoop(1, "Meadows outer ring", P(MMWestX - W, MileZ - W), P(MMEastX + W, MileZ - W), P(MMEastX + W, MMNorthZ + W), P(MMWestX - W, MMNorthZ + W));
        b.AddLoop(1, "Meadows cul-de-sac figure", P(MMSpurX - W, MileZ + W), P(MMSpurX - Ring, MMBulbAZ), P(MMSpurX - Ring, MMBulbBZ),
                  P(MMSpurX - W, MMNorthZ - W), P(MMWestX + W, MMNorthZ - W), P(MMWestX + W, MileZ + W));
        b.AddLoop(2, "Snootington inner ring", P(ElmX + W, SHSouthZ + W), P(SHEastX - W, SHSouthZ + W), P(SHEastX - W, SHNorthZ - W), P(ElmX + W, SHNorthZ - W));
        b.AddLoop(2, "Snootington outer ring", P(ElmX - W, SHSouthZ - W), P(ElmX - W, SHNorthZ + W), P(SHEastX + W, SHNorthZ + W), P(SHEastX + W, SHSouthZ - W));
        b.AddLoop(3, "Offshore promenade", P(ElmX + W, BBAveZ + W), P(BBBulbX + 12f + StreetGraphBuilder.BulbWalkMargin, BBAveZ), P(ElmX + W, BBAveZ - W),
                  P(ElmX - W, BBAveZ - W), P(ElmX - W, BBAveZ + W));

        var g = b.Build();
        g.name = "StreetGraph";

        // Cars whose driveway doesn't join a lane stay parked (corner lots).
        int parkedOnly = 0;
        foreach (var (lot, spec) in specs)
            foreach (var hc in lot.placedCars)
            {
                hc.parkedOnly = !spec.driveConnected;
                if (hc.parkedOnly) parkedOnly++;
            }
        c.graphNotes.Append(b.notes);
        c.graphNotes.AppendLine($"    household cars: {c.householdCars.Count} ({parkedOnly} parked-only at corner lots); builder warnings {b.warnings}");
        return g;
    }

    /// <summary>Where a person stands at a house's front door: the first hit on the house from the garden path (at 1 m),
    /// 0.45 m out, standing on the porch deck when there is one; stepped toward the street until a 0.35 m person fits
    /// (porch posts, railings, steps).</summary>
    static Vector3 ProbeDoor(Ctx c, LotInfo l, Vector3 estimate, Vector3 from)
    {
        Vector3 o = new Vector3(from.x, 1f, from.z), target = new Vector3(estimate.x, 1f, estimate.z);
        Vector3 dir = target - o;
        float dist = dir.magnitude;
        dir = dist > 1e-3f ? dir / dist : -l.f.normal;
        Vector3 facade = new Vector3(estimate.x, 1f, estimate.z);
        Transform owner = l.houseGo != null ? (l.estate ? l.houseGo.transform.parent : l.houseGo.transform) : null;
        float best = float.MaxValue;
        foreach (var h in Physics.RaycastAll(o, dir, dist + 3f, ~0, QueryTriggerInteraction.Ignore))
        {
            if (owner == null || h.collider.gameObject.layer != LayerGround || !h.collider.transform.IsChildOf(owner)) continue;
            if (h.distance < best) { best = h.distance; facade = h.point; }
        }
        Vector3 door = facade;
        for (int k = 0; k <= 70; k++)
        {
            door = facade - dir * (0.45f + 0.1f * k);
            door.y = DeckHeight(door, owner);
            if (StreetGraphProbes.PersonClear(door, c.scene, out _)) return door;
        }
        door.y = GrassTop;
        return door;
    }

    /// <summary>The walkable height at p: a house's low porch deck / step (≤ 0.45 m) when p stands on one, else the
    /// lawn. Anything higher (a raised porch, a railing) is not stood on: the door node steps out in front of it.</summary>
    static float DeckHeight(Vector3 p, Transform owner)
    {
        float y = GrassTop;
        if (owner == null) return y;
        foreach (var h in Physics.RaycastAll(new Vector3(p.x, 1.4f, p.z), Vector3.down, 1.6f, ~0, QueryTriggerInteraction.Ignore))
            if (h.collider.gameObject.layer == LayerGround && h.collider.transform.IsChildOf(owner) && h.point.y <= 0.45f && h.point.y > y) y = h.point.y;
        return y;
    }

    /// <summary>Package B's edit-time bake of each knockable's asset meshes (Setup_WorldLifeKnock.BakeScene), so a freshly
    /// built scene can fly its builder-made props straight away. Looked up by name: B's setup may not exist yet.</summary>
    static string BakeKnockProxies(Scene scene)
    {
        var t = typeof(NeighborhoodBuilder).Assembly.GetType("Setup_WorldLifeKnock");
        var m = t?.GetMethod("BakeScene", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null,
                             new[] { typeof(Scene), typeof(int).MakeByRefType() }, null);
        if (m == null) return "[NeighborhoodBuilder] knock proxy bake: Setup_WorldLifeKnock.BakeScene not found (skipped)\n";
        try
        {
            var args = new object[] { scene, 0 };
            string r = m.Invoke(null, args) as string ?? "";
            bool fail = r.Contains("FAIL");
            return $"[NeighborhoodBuilder] knock proxy bake (package B): {args[1]} knockables changed{(fail ? " — FAIL lines in B's report:\n" + r : "")}\n";
        }
        catch (Exception e)
        {
            Debug.LogException(e.InnerException ?? e);
            return "[NeighborhoodBuilder] knock proxy bake (package B) threw: " + (e.InnerException ?? e).Message + "\n";
        }
    }

    static StreetGraph SaveGraphAsset(StreetGraph g, string path)
    {
        EnsureFolder(System.IO.Path.GetDirectoryName(path).Replace('\\', '/'));
        var existing = AssetDatabase.LoadAssetAtPath<StreetGraph>(path);
        if (existing == null)
        {
            g.name = System.IO.Path.GetFileNameWithoutExtension(path);
            AssetDatabase.CreateAsset(g, path);
            existing = g;
        }
        else
        {
            EditorUtility.CopySerialized(g, existing);
            existing.name = System.IO.Path.GetFileNameWithoutExtension(path);
            existing.Invalidate();
            EditorUtility.SetDirty(existing);
        }
        AssetDatabase.SaveAssetIfDirty(existing);
        return existing;
    }

    // ==========================================================================================
    // WorldLife kids (Docs/WorldLife.md §3.1, §12.K): fenced pens where kids cheer the garbage truck. Built AFTER the street
    // graph and with no RNG at all, so the town, the HouseDirectory and StreetGraph.asset are byte-identical with or without
    // them. Each pen is one static GameObject: the hard fence (four BoxColliders, 1.6 m high, 0.9 m thick: even a 20 m/s
    // truck on discrete physics can't get through) + the sunny picket panels + a KidPen with the placement data.
    // ==========================================================================================

    const float KidFenceHalfThick = 0.45f, KidFenceColliderHeight = 1.6f, KidFenceHeightScale = 0.8f, KidLoopInset = 0.7f;
    /// <summary>The inner-park playground's fence: the sand (x 46..71, z 100.5..112) and its kit plus a margin, clear of the
    /// west park path (z 113.75 on) and of the park's trees (kept 2.5 m off the sand).</summary>
    static readonly Rect2 PlaygroundPenRect = new Rect2(45f, 99.5f, 72f, 113f);

    /// <summary>Yard pens: (frontage key, odd side, district tier, where along the frontage 0..1). One per row, on the lot
    /// nearest that point whose front yard has room (between the property line and the house, clear of the garden path,
    /// the driveway and anything solid).</summary>
    static readonly (string key, bool odd, int tier, float at)[] KidYards =
    {
        ("depot", true, 1, 0.5f),      // the first house you pass out of the depot
        ("maple", true, 1, 0.3f),      // Maple St, south side
        ("birch", false, 1, 0.6f),     // Birch St, north side
        ("oak", false, 1, 0.45f),      // Oak Ave, east side
        ("carpool", false, 2, 0.3f),   // Minivan Meadows: Carpool Ln, north side, the first street past the gate
    };

    static void BuildKidPens(Ctx c)
    {
        Physics.SyncTransforms();
        var g = Group(c.gWorldLife, "KidPens");
        var pens = new List<KidPen>();

        // The playground: front = east, toward the north-south park path the truck comes down.
        var r = PlaygroundPenRect;
        var pg = BuildKidPen(c, g, "playground", "", new Vector3(0.5f * (r.x0 + r.x1), SandTop, 0.5f * (r.z0 + r.z1)), 90f,
                             new Vector2(r.D * 0.5f, r.W * 0.5f), 5, true);
        PenSign(c, pg, "PLAYGROUND", "<size=60%>no trucks allowed\n(sorry, truck)</size>");
        pens.Add(pg);

        // The yards: one per row, on the lot nearest the preferred point.
        var tried = new StringBuilder();
        foreach (var y in KidYards)
        {
            var cands = new List<LotInfo>();
            foreach (var l in c.lots)
            {
                if (l.f.key != y.key || l.f.odd != y.odd || l.estate) continue;
                int tier = l.district != null ? l.district.tier : 1;
                if (tier == y.tier) cands.Add(l);
            }
            if (cands.Count == 0) { tried.Append($" {y.key}: no lots;"); continue; }
            float target = cands[0].f.lot0 + y.at * (cands[0].f.lot1 - cands[0].f.lot0);
            cands.Sort((a, b) =>
            {
                int cmp = Mathf.Abs(a.sc - target).CompareTo(Mathf.Abs(b.sc - target));
                return cmp != 0 ? cmp : string.CompareOrdinal(a.stopId, b.stopId);
            });
            bool placed = false;
            foreach (var l in cands)
            {
                if (!TryYardPen(c, l, out var ctr, out float yaw, out var half)) continue;
                int kids = 2 + (l.house != null && l.house.size >= 2 ? 1 : 0);
                pens.Add(BuildKidPen(c, g, l.stopId, l.stopId, ctr, yaw, half, kids, true));
                placed = true;
                break;
            }
            if (!placed) tried.Append($" {y.key}: no front yard with room;");
        }

        // ApplyStatic ran before the graph: give the pens the same flags (the KidPen data is read, never moved).
        const StaticEditorFlags flags = StaticEditorFlags.BatchingStatic | StaticEditorFlags.ContributeGI |
                                        StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic |
                                        StaticEditorFlags.ReflectionProbeStatic;
        foreach (var t in g.GetComponentsInChildren<Transform>(true))
            if (t.GetComponent<TMP_Text>() == null) GameObjectUtility.SetStaticEditorFlags(t.gameObject, flags);
        Physics.SyncTransforms();

        var sb = new StringBuilder();
        int kidsTotal = 0;
        foreach (var p in pens) { kidsTotal += p.kids; sb.Append($" {p.penId} ({p.kids} kids, {p.half.x * 2f:F1}×{p.half.y * 2f:F1} m)"); }
        c.notes.AppendLine($"kids: {pens.Count} pens, {kidsTotal} kids:{sb}{(tried.Length > 0 ? " | skipped:" + tried : "")}");
    }

    /// <summary>A free front-yard rectangle for a kid pen on lot <paramref name="l"/>: between the property line (behind the
    /// Meadows' knockable picket front) and the bushes along the house front, on a stretch clear of the garden path and the
    /// driveway, with nothing solid in it (a front-yard tree, a flamingo, a yard sign, a lemonade stand) and no bush.</summary>
    static bool TryYardPen(Ctx c, LotInfo l, out Vector3 center, out float yaw, out Vector2 half)
    {
        center = default; yaw = 0f; half = default;
        if (l.estate || l.houseGo == null) return false;
        // The Meadows' yards are shallow (setback 5.5–7 m) behind a knockable picket front at PropertyLine + 0.4: the pen
        // sits just behind it (its fence boxes 0.3 m clear of the pickets') and right up to the bushes.
        bool meadows = l.district != null;
        float d0 = PropertyLine + (meadows ? 1.2f : 0.8f);
        float d1 = Mathf.Min(l.frontD - (meadows ? 1.7f : 2.3f), d0 + 4.4f);
        if (d1 - d0 < (meadows ? 2.6f : 2.9f)) return false;
        var free = new List<Vector2> { new Vector2(-l.half + 0.9f, l.half - 0.9f) };
        CutInterval(free, l.doorA - 1.35f, l.doorA + 1.35f);                    // the garden path (+ a person + the fence)
        if (l.dwW > 0f) CutInterval(free, l.dwC - l.dwW * 0.5f - 0.9f, l.dwC + l.dwW * 0.5f + 0.9f);
        yaw = PuppetKit.YawOf(-l.f.normal);
        var rot = Quaternion.Euler(0f, yaw, 0f);
        foreach (float w in new[] { 5.2f, 4.6f, 4.0f, 3.6f })
            foreach (var iv in free)
            {
                if (iv.y - iv.x < w) continue;
                float lo = iv.x + w * 0.5f, hi = iv.y - w * 0.5f, mid = 0.5f * (lo + hi);
                for (int k = 0; k <= 60; k++)
                {
                    float a = mid + (k % 2 == 0 ? 1f : -1f) * ((k + 1) / 2) * 0.25f;
                    if (a < lo - 1e-3f || a > hi + 1e-3f) continue;
                    var ctr = l.f.P(l.sc + a, 0.5f * (d0 + d1), GrassTop);
                    var hlf = new Vector2(w * 0.5f, 0.5f * (d1 - d0));
                    if (!KidPenClear(c, l.houseGo.transform.parent, ctr, rot, hlf, meadows)) continue;
                    center = ctr; half = hlf;
                    return true;
                }
            }
        return false;
    }

    static void CutInterval(List<Vector2> segs, float a, float b)
    {
        for (int i = segs.Count - 1; i >= 0; i--)
        {
            var s = segs[i];
            if (b <= s.x || a >= s.y) continue;
            segs.RemoveAt(i);
            if (b < s.y) segs.Insert(i, new Vector2(b, s.y));
            if (a > s.x) segs.Insert(i, new Vector2(s.x, a));
        }
    }

    /// <summary>Nothing solid (taller than a curb) in the pen with its fence, and no bush or tree of the lot in it
    /// (<paramref name="softBushes"/>: a bush may brush the fence line, just not stand inside the pen).</summary>
    static bool KidPenClear(Ctx c, Transform lot, Vector3 ctr, Quaternion rot, Vector2 hlf, bool softBushes = false)
    {
        float pad = KidFenceHalfThick + 0.1f;
        var hits = Physics.OverlapBox(ctr + Vector3.up * 1.2f, new Vector3(hlf.x + pad, 0.8f, hlf.y + pad), rot, ~0, QueryTriggerInteraction.Ignore);
        foreach (var h in hits)
        {
            if (h.gameObject.scene != c.scene) continue;
            if (h.bounds.max.y <= ctr.y + 0.3f) continue;                       // the lawn, a path, a driveway slab
            return false;
        }
        if (lot == null) return true;
        var inv = Quaternion.Inverse(rot);
        foreach (Transform ch in lot)
        {
            if (!ch.name.StartsWith("SM_bush", StringComparison.Ordinal) && !ch.name.StartsWith("SM_tree", StringComparison.Ordinal)) continue;
            var rr = ch.GetComponentInChildren<Renderer>();
            bool tree = ch.name.StartsWith("SM_tree", StringComparison.Ordinal);
            float rad = rr != null ? Mathf.Max(rr.bounds.extents.x, rr.bounds.extents.z) * (tree ? 0.25f : 0.9f) : 0.6f;
            if (softBushes && !tree) rad = 0.3f - pad;                                 // only its middle must stay out
            Vector3 lp = inv * (ch.position - ctr);
            if (Mathf.Abs(lp.x) <= hlf.x + pad + rad && Mathf.Abs(lp.z) <= hlf.y + pad + rad) return false;
        }
        return true;
    }

    /// <summary>One pen: the root (KidPen + the four fence BoxColliders, layer Default) and the picket panels round it.</summary>
    static KidPen BuildKidPen(Ctx c, Transform parent, string penId, string stopId, Vector3 center, float yaw, Vector2 half, int kids, bool sign)
    {
        var go = new GameObject("KidPen_" + penId);
        go.transform.SetParent(parent, false);
        var rot = Yaw(yaw);
        go.transform.SetPositionAndRotation(center, rot);
        go.layer = LayerDefault;
        Vector3 W(float x, float z) => center + rot * new Vector3(x, 0f, z);
        float hx = half.x, hz = half.y;
        Vector3 bl = W(-hx, -hz), br = W(hx, -hz), fr = W(hx, hz), fl = W(-hx, hz);
        KidFenceRun(c, go.transform, bl, br);
        KidFenceRun(c, go.transform, br, fr);
        KidFenceRun(c, go.transform, fr, fl);
        KidFenceRun(c, go.transform, fl, bl);
        // The hard fence: one box per side, the full length plus the thickness so the corners are sealed.
        float T = KidFenceHalfThick, Hc = KidFenceColliderHeight;
        void Box(Vector3 localCenter, Vector3 size)
        {
            var b = go.AddComponent<BoxCollider>();
            b.center = localCenter;
            b.size = size;
        }
        Box(new Vector3(0f, Hc * 0.5f, -hz), new Vector3(2f * hx + 2f * T, Hc, 2f * T));
        Box(new Vector3(0f, Hc * 0.5f, hz), new Vector3(2f * hx + 2f * T, Hc, 2f * T));
        Box(new Vector3(-hx, Hc * 0.5f, 0f), new Vector3(2f * T, Hc, 2f * hz + 2f * T));
        Box(new Vector3(hx, Hc * 0.5f, 0f), new Vector3(2f * T, Hc, 2f * hz + 2f * T));

        var pen = go.AddComponent<KidPen>();
        pen.penId = penId;
        pen.stopId = stopId ?? "";
        pen.district = DistrictByte(center);
        pen.kids = kids;
        pen.center = center;
        pen.yaw = yaw;
        pen.half = half;
        pen.inset = KidLoopInset;
        pen.sign = sign;
        pen.spots = KidSpots(c, pen, go, kids);
        return pen;
    }

    /// <summary>Sunny picket panels from a to b (the builder's 4 m picket mesh, squashed to 0.86 m so the kids show over it).
    /// No colliders here: the pen root carries the fence's boxes.</summary>
    static void KidFenceRun(Ctx c, Transform parent, Vector3 a, Vector3 b)
    {
        Vector3 d = b - a; d.y = 0f;
        float len = d.magnitude;
        if (len < 0.3f) return;
        Vector3 v = d / len;
        var mesh = PicketPanelMesh(PicketPanelLength);
        int n = Mathf.Max(1, Mathf.RoundToInt(len / PicketPanelLength));
        float each = len / n;
        var rot = Quaternion.LookRotation(v);
        for (int i = 0; i < n; i++)
        {
            var go = new GameObject("Panel");
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(a + v * (each * i), rot);
            go.transform.localScale = new Vector3(1f, KidFenceHeightScale, each / PicketPanelLength);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = c.M("NB_KidFence");
        }
    }

    /// <summary>Home spots for the kids: clear cells inside the run loop whose straight way out to the loop is clear too
    /// (playground kit), spread by farthest-point sampling from the cell nearest the pen's centre. Deterministic, no RNG.</summary>
    static Vector3[] KidSpots(Ctx c, KidPen pen, GameObject own, int n)
    {
        var own0 = new HashSet<Collider>(own.GetComponents<Collider>());
        bool Clear(Vector3 p, float r)
        {
            foreach (var h in Physics.OverlapCapsule(p + Vector3.up * (0.3f + r), p + Vector3.up * 1.2f, r, ~0, QueryTriggerInteraction.Ignore))
            {
                if (h.gameObject.scene != c.scene || own0.Contains(h)) continue;
                if (h.bounds.max.y <= p.y + 0.25f) continue;
                return false;
            }
            return true;
        }
        var lh = pen.LoopHalf;
        // Big pens (the playground) keep their kids off the fence while they play: they run to it when a truck comes.
        float mx = lh.x >= 3f ? 1.5f : 0.5f, mz = lh.y >= 3f ? 1.5f : 0.5f;
        var cands = new List<Vector3>();
        for (float z = -lh.y + mz; z <= lh.y - mz + 1e-3f; z += 0.5f)
            for (float x = -lh.x + mx; x <= lh.x - mx + 1e-3f; x += 0.5f)
            {
                var p = pen.ToWorld(x, z);
                if (!Clear(p, 0.35f)) continue;
                var q = pen.LoopPoint(pen.LoopNearest(p));
                bool ok = true;
                float len = Vector3.Distance(p, q);
                for (float t = 0.4f; t < len && ok; t += 0.4f) ok = Clear(Vector3.Lerp(p, q, t / len), 0.25f);
                if (ok) cands.Add(p);
            }
        var picked = new List<Vector3>();
        if (cands.Count == 0) return picked.ToArray();
        int first = 0;
        for (int i = 1; i < cands.Count; i++)
            if ((cands[i] - pen.center).sqrMagnitude < (cands[first] - pen.center).sqrMagnitude - 1e-4f) first = i;
        picked.Add(cands[first]);
        while (picked.Count < n)
        {
            int best = -1; float bestD = -1f;
            for (int i = 0; i < cands.Count; i++)
            {
                float m = float.MaxValue;
                foreach (var p in picked) m = Mathf.Min(m, (cands[i] - p).sqrMagnitude);
                if (m > bestD + 1e-4f) { bestD = m; best = i; }
            }
            if (best < 0 || bestD < 0.25f) break;
            picked.Add(cands[best]);
        }
        return picked.ToArray();
    }

    /// <summary>A board on the pen's front fence (outside face), inside the fence's collider so it adds no obstacle.</summary>
    static void PenSign(Ctx c, KidPen pen, string title, string small)
    {
        var rot = Yaw(pen.yaw);
        Vector3 pos = pen.ToWorld(0f, pen.half.y + 0.05f) + Vector3.up * 0.55f;
        var board = SignBoard(c, pen.transform, "Sign", pos, rot, new Vector3(2.3f, 0.62f, 0.05f), c.M("NB_SignGreen"),
                              title + "\n" + small, new Color(1f, 0.93f, 0.4f), false);
        foreach (var col in board.parent.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(col);
    }

    /// <summary>§12.K checks: every pen has its four fence boxes, a clear run loop (0.25 m person every 0.5 m), its home
    /// spots inside and clear, and kids to show.</summary>
    static void ValidateKidPens(Ctx c, StringBuilder sb, Action<string> fail)
    {
        var pens = new List<KidPen>();
        foreach (var r in c.scene.GetRootGameObjects()) pens.AddRange(r.GetComponentsInChildren<KidPen>(true));
        if (pens.Count == 0) { fail("kids: no KidPen in the scene"); return; }
        int kids = 0, loopBad = 0, spotBad = 0;
        foreach (var p in pens)
        {
            kids += p.kids;
            var own = new HashSet<Collider>(p.GetComponents<Collider>());
            int boxes = 0;
            foreach (var b in p.GetComponents<BoxCollider>()) if (b.enabled && !b.isTrigger) boxes++;
            if (boxes != 4) fail($"kids: pen {p.penId} has {boxes} fence boxes (want 4)");
            bool Blocked(Vector3 q, float r, out string who)
            {
                who = null;
                foreach (var h in Physics.OverlapCapsule(q + Vector3.up * (0.3f + r), q + Vector3.up * 1.2f, r, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (h.gameObject.scene != c.scene || own.Contains(h) || h.bounds.max.y <= q.y + 0.25f) continue;
                    who = h.name;
                    return true;
                }
                return false;
            }
            int lb = 0; string first = null;
            for (float s = 0f; s < p.LoopLength; s += 0.5f)
                if (Blocked(p.LoopPoint(s), 0.25f, out var who)) { lb++; first ??= who; }
            int sbad = 0;
            if (p.spots == null || p.spots.Length < p.kids) { sbad += p.kids - (p.spots?.Length ?? 0); }
            if (p.spots != null) foreach (var s in p.spots) if (!p.Inside(s, -p.inset * 0.5f) || Blocked(s, 0.3f, out _)) sbad++;
            loopBad += lb; spotBad += sbad;
            sb.AppendLine($"    pen {p.penId,-16} {p.kids} kids, fence {p.half.x * 2f:F1}×{p.half.y * 2f:F1} m at {p.center.ToString("F1")} yaw {p.yaw:F0}, loop {p.LoopLength:F1} m " +
                          $"(blocked {lb}{(first != null ? " by " + first : "")}), spots {(p.spots?.Length ?? 0)} (bad {sbad})");
        }
        sb.AppendLine($"  kids: {pens.Count} pens, {kids} kids; loop samples blocked {loopBad}, bad spots {spotBad}");
        if (loopBad > 0) fail($"kids: {loopBad} run-loop samples blocked");
        if (spotBad > 0) fail($"kids: {spotBad} home spots missing / outside / blocked");
    }

    // ---------------- validation helpers (§5.3)

    static int CountGameObjects(Scene s)
    {
        int n = 0;
        foreach (var r in s.GetRootGameObjects()) n += r.GetComponentsInChildren<Transform>(true).Length;
        return n;
    }

    static List<HouseDirectory.Entry> SnapshotHouseDirectory()
    {
        var dir = AssetDatabase.LoadAssetAtPath<HouseDirectory>(HouseDirectory.AssetPath);
        if (dir == null) return null;
        var list = new List<HouseDirectory.Entry>();
        foreach (var e in dir.houses)
            list.Add(new HouseDirectory.Entry { stopId = e.stopId, address = e.address, capacity = e.capacity, wealth = e.wealth, position = e.position, districtId = e.districtId });
        return list;
    }

    /// <summary>The HouseDirectory this build would write vs the one on disk before it: stopIds, addresses, capacity,
    /// wealth, districts identical and positions within 0.001 m (district 1's byte stability, §5.3).</summary>
    static void CompareHouseDirectory(Ctx c, StringBuilder sb, Action<string> fail)
    {
        if (c.directoryBefore == null) { sb.AppendLine("  house directory: no previous asset to compare"); return; }
        var now = new Dictionary<string, PickupStop>();
        foreach (var s in c.stops) if (s != null && !string.IsNullOrEmpty(s.stopId)) now[s.stopId] = s;
        int same = 0, diff = 0, d1 = 0, lines = 0;
        foreach (var e in c.directoryBefore)
        {
            string why = null;
            if (!now.TryGetValue(e.stopId, out var s)) why = "missing";
            else if (s.address != e.address) why = $"address '{s.address}' != '{e.address}'";
            else if (s.Capacity != e.capacity) why = $"capacity {s.Capacity} != {e.capacity}";
            else if (s.wealth != e.wealth) why = $"wealth {s.wealth:R} != {e.wealth:R}";
            else if (Vector3.Distance(s.transform.position, e.position) > 0.001f) why = $"position {s.transform.position.ToString("F3")} != {e.position.ToString("F3")}";
            else if (!string.IsNullOrEmpty(e.districtId) && s.District.id != e.districtId) why = $"district {s.District.id} != {e.districtId}";
            if (why == null) { same++; if (string.IsNullOrEmpty(Districts.ForStopId(e.stopId).stopPrefix)) d1++; continue; }
            diff++;
            if (lines++ < 8) sb.AppendLine($"    house {e.stopId}: {why}");
        }
        if (now.Count != c.directoryBefore.Count) { diff++; sb.AppendLine($"    house count {now.Count} != {c.directoryBefore.Count}"); }
        sb.AppendLine($"  house directory: {same}/{c.directoryBefore.Count} entries identical (district 1: {d1}), {diff} differ");
        if (diff > 0) fail($"house directory changed ({diff} entries)");
    }

    /// <summary>Exits reachable, and which car households reach one with only their own district (and the starter) unlocked.</summary>
    static void ReportGraphReach(Ctx c, StringBuilder sb, Action<string> fail)
    {
        var g = c.graph;
        var reach = new bool[g.NodeCount];
        var q = new Queue<int>();
        var exitReached = new int[g.exits.Length];
        var perDistrict = new int[4, 3];      // households with cars: total, reach an exit with own + starter unlocked, with only the starter
        for (int h = 0; h < g.houses.Length; h++)
        {
            var hs = g.houses[h];
            if (hs.cars == 0 || hs.driveOut == StreetGraph.None) continue;
            int d = Mathf.Min(3, (int)hs.district);
            perDistrict[d, 0]++;
            g.Flood(hs.driveOut, reach, q, walk: false, unlockedMask: -1, district: -1);
            for (int x = 0; x < g.exits.Length; x++) if (reach[g.exits[x].node]) exitReached[x]++;
            g.Flood(hs.driveOut, reach, q, walk: false, unlockedMask: 1 | (1 << d), district: -1);
            foreach (var x in g.exits) if (reach[x.node]) { perDistrict[d, 1]++; break; }
            g.Flood(hs.driveOut, reach, q, walk: false, unlockedMask: 1, district: -1);
            foreach (var x in g.exits) if (reach[x.node]) { perDistrict[d, 2]++; break; }
        }
        for (int x = 0; x < g.exits.Length; x++)
        {
            sb.AppendLine($"  exit {g.exits[x].id} (district {g.exits[x].district}, node {g.exits[x].node}): reached by {exitReached[x]} car households (all gates open)");
            if (exitReached[x] == 0) fail($"exit {g.exits[x].id} is reached by no household");
        }
        for (int d = 0; d < 4; d++)
        {
            int stay = perDistrict[d, 0] - perDistrict[d, 2];
            sb.AppendLine($"  district {d}: {perDistrict[d, 0]} car households; reach an exit with their district unlocked {perDistrict[d, 1]}, with only Mildly Pleasant unlocked {perDistrict[d, 2]} (the other {stay} stay home while locked)");
            if (perDistrict[d, 1] != perDistrict[d, 0]) fail($"district {d}: {perDistrict[d, 0] - perDistrict[d, 1]} car households can't reach an exit even with their own district unlocked");
        }
    }

    // ------------------------------------------------------------------------------------------
    // Static flags
    // ------------------------------------------------------------------------------------------

    static void ApplyStatic(Ctx c)
    {
        const StaticEditorFlags flags = StaticEditorFlags.BatchingStatic | StaticEditorFlags.ContributeGI |
                                        StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic |
                                        StaticEditorFlags.ReflectionProbeStatic;
        foreach (var t in c.root.GetComponentsInChildren<Transform>(true))
        {
            if (t == c.root || IsUnder(t, c.dynamicRoots) || t.GetComponent<TMP_Text>() != null) continue;
            if (t.GetComponent<NeighborhoodAtmosphere>() != null) continue;
            GameObjectUtility.SetStaticEditorFlags(t.gameObject, flags);
        }
    }

    static bool IsUnder(Transform t, HashSet<Transform> roots)
    {
        for (var p = t; p != null; p = p.parent) if (roots.Contains(p)) return true;
        return false;
    }

    // ------------------------------------------------------------------------------------------
    // Validation
    // ------------------------------------------------------------------------------------------

    static string Validate(Ctx c)
    {
        Physics.SyncTransforms();
        var sb = new StringBuilder();
        int fails = 0;
        void Fail(string msg) { fails++; sb.AppendLine("  FAIL " + msg); }

        sb.AppendLine($"[NeighborhoodBuilder] seed {c.seed} -> {c.scene.path}");

        // 1) Ground under every stop and spawn (layer Ground, expected height).
        int groundOk = 0;
        foreach (var s in c.stops) if (CheckGround(c, s.transform, WalkTop, "stop " + s.stopId, Fail)) groundOk++;
        foreach (var s in c.spawns) if (CheckGround(c, s.t, s.y, s.t.name, Fail)) groundOk++;
        sb.AppendLine($"  ground raycasts ok: {groundOk}/{c.stops.Count + c.spawns.Count}");

        // 2) Lots / houses don't overlap each other, roads, the park or the depot.
        int overlapIssues = 0;
        for (int i = 0; i < c.lots.Count; i++)
        {
            var a = c.lots[i];
            if (!a.lotRect.Contains(a.houseRect, 0.05f)) { overlapIssues++; Fail($"house outside its lot: {a.name} house {a.houseRect} lot {a.lotRect}"); }
            foreach (var s in c.streets)
                if (a.lotRect.Overlaps(s.Expand(PropertyLine - RoadHalfWidth))) { overlapIssues++; Fail($"lot {a.name} {a.lotRect} overlaps street corridor {s}"); }
            if (a.lotRect.Overlaps(DepotPad.Expand(0.5f))) { overlapIssues++; Fail($"lot {a.name} overlaps depot pad"); }
            if (a.lotRect.Overlaps(ParkRect)) { overlapIssues++; Fail($"lot {a.name} overlaps park"); }
            for (int j = i + 1; j < c.lots.Count; j++)
            {
                var b = c.lots[j];
                if (a.lotRect.Overlaps(b.lotRect)) { overlapIssues++; Fail($"lots overlap: {a.name} {a.lotRect} / {b.name} {b.lotRect}"); }
                if (a.houseRect.Overlaps(b.houseRect)) { overlapIssues++; Fail($"houses overlap: {a.name} / {b.name}"); }
            }
            if (!PlayBounds.Contains(a.lotRect, 0f)) { overlapIssues++; Fail($"lot {a.name} outside play bounds"); }
        }
        sb.AppendLine($"  lots: {c.lots.Count}, overlap issues: {overlapIssues}");

        // 3) Spawn points clear of obstacles (ignoring the floor under them and triggers).
        foreach (var s in c.spawns)
        {
            var t = s.t;
            Vector3 half = s.truck ? new Vector3(1.5f, 1.6f, 3.3f) : new Vector3(0.45f, 0.8f, 0.45f);
            Vector3 center = t.position + Vector3.up * (s.truck ? 1.9f : 1.05f) + t.forward * (s.truck ? 0.8f : 0f);
            var hits = Physics.OverlapBox(center, half, t.rotation, ~0, QueryTriggerInteraction.Ignore);
            var blockers = new List<string>();
            foreach (var h in hits)
            {
                if (h.gameObject.scene != c.scene) continue;
                if (h.bounds.max.y <= t.position.y + 0.25f) continue; // the ground itself
                blockers.Add(h.name);
            }
            if (blockers.Count > 0) Fail($"spawn {t.name} blocked by {string.Join(", ", blockers)}");
        }
        if (c.doorZoneT != null)
        {
            var zone = c.doorZoneT.GetComponent<InteractionZone>();
            foreach (var s in c.spawns)
                if (s.t.name == "SpawnPoint_FromGarage_Truck" && zone.Contains(s.t.position)) Fail("truck spawn is inside the garage InteractionZone (would re-trigger the door)");
        }
        sb.AppendLine($"  spawn points checked: {c.spawns.Count}");

        // 4) Road network connected + every stop sits at a curb of a reachable road.
        var reached = new bool[c.roads.Count];
        int depotIdx = c.roads.Count - 1;
        var queue = new Queue<int>();
        queue.Enqueue(depotIdx); reached[depotIdx] = true;
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            for (int j = 0; j < c.roads.Count; j++)
                if (!reached[j] && c.roads[i].Expand(0.05f).Overlaps(c.roads[j], 0f)) { reached[j] = true; queue.Enqueue(j); }
        }
        for (int i = 0; i < c.roads.Count; i++) if (!reached[i]) Fail($"road {c.roads[i]} not connected to the depot");
        var ids = new HashSet<string>();
        foreach (var s in c.stops)
        {
            if (!ids.Add(s.stopId)) Fail("duplicate stopId " + s.stopId);
            var p = s.transform.position;
            float best = float.MaxValue; int bi = -1;
            for (int i = 0; i < c.roads.Count; i++) { float d = c.roads[i].Distance(p.x, p.z); if (d < best) { best = d; bi = i; } }
            if (bi < 0 || best > 1.5f || !reached[bi]) Fail($"stop {s.stopId} is {best:F2} m from the nearest road");
            Vector3 toRoad = new Vector3(Mathf.Clamp(p.x, c.roads[bi].x0, c.roads[bi].x1) - p.x, 0f, Mathf.Clamp(p.z, c.roads[bi].z0, c.roads[bi].z1) - p.z);
            if (toRoad.sqrMagnitude > 0.01f && Vector3.Dot(toRoad.normalized, s.transform.forward) < 0.9f) Fail($"stop {s.stopId} forward does not point at the road");
        }
        if (c.stops.Count != c.lots.Count) Fail($"{c.stops.Count} PickupStops for {c.lots.Count} houses (want one per house)");
        int crowded = 0;
        foreach (var s in c.stops)
        {
            // Curb slots: a row along the curb (+-0.8 m) 0.3 m back and a second row 1.0 m back (+0.4..+1.2 / -0.4 m).
            var t = s.transform;
            for (int slot = 0; slot < s.Capacity; slot++)
            {
                Vector3 p = s.SlotPosition(slot, false);
                var hits = Physics.OverlapBox(p + Vector3.up * 0.42f, new Vector3(0.26f, 0.32f, 0.26f), t.rotation, ~0, QueryTriggerInteraction.Ignore);
                foreach (var h in hits)
                {
                    if (h.gameObject.scene != c.scene || h.gameObject.layer == LayerGround) continue;
                    sb.AppendLine($"  WARN stop {s.stopId} slot {slot}: '{h.name}' crowds it (runtime nudges the bag)");
                    crowded++;
                    break;
                }
            }
            if (!SlotsOnSidewalk(s)) Fail($"stop {s.stopId}: curb slots leave the sidewalk");
        }
        bool SlotsOnSidewalk(PickupStop s)
        {
            // Every slot must sit on Ground at sidewalk height.
            for (int slot = 0; slot < s.Capacity; slot++)
            {
                Vector3 p = s.SlotPosition(slot, false);
                if (!Physics.Raycast(p + Vector3.up * 2f, Vector3.down, out var hit, 4f, 1 << LayerGround, QueryTriggerInteraction.Ignore)) return false;
                if (Mathf.Abs(hit.point.y - WalkTop) > 0.06f) return false;
            }
            return true;
        }
        int capSum = 0; foreach (var s in c.stops) capSum += s.Capacity;
        sb.AppendLine($"  route '{RouteName}': {c.stops.Count} stops (every house), curb capacity total {capSum}, crowded slots {crowded}");
        foreach (var s in c.stops) sb.AppendLine($"    {s.stopId,-10} {s.address,-14} cap {s.bagCount} wealth {s.wealth:F2} @ {s.transform.position.ToString("F1")}");

        // 4b) Drivability: both lanes of every street (+ the depot apron) have continuous Ground at road
        //     height and truck head-room (2.6 w x 3.3 h) with nothing solid in the way.
        int samples = 0, bad = 0;
        void Sweep(Vector3 a, Vector3 b, string label)
        {
            Vector3 d = b - a; float len = d.magnitude; Vector3 v = d / len;
            var rot = Quaternion.LookRotation(v);
            for (float t = 0f; t <= len + 0.01f; t += 2f)
            {
                var p = a + v * t;
                samples++;
                string why = null;
                var hits = Physics.RaycastAll(p + Vector3.up * 2f, Vector3.down, 4f, ~0, QueryTriggerInteraction.Ignore);
                bool found = false; RaycastHit best = default;
                foreach (var h in hits)
                    if (h.collider.gameObject.scene == c.scene && (!found || h.distance < best.distance)) { best = h; found = true; }
                if (!found) why = "no ground";
                else if (best.collider.gameObject.layer != LayerGround || Mathf.Abs(best.point.y - RoadTop) > 0.03f) why = $"ground '{best.collider.name}' y={best.point.y:F2}";
                else
                {
                    foreach (var h in Physics.OverlapBox(p + Vector3.up * 1.9f, new Vector3(1.3f, 1.55f, 1f), rot, ~0, QueryTriggerInteraction.Ignore))
                        if (h.gameObject.scene == c.scene && h.bounds.max.y > RoadTop + 0.25f && h.name != "GateBarrier") { why = $"blocked by '{h.name}'"; break; }
                }
                if (why != null && ++bad <= 8) Fail($"lane {label} @ {p.ToString("F1")}: {why}");
            }
        }
        foreach (float off in new[] { -2.5f, 2.5f })
        {
            Sweep(new Vector3(ElmX, 0, MapleZ + off), new Vector3(OakX, 0, MapleZ + off), "Maple");
            Sweep(new Vector3(ElmX, 0, BirchZ + off), new Vector3(OakX, 0, BirchZ + off), "Birch");
            Sweep(new Vector3(ElmX + off, 0, MapleZ), new Vector3(ElmX + off, 0, BirchZ), "Elm");
            Sweep(new Vector3(OakX + off, 0, MapleZ), new Vector3(OakX + off, 0, BirchZ), "Oak");
            Sweep(new Vector3(ElmX + off, 0, DepotRoadStartZ), new Vector3(ElmX + off, 0, MapleZ), "Depot Rd");
        }
        Sweep(new Vector3(0f, 0f, DepotFrontZ + 5f), new Vector3(0f, 0f, DepotRoadStartZ), "depot apron");
        // New districts: both lanes of every straight road (centre lines), the Mile out of the depot pad included.
        foreach (var d in c.districts)
            foreach (var l in d.lines)
            {
                Vector3 dir = (l.b - l.a).normalized;
                Vector3 side = Vector3.Cross(Vector3.up, dir);
                foreach (float off in new[] { -2.5f, 2.5f })
                    Sweep(l.a + side * off, l.b + side * off, d.prefix + l.name);
            }
        Sweep(new Vector3(10f, 0f, MileZ), new Vector3(DepotPad.x1 + 4f, 0f, MileZ), "pad -> Minivan Mile");
        // WorldLife exits: both lanes from the junction to just short of the tunnel mouth.
        foreach (var x in c.exits)
        {
            Vector3 side = RightOf(x.forward);
            foreach (float off in new[] { -2.5f, 2.5f })
                Sweep(x.junction + x.forward * (RoadHalfWidth + 1f) + side * off, x.portal - x.forward * 1f + side * off, "exit " + x.id);
        }
        sb.AppendLine($"  drivability samples: {samples}, problems: {bad}");

        // 4c) Districts: stops per district + prefixes, gates wired, wall colliders on layer Walls.
        var perDistrict = new Dictionary<string, int>();
        foreach (var s in c.stops)
        {
            var dd = s.District;
            perDistrict[dd.id] = perDistrict.TryGetValue(dd.id, out var pn) ? pn + 1 : 1;
            if (!string.IsNullOrEmpty(dd.stopPrefix) && !s.stopId.StartsWith(dd.stopPrefix)) Fail($"stop {s.stopId} in {dd.id} lacks prefix {dd.stopPrefix}");
            if (Districts.ForStopId(s.stopId) != dd) Fail($"stop {s.stopId}: districtId {s.districtId} != prefix district {Districts.ForStopId(s.stopId).id}");
            if (Districts.AtPosition(s.transform.position) != dd) Fail($"stop {s.stopId} at {s.transform.position} is outside {dd.id}'s bounds ({Districts.AtPosition(s.transform.position).id})");
        }
        foreach (var dd in Districts.All)
        {
            int have = perDistrict.TryGetValue(dd.id, out var hn) ? hn : 0;
            sb.AppendLine($"  {dd.id}: {have} houses (spec {dd.houses})");
            if (have != dd.houses) Fail($"{dd.id} has {have} houses, spec {dd.houses}");
        }
        foreach (var g in c.gates)
            if (g.barrierCollider == null || g.barrierCollider.gameObject.layer != LayerWalls || g.barrierPivot == null || g.ribbon == null || g.statusText == null)
                Fail($"gate {g.name} is missing parts");
        sb.AppendLine($"  gates: {c.gates.Count}");

        // 5) WorldLife (Docs/WorldLife.md §5.3 + §12.3 A): knockables, the street graph and its scene probes, exits,
        //    gates, district-1 byte stability.
        sb.AppendLine("  --- WorldLife (package A) ---");
        int kf = StreetGraphProbes.CheckKnockables(c.scene, sb, out int knockables);
        if (kf > 0) Fail($"{kf} knockable problems");
        if (knockables < 700) Fail($"only {knockables} knockables (want >= 700)");
        if (c.graph == null) Fail("no street graph");
        else
        {
            var errors = new List<string>();
            int ge = c.graph.Validate(errors);
            sb.AppendLine("  street graph: " + c.graph.Describe().Replace("\n", "\n    "));
            for (int i = 0; i < Mathf.Min(15, errors.Count); i++) sb.AppendLine("    " + errors[i]);
            if (ge > 0) Fail($"street graph: {ge} validation errors");
            int pf = StreetGraphProbes.Run(c.graph, c.scene, sb);
            if (pf > 0) Fail($"street graph probes: {pf} bad samples");
            ReportGraphReach(c, sb, Fail);
            var loopNames = new StringBuilder();
            foreach (var lp in c.graph.loops) loopNames.Append($" d{lp.district}:{lp.nodes.Length}");
            sb.AppendLine($"  loops ({c.graph.loops.Length}, district:nodes):{loopNames}");
            sb.Append(c.graphNotes);
        }
        CompareHouseDirectory(c, sb, Fail);
        ValidateKidPens(c, sb, Fail);

        // 6) Budget + missing assets.
        int total = 0;
        foreach (var r in c.scene.GetRootGameObjects()) total += r.GetComponentsInChildren<Transform>(true).Length;
        int gen = c.root.GetComponentsInChildren<Transform>(true).Length;
        sb.AppendLine($"  GameObjects: generated {gen}, scene total {total} (before this build {c.gameObjectsBefore}, {total - c.gameObjectsBefore:+#;-#;0}; vs the 1 Oct baseline 4,791: {total - 4791:+#;-#;0}, budget +700)");
        if (gen > 5500) Fail($"generated {gen} GameObjects (budget ~5500 for all four districts)");
        if (total - 4791 > WorldLifeTuning.Budget.ExtraGameObjects) Fail($"scene has {total - 4791} more GameObjects than the 1 Oct baseline (budget +{WorldLifeTuning.Budget.ExtraGameObjects})");
        foreach (var m in c.missing) Fail("missing asset " + m);
        sb.Append(c.notes);
        sb.AppendLine(fails == 0 ? "  RESULT: OK" : $"  RESULT: {fails} FAIL(s)");
        return sb.ToString();
    }

    static bool CheckGround(Ctx c, Transform t, float expectY, string label, Action<string> fail)
    {
        var hits = Physics.RaycastAll(t.position + Vector3.up * 2f, Vector3.down, 4f, ~0, QueryTriggerInteraction.Ignore);
        bool found = false; RaycastHit best = default;
        foreach (var h in hits)
        {
            if (h.collider.gameObject.scene != c.scene) continue;
            if (!found || h.distance < best.distance) { best = h; found = true; }
        }
        if (!found) { fail($"{label}: no ground below {t.position.ToString("F2")}"); return false; }
        if (best.collider.gameObject.layer != LayerGround) { fail($"{label}: first hit '{best.collider.name}' is on layer {LayerMask.LayerToName(best.collider.gameObject.layer)}, not Ground"); return false; }
        if (Mathf.Abs(best.point.y - expectY) > 0.03f) { fail($"{label}: ground at y={best.point.y:F3}, expected {expectY:F2} ('{best.collider.name}')"); return false; }
        return true;
    }

    // ------------------------------------------------------------------------------------------
    // Small helpers
    // ------------------------------------------------------------------------------------------

    static Quaternion Yaw(float deg) => Quaternion.Euler(0f, deg, 0f);

    static Transform Group(Transform parent, string name)
    {
        var t = new GameObject(name).transform;
        t.SetParent(parent, false);
        return t;
    }

    static GameObject FindRoot(Scene s, string name)
    {
        foreach (var r in s.GetRootGameObjects()) if (r.name == name) return r;
        return null;
    }

    static GameObject LoadPrefab(Ctx c, string path)
    {
        if (!path.EndsWith(".prefab")) path += ".prefab";
        if (c.prefabs.TryGetValue(path, out var p)) return p;
        p = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (p == null && !c.missing.Contains(path)) c.missing.Add(path);
        c.prefabs[path] = p;
        return p;
    }

    /// <summary>Instantiate a prefab (keeps the prefab link) at a world pose; scale multiplies the prefab's own root scale.</summary>
    static GameObject Place(Ctx c, string path, Transform parent, Vector3 pos, Quaternion rot, float scale, bool mirrorX, int layer)
    {
        var prefab = LoadPrefab(c, path);
        if (prefab == null) return null;
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
        go.transform.SetPositionAndRotation(pos, rot);
        var s = prefab.transform.localScale * scale;
        if (mirrorX) s.x = -s.x;
        go.transform.localScale = s;
        foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
        return go;
    }

    /// <summary>Tree with its full-mesh collider swapped for a trunk capsule (canopies shouldn't snag the truck).</summary>
    static void Tree(Ctx c, Transform parent, string type, Vector3 pos, float scale)
    {
        var go = Place(c, Sub + "Nature/" + type, parent, pos, Yaw(c.R(0f, 360f)), scale, false, LayerDefault);
        if (go == null) return;
        DisableColliders(go);
        var cap = go.AddComponent<CapsuleCollider>();
        cap.radius = 0.4f;
        cap.height = 6f;
        cap.center = new Vector3(0f, 3f, 0f);
        c.trees.Add(pos);
    }

    static void DisableColliders(GameObject go)
    {
        foreach (var col in go.GetComponentsInChildren<Collider>(true)) col.enabled = false;
    }

    static void NoShadows(GameObject go)
    {
        foreach (var r in go.GetComponentsInChildren<Renderer>(true)) r.shadowCastingMode = ShadowCastingMode.Off;
    }

    /// <summary>Flat walkable/drivable slab (BoxCollider, layer Ground) from top down to SlabBottom.</summary>
    static GameObject Slab(Ctx c, Transform parent, string name, Rect2 r, float top, Material m)
    {
        var go = OBox(c, parent, name, new Vector3(0.5f * (r.x0 + r.x1), 0.5f * (top + SlabBottom), 0.5f * (r.z0 + r.z1)),
                      Quaternion.identity, new Vector3(r.W, top - SlabBottom, r.D), m, LayerGround, true, false);
        return go;
    }

    static GameObject OBox(Ctx c, Transform parent, string name, Vector3 center, Quaternion rot, Vector3 size, Material m, int layer, bool collider, bool shadows = true)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(center, rot);
        go.transform.localScale = size;
        var mr = go.GetComponent<MeshRenderer>();
        mr.sharedMaterial = m;
        if (!shadows || size.y < 0.05f) mr.shadowCastingMode = ShadowCastingMode.Off;
        go.layer = layer;
        if (!collider) Object.DestroyImmediate(go.GetComponent<Collider>());
        return go;
    }

    static bool InAny(List<Rect2> rects, float x, float z, float margin = 0f)
    {
        foreach (var r in rects) if (r.Expand(margin).Contains(x, z)) return true;
        return false;
    }

    static bool TooClose(List<Vector3> pts, float x, float z, float minDist)
    {
        float m2 = minDist * minDist;
        foreach (var p in pts) { float dx = p.x - x, dz = p.z - z; if (dx * dx + dz * dz < m2) return true; }
        return false;
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        var parts = path.Split('/');
        string cur = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = cur + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(cur, parts[i]);
            cur = next;
        }
    }
}
