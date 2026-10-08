using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Chipped raccoons collect and deliver for the business that owns them (calling_collar_chip, calling_brain_chip 1-2).
/// This is the game's only background collection: data only (no driving AI), HOST only, sweeps at fixed hours, one crew
/// per OWNING business. Every number is in <see cref="RaccoonTuning"/>; every decision is a pure rule in <see cref="CrewRules"/>.
/// <list type="bullet">
/// <item><b>When</b>: Collar Chip 22:00 and 02:00; the Brain Chip adds 12:00; the Full-Time Chip (Brain Chip level 2) adds
/// 07:00. Businesses sweep one after another and a taken bag's record is removed at once, so no bag is ever collected
/// or paid twice.</item>
/// <item><b>Which bags</b>: kerb bags in districts the OWNER holds the permit for, up to tier 3 (never the Bluffs), and
/// never one a player is holding or has thrown. Collar Chip: oldest first. Brain Chip: the cheapest district first,
/// oldest first inside it (<see cref="CrewRules.CompareBags"/>).</item>
/// <item><b>How many</b>: 1-3 per raccoon by its Carry (2-6 with a Brain Chip), plus a Zoom chance of one more, capped per
/// business per sweep by hutch size and chip (<see cref="CrewRules.SweepCap"/>: 6 to 26). A raccoon eats a Greed-sized
/// share of its haul: those bags are just gone.</item>
/// <item><b>Pay</b>: 35% of fee x lateness fraction through the Ledger to the OWNER's business, no tips. The BagCollected
/// fact is raised inside that business's Ledger scope with <c>byRaccoon</c> set: half Rep, no Work Order credit, counted
/// as crew bags in the day's stats, and the Night Shift x1.5 lands on the same business as for any other night pickup.</item>
/// <item><b>Delivery</b>: the host's own business (and solo) gets the bags as depot deliveries (the tipping floor, saved
/// with the business). Another player's business gets them in its truck's hopper as cargo; what does not fit waits.</item>
/// <item><b>Report</b>: each raccoon's haul for the shift is in the roster; at 06:00 the shift closes and the owner's
/// machine shows the morning report ("Biscuit brought 6, ate 2, got chased by a dog.").</item>
/// </list>
/// </summary>
public static class RaccoonCrew
{
    /// <summary>Fired after a sweep (host): business id, bags delivered, bags eaten, fees paid.</summary>
    public static event Action<string, int, int, float> Swept;

