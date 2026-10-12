using UnityEditor;
using UnityEngine;

/// <summary>Play-mode shortcuts for trying the campaign contracts (Docs/Campaign.md §14) without a 24-hour hold.</summary>
public static class CampaignDebug
{
    const string Menu = "Rubbish/Debug/Campaign/";

    static bool Ready(out CampaignContract active)
    {
        active = null;
        if (!Application.isPlaying) { Debug.LogWarning("[Campaign] Play mode only."); return false; }
        if (!Businesses.IsAuthority) { Debug.LogWarning("[Campaign] Only the host runs contracts."); return false; }
        active = Campaign.Active;
        return true;
    }

    [MenuItem(Menu + "Win The Running Contract Now")]
    static void Win()
    {
        if (!Ready(out var c)) return;
        if (c == null) { Debug.Log("[Campaign] No contract is running (slice 1 ends at Coupon Commons)."); return; }
        Campaign.DebugWinActive();
    }

    [MenuItem(Menu + "Clear The Old Trash")]
    static void Clear()
    {
        if (!Ready(out var c) || c == null) return;
        Campaign.DebugClearBacklog(c.districtId);
        Debug.Log($"[Campaign] The old trash in {c.District.name} is gone. Keep it clean for {CampaignTuning.HoldHours:0} game hours.");
    }

    [MenuItem(Menu + "Put The Old Trash Back (restart the contract)")]
    static void Reseed()
    {
        if (!Ready(out var c) || c == null) return;
        Campaign.DebugReseed(c.districtId);
    }

    [MenuItem(Menu + "Log The Contract")]
    static void Log()
    {
        if (!Ready(out var c)) return;
        var d = Campaign.Display;
        Debug.Log(c == null
            ? "[Campaign] No contract running."
            : $"[Campaign] {c.District.name}: {d.backlog}/{d.backlogStart} old bags left, clean {d.cleanPct}%, hold {Campaign.HoldHours:0.0}/{CampaignTuning.HoldHours:0} h, attempts {Campaign.Attempts}. Opens {c.Unlocks.name}.");
    }

    [MenuItem(Menu + "Take Back The Meadows And Commons Permits (retest from the start)")]
    static void TakeBack()
    {
        if (!Ready(out _)) return;
        foreach (var c in Campaign.Contracts)
        {
            GameSession.SetUpgradeLevel(c.Unlocks.permitId, 0);
            GameSession.SetCounter(Campaign.WonKey + c.districtId, 0);
            GameSession.SetCounter(Campaign.BacklogKey + c.districtId, 0);
            GameSession.SetCounter(Campaign.IntroKey + c.districtId, 0);
        }
        Debug.Log("[Campaign] Permits and contract counters cleared. Leave and re-enter the session so the Acres' heaps go out again.");
    }
}
