using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>What the Hoverhog's model needs to know each frame.</summary>
public struct HogLook
{
    public bool flown, landed, dumping, beaming, boosting, gripping;
    public float battery, yaw;
    /// <summary>Velocity in the Hoverhog's own frame (x right, z forward).</summary>
    public Vector3 localVelocity;
    /// <summary>0 = claw stowed, 1 = at <see cref="clawTarget"/>.</summary>
    public float clawReach;
    public Vector3 clawTarget;
    public float beamLength, beamRadius;
}

/// <summary>
/// The Hoverhog's body (Docs/Progression.md §6.10): a fat lathed fuselage in Scrapazon orange over a cream chin, a ribbed
/// steel compactor drum slung underneath on hydraulic rams, four ducted fans with two contra-rotating props each, a
/// framed canopy, and the pig: snout, googly headlight eyes, ears that fold back at speed and a curly tail. Renderer
/// only, built from <see cref="HogMesh"/> shapes; the owned upgrade levels change what is bolted on.
/// </summary>
public sealed class HogModel
{
    public Transform Root { get; private set; }
    public Transform ClawStow { get; private set; }
    public HogLevels Levels { get; private set; }

    readonly List<Mesh> meshes = new List<Mesh>();
    readonly List<Material> ownMaterials = new List<Material>();
    readonly Transform[] rotors = new Transform[8];
    readonly Transform[] ears = new Transform[2];
    readonly Transform[] pupils = new Transform[2];
    readonly Renderer[] leds = new Renderer[5];
    readonly List<Transform> flames = new List<Transform>();
    Transform tail, bayLeft, bayRight, drum, cable, clawHub, beam, dish;
    Transform[] fingers, knuckles;
    Material ledOn, ledLow, ledOff, beamMat;
    float rotorSpeed, bayOpen, earFold, beamShown, flameShown;
    Vector2 pupilAt;

    const float BodyY = 2.3f, DrumY = 1.22f;

    // ------------------------------------------------------------------ build

    public static HogModel Build(Transform parent, HogLevels levels)
    {
        var m = new HogModel { Levels = levels };
        m.Root = ShopModKit.Pivot("Model", parent, Vector3.zero);
        m.BuildBody();
        m.BuildDrum(levels.belly);
        m.BuildFans(levels.rotors);
        m.BuildGear();
        m.BuildFace();
        m.BuildBatteries(levels.battery);
        m.BuildClaw(parent, levels.claw);
        foreach (var r in parent.GetComponentsInChildren<Renderer>(true)) r.gameObject.layer = 2;
        return m;
    }

    public void Destroy()
    {
        if (Root != null) Object.Destroy(Root.gameObject);
        if (cable != null) Object.Destroy(cable.gameObject);
        if (clawHub != null) Object.Destroy(clawHub.gameObject);
        if (beam != null) Object.Destroy(beam.gameObject);
        foreach (var mesh in meshes) if (mesh != null) Object.Destroy(mesh);
        foreach (var mat in ownMaterials) if (mat != null) Object.Destroy(mat);
        meshes.Clear();
        ownMaterials.Clear();
    }

    Transform Part(string name, Transform parent, Mesh mesh, Material mat, Vector3 pos, Vector3 euler = default)
    {
        meshes.Add(mesh);
        var go = new GameObject(name) { layer = 2 };
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localRotation = Quaternion.Euler(euler);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        return go.transform;
    }

    static Material Orange => ShopModKit.Lit("mat/shop_orange", new Color(1f, 0.6f, 0f), 0.4f);
    static Material Cream => ShopModKit.Lit("mat/hog_cream", new Color(0.98f, 0.93f, 0.82f), 0.35f);
    static Material Black => ShopModKit.Lit("mat/shop_headlamp_body", new Color(0.16f, 0.16f, 0.2f), 0.4f);
    static Material White => ShopModKit.Lit("mat/shop_white", new Color(0.97f, 0.97f, 0.97f), 0.6f);
    static Material Chrome => ShopModKit.Lit("mat/shop_chrome", new Color(0.86f, 0.88f, 0.92f), 0.85f, 0.9f);
    static Material Pink => ShopModKit.Lit("mat/hog_pink", new Color(1f, 0.62f, 0.72f), 0.45f);
    static Material DeepPink => ShopModKit.Lit("mat/hog_pink_deep", new Color(0.86f, 0.4f, 0.54f), 0.4f);
    static Material Steel => ShopModKit.Lit("mat/hog_belly_steel", new Color(0.36f, 0.4f, 0.45f), 0.6f, 0.7f);
    static Material DarkSteel => ShopModKit.Lit("mat/hog_steel_dark", new Color(0.22f, 0.24f, 0.28f), 0.5f, 0.6f);
    static Material Glass => ShopModKit.Lit("mat/hog_glass", new Color(0.1f, 0.2f, 0.3f), 0.95f, 0.3f);
    static Material Hazard => ShopModKit.Lit("mat/hog_hazard", new Color(1f, 0.85f, 0.1f), 0.4f);

