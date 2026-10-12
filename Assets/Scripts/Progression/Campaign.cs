using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Every number of the first campaign slice in one place (Docs/Campaign.md §14). Slice 1 covers two contracts: clean the
/// Acres to open the Meadows, clean the Meadows to open Coupon Commons. A district under contract STARTS MESSY (a heap of old
/// bags on its curbs) and its permit is no longer for sale: the city hands it over when the contract is won.
/// </summary>
public static class CampaignTuning
{
    /// <summary>How clean the district must read (the HUD's rounded percent counts, so 89.5% is a 90).</summary>
    public const float Threshold = 0.90f;
    /// <summary>The same line as a whole percent (what the bar's goal marker says).</summary>
    public const int ThresholdPct = 90;
    /// <summary>How long it must stay there without a dip, in GAME hours (a game day is 20 real minutes, an hour 50 s).</summary>
    public const double HoldHours = 24.0;
    /// <summary>Old bags each contract district starts with (a Curb Pup holds about 17 of them).</summary>
    public const int AcresBacklog = 56, MeadowsBacklog = 48;
    /// <summary>The Meadows' houses put trash out this much more often while its contract is running ("the second town is
    /// harder"). It still scales with the people online like every district: CurbTrashSpawner.RateScale / CapacityScale.</summary>
    public const float MeadowsRateMult = 1.5f;
    /// <summary>Old bags are this many hours past their district's neglect window (min..max), so every heap stinks.</summary>
    public const float BacklogMinExtraAgeHours = 2f, BacklogMaxExtraAgeHours = 20f;
    /// <summary>Roughly this many old bags per heap (a house's curb holds 3 to 6).</summary>
    public const float BagsPerHeap = 2.5f;
    /// <summary>The host looks at the contract this often (real seconds).</summary>
    public const float TickSeconds = 0.5f;
    /// <summary>One tick never counts more than this many game hours (a hitch must not skip the hold ahead).</summary>
    public const double MaxStepHours = 0.5;
}

/// <summary>One contract: clean <see cref="districtId"/> and hold it, and the city grants <see cref="unlocksId"/>.</summary>
public sealed class CampaignContract
{
    public readonly string districtId, unlocksId;
    public readonly int backlog;
    public readonly float rateMult;

    public CampaignContract(string districtId, string unlocksId, int backlog, float rateMult)
    {
        this.districtId = districtId; this.unlocksId = unlocksId; this.backlog = backlog; this.rateMult = rateMult;
    }

    public DistrictDef District => Districts.Get(districtId);
    public DistrictDef Unlocks => Districts.Get(unlocksId);
}

/// <summary>What the HUD shows of the contract being worked, on every machine (host-written, replicated by WorldState).</summary>
public struct CampaignDisplay : INetworkSerializable, IEquatable<CampaignDisplay>
{
    /// <summary>Index in <see cref="Districts.All"/> of the district being cleaned, or -1 when no contract is running.</summary>
    public sbyte district;
    /// <summary>Old bags left / old bags it started with (the clean-up progress).</summary>
    public ushort backlog, backlogStart;
    /// <summary>
    /// <c>GameClock.TotalHours</c> when the current unbroken hold began (0 = no hold is running). The countdown is worked out from
    /// this against each machine's own game clock, so it ticks down live in game minutes with no traffic between changes.
    /// </summary>
    public double holdStart;
    /// <summary>The bar, 0..100. While old bags remain it never reads above 89: the line is for the district, not for the heaps.</summary>
    public byte cleanPct;
    /// <summary>1 = the old trash is gone and the district is at or above the line right now.</summary>
    public byte qualifying;
    /// <summary>Bumps every time a hold is lost; <see cref="lostTenths"/> is how many game hours (tenths) it had counted.</summary>
    public ushort lostSerial, lostTenths;
    /// <summary>Bumps every time a contract is won (clients announce it); <see cref="lastWon"/> is the district index.</summary>
    public ushort wonSerial;
    public sbyte lastWon;

