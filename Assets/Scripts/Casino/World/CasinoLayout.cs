using UnityEngine;

/// <summary>
/// Where The Lucky Raccoon lives (Docs/Casino.md §2). Pure data shared by the runtime (<see cref="CasinoWorld"/>,
/// <see cref="CasinoFront"/>) and the scene builder (<c>Setup_Casino</c>).
/// <para><b>Pocket interior:</b> <c>Casino.unity</c> holds one room, 22 x 12 m, 3.6 m high, built around
/// <see cref="Origin"/>: 20 m under the town like the Depot Row interiors (everything keyed off "y below -5 is a pocket"
/// keeps working: weather, the Hoverhog, the relief), 240 m south of them and 560 m south of the town's edge, so nothing
/// can see or reach it. Room-local axes: +X east, +Z north, the entrance is in the south wall.</para>
/// <para><b>Street front:</b> the centre bay of the decorative storefront parcel <see cref="FrontSiteId"/> on the north
/// side of Coupon Street (Coupon Commons). The front is dressed at runtime; nothing in the baked town changes.</para>
/// </summary>
public static class CasinoLayout
{
    public const string SceneName = "Casino";
    public const string ScenePath = "Assets/Scenes/Casino.unity";
    public const string RootName = "CasinoRoot";

    /// <summary>Resources path of the street front prefab baked by <c>Setup_Casino</c>.</summary>
    public const string FrontResource = "Casino/CasinoFront";
    /// <summary>The decorative storefront parcel (CoastalCity.json infillSites) whose centre bay is the casino.</summary>
    public const string FrontSiteId = "coupon-opposite-east";
    /// <summary>The shop face is this far into the parcel (NeighborhoodBuilder.InfillStorefront's "front").</summary>
    public const float FrontFaceDepth = 3f;
    /// <summary>Used when the parcel can't be found: the centre bay's door on the shop face, world XZ, facing south.</summary>
    public static readonly Vector3 FrontFallback = new Vector3(271.8f, 0f, 482.3f);

    /// <summary>World position of the room's floor centre.</summary>
    public static readonly Vector3 Origin = new Vector3(325f, -20f, -640f);
    public const float HalfX = 11f, HalfZ = 6f, Height = 3.6f;

    /// <summary>Room-local spot inside the entrance, facing into the room (+Z). Three steps in from the south wall: the
    /// third-person camera rides 5 m behind the player, and any closer to the wall it arrives pressed into their back.</summary>
    public static readonly Vector3 EntranceLocal = new Vector3(-5f, 0.05f, -3.1f);
    /// <summary>Room-local centre of the exit door on the south wall.</summary>
    public static readonly Vector3 ExitDoorLocal = new Vector3(-5f, 0f, -HalfZ);

    public static Pose EntrancePose => new Pose(Origin + EntranceLocal, Quaternion.identity);

    /// <summary>A box around the room with a little slack (walls, a step below the floor).</summary>
    public static Bounds InteriorBounds =>
        new Bounds(Origin + new Vector3(0f, 1.5f, 0f), new Vector3(HalfX * 2f + 4f, 9f, HalfZ * 2f + 4f));

    /// <summary>Is this world point inside the casino pocket?</summary>
    public static bool Contains(Vector3 p)
    {
        if (p.y > -5f) return false; // fast out: the town
        return InteriorBounds.Contains(p);
    }

    /// <summary>Below the room's footprint: someone fell through the floor.</summary>
    public static bool Under(Vector3 p)
    {
        Vector3 d = p - Origin;
        return Mathf.Abs(d.x) < HalfX + 6f && Mathf.Abs(d.z) < HalfZ + 6f && d.y < -2f && d.y > -80f;
    }

    /// <summary>Slot machine display names by index (Docs/Casino.md §4.3). Hard-coded here so the world side does not
    /// depend on the rules files.</summary>
    public static readonly string[] SlotNames = { "Lucky Sevens", "Trash Panda Bandit", "Golden Gnome", "Dumpster Fire", "Mystery Bag" };
    public const int SlotCount = 5;

    public static string SlotName(int index) => index >= 0 && index < SlotNames.Length ? SlotNames[index] : "Slot Machine";

    /// <summary>
    /// Reel drums (<see cref="CasinoStation.reels"/>): a cylinder whose axis is the reel's local X, with
    /// <see cref="ReelFaces"/> equal faces around it. Face k is centred in the window when the reel's local rotation is
    /// <c>Quaternion.Euler(k * 360 / ReelFaces, 0, 0)</c>; positive X rotation rolls the symbols DOWN past the window.
    /// The drum texture is one column of <see cref="ReelFaces"/> cells, face 0 at the top (v = 1) of the texture.
    /// </summary>
    public const int ReelFaces = 12;

    // ------------------------------------------------------------------ seat mode (Docs/Casino.md §9 and §9.1)

    /// <summary>
    /// Physics layer of every pick collider in the room (bet circles, chip bins, buttons, the lever, the roulette layout
    /// plane, drink slots). It is the stock "Ignore Raycast" layer (2): ordinary raycasts (interaction scan, camera
    /// collision, AI sight) skip it by default, and the colliders are triggers, so they never block a body. Raycast with
    /// <see cref="PickMask"/> and <c>QueryTriggerInteraction.Collide</c> (what <see cref="CasinoCursor"/> does).
    /// </summary>
    public const int PickLayer = 2;
    public const int PickMask = 1 << PickLayer;

    /// <summary>Cartoon scale of the table props: cards and chips are 1.6x real so they read from a first-person eye.</summary>
    public const float PropScale = 1.6f;
    /// <summary>A card on the felt, metres (63 x 88 mm at 1.6x). Face up, width along local X, height along local Z.</summary>
    public const float CardWidth = 0.100f, CardHeight = 0.140f, CardThickness = 0.002f;
    /// <summary>A chip, metres (39 mm at 1.6x). A stack grows along local Y.</summary>
    public const float ChipDiameter = 0.064f, ChipThickness = 0.0065f;

    /// <summary>First-person eye height above the standing floor (the player root is scaled 0.8).</summary>
    public const float EyeHeight = 1.3f;

    public const int BlackjackSeats = 5, RouletteSpots = 4, BarSpots = 3, DealerCardSpots = 6, HandsPerSeat = 3, CardsPerHand = 5, DrinkSlots = 4;
    public const int ResultHistory = 12;
    /// <summary>Chip denominations in cents, in the order of <c>CasinoSpot.chipRack</c>.</summary>
    public static readonly int[] BlackjackChipCents = { 500, 1000, 2500, 5000, 10000 };
    public static readonly int[] RouletteChipCents = { 100, 500, 2500, 10000 };
    /// <summary>Button ids, in the order of <c>CasinoSpot.buttons</c>.</summary>
    public static readonly string[] BlackjackButtonIds = { "Hit", "Stand", "Double", "Split", "Insurance" };
    public static readonly string[] RouletteButtonIds = { "Ready", "Undo", "Clear", "Rebet" };
    public static readonly string[] SlotButtonIds = { "Spin", "BetUp", "BetDown" };
}
