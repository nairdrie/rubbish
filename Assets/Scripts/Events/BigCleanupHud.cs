using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>Creates the F5 event presentation once (HUD + Depot Row board); never in batchmode.</summary>
public static class CleanupBoot
{
    static GameObject root;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => root = null;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    public static void Ensure()
    {
        if (root != null || !Application.isPlaying || Application.isBatchMode) return;
        var c = UiKit.Canvas("[EventsHud]", FeelHud.SortingOrder + 2);
        root = c.gameObject;
        root.AddComponent<BigCleanupHud>().Init(c);
        root.AddComponent<CleanupBoard>();
    }
}

/// <summary>
/// F5 HUD (Docs/Multiplayer.md §14.5): the Big Cleanup banner + siren, a compact corner score with expandable standings, the
/// results card, and the "POACHED!" callout (<see cref="PoachingNet"/>). Reads only replicated state, so every screen shows
/// the same board. Its own overlay canvas just above the HUD.
/// </summary>
public static class BigCleanupPresentationRules
{
    /// <summary>Keep event cards out of first-person sorting, the laptop, maps, and menus.</summary>
    public static bool CanShow(HudMode mode, bool sorting, bool uiBlocked, bool transitioning) =>
        !sorting && !uiBlocked && !transitioning &&
        (mode == HudMode.Street || mode == HudMode.Driving || mode == HudMode.Depot);
}

public sealed class BigCleanupHud : MonoBehaviour
{
    static BigCleanupHud instance;

    Canvas canvas;
    RectTransform banner, board;
    CanvasGroup bannerGroup, boardGroup;
    TextMeshProUGUI bannerTitle, bannerSub, bannerCount, boardHead, boardRows;
    AudioSource siren;
    readonly List<CleanupScore> ranked = new List<CleanupScore>(4);
    float bannerPunch;
    CleanupPhase shown;
    int shownSerial = -1;
    float sirenUntil, nextBoardText;
    bool expanded;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => instance = null;

    public void Init(Canvas c)
    {
        instance = this;
        canvas = c;
        var root = (RectTransform)transform;

        // Banner: centre stage.
        banner = FeelUi.Rect("CleanupBanner", root, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -250f), new Vector2(760f, 190f));
        bannerGroup = banner.gameObject.AddComponent<CanvasGroup>();
        FeelUi.Panel(banner, new Color(0.13f, 0.38f, 0.16f, 0.96f), 0.6f);
        bannerTitle = FeelUi.Text(banner, "Title", 62f, new Color(1f, 0.92f, 0.3f), TextAlignmentOptions.Top);
        bannerTitle.rectTransform.offsetMax = new Vector2(0f, -10f);
        bannerSub = FeelUi.Text(banner, "Sub", 25f, Color.white, TextAlignmentOptions.Center, FontStyles.Bold);
        bannerSub.rectTransform.offsetMin = new Vector2(20f, 20f);
        bannerSub.rectTransform.offsetMax = new Vector2(-20f, -40f);
        bannerSub.textWrappingMode = TextWrappingModes.Normal;
        bannerCount = FeelUi.Text(banner, "Count", 30f, new Color(1f, 0.85f, 0.4f), TextAlignmentOptions.Bottom);
        bannerCount.rectTransform.offsetMin = new Vector2(0f, 12f);
        bannerGroup.alpha = 0f;

