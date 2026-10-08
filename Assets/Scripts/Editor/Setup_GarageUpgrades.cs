using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Builds every garage + Magnus upgrade (Docs/Progression.md §6.2, §6.3, §9.5 C) in ShopScene under one root,
/// "GarageUpgrades" (idempotent: the root is deleted and rebuilt every run). Everything is made from primitives and is
/// always in the scene; GarageUpgradeVisuals shows each piece by upgrade level at runtime (no runtime scene edits):
///   - the rail to the Sort Stop (comes with Magnus's job, magnus_brain L1; Setup_MagnusRail builds the cart and the
///     chess siding first), racing stripes + a rail motor on the cart (magnus_rail_motor L1)
///   - Bag Belt: back-wall bucket elevator, overhead belt above the workshop doorway and around the north wall, a slide
///     down into the two-bay queue rack beside the table (conveyor L1; Heavy-Duty Motor L2; Junk Diverter grate, shaker
///     and junk tube to the compactor L3). The belt never opens a bag.
///   - XL table leaf (sorting_table L1, handed to SortingTable.xlLeaf); the second sorting spot's mat, stool and sign,
///     cupholders + magnet strip (L2)
///   - Compactor Mk II badge (L1), Hazard Sniffer Nose (L2), Treasure Rescue Grate (L3)
///   - recycling contract banner, hazmat license, disco ball, the raccoon crew's delivery chute (Collar Chip)
///     (the trophy shelf + museum wing moved to the display line: Setup_Displays, root "GarageDisplays")
///   - Magnus props riding his cart / wrist / fingers (UpgradeAttachment): kitchen tongs (the job, until a gripper),
///     lobster claw (gripper L1), arcade claw (gripper L2),
///     X-ray googly eyes + scan beam, monocle, the jar-brain dome (grows per level) and a graduation cap at L4
///   - behaviour: GarageRig (wiring), MagnusSorter, MagnusVisionFlags, ConveyorFeeder/Line/Tray (the queue rack),
///     MagnetStrip, DepotDeliveryDropper.
/// Materials go to Assets/Materials/Generated/Garage/. Never touches the SortingStation / Magnus objects themselves
/// (props attach to Magnus at runtime).
/// </summary>
public static class Setup_GarageUpgrades
{
    public const string ShopScenePath = "Assets/Scenes/ShopScene.unity";
    public const string MaterialDir = "Assets/Materials/Generated/Garage";
    public const string RootName = "GarageUpgrades";

    const int DefaultLayer = 0;
    const int IgnoreRaycastLayer = 2;
    const float FallbackFloorY = -0.066f;

    // ---- world layout (ShopScene; see Docs/Progression.md §6.2 "Garage geometry") ----
    static readonly Vector3 ChessStopXZ = Setup_MagnusRail.ChessStop;
    static readonly Vector3 SortStopPos = Setup_MagnusRail.SortStop;             // cart y = 0 like the chess stop
    static readonly Vector3 ElevatorPos = GarageConveyorLayout.Elevator;
    static readonly Vector3 TraySlot0 = new Vector3(-1.1f, 1.0f, 2.55f);         // queue rack bays; y = above the floor
    static readonly Vector3 TraySlot1 = new Vector3(-0.35f, 1.0f, 2.55f);
    static readonly Vector3 DeliveryChuteMouth = GarageConveyorLayout.DeliveryChute + Vector3.up * FallbackFloorY;
    static readonly Vector3 DiscoPos = new Vector3(0.4f, 5.2f, 0.9f);
    const float JunkTubeHeight = 5.0f;   // the junk tube's run along the north wall: above the museum wing's sign

    const string CartPath = "Magnus Cart";
    const string WristPath = "Magnus Cart/Magnus/BasePivot/Shoulder/Elbow/Wrist";
    const string PointerPath = WristPath + "/Pointer";
    const string ThumbPath = WristPath + "/Thumb";

    [MenuItem("Rubbish/Setup/GarageUpgrades")]
    public static void Run() => Build(ShopScenePath);

    /// <summary>Build into any scene (e.g. a scratch copy of ShopScene for previews).</summary>
    public static string Build(string scenePath) => Build(scenePath, MaterialDir, true);

    public static string Build(string scenePath, string matDir, bool registerAssets, bool showAll = false)
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
                if (go.name == RootName) { Object.DestroyImmediate(go); log.AppendLine("Removed old GarageUpgrades root."); }

            var ctx = new Ctx(scene, new Mats(matDir), log);
            if (!ctx.Wire())
            {
                log.AppendLine("ShopScene is missing the SortingStation / Magnus: run Rubbish/Setup/SortingStation first.");
                Debug.LogWarning("[Setup_GarageUpgrades] " + log);
                return log.ToString();
            }
            var root = new GameObject(RootName);
            SceneManager.MoveGameObjectToScene(root, scene);
            ctx.root = root.transform;
            Physics.SyncTransforms();
            ctx.floor = FloorY(scene, new Vector3(-3.25f, 0f, 2.55f));

            ctx.BuildAll();

