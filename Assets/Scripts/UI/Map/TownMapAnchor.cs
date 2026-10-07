using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The town map's scene marker (Docs/UIPass.md §7.2): <c>Setup_TownMap</c> puts one on a root <c>[TownMapAnchor]</c> in
/// Neighborhood.unity, outside NeighborhoodGen (builder re-runs keep it), storing the bake's <see cref="sourceHash"/>. While
/// one is enabled the minimap shows; in the garage there is none (no scene-name checks, MP rule 6).
/// <para><b>Staleness.</b> In the Editor and development builds the anchor rehashes the live town once on enable
/// (<see cref="TownMapData.HashScene"/>: ground rects, stops, gates). If that differs from the asset's hash (the town was rebuilt
/// without a re-bake) the map still works, and one warning plus a dev toast say "Town map is stale: run Rubbish/Setup/Town Map".</para>
/// </summary>
[DisallowMultipleComponent]
public sealed class TownMapAnchor : MonoBehaviour
{
    [Tooltip("TownMap.asset's sourceHash when this anchor was baked.")]
    public string sourceHash = "";

    static readonly List<TownMapAnchor> enabledAnchors = new List<TownMapAnchor>(2);
    static bool warned;

    /// <summary>The most recently enabled anchor, or null (garage, menus).</summary>
    public static TownMapAnchor Active => enabledAnchors.Count > 0 ? enabledAnchors[enabledAnchors.Count - 1] : null;

    /// <summary>The live town's hash (dev check), or null when not computed.</summary>
    public string LiveHash { get; private set; }

    /// <summary>The baked map no longer matches the town it's in (dev check).</summary>
    public bool Stale { get; private set; }

    void OnEnable()
    {
        if (!enabledAnchors.Contains(this)) enabledAnchors.Add(this);
        if (Application.isPlaying && (Application.isEditor || Debug.isDebugBuild)) CheckStale();
    }

    void OnDisable() => enabledAnchors.Remove(this);

    /// <summary>Compare the asset's hash with this anchor's and with the live town (once per anchor).</summary>
    public void CheckStale()
    {
        var data = TownMapData.Load();
        if (data == null) return;
        Transform gen = null;
        foreach (var r in gameObject.scene.GetRootGameObjects())
            if (r.name == "NeighborhoodGen") { gen = r.transform; break; }
        if (gen == null) return;
        LiveHash = TownMapData.HashScene(gen);
        Stale = LiveHash != data.sourceHash || (!string.IsNullOrEmpty(sourceHash) && sourceHash != data.sourceHash);
        if (!Stale || warned) return;
        warned = true;
        Debug.LogWarning($"[TownMap] {UiText.MapStale} (asset {data.sourceHash}, anchor {sourceHash}, town {LiveHash})", this);
        GameHUD.Toast("DEV: " + UiText.MapStale, ToastStyle.Bad, 5f);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        enabledAnchors.Clear();
        warned = false;
    }
}
