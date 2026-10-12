using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The sort-streak show (Docs/Progression.md §3.6). Consecutive correct hand sorts climb <see cref="SortLadder"/> (x1.5 → x10);
/// this is everything the player sees and hears about it, built in code on its own overlay canvas (one step under the
/// promotion card and the treasure reveal, so those always win):
/// <list type="bullet">
/// <item>a big multiplier that punches on every sort, its tier name under it (NICE, HOT, ON FIRE, UNSTOPPABLE, LEGENDARY,
/// TRASH GOD), the run length, how many more sorts reach the next tier, and a bar that drains while the clock for the next
/// sort runs out (tighter at every tier);</item>
/// <item>every tier up: the number slams in, a rising stinger, a camera shake, confetti, a tier-coloured floating word at the
/// bin and, from x4, a white flash; x5 and x10 add a slow-mo beat, x10 a fanfare, a gold storm and rainbow text;</item>
/// <item>a heat vignette round the screen edge that grows with the tier;</item>
/// <item>from the 6th sort a melody climbs a pentatonic scale, one note per correct bin (the ding itself stops climbing at 5);</item>
/// <item>a wrong bin: the number cracks and drops with a deflating womp; running out of clock cools one tier with a small bloop.</item>
/// </list>
/// Flashes, the strobing rainbow and the vignette pulse are off with Settings → Accessibility → Reduce flashing. Never takes input.
/// The streak itself lives in <see cref="SortCombo"/>; robots never feed it, so Magnus sorting does not light this up.
/// </summary>
public sealed class SortStreakHud : MonoBehaviour
{
    /// <summary>Under the progression card (99) and the reveal card (100).</summary>
    public const int SortingOrder = FeelHud.SortingOrder - 2;

    const float PanelW = 420f, PanelH = 300f;
    const float SlamTime = 0.45f, LostTime = 1.25f;
    const int ConfettiMax = 130;

    /// <summary>Cool white, soft yellow, orange, deep orange, red, hot pink, gold (x10 cycles a rainbow on top).</summary>
    static readonly Color[] TierColors =
    {
        new Color(0.82f, 0.9f, 1f), new Color(1f, 0.93f, 0.45f), new Color(1f, 0.72f, 0.2f), new Color(1f, 0.5f, 0.15f),
        new Color(1f, 0.28f, 0.3f), new Color(1f, 0.35f, 0.85f), new Color(1f, 0.84f, 0.25f),
    };
    // how strong the heat vignette is at each tier (alpha at the very edge)
    static readonly float[] VignetteAlpha = { 0f, 0f, 0.10f, 0.16f, 0.22f, 0.28f, 0.36f };
    // major pentatonic climb in semitones; past the end it loops the top five notes
    static readonly int[] Penta = { 0, 2, 4, 7, 9, 12, 14, 16, 19, 21, 24 };

    public static Color ColorOf(int tier) => TierColors[Mathf.Clamp(tier, 0, SortLadder.MaxTier)];

    /// <summary>The pentatonic pitch ratio of the melody note for the nth correct sort of a streak (n ≥ 6), clamped under the sound pool's cap.</summary>
    public static float NotePitch(int streak)
    {
        int idx = Mathf.Max(0, streak - 6);
        int semis = idx < Penta.Length ? Penta[idx] : Penta[Penta.Length - 5 + (idx - Penta.Length) % 5];
        return 0.7f * Mathf.Pow(2f, semis / 12f);
    }

    // ------------------------------------------------------------------ singleton + boot

    static SortStreakHud instance;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => instance = null;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoBoot()
    {
        if (!Application.isPlaying || instance != null) return;
        var go = new GameObject("[SortStreak]", typeof(RectTransform)) { layer = 5 };
        DontDestroyOnLoad(go);
        instance = go.AddComponent<SortStreakHud>();
    }