    void BuildBody()
    {
        // The fuselage: one hull, cut lengthwise into an orange back and a cream chin, with a dark waistline between.
        var hull = new[]
        {
            new Vector2(-2.05f, 0.2f), new Vector2(-1.75f, 0.62f), new Vector2(-1.05f, 1f), new Vector2(0f, 1.14f),
            new Vector2(0.9f, 1.06f), new Vector2(1.5f, 0.84f), new Vector2(1.9f, 0.5f),
        };
        Vector3 at = new Vector3(0f, BodyY, 0f);
        Part("Hull top", Root, HogMesh.Lathe("hog_hull_top", hull, 10, true, true, 0.86f, -18f, 198f), Orange, at);
        Part("Hull chin", Root, HogMesh.Lathe("hog_hull_chin", hull, 6, true, true, 0.86f, 198f, 342f), Cream, at);
        var waist = new Vector2[hull.Length];
        for (int i = 0; i < hull.Length; i++) waist[i] = new Vector2(hull[i].x, hull[i].y + 0.025f);
        Part("Waistline L", Root, HogMesh.Lathe("hog_waist_l", waist, 1, false, false, 0.86f, 190f, 200f), Black, at);
        Part("Waistline R", Root, HogMesh.Lathe("hog_waist_r", waist, 1, false, false, 0.86f, -20f, -10f), Black, at);

        // A hazard collar behind the canopy and a spine fin with the antenna
        var collar = new[] { new Vector2(-0.5f, 1.15f), new Vector2(-0.28f, 1.165f) };
        Part("Collar", Root, HogMesh.Lathe("hog_collar", collar, 10, false, false, 0.86f, -18f, 198f), Hazard, at);
        var fin = new[] { new Vector2(-0.55f, 0f), new Vector2(0.45f, 0f), new Vector2(-0.2f, 0.55f), new Vector2(-0.6f, 0.5f) };
        Part("Spine fin", Root, HogMesh.Prism("hog_fin", fin, 0.1f), Orange, new Vector3(0f, BodyY + 0.78f, -1.15f), new Vector3(0f, -90f, 0f));
        ShopModKit.Prim(PrimitiveType.Cylinder, "Antenna", Root, Black, new Vector3(0f, BodyY + 1.75f, -1.3f), new Vector3(0.03f, 0.55f, 0.03f));
        var flag = new[] { new Vector2(0f, 0f), new Vector2(0.34f, 0.1f), new Vector2(0f, 0.2f) };
        Part("Antenna flag", Root, HogMesh.Prism("hog_flag", flag, 0.02f), Hazard, new Vector3(0f, BodyY + 2.08f, -1.3f), new Vector3(0f, 90f, 0f));

        // Canopy: a faceted glass bubble on a chrome sill, with one roll hoop over it
        var dome = new[] { new Vector2(0f, 0.78f), new Vector2(0.28f, 0.74f), new Vector2(0.52f, 0.56f), new Vector2(0.68f, 0.26f), new Vector2(0.72f, 0f) };
        var canopy = Part("Canopy", Root, HogMesh.Lathe("hog_canopy", dome, 8, false, true), Glass, new Vector3(0f, BodyY + 0.72f, 0.15f), new Vector3(-90f, 0f, 0f));
        canopy.localScale = new Vector3(0.8f, 1.05f, 0.9f);
        var sill = Part("Canopy sill", Root, HogMesh.Torus("hog_sill", 0.79f, 0.055f, 8, 5), Chrome, new Vector3(0f, BodyY + 0.74f, 0.15f));
        sill.localScale = new Vector3(0.8f, 1f, 1.05f);
        var hoop = Part("Canopy hoop", Root, HogMesh.Torus("hog_hoop", 0.76f, 0.04f, 6, 5, 0f, 180f), Chrome, new Vector3(0f, BodyY + 0.74f, 0.15f), new Vector3(-90f, 0f, 0f));
        hoop.localScale = new Vector3(0.8f, 1f, 0.9f);

        // Name plates on the flanks and a plate on the stern
        for (int s = -1; s <= 1; s += 2)
        {
            ShopModKit.Prim(PrimitiveType.Cube, "Name plate", Root, Black, new Vector3(s * 1.07f, BodyY - 0.02f, 0.5f), new Vector3(0.04f, 0.34f, 1.5f), new Vector3(0f, 0f, s * -6f));
            var name = WorkshopModels.MakeLabel(Root, "Name", "HOVERHOG H-1", 2.1f, Hazard.color);
            name.transform.localPosition = new Vector3(s * 1.1f, BodyY - 0.02f, 0.5f);
            name.transform.localRotation = Quaternion.Euler(0f, s * -90f, 0f);
        }
        ShopModKit.Prim(PrimitiveType.Cube, "Stern plate", Root, White, new Vector3(0f, BodyY - 0.3f, -1.83f), new Vector3(0.62f, 0.26f, 0.04f), new Vector3(-22f, 0f, 0f));
        var plate = WorkshopModels.MakeLabel(Root, "Stern plate text", "OINK 1", 1.5f, Black.color);
        plate.transform.localPosition = new Vector3(0f, BodyY - 0.3f, -1.86f);
        plate.transform.localRotation = Quaternion.Euler(22f, 180f, 0f);

        // Battery gauge: five lamps along the spine, behind the fin
        ledOn = ShopModKit.Emissive("mat/hog_lamp_green", new Color(0.3f, 1f, 0.35f), 2.5f);
        ledLow = ShopModKit.Emissive("mat/hog_lamp_red", new Color(1f, 0.15f, 0.1f), 3f);
        ledOff = DarkSteel;
        for (int i = 0; i < leds.Length; i++)
        {
            var led = ShopModKit.Prim(PrimitiveType.Cube, "Battery lamp", Root, ledOn,
                new Vector3(-0.36f + i * 0.18f, BodyY + 0.62f, -1.62f), new Vector3(0.12f, 0.1f, 0.12f), new Vector3(-24f, 0f, 0f));
            leds[i] = led.GetComponent<Renderer>();
        }
    }