    public bool Active => district >= 0;
    public bool Holding => holdStart > 0.0;
    /// <summary>Game hours held so far, 0..HoldHours, against a game clock reading of <paramref name="nowHours"/>.</summary>
    public double HoldHoursAt(double nowHours) =>
        !Holding ? 0.0 : Math.Max(0.0, Math.Min(CampaignTuning.HoldHours, nowHours - holdStart));
    /// <summary>Game hours still to hold (the full day when no hold is running).</summary>
    public double HoursLeftAt(double nowHours) => CampaignTuning.HoldHours - HoldHoursAt(nowHours);

    public static CampaignDisplay None => new CampaignDisplay { district = -1, lastWon = -1 };

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref district);
        s.SerializeValue(ref backlog);
        s.SerializeValue(ref backlogStart);
        s.SerializeValue(ref holdStart);
        s.SerializeValue(ref cleanPct);
        s.SerializeValue(ref qualifying);
        s.SerializeValue(ref lostSerial);
        s.SerializeValue(ref lostTenths);
        s.SerializeValue(ref wonSerial);
        s.SerializeValue(ref lastWon);
    }

    public bool Equals(CampaignDisplay o) =>
        district == o.district && backlog == o.backlog && backlogStart == o.backlogStart && holdStart.Equals(o.holdStart) &&
        cleanPct == o.cleanPct && qualifying == o.qualifying && lostSerial == o.lostSerial && lostTenths == o.lostTenths &&
        wonSerial == o.wonSerial && lastWon == o.lastWon;

    public override bool Equals(object obj) => obj is CampaignDisplay o && Equals(o);
    public override int GetHashCode() => (district << 24) ^ (cleanPct << 8) ^ backlog ^ (lostSerial << 16) ^ wonSerial;
}

/// <summary>The campaign's pure rules: no scene, no network, no clock. Pinned by CampaignRulesTests.</summary>
public static class CampaignRules
{
    /// <summary>"Old trash" bag ids look like "{stopId}/old{n}": still "{stopId}/..." so Districts.ForBagId works.</summary>
    public const string BacklogTag = "/old";

    public static bool IsBacklogBagId(string bagId) =>
        !string.IsNullOrEmpty(bagId) && bagId.IndexOf(BacklogTag, StringComparison.Ordinal) > 0;

    public static string BacklogBagId(string stopId, int n) => stopId + BacklogTag + n;

    /// <summary>The district reads clean enough (rounded percent: 89.5% counts as 90%).</summary>
    public static bool Qualifies(float clean01) => clean01 >= CampaignTuning.Threshold - 0.005f;

    /// <summary>
    /// One tick of the hold. Time only counts while the district qualifies; one dip ends the attempt. Returns the new hold.
    /// <paramref name="started"/>: this tick began an attempt. <paramref name="broke"/>: it ended one, with
    /// <paramref name="lost"/> = the hours that were held. <paramref name="won"/>: the full hold is in.
    /// </summary>
    public static double Advance(double held, bool qualifying, double dtHours, double holdHours,
                                 out bool started, out bool broke, out double lost, out bool won)
    {
        started = broke = won = false;
        lost = 0;
        if (!qualifying)
        {
            if (held > 0) { broke = true; lost = held; }
            return 0;
        }
        if (held <= 0 && dtHours > 0) started = true;
        held += Math.Max(0, dtHours);
        if (held >= holdHours) { won = true; held = holdHours; }
        return held;
    }

    /// <summary>How many houses carry the heaps: about <see cref="CampaignTuning.BagsPerHeap"/> bags each, at least 1.</summary>
    public static int HeapCount(int bags, int houses) =>
        bags <= 0 || houses <= 0 ? 0 : Math.Min(houses, Math.Max(1, (int)Math.Ceiling(bags / CampaignTuning.BagsPerHeap)));

    /// <summary>Age of the n-th old bag: past the district's neglect window by a hash-spread amount (deterministic).</summary>
    public static double BacklogAgeHours(float neglectedHours, uint hash)
    {
        double u = (hash & 0xFFFFu) / 65535.0;
        return neglectedHours + CampaignTuning.BacklogMinExtraAgeHours +
               u * (CampaignTuning.BacklogMaxExtraAgeHours - CampaignTuning.BacklogMinExtraAgeHours);
    }

