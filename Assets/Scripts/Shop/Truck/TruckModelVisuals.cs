using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Two vehicle bodies on the existing network truck. The mini is a real short-wheelbase, four-wheel cab-over:
/// its shell colliders, wheels, hopper, rider kit and upgrade mounts all use the same model-space conversion.
/// The NetworkObject, cargo and driver stay on the original root, so switching bodies never changes identity.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(-100)]
public sealed class TruckModelVisuals : MonoBehaviour
{
    public const string MiniRootName = "Mini cab-over truck";
    public static readonly Vector3 MiniScale = new Vector3(.82f, .90f, .72f);
    public int Model { get; private set; } = 1;
    public bool IsMini => Model == 0;
    public Vector3 ModelScale => IsMini ? MiniScale : Vector3.one;
    public Bounds BodyBounds
    {
        get
        {
            var canonical = TruckControllerV2.BodyBoxMetres;
            // The little bumper projects slightly beyond the legacy cab's nose; include it in impact sensors.
            if (IsMini) canonical.SetMinMax(new Vector3(-1.31f, .41f, -2.32f), new Vector3(1.31f, 3.31f, 3.94f));
            return new Bounds(Vector3.Scale(canonical.center, ModelScale), Vector3.Scale(canonical.size, ModelScale));
        }
    }

    struct Anchor
    {
        public Transform t;
        public Vector3 position, scale;
        public bool scaleShape;
    }
    struct Wheel
    {
        public WheelControl control;
        public WheelCollider collider;
        public Vector3 position, modelScale;
        public float radius;
        public JointSpring spring;
        public bool middle, enabled, modelActive, controlEnabled;
    }
    readonly List<Anchor> anchors = new List<Anchor>();
    readonly List<Wheel> wheels = new List<Wheel>();
    readonly List<Renderer> painted = new List<Renderer>();
    readonly List<Transform> bags = new List<Transform>();
    readonly List<Mesh> meshes = new List<Mesh>();
    TruckControllerV2 truck;
    Rigidbody body;
    Renderer originalBody;
    bool originalBodyActive, captured, applied;
    Transform mini;
    float originalMass, originalSteer, nextCheck, lastFill = -1f;
    MaterialPropertyBlock paint;

    public static TruckModelVisuals Ensure(TruckControllerV2 vehicle)
    {
        if (vehicle == null) return null;
        return vehicle.GetComponent<TruckModelVisuals>() ?? vehicle.gameObject.AddComponent<TruckModelVisuals>();
    }

    public static Vector3 ScaleFor(Transform vehicle)
    {
        var model = vehicle != null ? vehicle.GetComponent<TruckModelVisuals>() : null;
        return model != null ? model.ModelScale : Vector3.one;
    }
    public static Vector3 MapMetres(Transform vehicle, Vector3 canonical) => Vector3.Scale(canonical, ScaleFor(vehicle));
    public static Vector3 UnmapMetres(Transform vehicle, Vector3 metres)
    {
        Vector3 scale = ScaleFor(vehicle);
        return new Vector3(metres.x / scale.x, metres.y / scale.y, metres.z / scale.z);
    }

    /// <summary>Called by MetreRoot for direct truck attachments. Registered roots also resize on a body purchase.</summary>
    public static void RegisterMetreRoot(Transform vehicle, Transform attachment)
    {
        if (vehicle == null || attachment == null || vehicle.GetComponent<TruckControllerV2>() == null) return;
        var model = vehicle.GetComponent<TruckModelVisuals>();
        // A root created before ShopEffectsRunner attaches the model is discovered by Capture as well.
        var marker = attachment.GetComponent<TruckModelAnchor>() ?? attachment.gameObject.AddComponent<TruckModelAnchor>();
        marker.UnscaledScale = attachment.localScale;
        if (model != null) model.Register(attachment, true);
    }