        // A compact corner score by default; L expands the full live standings upward from the same corner.
        board = FeelUi.Rect("CleanupBoard", root, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-24f, 120f), new Vector2(360f, 74f));
        boardGroup = board.gameObject.AddComponent<CanvasGroup>();
        FeelUi.Panel(board, new Color(0.1f, 0.12f, 0.1f, 0.9f), 0.5f);
        boardHead = FeelUi.Text(board, "Head", 22f, new Color(1f, 0.9f, 0.3f), TextAlignmentOptions.TopLeft);
        boardHead.rectTransform.offsetMin = new Vector2(16f, 0f);
        boardHead.rectTransform.offsetMax = new Vector2(-16f, -8f);
        boardRows = FeelUi.Text(board, "Rows", 18f, Color.white, TextAlignmentOptions.TopLeft, FontStyles.Normal);
        boardRows.rectTransform.offsetMin = new Vector2(16f, 8f);
        boardRows.rectTransform.offsetMax = new Vector2(-16f, -34f);
        boardRows.lineSpacing = 4f;
        boardGroup.alpha = 0f;

        siren = gameObject.AddComponent<AudioSource>();
        siren.playOnAwake = false;
        siren.spatialBlend = 0f;
        siren.volume = 0.32f;
        siren.loop = true;
        siren.clip = SirenClip();

        BigCleanupNet.PhaseChanged += OnPhase;
        BigCleanupNet.TrophyReceived += OnTrophy;
    }

    void OnDestroy()
    {
        BigCleanupNet.PhaseChanged -= OnPhase;
        BigCleanupNet.TrophyReceived -= OnTrophy;
        if (instance == this) instance = null;
    }

    void OnPhase(CleanupPhase from, CleanupState s)
    {
        bannerPunch = 1f;
        bool present = CanPresent();
        switch (s.Phase)
        {
            case CleanupPhase.Announce:
                sirenUntil = Time.unscaledTime + 6f;
                siren.Play();
                if (present) Juice.Shake(0.15f, 0.4f);
                break;
            case CleanupPhase.Running:
                Sfx.Play("horn", null, 0.8f, 0.8f);
                if (present) GameHUD.Toast("BIG CLEANUP! Double trash on every street. GO GO GO!", ToastStyle.Good, 3f);
                break;
            case CleanupPhase.Results:
                Sfx.Play(s.winnerId.Length > 0 ? "legendary" : "truck_full", null, 0.9f, 1f);
                if (present && s.winnerId.Length > 0 && s.winnerId.ToString() == Businesses.LocalId) Juice.Shake(0.2f, 0.4f);
                break;
        }
    }

    void OnTrophy(ItemInstance item)
    {
        var d = item?.Def;
        if (d == null) return;
        GameHUD.Toast($"{d.name} added to your keep crate! Display it on a shelf.", ToastStyle.Good, 4f);
    }

    static bool CanPresent() => BigCleanupPresentationRules.CanShow(HudLayout.Mode, SortStation.LocalInSortMode,
                                                                     PlayerController.IsUIBlocking, SceneController.IsTransitioning);

    void Update()
    {
        float dt = Time.unscaledDeltaTime;
        var n = BigCleanupNet.Instance;
        bool live = n != null && n.IsSpawned;
        var s = live ? n.State : default;
        var phase = live ? s.Phase : CleanupPhase.Idle;
        bool unobstructed = CanPresent();

        if (siren.isPlaying && (Time.unscaledTime > sirenUntil || phase != CleanupPhase.Announce))
            siren.volume = Mathf.MoveTowards(siren.volume, 0f, dt * 0.6f);
        else if (siren.isPlaying) siren.volume = Mathf.MoveTowards(siren.volume, 0.32f, dt * 2f);
        if (siren.isPlaying && siren.volume <= 0.001f) { siren.Stop(); siren.volume = 0.32f; }

        // ---- banner (announce + results)
        bool showBanner = unobstructed && (phase == CleanupPhase.Announce || phase == CleanupPhase.Results ||
                          (phase == CleanupPhase.Running && n.State.phaseStart + 3.0 > n.ServerNow));
        bannerGroup.alpha = unobstructed ? Mathf.MoveTowards(bannerGroup.alpha, showBanner ? 1f : 0f, dt * 4f) : 0f;
        bannerPunch = Mathf.MoveTowards(bannerPunch, 0f, dt * 3f);
        banner.localScale = Vector3.one * (1f + 0.12f * Mathf.Sin(bannerPunch * Mathf.PI));
        if (bannerGroup.alpha > 0f && live) FillBanner(n, s);

        // ---- scoreboard
        bool showBoard = phase == CleanupPhase.Running && unobstructed;
        if (showBoard && Input.GetKeyDown(KeyCode.L)) { expanded = !expanded; nextBoardText = 0f; }
        if (phase != CleanupPhase.Running) expanded = false;
        boardGroup.alpha = unobstructed ? Mathf.MoveTowards(boardGroup.alpha, showBoard ? 1f : 0f, dt * 4f) : 0f;
        if (boardGroup.alpha > 0f && live && Time.unscaledTime >= nextBoardText)
        {
            nextBoardText = Time.unscaledTime + 0.2f;
            FillBoard(n, s);
        }
        var frame = HudLayout.Layout;
        if (frame.valid)
        {
            float x = Mathf.Max(HudLayout.Margin, frame.size.x - HudLayout.Margin - board.sizeDelta.x);
            // the corner is the pocket / gear bar's too: the board rides above it when it is up
            float bottom = frame.size.y - 116f;
            if (frame.pocket.width > 0.5f) bottom = Mathf.Min(bottom, frame.pocket.yMin - HudLayout.Gap);
            float y = Mathf.Max(HudLayout.ColumnTop, bottom - board.sizeDelta.y);
            HudLayout.PlaceAt(board, new Vector2(x, y));
        }

    }

    void FillBanner(BigCleanupNet n, CleanupState s)
    {
        float left = n.SecondsLeft;
        switch (s.Phase)
        {
            case CleanupPhase.Announce:
                bannerTitle.text = "BIG CLEANUP!";
                bannerSub.text = s.solo != 0
                    ? $"The city doubles the trash for {Mins(CleanupTuning.RunSeconds)}. Beat your best of {s.bestBefore} bags!"
                    : $"The city doubles the trash for {Mins(CleanupTuning.RunSeconds)}. Most bags wins cash + the Big Cleanup Cup!";
                bannerCount.text = $"Starts in {Mathf.CeilToInt(left)}";
                FeelUi.Wave(bannerTitle, Time.unscaledTime, 6f, true);
                break;
            case CleanupPhase.Running:
                bannerTitle.text = "GO GO GO!";
                bannerSub.text = "Every curb bag into your truck counts.";
                bannerCount.text = "";
                FeelUi.Wave(bannerTitle, Time.unscaledTime, 8f, true);
                break;
            case CleanupPhase.Results:
            {
                string prize = s.prizeCents > 0 ? Economy.Money(s.prizeCents / 100f) : "";
                if (s.solo != 0)
                {
                    n.Ranked(ranked);
                    int bags = ranked.Count > 0 ? ranked[0].bags : 0;
                    bannerTitle.text = s.newBest != 0 ? "NEW PERSONAL BEST!" : "TIME'S UP!";
                    bannerSub.text = s.newBest != 0
                        ? $"{bags} bags! +{prize} and a Big Cleanup Ribbon for the shelf."
                        : $"{bags} bags. Your best is still {s.bestBefore}. So close!";
                }
                else if (s.winnerId.Length > 0)
                {
                    bool me = s.winnerId.ToString() == Businesses.LocalId;
                    bannerTitle.text = me ? "YOU WON!" : "WINNER!";
                    bannerSub.text = $"<color={BusinessColors.Hex(s.winnerColor)}>●</color> {s.winnerName} takes {prize} and the Big Cleanup Cup!";
                }
                else
                {
                    bannerTitle.text = "TIME'S UP!";
                    bannerSub.text = "Nobody collected a thing. The city is disappointed in all of you.";
                }
                bannerCount.text = "";
                FeelUi.Wave(bannerTitle, Time.unscaledTime, 5f, s.winnerId.Length > 0);
                break;
            }
        }
    }

    void FillBoard(BigCleanupNet n, CleanupState s)
    {
        n.RankedLive(ranked);
        float left = n.SecondsLeft;
        string clock = $"{(int)(left / 60f)}:{(int)(left % 60f):00}";
        boardHead.text = $"BIG CLEANUP  <color=#FFFFFF>{clock}</color>" +
                         (expanded && s.solo != 0 ? $"   <size=17><color=#BBBBBB>best {s.bestBefore}</color></size>" : "");
        var sb = new System.Text.StringBuilder();
        int rows = Mathf.Min(4, ranked.Count);
        string local = Businesses.LocalId;
        if (!expanded)
        {
            int mine = ranked.FindIndex(r => r.Id == local);
            int bags = mine >= 0 ? ranked[mine].bags : 0;
            boardRows.text = $"You: {bags} bag{(bags == 1 ? "" : "s")}" +
                             (mine >= 0 && ranked.Count > 1 ? $"  ·  #{mine + 1}/{ranked.Count}" : "") + "   [L] standings";
            board.sizeDelta = new Vector2(360f, 74f);
            return;
        }
        for (int i = 0; i < rows; i++)
        {
            var r = ranked[i];
            bool me = r.Id == local;
            string name = r.Name.Length > 26 ? r.Name.Substring(0, 25) + "…" : r.Name;
            sb.Append(i > 0 ? "\n" : "")
              .Append(i + 1).Append(". <color=").Append(BusinessColors.Hex(r.color)).Append(">●</color> ")
              .Append(me ? "<b>" : "").Append(name).Append(me ? "</b>" : "")
              .Append("<pos=66%>").Append(r.bags).Append(r.bags == 1 ? " bag" : " bags")
              .Append("<pos=84%>").Append(r.Kg.ToString("0.0")).Append(" kg");
        }
        if (rows == 0) sb.Append("Nobody yet. Grab a bag!");
        sb.Append("\n<size=16><color=#BBBBBB>[L] close standings</color></size>");
        boardRows.text = sb.ToString();
        board.sizeDelta = new Vector2(500f, 60f + Mathf.Max(1, rows) * 27f);
    }

    static string Mins(float s) => s >= 60f ? $"{Mathf.RoundToInt(s / 60f)} minutes" : $"{Mathf.RoundToInt(s)} seconds";

    /// <summary>A city siren: two slow wails (sine sweeps with a little vibrato), looped while the banner is up.</summary>
    static AudioClip SirenClip()
    {
        const int rate = 22050;
        const float len = 3f;
        int n = (int)(rate * len);
        var d = new float[n];
        double ph = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float u = (t % 1.5f) / 1.5f;
            float sweep = u < 0.5f ? u * 2f : (1f - u) * 2f;
            float f = Mathf.Lerp(480f, 1050f, Mathf.SmoothStep(0f, 1f, sweep)) + Mathf.Sin(t * 38f) * 6f;
            ph += 2.0 * Mathf.PI * f / rate;
            float v = Mathf.Sin((float)ph) * 0.6f + Mathf.Sin((float)(ph * 2.0)) * 0.15f;
            float env = Mathf.Clamp01(t * 8f) * Mathf.Clamp01((len - t) * 8f);
            d[i] = v * env * 0.8f;
        }
        var clip = AudioClip.Create("cleanup_siren", n, 1, rate, false);
        clip.SetData(d, 0);
        return clip;
    }
}