    /// <summary>"23h 41m" for a countdown, rounded UP to the game minute so it starts at "24h 00m" and the win lands on "0m".</summary>
    public static string FormatLeft(double hoursLeft)
    {
        int minutes = (int)Math.Ceiling(Math.Max(0.0, hoursLeft) * 60.0 - 1e-6);
        int h = minutes / 60, m = minutes % 60;
        return h > 0 ? h + "h " + m.ToString("00") + "m" : m + "m";
    }

    /// <summary>The bar's reading: the district's clean percent, held just under the line while any old bag is left.</summary>
    public static int BarPercent(float clean01, bool oldTrashLeft)
    {
        int pct = (int)Math.Round(Math.Max(0f, Math.Min(1f, clean01)) * 100.0, MidpointRounding.AwayFromZero);
        return oldTrashLeft ? Math.Min(pct, CampaignTuning.ThresholdPct - 1) : pct;
    }

    /// <summary>Dirt one old bag adds while its district is under contract: the whole heap is exactly "all the dirt there is",
    /// so the bar moves with every bag instead of sitting at 0% until the last few.</summary>
    public static float HeapDirtPerBag(float dirtForZero, int heapSize) => dirtForZero / Math.Max(1, heapSize);

    /// <summary>The counter stores remaining + 1 so that 0 means "never seeded".</summary>
    public static int EncodeLeft(int left) => Math.Max(0, left) + 1;
    public static int DecodeLeft(int counter) => counter <= 0 ? -1 : counter - 1;
}

/// <summary>
/// The first campaign slice (Docs/Campaign.md §14): contract districts start messy, the permit of the district they open is
/// no longer for sale, and holding the district at <see cref="CampaignTuning.Threshold"/> for
/// <see cref="CampaignTuning.HoldHours"/> wins the contract: the city grants the next district's permit.
/// <para>State. "Won" is not stored: a contract is won when the district it opens is open in the Town (so a save that bought
/// the permit before this slice counts as won). The old-bag count per district rides in the host's business save as the counter
/// <c>campaign_backlog_{id}</c> (remaining + 1; 0 = never seeded), so quitting mid clean-up keeps the progress. The hold is not
/// saved: a new session starts a new attempt.</para>
/// <para>Host only: seeding, the hold and the win (CampaignRunner). Everyone: the HUD, which reads <see cref="Display"/>
/// (replicated through WorldState).</para>
/// </summary>
public static class Campaign
{
    public const string BacklogKey = "campaign_backlog_", WonKey = "campaign_won_", IntroKey = "campaign_intro_";

    static readonly CampaignContract[] contracts =
    {
        new CampaignContract(Districts.MildlyPleasant, Districts.MinivanMeadows, CampaignTuning.AcresBacklog, 1f),
        new CampaignContract(Districts.MinivanMeadows, Districts.CouponCommons, CampaignTuning.MeadowsBacklog, CampaignTuning.MeadowsRateMult),
    };

    public static IReadOnlyList<CampaignContract> Contracts => contracts;

    /// <summary>The contract whose district to clean is <paramref name="districtId"/> (null for every other district).</summary>
    public static CampaignContract ContractFor(string districtId)
    {
        if (string.IsNullOrEmpty(districtId)) return null;
        for (int i = 0; i < contracts.Length; i++) if (contracts[i].districtId == districtId) return contracts[i];
        return null;
    }

    /// <summary>The contract that opens <paramref name="districtId"/> (null when it opens another way).</summary>
    public static CampaignContract ContractOpening(string districtId)
    {
        if (string.IsNullOrEmpty(districtId)) return null;
        for (int i = 0; i < contracts.Length; i++) if (contracts[i].unlocksId == districtId) return contracts[i];
        return null;
    }

    /// <summary>This district is opened by winning a contract, not by buying its permit.</summary>
    public static bool IsGrantedByContract(string districtId) => ContractOpening(districtId) != null;

    /// <summary>The permit item belongs to a district opened by contract (it can't be bought).</summary>
    public static bool IsContractPermit(string permitId)
    {
        if (string.IsNullOrEmpty(permitId)) return false;
        for (int i = 0; i < contracts.Length; i++) if (contracts[i].Unlocks.permitId == permitId) return true;
        return false;
    }

    /// <summary>Won: the district it opens is open in the Town (granted by the contract, or bought before this slice).</summary>
    public static bool IsWon(CampaignContract c) => c != null && DistrictAccess.OpenInTown(c.Unlocks);

