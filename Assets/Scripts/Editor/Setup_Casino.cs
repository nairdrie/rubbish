// Setup_Casino, part 1: Rubbish/Setup/Casino. Builds Assets/Scenes/Casino.unity (the pocket interior of The Lucky
// Raccoon, Docs/Casino.md §2) and Resources/Casino/CasinoFront.prefab (the street front the runtime places on Coupon
// Street). Idempotent: one root rebuilt each run, assets refilled in place. Never touches the town or Bootstrap.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static partial class Setup_Casino
{
    const float HX = CasinoLayout.HalfX, HZ = CasinoLayout.HalfZ, H = CasinoLayout.Height;
    const string FrontPrefabPath = "Assets/Resources/Casino/CasinoFront.prefab";

    // where things stand (room-local)
    static readonly Vector3 BlackjackAt = new Vector3(3.0f, 0f, 2.3f), RouletteAt = new Vector3(-3.3f, 0f, 2.5f);
    const float SlotX = -10.5f, SlotZ0 = -4.0f, SlotStep = 1.6f;
    const float BarX = 8.6f, BarZ0 = -3.0f, BarZ1 = 5.4f, BarOrderZ = 0.6f;

    static readonly List<CasinoStation> stations = new List<CasinoStation>();
    static readonly List<CasinoStaff.Post> posts = new List<CasinoStaff.Post>();

    [MenuItem("Rubbish/Setup/Casino")]
    public static void Run() => Build();

    public static string Build()
    {
        var log = new StringBuilder("[Setup_Casino] ");
        if (EditorApplication.isPlayingOrWillChangePlaymode) { Debug.LogError("[Setup_Casino] Exit play mode first."); return "play mode"; }

        Scene scene = SceneManager.GetSceneByPath(CasinoLayout.ScenePath);
        bool opened = false;
        try
        {
            BeginAssets();
            props.Clear(); stations.Clear(); posts.Clear();
            PaintTextures();
            DefinePalette();

            if (!scene.IsValid() || !scene.isLoaded)
            {
                if (System.IO.File.Exists(CasinoLayout.ScenePath)) scene = EditorSceneManager.OpenScene(CasinoLayout.ScenePath, OpenSceneMode.Additive);
                else
                {
                    // Unity refuses an additive new scene next to an unsaved untitled one (batch mode always has one).
                    bool untitled = string.IsNullOrEmpty(SceneManager.GetActiveScene().path);
                    if (untitled && !Application.isBatchMode)
                    {
                        Debug.LogError("[Setup_Casino] Save or close the untitled scene first (Unity cannot create Casino.unity next to it).");
                        return "untitled scene";
                    }
                    scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, untitled ? NewSceneMode.Single : NewSceneMode.Additive);
                    EditorSceneManager.SaveScene(scene, CasinoLayout.ScenePath);
                    log.Append("created Casino.unity; ");
                }
                opened = true;
            }
            foreach (var r in scene.GetRootGameObjects()) if (r.name == CasinoLayout.RootName) UnityEngine.Object.DestroyImmediate(r);

            var root = new GameObject(CasinoLayout.RootName);
            SceneManager.MoveGameObjectToScene(root, scene);
            root.transform.position = CasinoLayout.Origin;
            var comp = root.AddComponent<CasinoScene>();
            var interior = Node(root.transform, "Interior");
            comp.interior = interior.gameObject;
            comp.entrance = Node(root.transform, "Entrance", CasinoLayout.EntranceLocal);

            BuildShell(interior);
            BuildCeiling(interior);
            BuildBar(interior);
            BuildBlackjack(interior);
            BuildRoulette(interior);
            BuildSlots(interior);
            BuildDoors(interior);
            BuildDecor(interior);
            BuildAtmosphere(interior);

            var staff = Node(interior, "Staff").gameObject.AddComponent<CasinoStaff>();
            staff.posts = posts.ToArray();
            comp.stations = stations.ToArray();

            BuildFrontPrefab(log);
            EndAssets();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AddToBuildSettings(log);
            log.Append($"{meshCount} meshes, {triCount} triangles, {mats.Count} materials, {stations.Count} stations, {posts.Count} people.");
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            log.Append("FAILED: " + e.Message);
        }
        finally
        {
            if (opened && scene.IsValid() && SceneManager.sceneCount > 1) EditorSceneManager.CloseScene(scene, true);
        }
        AssetDatabase.SaveAssets();
        Debug.Log(log.ToString());
        return log.ToString();
    }

    static void AddToBuildSettings(StringBuilder log)
    {
        var list = EditorBuildSettings.scenes.ToList();
        if (list.Any(s => s.path == CasinoLayout.ScenePath)) { log.Append("in build settings; "); return; }
        list.Add(new EditorBuildSettingsScene(CasinoLayout.ScenePath, true));
        EditorBuildSettings.scenes = list.ToArray();
        log.Append("added to build settings; ");
    }

    // ------------------------------------------------------------------------------------------------ palette

    static void DefinePalette()
    {
        // wood (one grain texture, tinted)
        Def("wood_wall", C(0.36f, 0.235f, 0.15f), 0.32f).tex = "wood";
        Def("wood_dark", C(0.235f, 0.145f, 0.095f), 0.38f).tex = "wood";
        Def("wood_mid", C(0.42f, 0.28f, 0.17f), 0.36f).tex = "wood";
        Def("wood_red", C(0.36f, 0.17f, 0.105f), 0.68f).tex = "wood";
        Def("wood_floor", C(0.28f, 0.19f, 0.125f), 0.42f).tex = "wood";
        // soft goods
        Def("carpet", C(1f, 1f, 1f), 0.04f).tex = "carpet";
        var lr = Def("leather_red", C(0.47f, 0.07f, 0.06f), 0.5f); lr.tex = "leather"; lr.tiling = new Vector2(3f, 3f);
        var lb = Def("leather_black", C(0.10f, 0.085f, 0.08f), 0.5f); lb.tex = "leather"; lb.tiling = new Vector2(4f, 2f);
        Def("felt_plain", FeltGreen, 0.03f);
        Def("felt_blackjack", Color.white, 0.03f).tex = "felt_blackjack";
        Def("felt_roulette", Color.white, 0.03f).tex = "felt_roulette";
        Def("coat", C(0.16f, 0.14f, 0.12f), 0.1f);
        Def("hat", C(0.09f, 0.08f, 0.08f), 0.25f);
        Def("velvet", C(0.42f, 0.03f, 0.05f), 0.1f);
        Def("leather_seat", C(0.42f, 0.065f, 0.055f), 0.55f);
        // walls and ceiling
        Def("plaster_stain", C(0.40f, 0.31f, 0.20f), 0.08f);
        Def("plaster", C(0.56f, 0.48f, 0.35f), 0.12f);
        var tin = Def("tin", C(0.47f, 0.37f, 0.24f), 0.3f, 0.25f); tin.tex = "tin";
        // metal, stone, glass
        Def("brass", C(0.74f, 0.55f, 0.22f), 0.72f, 1f);
        Def("gilt", C(0.60f, 0.45f, 0.19f), 0.5f, 0.85f);
        Def("chrome", C(0.72f, 0.73f, 0.75f), 0.85f, 1f);
        Def("black_iron", C(0.07f, 0.07f, 0.075f), 0.4f, 0.6f);
        Def("ink", C(0.03f, 0.03f, 0.03f), 0.3f);
        Def("frame_inner", C(0.05f, 0.04f, 0.035f), 0.2f);
        Def("mirror", C(0.55f, 0.56f, 0.52f), 0.93f, 0.9f).emission = C(0.05f, 0.045f, 0.035f);
        Def("board_black", C(0.035f, 0.035f, 0.04f), 0.15f);
        Def("leaf", C(0.16f, 0.24f, 0.09f), 0.25f); Def("leaf_dead", C(0.30f, 0.22f, 0.08f), 0.2f); Def("terracotta", C(0.45f, 0.21f, 0.12f), 0.2f);
        Def("fur", C(0.36f, 0.36f, 0.38f), 0.2f); Def("sack", C(0.52f, 0.40f, 0.22f), 0.15f);
        Glow("flame", C(1f, 0.4f, 0.05f), C(2.4f, 0.7f, 0.08f));
        Def("glass_clear", C(0.72f, 0.80f, 0.80f), 0.92f, 0.1f);
        Def("glass_smoke", C(0.22f, 0.2f, 0.17f), 0.9f, 0.1f);
        Def("paper", C(0.9f, 0.88f, 0.8f), 0.1f);
        Def("rubber", C(0.05f, 0.05f, 0.05f), 0.15f);
        Glow("ember", C(1f, 0.4f, 0.1f), C(3f, 0.9f, 0.2f));
        Glow("liquid_amber", C(0.7f, 0.38f, 0.08f), C(0.25f, 0.11f, 0.02f), 0.9f);
        Glow("bottle_amber", C(0.62f, 0.33f, 0.07f), C(0.20f, 0.09f, 0.015f), 0.92f);
        Glow("bottle_green", C(0.10f, 0.36f, 0.16f), C(0.02f, 0.10f, 0.04f), 0.92f);
        Glow("bottle_clear", C(0.70f, 0.78f, 0.76f), C(0.10f, 0.11f, 0.10f), 0.94f);
        Glow("bottle_brown", C(0.30f, 0.15f, 0.06f), C(0.07f, 0.03f, 0.01f), 0.92f);
        Glow("bottle_blue", C(0.12f, 0.25f, 0.55f), C(0.02f, 0.05f, 0.14f), 0.92f);
        Glow("bottle_red", C(0.55f, 0.07f, 0.09f), C(0.13f, 0.015f, 0.02f), 0.92f);
        Def("label_cream", C(0.82f, 0.77f, 0.6f), 0.1f); Def("label_red", C(0.6f, 0.1f, 0.08f), 0.1f); Def("label_gold", C(0.7f, 0.55f, 0.2f), 0.4f, 0.6f);
        // light
        Glow("lamp_amber", C(0.95f, 0.5f, 0.14f), C(1.15f, 0.44f, 0.07f), 0.8f).twoSided = true;
        Glow("lamp_green", C(0.06f, 0.30f, 0.14f), C(0.015f, 0.11f, 0.04f), 0.85f).twoSided = true;
        Glow("lamp_inside", C(1f, 0.92f, 0.7f), C(2.6f, 2.0f, 1.1f), 0.3f).twoSided = true;
        Glow("bulb", C(1f, 0.9f, 0.6f), C(2.6f, 1.8f, 0.7f));
        Glow("neon_red", C(0.3f, 0.03f, 0.02f), C(2.7f, 0.2f, 0.14f));
        Glow("neon_pink", C(0.3f, 0.04f, 0.12f), C(2.5f, 0.3f, 0.95f));
        Glow("neon_blue", C(0.03f, 0.1f, 0.3f), C(0.25f, 0.85f, 2.7f));
        Glow("neon_amber", C(0.3f, 0.14f, 0.02f), C(2.5f, 1.1f, 0.12f));
        Glow("neon_green", C(0.03f, 0.3f, 0.06f), C(0.25f, 2.2f, 0.45f));
        Glow("exit_green", C(0.1f, 0.6f, 0.25f), C(0.2f, 2.2f, 0.6f));
        Glow("payline", C(1f, 0.1f, 0.1f), C(3f, 0.2f, 0.2f));
        Glow("button_lit", C(1f, 0.6f, 0.1f), C(2.5f, 1.2f, 0.2f));
        // pictures
        for (int i = 0; i < PosterCount; i++) Def("poster_" + i, C(0.8f, 0.78f, 0.72f), 0.25f).tex = "poster_" + i;
        Def("clock", Color.white, 0.3f).tex = "clock";
        Def("wheel", Color.white, 0.6f).tex = "wheel";
        for (int i = 0; i < CasinoLayout.SlotCount; i++)
        {
            var reel = Glow("reel_" + i, Color.white, C(0.40f, 0.40f, 0.36f), 0.3f); reel.tex = "reel_" + i; reel.emissionTex = "reel_" + i;
            var belly = Glow("belly_" + i, Color.white, C(1.25f, 1.25f, 1.25f), 0.7f); belly.tex = "belly_" + i; belly.emissionTex = "belly_" + i;
            Color a = SlotAccent[i];
            Glow("topper_" + i, a, new Color(a.r * 1.7f, a.g * 1.7f, a.b * 1.7f), 0.6f);
            Color mc = SlotColor[i];
            Glow("marq_" + i, new Color(mc.r * 0.3f, mc.g * 0.3f, mc.b * 0.3f), new Color(mc.r * 0.16f, mc.g * 0.16f, mc.b * 0.16f), 0.6f);
            Def("cabinet_" + i, SlotColor[i], i == 2 ? 0.7f : 0.55f, i == 2 ? 0.7f : 0.15f);
            Def("knob_" + i, i == 0 ? C(0.05f, 0.05f, 0.05f) : i == 3 ? C(0.9f, 0.2f, 0.05f) : a, 0.8f);
        }
        // chips
        Def("chip_red", C(0.7f, 0.08f, 0.08f), 0.4f); Def("chip_green", C(0.08f, 0.45f, 0.18f), 0.4f); Def("chip_black", C(0.06f, 0.06f, 0.07f), 0.4f);
        Def("chip_blue", C(0.1f, 0.2f, 0.6f), 0.4f); Def("chip_white", C(0.88f, 0.86f, 0.8f), 0.4f);
        Def("ball", C(0.95f, 0.94f, 0.9f), 0.85f);
        // see-through
        var stain = Def("stain", Color.white, 0f); stain.tex = "stain"; stain.blend = Blend.Alpha;
        var haze = Def("haze", C(1f, 0.82f, 0.6f, 0.014f), 0f); haze.tex = "puff"; haze.blend = Blend.Additive; haze.unlit = true; haze.twoSided = true;
        var mote = Def("mote", C(1f, 0.9f, 0.7f, 0.5f), 0f); mote.tex = "puff"; mote.blend = Blend.Additive; mote.particle = true;
        var beam = Def("beam", C(1f, 0.93f, 0.75f, 0.009f), 0f); beam.blend = Blend.Additive; beam.unlit = true; beam.twoSided = true;
        // the street front
        Def("front_board", C(0.20f, 0.03f, 0.04f), 0.35f);
        Def("front_dark", C(0.07f, 0.035f, 0.03f), 0.3f);
        Def("carpet_red", C(0.50f, 0.04f, 0.05f), 0.05f);
        Glow("porthole", C(0.9f, 0.6f, 0.2f), C(1.6f, 0.8f, 0.2f), 0.8f);
        Glow("marquee", C(0.55f, 0.06f, 0.06f), C(0.12f, 0.01f, 0.01f), 0.4f);
        DefineSeatPalette();
    }

    // ------------------------------------------------------------------------------------------------ the room

    /// <summary>One wall in its own frame: X along the wall, Z into the room, origin on the floor at the wall's middle.</summary>
    static void Wall(MB d, float length)
    {
        const MB.Face F5 = MB.Face.XN | MB.Face.XP | MB.Face.YN | MB.Face.YP | MB.Face.ZP;
        float hl = length * .5f;
        d.grainAlong = false;
        d.Box("wood_wall", new Vector3(0, H * .5f, -.05f), new Vector3(length, H, .1f), default, MB.Face.ZP);
        // skirting, wainscot with raised panels, chair rail
        d.grainAlong = true;
        d.Box("wood_dark", new Vector3(0, .09f, .02f), new Vector3(length, .18f, .04f), default, F5);
        d.Box("wood_dark", new Vector3(0, 1.0f, .0125f), new Vector3(length, .12f, .025f), default, F5);
        d.Box("wood_mid", new Vector3(0, 1.09f, .03f), new Vector3(length, .06f, .06f), default, F5);
        d.Box("wood_dark", new Vector3(0, 1.135f, .02f), new Vector3(length, .03f, .04f), default, F5);
        d.grainAlong = false;
        int bays = Mathf.Max(1, Mathf.RoundToInt(length / 0.95f)); float bw = length / bays;
        for (int i = 0; i <= bays; i++)
            d.Box("wood_dark", new Vector3(-hl + i * bw, .56f, .0125f), new Vector3(.1f, .76f, .025f), default, F5);
        for (int i = 0; i < bays; i++)
        {
            float x = -hl + (i + .5f) * bw;
            d.Box("wood_mid", new Vector3(x, .56f, .009f), new Vector3(bw - .22f, .58f, .018f), default, F5);
            d.Box("wood_wall", new Vector3(x, .56f, .016f), new Vector3(bw - .34f, .46f, .012f), default, F5);
        }
        // upper wall: tall panels between slim pilasters
        int tall = Mathf.Max(1, Mathf.RoundToInt(length / 1.9f)); float tw = length / tall;
        for (int i = 0; i <= tall; i++)
            d.Box("wood_dark", new Vector3(-hl + i * tw, 2.02f, .014f), new Vector3(.12f, 1.75f, .028f), default, F5);
        for (int i = 0; i < tall; i++)
        {
            float x = -hl + (i + .5f) * tw;
            foreach (float yy in new[] { 1.22f, 2.82f }) d.Box("wood_dark", new Vector3(x, yy, .008f), new Vector3(tw - .12f, .07f, .016f), default, F5);
            d.Box("wood_mid", new Vector3(x, 2.02f, .006f), new Vector3(tw - .36f, 1.4f, .012f), default, F5);
            d.Box("wood_wall", new Vector3(x, 2.02f, .011f), new Vector3(tw - .44f, 1.32f, .01f), default, F5);
        }
        // picture rail, the nicotine frieze, crown
        d.grainAlong = true;
        d.Box("wood_dark", new Vector3(0, 2.93f, .025f), new Vector3(length, .06f, .05f), default, F5);
        d.Box("plaster_stain", new Vector3(0, 3.2f, .004f), new Vector3(length, .5f, .008f), default, MB.Face.ZP);
        d.Box("wood_dark", new Vector3(0, 3.46f, .03f), new Vector3(length, .07f, .06f), default, F5);
        d.Box("wood_mid", new Vector3(0, 3.515f, .055f), new Vector3(length, .05f, .11f), default, F5);
        d.Box("wood_dark", new Vector3(0, 3.57f, .08f), new Vector3(length, .06f, .16f), default, F5);
        d.grainAlong = false;
    }

    // wall frames: position of the wall's floor midpoint and the yaw that turns local +Z into the room
    static readonly Vector3 WallN = new Vector3(0, 0, HZ), WallS = new Vector3(0, 0, -HZ), WallE = new Vector3(HX, 0, 0), WallW = new Vector3(-HX, 0, 0);
    const float YawN = 180f, YawS = 0f, YawE = 270f, YawW = 90f;

    static void BuildShell(Transform parent)
    {
        var d = new MB { uvScale = 0.9f };
        d.Push(WallN, new Vector3(0, YawN, 0)); Wall(d, HX * 2); d.Pop();
        d.Push(WallS, new Vector3(0, YawS, 0)); Wall(d, HX * 2); d.Pop();
        d.Push(WallE, new Vector3(0, YawE, 0)); Wall(d, HZ * 2); d.Pop();
        d.Push(WallW, new Vector3(0, YawW, 0)); Wall(d, HZ * 2); d.Pop();
        var walls = Emit(parent, "Walls", d);
        ColBox(walls.transform, "ColN", new Vector3(0, H * .5f, HZ + .5f), new Vector3(HX * 2 + 2, H + 2, 1), LayerWalls);
        ColBox(walls.transform, "ColS", new Vector3(0, H * .5f, -HZ - .5f), new Vector3(HX * 2 + 2, H + 2, 1), LayerWalls);
        ColBox(walls.transform, "ColE", new Vector3(HX + .5f, H * .5f, 0), new Vector3(1, H + 2, HZ * 2 + 2), LayerWalls);
        ColBox(walls.transform, "ColW", new Vector3(-HX - .5f, H * .5f, 0), new Vector3(1, H + 2, HZ * 2 + 2), LayerWalls);
        ColBox(walls.transform, "ColCeiling", new Vector3(0, H + .5f, 0), new Vector3(HX * 2 + 2, 1, HZ * 2 + 2), LayerWalls);

        // floor: a border of dark boards round the carpet
        var f = new MB { uvScale = 1.1f };
        const float border = 0.55f;
        float cx = HX - border, cz = HZ - border;
        f.Quad("carpet", new Vector3(-cx, 0, -cz), new Vector3(-cx, 0, cz), new Vector3(cx, 0, cz), new Vector3(cx, 0, -cz), new Rect(0, 0, cx * 2 / 1.25f, cz * 2 / 1.25f));
        f.grainAlong = true;
        f.Box("wood_floor", new Vector3(0, -.05f, HZ - border * .5f), new Vector3(HX * 2, .1f, border), default, MB.Face.YP);
        f.Box("wood_floor", new Vector3(0, -.05f, -HZ + border * .5f), new Vector3(HX * 2, .1f, border), default, MB.Face.YP);
        f.grainAlong = false;
        f.Box("wood_floor", new Vector3(HX - border * .5f, -.05f, 0), new Vector3(border, .1f, cz * 2), default, MB.Face.YP);
        f.Box("wood_floor", new Vector3(-HX + border * .5f, -.05f, 0), new Vector3(border, .1f, cz * 2), default, MB.Face.YP);
        // a brass threshold strip where the carpet meets the boards
        f.Box("brass", new Vector3(0, .004f, cz), new Vector3(cx * 2, .008f, .03f)); f.Box("brass", new Vector3(0, .004f, -cz), new Vector3(cx * 2, .008f, .03f));
        f.Box("brass", new Vector3(cx, .004f, 0), new Vector3(.03f, .008f, cz * 2)); f.Box("brass", new Vector3(-cx, .004f, 0), new Vector3(.03f, .008f, cz * 2));
        var floor = Emit(parent, "Floor", f, default, 0, true, false, LayerGround);
        var fc = floor.AddComponent<BoxCollider>(); fc.center = new Vector3(0, -.5f, 0); fc.size = new Vector3(HX * 2 + 2, 1, HZ * 2 + 2);

        // what forty years of spilled drinks leave behind
        var s = new MB();
        var spots = new[] { new Vector3(6.9f, 0, 1.4f), new Vector3(7.1f, 0, -1.2f), new Vector3(-8.9f, 0, -1.6f), new Vector3(1.2f, 0, .2f), new Vector3(-4.4f, 0, -4.2f), new Vector3(3.6f, 0, -3.1f), new Vector3(-1.2f, 0, 4.4f) };
        for (int i = 0; i < spots.Length; i++)
        {
            float r = 0.5f + Hash01(i, 77) * 0.7f, rot = Hash01(i, 5) * 360f;
            s.Push(spots[i] + Vector3.up * (0.003f + i * 0.0004f), new Vector3(0, rot, 0));
            s.Quad("stain", new Vector3(-r, 0, -r * .8f), new Vector3(-r, 0, r * .8f), new Vector3(r, 0, r * .8f), new Vector3(r, 0, -r * .8f));
            s.Pop();
        }
        Emit(parent, "Stains", s);
    }

    static void BuildCeiling(Transform parent)
    {
        var d = new MB { uvScale = 1f };
        d.Quad("tin", new Vector3(-HX, H, HZ), new Vector3(-HX, H, -HZ), new Vector3(HX, H, -HZ), new Vector3(HX, H, HZ), new Rect(0, 0, HX * 2 / 0.6875f, HZ * 2 / 0.7f));
        // coffers: 8 x 6 bays of dark beams
        d.grainAlong = true;
        for (int i = 1; i < 8; i++) d.Box("wood_dark", new Vector3(-HX + i * HX * 2 / 8f, H - .09f, 0), new Vector3(.22f, .18f, HZ * 2), default, MB.Face.Sides | MB.Face.YN);
        d.grainAlong = false;
        d.Push(Vector3.zero, new Vector3(0, 90, 0));
        d.grainAlong = true;
        for (int i = 1; i < 6; i++) d.Box("wood_dark", new Vector3(-HZ + i * HZ * 2 / 6f, H - .08f, 0), new Vector3(.22f, .16f, HX * 2), default, MB.Face.Sides | MB.Face.YN);
        d.Pop(); d.grainAlong = false;
        // the plaster medallion: rings and a ring of beads, hung upside down from the ceiling
        d.Push(new Vector3(0, H - .17f, 0), new Vector3(180, 0, 0));
        d.Lathe("plaster", Vector3.zero, new[] { V(1.25f, -.17f), V(1.25f, -.02f), V(1.16f, 0), V(1.08f, -.03f), V(.98f, .0f), V(.9f, .03f), V(.74f, .0f), V(.6f, .04f), V(.44f, .01f), V(.3f, .07f), V(.16f, .05f), V(.1f, .12f), V(0, .13f) }, 40);
        for (int i = 0; i < 24; i++) { float a = i * 15f * Mathf.Deg2Rad; d.Sphere("plaster", new Vector3(Mathf.Sin(a) * .82f, .02f, Mathf.Cos(a) * .82f), new Vector3(.045f, .03f, .045f), 6, 3, 0, 90); }
        for (int i = 0; i < 12; i++) { float a = i * 30f * Mathf.Deg2Rad; d.Sphere("plaster", new Vector3(Mathf.Sin(a) * .52f, .03f, Mathf.Cos(a) * .52f), new Vector3(.06f, .035f, .11f), 6, 3, 0, 90); }
        d.Pop();
        Emit(parent, "Ceiling", d);

        // the chandelier: a brass stem, six arms, six little amber shades
        var c = new MB();
        float top = H - .3f, y0 = 2.62f;
        c.Lathe("brass", new Vector3(0, y0, 0), new[] { V(0, 0), V(.03f, .01f), V(.07f, .06f), V(.05f, .12f), V(.02f, .16f), V(.016f, top - y0 - .12f), V(.05f, top - y0 - .06f), V(.09f, top - y0), V(0, top - y0) }, 12);
        c.Sphere("brass", new Vector3(0, y0 - .03f, 0), Vector3.one * .045f, 10, 6);
        for (int i = 0; i < 6; i++)
        {
            float a = i * 60f * Mathf.Deg2Rad; Vector3 dir = new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a));
            var arm = new List<Vector3>();
            for (int k = 0; k <= 8; k++) { float t = k / 8f; arm.Add(dir * (0.04f + t * .5f) + Vector3.up * (y0 + .1f - Mathf.Sin(t * Mathf.PI) * .16f + t * .06f)); }
            c.Tube("brass", arm, .011f, 6);
            Vector3 tip = dir * .54f + Vector3.up * (y0 + .16f);
            c.Lathe("brass", tip, new[] { V(0, 0), V(.045f, 0), V(.05f, .012f), V(.015f, .02f), V(0, .02f) }, 8);
            c.Lathe("lamp_amber", tip + Vector3.up * .02f, new[] { V(.03f, 0), V(.07f, .03f), V(.085f, .09f), V(.075f, .15f), V(.06f, .17f) }, 10);
            c.Sphere("bulb", tip + Vector3.up * .09f, new Vector3(.022f, .032f, .022f), 6, 4);
        }
        Emit(parent, "Chandelier", c);
        AddLight(parent, "Chandelier light", new Vector3(0, 2.5f, 0), C(1f, 0.82f, 0.6f), 16f, 12.5f);
    }

    // ------------------------------------------------------------------------------------------------ the bar

    /// <summary>The bar's centreline, from the east wall round the corner and up the room (customers on the left of travel).</summary>
    static List<Vector2> BarLine()
    {
        var l = new List<Vector2> { new Vector2(HX - .05f, BarZ0) };
        const float r = 0.95f;
        for (int i = 0; i <= 10; i++)
        {
            float a = Mathf.Lerp(-90f, -180f, i / 10f) * Mathf.Deg2Rad; // from pointing south to pointing west, centre NE of the corner
            l.Add(new Vector2(BarX + r + Mathf.Cos(a) * r, BarZ0 + r + Mathf.Sin(a) * r));
        }
        l.Add(new Vector2(BarX, BarZ1));
        return l;
    }

    /// <summary>A band of constant half-width around a centreline (outline for a Prism), and the two offset lines.</summary>
    static List<Vector2> Band(List<Vector2> line, float left, float right, out List<Vector2> leftLine, out List<Vector2> rightLine)
    {
        leftLine = new List<Vector2>(); rightLine = new List<Vector2>();
        for (int i = 0; i < line.Count; i++)
        {
            Vector2 a = line[Mathf.Max(i - 1, 0)], b = line[Mathf.Min(i + 1, line.Count - 1)], t = (b - a).normalized;
            var n = new Vector2(-t.y, t.x); // left of travel
            leftLine.Add(line[i] + n * left); rightLine.Add(line[i] - n * right);
        }
        var o = new List<Vector2>(leftLine);
        for (int i = rightLine.Count - 1; i >= 0; i--) o.Add(rightLine[i]);
        return o;
    }

    static List<Vector3> Lift(List<Vector2> l, float y) { var o = new List<Vector3>(); foreach (var p in l) o.Add(new Vector3(p.x, y, p.y)); return o; }

    static void BuildBar(Transform parent)
    {
        var bar = Node(parent, "Bar");
        var line = BarLine();
        var d = new MB { uvScale = 1.2f };
        d.Prism("rubber", "rubber", Band(line, .27f, .27f, out _, out _), 0f, .1f);
        d.Prism("wood_dark", "wood_dark", Band(line, .31f, .31f, out var frontLine, out _), .1f, 1.03f);
        d.grainAlong = true;
        d.Prism("wood_red", "wood_red", Band(line, .43f, .36f, out var lipLine, out _), 1.03f, 1.09f, null, true);
        d.grainAlong = false;
        d.Tube("brass", Lift(lipLine, 1.06f), .022f, 8);                      // the bar-top edge
        Band(line, .56f, 0, out var railLine, out _);
        d.Tube("brass", Lift(railLine, .2f), .026f, 8);                       // the foot rail
        for (int i = 0; i < railLine.Count; i += i < 2 || i > railLine.Count - 3 ? 1 : 3)
            d.Tube("brass", new[] { new Vector3(frontLine[i].x, .2f, frontLine[i].y), new Vector3(railLine[i].x, .2f, railLine[i].y) }, .014f, 6);
        // raised panels and brass studs along the customer face of the long run
        for (float z = BarZ0 + 1.35f; z < BarZ1 - .4f; z += 1.05f)
        {
            d.Box("wood_mid", new Vector3(BarX - .318f, .56f, z), new Vector3(.02f, .68f, .86f));
            d.Box("wood_red", new Vector3(BarX - .33f, .56f, z), new Vector3(.02f, .52f, .7f));
            d.Sphere("brass", new Vector3(BarX - .34f, .56f, z), Vector3.one * .025f, 8, 5);
        }
        for (float x = BarX + 1.2f; x < HX - .3f; x += 1.0f)
        {
            d.Box("wood_mid", new Vector3(x, .56f, BarZ0 - .318f), new Vector3(.82f, .68f, .02f));
            d.Box("wood_red", new Vector3(x, .56f, BarZ0 - .33f), new Vector3(.66f, .52f, .02f));
        }
        // the staff flap at the far end
        d.Box("wood_dark", new Vector3(BarX, .52f, (BarZ1 + HZ) * .5f), new Vector3(.05f, .92f, HZ - BarZ1 - .06f));
        d.Box("wood_red", new Vector3(BarX, 1.0f, (BarZ1 + HZ) * .5f), new Vector3(.3f, .04f, HZ - BarZ1 - .02f));
        var body = Emit(bar, "Counter", d, default, 0, true, true);
        ColBox(body.transform, "ColLong", new Vector3(BarX, .55f, (BarZ0 + BarZ1) * .5f + .2f), new Vector3(.82f, 1.1f, BarZ1 - BarZ0 - .2f));
        ColBox(body.transform, "ColReturn", new Vector3((BarX + HX) * .5f + .1f, .55f, BarZ0), new Vector3(HX - BarX + .5f, 1.1f, .82f));
        ColBox(body.transform, "ColFlap", new Vector3(BarX, .55f, (BarZ1 + HZ) * .5f), new Vector3(.3f, 1.1f, HZ - BarZ1));

        // the back-bar: cabinets, a foxed mirror, three shelves of bottles, turned columns and a cornice
        const float bz0 = -2.2f, bz1 = 5.0f, bx = HX - .28f;
        var b = new MB { uvScale = 1.2f };
        b.Box("wood_dark", new Vector3(bx, .47f, (bz0 + bz1) * .5f), new Vector3(.56f, .94f, bz1 - bz0));
        b.grainAlong = true;
        b.Box("wood_red", new Vector3(bx - .02f, .96f, (bz0 + bz1) * .5f), new Vector3(.62f, .05f, bz1 - bz0 + .06f));
        b.grainAlong = false;
        int doors = 8; float dw = (bz1 - bz0) / doors;
        for (int i = 0; i < doors; i++)
        {
            float z = bz0 + (i + .5f) * dw;
            b.Box("wood_mid", new Vector3(bx - .285f, .48f, z), new Vector3(.02f, .72f, dw - .1f));
            b.Box("wood_wall", new Vector3(bx - .295f, .48f, z), new Vector3(.02f, .58f, dw - .24f));
            b.Sphere("brass", new Vector3(bx - .31f, .62f, z + dw * .5f - .1f), Vector3.one * .018f, 6, 4);
        }
        b.Box("mirror", new Vector3(HX - .03f, 1.95f, (bz0 + bz1) * .5f), new Vector3(.02f, 1.7f, bz1 - bz0 - .1f));
        foreach (float y in new[] { 1.38f, 1.82f, 2.26f })
        {
            b.Box("wood_red", new Vector3(HX - .14f, y, (bz0 + bz1) * .5f), new Vector3(.24f, .028f, bz1 - bz0 - .12f));
            b.Box("brass", new Vector3(HX - .255f, y + .03f, (bz0 + bz1) * .5f), new Vector3(.008f, .012f, bz1 - bz0 - .16f));
            b.Box("lamp_inside", new Vector3(HX - .05f, y + .02f, (bz0 + bz1) * .5f), new Vector3(.012f, .012f, bz1 - bz0 - .2f)); // a light strip behind each row of bottles
        }
        var colProfile = new[] { V(0, 0), V(.085f, 0), V(.085f, .1f), V(.06f, .13f), V(.055f, .16f), V(.065f, .19f), V(.052f, .24f), V(.045f, 1.3f), V(.055f, 1.36f), V(.045f, 1.4f), V(.07f, 1.45f), V(.09f, 1.5f), V(.09f, 1.58f), V(0, 1.58f) };
        foreach (float z in new[] { bz0, bz0 + (bz1 - bz0) / 3f, bz0 + (bz1 - bz0) * 2f / 3f, bz1 })
            b.Lathe("wood_red", new Vector3(HX - .2f, 1.0f, z), colProfile, 12);
        b.grainAlong = true;
        b.Box("wood_dark", new Vector3(HX - .17f, 2.66f, (bz0 + bz1) * .5f), new Vector3(.34f, .16f, bz1 - bz0 + .24f));
        b.Box("wood_red", new Vector3(HX - .2f, 2.77f, (bz0 + bz1) * .5f), new Vector3(.42f, .07f, bz1 - bz0 + .34f));
        b.grainAlong = false;
        Emit(bar, "Back bar", b, default, 0, true, true);
        ColBox(bar, "ColBackBar", new Vector3(bx, 1.4f, (bz0 + bz1) * .5f), new Vector3(.6f, 2.8f, bz1 - bz0));

        var bottles = new MB();
        int n = 0;
        foreach (float y in new[] { .985f, 1.394f, 1.834f, 2.274f })
        {
            bool counter = y < 1f;
            for (float z = bz0 + .22f; z < bz1 - .15f; z += counter ? .31f : .145f)
            {
                n++;
                if (Hash01(n, 3) < (counter ? 0.45f : 0.16f)) continue;                // gaps: it is not a well-stocked bar
                if (counter && Mathf.Abs(z - 1.3f) < .5f) continue;                    // the till
                float x = HX - (counter ? .2f : .13f) - Hash01(n, 9) * .05f;
                Bottle(bottles, new Vector3(x, y, z + (Hash01(n, 4) - .5f) * .04f), (int)(Hash01(n, 1) * 6f), (int)(Hash01(n, 2) * 6f), 0.92f + Hash01(n, 6) * .2f);
            }
        }
        Bottle(bottles, new Vector3(BarX + .05f, 1.09f, 2.05f), 0, 0, 1f); Bottle(bottles, new Vector3(BarX + .12f, 1.09f, -1.0f), 3, 1, 1f);
        Emit(bar, "Bottles", bottles);

        // the till, the taps, and what is left on the counter
        var t = new MB();
        t.Push(new Vector3(HX - .3f, .985f, 1.3f), new Vector3(0, 270, 0));
        t.Box("brass", new Vector3(0, .1f, 0), new Vector3(.4f, .2f, .38f));
        t.Box("brass", new Vector3(0, .26f, -.08f), new Vector3(.38f, .14f, .2f));
        t.Box("black_iron", new Vector3(0, .215f, .1f), new Vector3(.34f, .02f, .2f), new Vector3(-28, 0, 0));
        for (int r = 0; r < 4; r++) for (int k = 0; k < 6; k++)
            t.Lathe(r == 0 ? "label_red" : "label_cream", new Vector3(-.13f + k * .052f, .195f + r * .022f, .16f - r * .042f), new[] { V(0, 0), V(.016f, 0), V(.016f, .012f), V(0, .012f) }, 6, new Vector3(-28, 0, 0));
        t.Box("glass_smoke", new Vector3(0, .37f, -.08f), new Vector3(.3f, .08f, .06f));
        t.Tube("brass", new[] { new Vector3(.2f, .12f, 0), new Vector3(.26f, .12f, 0), new Vector3(.26f, .2f, .06f) }, .01f, 6);
        t.Sphere("ink", new Vector3(.26f, .21f, .07f), Vector3.one * .02f, 6, 4);
        t.Pop();
        // beer taps: a brass column with three handles
        Vector3 tap = new Vector3(BarX + .1f, 1.09f, 3.0f);
        t.Lathe("brass", tap, new[] { V(0, 0), V(.07f, 0), V(.07f, .015f), V(.035f, .03f), V(.03f, .3f), V(.04f, .32f), V(0, .33f) }, 12);
        t.Box("brass", tap + new Vector3(0, .31f, 0), new Vector3(.07f, .06f, .44f));
        for (int i = -1; i <= 1; i++)
        {
            Vector3 p = tap + new Vector3(-.035f, .3f, i * .15f);
            t.Tube("brass", new[] { p, p + new Vector3(-.06f, -.02f, 0), p + new Vector3(-.07f, -.1f, 0) }, .011f, 6);
            t.Lathe(i == 0 ? "label_red" : i < 0 ? "ink" : "label_cream", p + new Vector3(-.03f, .03f, 0), new[] { V(0, 0), V(.012f, 0), V(.022f, .1f), V(.016f, .16f), V(0, .17f) }, 8, new Vector3(0, 0, 12f));
        }
        t.Box("rubber", new Vector3(BarX - .12f, 1.094f, 3.0f), new Vector3(.2f, .008f, .5f));
        // cocktail shaker, an ice bucket, a bar mat, a bowl of something
        t.Lathe("chrome", new Vector3(BarX + .14f, 1.09f, .15f), new[] { V(0, 0), V(.036f, 0), V(.046f, .15f), V(.046f, .15f), V(.03f, .19f), V(.022f, .2f), V(.022f, .235f), V(0, .24f) }, 12);
        t.Lathe("chrome", new Vector3(BarX + .1f, 1.09f, 1.05f), new[] { V(0, 0), V(.07f, 0), V(.09f, .13f), V(.082f, .13f), V(.065f, .01f), V(0, .01f) }, 14);
        t.Box("rubber", new Vector3(BarX - .1f, 1.094f, BarOrderZ), new Vector3(.24f, .008f, .46f));
        t.Lathe("wood_mid", new Vector3(BarX - .15f, 1.09f, -2.0f), new[] { V(0, 0), V(.04f, 0), V(.075f, .04f), V(.068f, .04f), V(.036f, .008f), V(0, .008f) }, 12);
        Emit(bar, "Bar things", t);
        Place(bar, "tumbler", TumblerRecipe, new Vector3(BarX - .18f, 1.09f, -1.7f));
        Place(bar, "tumbler", TumblerRecipe, new Vector3(BarX - .2f, 1.09f, 4.45f));
        Place(bar, "tumbler", TumblerRecipe, new Vector3(BarX + .2f, 1.09f, .5f));
        Place(bar, "ashtray", AshtrayRecipe, new Vector3(BarX - .2f, 1.09f, -2.3f), 40); // (out of the way of the menu cards)
        Place(bar, "ashtray", AshtrayRecipe, new Vector3(BarX - .15f, 1.09f, 3.8f), 200);

        // five stools along the long run
        float[] stoolZ = { -1.8f, -.2f, 1.4f, 3.0f, 4.6f };
        for (int i = 0; i < stoolZ.Length; i++)
        {
            var s = Place(bar, "stool", StoolRecipe, new Vector3(BarX - .9f, 0, stoolZ[i]), i * 37f, true, true);
            ColCapsule(s.transform, "Col", new Vector3(0, .4f, 0), .2f, .8f);
        }
        posts.Add(new CasinoStaff.Post { label = "Regular at the bar", role = CasinoRole.Regular, pose = CasinoRegularPose.BarStool, seated = true, seed = 3,
            anchor = Node(bar, "Post regular A", new Vector3(BarX - .9f, StoolSeat, stoolZ[0]), new Vector3(0, 90, 0)) });
        posts.Add(new CasinoStaff.Post { label = "Regular asleep", role = CasinoRole.Regular, pose = CasinoRegularPose.AsleepOnBar, seated = true, seed = 8,
            anchor = Node(bar, "Post regular asleep", new Vector3(BarX - .86f, StoolSeat, stoolZ[4]), new Vector3(0, 90, 0)) });
        posts.Add(new CasinoStaff.Post { label = "Bartender", role = CasinoRole.Bartender, seed = 1,
            anchor = Node(bar, "Post bartender", new Vector3(BarX + 1.05f, 0, BarOrderZ + .1f), new Vector3(0, 270, 0)) });

        // three amber pendants over the counter
        float[] pz = { -1.4f, 1.3f, 4.0f };
        for (int i = 0; i < pz.Length; i++)
        {
            Place(bar, "pendant_amber", m => Pendant(m, 1.15f, "lamp_amber"), new Vector3(BarX + .02f, H - .16f, pz[i]));
            AddLight(bar, "Pendant light " + i, new Vector3(BarX + .02f, H - 1.42f, pz[i]), C(1f, 0.72f, 0.42f), 7f, 5.4f);
        }
        AddLight(bar, "Back bar light", new Vector3(HX - .8f, 1.7f, 1.4f), C(1f, 0.86f, 0.66f), 5f, 5.5f);
        AddLight(bar, "Back bar light 2", new Vector3(HX - .8f, 1.7f, 4.0f), C(1f, 0.86f, 0.66f), 3f, 4f);

        // COCKTAILS, in red tube with a blue border, on the frieze above the back-bar
        var neon = new MB();
        Vector3 nc = new Vector3(HX - .09f, 3.2f, 1.4f);
        NeonText(neon, "neon_red", "COCKTAILS", nc, Vector3.back, Vector3.up, .3f, .013f);
        var red = Emit(bar, "Neon COCKTAILS", neon, default, 0, false);
        var border = new MB();
        float nw = NeonWidth("COCKTAILS", .3f) * .5f + .16f;
        border.Tube("neon_blue", new[] { nc + new Vector3(0, .25f, -nw), nc + new Vector3(0, .25f, nw), nc + new Vector3(0, -.25f, nw), nc + new Vector3(0, -.25f, -nw) }, .011f, 6, true);
        var blue = Emit(bar, "Neon border", border, default, 0, false);
        var back = new MB();
        back.Box("front_dark", nc + new Vector3(.05f, 0, 0), new Vector3(.03f, .62f, nw * 2 + .14f));
        Emit(bar, "Neon backing", back);
        var glow = AddLight(bar, "Neon COCKTAILS light", nc + new Vector3(-.5f, -.1f, 0), C(1f, 0.3f, 0.26f), 2f, 4.5f);
        var cn = red.AddComponent<CasinoNeon>(); cn.renderers = new[] { red.GetComponent<Renderer>() }; cn.emission = C(2.7f, 0.2f, 0.14f); cn.glow = glow; cn.glowIntensity = 2f; cn.flicker = .45f; cn.seed = 4;
        var cb = blue.AddComponent<CasinoNeon>(); cb.renderers = new[] { blue.GetComponent<Renderer>() }; cb.emission = C(0.25f, 0.85f, 2.7f); cb.flicker = .1f; cb.seed = 9;

        // the station
        var st = Node(bar, "Station Bar", new Vector3(BarX, 0, BarOrderZ)).gameObject.AddComponent<CasinoStation>();
        st.kind = CasinoStationKind.Bar; st.index = 0; st.displayName = "The bar";
        st.view = LookNode(st.transform, "View", new Vector3(-1.9f, 1.75f, -.9f), new Vector3(.6f, 1.2f, .15f));
        BarSeats(bar, st); // seat mode: three spots along the counter, a menu card in front of each (sets seat, counterSpot, pourSpot)
        Zone(st, new Vector3(-.75f, 1.1f, 0), new Vector3(1.3f, 2.2f, 4.6f), 1.4f);
        stations.Add(st);
    }

    static Transform LookNode(Transform parent, string name, Vector3 localPos, Vector3 localTarget)
    {
        var t = Node(parent, name, localPos);
        t.localRotation = Quaternion.LookRotation((localTarget - localPos).normalized, Vector3.up);
        return t;
    }

    static void Zone(MonoBehaviour target, Vector3 center, Vector3 size, float score = 1.2f)
    {
        var z = target.gameObject.AddComponent<InteractionZone>();
        // 0.3 m of slack below the floor and above the head: a player standing exactly on the zone's lower face is still in it.
        z.center = center; z.size = size + new Vector3(0f, .6f, 0f); z.mode = ZoneMode.OnFoot; z.score = score; z.targetOverride = target;
    }
}
