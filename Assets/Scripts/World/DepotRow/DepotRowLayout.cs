using UnityEngine;

/// <summary>
/// Where Depot Row lives (Docs/Multiplayer.md §13.2, package N3). Pure data shared by the runtime and the two builders
/// (<c>NeighborhoodBuilder</c> lays the lot pads along Minivan Mile's south side; <c>Setup_DepotRow</c> builds the shop
/// facades on them and the four pocket interiors in <c>DepotRow.unity</c>).
/// <para>Street: Minivan Mile (centre z = −6) is Depot Row. Lot k's pad is x = <see cref="LotCenterX"/>(k) ± 15,
/// z −36 .. −11 (asphalt, cuts the south sidewalk); the shop front is the plane z = <see cref="FacadeZ"/>, facing +Z
/// (the road). Lot 1 (k = 0) is the host's and sits nearest the old depot pad.</para>
/// <para>Pocket interiors: lot k's copy of the ShopScene garage is offset by <see cref="InteriorOrigin"/>(k), 20 m under
/// the far-ground slab and ~400 m south of the town (nobody sees them from the street; all kill planes are below −30).</para>
/// </summary>
public static class DepotRowLayout
{
    public const int LotCount = 4;
    public const string SceneName = "DepotRow";
    public const string ScenePath = "Assets/Scenes/DepotRow.unity";

    /// <summary>Minivan Mile's centre line (the Depot Row road).</summary>
    public const float RoadZ = -6f;
    /// <summary>Shop fronts (garage door plane), facing +Z.</summary>
    public const float FacadeZ = -22f;
    public const float LotSpacing = 36f, FirstLotX = 46f;
    public const float PadHalfWidth = 15f, PadZ0 = -36f, PadZ1 = -11f;

    public static float LotCenterX(int lot) => FirstLotX + LotSpacing * Mathf.Clamp(lot, 0, LotCount - 1);

    /// <summary>The asphalt pad of lot k in world XZ (x0, z0, x1, z1).</summary>
    public static Rect PadRect(int lot)
    {
        float cx = LotCenterX(lot);
        return Rect.MinMaxRect(cx - PadHalfWidth, PadZ0, cx + PadHalfWidth, PadZ1);
    }

    /// <summary>Centre of the garage door at ground level (street side).</summary>
    public static Vector3 DoorPoint(int lot) => new Vector3(LotCenterX(lot), 0f, FacadeZ);

    /// <summary>Faces the road (+Z).</summary>
    public static Quaternion StreetFacing => Quaternion.identity;

    /// <summary>Where you stand after walking out of the shop.</summary>
    public static Pose StreetOnFoot(int lot) => new Pose(new Vector3(LotCenterX(lot) + 5.8f, 0.05f, FacadeZ + 2.2f), StreetFacing);

    /// <summary>Where the truck stands after driving out (nose toward the road).</summary>
    public static Pose StreetTruck(int lot) => new Pose(new Vector3(LotCenterX(lot), 0.15f, FacadeZ + 5.5f), StreetFacing);

    /// <summary>Where a player spawns / joins: on the apron, a little to the side of the door, looking at the shop.</summary>
    public static Pose StreetSpawn(int lot, int nth = 0) =>
        new Pose(new Vector3(LotCenterX(lot) - 6f + 1.2f * nth, 0.05f, FacadeZ + 9f), Quaternion.Euler(0f, 160f, 0f));

    // ------------------------------------------------------------------ pocket interiors

    /// <summary>World offset of lot k's interior relative to ShopScene's coordinates.</summary>
    public static Vector3 InteriorOrigin(int lot) => new Vector3(100f + 150f * Mathf.Clamp(lot, 0, LotCount - 1), -20f, -400f);

    /// <summary>A box around lot k's interior (ShopScene's garage is about x −8..9, z −6..8, y 0..9 around its origin).</summary>
    public static Bounds InteriorBounds(int lot) => new Bounds(InteriorOrigin(lot) + new Vector3(0.5f, 4f, 1f), new Vector3(30f, 16f, 30f));

    /// <summary>The lot whose interior box contains <paramref name="p"/>, or −1.</summary>
    public static int InteriorAt(Vector3 p)
    {
        if (p.y > -5f || p.z > -300f) return -1; // fast out: the street and the town
        for (int k = 0; k < LotCount; k++)
            if (InteriorBounds(k).Contains(p)) return k;
        return -1;
    }

    /// <summary>The lot whose street pad contains <paramref name="p"/> (XZ), or −1.</summary>
    public static int PadAt(Vector3 p)
    {
        for (int k = 0; k < LotCount; k++)
            if (PadRect(k).Contains(new Vector2(p.x, p.z))) return k;
        return -1;
    }
}
