using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>One shared-town bill, divided between operating businesses, not charged to the host by accident.</summary>
public static class MunicipalEconomy
{
    public const string HoursKey = "municipal_operating_hours";
    public const float GraceHours = 48f, RookieHours = 120f;
    public const long RookieNightCapCents = 1000;
    public static float LocalHours => SanitizeHours((float)GameSession.GetStamp(HoursKey));
    public static float SanitizeHours(float h) => float.IsNaN(h) || float.IsInfinity(h) ? 0f : Mathf.Clamp(h, 0f, RookieHours);
    public static bool InGrace(float hours) => SanitizeHours(hours) < GraceHours;
    public static float FineMultiplier(float hours) => InGrace(hours) ? 0f : SanitizeHours(hours) < RookieHours ? 0.5f : 1f;
    public static long NightCap(float hours, int topTier) => SanitizeHours(hours) < RookieHours
        ? RookieNightCapCents : 2500L * Mathf.Clamp(topTier, 1, 4);

    /// <summary>Exact, deterministic cents: total shares always add to the original pool.</summary>
    public static long Share(long total, int count, int index) => total <= 0 || count <= 0 || index < 0 || index >= count
        ? 0 : total / count + (index < total % count ? 1 : 0);

    public static long FineShare(long pool, int count, int index, float hours, long alreadyCharged, int topTier)
    {
        long due = (long)Math.Round(Share(pool, count, index) * (double)FineMultiplier(hours), MidpointRounding.AwayFromZero);
        return Math.Min(due, Math.Max(0, NightCap(hours, topTier) - Math.Max(0, alreadyCharged)));
    }

    public static List<BusinessInfo> OperatingBusinesses()
    {
        var result = new List<BusinessInfo>();
        foreach (var b in Businesses.All)
            if (b.IsValid && !Payroll.IsEmployee(b.ownerClientId)) result.Add(b);
        result.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return result;
    }

    public static string LocalPolicyText
    {
        get
        {
            if (Payroll.TryGetLocalJob(out _))
                return "City inspection: your employer's business shares the town bill/bonus. Your own business is exempt while hired.";
            float h = LocalHours;
            string rule = InGrace(h) ? $"Startup grace: {Mathf.CeilToInt(GraceHours - h)} game hours left — no neglect fines."
                : h < RookieHours ? "Starter protection: half neglect fines, capped at $10 per midnight."
                : $"Neglect fines capped at {Economy.Money(NightCap(h, Districts.Top.tier) / 100f)} per midnight.";
            return rule + "\nMidnight city bonus/bill is split across operating businesses; hired crews count as one.";
        }
    }

    public static void SaveLocalHours(float hours)
    {
        float value = SanitizeHours(hours);
        if (Mathf.Abs(value - LocalHours) > 0.0001f) GameSession.SetStamp(HoursKey, value);
    }

    public static void ShowReceipt(string message, bool fine)
    {
        GameHUD.Toast(message, fine ? ToastStyle.Bad : ToastStyle.Info, 7f);
        // Keep an inspectable explanation after the toast has gone, including on guests.
        HeadOffice.Send("City inspection receipt", message, popup: false);
    }
}
