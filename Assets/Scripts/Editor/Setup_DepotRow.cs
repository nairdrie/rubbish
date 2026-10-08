using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TMPro;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>
/// Builds <c>Assets/Scenes/DepotRow.unity</c> (Docs/Multiplayer.md §13.2, package N3), idempotently: everything lives
/// under one root, <c>DepotRowGen</c>, rebuilt on every run.
/// <list type="bullet">
/// <item><b>Four lots</b> on Depot Row's west side (<see cref="DepotRowLayout"/>; the pads themselves are laid by
/// NeighborhoodBuilder): a shop facade (the town depot's building: blocks, roller door, bay shell), a two-line sign
/// ("Nick's" / "RUBBISH CO."), shutters + "BACK SOON", a "FOR LEASE" board, a crane hook for the plop, the street door.</item>
/// <item><b>Four pocket interiors</b> (red-team S2: one additive scene, never duplicate ShopScene loads): each is a copy
/// of ShopScene's contents, made from ShopScene itself at edit time (so whatever the ShopScene setups
/// — SortingStation, GarageUpgrades, GarageDisplays, Shop, Ebae — built there is what every lot gets), offset to
/// <see cref="DepotRowLayout.InteriorOrigin"/> and bound to its lot: world-space fields moved, the bay marker untagged and
/// wired to the TIP IT lever, the door pointed back at the street, the chess NetworkObjects stripped (no in-scene
/// NetworkObjects in lots: red-team T10), the laptop given a "rename business" desk.</item>
/// <item>One shared laptop UI (ebag / Scrapazon) for the one world.</item>
/// </list>
/// Re-run it after any ShopScene setup (SortingStation, GarageUpgrades, GarageDisplays, Shop) so the lots match.
/// Never touches ShopScene, Neighborhood or Bootstrap. Adds DepotRow to the build settings.
/// </summary>
public static class Setup_DepotRow
{
    const string ShopScenePath = "Assets/Scenes/ShopScene.unity";
    const string RootName = "DepotRowGen";
    const string MatDir = "Assets/Materials/Generated/DepotRow";
    const string NbMatDir = "Assets/Materials/Generated/Neighborhood";
    const string Gar = "Assets/Garage (Low Poly)/Prefabs/";
    const int LayerDefault = 0, LayerInteractable = 3, LayerGround = 6;

    /// <summary>ShopScene roots that are not part of a shop interior (camera/UI/EventSystem; the door-glow sky light).</summary>
    static readonly string[] SkipRoots = { "CommonWorldElements", "WorldSky" };
    static readonly string[] OwnerOnlyRoots = { "GarageUpgrades", "GarageDisplays" };

    [MenuItem("Rubbish/Setup/DepotRow")]
    public static void Run() => Build();

    public static string Build()
    {
        var log = new StringBuilder("[Setup_DepotRow] ");
        if (EditorApplication.isPlayingOrWillChangePlaymode) { Debug.LogError("[Setup_DepotRow] Exit play mode first."); return "play mode"; }

        var prevActive = SceneManager.GetActiveScene();
        Scene shop = SceneManager.GetSceneByPath(ShopScenePath);
        bool openedShop = false;
        Scene depot = SceneManager.GetSceneByPath(DepotRowLayout.ScenePath);
        bool openedDepot = false;
        try
        {
            if (!shop.IsValid() || !shop.isLoaded) { shop = EditorSceneManager.OpenScene(ShopScenePath, OpenSceneMode.Additive); openedShop = true; }
            if (!depot.IsValid() || !depot.isLoaded)
            {
                if (System.IO.File.Exists(DepotRowLayout.ScenePath)) depot = EditorSceneManager.OpenScene(DepotRowLayout.ScenePath, OpenSceneMode.Additive);
                else
                {
                    depot = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                    EditorSceneManager.SaveScene(depot, DepotRowLayout.ScenePath);
                    log.Append("created DepotRow.unity; ");
                }
                openedDepot = true;
            }

            foreach (var r in depot.GetRootGameObjects()) if (r.name == RootName) Object.DestroyImmediate(r);
            var root = new GameObject(RootName);
            SceneManager.MoveGameObjectToScene(root, depot);
            var sceneComp = root.AddComponent<DepotRowScene>();
            CopyShopLighting(shop, sceneComp);

            BuildSharedUi(shop, root.transform, log);

            var mats = Materials();
            for (int k = 0; k < DepotRowLayout.LotCount; k++)
            {
                var lotGo = new GameObject($"Lot{k + 1}");
                lotGo.transform.SetParent(root.transform, false);
                var lot = lotGo.AddComponent<DepotLot>();
                lot.lot = k;
                sceneComp.lots[k] = lot;
                BuildStreet(lot, mats);
                BuildInterior(shop, lot, log);
            }

            EditorSceneManager.MarkSceneDirty(depot);
            EditorSceneManager.SaveScene(depot);
            AddToBuildSettings(log);
            log.Append($"built {DepotRowLayout.LotCount} lots; {Count(root)} objects; saved.");
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            log.Append("FAILED: " + e.Message);
        }
        finally
        {
            if (prevActive.IsValid() && prevActive.isLoaded) SceneManager.SetActiveScene(prevActive);
            if (openedShop && shop.IsValid()) EditorSceneManager.CloseScene(shop, true);
            if (openedDepot && depot.IsValid()) EditorSceneManager.CloseScene(depot, true);
        }
        Debug.Log(log.ToString());
        return log.ToString();
    }

