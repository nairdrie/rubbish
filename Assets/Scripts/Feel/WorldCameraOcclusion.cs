using System.Collections.Generic;
using Cinemachine;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// The outdoor half of the camera's "never look through a wall" rule. The authored Cinemachine collider on the walking
/// and driving cameras only knows the Walls layer (the garage). Out in the town the things that hide the view are houses,
/// the depot you have just driven out of, and tree canopies, which have no physics at all. This extension pulls the
/// camera in along its own line of sight until the player / truck / Hoverhog is visible again:
/// <list type="bullet">
/// <item><b>Buildings</b> (tall, wide, static colliders on Default / Ground / Walls): pulled in on the first frame, eased
/// back out, exactly like the shop.</item>
/// <item><b>Tree canopies</b> (<see cref="WorldCanopies"/>): eased in and out, so a tree-lined street does not make the
/// view pump.</item>
/// </list>
/// Poles, trunks, fences, hedges, parked cars, anything with a rigidbody and anything low never count. It never pulls
/// closer than <see cref="minDistance"/>, and it does nothing indoors (the shop keeps its own rule).
/// Added at runtime (<see cref="WorldCameraRuntime"/>): no camera prefab is edited.
/// </summary>
[DisallowMultipleComponent]
public sealed class WorldCameraOcclusion : CinemachineExtension
{
    public const float Radius = 0.35f, MinTall = 2.5f, MinWide = 1.5f;
    const int Mask = (1 << SortingKit.GroundLayer) | (1 << SortingKit.WallsLayer) | 1;
    static readonly RaycastHit[] hits = new RaycastHit[48];

    /// <summary>The closest the camera may come to what it follows.</summary>
    public float minDistance = 1.5f;
    /// <summary>The followed thing's own colliders (a truck's body, the Hoverhog) never hide it.</summary>
    public Transform ignoreRoot;

    sealed class State { public float distance = -1f; }

    public static WorldCameraOcclusion Ensure(CinemachineVirtualCameraBase camera, float minDistance, Transform ignoreRoot)
    {
        if (camera == null) return null;
        var extension = camera.GetComponent<WorldCameraOcclusion>();
        if (extension == null) extension = camera.gameObject.AddComponent<WorldCameraOcclusion>();
        extension.minDistance = minDistance;
        extension.ignoreRoot = ignoreRoot;
        return extension;
    }

    /// <summary>Out in the town? In co-op the shops are pocket interiors far below it; in solo the shop is its own scene.</summary>
    public static bool Outdoors(Vector3 target) => DepotRowWorld.Active ? target.y > -5f : TippingFloor.All.Count == 0;

    /// <summary>Does this collider count as a building: static, taller than a person and wider than a pole?</summary>
    public static bool IsBuilding(Collider c, Transform ignoreRoot)
    {
        if (c == null || c is WheelCollider || c is CharacterController || c.attachedRigidbody != null) return false;
        if (ignoreRoot != null && c.transform.IsChildOf(ignoreRoot)) return false;
        Vector3 size = c.bounds.size;
        return size.y >= MinTall && Mathf.Max(size.x, size.z) >= MinWide;
    }

    /// <summary>Distance from <paramref name="target"/> along <paramref name="direction"/> to the first building, or
    /// <paramref name="distance"/> when the way is clear.</summary>
    public static float BuildingDistance(Vector3 target, Vector3 direction, float distance, Transform ignoreRoot)
    {
        if (distance < 0.001f) return distance;
        int count = Physics.SphereCastNonAlloc(target, Radius, direction, hits, distance, Mask, QueryTriggerInteraction.Ignore);
        float clear = distance;
        for (int i = 0; i < count; i++)
        {
            // Distance 0 = already touching at the target (a truck parked against a wall): nothing to pull in front of.
            if (hits[i].distance <= 0f || !IsBuilding(hits[i].collider, ignoreRoot)) continue;
            clear = Mathf.Min(clear, Mathf.Max(0.05f, hits[i].distance - 0.05f));
        }
        return clear;
    }