    void BuildDrum(int belly)
    {
        // The compactor: a ribbed steel drum that gets fatter with every Belly level, held by four hydraulic rams
        float fat = 1f + 0.085f * Mathf.Clamp(belly, 0, 3);
        drum = ShopModKit.Pivot("Compactor drum", Root, new Vector3(0f, DrumY, -0.15f));
        var shell = new[]
        {
            new Vector2(-1.32f, 0.3f), new Vector2(-1.2f, 0.74f), new Vector2(-1.02f, 0.88f), new Vector2(0.95f, 0.88f),
            new Vector2(1.13f, 0.74f), new Vector2(1.25f, 0.3f),
        };
        var body = Part("Drum", drum, HogMesh.Lathe("hog_drum", shell, 12, true, true), Steel, Vector3.zero);
        body.localScale = new Vector3(fat, fat, 1f);
        int ribs = 3 + (belly >= 2 ? 1 : 0);
        for (int i = 0; i < ribs; i++)
        {
            float z = Mathf.Lerp(-0.85f, 0.78f, ribs == 1 ? 0.5f : i / (float)(ribs - 1));
            var rib = Part("Drum rib", drum, HogMesh.Torus("hog_rib", 0.9f, 0.055f, 12, 5), i % 2 == 0 ? Hazard : DarkSteel, new Vector3(0f, 0f, z), new Vector3(90f, 0f, 0f));
            rib.localScale = new Vector3(fat, 1f, fat);
        }
        for (int e = -1; e <= 1; e += 2) // bolted end caps
        {
            float z = e < 0 ? -1.33f : 1.26f;
            for (int i = 0; i < 6; i++)
            {
                float a = i * Mathf.PI / 3f;
                ShopModKit.Prim(PrimitiveType.Sphere, "Bolt", drum, Chrome, new Vector3(Mathf.Cos(a) * 0.2f, Mathf.Sin(a) * 0.2f, z), Vector3.one * 0.07f);
            }
        }
        for (int s = -1; s <= 1; s += 2)
            for (int z = -1; z <= 1; z += 2)
            {
                Vector3 top = new Vector3(s * 0.98f, BodyY - 0.28f, z * 0.8f - 0.15f), foot = new Vector3(s * 0.84f * fat, DrumY + 0.2f, z * 0.8f - 0.15f);
                Part("Ram sleeve", Root, HogMesh.Tube("hog_ram", new[] { top, Vector3.Lerp(top, foot, 0.55f) }, new[] { 0.085f }, 6), DarkSteel, Vector3.zero);
                Part("Ram rod", Root, HogMesh.Tube("hog_rod", new[] { Vector3.Lerp(top, foot, 0.5f), foot }, new[] { 0.05f }, 6), Chrome, Vector3.zero);
            }
        // Bomb-bay doors under the drum
        float under = -0.9f * fat;
        bayLeft = ShopModKit.Pivot("Bay hinge L", drum, new Vector3(-0.5f, under, 0f));
        ShopModKit.Prim(PrimitiveType.Cube, "Bay door", bayLeft, DarkSteel, new Vector3(0.25f, 0f, 0f), new Vector3(0.5f, 0.05f, 1.6f));
        ShopModKit.Prim(PrimitiveType.Cube, "Bay stripe", bayLeft, Hazard, new Vector3(0.44f, -0.005f, 0f), new Vector3(0.1f, 0.055f, 1.6f));
        bayRight = ShopModKit.Pivot("Bay hinge R", drum, new Vector3(0.5f, under, 0f));
        ShopModKit.Prim(PrimitiveType.Cube, "Bay door", bayRight, DarkSteel, new Vector3(-0.25f, 0f, 0f), new Vector3(0.5f, 0.05f, 1.6f));
        ShopModKit.Prim(PrimitiveType.Cube, "Bay stripe", bayRight, Hazard, new Vector3(-0.44f, -0.005f, 0f), new Vector3(0.1f, 0.055f, 1.6f));
    }

