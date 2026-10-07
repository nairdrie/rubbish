using System.Collections.Generic;
using System.IO;
using Cinemachine;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Builds the shop-side sorting payoff in ShopScene under one "SortingStation" root (idempotent: the root is
/// deleted and rebuilt every run):
///   - sorting table (top ≈ 0.95 m, lips/rails, receiver trigger) in the north-west area
///   - a row of six receptacles right behind it: HAZARD barrel, METAL / PLASTIC / GLASS / PAPER bins, KEEP crate
///   - the compactor (hopper, press, SMASH + CRUSH ALL buttons, bale corner) at the table's west end
///   - the TIP IT! lever + painted hazard stripes around the tipping floor behind the truck
///   - the first-person SortStation: stand point in front of the table, "SortCam" virtual camera (disabled until
///     you step up) and the "Step up to the sorting table" entry zones (along the table + compactor front, and
///     round the table's east end by the KEEP crate)
/// Materials go to Assets/Materials/Generated/Sorting/; runtime materials are registered in RubbishAssets.
/// </summary>
public static class Setup_SortingStation
{
    public const string ShopScenePath = "Assets/Scenes/ShopScene.unity";
    public const string MaterialDir = "Assets/Materials/Generated/Sorting";
    public const string RootName = "SortingStation";

    const int DefaultLayer = 0;
    const int IgnoreRaycastLayer = 2;
    const int InteractableLayer = 3;
    const float FallbackFloorY = -0.066f;

    // ---- world layout (ShopScene) ----
    static readonly Vector3 TablePos = new Vector3(-3.25f, 0f, 2.55f);
    static readonly Vector2 TableSize = new Vector2(3.2f, 0.9f);
    const float TableTop = 0.95f;
    const float BackRail = 0.26f, SideRail = 0.16f, FrontLip = 0.08f;
    const float BinRowZ = 3.39f;          // bins sit right behind the table (table back edge z = 3.0)
    const float BinWidth = 0.48f, BinDepth = 0.6f, BinHeight = 1.1f;
    static readonly Vector3 CompactorPos = new Vector3(-5.5f, 0f, 2.5f);     // faces south (rot Y 180)
    static readonly Vector3 BaleCorner = new Vector3(-5.5f, 0f, 3.95f);
    static readonly Vector3 LeverPos = new Vector3(4.45f, 0f, -1.56f);
    // First-person station: feet in front of the table middle (table front edge z = 2.1), facing the bins (+Z).
    static readonly Vector3 StandPos = new Vector3(-3.4f, 0f, 1.1f);
    // "Step up" zone (world XZ): along the table front + the compactor front, clear of the garage door.
    const float EntryMinX = -6.4f, EntryMaxX = -1.0f, EntryMinZ = 0.95f, EntryMaxZ = 2.45f;
    // Second "Step up" zone round the table's east end, beside the KEEP crate (third-person tossing into the station is
    // off, so this spot would otherwise be dead). The pillar starts at z 4.28; the north wall is at z 4.91.
    const float EntryEastMinX = -1.65f, EntryEastMaxX = -0.6f, EntryEastMinZ = 2.1f, EntryEastMaxZ = 3.95f;
    // Compactor buttons turn this far (degrees) toward the stand point: seen edge-on from there otherwise.
    const float ButtonTurn = 40f;
    // Tipping floor painted rectangle (doc: x 4.2–6.8, z -4.6..-1.6; the east wall sits at x 6.76).
    const float TipMinX = 4.2f, TipMaxX = 6.6f, TipMinZ = -4.6f, TipMaxZ = -1.6f;

    [MenuItem("Rubbish/Setup/SortingStation")]
    public static void Run() => Build(ShopScenePath);

    /// <summary>Build into any scene (e.g. a scratch copy of ShopScene for previews).</summary>
    public static string Build(string scenePath) => Build(scenePath, MaterialDir, true);

    /// <summary>
    /// Build into scenePath using materials in matDir. registerAssets=false skips touching RubbishAssets
    /// (for scratch previews).
    /// </summary>
    public static string Build(string scenePath, string matDir, bool registerAssets)
    {
        var scene = SceneManager.GetSceneByPath(scenePath);
        bool opened = false;
        if (!scene.IsValid() || !scene.isLoaded)
        {
            scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            opened = true;
        }

        var log = new System.Text.StringBuilder();
        try
        {
            foreach (var go in scene.GetRootGameObjects())
                if (go.name == RootName) { Object.DestroyImmediate(go); log.AppendLine("Removed old SortingStation root."); }

            var m = new Mats(matDir);
            var root = new GameObject(RootName);
            SceneManager.MoveGameObjectToScene(root, scene);
            Physics.SyncTransforms();

            float floor = FloorY(scene, new Vector3(TablePos.x, 0f, TablePos.z));
            float tipFloor = FloorY(scene, new Vector3((TipMinX + TipMaxX) * 0.5f, 0f, (TipMinZ + TipMaxZ) * 0.5f));

            BuildTable(root.transform, m, floor);
            BuildBins(root.transform, m, floor);
            BuildCompactor(root.transform, m, floor, tipFloor);
            BuildLever(root.transform, m, tipFloor);
            BuildStripes(root.transform, m, tipFloor);
            BuildSortStation(root.transform, floor);

            if (registerAssets)
            {
                RubbishAssets.EditorRegisterMaterial("mat/sorting_outline", m.Outline);
                RubbishAssets.EditorRegisterMaterial("mat/sorting_bale", m.Bale);
                RubbishAssets.EditorRegisterMaterial("mat/sorting_strap", m.Dark);
                RubbishAssets.EditorRegisterMaterial("mat/sorting_sack_scrap", m.Sack);
                log.AppendLine("Registered mat/sorting_outline, mat/sorting_bale, mat/sorting_strap, mat/sorting_sack_scrap.");
            }
            AssetDatabase.SaveAssets();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            log.AppendLine($"Built {RootName} in {scenePath} (floor y={floor:0.###}, tipping floor y={tipFloor:0.###}).");
            foreach (var go in scene.GetRootGameObjects())
                if (go.name == "GarageUpgrades")
                    log.AppendLine("Note: GarageUpgrades is wired to the old station objects; it re-resolves at runtime, but re-run Rubbish/Setup/GarageUpgrades to rebuild the XL leaf against this table.");
        }
        finally
        {
            if (opened) EditorSceneManager.CloseScene(scene, true);
        }
        Debug.Log("[Setup_SortingStation] " + log);
        return log.ToString();
    }

    static float FloorY(Scene scene, Vector3 at)
    {
        var ps = scene.GetPhysicsScene();
        if (ps.Raycast(at + Vector3.up * 3f, Vector3.down, out var hit, 10f, 1 << 6, QueryTriggerInteraction.Ignore))
            return hit.point.y;
        if (Physics.Raycast(at + Vector3.up * 3f, Vector3.down, out hit, 10f, 1 << 6, QueryTriggerInteraction.Ignore))
            return hit.point.y;
        return FallbackFloorY;
    }

    // =====================================================================================================
    // Table
    // =====================================================================================================

    static void BuildTable(Transform root, Mats m, float floor)
    {
        var t = Node(root, "SortingTable", new Vector3(TablePos.x, floor, TablePos.z), Quaternion.identity);
        var table = t.gameObject.AddComponent<SortingTable>();
        table.topSize = TableSize;
        table.topHeight = TableTop;

        float hx = TableSize.x * 0.5f, hz = TableSize.y * 0.5f;
        var vis = Node(t, "Visual", Vector3.zero, Quaternion.identity);
        table.visualRoot = vis;
        Box(vis, "Top", new Vector3(0f, TableTop - 0.03f, 0f), new Vector3(TableSize.x, 0.06f, TableSize.y), m.TableTop);
        Box(vis, "Apron", new Vector3(0f, TableTop - 0.16f, -hz + 0.02f), new Vector3(TableSize.x, 0.2f, 0.04f), m.Yellow);
        Box(vis, "ApronBack", new Vector3(0f, TableTop - 0.16f, hz - 0.02f), new Vector3(TableSize.x, 0.2f, 0.04f), m.Dark);
        foreach (float x in new[] { -hx + 0.06f, 0f, hx - 0.06f })
            foreach (float z in new[] { -hz + 0.06f, hz - 0.06f })
                Box(vis, "Leg", new Vector3(x, (TableTop - 0.06f) * 0.5f, z), new Vector3(0.08f, TableTop - 0.06f, 0.08f), m.Dark);
        Box(vis, "Shelf", new Vector3(0f, 0.22f, 0f), new Vector3(TableSize.x - 0.1f, 0.04f, TableSize.y - 0.1f), m.Dark);
        // Rails: a splash guard at the back (so spills never tumble into the bins behind), side rails, and a
        // low lip on the player side. Heights match the colliders below.
        Box(vis, "SplashGuard", new Vector3(0f, TableTop + BackRail * 0.5f, hz - 0.02f), new Vector3(TableSize.x, BackRail, 0.04f), m.Yellow);
        Box(vis, "SplashGuardTrim", new Vector3(0f, TableTop + BackRail, hz - 0.02f), new Vector3(TableSize.x + 0.02f, 0.03f, 0.06f), m.Dark);
        Box(vis, "RailL", new Vector3(-hx + 0.02f, TableTop + SideRail * 0.5f, 0f), new Vector3(0.04f, SideRail, TableSize.y), m.Yellow);
        Box(vis, "RailR", new Vector3(hx - 0.02f, TableTop + SideRail * 0.5f, 0f), new Vector3(0.04f, SideRail, TableSize.y), m.Yellow);
        Box(vis, "LipFront", new Vector3(0f, TableTop + FrontLip * 0.5f, -hz + 0.02f), new Vector3(TableSize.x, FrontLip, 0.04f), m.Yellow);
        // Derpy dents/stains on the top
        Box(vis, "Stain", new Vector3(-0.7f, TableTop + 0.001f, 0.1f), new Vector3(0.35f, 0.002f, 0.22f), m.Stain, Quaternion.Euler(0f, 23f, 0f));
        Box(vis, "Stain", new Vector3(0.9f, TableTop + 0.001f, -0.15f), new Vector3(0.22f, 0.002f, 0.18f), m.Stain, Quaternion.Euler(0f, -40f, 0f));
        Text(vis, "ApronText", "RIP 'EM HERE", new Vector3(0f, TableTop - 0.16f, -hz - 0.005f), Quaternion.identity,
             new Vector2(TableSize.x * 0.6f, 0.17f), new Color(0.1f, 0.1f, 0.1f));

        var cols = Node(t, "Colliders", Vector3.zero, Quaternion.identity);
        Col(cols, "Body", new Vector3(0f, TableTop * 0.5f, 0f), new Vector3(TableSize.x, TableTop, TableSize.y), DefaultLayer, m.TablePhysics);
        Col(cols, "SplashGuard", new Vector3(0f, TableTop + BackRail * 0.5f, hz - 0.025f), new Vector3(TableSize.x, BackRail, 0.05f), DefaultLayer);
        Col(cols, "RailL", new Vector3(-hx + 0.025f, TableTop + SideRail * 0.5f, 0f), new Vector3(0.05f, SideRail, TableSize.y), DefaultLayer);
        Col(cols, "RailR", new Vector3(hx - 0.025f, TableTop + SideRail * 0.5f, 0f), new Vector3(0.05f, SideRail, TableSize.y), DefaultLayer);
        Col(cols, "LipFront", new Vector3(0f, TableTop + FrontLip * 0.5f, -hz + 0.025f), new Vector3(TableSize.x, FrontLip, 0.05f), DefaultLayer);

        var trig = Col(t, "TopTrigger", new Vector3(0f, TableTop + 0.32f, 0f), new Vector3(TableSize.x - 0.1f, 0.55f, TableSize.y - 0.1f), IgnoreRaycastLayer);
        trig.isTrigger = true;
        trig.gameObject.AddComponent<CarryReceiverTrigger>();
    }

    // =====================================================================================================
    // Bins
    // =====================================================================================================

    struct BinSpec
    {
        public ItemCategory cat; public string sign; public string prompt; public Material body, rim, board; public Color text;
    }

    static void BuildBins(Transform root, Mats m, float floor)
    {
        var specs = new[]
        {
            new BinSpec { cat = ItemCategory.Hazard, sign = "HAZARD", prompt = "HAZARD barrel", body = m.Yellow, rim = m.Dark, board = m.Yellow, text = new Color(0.08f, 0.08f, 0.08f) },
            new BinSpec { cat = ItemCategory.Metal, sign = "METAL", prompt = "METAL bin", body = m.Metal, rim = m.MetalDark, board = m.Metal, text = Color.white },
            new BinSpec { cat = ItemCategory.Plastic, sign = "PLASTIC", prompt = "PLASTIC bin", body = m.Plastic, rim = m.PlasticDark, board = m.Plastic, text = Color.white },
            new BinSpec { cat = ItemCategory.Glass, sign = "GLASS", prompt = "GLASS bin", body = m.Glass, rim = m.GlassDark, board = m.Glass, text = Color.white },
            new BinSpec { cat = ItemCategory.Paper, sign = "PAPER", prompt = "PAPER bin", body = m.Paper, rim = m.PaperDark, board = m.Paper, text = new Color(0.2f, 0.12f, 0.05f) },
            new BinSpec { cat = ItemCategory.Valuable, sign = "KEEP!", prompt = "KEEP crate", body = m.Wood, rim = m.Gold, board = m.Gold, text = new Color(0.25f, 0.12f, 0.02f) },
        };
        float x0 = TablePos.x - TableSize.x * 0.5f;
        float gap = (TableSize.x - specs.Length * BinWidth) / (specs.Length - 1);
        float[] jitter = { 3f, -2f, 2.5f, -3f, 1.5f, -2.5f };
        for (int i = 0; i < specs.Length; i++)
        {
            float cx = x0 + BinWidth * 0.5f + i * (BinWidth + gap);
            BuildBin(root, m, specs[i], new Vector3(cx, floor, BinRowZ), jitter[i]);
        }
    }

    static void BuildBin(Transform root, Mats m, BinSpec s, Vector3 pos, float yaw)
    {
        string nodeName = s.cat == ItemCategory.Valuable ? "KeepCrate" : s.cat == ItemCategory.Hazard ? "HazardBarrel" : "Bin_" + s.sign;
        var t = Node(root, nodeName, pos, Quaternion.Euler(0f, yaw, 0f));
        var bin = t.gameObject.AddComponent<SortBin>();
        bin.category = s.cat;
        bin.displayName = s.prompt;

        float hw = BinWidth * 0.5f, hd = BinDepth * 0.5f, h = BinHeight;
        var vis = Node(t, "Visual", Vector3.zero, Quaternion.identity);
        bin.visualRoot = vis;

        if (s.cat == ItemCategory.Hazard)
        {
            // Yellow drum with black bands
            float d = Mathf.Min(BinWidth, BinDepth);
            Cyl(vis, "Drum", new Vector3(0f, h * 0.5f, 0f), new Vector3(d, h * 0.5f, d), m.Yellow);
            Cyl(vis, "Band", new Vector3(0f, h * 0.3f, 0f), new Vector3(d + 0.02f, 0.04f, d + 0.02f), m.Dark);
            Cyl(vis, "Band", new Vector3(0f, h * 0.68f, 0f), new Vector3(d + 0.02f, 0.04f, d + 0.02f), m.Dark);
            Cyl(vis, "Rim", new Vector3(0f, h - 0.01f, 0f), new Vector3(d + 0.03f, 0.02f, d + 0.03f), m.Frame);
            Cyl(vis, "Hole", new Vector3(0f, h + 0.012f, 0f), new Vector3(d - 0.07f, 0.004f, d - 0.07f), m.Interior);
            Text(vis, "Warn", "!", new Vector3(0f, h * 0.5f, -d * 0.5f - 0.012f), Quaternion.identity, new Vector2(0.2f, 0.26f), new Color(0.08f, 0.08f, 0.08f));
        }
        else
        {
            Box(vis, "WallFront", new Vector3(0f, h * 0.5f, -hd + 0.02f), new Vector3(BinWidth, h, 0.04f), s.body);
            Box(vis, "WallBack", new Vector3(0f, h * 0.5f, hd - 0.02f), new Vector3(BinWidth, h, 0.04f), s.body);
            Box(vis, "WallL", new Vector3(-hw + 0.02f, h * 0.5f, 0f), new Vector3(0.04f, h, BinDepth), s.body);
            Box(vis, "WallR", new Vector3(hw - 0.02f, h * 0.5f, 0f), new Vector3(0.04f, h, BinDepth), s.body);
            Box(vis, "Floor", new Vector3(0f, 0.06f, 0f), new Vector3(BinWidth - 0.06f, 0.04f, BinDepth - 0.06f), m.Interior);
            Box(vis, "Liner", new Vector3(0f, h * 0.5f, hd - 0.045f), new Vector3(BinWidth - 0.08f, h - 0.1f, 0.01f), m.Interior);
            Box(vis, "RimF", new Vector3(0f, h, -hd + 0.02f), new Vector3(BinWidth + 0.03f, 0.05f, 0.07f), s.rim);
            Box(vis, "RimB", new Vector3(0f, h, hd - 0.02f), new Vector3(BinWidth + 0.03f, 0.05f, 0.07f), s.rim);
            Box(vis, "RimL", new Vector3(-hw + 0.02f, h, 0f), new Vector3(0.07f, 0.05f, BinDepth + 0.03f), s.rim);
            Box(vis, "RimR", new Vector3(hw - 0.02f, h, 0f), new Vector3(0.07f, 0.05f, BinDepth + 0.03f), s.rim);
            if (s.cat == ItemCategory.Valuable)
            {
                // Crate slats + gold corner brackets
                for (int k = 0; k < 3; k++)
                    Box(vis, "Slat", new Vector3(0f, 0.2f + k * 0.35f, -hd - 0.005f), new Vector3(BinWidth + 0.01f, 0.06f, 0.02f), m.WoodDark);
                foreach (float x in new[] { -hw, hw })
                    Box(vis, "Bracket", new Vector3(x, h * 0.5f, -hd), new Vector3(0.05f, h, 0.05f), m.Gold);
                Text(vis, "Dollar", "$", new Vector3(0f, h * 0.55f, -hd - 0.02f), Quaternion.identity, new Vector2(0.3f, 0.35f), m.GoldColor);
            }
            else
            {
                // Big category letter on the front for readability
                Text(vis, "Icon", s.sign.Substring(0, 1), new Vector3(0f, h * 0.55f, -hd - 0.005f), Quaternion.identity, new Vector2(0.3f, 0.35f), Color.white);
            }
        }

        // Fill level (rises as the bin fills)
        var fill = Node(vis, "Fill", new Vector3(0f, 0.08f, 0f), Quaternion.identity);
        fill.localScale = new Vector3(1f, h - 0.25f, 1f);
        var fillMat = s.cat == ItemCategory.Valuable ? m.Gold : s.rim;
        Box(fill, "Stuff", new Vector3(0f, 0.5f, 0f), new Vector3(BinWidth - 0.1f, 1f, BinDepth - 0.1f), fillMat);
        bin.fillVisual = fill;

        // Sign above the back
        Box(vis, "SignPost", new Vector3(0f, h + 0.2f, hd - 0.03f), new Vector3(0.04f, 0.4f, 0.03f), m.Dark);
        Box(vis, "SignBoard", new Vector3(0f, h + 0.42f, hd - 0.03f), new Vector3(BinWidth + 0.02f, 0.24f, 0.035f), s.board);
        Text(vis, "SignText", s.sign, new Vector3(0f, h + 0.42f, hd - 0.052f), Quaternion.identity, new Vector2(BinWidth - 0.04f, 0.2f), s.text);

        // Colliders: open box (thick walls so fast throws don't tunnel)
        var cols = Node(t, "Colliders", Vector3.zero, Quaternion.identity);
        Col(cols, "Floor", new Vector3(0f, 0.05f, 0f), new Vector3(BinWidth, 0.1f, BinDepth), DefaultLayer);
        Col(cols, "WallF", new Vector3(0f, h * 0.5f, -hd + 0.04f), new Vector3(BinWidth, h, 0.08f), DefaultLayer);
        Col(cols, "WallB", new Vector3(0f, h * 0.5f, hd - 0.04f), new Vector3(BinWidth, h, 0.08f), DefaultLayer);
        Col(cols, "WallL", new Vector3(-hw + 0.04f, h * 0.5f, 0f), new Vector3(0.08f, h, BinDepth), DefaultLayer);
        Col(cols, "WallR", new Vector3(hw - 0.04f, h * 0.5f, 0f), new Vector3(0.08f, h, BinDepth), DefaultLayer);
        Col(cols, "Sign", new Vector3(0f, h + 0.32f, hd - 0.03f), new Vector3(BinWidth, 0.44f, 0.05f), DefaultLayer);

        var mouth = Node(t, "Mouth", new Vector3(0f, h, 0f), Quaternion.identity);
        bin.mouth = mouth;
        var trig = Col(t, "MouthTrigger", new Vector3(0f, h - 0.3f, 0f), new Vector3(BinWidth - 0.14f, 0.5f, BinDepth - 0.14f), IgnoreRaycastLayer);
        trig.isTrigger = true;
        trig.gameObject.AddComponent<CarryReceiverTrigger>();
    }

    // =====================================================================================================
    // Compactor
    // =====================================================================================================

    static void BuildCompactor(Transform root, Mats m, float floor, float tipFloor)
    {
        // Local +Z = front (faces south toward the walkway).
        var t = Node(root, "Compactor", new Vector3(CompactorPos.x, floor, CompactorPos.z), Quaternion.Euler(0f, 180f, 0f));
        var comp = t.gameObject.AddComponent<Compactor>();

        const float W = 1.0f, D = 1.1f, WallTop = 1.15f, Plinth = 0.15f;
        float hw = W * 0.5f, hd = D * 0.5f;

        var vis = Node(t, "Visual", Vector3.zero, Quaternion.identity);
        comp.visualRoot = vis;
        Box(vis, "Plinth", new Vector3(0f, Plinth * 0.5f, 0f), new Vector3(W + 0.06f, Plinth, D + 0.06f), m.Dark);
        Box(vis, "WallL", new Vector3(-hw + 0.04f, (Plinth + WallTop) * 0.5f, 0f), new Vector3(0.08f, WallTop - Plinth, D), m.CompactorBody);
        Box(vis, "WallR", new Vector3(hw - 0.04f, (Plinth + WallTop) * 0.5f, 0f), new Vector3(0.08f, WallTop - Plinth, D), m.CompactorBody);
        Box(vis, "WallBack", new Vector3(0f, (Plinth + WallTop) * 0.5f, -hd + 0.04f), new Vector3(W, WallTop - Plinth, 0.08f), m.CompactorBody);
        Box(vis, "WallFrontLow", new Vector3(0f, 0.45f, hd - 0.04f), new Vector3(W, 0.6f, 0.08f), m.CompactorBody);
        Box(vis, "FrontTopRail", new Vector3(0f, WallTop - 0.03f, hd - 0.04f), new Vector3(W, 0.06f, 0.08f), m.CompactorBody);
        for (int i = -2; i <= 2; i++)
            Box(vis, "Bar", new Vector3(i * 0.17f, 0.95f, hd - 0.04f), new Vector3(0.035f, 0.36f, 0.035f), m.Dark);
        Box(vis, "Floor", new Vector3(0f, Plinth + 0.005f, 0f), new Vector3(W - 0.16f, 0.01f, D - 0.16f), m.Interior);
        // Hazard stripe band across the front
        for (int i = 0; i < 8; i++)
            Box(vis, "Stripe", new Vector3(-hw + 0.0625f + i * 0.125f, 0.22f, hd + 0.001f), new Vector3(0.125f, 0.1f, 0.01f), i % 2 == 0 ? m.Yellow : m.Dark);
        // Frame + press
        foreach (float x in new[] { -hw - 0.04f, hw + 0.04f })
            Box(vis, "Post", new Vector3(x, 1.3f, 0f), new Vector3(0.08f, 2.3f, 0.14f), m.Frame);
        Box(vis, "Crossbeam", new Vector3(0f, 2.45f, 0f), new Vector3(W + 0.2f, 0.14f, 0.18f), m.Frame);
        Cyl(vis, "PistonHousing", new Vector3(0f, 2.62f, 0f), new Vector3(0.22f, 0.12f, 0.22f), m.Frame);
        // Sign sits in front of the piston rod (rod runs up the middle at z = 0).
        Box(vis, "SignPostL", new Vector3(-0.45f, 2.7f, 0.14f), new Vector3(0.04f, 0.4f, 0.04f), m.Frame);
        Box(vis, "SignPostR", new Vector3(0.45f, 2.7f, 0.14f), new Vector3(0.04f, 0.4f, 0.04f), m.Frame);
        Box(vis, "SignBoard", new Vector3(0f, 2.98f, 0.14f), new Vector3(1.15f, 0.26f, 0.05f), m.Red);
        Text(vis, "SignText", "COMPACTOR", new Vector3(0f, 2.98f, 0.167f), Quaternion.Euler(0f, 180f, 0f), new Vector2(1.08f, 0.22f), Color.white);
        Text(vis, "SignTextBack", "COMPACTOR", new Vector3(0f, 2.98f, 0.113f), Quaternion.identity, new Vector2(1.08f, 0.22f), Color.white);
        // Readout panel (on the far side of the front: the buttons stack on the side nearest the table)
        Box(vis, "ReadoutPanel", new Vector3(0.24f, 0.42f, hd + 0.005f), new Vector3(0.42f, 0.15f, 0.02f), m.Screen);
        var readout = Text(vis, "Readout", "EMPTY", new Vector3(0.24f, 0.42f, hd + 0.017f), Quaternion.Euler(0f, 180f, 0f), new Vector2(0.38f, 0.11f), new Color(0.4f, 1f, 0.45f));
        comp.readout = readout;

        const float PressUp = 1.95f, PressDown = 0.27f;
        var plate = Node(t, "PressPlate", new Vector3(0f, PressUp, 0f), Quaternion.identity);
        Box(plate, "Plate", Vector3.zero, new Vector3(W - 0.18f, 0.12f, D - 0.18f), m.Red);
        for (int i = 0; i < 6; i++)
            Box(plate, "Stripe", new Vector3(-0.35f + i * 0.14f, 0f, (D - 0.18f) * 0.5f + 0.004f), new Vector3(0.07f, 0.1f, 0.01f), m.Yellow);
        Cyl(plate, "Rod", new Vector3(0f, 1.2f, 0f), new Vector3(0.1f, 1.15f, 0.1f), m.Steel);
        comp.pressPlate = plate;
        comp.pressUpY = PressUp;
        comp.pressDownY = PressDown;

        // Googly eyes on the crossbeam: it's a friend now. (Faces the front = local +Z.)
        foreach (float x in new[] { -0.19f, 0.19f })
            GooglyEyeAt(vis, m, new Vector3(x, 2.45f, 0.1f), Quaternion.Euler(0f, 180f, 0f), 0.2f);

        var cols = Node(t, "Colliders", Vector3.zero, Quaternion.identity);
        Col(cols, "Plinth", new Vector3(0f, Plinth * 0.5f, 0f), new Vector3(W + 0.06f, Plinth, D + 0.06f), DefaultLayer);
        Col(cols, "WallL", new Vector3(-hw + 0.04f, (Plinth + WallTop) * 0.5f, 0f), new Vector3(0.08f, WallTop - Plinth, D), DefaultLayer);
        Col(cols, "WallR", new Vector3(hw - 0.04f, (Plinth + WallTop) * 0.5f, 0f), new Vector3(0.08f, WallTop - Plinth, D), DefaultLayer);
        Col(cols, "WallBack", new Vector3(0f, (Plinth + WallTop) * 0.5f, -hd + 0.04f), new Vector3(W, WallTop - Plinth, 0.08f), DefaultLayer);
        Col(cols, "WallFront", new Vector3(0f, (Plinth + WallTop) * 0.5f, hd - 0.04f), new Vector3(W, WallTop - Plinth, 0.08f), DefaultLayer);
        foreach (float x in new[] { -hw - 0.04f, hw + 0.04f })
            Col(cols, "Post", new Vector3(x, 1.3f, 0f), new Vector3(0.08f, 2.3f, 0.14f), DefaultLayer);
        Col(cols, "Crossbeam", new Vector3(0f, 2.45f, 0f), new Vector3(W + 0.2f, 0.14f, 0.18f), DefaultLayer);

        var chamber = Node(t, "Chamber", new Vector3(0f, (Plinth + WallTop) * 0.5f, 0f), Quaternion.identity);
        comp.chamber = chamber;
        comp.chamberSize = new Vector3(W - 0.16f, WallTop - Plinth, D - 0.16f);
        comp.mouth = Node(t, "Mouth", new Vector3(0f, WallTop + 0.2f, 0f), Quaternion.identity);
        var trig = Col(t, "HopperTrigger", new Vector3(0f, WallTop - 0.15f, 0f), new Vector3(W - 0.2f, 0.35f, D - 0.2f), IgnoreRaycastLayer);
        trig.isTrigger = true;
        trig.gameObject.AddComponent<CarryReceiverTrigger>();

        // Bales pop out of the top and land in the corner behind the machine.
        comp.baleSpawn = Node(t, "BaleSpawn", new Vector3(0f, 1.75f, -0.2f), Quaternion.identity);
        var landing = Node(root, "BaleCorner", new Vector3(BaleCorner.x, floor, BaleCorner.z), Quaternion.identity);
        comp.baleLanding = landing;
        comp.baleLandingSize = new Vector2(0.75f, 1.0f);
        Box(landing, "Pallet", new Vector3(0f, 0.05f, 0f), new Vector3(0.95f, 0.1f, 1.2f), m.Wood);
        Col(landing, "PalletCol", new Vector3(0f, 0.05f, 0f), new Vector3(0.95f, 0.1f, 1.2f), DefaultLayer);

        // Lazy crush sweeps the tipping floor.
        comp.pileCenter = new Vector3((TipMinX + TipMaxX) * 0.5f, tipFloor + 1.1f, (TipMinZ + TipMaxZ) * 0.5f);
        comp.pileSize = new Vector3(TipMaxX - TipMinX + 0.4f, 2.2f, TipMaxZ - TipMinZ + 0.4f);

        // Buttons on the front face, stacked on the side nearest the table and turned toward the first-person stand
        // point (side by side they lined up edge-on from there and SMASH hid CRUSH ALL): SMASH (big red) on top,
        // CRUSH ALL (lazy, yellow/black) below it. Local -X = world +X (the table side); a negative yaw turns them that way.
        Quaternion turn = Quaternion.Euler(0f, -ButtonTurn, 0f);
        BuildButton(t, m, comp, CompactorButton.Mode.Smash, new Vector3(-0.25f, 0.9f, hd), turn, "COMPACT\nCHAMBER", m.Red, 0.2f);
        BuildButton(t, m, comp, CompactorButton.Mode.CrushAll, new Vector3(-0.25f, 0.4f, hd), turn, "BULK COMPACT\nSHOP", m.Orange, 0.14f);
    }

    static void BuildButton(Transform parent, Mats m, Compactor comp, CompactorButton.Mode mode, Vector3 localPos, Quaternion localRot, string label, Material capMat, float capSize)
    {
        var b = Node(parent, mode == CompactorButton.Mode.Smash ? "SmashButton" : "CrushAllButton", localPos, localRot);
        b.gameObject.layer = InteractableLayer;
        var btn = b.gameObject.AddComponent<CompactorButton>();
        btn.compactor = comp;
        btn.mode = mode;
        Box(b, "Housing", new Vector3(0f, 0f, 0.06f), new Vector3(capSize + 0.08f, capSize + 0.08f, 0.12f), mode == CompactorButton.Mode.Smash ? m.Yellow : m.Dark);
        // Cylinder rotated to point out of the face (local +Z)
        var cap = Cyl(b, "Cap", new Vector3(0f, 0f, 0.14f), new Vector3(capSize, 0.035f, capSize), capMat, Quaternion.Euler(90f, 0f, 0f));
        btn.cap = cap;
        btn.pressAxis = Vector3.back;
        btn.pressDepth = 0.04f;
        Text(b, "Label", label, new Vector3(0f, -(capSize * 0.5f + 0.1f), 0.125f), Quaternion.Euler(0f, 180f, 0f), new Vector2(0.3f, 0.07f), Color.white);
        Box(b, "LabelPlate", new Vector3(0f, -(capSize * 0.5f + 0.1f), 0.115f), new Vector3(0.3f, 0.08f, 0.01f), m.Dark);
        var col = b.gameObject.AddComponent<BoxCollider>();
        col.center = new Vector3(0f, -0.04f, 0.1f);
        col.size = new Vector3(capSize + 0.16f, capSize + 0.28f, 0.2f);
    }

    // =====================================================================================================
    // TIP IT lever
    // =====================================================================================================

    static void BuildLever(Transform root, Mats m, float floor)
    {
        // Local +Z faces west (toward the truck's rear and the walkway).
        var t = Node(root, "TipLever", new Vector3(LeverPos.x, floor, LeverPos.z), Quaternion.Euler(0f, 270f, 0f));
        t.gameObject.layer = InteractableLayer;
        var un = t.gameObject.AddComponent<TruckUnloader>();
        // Landing zone: inset from the painted edges, and kept clear of the lever in the north-west corner.
        un.pileCenter = new Vector3(5.75f, floor, -3.2f);
        un.pileSize = new Vector2(1.5f, 2.2f);

        var vis = Node(t, "Visual", Vector3.zero, Quaternion.identity);
        Box(vis, "Base", new Vector3(0f, 0.04f, 0f), new Vector3(0.46f, 0.08f, 0.46f), m.Yellow);
        for (int i = 0; i < 3; i++)
            Box(vis, "BaseStripe", new Vector3(-0.15f + i * 0.15f, 0.081f, 0f), new Vector3(0.06f, 0.002f, 0.46f), m.Dark, Quaternion.Euler(0f, 35f, 0f));
        Box(vis, "Post", new Vector3(0f, 0.86f, 0f), new Vector3(0.14f, 1.6f, 0.14f), m.Frame);
        Box(vis, "PivotBlock", new Vector3(0.1f, 0.85f, 0f), new Vector3(0.08f, 0.14f, 0.14f), m.Dark);
        var pivot = Node(t, "LeverPivot", new Vector3(0.16f, 0.85f, 0f), Quaternion.identity);
        Box(pivot, "Arm", new Vector3(0f, 0.28f, 0f), new Vector3(0.045f, 0.56f, 0.045f), m.Steel);
        Sphere(pivot, "Knob", new Vector3(0f, 0.6f, 0f), 0.13f, m.Red);
        un.lever = pivot;
        un.leverRestAngle = -35f;
        un.leverPulledAngle = 50f;

        Box(vis, "SignBoard", new Vector3(0f, 1.8f, 0f), new Vector3(0.72f, 0.3f, 0.04f), m.Yellow);
        Text(vis, "SignFront", "TIP IT!", new Vector3(0f, 1.8f, 0.022f), Quaternion.Euler(0f, 180f, 0f), new Vector2(0.66f, 0.24f), new Color(0.08f, 0.08f, 0.08f));
        Text(vis, "SignBack", "TIP IT!", new Vector3(0f, 1.8f, -0.022f), Quaternion.identity, new Vector2(0.66f, 0.24f), new Color(0.08f, 0.08f, 0.08f));
        Box(vis, "StatusPanel", new Vector3(0f, 1.25f, 0.075f), new Vector3(0.3f, 0.12f, 0.01f), m.Screen);
        un.statusText = Text(vis, "Status", "NO TRUCK", new Vector3(0f, 1.25f, 0.082f), Quaternion.Euler(0f, 180f, 0f), new Vector2(0.27f, 0.09f), new Color(0.4f, 1f, 0.45f));

        var col = t.gameObject.AddComponent<BoxCollider>();
        col.center = new Vector3(0.05f, 0.95f, 0f);
        col.size = new Vector3(0.55f, 1.9f, 0.5f);

        var zoneGo = Node(t, "Zone", Vector3.zero, Quaternion.identity);
        var proxy = zoneGo.gameObject.AddComponent<TruckUnloaderZoneTarget>();
        proxy.unloader = un;
        var zone = zoneGo.gameObject.AddComponent<InteractionZone>();
        zone.center = new Vector3(0f, 1f, 0.3f);
        zone.size = new Vector3(2.6f, 2.4f, 2.8f);
        zone.mode = ZoneMode.OnFoot;
        zone.score = 1.1f;
        zone.targetOverride = proxy;
    }

    // =====================================================================================================
    // First-person sort station
    // =====================================================================================================

    static void BuildSortStation(Transform root, float floor)
    {
        // Local +Z = facing the bins. The SortStation node itself sits at the stand point (feet).
        var t = Node(root, "SortStation", new Vector3(StandPos.x, floor, StandPos.z), Quaternion.identity);
        var st = t.gameObject.AddComponent<SortStation>();
        st.table = root.GetComponentInChildren<SortingTable>();
        st.compactor = root.GetComponentInChildren<Compactor>();
        st.bins = root.GetComponentsInChildren<SortBin>();
        st.standPoint = Node(t, "StandPoint", Vector3.zero, Quaternion.identity);

        // First-person camera: no body/aim (SortStation drives its pose). Disabled + priority 0 until you step up.
        var camT = Node(t, "SortCam", new Vector3(0f, st.eyeHeight, st.eyeForward), Quaternion.Euler(28f, 0f, 0f));
        var vcam = camT.gameObject.AddComponent<CinemachineVirtualCamera>();
        vcam.Priority = 0;
        var lens = vcam.m_Lens;
        lens.FieldOfView = st.fieldOfView;
        lens.NearClipPlane = st.nearClip;
        vcam.m_Lens = lens;
        vcam.enabled = false;
        st.sortCam = vcam;

        var zoneT = Node(t, "EntryZone", Vector3.zero, Quaternion.identity);
        var entry = zoneT.gameObject.AddComponent<SortStationEntry>();
        entry.station = st;
        var zone = zoneT.gameObject.AddComponent<InteractionZone>();
        Vector3 worldCenter = new Vector3((EntryMinX + EntryMaxX) * 0.5f, floor + 1.1f, (EntryMinZ + EntryMaxZ) * 0.5f);
        zone.center = t.InverseTransformPoint(worldCenter);
        zone.size = new Vector3(EntryMaxX - EntryMinX, 2.4f, EntryMaxZ - EntryMinZ);
        zone.mode = ZoneMode.OnFoot;
        zone.score = 4f; // beats line-of-sight (≤2), proximity (≤1.5) and the carrier's toss candidates (3)
        zone.targetOverride = entry;
        st.entryZone = zone;

        var eastT = Node(t, "EntryZoneEast", Vector3.zero, Quaternion.identity);
        var eastEntry = eastT.gameObject.AddComponent<SortStationEntry>();
        eastEntry.station = st;
        var east = eastT.gameObject.AddComponent<InteractionZone>();
        Vector3 eastCenter = new Vector3((EntryEastMinX + EntryEastMaxX) * 0.5f, floor + 1.1f, (EntryEastMinZ + EntryEastMaxZ) * 0.5f);
        east.center = t.InverseTransformPoint(eastCenter);
        east.size = new Vector3(EntryEastMaxX - EntryEastMinX, 2.4f, EntryEastMaxZ - EntryEastMinZ);
        east.mode = ZoneMode.OnFoot;
        east.score = 4f;
        east.targetOverride = eastEntry;
    }

    // =====================================================================================================
    // Tipping floor paint
    // =====================================================================================================

    static void BuildStripes(Transform root, Mats m, float floor)
    {
        var t = Node(root, "TippingFloorStripes", new Vector3(0f, floor + 0.006f, 0f), Quaternion.identity);
        const float seg = 0.3f, width = 0.12f;
        int k = 0;
        void Edge(Vector3 a, Vector3 b)
        {
            Vector3 d = b - a;
            float len = d.magnitude;
            int n = Mathf.Max(1, Mathf.RoundToInt(len / seg));
            float s = len / n;
            Vector3 dir = d / len;
            Quaternion rot = Quaternion.LookRotation(dir, Vector3.up);
            for (int i = 0; i < n; i++)
            {
                Vector3 c = a + dir * (s * (i + 0.5f));
                Box(t, "Stripe", new Vector3(c.x, 0f, c.z), new Vector3(width, 0.004f, s), (k++ % 2 == 0) ? m.Yellow : m.Dark, rot, shadows: false);
            }
        }
        Vector3 p00 = new Vector3(TipMinX, 0f, TipMinZ), p10 = new Vector3(TipMaxX, 0f, TipMinZ);
        Vector3 p11 = new Vector3(TipMaxX, 0f, TipMaxZ), p01 = new Vector3(TipMinX, 0f, TipMaxZ);
        Edge(p01, p11); Edge(p11, p10); Edge(p10, p00); Edge(p00, p01);
        // Painted label, readable from the walkway (north side) looking south.
        Text(t, "FloorText", "DUMP ZONE", new Vector3((TipMinX + TipMaxX) * 0.5f + 0.25f, 0.001f, TipMaxZ - 0.45f), Quaternion.Euler(90f, 180f, 0f),
             new Vector2(1.6f, 0.34f), new Color(1f, 0.8f, 0.1f, 0.85f));
    }

    // =====================================================================================================
    // Helpers
    // =====================================================================================================

    static Transform Node(Transform parent, string name, Vector3 localPos, Quaternion localRot)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localRotation = localRot;
        go.layer = parent != null ? parent.gameObject.layer == InteractableLayer ? InteractableLayer : DefaultLayer : DefaultLayer;
        return go.transform;
    }

    static Transform Box(Transform parent, string name, Vector3 localPos, Vector3 size, Material mat, Quaternion? rot = null, bool shadows = true)
    {
        return Prim(PrimitiveType.Cube, parent, name, localPos, size, mat, rot ?? Quaternion.identity, shadows);
    }

    /// <summary>Unity cylinder: size.y is HALF the height (primitive is 2 units tall).</summary>
    static Transform Cyl(Transform parent, string name, Vector3 localPos, Vector3 scale, Material mat, Quaternion? rot = null)
    {
        return Prim(PrimitiveType.Cylinder, parent, name, localPos, scale, mat, rot ?? Quaternion.identity, true);
    }

    static Transform Sphere(Transform parent, string name, Vector3 localPos, float diameter, Material mat)
    {
        return Prim(PrimitiveType.Sphere, parent, name, localPos, Vector3.one * diameter, mat, Quaternion.identity, true);
    }

    static Transform Prim(PrimitiveType type, Transform parent, string name, Vector3 localPos, Vector3 scale, Material mat, Quaternion rot, bool shadows)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localRotation = rot;
        go.transform.localScale = scale;
        go.layer = parent.gameObject.layer;
        var r = go.GetComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        if (!shadows) { r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; }
        return go.transform;
    }

    static BoxCollider Col(Transform parent, string name, Vector3 localCenter, Vector3 size, int layer, PhysicsMaterial pm = null)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localCenter;
        go.layer = layer;
        var c = go.AddComponent<BoxCollider>();
        c.size = size;
        if (pm != null) c.sharedMaterial = pm;
        return c;
    }

    static TextMeshPro Text(Transform parent, string name, string text, Vector3 localPos, Quaternion localRot, Vector2 box, Color color)
    {
        // TMP swaps in a RectTransform whose serialized anchoredPosition wins over localPosition on reload,
        // so position a plain holder and keep the text itself at the holder's origin.
        var holder = Node(parent, name, localPos, localRot);
        var go = new GameObject("Text");
        go.transform.SetParent(holder, false);
        go.layer = holder.gameObject.layer;
        var tmp = go.AddComponent<TextMeshPro>();
        tmp.text = text;
        tmp.color = color;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.fontStyle = FontStyles.Bold;
        tmp.enableAutoSizing = true;
        tmp.fontSizeMin = 0.1f;
        tmp.fontSizeMax = 12f;
        tmp.margin = Vector4.zero;
        var rt = tmp.rectTransform;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = box;
        rt.anchoredPosition = Vector2.zero;
        rt.localPosition = Vector3.zero;
        rt.localRotation = Quaternion.identity;
        rt.localScale = Vector3.one;
        var r = go.GetComponent<MeshRenderer>();
        if (r != null) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        tmp.ForceMeshUpdate(true, true);
        return tmp;
    }

    /// <summary>
    /// Primitive googly eye (the pack's .blend meshes need Blender to import): white disc + black pupil,
    /// jiggled by the pack's GooglyEye script whenever the parent moves/squashes. Faces local -Z.
    /// </summary>
    static void GooglyEyeAt(Transform parent, Mats m, Vector3 localPos, Quaternion localRot, float diameter)
    {
        var root = Node(parent, "GooglyEye", localPos, localRot);
        root.localScale = Vector3.one * diameter;
        Cyl(root, "Backplate", Vector3.zero, new Vector3(1f, 0.04f, 1f), m.White, Quaternion.Euler(90f, 0f, 0f));
        var pupil = Sphere(root, "Eye", new Vector3(0f, -0.12f, -0.06f), 0.42f, m.Dark);
        pupil.localScale = new Vector3(0.42f, 0.42f, 0.12f);
        var ge = root.gameObject.AddComponent<GooglyEye>();
        ge.Eye = pupil;
        ge.Speed = 2.5f;
        ge.GravityMultiplier = 1f;
        ge.Bounciness = 0.55f;
    }

    /// <summary>All materials (created once under the material dir, colors refreshed every run).</summary>
    class Mats
    {
        readonly string dir;
        public Material TableTop, Dark, Yellow, Stain, Interior, Metal, MetalDark, Plastic, PlasticDark, Glass, GlassDark,
            Paper, PaperDark, Wood, WoodDark, Gold, CompactorBody, Frame, Red, Orange, Steel, Screen, Outline, Bale, Sack, White;
        public PhysicsMaterial TablePhysics;
        public Color GoldColor = new Color(1f, 0.82f, 0.2f);

        public Mats(string dir)
        {
            this.dir = dir;
            EnsureFolder(dir);
            TableTop = Lit("TableTop", new Color(0.78f, 0.8f, 0.8f), 0.35f);
            Dark = Lit("Dark", new Color(0.09f, 0.09f, 0.1f), 0.3f);
            Yellow = Lit("Yellow", new Color(1f, 0.78f, 0.08f), 0.35f);
            Stain = Lit("Stain", new Color(0.45f, 0.38f, 0.22f), 0.6f);
            Interior = Lit("Interior", new Color(0.07f, 0.07f, 0.08f), 0.1f);
            Metal = Lit("BinMetal", new Color(0.52f, 0.58f, 0.66f), 0.55f, 0.4f);
            MetalDark = Lit("BinMetalDark", new Color(0.3f, 0.34f, 0.4f), 0.5f, 0.4f);
            Plastic = Lit("BinPlastic", new Color(0.12f, 0.45f, 1f), 0.45f);
            PlasticDark = Lit("BinPlasticDark", new Color(0.06f, 0.24f, 0.6f), 0.45f);
            Glass = Lit("BinGlass", new Color(0.15f, 0.72f, 0.35f), 0.45f);
            GlassDark = Lit("BinGlassDark", new Color(0.06f, 0.4f, 0.18f), 0.45f);
            Paper = Lit("BinPaper", new Color(0.86f, 0.66f, 0.38f), 0.2f);
            PaperDark = Lit("BinPaperDark", new Color(0.55f, 0.38f, 0.18f), 0.2f);
            Wood = Lit("Wood", new Color(0.55f, 0.34f, 0.16f), 0.15f);
            WoodDark = Lit("WoodDark", new Color(0.36f, 0.21f, 0.09f), 0.15f);
            Gold = Lit("Gold", GoldColor, 0.65f, 0.35f); // very metallic reads as mud in the dim garage
            CompactorBody = Lit("CompactorBody", new Color(0.2f, 0.5f, 0.45f), 0.4f, 0.2f);
            Frame = Lit("Frame", new Color(0.33f, 0.35f, 0.38f), 0.45f, 0.5f);
            Red = Lit("Red", new Color(0.92f, 0.12f, 0.1f), 0.55f);
            Orange = Lit("Orange", new Color(1f, 0.5f, 0.08f), 0.5f);
            Steel = Lit("Steel", new Color(0.72f, 0.74f, 0.78f), 0.75f, 0.9f);
            Screen = Lit("Screen", new Color(0.03f, 0.05f, 0.04f), 0.9f);
            Bale = Lit("Bale", new Color(0.55f, 0.5f, 0.45f), 0.15f);
            Sack = Lit("SackScrap", new Color(0.08f, 0.08f, 0.1f), 0.6f);
            White = Lit("White", new Color(0.97f, 0.97f, 0.95f), 0.5f);
            Outline = OutlineMat();
            TablePhysics = PhysMat("TableTop", 0.8f, 0.9f, 0.05f);
        }

        Material Lit(string name, Color c, float smooth, float metal = 0f)
        {
            string path = $"{dir}/Sorting_{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            else if (mat.shader != shader && shader != null) mat.shader = shader;
            mat.SetColor("_BaseColor", c);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smooth);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", metal);
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        Material OutlineMat()
        {
            string path = $"{dir}/Sorting_Outline.mat";
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            else if (shader != null && mat.shader != shader) mat.shader = shader;
            mat.SetColor("_BaseColor", Color.white);
            if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 1f); // Front: inverted hull
            EditorUtility.SetDirty(mat);
            return mat;
        }

        PhysicsMaterial PhysMat(string name, float dyn, float stat, float bounce)
        {
            string path = $"{dir}/Sorting_{name}.physicMaterial";
            var pm = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(path);
            if (pm == null)
            {
                pm = new PhysicsMaterial(name);
                AssetDatabase.CreateAsset(pm, path);
            }
            pm.dynamicFriction = dyn;
            pm.staticFriction = stat;
            pm.bounciness = bounce;
            pm.frictionCombine = PhysicsMaterialCombine.Maximum;
            pm.bounceCombine = PhysicsMaterialCombine.Minimum;
            EditorUtility.SetDirty(pm);
            return pm;
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            string leaf = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