    static int Count(GameObject go) => go.GetComponentsInChildren<Transform>(true).Length;

    // =====================================================================
    // Pocket interiors (copies of ShopScene)
    // =====================================================================

    static void BuildInterior(Scene shop, DepotLot lot, StringBuilder log)
    {
        int k = lot.lot;
        Vector3 origin = DepotRowLayout.InteriorOrigin(k);
        var interior = new GameObject("Interior");
        interior.transform.SetParent(lot.transform, false);
        interior.transform.localPosition = origin;

        // Clone every interior root, then point references between roots at the copies (Instantiate only remaps inside one hierarchy).
        var sources = shop.GetRootGameObjects().Where(r => !SkipRoots.Contains(r.name)).ToList();
        var map = new Dictionary<Object, Object>();
        var clones = new List<GameObject>();
        foreach (var src in sources)
        {
            var c = Object.Instantiate(src, interior.transform, false);
            c.name = src.name;
            c.transform.localPosition = src.transform.position;
            c.transform.localRotation = src.transform.rotation;
            c.transform.localScale = src.transform.lossyScale;
            c.SetActive(src.activeSelf);
            clones.Add(c);
            MapHierarchy(src, c, map);
        }
        int remapped = 0;
        foreach (var c in clones)
            foreach (var comp in c.GetComponentsInChildren<Component>(true))
            {
                if (comp == null || comp is Transform) continue;
                var so = new SerializedObject(comp);
                var it = so.GetIterator();
                bool changed = false;
                while (it.Next(true))
                {
                    if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                    var o = it.objectReferenceValue;
                    if (o != null && map.TryGetValue(o, out var to) && to != o) { it.objectReferenceValue = to; changed = true; remapped++; }
                }
                if (changed) so.ApplyModifiedPropertiesWithoutUndo();
            }

        // Convert each copied chess set to a pure local visual/interaction facade before stripping the legacy
        // scene NetworkObjects. DepotChessNet on NetSession owns all four games and routes by lot + piece index.
        int chessBoards = 0;
        foreach (var chess in interior.GetComponentsInChildren<ChessController>(true))
        {
            var source = new SerializedObject(chess);
            var bot = chess.GetComponent<MagnusChessBot>();
            var botSource = bot ? new SerializedObject(bot) : null;
            var board = chess.gameObject.AddComponent<DepotChessBoard>();
            board.lot = k;
            board.whiteCam = source.FindProperty("whiteCam").objectReferenceValue as Cinemachine.CinemachineVirtualCamera;
            board.blackCam = source.FindProperty("blackCam").objectReferenceValue as Cinemachine.CinemachineVirtualCamera;
            board.boardParent = source.FindProperty("boardParent").objectReferenceValue as Transform;
            board.whiteCaptured = source.FindProperty("whiteCapturedPiecesContainer").objectReferenceValue as Transform;
            board.blackCaptured = source.FindProperty("blackCapturedPiecesContainer").objectReferenceValue as Transform;
            board.turnIndicator = source.FindProperty("turnIndicatorText").objectReferenceValue as TextMeshProUGUI;
            board.boardLayer = source.FindProperty("boardLayer").intValue;
            board.pieceLayer = source.FindProperty("pieceLayer").intValue;
            board.capturedSpacing = source.FindProperty("capturedSpacing").floatValue;
            board.capturedPerColumn = source.FindProperty("capturedPerColumn").intValue;
            if (botSource != null)
            {
                board.arm = botSource.FindProperty("arm").objectReferenceValue as MagnusArm;
                board.track = botSource.FindProperty("track").objectReferenceValue as MagnusTrack;
                board.magnusPlays = botSource.FindProperty("magnusPlays").boolValue;
            }
            var sourcePieces = chess.GetComponentsInChildren<ChessPiece>(true);
            board.pieces = new DepotChessPiece[sourcePieces.Length];
            for (int i = 0; i < sourcePieces.Length; i++)
            {
                var local = sourcePieces[i].gameObject.AddComponent<DepotChessPiece>();
                local.index = (byte)i;
                local.color = sourcePieces[i].Color;
                local.startingType = sourcePieces[i].Type;
                board.pieces[i] = local;
            }
            // ShopScene's "Interaction Layer" sphere is a sibling of the legacy ChessController under the
            // chess-table root, so GetComponentInParent<IInteractable>() never found the board itself.
            var tableRoot = chess.transform.parent;
            if (tableRoot != null)
                foreach (var t in tableRoot.GetComponentsInChildren<Transform>(true))
                    if (t.name == "Interaction Layer" && t.GetComponent<Collider>() != null &&
                        (t.position - chess.transform.position).sqrMagnitude < 4f)
                        t.gameObject.AddComponent<DepotChessInteractable>().board = board;
            chessBoards++;
        }
        // No in-scene NetworkObjects inside lots (T10). ChessSquare is a plain MonoBehaviour and stays.
        int stripped = 0;
        foreach (var t in new[] { typeof(MagnusChessBot), typeof(ChessController), typeof(ChessPiece) })
            foreach (var comp in interior.GetComponentsInChildren(t, true)) { Object.DestroyImmediate(comp); stripped++; }
        foreach (var nb in interior.GetComponentsInChildren<NetworkBehaviour>(true)) { Object.DestroyImmediate(nb); stripped++; }
        foreach (var no in interior.GetComponentsInChildren<NetworkObject>(true)) { Object.DestroyImmediate(no); stripped++; }
        // Nothing that would fight the town's camera, listener or sun.
        foreach (var cam in interior.GetComponentsInChildren<Camera>(true)) if (cam.targetTexture == null) { Object.DestroyImmediate(cam); stripped++; }
        foreach (var al in interior.GetComponentsInChildren<AudioListener>(true)) { Object.DestroyImmediate(al); stripped++; }
        foreach (var l in interior.GetComponentsInChildren<Light>(true)) if (l.type == LightType.Directional) { l.enabled = false; stripped++; }
        log.Append($"chess={chessBoards}, ");

        // World-space fields move with the interior.
        foreach (var u in interior.GetComponentsInChildren<TippingFloor>(true)) u.pileCenter += origin;
        foreach (var c in interior.GetComponentsInChildren<Compactor>(true)) c.pileCenter += origin;

        // Markers: untag (FlowUtil.FindTagged must never find a lot's copy), wire the bay to its tipping floor.
        Transform bay = null;
        foreach (var t in interior.GetComponentsInChildren<Transform>(true))
        {
            switch (t.name)
            {
                case "TruckSpawner": bay = t; t.name = "Bay"; break;
                case "PlayerSpawner": t.name = "PlayerSpot"; break;
                case "SpawnPoint_FromNeighborhood": lot.interiorOnFoot = t; t.name = "Arrive_OnFoot"; break;
                case "SpawnPoint_FromNeighborhood_Truck": lot.interiorTruck = t; t.name = "Arrive_Truck"; break;
            }
            if (t.CompareTag(FlowUtil.PlayerSpawnerTag) || t.CompareTag(FlowUtil.TruckSpawnerTag)) t.tag = "Untagged";
        }
        foreach (var u in interior.GetComponentsInChildren<TippingFloor>(true)) u.bayAnchor = bay;

        // The door inside goes back out to this lot's apron.
        foreach (var d in interior.GetComponentsInChildren<GarageDoorController>(true))
        {
            d.depotLot = k;
            d.depotExit = true;
            d.targetSceneName = "";
            d.promptOnFoot = "Out to Depot Row";
            d.promptDriving = "Drive out";
            lot.interiorDoor = d;
        }

        // Laptop: the owner's "rename business" desk next to it (F = the laptop's second option).
        var laptop = interior.GetComponentInChildren<LaptopController>(true);
        lot.laptop = laptop;
        if (laptop != null)
        {
            var desk = new GameObject("RenameDesk");
            desk.transform.SetParent(laptop.transform, false);
            desk.transform.localPosition = Vector3.zero;
            desk.transform.localRotation = Quaternion.identity;
            var rd = desk.AddComponent<BusinessRenameDesk>();
            rd.lot = k;
            var zone = desk.AddComponent<InteractionZone>();
            zone.mode = ZoneMode.OnFoot;
            zone.center = Vector3.zero;
            var ls = laptop.transform.lossyScale;
            zone.size = new Vector3(3.2f / Mathf.Max(0.01f, Mathf.Abs(ls.x)), 3f / Mathf.Max(0.01f, Mathf.Abs(ls.y)), 3.2f / Mathf.Max(0.01f, Mathf.Abs(ls.z)));
            zone.score = 0.35f;
            zone.targetOverride = rd;
        }
        else log.Append($"lot {k + 1}: no laptop in ShopScene?; ");

        lot.ownerOnly = clones.Where(c => OwnerOnlyRoots.Contains(c.name)).ToArray();
        lot.interior = interior;

        // A floor under the bay that stays on while the lot is claimed (a parked truck must not fall on machines that
        // have this interior switched off).
        var floor = new GameObject("PocketFloor");
        floor.transform.SetParent(lot.transform, false);
        floor.transform.position = origin + new Vector3(0.5f, -0.25f, 0.5f);
        floor.layer = LayerGround;
        var bc = floor.AddComponent<BoxCollider>();
        bc.size = new Vector3(18f, 0.5f, 14f);
        lot.pocketFloor = floor;
        floor.SetActive(false);

        interior.SetActive(false);
        log.Append($"lot {k + 1}: {Count(interior)} objects, {remapped} cross-refs, {stripped} stripped; ");
    }

