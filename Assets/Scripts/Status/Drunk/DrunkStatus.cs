using System;
using System.Collections;
using StarterAssets;
using UnityEngine;

/// <summary>
/// Being drunk, on the local player (Docs/Casino.md §5). Added at runtime next to <see cref="RabiesStatus"/> by
/// <see cref="DrunkRunner"/> (no prefab edit). The level is one static number for this machine's player, so it survives the
/// player object being respawned; it is not saved, and it is cleared when there is no session (menu, new session).
///
/// The contract: <see cref="Drink"/> adds units (the bar calls it after the host confirms the purchase; water is negative),
/// <see cref="Level"/> is 0..<see cref="DrunkRules.MaxLevel"/>, <see cref="Changed"/> fires on a drink, a stage change and
/// either end of a pass-out. The level falls with game time (<see cref="GameClock"/>).
///
/// Symptoms, each scaled by its <see cref="DrunkRules"/> curve: blur and double vision (<see cref="DrunkPostFx"/>), camera
/// sway and a slow swirl (the drunk contributor of <see cref="RabiesCameraSway"/>, so rabid + drunk share one component),
/// veering and stumbling (writes StarterAssetsInputs.move right after RabiesStatus and takes it back before RabiesStatus
/// does), a slower walk (<see cref="SpeedMult"/> in BootsMods), hiccup hops, and passing out.
/// </summary>
[DefaultExecutionOrder(-79)] // right after RabiesStatus (-80), before the ThirdPersonController reads the input
[DisallowMultipleComponent]
public class DrunkStatus : MonoBehaviour
{
    /// <summary>The status component on the local player (null when there is no local player).</summary>
    public static DrunkStatus Local { get; private set; }

    // ------------------------------------------------------------------ the level (static: one local player per machine)

    static float level;
    static double decayedAtHours = -1;
    static DrunkStage lastStage;

    /// <summary>How drunk this machine's player is: 0 (sober, and whenever there is no session) to 1.2.</summary>
    public static float Level => level;
    public static DrunkStage Stage => DrunkRules.Stage(level);
    /// <summary>A drink, a stage change, passing out or waking up. Not raised for the slow decay between stages.</summary>
    public static event Action Changed;

    /// <summary>Walk / sprint speed factor (BootsMods multiplies it in): up to 20 % slower when legless.</summary>
    public static float SpeedMult => 1f - 0.2f * DrunkRules.Walk01(level);

    /// <summary>Seconds between pass-out rolls.</summary>
    public const float PassOutCheckSeconds = 10f;

    /// <summary>The drunk pass-out sequence is running (RabiesStatus waits for it, and it waits for rabies').</summary>
    public static bool PassingOut { get; private set; }
    static bool passOutVisuals;      // full-strength symptoms while keeling over
    static float passOutTip;         // 0..1 while keeling over
    static float passOutStartedAt;
    static float nextPassOutCheck = -1f;

    // Decide: one roll per player about that player's own body, made on the machine that owns the body ("you own your
    // hands", Multiplayer.md §1.2), the same way rabies decides its own pass-out. The result reaches everyone as the flop
    // (HandsNet) or the ledger line; no other machine needs to agree on the roll itself, so it is not a ContentRng roll.
    static readonly NightDice Dice = new NightDice((uint)DateTime.UtcNow.Ticks ^ 0x51ED270Bu);

    /// <summary>Add drink units to the local player (negative units sober up). Safe from anywhere.</summary>
    public static void Drink(float units)
    {
        if (float.IsNaN(units)) return;
        SetLevel(DrunkRules.AfterDrink(level, units), true);
    }

    /// <summary>Debug / tools: set the level outright.</summary>
    public static void SetLevelDirect(float value) => SetLevel(DrunkRules.AfterDrink(0f, value), true);

    static void SetLevel(float value, bool notify)
    {
        bool same = Mathf.Approximately(value, level);
        level = value;
        decayedAtHours = GameClock.TotalHours;
        var stage = DrunkRules.Stage(level);
        bool stageChanged = stage != lastStage;
        if (stageChanged) AnnounceStage(lastStage, stage);
        lastStage = stage;
        if ((notify && !same) || stageChanged) RaiseChanged();
    }

    static void RaiseChanged()
    {
        var changed = Changed;
        if (changed == null) return;
        foreach (var d in changed.GetInvocationList())
            try { ((Action)d)(); } catch (Exception e) { Debug.LogException(e); }
    }

