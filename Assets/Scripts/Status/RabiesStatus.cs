using System.Collections;
using StarterAssets;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Rabies on the local player. Auto-attached to PlayerController.Local at runtime by CritterSpawner (no prefab edit).
/// State lives in GameSession (RabiesInfectedAtHours / Infect / CureRabies); this component is only the symptoms.
///
/// Timeline (in-game hours since the bite, GameClock):
///   0 → Economy.RabiesIncubationHours       Incubating: nothing yet (toast on infection).
///   → ramps over ~3 h to 75 %, → 100 % at pass-out   Symptomatic: trippy URP volume (lens wobble, chromatic
///       aberration, hue drift, green vignette), camera sway, drifting/stumbling movement, random hissing, foam.
///   Economy.RabiesPassOutHours              Pass out: world tips over, fade, wake up in the garage, pay
///       Economy.HospitalBill (can go negative), cured.
/// Cure any time with <see cref="CureWithShot"/> (Scrapazon 'rabies_shot'; a stored shot is auto-used on infection).
/// Everything (volume, camera, inputs, foam) is restored on cure, scene change, disable and destroy.
/// F4 night shift (Docs/Multiplayer.md §14.4): rabies is per player in co-op (<see cref="NightNet"/> keeps a host record keyed
/// by PlayerKey, so it survives a reconnect, and everyone sees everyone's foam); symptomatic players get goofy hiccups (a
/// little hop + "*HIC*"); in the one world, passing out means lying there for up to 30 s so a friend can jab you with a
/// Rabies Shot (<see cref="RabiesJab"/>), then the hospital bill where you fell (no scene trip).
///
/// Movement drift nudges StarterAssetsInputs.move before the ThirdPersonController reads it (execution order -80)
/// and puts the real input back in LateUpdate — never while a UI lock / the sort station / driving owns the input.
/// </summary>
[DefaultExecutionOrder(-80)]
[DisallowMultipleComponent]
public class RabiesStatus : MonoBehaviour
{
    public enum Stage { Healthy, Incubating, Symptomatic, PassingOut }

    /// <summary>The status component on the local player (null when there's no local player).</summary>
    public static RabiesStatus Local { get; private set; }

    // ------------------------------------------------------------------ static read API (HUD / Shop)

    public static bool IsInfected => GameSession.HasRabies;
    public static double HoursInfected => GameSession.HasRabies ? GameClock.AgeHours(GameSession.RabiesInfectedAtHours) : 0.0;
    public static float HoursUntilSymptoms => GameSession.HasRabies ? Mathf.Max(0f, Economy.RabiesIncubationHours - (float)HoursInfected) : 0f;
    public static float HoursUntilPassOut => GameSession.HasRabies ? Mathf.Max(0f, Economy.RabiesPassOutHours - (float)HoursInfected) : 0f;

    public static Stage CurrentStage
    {
        get
        {
            if (passOutInProgress) return Stage.PassingOut;
            if (!GameSession.HasRabies) return Stage.Healthy;
            return HoursInfected < Economy.RabiesIncubationHours ? Stage.Incubating : Stage.Symptomatic;
        }
    }

