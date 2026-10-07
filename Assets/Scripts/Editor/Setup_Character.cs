using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Builds Chuck Dumpling (Docs/Character.md) onto Assets/Prefabs/Player.prefab. Idempotent: every object it makes carries
/// a <see cref="ChuckPart"/> marker and is deleted and rebuilt on each run.
///
/// Order (section 7): load the prefab contents (bones in the saved A-pose, never an animated frame) → set the root to
/// scale 1 → delete the previous puppet → build the 81 parts from <see cref="ChuckPartsData"/> (primitive, place at
/// bone + offset in root axes, set localScale unparented, SetParent(worldPositionStays), shared material by colour role,
/// shadows off where marked, variant-only parts inactive) → deactivate the mannequin mesh (SetActive, never
/// renderer.enabled: the truck/chess code re-enable every child renderer) → googly eyes + ChuckPuppet → apply the
/// section 5 prefab settings (root scale 0.8, capsule, grounding, camera root, Animator culling, carry hold point) → save.
/// Materials: Assets/Materials/Generated/Character/M_Chuck_&lt;Role&gt;.mat (URP Lit, flat colour, shared).
/// </summary>
public static class Setup_Character
{
    public const string PlayerPrefabPath = "Assets/Prefabs/Player.prefab";
    public const string MatDir = "Assets/Materials/Generated/Character";
    public const string RigMarkerName = "ChuckRig";
    public const float RootScale = 0.8f;
    /// <summary>Emission multiplier on the googly-eye white (spec palette has none).</summary>
    public const float EyeWhiteGlow = 0.3f;

    [MenuItem("Rubbish/Setup/Character")]
    public static void Run() => Build();

