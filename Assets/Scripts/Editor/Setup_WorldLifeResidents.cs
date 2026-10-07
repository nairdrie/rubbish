using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// WorldLife package C setup (Docs/WorldLife.md §3.1, §12.1). Idempotent; run by the umbrella <c>Rubbish/Setup/WorldLife</c>
/// (Graph → Knock → Residents), or on its own. C needs no scene changes (puppets are pooled at runtime; the Bluffs'
/// static butlers are hidden at runtime and restored on sleep), so this only:
/// <list type="number">
/// <item>Creates the base materials <c>Assets/Materials/Generated/WorldLife/Residents/worldlife_res_{lit,emissive,unlit,beam}.mat</c>
/// and registers them in RubbishAssets as <c>mat/worldlife_res_*</c> (so builds keep the shader variants; the puppets
/// clone one material per colour from them at runtime).</item>
/// <item>Runs <see cref="SelfTest"/> (no scene): cast determinism, every archetype's rig (renderer counts full / lite, no
/// colliders), walk routes for every house, the flop arc.</item>
/// </list>
/// Plus <b>Rubbish/WorldLife/Render Resident Lineup</b> (Docs/WorldLifeRefs/residents_lineup.png) and <see cref="Kit"/>,
/// the play-test helpers the acceptance evals use. Never saves a scene; never touches Bootstrap.
/// </summary>
public static class Setup_WorldLifeResidents
{
    public const string MatDir = "Assets/Materials/Generated/WorldLife/Residents";

    [MenuItem("Rubbish/Setup/WorldLife Residents")]
    public static void RunMenu() => Run();