    /// <summary>0..1 symptom strength (0 while healthy / incubating).</summary>
    public static float Intensity01
    {
        get
        {
            if (passOutVisuals) return 1f;
            if (!GameSession.HasRabies) return 0f;
            float h = (float)HoursInfected - Economy.RabiesIncubationHours;
            if (h <= 0f) return 0f;
            float ramp = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(h / 3f)) * 0.75f;
            float lateStart = Economy.RabiesPassOutHours - Economy.RabiesIncubationHours - 5f;
            float late = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((h - lateStart) / 5f)) * 0.25f;
            return Mathf.Clamp01(Mathf.Max(0.15f, ramp) + late);
        }
    }

    /// <summary>
    /// Cure rabies right now with the giant comedic syringe. Returns true if the player was infected (cured),
    /// false if they were healthy (nothing happens — the Shop keeps the shot in stock). Safe from any scene.
    /// </summary>
    public static bool CureWithShot()
    {
        if (!GameSession.HasRabies) return false;
        GameSession.CureRabies();
        try { RabiesSyringe.Play(PlayerController.Local); }
        catch (System.Exception e) { Debug.LogException(e); GameHUD.Toast("OWWW. Cured!", ToastStyle.Good); }
        return true;
    }

    // ------------------------------------------------------------------ instance

    PlayerController pc;
    StarterAssetsInputs inputs;
    readonly RabiesPostFx postFx = new RabiesPostFx();
    RabiesCameraSway sway;
    FoamBubbles foam;
    float shown;          // smoothed intensity actually displayed
    float passOutTip;     // 0..1 while keeling over

    // drift
    bool driftActive;
    Vector2 driftBase, driftWritten;
    float stumbleUntil, nextStumble, nextHiss;
    Vector2 stumbleDir;

    // messages
    bool toldSymptoms, warnedPassOut;
    double nextReminderAt;

    static bool passOutInProgress;   // the pass-out sequence owns the flow (no second trigger)
    static bool passOutVisuals;      // full-strength symptoms while keeling over (off the moment you're cured)
    static float passOutStartedAt;

    static readonly string[] HissWords = { "hssss", "HSSSS!", "*hiss*", "grrrhss" };
    static readonly string[] StumbleWords = { "whoa-", "wheee", "*hic*", "the floor moved", "uhh" };

    void Awake()
    {
        pc = GetComponent<PlayerController>();
        inputs = GetComponent<StarterAssetsInputs>();
        nextStumble = Time.time + Random.Range(6f, 10f);
        nextHiss = Time.time + Random.Range(8f, 14f);
        // Respawned mid-infection (scene change): don't repeat the announcements we already made.
        if (GameSession.HasRabies)
        {
            toldSymptoms = HoursInfected >= Economy.RabiesIncubationHours;
            warnedPassOut = HoursUntilPassOut <= 2f;
            nextReminderAt = GameClock.TotalHours + 1.5;
        }
    }

    void OnEnable()
    {
        GameSession.RabiesChanged += OnRabiesChanged;
        SceneManager.activeSceneChanged += OnActiveSceneChanged;
    }

    void OnDisable()
    {
        GameSession.RabiesChanged -= OnRabiesChanged;
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
        RestoreAll();
    }

    void OnDestroy()
    {
        RestoreAll();
        if (Local == this) Local = null;
    }

    bool IsLocalPlayer => pc != null && (pc == PlayerController.Local || pc.IsOwner);

    void OnRabiesChanged(bool infected)
    {
        if (!IsLocalPlayer) return;
        if (infected)
        {
            toldSymptoms = false;
            warnedPassOut = false;
            nextReminderAt = GameClock.TotalHours + Economy.RabiesIncubationHours + 3.0;
            // A packed shot auto-injects ~1.5 s later ("Good thing you packed a Rabies Shot!"): no shop advert then.
            if (FeralStatus.LocalEmbraced) { } // Embrace It: FeralStatus greets the foam instead
            else if (GameSession.GetConsumable(CritterSpawner.RabiesShotId) > 0)
                GameHUD.Toast("CHOMP. That's rabies. Uh oh...", ToastStyle.Bad, 1.8f);
            else
                GameHUD.Toast($"You got RABIES! Symptoms soon. {Scrapazon.Name} sells Rabies Shots.", ToastStyle.Bad, 3.5f);
            Sfx.Play("buzz", null, 0.7f, 0.8f);
            Juice.Shake(0.15f, 0.2f);
        }
        // Cured: effects fade out in Update, then RestoreAll.
    }

    void OnActiveSceneChanged(Scene from, Scene to) => RestoreAll();

    void Update()
    {
        if (!IsLocalPlayer) return;
        Local = this;

        float target = FeralStatus.LocalShown(Intensity01); // Embrace It: a stylised hint, not the full trip
        // Snap up quickly when symptoms start, fade out over ~1 s when cured.
        shown = Mathf.MoveTowards(shown, target, Time.unscaledDeltaTime * (target > shown ? 0.5f : 1.2f));
        if (passOutVisuals) shown = 1f;
        // Watchdog: never leave the pass-out lock stuck (e.g. its runner died with a scene).
        if (passOutInProgress && Time.unscaledTime - passOutStartedAt > 45f) { passOutInProgress = false; passOutVisuals = false; }

        if (shown <= 0.001f && !passOutVisuals)
        {
            if (postFx.Alive || sway != null || foam != null) RestoreAll();
            ApplyDriftNone();
            if (GameSession.HasRabies) Messages();
            return;
        }

        Messages();
        ApplyVisuals(shown);
        ApplyDrift(shown);
        Hiss(shown);
        Hiccup(shown);

        if (!passOutInProgress && FeralStatus.LocalPassesOut && GameSession.HasRabies && HoursInfected >= Economy.RabiesPassOutHours
            && !SceneController.IsTransitioning && !ScreenFader.IsFading)
        {
            var host = (MonoBehaviour)CritterSpawner.Instance ?? this;
            host.StartCoroutine(PassOutRoutine());
        }
    }

    void LateUpdate()
    {
        RemoveDrift();
    }

    // ------------------------------------------------------------------ visuals

    void ApplyVisuals(float i)
    {
        postFx.Apply(i * GameSettings.ScreenFx01, Time.time, transform); // Settings → Accessibility → Screen effects

        var cam = Camera.main;
        if (cam != null)
        {
            if (sway == null || sway.gameObject != cam.gameObject)
            {
                if (sway != null) sway.Remove();
                sway = cam.GetComponent<RabiesCameraSway>();
                if (sway == null) sway = cam.gameObject.AddComponent<RabiesCameraSway>();
            }
            bool driving = pc != null && pc.IsDriving;
            sway.amount = i * (driving ? 0.6f : 1f);
            sway.extraRoll = passOutTip * 78f;
            sway.extraPitch = passOutTip * 22f;
        }

        bool mouthVisible = pc != null && !pc.IsDriving && !pc.IsPlayingChess;
        if (foam == null)
        {
            foam = gameObject.AddComponent<FoamBubbles>();
            foam.size = 0.045f;
            // Chuck's mouth sits under his mustache (about 1.1 m up at root scale 0.8); fall back to the old mannequin height.
            var puppet = GetComponent<ChuckPuppet>();
            Transform mouth = puppet != null ? puppet.MouthPoint : null;
            foam.follow = () => mouth != null ? mouth.position + transform.forward * 0.05f
                                              : transform.position + Vector3.up * 1.5f + transform.forward * 0.2f;
            foam.forward = () => transform.forward;
        }
        foam.rate = mouthVisible ? Mathf.Lerp(1.5f, 9f, i) : 0f;
    }

    void Hiss(float i)
    {
        if (Time.time < nextHiss || passOutInProgress) return;
        nextHiss = Time.time + Random.Range(14f, 30f) / Mathf.Lerp(0.8f, 1.6f, i);
        if (pc == null || pc.IsPlayingChess) return;
        Vector3 head = transform.position + Vector3.up * 1.7f;
        Sfx.Play("hiss", head, 0.9f, Random.Range(0.85f, 1.1f));
        GameHUD.FloatingText(head + Vector3.up * 0.2f, HissWords[Random.Range(0, HissWords.Length)], new Color(0.55f, 1f, 0.35f), 1f);
        Juice.Shake(0.05f + 0.06f * i, 0.2f);
    }

    float nextHiccup = -1f;
    static readonly string[] HicWords = { "*HIC*", "*hic!*", "HIC", "*hiccup*" };

    /// <summary>Goofy rabies hiccups (F4): a little involuntary hop now and then. Everyone sees the hop (it's real movement).</summary>
    void Hiccup(float i)
    {
        if (passOutInProgress || pc == null || pc.IsDriving || pc.IsPlayingChess || i < 0.1f || !FeralStatus.LocalStumbles) return;
        if (nextHiccup < 0f) { nextHiccup = Time.time + NightDice.Cosmetic.Range(3f, 7f); return; }
        if (Time.time < nextHiccup) return;
        nextHiccup = Time.time + NightDice.Cosmetic.Range(5f, 12f) / Mathf.Lerp(0.8f, 1.6f, i);
        var kb = GetComponent<CritterKnockback>();
        if (kb != null && !kb.IsKnocking) kb.Knock(Vector3.zero, 0f, Mathf.Lerp(0.25f, 0.45f, i), 0.22f);
        Vector3 head = transform.position + Vector3.up * 1.8f;
        GameHUD.FloatingText(head, NightDice.Cosmetic.Pick(HicWords), new Color(0.6f, 1f, 0.45f), 1.1f);
        Sfx.Play("squeak", head, 0.6f, NightDice.Cosmetic.Range(1.5f, 1.9f));
    }

    void Messages()
    {
        if (!GameSession.HasRabies || passOutInProgress || FeralStatus.LocalEmbraced) return; // the embraced get no warnings: nothing to warn about
        double h = HoursInfected;
        if (!toldSymptoms && h >= Economy.RabiesIncubationHours)
        {
            toldSymptoms = true;
            nextReminderAt = GameClock.TotalHours + 3.0;
            GameHUD.Toast($"You don't feel so good... is the sidewalk breathing? (Rabies! Get a shot on {Scrapazon.Name})", ToastStyle.Bad, 4f);
            Sfx.Play("hiss", transform.position + Vector3.up * 1.7f, 0.8f, 0.9f);
        }
        float left = HoursUntilPassOut;
        if (!warnedPassOut && left <= 2f)
        {
            warnedPassOut = true;
            GameHUD.Toast("You're about to pass out!! Get to the garage laptop and buy a Rabies Shot!", ToastStyle.Bad, 4f);
            Sfx.Play("buzz", null, 0.9f, 0.7f);
            Juice.Shake(0.2f, 0.3f);
        }
        else if (toldSymptoms && GameClock.TotalHours >= nextReminderAt && left > 2f)
        {
            nextReminderAt = GameClock.TotalHours + 4.0;
            GameHUD.Toast($"Rabies: ~{Mathf.CeilToInt(left)} h until you pass out. {Scrapazon.Name} has shots!", ToastStyle.Bad, 3f);
        }
    }

    // ------------------------------------------------------------------ movement drift

    bool InputIsOurs =>
        inputs != null && pc != null && !PlayerController.IsUIBlocking && !pc.IsDriving && !pc.IsPlayingChess
        && PlayerController.InteractionFocusOverride == null && !SceneController.IsTransitioning && !passOutInProgress
        && (TrashCarrier.Local == null || !TrashCarrier.Local.ExternalControl);

    void ApplyDrift(float i)
    {
        if (driftActive) RemoveDrift();
        if (!InputIsOurs || i <= 0.01f || !FeralStatus.LocalStumbles) return;

        Vector2 baseMove = inputs.move;
        Vector2 drift = Vector2.zero;
        float t = Time.time;
        if (baseMove.sqrMagnitude > 0.01f)
        {
            // Veer side to side like the sidewalk is tilting.
            Vector2 perp = new Vector2(-baseMove.y, baseMove.x).normalized;
            drift = perp * (Mathf.Sin(t * 0.8f) * 0.32f + Mathf.Sin(t * 2.1f) * 0.12f) * i;
        }

        if (t < stumbleUntil) drift += stumbleDir;
        else if (t >= nextStumble && i > 0.2f)
        {
            nextStumble = t + Random.Range(7f, 15f) / Mathf.Lerp(0.8f, 1.8f, i);
            stumbleUntil = t + Random.Range(0.25f, 0.45f);
            stumbleDir = Random.insideUnitCircle.normalized * Mathf.Lerp(0.6f, 1.1f, i);
            Vector3 head = transform.position + Vector3.up * 1.8f;
            GameHUD.FloatingText(head, StumbleWords[Random.Range(0, StumbleWords.Length)], new Color(0.7f, 1f, 0.6f), 0.85f);
            Sfx.Play("squeak", transform.position, 0.4f, Random.Range(0.6f, 0.8f));
            if (sway != null) Juice.Shake(0.08f, 0.2f);
        }

        if (drift.sqrMagnitude < 0.0001f) return;
        driftBase = baseMove;
        driftWritten = baseMove + drift;
        inputs.move = driftWritten;
        driftActive = true;
    }

    void ApplyDriftNone()
    {
        if (driftActive) RemoveDrift();
    }

    void RemoveDrift()
    {
        if (!driftActive) return;
        driftActive = false;
        // Only put it back if nothing (a fresh input event, a UI lock) overwrote it meanwhile.
        if (inputs != null && inputs.move == driftWritten) inputs.move = driftBase;
    }

    // ------------------------------------------------------------------ restore

    void RestoreAll()
    {
        RemoveDrift();
        postFx.Dispose();
        if (sway != null) { sway.Remove(); sway = null; }
        else if (Camera.main != null)
        {
            var s = Camera.main.GetComponent<RabiesCameraSway>();
            if (s != null) s.Remove();
        }
        if (foam != null) { Destroy(foam); foam = null; }
        shown = 0f;
        passOutTip = 0f;
    }

    // ------------------------------------------------------------------ pass out

    static IEnumerator PassOutRoutine()
    {
        passOutInProgress = true;
        passOutVisuals = true;
        passOutStartedAt = Time.unscaledTime;
        GameHUD.Toast("Everything is spinning... *thud*", ToastStyle.Bad, 2.5f);
        Sfx.Play("hiss", null, 0.8f, 0.6f);

        // Keel over.
        float t = 0f;
        while (t < 1.3f)
        {
            t += Time.unscaledDeltaTime;
            if (Local != null) Local.passOutTip = Mathf.SmoothStep(0f, 1f, t / 1.3f);
            yield return null;
        }
        Sfx.Play("thud", null, 1f, 0.7f);
        Juice.Shake(0.35f, 0.3f);

        // Co-op one world (F4): lie there for up to 30 s; a friend can jab you with a Rabies Shot where you lie.
        if (DepotRowWorld.Active)
        {
            var hn = HandsNet.Instance;
            bool flopped = hn != null && hn.Active;
            if (flopped) hn.LocalFlop(true);
            GameHUD.Toast($"You passed out! A friend can jab you with a Rabies Shot ({Economy.Money(NightShift.RabiesShotPrice)})...", ToastStyle.Bad, 4f);
            float waitUntil = Time.unscaledTime + 30f;
            if (Local != null) Local.passOutTip = 0.3f;
            while (Time.unscaledTime < waitUntil && GameSession.HasRabies) yield return null;
            if (flopped && hn != null) hn.LocalFlop(false);
            if (!GameSession.HasRabies)
            {
                passOutVisuals = false;
                if (Local != null) Local.RestoreAll();
                passOutInProgress = false;
                yield break;
            }
        }
        yield return ScreenFader.FadeOut(0.6f, false);

        // Hospital: the full bill (yes, even if it puts you in the red), cure.
        float bill = Economy.HospitalBill;
        bool debt = GameSession.Cash < bill;
        Ledger.Charge(bill, "Hospital bill", LedgerIds.Hospital); // the one money funnel (Progression.md §9.1)
        GameSession.CureRabies();
        passOutVisuals = false;
        if (Local != null) Local.RestoreAll(); // wake up clear-headed (the black screen hides the snap)
        string msg = !debt
            ? $"You passed out foaming and woke up in the garage. Hospital bill: -{Economy.Money(bill)}"
            : $"You passed out. Hospital bill: -{Economy.Money(bill)}. You're in debt now. And missing a shoe.";

        var nm = NetworkManager.Singleton;
        var sc = SceneController.Instance;
        bool canTravel = !CritterWorld.InGarage && sc != null && nm != null && nm.IsServer && !DepotRowWorld.Active
                         && Application.CanStreamedLevelBeLoaded(SceneController.GarageSceneName);
        if (canTravel)
        {
            // You wake up in the garage, so the truck comes home too (otherwise it sits on some street 200 m from
            // the depot while the garage HUD still shows its load). Free tow: the hospital bill hurts enough.
            bool tow = !string.IsNullOrEmpty(GameSession.TruckSceneName) && GameSession.TruckSceneName != SceneController.GarageSceneName;
            string arrivalMsg = tow ? msg + (msg.EndsWith(".") ? "" : ".") + " A tow truck dragged your rig home." : msg;
            SceneTransitionData.PendingArrivalToast = arrivalMsg;
            SceneTransitionData.PendingArrivalToastStyle = ToastStyle.Bad;
            if (tow) ArmTow();
            sc.TransitionToScene(SceneController.GarageSceneName, "SpawnPoint_FromNeighborhood", false, "SpawnPoint_FromNeighborhood");
            if (SceneController.IsTransitioning)
            {
                while (SceneController.IsTransitioning) yield return null;
            }
            else
            {
                DisarmTow();
                SceneTransitionData.PendingArrivalToast = null;
                GameHUD.Toast(msg, ToastStyle.Bad, 4f);
                yield return ScreenFader.FadeIn(0.8f);
            }
            DisarmTow();
        }
        else
        {
            yield return new WaitForSecondsRealtime(1.2f);
            GameHUD.Toast(msg, ToastStyle.Bad, 4f);
            Sfx.Play("buzz", null, 0.7f, 0.8f);
            yield return ScreenFader.FadeIn(0.8f);
        }
        passOutInProgress = false;
    }

    // ------------------------------------------------------------------ tow the truck home on pass-out

    static bool towArmed;

    static void ArmTow()
    {
        if (towArmed) return;
        towArmed = true;
        SceneManager.sceneLoaded += OnSceneLoadedTow;
    }

    static void DisarmTow()
    {
        if (!towArmed) return;
        towArmed = false;
        SceneManager.sceneLoaded -= OnSceneLoadedTow;
    }

    // The scene controller records the truck as parked where you fell (on-foot transition), then loads the garage.
    // Once the garage is loaded — before the respawned player's TruckRequester runs — forget that parking spot:
    // with no truck recorded anywhere, TruckRequester spawns it at the garage's TruckSpawner and records it there.
    static void OnSceneLoadedTow(Scene scene, LoadSceneMode mode)
    {
        if (scene.name != SceneController.GarageSceneName) return;
        DisarmTow();
        if (string.IsNullOrEmpty(GameSession.TruckSceneName) || GameSession.TruckSceneName == SceneController.GarageSceneName) return;
        Debug.Log($"[RabiesStatus] Towing the truck home from '{GameSession.TruckSceneName}'.");
        GameSession.TruckSceneName = null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Local = null;
        passOutInProgress = false;
        passOutVisuals = false;
        SceneManager.sceneLoaded -= OnSceneLoadedTow;
        towArmed = false;
    }
}