    void BuildFans(int level)
    {
        // Four ducted fans on bent booms, two contra-rotating props in each. Rotors 1 adds chrome lips, 2 a second
        // stator ring, 3 a pair of jet pods on the stern.
        int k = 0;
        for (int sx = -1; sx <= 1; sx += 2)
            for (int sz = -1; sz <= 1; sz += 2)
            {
                Vector3 root = new Vector3(sx * 0.8f, BodyY + 0.42f, sz * 0.95f - 0.1f);
                Vector3 hub = new Vector3(sx * 2.35f, BodyY + 0.62f, sz * 1.95f - 0.1f);
                Vector3 elbow = Vector3.Lerp(root, hub, 0.45f) + Vector3.up * 0.3f;
                Vector3 rim = hub + (root - hub).normalized * 1.0f;
                Part("Boom", Root, HogMesh.Tube("hog_boom", new[] { root, elbow, rim }, new[] { 0.17f, 0.13f, 0.1f }, 6), DarkSteel, Vector3.zero);
                var fan = ShopModKit.Pivot("Fan", Root, hub);
                Part("Duct", fan, HogMesh.Torus("hog_duct", 1.02f, 0.15f, 12, 6), Orange, Vector3.zero).localScale = new Vector3(1f, 1.7f, 1f);
                Part("Duct stripe", fan, HogMesh.Torus("hog_duct_stripe", 1.16f, 0.035f, 12, 4), sx < 0 ? Hazard : Black, Vector3.zero);
                if (level >= 1) Part("Duct lip", fan, HogMesh.Torus("hog_duct_lip", 1.0f, 0.05f, 12, 4), Chrome, new Vector3(0f, 0.25f, 0f));
                if (level >= 2) Part("Stator ring", fan, HogMesh.Torus("hog_stator", 0.55f, 0.03f, 10, 4), Chrome, new Vector3(0f, -0.12f, 0f));
                ShopModKit.Prim(PrimitiveType.Cylinder, "Hub", fan, Chrome, Vector3.zero, new Vector3(0.3f, 0.2f, 0.3f));
                ShopModKit.Prim(PrimitiveType.Sphere, "Spinner", fan, Hazard, new Vector3(0f, 0.2f, 0f), new Vector3(0.24f, 0.2f, 0.24f));
                for (int i = 0; i < 3; i++)
                    ShopModKit.Prim(PrimitiveType.Cube, "Stator", fan, DarkSteel, Quaternion.Euler(0f, i * 120f + 30f, 0f) * new Vector3(0f, -0.14f, 0.5f), new Vector3(0.05f, 0.05f, 0.95f), new Vector3(0f, i * 120f + 30f, 0f));
                var blade = new[] { new Vector2(0.1f, -0.07f), new Vector2(0.86f, -0.1f), new Vector2(0.9f, 0.06f), new Vector2(0.1f, 0.04f) };
                for (int deck = 0; deck < 2; deck++)
                {
                    var rotor = ShopModKit.Pivot("Rotor", fan, new Vector3(0f, deck == 0 ? 0.08f : -0.05f, 0f));
                    for (int i = 0; i < 3; i++)
                        Part("Blade", rotor, HogMesh.Prism("hog_blade", blade, 0.025f), deck == 0 ? Black : Hazard, Vector3.zero, new Vector3(90f + (deck == 0 ? 14f : -14f), i * 120f + deck * 60f, 0f));
                    rotors[k++] = rotor;
                }
                // A running light under each duct: red to port, green to starboard
                var nav = sx < 0 ? ShopModKit.Emissive("mat/hog_lamp_red", new Color(1f, 0.15f, 0.1f), 3f) : ShopModKit.Emissive("mat/hog_lamp_green", new Color(0.3f, 1f, 0.35f), 2.5f);
                ShopModKit.Prim(PrimitiveType.Sphere, "Running light", fan, nav, new Vector3(sx * 1.05f, -0.28f, 0f), Vector3.one * 0.13f);
            }
        if (level < 3) return;
        var pod = new[] { new Vector2(-0.62f, 0.14f), new Vector2(-0.5f, 0.22f), new Vector2(0.2f, 0.25f), new Vector2(0.48f, 0.2f), new Vector2(0.58f, 0.12f) };
        var flameMat = ShopModKit.Emissive("mat/hog_jet_flame", new Color(1f, 0.55f, 0.1f), 4f);
        for (int s = -1; s <= 1; s += 2)
        {
            Vector3 at = new Vector3(s * 0.95f, BodyY + 0.5f, -1.25f);
            Part("Jet pod", Root, HogMesh.Lathe("hog_pod", pod, 8, false, false), Chrome, at);
            Part("Jet intake", Root, HogMesh.Lathe("hog_pod_core", new[] { new Vector2(-0.6f, 0.1f), new Vector2(0.56f, 0.1f) }, 6, true, true), Black, at);
            ShopModKit.Prim(PrimitiveType.Cube, "Jet pylon", Root, DarkSteel, at + new Vector3(-s * 0.2f, -0.12f, 0f), new Vector3(0.3f, 0.08f, 0.5f));
            var flame = Part("Jet flame", Root, HogMesh.Lathe("hog_flame", new[] { new Vector2(-1.5f, 0.01f), new Vector2(-0.3f, 0.13f), new Vector2(0f, 0.1f) }, 6, false, false), flameMat, at + Vector3.back * 0.6f);
            flame.localScale = new Vector3(1f, 1f, 0.01f);
            flames.Add(flame);
        }
    }

