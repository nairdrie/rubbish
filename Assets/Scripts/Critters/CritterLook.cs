using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Look-and-feel data for the pile squatter. The design lead may swap the Pile Goblin for something else later,
/// so its name, colors and voice lines live here (assign <see cref="Current"/> to reskin every future spawn).
/// </summary>
[Serializable]
public class SquatterProfile
{
    public string displayName = "Pile Goblin";
    public Color skin = new Color(0.42f, 0.72f, 0.28f);
    public Color belly = new Color(0.64f, 0.84f, 0.46f);
    public Color nose = new Color(0.33f, 0.58f, 0.2f);
    public Color earInner = new Color(0.95f, 0.55f, 0.6f);
    public Color capA = new Color(0.86f, 0.16f, 0.18f);
    public Color capB = new Color(0.97f, 0.95f, 0.9f);
    public float earSize = 1f;
    public string angrySfx = "goblin_angry";
    public string snoreSfx = "snore";
    public string grumbleSfx = "grumble";
    public string[] wakeLines = { "WHO TOUCHED MY PILE?!", "MINE!!", "HEY! THAT'S MY BED!", "I WAS NAPPING!" };
    public string[] bonkLines = { "BONK!", "BONK!!", "*BONK*", "THWACK!" };
    public string[] gloatLines = { "HMPH!", "and STAY out", "nyeh!", "hehehe" };
    public string[] evictLines = { "MY PILE!!", "NOOO my stinky castle", "you'll hear from my lawyer", "I'm telling my mom" };
    public string[] mutterLines = { "grumble grumble", "stupid clean streets", "mutter mutter", "*angry goblin noises*" };
    public string[] giveUpLines = { "bah!", "not worth it", "*yawn* ...later" };

    static SquatterProfile current;
    /// <summary>The look used for new pile squatters.</summary>
    public static SquatterProfile Current
    {
        get { if (current == null) current = new SquatterProfile(); return current; }
        set => current = value;
    }
}

/// <summary>All transforms a critter builder hands back for procedural animation. Unused slots stay null.</summary>
public class CritterRig
{
    public Transform root;        // the critter GameObject
    public Transform visual;      // procedural bob / lean / squash
    public Transform juice;       // Juice.* target (never touched procedurally)
    public Transform rear;        // pitch pivot (raccoon rearing up, goblin slump)
    public Transform body;
    public Transform head;
    public Transform mouth;       // anchor at the mouth (foam, yelling)
    public Transform mouthShape;  // renderer that opens/closes
    public Transform nose;
    public Transform bubble;      // goblin snot bubble
    public Transform zAnchor;     // where snore Z's come from
    public Transform weapon;      // goblin plunger
    public Transform armL, armR, legL, legR;
    public Transform capTip;
    public readonly List<Transform> tail = new List<Transform>();
    public readonly List<Quaternion> tailRest = new List<Quaternion>();
    public readonly List<Transform> paws = new List<Transform>();
    public readonly List<Vector3> pawRest = new List<Vector3>();
    public readonly List<Transform> ears = new List<Transform>();
    public readonly List<Quaternion> earRest = new List<Quaternion>();
    public readonly List<Transform> cap = new List<Transform>();
    public readonly List<Quaternion> capRest = new List<Quaternion>();
    public readonly List<CritterGooglyEye> eyes = new List<CritterGooglyEye>();

    public void KickEyes(float s) { foreach (var e in eyes) if (e != null) e.Kick(s); }
    public void CloseEyes(bool closed) { foreach (var e in eyes) if (e != null) e.Closed = closed; }
}

