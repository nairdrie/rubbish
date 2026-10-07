using System.Collections.Generic;
using UnityEngine;

/// <summary>Loose junk remains raycastable/solid against the world, but cannot block people or trucks.</summary>
public sealed class LooseTrashCollisions : MonoBehaviour
{
    static readonly List<Collider> actors = new List<Collider>();
    static float nextRefresh;
    Collider body;
    float nextApply;
    bool wasEnabled;

    public static bool IsSolidActor(Collider c) => c != null && !c.isTrigger &&
        c.GetComponentInParent<Carryable>() == null &&
        (c.GetComponentInParent<PlayerController>() != null || c.GetComponentInParent<TruckControllerV2>() != null);

    void Awake() { body = GetComponent<Collider>(); Apply(); }
    void FixedUpdate()
    {
        if (body == null) return;
        bool on = body.enabled && body.gameObject.activeInHierarchy;
        if (on && (!wasEnabled || Time.time >= nextApply)) Apply();
        wasEnabled = on;
    }

    public void Apply()
    {
        if (body == null) body = GetComponent<Collider>();
        if (body == null || !body.enabled) return;
        if (Time.time >= nextRefresh || actors.Count == 0)
        {
            nextRefresh = Time.time + 0.4f;
            actors.Clear();
            foreach (var player in NightPlayers.All) if (player != null) Add(player.transform);
            foreach (var truck in TruckControllerV2.All) if (truck != null) Add(truck.transform);
            foreach (var life in LifeBodies.All)
                if (life is ResidentAgent resident && resident.rig?.root != null) Add(resident.rig.root);
        }
        foreach (var actor in actors)
            if (actor != null && actor.enabled && actor.gameObject.activeInHierarchy) Physics.IgnoreCollision(body, actor, true);
        nextApply = Time.time + 0.4f;
    }

    static void Add(Transform root)
    {
        foreach (var c in root.GetComponentsInChildren<Collider>(true))
            if (!c.isTrigger && c.GetComponentInParent<Carryable>() == null && !actors.Contains(c)) actors.Add(c);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { actors.Clear(); nextRefresh = 0f; }
}
