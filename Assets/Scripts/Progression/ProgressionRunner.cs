using System;
using UnityEngine;

/// <summary>
/// Hidden DontDestroyOnLoad host for progression (play mode only): drives the <see cref="ProgressionHud"/>, keeps gnome
/// timers honest, announces the day's Work Orders at dawn, and runs the midnight fallback — when the day rolls over and
/// D's <c>CityCleanliness.MidnightInspection</c> hasn't raised <see cref="ProgressionEvents.Midnight"/> within a few
/// seconds, it runs the review from a synthesized report so stats, Work Orders, Clean Days and promotions never stall.
/// </summary>
[DefaultExecutionOrder(-40)]
public sealed class ProgressionRunner : MonoBehaviour
{
    public const float MidnightFallbackSeconds = 5f;

    static ProgressionRunner instance;
    readonly ProgressionHud hud = new ProgressionHud();
    float fallbackAt = -1f, nextGnomeTick, nextDayCheck, nextSeveranceCheck;

    public static ProgressionRunner Instance => instance;
    public ProgressionHud Hud => hud;

    public static void Ensure()
    {
        if (instance != null || !Application.isPlaying) return;
        var go = new GameObject("[Progression]");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<ProgressionRunner>();
    }

    void Awake()
    {
        if (instance != null && instance != this) { Destroy(gameObject); return; }
        instance = this;
        try { hud.Build(transform); } catch (Exception e) { Debug.LogException(e); }

        ProgressionEvents.RankChanged += OnRankChanged;
        GameSession.RepChanged += OnRepChanged;
        WorkOrders.Completed += OnWorkOrderDone;
        HeadOffice.LetterSent += OnLetter;
        Progression.KeyToCityGranted += OnKey;
        Progression.DistrictOpened += OnDistrictOpened;
        Progression.MidnightReviewed += OnMidnightReviewed;
        GameClock.MorningCame += OnMorning;
    }

    void OnDestroy()
    {
        ProgressionEvents.RankChanged -= OnRankChanged;
        GameSession.RepChanged -= OnRepChanged;
        WorkOrders.Completed -= OnWorkOrderDone;
        HeadOffice.LetterSent -= OnLetter;
        Progression.KeyToCityGranted -= OnKey;
        Progression.DistrictOpened -= OnDistrictOpened;
        Progression.MidnightReviewed -= OnMidnightReviewed;
        GameClock.MorningCame -= OnMorning;
        hud.Destroy();
        if (instance == this) instance = null;
    }

    // ------------------------------------------------------------------ event → presentation

    void OnRankChanged(int oldRank, int newRank)
    {
        hud.ShowPromotion(oldRank, newRank);
        int unlocks = Progression.UnlockCountBetween(oldRank, newRank);
        if (unlocks > 0) GameHUD.Toast($"New on {Scrapazon.Name}: {unlocks} item{(unlocks == 1 ? "" : "s")}", ToastStyle.Rare, 3.5f);
    }

    void OnRepChanged(float delta, string repId, string districtId, Vector3? pos) => hud.OnRep(delta, pos);

    void OnWorkOrderDone(WorkOrder w, float pay, float rep)
    {
        hud.ShowWorkOrderDone(w, pay, rep); // the stamp card shows the reward; no extra toast
    }

    void OnLetter(HeadOfficeLetter l) => hud.ShowLetter(l);
    void OnKey() => hud.ShowKeyToCity();
    void OnDistrictOpened(DistrictDef d) => hud.ShowPermit(d);

    void OnMidnightReviewed(bool cleanDay, float rep, MidnightReport r)
    {
        string s = $"Midnight review: {(rep >= 0 ? "+" : "")}{Mathf.RoundToInt(rep):N0} Rep";
        s += cleanDay ? "  ·  CLEAN DAY!" : "  ·  not a Clean Day (top district < 70% or < 5 bags)";
        GameHUD.Toast(s, cleanDay ? ToastStyle.Good : ToastStyle.Info, 4f);
        if (cleanDay) Sfx.Play("sparkle", null, 0.7f, 1.15f);
    }

    void OnMorning()
    {
        if (GameSession.IsGuest) return;
        var wos = WorkOrders.Today;
        if (wos.Count == 0) return;
        var sb = new System.Text.StringBuilder("Today's Work Orders: ");
        for (int i = 0; i < wos.Count; i++) sb.Append(i == 0 ? "" : "  ·  ").Append(wos[i].Text);
        GameHUD.Toast(sb.ToString(), ToastStyle.Info, 5f);
    }

    // ------------------------------------------------------------------ per frame

    void Update()
    {
        float now = Time.unscaledTime;
        bool host = !GameSession.IsGuest;

        // midnight fallback: the day changed but no Midnight fact arrived
        if (host && Progression.SessionLive && now >= nextDayCheck)
        {
            nextDayCheck = now + 1f;
            if (Progression.IsBooted && GameSession.GetCounter("stats_day") != Progression.Today && !Progression.MidnightDoneToday && fallbackAt < 0f)
                fallbackAt = now + MidnightFallbackSeconds;
        }
        if (fallbackAt > 0f && now >= fallbackAt)
        {
            fallbackAt = -1f;
            if (!Progression.MidnightDoneToday)
            {
                try
                {
                    Debug.Log("[Progression] No Midnight report from the Trash system; running the review from city-wide cleanliness.");
                    Progression.OnMidnight(Progression.SynthesizeMidnight());
                }
                catch (Exception e) { Debug.LogException(e); }
            }
        }

        // One-time note for a business that owned Kevin's Crew (refunded while its save loaded): any machine, own business.
        if (Progression.SessionLive && now >= nextSeveranceCheck)
        {
            nextSeveranceCheck = now + 1.5f;
            try { KevinSeverance.TryTell(); } catch (Exception e) { Debug.LogException(e); }
        }

        if (host && now >= nextGnomeTick)
        {
            nextGnomeTick = now + 2f;
            try { Progression.TickGnomes(); } catch (Exception e) { Debug.LogException(e); }
        }

        try { hud.Tick(Time.unscaledDeltaTime); } catch (Exception e) { Debug.LogException(e); }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => instance = null;
}