    static void MapHierarchy(GameObject src, GameObject dst, Dictionary<Object, Object> map)
    {
        var a = src.GetComponentsInChildren<Transform>(true);
        var b = dst.GetComponentsInChildren<Transform>(true);
        if (a.Length != b.Length) { Debug.LogWarning($"[Setup_DepotRow] hierarchy mismatch cloning {src.name}"); return; }
        for (int i = 0; i < a.Length; i++)
        {
            map[a[i].gameObject] = b[i].gameObject;
            var ca = a[i].GetComponents<Component>();
            var cb = b[i].GetComponents<Component>();
            if (ca.Length != cb.Length) continue;
            for (int j = 0; j < ca.Length; j++) if (ca[j] != null && cb[j] != null) map[ca[j]] = cb[j];
        }
    }

    static void CopyShopLighting(Scene shop, DepotRowScene comp)
    {
        var prev = SceneManager.GetActiveScene();
        SceneManager.SetActiveScene(shop);
        comp.shopAmbientMode = RenderSettings.ambientMode;
        comp.shopAmbientSky = RenderSettings.ambientSkyColor;
        comp.shopAmbientEquator = RenderSettings.ambientEquatorColor;
        comp.shopAmbientGround = RenderSettings.ambientGroundColor;
        comp.shopReflectionIntensity = RenderSettings.reflectionIntensity;
        if (prev.IsValid() && prev.isLoaded) SceneManager.SetActiveScene(prev);
    }