/// <summary>
/// Builds the derpy low-poly critters out of Unity primitives (no assets needed) and owns their materials.
/// Materials come from RubbishAssets ("mat/critters_lit", "mat/critters_lit_emissive", "mat/critters_unlit",
/// registered by Rubbish/Setup/Critters so builds keep the shader variants) and fall back to runtime-created
/// URP materials. One cached material per color keeps SRP batching happy.
/// </summary>
public static class CritterLook
{
    public static readonly Color RaccoonFur = new Color(0.50f, 0.50f, 0.54f);
    public static readonly Color RaccoonFurDark = new Color(0.30f, 0.30f, 0.33f);
    public static readonly Color RabidFur = new Color(0.40f, 0.42f, 0.36f);
    public static readonly Color RaccoonLight = new Color(0.86f, 0.84f, 0.80f);
    public static readonly Color MaskBlack = new Color(0.07f, 0.07f, 0.08f);
    public static readonly Color EyeWhite = new Color(0.98f, 0.98f, 0.96f);
    public static readonly Color PupilBlack = new Color(0.02f, 0.02f, 0.02f);
    public static readonly Color RabidRed = new Color(1f, 0.12f, 0.06f);
    public static readonly Color FoamWhite = new Color(0.97f, 0.98f, 0.95f);

    // ------------------------------------------------------------------ materials

    static readonly Dictionary<long, Material> litCache = new Dictionary<long, Material>();
    static readonly Dictionary<long, Material> unlitCache = new Dictionary<long, Material>();

    static long Key(Color c, float a, float b)
    {
        Color32 k = c;
        return ((long)k.r) | ((long)k.g << 8) | ((long)k.b << 16) | ((long)k.a << 24)
             | ((long)Mathf.RoundToInt(Mathf.Clamp01(a) * 255f) << 32) | ((long)Mathf.RoundToInt(Mathf.Clamp(b, 0f, 8f) * 31f) << 40);
    }

    /// <summary>Lit (URP Lit) material of this color. emission > 0 makes it glow (eye shine at night).</summary>
    public static Material Mat(Color color, float smoothness = 0.3f, float emission = 0f)
    {
        long key = Key(color, smoothness, emission);
        if (litCache.TryGetValue(key, out var m) && m != null) return m;
        Material src = emission > 0f ? RubbishAssets.Material("mat/critters_lit_emissive") : RubbishAssets.Material("mat/critters_lit");
        if (src != null) m = new Material(src);
        else
        {
            var sh = Shader.Find("Universal Render Pipeline/Lit");
            if (sh == null) sh = Shader.Find("Standard");
            m = new Material(sh);
        }
        m.name = "Critter_" + ColorUtility.ToHtmlStringRGB(color);
        m.SetColor("_BaseColor", color);
        m.SetColor("_Color", color);
        m.SetFloat("_Smoothness", smoothness);
        m.SetFloat("_Metallic", 0f);
        if (emission > 0f)
        {
            m.EnableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            m.SetColor("_EmissionColor", color * emission);
        }
        m.hideFlags = HideFlags.DontSave;
        litCache[key] = m;
        return m;
    }

