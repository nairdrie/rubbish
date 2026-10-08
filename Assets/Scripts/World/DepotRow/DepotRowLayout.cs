using UnityEngine;

/// <summary>
/// Where Depot Row lives (Docs/Multiplayer.md §13.2, package N3). Pure data shared by the runtime and the two builders
/// (<c>NeighborhoodBuilder</c> lays the lot pads along Depot Row; <c>Setup_DepotRow</c> builds the shop
/// facades on them and the four pocket interiors in <c>DepotRow.unity</c>).
/// <para>Street: the four life-sized shops face east along the north–south Depot Row road. Only the master-plan
/// anchors use the city scale; door-relative distances, vehicles and buildings remain life-sized. Lot indices remain
/// stable business identities, independent of the street's position and orientation.</para>
/// <para>Pocket interiors: lot k's copy of the ShopScene garage is offset by <see cref="InteriorOrigin"/>(k), 20 m under
/// the far-ground slab and ~400 m south of the town (nobody sees them from the street; all kill planes are below −30).</para>
/// </summary>
public static class DepotRowLayout
{
    public const int LotCount = 4;
    public const string SceneName = "DepotRow";
    public const string ScenePath = "Assets/Scenes/DepotRow.unity";

    /// <summary>The asphalt pad of lot k in world XZ (x0, z0, x1, z1).</summary>
    public static Rect PadRect(int lot) => CoastalCityLayout.DepotPad(Mathf.Clamp(lot, 0, LotCount - 1));

    /// <summary>Centre of the garage door at ground level (street side).</summary>
    public static Vector3 DoorPoint(int lot) => CoastalCityLayout.DepotDoor(Mathf.Clamp(lot, 0, LotCount - 1));

    /// <summary>Road centre opposite a shop, for town events and street fixtures.</summary>
    public static Vector3 RoadPoint(int lot) => CoastalCityLayout.DepotRoadPosition(Mathf.Clamp(lot, 0, LotCount - 1));

    /// <summary>Door-local extents of the canonical pad, used to keep notices clear of its end/front edges.</summary>
    public static Vector2 PadLocalEdge(int lot)
    {
        var rect = PadRect(lot);
        var facing = StreetFacing;
        var right = facing * Vector3.right;
        var forward = facing * Vector3.forward;
        var center = new Vector3(rect.center.x, DoorPoint(lot).y, rect.center.y) - DoorPoint(lot);
        float halfSide = Mathf.Abs(right.x) * rect.width * .5f + Mathf.Abs(right.z) * rect.height * .5f;
        float halfFront = Mathf.Abs(forward.x) * rect.width * .5f + Mathf.Abs(forward.z) * rect.height * .5f;
        return new Vector2(Vector3.Dot(center, right) - halfSide, Vector3.Dot(center, forward) + halfFront);
    }

    /// <summary>Life-sized offset from a door: local +Z points toward the road, +X across the shop front.</summary>
    public static Vector3 StreetPoint(int lot, Vector3 localOffset) => DoorPoint(lot) + StreetFacing * localOffset;

    public static Pose StreetPose(int lot, Vector3 localOffset, float localYaw = 0f) =>
        new Pose(StreetPoint(lot, localOffset), StreetFacing * Quaternion.Euler(0f, localYaw, 0f));

    /// <summary>Faces the road (east, +X).</summary>
    public static Quaternion StreetFacing => CoastalCityLayout.DepotFacing;

    /// <summary>Where you stand after walking out of the shop.</summary>
    public static Pose StreetOnFoot(int lot) => StreetPose(lot, new Vector3(5.8f, 0.05f, 2.2f));

    /// <summary>Where the truck stands after driving out (nose toward the road).</summary>
    public static Pose StreetTruck(int lot) => StreetPose(lot, new Vector3(0f, 0.15f, 5.5f));

    /// <summary>A fresh truck rolls forward from the door, using the same frame as return/tow spawns.</summary>
    public static Pose StreetTruckRollout(int lot) => StreetPose(lot, new Vector3(0f, 0.35f, 3f));

    /// <summary>Where a player spawns / joins: on the apron, a little to the side of the door, looking at the shop.</summary>
    public static Pose StreetSpawn(int lot, int nth = 0) =>
        StreetPose(lot, new Vector3(-6f + 1.2f * nth, 0.05f, 9f), 160f);

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