    // =====================================================================
    // Shared laptop UI
    // =====================================================================

    static void BuildSharedUi(Scene shop, Transform root, StringBuilder log)
    {
        EbaeUI src = null;
        foreach (var r in shop.GetRootGameObjects())
        {
            src = r.GetComponentInChildren<EbaeUI>(true);
            if (src != null) break;
        }
        if (src == null) { log.Append("no EbaeUI in ShopScene: laptop UI missing; "); return; }
        var so = new SerializedObject(src);
        var doc = so.FindProperty("ebae")?.objectReferenceValue as GameObject;
        var ui = new GameObject("LaptopUI");
        ui.transform.SetParent(root, false);
        if (doc != null)
        {
            var copy = Object.Instantiate(doc, ui.transform, false);
            copy.name = doc.name;
            copy.SetActive(false);
            var e = ui.AddComponent<EbaeUI>();
            var eso = new SerializedObject(e);
            eso.FindProperty("ebae").objectReferenceValue = copy;
            eso.ApplyModifiedPropertiesWithoutUndo();
        }
        else log.Append("EbaeUI has no 'ebag' document; ");
    }

    // =====================================================================
    // Street side
    // =====================================================================

    sealed class Mats { public Material wall, metal, board, dark, white, yellow, black, cable, lease; }

