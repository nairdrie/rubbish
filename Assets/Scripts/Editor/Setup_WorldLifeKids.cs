using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// WorldLife kids setup (Docs/WorldLife.md §3.1, §12.K). Idempotent; run by the umbrella <c>Rubbish/Setup/WorldLife</c>
/// after Rules, or on its own. The kids need no assets of their own (the residents' <c>mat/worldlife_res_*</c> materials and
/// the pens NeighborhoodBuilder places), so this only runs <see cref="SelfTest"/> (no scene) and <see cref="SceneCheck"/>
/// (read-only: Neighborhood.unity opened additively, inspected, closed). Plus <b>Rubbish/WorldLife/Render Kids</b> (the
/// lineup and an edit-mode preview of the playground) and <see cref="Kit"/> for play tests. Never saves a scene; never
/// touches Bootstrap.
/// </summary>
public static class Setup_WorldLifeKids
{
    public const string ScenePath = "Assets/Scenes/Neighborhood.unity";

    [MenuItem("Rubbish/Setup/WorldLife Kids")]
    public static void RunMenu() => Run();

    public static string Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[Setup_WorldLifeKids] Exit play mode first.");
            return "ERROR: play mode";
        }
        var sb = new StringBuilder("[Setup_WorldLifeKids]\n");
        sb.Append(SelfTest());
        sb.Append(SceneCheck());
        string report = sb.ToString();
        if (report.Contains("FAIL")) Debug.LogWarning(report); else Debug.Log(report);
        return report;
    }

    // ================================================================================================= self test (no scene)

    [MenuItem("Rubbish/WorldLife/Kids Self Test")]
    public static void SelfTestMenu() => Debug.Log("[Setup_WorldLifeKids]\n" + SelfTest());

    public static string SelfTest()
    {
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(bool ok, string what) { if (ok) pass++; else fail++; sb.AppendLine($"    {(ok ? "PASS" : "FAIL")} {what}"); }

        // ---- never a body: no ILifeBody, no sensor client, nothing in LifeBodies
        Check(!typeof(ILifeBody).IsAssignableFrom(typeof(KidAgent)) && !typeof(ITruckSensorClient).IsAssignableFrom(typeof(KidDirector)),
              "kids are not ILifeBody and the kid director is no sensor client (no rule can hit a kid)");

        // ---- the schedule: pure, after school + the odd morning, never at night
        uint key = LifeHash.Str("playground");
        bool same = true;
        for (int d = 0; d < 10; d++)
            if (KidSchedule.Describe(1234, key, 2, d) != KidSchedule.Describe(1234, key, 2, d)) same = false;
        bool differs = false;
        for (int d = 0; d < 10; d++) if (KidSchedule.Describe(1234, key, 2, d) != KidSchedule.Describe(99, key, 2, d)) differs = true;
        Check(same && differs, "the timetable is a pure function of (Town seed, pen, kid, day): " + KidSchedule.Describe(1234, key, 2, 0) + " on day 0");
        int nightOut = 0, afternoons = 0, afternoonSlots = 0, mornings = 0, morningSlots = 0, kidDays = 0, sampleNight = 0;
        for (int kid = 0; kid < 30; kid++)
            for (int day = 0; day < 40; day++)
            {
                kidDays++;
                uint pk = LifeHash.Str("pen" + (kid % 6));
                for (int m = 0; m < 24 * 60; m += 6)
                {
                    double h = day * 24.0 + m / 60.0;
                    bool o = KidSchedule.IsOut(1234, pk, kid, h);
                    float hod = m / 60f;
                    bool night = hod >= 19f || hod < 8.5f;
                    if (night) { sampleNight++; if (o) nightOut++; }
                    if (Mathf.Approximately(hod, 16f)) { afternoonSlots++; if (o) afternoons++; }
                    if (Mathf.Approximately(hod, 10f)) { morningSlots++; if (o) mornings++; }
                }
            }
        float pa = afternoons / (float)afternoonSlots, pm = mornings / (float)morningSlots;
        Check(nightOut == 0, $"indoors at night: 0 of {sampleNight} samples between 19:00 and 08:30 out ({nightOut})");
        Check(pa > 0.78f && pa < 0.96f, $"out at 16:00 on {pa:P0} of {afternoonSlots} kid-days (~88%)");
        Check(pm > 0.08f && pm < 0.34f, $"out at 10:00 on {pm:P0} of {morningSlots} kid-days (no-school mornings, ~21%)");

        // ---- the fan moment: once per pen per in-game day, host only, the Neighborly fact (+1 Rep)
        var books = new MemoryBooks { hours = 16.0 };
        bool a1 = KidDirector.ClaimFan(books, 0, "maple-7", 0, Vector3.zero);
        bool a2 = KidDirector.ClaimFan(books, 0, "maple-7", 0, Vector3.zero);
        bool b1 = KidDirector.ClaimFan(books, 3, "", 0, Vector3.zero);
        books.hours = 40.0;
        bool a3 = KidDirector.ClaimFan(books, 0, "maple-7", 0, Vector3.zero);
        var guest = new MemoryBooks { host = false, hours = 16.0 };
        bool g1 = KidDirector.ClaimFan(guest, 0, "maple-7", 0, Vector3.zero);
        int facts = books.IncidentCount(NeighborIncidentKind.Neighborly);
        Check(a1 && !a2 && b1 && a3 && !g1 && facts == 3 && guest.incidents.Count == 0,
              $"fan moment: first honk of the day per pen only (pen 0: {a1}/{a2}, pen 3: {b1}, next day: {a3}), a guest books nothing ({g1}); {facts} Neighborly facts");

        // ---- looks: kid-sized, the universal rig (renderers full / lite), no colliders
        var prevScene = SceneManager.GetActiveScene();
        var tmp = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        SceneManager.SetActiveScene(tmp);
        var rootGo = new GameObject("WL_Kids_SelfTest");
        try
        {
            var rig = PuppetRig.Build(rootGo.transform, "KidProbe");
            var spec = new RigSpec();
            int maxFull = 0, minFull = 99, maxLite = 0, minLite = 99;
            float hMin = 9f, hMax = 0f;
            var props = new int[5]; var hats = new int[5];
            for (int i = 0; i < 80; i++)
            {
                var st = KidLook.StyleFor(LifeHash.U64(1234, LifeKind.Kid, i, -1, 7u));
                hMin = Mathf.Min(hMin, st.height); hMax = Mathf.Max(hMax, st.height);
                props[(int)st.prop]++; hats[(int)st.hat]++;
                KidLook.Compose(st, i % 7 == 0, spec);
                rig.Apply(spec);
                rig.SetLite(false);
                int full = Active(rig);
                rig.SetLite(true);
                int lite = Active(rig);
                rig.SetLite(false);
                maxFull = Mathf.Max(maxFull, full); minFull = Mathf.Min(minFull, full);
                maxLite = Mathf.Max(maxLite, lite); minLite = Mathf.Min(minLite, lite);
            }
            Check(hMin >= 0.95f && hMax <= 1.15f, $"kid heights {hMin:F2}–{hMax:F2} m (0.95–1.15; the grown-ups are 1.45–1.85)");
            Check(maxFull <= 24 && minFull >= 14, $"full kid puppets {minFull}–{maxFull} renderers");
            Check(maxLite <= 5 && minLite >= 3, $"lite kid puppets {minLite}–{maxLite} renderers (body, head, nose, hair / hat)");
            Check(rig.go.GetComponentsInChildren<Collider>(true).Length == 0, "kid puppets have no colliders");
            sb.AppendLine($"    props none/balloon/truck/lollipop: {props[0]}/{props[1]}/{props[2]}/{props[3]}; hats none/cap/backwards/propeller/bucket: {hats[0]}/{hats[1]}/{hats[2]}/{hats[3]}/{hats[4]}");
            bool ascii = true;
            foreach (var arr in new[] { KidLook.SignTexts, KidLines.Wave, KidLines.Yay, KidLines.Bye, KidLines.Fans, KidLines.Eek })
                foreach (var s in arr) foreach (char ch in s) if (ch > 126 || (ch < 32 && ch != '\n')) ascii = false;
            Check(ascii, "signs and lines are plain ASCII");

            // ---- the run loop maths (a pen turned 90°, like the playground)
            var penGo = new GameObject("PenProbe");
            var pen = penGo.AddComponent<KidPen>();
            pen.center = new Vector3(58.5f, 0.12f, 106.25f); pen.yaw = 90f; pen.half = new Vector2(6.75f, 13.5f); pen.inset = 0.7f;
            var dice = new PuppetDice(42);
            float worst = 0f; int sideBad = 0;
            for (int i = 0; i < 200; i++)
            {
                float s = dice.Range(0f, pen.LoopLength);
                Vector3 p = pen.LoopPoint(s);
                float back = pen.LoopNearest(p);
                worst = Mathf.Max(worst, Mathf.Abs(pen.LoopDelta(s, back)));
                if (!pen.Inside(p, -pen.inset + 0.01f)) sideBad++;
            }
            Vector3 east = new Vector3(80f, 0f, 104f);
            Vector3 onLoop = pen.LoopPoint(pen.LoopNearest(east));
            Check(worst < 0.01f && sideBad == 0 && Mathf.Abs(onLoop.x - 71.3f) < 0.05f && Mathf.Abs(onLoop.z - 104f) < 0.05f,
                  $"loop: point ↔ arc round trip ≤ {worst:F4} m; a truck on the park path at (80, 104) → the east run line at {onLoop.ToString("F2")}");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(rootGo);
            SceneManager.SetActiveScene(prevScene);
            EditorSceneManager.CloseScene(tmp, true);
        }
        sb.Insert(0, $"  self test: {pass} pass, {fail} fail\n");
        return sb.ToString();
    }

    static int Active(PuppetRig rig)
    {
        int n = 0;
        foreach (var r in rig.r) if (r.gameObject.activeInHierarchy) n++;
        return n;
    }

    // ================================================================================================= scene check (read-only)

    [MenuItem("Rubbish/WorldLife/Kids Scene Check")]
    public static void SceneCheckMenu() => Debug.Log("[Setup_WorldLifeKids]\n" + SceneCheck());

    /// <summary>The pens NeighborhoodBuilder placed: fence boxes, the run loop and home spots clear, the kids' roster and
    /// today's timetable. Opens the scene additively if it isn't loaded, reads it and closes it again.</summary>
    public static string SceneCheck()
    {
        var sb = new StringBuilder();
        var scene = SceneManager.GetSceneByPath(ScenePath);
        bool opened = false;
        if (!scene.IsValid() || !scene.isLoaded) { scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive); opened = true; }
        try
        {
            Physics.SyncTransforms();
            var pens = new List<KidPen>();
            foreach (var r in scene.GetRootGameObjects()) pens.AddRange(r.GetComponentsInChildren<KidPen>(true));
            pens.Sort((a, b) => string.CompareOrdinal(a.penId, b.penId));
            if (pens.Count == 0) { sb.AppendLine("  scene: FAIL no KidPen in Neighborhood.unity (run Rubbish/Setup/Neighborhood)"); return sb.ToString(); }
            int kids = 0, bad = 0;
            foreach (var p in pens)
            {
                kids += p.kids;
                var own = new HashSet<Collider>(p.GetComponents<Collider>());
                int boxes = 0; float minH = 99f, minT = 99f;
                foreach (var b in p.GetComponents<BoxCollider>())
                {
                    if (!b.enabled || b.isTrigger) continue;
                    boxes++;
                    minH = Mathf.Min(minH, b.size.y);
                    minT = Mathf.Min(minT, Mathf.Min(b.size.x, b.size.z));
                }
                int loopBlocked = 0;
                for (float s = 0f; s < p.LoopLength; s += 0.5f)
                {
                    var q = p.LoopPoint(s);
                    foreach (var h in Physics.OverlapCapsule(q + Vector3.up * 0.55f, q + Vector3.up * 1.2f, 0.25f, ~0, QueryTriggerInteraction.Ignore))
                        if (h.gameObject.scene == scene && !own.Contains(h) && h.bounds.max.y > q.y + 0.25f) { loopBlocked++; break; }
                }
                bool ok = boxes == 4 && minH >= 1.5f && minT >= 0.8f && loopBlocked == 0 && p.spots != null && p.spots.Length >= p.kids && p.gameObject.layer == 0;
                if (!ok) bad++;
                sb.AppendLine($"    {(ok ? "ok  " : "FAIL")} pen {p.penId,-16} district {p.district}, {p.kids} kids, fence {p.half.x * 2f:F1}×{p.half.y * 2f:F1} m " +
                              $"({boxes} boxes, {minH:F1} m high, {minT:F2} m thick), loop {p.LoopLength:F1} m (blocked {loopBlocked}), spots {p.spots?.Length ?? 0}; " +
                              $"kid 0 today: {KidSchedule.Describe(1234, LifeHash.Str(p.penId), 0, 0)}");
            }
            sb.Insert(0, $"  scene: {pens.Count} pens, {kids} kids, {bad} bad\n");
        }
        finally { if (opened) EditorSceneManager.CloseScene(scene, true); }
        return sb.ToString();
    }

    // ================================================================================================= renders

    [MenuItem("Rubbish/WorldLife/Render Kids")]
    public static void RenderMenu()
    {
        Debug.Log(RenderLineup("Docs/WorldLifeRefs/kids_lineup.png"));
        Debug.Log(RenderPen("Docs/WorldLifeRefs/kids_playground_preview.png", "playground", new Vector3(86f, 7.5f, 96f), new Vector3(62f, 0.6f, 106.5f), 42f, true));
    }

    /// <summary>A row of kids (the looks, a sign, a cheer and a wave) in a throwaway scene, rendered to a PNG.</summary>
    public static string RenderLineup(string outPath)
    {
        var prev = SceneManager.GetActiveScene();
        var s = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        SceneManager.SetActiveScene(s);
        try
        {
            var lg = new GameObject("Sun"); var l = lg.AddComponent<Light>(); l.type = LightType.Directional; l.intensity = 1.25f;
            lg.transform.rotation = Quaternion.Euler(40f, -35f, 0f);
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.transform.localScale = new Vector3(3f, 1f, 1.5f);
            ground.GetComponent<Renderer>().sharedMaterial = PuppetKit.Lit(new Color(0.42f, 0.62f, 0.32f), 0.1f);
            var root = new GameObject("Kids").transform;
            var spec = new RigSpec();
            const int N = 12;
            var names = new StringBuilder();
            for (int i = 0; i < N; i++)
            {
                var st = KidLook.StyleFor(LifeHash.U64(1234, LifeKind.Kid, i, -1, LifeHash.Str("lineup")));
                bool sign = i == 3 || i == 8;
                if (sign) st.prop = KidProp.Sign;
                var rig = PuppetRig.Build(root, "Kid" + i);
                KidLook.Compose(st, sign, spec);
                rig.Apply(spec);
                var pos = new Vector3(-5.5f + i * 1.0f, 0f, (i % 2) * 0.6f);
                var pose = i == 5 || i == 6 || i == 8 ? Pose.Cheer : i == 2 || i == 10 ? Pose.Wave : Pose.Play;
                PoseKid(rig, st, pos, 180f + (i % 3 - 1) * 12f, pose, sign ? KidLook.SignTexts[i % KidLook.SignTexts.Length] : null, i);
                names.Append($"{st.name} {st.height:F2} m ({st.hairKind}, {st.hat}, {st.prop}); ");
            }
            var camGo = new GameObject("Cam"); var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.58f, 0.74f, 0.9f);
            cam.scene = s; cam.fieldOfView = 30f;
            cam.transform.position = new Vector3(0f, 2.2f, -9.5f);
            cam.transform.LookAt(new Vector3(0f, 0.75f, 0.3f));
            string abs = Save(cam, 2400, 900, outPath);
            return "kids lineup → " + abs + "\n  " + names;
        }
        finally
        {
            SceneManager.SetActiveScene(prev);
            EditorSceneManager.CloseScene(s, true);
        }
    }

    /// <summary>
    /// Edit-mode preview of one pen in the real town: Neighborhood.unity opened additively (refused if it's already open, so
    /// a user's unsaved scene is never touched), the pen's kids posed at their spots (or lined up along the front fence,
    /// cheering), one camera render with the scene's own lighting (no fog), everything temporary destroyed and the scene
    /// closed without saving.
    /// </summary>
    public static string RenderPen(string outPath, string penId, Vector3 camPos, Vector3 look, float fov, bool cheering)
    {
        var scene = SceneManager.GetSceneByPath(ScenePath);
        if (scene.IsValid() && scene.isLoaded) return "RenderPen: Neighborhood.unity is open in the editor; close it first (never renders into a user's scene)";
        scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
        var prevActive = SceneManager.GetActiveScene();
        var temp = new List<GameObject>();
        try
        {
            KidPen pen = null;
            foreach (var r in scene.GetRootGameObjects()) foreach (var p in r.GetComponentsInChildren<KidPen>(true)) if (p.penId == penId) pen = p;
            if (pen == null) return "RenderPen: no pen " + penId;
            var root = new GameObject("TmpKids");
            SceneManager.MoveGameObjectToScene(root, scene);
            temp.Add(root);
            var spec = new RigSpec();
            uint key = LifeHash.Str(pen.penId);
            int signer = pen.sign ? LifeHash.Range(LifeHash.U64(1234, LifeKind.Kid, 0, -1, key ^ 0x51u), 0, pen.kids) : -1;
            float sLook = pen.LoopNearest(camPos);
            for (int k = 0; k < pen.kids; k++)
            {
                ulong h = LifeHash.U64(1234, LifeKind.Kid, k, -1, key);
                var st = KidLook.StyleFor(h);
                bool sign = k == signer;
                if (sign) st.prop = KidProp.Sign;
                var rig = PuppetRig.Build(root.transform, "Kid" + k);
                KidLook.Compose(st, sign, spec);
                rig.Apply(spec);
                Vector3 pos = cheering ? pen.LoopPoint(sLook + (k - (pen.kids - 1) * 0.5f) * WorldLifeTuning.Kids.Spread) : pen.Spot(k);
                pos.y = pen.center.y;
                float yaw = PuppetKit.YawOf(camPos - pos);
                PoseKid(rig, st, pos, yaw, cheering ? (k % 2 == 0 ? Pose.Cheer : Pose.Wave) : Pose.Play,
                        sign ? KidLook.SignTexts[LifeHash.Range(LifeHash.Mix(h ^ 0x5161UL), 0, KidLook.SignTexts.Length)] : null, k);
            }
            var camGo = new GameObject("TmpKidCam");
            SceneManager.MoveGameObjectToScene(camGo, scene);
            temp.Add(camGo);
            var cam = camGo.AddComponent<Camera>();
            cam.scene = scene;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.55f, 0.7f, 0.85f);
            cam.fieldOfView = fov; cam.farClipPlane = 1500f;
            cam.transform.SetPositionAndRotation(camPos, Quaternion.LookRotation(look - camPos));
            SceneManager.SetActiveScene(scene);
            RenderSettings.fog = false;
            string abs = Save(cam, 1600, 900, outPath);
            return $"pen {pen.penId} preview → {abs} ({pen.kids} kids, {(cheering ? "cheering at the fence" : "playing")})";
        }
        finally
        {
            // (The fog switch-off belongs to this throwaway copy of the scene: it's closed without saving, never restored
            // into another scene's RenderSettings.)
            foreach (var g in temp) if (g != null) UnityEngine.Object.DestroyImmediate(g);
            if (prevActive.IsValid() && prevActive.isLoaded) SceneManager.SetActiveScene(prevActive);
            EditorSceneManager.CloseScene(scene, true);
        }
    }

    public enum Pose { Play, Wave, Cheer }

    /// <summary>Static pose for a render: the runtime director's poses frozen (arms, hop, mouth, the balloon above the hand,
    /// a sign held up).</summary>
    public static void PoseKid(PuppetRig rig, in KidStyle st, Vector3 pos, float yaw, Pose pose, string signText, int seed)
    {
        rig.Place(pos, yaw);
        float H = st.height;
        float hop = 0f, alx = -10f, alz = 12f, arx = -10f, arz = 12f, mouth = 0.15f, headP = 0f, headR = 0f;
        switch (pose)
        {
            case Pose.Cheer:
                alx = arx = -168f; alz = 26f; arz = 14f; hop = 0.18f + 0.12f * (seed % 2); mouth = 1f; headP = -10f;
                break;
            case Pose.Wave:
                if (st.WavesLeft) { alx = -160f; alz = 40f; arx = st.prop == KidProp.ToyTruck ? -95f : -35f; arz = 14f; }
                else { arx = -160f; arz = 40f; }
                mouth = 0.6f; headR = 6f; hop = 0.06f;
                break;
            default:
                if (st.prop == KidProp.ToyTruck) { arx = -55f; arz = 4f; }
                if (st.prop == KidProp.Lollipop) { arx = -125f; arz = -12f; }
                break;
        }
        if (signText != null) { float lift = pose == Pose.Cheer ? -168f : -82f; alx = arx = lift; alz = arz = -6f; }
        rig.Body(new Vector3(0f, hop, 0f), Quaternion.identity, pose == Pose.Cheer ? 0.04f : 0f);
        rig.Torso(pose == Pose.Cheer ? -6f : 0f, 0f);
        rig.Head(headP, 0f, headR);
        rig.Arms(alx, alz, arx, arz);
        rig.Legs(hop > 0.05f ? -8f : 0f, hop > 0.05f ? 8f : 0f, hop > 0.05f ? 10f : 0f);
        rig.SetMouth(mouth, pose == Pose.Cheer ? 1.25f : 1f);
        if (st.prop == KidProp.Balloon && signText == null && rig.PartOn(PartSlot.Prop2))
        {
            Vector3 hand = rig.handR.position, top = hand + new Vector3(0.08f, 0.8f, 0.05f);
            Vector3 dir = top - hand; float len = dir.magnitude;
            SetWorld(rig.t[(int)PartSlot.Prop], hand + dir * 0.5f, Quaternion.FromToRotation(Vector3.up, dir / len), new Vector3(0.008f, len * 0.5f, 0.008f));
            SetWorld(rig.t[(int)PartSlot.Prop2], top, Quaternion.identity, new Vector3(0.3f, 0.36f, 0.3f));
        }
        if (st.hat == KidHat.Propeller) rig.t[(int)PartSlot.Hat2].localRotation = Quaternion.Euler(0f, 35f + seed * 40f, 0f);
        if (signText != null)
        {
            var L = rig.layout;
            var sg = PuppetKit.Node(rig.torso, "KidSign");
            var board = PuppetKit.Part(sg, "Board", out var f);
            var ps = new PartSpec { on = true, shape = PuppetKit.Shape.Cube, color = new Color(0.8f, 0.62f, 0.4f), smooth = 0.1f, scale = new Vector3(0.6f, 0.38f, 0.025f), shadow = true };
            PuppetRig.ApplyPart(board, f, in ps);
            var tgo = new GameObject("Text");
            tgo.transform.SetParent(sg, false);
            tgo.transform.localPosition = new Vector3(0f, 0f, 0.016f);
            tgo.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            var t = tgo.AddComponent<TextMeshPro>();
            t.text = signText; t.fontStyle = FontStyles.Bold; t.alignment = TextAlignmentOptions.Center; t.color = new Color(0.1f, 0.08f, 0.08f);
            t.enableAutoSizing = true; t.fontSizeMin = 0.4f; t.fontSizeMax = 2f; t.rectTransform.sizeDelta = new Vector2(0.54f, 0.32f);
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.ForceMeshUpdate();
            bool up = pose == Pose.Cheer;
            sg.localPosition = up ? new Vector3(0f, H - L.hipY + 0.22f, 0.1f) : new Vector3(0f, L.shoulderY - L.hipY - 0.04f * H, L.bodyD * 0.5f + 0.2f);
            sg.localRotation = Quaternion.Euler(up ? 12f : -8f, 0f, 0f);
        }
    }

    static void SetWorld(Transform tr, Vector3 pos, Quaternion rot, Vector3 worldScale)
    {
        tr.SetPositionAndRotation(pos, rot);
        Vector3 ps = tr.parent != null ? tr.parent.lossyScale : Vector3.one;
        tr.localScale = new Vector3(worldScale.x / Mathf.Max(1e-4f, Mathf.Abs(ps.x)), worldScale.y / Mathf.Max(1e-4f, Mathf.Abs(ps.y)), worldScale.z / Mathf.Max(1e-4f, Mathf.Abs(ps.z)));
    }

    static string Save(Camera cam, int w, int h, string outPath)
    {
        var rt = new RenderTexture(w, h, 24) { antiAliasing = 4 };
        cam.targetTexture = rt;
        cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0); tex.Apply();
        string abs = System.IO.Path.IsPathRooted(outPath) ? outPath : System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), outPath);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(abs));
        System.IO.File.WriteAllBytes(abs, tex.EncodeToPNG());
        RenderTexture.active = null; cam.targetTexture = null;
        UnityEngine.Object.DestroyImmediate(rt); UnityEngine.Object.DestroyImmediate(tex);
        return abs;
    }

    // ================================================================================================= play-test kit

    /// <summary>Helpers for the play-test evals (play mode, test data root only).</summary>
    public static class Kit
    {
        public static KidPen Pen(string penId)
        {
            foreach (var p in KidPen.All) if (p != null && p.penId == penId) return p;
            return null;
        }

        /// <summary>One line per kid of a pen: where, mode, pop, cheer, rig.</summary>
        public static string Kids(string penId)
        {
            var d = KidDirector.Instance;
            if (d == null) return "no director";
            var sb = new StringBuilder();
            foreach (var k in d.Kids)
            {
                if (k.pen == null || k.pen.penId != penId) continue;
                sb.AppendLine($"  kid {k.slot} {k.style.name} ({k.style.prop}{(k.holdsSign ? " '" + k.signText.Replace("\n", " ") + "'" : "")}): {k.mode} pop {k.pop:F2} at {k.pos.ToString("F1")} " +
                              $"onLoop {k.onLoop} cheer {k.cheerT:F1} shy {k.shyT:F1} rig {(k.rig == null ? "-" : k.lite ? "lite" : "full")} out(sched) {k.scheduledOut}");
            }
            return sb.ToString();
        }

        /// <summary>LifeBodies standing inside a kid pen (must be 0: kids aren't bodies, and nobody else gets in).</summary>
        public static int BodiesInPens()
        {
            int n = 0;
            var all = LifeBodies.All;
            for (int i = 0; i < all.Count; i++)
                foreach (var p in KidPen.All) if (p != null && p.Inside(all[i].Position)) { n++; break; }
            return n;
        }

        public static void Honk(TruckControllerV2 t) => Setup_WorldLifeRules.Kit.Honk(t);
    }
}
