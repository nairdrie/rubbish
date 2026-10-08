using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The Scavenger calling as THIS machine's player uses it: your own levels, your requests to the host and what comes
/// back. On the host (and solo) a request is a direct call into <see cref="ScavengerHost"/>; on a guest it is a guarded
/// RPC and the answer arrives a moment later. State kept here is the local business's own (its hot goods are in
/// <c>GameSession.Inventory</c>, its theft counters and Call It day are company counters), so it saves with the business.
/// </summary>
public static class Scavenger
{
    public static int Level(string callingId) => GameSession.GetUpgradeLevel(callingId);
    public static bool HasItch => Level(CallingIds.TheItch) > 0;
    public static int HotHeld => ScavengerRules.HotCount(GameSession.Inventory);
    public static int SackCapacity => ScavengerRules.SackCapacity(Level(CallingIds.SwagSack));
    static ulong Me => Permissions.LocalClientId;
    static HandsNet Net => HandsNet.Instance != null && HandsNet.Instance.IsSpawned ? HandsNet.Instance : null;

    /// <summary>The host answered (stopId, info).</summary>
    public static event Action<string, CaseInfo> CaseAnswered;
    public static event Action<BreakInResult, List<ItemInstance>> BreakInAnswered;
    public static event Action<CallResult> CallAnswered;
    public static event Action<FenceResult> FenceAnswered;

    static void Raise<T>(Action<T> handler, T arg) { try { handler?.Invoke(arg); } catch (Exception e) { Debug.LogException(e); } }

    // ------------------------------------------------------------------ x-ray specs

    static readonly Dictionary<string, XRaySummary> peeks = new Dictionary<string, XRaySummary>(StringComparer.Ordinal);
    static readonly Dictionary<string, float> nextAsk = new Dictionary<string, float>(StringComparer.Ordinal);

    /// <summary>What your specs show of this bag. False while nothing is known yet (the question is on its way to the host).</summary>
    public static bool TryPeek(TrashBag bag, out XRaySummary summary)
    {
        summary = default;
        int level = Level(CallingIds.XRaySpecs);
        string id = bag != null && bag.Contents != null ? bag.Contents.bagId : null;
        if (level <= 0 || string.IsNullOrEmpty(id)) return false;
        bool known = peeks.TryGetValue(id, out summary);
        if (known && summary.level >= level) return true;
        if (!nextAsk.TryGetValue(id, out float at) || Time.unscaledTime >= at)
        {
            if (nextAsk.Count > 600) { nextAsk.Clear(); peeks.Clear(); known = false; }
            nextAsk[id] = Time.unscaledTime + ScavengerTuning.XRayRetrySeconds;
            if (ScavengerHost.Authority)
            {
                var s = ScavengerHost.Peek(Me, id, bag.NetId);
                if (s.level > 0) { peeks[id] = summary = s; return true; }
            }
            else Net?.ScavAskPeek(id, bag.NetId);
        }
        return known && summary.level > 0;
    }

    internal static void OnPeek(string bagId, XRaySummary summary)
    {
        if (string.IsNullOrEmpty(bagId) || summary.level == 0) return;
        if (!peeks.TryGetValue(bagId, out var old) || summary.level >= old.level) peeks[bagId] = summary;
    }

    // ------------------------------------------------------------------ call it

    static readonly HashSet<string> calledHere = new HashSet<string>(StringComparer.Ordinal);
    public static bool CallReady => Level(CallingIds.CallIt) > 0 && ScavengerRules.CallAvailable(GameSession.GetCounter(ScavengerIds.CallItDay), GameClock.Day);
    public static bool CanCall(TrashBag bag) =>
        CallReady && bag != null && bag.Contents != null && !string.IsNullOrEmpty(bag.Contents.bagId) && !calledHere.Contains(bag.Contents.bagId);

    public static void RequestCall(TrashBag bag, int band)
    {
        if (!CanCall(bag)) return;
        string id = bag.Contents.bagId;
        if (ScavengerHost.Authority) OnCall(ScavengerHost.CallIt(Me, id, bag.NetId, band));
        else if (Net != null) Net.ScavAskCall(id, bag.NetId, band);
        else OnCall(new CallResult { why = "No connection to the Town." });
        calledHere.Add(id);
    }