    static void AnnounceStage(DrunkStage from, DrunkStage to)
    {
        if (PassingOut) return; // the pass-out says its own piece
        if (to > from)
        {
            if (to == DrunkStage.Tipsy) GameHUD.Toast("You're feeling tipsy.", ToastStyle.Info, 2.5f);
            else if (to == DrunkStage.Drunk) GameHUD.Toast("You're drunk. The floor has opinions.", ToastStyle.Bad, 3f);
            else if (to == DrunkStage.Wasted) GameHUD.Toast("You're WASTED. One more and you'll pass out.", ToastStyle.Bad, 3.5f);
        }
        else if (to == DrunkStage.Sober && from != DrunkStage.Sober) GameHUD.Toast("Sober again.", ToastStyle.Good, 2.5f);
    }

    /// <summary>Every frame, with or without a player object: wear the level off with game time; forget it outside a session.</summary>
    internal static void TickLevel()
    {
        if (!NetSession.InSession)
        {
            if (level > 0f || PassingOut) ResetState();
            return;
        }
        // Never leave the pass-out lock stuck (its runner died, an exception): let go, and never leave the screen black.
        if (PassingOut && Time.unscaledTime - passOutStartedAt > DrunkRules.CoopFlopSeconds + 25f)
        {
            PassingOut = false; passOutVisuals = false; passOutTip = 0f;
            if (ScreenFader.IsBlack && !ScreenFader.IsFading && !SceneController.IsTransitioning && !DepotRowWorld.Transitioning)
                ScreenFader.FadeIn(0.8f);
            RaiseChanged();
        }
        if (level <= 0f) { decayedAtHours = -1; return; }
        double now = GameClock.TotalHours;
        if (decayedAtHours < 0 || now < decayedAtHours) { decayedAtHours = now; return; } // a new clock: start counting from here
        float hours = (float)(now - decayedAtHours);
        if (hours < 0.002f) return;
        SetLevel(DrunkRules.Decay(level, hours), false);
    }

    static void ResetState()
    {
        level = 0f;
        decayedAtHours = -1;
        lastStage = DrunkStage.Sober;
        PassingOut = false;
        passOutVisuals = false;
        passOutTip = 0f;
        nextPassOutCheck = -1f;
        RaiseChanged();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Local = null;
        Changed = null;
        level = 0f;
        decayedAtHours = -1;
        lastStage = DrunkStage.Sober;
        PassingOut = false;
        passOutVisuals = false;
        passOutTip = 0f;
        nextPassOutCheck = -1f;
    }

    // ------------------------------------------------------------------ instance (the symptoms)

    PlayerController pc;
    StarterAssetsInputs inputs;
    readonly DrunkPostFx postFx = new DrunkPostFx();
    RabiesCameraSway sway;
    float shown;            // smoothed vision strength actually displayed
    float seed;

    // drift
    bool driftActive, driftUnresolved;
    Vector2 driftBase, driftWritten;
    float stumbleUntil, nextStumble, nextHiccup = -1f;
    Vector2 stumbleDir;
    Coroutine restoreLoop;

    static readonly string[] StumbleWords = { "whoops", "wheee", "steady...", "who moved the floor", "I'm fine" };
    static readonly string[] HicWords = { "*hic*", "*HIC*", "*hic!*", "*burp*" };
    static readonly Color TextColor = new Color(1f, 0.82f, 0.42f);

    void Awake()
    {
        pc = GetComponent<PlayerController>();
        inputs = GetComponent<StarterAssetsInputs>();
        seed = NightDice.Cosmetic.Range(0f, 100f);
        nextStumble = Time.time + NightDice.Cosmetic.Range(5f, 9f);
    }

    void OnEnable() => restoreLoop = StartCoroutine(RestoreAfterUpdate());

    void OnDisable()
    {
        if (restoreLoop != null) { StopCoroutine(restoreLoop); restoreLoop = null; }
        RestoreAll();
    }

    void OnDestroy()
    {
        RestoreAll();
        if (Local == this) Local = null;
    }

    bool IsLocalPlayer => pc != null && NightPlayers.IsLocal(pc);

    void Update()
    {
        if (!IsLocalPlayer) return;
        Local = this;
        ReconcileDrift();

        float lv = level;
        float target = passOutVisuals ? 1f : DrunkRules.Vision01(lv);
        shown = Mathf.MoveTowards(shown, target, Time.unscaledDeltaTime * (target > shown ? 0.6f : 0.35f));
        float walk = DrunkRules.Walk01(lv);

        if (shown <= 0.001f && walk <= 0.001f && !PassingOut)
        {
            if (postFx.Alive || sway != null) RestoreVisuals();
            DrunkPostFx.SettleCamera(); // sobered up while rabid: the camera flag goes back when rabies ends too
            RemoveDrift();
            return;
        }

        ApplyVisuals(shown);
        ApplyDrift(walk);
        Hiccup(lv);
        PassOutCheck(lv);
    }

