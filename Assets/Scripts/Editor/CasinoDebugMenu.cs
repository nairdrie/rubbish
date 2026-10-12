using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

/// <summary>
/// Rubbish/Debug/Casino: jump the local player in and out of The Lucky Raccoon in play mode, and (edit mode / batch mode)
/// render preview stills of the interior and the street front to PNG for a look check. Nothing here is saved to a scene.
/// </summary>
public static class CasinoDebugMenu
{
    [MenuItem("Rubbish/Debug/Casino/Teleport inside")]
    static void TeleportInside()
    {
        if (!Application.isPlaying) { Debug.LogWarning("[Casino] Play mode only."); return; }
        if (!CasinoWorld.SceneReady) { Debug.LogWarning("[Casino] Casino.unity is not loaded (in the town? Rubbish/Setup/Casino run?)."); return; }
        if (!CasinoWorld.LocalGoInside()) Debug.LogWarning("[Casino] Can't go inside right now (driving, or a transition is running).");
    }

    [MenuItem("Rubbish/Debug/Casino/Teleport to street front")]
    static void TeleportToFront()
    {
        if (!Application.isPlaying) { Debug.LogWarning("[Casino] Play mode only."); return; }
        if (!CasinoWorld.LocalGoToStreet()) Debug.LogWarning("[Casino] Can't go to the front right now (driving, or a transition is running).");
    }

    [MenuItem("Rubbish/Debug/Casino/Render preview stills")]
    public static void RenderStills() => Shots(Path.Combine(Directory.GetParent(Application.dataPath).FullName, "CasinoShots"));

