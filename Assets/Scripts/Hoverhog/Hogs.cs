using System.Collections.Generic;
using UnityEngine;

/// <summary>One lot's Hoverhog as this machine knows it. The host's copy is the truth (HogNet replicates it); the pilot's
/// machine simulates pose and battery while seated and reports them.</summary>
public sealed class HogData
{
    public ulong pilot = Hogs.Nobody;
    public float battery = 1f;
    public Vector3 pos;
    public float yaw;
    /// <summary>False until the Hoverhog has been put on its pad once this session.</summary>
    public bool placed;
    public float loadKg;
    /// <summary>Bay doors open / the tractor beam is on / the jets are lit (shown on every machine).</summary>
    public bool dumping, beaming, boosting;
    /// <summary>The pilot is holding the beam key (the host decides whether the beam actually runs).</summary>
    public bool beamHeld;
    /// <summary>The owning business's upgrade levels as replicated (<see cref="Hogs.LevelsOf"/> prefers the local save).</summary>
    public ushort levels;
    /// <summary>Host only: the belly's load, newest last.</summary>
    public readonly List<BagContents> cargo = new List<BagContents>();
    /// <summary>Host only: how long the pilot's player has been missing (a disconnect frees the seat).</summary>
    public float pilotMissing;
    public int looseSerial;
}

/// <summary>
/// The Hoverhogs of Depot Row (Docs/Progression.md §6.10): per-lot state, who may fly, and the requests a pilot makes of
/// the host (seat, claw, beam, bomb bay, reactor). One per business that owns <see cref="HogShop.Hoverhog"/>; in the solo
/// scene flow it is the one shop's (<see cref="DepotFlowRules.LegacyShopLot"/>). Belly cargo is its own load, never the
/// truck's.
/// </summary>
public static class Hogs
{
    public const ulong Nobody = ulong.MaxValue;
    public const byte PilotBoosting = 1, PilotBeam = 2;

    static readonly HogData[] data = NewData();
    static HogData[] NewData()
    {
        var d = new HogData[DepotRowLayout.LotCount];
        for (int i = 0; i < d.Length; i++) d[i] = new HogData();
        return d;
    }

    public static bool ValidLot(int lot) => lot >= 0 && lot < data.Length;
    public static HogData Of(int lot) => ValidLot(lot) ? data[lot] : null;

    /// <summary>This machine decides (solo, or the session's host).</summary>
    public static bool IsHost => ShopModKit.IsHost;
    public static ulong LocalClient => Permissions.LocalClientId;

    /// <summary>Does the business on this lot own a Hoverhog? Replicated truth in the one world; the local save in solo.</summary>
    public static int Level(int lot)
    {
        if (!ValidLot(lot)) return 0;
        if (DepotRowWorld.Active) return BusinessUpgradeNet.LevelForLot(lot, HogShop.Hoverhog);
        return lot == DepotFlowRules.LegacyShopLot ? GameSession.GetUpgradeLevel(HogShop.Hoverhog) : 0;
    }

    public static string BusinessOf(int lot) => DepotRowWorld.Active ? DepotTruckCargo.BusinessOfLot(lot) : Businesses.LocalId;
    public static bool IsOwnBusiness(string businessId) => string.IsNullOrEmpty(businessId) || businessId == Businesses.LocalId;
    /// <summary>Is this lot's Hoverhog this machine's own business's?</summary>
    public static bool IsOwnLot(int lot) => DepotRowWorld.Active ? DepotRowWorld.LocalLot == lot : lot == DepotFlowRules.LegacyShopLot;

    /// <summary>The upgrade levels of this lot's Hoverhog: this machine's save for its own, the replicated set for others'.</summary>
    public static HogLevels LevelsOf(int lot)
    {
        if (!ValidLot(lot)) return default;
        return IsOwnLot(lot) ? HogLevels.Local : HogLevels.Unpack(data[lot].levels);
    }

    public static float BellyKg(int lot) => HogTuning.BellyKgAt(LevelsOf(lot).belly);
    public static float FlightSeconds(int lot) => HogTuning.FlightSecondsAt(LevelsOf(lot).battery);

    /// <summary>Whoever may work in the shop may fly its Hoverhog (the owner and on-shift employees).</summary>
    public static bool MayFly(ulong client, int lot) => !DepotRowWorld.Active || Permissions.CanWorkIn(client, lot);