            if (showAll) foreach (var e in ctx.entries) if (e.target != null) e.target.SetActive(true);
            if (registerAssets)
            {
                RubbishAssets.EditorRegisterMaterial("mat/garage_glint", ctx.m.Glint);
                log.AppendLine("Registered mat/garage_glint.");
            }
            AssetDatabase.SaveAssets();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            log.AppendLine($"Built {RootName} in {scenePath}: {ctx.entries.Count} level-gated pieces (floor y={ctx.floor:0.###}).");
        }
        finally
        {
            if (opened) EditorSceneManager.CloseScene(scene, true);
        }
        Debug.Log("[Setup_GarageUpgrades] " + log);
        return log.ToString();
    }

    static float FloorY(Scene scene, Vector3 at)
    {
        var ps = scene.GetPhysicsScene();
        if (ps.Raycast(at + Vector3.up * 3f, Vector3.down, out var hit, 10f, 1 << 6, QueryTriggerInteraction.Ignore)) return hit.point.y;
        if (Physics.Raycast(at + Vector3.up * 3f, Vector3.down, out hit, 10f, 1 << 6, QueryTriggerInteraction.Ignore)) return hit.point.y;
        return FallbackFloorY;
    }

    // =====================================================================================================
    // Build context
    // =====================================================================================================

    class Ctx
    {
        public readonly Scene scene;
        public readonly Mats m;
        public readonly System.Text.StringBuilder log;
        public Transform root;
        public float floor;
        public readonly List<GarageUpgradeVisuals.Entry> entries = new List<GarageUpgradeVisuals.Entry>();
        Transform leftoversSpot;

        // Existing garage pieces (read-only; never modified here except SortingTable is left to take the leaf at runtime).
        public SortStation station;
        public SortingTable table;
        public Compactor compactor;
        public TippingFloor tippingFloor;
        public SortStation station2;
        public MagnusTrack track;
        public MagnusArm arm;
        public MagnusChessBot chessBot;
        public ChessController chess;
        public SortBin metalBin, hazardBin, keepCrate;
        public Transform wrist, pointer, thumb, cart, clawCenter;

        public Ctx(Scene scene, Mats m, System.Text.StringBuilder log) { this.scene = scene; this.m = m; this.log = log; }

        public bool Wire()
        {
            foreach (var r in scene.GetRootGameObjects())
            {
                foreach (var s in r.GetComponentsInChildren<SortStation>(true))
                {
                    if (s.seat == 0 && !station) station = s;
                    else if (s.seat == 1 && !station2) station2 = s;
                }
                if (!table) table = r.GetComponentInChildren<SortingTable>(true);
                if (!compactor) compactor = r.GetComponentInChildren<Compactor>(true);
                if (!tippingFloor) tippingFloor = r.GetComponentInChildren<TippingFloor>(true);
                if (!track) track = r.GetComponentInChildren<MagnusTrack>(true);
                if (!arm) arm = r.GetComponentInChildren<MagnusArm>(true);
                if (!chessBot) chessBot = r.GetComponentInChildren<MagnusChessBot>(true);
                if (!chess) chess = r.GetComponentInChildren<ChessController>(true);
            }
            if (station != null)
            {
                var sroot = station.transform.parent != null ? station.transform.parent : station.transform;
                foreach (var b in sroot.GetComponentsInChildren<SortBin>(true))
                {
                    if (b.category == ItemCategory.Metal) metalBin = b;
                    else if (b.category == ItemCategory.Hazard) hazardBin = b;
                    else if (b.category == ItemCategory.Valuable) keepCrate = b;
                }
            }
            wrist = UpgradeAttachment.Resolve(scene, WristPath);
            pointer = UpgradeAttachment.Resolve(scene, PointerPath);
            thumb = UpgradeAttachment.Resolve(scene, ThumbPath);
            cart = UpgradeAttachment.Resolve(scene, CartPath);
            clawCenter = wrist != null ? wrist.Find("Claw Center") : null;
            return station && table && compactor && tippingFloor && track && arm && wrist && cart;
        }

        public void Gate(GameObject go, string id, int min, int max = 99)
        {
            entries.Add(new GarageUpgradeVisuals.Entry { id = id, minLevel = min, maxLevel = max, target = go });
            go.SetActive(false);
        }

        /// <summary>Shown while BOTH levels are in range (e.g. the job's tongs: brain >= 1 and no gripper yet).</summary>
        public void Gate(GameObject go, string id, int min, int max, string andId, int andMin, int andMax)
        {
            entries.Add(new GarageUpgradeVisuals.Entry
            {
                id = id, minLevel = min, maxLevel = max, target = go, andId = andId, andMinLevel = andMin, andMaxLevel = andMax,
            });
            go.SetActive(false);
        }

        public void BuildAll()
        {
            var rig = root.gameObject.AddComponent<GarageRig>();
            rig.station = station; rig.table = table; rig.compactor = compactor; rig.tippingFloor = tippingFloor;
            rig.secondStation = station2;
            rig.track = track; rig.arm = arm; rig.chessBot = chessBot; rig.chess = chess;

            BuildRailBranch(rig);
            BuildCartStripes();
            BuildConveyor(rig);
            BuildTable(rig);
            BuildCompactorMods();
            BuildContractsAndLicense();
            BuildDisco();
            BuildDeliveryChute(rig);
            BuildMagnusProps();

            var sorter = root.gameObject.AddComponent<MagnusSorter>();
            sorter.rig = rig;
            sorter.leftoversCorner = leftoversSpot;
            var vision = root.gameObject.AddComponent<MagnusVisionFlags>();
            vision.rig = rig;

            var vis = root.gameObject.AddComponent<GarageUpgradeVisuals>();
            vis.entries = entries.ToArray();
        }

        Vector3 F(Vector3 p) => new Vector3(p.x, floor + p.y, p.z);

        // ------------------------------------------------------------------ Magnus rail branch + Sort Stop

        void BuildRailBranch(GarageRig rig)
        {
            var stop = Node(root, "Sort Stop", SortStopPos, Quaternion.identity);
            rig.sortStop = stop;

            // The job's stretch of track: from where the always-there chess siding ends (Setup_MagnusRail) to the Sort
            // Stop, with a buffer stop at the table end. One straight line; there is no other branch.
            var g = Node(root, "RailBranch", Vector3.zero, Quaternion.identity);
            Vector3 dir = Setup_MagnusRail.Dir;
            Quaternion along = Setup_MagnusRail.Along;
            var rm = new Setup_MagnusRail.Mats(MaterialDir);
            Setup_MagnusRail.BuildTrack(g, rm, ChessStopXZ + dir * Setup_MagnusRail.SidingReach, SortStopPos + dir * Setup_MagnusRail.BufferBeyond, floor, false, true);
            // Painted parking box + sign at the Sort Stop (squared to the track).
            Vector3 c = F(SortStopPos);
            foreach (var (p, s) in new[] { (new Vector3(0.62f, 0.004f, 0f), new Vector3(0.06f, 0.006f, 1.26f)), (new Vector3(-0.62f, 0.004f, 0f), new Vector3(0.06f, 0.006f, 1.26f)),
                                          (new Vector3(0f, 0.004f, 0.6f), new Vector3(1.3f, 0.006f, 0.06f)), (new Vector3(0f, 0.004f, -0.6f), new Vector3(1.3f, 0.006f, 0.06f)) })
                Box(g, "ParkLine", c + along * p, s, m.Yellow, along, false);
            Text(g, "ParkText", "MAGNUS\nPARKING", c + along * new Vector3(-0.78f, 0.008f, 0f), along * Quaternion.Euler(90f, 90f, 0f), new Vector2(0.9f, 0.3f), new Color(1f, 0.85f, 0.2f));
            Gate(g.gameObject, ShopIds.MagnusBrain, 1);   // career step 1 "Magnus Gets a Job" brings the rail
        }

        void BuildCartStripes()
        {
            // Rail motor (magnus_rail_motor L1): twin racing stripes down both skirts, a finned motor housing with a
            // little chrome exhaust under the rear headstock, and a tidy spoiler on the back. No lettering.
            var chassis = cart.Find(Setup_MagnusRail.ChassisName);
            var frame = chassis != null ? chassis : cart;
            Vector3 P(float x, float y, float z) => frame.TransformPoint(new Vector3(x, y, z));
            Quaternion R(float x = 0f, float y = 0f, float z = 0f) => frame.rotation * Quaternion.Euler(x, y, z);
            const float L = Setup_MagnusRail.CartHalfLength;
            var stripes = Node(root, "CartStripes", frame.position, frame.rotation);
            foreach (float sx in new[] { -1f, 1f })
                foreach (float y in new[] { 0.195f, 0.215f })
                    Box(stripes, "Stripe", P(sx * 0.473f, y, 0f), new Vector3(0.006f, 0.012f, 0.74f), m.Red, R(), false);
            Box(stripes, "Motor", P(0f, 0.17f, -L + 0.2f), new Vector3(0.34f, 0.12f, 0.22f), m.Red, R());
            for (int i = 0; i < 5; i++)
                Box(stripes, "Fin", P(-0.12f + i * 0.06f, 0.17f, -L + 0.2f), new Vector3(0.012f, 0.14f, 0.24f), m.Dark, R(), false);
            Cyl(stripes, "Exhaust", P(0.24f, 0.15f, -L - 0.02f), new Vector3(0.035f, 0.07f, 0.035f), m.Steel, R(90f, 0f, 0f));
            foreach (float x in new[] { -0.3f, 0.3f })
                Box(stripes, "SpoilerPost", P(x, Setup_MagnusRail.DeckTop + 0.06f, -L + 0.03f), new Vector3(0.025f, 0.12f, 0.025f), m.Dark, R());
            Box(stripes, "Spoiler", P(0f, Setup_MagnusRail.DeckTop + 0.125f, -L + 0.01f), new Vector3(0.78f, 0.02f, 0.12f), m.Red, R(-10f, 0f, 0f));
            Attach(stripes.gameObject, frame);
            Gate(stripes.gameObject, ShopIds.MagnusRailMotor, 1);
        }

        // ------------------------------------------------------------------ Bag Belt

        void BuildConveyor(GarageRig rig)
        {
            var conv = Node(root, "Conveyor", Vector3.zero, Quaternion.identity);
            var feeder = conv.gameObject.AddComponent<ConveyorFeeder>();
            feeder.rig = rig;

            // The queue rack beside the table: two bays the belt drops bags into. A bag in a bay is an ordinary resting
            // bag again (grab it from the sort station or on foot), so the rack has real colliders to hold it.
            var trayGo = Node(conv, "Tray", F(TraySlot0), Quaternion.identity);
            var tray = trayGo.gameObject.AddComponent<ConveyorTray>();
            tray.rig = rig;
            var slot0 = Node(trayGo, "Slot0", F(TraySlot0), Quaternion.identity);
            var slot1 = Node(trayGo, "Slot1", F(TraySlot1), Quaternion.identity);
            tray.slots = new[] { slot0, slot1 };
            var rack = BuildQueueRack(conv, "QueueRack", F(TraySlot0), F(TraySlot1));
            Gate(rack, ShopIds.ConveyorBelt, 1);

            // Twin-column bucket elevator against the back wall, directly behind the truck.
            var elevator = BuildElevator(conv);
            Gate(elevator, ShopIds.ConveyorBelt, 1);

            // Shared with the runtime migration of existing scene copies: above the workshop doorway, around the
            // north wall, inward ahead of its structural pillar, and down behind the queue rack.
            var lane0 = BuildLane(conv, 0, tray, GarageConveyorLayout.Route());
            feeder.lanes = new[] { lane0 };

            // L2 Heavy-Duty Motor: a chunky drive unit bolted to the elevator head (it lifts the heavy bags now).
            Vector3 head = F(ElevatorPos) + new Vector3(-0.42f, 3.95f, 0f);
            var motor = Node(conv, "HeavyMotor", head, GarageConveyorLayout.WallFacing);
            Box(motor, "Housing", head, new Vector3(0.5f, 0.42f, 0.46f), m.Red, GarageConveyorLayout.WallFacing);
            for (int i = 0; i < 5; i++)
                Box(motor, "Fin", head + new Vector3(0f, 0f, -0.18f + i * 0.09f), new Vector3(0.54f, 0.46f, 0.02f), m.Dark, null, false);
            Cyl(motor, "Flywheel", head + new Vector3(-0.27f, 0f, 0f), new Vector3(0.36f, 0.03f, 0.36f), m.Steel, Quaternion.Euler(0f, 0f, 90f));
            Cyl(motor, "Hub", head + new Vector3(-0.31f, 0f, 0f), new Vector3(0.1f, 0.02f, 0.1f), m.Gold, Quaternion.Euler(0f, 0f, 90f));
            Box(motor, "PlateBoard", head + new Vector3(-0.1f, -0.36f, 0f), new Vector3(0.03f, 0.2f, 0.72f), m.Yellow);
            Text(motor, "Plate", "HEAVY DUTY", head + new Vector3(-0.12f, -0.36f, 0f), Quaternion.Euler(0f, 90f, 0f), new Vector2(0.68f, 0.16f), new Color(0.1f, 0.1f, 0.1f));
            Gate(motor.gameObject, ShopIds.ConveyorBelt, 2);

            // L3 Junk Diverter: a shaker box with a grate on the north-wall run, and a junk tube from under it along the
            // wall to a spout over the compactor's hopper. What falls through the grate comes out of the spout.
            Vector3 gp = F(GarageConveyorLayout.Grate), sp = F(GarageConveyorLayout.JunkSpout);
            var div = Node(conv, "JunkDiverter", gp, Quaternion.identity);
            var grate = Node(div, "Grate", gp, Quaternion.identity);
            Box(grate, "ShakerL", gp + new Vector3(0f, 0.1f, -0.31f), new Vector3(0.9f, 0.34f, 0.05f), m.Yellow);
            Box(grate, "ShakerR", gp + new Vector3(0f, 0.1f, 0.31f), new Vector3(0.9f, 0.34f, 0.05f), m.Yellow);
            for (int i = 0; i < 7; i++)
                Box(grate, "Bar", gp + new Vector3(-0.36f + i * 0.12f, -0.07f, 0f), new Vector3(0.03f, 0.03f, 0.58f), m.Steel, null, false);
            foreach (float x in new[] { -0.3f, 0.3f })
                GooglyEye(grate, gp + new Vector3(x, 0.14f, -0.345f), Quaternion.identity, 0.14f);
            Box(div, "SignBoard", gp + new Vector3(0f, 0.42f, -0.31f), new Vector3(0.92f, 0.2f, 0.03f), m.Dark);
            Text(div, "Sign", "JUNK DIVERTER", gp + new Vector3(0f, 0.42f, -0.33f), Quaternion.identity, new Vector2(0.88f, 0.16f), new Color(1f, 0.85f, 0.2f));
            // A catch funnel under the grate, then a vacuum tube: up the wall, west along it ABOVE the museum shelves,
            // out over the compactor and down to the spout over its hopper.
            Box(div, "Funnel", gp + new Vector3(0f, -0.3f, 0f), new Vector3(0.6f, 0.36f, 0.5f), m.Frame);
            float high = floor + JunkTubeHeight;
            Vector3 t0 = gp + new Vector3(-0.2f, -0.4f, 0.33f);  // out of the funnel's back, against the wall
            Vector3 t1 = new Vector3(t0.x, high, t0.z);          // up the wall
            Vector3 t2 = new Vector3(sp.x, high, t0.z);          // west along it, over the display shelves
            Vector3 t3 = new Vector3(sp.x, high, sp.z);          // out from the wall over the hopper
            Tube(div, "Tube", t0, t1);
            Tube(div, "Tube", t1, t2);
            Tube(div, "Tube", t2, t3);
            Tube(div, "Tube", t3, sp);
            foreach (var p in new[] { t0, t1, t2, t3 }) Sphere(div, "Elbow", p, 0.3f, m.Steel);
            Cyl(div, "SpoutLip", sp, new Vector3(0.34f, 0.03f, 0.34f), m.Yellow);
            Box(div, "VacBox", t1 + new Vector3(0f, 0.1f, 0f), new Vector3(0.42f, 0.42f, 0.36f), m.Red);
            Text(div, "VacSign", "JUNK VAC", t1 + new Vector3(0f, 0.1f, -0.19f), Quaternion.identity, new Vector2(0.38f, 0.12f), Color.white);
            for (float x = t1.x - 1.2f; x > t2.x + 0.4f; x -= 1.6f)
                Box(div, "Strap", new Vector3(x, (high + 8f) * 0.5f, t0.z), new Vector3(0.03f, 8f - high, 0.03f), m.Frame, null, false);
            lane0.divertAt = GarageConveyorLayout.DivertDistance();
            lane0.grate = grate;
            lane0.junkSpout = Node(div, "JunkSpout", sp + Vector3.down * 0.1f, Quaternion.identity);
            Gate(div.gameObject, ShopIds.ConveyorBelt, 3);
        }

        /// <summary>A round steel tube from a to b (visual only).</summary>
        void Tube(Transform parent, string name, Vector3 a, Vector3 b)
        {
            float len = Vector3.Distance(a, b);
            if (len < 0.02f) return;
            Cyl(parent, name, (a + b) * 0.5f, new Vector3(0.24f, len * 0.5f, 0.24f), m.Steel, Quaternion.FromToRotation(Vector3.up, (b - a).normalized));
        }

        /// <summary>
        /// The queue rack: a low steel table with two walled bays (one per slot) and a "GRAB A BAG" plate. It has
        /// colliders: a floor under both bays, a back wall, end walls and a divider, low enough to reach over.
        /// </summary>
        GameObject BuildQueueRack(Transform parent, string name, Vector3 slotA, Vector3 slotB)
        {
            Vector3 c = (slotA + slotB) * 0.5f;
            float span = Mathf.Abs(slotB.x - slotA.x);
            float w = span + 0.84f, d = 0.84f, top = c.y;
            var t = Node(parent, name, c, Quaternion.identity);
            Box(t, "Pan", c + new Vector3(0f, -0.03f, 0f), new Vector3(w, 0.05f, d), m.Steel);
            // walls: back (north, toward the slide) is tall, the front (south) is a low lip you reach over
            Box(t, "Back", c + new Vector3(0f, 0.16f, d * 0.5f - 0.015f), new Vector3(w, 0.34f, 0.03f), m.Yellow);
            Box(t, "FrontLip", c + new Vector3(0f, 0.03f, -d * 0.5f + 0.015f), new Vector3(w, 0.08f, 0.03f), m.Yellow);
            foreach (float x in new[] { -w * 0.5f + 0.015f, w * 0.5f - 0.015f })
                Box(t, "End", c + new Vector3(x, 0.1f, 0f), new Vector3(0.03f, 0.22f, d), m.Yellow);
            Box(t, "Divider", c + new Vector3(0f, 0.06f, 0f), new Vector3(0.03f, 0.14f, d - 0.06f), m.Dark);
            float legH = top - floor - 0.05f;
            foreach (var (x, z) in new[] { (-w * 0.5f + 0.06f, -d * 0.5f + 0.06f), (w * 0.5f - 0.06f, -d * 0.5f + 0.06f), (-w * 0.5f + 0.06f, d * 0.5f - 0.06f), (w * 0.5f - 0.06f, d * 0.5f - 0.06f) })
                Box(t, "Leg", new Vector3(c.x + x, floor + legH * 0.5f, c.z + z), new Vector3(0.05f, legH, 0.05f), m.Dark);
            Box(t, "Brace", new Vector3(c.x, floor + legH * 0.35f, c.z), new Vector3(w - 0.12f, 0.03f, 0.03f), m.Dark, null, false);
            Box(t, "PlateBoard", c + new Vector3(0f, -0.13f, -d * 0.5f - 0.005f), new Vector3(0.7f, 0.12f, 0.02f), m.Dark);
            Text(t, "Label", "BAG QUEUE  ·  GRAB ONE", c + new Vector3(0f, -0.13f, -d * 0.5f - 0.018f), Quaternion.identity, new Vector2(0.66f, 0.08f), new Color(1f, 0.85f, 0.2f));
            var cols = Node(t, "Colliders", c, Quaternion.identity);
            Col(cols, "Pan", c + new Vector3(0f, -0.03f, 0f), new Vector3(w, 0.05f, d), DefaultLayer);
            Col(cols, "Back", c + new Vector3(0f, 0.16f, d * 0.5f - 0.015f), new Vector3(w, 0.34f, 0.03f), DefaultLayer);
            Col(cols, "FrontLip", c + new Vector3(0f, 0.03f, -d * 0.5f + 0.015f), new Vector3(w, 0.08f, 0.03f), DefaultLayer);
            foreach (float x in new[] { -w * 0.5f + 0.015f, w * 0.5f - 0.015f })
                Col(cols, "End", c + new Vector3(x, 0.1f, 0f), new Vector3(0.03f, 0.22f, d), DefaultLayer);
            return t.gameObject;
        }

        GameObject BuildElevator(Transform parent)
        {
            Vector3 c = F(ElevatorPos);
            var t = Node(parent, "BagOVator", c, Quaternion.identity);
            const float W = 1.1f, D = 0.7f, H = 3.7f;
            foreach (var (x, z) in new[] { (-W / 2, -D / 2), (W / 2, -D / 2), (-W / 2, D / 2), (W / 2, D / 2) })
                Box(t, "Post", c + new Vector3(x, H * 0.5f, z), new Vector3(0.07f, H, 0.07f), m.Frame);
            Box(t, "Back", c + new Vector3(0f, H * 0.5f, D / 2), new Vector3(W, H, 0.03f), m.Steel);
            Box(t, "Divider", c + new Vector3(0f, H * 0.5f, 0f), new Vector3(0.03f, H, D), m.Frame);
            Box(t, "Head", c + new Vector3(0f, H + 0.15f, 0f), new Vector3(W + 0.1f, 0.35f, D + 0.1f), m.Yellow);
            Box(t, "Hopper", c + new Vector3(0f, 0.25f, 0f), new Vector3(W, 0.5f, D), m.Yellow);
            for (int i = 0; i < 5; i++) Box(t, "Stripe", c + new Vector3(-0.44f + i * 0.22f, 0.25f, -D / 2 - 0.005f), new Vector3(0.11f, 0.45f, 0.005f), m.Dark, Quaternion.Euler(0f, 0f, 30f));
            // Static buckets on both columns.
            foreach (float x in new[] { -0.2f, 0.2f })
                for (float y = 0.7f; y < H - 0.2f; y += 0.55f)
                    Box(t, "Bucket", c + new Vector3(x, y, -0.15f), new Vector3(0.32f, 0.12f, 0.22f), m.Red, Quaternion.Euler(-15f, 0f, 0f));
            Box(t, "SignBoard", c + new Vector3(0f, H - 0.4f, -D / 2 - 0.03f), new Vector3(1.0f, 0.3f, 0.03f), m.Red);
            Text(t, "Sign", "BAG-O-VATOR 3000", c + new Vector3(0f, H - 0.4f, -D / 2 - 0.05f), Quaternion.identity, new Vector2(0.95f, 0.25f), Color.white);
            // googly eyes on the head (it's a friend)
            foreach (float x in new[] { -0.18f, 0.18f })
                GooglyEye(t, c + new Vector3(x, H + 0.15f, -D / 2 - 0.06f), Quaternion.identity, 0.18f);
            Col(t, "BaseCollider", c + new Vector3(0f, 0.5f, 0f), new Vector3(W, 1.0f, D), DefaultLayer);
            t.rotation = GarageConveyorLayout.WallFacing;
            return t.gameObject;
        }

        ConveyorLine BuildLane(Transform parent, int lane, ConveyorTray tray, Vector3[] path)
        {
            var t = Node(parent, "Lane" + lane, Vector3.zero, Quaternion.identity);
            var line = t.gameObject.AddComponent<ConveyorLine>();
            line.lane = lane;
            line.tray = tray;
            var wps = new Transform[path.Length];
            for (int i = 0; i < path.Length; i++) wps[i] = Node(t, "WP" + i, F(path[i]), Quaternion.identity);
            line.waypoints = wps;

            var vis = Node(t, "Visual", Vector3.zero, Quaternion.identity);
            var rollers = new List<Transform>();
            // Turn piece from the elevator head onto the belt.
            Segment(vis, "Turn", F(path[1]), F(path[2]), 0.5f, m.Rubber, m.Yellow);
            // Every flat stretch after the turn is an overhead belt (rubber belt, yellow side rails, rollers, hanger rods
            // to the ceiling); every sloped one is a slide (a steel trough with yellow walls).
            for (int i = 2; i < path.Length - 1; i++)
            {
                Vector3 a = F(path[i]), b = F(path[i + 1]);
                if (Mathf.Abs(b.y - a.y) < 0.05f) OverheadBelt(vis, a, b, rollers);
                else Segment(vis, "Slide", a, b, 0.5f, m.Steel, m.Yellow);
            }
            line.rollers = rollers.ToArray();
            Gate(vis.gameObject, ShopIds.ConveyorBelt, 1);
            return line;
        }

        /// <summary>A flat overhead belt from b0 to b1: rubber belt, yellow side rails, rollers, hanger rods to the ceiling.</summary>
        void OverheadBelt(Transform vis, Vector3 b0, Vector3 b1, List<Transform> rollers)
        {
            Vector3 dir = (b1 - b0).normalized;
            float len = Vector3.Distance(b0, b1);
            if (len < 0.05f) return;
            Quaternion along = Quaternion.LookRotation(dir, Vector3.up);
            Vector3 mid = (b0 + b1) * 0.5f;
            Box(vis, "Belt", mid + Vector3.down * 0.04f, new Vector3(0.5f, 0.06f, len + 0.3f), m.Rubber, along);
            Vector3 side = Vector3.Cross(Vector3.up, dir);
            foreach (float s in new[] { -0.28f, 0.28f })
                Box(vis, "Rail", mid + side * s + Vector3.up * 0.06f, new Vector3(0.04f, 0.14f, len + 0.3f), m.Yellow, along);
            for (float d = 0f; d <= len; d += 0.6f)
            {
                var r = Node(vis, "Roller", b0 + dir * d + Vector3.down * 0.1f, along);
                Cyl(r, "Drum", r.position, new Vector3(0.08f, 0.26f, 0.08f), m.Steel, along * Quaternion.Euler(0f, 0f, 90f));
                rollers.Add(r);
            }
            for (float d = 0.6f; d < len; d += 1.6f)
                foreach (float s in new[] { -0.3f, 0.3f })
                {
                    Vector3 p = b0 + dir * d + side * s;
                    float top = 8.0f;
                    Box(vis, "Hanger", new Vector3(p.x, (p.y + top) * 0.5f, p.z), new Vector3(0.03f, top - p.y, 0.03f), m.Frame, Quaternion.identity, false);
                }
        }

        /// <summary>A trough from a to b (floor of width w + two side walls). The bag pivot rides on its surface.</summary>
        void Segment(Transform parent, string name, Vector3 a, Vector3 b, float w, Material floorMat, Material wallMat)
        {
            Vector3 d = b - a;
            float len = d.magnitude;
            if (len < 0.05f) return;
            Vector3 flat = new Vector3(d.x, 0f, d.z);
            Vector3 up = Vector3.up;
            Quaternion rot = Quaternion.LookRotation(d.normalized, flat.sqrMagnitude > 1e-4f ? Vector3.up : Vector3.forward);
            Vector3 mid = (a + b) * 0.5f;
            Vector3 normal = rot * Vector3.up;
            Vector3 side = rot * Vector3.right;
            Box(parent, name, mid - normal * 0.03f, new Vector3(w, 0.04f, len), floorMat, rot);
            foreach (float s in new[] { -w * 0.5f, w * 0.5f })
                Box(parent, name + "Wall", mid + side * s + normal * 0.07f, new Vector3(0.03f, 0.18f, len), wallMat, rot);
        }

        // ------------------------------------------------------------------ table: XL leaf, cupholders, magnet strip

        void BuildTable(GarageRig rig)
        {
            var tt = table.transform;
            float top = table.topHeight;
            float hx = table.topSize.x * 0.5f, hz = table.topSize.y * 0.5f;
            float ext = table.xlFrontExtension > 0f ? table.xlFrontExtension : 0.35f;
            float z0 = -hz - ext, z1 = -hz;           // leaf spans local z [z0, z1]
            float zc = (z0 + z1) * 0.5f;
            Vector3 L(float x, float y, float z) => tt.TransformPoint(x, y, z);

            var g = Node(root, "Table", tt.position, tt.rotation);
            var leaf = Node(g, "XLLeaf", tt.position, tt.rotation);
            Box(leaf, "Top", L(0f, top - 0.03f, zc), new Vector3(table.topSize.x, 0.06f, ext), m.TableTopXL, tt.rotation);
            Box(leaf, "Apron", L(0f, top - 0.16f, z0 + 0.02f), new Vector3(table.topSize.x, 0.2f, 0.04f), m.Yellow, tt.rotation);
            Box(leaf, "LipFront", L(0f, top + 0.04f, z0 + 0.02f), new Vector3(table.topSize.x, 0.08f, 0.04f), m.Yellow, tt.rotation);
            foreach (float x in new[] { -hx + 0.02f, hx - 0.02f })
                Box(leaf, "Rail", L(x, top + 0.08f, zc), new Vector3(0.04f, 0.16f, ext), m.Yellow, tt.rotation);
            foreach (float x in new[] { -hx + 0.25f, hx - 0.25f })
            {
                // Derpy fold-out braces from the old legs to the leaf's front edge.
                Vector3 a = L(x, 0.25f, z1 - 0.05f), b = L(x, top - 0.08f, z0 + 0.06f);
                Box(leaf, "Brace", (a + b) * 0.5f, new Vector3(0.04f, Vector3.Distance(a, b), 0.04f), m.Dark, Quaternion.FromToRotation(Vector3.up, (b - a).normalized));
            }
            for (int i = 0; i < 4; i++)
                Cyl(leaf, "Hinge", L(-hx + 0.4f + i * (table.topSize.x - 0.8f) / 3f, top - 0.06f, z1), new Vector3(0.05f, 0.08f, 0.05f), m.Steel, tt.rotation * Quaternion.Euler(0f, 0f, 90f));
            Text(leaf, "Sticker", "XL!  FITS 2 BAGS + A SANDWICH", L(0f, top - 0.16f, z0 - 0.003f), tt.rotation, new Vector2(table.topSize.x * 0.7f, 0.15f), new Color(0.1f, 0.1f, 0.1f));
            var cols = Node(leaf, "Colliders", tt.position, tt.rotation);
            var body = Col(cols, "Body", L(0f, top * 0.5f, zc), new Vector3(table.topSize.x, top, ext), DefaultLayer, m.TablePhysics, tt.rotation);
            Col(cols, "LipFront", L(0f, top + 0.04f, z0 + 0.025f), new Vector3(table.topSize.x, 0.08f, 0.05f), DefaultLayer, null, tt.rotation);
            foreach (float x in new[] { -hx + 0.025f, hx - 0.025f })
                Col(cols, "Rail", L(x, top + 0.08f, zc), new Vector3(0.05f, 0.16f, ext), DefaultLayer, null, tt.rotation);
            var trig = Col(leaf, "TopTriggerXL", L(0f, top + 0.32f, zc), new Vector3(table.topSize.x - 0.1f, 0.55f, ext), IgnoreRaycastLayer, null, tt.rotation);
            trig.isTrigger = true;
            var crt = trig.gameObject.AddComponent<CarryReceiverTrigger>();
            crt.receiverOverride = table;
            leaf.gameObject.SetActive(false);   // SortingTable.ApplyUpgrades shows it with sorting_table L1
            rig.tableLeaf = leaf.gameObject;

            // L2: the second sorting spot reads in the world (numbered floor mats at both spots, a stool tucked beside
            // the second one, a "2 SORTERS" plate on the apron), cupholders on the front corners + the magnet strip.
            var l2 = Node(g, "DoubleWide", tt.position, tt.rotation);
            if (station != null && station2 != null && station.standPoint != null && station2.standPoint != null)
            {
                // Spot 1 is Magnus's parking box on the track; spot 2 gets its own mat (it stands clear of the rails).
                {
                    Vector3 sp = station2.standPoint.position;
                    sp.y = floor;
                    Box(l2, "SpotMat", sp + new Vector3(0f, 0.006f, 0f), new Vector3(0.78f, 0.012f, 0.62f), m.Rubber, tt.rotation, false);
                    Box(l2, "SpotMatEdge", sp + new Vector3(0f, 0.004f, 0f), new Vector3(0.86f, 0.008f, 0.7f), m.Yellow, tt.rotation, false);
                    Text(l2, "SpotNumber", "2", sp + new Vector3(0f, 0.014f, -0.12f), tt.rotation * Quaternion.Euler(90f, 0f, 0f), new Vector2(0.3f, 0.3f), new Color(1f, 0.85f, 0.2f));
                }
                // A stool behind and to the left of spot 2 (out of the standing spot, the track and the compactor's front).
                Vector3 stool = station2.standPoint.position + tt.rotation * new Vector3(-0.75f, 0f, -0.45f);
                stool.y = floor;
                Cyl(l2, "StoolSeat", stool + new Vector3(0f, 0.6f, 0f), new Vector3(0.34f, 0.03f, 0.34f), m.Red);
                Cyl(l2, "StoolCushion", stool + new Vector3(0f, 0.64f, 0f), new Vector3(0.3f, 0.015f, 0.3f), m.Yellow);
                for (int i = 0; i < 3; i++)
                {
                    Vector3 off = Quaternion.Euler(0f, i * 120f, 0f) * new Vector3(0.13f, 0f, 0f);
                    Box(l2, "StoolLeg", stool + off * 1.2f + new Vector3(0f, 0.29f, 0f), new Vector3(0.03f, 0.58f, 0.03f), m.Dark, Quaternion.FromToRotation(Vector3.up, (Vector3.up * 0.58f - off * 0.5f).normalized));
                }
                Cyl(l2, "StoolRing", stool + new Vector3(0f, 0.22f, 0f), new Vector3(0.3f, 0.008f, 0.3f), m.Steel);
                float midX = (tt.InverseTransformPoint(station2.standPoint.position).x);
                Box(l2, "SpotPlate", L(midX, top - 0.3f, z0 - 0.012f), new Vector3(0.46f, 0.12f, 0.012f), m.Dark, tt.rotation, false);
                Text(l2, "SpotPlateText", "SPOT 2  ·  SORT TOGETHER", L(midX, top - 0.3f, z0 - 0.02f), tt.rotation, new Vector2(0.43f, 0.08f), new Color(1f, 0.85f, 0.2f));
            }
            foreach (float x in new[] { -hx + 0.12f, hx - 0.12f })
            {
                Cyl(l2, "Cupholder", L(x, top - 0.05f, z0 - 0.07f), new Vector3(0.11f, 0.05f, 0.11f), m.Dark, tt.rotation);
                Cyl(l2, "Soda", L(x, top + 0.02f, z0 - 0.07f), new Vector3(0.07f, 0.07f, 0.07f), m.Red, tt.rotation);
            }
            Box(l2, "MagnetStrip", L(0f, top + 0.13f, hz - 0.05f), new Vector3(table.topSize.x - 0.2f, 0.06f, 0.03f), m.Red, tt.rotation);
            Box(l2, "MagnetPoles", L(0f, top + 0.13f, hz - 0.067f), new Vector3(table.topSize.x - 0.2f, 0.02f, 0.006f), m.Steel, tt.rotation);
            Text(l2, "MagnetText", "MAGNET STRIP: METAL JUST... LEAVES", L(0f, top + 0.13f, hz - 0.072f), tt.rotation, new Vector2(1.6f, 0.05f), Color.white);
            Gate(l2.gameObject, ShopIds.SortingTable, 2);
            var ms = Node(g, "MagnetStrip", L(0f, top + 0.13f, hz - 0.05f), tt.rotation).gameObject.AddComponent<MagnetStrip>();
            ms.table = table;
            ms.metalBin = metalBin;
            ms.stripVisual = l2;
        }

        // ------------------------------------------------------------------ compactor Mk II

        void BuildCompactorMods()
        {
            var ct = compactor.transform;
            Vector3 L(float x, float y, float z) => ct.TransformPoint(x, y, z);
            Quaternion R(float x = 0f, float y = 0f, float z = 0f) => ct.rotation * Quaternion.Euler(x, y, z);
            const float hd = 0.55f;

            // Mk II plate standing up off the hopper's front rail, plus the fill sensor eye.
            var mk2 = Node(root, "CompactorMkII", ct.position, ct.rotation);
            Box(mk2, "Badge", L(0f, 1.3f, hd - 0.04f), new Vector3(0.46f, 0.15f, 0.03f), m.Gold, R());
            Text(mk2, "BadgeText", "MK II  AUTO-BALE", L(0f, 1.3f, hd - 0.02f), R(0f, 180f, 0f), new Vector2(0.44f, 0.12f), new Color(0.2f, 0.1f, 0.02f));
            Cyl(mk2, "Sensor", L(0.42f, 1.2f, hd + 0.02f), new Vector3(0.08f, 0.02f, 0.08f), m.Lamp, R(90f, 0f, 0f));
            Gate(mk2.gameObject, ShopIds.CompactorMk2, 1);

            var nose = Node(root, "SnifferNose", L(0f, 2.28f, 0.16f), R());
            var n = Sphere(nose, "Nose", nose.position, 0.2f, m.Nose);
            n.localScale = new Vector3(0.2f, 0.15f, 0.17f);
            foreach (float x in new[] { -0.04f, 0.04f })
            {
                var hole = Sphere(nose, "Nostril", L(x, 2.24f, 0.24f), 0.04f, m.Dark);
                hole.localScale = new Vector3(0.04f, 0.03f, 0.02f);
            }
            Text(nose, "Label", "HAZARD SNIFFER", L(0f, 2.1f, 0.16f), R(0f, 180f, 0f), new Vector2(0.4f, 0.06f), Color.white);
            Gate(nose.gameObject, ShopIds.CompactorMk2, 2);

            // The grate lines the chamber floor (you see it looking into the hopper) + a gold label on the stripe band.
            var grate = Node(root, "RescueGrate", L(0f, 0.17f, 0f), R());
            Box(grate, "Frame", L(0f, 0.165f, 0f), new Vector3(0.84f, 0.02f, 0.94f), m.Gold, R(), false);
            for (int i = 0; i < 7; i++) Box(grate, "Bar", L(-0.36f + i * 0.12f, 0.18f, 0f), new Vector3(0.03f, 0.02f, 0.92f), m.Dark, R(), false);
            Box(grate, "LabelPlate", L(0f, 0.22f, hd + 0.008f), new Vector3(0.62f, 0.08f, 0.01f), m.Dark, R());
            Text(grate, "Label", "TREASURE RESCUE GRATE INSIDE", L(0f, 0.22f, hd + 0.015f), R(0f, 180f, 0f), new Vector2(0.58f, 0.06f), new Color(1f, 0.85f, 0.2f));
            Gate(grate.gameObject, ShopIds.CompactorMk2, 3);
        }

        // ------------------------------------------------------------------ contracts, hazmat license

        void BuildContractsAndLicense()
        {
            float y = floor + 1.98f;
            var banner = Node(root, "RecyclingContractBanner", new Vector3(-3.25f, y, 3.72f), Quaternion.identity);
            Box(banner, "Board", banner.position, new Vector3(2.3f, 0.24f, 0.03f), m.Green);
            foreach (float x in new[] { -1.05f, 1.05f }) Box(banner, "Post", banner.position + new Vector3(x, -0.2f, 0.02f), new Vector3(0.03f, 0.25f, 0.03f), m.Dark);
            var tmp = Text(banner, "Text", "NOW BUYING YOUR RECYCLING", banner.position + new Vector3(0f, 0f, -0.02f), Quaternion.identity, new Vector2(2.2f, 0.2f), Color.white);
            var sign = banner.gameObject.AddComponent<UpgradeSign>();
            sign.id = ShopIds.RecyclingContracts;
            sign.text = tmp;
            sign.texts = new[]
            {
                "", "NOW BUYING: Glassy McGlassface Co.  (x1.5)", "NOW BUYING: Tin Can Tony's Metals  (x2)",
                "NOW BUYING: Big Plastic (Not That One)  (x3)", "NOW BUYING: The Cardboard Cartel  (x4)",
            };
            Gate(banner.gameObject, ShopIds.RecyclingContracts, 1);

            if (hazardBin != null)
            {
                Vector3 front = hazardBin.transform.position + new Vector3(0f, 0f, -0.26f);
                var lic = Node(root, "HazmatLicense", front, Quaternion.identity);
                Box(lic, "Card", front + new Vector3(0.1f, 0.62f, -0.005f), new Vector3(0.2f, 0.13f, 0.006f), m.White);
                Text(lic, "Text", "HAZMAT\nLICENSE\n(laminated)", front + new Vector3(0.1f, 0.62f, -0.01f), Quaternion.identity, new Vector2(0.18f, 0.11f), new Color(0.1f, 0.1f, 0.1f));
                Gate(lic.gameObject, ShopIds.HazmatContract, 1);
                var ew = Node(root, "EWasteSticker", front, Quaternion.identity);
                Box(ew, "Sticker", front + new Vector3(-0.1f, 0.42f, -0.005f), new Vector3(0.18f, 0.12f, 0.006f), m.Green);
                Text(ew, "Text", "E-WASTE\nBUYBACK\n$4 EACH", front + new Vector3(-0.1f, 0.42f, -0.01f), Quaternion.identity, new Vector2(0.16f, 0.1f), Color.white);
                Gate(ew.gameObject, ShopIds.HazmatContract, 2);
            }
        }

        // (The trophy shelf + museum wing are display pieces now: Setup_Displays builds them on the GarageDisplays root.)

        // ------------------------------------------------------------------ disco ball

        void BuildDisco()
        {
            var g = Node(root, "DiscoBall", DiscoPos, Quaternion.identity);
            var disco = g.gameObject.AddComponent<DiscoBall>();
            Box(g, "Chain", new Vector3(DiscoPos.x, (DiscoPos.y + 8.0f) * 0.5f + 0.15f, DiscoPos.z), new Vector3(0.02f, 8.0f - DiscoPos.y - 0.3f, 0.02f), m.Steel, Quaternion.identity, false);
            var ball = Node(g, "Ball", DiscoPos, Quaternion.identity);
            Sphere(ball, "Mirror", DiscoPos, 0.55f, m.Mirror);
            for (int i = 0; i < 24; i++)
            {
                Quaternion q = Quaternion.Euler(-60f + (i / 6) * 40f, (i % 6) * 60f + (i / 6) * 30f, 0f);
                Box(ball, "Facet", DiscoPos + q * Vector3.forward * 0.27f, new Vector3(0.09f, 0.09f, 0.01f), m.MirrorBright, q);
            }
            disco.ball = ball;
            var lights = new List<Light>();
            Color[] cols = { new Color(1f, 0.2f, 0.6f), new Color(0.2f, 0.8f, 1f), new Color(1f, 0.9f, 0.2f) };
            for (int i = 0; i < cols.Length; i++)
            {
                var lt = Node(g, "Light" + i, DiscoPos, Quaternion.Euler(60f, i * 120f, 0f));
                var l = lt.gameObject.AddComponent<Light>();
                l.type = LightType.Spot;
                l.color = cols[i];
                l.intensity = 6f;
                l.range = 12f;
                l.spotAngle = 28f;
                l.shadows = LightShadows.None;
                lights.Add(l);
            }
            disco.lights = lights.ToArray();
            Gate(g.gameObject, ShopIds.GarageDiscoBall, 1);
        }

        // ------------------------------------------------------------------ the raccoon crew's delivery chute

        void BuildDeliveryChute(GarageRig rig)
        {
            var g = Node(root, GarageConveyorLayout.DeliveryChuteName, DeliveryChuteMouth, Quaternion.identity);
            var dropper = g.gameObject.AddComponent<DepotDeliveryDropper>();
            dropper.rig = rig;
            dropper.chute = Node(g, "Mouth", DeliveryChuteMouth + new Vector3(-0.2f, -0.1f, 0f), Quaternion.identity);
            var vis = Node(g, "Visual", DeliveryChuteMouth, Quaternion.identity);
            Vector3 wall = DeliveryChuteMouth + new Vector3(.39f, .45f, 0f);
            Segment(vis, "Duct", wall, DeliveryChuteMouth, 0.7f, m.Steel, m.Frame);
            Box(vis, "Flap", DeliveryChuteMouth + new Vector3(-0.05f, -0.05f, 0f), new Vector3(0.04f, 0.4f, 0.66f), m.Yellow, Quaternion.Euler(0f, 0f, 20f));
            Box(vis, "SignBoard", DeliveryChuteMouth + new Vector3(.37f, 1.05f, 0f), new Vector3(0.03f, 0.32f, 1.3f), m.Green);
            Text(vis, "Sign", DepotDeliveryDropper.SignText, DeliveryChuteMouth + new Vector3(.35f, 1.05f, 0f), Quaternion.Euler(0f, 90f, 0f), new Vector2(1.25f, 0.28f), Color.white);
            g.rotation = GarageConveyorLayout.WallFacing;
            Gate(vis.gameObject, CallingIds.CollarChip, 1);
        }

        // ------------------------------------------------------------------ Magnus props (attach at runtime)

        void BuildMagnusProps()
        {
            Vector3 up = Vector3.up;
            Vector3 w = wrist.position;
            // The googly eyes are on the wrist block; "outward" = from the wrist centre toward them.
            var ges = wrist.GetComponentsInChildren<global::GooglyEye>(true);
            Vector3 eyeMid = w + Vector3.left * 0.1f, outward = Vector3.left;
            if (ges.Length > 0)
            {
                Vector3 sum = Vector3.zero;
                foreach (var ge in ges) sum += ge.transform.position;
                eyeMid = sum / ges.Length;
                Vector3 o = eyeMid - w; o.y = 0f;
                if (o.sqrMagnitude > 1e-4f) outward = o.normalized;
            }
            Vector3 claw = clawCenter != null ? clawCenter.position : w + Vector3.down * 0.75f;
            Vector3 down = (claw - w).normalized;

            // The job (brain L1): taped kitchen tongs on both fingers, until a real gripper replaces them.
            int k = 0;
            foreach (var f in new[] { pointer, thumb })
            {
                if (f == null) continue;
                var (pivot, tip) = FingerLine(f);
                var tongs = Node(root, "Tongs" + k, pivot, Quaternion.identity);
                Vector3 d = tip - pivot;
                Box(tongs, "Tong", (pivot + tip) * 0.5f + d.normalized * 0.06f, new Vector3(0.03f, 0.02f, d.magnitude + 0.12f), m.Steel, Quaternion.LookRotation(d.normalized));
                Box(tongs, "Tape", pivot + d * 0.35f, new Vector3(0.07f, 0.07f, 0.05f), m.Tape, Quaternion.LookRotation(d.normalized));
                Box(tongs, "Scallop", tip + d.normalized * 0.1f, new Vector3(0.06f, 0.03f, 0.06f), m.Steel, Quaternion.LookRotation(d.normalized));
                Attach(tongs.gameObject, f);
                Gate(tongs.gameObject, ShopIds.MagnusBrain, 1, 99, ShopIds.MagnusGripper, 0, 0);

                // Gripper L1: lobster pincer shells.
                var lob = Node(root, "Lobster" + k, pivot, Quaternion.identity);
                var shell = Sphere(lob, "Shell", (pivot + tip) * 0.5f + d.normalized * 0.05f, 0.1f, m.Lobster);
                shell.rotation = Quaternion.LookRotation(d.normalized);
                shell.localScale = new Vector3(0.11f, 0.08f, d.magnitude + 0.16f);
                for (int s = 0; s < 3; s++)
                {
                    var bump = Sphere(lob, "Bump", pivot + d * (0.3f + s * 0.22f) + outward * 0.03f, 0.04f, m.Lobster);
                    bump.localScale = Vector3.one * 0.045f;
                }
                Attach(lob.gameObject, f);
                Gate(lob.gameObject, ShopIds.MagnusGripper, 1, 1);
                k++;
            }

            // Gripper L2: arcade quad-claw (three gold prongs round the claw centre, riding the wrist).
            var arcade = Node(root, "ArcadeClaw", claw, Quaternion.identity);
            Cyl(arcade, "Hub", claw - down * 0.12f, new Vector3(0.14f, 0.05f, 0.14f), m.Gold, Quaternion.FromToRotation(Vector3.up, -down));
            for (int i = 0; i < 3; i++)
            {
                Vector3 side = Quaternion.AngleAxis(i * 120f, down) * Vector3.Cross(down, outward).normalized;
                Vector3 a = claw - down * 0.1f + side * 0.06f, b = claw + down * 0.12f + side * 0.16f;
                Box(arcade, "Prong", (a + b) * 0.5f, new Vector3(0.025f, Vector3.Distance(a, b), 0.025f), m.Gold, Quaternion.FromToRotation(Vector3.up, (b - a).normalized));
                Sphere(arcade, "Tip", b, 0.04f, m.Gold);
            }
            Attach(arcade.gameObject, wrist);
            Gate(arcade.gameObject, ShopIds.MagnusGripper, 2);

            // Vision L1: X-ray googly eyes + a red scan beam pointing down from the claw.
            var eyes = Node(root, "XRayEyes", eyeMid, Quaternion.identity);
            Vector3 lat = Vector3.Cross(Vector3.up, outward).normalized;
            foreach (float s in new[] { -0.09f, 0.09f })
                GooglyEye(eyes, eyeMid + outward * 0.05f + lat * s + up * 0.02f, Quaternion.LookRotation(-outward), 0.15f);
            var beam = Cyl(eyes, "ScanBeam", claw + down * 0.55f, new Vector3(0.03f, 0.55f, 0.03f), m.Laser, Quaternion.FromToRotation(Vector3.up, down));
            beam.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Attach(eyes.gameObject, wrist);
            Gate(eyes.gameObject, ShopIds.MagnusVision, 1);

            // Vision L2: appraisal monocle over the left eye, with a little gold chain.
            var mono = Node(root, "Monocle", eyeMid, Quaternion.identity);
            Vector3 mc = eyeMid + outward * 0.08f + lat * -0.09f + up * 0.02f;
            Cyl(mono, "Rim", mc, new Vector3(0.12f, 0.01f, 0.12f), m.Gold, Quaternion.FromToRotation(Vector3.up, outward));
            Cyl(mono, "Lens", mc + outward * 0.004f, new Vector3(0.1f, 0.006f, 0.1f), m.GlassBlue, Quaternion.FromToRotation(Vector3.up, outward));
            Box(mono, "Chain", mc + down * 0.12f + lat * -0.02f, new Vector3(0.01f, 0.22f, 0.01f), m.Gold, Quaternion.FromToRotation(Vector3.up, down));
            Attach(mono.gameObject, wrist);
            Gate(mono.gameObject, ShopIds.MagnusVision, 2);

            // Brain: a glass jar dome with a pink brain, bigger every level; graduation cap at L4.
            float[] dome = { 0.2f, 0.26f, 0.32f };
            for (int lvl = 1; lvl <= 3; lvl++)
            {
                float r = dome[lvl - 1];
                Vector3 c = w + up * (0.12f + r * 0.5f);
                var jar = Node(root, "BrainJar" + lvl, c, Quaternion.identity);
                Cyl(jar, "Collar", w + up * 0.11f, new Vector3(r * 0.9f, 0.03f, r * 0.9f), m.Steel);
                Sphere(jar, "Glass", c, r, m.Glass);
                foreach (var o in new[] { new Vector3(-0.25f, 0f, 0f), new Vector3(0.25f, 0.05f, 0f), new Vector3(0f, 0.15f, 0.1f) })
                {
                    var lobe = Sphere(jar, "Brain", c + o * r, r * 0.42f, m.Brain);
                    lobe.localScale = new Vector3(r * 0.45f, r * 0.38f, r * 0.5f);
                }
                Attach(jar.gameObject, wrist);
                Gate(jar.gameObject, ShopIds.MagnusBrain, lvl, lvl == 3 ? 4 : lvl);
            }
            var cap = Node(root, "GradCap", w + up * 0.5f, Quaternion.identity);
            float capY = 0.12f + dome[2] + 0.02f;
            Box(cap, "Board", w + up * capY, new Vector3(0.32f, 0.025f, 0.32f), m.Dark, Quaternion.Euler(0f, 45f, 0f));
            Cyl(cap, "Crown", w + up * (capY - 0.04f), new Vector3(0.16f, 0.04f, 0.16f), m.Dark);
            Box(cap, "Tassel", w + up * (capY - 0.06f) + new Vector3(0.15f, 0f, 0f), new Vector3(0.015f, 0.12f, 0.015f), m.Yellow);
            Attach(cap.gameObject, wrist);
            Gate(cap.gameObject, ShopIds.MagnusBrain, 4);

            // LEFTOVERS tape on the table's front-right corner (where he dumps what he can't handle).
            var tt = table.transform;
            var tape = Node(root, "LeftoversTape", tt.position, tt.rotation);
            Vector3 corner = tt.TransformPoint(table.topSize.x * 0.5f - 0.25f, table.topHeight + 0.002f, -table.topSize.y * 0.5f + 0.2f);
            Box(tape, "Tape", corner, new Vector3(0.5f, 0.003f, 0.36f), m.TapeYellow, tt.rotation, false);
            Text(tape, "Text", "LEFTOVERS", corner + Vector3.up * 0.004f, tt.rotation * Quaternion.Euler(90f, 0f, 0f), new Vector2(0.44f, 0.12f), new Color(0.1f, 0.1f, 0.1f));
            Gate(tape.gameObject, ShopIds.MagnusBrain, 1);
            leftoversSpot = Node(root, "LeftoversSpot", corner + Vector3.up * 0.02f, tt.rotation);
        }

        static (Vector3 pivot, Vector3 tip) FingerLine(Transform finger)
        {
            Vector3 pivot = finger.position, tip = pivot + Vector3.down * 0.4f;
            float best = -1f;
            foreach (var r in finger.GetComponentsInChildren<Renderer>(true))
            {
                var b = r.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 p = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    float d = (p - pivot).sqrMagnitude;
                    if (d > best) { best = d; tip = p; }
                }
            }
            return (pivot, tip);
        }

        /// <summary>Make <paramref name="go"/> ride on <paramref name="target"/> at runtime, keeping today's relative pose.</summary>
        void Attach(GameObject go, Transform target)
        {
            var a = go.AddComponent<UpgradeAttachment>();
            a.targetPath = PathOf(target);
            var t = go.transform;
            a.localPosition = target.InverseTransformPoint(t.position);
            a.localRotation = Quaternion.Inverse(target.rotation) * t.rotation;
            Vector3 ts = target.lossyScale, s = t.lossyScale;
            a.localScale = new Vector3(s.x / Mathf.Max(1e-5f, ts.x), s.y / Mathf.Max(1e-5f, ts.y), s.z / Mathf.Max(1e-5f, ts.z));
        }

        static string PathOf(Transform t)
        {
            string p = t.name;
            while (t.parent != null) { t = t.parent; p = t.name + "/" + p; }
            return p;
        }

        // ------------------------------------------------------------------ primitives (world-space placement)

        void GooglyEye(Transform parent, Vector3 pos, Quaternion rot, float diameter)
        {
            var r = Node(parent, "GooglyEye", pos, rot);
            r.localScale = Vector3.one * diameter / Mathf.Max(1e-4f, parent.lossyScale.x);
            var back = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            back.name = "Backplate";
            Object.DestroyImmediate(back.GetComponent<Collider>());
            back.transform.SetParent(r, false);
            back.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            back.transform.localScale = new Vector3(1f, 0.04f, 1f);
            back.GetComponent<Renderer>().sharedMaterial = m.White;
            var pupil = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            pupil.name = "Eye";
            Object.DestroyImmediate(pupil.GetComponent<Collider>());
            pupil.transform.SetParent(r, false);
            pupil.transform.localPosition = new Vector3(0f, -0.12f, -0.06f);
            pupil.transform.localScale = new Vector3(0.42f, 0.42f, 0.12f);
            pupil.GetComponent<Renderer>().sharedMaterial = m.Dark;
            var ge = r.gameObject.AddComponent<global::GooglyEye>();
            ge.Eye = pupil.transform;
            ge.Speed = 2.5f;
            ge.GravityMultiplier = 1f;
            ge.Bounciness = 0.55f;
        }
    }

    // =====================================================================================================
    // Primitive helpers (world-space positions; parents keep scale 1 except attachments' targets)
    // =====================================================================================================

    static Transform Node(Transform parent, string name, Vector3 worldPos, Quaternion worldRot)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(worldPos, worldRot);
        go.layer = parent != null ? parent.gameObject.layer : 0;
        return go.transform;
    }

    static Transform Prim(PrimitiveType type, Transform parent, string name, Vector3 worldPos, Vector3 worldScale, Material mat, Quaternion worldRot, bool shadows)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(worldPos, worldRot);
        Vector3 ps = parent != null ? parent.lossyScale : Vector3.one;
        go.transform.localScale = new Vector3(worldScale.x / ps.x, worldScale.y / ps.y, worldScale.z / ps.z);
        go.layer = parent != null ? parent.gameObject.layer : 0;
        var r = go.GetComponent<MeshRenderer>();
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

    static BoxCollider Col(Transform parent, string name, Vector3 worldCenter, Vector3 size, int layer, PhysicsMaterial pm = null, Quaternion? rot = null)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(worldCenter, rot ?? Quaternion.identity);
        go.layer = layer;
        var c = go.AddComponent<BoxCollider>();
        c.size = size;
        if (pm != null) c.sharedMaterial = pm;
        return c;
    }

    static TextMeshPro Text(Transform parent, string name, string text, Vector3 worldPos, Quaternion worldRot, Vector2 box, Color color)
    {
        // TMP swaps in a RectTransform whose serialized anchoredPosition wins over localPosition on reload,
        // so position a plain holder and keep the text itself at the holder's origin.
        var holder = Node(parent, name, worldPos, worldRot);
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

    // =====================================================================================================
    // Materials
    // =====================================================================================================

    class Mats
    {
        readonly string dir;
        public Material Steel, Frame, Dark, Yellow, Red, Gold, White, Wood, Velvet, Rubber, Green, Lamp, Nose, Lobster, Tape, TapeYellow,
            Brain, Glass, GlassBlue, Laser, Mirror, MirrorBright, TableTopXL, Glint;
        public PhysicsMaterial TablePhysics;

        public Mats(string dir)
        {
            this.dir = dir;
            EnsureFolder(dir);
            Steel = Lit("Steel", new Color(0.72f, 0.74f, 0.78f), 0.75f, 0.8f);
            Frame = Lit("Frame", new Color(0.33f, 0.35f, 0.38f), 0.45f, 0.5f);
            Dark = Lit("Dark", new Color(0.09f, 0.09f, 0.1f), 0.3f);
            Yellow = Lit("Yellow", new Color(1f, 0.78f, 0.08f), 0.35f);
            Red = Lit("Red", new Color(0.92f, 0.12f, 0.1f), 0.55f);
            Gold = Lit("Gold", new Color(1f, 0.82f, 0.2f), 0.65f, 0.35f);
            White = Lit("White", new Color(0.97f, 0.97f, 0.95f), 0.5f);
            Wood = Lit("Wood", new Color(0.55f, 0.34f, 0.16f), 0.15f);
            Velvet = Lit("Velvet", new Color(0.45f, 0.03f, 0.08f), 0.2f);
            Rubber = Lit("Rubber", new Color(0.12f, 0.12f, 0.13f), 0.15f);
            Green = Lit("Green", new Color(0.1f, 0.5f, 0.25f), 0.35f);
            Lamp = Lit("Lamp", new Color(0.3f, 1f, 0.35f), 0.8f, 0f, new Color(0.2f, 1f, 0.25f));
            Nose = Lit("Nose", new Color(1f, 0.55f, 0.6f), 0.5f);
            Lobster = Lit("Lobster", new Color(0.95f, 0.3f, 0.12f), 0.55f);
            Tape = Lit("Tape", new Color(0.55f, 0.55f, 0.58f), 0.3f);
            TapeYellow = Lit("TapeYellow", new Color(1f, 0.85f, 0.15f), 0.2f);
            Brain = Lit("Brain", new Color(1f, 0.6f, 0.7f), 0.6f);
            Glass = Transparent("Glass", new Color(0.75f, 0.9f, 1f, 0.28f));
            GlassBlue = Transparent("GlassBlue", new Color(0.6f, 0.85f, 1f, 0.45f));
            Laser = Unlit("Laser", new Color(1f, 0.1f, 0.1f));
            Mirror = Lit("Mirror", new Color(0.8f, 0.82f, 0.86f), 0.95f, 1f);
            MirrorBright = Lit("MirrorFacet", new Color(0.95f, 0.95f, 1f), 1f, 0.9f, new Color(0.3f, 0.3f, 0.35f));
            TableTopXL = Lit("TableTopXL", new Color(0.83f, 0.85f, 0.86f), 0.35f);
            Glint = Unlit("Glint", Color.white);
            TablePhysics = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>("Assets/Materials/Generated/Sorting/Sorting_TableTop.physicMaterial");
        }

        Material Load(string name, Shader shader)
        {
            string path = $"{dir}/Garage_{name}.mat";
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

        Material Unlit(string name, Color c)
        {
            var mat = Load(name, Shader.Find("Universal Render Pipeline/Unlit"));
            mat.SetColor("_BaseColor", c);
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