    /// <summary>Unlit flat color (Z's, stars, rings, syringe liquid).</summary>
    public static Material Unlit(Color color)
    {
        long key = Key(color, 0f, 0f);
        if (unlitCache.TryGetValue(key, out var m) && m != null) return m;
        var src = RubbishAssets.Material("mat/critters_unlit");
        if (src != null) m = new Material(src);
        else
        {
            var sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) sh = Shader.Find("Unlit/Color");
            m = new Material(sh);
        }
        m.name = "CritterUnlit_" + ColorUtility.ToHtmlStringRGB(color);
        m.SetColor("_BaseColor", color);
        m.SetColor("_Color", color);
        m.hideFlags = HideFlags.DontSave;
        unlitCache[key] = m;
        return m;
    }

    // ------------------------------------------------------------------ primitive helpers

    public static Transform Part(Transform parent, PrimitiveType type, string name, Vector3 pos, Vector3 scale, Material mat,
        Vector3 euler = default, bool shadows = true)
    {
        var go = GameObject.CreatePrimitive(type);
        UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
        go.name = name;
        go.layer = CritterWorld.CritterLayer;
        var t = go.transform;
        t.SetParent(parent, false);
        t.localPosition = pos;
        t.localRotation = Quaternion.Euler(euler);
        t.localScale = scale;
        var r = go.GetComponent<Renderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
        r.receiveShadows = true;
        return t;
    }

    public static Transform Node(Transform parent, string name, Vector3 pos, Vector3 euler = default)
    {
        var t = new GameObject(name).transform;
        t.gameObject.layer = CritterWorld.CritterLayer;
        t.SetParent(parent, false);
        t.localPosition = pos;
        t.localRotation = Quaternion.Euler(euler);
        return t;
    }

    /// <summary>A googly eye: white ball + black pupil that rattles. size = eye diameter (m).</summary>
    public static CritterGooglyEye Eye(Transform parent, string name, Vector3 pos, float size, Color pupilColor, float pupilGlow = 0f,
        Color? white = null, float whiteGlow = 0.35f)
    {
        var eye = Part(parent, PrimitiveType.Sphere, name, pos, new Vector3(size, size, size * 0.55f),
            Mat(white ?? EyeWhite, 0.9f, whiteGlow), Vector3.zero, false);
        var pupil = Part(eye, PrimitiveType.Sphere, "Pupil", new Vector3(0f, -0.1f, 0.33f), new Vector3(0.5f, 0.5f, 0.45f),
            pupilGlow > 0f ? Mat(pupilColor, 0.9f, pupilGlow) : Mat(pupilColor, 0.95f), Vector3.zero, false);
        var g = eye.gameObject.AddComponent<CritterGooglyEye>();
        g.pupil = pupil;
        g.maxOffset = 0.2f;
        return g;
    }

    /// <summary>Common root: kinematic trigger body on the critter layer, visual → juice hierarchy.</summary>
    static CritterRig NewRig(GameObject root, float triggerRadius, float triggerHeight)
    {
        root.layer = CritterWorld.CritterLayer;
        var rig = new CritterRig { root = root.transform };
        rig.visual = Node(root.transform, "Visual", Vector3.zero);
        rig.juice = Node(rig.visual, "Juice", Vector3.zero);
        if (Application.isPlaying)
        {
            var col = root.AddComponent<SphereCollider>();
            col.isTrigger = true;
            col.radius = triggerRadius;
            col.center = new Vector3(0f, triggerHeight, 0f);
            var rb = root.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.interpolation = RigidbodyInterpolation.None;
        }
        return rig;
    }

    // ------------------------------------------------------------------ raccoon

    /// <summary>
    /// Chubby gray raccoon: round body, black bandit mask, ringed tail, tiny black paws, googly eyes with a bit of
    /// eye shine at night. Rabid ones are mangier, with red glowing eyes, angry brows and a foam anchor at the mouth.
    /// Root pivot = ground under the belly, +Z forward. ~0.45 m tall, 0.9 m long with the tail.
    /// </summary>
    public static CritterRig BuildRaccoon(GameObject root, bool rabid)
    {
        var rig = NewRig(root, 0.42f, 0.3f);
        Color fur = rabid ? RabidFur : RaccoonFur;
        var furM = Mat(fur, 0.15f);
        var darkM = Mat(RaccoonFurDark, 0.15f);
        var lightM = Mat(RaccoonLight, 0.12f);
        var maskM = Mat(MaskBlack, 0.35f);
        var pawM = Mat(new Color(0.12f, 0.11f, 0.12f), 0.4f);

        rig.rear = Node(rig.juice, "Rear", new Vector3(0f, 0.06f, -0.2f));
        var r = rig.rear;
        rig.body = Part(r, PrimitiveType.Sphere, "Body", new Vector3(0f, 0.2f, 0.2f), new Vector3(0.48f, 0.4f, 0.6f), furM);
        Part(r, PrimitiveType.Sphere, "Belly", new Vector3(0f, 0.14f, 0.27f), new Vector3(0.36f, 0.26f, 0.42f), lightM);
        Part(r, PrimitiveType.Sphere, "Back", new Vector3(0f, 0.3f, 0.12f), new Vector3(0.34f, 0.18f, 0.36f), darkM);

        // Head
        rig.head = Node(r, "Head", new Vector3(0f, 0.34f, 0.5f));
        var h = rig.head;
        Part(h, PrimitiveType.Sphere, "Skull", Vector3.zero, new Vector3(0.38f, 0.33f, 0.33f), furM);
        Part(h, PrimitiveType.Sphere, "CheekL", new Vector3(-0.1f, -0.05f, 0.08f), new Vector3(0.17f, 0.13f, 0.14f), lightM);
        Part(h, PrimitiveType.Sphere, "CheekR", new Vector3(0.1f, -0.05f, 0.08f), new Vector3(0.17f, 0.13f, 0.14f), lightM);
        Part(h, PrimitiveType.Sphere, "Brow", new Vector3(0f, 0.08f, 0.1f), new Vector3(0.22f, 0.06f, 0.14f), lightM);
        Part(h, PrimitiveType.Sphere, "Mask", new Vector3(0f, 0.025f, 0.11f), new Vector3(0.37f, 0.11f, 0.17f), maskM);
        Part(h, PrimitiveType.Sphere, "Snout", new Vector3(0f, -0.055f, 0.16f), new Vector3(0.15f, 0.11f, 0.17f), lightM);
        rig.nose = Part(h, PrimitiveType.Sphere, "Nose", new Vector3(0f, -0.035f, 0.245f), new Vector3(0.065f, 0.05f, 0.05f), Mat(MaskBlack, 0.9f), default, false);
        rig.mouthShape = Part(h, PrimitiveType.Sphere, "Mouth", new Vector3(0f, -0.1f, 0.2f), new Vector3(0.07f, 0.015f, 0.04f), Mat(new Color(0.35f, 0.08f, 0.1f), 0.5f), default, false);
        rig.mouth = Node(h, "MouthAnchor", new Vector3(0f, -0.11f, 0.23f));
        Part(rig.mouth, PrimitiveType.Cube, "ToothL", new Vector3(-0.015f, 0.012f, -0.01f), new Vector3(0.018f, 0.022f, 0.01f), Mat(EyeWhite, 0.6f), default, false);
        Part(rig.mouth, PrimitiveType.Cube, "ToothR", new Vector3(0.015f, 0.012f, -0.01f), new Vector3(0.018f, 0.022f, 0.01f), Mat(EyeWhite, 0.6f), default, false);

        Color pupil = rabid ? RabidRed : PupilBlack;
        float glow = rabid ? 2.2f : 0f;
        Color? white = rabid ? new Color(1f, 0.82f, 0.78f) : (Color?)null;
        rig.eyes.Add(Eye(h, "EyeL", new Vector3(-0.075f, 0.035f, 0.155f), 0.105f, pupil, glow, white));
        rig.eyes.Add(Eye(h, "EyeR", new Vector3(0.075f, 0.035f, 0.155f), rabid ? 0.12f : 0.1f, pupil, glow, white)); // derp: slightly odd eyes
        if (rabid)
        {
            Part(h, PrimitiveType.Cube, "BrowL", new Vector3(-0.075f, 0.1f, 0.16f), new Vector3(0.1f, 0.02f, 0.02f), maskM, new Vector3(0f, 0f, -24f), false);
            Part(h, PrimitiveType.Cube, "BrowR", new Vector3(0.075f, 0.1f, 0.16f), new Vector3(0.1f, 0.02f, 0.02f), maskM, new Vector3(0f, 0f, 24f), false);
        }

        // Ears
        for (int s = -1; s <= 1; s += 2)
        {
            var ear = Node(h, s < 0 ? "EarL" : "EarR", new Vector3(0.12f * s, 0.14f, -0.03f), new Vector3(0f, 0f, -22f * s));
            Part(ear, PrimitiveType.Sphere, "Outer", new Vector3(0f, 0.04f, 0f), new Vector3(0.1f, 0.11f, 0.05f), furM);
            Part(ear, PrimitiveType.Sphere, "Inner", new Vector3(0f, 0.04f, 0.015f), new Vector3(0.06f, 0.07f, 0.03f), maskM, default, false);
            Part(ear, PrimitiveType.Sphere, "Tip", new Vector3(0f, 0.085f, 0f), new Vector3(0.05f, 0.03f, 0.04f), lightM, default, false);
            rig.ears.Add(ear);
            rig.earRest.Add(ear.localRotation);
        }

        // Ringed tail: a chain of fluffy balls curling up and back.
        var seg = Node(r, "Tail0", new Vector3(0f, 0.22f, -0.07f), new Vector3(-35f, 0f, 0f));
        float ts = 0.15f;
        for (int i = 0; i < 7; i++)
        {
            bool ring = i % 2 == 1;
            Part(seg, PrimitiveType.Sphere, "Fluff", new Vector3(0f, 0f, -ts * 0.45f), new Vector3(ts * 1.05f, ts * 1.05f, ts * 1.2f),
                ring ? maskM : (i == 6 ? maskM : furM));
            rig.tail.Add(seg);
            rig.tailRest.Add(seg.localRotation);
            if (i == 6) break;
            seg = Node(seg, "Tail" + (i + 1), new Vector3(0f, 0f, -ts * 0.8f), new Vector3(-10f, 0f, 0f));
            ts *= 0.93f;
        }

        // Tiny paws
        Vector3[] paws = { new Vector3(-0.13f, -0.02f, 0.4f), new Vector3(0.13f, -0.02f, 0.4f), new Vector3(-0.15f, -0.02f, 0.02f), new Vector3(0.15f, -0.02f, 0.02f) };
        for (int i = 0; i < paws.Length; i++)
        {
            var p = Part(r, PrimitiveType.Sphere, "Paw" + i, paws[i], new Vector3(0.1f, 0.07f, 0.13f), pawM);
            rig.paws.Add(p);
            rig.pawRest.Add(p.localPosition);
        }

        if (rabid)
        {
            // Mangy tufts sticking out of the back.
            var rng = new System.Random(root.GetInstanceID());
            for (int i = 0; i < 5; i++)
            {
                float a = (float)rng.NextDouble() * Mathf.PI - Mathf.PI * 0.5f;
                Vector3 p = new Vector3(Mathf.Sin(a) * 0.2f, 0.3f + (float)rng.NextDouble() * 0.08f, 0.05f + (float)rng.NextDouble() * 0.3f);
                Part(r, PrimitiveType.Cube, "Tuft", p, new Vector3(0.03f, 0.1f, 0.03f), darkM,
                    new Vector3((float)rng.NextDouble() * 50f - 25f, (float)rng.NextDouble() * 90f, Mathf.Sin(a) * -40f), false);
            }
        }
        return rig;
    }

    // ------------------------------------------------------------------ pile goblin

    /// <summary>
    /// Grumpy little gremlin (~0.95 m): round green body, huge floppy ears, bulbous nose, googly eyes that squint
    /// shut when asleep, a striped nightcap with a pom-pom, a snot bubble, and a trusty plunger for bonking.
    /// Colors/name come from the SquatterProfile so the look is swappable.
    /// </summary>
    public static CritterRig BuildGoblin(GameObject root, SquatterProfile look)
    {
        look ??= SquatterProfile.Current;
        var rig = NewRig(root, 0.5f, 0.45f);
        var skinM = Mat(look.skin, 0.25f);
        var bellyM = Mat(look.belly, 0.2f);
        var noseM = Mat(look.nose, 0.45f);
        var darkM = Mat(new Color(look.skin.r * 0.6f, look.skin.g * 0.6f, look.skin.b * 0.6f, 1f), 0.2f);
        var capA = Mat(look.capA, 0.2f);
        var capB = Mat(look.capB, 0.2f);
        var earIn = Mat(look.earInner, 0.3f);

        rig.rear = Node(rig.juice, "Hips", new Vector3(0f, 0.18f, 0f));
        var r = rig.rear;
        rig.body = Part(r, PrimitiveType.Sphere, "Body", new Vector3(0f, 0.2f, 0f), new Vector3(0.54f, 0.5f, 0.48f), skinM);
        Part(r, PrimitiveType.Sphere, "Belly", new Vector3(0f, 0.16f, 0.1f), new Vector3(0.42f, 0.38f, 0.34f), bellyM);
        Part(r, PrimitiveType.Sphere, "BellyButton", new Vector3(0f, 0.14f, 0.27f), new Vector3(0.03f, 0.03f, 0.02f), darkM, default, false);
        // Pajama shorts (matching the nightcap, obviously).
        Part(r, PrimitiveType.Cylinder, "Shorts", new Vector3(0f, 0.02f, 0f), new Vector3(0.5f, 0.07f, 0.44f), capA);

        // Legs + big flat feet
        rig.legL = Node(r, "LegL", new Vector3(-0.13f, 0.0f, 0f));
        rig.legR = Node(r, "LegR", new Vector3(0.13f, 0.0f, 0f));
        foreach (var leg in new[] { rig.legL, rig.legR })
        {
            Part(leg, PrimitiveType.Capsule, "Shin", new Vector3(0f, -0.08f, 0f), new Vector3(0.11f, 0.08f, 0.11f), skinM);
            Part(leg, PrimitiveType.Sphere, "Foot", new Vector3(0f, -0.16f, 0.06f), new Vector3(0.17f, 0.07f, 0.26f), darkM);
        }

        // Arms (pivot at the shoulder, hanging down)
        rig.armL = Node(r, "ArmL", new Vector3(-0.26f, 0.3f, 0.02f), new Vector3(0f, 0f, -20f));
        rig.armR = Node(r, "ArmR", new Vector3(0.26f, 0.3f, 0.02f), new Vector3(0f, 0f, 20f));
        foreach (var arm in new[] { rig.armL, rig.armR })
        {
            Part(arm, PrimitiveType.Capsule, "Upper", new Vector3(0f, -0.11f, 0f), new Vector3(0.09f, 0.12f, 0.09f), skinM);
            Part(arm, PrimitiveType.Sphere, "Hand", new Vector3(0f, -0.23f, 0.01f), new Vector3(0.1f, 0.1f, 0.1f), skinM);
        }
        // The Plunger of Justice
        rig.weapon = Node(rig.armR, "Plunger", new Vector3(0f, -0.23f, 0.02f), new Vector3(70f, 0f, 0f));
        Part(rig.weapon, PrimitiveType.Cylinder, "Stick", new Vector3(0f, 0.2f, 0f), new Vector3(0.035f, 0.24f, 0.035f), Mat(new Color(0.62f, 0.42f, 0.22f), 0.3f));
        Part(rig.weapon, PrimitiveType.Sphere, "Cup", new Vector3(0f, 0.46f, 0f), new Vector3(0.2f, 0.11f, 0.2f), Mat(new Color(0.85f, 0.12f, 0.14f), 0.55f));

        // Head
        rig.head = Node(r, "Head", new Vector3(0f, 0.52f, 0.03f));
        var h = rig.head;
        Part(h, PrimitiveType.Sphere, "Skull", Vector3.zero, new Vector3(0.48f, 0.42f, 0.44f), skinM);
        rig.nose = Part(h, PrimitiveType.Sphere, "Nose", new Vector3(0f, -0.03f, 0.23f), new Vector3(0.15f, 0.13f, 0.17f), noseM);
        Part(h, PrimitiveType.Sphere, "Wart", new Vector3(0.05f, 0.01f, 0.29f), new Vector3(0.03f, 0.03f, 0.03f), darkM, default, false);
        rig.mouthShape = Part(h, PrimitiveType.Sphere, "Mouth", new Vector3(0f, -0.13f, 0.18f), new Vector3(0.16f, 0.035f, 0.06f), Mat(new Color(0.18f, 0.05f, 0.06f), 0.4f), default, false);
        Part(h, PrimitiveType.Cube, "Snaggletooth", new Vector3(0.035f, -0.115f, 0.2f), new Vector3(0.03f, 0.04f, 0.02f), Mat(new Color(1f, 0.97f, 0.85f), 0.6f), default, false);
        rig.mouth = Node(h, "MouthAnchor", new Vector3(0f, -0.13f, 0.22f));
        rig.eyes.Add(Eye(h, "EyeL", new Vector3(-0.1f, 0.07f, 0.17f), 0.14f, PupilBlack, 0f, null, 0.2f));
        rig.eyes.Add(Eye(h, "EyeR", new Vector3(0.1f, 0.06f, 0.17f), 0.12f, PupilBlack, 0f, null, 0.2f));
        Part(h, PrimitiveType.Cube, "BrowL", new Vector3(-0.1f, 0.15f, 0.18f), new Vector3(0.12f, 0.025f, 0.03f), darkM, new Vector3(0f, 0f, -18f), false);
        Part(h, PrimitiveType.Cube, "BrowR", new Vector3(0.1f, 0.15f, 0.18f), new Vector3(0.12f, 0.025f, 0.03f), darkM, new Vector3(0f, 0f, 18f), false);

        // Big floppy ears
        float es = look.earSize;
        for (int s = -1; s <= 1; s += 2)
        {
            var ear = Node(h, s < 0 ? "EarL" : "EarR", new Vector3(0.21f * s, 0.05f, -0.02f), new Vector3(0f, -15f * s, -15f * s));
            Part(ear, PrimitiveType.Sphere, "Outer", new Vector3(0.17f * s * es, 0f, 0f), new Vector3(0.38f * es, 0.13f * es, 0.2f * es), skinM);
            Part(ear, PrimitiveType.Sphere, "Inner", new Vector3(0.17f * s * es, 0.005f, 0.035f), new Vector3(0.28f * es, 0.08f * es, 0.12f * es), earIn, default, false);
            rig.ears.Add(ear);
            rig.earRest.Add(ear.localRotation);
        }

        // Striped nightcap: a beanie dome over the skull, then a long floppy cone curling back, pom-pom on the end.
        Part(h, PrimitiveType.Sphere, "CapDome", new Vector3(0f, 0.1f, -0.03f), new Vector3(0.47f, 0.3f, 0.45f), capA, new Vector3(-10f, 0f, 0f));
        Part(h, PrimitiveType.Cylinder, "CapBand", new Vector3(0f, 0.08f, -0.02f), new Vector3(0.5f, 0.035f, 0.48f), capB, new Vector3(-10f, 0f, 0f));
        var seg = Node(h, "Cap0", new Vector3(0f, 0.2f, -0.06f), new Vector3(-22f, 0f, 6f));
        float w = 0.26f;
        for (int i = 0; i < 9; i++)
        {
            Part(seg, PrimitiveType.Cylinder, "Stripe", new Vector3(0f, 0.03f, 0f), new Vector3(w, 0.04f, w), (i / 2) % 2 == 0 ? capA : capB);
            rig.cap.Add(seg);
            rig.capRest.Add(seg.localRotation);
            seg = Node(seg, "Cap" + (i + 1), new Vector3(0f, 0.052f, 0f), new Vector3(-13f, 0f, 3f));
            w *= 0.86f;
        }
        rig.capTip = seg;
        Part(seg, PrimitiveType.Sphere, "PomPom", new Vector3(0f, 0.035f, 0f), new Vector3(0.12f, 0.12f, 0.12f), capB);
        rig.zAnchor = Node(h, "ZAnchor", new Vector3(0.18f, 0.32f, 0f));

        // Snot bubble (hidden until asleep)
        // Snot bubble hanging off one nostril (hidden until asleep), with a shiny highlight so it reads as a bubble.
        rig.bubble = Part(h, PrimitiveType.Sphere, "SnotBubble", new Vector3(0.07f, -0.1f, 0.3f), Vector3.one * 0.001f,
            Mat(new Color(0.74f, 0.95f, 0.72f), 0.95f, 0.12f), default, false);
        Part(rig.bubble, PrimitiveType.Sphere, "Shine", new Vector3(-0.2f, 0.24f, 0.3f), Vector3.one * 0.22f, Unlit(Color.white), default, false);
        return rig;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        foreach (var m in litCache.Values) if (m != null) UnityEngine.Object.DestroyImmediate(m);
        foreach (var m in unlitCache.Values) if (m != null) UnityEngine.Object.DestroyImmediate(m);
        litCache.Clear();
        unlitCache.Clear();
    }
}
