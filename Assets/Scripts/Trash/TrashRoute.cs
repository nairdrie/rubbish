using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Scene singleton (Neighborhood): the LIVE ROUTE PLANNER over random curb trash. Owned by the Trash system.
/// FROZEN CONTRACT: Current, routeName, Stops, StopsTotal, StopsCleared, NextStop(from), IsComplete.
///   Stops        = every house's PickupStop (all houses are on the route now).
///   StopsTotal   = houses in the neighborhood; StopsCleared = houses whose curb is clean right now;
///   StopsRemaining = houses with trash out; IsComplete = no trash at any curb (HUD points at the depot).
///   NextStop(from) = the best house to hit next: urgency (bags close to going late or already late score
///                    highest, more bags = more worth it, a clean curb earns a bonus) divided by distance, with
///                    hysteresis so the arrow doesn't flicker, and it sticks to the stop you're working at.
///   PlannedRoute(from, n) = the next n stops as a greedy chain (from → best → best-from-there …).
///   TotalCurbBags / LateBags / NeglectedStops / OldestBagAgeHours for the HUD route panel.
/// The HUD arrow, the stop beams and the bigger "next" arrows all point at NextStop.
/// Districts: Stops holds every house in the scene (all districts, so the spawner knows them all), but the counters
/// only count houses in UNLOCKED districts (locked ones never have trash). A curb's appeal is scaled by its district's
/// pay (CurbTrash.PayWeight: ×1 in the starter suburb, so its routing is unchanged; ≈ ×1.9 / ×3.7 / ×5.5 in the richer
/// districts), so a Butler Drop across town can outrank a lone bag next door.
/// </summary>
public class TrashRoute : MonoBehaviour
{
    public static TrashRoute Current { get; private set; }

    public string routeName = "Route 1 - Maple & Oak";
    [Tooltip("Seconds after load before the route toast (lets the HUD/scene settle).")]
    public float announceDelay = 1.2f;

    [Header("Planner")]
    [Tooltip("Distance (m) at which a stop's appeal halves.")]
    public float distanceHalfValue = 45f;
    [Tooltip("Another stop must score this much better (fraction) before the arrow switches to it.")]
    public float switchMargin = 0.25f;
    [Tooltip("While you're this close (m) to the current next stop's pile and it still has trash, the arrow stays on it.")]
    public float stickyRadius = 14f;

    readonly List<PickupStop> stops = new List<PickupStop>();
    public IReadOnlyList<PickupStop> Stops => stops;

    // ---------------- frozen counters (living-world meaning) ----------------
    /// <summary>Houses we serve: every house in an unlocked district (every house has a curb).</summary>
    public int StopsTotal
    {
        get { int n = 0; foreach (var s in stops) if (s != null && s.DistrictUnlocked) n++; return n; }
    }
    /// <summary>Houses (in unlocked districts) whose curb is clean right now.</summary>
    public int StopsCleared
    {
        get { int n = 0; foreach (var s in stops) if (s != null && s.IsCleared && s.DistrictUnlocked) n++; return n; }
    }
    /// <summary>Houses with trash out right now.</summary>
    public int StopsRemaining => Mathf.Max(0, StopsTotal - StopsCleared);
    /// <summary>No trash at any curb in town (the HUD sends you to the depot).</summary>
    public bool IsComplete => stops.Count > 0 && GameSession.CurbBags.Count == 0;

    // ---------------- live numbers ----------------
    /// <summary>Bags sitting at curbs across the whole town.</summary>
    public int TotalCurbBags => GameSession.CurbBags.Count;
    /// <summary>Of those, bags past the on-time window (paying less every hour).</summary>
    public int LateBags => CurbTrash.LateBagCount;
    /// <summary>Houses with a neglected (stinky, Pile Goblin-prone) pile.</summary>
    public int NeglectedStops => CurbTrash.NeglectedStopCount;
    /// <summary>Oldest bag at any curb, in in-game hours (0 when clean).</summary>
    public double OldestBagAgeHours => CurbTrash.OldestAgeHours;
    /// <summary>The truck can't fit another typical bag: time to dump at the depot.</summary>
    public static bool TruckNeedsDump => GameSession.TruckCapacityKg - GameSession.TruckLoadKg < 4.5f;

    /// <summary>Short route name for toasts ("Route 1" from "Route 1 - Maple &amp; Oak").</summary>
    public string ShortName
    {
        get
        {
            if (string.IsNullOrEmpty(routeName)) return "Route";
            int i = routeName.IndexOf(" - ", System.StringComparison.Ordinal);
            if (i <= 0) i = routeName.IndexOf(" — ", System.StringComparison.Ordinal); // builder uses an em dash
            return i > 0 ? routeName.Substring(0, i) : routeName;
        }
    }