    /// <summary>Running: its district is open and the contract isn't won yet.</summary>
    public static bool IsActive(CampaignContract c) => c != null && DistrictAccess.OpenInTown(c.District) && !IsWon(c);

    /// <summary>The contract being worked right now (at most one): its district is open and the one it opens is not.</summary>
    public static CampaignContract Active
    {
        get
        {
            for (int i = 0; i < contracts.Length; i++)
                if (IsActive(contracts[i])) return contracts[i];
            return null;
        }
    }

    /// <summary>The district is under contract right now: it is messy on purpose (no neglect fines, Trust losses or worst-district
    /// penalty for it) and its houses put trash out at the contract's rate.</summary>
    public static bool IsUnderContract(string districtId)
    {
        var a = Active;
        return a != null && a.districtId == districtId;
    }

    /// <summary>Put-out rate multiplier for a district's houses (1 outside a contract).</summary>
    public static float RateMult(DistrictDef d)
    {
        if (d == null) return 1f;
        var a = Active;
        return a != null && a.districtId == d.id ? a.rateMult : 1f;
    }

    /// <summary>The district whose cleanliness a Clean Day is judged on: the top open district, or the one before it while the
    /// top one is still under contract (it is meant to be dirty).</summary>
    public static DistrictDef CleanDayDistrict(DistrictDef top)
    {
        var a = Active;
        if (top == null || a == null || a.districtId != top.id) return top;
        var order = Districts.ProgressionOrder;
        for (int i = 1; i < order.Count; i++)
            if (order[i].id == top.id) return order[i - 1];
        return top;
    }

    // ------------------------------------------------------------------ what the store, gate and map say

    /// <summary>"Win the Mildly Pleasant contract: keep it 90% clean for 24 hours." (null when the district isn't contract-opened.)</summary>
    public static string LockedReason(string districtId)
    {
        var c = ContractOpening(districtId);
        if (c == null) return null;
        return $"Win the {CityCleanliness.ShortName(c.District)} contract: keep it {Mathf.RoundToInt(CampaignTuning.Threshold * 100f)}% clean for {CampaignTuning.HoldHours:0} hours";
    }

    /// <summary>The Scrapazon lock text for a contract district's permit.</summary>
    public static string PermitLockedReason(string permitId)
    {
        for (int i = 0; i < contracts.Length; i++)
            if (contracts[i].Unlocks.permitId == permitId) return LockedReason(contracts[i].unlocksId);
        return null;
    }

    /// <summary>The dirt one old bag adds to its district while that district is under contract (see CampaignRules.HeapDirtPerBag).</summary>
    public static float HeapDirtPerBag(DistrictDef d)
    {
        var c = d == null ? null : ContractFor(d.id);
        return c == null ? 0f : CampaignRules.HeapDirtPerBag(d.dirtForZero, c.backlog);
    }

    /// <summary>The short caps name the bar wears ("ACRES").</summary>
    public static string HudName(DistrictDef d)
    {
        if (d == null) return "";
        switch (d.id)
        {
            case Districts.MildlyPleasant: return "ACRES";
            case Districts.MinivanMeadows: return "MEADOWS";
            case Districts.CouponCommons: return "COMMONS";
            case Districts.SnootingtonHills: return "SNOOTINGTON";
            case Districts.StackingtonHeights: return "STACKINGTON";
            case Districts.BillionaireBluffs: return "BLUFFS";
            default: return d.name.ToUpperInvariant();
        }
    }

    // ------------------------------------------------------------------ the HUD's view

    static CampaignDisplay display = CampaignDisplay.None;
    static ushort seenWonSerial;
    static bool seenWonInit;

    /// <summary>What the HUD shows of the running contract. The host writes it; guests receive it.</summary>
    public static CampaignDisplay Display => display;

    /// <summary>The display changed (the host's tick, or a guest receiving it).</summary>
    public static event Action<CampaignDisplay> DisplayChanged;

    /// <summary>A contract was just won (every machine): the contract's district.</summary>
    public static event Action<CampaignContract> ContractWon;