    /// <summary>An unattached copy for screenshots in edit mode (not subscribed to anything; drive it with <see cref="Preview"/> and <see cref="Tick"/>).</summary>
    public static SortStreakHud CreateForPreview()
    {
        var go = new GameObject("SortStreakPreview", typeof(RectTransform)) { layer = 5 };
        var hud = go.AddComponent<SortStreakHud>();
        hud.preview = true;
        hud.Build();
        return hud;
    }

    // ------------------------------------------------------------------ state

    struct Data { public int streak; public float timeLeft; public HudMode mode; }

    bool built, preview;
    Data previewData;
    float previewClock;
    Canvas canvas;
    CanvasScaler scaler;
    RectTransform root, panel, rays, barFill, lostRoot, confettiRoot, plate;
    CanvasGroup panelGroup, lostGroup;
    Image raysImg, barFillImg, vignette, flash, plateBody;
    TextMeshProUGUI multText, nameText, countText, nextText, lostText, lostSub;

    int shownStreak, shownTier;
    float alpha, punch = 1f, punchVel, slamAge = 99f, coolBump, vigShown, flashAlpha, redFlash, lostAge = 99f, lostSfxAt = -1f;
    Color flashColor = Color.white;
    int textStreak = -1;
    float liftShown;
    RectTransform lostHolder;
    int lostHad, lostTier, peak;   // peak: the highest streak since it was last empty (for the playtest metrics)

    // the same-frame handshake with ProgressionEvents.ItemSorted, which carries the bin's position
    int pendingFrame = -1, pendingTier;
    bool pendingUp;

    struct Conf { public RectTransform rt; public Image img; public Vector2 pos, vel; public float rot, spin, life, maxLife; public bool alive; }
    readonly List<Conf> confetti = new List<Conf>();

    static bool Calm => Application.isPlaying && GameSettings.ReduceFlashing;

    // ------------------------------------------------------------------ build

    void Awake()
    {
        if (preview) return;
        if (instance != null && instance != this) { Destroy(gameObject); return; }
        instance = this;
        Build();
        SortCombo.Changed += OnChanged;
        SortCombo.Lost += OnLost;
        ProgressionEvents.ItemSorted += OnItemSorted;
    }

    void OnDestroy()
    {
        SortCombo.Changed -= OnChanged;
        SortCombo.Lost -= OnLost;
        ProgressionEvents.ItemSorted -= OnItemSorted;
        if (scaler != null) UiScale.Unregister(scaler);
        if (instance == this) instance = null;
    }

    static void Place(RectTransform rt, Vector2 pos, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
    }

    void Build()
    {
        if (built) return;
        built = true;
        canvas = gameObject.GetComponent<Canvas>();
        if (canvas == null) canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = SortingOrder;
        canvas.additionalShaderChannels |= AdditionalCanvasShaderChannels.TexCoord1 | AdditionalCanvasShaderChannels.Normal |
                                           AdditionalCanvasShaderChannels.Tangent;
        scaler = gameObject.GetComponent<CanvasScaler>();
        if (scaler == null) scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;
        if (Application.isPlaying && !preview) UiScale.Register(scaler); // Settings → Interface → UI scale
        // deliberately no GraphicRaycaster: this never eats a click
        root = (RectTransform)transform;

        vignette = FeelUi.Img(root, "Heat", VignetteSprite, Color.clear);
        flash = FeelUi.Img(root, "Flash", null, Color.clear);
        confettiRoot = FeelUi.Stretch("Confetti", root);

        panel = FeelUi.Rect("Panel", root, new Vector2(1f, 0.53f), new Vector2(1f, 0.5f), new Vector2(-36f, 0f), new Vector2(PanelW, PanelH));
        panelGroup = panel.gameObject.AddComponent<CanvasGroup>();
        panelGroup.interactable = false;
        panelGroup.blocksRaycasts = false;
        panelGroup.alpha = 0f;

        rays = FeelUi.Rect("Rays", panel, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 80f), new Vector2(760f, 760f));
        raysImg = rays.gameObject.AddComponent<Image>();
        raysImg.sprite = FeelUi.Rays;
        raysImg.raycastTarget = false;
        raysImg.color = Color.clear;