    void BuildGear()
    {
        // Skids with turned-up toes on splayed legs, cross-braced
        for (int s = -1; s <= 1; s += 2)
        {
            float x = s * 1.18f;
            Part("Skid", Root, HogMesh.Tube("hog_skid", new[]
            {
                new Vector3(x, 0.13f, -1.75f), new Vector3(x, 0.07f, -1.5f), new Vector3(x, 0.07f, 1.35f), new Vector3(x, 0.16f, 1.65f), new Vector3(x, 0.36f, 1.85f),
            }, new[] { 0.075f }, 6), Black, Vector3.zero);
            for (int z = -1; z <= 1; z += 2)
                Part("Leg", Root, HogMesh.Tube("hog_leg", new[] { new Vector3(s * 0.62f, DrumY + 0.5f, z * 0.95f - 0.1f), new Vector3(x, 0.09f, z * 1.0f - 0.1f) }, new[] { 0.07f, 0.055f }, 6), DarkSteel, Vector3.zero);
            ShopModKit.Prim(PrimitiveType.Cube, "Skid pad", Root, Hazard, new Vector3(x, 0.07f, -0.1f), new Vector3(0.2f, 0.1f, 0.5f));
        }
    }

    void BuildFace()
    {
        // The snout doubles as the intake: a stubby pink barrel with two nostrils
        var snout = new[] { new Vector2(0f, 0.5f), new Vector2(0.34f, 0.46f), new Vector2(0.42f, 0.38f) };
        Part("Snout", Root, HogMesh.Lathe("hog_snout", snout, 10, false, true, 0.82f), Pink, new Vector3(0f, BodyY - 0.05f, 1.84f));
        for (int s = -1; s <= 1; s += 2)
            ShopModKit.Prim(PrimitiveType.Sphere, "Nostril", Root, DeepPink, new Vector3(s * 0.15f, BodyY - 0.03f, 2.26f), new Vector3(0.14f, 0.2f, 0.06f));
        // A crooked grin on the chin
        Part("Grin", Root, HogMesh.Torus("hog_grin", 0.5f, 0.035f, 6, 4, 205f, 335f), Black, new Vector3(0.04f, BodyY - 0.35f, 1.75f), new Vector3(-58f, 0f, 4f));

        // Headlight eyes on stalks, with pupils that slosh the way it is going
        var lens = ShopModKit.Emissive("mat/hog_eye", new Color(1f, 1f, 0.95f), 0.6f);
        for (int s = -1; s <= 1; s += 2)
        {
            int i = s < 0 ? 0 : 1;
            Vector3 at = new Vector3(s * 0.46f, BodyY + 0.62f, 1.42f);
            ShopModKit.Prim(PrimitiveType.Cylinder, "Eye stalk", Root, Orange, at + new Vector3(0f, -0.2f, -0.05f), new Vector3(0.2f, 0.16f, 0.2f));
            Part("Eye rim", Root, HogMesh.Torus("hog_eye_rim", 0.25f, 0.045f, 10, 4), Chrome, at + Vector3.forward * 0.1f, new Vector3(90f, 0f, 0f));
            ShopModKit.Prim(PrimitiveType.Sphere, "Eye", Root, lens, at, new Vector3(0.5f, 0.5f, 0.42f));
            pupils[i] = ShopModKit.Prim(PrimitiveType.Sphere, "Pupil", Root, Black, at + Vector3.forward * 0.19f, new Vector3(0.2f, 0.22f, 0.08f));
            // Ears: soft triangles that stand up at a hover and fold back at speed
            var ear = ShopModKit.Pivot("Ear", Root, new Vector3(s * 0.62f, BodyY + 0.72f, 0.98f));
            var shape = new[] { new Vector2(-0.24f, 0f), new Vector2(0.24f, 0f), new Vector2(0.08f * s, 0.56f) };
            Part("Ear flap", ear, HogMesh.Prism("hog_ear", shape, 0.07f), Pink, Vector3.zero, new Vector3(0f, s * 24f, 0f));
            var inner = new[] { new Vector2(-0.13f, 0.06f), new Vector2(0.13f, 0.06f), new Vector2(0.05f * s, 0.4f) };
            Part("Ear inside", ear, HogMesh.Prism("hog_ear_in", inner, 0.02f), DeepPink, Quaternion.Euler(0f, s * 24f, 0f) * new Vector3(0f, 0f, 0.035f), new Vector3(0f, s * 24f, 0f));
            ears[i] = ear;
        }

        // A curly tail on the stern
        var curl = new Vector3[14];
        var thick = new float[14];
        for (int i = 0; i < curl.Length; i++)
        {
            float t = i / (float)(curl.Length - 1), a = t * Mathf.PI * 3.4f;
            curl[i] = new Vector3(Mathf.Sin(a) * 0.17f, (1f - Mathf.Cos(a)) * 0.17f + t * 0.1f, -t * 0.55f);
            thick[i] = Mathf.Lerp(0.07f, 0.025f, t);
        }
        tail = Part("Tail", Root, HogMesh.Tube("hog_tail", curl, thick, 5), Pink, new Vector3(0f, BodyY + 0.12f, -2.0f));
    }

