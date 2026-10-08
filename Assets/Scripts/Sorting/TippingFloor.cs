using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The bay's tipping floor: the painted box behind the parked truck where a tipped load lands (Docs/Progression.md
/// §6.1-§6.2). Tipping itself lives on the truck now (<see cref="TruckTipper"/>: the driver holds the tip key); when the
/// truck stands inside a shop, the bags fly here, exactly where the old floor lever put them, so the Bag Belt, the
/// backlog cap (<see cref="DepotBacklog"/>), the raccoon crew's deliveries and the Hauler's hatch all share one pile.
/// No interaction of its own. Built by Setup_SortingStation; Depot Row copies get their own (Setup_DepotRow).
/// </summary>
public class TippingFloor : MonoBehaviour
{
    static readonly List<TippingFloor> floors = new List<TippingFloor>();
    /// <summary>Every enabled tipping floor in the loaded scenes.</summary>
    public static IReadOnlyList<TippingFloor> All => floors;

    [Header("Landing zone (world space)")]
    public Vector3 pileCenter = new Vector3(5.75f, 0f, -3.2f);
    [Tooltip("World X / Z extent of the landing area.")]
    public Vector2 pileSize = new Vector2(1.5f, 2.2f);
    [Tooltip("The bay this floor serves (Depot Row pocket interiors set it). Empty = this object.")]
    public Transform bayAnchor;
    [Tooltip("Outside the one world (a stand-alone garage scene) a truck within this many metres of the bay tips here.")]
    public float soloRange = 25f;

    /// <summary>A truck is tipping onto this floor right now (set by the host's TruckTipper).</summary>
    public bool Busy { get; internal set; }

    /// <summary>The Depot Row lot this floor is in (−1 outside the one world).</summary>
    public int Lot => DepotRowWorld.Active ? DepotRowLayout.InteriorAt(transform.position) : -1;

    void OnEnable() { if (!floors.Contains(this)) floors.Add(this); }
    void OnDisable() { floors.Remove(this); Busy = false; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => floors.Clear();

    /// <summary>
    /// The tipping floor a truck standing at <paramref name="truckPos"/> would tip onto, or null when it is outdoors.
    /// One world: the floor of the shop interior the truck is in. Otherwise: the nearest floor within its soloRange.
    /// <paramref name="insideAShop"/> is true when the truck is inside some shop interior, with or without a floor.
    /// </summary>
    public static TippingFloor At(Vector3 truckPos, out bool insideAShop)
    {
        insideAShop = false;
        if (DepotRowWorld.Active)
        {
            int lot = DepotRowLayout.InteriorAt(truckPos);
            if (lot < 0) return null;
            insideAShop = true;
            for (int i = 0; i < floors.Count; i++)
                if (floors[i] != null && DepotRowLayout.InteriorAt(floors[i].transform.position) == lot) return floors[i];
            return null;
        }
        TippingFloor best = null;
        float bestD = float.MaxValue;
        for (int i = 0; i < floors.Count; i++)
        {
            var f = floors[i];
            if (f == null) continue;
            Vector3 d = truckPos - (f.bayAnchor != null ? f.bayAnchor.position : f.transform.position);
            if (Mathf.Abs(d.y) > 6f) continue;
            d.y = 0f;
            float m = d.magnitude;
            if (m <= f.soloRange && m < bestD) { bestD = m; best = f; }
        }
        insideAShop = best != null;
        return best;
    }

    /// <summary>A landing point in the pile for the thing called <paramref name="key"/> (a bag id): stable scatter, no RNG.</summary>
    public Vector3 PilePoint(float y, string key) => new Vector3(
        pileCenter.x + (GarageRoll.Value((key ?? "bag") + "/pile/x") - 0.5f) * pileSize.x, y,
        pileCenter.z + (GarageRoll.Value((key ?? "bag") + "/pile/z") - 0.5f) * pileSize.y);

    public Vector3 ClampToPile(Vector3 p, float y)
    {
        float hx = pileSize.x * 0.5f, hz = pileSize.y * 0.5f;
        return new Vector3(Mathf.Clamp(p.x, pileCenter.x - hx, pileCenter.x + hx), y, Mathf.Clamp(p.z, pileCenter.z - hz, pileCenter.z + hz));
    }

    /// <summary>The floor's height under the pile (raycast, else this object's).</summary>
    public float FloorY => SortingKit.FloorY(pileCenter, transform.position.y);

    /// <summary>Stink lines over the pile while anything lies on it (one anchor per floor).</summary>
    public void EnsureStink()
    {
        if (stink != null) return;
        float floorY = FloorY;
        stink = new GameObject("TippingFloorStink");
        stink.transform.position = new Vector3(pileCenter.x, floorY + 0.8f, pileCenter.z);
        stink.AddComponent<PileStink>().Init(pileCenter, new Vector3(pileSize.x + 1.6f, 3f, pileSize.y + 1.2f));
        Fx.Stink(stink.transform);
    }
    GameObject stink;

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.8f, 0.1f, 0.5f);
        Gizmos.DrawWireCube(pileCenter + Vector3.up * 0.3f, new Vector3(pileSize.x, 0.6f, pileSize.y));
    }
}