    protected virtual void Awake()
    {
        Current = this;
        Refresh();
    }

    protected virtual void Start()
    {
        StartCoroutine(Announce(announceDelay));
    }

    protected virtual void OnDestroy()
    {
        if (Current == this) Current = null;
    }

    /// <summary>Re-scan the scene for PickupStops (every house) and hand them to the trash spawner.</summary>
    public void Refresh()
    {
        stops.Clear();
        foreach (var s in FindObjectsByType<PickupStop>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            stops.Add(s);
        // Stable order (HUD lists etc): by stopId.
        stops.Sort((a, b) => string.CompareOrdinal(a.stopId, b.stopId));
        if (Application.isPlaying) CurbTrashSpawner.RegisterSceneHouses(stops);
        lastNext = null;
        cacheFrame = -1;
    }

    IEnumerator Announce(float delay)
    {
        if (delay > 0f) yield return new WaitForSeconds(delay);
        if (StopsTotal == 0) yield break;
        string name = ShortName;
        int bags = TotalCurbBags, houses = StopsRemaining, late = LateBags;
        if (bags == 0)
        {
            GameHUD.Toast($"{name}: streets are clean! Folks put trash out through the day.", ToastStyle.Info, 3.5f);
        }
        else
        {
            string lateTxt = late > 0 ? $" ({late} late!)" : "";
            GameHUD.Toast($"{name}: {bags} bag{(bags == 1 ? "" : "s")} out at {houses} house{(houses == 1 ? "" : "s")}{lateTxt}. Follow the arrow!", late > 0 ? ToastStyle.Bad : ToastStyle.Info, 3.5f);
        }
        Sfx.Play("notify", null, 0.8f, 1f);
    }

    // =====================================================================
    // Planner
    // =====================================================================

    PickupStop lastNext;
    int cacheFrame = -1;
    Vector3 cacheFrom;
    PickupStop cacheNext;

    /// <summary>Planner score of a stop from a position (0 when its curb is clean). Higher = go there first.</summary>
    public float Score(PickupStop s, Vector3 from)
    {
        if (s == null) return 0f;
        var st = CurbTrash.StatsFor(s.stopId);
        if (st.bags <= 0) return 0f;
        Vector3 d = s.PileCenter - from; d.y = 0f;
        float value = (st.urgency + CurbTrash.CurbCleanAppeal) * CurbTrash.PayWeight(s.District);
        return value / (1f + d.magnitude / Mathf.Max(1f, distanceHalfValue));
    }

    /// <summary>
    /// Best stop to hit next from a world position (usually the player or their truck), or null when every
    /// curb is clean. Stable: keeps the current target while you're working it and only switches when another
    /// stop is clearly better.
    /// </summary>
    public PickupStop NextStop(Vector3 from)
    {
        if (cacheFrame == Time.frameCount && (from - cacheFrom).sqrMagnitude < 1f) return cacheNext;

        PickupStop best = BestFrom(from, null, out float bestScore);
        if (lastNext != null && lastNext != best && lastNext.HasTrash)
        {
            Vector3 d = lastNext.PileCenter - from; d.y = 0f;
            float cur = Score(lastNext, from);
            if (d.sqrMagnitude < stickyRadius * stickyRadius || cur * (1f + switchMargin) >= bestScore) best = lastNext;
        }
        lastNext = best;
        cacheFrame = Time.frameCount; cacheFrom = from; cacheNext = best;
        return best;
    }

    PickupStop BestFrom(Vector3 from, List<PickupStop> exclude, out float bestScore)
    {
        PickupStop best = null; bestScore = 0f;
        foreach (var s in stops)
        {
            if (s == null || (exclude != null && exclude.Contains(s))) continue;
            float sc = Score(s, from);
            if (sc > bestScore) { bestScore = sc; best = s; }
        }
        return best;
    }

    /// <summary>The next n stops as a greedy chain starting with NextStop(from). New list (see the non-alloc overload).</summary>
    public List<PickupStop> PlannedRoute(Vector3 from, int n)
    {
        var list = new List<PickupStop>(Mathf.Max(0, n));
        PlannedRoute(from, n, list);
        return list;
    }

    /// <summary>Fills <paramref name="into"/> with the next n stops (greedy chain from NextStop(from)).</summary>
    public void PlannedRoute(Vector3 from, int n, List<PickupStop> into)
    {
        into.Clear();
        if (n <= 0) return;
        var first = NextStop(from);
        if (first == null) return;
        into.Add(first);
        Vector3 at = first.PileCenter;
        while (into.Count < n)
        {
            var next = BestFrom(at, into, out _);
            if (next == null) break;
            into.Add(next);
            at = next.PileCenter;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Current = null;
}