    void Awake() => Capture();
    void OnEnable() { GameSession.UpgradeChanged += Changed; nextCheck = 0f; }
    void OnDisable() => GameSession.UpgradeChanged -= Changed;
    void OnDestroy()
    {
        foreach (var mesh in meshes)
            if (mesh != null) { if (Application.isPlaying) Destroy(mesh); else DestroyImmediate(mesh); }
    }
    void Changed(string id, int level) { nextCheck = 0f; }

    void Update()
    {
        if (Time.unscaledTime < nextCheck) return;
        nextCheck = Time.unscaledTime + .25f;
        ApplyModel(TruckProgression.ModelFor(truck));
        if (IsMini) { CopyBusinessPaint(); UpdateCargo(); }
    }

    void Capture()
    {
        if (captured) return;
        captured = true;
        truck = GetComponent<TruckControllerV2>();
        body = GetComponent<Rigidbody>();
        if (body != null) originalMass = body.mass;
        if (truck != null) originalSteer = truck.steeringRange;
        foreach (var renderer in GetComponentsInChildren<Renderer>(true))
            if (renderer.name == "TruckBody") { originalBody = renderer; break; }
        if (originalBody != null) originalBodyActive = originalBody.gameObject.activeSelf;
        foreach (var wheel in GetComponentsInChildren<WheelControl>(true))
        {
            var wc = wheel.GetComponent<WheelCollider>();
            if (wc == null) continue;
            wheel.WheelCollider = wc;
            wheels.Add(new Wheel {
                control = wheel, collider = wc, position = wc.transform.localPosition, radius = wc.radius,
                spring = wc.suspensionSpring, enabled = wc.enabled, controlEnabled = wheel.enabled,
                middle = wheel.name.StartsWith("ML") || wheel.name.StartsWith("MR"),
                modelScale = wheel.wheelModel != null ? wheel.wheelModel.localScale : Vector3.one,
                modelActive = wheel.wheelModel != null && wheel.wheelModel.gameObject.activeSelf,
            });
        }
        // Only fixed prefab anchors: never touch camera transforms or the actual network player.
        foreach (Transform child in transform)
        {
            if (child.GetComponent<TruckModelAnchor>() != null || child.GetComponent<TruckHopper>() != null)
                Register(child, true);
            else if (child.name == "DriverEjectPosition" || child.name == "TruckCameraRoot" ||
                     child.name == "LeftLight" || child.name == "RightLight" ||
                     child.name == "RL Light" || child.name == "RR Light") Register(child, false);
        }
    }

    void Register(Transform attachment, bool scaleShape)
    {
        for (int i = 0; i < anchors.Count; i++) if (anchors[i].t == attachment) return;
        var marker = attachment.GetComponent<TruckModelAnchor>();
        var anchor = new Anchor { t = attachment, position = attachment.localPosition,
            scale = marker != null ? marker.UnscaledScale : attachment.localScale, scaleShape = scaleShape };
        anchors.Add(anchor);
        ApplyAnchor(anchor);
    }

    void ApplyAnchor(Anchor a)
    {
        if (a.t == null) return;
        a.t.localPosition = Vector3.Scale(a.position, ModelScale);
        a.t.localScale = a.scaleShape ? Vector3.Scale(a.scale, ModelScale) : a.scale;
    }

