using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// WorldLife W0 CONTRACT (Docs/WorldLife.md §6): marks a piece of street furniture as knockable. NeighborhoodBuilder
/// (package A) adds it to each lamp, mailbox, bin, sign, picket panel... with a stable <see cref="id"/> in build order
/// and gives it primitive rest colliders; package B runs the lifecycle (rest → proxy → settle → reset) by id.
///
/// The object itself stays batching-static and never moves: a knock disables its <see cref="restColliders"/> and
/// <see cref="renderers"/> and B flies a proxy (from <see cref="proxyKey"/>) instead. Never Instantiate this object —
/// its mesh may be the combined static batch.
/// </summary>
[DisallowMultipleComponent]
public class Knockable : MonoBehaviour
{
    [Tooltip("Stable id in build order (unique per scene). The down bitset, claims and events use it.")]
    public ushort id;
    public KnockKind kind;
    [Tooltip("Proxy source: 'knock/<prefabName>' (RubbishAssets prefab) for prefab props, or 'recipe/<name>' for builder-made ones.")]
    public string proxyKey = "";
    [Tooltip("Colliders that are solid at rest and switched off while down. Empty = every collider under this object.")]
    public Collider[] restColliders;
    [Tooltip("Renderers hidden while down (TMP text included). Empty = every renderer under this object.")]
    public Renderer[] renderers;
    [Tooltip("The house this belongs to (mailbox, bin, hoop, flamingo...), else empty.")]
    public string stopId = "";
    [Tooltip("District byte (WorldLifeIds.DistrictIndex): where City Works bills it.")]
    public byte district;

    /// <summary>Down right now on this machine (B sets it; the sensor skips down props).</summary>
    [System.NonSerialized] public bool IsDown;

    public WorldLifeTuning.KnockSpec Spec => WorldLifeTuning.Spec(kind);

    // ------------------------------------------------------------------ registry (enabled knockables in loaded scenes)

    static readonly List<Knockable> all = new List<Knockable>(1024);
    static readonly Dictionary<Collider, Knockable> byCollider = new Dictionary<Collider, Knockable>(2048);
    static readonly Dictionary<ushort, Knockable> byId = new Dictionary<ushort, Knockable>(1024);

    public static IReadOnlyList<Knockable> All => all;
    public static int Count => all.Count;

    /// <summary>The knockable owning this collider (a rest collider or any collider under it), or null. O(1).</summary>
    public static Knockable ForCollider(Collider c) => c != null && byCollider.TryGetValue(c, out var k) ? k : null;

    public static Knockable ById(ushort knockId) => byId.TryGetValue(knockId, out var k) ? k : null;

    /// <summary>The rest colliders (resolved: the assigned list, else every collider under this object).</summary>
    public Collider[] RestColliders
    {
        get
        {
            if (restColliders == null || restColliders.Length == 0) restColliders = GetComponentsInChildren<Collider>(true);
            return restColliders;
        }
    }

    /// <summary>The renderers to hide while down (resolved: the assigned list, else every renderer under this object).</summary>
    public Renderer[] Renderers
    {
        get
        {
            if (renderers == null || renderers.Length == 0) renderers = GetComponentsInChildren<Renderer>(true);
            return renderers;
        }
    }

    void OnEnable()
    {
        all.Add(this);
        if (byId.TryGetValue(id, out var other) && other != null && other != this)
            Debug.LogWarning($"[Knockable] Duplicate id {id}: '{name}' and '{other.name}'.", this);
        byId[id] = this;
        var cols = RestColliders;
        for (int i = 0; i < cols.Length; i++) if (cols[i] != null) byCollider[cols[i]] = this;
    }

    void OnDisable()
    {
        all.Remove(this);
        if (byId.TryGetValue(id, out var k) && k == this) byId.Remove(id);
        var cols = restColliders;
        if (cols != null)
            for (int i = 0; i < cols.Length; i++)
                if (cols[i] != null && byCollider.TryGetValue(cols[i], out var o) && o == this) byCollider.Remove(cols[i]);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { all.Clear(); byCollider.Clear(); byId.Clear(); }
}