    /// <summary>Batch mode: -executeMethod CasinoDebugMenu.BatchShots [-casinoShots dir] [-casinoNoTown].</summary>
    public static void BatchShots()
    {
        string dir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "CasinoShots");
        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "-casinoShots") dir = args[i + 1];
        Shots(dir, System.Array.IndexOf(args, "-casinoNoTown") < 0);
    }

    /// <summary>Batch mode: build, then shoot.</summary>
    public static void BatchBuildAndShoot()
    {
        Setup_Casino.Build();
        BatchShots();
    }

    static void Shots(string dir, bool town = true)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) { Debug.LogWarning("[Casino] Exit play mode first."); return; }
        Directory.CreateDirectory(dir);
        var scene = EditorSceneManager.OpenScene(CasinoLayout.ScenePath, OpenSceneMode.Single);
        CasinoScene cs = null;
        foreach (var r in scene.GetRootGameObjects()) { cs = r.GetComponent<CasinoScene>(); if (cs != null) break; }
        if (cs == null) { Debug.LogError("[Casino] No CasinoScene root: run Rubbish/Setup/Casino."); return; }
        cs.interior.SetActive(true);
        var staff = cs.GetComponentInChildren<CasinoStaff>(true);
        if (staff != null) { staff.BuildPeople(); staff.PosePreview(3.1f); }
        foreach (var ps in cs.GetComponentsInChildren<ParticleSystem>(true)) { ps.Simulate(12f, true, true); }

        // the room's look: its ambient, no sun, its grade
        CasinoScene.ApplyRoomAmbient(cs);
        RenderSettings.skybox = null;
        var vol = new GameObject("[preview volume]") { hideFlags = HideFlags.DontSave }.AddComponent<Volume>();
        vol.isGlobal = true; vol.priority = 300; vol.sharedProfile = CasinoScene.BuildProfile();

        Vector3 o = CasinoLayout.Origin;
        Shoot(dir, "interior_1_entrance", o + new Vector3(-5.2f, 1.75f, -5.4f), o + new Vector3(2.5f, 1.1f, 2.0f), 68f);
        Shoot(dir, "interior_2_bar", o + new Vector3(4.2f, 1.7f, -1.6f), o + new Vector3(9.6f, 1.35f, 1.6f), 60f);
        Shoot(dir, "interior_3_tables", o + new Vector3(0.2f, 2.3f, -2.4f), o + new Vector3(-1.0f, 0.9f, 2.6f), 62f);
        Shoot(dir, "interior_4_slots", o + new Vector3(-6.0f, 1.6f, -2.6f), o + new Vector3(-10.6f, 1.3f, -0.4f), 60f);
        Shoot(dir, "interior_5_reference", o + new Vector3(4.6f, 2.6f, -5.2f), o + new Vector3(-2.2f, 0.9f, 2.6f), 68f);
        Shoot(dir, "interior_6_roulette", o + new Vector3(-3.0f, 2.0f, 0.9f), o + new Vector3(-3.6f, 0.9f, 2.5f), 50f);
        Shoot(dir, "interior_7_slot_close", o + new Vector3(-9.1f, 1.45f, -1.2f), o + new Vector3(-10.5f, 1.25f, -0.8f), 50f);
        Shoot(dir, "interior_8_blackjack", o + new Vector3(3.0f, 2.1f, 0.3f), o + new Vector3(3.0f, 0.9f, 2.4f), 55f);
        Shoot(dir, "interior_10_booths", o + new Vector3(0.2f, 1.7f, -0.6f), o + new Vector3(4.2f, 0.9f, -5.6f), 66f);
        Shoot(dir, "interior_11_stools", o + new Vector3(5.2f, 1.2f, -2.2f), o + new Vector3(8.0f, 0.8f, -1.4f), 55f);
        Shoot(dir, "interior_12_dealer", o + new Vector3(1.2f, 1.5f, 1.6f), o + new Vector3(3.0f, 1.0f, 2.9f), 50f);
        Shoot(dir, "interior_9_back", o + new Vector3(9.6f, 2.1f, 5.4f), o + new Vector3(-3f, 1.0f, -1.5f), 70f);
        if (staff != null) staff.ClearPeople();
        Object.DestroyImmediate(vol.sharedProfile); Object.DestroyImmediate(vol.gameObject);

        if (town) StreetShots(dir);
        // leave nothing behind: reopen an empty scene without saving anything
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        Debug.Log("[Casino] Preview stills written to " + dir);
    }

    static void StreetShots(string dir)
    {
        string townPath = "Assets/Scenes/" + DepotRowWorld.TownSceneName + ".unity";
        if (!File.Exists(townPath)) { Debug.LogWarning("[Casino] No town scene at " + townPath); return; }
        EditorSceneManager.OpenScene(townPath, OpenSceneMode.Single);
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/Casino/CasinoFront.prefab");
        if (prefab == null) { Debug.LogWarning("[Casino] No front prefab."); return; }
        CasinoFront.TryLocate(out Vector3 pos, out Quaternion rot, out _, out bool found);
        Debug.Log($"[Casino] Front preview at {pos} (parcel found: {found}).");
        var go = Object.Instantiate(prefab, pos, rot);
        go.hideFlags = HideFlags.DontSave;
        var front = go.GetComponent<CasinoFront>();
        Physics.SyncTransforms();
        front.SnapToGround();
        Vector3 f = rot * Vector3.forward, r = rot * Vector3.right, up = Vector3.up;

        // day: as the scene is
        CasinoFront.previewNight = 0f; front.PreviewTick();
        Shoot(dir, "street_1_day", pos - f * 13f + r * 4f + up * 2.2f, pos + up * 3.2f, 62f);

        // night: sun off, a dark blue ambient, bloom
        foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None)) if (l.type == LightType.Directional) l.enabled = false;
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.06f, 0.08f, 0.16f); RenderSettings.ambientEquatorColor = new Color(0.05f, 0.06f, 0.11f); RenderSettings.ambientGroundColor = new Color(0.02f, 0.02f, 0.04f);
        RenderSettings.fog = false; RenderSettings.skybox = null; RenderSettings.reflectionIntensity = 0.1f;
        var vol = new GameObject("[preview volume]") { hideFlags = HideFlags.DontSave }.AddComponent<Volume>();
        vol.isGlobal = true; vol.priority = 300;
        var p = ScriptableObject.CreateInstance<VolumeProfile>(); p.hideFlags = HideFlags.DontSave;
        var bloom = p.Add<Bloom>(false); bloom.threshold.Override(0.95f); bloom.intensity.Override(1.1f); bloom.scatter.Override(0.7f);
        vol.sharedProfile = p;
        CasinoFront.previewNight = 1f; front.PreviewTick();
        Shoot(dir, "street_2_night", pos - f * 13f + r * 4f + up * 2.2f, pos + up * 3.2f, 62f, new Color(0.015f, 0.02f, 0.05f));
        Shoot(dir, "street_3_night_door", pos - f * 5.5f - r * 1.6f + up * 1.7f, pos + up * 2.3f, 68f, new Color(0.015f, 0.02f, 0.05f));
        Shoot(dir, "street_4_night_far", pos - f * 9f - r * 26f + up * 2.4f, pos + up * 5f - f * 1f, 62f, new Color(0.015f, 0.02f, 0.05f));
        Object.DestroyImmediate(p); Object.DestroyImmediate(vol.gameObject); Object.DestroyImmediate(go);
    }

    static void Shoot(string dir, string name, Vector3 from, Vector3 lookAt, float fov, Color? background = null)
        => ShootRot(dir, name, from, Quaternion.LookRotation(lookAt - from, Vector3.up), fov, background);

    static void ShootRot(string dir, string name, Vector3 from, Quaternion rot, float fov, Color? background = null)
    {
        const int W = 1600, H = 900;
        var go = new GameObject("[preview camera]") { hideFlags = HideFlags.DontSave };
        var cam = go.AddComponent<Camera>();
        cam.transform.position = from; cam.transform.rotation = rot;
        cam.fieldOfView = fov; cam.nearClipPlane = 0.05f; cam.farClipPlane = 600f;
        cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = background ?? new Color(0.01f, 0.01f, 0.012f);
        cam.allowHDR = true;
        var data = go.AddComponent<UniversalAdditionalCameraData>();
        data.renderPostProcessing = true; data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing; data.renderShadows = true;
        var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 4 };
        cam.targetTexture = rt;
        cam.Render();
        var prev = RenderTexture.active; RenderTexture.active = rt;
        var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply();
        RenderTexture.active = prev;
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), tex.EncodeToPNG());
        cam.targetTexture = null;
        Object.DestroyImmediate(tex); rt.Release(); Object.DestroyImmediate(rt); Object.DestroyImmediate(go);
    }

    // ================================================================================================ seat mode (Docs/Casino.md §9.1)

    const string SeatRoot = "Rubbish/Debug/Casino/Seat camera at ";
    static CasinoStationKind pendingKind; static int pendingIndex, pendingSpot; static double pendingSince;

    static void SeatAt(CasinoStationKind kind, int index, int spot)
    {
        if (!Application.isPlaying) { Debug.LogWarning("[Casino] Play mode only."); return; }
        var st = CasinoStation.Find(kind, kind == CasinoStationKind.Slot ? index : 0);
        if (st == null) { Debug.LogWarning("[Casino] Casino.unity is not loaded (in the town? Rubbish/Setup/Casino run?)."); return; }
        if (CasinoSeatCamera.IsSeated) CasinoSeatCamera.Instance.Exit(CasinoSeatCamera.ExitReason.Requested);
        if (!CasinoWorld.LocalInside)
        {
            if (!CasinoWorld.LocalGoInside()) { Debug.LogWarning("[Casino] Can't go inside right now."); return; }
            pendingKind = kind; pendingIndex = index; pendingSpot = spot; pendingSince = EditorApplication.timeSinceStartup;
            EditorApplication.update -= WaitThenSeat; EditorApplication.update += WaitThenSeat;
            return;
        }
        EnterNow(st, spot);
    }

    static void WaitThenSeat()
    {
        double waited = EditorApplication.timeSinceStartup - pendingSince;
        if (!Application.isPlaying || waited > 20.0) { EditorApplication.update -= WaitThenSeat; return; }
        if (waited < 1.2 || CasinoWorld.Transitioning || !CasinoWorld.LocalInside) return;
        EditorApplication.update -= WaitThenSeat;
        var st = CasinoStation.Find(pendingKind, pendingKind == CasinoStationKind.Slot ? pendingIndex : 0);
        if (st != null) EnterNow(st, pendingSpot);
    }

    static void EnterNow(CasinoStation st, int spot)
    {
        var cam = CasinoSeatCamera.Ensure();
        if (!cam.Enter(st, spot)) Debug.LogWarning("[Casino] The seat camera refused (driving, busy, hands full, mid-transition?).");
        else Debug.Log("[Casino] Seat camera: " + st.kind + " spot " + (spot + 1) + ". Esc leaves.");
    }

    [MenuItem(SeatRoot + "Blackjack seat 1", false, 200)] static void Bj1() => SeatAt(CasinoStationKind.Blackjack, 0, 0);
    [MenuItem(SeatRoot + "Blackjack seat 2", false, 201)] static void Bj2() => SeatAt(CasinoStationKind.Blackjack, 0, 1);
    [MenuItem(SeatRoot + "Blackjack seat 3", false, 202)] static void Bj3() => SeatAt(CasinoStationKind.Blackjack, 0, 2);
    [MenuItem(SeatRoot + "Blackjack seat 4", false, 203)] static void Bj4() => SeatAt(CasinoStationKind.Blackjack, 0, 3);
    [MenuItem(SeatRoot + "Blackjack seat 5", false, 204)] static void Bj5() => SeatAt(CasinoStationKind.Blackjack, 0, 4);
    [MenuItem(SeatRoot + "Roulette spot 1", false, 210)] static void Rl1() => SeatAt(CasinoStationKind.Roulette, 0, 0);
    [MenuItem(SeatRoot + "Roulette spot 2", false, 211)] static void Rl2() => SeatAt(CasinoStationKind.Roulette, 0, 1);
    [MenuItem(SeatRoot + "Roulette spot 3", false, 212)] static void Rl3() => SeatAt(CasinoStationKind.Roulette, 0, 2);
    [MenuItem(SeatRoot + "Roulette spot 4", false, 213)] static void Rl4() => SeatAt(CasinoStationKind.Roulette, 0, 3);
    [MenuItem(SeatRoot + "Slot 0 Lucky Sevens", false, 220)] static void Sl0() => SeatAt(CasinoStationKind.Slot, 0, 0);
    [MenuItem(SeatRoot + "Slot 1 Trash Panda Bandit", false, 221)] static void Sl1() => SeatAt(CasinoStationKind.Slot, 1, 0);
    [MenuItem(SeatRoot + "Slot 2 Golden Gnome", false, 222)] static void Sl2() => SeatAt(CasinoStationKind.Slot, 2, 0);
    [MenuItem(SeatRoot + "Slot 3 Dumpster Fire", false, 223)] static void Sl3() => SeatAt(CasinoStationKind.Slot, 3, 0);
    [MenuItem(SeatRoot + "Slot 4 Mystery Bag", false, 224)] static void Sl4() => SeatAt(CasinoStationKind.Slot, 4, 0);
    [MenuItem(SeatRoot + "Bar spot 1", false, 230)] static void Ba1() => SeatAt(CasinoStationKind.Bar, 0, 0);
    [MenuItem(SeatRoot + "Bar spot 2", false, 231)] static void Ba2() => SeatAt(CasinoStationKind.Bar, 0, 1);
    [MenuItem(SeatRoot + "Bar spot 3", false, 232)] static void Ba3() => SeatAt(CasinoStationKind.Bar, 0, 2);

    [MenuItem("Rubbish/Debug/Casino/Seat camera: leave", false, 240)]
    static void SeatLeave() { if (CasinoSeatCamera.IsSeated) CasinoSeatCamera.Instance.Exit(CasinoSeatCamera.ExitReason.Requested); }
    [MenuItem("Rubbish/Debug/Casino/Seat camera: toggle lean", false, 241)]
    static void SeatLean() { if (CasinoSeatCamera.IsSeated) CasinoSeatCamera.Instance.ToggleLean(); }
    [MenuItem("Rubbish/Debug/Casino/Seat camera: toggle watch pose", false, 242)]
    static void SeatWatch() { if (CasinoSeatCamera.IsSeated) CasinoSeatCamera.Instance.Watch(!CasinoSeatCamera.Instance.Watching); }
    [MenuItem("Rubbish/Debug/Casino/Seat camera: toggle free cursor mode", false, 243)]
    static void SeatMode()
    {
        var c = CasinoSeatCamera.Ensure();
        c.Mode = c.Mode == CasinoSeatCamera.CursorMode.Locked ? CasinoSeatCamera.CursorMode.Free : CasinoSeatCamera.CursorMode.Locked;
        Debug.Log("[Casino] Seat cursor mode: " + c.Mode);
    }

    static CasinoCursor debugCursor;
    [MenuItem("Rubbish/Debug/Casino/Seat camera: world cursor logging", false, 244)]
    static void SeatCursor()
    {
        if (!Application.isPlaying) return;
        if (debugCursor != null) { Object.Destroy(debugCursor.gameObject); debugCursor = null; Debug.Log("[Casino] cursor logging off"); return; }
        debugCursor = CasinoCursor.Create();
        debugCursor.HoverChanged += (o, n) => Debug.Log("[CasinoCursor] hover " + (n != null ? n.kind + " '" + n.id + "' spot " + n.spot + " idx " + n.index + " " + n.hint : "(nothing)"));
        debugCursor.Clicked += c => Debug.Log("[CasinoCursor] " + (c.secondary ? "secondary" : "primary") + " click on " + (c.target != null ? c.target.kind + " '" + c.target.id + "'" : "nothing") + " at " + c.point);
        Debug.Log("[Casino] cursor logging on");
    }

    // ------------------------------------------------------------------------------------------------ seat stills

    /// <summary>Batch mode: -executeMethod CasinoDebugMenu.BatchSeatShots [-casinoShots dir]. Renders the first-person poses with
    /// stand-in cards, chips and board text so legibility can be judged.</summary>
    public static void BatchSeatShots()
    {
        string dir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "CasinoShots");
        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "-casinoShots") dir = args[i + 1];
        SeatShots(dir);
    }

    /// <summary>Batch mode: build the casino, then the seat stills.</summary>
    public static void BatchBuildAndSeatShots()
    {
        Setup_Casino.Build();
        BatchSeatShots();
    }

    /// <summary>Batch probe: what the saved scene holds (reference resolution of the seat components).</summary>
    public static void BatchProbe()
    {
        var scene = EditorSceneManager.OpenScene(CasinoLayout.ScenePath, OpenSceneMode.Single);
        CasinoScene cs = null;
        foreach (var r in scene.GetRootGameObjects()) { cs = r.GetComponent<CasinoScene>(); if (cs != null) break; }
        Debug.Log("[Casino] probe: scene stations=" + (cs != null && cs.stations != null ? cs.stations.Length : -1));
        foreach (var st in cs.stations)
        {
            Debug.Log("[Casino] probe: " + st.name + " kind=" + st.kind + " spots=" + st.SpotCount + " timerLamp=" + (st.timerLamp != null) + " resultBoard=" + (st.resultBoard != null) + " payPlate=" + (st.payPlate != null) + " layoutPlane=" + (st.layoutPlane != null));
            foreach (var c in st.transform.parent.GetComponentsInChildren<Component>(true))
                if (c == null) Debug.Log("[Casino] probe: NULL component somewhere under " + st.name);
        }
        foreach (var g in cs.GetComponentsInChildren<Transform>(true))
            if (g.name == "Result board") foreach (var c in g.GetComponents<Component>()) Debug.Log("[Casino] probe: Result board has " + (c == null ? "NULL (missing script)" : c.GetType().Name));
    }

    /// <summary>Batch mode: every pick collider can be hit from its spot's eye (nothing in front of it steals the ray), and the roulette
    /// layout's LayoutHit / LayoutPoint round-trip. Logs PASS / FAIL lines.</summary>
    public static void BatchPickTest()
    {
        var scene = EditorSceneManager.OpenScene(CasinoLayout.ScenePath, OpenSceneMode.Single);
        CasinoScene cs = null;
        foreach (var r in scene.GetRootGameObjects()) { cs = r.GetComponent<CasinoScene>(); if (cs != null) break; }
        cs.interior.SetActive(true);
        Physics.SyncTransforms();
        int checks = 0, fails = 0;
        foreach (var p in cs.GetComponentsInChildren<CasinoPickable>(true))
        {
            var col = p.GetComponent<Collider>();
            if (col == null) { Debug.Log("[Casino] PICK FAIL: no collider on " + p.name); fails++; continue; }
            if (p.kind == CasinoPickKind.Layout) continue; // tested below
            var st = p.station;
            if (st == null) { Debug.Log("[Casino] PICK FAIL: no station on " + p.name); fails++; continue; }
            for (int sIdx = 0; sIdx < st.SpotCount; sIdx++)
            {
                if (p.spot >= 0 && p.spot != sIdx) continue;
                var spot = st.spots[sIdx];
                Vector3 from = spot.eye.position, to = col.bounds.center;
                if (p.kind == CasinoPickKind.Lever) to = col.bounds.center;
                var hits = Physics.RaycastAll(new Ray(from, (to - from).normalized), (to - from).magnitude + 0.6f, CasinoLayout.PickMask, QueryTriggerInteraction.Collide);
                float best = float.MaxValue; CasinoPickable first = null;
                foreach (var h in hits) { var q = h.collider.GetComponentInParent<CasinoPickable>(); if (q != null && h.distance < best) { best = h.distance; first = q; } }
                checks++;
                if (first != p) { fails++; Debug.Log("[Casino] PICK FAIL: " + st.kind + " spot " + (sIdx + 1) + " -> " + p.kind + " '" + p.id + "' (" + p.name + ") hits " + (first != null ? first.kind + " '" + first.id + "' " + first.name : "nothing")); }
            }
        }
        var rl = CasinoStation.Find(CasinoStationKind.Roulette);
        if (rl != null)
        {
            var plane = rl.layoutPlane;
            foreach (var uv in new[] { new Vector2(0.5f, 0.5f), new Vector2(11.5f, 2.5f), new Vector2(-0.5f, 0.75f), new Vector2(12.5f, 1.5f), new Vector2(2f, -0.3f), new Vector2(7f, -1.1f), new Vector2(6f, 3f) })
            {
                Vector3 target = rl.LayoutPoint(uv.x, uv.y);
                for (int i = 0; i < rl.SpotCount; i++)
                {
                    Vector3 from = rl.spots[i].eye.position;
                    var ray = new Ray(from, (target - from).normalized);
                    checks++;
                    if (!plane.Raycast(ray, out var hit, 10f)) { fails++; Debug.Log("[Casino] PICK FAIL: layout plane missed at " + uv + " from spot " + (i + 1)); continue; }
                    if (!rl.LayoutHitLocal(hit.point, out float u, out float v) || Mathf.Abs(u - uv.x) > 0.06f || Mathf.Abs(v - uv.y) > 0.06f)
                    { fails++; Debug.Log("[Casino] PICK FAIL: layout (" + uv.x + "," + uv.y + ") came back as (" + u + "," + v + ") from spot " + (i + 1)); }
                    if (!CasinoStation.LayoutHit(hit.point, out float u2, out float v2) || Mathf.Abs(u2 - u) > 1e-4f) { fails++; Debug.Log("[Casino] PICK FAIL: static LayoutHit disagrees"); }
                }
            }
            // a number's cell centre round-trips through LayoutPoint(int)
            for (int n = 0; n <= 37; n++)
            {
                rl.LayoutHitLocal(rl.LayoutPoint(n), out float u, out float v); checks++;
                float eu = n == 0 || n == 37 ? -0.5f : (n - 1) / 3 + 0.5f, ev = n == 0 ? 0.75f : n == 37 ? 2.25f : (n - 1) % 3 + 0.5f;
                if (Mathf.Abs(u - eu) > 1e-3f || Mathf.Abs(v - ev) > 1e-3f) { fails++; Debug.Log("[Casino] PICK FAIL: number " + n + " cell is at (" + u + "," + v + ")"); }
            }
        }
        // bodies and cameras: nothing solid at a standing spot (except a stool that slides away and the floor), no neighbour overlap, no solid at an eye
        var mask = ~CasinoLayout.PickMask;
        foreach (var st in cs.stations)
        {
            for (int i = 0; st.spots != null && i < st.spots.Length; i++)
            {
                var sp = st.spots[i];
                Vector3 b0 = sp.stand.position + Vector3.up * 0.30f, b1 = sp.stand.position + Vector3.up * 1.22f;
                checks++;
                foreach (var c in Physics.OverlapCapsule(b0, b1, 0.256f, mask, QueryTriggerInteraction.Ignore))
                {
                    if (sp.stool != null && c.transform.IsChildOf(sp.stool)) continue; // the stool slides away under an occupant
                    if (c.gameObject.layer == 6) continue; // the floor
                    fails++; Debug.Log("[Casino] BODY FAIL: " + st.kind + " spot " + (i + 1) + " stands inside " + c.transform.parent?.name + "/" + c.name);
                }
                for (int j = i + 1; j < st.spots.Length; j++)
                {
                    checks++;
                    if (Vector2.Distance(new Vector2(sp.stand.position.x, sp.stand.position.z), new Vector2(st.spots[j].stand.position.x, st.spots[j].stand.position.z)) < 0.52f)
                    { fails++; Debug.Log("[Casino] BODY FAIL: " + st.kind + " spots " + (i + 1) + " and " + (j + 1) + " overlap"); }
                }
                foreach (var pose in new[] { sp.eye, sp.lean, sp.watch })
                {
                    if (pose == null) continue;
                    checks++;
                    foreach (var c in Physics.OverlapSphere(pose.position, 0.12f, mask, QueryTriggerInteraction.Ignore))
                    { fails++; Debug.Log("[Casino] EYE FAIL: " + st.kind + " spot " + (i + 1) + " " + pose.name + " is inside/near " + c.transform.parent?.name + "/" + c.name); }
                }
            }
        }
        Debug.Log("[Casino] PICK TEST " + (fails == 0 ? "PASS" : "FAILED") + ": " + checks + " checks, " + fails + " failures");
    }

    static void SeatShots(string dir)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) { Debug.LogWarning("[Casino] Exit play mode first."); return; }
        Directory.CreateDirectory(dir);
        var scene = EditorSceneManager.OpenScene(CasinoLayout.ScenePath, OpenSceneMode.Single);
        CasinoScene cs = null;
        foreach (var r in scene.GetRootGameObjects()) { cs = r.GetComponent<CasinoScene>(); if (cs != null) break; }
        if (cs == null) { Debug.LogError("[Casino] No CasinoScene root: run Rubbish/Setup/Casino."); return; }
        cs.interior.SetActive(true);
        var staff = cs.GetComponentInChildren<CasinoStaff>(true);
        if (staff != null) { staff.BuildPeople(); staff.PosePreview(3.1f); }
        foreach (var ps in cs.GetComponentsInChildren<ParticleSystem>(true)) { ps.Simulate(12f, true, true); }
        CasinoScene.ApplyRoomAmbient(cs);
        RenderSettings.skybox = null;
        var vol = new GameObject("[preview volume]") { hideFlags = HideFlags.DontSave }.AddComponent<Volume>();
        vol.isGlobal = true; vol.priority = 300; vol.sharedProfile = CasinoScene.BuildProfile();

        var props = DemoDress(cs);
        foreach (var bt in cs.GetComponentsInChildren<CasinoButton>(true)) { bt.SetEnabled(false); bt.SetEnabled(true); } // apply the dim / lit property blocks (Awake does it at run time)
        foreach (var t in cs.GetComponentsInChildren<TMPro.TMP_Text>(true)) t.ForceMeshUpdate(); // text set after the build must be meshed by hand in edit mode
        foreach (var st0 in cs.stations)
            if (st0 != null && st0.resultBoard != null)
            {
                var rb0 = st0.resultBoard; var blk = new MaterialPropertyBlock();
                rb0.discs[0].GetPropertyBlock(blk);
                Debug.Log("[Casino] probe board: discs=" + rb0.discs.Length + " first=" + (rb0.discs[0] != null ? rb0.discs[0].name : "null") + " base=" + blk.GetColor("_BaseColor") + " emis=" + blk.GetColor("_EmissionColor") + " num0='" + rb0.numbers[0].text + "' mat=" + rb0.discs[0].sharedMaterial.name);
            }
        string[] prefix = { "bj", "rl", "sl", "bar" };
        foreach (var st in cs.stations)
        {
            if (st == null) continue;
            string pre = prefix[(int)st.kind] + (st.kind == CasinoStationKind.Slot ? st.index.ToString() : "");
            for (int i = 0; st.spots != null && i < st.spots.Length; i++)
            {
                var sp = st.spots[i];
                if (sp == null || sp.eye == null) continue;
                string n = pre + "_s" + (i + 1);
                ShootRot(dir, n + "_eye", sp.eye.position, sp.eye.rotation, sp.eyeFov);
                if (sp.lean != null) ShootRot(dir, n + "_lean", sp.lean.position, sp.lean.rotation, sp.leanFov);
                if (sp.watch != null) ShootRot(dir, n + "_watch", sp.watch.position, sp.watch.rotation, sp.watchFov);
                // look-limit extremes (only for the first and last spot of a table, to keep the count down)
                if (i == 0 || i == st.spots.Length - 1)
                {
                    float yaw0 = sp.eye.eulerAngles.y, pitch0 = Mathf.Asin(Mathf.Clamp((sp.eye.rotation * Vector3.forward).y, -1f, 1f)) * Mathf.Rad2Deg;
                    ShootRot(dir, n + "_limL", sp.eye.position, Quaternion.Euler(-pitch0, yaw0 - sp.lookYaw, 0), sp.eyeFov);
                    ShootRot(dir, n + "_limR", sp.eye.position, Quaternion.Euler(-pitch0, yaw0 + sp.lookYaw, 0), sp.eyeFov);
                    ShootRot(dir, n + "_limU", sp.eye.position, Quaternion.Euler(-sp.lookPitchMax, yaw0, 0), sp.eyeFov);
                    ShootRot(dir, n + "_limD", sp.eye.position, Quaternion.Euler(-sp.lookPitchMin, yaw0, 0), sp.eyeFov);
                }
            }
        }
        // two overviews so the layout can be judged from outside
        Vector3 o = CasinoLayout.Origin;
        Shoot(dir, "ov_tables", o + new Vector3(0.2f, 2.6f, -3.6f), o + new Vector3(0.0f, 0.9f, 2.5f), 62f);
        Shoot(dir, "ov_slot_row", o + new Vector3(-6.4f, 1.7f, -1.4f), o + new Vector3(-10.4f, 1.3f, -0.8f), 62f);
        if (staff != null) staff.ClearPeople();
        foreach (var g in props) if (g != null) Object.DestroyImmediate(g);
        Object.DestroyImmediate(vol.sharedProfile); Object.DestroyImmediate(vol.gameObject);
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        Debug.Log("[Casino] Seat stills written to " + dir);
    }

    // ---- stand-in props (what the presenters will put there), editor-only and never saved

    static readonly System.Collections.Generic.Dictionary<string, Material> demoMats = new System.Collections.Generic.Dictionary<string, Material>();

    static Material DemoMat(Color c, float emission = 0f)
    {
        string key = c.ToString() + emission;
        if (demoMats.TryGetValue(key, out var m) && m != null) return m;
        m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { hideFlags = HideFlags.DontSave };
        m.SetColor("_BaseColor", c); m.SetFloat("_Smoothness", 0.35f);
        if (emission > 0f) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", c * emission); }
        demoMats[key] = m;
        return m;
    }

    static GameObject DemoPrim(PrimitiveType t, Transform parent, string name, Vector3 pos, Vector3 scale, Material mat, Vector3 euler = default)
    {
        var g = GameObject.CreatePrimitive(t); g.name = name;
        Object.DestroyImmediate(g.GetComponent<Collider>());
        g.transform.SetParent(parent, false); g.transform.localPosition = pos; g.transform.localRotation = Quaternion.Euler(euler); g.transform.localScale = scale;
        g.GetComponent<MeshRenderer>().sharedMaterial = mat;
        return g;
    }

    static void DemoCard(Transform spot, string rank, bool red, bool down, System.Collections.Generic.List<GameObject> keep)
    {
        if (spot == null) return;
        float w = CasinoLayout.CardWidth, h = CasinoLayout.CardHeight, t = CasinoLayout.CardThickness;
        var holder = new GameObject("DemoCard").transform; holder.SetParent(spot, false); keep.Add(holder.gameObject);
        var card = DemoPrim(PrimitiveType.Cube, holder, "Card", new Vector3(0, t * .5f, 0), new Vector3(w, t, h), DemoMat(down ? new Color(0.14f, 0.2f, 0.5f) : new Color(0.95f, 0.93f, 0.86f)));
        if (down) { DemoPrim(PrimitiveType.Cube, holder, "Back", new Vector3(0, t, 0), new Vector3(w * .7f, t * .5f, h * .8f), DemoMat(new Color(0.7f, 0.15f, 0.15f))); return; }
        Color ink = red ? new Color(0.78f, 0.08f, 0.08f) : new Color(0.05f, 0.05f, 0.06f);
        var tm = new GameObject("Rank").AddComponent<TMPro.TextMeshPro>();
        tm.transform.SetParent(holder, false);
        tm.transform.localPosition = new Vector3(-w * .28f, t + .0003f, h * .30f);
        tm.transform.localRotation = Quaternion.Euler(90, 0, 0);
        tm.rectTransform.sizeDelta = new Vector2(.042f, .046f);
        tm.enableAutoSizing = true; tm.fontSizeMin = .1f; tm.fontSizeMax = 72f; tm.alignment = TMPro.TextAlignmentOptions.Center; tm.fontStyle = TMPro.FontStyles.Bold;
        tm.text = rank; tm.color = ink;
        DemoPrim(PrimitiveType.Cube, holder, "Pip", new Vector3(0, t + .0003f, -h * .05f), new Vector3(w * .34f, t * .5f, w * .34f), DemoMat(ink), new Vector3(0, 45, 0));
    }

    static void DemoChips(Transform at, int cents, int n, System.Collections.Generic.List<GameObject> keep)
    {
        if (at == null) return;
        Color c = cents >= 10000 ? new Color(0.12f, 0.12f, 0.15f) : cents >= 5000 ? new Color(0.88f, 0.48f, 0.10f) : cents >= 2500 ? new Color(0.12f, 0.54f, 0.29f)
            : cents >= 1000 ? new Color(0.14f, 0.34f, 0.77f) : cents >= 500 ? new Color(0.78f, 0.125f, 0.17f) : new Color(0.93f, 0.9f, 0.84f);
        for (int i = 0; i < n; i++)
        {
            var g = DemoPrim(PrimitiveType.Cylinder, at, "DemoChip", new Vector3(0, CasinoLayout.ChipThickness * (i + .5f), 0), new Vector3(CasinoLayout.ChipDiameter, CasinoLayout.ChipThickness * .5f, CasinoLayout.ChipDiameter), DemoMat(c));
            keep.Add(g);
            if (i % 2 == 1) keep.Add(DemoPrim(PrimitiveType.Cylinder, g.transform, "Ins", Vector3.zero, new Vector3(1.01f, .55f, 1.01f), DemoMat(new Color(0.9f, 0.9f, 0.85f))));
        }
    }

    static System.Collections.Generic.List<GameObject> DemoDress(CasinoScene cs)
    {
        var keep = new System.Collections.Generic.List<GameObject>();
        foreach (var st in cs.stations)
        {
            if (st == null || st.spots == null) continue;
            switch (st.kind)
            {
                case CasinoStationKind.Blackjack:
                {
                    st.timerLamp.SetValue(0.55f);
                    DemoCard(st.dealerSpots[0], "K", false, false, keep); DemoCard(st.dealerSpots[1], "", false, true, keep); DemoCard(st.dealerSpots[2], "7", true, false, keep);
                    string[][] hands = { new[] { "A", "K" }, new[] { "10", "6" }, new[] { "2", "3", "4", "5", "A" }, new string[0], new[] { "Q", "8" } };
                    int[] bet = { 2500, 5000, 500, 0, 10000 };
                    for (int k = 0; k < st.spots.Length; k++)
                    {
                        var sp = st.spots[k];
                        for (int c = 0; c < hands[k].Length; c++) DemoCard(sp.handSpots[0].cards[c], hands[k][c], (c + k) % 2 == 0, false, keep);
                        if (k == 1) DemoCard(sp.handSpots[1].cards[0], "9", true, false, keep);
                        if (bet[k] > 0) DemoChips(sp.betCircle, bet[k], 2 + k % 3, keep);
                        for (int j = 0; j < sp.chipRack.Length; j++) DemoChips(sp.chipRack[j], CasinoLayout.BlackjackChipCents[j], k == 2 ? 9 - j : 3 + (j + k) % 3, keep);
                    }
                    break;
                }
                case CasinoStationKind.Roulette:
                {
                    for (int k = 0; k < st.spots.Length; k++)
                        for (int j = 0; j < st.spots[k].chipRack.Length; j++) DemoChips(st.spots[k].chipRack[j], CasinoLayout.RouletteChipCents[j], 3 + (j + k) % 5, keep);
                    var a = new GameObject("DemoAt").transform; a.SetParent(st.transform, false); keep.Add(a.gameObject);
                    foreach (var p in new[] { st.LayoutPoint(17), st.LayoutPoint(32), st.LayoutPoint(0), st.LayoutPoint(5.0f, 1.0f), st.LayoutPoint(2.5f, -0.35f), st.LayoutPoint(10.0f, -1.1f) })
                    {
                        var g = new GameObject("DemoStack").transform; g.SetParent(st.transform, false); g.position = p; keep.Add(g.gameObject);
                        DemoChips(g, 1000, 3, keep);
                    }
                    st.PlaceWinMarker(17);
                    st.resultBoard.SetHistory(new[] { 17, 0, 32, 37, 5, 23, 8, 14, 29, 3, 36, 12 });
                    st.timerLamp.SetValue(0.7f);
                    break;
                }
                case CasinoStationKind.Slot:
                {
                    var pp = st.payPlate;
                    pp.SetTitle(st.displayName.ToUpperInvariant());
                    string[] combos = { "7 7 7", "BAR BAR BAR", "BELL x3", "CHERRY x3", "STAR x3", "ANY 3 SAME", "7 7 any", "CHERRY CHERRY", "CHERRY any", "7 any any", "BAR BAR any", "BELL BELL any", "STAR STAR any", "ANY BAR x3" };
                    string[] pays = { "500", "100", "50", "25", "20", "15", "10", "8", "5", "4", "3", "3", "2", "2" };
                    for (int l = 0; l < CasinoPayPlate.Lines; l++) pp.SetLine(l, combos[l], pays[l] + "x");
                    pp.SetReturns("RETURNS 95.0% over the long run");
                    pp.SetStake("STAKE $1");
                    break;
                }
            }
        }
        return keep;
    }
}