    // ------------------------------------------------------------------ the roof frame and the pads

    public static Vector3 StreetLocal(int lot, Vector3 world) =>
        Quaternion.Inverse(DepotRowLayout.StreetFacing) * (world - DepotRowLayout.DoorPoint(lot));

    public static Pose PadPose(int lot) => DepotRowLayout.StreetPose(lot, HogTuning.PadLocal + Vector3.up * 0.04f);
    public static bool OverOwnRoof(int lot, Vector3 world) => HogRules.OverRoof(StreetLocal(lot, world));

    /// <summary>Landed on a pad this Hoverhog may charge on: its own roof, or a satellite pad its business has bought.</summary>
    public static bool OnChargingPad(int lot, Vector3 world)
    {
        if (HogRules.OnPad(StreetLocal(lot, world))) return true;
        int pads = LevelsOf(lot).pads;
        for (int i = 0; i < pads && i < HogTuning.SatPads.Length; i++)
        {
            Vector3 d = world - HogTuning.SatPadPosition(i);
            if (d.y > -1f && d.y < 2.5f && d.x * d.x + d.z * d.z <= HogTuning.PadRadius * HogTuning.PadRadius) return true;
        }
        return false;
    }

    /// <summary>Put a never-placed Hoverhog on its pad (and, for this machine's own business, restore its saved battery).</summary>
    public static void EnsurePlaced(int lot)
    {
        var d = Of(lot);
        if (d == null || d.placed) return;
        var pad = PadPose(lot);
        d.pos = pad.position;
        d.yaw = pad.rotation.eulerAngles.y;
        d.placed = true;
        if (IsHost && IsOwnBusiness(BusinessOf(lot)))
        {
            int saved = GameSession.GetCounter(HogTuning.BatteryCounter);
            if (saved > 0) d.battery = Mathf.Clamp01((saved - 1) / 100f);
        }
    }

    // ------------------------------------------------------------------ cargo (host)

    public static bool CanFit(int lot, float kg) { var d = Of(lot); return d != null && HogRules.Fits(d.loadKg, kg, BellyKg(lot)); }

    public static void AddCargo(int lot, BagContents bag)
    {
        var d = Of(lot);
        if (d == null || bag == null) return;
        d.cargo.Add(bag);
        Recount(d);
    }

    /// <summary>Host: a loose item joins the belly's one loose pile (like the truck's litter vac).</summary>
    public static void AddLoose(int lot, ItemInstance item)
    {
        var d = Of(lot);
        if (d == null || item == null) return;
        BagContents pile = null;
        for (int i = d.cargo.Count - 1; i >= 0; i--)
            if (d.cargo[i] != null && d.cargo[i].sackVariant == -1) { pile = d.cargo[i]; break; }
        if (pile == null)
        {
            pile = new BagContents { bagId = $"loose/hog{lot}-{++d.looseSerial}", foundAt = item.foundAt, bagMassKg = 0f, sackVariant = -1 };
            d.cargo.Add(pile);
        }
        pile.items.Add(item);
        Recount(d);
    }

    public static BagContents PeekCargo(int lot) { var d = Of(lot); return d != null && d.cargo.Count > 0 ? d.cargo[d.cargo.Count - 1] : null; }

    public static BagContents TakeCargo(int lot)
    {
        var d = Of(lot);
        if (d == null || d.cargo.Count == 0) return null;
        var bag = d.cargo[d.cargo.Count - 1];
        d.cargo.RemoveAt(d.cargo.Count - 1);
        Recount(d);
        return bag;
    }

    /// <summary>Host: remove and return the lightest thing in the belly (the reactor's next meal), or null.</summary>
    public static BagContents TakeLightest(int lot)
    {
        var d = Of(lot);
        if (d == null || d.cargo.Count == 0) return null;
        int best = 0;
        for (int i = 1; i < d.cargo.Count; i++)
            if (d.cargo[i] != null && (d.cargo[best] == null || d.cargo[i].TotalMassKg < d.cargo[best].TotalMassKg)) best = i;
        var bag = d.cargo[best];
        d.cargo.RemoveAt(best);
        Recount(d);
        return bag;
    }