    void BuildBatteries(int level)
    {
        // Battery 1 and 2 strap packs to the flanks; Battery 3 swaps the stern for a small, worrying reactor
        var cell = ShopModKit.Emissive("mat/hog_cell", new Color(0.3f, 0.9f, 1f), 1.6f);
        int packs = Mathf.Clamp(level, 0, 2);
        for (int s = -1; s <= 1; s += 2)
            for (int i = 0; i < packs; i++)
            {
                Vector3 at = new Vector3(s * (0.98f - i * 0.02f), BodyY + 0.34f, -0.95f - i * 0.5f);
                ShopModKit.Prim(PrimitiveType.Cube, "Battery pack", Root, DarkSteel, at, new Vector3(0.2f, 0.34f, 0.42f), new Vector3(0f, s * 10f, s * -24f));
                ShopModKit.Prim(PrimitiveType.Cube, "Battery cell", Root, cell, at + new Vector3(s * 0.1f, 0.03f, 0f), new Vector3(0.03f, 0.2f, 0.3f), new Vector3(0f, s * 10f, s * -24f));
                ShopModKit.Prim(PrimitiveType.Cube, "Battery strap", Root, Hazard, at, new Vector3(0.22f, 0.36f, 0.06f), new Vector3(0f, s * 10f, s * -24f));
            }
        if (level < 3) return;
        var goo = ShopModKit.Emissive("mat/hog_reactor", new Color(0.45f, 1f, 0.2f), 3f);
        Vector3 core = new Vector3(0f, BodyY + 0.86f, -0.55f);
        var jar = new[] { new Vector2(-0.3f, 0.2f), new Vector2(-0.24f, 0.27f), new Vector2(0.24f, 0.27f), new Vector2(0.3f, 0.2f) };
        Part("Reactor jar", Root, HogMesh.Lathe("hog_reactor", jar, 8, true, true), goo, core, new Vector3(-90f, 0f, 0f));
        for (int e = -1; e <= 1; e += 2)
            Part("Reactor band", Root, HogMesh.Torus("hog_reactor_band", 0.29f, 0.04f, 8, 4), Chrome, core + Vector3.up * e * 0.22f);
        Part("Reactor stack", Root, HogMesh.Tube("hog_stack", new[] { core + new Vector3(0.22f, 0.2f, -0.2f), core + new Vector3(0.3f, 0.75f, -0.32f) }, new[] { 0.06f, 0.08f }, 6), DarkSteel, Vector3.zero);
    }