    void LateUpdate()
    {
        RemoveDrift();
        ReconcileDrift();
    }

    // ------------------------------------------------------------------ visuals

    void ApplyVisuals(float v)
    {
        postFx.Apply(v * GameSettings.ScreenFx01, Time.time + seed); // Settings → Accessibility → Screen effects

        var cam = Camera.main;
        if (cam == null) return;
        if (sway == null || sway.gameObject != cam.gameObject)
        {
            if (sway != null) sway.RemoveDrunk();
            sway = cam.GetComponent<RabiesCameraSway>();
            if (sway == null) sway = cam.gameObject.AddComponent<RabiesCameraSway>();
        }
        bool driving = pc != null && pc.IsDriving;
        // RabiesCameraSway applies ScreenFx01 itself and takes the stronger of the rabies and drunk sway.
        sway.drunkAmount = v * (driving ? 0.45f : 0.75f);
        sway.drunkSpin = v * (driving ? 0.6f : 1f);
        sway.drunkRoll = passOutTip * 78f;
        sway.drunkPitch = passOutTip * 22f;
    }

    void RestoreVisuals()
    {
        postFx.Dispose();
        if (sway != null) { sway.RemoveDrunk(); sway = null; }
        else
        {
            var cam = Camera.main;
            var s = cam != null ? cam.GetComponent<RabiesCameraSway>() : null;
            if (s != null) s.RemoveDrunk();
        }
        shown = 0f;
    }

    void RestoreAll()
    {
        RemoveDrift();
        ReconcileDrift();
        RestoreVisuals();
    }

    // ------------------------------------------------------------------ hiccups

    /// <summary>A little involuntary hop now and then (real movement, so everyone sees it). Rabies has its own; no doubles.</summary>
    void Hiccup(float lv)
    {
        if (PassingOut || pc == null || pc.IsDriving || pc.IsPlayingChess || DrunkRules.Stage(lv) == DrunkStage.Sober) return;
        if (RabiesStatus.Intensity01 >= 0.1f) return;
        if (nextHiccup < 0f) { nextHiccup = Time.time + NightDice.Cosmetic.Range(4f, 9f); return; }
        if (Time.time < nextHiccup) return;
        float w = Mathf.Clamp01(lv / DrunkRules.MaxLevel);
        nextHiccup = Time.time + NightDice.Cosmetic.Range(7f, 16f) / Mathf.Lerp(0.8f, 1.7f, w);
        var kb = GetComponent<CritterKnockback>();
        if (kb == null) kb = gameObject.AddComponent<CritterKnockback>();
        if (!kb.IsKnocking) kb.Knock(Vector3.zero, 0f, Mathf.Lerp(0.18f, 0.38f, w), 0.22f);
        Vector3 head = transform.position + Vector3.up * 1.8f;
        GameHUD.FloatingText(head, NightDice.Cosmetic.Pick(HicWords), TextColor, 1.05f);
        Sfx.Play("squeak", head, 0.55f, NightDice.Cosmetic.Range(1.25f, 1.6f));
    }

    // ------------------------------------------------------------------ movement drift

    bool InputIsOurs =>
        inputs != null && pc != null && !PlayerController.IsUIBlocking && !pc.IsDriving && !pc.IsPlayingChess
        && PlayerController.InteractionFocusOverride == null && !SceneController.IsTransitioning && !PassingOut
        && RabiesStatus.CurrentStage != RabiesStatus.Stage.PassingOut
        && (TrashCarrier.Local == null || !TrashCarrier.Local.ExternalControl);