    /// <summary>Explicit, idempotent model switch. Runtime callers choose the owning business's replicated model.</summary>
    public void ApplyModel(int model)
    {
        Capture();
        model = model <= 0 ? 0 : 1;
        if (applied && Model == model) return;
        Model = model;
        applied = true;
        if (IsMini && mini == null) BuildMini();
        if (originalBody != null) originalBody.gameObject.SetActive(!IsMini && originalBodyActive);
        if (mini != null) mini.gameObject.SetActive(IsMini);
        for (int i = anchors.Count - 1; i >= 0; i--)
        {
            if (anchors[i].t == null) anchors.RemoveAt(i);
            else ApplyAnchor(anchors[i]);
        }
        foreach (var w in wheels)
        {
            if (w.collider == null) continue;
            w.collider.transform.localPosition = Vector3.Scale(w.position, ModelScale);
            w.collider.radius = w.radius * (IsMini ? .90f : 1f);
            var spring = w.spring;
            if (IsMini) { spring.spring *= .8f; spring.damper *= .8f; }
            w.collider.suspensionSpring = spring;
            w.collider.enabled = w.enabled && !(IsMini && w.middle);
            w.control.enabled = w.controlEnabled && !(IsMini && w.middle);
            if (w.control.wheelModel != null)
            {
                w.control.wheelModel.localScale = w.modelScale * (IsMini ? .90f : 1f);
                w.control.wheelModel.gameObject.SetActive(w.modelActive && !(IsMini && w.middle));
                // WheelControl follows physics every frame; install the matching pose immediately for previews
                // and for a parked trade-in, before the next physics step has refreshed wheel contacts.
                w.collider.GetWorldPose(out var wheelPosition, out var wheelRotation);
                w.control.wheelModel.SetPositionAndRotation(wheelPosition, wheelRotation);
            }
        }
        // The compact chassis is lighter: acceleration stays lively without raising cruise speed.
        if (body != null)
        {
            body.mass = IsMini ? originalMass * .60f : originalMass;
            body.ResetCenterOfMass();
            var center = body.centerOfMass;
            center.y += truck != null ? truck.centreOfGravityOffset : -1f;
            body.centerOfMass = center;
        }
        if (truck != null) truck.steeringRange = IsMini ? originalSteer * 1.18f : originalSteer;
        GetComponent<TruckHandlingMods>()?.Apply();
        CopyBusinessPaint();
        lastFill = -1f;
        UpdateCargo();
    }