    void BuildClaw(Transform owner, int level)
    {
        // The Claw hangs from a winch under the chin. From Claw 2 it is a tractor-beam dish instead.
        Vector3 stow = new Vector3(0f, BodyY - 1.02f, 1.5f);
        ClawStow = ShopModKit.Pivot("Claw stow", Root, stow);
        ShopModKit.Prim(PrimitiveType.Cylinder, "Winch", Root, Chrome, stow + Vector3.up * 0.16f, new Vector3(0.22f, 0.2f, 0.22f), new Vector3(0f, 0f, 90f));
        for (int s = -1; s <= 1; s += 2)
            ShopModKit.Prim(PrimitiveType.Cube, "Winch cheek", Root, DarkSteel, stow + new Vector3(s * 0.22f, 0.2f, 0f), new Vector3(0.05f, 0.34f, 0.3f));
        if (level >= 2)
        {
            var glow = ShopModKit.Emissive("mat/hog_beam_ring", new Color(0.45f, 1f, 0.75f), 3f);
            dish = ShopModKit.Pivot("Beam dish", Root, stow + Vector3.down * 0.02f);
            var bowl = new[] { new Vector2(0f, 0.16f), new Vector2(0.1f, 0.42f), new Vector2(0.16f, level >= 3 ? 0.78f : 0.6f) };
            Part("Dish", dish, HogMesh.Lathe("hog_dish", bowl, 10, true, false), Chrome, Vector3.zero, new Vector3(90f, 0f, 0f));
            Part("Dish ring", dish, HogMesh.Torus("hog_dish_ring", level >= 3 ? 0.76f : 0.58f, 0.045f, 10, 4), glow, new Vector3(0f, -0.17f, 0f));
            ShopModKit.Prim(PrimitiveType.Sphere, "Emitter", dish, glow, new Vector3(0f, -0.1f, 0f), Vector3.one * 0.2f);
            beamMat = new Material(ShopModKit.Glow("mat/hog_beam", new Color(0.5f, 1f, 0.8f, 0.3f)));
            ownMaterials.Add(beamMat);
            beam = Part("Beam", owner, HogMesh.Cone("hog_beam", 0.25f, 1f, 1f, 14), beamMat, Vector3.zero);
            beam.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            beam.gameObject.SetActive(false);
            return;
        }
        cable = ShopModKit.Prim(PrimitiveType.Cylinder, "Claw cable", owner, Black, Vector3.zero, new Vector3(0.05f, 0.1f, 0.05f));
        clawHub = ShopModKit.Pivot("Claw hub", owner, stow);
        ShopModKit.Prim(PrimitiveType.Cylinder, "Hub", clawHub, Chrome, Vector3.zero, new Vector3(0.3f, 0.07f, 0.3f));
        ShopModKit.Prim(PrimitiveType.Sphere, "Hub cap", clawHub, Hazard, new Vector3(0f, 0.08f, 0f), new Vector3(0.16f, 0.12f, 0.16f));
        int count = level >= 1 ? 4 : 3;
        fingers = new Transform[count];
        knuckles = new Transform[count];
        for (int i = 0; i < count; i++)
        {
            // Two-jointed fingers: an upper arm that swings out and a hooked tip that curls in
            var f = ShopModKit.Pivot("Finger", clawHub, Vector3.zero);
            ShopModKit.Prim(PrimitiveType.Cube, "Upper", f, Chrome, new Vector3(0f, -0.2f, 0.16f), new Vector3(0.08f, 0.46f, 0.07f), new Vector3(-38f, 0f, 0f));
            var kn = ShopModKit.Pivot("Knuckle", f, new Vector3(0f, -0.38f, 0.3f));
            ShopModKit.Prim(PrimitiveType.Sphere, "Joint", kn, DarkSteel, Vector3.zero, Vector3.one * 0.11f);
            ShopModKit.Prim(PrimitiveType.Cube, "Tip", kn, Chrome, new Vector3(0f, -0.2f, -0.08f), new Vector3(0.07f, 0.44f, 0.06f), new Vector3(22f, 0f, 0f));
            ShopModKit.Prim(PrimitiveType.Cube, "Hook", kn, Hazard, new Vector3(0f, -0.42f, -0.2f), new Vector3(0.07f, 0.06f, 0.16f));
            fingers[i] = f;
            knuckles[i] = kn;
        }
    }

    // ------------------------------------------------------------------ animate

    public Vector3 ClawTip => clawHub != null ? clawHub.position + Vector3.down * 0.6f : ClawStow.position;

