using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// WorldLife W0 CONTRACT (Docs/WorldLife.md §2.2): the scene marker that switches neighbourhood life on. NeighborhoodBuilder
/// (package A) puts one under NeighborhoodGen; sandbox scenes add one with <see cref="useTestGraph"/>. The DontDestroyOnLoad
/// <see cref="WorldLifeRunner"/> wakes while an anchor is enabled and sleeps when it goes away, and every presentation
/// object lives in the anchor's scene, so it all vanishes with the scene. No scene-name checks (rule 6).
/// Creating one from code? AddComponent it to an INACTIVE GameObject, set the fields, then activate it: the runner reads
/// <see cref="Graph"/> when the anchor enables.
/// </summary>
[DisallowMultipleComponent]
public sealed class WorldLifeAnchor : MonoBehaviour
{
    [Tooltip("The street graph (Assets/Resources/StreetGraph.asset, written by NeighborhoodBuilder). Empty = load it from Resources.")]
    public StreetGraph graph;
    [Tooltip("Sandbox scenes: use StreetGraph.Test() (the hand-made 2-block graph) instead of the asset.")]
    public bool useTestGraph;
    [Tooltip("The builder seed: the Town seed until M1 hashes TownMeta.guid (F sets WorldLifeRunner.TownSeedProvider).")]
    public int builderSeed = 1234;

    StreetGraph resolved;
    bool resolvedTest;
    StreetGraph resolvedFrom;

    /// <summary>The graph this anchor runs on (never null: an empty graph when nothing is available, with a warning).
    /// Re-resolved when <see cref="graph"/> or <see cref="useTestGraph"/> change.</summary>
    public StreetGraph Graph
    {
        get
        {
            if (resolved != null && resolvedTest == useTestGraph && resolvedFrom == graph) return resolved;
            resolvedTest = useTestGraph;
            resolvedFrom = graph;
            if (useTestGraph) resolved = StreetGraph.Test();
            else if (graph != null) resolved = graph;
            else resolved = StreetGraph.Load();
            if (resolved == null)
            {
                Debug.LogWarning("[WorldLife] No StreetGraph asset yet (package A writes Resources/StreetGraph.asset); running on an empty graph.", this);
                resolved = StreetGraph.Empty();
            }
            if (!useTestGraph && Application.isPlaying) resolved = NeighborhoodConnection.WithConnection(resolved);
            return resolved;
        }
    }

    // ------------------------------------------------------------------ registry

    static readonly List<WorldLifeAnchor> enabledAnchors = new List<WorldLifeAnchor>(2);

    /// <summary>The most recently enabled anchor, or null.</summary>
    public static WorldLifeAnchor Latest => enabledAnchors.Count > 0 ? enabledAnchors[enabledAnchors.Count - 1] : null;
    public static int EnabledCount => enabledAnchors.Count;

    public static event Action<WorldLifeAnchor> AnchorEnabled;
    public static event Action<WorldLifeAnchor> AnchorDisabled;

    void OnEnable()
    {
        if (Application.isPlaying && !useTestGraph) NeighborhoodConnection.Ensure(gameObject.scene);
        enabledAnchors.Remove(this);
        enabledAnchors.Add(this);
        Fire(AnchorEnabled);
    }

    void OnDisable()
    {
        enabledAnchors.Remove(this);
        Fire(AnchorDisabled);
    }

    void Fire(Action<WorldLifeAnchor> h)
    {
        if (h == null) return;
        foreach (var d in h.GetInvocationList())
            try { ((Action<WorldLifeAnchor>)d)(this); } catch (Exception e) { Debug.LogException(e); }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { enabledAnchors.Clear(); AnchorEnabled = null; AnchorDisabled = null; }
}
