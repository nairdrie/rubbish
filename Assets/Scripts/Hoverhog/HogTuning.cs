using UnityEngine;

/// <summary>A business's Hoverhog upgrade levels (Docs/Progression.md §6.10 stage 2), ten bits on the wire.</summary>
public struct HogLevels : System.IEquatable<HogLevels>
{
    public int belly, claw, rotors, battery, pads;
    public const int Max = 3;

    public ushort Pack() => (ushort)(C(belly) | C(claw) << 2 | C(rotors) << 4 | C(battery) << 6 | C(pads) << 8);
    static int C(int level) => Mathf.Clamp(level, 0, Max);

    public static HogLevels Unpack(ushort p) => new HogLevels
    {
        belly = p & 3, claw = (p >> 2) & 3, rotors = (p >> 4) & 3, battery = (p >> 6) & 3, pads = (p >> 8) & 3,
    };

    /// <summary>Every field is two bits, so any ten-bit value is a legal set of levels.</summary>
    public static bool Valid(ushort p) => (p >> 10) == 0;

    /// <summary>This machine's own business.</summary>
    public static HogLevels Local => new HogLevels
    {
        belly = C(GameSession.GetUpgradeLevel(HogShop.Belly)), claw = C(GameSession.GetUpgradeLevel(HogShop.Claw)),
        rotors = C(GameSession.GetUpgradeLevel(HogShop.Rotors)), battery = C(GameSession.GetUpgradeLevel(HogShop.Battery)),
        pads = C(GameSession.GetUpgradeLevel(HogShop.SatPad)),
    };

    public bool Equals(HogLevels o) => Pack() == o.Pack();
    public override bool Equals(object obj) => obj is HogLevels o && Equals(o);
    public override int GetHashCode() => Pack();
}

/// <summary>Every number of the Hoverhog (Docs/Progression.md §6.10). Level tables are absolute values, never multipliers.</summary>
public static class HogTuning
{
    // ---- belly, battery
    public const float BellyKg = 300f;
    static readonly float[] bellyKg = { 300f, 450f, 600f, 850f };
    public static float BellyKgAt(int level) => bellyKg[Mathf.Clamp(level, 0, 3)];
    /// <summary>Seconds in the air on a full battery.</summary>
    public const float FlightSeconds = 300f;
    static readonly float[] flightSeconds = { 300f, 480f, 720f, 720f };
    public static float FlightSecondsAt(int level) => flightSeconds[Mathf.Clamp(level, 0, 3)];
    /// <summary>Battery 3, the Garbage-Juice Reactor: seconds of flight per kilogram of belly load burned, one bag per pulse.</summary>
    public const float ReactorSecondsPerKg = 3f, ReactorInterval = 0.5f;
    public static bool HasReactor(int batteryLevel) => batteryLevel >= 3;
    /// <summary>Seconds from empty to full on the owner's helipad.</summary>
    public const float ChargeSeconds = 40f;
    /// <summary>Landed anywhere else, an almost-empty battery sulks back up to <see cref="SulkCharge"/> in this long.</summary>
    public const float SulkSeconds = 180f, SulkCharge = 0.2f;
    public const float TowFee = 250f, TowHoldSeconds = 1.5f, TowBelow = 0.03f;

    // ---- flying
    public const float Speed = 12f, ReverseSpeed = 6f, Climb = 5f, Accel = 9f, Brake = 16f, TurnRate = 100f;
    static readonly float[] speed = { 12f, 15f, 18f, 22f };
    public static float SpeedAt(int rotors) => speed[Mathf.Clamp(rotors, 0, 3)];
    /// <summary>Rotors 3, the jet pods: a boost on Shift.</summary>
    public const float BoostSpeed = 6f, BoostSeconds = 3f, BoostCooldown = 15f;
    public static bool HasBoost(int rotors) => rotors >= 3;
    /// <summary>Metres above sea level (the town is flat at y ≈ 0).</summary>
    public const float Ceiling = 40f;
    public static float CeilingAt(int rotors) => rotors >= 3 ? 60f : Ceiling;
    public const float DeadSink = 3f;
    public const float BodyRadius = 2.2f, BodyCentreY = 2.3f, Skin = 0.05f;

