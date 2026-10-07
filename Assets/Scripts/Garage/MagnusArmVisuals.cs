using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// A cosmetic industrial arm built on Magnus's existing seven animated joints. Never changes their lengths,
/// rotations, claw centre, reach, sorting rules or replicated pose. All offsets below are metres, despite the
/// imported rig's scale of 60. Existing eyes, brain jars and vision accessories remain attached to the wrist.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(300)] // after MagnusArm (100) and the guest's MagnusPoseNet interpolation (200)
public sealed class MagnusArmVisuals : MonoBehaviour
{
    public const string ModelName = "Magnus Industrial Arm";
    readonly List<Transform> roots = new List<Transform>();
    readonly List<Mesh> ownedMeshes = new List<Mesh>();
    readonly List<Renderer> original = new List<Renderer>();
    readonly List<bool> originalEnabled = new List<bool>();
    readonly List<Actuator> actuators = new List<Actuator>();
    Transform baseJoint, shoulder, elbow, wrist, pointer, thumb, claw;
    int signature = -1;
    float nextCheck;
    Material paint, accent, dark, metal, rubber, gold;
    int tier;

    sealed class Actuator
    {
        public Transform from, to, frame, barrel, rod;
        public Vector3 a, b;
    }

    public static MagnusArmVisuals Ensure(MagnusArm arm) => arm == null ? null :
        arm.GetComponent<MagnusArmVisuals>() ?? arm.gameObject.AddComponent<MagnusArmVisuals>();

    public static bool IsLegacyClaw(string name) => name == "Tongs0" || name == "Tongs1" ||
        name == "Lobster0" || name == "Lobster1" || name == "ArcadeClaw";

    /// <summary>Chess-only Magnus remains stock. Getting the job installs a real two-jaw servo arm.</summary>
    public static int TierFor(int brain, int gripper) => brain < 1 ? 0 : 1 + Mathf.Clamp(gripper, 0, 2);
    public int VisualTier => tier;

    void Awake() => Resolve();

    bool Resolve()
    {
        if (claw != null) return true;
        baseJoint = transform.Find("BasePivot");
        shoulder = baseJoint != null ? baseJoint.Find("Shoulder") : null;
        elbow = shoulder != null ? shoulder.Find("Elbow") : null;
        wrist = elbow != null ? elbow.Find("Wrist") : null;
        pointer = wrist != null ? wrist.Find("Pointer") : null;
        thumb = wrist != null ? wrist.Find("Thumb") : null;
        claw = wrist != null ? wrist.Find("Claw Center") : null;
        if (claw == null || pointer == null || thumb == null) return false;
        if (original.Count == 0)
            foreach (var renderer in GetComponentsInChildren<Renderer>(true))
            {
                // Only the imported arm meshes. Do not hide any googly-eye or upgrade attachment renderer.
                string n = renderer.name;
                if (!(n.StartsWith("pCube") || n.StartsWith("pCylinder") || n.StartsWith("pasted__") ||
                    n == "arm_stand" || n == "base")) continue;
                if (renderer.GetComponentInParent<GooglyEye>(true) != null ||
                    renderer.GetComponentInParent<UpgradeAttachment>(true) != null) continue;
                original.Add(renderer); originalEnabled.Add(renderer.enabled);
            }
        return true;
    }

    void LateUpdate()
    {
        if (Time.unscaledTime >= nextCheck)
        {
            nextCheck = Time.unscaledTime + .25f;
            RebuildForLevels(DepotMachineContext.Level(this, ShopIds.MagnusBrain),
                DepotMachineContext.Level(this, ShopIds.MagnusGripper));
        }
        UpdateActuators();
    }