    /// <summary>The host's own display, or a guest's copy of it.</summary>
    internal static void SetDisplay(CampaignDisplay d)
    {
        if (display.Equals(d)) return;
        display = d;
        try { DisplayChanged?.Invoke(d); } catch (Exception e) { Debug.LogException(e); }
    }

    /// <summary>Guest: the host's display arrived. A bumped win serial means a contract was won (announce it).</summary>
    public static void ApplyRemote(CampaignDisplay d)
    {
        bool won = seenWonInit && d.wonSerial != seenWonSerial && d.lastWon >= 0;
        seenWonSerial = d.wonSerial;
        seenWonInit = true;
        SetDisplay(d);
        if (won && d.lastWon < Districts.All.Count) Announce(ContractFor(Districts.All[d.lastWon].id), false);
    }

    /// <summary>Everything the player should notice when a contract is won (every machine, once).</summary>
    internal static void Announce(CampaignContract c, bool host)
    {
        if (c == null) return;
        var next = c.Unlocks;
        GameHUD.Toast($"CONTRACT WON! {c.District.name} stayed spotless for {CampaignTuning.HoldHours:0} hours. The city hands over {next.name}. Mind the ribbon.",
                      ToastStyle.Legendary, 5f);
        Sfx.Play("legendary", null, 1f, 1f);
        Juice.Shake(0.25f, 0.3f);
        HeadOffice.Send($"Contract won: {c.District.name}",
            $"Mayor Bandit's office inspected {c.District.name} for {CampaignTuning.HoldHours:0} hours and could not find a single stink. " +
            $"GoldStar Premium Waste has been told. GoldStar Premium Waste said a rude word. The collection permit for {next.name} is yours. Please wipe your tires.");
        Metrics.Begin("contract_announced").Str("dist", c.districtId).Bool("host", host).End();
        try { ContractWon?.Invoke(c); } catch (Exception e) { Debug.LogException(e); }
    }

    // ------------------------------------------------------------------ host: state

    static double holdHours = 0, lastTickHours = double.NaN, nextTickAt;
    static int attempts;
    static ushort wonSerial, lostSerial, lostTenths;
    static sbyte lastWon = -1;
    static string trackedDistrict;
    static bool backlogClearedLogged, seededSession;
    static int placedThisSession;
    /// <summary>Old bags that did not fit the curbs this session (they are still owed: they stay in the saved count).</summary>
    static int unplaced;

    public static double HoldHours => holdHours;
    public static int Attempts => attempts;

    /// <summary>Old bags this district still has, or -1 when it was never seeded.</summary>
    public static int BacklogLeft(string districtId) => CampaignRules.DecodeLeft(GameSession.GetCounter(BacklogKey + districtId));

    static void SetBacklogLeft(string districtId, int left) => GameSession.SetCounter(BacklogKey + districtId, CampaignRules.EncodeLeft(left));

    /// <summary>Old bags of one district on the curbs right now.</summary>
    public static int BacklogOnCurbs(string districtId)
    {
        var bags = GameSession.CurbBags;
        int n = 0;
        for (int i = 0; i < bags.Count; i++)
        {
            var b = bags[i];
            if (b != null && CampaignRules.IsBacklogBagId(b.bagId) && Districts.ForStopId(b.stopId).id == districtId) n++;
        }
        return n;
    }

    /// <summary>A new host session began and CurbTrashSpawner wiped the curbs: nothing is counted until the heaps are back out.</summary>
    internal static void OnSessionBegin()
    {
        seededSession = false;
        holdHours = 0; lastTickHours = double.NaN; attempts = 0; trackedDistrict = null; backlogClearedLogged = false;
        placedThisSession = 0; unplaced = 0;
    }

    /// <summary>The spawner knows its houses and is about to put the opening bags out: the heaps go out first (they get the room).</summary>
    internal static void OnSpawnerSession()
    {
        for (int i = 0; i < contracts.Length; i++)
            if (IsActive(contracts[i])) SeedBacklog(contracts[i]);
        seededSession = true;
    }