    // ---- the Claw
    public const float ClawMaxHeight = 10f, ClawRadius = 3f, ClawSeconds = 4f, ClawSlip = 0.10f, ClawDrift = 4f;
    /// <summary>Fractions of a claw cycle: drop, grip, lift (the rest is stowing).</summary>
    public const float ClawDrop = 0.35f, ClawGrip = 0.1f, ClawLift = 0.4f;
    /// <summary>Claw 1, the Three-Finger Discount Claw: three bags a drop, faster, no slips.</summary>
    public static int ClawBagsAt(int claw) => claw >= 1 ? 3 : 1;
    public static float ClawSecondsAt(int claw) => claw >= 1 ? 2.5f : ClawSeconds;
    public static float ClawSlipAt(int claw) => claw >= 1 ? 0f : ClawSlip;

    // ---- the beam (Claw 2 and 3 replace the claw with it: hold X)
    public static bool HasBeam(int claw) => claw >= 2;
    public static float BeamRadiusAt(int claw) => claw >= 3 ? 11f : 6f;
    public static float BeamIntervalAt(int claw) => claw >= 3 ? 0.25f : 0.6f;
    public static float BeamMaxSpeedAt(int claw) => claw >= 3 ? 6f : 2f;
    public const float BeamMaxHeight = 12f, BeamLiftSeconds = 0.9f;
    /// <summary>Claw 3 leaves a crop circle on grass every this many metres (cosmetic, each machine draws its own).</summary>
    public const float CropCircleEvery = 9f, CropCircleSeconds = 240f, CropCircleOffRoad = 8f;

    // ---- the bomb bay
    public const float DumpHoldSeconds = 0.6f, DumpInterval = 0.3f, WaterlineY = -0.9f;
    /// <summary>Belly 3: the whole load is out in this long, and a tap of G drops a single bag.</summary>
    public const float FastDumpSeconds = 3f;
    public static bool HasPrecisionDrop(int belly) => belly >= 3;

    // ---- the roof, in the lot's street frame (DepotRowLayout.StreetPose: +Z to the road, +X across the shop front)
    /// <summary>Top of the helipad's roof deck. The facade's own roof is stepped (the bay shell at 7.83, the side blocks at
    /// 7.63), so the helipad lays one flat deck over it.</summary>
    public const float RoofY = 7.86f, RoofHalfX = 9.65f, RoofBackZ = -8.2f, RoofFrontZ = 0f;
    public static readonly Vector3 DeckCentre = new Vector3(0f, -0.13f, -4.15f), DeckSize = new Vector3(18.7f, 0.26f, 7.7f);
    /// <summary>How far above the roof a hovering Hoverhog still unloads down the hatch.</summary>
    public const float UnloadAbove = 14f;
    public static readonly Vector3 PadLocal = new Vector3(-4.6f, RoofY, -4.5f);
    public const float PadRadius = 3.1f;
    public static readonly Vector3 HatchLocal = new Vector3(1.7f, RoofY, -5.4f);
    public static readonly Vector3 StairHutLocal = new Vector3(7.3f, RoofY, -5.2f);
    /// <summary>Where the stairs put you on the roof (facing the pad).</summary>
    public static readonly Vector3 RoofArrivalLocal = new Vector3(5.3f, RoofY + 0.06f, -5.2f);
    public const float RoofArrivalYaw = -90f;

    // ---- the stairwell's shop end, in ShopScene coordinates (the free nook between the south-east pillar and the pile)
    public static readonly Vector3 ShopPileCentre = new Vector3(5.75f, 0f, -3.2f);
    public static readonly Vector3 ShopStairLocal = new Vector3(4.17f, 0f, -4.62f);
    public static readonly Vector3 ShopArrivalLocal = new Vector3(4.17f, 0.06f, -3.55f);

    public const string BatteryCounter = "hog_battery_pct1";