    static Mats Materials()
    {
        if (!AssetDatabase.IsValidFolder(MatDir))
        {
            if (!AssetDatabase.IsValidFolder("Assets/Materials/Generated")) AssetDatabase.CreateFolder("Assets/Materials", "Generated");
            AssetDatabase.CreateFolder("Assets/Materials/Generated", "DepotRow");
        }
        var wall = AssetDatabase.LoadAssetAtPath<Material>("Assets/Garage (Low Poly)/Materials/M_wall_a.mat");
        return new Mats
        {
            wall = wall != null ? wall : Mat("DR_Wall", new Color(0.55f, 0.57f, 0.6f), 0.1f, 0f),
            metal = AssetDatabase.LoadAssetAtPath<Material>(NbMatDir + "/NB_Metal.mat") ?? Mat("DR_Metal", new Color(0.45f, 0.47f, 0.5f), 0.4f, 0.6f),
            board = Mat("DR_SignBoard", Color.white, 0.25f, 0f),
            dark = Mat("DR_Shutter", new Color(0.24f, 0.25f, 0.27f), 0.3f, 0.4f),
            white = Mat("DR_White", new Color(0.95f, 0.94f, 0.9f), 0.2f, 0f),
            yellow = AssetDatabase.LoadAssetAtPath<Material>(NbMatDir + "/NB_LineYellow.mat") ?? Mat("DR_Yellow", new Color(1f, 0.8f, 0.1f), 0.2f, 0f),
            black = Mat("DR_Black", new Color(0.06f, 0.06f, 0.07f), 0.3f, 0f),
            cable = Mat("DR_Cable", new Color(0.12f, 0.12f, 0.13f), 0.5f, 0.6f),
            lease = Mat("DR_LeaseBoard", new Color(0.93f, 0.33f, 0.2f), 0.2f, 0f),
        };
    }