    /// <summary>Also used by disposable visual previews. Disable this component's tick when previewing arbitrary
    /// tiers; it never writes upgrades or saves. Repeated calls at the same tier are allocation-free.</summary>
    public void RebuildForLevels(int brain, int gripper)
    {
        if (!Resolve()) return;
        int wanted = TierFor(brain, gripper);
        bool prime = brain >= 4;
        int next = wanted + (prime ? 10 : 0);
        if (signature == next) return;
        MagnusEyeFallback.RepairMissing(transform);
        ClearModel();
        signature = next; tier = wanted;
        for (int i = 0; i < original.Count; i++) if (original[i] != null)
            original[i].enabled = wanted == 0 && originalEnabled[i];
        if (wanted == 0) return;

        paint = Mat("paint_" + tier, tier == 2 ? new Color(.80f, .22f, .10f) : new Color(.83f, .83f, .73f), .38f, .25f);
        accent = Mat("accent_" + tier, tier == 3 ? new Color(.08f, .60f, .60f) : new Color(1f, .50f, .10f), .42f, .3f);
        dark = Mat("graphite", new Color(.13f, .16f, .18f), .48f, .45f);
        metal = Mat("machined", new Color(.53f, .62f, .66f), .78f, .8f);
        rubber = Mat("grip", new Color(.04f, .045f, .045f), .18f, .05f);
        gold = Mat("brass", new Color(.88f, .61f, .20f), .64f, .75f);

        var baseModel = Frame(baseJoint, "Base");
        float height = Vector3.Distance(baseJoint.position, shoulder.position);
        Cylinder(baseModel, "Bolted foot", new Vector3(0f, .035f, 0f), .52f, .07f, dark);
        Cylinder(baseModel, "Turntable bearing", new Vector3(0f, .11f, 0f), .44f, .10f, metal);
        Cylinder(baseModel, "Yaw motor casing", new Vector3(0f, .20f, 0f), .39f, .10f, paint);
        Sweep(baseModel, "Cast shoulder pedestal", new[] { new Vector3(0f, .23f, 0f), new Vector3(0f, height - .06f, 0f) },
            new[] { .39f, .33f }, new[] { .35f, .29f }, paint);
        for (int i = 0; i < 4; i++)
        {
            float angle = (45f + i * 90f) * Mathf.Deg2Rad;
            Cylinder(baseModel, "Foot bolt", new Vector3(Mathf.Cos(angle) * .205f, .079f, Mathf.Sin(angle) * .205f), .045f, .035f, metal);
        }
        Box(baseModel, "Power pack", new Vector3(0f, height * .52f, .21f), new Vector3(.27f, .29f, .15f), dark);
        for (int i = 0; i < 3; i++)
            Box(baseModel, "Power pack cooling slot", new Vector3(0f, height * .52f - .075f + i * .075f, .292f), new Vector3(.19f, .022f, .014f), metal);

        BuildLink(shoulder, elbow, "Upper", true);
        BuildLink(elbow, wrist, "Forearm", false);
        var head = Frame(wrist, "Wrist");
        // The eye plane is x=-.025m. A full-width disc put its front cap at x=-.184m and masked half the face.
        // A compact bearing sits behind the face instead; the large shoulder/elbow joints keep the heavy silhouette.
        var bearing = new GameObject("Recessed wrist bearing").transform;
        bearing.SetParent(head, false);
        bearing.localPosition = new Vector3(.085f, 0f, 0f);
        Joint(bearing, "Wrist bearing", .17f, .06f);
        // The face is on -X. Leave it clear for the authored eyes and vision/brain attachments.
        Box(head, "Wrist rear motor", new Vector3(.13f, 0f, -.10f), new Vector3(.16f, .24f, .19f), dark);
        // The actual eyes are on the thin -X face near z=-.197, not the end of the tool. A full round quick-release
        // covered them. This C-shaped collar supports the face from behind and leaves both existing eyes exposed.
        Box(head, "Tool quick release back", new Vector3(.12f, .005f, -.20f), new Vector3(.13f, .35f, .12f), tier == 3 ? gold : metal);
        for (int side = -1; side <= 1; side += 2)
            Box(head, "Tool quick release cheek", new Vector3(.075f, .005f + side * .215f, -.18f), new Vector3(.16f, .065f, .15f), tier == 3 ? gold : metal);
        Box(head, "Face mounting plate", new Vector3(.035f, .02f, -.196f), new Vector3(.07f, .36f, .12f), paint);
        Box(head, "Servo bridge", new Vector3(.075f, .005f, -.24f), new Vector3(.13f, .29f, .08f), paint);
        Box(head, "Tool identification stripe", new Vector3(.194f, 0f, -.205f), new Vector3(.02f, .20f, .06f), accent);
        BuildJaws(pointer, 1f);
        BuildJaws(thumb, -1f);
        if (tier >= 2)
        {
            AddActuator(shoulder, elbow, 1f);
            AddActuator(shoulder, elbow, -1f);
            AddActuator(elbow, wrist, 1f);
        }
        if (prime)
        {
            var medal = Frame(baseJoint, "Prime insignia");
            Cylinder(medal, "Grandmaster badge", new Vector3(-.185f, height * .57f, 0f), .16f, .025f, gold, Vector3.left);
            Box(medal, "Rank bar", new Vector3(-.202f, height * .57f, 0f), new Vector3(.02f, .03f, .11f), dark);
        }
        UpdateActuators();
    }