    // ---- satellite pads: one per richer district, bought in this order (hog_sat_pad level = how many you own)
    public struct SatPad
    {
        public string districtId, label;
        public Vector3 pos;
        public float yaw;
    }
    /// <summary>World poses measured on the published town (2026-10-08). A pad with y above 0.5 stands at that height
    /// (the Meadows one perches on the park pavilion's flat roof, whose cornice slab tops out at y 7.55); the others are snapped to the ground.</summary>
    public static readonly SatPad[] SatPads =
    {
        new SatPad { districtId = "minivan_meadows", label = "MEADOWS", pos = new Vector3(644.5f, 7.57f, 217.5f), yaw = 20f },
        new SatPad { districtId = "snootington_hills", label = "SNOOTINGTON", pos = new Vector3(202.3f, 0f, 714.8f), yaw = 0f },
        new SatPad { districtId = "billionaire_bluffs", label = "THE BLUFFS", pos = new Vector3(620.8f, 0f, 732.8f), yaw = 0f },
    };
    /// <summary>Where a satellite pad stands in the world: the poses above are flat-town coordinates, the land under
    /// them has relief (the bake levels a patch for each pad that stands on the ground).</summary>
    public static Vector3 SatPadPosition(int index) => TownRelief.Lift(SatPads[index].pos);
}

/// <summary>The Hoverhog's pure rules (EditMode tests reach them by reflection: HogRulesTests).</summary>
public static class HogRules
{
    public enum Claw { Ok, NotPilot, Busy, TooHigh, NoBag, Full, NoPermit }
    public enum Dump { Ok, NotPilot, Empty, NoGround, Water, DepotFull }

    /// <summary>Battery (0..1) after <paramref name="dt"/> seconds: drains in the air, charges landed on the owner's pad,
    /// and a nearly flat one landed anywhere else creeps back up to <see cref="HogTuning.SulkCharge"/>.</summary>
    public static float BatteryAfter(float battery, float dt, bool airborne, bool onOwnPad, float flightSeconds = HogTuning.FlightSeconds)
    {
        battery = Mathf.Clamp01(battery);
        if (dt <= 0f) return battery;
        if (airborne) return Mathf.Max(0f, battery - dt / Mathf.Max(1f, flightSeconds));
        if (onOwnPad) return Mathf.Min(1f, battery + dt / HogTuning.ChargeSeconds);
        if (battery < HogTuning.SulkCharge)
            return Mathf.Min(HogTuning.SulkCharge, battery + dt * HogTuning.SulkCharge / HogTuning.SulkSeconds);
        return battery;
    }

    public static bool Fits(float loadKg, float bagKg, float capacityKg) => loadKg + bagKg <= capacityKg + 0.001f;

    /// <summary>Is a point (in the lot's street frame) over the depot's roof, low enough to unload down the hatch?</summary>
    public static bool OverRoof(Vector3 streetLocal)
    {
        return Mathf.Abs(streetLocal.x) <= HogTuning.RoofHalfX &&
               streetLocal.z >= HogTuning.RoofBackZ && streetLocal.z <= HogTuning.RoofFrontZ &&
               streetLocal.y >= HogTuning.RoofY - 0.5f && streetLocal.y <= HogTuning.RoofY + HogTuning.UnloadAbove;
    }

    /// <summary>Landed on the pad itself (charging)?</summary>
    public static bool OnPad(Vector3 streetLocal)
    {
        Vector3 d = streetLocal - HogTuning.PadLocal;
        return Mathf.Abs(d.y) < 1.2f && d.x * d.x + d.z * d.z <= HogTuning.PadRadius * HogTuning.PadRadius;
    }

    public static Claw CheckClaw(bool isPilot, bool busy, float heightAboveGround, bool anyBag, bool anyFits, bool anyPermitted)
    {
        if (!isPilot) return Claw.NotPilot;
        if (busy) return Claw.Busy;
        if (heightAboveGround > HogTuning.ClawMaxHeight) return Claw.TooHigh;
        if (!anyBag) return Claw.NoBag;
        if (!anyPermitted) return Claw.NoPermit;
        if (!anyFits) return Claw.Full;
        return Claw.Ok;
    }

