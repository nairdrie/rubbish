using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The existing garage's wall-mounted bag route. Applies to the authored solo garage and each translated depot copy,
/// without rebuilding the room, moving a machine, changing upgrade gates or replacing conveyor behaviour references.
/// </summary>
[DisallowMultipleComponent]
public sealed class GarageConveyorLayout : MonoBehaviour
{
    public const float BeltHeight = 3.7f;
    public static readonly Vector3 Elevator = new Vector3(6.25f, 0f, -2.55f);
    public static readonly Vector3 DirectFeed = new Vector3(5.35f, 0f, -3.95f);
    public static readonly Vector3 KevinChute = new Vector3(5.55f, 3f, -4.56f);
    public static Quaternion WallFacing => Quaternion.Euler(0f, 90f, 0f);
    public bool Applied { get; private set; }

    /// <summary>Metres above the garage floor. The two lanes never cross, including their elevator exit turns.</summary>
    public static Vector3[] Route(int lane) => lane == 0 ? new[]
    {
        new Vector3(6.25f, .25f, -2.35f), new Vector3(6.25f, BeltHeight, -2.35f),
        new Vector3(6.15f, BeltHeight, -1.9f), new Vector3(6.15f, BeltHeight, 4.4f),
        new Vector3(1.6f, BeltHeight, 4.4f), new Vector3(1.2f, BeltHeight, 3.9f),
        new Vector3(-.65f, 1.9f, 3.9f), new Vector3(-1.15f, 1.5f, 3.5f), new Vector3(-1.2f, 1.2f, 2.95f),
    } : new[]
    {
        new Vector3(6.25f, .25f, -2.75f), new Vector3(6.25f, BeltHeight, -2.75f),
        new Vector3(5.5f, BeltHeight, -2.75f), new Vector3(5.5f, BeltHeight, 3.7f),
        new Vector3(2.1f, BeltHeight, 3.7f), new Vector3(1.7f, BeltHeight, 3.25f),
        new Vector3(.8f, BeltHeight - .1f, 3.25f), new Vector3(-.2f, 2.2f, 3.25f),
        new Vector3(-.55f, 1.5f, 3.05f), new Vector3(-.6f, 1.2f, 2.95f),
    };

    public static GarageConveyorLayout Ensure(GarageRig rig)
    {
        if (rig == null) return null;
        var layout = rig.GetComponent<GarageConveyorLayout>();
        if (layout != null && layout.Applied) return layout;
        var conveyor = rig.transform.Find("Conveyor");
        if (conveyor == null) return null; // Not an authored upgrade rig; never search another business for one.
        var feeder = conveyor.GetComponent<ConveyorFeeder>();
        if (feeder == null || feeder.lanes == null || feeder.lanes.Length != 2 ||
            feeder.lanes[0] == null || feeder.lanes[1] == null) return null;
        foreach (var line in feeder.lanes)
            if (!line.transform.IsChildOf(conveyor) || line.transform.Find("Visual") == null) return null;
        if (layout == null) layout = rig.gameObject.AddComponent<GarageConveyorLayout>();
        layout.Apply(rig, conveyor, feeder);
        return layout;
    }

    void Apply(GarageRig rig, Transform conveyor, ConveyorFeeder feeder)
    {
        float floor = rig.table != null ? rig.transform.InverseTransformPoint(rig.table.transform.position).y : WorkshopAnnex.FloorY;
        Vector3 World(Vector3 p) => rig.transform.TransformPoint(p + Vector3.up * floor);
        var elevator = conveyor.Find("BagOVator");
        if (elevator != null) elevator.SetPositionAndRotation(World(Elevator), rig.transform.rotation * WallFacing);
        var directFeed = conveyor.Find("DirectFeedChute");
        if (directFeed != null) directFeed.SetPositionAndRotation(World(DirectFeed), rig.transform.rotation * Quaternion.Euler(0f, 180f, 0f));
        var kevin = rig.transform.Find("KevinChute");
        if (kevin != null) kevin.SetPositionAndRotation(World(KevinChute), rig.transform.rotation * WallFacing);

        // Keep the gated Visual objects themselves: GarageUpgradeVisuals still owns their active state.
        var materials = new Materials(rig);
        foreach (var line in feeder.lanes)
        {
            Vector3[] route = Route(line.lane);
            var points = new Transform[route.Length];
            for (int i = 0; i < route.Length; i++)
            {
                var old = line.waypoints != null && i < line.waypoints.Length ? line.waypoints[i] : null;
                points[i] = old != null && old.IsChildOf(line.transform) ? old : Node(line.transform, "WP" + i);
                points[i].position = World(route[i]);
            }
            line.waypoints = points;
            line.BuildPath();
            var visual = line.transform.Find("Visual");
            for (int i = visual.childCount - 1; i >= 0; i--)
            {
                var old = visual.GetChild(i).gameObject;
                old.SetActive(false); // Also stops obsolete colliders immediately before deferred Destroy.
                if (Application.isPlaying) Destroy(old); else DestroyImmediate(old);
            }
            var rollers = new List<Transform>();
            Trough(visual, "Elevator exit", World(route[1]), World(route[2]), materials.rubber, materials.yellow);
            for (int i = 2; i + 1 < route.Length; i++)
            {
                Vector3 a = World(route[i]), b = World(route[i + 1]);
                if (Mathf.Abs(a.y - b.y) < .05f) Belt(visual, a, b, materials, rollers, World(new Vector3(0f, 8f, 0f)).y);
                else Trough(visual, "Slide", a, b, materials.steel, materials.yellow);
            }
            line.rollers = rollers.ToArray();
        }
        Applied = true;
    }