    void BuildLink(Transform joint, Transform next, string name, bool upper)
    {
        var frame = Frame(joint, name);
        Vector3 end = LocalMetres(joint, next.position);
        float length = end.magnitude;
        float width = tier == 2 ? .43f : tier == 3 ? .39f : .36f;
        float depth = upper ? .34f : .30f;
        Joint(frame, name + " gear housing", upper ? .39f : .34f, width + .015f);
        var points = new[] { end * .10f, end * .22f, end * .76f, end * .91f };
        Sweep(frame, name + " chamfered casting", points,
            new[] { width * .72f, width, width * .81f, width * .61f },
            new[] { depth * .68f, depth, depth * .78f, depth * .62f }, paint);
        // Separate inset spine and broad side panels make it a cast assembly, not a bare connecting stick.
        var spine = points.Clone() as Vector3[];
        for (int i = 0; i < spine.Length; i++) spine[i].y += depth * .46f;
        Sweep(frame, name + " inset spine", spine, new[] { .09f, .12f, .11f, .075f }, new[] { .025f, .025f, .025f, .02f }, accent);
        for (int side = -1; side <= 1; side += 2)
        {
            var panel = Box(frame, name + " removable panel", end * .48f + Vector3.right * (side * width * .48f),
                new Vector3(.025f, depth * .58f, length * .38f), tier == 3 ? dark : metal);
            panel.localRotation = Quaternion.FromToRotation(Vector3.back, end.normalized);
            for (int bolt = 0; bolt < 2; bolt++)
                Cylinder(frame, name + " panel bolt", end * (.32f + bolt * .31f) + Vector3.right * (side * width * .515f),
                    .038f, .028f, dark, Vector3.right);
        }
        if (tier == 3)
            for (int i = 0; i < 3; i++)
                Box(frame, name + " precision cooling fin", end * (.35f + i * .11f) + Vector3.up * depth * .62f,
                    new Vector3(width * .65f, .065f, .045f), dark);
    }

    void BuildJaws(Transform finger, float sign)
    {
        var frame = Frame(finger, sign > 0 ? "Pointer jaws" : "Thumb jaws");
        // Calibrate contact to the EXISTING claw centre at the stock closed-grip angle. No IK/tuning change.
        Vector3 wristClaw = LocalMetres(wrist, claw.position);
        Vector3 wristPivot = LocalMetres(wrist, finger.position);
        Vector3 tip = Quaternion.Inverse(Quaternion.Euler(sign * 19.5f, 0f, 0f)) * (wristClaw - wristPivot);
        int count = tier == 3 ? 2 : 1;
        for (int i = 0; i < count; i++)
        {
            float x = count == 2 ? (i == 0 ? -.105f : .105f) : 0f;
            // All pivots sit behind the face. The quad-claw fans out below it to the same four contact points;
            // putting its front pivot at x=-.105m covered the googly eyes with a hinge/drive block.
            float pivotX = .11f + (count == 2 ? i * .10f : 0f);
            float width = tier == 2 ? .19f : tier == 3 ? .085f : .145f;
            var curve = new[]
            {
                new Vector3(pivotX, 0f, -.11f),
                new Vector3(pivotX, sign * (tier == 2 ? .095f : .055f), -.17f),
                new Vector3(Mathf.Lerp(pivotX, x, .8f), tip.y * .46f, tip.z * .74f),
                new Vector3(x, tip.y, tip.z),
            };
            Sweep(frame, "Articulated jaw casting " + i, curve,
                new[] { width, width * 1.20f, width, width * .78f },
                new[] { .105f, tier == 2 ? .13f : .095f, .078f, .07f }, paint);
            Cylinder(frame, "Finger hinge " + i, curve[0] + Vector3.right * .075f, .15f, width * .6f, metal, Vector3.right);
            Box(frame, "Finger drive block " + i, new Vector3(pivotX + .075f, 0f, -.07f), new Vector3(.075f, .11f, .14f), dark);
            var pad = Box(frame, "Replaceable rubber jaw pad " + i, curve[3], new Vector3(width * .84f, .055f, .10f), rubber);
            pad.localRotation = Quaternion.Euler(-sign * 19.5f, 0f, 0f);
            for (int tooth = 0; tooth < 3; tooth++)
                Box(frame, "Grip tread " + i + " " + tooth, curve[3] + new Vector3(0f, sign * -.028f, (tooth - 1) * .03f),
                    new Vector3(width * .80f, .017f, .011f), metal);
            // Bright knuckle shield visually belongs to the casting; it follows the same actual finger bone.
            Box(frame, "Knuckle shield " + i, curve[1] + Vector3.up * (sign * .055f),
                new Vector3(width * 1.10f, .035f, .13f), tier == 3 ? gold : accent);
        }
    }