    public static string Build()
    {
        var log = new StringBuilder("[Setup_Character] ");
        if (Application.isPlaying)
        {
            Debug.LogError("[Setup_Character] Exit play mode first.");
            return "play mode";
        }
        var mats = EnsureMaterials(log);
        if (mats == null) { Debug.LogError(log.ToString()); return log.ToString(); }

        if (AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath) == null)
        {
            log.Append("MISSING " + PlayerPrefabPath);
            Debug.LogError(log.ToString());
            return log.ToString();
        }
        var root = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);
        try
        {
            if (!BuildRig(root, mats, log))
            {
                Debug.LogError(log.ToString());
                return log.ToString();
            }
            ApplyPrefabSettings(root, log);
            PrefabUtility.SaveAsPrefabAsset(root, PlayerPrefabPath, out bool ok);
            log.Append(ok ? "prefab saved." : "PREFAB SAVE FAILED.");
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
        AssetDatabase.SaveAssets();
        Debug.Log(log.ToString());
        return log.ToString();
    }

    // =====================================================================
    // Materials
    // =====================================================================

    public static Dictionary<ChuckRole, Material> EnsureMaterials(StringBuilder log)
    {
        var lit = Shader.Find("Universal Render Pipeline/Lit");
        if (lit == null) { log?.Append("URP Lit shader not found. "); return null; }
        EnsureFolder(MatDir);
        var mats = new Dictionary<ChuckRole, Material>();
        foreach (var pd in ChuckPartsData.Palette)
        {
            string path = $"{MatDir}/M_Chuck_{pd.role}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            bool created = m == null;
            if (created) m = new Material(lit);
            else if (m.shader != lit) m.shader = lit;
            m.SetColor("_BaseColor", pd.color);
            m.SetColor("_Color", pd.color);
            m.SetFloat("_Smoothness", pd.smoothness);
            m.SetFloat("_Metallic", pd.metallic);
            m.SetFloat("_WorkflowMode", 1f);
            m.SetFloat("_ReceiveShadows", 1f);
            m.DisableKeyword("_RECEIVE_SHADOWS_OFF");
            // Tuning on top of the spec palette: a faint self-glow keeps the googly whites (eyes + Company Eye pin)
            // white under the cap brim and in dim light, so the face always reads first.
            float emission = pd.role == ChuckRole.EyeWhite ? EyeWhiteGlow : pd.emission;
            if (emission > 0f)
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", pd.color * emission);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            else
            {
                m.DisableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", Color.black);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            }
            if (created) AssetDatabase.CreateAsset(m, path);
            else EditorUtility.SetDirty(m);
            mats[pd.role] = m;
        }
        log?.Append($"{mats.Count} materials; ");
        return mats;
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
    }

    // =====================================================================
    // Rig
    // =====================================================================

    static readonly string[] BoneNames =
    {
        "Hips", "Spine", "Chest", "UpperChest", "Head",
        "Left_UpperArm", "Left_LowerArm", "Left_Hand", "Right_UpperArm", "Right_LowerArm", "Right_Hand",
        "Left_UpperLeg", "Left_LowerLeg", "Left_Foot", "Left_Toes", "Right_UpperLeg", "Right_LowerLeg", "Right_Foot", "Right_Toes",
    };

    /// <summary>
    /// Build (or rebuild) the puppet on a Player root that is in its saved bind pose. Leaves the root at scale 1;
    /// <see cref="ApplyPrefabSettings"/> sets the 0.8 scale afterwards. Returns false (and logs why) if the rig is unusable.
    /// </summary>
    public static bool BuildRig(GameObject root, Dictionary<ChuckRole, Material> mats, StringBuilder log)
    {
        var rt = root.transform;
        rt.localScale = Vector3.one; // build every part at root scale 1

        // 1. previous build
        int removed = 0;
        foreach (var old in root.GetComponentsInChildren<ChuckPart>(true))
        {
            if (old == null) continue; // already gone with its parent
            Object.DestroyImmediate(old.gameObject);
            removed++;
        }

        // 2. bones
        var skel = rt.Find("Skeleton");
        if (skel == null) { log.Append("no Skeleton child. "); return false; }
        var bones = new Dictionary<string, Transform>();
        foreach (var t in skel.GetComponentsInChildren<Transform>(true))
            if (!bones.ContainsKey(t.name)) bones[t.name] = t;
        foreach (var bn in BoneNames)
            if (!bones.ContainsKey(bn)) { log.Append($"missing bone {bn}. "); return false; }

        // Bind-pose sanity: the A-pose left hand sits at root-local (-0.44, 1.07, 0.12).
        Vector3 hand = rt.InverseTransformPoint(bones["Left_Hand"].position);
        if ((hand - new Vector3(-0.442f, 1.068f, 0.1235f)).magnitude > 0.02f)
            log.Append($"WARNING: Left_Hand at {hand:F3}, not the saved A-pose; parts may be misplaced. ");
        foreach (var bn in BoneNames)
            if ((bones[bn].lossyScale - Vector3.one).sqrMagnitude > 1e-6f) log.Append($"WARNING: bone {bn} scale {bones[bn].lossyScale}. ");

        var mesh = rt.Find("Geometry/Armature_Mesh");
        int layer = mesh != null ? mesh.gameObject.layer : root.layer;

        // 3. marker
        var marker = new GameObject(RigMarkerName);
        marker.transform.SetParent(rt, false);
        marker.AddComponent<ChuckPart>().index = -1;
        marker.layer = layer;

        // 4. parts
        int n = ChuckPartsData.Count;
        var built = new Transform[n];
        int renderers = 0, empties = 0, inactive = 0, shadowOff = 0;
        for (int i = 0; i < n; i++)
        {
            var def = ChuckPartsData.Parts[i];
            var bone = bones.TryGetValue(def.bone, out var b) ? b : null;
            if (bone == null) { log.Append($"part {def.name}: missing bone {def.bone}. "); return false; }
            GameObject go;
            if (def.prim == ChuckPrim.Empty) { go = new GameObject(def.name); empties++; }
            else
            {
                go = GameObject.CreatePrimitive(PrimOf(def.prim));
                go.name = def.name;
                var col = go.GetComponent<Collider>();
                if (col != null) Object.DestroyImmediate(col);
            }
            go.AddComponent<ChuckPart>().index = i;
            go.layer = layer;
            var t = go.transform;
            Vector3 centre = rt.TransformPoint(rt.InverseTransformPoint(bone.position) + def.offset);
            t.SetPositionAndRotation(centre, rt.rotation * Quaternion.Euler(def.euler));
            t.localScale = def.scale;
            var parent = def.parent >= 0 ? built[def.parent] : bone;
            t.SetParent(parent, true);
            if ((t.localScale - def.scale).sqrMagnitude > 1e-8f)
                log.Append($"WARNING {def.name} localScale {t.localScale:F4} != {def.scale:F4}. ");
            t.localScale = def.scale;

            var r = go.GetComponent<MeshRenderer>();
            if (r != null)
            {
                if (!mats.TryGetValue(def.role, out var m)) { log.Append($"no material for {def.role}. "); return false; }
                r.sharedMaterial = m;
                r.shadowCastingMode = def.shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
                // Googly eyes never take shadows (the brim would grey them out at noon): the face reads first.
                r.receiveShadows = def.role != ChuckRole.EyeWhite && def.role != ChuckRole.Pupil;
                r.lightProbeUsage = LightProbeUsage.BlendProbes;
                r.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
                r.motionVectorGenerationMode = MotionVectorGenerationMode.Object;
                if (def.variantOnly == 0) { renderers++; if (!def.shadows) shadowOff++; }
            }
            if (def.variantOnly != 0) { go.SetActive(false); inactive++; }
            built[i] = t;
        }

        // 5. hide the mannequin (SetActive only; Skeleton stays active)
        if (mesh != null) mesh.gameObject.SetActive(false);
        else log.Append("WARNING: Geometry/Armature_Mesh not found. ");

        // 6. googly eyes (rig-metre tuning; never the depot mascot defaults)
        AddEye(built[ChuckPartsData.EyeRoot_L], built[ChuckPartsData.Pupil_L], 0.028f, 0.8f, 10f, 0.55f, 0.12f);
        AddEye(built[ChuckPartsData.EyeRoot_R], built[ChuckPartsData.Pupil_R], 0.031f, 0.8f, 10f, 0.55f, 0.12f);
        AddEye(built[ChuckPartsData.BadgeEyeRoot], built[ChuckPartsData.Badge_Pupil], 0.036f, 1.0f, 12f, 0.5f, 0.2f);

        // 7. puppet
        var puppet = root.GetComponent<ChuckPuppet>();
        if (puppet == null) puppet = root.AddComponent<ChuckPuppet>();
        puppet.parts = built;
        puppet.hips = bones["Hips"];
        puppet.spine = bones["Spine"];
        puppet.chest = bones["Chest"];
        puppet.upperChest = bones["UpperChest"];
        puppet.head = bones["Head"];
        puppet.upperArmL = bones["Left_UpperArm"];
        puppet.lowerArmL = bones["Left_LowerArm"];
        puppet.handL = bones["Left_Hand"];
        puppet.upperArmR = bones["Right_UpperArm"];
        puppet.lowerArmR = bones["Right_LowerArm"];
        puppet.handR = bones["Right_Hand"];
        puppet.rootScale = RootScale;

        log.Append($"rig: {renderers} renderers ({shadowOff} no-shadow), {empties} pivots, {inactive} variant-only (inactive), removed {removed} old; ");
        return true;
    }

    static PrimitiveType PrimOf(ChuckPrim p)
    {
        switch (p)
        {
            case ChuckPrim.Cube: return PrimitiveType.Cube;
            case ChuckPrim.Cylinder: return PrimitiveType.Cylinder;
            case ChuckPrim.Capsule: return PrimitiveType.Capsule;
            default: return PrimitiveType.Sphere;
        }
    }

    static void AddEye(Transform eyeRoot, Transform pupil, float maxOffset, float gravity, float motionKick, float bounciness, float twitch)
    {
        var e = eyeRoot.GetComponent<ChuckGooglyEye>();
        if (e == null) e = eyeRoot.gameObject.AddComponent<ChuckGooglyEye>();
        e.pupil = pupil;
        e.maxOffset = maxOffset;
        e.gravity = gravity;
        e.motionKick = motionKick;
        e.bounciness = bounciness;
        e.twitch = twitch;
    }

    // =====================================================================
    // Prefab settings (Docs/Character.md section 5)
    // =====================================================================

    public static void ApplyPrefabSettings(GameObject root, StringBuilder log)
    {
        var rt = root.transform;
        rt.localScale = Vector3.one * RootScale;

        var cc = root.GetComponent<CharacterController>();
        if (cc != null)
        {
            cc.height = 1.90f;
            cc.radius = 0.32f;
            cc.center = new Vector3(0f, 0.95f, 0f);
        }
        else log.Append("WARNING no CharacterController; ");

        var tpc = root.GetComponent<StarterAssets.ThirdPersonController>();
        if (tpc != null)
        {
            tpc.GroundedRadius = 0.25f;
            tpc.GroundedOffset = -0.11f;
        }
        else log.Append("WARNING no ThirdPersonController; ");

        var camRoot = rt.Find("PlayerCameraRoot");
        if (camRoot != null)
        {
            var p = camRoot.localPosition;
            p.y = 1.72f;
            camRoot.localPosition = p;
        }
        else log.Append("WARNING no PlayerCameraRoot; ");

        var an = root.GetComponent<Animator>();
        if (an != null) an.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        var tc = root.GetComponent<TrashCarrier>();
        if (tc != null)
        {
            tc.holdLocalPosition = new Vector3(0f, 0.90f, 0.48f);
            tc.chargePullIn = 0f;
            tc.chargeLift = 0.08f;
            tc.bellyStop = 0.04f; // his belly is right behind the bag: at most 4 cm of sag on starts and turns
        }
        else log.Append("WARNING no TrashCarrier; ");

        log.Append($"prefab: scale {RootScale}, capsule h1.90 r0.32 c0.95, grounded 0.25/-0.11, cam root y1.72, AlwaysAnimate, hold (0,0.90,0.48) pull 0 lift 0.08 belly stop 0.04; ");
    }

    // =====================================================================
    // Debug menu (play mode)
    // =====================================================================

    const string Dbg = "Rubbish/Debug/Character/";

    static ChuckPuppet LocalPuppet() => PlayerController.Local != null ? PlayerController.Local.GetComponent<ChuckPuppet>() : null;

    [MenuItem(Dbg + "Play Fidget: Sniff")] static void F1() => LocalPuppet()?.DebugFidget(1);
    [MenuItem(Dbg + "Play Fidget: Tummy Pat")] static void F2() => LocalPuppet()?.DebugFidget(2);
    [MenuItem(Dbg + "Play Fidget: Hat Pop")] static void F3() => LocalPuppet()?.DebugFidget(3);
    [MenuItem(Dbg + "Play Fidget: Googly Cross")] static void F4() => LocalPuppet()?.DebugFidget(4);
    [MenuItem(Dbg + "Play Fidget: Stretch-Yawn")] static void F5() => LocalPuppet()?.DebugFidget(5);
    [MenuItem(Dbg + "Play Fidget: Look-See")] static void F6() => LocalPuppet()?.DebugFidget(6);

    [MenuItem(Dbg + "Cycle Variant (local player)")]
    static void CycleVariant()
    {
        var p = LocalPuppet();
        if (p == null) return;
        p.variantOverride = (Mathf.Max(p.Variant, 0) + 1) % ChuckPartsData.Variants.Length;
        Debug.Log($"[Character] variant -> {ChuckPartsData.Variants[p.variantOverride].name}");
    }

    [MenuItem(Dbg + "Play Fidget: Sniff", true)] static bool V1() => Application.isPlaying;
    [MenuItem(Dbg + "Play Fidget: Tummy Pat", true)] static bool V2() => Application.isPlaying;
    [MenuItem(Dbg + "Play Fidget: Hat Pop", true)] static bool V3() => Application.isPlaying;
    [MenuItem(Dbg + "Play Fidget: Googly Cross", true)] static bool V4() => Application.isPlaying;
    [MenuItem(Dbg + "Play Fidget: Stretch-Yawn", true)] static bool V5() => Application.isPlaying;
    [MenuItem(Dbg + "Play Fidget: Look-See", true)] static bool V6() => Application.isPlaying;
    [MenuItem(Dbg + "Cycle Variant (local player)", true)] static bool V7() => Application.isPlaying;
}