    protected override void PostPipelineStageCallback(CinemachineVirtualCameraBase camera, CinemachineCore.Stage stage,
        ref CameraState state, float deltaTime)
    {
        if (stage != CinemachineCore.Stage.Body) return;
        var memory = GetExtraState<State>(camera);
        if (!state.HasLookAt && camera.Follow == null) return;
        Vector3 target = state.HasLookAt ? state.ReferenceLookAt : camera.Follow.position;
        if (!Outdoors(target)) { memory.distance = -1f; return; }
        Vector3 offset = state.CorrectedPosition - target;
        float wanted = offset.magnitude;
        if (wanted <= minDistance) { memory.distance = -1f; return; }
        Vector3 direction = offset / wanted;

        float wall = Mathf.Max(minDistance, BuildingDistance(target, direction, wanted, ignoreRoot));
        float leaves = Mathf.Max(minDistance, WorldCanopies.Distance(target, direction, wanted, Radius));
        float clear = Mathf.Min(wall, leaves);
        float resolved;
        if (deltaTime < 0f || memory.distance < 0f) resolved = clear;
        else
        {
            // Walls: in at once, never through. Leaves: ease in. Everything eases back out.
            float from = Mathf.Min(memory.distance, wall);
            float tau = clear < from ? 0.12f : 0.45f;
            resolved = Mathf.Lerp(from, clear, 1f - Mathf.Exp(-deltaTime / tau));
        }
        resolved = Mathf.Clamp(resolved, Mathf.Min(minDistance, wanted), wanted);
        memory.distance = resolved;
        state.PositionCorrection += direction * (resolved - wanted);
    }
}

/// <summary>
/// Where the town's tree canopies are, for the camera (trees only have a trunk collider). Built once per set of loaded
/// scenes from the renderers of anything named like a tree; each canopy is a ball sitting in the top of its bounds, so
/// the space under the leaves, where trucks and people are, stays clear.
/// </summary>
public static class WorldCanopies
{
    struct Ball { public Vector3 centre; public float radius; }

    const float Cell = 16f;
    static readonly Dictionary<long, List<Ball>> grid = new Dictionary<long, List<Ball>>();
    static bool built, hooked;
    public static int Count { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        grid.Clear();
        built = hooked = false;
        Count = 0;
    }

    static void Dirty(Scene scene, LoadSceneMode mode) => built = false;
    static void Dirty(Scene scene) => built = false;

    static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