    void AddActuator(Transform from, Transform to, float side)
    {
        var frame = Frame(from, "Hydraulic actuator");
        var a = new Actuator { from = from, to = to, frame = frame,
            a = new Vector3(side * .235f, .06f, -.13f), b = new Vector3(side * .235f, -.11f, -.13f) };
        a.barrel = Cylinder(frame, "Sealed hydraulic barrel", Vector3.zero, .082f, .3f, dark);
        a.rod = Cylinder(frame, "Chrome piston rod", Vector3.zero, .040f, .3f, metal);
        var mount = Frame(to, "Hydraulic clevis");
        Cylinder(mount, "Clevis pin", a.b, .105f, .095f, accent, Vector3.right);
        Cylinder(frame, "Actuator mounting pin", a.a, .11f, .095f, accent, Vector3.right);
        actuators.Add(a);
    }

    public void UpdateActuators()
    {
        foreach (var a in actuators)
        {
            Vector3 start = a.frame.InverseTransformPoint(WorldMetres(a.from, a.a));
            Vector3 end = a.frame.InverseTransformPoint(WorldMetres(a.to, a.b));
            Segment(a.barrel, start, Vector3.Lerp(start, end, .62f), .082f);
            Segment(a.rod, Vector3.Lerp(start, end, .48f), end, .040f);
        }
    }

    static void Segment(Transform t, Vector3 a, Vector3 b, float diameter)
    {
        t.localPosition = (a + b) * .5f;
        t.localRotation = Quaternion.FromToRotation(Vector3.up, b - a);
        t.localScale = new Vector3(diameter, Vector3.Distance(a, b) * .5f, diameter);
    }

    Transform Frame(Transform joint, string name)
    {
        var go = new GameObject(ModelName + " / " + name);
        go.transform.SetParent(joint, false);
        var scale = joint.lossyScale;
        go.transform.localScale = new Vector3(1f / Mathf.Max(.0001f, Mathf.Abs(scale.x)),
            1f / Mathf.Max(.0001f, Mathf.Abs(scale.y)), 1f / Mathf.Max(.0001f, Mathf.Abs(scale.z)));
        roots.Add(go.transform);
        return go.transform;
    }

    static Vector3 LocalMetres(Transform frame, Vector3 world) => Quaternion.Inverse(frame.rotation) * (world - frame.position);
    static Vector3 WorldMetres(Transform frame, Vector3 local) => frame.position + frame.rotation * local;
    static Material Mat(string key, Color color, float smooth, float metal) => ShopMaterials.Get("magnus_industrial_" + key, color, smooth, metal);

    Transform Box(Transform parent, string name, Vector3 at, Vector3 size, Material material)
    {
        var t = Primitive(parent, name, PrimitiveType.Cube, material);
        t.localPosition = at; t.localScale = size; return t;
    }

    Transform Cylinder(Transform parent, string name, Vector3 at, float diameter, float length, Material material, Vector3 axis = default)
    {
        var t = Primitive(parent, name, PrimitiveType.Cylinder, material);
        t.localPosition = at; t.localScale = new Vector3(diameter, length * .5f, diameter);
        if (axis.sqrMagnitude > .001f) t.localRotation = Quaternion.FromToRotation(Vector3.up, axis);
        return t;
    }