    void BuildMini()
    {
        mini = ShopModKit.MetreRoot(transform, MiniRootName);
        var enamel = ShopModKit.Lit("mini_truck_enamel", new Color(.42f, .53f, .23f), .32f, .12f);
        var cream = ShopModKit.Lit("mini_truck_cream", new Color(.89f, .85f, .69f), .32f, .05f);
        var steel = ShopModKit.Lit("mini_truck_steel", new Color(.31f, .34f, .33f), .4f, .6f);
        var dark = ShopModKit.Lit("mini_truck_rubber", new Color(.075f, .085f, .08f), .2f);
        var chrome = ShopModKit.Lit("mini_truck_chrome", new Color(.72f, .77f, .76f), .7f, .85f);
        var yellow = ShopModKit.Lit("mini_truck_warning", new Color(1f, .68f, .10f), .3f);
        var lamp = ShopModKit.Emissive("mini_truck_lamp", new Color(1f, .88f, .57f), .7f);
        var glass = Glass();

        // A short upright cab, with open clear window apertures: the actual driver remains visible inside.
        Part("Chassis", steel, new Vector3(0f, .70f, .65f), new Vector3(1.55f, .18f, 5.8f), true);
        Chamfer(Part("Cab lower body", enamel, new Vector3(0f, 1.24f, 3.12f), new Vector3(1.94f, .82f, 1.38f), true, true), .11f);
        Chamfer(Part("Cab roof", cream, new Vector3(0f, 2.49f, 3.08f), new Vector3(2.04f, .15f, 1.55f), true), .08f);
        Part("Cab back", enamel, new Vector3(0f, 1.98f, 2.40f), new Vector3(1.94f, .92f, .12f), true, true);
        var glazing = ShopModKit.Pivot("Clear cab glazing collision", mini, new Vector3(0f, 2.01f, 3.12f));
        glazing.gameObject.AddComponent<BoxCollider>().size = new Vector3(1.86f, .81f, 1.29f);
        Part("Dashboard", dark, new Vector3(0f, 1.64f, 3.64f), new Vector3(1.78f, .16f, .27f));
        Chamfer(Part("Bumper", steel, new Vector3(0f, .86f, 3.85f), new Vector3(2.05f, .22f, .17f), true), .13f);
        Part("Grille surround", dark, new Vector3(0f, 1.17f, 3.825f), new Vector3(.87f, .28f, .025f));
        for (int i = 0; i < 4; i++)
            Part("Grille slat", chrome, new Vector3(0f, 1.075f + i * .06f, 3.845f), new Vector3(.77f, .021f, .025f));
        Part("Windscreen divider", cream, new Vector3(0f, 2.02f, 3.72f), new Vector3(.055f, .77f, .07f), euler: new Vector3(-8f, 0f, 0f));
        Part("Sun visor lip", cream, new Vector3(0f, 2.405f, 3.79f), new Vector3(1.87f, .085f, .18f), euler: new Vector3(-7f, 0f, 0f));
        Part("Licence plate", cream, new Vector3(0f, .86f, 3.944f), new Vector3(.36f, .13f, .018f));
        Part("Maker badge", chrome, new Vector3(0f, 1.46f, 3.814f), new Vector3(.16f, .065f, .025f), euler: new Vector3(0f, 0f, 5f));
        for (int side = -1; side <= 1; side += 2)
        {
            float x = side * .95f;
            Part("Front window pillar", cream, new Vector3(x, 2.02f, 3.72f), new Vector3(.10f, .84f, .12f), euler: new Vector3(-8f, 0f, 0f));
            Part("Rear window pillar", cream, new Vector3(x, 2.02f, 2.49f), new Vector3(.10f, .84f, .12f));
            var windshield = Part("Clear windshield", glass, new Vector3(side * .467f, 2.02f, 3.722f),
                new Vector3(.86f, .71f, .008f), euler: new Vector3(-8f, 0f, 0f));
            windshield.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var window = Part("Clear side window", glass, new Vector3(side * .951f, 2.02f, 3.08f), new Vector3(.008f, .72f, 1.09f));
            window.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Part("Window weatherstrip", dark, new Vector3(side * .968f, 1.637f, 3.10f), new Vector3(.055f, .045f, 1.19f));
            Part("Quarter window seam", cream, new Vector3(side * .97f, 2.02f, 3.39f), new Vector3(.035f, .76f, .035f));
            Part("Wiper pivot", dark, new Vector3(side * .49f, 1.66f, 3.798f), new Vector3(.08f, .05f, .025f));
            Part("Bent wiper", dark, new Vector3(side * .43f, 1.79f, 3.79f), new Vector3(.025f, .34f, .026f), euler: new Vector3(-8f, 0f, side * 21f));
            Chamfer(Part("Door inset", cream, new Vector3(side * .979f, 1.38f, 3.12f), new Vector3(.035f, .33f, .77f)), .12f);
            Part("Door handle", chrome, new Vector3(side * 1.011f, 1.50f, 2.79f), new Vector3(.06f, .045f, .20f));
            Part("Door seam", dark, new Vector3(side * .973f, 1.30f, 2.55f), new Vector3(.012f, .56f, .018f));
            Part("Cab step", dark, new Vector3(side * .98f, .80f, 3.04f), new Vector3(.25f, .10f, .55f));
            Part("Mirror arm", chrome, new Vector3(side * 1.09f, 2.05f, 3.42f), new Vector3(.33f, .045f, .05f));
            Part("Big mirror", dark, new Vector3(side * 1.26f, 2.08f, 3.42f), new Vector3(.09f, .35f, .23f));
            Part("Mirror glass", chrome, new Vector3(side * 1.27f, 2.08f, 3.29f), new Vector3(.075f, .29f, .025f));
            ShopModKit.Prim(PrimitiveType.Cylinder, "Round headlight rim", mini, chrome,
                new Vector3(side * .72f, 1.26f, 3.821f), new Vector3(.32f, .036f, .32f), new Vector3(90f, 0f, 0f));
            ShopModKit.Prim(PrimitiveType.Cylinder, "Round headlight lens", mini, lamp,
                new Vector3(side * .72f, 1.26f, 3.861f), new Vector3(.265f, .012f, .265f), new Vector3(90f, 0f, 0f));
            Part("Indicator", yellow, new Vector3(side * .74f, 1.03f, 3.85f), new Vector3(.26f, .075f, .07f));
            Part("Bumper end pad", dark, new Vector3(side * .82f, .87f, 3.936f), new Vector3(.20f, .145f, .03f));
            WheelArch(side, 2.685f, .83f, steel, dark);
            WheelArch(side, -.409f, .83f, steel, dark);
        }

        // Open tipper hopper. Individual floor and side colliders leave the low rear mouth usable for throws.
        Part("Hopper floor", steel, new Vector3(0f, 1.08f, -.08f), new Vector3(1.86f, .16f, 4.16f), true);
        Part("Hopper front wall", enamel, new Vector3(0f, 1.84f, 2.02f), new Vector3(1.94f, 1.56f, .14f), true, true);
        for (int side = -1; side <= 1; side += 2)
        {
            Part("Tipper side panel", enamel, new Vector3(side * .93f, 1.73f, -.05f), new Vector3(.12f, 1.24f, 4.15f), true, true);
            Part("Bed sill", steel, new Vector3(side * .98f, 1.145f, -.05f), new Vector3(.11f, .11f, 4.24f));
            Part("Pressed panel top edge", enamel, new Vector3(side * .975f, 2.325f, -.05f), new Vector3(.085f, .085f, 4.18f), businessPaint: true);
            Part("Top cage rail", steel, new Vector3(side * .95f, 3.04f, -.05f), new Vector3(.075f, .075f, 4.22f));
            for (int i = 0; i < 5; i++)
            {
                Part("Cage upright", steel, new Vector3(side * .95f, 2.45f, -2.1f + i * 1.04f), new Vector3(.075f, 1.21f, .075f));
                Part("Panel reinforcing rib", enamel, new Vector3(side * 1.005f, 1.75f, -2.08f + i * 1.035f), new Vector3(.05f, 1.13f, .06f), businessPaint: true);
                Part("Bed hinge", chrome, new Vector3(side * 1.02f, 1.16f, -1.79f + i * .88f), new Vector3(.06f, .13f, .18f));
            }
            Part("Side repair patch", cream, new Vector3(side * 1.0f, 1.76f, -.84f), new Vector3(.025f, .39f, .72f),
                euler: new Vector3(side * 7f, 0f, 0f));
            Part("Mudflap", dark, new Vector3(side * .80f, .63f, -.92f), new Vector3(.43f, .46f, .06f));
        }
        Part("Rear low loading lip", enamel, new Vector3(0f, 1.26f, -2.16f), new Vector3(1.84f, .25f, .13f), true, true);
        Part("Rear bumper", steel, new Vector3(0f, .84f, -2.22f), new Vector3(1.92f, .19f, .16f), true);
        for (int i = 0; i < 8; i++)
            Part("Hazard stripe", i % 2 == 0 ? yellow : dark, new Vector3(-.79f + i * .225f, .84f, -2.307f),
                new Vector3(.15f, .16f, .012f), euler: new Vector3(0f, 0f, -20f));
        Part("Beacon base", dark, new Vector3(.46f, 2.61f, 2.76f), new Vector3(.25f, .09f, .25f));
        ShopModKit.Prim(PrimitiveType.Cylinder, "Amber beacon", mini, yellow, new Vector3(.46f, 2.74f, 2.76f), new Vector3(.20f, .12f, .20f));

        // Cosmetic cargo, never colliders or collectible items. It is driven by this truck's business cargo.
        var sack = ShopModKit.Lit("mini_truck_cargo_bag", new Color(.16f, .19f, .16f), .14f);
        for (int i = 0; i < 18; i++)
        {
            int row = i / 3;
            var group = ShopModKit.Pivot("Visible load bag " + i, mini,
                new Vector3((i % 3 - 1) * .53f, 1.43f + row / 2 * .67f, -.96f + row % 2 * 1.42f));
            ShopModKit.Prim(PrimitiveType.Sphere, "Sack", group, sack, Vector3.zero, new Vector3(.64f, .73f, .75f));
            ShopModKit.Prim(PrimitiveType.Sphere, "Knot", group, sack, new Vector3(.06f, .39f, 0f), new Vector3(.16f, .18f, .17f));
            group.localRotation = Quaternion.Euler(i * 7 % 15, i * 43, i % 2 == 0 ? -8f : 9f);
            bags.Add(group);
        }
    }