    public void Animate(float dt, in HogLook look)
    {
        float wantSpin = !look.flown || look.battery <= 0f ? 0f : look.landed ? 480f : look.boosting ? 2400f : 1700f;
        rotorSpeed = Mathf.MoveTowards(rotorSpeed, wantSpin, 1400f * dt);
        for (int i = 0; i < rotors.Length; i++)
            if (rotors[i] != null) rotors[i].Rotate(0f, rotorSpeed * dt * ((i & 1) == 0 ? 1f : -1f), 0f, Space.Self);

        // Lean into the way it is going; bob a little in the air
        float ease = 1f - Mathf.Exp(-6f * dt);
        float pitch = Mathf.Clamp(look.localVelocity.z * 1.2f, -13f, 17f);
        float roll = Mathf.Clamp(-look.localVelocity.x * 1.3f, -12f, 12f);
        float bob = look.landed ? 0f : Mathf.Sin(Time.time * 2.3f) * 0.07f;
        Root.localRotation = Quaternion.Slerp(Root.localRotation, Quaternion.Euler(pitch, 0f, roll), ease);
        Root.localPosition = Vector3.Lerp(Root.localPosition, Vector3.up * bob, ease);

        // Ears stand up at a hover, fold back at speed, and twitch when it is parked
        float speed01 = Mathf.Clamp01(look.localVelocity.magnitude / 12f);
        float fold = look.flown ? Mathf.Lerp(8f, 62f, speed01) + Mathf.Sin(Time.time * 9f) * 3f * speed01 : 22f + Mathf.Max(0f, Mathf.Sin(Time.time * 0.7f) - 0.93f) * 260f;
        earFold = Mathf.Lerp(earFold, fold, ease);
        for (int i = 0; i < ears.Length; i++)
            if (ears[i] != null) ears[i].localRotation = Quaternion.Euler(-earFold, 0f, (i == 0 ? 1f : -1f) * (10f + earFold * 0.25f));
        if (tail != null) tail.localRotation = Quaternion.Euler(0f, Mathf.Sin(Time.time * (look.flown ? 7f : 1.6f)) * (look.flown ? 16f : 7f), 0f);

        // Pupils slosh towards where it is heading (and cross a little when the battery is flat)
        Vector2 want = look.battery <= 0f ? Vector2.zero : new Vector2(Mathf.Clamp(look.localVelocity.x * 0.012f, -0.09f, 0.09f), Mathf.Clamp(look.localVelocity.y * 0.02f, -0.09f, 0.09f));
        pupilAt = Vector2.Lerp(pupilAt, want, 1f - Mathf.Exp(-9f * dt));
        for (int i = 0; i < pupils.Length; i++)
        {
            if (pupils[i] == null) continue;
            float s = i == 0 ? -1f : 1f;
            float cross = look.battery <= 0f ? -s * 0.08f : 0f;
            pupils[i].localPosition = new Vector3(s * 0.46f + pupilAt.x + cross, BodyY + 0.62f + pupilAt.y, 1.61f);
        }

        bayOpen = Mathf.MoveTowards(bayOpen, look.dumping ? 1f : 0f, dt * 3f);
        if (bayLeft != null) bayLeft.localRotation = Quaternion.Euler(0f, 0f, -82f * bayOpen);
        if (bayRight != null) bayRight.localRotation = Quaternion.Euler(0f, 0f, 82f * bayOpen);

        int lit = Mathf.CeilToInt(Mathf.Clamp01(look.battery) * leds.Length - 0.001f);
        bool blink = look.battery <= 0.2f && Mathf.Repeat(Time.time * 3f, 1f) < 0.45f;
        for (int i = 0; i < leds.Length; i++)
        {
            if (leds[i] == null) continue;
            var mat = i >= lit || blink ? ledOff : look.battery <= 0.2f ? ledLow : ledOn;
            if (leds[i].sharedMaterial != mat) leds[i].sharedMaterial = mat;
        }

        flameShown = Mathf.MoveTowards(flameShown, look.boosting ? 1f : 0f, dt * 6f);
        for (int i = 0; i < flames.Count; i++)
            if (flames[i] != null) flames[i].localScale = new Vector3(1f, 1f, Mathf.Max(0.01f, flameShown * (0.85f + Mathf.Sin(Time.time * 40f + i) * 0.15f)));

        AnimateClaw(look);
        AnimateBeam(dt, look);
    }

    void AnimateClaw(in HogLook look)
    {
        if (clawHub == null || cable == null) return;
        Vector3 stowed = ClawStow.position;
        Vector3 hub = Vector3.Lerp(stowed, look.clawTarget + Vector3.up * 0.6f, look.clawReach);
        clawHub.position = hub;
        clawHub.rotation = Quaternion.Euler(0f, look.yaw, 0f);
        Vector3 span = hub - stowed;
        float len = span.magnitude;
        cable.gameObject.SetActive(len > 0.05f);
        if (len > 0.05f)
        {
            cable.position = stowed + span * 0.5f;
            cable.rotation = Quaternion.FromToRotation(Vector3.up, span / len);
            cable.localScale = new Vector3(0.05f, len * 0.5f, 0.05f);
        }
        float grip = look.gripping || look.clawReach > 0.97f ? 1f : 0f;
        float spread = Mathf.Lerp(look.clawReach > 0.02f ? 34f : 6f, -4f, grip);
        for (int i = 0; i < fingers.Length; i++)
        {
            if (fingers[i] == null) continue;
            fingers[i].localRotation = Quaternion.Euler(0f, i * 360f / fingers.Length, 0f) * Quaternion.Euler(-spread, 0f, 0f);
            knuckles[i].localRotation = Quaternion.Euler(Mathf.Lerp(-6f, 30f, grip), 0f, 0f);
        }
    }

    void AnimateBeam(float dt, in HogLook look)
    {
        if (beam == null) return;
        beamShown = Mathf.MoveTowards(beamShown, look.beaming ? 1f : 0f, dt * 4f);
        bool on = beamShown > 0.01f;
        if (beam.gameObject.activeSelf != on) beam.gameObject.SetActive(on);
        if (dish != null) dish.localRotation = Quaternion.Euler(0f, Time.time * (look.beaming ? 220f : 25f), 0f);
        if (!on) return;
        beam.position = ClawStow.position + Vector3.down * 0.15f;
        beam.rotation = Quaternion.Euler(0f, Time.time * 40f, 0f);
        float wobble = 1f + Mathf.Sin(Time.time * 11f) * 0.04f;
        beam.localScale = new Vector3(look.beamRadius * wobble, Mathf.Max(0.5f, look.beamLength), look.beamRadius * wobble);
        var c = beamMat.GetColor("_BaseColor");
        c.a = beamShown * (0.15f + Mathf.Sin(Time.time * 6f) * 0.04f);
        beamMat.SetColor("_BaseColor", c);
        beamMat.SetColor("_Color", c);
    }
}
