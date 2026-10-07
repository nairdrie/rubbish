using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Builds the garage's display line (Docs/Progression.md §6.8) in ShopScene under one root, "GarageDisplays"
/// (idempotent: deleted and rebuilt every run). Primitives only; every piece is always in the scene and
/// GarageUpgradeVisuals switches it on by upgrade level; DisplayGallery fills the slots with your kept finds at runtime.
///   North wall (the sort station wall):
///     - Wobbly Plank Shelf (display_plank_shelf): 3 finds on a plank over the bale corner, left of the trophy shelf
///     - Trophy Shelf (trophy_shelf L1, 6) and Museum Wing (trophy_shelf L2, 18 plinths + velvet rope + title board),
///       the same spot and size as before (moved here from Setup_GarageUpgrades)
///     - The Legendary Vault (display_vault): 6 gold-lit niches (Epic+) and a giant round door, on the wall between the
///       north pillar and the office, straight across from where you walk in (it hid behind the parked truck on the
///       south wall until 2026-10-01)
///   South wall, west of the SW pillar (by the garage door; nothing deeper than the pillars' faces, so the truck's path
///   stays clear):
///     - Lit Glass Case (display_glass_case): 4 finds on two glass levels, interior light
///     - Pedestal of Honor (display_pedestal): 1 find on marble behind a velvet rope under its own spotlight
/// Each piece gets an InteractionZone in front of it for "Arrange". Materials go to Assets/Materials/Generated/Displays/.
/// Also removes the old GarageUpgrades/TrophyShelf if an older Setup_GarageUpgrades left one behind.
/// </summary>
public static class Setup_Displays
{
    public const string ShopScenePath = "Assets/Scenes/ShopScene.unity";
    public const string MaterialDir = "Assets/Materials/Generated/Displays";
    public const string RootName = "GarageDisplays";
    const float FallbackFloorY = -0.066f;

    // ---- layout (ShopScene; walls measured: north z 4.91, south z -5.00, west x -6.92; see Docs/Progression.md §6.8)
    public const float NorthWallZ = 4.91f, SouthWallZ = -5.0f;
    // north wall
    public static readonly Vector3 PlankAt = new Vector3(-5.45f, 1.95f, 4.76f);     // y above the floor
    public const float ShelfZ = 4.71f, ShelfX0 = -4.45f, ShelfDX = 0.48f, ShelfCX = -3.25f;
    public static readonly float[] ShelfRows = { 2.3f, 2.85f, 3.4f, 3.95f };
    // south wall (between the west wall / garage door corner and the SW pillar x -3.81, then between the pillars)
    public const float CaseX = -5.87f, CaseW = 1.2f, CaseD = 0.46f;
    public const float PedestalX = -4.48f;
    // north wall, centre (between the north pillar x -0.63 and the office, whose shelves start at x 2.76)
    public const float VaultX = 1.03f, VaultW = 3.22f;           // backplate centre / width: x -0.58..2.64
    public const float VaultGridX = 1.56f;                       // niche grid centre: frame x 0.51..2.61
    public const float VaultDoorX = -0.02f, VaultDoorR = 0.5f;   // round door: x -0.52..0.48, beside the pillar

    [MenuItem("Rubbish/Setup/GarageDisplays")]
    public static void Run() => Build(ShopScenePath);

    public static string Build(string scenePath) => Build(scenePath, MaterialDir, false);