    static Material Glass()
    {
        // Same shipped URP-Lit transparent variant as TruckCabVisuals; blue-grey tint stays very light so the
        // driver and their name remain readable from outside rather than disappearing behind opaque windows.
        var glass = ShopModKit.Lit("mini_truck_glass", new Color(.47f, .69f, .76f, .19f), .75f, .08f);
        glass.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        glass.SetOverrideTag("RenderType", "Transparent");
        glass.SetFloat("_Surface", 1f);
        glass.SetFloat("_Blend", 0f);
        glass.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        glass.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        glass.SetFloat("_ZWrite", 0f);
        glass.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        glass.EnableKeyword("_ALPHABLEND_ON");
        glass.DisableKeyword("_ALPHATEST_ON");
        glass.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        glass.SetShaderPassEnabled("ShadowCaster", false);
        return glass;
    }

    void WheelArch(int side, float z, float y, Material rim, Material shadow)
    {
        // A broad dark recess under a proper extruded half-ring gives the cab a wheel cut-out silhouette.
        ArcMesh("Wheel arch recess", side, z, y, .025f, .49f, .012f, shadow);
        ArcMesh("Stamped wheel arch", side, z, y, .425f, .515f, .13f, rim);
    }

    void ArcMesh(string name, int side, float z, float y, float inside, float outside, float width, Material material)
    {
        const int segments = 12;
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        float x = side * .965f;
        for (int i = 0; i < segments; i++)
        {
            float a = Mathf.PI * i / segments, b = Mathf.PI * (i + 1) / segments;
            Vector3 V(float angle, float radius, float depth) => new Vector3(x + side * depth,
                y + Mathf.Sin(angle) * radius, z + Mathf.Cos(angle) * radius);
            var v0 = V(a, inside, 0f); var v1 = V(a, outside, 0f);
            var v2 = V(b, outside, 0f); var v3 = V(b, inside, 0f);
            var v4 = V(a, inside, width); var v5 = V(a, outside, width);
            var v6 = V(b, outside, width); var v7 = V(b, inside, width);
            Quad(vertices, indices, v0, v1, v2, v3, side < 0);
            Quad(vertices, indices, v4, v5, v6, v7, side > 0);
            Quad(vertices, indices, v1, v5, v6, v2, side < 0);
            Quad(vertices, indices, v0, v4, v7, v3, side > 0);
        }
        var part = new GameObject(name).transform;
        part.SetParent(mini, false);
        var mesh = new Mesh { name = "Curb Pup " + name };
        mesh.SetVertices(vertices); mesh.SetTriangles(indices, 0); mesh.RecalculateNormals(); mesh.RecalculateBounds();
        meshes.Add(mesh);
        part.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
        part.gameObject.AddComponent<MeshRenderer>().sharedMaterial = material;
    }

