using System;
using UnityEngine;

/// <summary>A one-time first-upgrade grant for a new business. Actual paid curb pickups count, not treasure luck.</summary>
public static class StarterCollectionContract
{
    public const string ProgressKey = "starter_collection_progress";
    public const string LedgerId = "starter_collection";
    public const int RequiredBags = 8;
    public const int TargetBags = RequiredBags; // save-migration contract
    public const float Reward = 100f;
    public const string RewardReason = "Starter collection contract: 8 curb bags";

    // Eight is terminal (already paid, or an established pre-contract save). Never reset it at midnight or on joining.
    public static int SanitizeProgress(int value) => Mathf.Clamp(value, 0, RequiredBags);
    public static int LocalProgress => SanitizeProgress(GameSession.GetCounter(ProgressKey));
    public static bool Complete => LocalProgress >= RequiredBags;
    public static int MergeProgress(int remembered, int saved) => Mathf.Max(SanitizeProgress(remembered), SanitizeProgress(saved));
    public static bool IsReservedLedgerId(string ledgerId) => ledgerId == LedgerId;
    public static bool IsRewardCredit(string kind, string ledgerId, long cents) =>
        kind == Ledger.KindCredit && IsReservedLedgerId(ledgerId) && cents == (long)(Reward * 100f);
    public static string ProgressText(int progress) =>
        $"{SanitizeProgress(progress)}/{RequiredBags} · {(SanitizeProgress(progress) >= RequiredBags ? "COMPLETE" : "+" + Economy.Money(Reward))}";

    /// <summary>Returns the next count and the one edge that earns the grant. Client-authored ledger requests cannot advance it.</summary>
    public static int Advance(int current, string kind, string ledgerId, long cents, bool authoritativePickup, out bool award)
    {
        int before = SanitizeProgress(current);
        int after = authoritativePickup && kind == Ledger.KindCredit && ledgerId == LedgerIds.PickupFee && cents > 0
            ? Mathf.Min(RequiredBags, before + 1) : before;
        award = before < RequiredBags && after == RequiredBags;
        return after;
    }

    public static bool SnapshotMatches(string localBusiness, string messageBusiness, int progress) =>
        !string.IsNullOrEmpty(localBusiness) && string.Equals(localBusiness, messageBusiness, StringComparison.Ordinal) &&
        progress >= 0 && progress <= RequiredBags;

    public static void SaveLocalProgress(int progress, bool announceStart = false)
    {
        int before = LocalProgress;
        int next = MergeProgress(before, progress);
        if (next == before) return;
        GameSession.SetCounter(ProgressKey, next);
        if (announceStart && before == 0 && next == 1)
            GameHUD.Toast($"Starter contract: 1/{RequiredBags} curb bags collected for your business. Earn {Economy.Money(Reward)} for your first upgrade!", ToastStyle.Good, 6f);
    }

    public static void ShowCompleted() => GameHUD.Toast(
        $"STARTER CONTRACT COMPLETE! +{Economy.Money(Reward)} to your business. Hopper sideboards cost $100 on Scrapazon.", ToastStyle.Good, 7f);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void HookOfflineFallback()
    {
        Ledger.PostedAnyBusiness -= OnOfflinePosted;
        Ledger.PostedAnyBusiness += OnOfflinePosted;
    }

    // Solo normally has a host registry too. This also covers the legacy offline shop/route scenes without paying twice.
    static void OnOfflinePosted(LedgerEntry entry)
    {
        if (Businesses.Registry != null || GameSession.IsConnectedGuest || entry.businessId != GameSession.BusinessId) return;
        int next = Advance(LocalProgress, entry.kind, entry.id, entry.cents, true, out bool award);
        SaveLocalProgress(next, announceStart: true);
        if (!award) return;
        using (Ledger.ToBusiness(GameSession.BusinessId)) Ledger.Credit(Reward, RewardReason, LedgerId);
        ShowCompleted();
    }
}