    /// <summary>Build into any scene (e.g. a scratch copy of ShopScene for previews). showAll = every piece active.</summary>
    public static string Build(string scenePath, string matDir, bool showAll)
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
            {
                if (go.name == RootName) { Object.DestroyImmediate(go); log.AppendLine("Removed old GarageDisplays root."); }
                else if (go.name == Setup_GarageUpgrades.RootName) RemoveOldTrophyShelf(go, log);
            }
            var root = new GameObject(RootName);
            SceneManager.MoveGameObjectToScene(root, scene);
            Physics.SyncTransforms();
            var ctx = new Ctx(new Mats(matDir), root.transform, FloorY(scene, new Vector3(-3.25f, 0f, 2.55f)));
            ctx.BuildAll();
            if (showAll) foreach (var e in ctx.entries) if (e.target != null) e.target.SetActive(true);
            AssetDatabase.SaveAssets();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            log.AppendLine($"Built {RootName} in {scenePath}: {ctx.pieces.Count} pieces, {ctx.slotCount} slots (floor y={ctx.floor:0.###}).");
        }
        finally
        {
            if (opened) EditorSceneManager.CloseScene(scene, true);
        }
        Debug.Log("[Setup_Displays] " + log);
        return log.ToString();
    }

    static void RemoveOldTrophyShelf(GameObject upgradesRoot, System.Text.StringBuilder log)
    {
        var old = upgradesRoot.transform.Find("TrophyShelf");
        if (old == null) return;
        Object.DestroyImmediate(old.gameObject);
        var vis = upgradesRoot.GetComponent<GarageUpgradeVisuals>();
        if (vis != null && vis.entries != null)
        {
            var keep = new List<GarageUpgradeVisuals.Entry>();
            foreach (var e in vis.entries) if (e != null && e.target != null) keep.Add(e);
            vis.entries = keep.ToArray();
        }
        log.AppendLine("Removed the old GarageUpgrades/TrophyShelf (the trophy shelf is a display piece now).");
    }

    static float FloorY(Scene scene, Vector3 at)
    {
        var ps = scene.GetPhysicsScene();
        if (ps.Raycast(at + Vector3.up * 3f, Vector3.down, out var hit, 10f, 1 << 6, QueryTriggerInteraction.Ignore)) return hit.point.y;
        return FallbackFloorY;
    }

    // =====================================================================================================

    class Ctx
    {
        public readonly Mats m;
        public readonly Transform root;
        public readonly float floor;
        public readonly List<GarageUpgradeVisuals.Entry> entries = new List<GarageUpgradeVisuals.Entry>();
        public readonly List<DisplayPiece> pieces = new List<DisplayPiece>();
        public int slotCount;
        DisplayGallery gallery;

        public Ctx(Mats m, Transform root, float floor) { this.m = m; this.root = root; this.floor = floor; }

        Vector3 F(float x, float y, float z) => new Vector3(x, floor + y, z);

        void Gate(GameObject go, string id, int min)
        {
            entries.Add(new GarageUpgradeVisuals.Entry { id = id, minLevel = min, maxLevel = 99, target = go });
            go.SetActive(false);
        }

        T Piece<T>(string name, string key, Vector3 at) where T : DisplayPiece
        {
            var t = Node(root, name, at, Quaternion.identity);
            var p = t.gameObject.AddComponent<T>();
            p.pieceKey = key;
            p.gallery = gallery;
            pieces.Add(p);
            return p;
        }

        static Transform Slot(Transform parent, string name, Vector3 world, Vector3 facing)
        {
            var s = Node(parent, name, world, Quaternion.LookRotation(facing, Vector3.up));
            return s;
        }

        static void Zone(DisplayPiece p, Vector3 localCenter, Vector3 size)
        {
            var z = p.gameObject.AddComponent<InteractionZone>();
            z.center = localCenter;
            z.size = size;
            z.mode = ZoneMode.OnFoot;
            z.score = 0.6f;
            z.targetOverride = p;
        }

        public void BuildAll()
        {
            gallery = root.gameObject.AddComponent<DisplayGallery>();
            BuildPlank();
            BuildTrophyShelfAndMuseum();
            BuildGlassCase();
            BuildPedestal();
            BuildVault();
            gallery.pieces = pieces.ToArray();
            var vis = root.gameObject.AddComponent<GarageUpgradeVisuals>();
            vis.entries = entries.ToArray();
            foreach (var p in pieces) slotCount += p.SlotCount;
        }

        // ------------------------------------------------------------------ wobbly plank shelf (north wall)

        void BuildPlank()
        {
            Vector3 at = F(PlankAt.x, PlankAt.y, PlankAt.z);
            var p = Piece<DisplayPiece>("PlankShelf", DisplayPlanner.PlankShelf, at);
            p.maxPropSize = 0.24f;
            p.spin = 0f;
            p.gazeRadius = 0.2f;
            p.plateOffset = new Vector3(0f, -0.03f, 0.135f);
            p.plateSize = new Vector2(0.26f, 0.06f);
            // One big nail in the middle: the plank seesaws on it.
            var pivot = Node(p.transform, "Wobble", at, Quaternion.identity);
            p.wobble = pivot;
            Box(pivot, "Plank", at, new Vector3(1.04f, 0.035f, 0.27f), m.PlankWood, Quaternion.Euler(0f, 0f, 1.2f));
            Box(pivot, "Knot", at + new Vector3(0.28f, 0.0185f, 0.04f), new Vector3(0.05f, 0.002f, 0.035f), m.Dark);
            Cyl(p.transform, "NailHead", at + new Vector3(0f, 0f, -0.138f), new Vector3(0.04f, 0.005f, 0.04f), m.Steel, Quaternion.Euler(90f, 0f, 0f));
            // A string from a second nail, for "safety" (it holds nothing).
            Vector3 s0 = at + new Vector3(-0.4f, 0.02f, -0.02f), s1 = at + new Vector3(-0.34f, 0.46f, 0.1f);
            Box(p.transform, "String", (s0 + s1) * 0.5f, new Vector3(0.006f, 0.006f, Vector3.Distance(s0, s1)), m.White, Quaternion.LookRotation(s1 - s0));
            Cyl(p.transform, "SafetyNail", s1 + new Vector3(0f, 0f, -0.01f), new Vector3(0.03f, 0.005f, 0.03f), m.Steel, Quaternion.Euler(90f, 0f, 0f));
            // Cardboard sign, taped on.
            var sign = Node(p.transform, "Sign", at + new Vector3(0.05f, 0.62f, 0.13f), Quaternion.identity);
            Box(sign, "Cardboard", sign.position, new Vector3(0.62f, 0.22f, 0.01f), m.Cardboard, Quaternion.Euler(0f, 0f, -3f));
            Box(sign, "Tape", sign.position + new Vector3(-0.27f, 0.09f, -0.006f), new Vector3(0.1f, 0.03f, 0.002f), m.Tape, Quaternion.Euler(0f, 0f, 30f));
            Box(sign, "Tape", sign.position + new Vector3(0.27f, 0.09f, -0.006f), new Vector3(0.1f, 0.03f, 0.002f), m.Tape, Quaternion.Euler(0f, 0f, -30f));
            Text(sign, "Text", "MY STUFF\n<size=60%>(DO NOT TOUCH)</size>", sign.position + new Vector3(0f, 0f, -0.007f), Quaternion.Euler(0f, 0f, -3f), new Vector2(0.56f, 0.19f), new Color(0.15f, 0.1f, 0.05f));
            var slots = new List<Transform>();
            for (int i = 0; i < 3; i++)
                slots.Add(Slot(pivot, "Slot" + i, at + new Vector3(-0.34f + i * 0.34f, 0.0175f + (i - 1) * 0.007f, -0.01f), Vector3.back));
            p.slots = slots.ToArray();
            Zone(p, new Vector3(0f, -1.0f, -1.1f), new Vector3(1.9f, 2.4f, 2.0f));
            Gate(p.gameObject, ShopIds.DisplayPlankShelf, 1);
        }

        // ------------------------------------------------------------------ trophy shelf + museum wing (north wall)

        void BuildTrophyShelfAndMuseum()
        {
            for (int part = 0; part < 2; part++)
            {
                bool museum = part == 1;
                Vector3 at = F(ShelfCX, ShelfRows[museum ? 1 : 0], ShelfZ);
                DisplayPiece p = museum ? Piece<DisplayPiece>("MuseumWing", DisplayPlanner.MuseumWing, at)
                                        : Piece<TrophyShelf>("TrophyShelf", DisplayPlanner.TrophyShelfKey, at);
                p.maxPropSize = museum ? 0.28f : 0.32f;
                p.plateOffset = new Vector3(0f, museum ? -0.075f : -0.035f, 0.188f);
                p.plateSize = new Vector2(0.4f, 0.075f);
                var slots = new List<Transform>();
                int r0 = museum ? 1 : 0, r1 = museum ? ShelfRows.Length : 1;
                for (int row = r0; row < r1; row++)
                {
                    float y = floor + ShelfRows[row];
                    var rowT = Node(p.transform, "Row" + row, new Vector3(ShelfCX, y, ShelfZ), Quaternion.identity);
                    Box(rowT, "Board", new Vector3(ShelfCX, y - 0.025f, ShelfZ), new Vector3(ShelfDX * 6f + 0.2f, 0.05f, 0.38f), row == 0 ? m.Wood : m.Velvet);
                    Box(rowT, "Trim", new Vector3(ShelfCX, y - 0.04f, ShelfZ - 0.19f), new Vector3(ShelfDX * 6f + 0.22f, 0.04f, 0.02f), m.Gold);
                    foreach (float x in new[] { ShelfCX - ShelfDX * 3f, ShelfCX + ShelfDX * 3f })
                        Box(rowT, "Bracket", new Vector3(x, y - 0.15f, ShelfZ + 0.08f), new Vector3(0.04f, 0.22f, 0.22f), m.Dark);
                    for (int i = 0; i < 6; i++)
                    {
                        Vector3 sp = new Vector3(ShelfX0 + i * ShelfDX, y, ShelfZ - 0.02f);
                        if (museum) Box(rowT, "Plinth", sp + new Vector3(0f, 0.04f, 0f), new Vector3(0.22f, 0.08f, 0.22f), m.White);
                        slots.Add(Slot(rowT, "Slot" + slots.Count, sp + new Vector3(0f, museum ? 0.08f : 0f, 0f), Vector3.back));
                    }
                    if (!museum)
                    {
                        Box(rowT, "TitlePlate", new Vector3(ShelfCX, y - 0.16f, NorthWallZ - 0.012f), new Vector3(1.1f, 0.12f, 0.02f), m.Wood);
                        Text(rowT, "Title", "WALL OF FINDS", new Vector3(ShelfCX, y - 0.16f, NorthWallZ - 0.024f), Quaternion.identity, new Vector2(1.0f, 0.09f), new Color(1f, 0.85f, 0.4f));
                    }
                }
                if (museum)
                {
                    float ty = floor + 4.45f;
                    Box(p.transform, "TitleBoard", new Vector3(ShelfCX, ty, ShelfZ + 0.1f), new Vector3(2.4f, 0.32f, 0.04f), m.Velvet);
                    Box(p.transform, "TitleFrame", new Vector3(ShelfCX, ty, ShelfZ + 0.12f), new Vector3(2.5f, 0.4f, 0.03f), m.Gold);
                    Text(p.transform, "Title", "MUSEUM OF FINE GARBAGE", new Vector3(ShelfCX, ty, ShelfZ + 0.075f), Quaternion.identity, new Vector2(2.3f, 0.26f), new Color(1f, 0.88f, 0.35f));
                    // velvet rope swag under the bottom shelf
                    float ry = floor + 2.05f, rz = ShelfZ - 0.12f;
                    for (int i = 0; i <= 6; i++)
                    {
                        float x0 = ShelfX0 - 0.24f + i * ShelfDX;
                        Sphere(p.transform, "Stanchion", new Vector3(x0, ry, rz), 0.05f, m.Gold);
                        if (i == 6) break;
                        Vector3 a = new Vector3(x0, ry, rz), b = new Vector3(x0 + ShelfDX, ry, rz), mid = (a + b) * 0.5f + Vector3.down * 0.1f;
                        Box(p.transform, "Rope", (a + mid) * 0.5f, new Vector3(0.025f, 0.025f, Vector3.Distance(a, mid)), m.Velvet, Quaternion.LookRotation(mid - a));
                        Box(p.transform, "Rope", (mid + b) * 0.5f, new Vector3(0.025f, 0.025f, Vector3.Distance(mid, b)), m.Velvet, Quaternion.LookRotation(b - mid));
                    }
                }
                p.slots = slots.ToArray();
                // Read from the sort station / the floor in front of the bins.
                Zone(p, new Vector3(0f, (museum ? -2.85f : -2.3f) + 1.2f, -3.0f), new Vector3(3.8f, 2.6f, 3.6f));
                Gate(p.gameObject, ShopIds.TrophyShelf, museum ? 2 : 1);
            }
        }

        // ------------------------------------------------------------------ lit glass case (south wall, west)

        void BuildGlassCase()
        {
            float S = SouthWallZ, front = S + CaseD, cz = S + CaseD * 0.5f;
            var p = Piece<DisplayPiece>("GlassCase", DisplayPlanner.GlassCase, F(CaseX, 0f, S));
            p.maxPropSize = 0.37f;
            p.spin = 22f;
            p.plateOffset = new Vector3(0f, -0.005f, 0.15f);
            p.plateSize = new Vector2(0.3f, 0.06f);
            var t = p.transform;
            // cabinet
            Box(t, "Base", F(CaseX, 0.36f, cz), new Vector3(CaseW, 0.72f, CaseD), m.DarkWood);
            Box(t, "Kick", F(CaseX, 0.05f, front + 0.005f), new Vector3(CaseW + 0.01f, 0.1f, 0.012f), m.Gold);
            Box(t, "BaseTrim", F(CaseX, 0.715f, front + 0.005f), new Vector3(CaseW + 0.01f, 0.03f, 0.012f), m.Gold);
            Box(t, "Floor", F(CaseX, 0.725f, cz), new Vector3(CaseW - 0.04f, 0.01f, CaseD - 0.04f), m.Velvet);
            Box(t, "Back", F(CaseX, 1.34f, S + 0.025f), new Vector3(CaseW - 0.04f, 1.24f, 0.02f), m.Velvet);
            Box(t, "Top", F(CaseX, 1.99f, cz), new Vector3(CaseW + 0.04f, 0.06f, CaseD + 0.04f), m.DarkWood);
            Box(t, "TopTrim", F(CaseX, 1.965f, front + 0.022f), new Vector3(CaseW + 0.05f, 0.025f, 0.012f), m.Gold);
            foreach (float sx in new[] { -1f, 1f })
                foreach (float fz in new[] { S + 0.02f, front - 0.012f })
                    Box(t, "Post", F(CaseX + sx * (CaseW * 0.5f - 0.012f), 1.345f, fz), new Vector3(0.024f, 1.24f, 0.024f), m.Gold);
            // glass
            Box(t, "GlassFront", F(CaseX, 1.345f, front - 0.012f), new Vector3(CaseW - 0.03f, 1.24f, 0.01f), m.Glass, null, false);
            foreach (float sx in new[] { -1f, 1f })
                Box(t, "GlassSide", F(CaseX + sx * (CaseW * 0.5f - 0.012f), 1.345f, cz), new Vector3(0.01f, 1.24f, CaseD - 0.04f), m.Glass, null, false);
            Box(t, "Shelf", F(CaseX, 1.325f, cz), new Vector3(CaseW - 0.05f, 0.012f, CaseD - 0.05f), m.GlassBlue, null, false);
            // little lights + the glow
            for (int i = 0; i < 3; i++)
                Sphere(t, "Bulb", F(CaseX - 0.4f + i * 0.4f, 1.93f, cz + 0.08f), 0.045f, m.Bulb);
            var lt = Node(t, "CaseLight", F(CaseX, 1.86f, cz + 0.12f), Quaternion.identity);
            var l = lt.gameObject.AddComponent<Light>();
            l.type = LightType.Point; l.color = new Color(1f, 0.92f, 0.78f); l.intensity = 1.25f; l.range = 1.6f; l.shadows = LightShadows.None;
            var lt2 = Node(t, "CaseLightLow", F(CaseX, 1.22f, cz + 0.12f), Quaternion.identity);
            var l2 = lt2.gameObject.AddComponent<Light>();
            l2.type = LightType.Point; l2.color = new Color(1f, 0.92f, 0.78f); l2.intensity = 0.8f; l2.range = 1.0f; l2.shadows = LightShadows.None;
            p.lights = new[] { l, l2 };
            // sign on the cap
            Box(t, "SignPlate", F(CaseX, 2.11f, cz + 0.05f), new Vector3(0.7f, 0.17f, 0.02f), m.DarkWood);
            Text(t, "Sign", "SHOWCASE", F(CaseX, 2.11f, cz + 0.062f), Quaternion.Euler(0f, 180f, 0f), new Vector2(0.62f, 0.12f), new Color(1f, 0.85f, 0.35f));
            // slots: top level first (eye height), left to right as you face it
            var slots = new List<Transform>();
            foreach (float y in new[] { 1.332f, 0.73f })
                foreach (float x in new[] { -0.28f, 0.28f })
                    slots.Add(Slot(t, "Slot" + slots.Count, F(CaseX - x, y, cz + 0.02f), Vector3.forward));
            p.slots = slots.ToArray();
            Col(t, "Collider", F(CaseX, 1.0f, cz), new Vector3(CaseW, 2.0f, CaseD));
            Zone(p, new Vector3(0f, 1.0f, 1.6f), new Vector3(2.0f, 2.6f, 2.4f));
            Gate(p.gameObject, ShopIds.DisplayGlassCase, 1);
        }

        // ------------------------------------------------------------------ pedestal of honor (south wall, by the SW pillar)

        void BuildPedestal()
        {
            float S = SouthWallZ, pz = S + 0.3f, X = PedestalX;
            var p = Piece<DisplayPiece>("Pedestal", DisplayPlanner.Pedestal, F(X, 0f, S));
            p.maxPropSize = 0.44f;
            p.spin = 30f;
            p.bob = 0.02f;
            p.gazeRadius = 0.3f;
            p.plateOffset = new Vector3(0f, -0.22f, 0.205f);
            p.plateSize = new Vector2(0.32f, 0.08f);
            var t = p.transform;
            Box(t, "Foot", F(X, 0.04f, pz), new Vector3(0.52f, 0.08f, 0.52f), m.Marble);
            Box(t, "Column", F(X, 0.53f, pz), new Vector3(0.36f, 0.9f, 0.36f), m.Marble);
            Box(t, "Cap", F(X, 1.01f, pz), new Vector3(0.5f, 0.06f, 0.5f), m.Marble);
            Box(t, "Cushion", F(X, 1.05f, pz), new Vector3(0.36f, 0.03f, 0.36f), m.Velvet);
            foreach (float y in new[] { 0.12f, 0.96f })
                Box(t, "GoldBand", F(X, y, pz), new Vector3(0.375f, 0.025f, 0.375f), m.Gold);
            // velvet rope across the front (flush with the pillar's face)
            float rz = S + 0.54f, ry = 0.82f;   // stanchion bases end at the pillars' face line (z -4.38)
            float[] posts = { X - 0.58f, X + 0.55f };
            foreach (float x in posts)
            {
                Cyl(t, "StanchionBase", F(x, 0.02f, rz), new Vector3(0.16f, 0.02f, 0.16f), m.Gold);
                Cyl(t, "Stanchion", F(x, 0.43f, rz), new Vector3(0.045f, 0.41f, 0.045f), m.Gold);
                Sphere(t, "StanchionTop", F(x, 0.86f, rz), 0.08f, m.Gold);
            }
            Vector3 a = F(posts[0], ry, rz), b = F(posts[1], ry, rz), mid = (a + b) * 0.5f + Vector3.down * 0.16f;
            Box(t, "Rope", (a + mid) * 0.5f, new Vector3(0.035f, 0.035f, Vector3.Distance(a, mid)), m.Velvet, Quaternion.LookRotation(mid - a));
            Box(t, "Rope", (mid + b) * 0.5f, new Vector3(0.035f, 0.035f, Vector3.Distance(mid, b)), m.Velvet, Quaternion.LookRotation(b - mid));
            // wall sign + the spotlight on its arm
            Box(t, "SignPlate", F(X, 2.05f, S + 0.012f), new Vector3(0.9f, 0.26f, 0.02f), m.Velvet);
            Box(t, "SignFrame", F(X, 2.05f, S + 0.006f), new Vector3(0.96f, 0.32f, 0.01f), m.Gold);
            Text(t, "Sign", "OUR FINEST FIND\n<size=55%>(please do not lick)</size>", F(X, 2.05f, S + 0.024f), Quaternion.Euler(0f, 180f, 0f), new Vector2(0.84f, 0.22f), new Color(1f, 0.88f, 0.4f));
            Box(t, "LampArm", F(X, 2.95f, S + 0.25f), new Vector3(0.05f, 0.05f, 0.5f), m.Dark);
            Vector3 lampPos = F(X, 2.88f, S + 0.48f), target = F(X, 1.25f, pz);
            Cyl(t, "LampHousing", lampPos, new Vector3(0.2f, 0.12f, 0.2f), m.Dark, Quaternion.FromToRotation(Vector3.up, lampPos - target));
            Sphere(t, "LampLens", lampPos + (target - lampPos).normalized * 0.12f, 0.15f, m.Bulb);
            var lt = Node(t, "Spotlight", lampPos + (target - lampPos).normalized * 0.16f, Quaternion.LookRotation(target - lampPos));
            var l = lt.gameObject.AddComponent<Light>();
            l.type = LightType.Spot; l.color = new Color(1f, 0.93f, 0.78f); l.intensity = 7f; l.range = 4.5f; l.spotAngle = 26f;
            l.innerSpotAngle = 12f; l.shadows = LightShadows.None;
            p.lights = new[] { l };
            p.slots = new[] { Slot(t, "Slot0", F(X, 1.065f, pz), Vector3.forward) };
            Col(t, "Collider", F(X, 0.5f, S + 0.31f), new Vector3(1.25f, 1.0f, 0.6f));
            Zone(p, new Vector3(0f, 1.0f, 1.7f), new Vector3(1.8f, 2.6f, 2.4f));
            Gate(p.gameObject, ShopIds.DisplayPedestal, 1);
        }

        // ------------------------------------------------------------------ the legendary vault (north wall, centre)

        /// <summary>
        /// The north wall between the north pillar (x -0.63) and the office (its shelves start at x 2.76), straight across
        /// from where you walk in: never behind the parked truck (it sat between the south pillars until 2026-10-01 and the
        /// truck hid all of it but the sign). 6 niches in 2 rows of 3 on the right; the round door swung open flat on the
        /// left, beside the pillar (the Bag Belt's slides pass in front of that part, so the finds aren't there). Everything
        /// stays under the overhead belt (y 3.09) and within 0.44 m of the wall.
        /// </summary>
        void BuildVault()
        {
            float N = NorthWallZ, X = VaultX;
            var p = Piece<DisplayPiece>("Vault", DisplayPlanner.Vault, F(X, 0f, N));
            p.maxPropSize = 0.3f;
            p.spin = 18f;
            p.gazeRadius = 0.26f;
            p.plateOffset = new Vector3(0f, -0.03f, 0.165f);
            p.plateSize = new Vector2(0.36f, 0.065f);
            var t = p.transform;
            float gx = VaultGridX;
            float[] cols = { gx - 0.65f, gx, gx + 0.65f };
            float[] rows = { 1.72f, 0.92f };            // niche floors (top row first)
            const float nw = 0.56f, nh = 0.62f;
            // backplate + the heavy frame around the niche grid
            Box(t, "Backplate", F(X, 1.52f, N - 0.06f), new Vector3(VaultW, 3.04f, 0.12f), m.SteelDark);
            float gw = cols[2] - cols[0] + nw + 0.24f;
            Box(t, "FrameTop", F(gx, rows[0] + nh + 0.1f, N - 0.27f), new Vector3(gw, 0.16f, 0.3f), m.Steel);
            Box(t, "FrameBottom", F(gx, rows[1] - 0.1f, N - 0.27f), new Vector3(gw, 0.16f, 0.3f), m.Steel);
            foreach (float sx in new[] { -1f, 1f })
                Box(t, "FrameSide", F(gx + sx * (gw * 0.5f - 0.06f), (rows[0] + nh + rows[1]) * 0.5f, N - 0.27f), new Vector3(0.12f, rows[0] + nh - rows[1] + 0.36f, 0.3f), m.Steel);
            Box(t, "FrameMid", F(gx, rows[1] + nh + 0.09f, N - 0.27f), new Vector3(gw - 0.1f, 0.18f, 0.3f), m.Steel);
            for (int c = 0; c < 2; c++)
                Box(t, "FrameMullion", F((cols[c] + cols[c + 1]) * 0.5f, (rows[0] + nh + rows[1]) * 0.5f, N - 0.27f), new Vector3(cols[1] - cols[0] - nw, rows[0] + nh - rows[1] + 0.2f, 0.3f), m.Steel);
            // rivets on the frame
            for (int i = 0; i < 7; i++)
            {
                float x = gx - gw * 0.5f + 0.1f + i * (gw - 0.2f) / 6f;
                Sphere(t, "Rivet", F(x, rows[0] + nh + 0.1f, N - 0.425f), 0.045f, m.Gold);
                Sphere(t, "Rivet", F(x, rows[1] - 0.1f, N - 0.425f), 0.045f, m.Gold);
            }
            // niches: gold-lit backs, velvet floors, gold lips
            var slots = new List<Transform>();
            foreach (float ry in rows)
                foreach (float cx in cols)
                {
                    Box(t, "NicheBack", F(cx, ry + nh * 0.5f, N - 0.125f), new Vector3(nw, nh, 0.01f), m.GoldGlow);
                    Box(t, "NicheFloor", F(cx, ry - 0.01f, N - 0.27f), new Vector3(nw, 0.02f, 0.3f), m.Velvet);
                    Box(t, "NicheLip", F(cx, ry - 0.015f, N - 0.42f), new Vector3(nw + 0.02f, 0.03f, 0.02f), m.Gold);
                    slots.Add(Slot(t, "Slot" + slots.Count, F(cx, ry, N - 0.27f), Vector3.back));
                }
            // the giant round door, swung open flat against the wall left of the niches (pivot = hinge on its right edge,
            // next to the grid; it wiggles on arrivals)
            float dr = VaultDoorR, dcx = VaultDoorX, dcy = 1.45f;
            var hinge = Node(t, "DoorHinge", F(dcx + dr, dcy, N - 0.22f), Quaternion.identity);
            p.door = hinge;
            Cyl(hinge, "Door", F(dcx, dcy, N - 0.2f), new Vector3(dr * 2f, 0.08f, dr * 2f), m.Steel, Quaternion.Euler(90f, 0f, 0f));
            Cyl(hinge, "DoorRim", F(dcx, dcy, N - 0.205f), new Vector3(dr * 2f + 0.08f, 0.06f, dr * 2f + 0.08f), m.SteelDark, Quaternion.Euler(90f, 0f, 0f));
            for (int i = 0; i < 12; i++)
            {
                float a = i * Mathf.PI * 2f / 12f;
                Cyl(hinge, "Bolt", F(dcx + Mathf.Cos(a) * (dr - 0.08f), dcy + Mathf.Sin(a) * (dr - 0.08f), N - 0.29f), new Vector3(0.06f, 0.02f, 0.06f), m.Gold, Quaternion.Euler(90f, 0f, 0f));
            }
            Cyl(hinge, "Hub", F(dcx, dcy, N - 0.31f), new Vector3(0.18f, 0.04f, 0.18f), m.Gold, Quaternion.Euler(90f, 0f, 0f));
            for (int i = 0; i < 3; i++)
                Box(hinge, "Spoke", F(dcx, dcy, N - 0.35f), new Vector3(0.62f, 0.045f, 0.045f), m.Gold, Quaternion.Euler(0f, 0f, i * 60f));
            for (int i = 0; i < 6; i++)
            {
                float a = i * Mathf.PI / 3f;
                Sphere(hinge, "SpokeKnob", F(dcx + Mathf.Cos(a) * 0.31f, dcy + Mathf.Sin(a) * 0.31f, N - 0.35f), 0.07f, m.Gold);
            }
            foreach (float hy in new[] { dcy + 0.28f, dcy - 0.28f })
                Box(t, "HingeBlock", F(dcx + dr + 0.02f, hy, N - 0.18f), new Vector3(0.12f, 0.16f, 0.18f), m.SteelDark);
            // sign over the niches (on the backplate's face), under the Bag Belt (y 3.09)
            Box(t, "SignFrame", F(gx, 2.82f, N - 0.14f), new Vector3(gw - 0.02f, 0.44f, 0.04f), m.Gold);
            Box(t, "SignPlate", F(gx, 2.82f, N - 0.17f), new Vector3(gw - 0.1f, 0.36f, 0.04f), m.SteelDark);
            Text(t, "Sign", "THE LEGENDARY VAULT\n<size=45%>EPIC + LEGENDARY FINDS ONLY. NO RIFF-RAFF.</size>", F(gx, 2.82f, N - 0.195f), Quaternion.identity, new Vector2(gw - 0.2f, 0.32f), new Color(1f, 0.82f, 0.25f));
            // warm gold glow on the niches
            var lt = Node(t, "VaultGlow", F(gx, 2.1f, N - 1.25f), Quaternion.identity);
            var l = lt.gameObject.AddComponent<Light>();
            l.type = LightType.Point; l.color = new Color(1f, 0.78f, 0.42f); l.intensity = 1.7f; l.range = 3.0f; l.shadows = LightShadows.None;
            p.lights = new[] { l };
            p.slots = slots.ToArray();
            Col(t, "Collider", F(X, 1.52f, N - 0.22f), new Vector3(VaultW, 3.04f, 0.44f));
            // Arrange from the floor in front of the niches (x -0.33..2.57): clear of the sort station's east step-up zone (x <= -0.6).
            Zone(p, new Vector3(gx - 0.44f - X, 1.0f, -1.6f), new Vector3(2.9f, 2.6f, 2.4f));
            Gate(p.gameObject, ShopIds.DisplayVault, 1);
        }
    }

    // =====================================================================================================
    // primitives

    static Transform Node(Transform parent, string name, Vector3 worldPos, Quaternion worldRot)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(worldPos, worldRot);
        return go.transform;
    }

    static Transform Prim(PrimitiveType type, Transform parent, string name, Vector3 worldPos, Vector3 size, Material mat, Quaternion rot, bool shadows)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(worldPos, rot);
        // localScale in the parent's space: parents here are unscaled, so world size = local scale
        go.transform.localScale = size;
        var r = go.GetComponent<Renderer>();
        r.sharedMaterial = mat;
        if (!shadows) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return go.transform;
    }

    static Transform Box(Transform parent, string name, Vector3 worldPos, Vector3 size, Material mat, Quaternion? rot = null, bool shadows = true) =>
        Prim(PrimitiveType.Cube, parent, name, worldPos, size, mat, rot ?? Quaternion.identity, shadows);

    /// <summary>Unity cylinder: height 2 along local Y, so size.y = half-height in metres.</summary>
    static Transform Cyl(Transform parent, string name, Vector3 worldPos, Vector3 size, Material mat, Quaternion? rot = null) =>
        Prim(PrimitiveType.Cylinder, parent, name, worldPos, size, mat, rot ?? Quaternion.identity, true);

    static Transform Sphere(Transform parent, string name, Vector3 worldPos, float diameter, Material mat) =>
        Prim(PrimitiveType.Sphere, parent, name, worldPos, Vector3.one * diameter, mat, Quaternion.identity, true);

    static void Col(Transform parent, string name, Vector3 worldCenter, Vector3 size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(worldCenter, Quaternion.identity);
        go.AddComponent<BoxCollider>().size = size;
    }

    static TextMeshPro Text(Transform parent, string name, string text, Vector3 worldPos, Quaternion worldRot, Vector2 box, Color color)
    {
        // TMP's RectTransform anchoredPosition wins over localPosition on reload: position a plain holder instead.
        var holder = Node(parent, name, worldPos, worldRot);
        var go = new GameObject("Text");
        go.transform.SetParent(holder, false);
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

    // =====================================================================================================
    // materials

    class Mats
    {
        readonly string dir;
        public Material Steel, SteelDark, Dark, Gold, GoldGlow, White, Marble, Wood, DarkWood, PlankWood, Velvet, Glass, GlassBlue,
            Bulb, Cardboard, Tape;

        public Mats(string dir)
        {
            this.dir = dir;
            EnsureFolder(dir);
            Steel = Lit("Steel", new Color(0.66f, 0.68f, 0.72f), 0.7f, 0.75f);
            SteelDark = Lit("SteelDark", new Color(0.22f, 0.23f, 0.26f), 0.55f, 0.6f);
            Dark = Lit("Dark", new Color(0.09f, 0.09f, 0.1f), 0.3f);
            Gold = Lit("Gold", new Color(1f, 0.8f, 0.22f), 0.7f, 0.45f);
            GoldGlow = Lit("GoldGlow", new Color(1f, 0.78f, 0.35f), 0.5f, 0f, new Color(0.9f, 0.6f, 0.2f));
            White = Lit("White", new Color(0.96f, 0.96f, 0.94f), 0.5f);
            Marble = Lit("Marble", new Color(0.93f, 0.92f, 0.9f), 0.8f);
            Wood = Lit("Wood", new Color(0.55f, 0.34f, 0.16f), 0.15f);
            DarkWood = Lit("DarkWood", new Color(0.24f, 0.13f, 0.07f), 0.35f);
            PlankWood = Lit("PlankWood", new Color(0.66f, 0.5f, 0.3f), 0.08f);
            Velvet = Lit("Velvet", new Color(0.4f, 0.03f, 0.07f), 0.15f);
            Glass = Transparent("Glass", new Color(0.8f, 0.92f, 1f, 0.16f));
            GlassBlue = Transparent("GlassShelf", new Color(0.65f, 0.85f, 1f, 0.35f));
            Bulb = Lit("Bulb", new Color(1f, 0.95f, 0.8f), 0.8f, 0f, new Color(1.6f, 1.4f, 1.0f));
            Cardboard = Lit("Cardboard", new Color(0.72f, 0.56f, 0.36f), 0.05f);
            Tape = Lit("Tape", new Color(0.75f, 0.75f, 0.7f), 0.4f);
        }

        Material Load(string name, Shader shader)
        {
            string path = $"{dir}/Display_{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            else if (shader != null && mat.shader != shader) mat.shader = shader;
            return mat;
        }

        Material Lit(string name, Color c, float smooth, float metal = 0f, Color? emission = null)
        {
            var mat = Load(name, Shader.Find("Universal Render Pipeline/Lit"));
            mat.SetColor("_BaseColor", c);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smooth);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", metal);
            if (emission.HasValue)
            {
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", emission.Value);
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        Material Transparent(string name, Color c)
        {
            var mat = Load(name, Shader.Find("Universal Render Pipeline/Lit"));
            mat.SetColor("_BaseColor", c);
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 0f);
            mat.SetFloat("_ZWrite", 0f);
            mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.95f);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            mat.SetOverrideTag("RenderType", "Transparent");
            EditorUtility.SetDirty(mat);
            return mat;
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