    void Chamfer(Transform part, float bevel)
    {
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        Vector3 Ring(int corner, int level)
        {
            float edge = level == 0 || level == 3 ? .5f - bevel : .5f;
            float cut = bevel * .7f;
            float y = level == 0 ? -.5f : level == 1 ? -.5f + bevel : level == 2 ? .5f - bevel : .5f;
            Vector2[] perimeter = {
                new Vector2(-edge + cut, -edge), new Vector2(edge - cut, -edge),
                new Vector2(edge, -edge + cut), new Vector2(edge, edge - cut),
                new Vector2(edge - cut, edge), new Vector2(-edge + cut, edge),
                new Vector2(-edge, edge - cut), new Vector2(-edge, -edge + cut),
            };
            return new Vector3(perimeter[corner].x, y, perimeter[corner].y);
        }
        for (int level = 0; level < 3; level++)
            for (int i = 0; i < 8; i++)
                Quad(vertices, indices, Ring(i, level), Ring(i, level + 1), Ring((i + 1) % 8, level + 1), Ring((i + 1) % 8, level), false);
        for (int i = 0; i < 8; i++)
        {
            int at = vertices.Count;
            vertices.Add(new Vector3(0f, -.5f, 0f)); vertices.Add(Ring(i, 0)); vertices.Add(Ring((i + 1) % 8, 0));
            indices.Add(at); indices.Add(at + 1); indices.Add(at + 2);
            at = vertices.Count;
            vertices.Add(new Vector3(0f, .5f, 0f)); vertices.Add(Ring((i + 1) % 8, 3)); vertices.Add(Ring(i, 3));
            indices.Add(at); indices.Add(at + 1); indices.Add(at + 2);
        }
        var mesh = new Mesh { name = "Curb Pup chamfered " + part.name };
        mesh.SetVertices(vertices); mesh.SetTriangles(indices, 0); mesh.RecalculateNormals(); mesh.RecalculateBounds();
        meshes.Add(mesh);
        part.GetComponent<MeshFilter>().sharedMesh = mesh;
    }

