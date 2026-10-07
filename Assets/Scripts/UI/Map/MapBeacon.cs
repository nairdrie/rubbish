using System.Collections.Generic;
using UnityEngine;

public enum MapBeaconKind : byte { Truck, Player, Car }

/// <summary>
/// Something the minimap and full map draw as a moving icon (Docs/UIPass.md §7.5): trucks (top-down truck in the company
/// tint, rotated to heading), players (arrowheads in their Dumpling colour) and, optionally, cars (small grey dots). A tiny
/// component with a static registry, so the map never searches the scene (no Find*ObjectByType, MP rule 3).
/// <list type="bullet">
/// <item>Trucks: <c>Setup_MapBeacons</c> adds one to <c>Truck V2.prefab</c> (run after <c>Setup_Coop</c>).</item>
/// <item>Players: added at runtime on <see cref="CoopPlayerHooks.Spawned"/> (no Player.prefab edit; Character and Coop share
/// that prefab).</item>
/// </list>
/// Presentation only: nothing is networked; every machine draws what it already has (transforms are mirrored by NGO).
/// </summary>
[DisallowMultipleComponent]
public sealed class MapBeacon : MonoBehaviour
{
    public MapBeaconKind kind = MapBeaconKind.Truck;
    [Tooltip("Icon colour. Trucks: the company tint (Rubbish Co. olive until M1's CompanyState.Tint). Players: their Dumpling colour.")]
    public Color tint = new Color(0.357f, 0.42f, 0.184f, 1f);
    [Tooltip("Track this transform instead of our own (e.g. a body that moves separately from the root).")]
    public Transform overrideTarget;

    static readonly List<MapBeacon> all = new List<MapBeacon>(8);

    /// <summary>Every enabled beacon in loaded scenes.</summary>
    public static IReadOnlyList<MapBeacon> All => all;

    /// <summary>The transform to draw (override or own).</summary>
    public Transform Target => overrideTarget != null ? overrideTarget : transform;

    /// <summary>Rubbish Co. truck olive (#5B6B2F).</summary>
    public static readonly Color TruckOlive = new Color(0.357f, 0.42f, 0.184f, 1f);

    void OnEnable() { if (!all.Contains(this)) all.Add(this); }
    void OnDisable() => all.Remove(this);

    /// <summary>The player this beacon follows, if it's a player beacon.</summary>
    public PlayerController Player { get; private set; }

    /// <summary>The Dumpling colour for a player: roster slot → ChuckPartsData.Variants[slot].playerColor.</summary>
    public static Color PlayerColor(PlayerController p)
    {
        int slot = -1;
        if (p != null)
        {
            var rv = p.GetComponent<RosterView>();
            if (rv != null) slot = rv.Slot;
            if (slot < 0)
            {
                var puppet = p.GetComponent<ChuckPuppet>();
                if (puppet != null && puppet.variantOverride >= 0) slot = puppet.variantOverride;
            }
            if (slot < 0 && p.IsSpawned) slot = (int)(p.OwnerClientId % 4);
        }
        var v = ChuckPartsData.Variants;
        if (v == null || v.Length == 0) return new Color(1f, 0.48f, 0.12f);
        return v[Mathf.Clamp(slot < 0 ? 0 : slot, 0, v.Length - 1)].playerColor;
    }

    /// <summary>Refresh a player beacon's tint (the roster slot can arrive after the player spawns).</summary>
    public void RefreshPlayerTint()
    {
        if (kind == MapBeaconKind.Player && Player != null) tint = PlayerColor(Player);
    }

    static void OnPlayerSpawned(PlayerController p)
    {
        if (p == null) return;
        var b = p.GetComponent<MapBeacon>();
        if (b == null) b = p.gameObject.AddComponent<MapBeacon>();
        b.kind = MapBeaconKind.Player;
        b.Player = p;
        b.tint = PlayerColor(p);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => all.Clear();

    // CoopPlayerHooks resets its event at SubsystemRegistration; subscribe after that.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Hook()
    {
        CoopPlayerHooks.Spawned -= OnPlayerSpawned;
        CoopPlayerHooks.Spawned += OnPlayerSpawned;
    }
}