    static double lastTick = -1;
    static float nextReportCheck, nextDeliveryCheck, nextHostTick;
    static readonly Dictionary<string, List<BagContents>> waiting = new Dictionary<string, List<BagContents>>(StringComparer.Ordinal);
    static readonly List<CurbBag> tmpBags = new List<CurbBag>();
    static readonly Dictionary<string, float> tmpFees = new Dictionary<string, float>(StringComparer.Ordinal);
    static bool hooked;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Swept = null; lastTick = -1; nextReportCheck = nextDeliveryCheck = nextHostTick = 0f; waiting.Clear(); hooked = false;
        lastSeenReport = -1; lastSeenBusiness = null; tmpFees.Clear();
    }

    // ------------------------------------------------------------------ host tick

    internal static void HostTick()
    {
        if (GameSession.IsGuest || CurbTrashSpawner.Houses.Count == 0) return;
        if (!hooked) { hooked = true; BusinessRegistry.Closing -= OnClosing; BusinessRegistry.Closing += OnClosing; }
        if (Time.unscaledTime < nextHostTick) return;
        nextHostTick = Time.unscaledTime + 0.5f;
        double now = GameClock.TotalHours;
        if (lastTick < 0 || now < lastTick) { lastTick = now; return; } // a new server session restarts the clock: resume from now
        HostReports();
        var due = CrewRules.DueSlots(lastTick, now, RaccoonTuning.FullTimeSweepHours); // the superset of every chip's hours
        lastTick = now;
        foreach (int slot in due)
        {
            int hour = slot % 24;
            foreach (var business in Businesses.All)
            {
                if (!business.IsValid) continue;
                int collar = Hutch.LevelFor(business.ownerClientId, CallingIds.CollarChip), brain = Hutch.LevelFor(business.ownerClientId, CallingIds.BrainChip);
                if (Array.IndexOf(CrewRules.SweepHours(collar, brain), hour) < 0) continue;
                try { Sweep(business, Hutch.LevelFor(business.ownerClientId, CallingIds.CritterHutch), collar, brain, slot); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }
        HostDeliverWaiting();
    }

    static void OnClosing(string businessId, ulong owner) => waiting.Remove(businessId ?? "");

    /// <summary>The 06:00 rollover: any roster still holding an untold, finished shift gets its report closed.</summary>
    static void HostReports()
    {
        if (Time.unscaledTime < nextReportCheck) return;
        nextReportCheck = Time.unscaledTime + 3f;
        int shift = CrewRules.ShiftOf(GameClock.Day, GameClock.Hours);
        foreach (var business in Businesses.All)
        {
            if (!business.IsValid) continue;
            var seen = Hutch.For(business.Id);
            if (seen.told || seen.shift == shift || seen.Count == 0) continue;
            Hutch.HostEdit(business.Id, h => { if (h.told || h.shift == shift) return false; CrewRules.CloseShift(h); return true; });
        }
    }

    static bool Permitted(BusinessInfo business, DistrictDef district)
    {
        if (district == null || !CrewRules.WorksTier(district.tier)) return false;
        return business.Id == Businesses.LocalId ? Districts.IsUnlocked(district) : BusinessUpgradeNet.HasPermitForBusiness(business.Id, district);
    }

    /// <summary>A bag being carried / thrown / tossed is the player's: crews leave it alone.</summary>
    internal static bool HeldByAPlayer(CurbBag rec)
    {
        var stop = PickupStop.Find(rec.stopId);
        if (stop == null) return false;
        var bags = stop.SpawnedBags;
        for (int i = 0; i < bags.Count; i++)
        {
            var bag = bags[i];
            if (bag == null || bag.Contents == null || bag.Contents.bagId != rec.bagId) continue;
            return bag.State != CarryState.Resting || bag.IsInScriptedFlight;
        }
        return false;
    }

    /// <summary>One business's crew sweeps now (host). <paramref name="slot"/> = day index x 24 + hour.</summary>
    static void Sweep(BusinessInfo business, int hutchLevel, int collar, int brain, int slot)
    {
        var seen = Hutch.For(business.Id);
        bool anyone = false;
        foreach (var r in seen.raccoons) if (r.job == (int)HutchJob.Crew && r.Available) { anyone = true; break; }
        if (!anyone) return;

        bool local = business.Id == Businesses.LocalId;
        tmpBags.Clear();
        tmpFees.Clear();
        foreach (var bag in GameSession.CurbBags)
        {
            if (bag == null) continue;
            var district = Districts.ForStopId(bag.stopId);
            if (!Permitted(business, district) || HeldByAPlayer(bag)) continue;
            tmpBags.Add(bag);
            tmpFees[bag.bagId ?? ""] = district.fee;
        }
        tmpBags.Sort((a, b) => CrewRules.CompareBags(brain, tmpFees[a.bagId ?? ""], a.putOutAtHours, a.bagId, tmpFees[b.bagId ?? ""], b.putOutAtHours, b.bagId));

        double atHours = slot; // slot is in whole in-game hours since the session clock's day 0
        int shift = CrewRules.ShiftOf(GameClock.BaseDay + slot / 24, slot % 24);
        List<CrewTake> takes = null;
        if (!Hutch.HostEdit(business.Id, h =>
        {
            CrewRules.BeginShift(h, shift);
            takes = CrewRules.Sweep(h, hutchLevel, collar, brain, tmpBags.Count, NightDice.Host.Range);
            return true;
        }) || takes == null) return;

        int next = 0, delivered = 0, eaten = 0, anticsCents = 0;
        float fees = 0f;
        var feesByDistrict = new Dictionary<DistrictDef, float>();
        var facts = new List<BagCollectedFact>();
        foreach (var take in takes)
        {
            anticsCents += take.cents;
            for (int k = 0; k < take.taken && next < tmpBags.Count; k++)
            {
                var bag = tmpBags[next++];
                GameSession.RemoveCurbBag(bag.bagId);
                if (k < take.eaten) { eaten++; continue; }
                var d = Districts.ForStopId(bag.stopId);
                double age = Math.Max(0, atHours - bag.putOutAtHours);
                float pay = CrewRules.BagPay(d.fee, Districts.PayFraction(d, age));
                feesByDistrict[d] = (feesByDistrict.TryGetValue(d, out float f) ? f : 0f) + pay;
                fees += pay; delivered++;
                TownWorkDirector.Collected();
                if (bag.contents != null) Deliver(business, local, bag.contents);
                var house = CurbTrashSpawner.FindHouse(bag.stopId);
                facts.Add(new BagCollectedFact
                {
                    bagId = bag.bagId, stopId = bag.stopId, districtId = d.id, ageHours = age, onTime = !Districts.IsLate(d, age),
                    byArm = false, byRaccoon = true, pay = pay, pos = house != null ? house.position : Vector3.zero,
                });
            }
        }
        CurbTrash.Invalidate();
        CityCleanliness.Invalidate();

        using (Ledger.ToBusiness(business.Id))
        {
            foreach (var kv in feesByDistrict)
                if (kv.Value > 0f) Ledger.Credit(Mathf.Round(kv.Value * 100f) / 100f, "Raccoon crew collected", RaccoonTuning.LedgerCrew, null, kv.Key.id);
            if (anticsCents > 0) Ledger.Credit(anticsCents / 100f, "A raccoon found a wallet", RaccoonTuning.LedgerCrewEvent);
            else if (anticsCents < 0) Ledger.Charge(-anticsCents / 100f, "Garden gnome damages", RaccoonTuning.LedgerCrewEvent);
            // Facts inside the owner's scope: the Rep (halved for crew work) and the Night Shift bonus go to the same business.
            foreach (var fact in facts)
            {
                try { ProgressionEvents.RaiseBagCollected(fact); } catch (Exception e) { Debug.LogException(e); }
            }
        }

        if (local && delivered > 0) DropAtOwnDepot();
        if (delivered + eaten > 0)
        {
            string where = local ? "to the depot" : "into your truck";
            NightNet.RaccoonTell(business.ownerClientId, true,
                $"Raccoon crew: {delivered} bag{(delivered == 1 ? "" : "s")} {where}{(eaten > 0 ? $", {eaten} eaten on the way" : "")}. +{Economy.Money(fees)}");
        }
        NightAudit.Event("crew", new Dictionary<string, object>
        {
            { "business", business.Id }, { "slot", slot }, { "delivered", delivered }, { "eaten", eaten },
            { "cents", Mathf.RoundToInt(fees * 100f) }, { "antics", anticsCents }, { "available", tmpBags.Count },
        });
        try { Swept?.Invoke(business.Id, delivered, eaten, fees); } catch (Exception e) { Debug.LogException(e); }
    }

    /// <summary>Debug / QA (host or solo): this machine's own crew sweeps right now, whatever the hour.</summary>
    public static string DebugSweepNow()
    {
        if (GameSession.IsConnectedGuest) return "Host or solo only.";
        if (!Businesses.TryGet(Hutch.LocalBusinessId, out var business)) business = Businesses.Local;
        if (!business.IsValid) return "No business.";
        int collar = Hutch.LocalLevel(CallingIds.CollarChip), brain = Hutch.LocalLevel(CallingIds.BrainChip);
        if (collar <= 0) return "Build the Collar Chip first.";
        int before = GameSession.CurbBags.Count;
        Sweep(business, Hutch.LocalLevel(CallingIds.CritterHutch), collar, brain, (int)Math.Floor(GameClock.TotalHours));
        return $"Swept: {before - GameSession.CurbBags.Count} of {before} kerb bags taken.";
    }

    // ------------------------------------------------------------------ delivery

    static void Deliver(BusinessInfo business, bool local, BagContents contents)
    {
        if (local) { GameSession.AddDepotDelivery(contents); return; } // saved with the business; the depot's chute drops them
        if (!waiting.TryGetValue(business.Id, out var list)) waiting[business.Id] = list = new List<BagContents>();
        list.Add(contents);
    }

    /// <summary>The host's own depot chute drops the new deliveries now if that depot is up on this machine (otherwise they
    /// wait in the saved list for the next visit or the 18:00 drop). Never another business's chute.</summary>
    static void DropAtOwnDepot()
    {
        foreach (var dropper in UnityEngine.Object.FindObjectsByType<DepotDeliveryDropper>(FindObjectsSortMode.None))
        {
            if (dropper == null || !dropper.isActiveAndEnabled) continue;
            int lot = DepotMachineContext.LotOf(dropper);
            if (lot >= 0 && !(Workshop.TryBusinessAt(lot, out var owner) && owner.Id == Businesses.LocalId)) continue;
            dropper.DropPending(DeliveryWho + " dropped off");
        }
    }

    /// <summary>Host: bags for other players' businesses go into that business's truck as cargo, as room allows.</summary>
    static void HostDeliverWaiting()
    {
        if (waiting.Count == 0 || Time.unscaledTime < nextDeliveryCheck) return;
        nextDeliveryCheck = Time.unscaledTime + 2f;
        foreach (var kv in waiting)
        {
            var list = kv.Value;
            if (list.Count == 0 || !Businesses.TryGet(kv.Key, out _)) continue;
            var cargo = Businesses.HostCargo(kv.Key);
            if (cargo == null || !Businesses.TryGetTruckLoad(kv.Key, out float kg, out float cap)) continue;
            bool moved = false;
            while (list.Count > 0 && kg + list[0].TotalMassKg <= cap + 0.001f)
            {
                kg += list[0].TotalMassKg; cargo.Add(list[0]); list.RemoveAt(0); moved = true;
            }
            if (moved) Businesses.SetTruckLoad(kv.Key, kg, cap);
        }
    }

    // ------------------------------------------------------------------ depot chute wording (DepotDeliveryDropper)

    static bool LocalCrewWorks => Hutch.LocalLevel(CallingIds.CollarChip) > 0 && Hutch.Local.CountJob(HutchJob.Crew) > 0;
    /// <summary>Who the depot's delivery chute credits. Bags can also be waiting from before this business had a working
    /// crew (an older save's deliveries, or a crew that has since been sent to rest): those are simply "a delivery".</summary>
    public static string DeliveryWho => LocalCrewWorks ? "Your raccoons" : "The night crew";
    public static string DeliveryTail => LocalCrewWorks ? "They want kibble." : "Nobody signed for it.";

    // ------------------------------------------------------------------ the owner's morning report (every machine, own business)

    static int lastSeenReport = -1;
    static string lastSeenBusiness;

    internal static void LocalTick()
    {
        string id = Hutch.LocalBusinessId;
        var h = Hutch.Local;
        if (id != lastSeenBusiness || lastSeenReport < 0) { lastSeenBusiness = id; lastSeenReport = h.reportDay; return; }
        if (h.reportDay == lastSeenReport) return;
        bool fresh = h.reportDay > lastSeenReport;
        lastSeenReport = h.reportDay;
        if (!fresh || Hutch.LocalLevel(CallingIds.CollarChip) <= 0) return;
        var lines = CrewRules.ReportLines(h);
        if (lines.Count == 0) return;
        CrewRules.ShiftTotals(h, out int brought, out int ate);
        GameHUD.Toast($"Morning report: your raccoons brought {brought} bag{(brought == 1 ? "" : "s")}{(ate > 0 ? $" and ate {ate}" : "")}. {lines[0]}", ToastStyle.Good, 5f);
        HeadOffice.Send("Night crew report (unofficial)", string.Join(" ", lines) + " Head Office does not recognise raccoons as staff.", false);
        Sfx.Play("chitter", null, 0.5f, 1.3f);
    }
}