    /// <summary>Is this the name of a tree (or a clump of them)? "street" must not match.</summary>
    public static bool IsTreeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string n = name.ToLowerInvariant();
        return n.Contains(".tree") || n.Contains("_tree") || n.StartsWith("tree") || n.Contains(".clump") || n.Contains("edges.wood");
    }

    /// <summary>The canopy ball for a tree renderer's bounds, or radius 0 when it is not one (a trunk, a whole wood).</summary>
    public static bool CanopyOf(Bounds b, out Vector3 centre, out float radius)
    {
        centre = default;
        radius = 0f;
        float wide = Mathf.Max(b.size.x, b.size.z);
        if (b.size.y < 3f || wide < 1.5f || wide > 20f) return false;
        radius = Mathf.Min(wide * 0.45f, b.size.y * 0.5f);
        centre = new Vector3(b.center.x, b.max.y - radius * 0.95f, b.center.z);
        return true;
    }

    static void Build()
    {
        built = true;
        if (!hooked)
        {
            hooked = true;
            SceneManager.sceneLoaded += Dirty;
            SceneManager.sceneUnloaded += Dirty;
        }
        grid.Clear();
        Count = 0;
        foreach (var r in Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
        {
            if (r == null || !r.enabled) continue;
            var t = r.transform;
            bool tree = false;
            for (int up = 0; up < 3 && t != null && !tree; up++, t = t.parent) tree = IsTreeName(t.name);
            if (!tree || !CanopyOf(r.bounds, out Vector3 centre, out float radius)) continue;
            var ball = new Ball { centre = centre, radius = radius };
            int x0 = Mathf.FloorToInt((centre.x - radius) / Cell), x1 = Mathf.FloorToInt((centre.x + radius) / Cell);
            int z0 = Mathf.FloorToInt((centre.z - radius) / Cell), z1 = Mathf.FloorToInt((centre.z + radius) / Cell);
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    long key = Key(x, z);
                    if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<Ball>();
                    list.Add(ball);
                }
            Count++;
        }
    }

    /// <summary>Where a ray from <paramref name="origin"/> first enters a ball (grown by <paramref name="pad"/>), or
    /// <paramref name="distance"/>. A ray that starts inside one ignores it: you cannot pull in front of leaves you are under.</summary>
    public static float RayBall(Vector3 origin, Vector3 direction, float distance, Vector3 centre, float radius, float pad)
    {
        Vector3 to = centre - origin;
        float r = radius + pad;
        float c = to.sqrMagnitude - r * r;
        if (c <= 0f) return distance;
        float along = Vector3.Dot(to, direction);
        if (along <= 0f) return distance;
        float disc = along * along - c;
        if (disc <= 0f) return distance;
        float t = along - Mathf.Sqrt(disc);
        return t < distance ? t : distance;
    }

    /// <summary>Distance from <paramref name="origin"/> along <paramref name="direction"/> to the first canopy.</summary>
    public static float Distance(Vector3 origin, Vector3 direction, float distance, float pad)
    {
        if (!built) Build();
        if (Count == 0 || distance < 0.001f) return distance;
        float clear = distance;
        long last = long.MinValue;
        // The ray crosses a cell every few metres; canopies are registered in every cell they touch.
        int steps = Mathf.CeilToInt(distance / (Cell * 0.5f));
        for (int i = 0; i <= steps; i++)
        {
            Vector3 p = origin + direction * Mathf.Min(distance, i * Cell * 0.5f);
            long key = Key(Mathf.FloorToInt(p.x / Cell), Mathf.FloorToInt(p.z / Cell));
            if (key == last || !grid.TryGetValue(key, out var list)) continue;
            last = key;
            for (int k = 0; k < list.Count; k++)
                clear = Mathf.Min(clear, RayBall(origin, direction, clear, list[k].centre, list[k].radius, pad));
        }
        return clear;
    }
}

/// <summary>Puts <see cref="WorldCameraOcclusion"/> on this machine's walking camera and on every truck's driving camera
/// (the Hoverhog adds its own). Runtime only, so the camera prefabs and their authored shop rule are untouched.</summary>
public class WorldCameraRuntime : MonoBehaviour
{
    public const float OnFootMin = 1.4f, TruckMin = 6f, HoverhogMin = 7f;
    static WorldCameraRuntime instance;
    float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (instance != null) return;
        var go = new GameObject("[World camera]");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<WorldCameraRuntime>();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => instance = null;

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + 1f;
        var me = PlayerController.Local; // coop:allow(local) cameras belong to this machine's own player
        if (me != null && me.playerCamVCam != null && me.playerCamVCam.GetComponent<WorldCameraOcclusion>() == null)
            WorldCameraOcclusion.Ensure(me.playerCamVCam, OnFootMin, me.transform);
        var trucks = TruckControllerV2.All;
        for (int i = 0; i < trucks.Count; i++)
        {
            var truck = trucks[i];
            if (truck == null) continue;
            var cam = truck.GetComponentInChildren<CinemachineVirtualCamera>(true);
            if (cam != null && cam.GetComponent<WorldCameraOcclusion>() == null) WorldCameraOcclusion.Ensure(cam, TruckMin, truck.transform);
        }
    }
}