    Transform Primitive(Transform parent, string name, PrimitiveType shape, Material material)
    {
        // Built-in meshes without even temporarily creating gameplay colliders.
        string resource = shape == PrimitiveType.Cylinder ? "Cylinder.fbx" : "Cube.fbx";
        var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
        go.transform.SetParent(parent, false);
        go.GetComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>(resource);
        var renderer = go.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.On;
        return go.transform;
    }

    void Joint(Transform frame, string name, float diameter, float length)
    {
        Cylinder(frame, name, Vector3.zero, diameter, length, paint, Vector3.right);
        for (int side = -1; side <= 1; side += 2)
        {
            Cylinder(frame, name + " machined rim", Vector3.right * (side * length * .5f), diameter * .90f, .04f, metal, Vector3.right);
            Cylinder(frame, name + " enamel cap", Vector3.right * (side * (length * .5f + .024f)), diameter * .61f, .03f,
                tier == 3 ? gold : accent, Vector3.right);
            Cylinder(frame, name + " axle bolt", Vector3.right * (side * (length * .5f + .047f)), diameter * .18f, .023f, dark, Vector3.right);
        }
    }

    /// <summary>Continuous chamfered cast-metal sweep, not a stack of disconnected primitive sticks.</summary>
    void Sweep(Transform parent, string name, Vector3[] centers, float[] widths, float[] depths, Material material)
    {
        var ring = new[] { new Vector2(-.36f, -.5f), new Vector2(.36f, -.5f), new Vector2(.5f, -.36f), new Vector2(.5f, .36f),
            new Vector2(.36f, .5f), new Vector2(-.36f, .5f), new Vector2(-.5f, .36f), new Vector2(-.5f, -.36f) };
        var vertices = new Vector3[centers.Length * 8];
        var triangles = new List<int>((centers.Length - 1) * 48 + 36);
        for (int i = 0; i < centers.Length; i++)
        {
            Vector3 tangent = (centers[Mathf.Min(i + 1, centers.Length - 1)] - centers[Mathf.Max(0, i - 1)]).normalized;
            Vector3 side = Vector3.right;
            Vector3 across = Vector3.Cross(tangent, side).normalized;
            for (int v = 0; v < 8; v++) vertices[i * 8 + v] = centers[i] + side * (ring[v].x * widths[i]) + across * (ring[v].y * depths[i]);
            if (i == 0) continue;
            for (int v = 0; v < 8; v++)
            {
                int a = (i - 1) * 8 + v, b = (i - 1) * 8 + (v + 1) % 8, c = i * 8 + v, d = i * 8 + (v + 1) % 8;
                triangles.Add(a); triangles.Add(b); triangles.Add(c);
                triangles.Add(b); triangles.Add(d); triangles.Add(c);
            }
        }
        for (int i = 1; i < 7; i++)
        {
            triangles.Add(0); triangles.Add(i + 1); triangles.Add(i);
            int end = vertices.Length - 8;
            triangles.Add(end); triangles.Add(end + i); triangles.Add(end + i + 1);
        }
        // Castings have planar bevels and machined end faces: do not average end-cap normals into the long sides.
        // Separate face vertices also make the silhouette read as solid metal under the shop's side lighting.
        var flatVertices = new Vector3[triangles.Count];
        var flatIndices = new int[triangles.Count];
        for (int i = 0; i < triangles.Count; i++) { flatVertices[i] = vertices[triangles[i]]; flatIndices[i] = i; }
        var mesh = new Mesh { name = "Magnus cast " + name };
        mesh.vertices = flatVertices; mesh.SetTriangles(flatIndices, 0); mesh.RecalculateNormals(); mesh.RecalculateBounds();
        ownedMeshes.Add(mesh);
        var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
        go.transform.SetParent(parent, false);
        go.GetComponent<MeshFilter>().sharedMesh = mesh;
        go.GetComponent<MeshRenderer>().sharedMaterial = material;
    }

    void ClearModel()
    {
        foreach (var root in roots) if (root != null) { root.gameObject.SetActive(false); Dispose(root.gameObject); }
        roots.Clear(); actuators.Clear();
        foreach (var mesh in ownedMeshes) if (mesh != null) Dispose(mesh);
        ownedMeshes.Clear();
    }

    static void Dispose(Object item) { if (Application.isPlaying) Destroy(item); else DestroyImmediate(item); }

    void OnDestroy()
    {
        ClearModel();
        for (int i = 0; i < original.Count; i++) if (original[i] != null) original[i].enabled = originalEnabled[i];
    }
}