    internal static void OnCall(CallResult result)
    {
        result.why ??= "";
        if (result.status != 0) GameSession.SetCounter(ScavengerIds.CallItDay, GameClock.Day); // right or wrong, that was today's call
        Raise(CallAnswered, result);
    }

    // ------------------------------------------------------------------ burglary

    public static void RequestCase(string stopId)
    {
        if (string.IsNullOrEmpty(stopId) || !HasItch) return;
        if (ScavengerHost.Authority) OnCase(stopId, ScavengerHost.Case(Me, stopId));
        else Net?.ScavAskCase(stopId);
    }

    internal static void OnCase(string stopId, CaseInfo info)
    {
        var h = CaseAnswered;
        if (h != null) { try { h(stopId, info); } catch (Exception e) { Debug.LogException(e); } }
    }

    public static void NotifyBreakStart(string stopId)
    {
        if (ScavengerHost.Authority) ScavengerHost.BreakStart(Me, stopId);
        else Net?.ScavTellBreakStart(stopId);
    }

    public static void RequestBreakFinish(string stopId, int misses)
    {
        if (ScavengerHost.Authority)
        {
            var result = ScavengerHost.BreakFinish(Me, stopId, misses, HotHeld);
            OnBreakIn(result);
            if (result.outcome == BreakInOutcome.Caught || result.outcome == BreakInOutcome.Spotted)
            {
                if (HandsNet.Instance != null) HandsNet.Instance.ScavAlarm(Me, stopId);
                else if (ScavHouses.TryDoor(stopId, out var door)) PlayAlarm(door, "", Me);
            }
        }
        else if (Net != null) Net.ScavAskBreakFinish(stopId, misses, HotHeld);
        else OnBreakIn(new BreakInResult { outcome = BreakInOutcome.Denied, stopId = stopId, why = "No connection to the Town.", lootJson = "" });
    }

    /// <summary>The host's verdict reaches the burglar's own machine: the goods go into this business's keep crate (hot),
    /// today's theft is counted for the midnight Trust review, and the letter is filed.</summary>
    internal static void OnBreakIn(BreakInResult result)
    {
        var got = new List<ItemInstance>();
        if (!string.IsNullOrEmpty(result.lootJson) && result.lootJson.Length <= 8000 &&
            (result.outcome == BreakInOutcome.Clean || result.outcome == BreakInOutcome.Spotted))
        {
            ScavLoot loot = null;
            try { loot = JsonUtility.FromJson<ScavLoot>(result.lootJson); } catch (Exception) { }
            int room = Mathf.Max(0, ScavengerRules.SackCapacity(2) - HotHeld); // never more than the deepest sack, whatever arrives
            if (loot != null && loot.items != null)
                foreach (var item in loot.items)
                {
                    if (got.Count >= room || item == null || !EbaeItemValidation.IsPlausible(item, out _) || Owns(item.uid)) continue;
                    item.hot = true;
                    GameSession.AddToInventory(item);
                    got.Add(item);
                }
        }
        if (!string.IsNullOrEmpty(result.districtId) && result.outcome != BreakInOutcome.Denied && result.outcome != BreakInOutcome.Fumbled)
        {
            bool seen = result.outcome == BreakInOutcome.Caught || result.outcome == BreakInOutcome.Spotted;
            DailyCount.Add(seen ? ScavengerIds.WitnessedToday(result.districtId) : ScavengerIds.UnwitnessedToday(result.districtId), GameClock.Day);
        }
        if (!string.IsNullOrEmpty(result.letterBody))
        {
            if (ScavengerHost.Authority) NeighborLetters.Send(RulesBooks.Active, result.letterSubject, result.letterBody);
            else HeadOffice.Send(result.letterSubject, result.letterBody, true);
        }
        var h = BreakInAnswered;
        if (h != null) { try { h(result, got); } catch (Exception e) { Debug.LogException(e); } }
    }

    static bool Owns(string uid)
    {
        foreach (var it in GameSession.Inventory) if (it != null && it.uid == uid) return true;
        return false;
    }