    public static string Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[Setup_WorldLifeResidents] Exit play mode first.");
            return "ERROR: play mode";
        }
        var sb = new StringBuilder("[Setup_WorldLifeResidents]\n");
        sb.Append(EnsureMaterials());
        AssetDatabase.SaveAssets();
        sb.Append(SelfTest());
        string report = sb.ToString();
        if (report.Contains("FAIL")) Debug.LogWarning(report); else Debug.Log(report);
        return report;
    }

    // ================================================================================================= materials

    public static string EnsureMaterials()
    {
        EnsureFolder(MatDir);
        var lit = Shader.Find("Universal Render Pipeline/Lit");
        var unlit = Shader.Find("Universal Render Pipeline/Unlit");
        if (lit == null || unlit == null) return "  materials: FAIL no URP Lit / Unlit shader\n";
        int made = 0;
        var mLit = GetOrCreate("lit", lit, ref made);
        mLit.SetColor("_BaseColor", Color.white); mLit.SetFloat("_Smoothness", 0.25f); mLit.SetFloat("_Metallic", 0f);
        var mEm = GetOrCreate("emissive", lit, ref made);
        mEm.SetColor("_BaseColor", Color.white); mEm.EnableKeyword("_EMISSION"); mEm.SetColor("_EmissionColor", Color.white * 0.5f);
        mEm.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        var mUn = GetOrCreate("unlit", unlit, ref made);
        mUn.SetColor("_BaseColor", Color.white);
        var mBeam = GetOrCreate("beam", unlit, ref made);
        PuppetKit.MakeTransparent(mBeam);
        mBeam.SetColor("_BaseColor", new Color(1f, 0.95f, 0.6f, 0.2f));
        foreach (var m in new[] { mLit, mEm, mUn, mBeam }) EditorUtility.SetDirty(m);
        RubbishAssets.EditorRegisterMaterial(PuppetKit.LitKey, mLit);
        RubbishAssets.EditorRegisterMaterial(PuppetKit.EmissiveKey, mEm);
        RubbishAssets.EditorRegisterMaterial(PuppetKit.UnlitKey, mUn);
        RubbishAssets.EditorRegisterMaterial(PuppetKit.BeamKey, mBeam);
        return $"  materials: 4 registered as mat/worldlife_res_* ({made} new)\n";
    }

    static Material GetOrCreate(string name, Shader sh, ref int made)
    {
        string path = $"{MatDir}/worldlife_res_{name}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m != null) return m;
        m = new Material(sh) { name = "worldlife_res_" + name };
        AssetDatabase.CreateAsset(m, path);
        made++;
        return m;
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
    }

    // ================================================================================================= self test

    [MenuItem("Rubbish/WorldLife/Residents Self Test")]
    public static void SelfTestMenu() => Debug.Log("[Setup_WorldLifeResidents]\n" + SelfTest());

    public static string SelfTest()
    {
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(bool ok, string what) { if (ok) pass++; else fail++; sb.AppendLine($"    {(ok ? "PASS" : "FAIL")} {what}"); }

        // ---- the cast: deterministic, sizes per district
        var test = StreetGraph.BuildTest();
        ResidentCast.Ensure(test, 1234);
        ulong f1 = ResidentCast.Fingerprint();
        ResidentCast.Ensure(test, 99);
        ulong fOther = ResidentCast.Fingerprint();
        ResidentCast.Ensure(test, 1234);
        ulong f2 = ResidentCast.Fingerprint();
        Check(f1 == f2 && f1 != fOther, $"cast is a pure function of (graph, seed): {f1:X16} twice; seed 99 differs ({fOther:X16})");
        UnityEngine.Object.DestroyImmediate(test);

        var real = AssetDatabase.LoadAssetAtPath<StreetGraph>(StreetGraph.AssetPath);
        if (real != null)
        {
            ResidentCast.Ensure(real, 1234);
            sb.AppendLine("    cast (real graph, seed 1234): " + ResidentCast.Describe());
            int hoa = 0, pem = 0, butlers = 0, kids = 0;
            bool sizesOk = true;
            for (int h = 0; h < real.houses.Length; h++)
            {
                var hh = ResidentCast.HouseholdOf(h);
                var hs = real.houses[h];
                int lo, hi;
                switch (hs.district) { case 1: lo = 2; hi = 4; break; case 2: lo = hi = 3; break; case 3: lo = 2; hi = 3; break;
                    default: lo = hs.size == 0 ? 1 : 2; hi = hs.size == 0 ? 2 : hs.size == 1 ? 3 : 4; break; }
                if (hh.count < lo || hh.count > hi) sizesOk = false;
            }
            for (int i = 0; i < ResidentCast.Count; i++)
            {
                var m = ResidentCast.Get(i);
                if (m.archetype == ResidentArchetype.HoaLady) hoa++;
                if (m.archetype == ResidentArchetype.Pemberton) pem++;
                if (m.archetype == ResidentArchetype.Butler) butlers++;
                if (ResidentCast.Style(LifeId.Resident(i)).height < 1.4f) kids++;
            }
            Check(sizesOk, "household sizes per §7.1 (small 1–2, medium 2–3, big 2–4; Meadows 2–4; Snootington 2 + staff; Bluffs owner + butler (+ bodyguard))");
            Check(hoa == 1 && pem == 1, $"one HOA Lady ({hoa}) and one Mr. Pemberton ({pem})");
            Check(butlers == 8, $"a butler per Bluffs estate ({butlers})");
            Check(kids == 0, "no children (every resident ≥ 1.4 m)");

            // ---- walk routes (door → curb) for every house
            var lens = new List<float>();
            int noRoute = 0, tooLong = 0;
            for (int h = 0; h < real.houses.Length; h++)
            {
                if (real.houses[h].district == 3) continue;   // butlers
                var nodes = HouseRoutes.DoorToCurb(real, h);
                if (nodes == null || nodes.Length == 0) { noRoute++; continue; }
                float len = HouseRoutes.Length(real, nodes) + 1.5f;   // + the stand point behind the slot
                if (len > HouseRoutes.MaxRouteMetres) tooLong++;
                lens.Add(len);
            }
            lens.Sort();
            float avg = 0f; foreach (var l in lens) avg += l; avg /= Mathf.Max(1, lens.Count);
            float setMin = WalkOutPresenter.PopSeconds + lens[0] / WorldLifeTuning.Walk.Carrying + 0.6f;
            float setMax = WalkOutPresenter.PopSeconds + lens[lens.Count - 1] / WorldLifeTuning.Walk.Carrying + 0.6f;
            Check(noRoute == 0, $"every non-Bluffs door walks to its curb on the graph ({lens.Count} routes, {noRoute} missing)");
            Check(tooLong == 0, $"routes ≤ {HouseRoutes.MaxRouteMetres} m ({tooLong} longer): min {lens[0]:F1} / avg {avg:F1} / max {lens[lens.Count - 1]:F1} m → set-down {setMin:F1}–{setMax:F1} real s after T ({setMin / GameClock.SecondsPerGameHour:F2}–{setMax / GameClock.SecondsPerGameHour:F2} in-game h)");
            int posts = 0;
            foreach (var hs in real.houses) if (hs.butlerPost.position != Vector3.zero) posts++;
            Check(posts == 8, $"8 Bluffs butler posts in the graph ({posts})");
        }
        else sb.AppendLine("    (no Resources/StreetGraph.asset: real-graph checks skipped)");

        // ---- every archetype: build, restyle, renderer counts, no colliders (in a throwaway scene: nothing else dirtied)
        var prevScene = SceneManager.GetActiveScene();
        var tmpScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        SceneManager.SetActiveScene(tmpScene);
        var rootGo = new GameObject("WL_C_SelfTest");
        try
        {
            var rig = PuppetRig.Build(rootGo.transform, "Probe");
            var spec = new RigSpec();
            int maxFull = 0, minFull = 99, maxLite = 0, minLite = 99;
            var counts = new StringBuilder();
            foreach (ResidentArchetype a in Enum.GetValues(typeof(ResidentArchetype)))
            {
                for (int k = 0; k < 3; k++)
                {
                    byte d = (byte)(a <= ResidentArchetype.Gardener ? 0 : a <= ResidentArchetype.HoaLady ? 1 : a <= ResidentArchetype.Housekeeper ? 2 : 3);
                    var st = ResidentLook.StyleFor(a, d, LifeHash.U64(1234, LifeKind.Resident, (int)a * 10 + k, -1));
                    ResidentLook.Compose(st, k == 2, true, spec);
                    rig.Apply(spec);
                    rig.SetLite(false);
                    int full = Active(rig);
                    rig.SetLite(true);
                    int lite = Active(rig);
                    rig.SetLite(false);
                    maxFull = Mathf.Max(maxFull, full); minFull = Mathf.Min(minFull, full);
                    maxLite = Mathf.Max(maxLite, lite); minLite = Mathf.Min(minLite, lite);
                    if (k == 0) counts.Append($"{a} {full}/{lite} ");
                }
            }
            sb.AppendLine("    renderers full/lite: " + counts.ToString().TrimEnd());
            Check(maxFull <= 24 && minFull >= 14, $"full puppets {minFull}–{maxFull} renderers (§3.1 ~18–20; brows add 2 when angry)");
            Check(maxLite <= 5 && minLite >= 3, $"lite puppets {minLite}–{maxLite} renderers (§3.1: body, head, nose, hat)");
            Check(rig.go.GetComponentsInChildren<Collider>(true).Length == 0, "puppets have no colliders (hits by rule only, rule 13)");
            var heights = new List<float>();
            foreach (ResidentArchetype a in Enum.GetValues(typeof(ResidentArchetype)))
                for (int k = 0; k < 20; k++) heights.Add(ResidentLook.StyleFor(a, 0, LifeHash.U64(7, LifeKind.Resident, k, -1, (uint)a)).height);
            heights.Sort();
            Check(heights[0] >= 1.45f && heights[heights.Count - 1] <= 1.86f, $"heights {heights[0]:F2}–{heights[heights.Count - 1]:F2} m (§3.1 1.45–1.85)");

            // the dogs
            var dog = DogRig.Build(rootGo.transform, "DogProbe");
            int dmin = 99, dmax = 0;
            foreach (DogBreed b in Enum.GetValues(typeof(DogBreed)))
            {
                dog.Apply(DogRig.StyleFor(b, 1));
                int n = 0; foreach (var r in dog.r) if (r.gameObject.activeSelf) n++;
                dmin = Mathf.Min(dmin, n); dmax = Mathf.Max(dmax, n);
            }
            Check(dmax <= 18 && dmin >= 14, $"dogs {dmin}–{dmax} renderers (sausage, corgi, poodle) + a leash");

            // the driver's head for package D
            var head = ResidentLook.BuildDriverHead(rootGo.transform, ResidentLook.StyleFor(ResidentArchetype.Commuter, 0, 42));
            int hr = head.GetComponentsInChildren<Renderer>().Length;
            Check(hr >= 2 && hr <= 3, $"driver head {hr} renderers (§3.2: head, nose, hat)");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(rootGo);
            SceneManager.SetActiveScene(prevScene);
            EditorSceneManager.CloseScene(tmpScene, true);
        }

        // ---- the flop arc (§8.1): 6 m/s → lands 3–8 m away, airborne 0.8–1.6 s, one bounce
        foreach (float speed in new[] { 2.5f, 6f, 12f })
        {
            Vector3 v = WorldLifeTuning.Hits.FlopLaunch(new Vector3(0f, 0f, speed), Vector3.right);
            float tAir = 2f * v.y / -Physics.gravity.y;
            Vector3 h = new Vector3(v.x, 0f, v.z);
            float d1 = h.magnitude * tAir;
            float vy2 = v.y * WorldLifeTuning.Hits.BounceRestitution;
            float t2 = 2f * vy2 / -Physics.gravity.y;
            float d2 = h.magnitude * 0.4f * t2;
            sb.AppendLine($"    flop at {speed} m/s: launch {v:F2}, air {tAir:F2} s + bounce {t2:F2} s, lands {d1:F1} m (+{d2:F1} m after the bounce)");
            if (Mathf.Approximately(speed, 6f)) Check(d1 + d2 >= 3f && d1 + d2 <= 8f && tAir >= 0.8f && tAir <= 1.6f, $"6 m/s flop lands {d1 + d2:F1} m away (3–8) after {tAir:F2} s in the air (0.8–1.6)");
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

    // ================================================================================================= lineup render

    [MenuItem("Rubbish/WorldLife/Render Resident Lineup")]
    public static void RenderLineupMenu() => Debug.Log(RenderLineup("Docs/WorldLifeRefs/residents_lineup.png", false));

    /// <summary>Every archetype in a row (plus the dogs and two night walk-outs) rendered to a PNG (project-relative or
    /// absolute path). <paramref name="poses"/>: a second image of poses (carry, flop, starfish, fist, cloche).</summary>
    public static string RenderLineup(string outPath, bool poses)
    {
        var prev = SceneManager.GetActiveScene();
        var s = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        SceneManager.SetActiveScene(s);
        var sb = new StringBuilder();
        try
        {
            var lg = new GameObject("Sun"); var l = lg.AddComponent<Light>(); l.type = LightType.Directional; l.intensity = 1.25f;
            lg.transform.rotation = Quaternion.Euler(40f, -35f, 0f);
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.transform.localScale = new Vector3(6f, 1f, 2f);
            ground.GetComponent<Renderer>().sharedMaterial = PuppetKit.Lit(new Color(0.42f, 0.62f, 0.32f), 0.1f);
            var root = new GameObject("Lineup").transform;
            var spec = new RigSpec();
            // Two rows: the cast (back row: Snootington + Bluffs + the night walk-outs + dogs), 1.5 m apart.
            int k = 0;
            Vector3 Slot(int i) { int row = i < 11 ? 0 : 1; int col = row == 0 ? i : i - 11; return new Vector3(-7.5f + col * 1.5f + row * 0.75f, 0f, row * 2.4f); }
            foreach (ResidentArchetype a in Enum.GetValues(typeof(ResidentArchetype)))
            {
                byte d = (byte)(a <= ResidentArchetype.Gardener ? 0 : a <= ResidentArchetype.HoaLady ? 1 : a <= ResidentArchetype.Housekeeper ? 2 : 3);
                var st = ResidentLook.StyleFor(a, d, LifeHash.U64(1234, LifeKind.Resident, (int)a * 3 + 1, -1));
                var rig = PuppetRig.Build(root, a.ToString());
                ResidentLook.Compose(st, false, true, spec);
                rig.Apply(spec);
                rig.Place(Slot(k), 180f + (k % 3 - 1) * 14f);
                if (a == ResidentArchetype.Butler) rig.Arms(-10f, 8f, -ResidentLook.PlatterTilt, 4f);
                k++;
                sb.Append($"{a} {st.height:F2} m; ");
            }
            // two night walk-outs (pajamas + flashlight) and the dogs
            for (int i = 0; i < 2; i++)
            {
                var st = ResidentLook.StyleFor(i == 0 ? ResidentArchetype.RobeDad : ResidentArchetype.SoccerMom, (byte)i, LifeHash.U64(5, LifeKind.Resident, i, -1));
                var rig = PuppetRig.Build(root, "Night" + i);
                ResidentLook.Compose(st, true, false, spec);
                rig.Apply(spec);
                rig.Place(Slot(k++), 180f);
                rig.Arms(-62f, 24f, -62f, 24f);
            }
            float x = Slot(k).x;
            foreach (DogBreed b in Enum.GetValues(typeof(DogBreed)))
            {
                var dr = DogRig.Build(root, b.ToString());
                dr.Apply(DogRig.StyleFor(b, (ulong)b + 3));
                dr.root.SetPositionAndRotation(new Vector3(x, 0f, 1.2f), Quaternion.Euler(0f, 150f, 0f));
                x += 0.9f;
            }
            var camGo = new GameObject("Cam"); var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.58f, 0.74f, 0.9f);
            cam.fieldOfView = 30f; cam.scene = s;
            cam.fieldOfView = 34f;
            cam.transform.position = new Vector3(0.4f, 3.0f, -13.5f);
            cam.transform.LookAt(new Vector3(0.4f, 0.95f, 1.2f));
            int W = 2400, H = 1000;
            var rt = new RenderTexture(W, H, 24) { antiAliasing = 4 };
            cam.targetTexture = rt; cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply();
            string abs = System.IO.Path.IsPathRooted(outPath) ? outPath : System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), outPath);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(abs));
            System.IO.File.WriteAllBytes(abs, tex.EncodeToPNG());
            RenderTexture.active = null; cam.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(rt); UnityEngine.Object.DestroyImmediate(tex);
            sb.Insert(0, "lineup → " + abs + "\n");
        }
        finally
        {
            SceneManager.SetActiveScene(prev);
            EditorSceneManager.CloseScene(s, true);
        }
        return sb.ToString();
    }

    // ================================================================================================= play-test kit

    /// <summary>Helpers for the acceptance evals (play mode, test data root only).</summary>
    public static class Kit
    {
        /// <summary>Put n bags out at one house right now (CurbTrashSpawner.PutOut, the real path; ageHours back-dates).</summary>
        public static int PutOutAt(string stopId, int n = 1, double ageHours = 0)
        {
            var h = CurbTrashSpawner.FindHouse(stopId);
            if (h == null) return -1;
            var m = typeof(CurbTrashSpawner).GetMethod("PutOut", BindingFlags.NonPublic | BindingFlags.Static);
            return (int)m.Invoke(null, new object[] { h, GameClock.TotalHours - ageHours, n });
        }

        /// <summary>
        /// A test director (slot Rules, registered from an eval): pushes a truck at a constant speed along a direction until
        /// its nose passes a point ("holding W" can't be injected), and samples one agent every frame (its act, flop phase,
        /// stars, position) so the evals can read lie / chase / landing timings.
        /// </summary>
        public sealed class Driver : WorldLifeDirector
        {
            public static Driver Installed;
            public override WorldLifeSlot Slot => WorldLifeSlot.Rules;
            public TruckControllerV2 truck;
            public Vector3 cruiseDir, cruiseStop;
            public float cruiseSpeed;
            public bool cruising;
            public LifeId watch;
            public readonly List<string> log = new List<string>();
            public float starsSeenSeconds, lieStart = -1f, getUpAt = -1f, chaseStart = -1f, chaseEnd = -1f, rejoinEnd = -1f;
            ResidentAgent.FlopPhase lastPhase = ResidentAgent.FlopPhase.Air;
            ResidentAgent.Act lastAct = ResidentAgent.Act.None;
            public int fixedTicks;

            public static Driver Install()
            {
                if (Installed != null) WorldLifeRunner.Unregister(Installed);
                Installed = new Driver();
                WorldLifeRunner.Register(Installed);
                return Installed;
            }

            public static void Uninstall()
            {
                if (Installed != null) WorldLifeRunner.Unregister(Installed);
                Installed = null;
            }

            public override void FixedTick(WorldLifeContext ctx)
            {
                fixedTicks++;
                if (truck == null || !cruising) return;
                var rb = truck.Body;
                Vector3 nose = rb.position + rb.rotation * new Vector3(0f, 0f, TruckControllerV2.BodyBoxMetres.max.z);
                if (Vector3.Dot(cruiseStop - nose, cruiseDir) > 0f)
                {
                    var v = rb.linearVelocity;
                    rb.linearVelocity = new Vector3(cruiseDir.x * cruiseSpeed, v.y, cruiseDir.z * cruiseSpeed);
                }
                else cruising = false;
            }

            public override void LateTick(WorldLifeContext ctx)
            {
                var dir = ResidentDirector.Instance;
                if (dir == null || !watch.IsValid) return;
                var a = dir.Find(watch);
                if (a == null) return;
                float now = Time.time;
                if (a.act != lastAct || (a.act == ResidentAgent.Act.Flop && a.flop != lastPhase))
                {
                    log.Add($"{now:F2} {a.act}/{(a.act == ResidentAgent.Act.Flop ? a.flop.ToString() : "-")} at {a.pos:F1}");
                    if (a.act == ResidentAgent.Act.Flop && a.flop == ResidentAgent.FlopPhase.Lie) lieStart = now;
                    if (a.act == ResidentAgent.Act.Flop && a.flop == ResidentAgent.FlopPhase.GetUp) getUpAt = now;
                    if (a.act == ResidentAgent.Act.Flop && a.flop == ResidentAgent.FlopPhase.Chase) chaseStart = now;
                    if (lastAct == ResidentAgent.Act.Flop && lastPhase == ResidentAgent.FlopPhase.Chase && a.flop != ResidentAgent.FlopPhase.Chase) chaseEnd = now;
                    if (lastAct == ResidentAgent.Act.Flop && a.act == ResidentAgent.Act.None) rejoinEnd = now;
                    lastAct = a.act; lastPhase = a.flop;
                }
                if (a.act == ResidentAgent.Act.Flop && a.flop == ResidentAgent.FlopPhase.Lie && dir.Fx.StarsOn(a.rig != null ? a.rig.head : null)) starsSeenSeconds += ctx.DeltaTime;
            }
        }

        /// <summary>The house (by stopId) whose curb is nearest the point and has a free slot, optionally in a district.</summary>
        public static PickupStop NearestStop(Vector3 p, float minDist, float maxDist, int district = -1)
        {
            PickupStop best = null; float bd = float.MaxValue;
            foreach (var s in PickupStop.All)
            {
                if (s == null || s.IsFull) continue;
                if (district >= 0 && WorldLifeIds.DistrictIndex(s.District) != district) continue;
                float d = Vector3.Distance(s.transform.position, p);
                if (d < minDist || d > maxDist) continue;
                float score = Mathf.Abs(d - (minDist + maxDist) * 0.5f);
                if (score < bd) { bd = score; best = s; }
            }
            return best;
        }
    }
}