    static Material Mat(string name, Color col, float smooth, float metal)
    {
        string path = MatDir + "/" + name + ".mat";
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null) { m = new Material(shader) { name = name }; AssetDatabase.CreateAsset(m, path); }
        else if (shader != null && m.shader != shader) m.shader = shader;
        m.SetColor("_BaseColor", col);
        m.color = col;
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metal);
        m.enableInstancing = true;
        EditorUtility.SetDirty(m);
        return m;
    }

    static void BuildStreet(DepotLot lot, Mats m)
    {
        int k = lot.lot;
        // Author the whole street assembly in its familiar +Z frame, then rigidly place only the facade/lease
        // roots. Never rotate the DepotLot root: it also owns the unrelated underground pocket interior.
        const float cx = 0f, fz = 0f;

        // ---- FOR LEASE board (on the empty pad; off once claimed)
        var lease = new GameObject("ForLease");
        lease.transform.SetParent(lot.transform, false);
        lease.transform.position = new Vector3(cx + 7f, 0f, fz + 8f);
        Box(lease.transform, "Post", new Vector3(cx + 7f, 1.2f, fz + 8f), new Vector3(0.18f, 2.4f, 0.18f), m.metal, true);
        Box(lease.transform, "Board", new Vector3(cx + 7f, 2.6f, fz + 8.05f), new Vector3(3.2f, 1.6f, 0.1f), m.lease, false);
        Label(lease.transform, "Text", $"FOR LEASE\n<size=55%>DEPOT ROW · LOT {k + 1}</size>", new Vector3(cx + 7f, 2.6f, fz + 8.12f),
              Quaternion.Euler(0f, 180f, 0f), new Vector2(3f, 1.4f), Color.white);
        lot.forLeaseSign = lease;

        // ---- the shop (drops in on claim)
        var facade = new GameObject("Shop");
        facade.transform.SetParent(lot.transform, false);
        facade.transform.position = new Vector3(cx, 0f, fz);
        var f = facade.transform;
        Vector3 P(float x, float y, float z) => new Vector3(cx + x, y, fz + z);

        Prefab(Gar + "SM_bld_block_01", f, P(-6.72f, 0f, -4.10f), Quaternion.identity, false, LayerGround);
        Prefab(Gar + "SM_bld_block_01", f, P(6.72f, 0f, -4.10f), Quaternion.identity, true, LayerGround);
        Prefab(Gar + "SM_garage_entry_01", f, P(0f, 0f, 0f), Quaternion.identity, false, LayerGround);
        var shell = Box(f, "BayShell", P(0f, 3.915f, -4.2f), new Vector3(7.5f, 7.83f, 8.0f), m.wall, true);
        shell.layer = LayerGround;
        var door = Prefab(Gar + "SM_garage_door", f, P(-0.025f, 0.16f, 0.02f), Quaternion.identity, false, LayerGround);
        if (door != null) door.transform.localScale = new Vector3(1.76f, 1.77f, 1f);

        // Colour trims down both sides of the door (business colour) + a kerb stripe.
        var paints = new List<Renderer>();
        paints.Add(Box(f, "Trim_L", P(-3.35f, 2.8f, 0.12f), new Vector3(0.35f, 5.6f, 0.12f), m.board, false).GetComponent<Renderer>());
        paints.Add(Box(f, "Trim_R", P(3.35f, 2.8f, 0.12f), new Vector3(0.35f, 5.6f, 0.12f), m.board, false).GetComponent<Renderer>());
        paints.Add(Box(f, "Stripe", P(0f, 0.06f, 1.6f), new Vector3(9.6f, 0.02f, 0.3f), m.board, false).GetComponent<Renderer>());

        // Sign: posts + board in the business colour, "Nick's" small on top, "RUBBISH CO." big.
        var sign = new GameObject("Sign").transform;
        sign.SetParent(f, false);
        sign.position = P(0f, 8.6f, -0.5f);
        Box(sign, "PostL", P(-3.6f, 8.45f, -0.7f), new Vector3(0.3f, 1.3f, 0.3f), m.metal, true);
        Box(sign, "PostR", P(3.6f, 8.45f, -0.7f), new Vector3(0.3f, 1.3f, 0.3f), m.metal, true);
        var board = Box(sign, "Board", P(0f, 10.15f, -0.55f), new Vector3(11.2f, 2.9f, 0.3f), m.board, true);
        paints.Add(board.GetComponent<Renderer>());
        Box(sign, "BoardEdge", P(0f, 10.15f, -0.6f), new Vector3(11.5f, 3.15f, 0.04f), m.black, false); // inside the board: a rim
        Box(sign, "Bolt_L", P(-5.2f, 10.15f, -0.36f), new Vector3(0.22f, 0.22f, 0.06f), m.metal, false);
        Box(sign, "Bolt_R", P(5.2f, 10.15f, -0.36f), new Vector3(0.22f, 0.22f, 0.06f), m.metal, false);
        lot.ownerText = Label(sign, "Owner", "Nick's", P(0f, 11.05f, -0.36f), Quaternion.Euler(0f, 180f, 0f), new Vector2(8f, 0.75f), new Color(0.06f, 0.06f, 0.07f));
        lot.ownerText.fontStyle = FontStyles.Bold | FontStyles.Italic;
        lot.nameText = Label(sign, "Name", DepotBusinesses.DefaultName, P(0f, 9.85f, -0.36f), Quaternion.Euler(0f, 180f, 0f), new Vector2(10.6f, 1.7f), Color.white);
        lot.nameText.outlineWidth = 0.18f;
        lot.nameText.outlineColor = new Color32(20, 20, 24, 255);
        lot.signRoot = sign;
        lot.paintRenderers = paints.ToArray();

        // Shutters + BACK SOON (owner away).
        var shut = new GameObject("Shutters");
        shut.transform.SetParent(f, false);
        for (int i = 0; i < 9; i++)
            Box(shut.transform, "Slat" + i, P(0f, 0.35f + i * 0.6f, 0.22f), new Vector3(6.1f, 0.52f, 0.08f), m.dark, false);
        lot.shutters = shut;
        shut.SetActive(false);
        var soon = new GameObject("BackSoon");
        soon.transform.SetParent(f, false);
        Box(soon.transform, "Plank", P(0f, 2.9f, 0.32f), new Vector3(3.6f, 1.1f, 0.08f), m.yellow, false);
        Label(soon.transform, "Text", "BACK SOON", P(0f, 2.9f, 0.38f), Quaternion.Euler(0f, 180f, 0f), new Vector2(3.3f, 0.9f), new Color(0.08f, 0.08f, 0.1f));
        lot.backSoonSign = soon;
        soon.SetActive(false);

        // Crane hook (only shown during the plop).
        var hook = new GameObject("CraneHook").transform;
        hook.SetParent(f, false);
        hook.position = P(0f, 12.2f, -4f);
        Box(hook, "Cable", P(0f, 27f, -4f), new Vector3(0.09f, 28f, 0.09f), m.cable, false);
        Box(hook, "Hook", P(0f, 12.6f, -4f), new Vector3(0.6f, 0.8f, 0.25f), m.yellow, false);
        Box(hook, "Chain_L", P(-2.6f, 12.1f, -4f), new Vector3(5.4f, 0.06f, 0.06f), m.cable, false).transform.rotation = Quaternion.Euler(0f, 0f, -12f);
        Box(hook, "Chain_R", P(2.6f, 12.1f, -4f), new Vector3(5.4f, 0.06f, 0.06f), m.cable, false).transform.rotation = Quaternion.Euler(0f, 0f, 12f);
        lot.craneHook = hook;
        hook.gameObject.SetActive(false);

        // Street door (on foot: walk in; driving: the bay).
        var doorGo = new GameObject("StreetDoor");
        doorGo.transform.SetParent(f, false);
        doorGo.transform.SetPositionAndRotation(P(0f, 0f, 0.2f), Quaternion.identity);
        var gdc = doorGo.AddComponent<GarageDoorController>();
        gdc.depotLot = k;
        gdc.depotExit = false;
        gdc.targetSceneName = "";
        gdc.promptOnFoot = "Enter your shop";
        gdc.promptDriving = "Drive into the garage";
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
        lot.streetDoor = gdc;

        lot.facade = f;
        lot.ApplyStreetLayout();
        facade.SetActive(false);
    }

    static GameObject Prefab(string path, Transform parent, Vector3 pos, Quaternion rot, bool mirrorX, int layer)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path + ".prefab");
        if (prefab == null) { Debug.LogWarning("[Setup_DepotRow] missing prefab " + path); return null; }
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
        go.transform.SetPositionAndRotation(pos, rot);
        var s = prefab.transform.localScale;
        if (mirrorX) s.x = -s.x;
        go.transform.localScale = s;
        foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
        return go;
    }

    static GameObject Box(Transform parent, string name, Vector3 center, Vector3 size, Material m, bool collider)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(center, Quaternion.identity);
        go.transform.localScale = size;
        go.GetComponent<MeshRenderer>().sharedMaterial = m;
        if (size.y < 0.05f || size.x < 0.1f || size.z < 0.1f) go.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
        if (!collider) Object.DestroyImmediate(go.GetComponent<Collider>());
        return go;
    }

    static TextMeshPro Label(Transform parent, string name, string text, Vector3 pos, Quaternion rot, Vector2 size, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(pos, rot);
        var t = go.AddComponent<TextMeshPro>();
        t.text = text;
        t.color = color;
        t.fontStyle = FontStyles.Bold;
        t.alignment = TextAlignmentOptions.Center;
        t.textWrappingMode = TextWrappingModes.NoWrap;
        t.enableAutoSizing = true;
        t.fontSizeMin = 0.5f;
        t.fontSizeMax = 72f;
        t.rectTransform.sizeDelta = size;
        t.ForceMeshUpdate();
        return t;
    }

    // =====================================================================
    // Build settings
    // =====================================================================

    static void AddToBuildSettings(StringBuilder log)
    {
        var list = EditorBuildSettings.scenes.ToList();
        if (list.Any(s => s.path == DepotRowLayout.ScenePath)) { log.Append("in build settings; "); return; }
        list.Add(new EditorBuildSettingsScene(DepotRowLayout.ScenePath, true));
        EditorBuildSettings.scenes = list.ToArray();
        log.Append("added to build settings; ");
    }
}
