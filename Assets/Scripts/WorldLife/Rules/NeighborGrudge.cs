using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// WorldLife package E (Docs/WorldLife.md §9.1): the grudge, the lateness consequence. Flatten a resident or shunt their
/// car and, until the next midnight, that house's bags pay as if <see cref="WorldLifeTuning.Fines.GrudgeExtraHours"/>
/// in-game hours older (fee × PayFraction(age + 1)) and the on-time tip is void (W0's
/// <see cref="CurbTrash.ExtraAgeHours"/> hook). Clear every bag at that curb and you get "Apology accepted", which clears
/// it (no Rep, so it can't be farmed). The household glares at your truck meanwhile instead of waving (RulesDirector).
///
/// Stored per company as a GameSession stamp (<see cref="WorldLifeIds.GrudgeStamp"/> = the until-hours) until M1's stores
/// convert timers to remaining hours (rule 9). Written on the host only (rules 15, 16); reading works anywhere, including
/// the garage, so a grudged bag collected later still pays less.
/// </summary>
public static class NeighborGrudge
{
    /// <summary>A house got a grudge (stopId).</summary>
    public static event Action<string> Added;
    /// <summary>A grudge was cleared by clearing the curb (stopId, curb position).</summary>
    public static event Action<string, Vector3> Forgiven;

    public static int AddedCount { get; private set; }
    public static int ForgivenCount { get; private set; }

    static readonly Dictionary<string, string> keys = new Dictionary<string, string>(96, StringComparer.Ordinal);

    /// <summary>The stamp key for a stopId, cached so per-frame reads (the glare) don't allocate.</summary>
    public static string Key(string stopId)
    {
        if (!keys.TryGetValue(stopId, out var k)) { k = WorldLifeIds.GrudgeStamp(stopId); keys[stopId] = k; }
        return k;
    }

    // ------------------------------------------------------------------ queries

    public static bool Has(string stopId) => Has(stopId, RulesBooks.Active);

    /// <summary>Does this house hold a grudge right now? (A stamp more than a day ahead is stale and ignored.)</summary>
    public static bool Has(string stopId, IRulesBooks b)
    {
        if (string.IsNullOrEmpty(stopId) || b == null) return false;
        double until = b.GetStamp(Key(stopId));
        if (until <= 0) return false;
        double left = until - b.Hours;
        return left > 0 && left <= RulesTuning.GrudgeMaxHours + 1e-6;
    }

    /// <summary>In-game hours left on a grudge (0 when none).</summary>
    public static double HoursLeft(string stopId, IRulesBooks b) => Has(stopId, b) ? b.GetStamp(Key(stopId)) - b.Hours : 0;

    /// <summary>The grudge lasts until the next midnight.</summary>
    public static double UntilFor(double nowHours) => (Math.Floor(nowHours / 24.0) + 1.0) * 24.0;

    /// <summary>The CurbTrash pay hook: extra hours of age for this house's bags (0 when no grudge).</summary>
    public static float ExtraHours(string stopId) => Has(stopId, RulesBooks.Active) ? WorldLifeTuning.Fines.GrudgeExtraHours : 0f;

    // ------------------------------------------------------------------ writes (host)

    /// <summary>Put a grudge on a house until the next midnight (host only; a second incident just keeps it).</summary>
    public static bool Add(string stopId, IRulesBooks b)
    {
        if (string.IsNullOrEmpty(stopId) || b == null || !b.IsHost) return false;
        b.SetStamp(Key(stopId), UntilFor(b.Hours));
        AddedCount++;
        var h = Added;
        if (h != null) { try { h(stopId); } catch (Exception e) { Debug.LogException(e); } }
        return true;
    }

    /// <summary>Drop a grudge (host only). True when there was one.</summary>
    public static bool Clear(string stopId, IRulesBooks b)
    {
        if (b == null || !b.IsHost || !Has(stopId, b)) return false;
        b.SetStamp(Key(stopId), 0);
        return true;
    }

    // ------------------------------------------------------------------ boot: the pay hook + redemption

    /// <summary>Idempotent: the CurbTrash pay hook and the "Apology accepted" listener (static; works in any scene).</summary>
    public static void Boot()
    {
        CurbTrash.ExtraAgeHours = ExtraHours;
        ProgressionEvents.CurbCleared -= OnCurbCleared;
        ProgressionEvents.CurbCleared += OnCurbCleared;
    }

    /// <summary>The house's last curb bag went into the truck: a grudged house forgives you.</summary>
    static void OnCurbCleared(string stopId, string districtId, Vector3 pos)
    {
        var b = RulesBooks.Active;
        if (!Clear(stopId, b)) return;
        ForgivenCount++;
        var house = CurbTrashSpawner.FindHouse(stopId);
        string where = house != null && !string.IsNullOrEmpty(house.address) ? house.address : stopId;
        if (CurbTrash.ActorFeedbackActive)
        {
            GameHUD.FloatingText(pos + Vector3.up * 2.1f, "Apology accepted!", new Color(0.55f, 1f, 0.6f), 1.15f);
            GameHUD.Toast($"Apology accepted: {where} forgives you.", ToastStyle.Good, 3f);
            Sfx.Play("ding", pos, 0.6f, 1.25f);
        }
        var h = Forgiven;
        if (h != null) { try { h(stopId, pos); } catch (Exception e) { Debug.LogException(e); } }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { Added = null; Forgiven = null; AddedCount = ForgivenCount = 0; }
}