    static void Quad(List<Vector3> vertices, List<int> indices, Vector3 a, Vector3 b, Vector3 c, Vector3 d, bool reverse)
    {
        int start = vertices.Count;
        vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
        indices.Add(start); indices.Add(start + (reverse ? 2 : 1)); indices.Add(start + (reverse ? 1 : 2));
        indices.Add(start); indices.Add(start + (reverse ? 3 : 2)); indices.Add(start + (reverse ? 2 : 3));
    }

    Transform Part(string name, Material material, Vector3 position, Vector3 size,
        bool solid = false, bool businessPaint = false, Vector3 euler = default)
    {
        var part = ShopModKit.Prim(PrimitiveType.Cube, name, mini, material, position, size, euler);
        var renderer = part.GetComponent<Renderer>();
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        if (businessPaint) painted.Add(renderer);
        if (solid) part.gameObject.AddComponent<BoxCollider>();
        return part;
    }

    void CopyBusinessPaint()
    {
        if (originalBody == null || painted.Count == 0) return;
        paint ??= new MaterialPropertyBlock();
        originalBody.GetPropertyBlock(paint, 0);
        foreach (var renderer in painted) if (renderer != null) renderer.SetPropertyBlock(paint);
    }

    void UpdateCargo()
    {
        if (!IsMini || bags.Count == 0 || truck == null) return;
        var hopper = truck.GetComponentInChildren<TruckHopper>();
        string business = hopper != null ? hopper.ResolveBusiness() : null;
        float kg, capacity;
        if (Businesses.IsAuthority && (string.IsNullOrEmpty(business) || business == Businesses.LocalId))
        { kg = GameSession.TruckLoadKg; capacity = GameSession.TruckCapacityKg; }
        else if (!Businesses.TryGetTruckLoad(business, out kg, out capacity)) { kg = 0f; capacity = TruckProgression.MiniCapacityKg; }
        float fill = Mathf.Clamp01(kg / Mathf.Max(1f, capacity));
        if (Mathf.Approximately(fill, lastFill)) return;
        lastFill = fill;
        int count = kg > .01f ? Mathf.CeilToInt(fill * bags.Count) : 0;
        for (int i = 0; i < bags.Count; i++) bags[i].gameObject.SetActive(i < count);
    }
}

/// <summary>Remembers an attachment's canonical metre scale before a vehicle model changes it.</summary>
public sealed class TruckModelAnchor : MonoBehaviour
{
    public Vector3 UnscaledScale = Vector3.one;
}