        plate = FeelUi.Rect("Plate", panel, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -8f), new Vector2(380f, 262f));
        plateBody = FeelUi.Panel(plate, new Color(0.07f, 0.05f, 0.13f, 0.5f), 1.2f, new Vector2(5f, -6f));

        multText = FeelUi.Text(panel, "Mult", 120f, Color.white, TextAlignmentOptions.Center);
        Place(multText.rectTransform, new Vector2(0f, 82f), new Vector2(400f, 180f));
        nameText = FeelUi.Text(panel, "Name", 46f, Color.white, TextAlignmentOptions.Center);
        Place(nameText.rectTransform, new Vector2(0f, -12f), new Vector2(420f, 60f));
        countText = FeelUi.Text(panel, "Count", 28f, new Color(0.95f, 0.95f, 1f), TextAlignmentOptions.Center);
        Place(countText.rectTransform, new Vector2(0f, -54f), new Vector2(420f, 40f));

        var barBg = FeelUi.Rect("Bar", panel, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -92f), new Vector2(330f, 20f));
        FeelUi.Img(barBg, "BarBg", FeelUi.Rounded, new Color(0f, 0f, 0f, 0.6f), 0.5f);
        var fillArea = FeelUi.Stretch("FillArea", barBg);
        fillArea.offsetMin = new Vector2(3f, 3f);
        fillArea.offsetMax = new Vector2(-3f, -3f);
        barFillImg = FeelUi.Img(fillArea, "Fill", FeelUi.Rounded, Color.white, 0.4f);
        barFill = barFillImg.rectTransform;
        barFill.anchorMin = Vector2.zero;
        barFill.anchorMax = Vector2.one;

        nextText = FeelUi.Text(panel, "Next", 22f, new Color(0.78f, 0.8f, 0.92f), TextAlignmentOptions.Center, FontStyles.Normal);
        Place(nextText.rectTransform, new Vector2(0f, -122f), new Vector2(420f, 32f));

        // not under the panel: the panel's own group fades to nothing the moment the streak is gone, which is when this shows
        lostHolder = FeelUi.Rect("LostHolder", root, new Vector2(1f, 0.53f), new Vector2(1f, 0.5f), new Vector2(-36f, 0f), new Vector2(PanelW, PanelH));
        lostRoot = FeelUi.Rect("Lost", lostHolder, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 30f), new Vector2(420f, 160f));
        lostGroup = lostRoot.gameObject.AddComponent<CanvasGroup>();
        lostGroup.interactable = false;
        lostGroup.blocksRaycasts = false;
        lostGroup.alpha = 0f;
        lostText = FeelUi.Text(lostRoot, "LostText", 54f, FeelUi.BadRed, TextAlignmentOptions.Center);
        Place(lostText.rectTransform, new Vector2(0f, 24f), new Vector2(420f, 80f));
        lostSub = FeelUi.Text(lostRoot, "LostSub", 28f, new Color(1f, 0.85f, 0.85f), TextAlignmentOptions.Center);
        Place(lostSub.rectTransform, new Vector2(0f, -30f), new Vector2(420f, 40f));

        for (int i = 0; i < 70; i++) confetti.Add(NewBit());     // pre-warmed: a x5 is 55 bits at once, a x10 120
        canvas.enabled = false;
    }

    static Sprite vignetteSprite;
    static Sprite VignetteSprite
    {
        get
        {
            if (vignetteSprite != null) return vignetteSprite;
            const int n = 64;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave, name = "SortStreakHeat",
            };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                    float r = Mathf.Sqrt(dx * dx * 0.85f + dy * dy);
                    float u = Mathf.Clamp01((r - 0.5f) / 0.7f);          // clear in the middle, full at the corners
                    float a = u * u * (3f - 2f * u);
                    px[y * n + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
                }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            vignetteSprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
            vignetteSprite.hideFlags = HideFlags.HideAndDontSave;
            return vignetteSprite;
        }
    }

    // ------------------------------------------------------------------ events (live)

    void OnChanged(int streak, float mult)
    {
        if (streak <= 0) { shownStreak = 0; shownTier = 0; return; }
        int tier = SortLadder.TierFor(streak);
        bool up = tier > shownTier && tier >= 1;
        peak = Mathf.Max(peak, streak);
        punchVel += 6f + tier * 1.3f;
        if (up) TierUp(tier);
        if (streak >= 6)
            Sfx.Play("streak_note", null, Mathf.Clamp(0.34f + 0.06f * tier, 0f, 0.75f), NotePitch(streak));
        pendingFrame = Time.frameCount;
        pendingTier = tier;
        pendingUp = up;
        shownStreak = streak;
        shownTier = tier;
    }

    void OnLost(int had)
    {
        EndStreak("wrong");
        if (had < 3) return;
        BeginLost(had);
    }

    /// <summary>A streak ended: one playtest-metrics line when it got to x1.5 or more (how high, and whether a wrong bin or the clock ended it).</summary>
    void EndStreak(string how)
    {
        int top = peak;
        peak = 0;
        if (top < 3 || preview || !Application.isPlaying) return;
        Metrics.Begin("sort_streak").Int("peak", top).Int("tier", SortLadder.TierFor(top)).Str("end", how, 8).End();
    }

    /// <summary>The correct sort's bin position arrives in the same frame as the streak change: burst and word right there.</summary>
    void OnItemSorted(ItemSortedFact f)
    {
        if (pendingFrame != Time.frameCount) return;
        pendingFrame = -1;
        if (!f.correct) return;
        WorldFx(f.pos, pendingTier, pendingUp);
    }

    void WorldFx(Vector3 pos, int tier, bool up)
    {
        if (tier < 1) return;
        Color c = ColorOf(tier);
        Fx.Burst(pos + Vector3.up * 0.25f, c, 6 + 4 * tier, 3f + 0.45f * tier);
        if (tier >= 3) Fx.Burst(pos + Vector3.up * 0.3f, FeelUi.Gold, 8 + 4 * tier, 4.5f + 0.5f * tier);
        if (!up) return;
        Fx.Burst(pos + Vector3.up * 0.2f, Color.white, 20 + 8 * tier, 6f);
        GameHUD.FloatingText(pos + Vector3.up * 1.2f, $"{SortLadder.Name(tier)}! x{SortLadder.Multiplier(tier):0.#}", c, 1.4f + 0.12f * tier);
    }

    // ------------------------------------------------------------------ the moments

    void TierUp(int tier)
    {
        slamAge = 0f;
        Sfx.Play("streak_up", null, Mathf.Min(1f, 0.55f + 0.07f * tier), 0.88f + 0.06f * tier);
        Juice.Shake(Mathf.Lerp(0.05f, 0.3f, tier / 6f), 0.18f + 0.04f * tier);
        if (tier >= SortLadder.MaxTier)
        {
            Sfx.Play("streak_max", null, 1f, 1f);
            Juice.HitStop(0.14f, 0.15f);
        }
        else if (tier >= 5) Juice.HitStop(0.08f, 0.2f);
        SpawnConfetti(tier switch { 1 => 0, 2 => 8, 3 => 18, 4 => 30, 5 => 55, _ => 120 }, tier >= SortLadder.MaxTier);
        if (!Calm && tier >= 4)
        {
            flashAlpha = tier >= SortLadder.MaxTier ? 0.5f : 0.26f;
            flashColor = Color.Lerp(ColorOf(tier), Color.white, 0.6f);
        }
    }

    void BeginLost(int had)
    {
        lostHad = had;
        lostTier = SortLadder.TierFor(had);
        lostAge = 0f;
        lostSfxAt = (preview ? previewClock : Time.unscaledTime) + 0.28f;   // after the wrong-bin buzzer, not on top of it
        if (!Calm) redFlash = 0.35f;
        lostText.text = lostTier >= 3 ? "STREAK LOST!" : "streak lost";
        lostSub.text = $"x{SortLadder.Multiplier(lostTier):0.#}  ·  {had} in a row";
        Juice.Shake(0.06f + 0.015f * lostTier, 0.15f);
    }

    Conf NewBit()
    {
        var bit = new Conf();
        bit.rt = FeelUi.Rect("Bit", confettiRoot, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(12f, 20f));
        bit.img = bit.rt.gameObject.AddComponent<Image>();
        bit.img.sprite = FeelUi.Rounded;
        bit.img.raycastTarget = false;
        bit.rt.gameObject.SetActive(false);
        return bit;
    }

    void SpawnConfetti(int n, bool rainbow)
    {
        if (n <= 0) return;
        // the number's centre, relative to the canvas centre (the panel hangs 36 px off the right edge at 53% height)
        var origin = new Vector2(root.rect.width * 0.5f - 36f - PanelW * 0.5f, 85f + (0.53f - 0.5f) * root.rect.height);
        for (int i = 0; i < n; i++)
        {
            int slot = -1;
            for (int k = 0; k < confetti.Count; k++) if (!confetti[k].alive) { slot = k; break; }
            if (slot < 0)
            {
                if (confetti.Count >= ConfettiMax) return;
                confetti.Add(NewBit());
                slot = confetti.Count - 1;
            }
            var c = confetti[slot];
            c.alive = true;
            // up and out, mostly to the left (into the screen); all of this is one player's own cosmetic HUD
            float ang = Random.Range(Mathf.PI * 0.2f, Mathf.PI * 0.95f);                           // coop:allow(random) cosmetic confetti
            float speed = Random.Range(500f, 1250f);                                               // coop:allow(random) cosmetic confetti
            c.pos = origin + new Vector2(Random.Range(-30f, 30f), Random.Range(-10f, 30f));        // coop:allow(random) cosmetic confetti
            c.vel = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * speed;
            c.rot = Random.Range(0f, 360f);                                                        // coop:allow(random) cosmetic confetti
            c.spin = Random.Range(-720f, 720f);                                                    // coop:allow(random) cosmetic confetti
            c.maxLife = c.life = Random.Range(1.4f, 2.4f);                                         // coop:allow(random) cosmetic confetti
            c.img.color = rainbow ? Color.HSVToRGB(Random.value, 0.75f, 1f)                        // coop:allow(random) cosmetic confetti
                                  : Color.Lerp(ColorOf(Random.Range(1, 7)), Color.white, 0.2f);    // coop:allow(random) cosmetic confetti
            c.rt.sizeDelta = new Vector2(Random.Range(9f, 14f), Random.Range(14f, 24f));           // coop:allow(random) cosmetic confetti
            c.rt.gameObject.SetActive(true);
            confetti[slot] = c;
        }
    }

    // ------------------------------------------------------------------ per frame

    void Update()
    {
        if (!built) return;
        Tick(Time.unscaledDeltaTime);
    }

    /// <summary>Preview (edit mode): what the HUD should be showing, for screenshots.</summary>
    public void Preview(int streak, float timeLeft01, HudMode mode = HudMode.Station)
    {
        previewData = new Data { streak = streak, timeLeft = timeLeft01, mode = mode };
    }

    /// <summary>Preview: the moments, without a sorting bench (what <see cref="SortCombo"/> would have raised).</summary>
    public void PreviewSort(int streak)
    {
        OnChanged(streak, SortLadder.MultiplierFor(streak));
        Preview(streak, 1f, previewData.mode);
    }

    public void PreviewLost(int had)
    {
        OnLost(had);
        OnChanged(0, 1f);
        Preview(0, 0f, previewData.mode);
    }

    /// <summary>Advance all animation by <paramref name="dt"/> real seconds.</summary>
    public void Tick(float dt)
    {
        dt = Mathf.Clamp(dt, 0f, 0.1f);
        Data d;
        float time;
        if (preview) { previewClock += dt; time = previewClock; d = previewData; }
        else
        {
            time = Time.unscaledTime;
            d = new Data { streak = SortCombo.Streak, timeLeft = SortCombo.TimeLeft01, mode = HudLayout.Layout.mode };
        }

        int streak = d.streak;
        int tier = SortLadder.TierFor(streak);
        bool showMode = d.mode == HudMode.Station || d.mode == HudMode.Depot || d.mode == HudMode.Street || d.mode == HudMode.Driving;
        bool lostPlaying = lostAge < LostTime;

        // the clock cooling the streak: the tier fell with no wrong bin (a wrong bin already zeroed shownStreak)
        if (streak < shownStreak)
        {
            if (streak == 0) EndStreak("slow");        // cooled all the way: the clock, not a wrong bin (a wrong bin zeroed shownStreak already)
            if (tier < shownTier && shownTier >= 2) { Sfx.Play("streak_cool", null, 0.5f, 0.85f + 0.05f * tier); coolBump = 1f; }
            shownStreak = streak;
            shownTier = tier;
        }

        if (lostSfxAt > 0f && time >= lostSfxAt)
        {
            lostSfxAt = -1f;
            if (lostHad >= 6) Sfx.Play("streak_lost", null, Mathf.Min(0.9f, 0.45f + 0.07f * lostTier), 1f);
        }

        // ---- visibility
        bool want = showMode && streak >= 2 && !lostPlaying;
        alpha = Mathf.MoveTowards(alpha, want ? 1f : 0f, dt * (want ? 9f : lostPlaying ? 16f : 5f));
        float lostWant = showMode && lostPlaying ? 1f : 0f;
        canvas.enabled = alpha > 0.002f || lostWant > 0f || vigShown > 0.002f || flashAlpha > 0.002f || redFlash > 0.002f || AnyConfetti();
        panelGroup.alpha = alpha;

        // ---- the number (and everything on the panel); nothing to draw while it is faded out
        if (alpha <= 0.002f)
        {
            punch = 1f; punchVel = 0f;
            TickScreen(dt, time, streak, tier, showMode);
            return;
        }
        punch = FeelUi.Spring(punch, ref punchVel, 1f, dt, 260f, 13f);
        float slam = slamAge < SlamTime ? Mathf.LerpUnclamped(2.3f, 1f, FeelEase.OutBack(slamAge / SlamTime, 2.2f)) : 1f;
        float slamTilt = slamAge < SlamTime ? (1f - slamAge / SlamTime) * -9f : 0f;
        slamAge += dt;
        coolBump = Mathf.MoveTowards(coolBump, 0f, dt * 3f);

        Color tc = ColorOf(tier);
        bool max = tier >= SortLadder.MaxTier;
        Color numberColor = max && !Calm ? Color.HSVToRGB(Mathf.Repeat(time * 0.7f, 1f), 0.65f, 1f) : tc;
        if (coolBump > 0f) numberColor = Color.Lerp(numberColor, new Color(0.6f, 0.75f, 1f), coolBump * 0.7f);

        if (streak != textStreak)                       // strings only when the streak moved: nothing allocates while it idles
        {
            textStreak = streak;
            multText.text = $"x{SortLadder.Multiplier(tier):0.#}";
            nameText.text = tier == 0 ? "COMBO" : SortLadder.Name(tier) + "!";
            countText.text = $"{streak} IN A ROW";
            int upNext = SortLadder.NextTierAt(streak);
            nextText.text = upNext < 0 ? "MAX HEAT! DON'T STOP!" : $"{upNext - streak} more for x{SortLadder.Multiplier(tier + 1):0.#}";
        }
        multText.fontSize = Mathf.Lerp(96f, 170f, tier / 6f);
        multText.color = numberColor;
        float s = Mathf.Clamp(punch, 0.6f, 1.8f) * slam * (1f - 0.12f * coolBump);
        multText.rectTransform.localScale = new Vector3(s, s, 1f);
        multText.rectTransform.localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin(time * (2.5f + tier)) * (1f + tier * 0.7f) + slamTilt);

        nameText.color = tier == 0 ? new Color(0.85f, 0.9f, 1f) : numberColor;
        nameText.fontSize = 40f + tier * 1.5f;
        nameText.rectTransform.localScale = Vector3.one * Mathf.Lerp(1f, 1.15f, Mathf.Clamp01(1f - slamAge / SlamTime) * (tier > 0 ? 1f : 0f));
        if (alpha > 0.01f && tier >= 4) FeelUi.Wave(nameText, time, tier >= 5 ? 6f : 4f, max && !Calm, 7f + tier);


        // ---- the clock bar: drains inside the window; a short one flickers
        float left = Mathf.Clamp01(d.timeLeft);
        barFill.anchorMax = new Vector2(Mathf.Max(0.02f, left), 1f);
        bool hurry = left < 0.3f && streak >= 3;
        Color barColor = hurry && !Calm ? Color.Lerp(tc, FeelUi.BadRed, 0.5f + 0.5f * Mathf.Sin(time * 22f)) : tc;
        barFillImg.color = barColor;

        // ---- the panel: shakes when the clock is nearly out, and harder the hotter it is
        Vector2 jitter = Vector2.zero;
        if (hurry && tier >= 2 && alpha > 0.5f && !Calm) jitter = new Vector2(Mathf.Sin(time * 61f), Mathf.Cos(time * 53f)) * (1.5f + tier * 0.6f);
        liftShown = Mathf.MoveTowards(liftShown, WantedLift(), dt * 500f);
        panel.anchoredPosition = new Vector2(-36f, liftShown) + jitter;
        lostHolder.anchoredPosition = new Vector2(-36f, liftShown);
        panel.localScale = Vector3.one * (Calm ? 1f : 1f + 0.012f * tier * Mathf.Sin(time * 6f));
        plateBody.color = Color.Lerp(new Color(0.07f, 0.05f, 0.13f, 0.5f), new Color(tc.r * 0.22f, tc.g * 0.12f, tc.b * 0.2f, 0.62f), tier / 6f);

        // ---- the rays behind it from x3
        float rayAlpha = tier >= 3 ? Mathf.Lerp(0.14f, 0.42f, (tier - 3) / 3f) : 0f;
        raysImg.color = new Color(tc.r, tc.g, tc.b, rayAlpha * alpha);
        rays.localRotation = Calm ? Quaternion.identity : Quaternion.Euler(0f, 0f, -time * (18f + tier * 8f));
        rays.localScale = Vector3.one * (Calm ? 1f : Mathf.Clamp(punch, 0.8f, 1.4f) * (1f + 0.04f * Mathf.Sin(time * 3f)));

        TickScreen(dt, time, streak, tier, showMode);
    }

    /// <summary>The screen edge: heat by tier, a hot pulse, a red flash when lost, a white flash on a big tier up; then the lost banner and the confetti.</summary>
    void TickScreen(float dt, float time, int streak, int tier, bool showMode)
    {
        bool max = tier >= SortLadder.MaxTier;
        Color tc = ColorOf(tier);
        float vigTarget = showMode && streak >= 1 ? VignetteAlpha[tier] : 0f;
        vigShown = Mathf.MoveTowards(vigShown, vigTarget, dt * 0.5f);
        float pulse = Calm ? 1f : 1f + 0.22f * Mathf.Sin(time * (2f + tier));
        Color vc = max && !Calm ? Color.HSVToRGB(Mathf.Repeat(time * 0.4f, 1f), 0.6f, 1f) : tc;
        redFlash = Mathf.MoveTowards(redFlash, 0f, dt * 1.2f);
        Color vcol = Color.Lerp(vc, new Color(1f, 0.1f, 0.1f), Mathf.Clamp01(redFlash * 2.5f));
        vignette.color = new Color(vcol.r, vcol.g, vcol.b, Mathf.Clamp01(vigShown * pulse + redFlash));
        flashAlpha = Mathf.MoveTowards(flashAlpha, 0f, dt * 2.6f);
        flash.color = new Color(flashColor.r, flashColor.g, flashColor.b, flashAlpha);

        TickLost(dt);
        TickConfetti(dt);
    }

    /// <summary>How far to lift the panel so it stays above the pocket / gear bar's corner (it only exists outside Station mode).</summary>
    float WantedLift()
    {
        if (preview) return 0f;
        var f = HudLayout.Layout;
        if (!f.valid || f.mode == HudMode.Station || f.pocket.width <= 0f) return 0f;
        float panelBottom = f.size.y * (1f - 0.53f) + PanelH * 0.5f;      // top-down
        return Mathf.Max(0f, panelBottom - (f.pocket.yMin - 12f));
    }

    void TickLost(float dt)
    {
        if (lostAge >= LostTime)
        {
            lostGroup.alpha = 0f;
            return;
        }
        lostAge += dt;
        float p = Mathf.Clamp01(lostAge / LostTime);
        // slams in red, hangs and trembles for a beat, then falls away tilting
        float pop = Mathf.LerpUnclamped(1.9f, 1f, FeelEase.OutBack(Mathf.Clamp01(lostAge / 0.22f), 2.5f));
        float fall = p > 0.55f ? FeelEase.InQuad((p - 0.55f) / 0.45f) : 0f;
        lostRoot.localScale = Vector3.one * pop * (1f - 0.25f * fall);
        lostRoot.localRotation = Quaternion.Euler(0f, 0f, (Calm ? 0f : Mathf.Sin(lostAge * 40f) * (1f - p) * 3f) + fall * 14f);
        lostRoot.anchoredPosition = new Vector2(0f, 30f - fall * 120f);
        lostGroup.alpha = p > 0.7f ? 1f - (p - 0.7f) / 0.3f : 1f;
        lostText.color = Color.Lerp(FeelUi.BadRed, new Color(0.7f, 0.2f, 0.25f), p);
    }

    bool AnyConfetti()
    {
        for (int i = 0; i < confetti.Count; i++) if (confetti[i].alive) return true;
        return false;
    }

    void TickConfetti(float dt)
    {
        for (int i = 0; i < confetti.Count; i++)
        {
            var c = confetti[i];
            if (!c.alive) continue;
            c.life -= dt;
            if (c.life <= 0f)
            {
                c.alive = false;
                c.rt.gameObject.SetActive(false);
                confetti[i] = c;
                continue;
            }
            c.vel.y -= 1500f * dt;
            c.vel.x *= Mathf.Exp(-1.2f * dt);
            if (c.vel.y < -300f) c.vel.y = -300f;          // flutter down
            c.pos += c.vel * dt;
            c.rot += c.spin * dt;
            c.rt.anchoredPosition = c.pos;
            c.rt.localRotation = Quaternion.Euler(0f, 0f, c.rot);
            float flip = Mathf.Abs(Mathf.Sin(c.rot * Mathf.Deg2Rad * 2f));
            c.rt.localScale = new Vector3(0.3f + 0.7f * flip, 1f, 1f);
            var col = c.img.color;
            col.a = Mathf.Clamp01(c.life / 0.4f);
            c.img.color = col;
            confetti[i] = c;
        }
    }
}