    public static string Message(Claw v)
    {
        switch (v)
        {
            case Claw.Busy: return "The Claw is busy.";
            case Claw.TooHigh: return $"Too high for the Claw! Get under {HogTuning.ClawMaxHeight:0} m.";
            case Claw.NoBag: return "Nothing to grab down there. Hover right over the bags.";
            case Claw.Full: return "Belly's full! Go drop it on your roof.";
            case Claw.NoPermit: return "No permit for this district. The Claw has principles.";
            default: return null;
        }
    }

    public static Dump CheckDump(bool isPilot, bool hasCargo, bool overOwnRoof, bool groundBelow, float groundY, bool depotRoom)
    {
        if (!isPilot) return Dump.NotPilot;
        if (!hasCargo) return Dump.Empty;
        if (overOwnRoof) return depotRoom ? Dump.Ok : Dump.DepotFull;
        if (!groundBelow) return Dump.NoGround;
        if (groundY < HogTuning.WaterlineY) return Dump.Water;
        return Dump.Ok;
    }

    public static string Message(Dump v)
    {
        switch (v)
        {
            case Dump.Empty: return "Belly's empty.";
            case Dump.NoGround: return "Nothing down there to drop it on!";
            case Dump.Water: return "Not over the water!";
            case Dump.DepotFull: return DepotBacklog.FullMessage.Replace("truck", "belly");
            default: return null;
        }
    }

    /// <summary>How far down its cable the claw is (0 stowed, 1 at the bag) at <paramref name="t01"/> of a cycle.</summary>
    public static float ClawReach01(float t01)
    {
        float a = HogTuning.ClawDrop, b = a + HogTuning.ClawGrip, c = b + HogTuning.ClawLift;
        if (t01 <= 0f || t01 >= c) return 0f;
        if (t01 < a) return Mathf.SmoothStep(0f, 1f, t01 / a);
        if (t01 < b) return 1f;
        return 1f - Mathf.SmoothStep(0f, 1f, (t01 - b) / (c - b));
    }

    public enum Beam { Ok, NotPilot, NoBeam, TooHigh, TooFast }

    /// <summary>May the tractor beam run right now? It needs a low, slow hover (Claw 3 lets it crawl).</summary>
    public static Beam CheckBeam(bool isPilot, int clawLevel, float heightAboveGround, float flatSpeed)
    {
        if (!isPilot) return Beam.NotPilot;
        if (!HogTuning.HasBeam(clawLevel)) return Beam.NoBeam;
        if (heightAboveGround > HogTuning.BeamMaxHeight) return Beam.TooHigh;
        if (flatSpeed > HogTuning.BeamMaxSpeedAt(clawLevel) + 0.25f) return Beam.TooFast;
        return Beam.Ok;
    }

    public static string Message(Beam v)
    {
        switch (v)
        {
            case Beam.TooHigh: return $"Too high for the beam! Get under {HogTuning.BeamMaxHeight:0} m.";
            case Beam.TooFast: return "Too fast for the beam! Slow right down.";
            default: return null;
        }
    }

    /// <summary>Battery (0..1) gained by burning <paramref name="kg"/> of belly load in the reactor.</summary>
    public static float ReactorCredit(float kg, float flightSeconds) =>
        Mathf.Max(0f, kg) * HogTuning.ReactorSecondsPerKg / Mathf.Max(1f, flightSeconds);

    /// <summary>Seconds between bags out of the bomb bay: Belly 3 empties any load in three seconds.</summary>
    public static float DumpInterval(int bellyLevel, int bags) =>
        HogTuning.HasPrecisionDrop(bellyLevel) && bags > 0 ? Mathf.Min(HogTuning.DumpInterval, HogTuning.FastDumpSeconds / bags) : HogTuning.DumpInterval;

    public static byte PackBattery(float battery01) => (byte)Mathf.RoundToInt(Mathf.Clamp01(battery01) * 200f);
    public static float UnpackBattery(byte b) => Mathf.Clamp01(b / 200f);
}
