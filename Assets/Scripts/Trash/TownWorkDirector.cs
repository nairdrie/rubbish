using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Host-only, slowly changing workload budget. No special treatment of the host's whereabouts.</summary>
public static class TownWorkDirector
{
    public const float CollectionWindowHours = 4f, RampHours = 1.8f;
    public const float ActiveSoloSupplyMultiplier = 1.15f;
    public static float Scale { get; private set; } = 0.75f;
    public static float Target { get; private set; } = 0.75f;
    public static int ActiveCrews { get; private set; }
    public static int RecentCollections { get; private set; }
    public static float OutstandingKg { get; private set; }
    static readonly Queue<double> collections = new Queue<double>();
    static readonly Dictionary<ulong, Vector3> positions = new Dictionary<ulong, Vector3>();
    static readonly Dictionary<ulong, double> activeUntil = new Dictionary<ulong, double>();
    static double lastSample = -1;

    public static void Collected()
    {
        if (!Businesses.IsAuthority) return;
        double now = GameClock.TotalHours;
        collections.Enqueue(now);
        string id = Ledger.TargetBusiness ?? Businesses.LocalId;
        if (Businesses.TryGet(id, out var b)) activeUntil[b.ownerClientId] = now + CollectionWindowHours;
    }

    public static float TargetScale(int activeCrews, int recentCollections, float outstandingKg, int curbBags)
    {
        int crews = Mathf.Clamp(activeCrews, 1, 4);
        float pace = Mathf.Clamp01(Mathf.Max(0, recentCollections) / (3f * crews));
        float activity = activeCrews <= 0 ? 0.35f : Mathf.Lerp(0.65f, 1.2f, pace);
        float backlog = 1f / (1f + Mathf.Max(0, outstandingKg / crews - 35f) / 80f
                                + Mathf.Max(0, curbBags / (float)crews - 8f) / 20f);
        // Empty towns still offer work, even when everyone is browsing upgrades.
        if (curbBags < 4 * crews && outstandingKg < 20f * crews) activity = Mathf.Max(activity, 0.8f);
        return Mathf.Clamp(BusinessRates.PutOutScale(crews) * activity * Mathf.Max(0.2f, backlog), 0.15f, 3.36f);
    }

    /// <summary>A small solo pickup-work bonus, not heavier bags. Two players sharing one crew still use the co-op budget.
    /// Apply after the existing backlog curve so sorting backlogs and neglected streets still throttle proportionally.</summary>
    public static float TargetScaleForPlayers(int activeCrews, int recentCollections, float outstandingKg, int curbBags, int players)
    {
        float target = TargetScale(activeCrews, recentCollections, outstandingKg, curbBags);
        return players == 1 && activeCrews == 1 ? target * ActiveSoloSupplyMultiplier : target;
    }

    public static float Approach(float current, float target, float hours) =>
        Mathf.Lerp(current, target, 1f - Mathf.Exp(-Mathf.Max(0f, hours) / RampHours));

    public static int ScaledDropCount(int bags, float scale, float roll)
    {
        float expected = Mathf.Max(0, bags) * Mathf.Clamp(scale, 0f, 8f);
        int whole = Mathf.FloorToInt(expected);
        return whole + (Mathf.Clamp01(roll) < expected - whole ? 1 : 0);
    }

    public static void Tick()
    {
        if (!Businesses.IsAuthority) return;
        double now = GameClock.TotalHours;
        if (lastSample < 0) { lastSample = now; return; }
        double elapsed = now - lastSample;
        if (elapsed < 0) { BeginTown(); lastSample = now; return; }
        if (elapsed < 0.1) return; // one inexpensive world scan per ~5 real seconds
        lastSample = now;
        while (collections.Count > 0 && now - collections.Peek() > CollectionWindowHours) collections.Dequeue();
        RecentCollections = collections.Count;

        var crews = new HashSet<ulong>();
        var present = new HashSet<ulong>();
        foreach (var p in UnityEngine.Object.FindObjectsByType<PlayerController>(FindObjectsSortMode.None))
        {
            if (p == null) continue;
            ulong client = p.IsSpawned ? p.OwnerClientId : Permissions.LocalClientId;
            present.Add(client);
            Vector3 pos = p.transform.position;
            if (!positions.TryGetValue(client, out var old) || (pos - old).sqrMagnitude > 0.25f)
                activeUntil[client] = Math.Max(activeUntil.TryGetValue(client, out double prior) ? prior : 0, now + 1.5);
            positions[client] = pos;
            if (!activeUntil.TryGetValue(client, out double until) || until < now) continue;
            crews.Add(Payroll.TryGetJobOf(client, out var job) ? job.employerClientId : client);
        }
        foreach (var client in new List<ulong>(positions.Keys))
            if (!present.Contains(client)) { positions.Remove(client); activeUntil.Remove(client); }
        ActiveCrews = crews.Count;
        float kg = 0f;
        foreach (var b in Businesses.All)
        {
            if (!b.IsValid) continue;
            kg += Mathf.Max(0f, b.truckKg);
            if (b.lot >= 0) kg += DepotBacklog.UsedKg(b.lot);
        }
        if (!DepotRowWorld.Active) kg += DepotBacklog.UsedKg(-1);
        foreach (var bag in GameSession.DepotDeliveries) if (bag != null) kg += bag.TotalMassKg;
        OutstandingKg = kg;
        // Count connected people as well as loaded avatars: a joining/indoors/idle guest must not turn a co-op town into solo.
        int players = Mathf.Max(present.Count, SessionService.PlayerCount);
        Target = TargetScaleForPlayers(ActiveCrews, RecentCollections, kg, GameSession.CurbBags.Count, players);
        Scale = Approach(Scale, Target, (float)Math.Min(elapsed, 1.0));
        // Legacy offline scene support; a network registry accrues each business separately.
        if (Businesses.Registry == null) MunicipalEconomy.SaveLocalHours(MunicipalEconomy.LocalHours + (float)elapsed);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    public static void BeginTown()
    {
        collections.Clear(); positions.Clear(); activeUntil.Clear(); lastSample = -1;
        Scale = Target = 0.75f; ActiveCrews = RecentCollections = 0; OutstandingKg = 0;
    }
}
