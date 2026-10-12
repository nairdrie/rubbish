using UnityEngine;

/// <summary>
/// What every machine tells its player about the contract as it happens (host and guests alike: it reads only the replicated
/// <see cref="CampaignDisplay"/>): the old trash is gone, the hold starts, it is halfway, one hour to go, it is lost. The bar and
/// its countdown (FeelHudCity) are the steady view; these are the moments, kept few and short.
/// </summary>
public static class ContractFeedback
{
    static CampaignDisplay prev = CampaignDisplay.None;
    static bool init, halfwayShown, hourShown;

    /// <summary>The host's tick or a guest's copy changed the display.</summary>
    public static void OnDisplay(CampaignDisplay now)
    {
        if (!init) { init = true; prev = now; return; }          // a screen that starts mid-contract announces nothing
        if (now.Active && prev.Active && now.district == prev.district)
        {
            string name = Campaign.HudName(Districts.All[Mathf.Clamp(now.district, 0, Districts.All.Count - 1)]);
            if (prev.backlog > 0 && now.backlog == 0)
            {
                GameHUD.Toast($"ALL THE OLD TRASH IS GONE! Get {name} over the {CampaignTuning.ThresholdPct}% line and HOLD it for {CampaignTuning.HoldHours:0} hours.", ToastStyle.Good, 5f);
                Sfx.Play("sparkle", null, 0.8f, 1f);
            }
            if (!prev.Holding && now.Holding)
            {
                halfwayShown = hourShown = false;
                GameHUD.Toast($"HOLD STARTED! Stay over {CampaignTuning.ThresholdPct}% for {CampaignRules.FormatLeft(CampaignTuning.HoldHours)}. Dip under the line and it starts over.", ToastStyle.Good, 5f);
                Sfx.Play("ding", null, 0.9f, 1.1f);
            }
            if (now.lostSerial != prev.lostSerial)
            {
                double lost = now.lostTenths / 10.0;
                GameHUD.Toast(lost >= 1.0
                    ? $"HOLD LOST! {name} slipped under {CampaignTuning.ThresholdPct}% after {CampaignRules.FormatLeft(lost)}. The {CampaignTuning.HoldHours:0} hours start over."
                    : $"HOLD LOST! {name} slipped under {CampaignTuning.ThresholdPct}%. Back over the line to start again.", ToastStyle.Bad, 5f);
                Sfx.Play("buzz", null, 0.9f, 0.8f);
                Juice.Shake(0.15f, 0.25f);
            }
        }
        else if (!now.Active || now.district != prev.district) { halfwayShown = hourShown = false; }
        prev = now;
    }

    /// <summary>Every frame on every machine: the two countdown milestones, from the anchor and this machine's clock.</summary>
    public static void Tick(double nowHours)
    {
        if (!prev.Active || !prev.Holding) return;
        double left = prev.HoursLeftAt(nowHours);
        if (!halfwayShown && left <= CampaignTuning.HoldHours * 0.5)
        {
            halfwayShown = true;
            GameHUD.Toast($"HALFWAY! {CampaignRules.FormatLeft(left)} to go. Keep it up.", ToastStyle.Good, 3.5f);
        }
        if (!hourShown && left <= 1.0)
        {
            hourShown = true;
            GameHUD.Toast("ONE HOUR TO GO! Do not let a single bag go stale.", ToastStyle.Rare, 4f);
            Sfx.Play("notify", null, 0.9f, 1.2f);
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { prev = CampaignDisplay.None; init = halfwayShown = hourShown = false; }
}