    void ApplyDrift(float w)
    {
        if (driftActive) RemoveDrift();
        if (!InputIsOurs || w <= 0.01f) return;

        // RabiesStatus (-80) has already added its own drift this frame; ours goes on top of whatever is there.
        Vector2 baseMove = inputs.move;
        Vector2 drift = Vector2.zero;
        float t = Time.time + seed;
        if (baseMove.sqrMagnitude > 0.01f)
        {
            // A long lazy weave with a shorter wobble on it: you cannot walk a straight line.
            Vector2 perp = new Vector2(-baseMove.y, baseMove.x).normalized;
            drift = perp * (Mathf.Sin(t * 0.55f) * 0.4f + Mathf.Sin(t * 1.7f) * 0.14f) * w;
        }

        float now = Time.time;
        if (now < stumbleUntil) drift += stumbleDir;
        else if (now >= nextStumble && w > 0.25f)
        {
            nextStumble = now + NightDice.Cosmetic.Range(6f, 14f) / Mathf.Lerp(0.7f, 1.8f, w);
            stumbleUntil = now + NightDice.Cosmetic.Range(0.3f, 0.55f);
            Vector3 dir = NightDice.Cosmetic.OnCircle(Mathf.Lerp(0.5f, 1.05f, w));
            stumbleDir = new Vector2(dir.x, dir.z);
            Vector3 head = transform.position + Vector3.up * 1.8f;
            GameHUD.FloatingText(head, NightDice.Cosmetic.Pick(StumbleWords), TextColor, 0.85f);
            Sfx.Play("squeak", transform.position, 0.35f, NightDice.Cosmetic.Range(0.55f, 0.75f));
            Juice.Shake(0.06f, 0.2f);
        }

        if (drift.sqrMagnitude < 0.0001f) return;
        driftBase = baseMove;
        driftWritten = baseMove + drift;
        inputs.move = driftWritten;
        driftActive = true;
        driftUnresolved = false;
    }

    /// <summary>
    /// Runs after every Update and before any LateUpdate: take our drift off before RabiesStatus (LateUpdate) takes its own
    /// off, so the two unwind in the reverse of the order they were applied and neither leaves a residue in the input.
    /// </summary>
    IEnumerator RestoreAfterUpdate()
    {
        while (true)
        {
            yield return null;
            RemoveDrift();
        }
    }

    void RemoveDrift()
    {
        if (!driftActive) return;
        driftActive = false;
        if (inputs == null) return;
        // Only put it back if nothing (a fresh input event, a UI lock, another modifier on top) overwrote it meanwhile.
        if (inputs.move == driftWritten) inputs.move = driftBase;
        else driftUnresolved = true;
    }

    /// <summary>Another input modifier applied on top of ours has since handed back exactly what we wrote: finish the undo.</summary>
    void ReconcileDrift()
    {
        if (!driftUnresolved || driftActive) return;
        if (inputs != null && inputs.move == driftWritten) { inputs.move = driftBase; driftUnresolved = false; }
    }

    // ------------------------------------------------------------------ pass out

    static bool CanPassOut =>
        !PassingOut && RabiesStatus.CurrentStage != RabiesStatus.Stage.PassingOut && NightPlayers.LocalPlayer != null
        && !SceneController.IsTransitioning && !ScreenFader.IsFading && !DepotRowWorld.Transitioning;

    void PassOutCheck(float lv)
    {
        if (DrunkRules.Stage(lv) != DrunkStage.Wasted) { nextPassOutCheck = -1f; return; }
        float now = Time.unscaledTime;
        if (nextPassOutCheck < 0f) { nextPassOutCheck = now + (lv >= DrunkRules.MaxLevel ? 1.5f : PassOutCheckSeconds); return; }
        if (now < nextPassOutCheck) return;
        // Not in the middle of a screen (the bar, a game table) or another transition: the roll waits for a clear moment.
        if (!CanPassOut || PlayerController.IsUIBlocking) return;
        nextPassOutCheck = now + PassOutCheckSeconds;
        if (Dice.Value < DrunkRules.PassOutChance(lv, PassOutCheckSeconds)) PassOutNow();
    }

    /// <summary>Pass out right now (the roll came up, or the debug menu). False if a pass-out or a transition is already running.</summary>
    public static bool PassOutNow()
    {
        if (!CanPassOut) return false;
        var runner = DrunkRunner.Instance;
        MonoBehaviour host = runner != null ? runner : Local;
        if (host == null || !host.isActiveAndEnabled) return false;
        host.StartCoroutine(PassOutRoutine());
        return true;
    }