    /// <summary>Everybody near a door where a burglar was seen (presentation only; decided on the host).</summary>
    internal static void PlayAlarm(Vector3 door, string who, ulong burglar)
    {
        var cam = Camera.main;
        if (cam == null || (cam.transform.position - door).sqrMagnitude > 70f * 70f) return;
        bool mine = burglar == Me;
        GameHUD.FloatingText(door + Vector3.up * 2.4f, mine ? "SEEN!" : string.IsNullOrEmpty(who) ? "BURGLAR!" : "BURGLAR! (" + who + ")", new Color(1f, 0.3f, 0.25f), 1.5f);
        Sfx.Play("car_alarm", door, 0.7f, 1.15f);
        Sfx.Play("bark", door, 0.6f, 1.1f);
        Fx.Burst(door + Vector3.up * 1.4f, new Color(1f, 0.85f, 0.3f), 14, 4f);
    }

    // ------------------------------------------------------------------ the fence

    static readonly HashSet<string> selling = new HashSet<string>(StringComparer.Ordinal);
    public static bool IsSelling(string uid) => uid != null && selling.Contains(uid);

    public static void RequestFenceSell(ItemInstance item)
    {
        if (item == null || !item.hot || string.IsNullOrEmpty(item.uid) || !selling.Add(item.uid)) return;
        if (ScavengerHost.Authority) OnFence(ScavengerHost.FenceSell(Me, item));
        else if (Net != null) Net.ScavAskFence(JsonUtility.ToJson(item));
        else OnFence(new FenceResult { uid = item.uid, why = "No connection to the Town." });
    }

    internal static void OnFence(FenceResult result)
    {
        result.uid ??= ""; result.why ??= "";
        selling.Remove(result.uid);
        if (result.ok)
        {
            ItemInstance item = null;
            foreach (var it in GameSession.Inventory) if (it != null && it.uid == result.uid && it.hot) { item = it; break; }
            if (item != null) GameSession.RemoveFromInventory(item.uid);
        }
        Raise(FenceAnswered, result);
    }

    // ------------------------------------------------------------------ the day rolls over

    static int tickedDay = int.MinValue;

    /// <summary>
    /// Call now and then while in a game. On a new in-game day: the host fades the Town's suspicion, and THIS business's
    /// thefts of the day that ended come off its district Trust (Trust only ever moves at midnight). Each machine does
    /// its own business; taking the counters makes it happen exactly once.
    /// </summary>
    public static void TickDay()
    {
        int today = GameClock.Day;
        if (today == tickedDay) return;
        tickedDay = today;
        if (ScavengerHost.Authority) ScavengerHost.HostDayTick();
        foreach (var d in Districts.All)
        {
            int witnessed = TakeEarlierDay(ScavengerIds.WitnessedToday(d.id), today), unwitnessed = TakeEarlierDay(ScavengerIds.UnwitnessedToday(d.id), today);
            float penalty = ScavengerRules.TrustPenalty(witnessed, unwitnessed);
            if (penalty <= 0f) continue;
            float before = Progression.Trust01(d);
            GameSession.SetDistrictTrust(d.id, before - penalty);
            int points = Mathf.RoundToInt((before - Progression.Trust01(d)) * 100f);
            HeadOffice.Send("Trust report: " + d.name,
                $"Word got round {d.name} about yesterday ({witnessed} seen, {unwitnessed} \"things went missing\"). Trust is down {points} points. Fewer houses will put bags out for you until you earn it back.", false);
        }
    }

    /// <summary>The count of a packed (day, count) counter if it is from a day before <paramref name="today"/>, zeroing it.</summary>
    static int TakeEarlierDay(string key, int today)
    {
        int packed = GameSession.GetCounter(key);
        if (packed == 0 || (packed >> 10) == today) return 0;
        GameSession.SetCounter(key, 0);
        return packed & 1023;
    }

    /// <summary>The local player left the game: forget everything that only made sense inside that session.</summary>
    internal static void ResetSession()
    {
        peeks.Clear(); nextAsk.Clear(); calledHere.Clear(); selling.Clear(); tickedDay = int.MinValue;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        peeks.Clear(); nextAsk.Clear(); calledHere.Clear(); selling.Clear(); tickedDay = int.MinValue;
        CaseAnswered = null; BreakInAnswered = null; CallAnswered = null; FenceAnswered = null;
    }
}