    /// <summary>Put a contract district's old bags on its curbs (the saved remainder, or the full heap the first time).</summary>
    internal static void SeedBacklog(CampaignContract c)
    {
        if (c == null || !Businesses.IsAuthority) return;
        var d = c.District;
        int saved = BacklogLeft(d.id);
        bool first = saved < 0;
        int left = first ? c.backlog : saved;
        if (first) SetBacklogLeft(d.id, left);

        // The district's houses, most-hashed first: the same houses carry the heaps every session.
        var houses = new List<CurbTrashSpawner.House>();
        foreach (var h in CurbTrashSpawner.Houses) if (h != null && h.districtId == d.id) houses.Add(h);
        if (houses.Count == 0) { unplaced = left; return; }
        houses.Sort((a, b) =>
        {
            int ha = PickupVisuals.StableHash("heap:" + a.stopId), hb = PickupVisuals.StableHash("heap:" + b.stopId);
            return ha != hb ? ha.CompareTo(hb) : string.CompareOrdinal(a.stopId, b.stopId);
        });
        int heaps = CampaignRules.HeapCount(left, houses.Count);
        float trust = GameSession.GetDistrictTrust(d.id, d.trustStart);
        int placed = 0, guard = 0, seq = placedThisSession;
        while (placed < left && guard++ < left * 8 + 64)
        {
            if (GameSession.CurbBags.Count >= CurbTrashSpawner.Cap || CurbTrash.BagsIn(d) >= CurbTrashSpawner.CurbCap(d)) break;
            var h = houses[placed % Mathf.Max(1, heaps)];
            int slot = CurbTrash.FreeSlot(h.stopId, h.capacity);
            if (slot < 0)
            {
                // this house is full: the next one in line takes it
                bool put = false;
                for (int k = 0; k < houses.Count && !put; k++)
                {
                    slot = CurbTrash.FreeSlot(houses[k].stopId, houses[k].capacity);
                    if (slot >= 0) { h = houses[k]; put = true; }
                }
                if (!put) break;
            }
            string bagId = CampaignRules.BacklogBagId(h.stopId, ++seq);
            if (GameSession.FindCurbBag(bagId) != null) continue;
            // The district's own profile: no Rookie Luck, no gnomes. Fifty-odd bags at four times the treasure would buy the shop.
            var contents = ItemCatalog.RollBag(bagId, PickupVisuals.StableHash(bagId), h.wealth, h.address, d.loot, trust);
            if (contents == null) break;
            if (string.IsNullOrEmpty(contents.bagId)) contents.bagId = bagId;
            if (string.IsNullOrEmpty(contents.foundAt)) contents.foundAt = h.address;
            double age = CampaignRules.BacklogAgeHours(d.neglectedHours, (uint)PickupVisuals.StableHash("age:" + bagId));
            GameSession.AddCurbBag(new CurbBag { bagId = contents.bagId, stopId = h.stopId, slot = slot, putOutAtHours = GameClock.TotalHours - age, contents = contents });
            placed++;
        }
        placedThisSession = seq;
        unplaced = Mathf.Max(0, left - placed);
        CurbTrash.Invalidate();
        Debug.Log($"[Campaign] {d.name}: {placed} old bags on {heaps} heaps ({(first ? "new contract" : "resumed")}, {unplaced} did not fit).");
        Metrics.Begin(first ? "contract_start" : "contract_resume").Str("dist", d.id).Int("bags", placed).Int("owed", left)
               .Int("heaps", heaps).Int("players", Businesses.OnlineCount).End();

        if (first && GameSession.GetCounter(IntroKey + d.id) == 0)
        {
            GameSession.SetCounter(IntroKey + d.id, 1);
            HeadOffice.Send($"Contract: {d.name}",
                $"{d.name} has not seen a collection in a very long time. There are {left} old bags out there and the raccoons are running the place. " +
                $"Clear them, then keep the district at {Mathf.RoundToInt(CampaignTuning.Threshold * 100f)}% clean for {CampaignTuning.HoldHours:0} hours straight (one full day, night included) " +
                $"and the city will hand you {c.Unlocks.name}.");
        }
    }

    // ------------------------------------------------------------------ host: the tick