    static IEnumerator PassOutRoutine()
    {
        PassingOut = true;
        passOutVisuals = true;
        passOutStartedAt = Time.unscaledTime;
        RaiseChanged();
        GameHUD.Toast("One too many... *thud*", ToastStyle.Bad, 2.5f);
        Sfx.Play("squeak", null, 0.7f, 0.55f);

        // Keel over (the same tip RabiesStatus uses, through the shared camera sway).
        float t = 0f;
        while (t < 1.3f)
        {
            t += Time.unscaledDeltaTime;
            passOutTip = Mathf.SmoothStep(0f, 1f, t / 1.3f);
            yield return null;
        }
        Sfx.Play("thud", null, 1f, 0.7f);
        Juice.Shake(0.35f, 0.3f);

        // Co-op: flop where you stand; friends can carry you. No cab, no fare.
        var hn = HandsNet.Instance;
        bool coop = NetSession.InSession && !NetSession.IsSolo && !SessionService.SoloOnly;
        if (coop && hn != null && hn.Active)
        {
            hn.LocalFlop(true);
            GameHUD.Toast("You passed out. A friend can carry you somewhere safe...", ToastStyle.Bad, 4f);
            passOutTip = 0.3f;
            float waitUntil = Time.unscaledTime + DrunkRules.CoopFlopSeconds;
            // A glass of water (or anything else that sobers you to the waking level) gets you up early.
            while (Time.unscaledTime < waitUntil && level > DrunkRules.WakeLevel + 0.001f && NetSession.InSession) yield return null;
            if (hn != null) hn.LocalFlop(false);
            Wake();
            GameHUD.Toast("You come round. Your head is pounding.", ToastStyle.Info, 3.5f);
            PassingOut = false;
            RaiseChanged();
            yield break;
        }

        yield return ScreenFader.FadeOut(0.6f, false);

        // Solo: a cab takes you home, and the meter was running.
        float fare = DrunkRules.CabFareCents / 100f;
        Ledger.Charge(fare, "Cab fare", LedgerIds.CabFare); // the one money funnel (Multiplayer.md §9 rule 1)
        Wake(); // hung over, not sober (the black screen hides the snap)
        string price = DrunkRules.CabFareCents % 100 == 0 ? "$" + (DrunkRules.CabFareCents / 100) : Economy.Money(fare);
        string msg = $"You woke up at the depot. The cab cost {price}.";

        int lot = DepotRowWorld.LocalLot;
        if (DepotRowWorld.Active && lot >= 0)
        {
            // The one world: your own shop is a door away, not a scene away. The truck stays where you left it.
            DepotRowWorld.LocalGoInside(lot, false);
            yield return null;
            float guard = Time.unscaledTime + 6f;
            while (DepotRowWorld.Transitioning && Time.unscaledTime < guard) yield return null;
            GameHUD.Toast(msg, ToastStyle.Bad, 4f);
            Sfx.Play("buzz", null, 0.7f, 0.8f);
            if (ScreenFader.Alpha > 0.01f && !ScreenFader.IsFading) yield return ScreenFader.FadeIn(0.8f);
        }
        else
        {
            // Scene-per-place flow: the same trip home as a rabies pass-out (truck towed), or wake where you fell.
            yield return RabiesStatus.ArriveAtGarage(msg);
        }
        PassingOut = false;
        RaiseChanged();
    }

    /// <summary>Come round at the waking level. Rabies is untouched: passing out drunk cures nothing.</summary>
    static void Wake()
    {
        passOutVisuals = false;
        passOutTip = 0f;
        nextPassOutCheck = -1f;
        SetLevel(Mathf.Min(level, DrunkRules.WakeLevel), false);
        if (Local != null) Local.shown = DrunkRules.Vision01(level);
    }
}

/// <summary>
/// Keeps the drunk status going on this machine: created at startup (no scene or prefab edit), it ticks the level, puts a
/// <see cref="DrunkStatus"/> on the local player and a <see cref="DrunkLook"/> on every player puppet (the local one too, so
/// the third-person camera shows you staggering), and carries <see cref="DrunkDriving"/>.
/// </summary>
public sealed class DrunkRunner : MonoBehaviour
{
    public static DrunkRunner Instance { get; private set; }
    float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Instance != null) return;
        var go = new GameObject("[Drunk]");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<DrunkRunner>();
        go.AddComponent<DrunkDriving>();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Instance = null;

    void Update()
    {
        DrunkStatus.TickLevel();
        float now = Time.unscaledTime;
        if (now < nextScan) return;
        nextScan = now + 1f;
        var me = NightPlayers.LocalPlayer;
        if (me != null && me.GetComponent<DrunkStatus>() == null) me.gameObject.AddComponent<DrunkStatus>();
        var all = NightPlayers.All;
        for (int i = 0; i < all.Count; i++)
        {
            var pc = all[i];
            if (pc != null && pc.GetComponent<DrunkLook>() == null) pc.gameObject.AddComponent<DrunkLook>();
        }
    }
}