    static void Recount(HogData d)
    {
        float kg = 0f;
        for (int i = 0; i < d.cargo.Count; i++) if (d.cargo[i] != null) kg += d.cargo[i].TotalMassKg;
        d.loadKg = kg;
    }

    // ------------------------------------------------------------------ requests (any machine) and their host side

    public static void Claim(int lot)
    {
        if (IsHost) HostClaim(lot, LocalClient);
        else if (HogNet.Instance != null) HogNet.Instance.SendClaim(lot);
    }

    public static void HostClaim(int lot, ulong client)
    {
        var d = Of(lot);
        if (d == null || !IsHost) return;
        if (d.pilot == client) return;
        if (Level(lot) <= 0 || !MayFly(client, lot)) { Notify(client, "Not your Hoverhog!", ToastStyle.Bad); return; }
        if (d.pilot != Nobody) { Notify(client, "Somebody is already flying it.", ToastStyle.Bad); return; }
        d.pilot = client;
        d.pilotMissing = 0f;
    }

    /// <summary>The pilot's pose, battery and held keys, twice a second while flying; <paramref name="park"/> = they got out.</summary>
    public static void Report(int lot, Vector3 pos, float yaw, float battery, byte flags, bool park)
    {
        if (IsHost) HostReport(lot, LocalClient, pos, yaw, battery, flags, park);
        else if (HogNet.Instance != null) HogNet.Instance.SendReport(lot, pos, yaw, battery, flags, park);
    }

    public static void HostReport(int lot, ulong client, Vector3 pos, float yaw, float battery, byte flags, bool park)
    {
        var d = Of(lot);
        if (d == null || !IsHost || d.pilot != client) return;
        if (float.IsNaN(pos.x + pos.y + pos.z + yaw + battery)) return;
        d.pos = pos;
        d.yaw = yaw;
        // A pilot can only ever report a battery that went down, or charged at pad speed (two reports a second).
        d.battery = Mathf.Clamp(battery, 0f, Mathf.Min(1f, d.battery + 2f / HogTuning.ChargeSeconds));
        d.boosting = !park && (flags & PilotBoosting) != 0 && HogTuning.HasBoost(LevelsOf(lot).rotors);
        d.beamHeld = !park && (flags & PilotBeam) != 0;
        if (park)
        {
            d.pilot = Nobody;
            d.dumping = false;
            d.beaming = false;
        }
    }

    public static void RequestClaw(int lot)
    {
        if (IsHost) HostClaw(lot, LocalClient);
        else if (HogNet.Instance != null) HogNet.Instance.SendClaw(lot);
    }

    public static void HostClaw(int lot, ulong client)
    {
        var hog = Hoverhog.ForLot(lot);
        if (IsHost && hog != null) hog.HostClaw(client);
    }

    /// <summary><paramref name="single"/>: Belly 3's one-bag tap.</summary>
    public static void RequestDump(int lot, bool single)
    {
        if (IsHost) HostDump(lot, LocalClient, single);
        else if (HogNet.Instance != null) HogNet.Instance.SendDump(lot, single);
    }

    public static void HostDump(int lot, ulong client, bool single)
    {
        var hog = Hoverhog.ForLot(lot);
        if (IsHost && hog != null) hog.HostDump(client, single);
    }

    /// <summary>The pilot is holding the reactor key: burn the lightest thing in the belly for charge.</summary>
    public static void RequestBurn(int lot)
    {
        if (IsHost) HostBurn(lot, LocalClient);
        else if (HogNet.Instance != null) HogNet.Instance.SendBurn(lot);
    }

    public static void HostBurn(int lot, ulong client)
    {
        var d = Of(lot);
        if (d == null || !IsHost || d.pilot != client || !HogTuning.HasReactor(LevelsOf(lot).battery)) return;
        if (d.battery >= 0.995f || d.cargo.Count == 0) return;
        var bag = TakeLightest(lot);
        if (bag == null) return;
        float kg = bag.TotalMassKg;
        float credit = HogRules.ReactorCredit(kg, FlightSeconds(lot));
        d.battery = Mathf.Min(1f, d.battery + credit);
        if (client != LocalClient && HogNet.Instance != null) HogNet.Instance.Credit(client, lot, credit);
        var hog = Hoverhog.ForLot(lot);
        Vector3 at = hog != null ? hog.transform.position + Vector3.up * 3f : d.pos;
        Sfx.Play("crunch", at, 0.6f, 0.7f);
        Fx.Burst(at, new Color(0.45f, 1f, 0.2f), 14, 3f);
        Notify(client, $"Reactor ate {kg:0.#} kg: +{kg * HogTuning.ReactorSecondsPerKg:0} s of flight.", ToastStyle.Info);
        NetAudit.Event("hog_burn", new Dictionary<string, object> { { "lot", lot }, { "bag", bag.bagId }, { "kg", kg } });
    }

