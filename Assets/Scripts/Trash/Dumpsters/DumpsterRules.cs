using UnityEngine;

/// <summary>
/// Dumpsters (Docs/Progression.md §6.11): every commercial stop in Coupon Commons has one beside its curb. Its bags
/// are ordinary curb records of that stop that sit in slots <see cref="FirstSlot"/> and up, so they are counted, aged,
/// paid, fined and replicated exactly like curb bags, but no bag object is ever put on the curb for them. The only way
/// to collect them is a rear-loader with the Dumpster Tipper (<see cref="UpgradeId"/>) backed up to the dumpster.
/// Pure rules; EditMode tests reach them by reflection (DumpsterRulesTests).
/// </summary>
public static class DumpsterRules
{
    public const string UpgradeId = "truck_dumpster_lift";
    public const int MaxLevel = 2;

    /// <summary>Curb slots are 0..5; the wire slot is a byte and the stop's slot mask covers 0..30.</summary>
    public const int FirstSlot = 8, Capacity = 6;
    /// <summary>Share of a dumpster stop's bags that go in the dumpster while both it and the curb have room.</summary>
    public const float PutOutShare = 0.6f;

    /// <summary>The dumpster's spot in its stop's frame (+Z towards the road, +X along the kerb): behind the pavement, to
    /// the right of the shop's entry path. Its open front faces the road, so a truck backs straight up to it.</summary>
    public static readonly Vector3 PadLocal = new Vector3(9f, 0f, -5.6f);

    // ---- the lift
    public const float Reach = 4.2f, HalfWidth = 1.9f, MaxYawOff = 42f, MaxSpeed = 1f, HoldSeconds = 0.6f;
    public const float LiftSeconds = 1.3f, LowerSeconds = 0.9f;
    public static float SecondsPerBag(int level) => level >= 2 ? 0.18f : 0.45f;

    public static bool HasDumpster(string districtId) => districtId == Districts.CouponCommons;
    public static bool IsDumpsterSlot(int slot) => slot >= FirstSlot && slot < FirstSlot + Capacity;

    /// <summary>How many of a stop's records are in its dumpster, from the stop's used-slot mask.</summary>
    public static int Count(int usedSlotMask)
    {
        int n = 0;
        for (int i = 0; i < Capacity; i++) if ((usedSlotMask & (1 << (FirstSlot + i))) != 0) n++;
        return n;
    }

    /// <summary>The lowest free dumpster slot, or -1 when it is full.</summary>
    public static int FreeSlot(int usedSlotMask)
    {
        for (int i = 0; i < Capacity; i++) if ((usedSlotMask & (1 << (FirstSlot + i))) == 0) return FirstSlot + i;
        return -1;
    }

    /// <summary>Does this new bag go in the dumpster rather than on the curb? Stable per bag id.</summary>
    public static bool GoesInDumpster(string bagId, bool curbHasRoom, bool dumpsterHasRoom)
    {
        if (!dumpsterHasRoom) return false;
        if (!curbHasRoom) return true;
        return ((uint)PickupVisuals.StableHash(bagId ?? "") % 1000u) < (uint)(PutOutShare * 1000f);
    }

    /// <summary>Is a dumpster, given in the truck's own metres (+Z forward) with its facing relative to the truck's, close
    /// enough behind the hopper and square enough to hook?</summary>
    public static bool InReach(Vector3 dumpsterInTruckMetres, float yawOffDegrees)
    {
        float behind = ShopModKit.BodyRearZ - dumpsterInTruckMetres.z;
        return behind >= -0.4f && behind <= Reach && Mathf.Abs(dumpsterInTruckMetres.x) <= HalfWidth &&
               Mathf.Abs(Mathf.DeltaAngle(0f, yawOffDegrees)) <= MaxYawOff;
    }

    public enum Verdict { Ok, NotDriver, NoLift, WrongTruck, Busy, Moving, NoDumpster, Empty, NoPermit, Full }

    public static Verdict Check(bool isDriver, int liftLevel, bool rearLoader, bool busy, float flatSpeed, bool inReach,
                                int bags, bool permit, bool fitsNext)
    {
        if (!isDriver) return Verdict.NotDriver;
        if (!rearLoader) return Verdict.WrongTruck;
        if (liftLevel <= 0) return Verdict.NoLift;
        if (busy) return Verdict.Busy;
        if (!inReach) return Verdict.NoDumpster;
        if (flatSpeed > MaxSpeed) return Verdict.Moving;
        if (bags <= 0) return Verdict.Empty;
        if (!permit) return Verdict.NoPermit;
        if (!fitsNext) return Verdict.Full;
        return Verdict.Ok;
    }

    public static string Message(Verdict v)
    {
        switch (v)
        {
            case Verdict.WrongTruck: return "Dumpsters need the rear-loader. The Curb Pup would fold in half.";
            case Verdict.NoLift: return $"That needs the Dumpster Dunker ({Scrapazon.Name} > Truck).";
            case Verdict.Busy: return "Already tipping.";
            case Verdict.Moving: return "Stop the truck to tip the dumpster.";
            case Verdict.NoDumpster: return "Back the hopper right up to the dumpster.";
            case Verdict.Empty: return "That dumpster's empty.";
            case Verdict.NoPermit: return "No permit for this district.";
            case Verdict.Full: return "Truck's full! Go tip it at the depot.";
            default: return null;
        }
    }
}