    /// <summary>Host, a couple of times a second (CampaignRunner): count the old bags, advance the hold, win.</summary>
    internal static void HostTick()
    {
        if (!Businesses.IsAuthority || !CurbTrashSpawner.IsRunning || !seededSession) return;
        if (Time.unscaledTime < nextTickAt) return;
        nextTickAt = Time.unscaledTime + CampaignTuning.TickSeconds;

        var c = Active;
        if (c == null)
        {
            if (trackedDistrict != null) { trackedDistrict = null; holdHours = 0; }
            PublishDisplay(null, 0, 0, 0, false);
            return;
        }

        var d = c.District;
        if (trackedDistrict != d.id)
        {
            trackedDistrict = d.id; holdHours = 0; lastTickHours = double.NaN; attempts = 0; backlogClearedLogged = false;
        }
        if (BacklogLeft(d.id) < 0) SeedBacklog(c);      // opened without a session start (the debug menu, a save change)

        int onCurbs = BacklogOnCurbs(d.id);
        int left = onCurbs + unplaced;
        if (BacklogLeft(d.id) >= 0 && BacklogLeft(d.id) != left) SetBacklogLeft(d.id, left);
        if (left == 0 && !backlogClearedLogged)
        {
            backlogClearedLogged = true;
            Metrics.Begin("backlog_cleared").Str("dist", d.id).Int("players", Businesses.OnlineCount).End();
        }

        double now = GameClock.TotalHours;
        double dt = double.IsNaN(lastTickHours) ? 0 : Math.Min(CampaignTuning.MaxStepHours, Math.Max(0, now - lastTickHours));
        lastTickHours = now;

        float clean = CityCleanliness.District01(d.id);
        bool ok = left == 0 && CampaignRules.Qualifies(clean);     // the old trash has to be gone first, not just almost
        double before = holdHours;
        holdHours = CampaignRules.Advance(holdHours, ok, dt, CampaignTuning.HoldHours, out bool started, out bool broke, out double lost, out bool won);
        if (started)
        {
            attempts++;
            Metrics.Begin("hold_start").Str("dist", d.id).Int("attempt", attempts).Int("clean", Mathf.RoundToInt(clean * 100f))
                   .Int("players", Businesses.OnlineCount).End();
        }
        if (broke)
        {
            // What broke it: the stalest bag and how many are standing (the numbers a balance pass needs).
            var s = SnapshotDistrict(d);
            Metrics.Begin("hold_break").Str("dist", d.id).Int("attempt", attempts).Num("held_h", lost).Int("clean", Mathf.RoundToInt(clean * 100f))
                   .Int("bags", s.bags).Int("late", s.late).Int("neglected", s.neglected).Num("oldest_h", s.oldestAge)
                   .Int("litter", CityCleanliness.LitterCount).Int("players", Businesses.OnlineCount).End();
            lostSerial++;
            lostTenths = (ushort)Mathf.Clamp(Mathf.RoundToInt((float)(lost * 10.0)), 0, ushort.MaxValue);   // every HUD flashes it and says how much
        }
        PublishDisplay(c, left, c.backlog, clean, ok);
        if (won) Win(c);
    }

    static void PublishDisplay(CampaignContract c, int left, int start, float clean, bool ok)
    {
        var d = display;
        if (c == null)
        {
            d.district = -1; d.backlog = 0; d.backlogStart = 0; d.holdStart = 0; d.cleanPct = 0; d.qualifying = 0;
        }
        else
        {
            d.district = (sbyte)Districts.IndexOf(c.districtId);
            d.backlog = (ushort)Mathf.Clamp(left, 0, ushort.MaxValue);
            d.backlogStart = (ushort)Mathf.Clamp(Mathf.Max(start, left), 0, ushort.MaxValue);
            d.cleanPct = (byte)CampaignRules.BarPercent(clean, left > 0);
            d.qualifying = (byte)(ok ? 1 : 0);
            if (ok && holdHours > 0.0)
            {
                // The countdown is an anchor on the game clock: keep the old one while it still agrees (no traffic while it runs).
                double anchor = GameClock.TotalHours - holdHours;
                d.holdStart = d.Holding && Math.Abs(d.holdStart - anchor) <= 0.02 ? d.holdStart : anchor;
            }
            else d.holdStart = 0;
        }
        d.lostSerial = lostSerial;
        d.lostTenths = lostTenths;
        d.wonSerial = wonSerial;
        d.lastWon = lastWon;
        SetDisplay(d);
    }

    struct DistrictSnap { public int bags, late, neglected; public double oldestAge; }