    /// <summary>The pilot's machine: the host burned a bag for us.</summary>
    public static void ApplyCredit(int lot, float credit)
    {
        var d = Of(lot);
        if (d != null && credit > 0f && credit <= 1f) d.battery = Mathf.Min(1f, d.battery + credit);
    }

    /// <summary>Host: a business told us its Hoverhog's upgrade levels.</summary>
    public static void HostDeclare(int lot, ulong client, ushort packed)
    {
        var d = Of(lot);
        if (d == null || !IsHost || !HogLevels.Valid(packed)) return;
        if (!BusinessUpgradeNet.TryOwnerForLot(lot, out _, out ulong owner) || owner != client) return;
        d.levels = packed;
    }

    /// <summary>Host: every machine plays the claw's drop and lift.</summary>
    public static void ClawFx(int lot, Vector3 target, float seconds)
    {
        if (HogNet.Instance != null && HogNet.Instance.IsSpawned) HogNet.Instance.BroadcastClaw(lot, target, seconds);
        else Hoverhog.ForLot(lot)?.PresentClaw(target, seconds);
    }

    /// <summary>Host: a toast for one player.</summary>
    public static void Notify(ulong client, string message, ToastStyle style = ToastStyle.Info)
    {
        if (string.IsNullOrEmpty(message)) return;
        if (client == LocalClient || HogNet.Instance == null || !HogNet.Instance.IsSpawned) GameHUD.Toast(message, style, 2.5f);
        else HogNet.Instance.Notify(client, message, style);
    }

    // ------------------------------------------------------------------ host heartbeat

    static int savedPct = -1;
    static string tickedBusiness;

    /// <summary>Forget every Hoverhog (another save was loaded, or this machine joined somebody's session).</summary>
    public static void ResetAll()
    {
        for (int i = 0; i < data.Length; i++) data[i] = new HogData();
        savedPct = -1;
    }

    /// <summary>Host: parked Hoverhogs charge on their pads (whether or not the town is loaded), a vanished pilot loses
    /// the seat, and this machine's own battery is written to its save in 5% steps.</summary>
    public static void HostTick(float dt)
    {
        if (!IsHost) return;
        string business = GameSession.BusinessId;
        if (business != tickedBusiness)
        {
            // Another save: its Hoverhog starts on its own pad with its own battery, and an empty belly.
            tickedBusiness = business;
            if (Hoverhog.LocalSeat == null) ResetAll();
        }
        for (int lot = 0; lot < data.Length; lot++)
        {
            if (Level(lot) <= 0) continue;
            EnsurePlaced(lot);
            var d = data[lot];
            if (IsOwnLot(lot)) d.levels = HogLevels.Local.Pack();
            if (d.pilot == Nobody)
            {
                d.battery = HogRules.BatteryAfter(d.battery, dt, false, OnChargingPad(lot, d.pos), FlightSeconds(lot));
                d.dumping = d.beaming = d.boosting = d.beamHeld = false;
            }
            else if (PlayerTells.For(d.pilot) == null)
            {
                d.pilotMissing += dt;
                if (d.pilotMissing > 3f) { d.pilot = Nobody; d.pilotMissing = 0f; }
            }
            else d.pilotMissing = 0f;

            if (IsOwnBusiness(BusinessOf(lot)) && !GameSession.IsConnectedGuest)
            {
                int pct = Mathf.RoundToInt(d.battery * 20f) * 5;
                if (pct != savedPct)
                {
                    savedPct = pct;
                    GameSession.SetCounter(HogTuning.BatteryCounter, pct + 1);
                }
            }
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        ResetAll();
        tickedBusiness = null;
    }
}