    sealed class Materials
    {
        public readonly Material rubber, yellow, steel, frame;
        public Materials(GarageRig rig)
        {
            var renderers = rig.GetComponentsInChildren<Renderer>(true);
            rubber = Find(renderers, "Garage_Rubber", new Color(.08f, .1f, .11f));
            yellow = Find(renderers, "Garage_Yellow", new Color(.96f, .71f, .16f));
            steel = Find(renderers, "Garage_Steel", new Color(.56f, .61f, .64f));
            frame = Find(renderers, "Garage_Frame", new Color(.19f, .25f, .27f));
        }
        static Material Find(Renderer[] renderers, string name, Color fallback)
        {
            foreach (var renderer in renderers)
                if (renderer.sharedMaterial != null && renderer.sharedMaterial.name == name) return renderer.sharedMaterial;
            return SortingKit.RuntimeMaterial("mat/wall_belt/" + name, fallback);
        }
    }

    static Transform Node(Transform parent, string name)
    {
        var go = new GameObject(name); go.transform.SetParent(parent, false); return go.transform;
    }

    static void Mesh(Transform parent, string name, Vector3 at, Vector3 size, Quaternion rotation, Material material, bool cylinder = false)
    {
        var t = Node(parent, name);
        t.SetPositionAndRotation(at, rotation);
        Vector3 scale = t.parent.lossyScale;
        t.localScale = new Vector3(size.x / scale.x, size.y / scale.y, size.z / scale.z);
        t.gameObject.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>(cylinder ? "Cylinder.fbx" : "Cube.fbx");
        t.gameObject.AddComponent<MeshRenderer>().sharedMaterial = material;
        // Carriers are scripted; overhead belts need no player, truck or loose-trash collision shell.
    }

    static void Belt(Transform parent, Vector3 a, Vector3 b, Materials m, List<Transform> rollers, float ceiling)
    {
        Vector3 d = b - a; float length = d.magnitude;
        if (length < .05f) return;
        d /= length;
        Quaternion along = Quaternion.LookRotation(d, Vector3.up);
        Vector3 side = Vector3.Cross(Vector3.up, d), mid = (a + b) * .5f;
        Mesh(parent, "Belt", mid - Vector3.up * .04f, new Vector3(.5f, .06f, length + .15f), along, m.rubber);
        foreach (float s in new[] { -.28f, .28f })
            Mesh(parent, "Rail", mid + side * s + Vector3.up * .06f, new Vector3(.04f, .14f, length + .15f), along, m.yellow);
        for (float at = 0f; at <= length; at += .6f)
        {
            var roller = Node(parent, "Roller"); roller.SetPositionAndRotation(a + d * at - Vector3.up * .1f, along);
            Mesh(roller, "Drum", roller.position, new Vector3(.08f, .26f, .08f), along * Quaternion.Euler(0f, 0f, 90f), m.steel, true);
            rollers.Add(roller);
        }
        for (float at = .6f; at < length; at += 1.6f)
            foreach (float s in new[] { -.3f, .3f })
            {
                Vector3 p = a + d * at + side * s;
                Mesh(parent, "Hanger", new Vector3(p.x, (p.y + ceiling) * .5f, p.z),
                    new Vector3(.03f, ceiling - p.y, .03f), Quaternion.identity, m.frame);
            }
    }

    static void Trough(Transform parent, string name, Vector3 a, Vector3 b, Material floor, Material walls)
    {
        Vector3 d = b - a;
        if (d.sqrMagnitude < .0025f) return;
        Quaternion rotation = Quaternion.LookRotation(d.normalized, Vector3.up);
        Vector3 up = rotation * Vector3.up, side = rotation * Vector3.right, mid = (a + b) * .5f;
        Mesh(parent, name, mid - up * .03f, new Vector3(.5f, .04f, d.magnitude), rotation, floor);
        foreach (float s in new[] { -.25f, .25f })
            Mesh(parent, name + " wall", mid + side * s + up * .07f, new Vector3(.03f, .18f, d.magnitude), rotation, walls);
    }
}