    static DistrictSnap SnapshotDistrict(DistrictDef d)
    {
        var s = new DistrictSnap();
        double now = GameClock.TotalHours;
        var bags = GameSession.CurbBags;
        for (int i = 0; i < bags.Count; i++)
        {
            var b = bags[i];
            if (b == null || Districts.ForStopId(b.stopId) != d) continue;
            double age = Math.Max(0, now - b.putOutAtHours);
            s.bags++;
            if (Districts.IsLate(d, age)) s.late++;
            if (age > s.oldestAge) s.oldestAge = age;
        }
        s.neglected = CurbTrash.NeglectedStopsIn(d);
        return s;
    }

    /// <summary>Host: the hold is complete. The city grants the next district (its permit goes to this business, the Town opens
    /// it for everyone), the next contract's heaps go out, and every machine hears about it.</summary>
    internal static void Win(CampaignContract c)
    {
        if (c == null) return;
        var next = c.Unlocks;
        Metrics.Begin("contract_win").Str("dist", c.districtId).Str("unlocks", c.unlocksId).Int("attempts", attempts)
               .Int("players", Businesses.OnlineCount).Int("rank", Progression.Rank).Num("cash", GameSession.Cash).End();
        GameSession.SetCounter(WonKey + c.districtId, 1);
        holdHours = 0; trackedDistrict = null; backlogClearedLogged = false;
        wonSerial++;
        lastWon = (sbyte)Districts.IndexOf(c.districtId);
        Progression.UnlockDistrict(next);                           // permit level 1, Trust at its start, ribbon, the stamp card
        Announce(c, true);
        var follow = ContractFor(next.id);
        if (follow != null) SeedBacklog(follow);
        PublishDisplay(Active, Active != null ? BacklogLeft(Active.districtId) : 0, Active != null ? Active.backlog : 0, 0f, false);
    }

    /// <summary>The Editor's debug menu: win the running contract now / forget a district's heaps so they seed again.</summary>
    public static bool DebugWinActive()
    {
        var c = Active;
        if (c == null || !Businesses.IsAuthority) return false;
        Win(c);
        return true;
    }

    public static void DebugClearBacklog(string districtId)
    {
        var bags = GameSession.CurbBags;
        for (int i = bags.Count - 1; i >= 0; i--)
            if (CampaignRules.IsBacklogBagId(bags[i].bagId) && Districts.ForStopId(bags[i].stopId).id == districtId)
                GameSession.RemoveCurbBag(bags[i].bagId);
        unplaced = 0;
        SetBacklogLeft(districtId, 0);
        CurbTrash.Invalidate();
    }

    public static void DebugReseed(string districtId)
    {
        GameSession.SetCounter(BacklogKey + districtId, 0);
        GameSession.SetCounter(IntroKey + districtId, 0);
        DebugClearBacklog(districtId);
        GameSession.SetCounter(BacklogKey + districtId, 0);
        SeedBacklog(ContractFor(districtId));
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        display = CampaignDisplay.None;
        DisplayChanged = null; ContractWon = null;
        holdHours = 0; lastTickHours = double.NaN; nextTickAt = 0; attempts = 0; wonSerial = 0; lostSerial = 0; lostTenths = 0; lastWon = -1;
        trackedDistrict = null; backlogClearedLogged = false; seededSession = false; placedThisSession = 0; unplaced = 0;
        seenWonSerial = 0; seenWonInit = false;
    }
}

/// <summary>Drives <see cref="Campaign.HostTick"/> on the host. Hidden DontDestroyOnLoad object, play mode only.</summary>
public sealed class CampaignRunner : MonoBehaviour
{
    static CampaignRunner instance;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (instance != null || !Application.isPlaying) return;
        var go = new GameObject("[Campaign]");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<CampaignRunner>();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => instance = null;

    void Awake() => Campaign.DisplayChanged += ContractFeedback.OnDisplay;

    void OnDestroy()
    {
        Campaign.DisplayChanged -= ContractFeedback.OnDisplay;
        if (instance == this) instance = null;
    }

    void Update()
    {
        Campaign.HostTick();                                  // host only: it ignores the call anywhere else
        ContractFeedback.Tick(GameClock.TotalHours);           // everywhere
    }
}
