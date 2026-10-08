using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// DontDestroyOnLoad heartbeat for <see cref="ShopEffects"/>: re-applies upgrades on scene load, local player
/// spawn and truck spawn, runs the rabies-shot injector (purchase while infected / stored shot on infection)
/// and the repellent stink aura. Created automatically at startup in play mode; never place one in a scene.
/// </summary>
public class ShopEffectsRunner : MonoBehaviour
{
    static ShopEffectsRunner instance;

    PlayerController lastPlayer;
    float nextTruckScan, nextAuraCheck;
    bool auraWanted;
    float rabiesShotAt = -1f;
    float cureWatchAt = -1f;
    double cureWatchStamp;
    GameObject aura;
    bool applyPending = true;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (instance != null) return;
        var go = new GameObject("[ShopEffects]");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<ShopEffectsRunner>();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => instance = null;

    // ---------------- static hooks used by ShopEffects ----------------

    internal static void EnsureAttached()
    {
        if (instance == null) return;
        instance.AttachToPlayer(true);
        instance.ScanTrucks();
    }

    internal static void ScheduleRabiesShot(float delay)
    {
        if (instance == null) { ShopEffects.TryUseRabiesShot(); return; }
        instance.rabiesShotAt = Time.unscaledTime + Mathf.Max(0f, delay);
    }

    internal static void WatchCure(double infectedAtHours)
    {
        if (instance == null) return;
        instance.cureWatchAt = Time.unscaledTime + 8f;
        instance.cureWatchStamp = infectedAtHours;
    }

    // ---------------- lifecycle ----------------

    void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
        GameSession.UpgradeChanged += OnUpgradeChanged;
        GameSession.RabiesChanged += OnRabiesChanged;
        GameSession.InfectionResisted += OnInfectionResisted;
    }

    void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        GameSession.UpgradeChanged -= OnUpgradeChanged;
        GameSession.RabiesChanged -= OnRabiesChanged;
        GameSession.InfectionResisted -= OnInfectionResisted;
    }

    void OnDestroy()
    {
        if (instance == this) instance = null;
        if (aura != null) Destroy(aura);
    }

    void OnSceneLoaded(Scene s, LoadSceneMode m)
    {
        applyPending = true;
        nextTruckScan = 0f;
    }

    void OnUpgradeChanged(string id, int level)
    {
        if (id == ShopIds.TruckCapacity || id == ShopIds.TruckModel) ShopEffects.ApplyTruckCapacity();
        if (id == ShopIds.RabiesVaccine) ShopEffects.ApplyInfectionResist();
        if (id == ShopIds.Headlamp || id == ShopIds.WorkGloves || id == ShopIds.WorkBoots || id == ShopIds.TreasureWand)
            AttachToPlayer(true);
        if (id == ShopIds.TruckTurbo || id == ShopIds.AirHorn) ScanTrucks();
    }

    /// <summary>A rabid bite bounced off the vaccine: make it a moment.</summary>
    void OnInfectionResisted()
    {
        var p = lastPlayer;
        GameHUD.Toast(UpgradeEffects.InfectionResist >= 1f ? "Rabies? Not today. You're IMMUNE. (Smug.)" : "The vaccine worked! That bite didn't take.", ToastStyle.Good, 2.5f);
        Sfx.Play("syringe", null, 0.4f, 1.3f);
        if (p == null) return;
        Vector3 head = p.transform.position + Vector3.up * 1.9f;
        GameHUD.FloatingText(head, "IMMUNE!", new Color(0.55f, 1f, 0.45f), 1.4f);
        Fx.Burst(head, new Color(0.55f, 1f, 0.45f), 18, 3f);
    }

    void OnRabiesChanged(bool infected)
    {
        // Got bitten with a shot in your pocket: let the bite land, then auto-inject.
        if (infected && ShopEffects.RabiesShots > 0 && rabiesShotAt < 0f && FeralStatus.LocalAutoShot) // Embrace It: no auto-shot
            rabiesShotAt = Time.unscaledTime + 2.2f; // CritterSpawner auto-injects at 1.5 s; we are the fallback
    }

    void Update()
    {
        float now = Time.unscaledTime;

        if (applyPending)
        {
            applyPending = false;
            ShopEffects.ApplyTruckCapacity();
            ShopEffects.ApplyInfectionResist();
            if (GameSession.HasRabies && ShopEffects.RabiesShots > 0 && rabiesShotAt < 0f && FeralStatus.LocalAutoShot) rabiesShotAt = now + 2.2f;
        }

        if (PlayerController.Local != lastPlayer) AttachToPlayer(false);

        if (now >= nextTruckScan)
        {
            nextTruckScan = now + 0.5f;
            ScanTrucks();
        }

        if (rabiesShotAt >= 0f && now >= rabiesShotAt)
        {
            rabiesShotAt = -1f;
            if (EbaeUI.IsOpen) EbaeUI.Instance.Hide(); // the syringe moment happens in the world, not behind the laptop
            if (ShopEffects.TryUseRabiesShot())
                Debug.Log($"[Shop] Rabies Shot used ({ShopEffects.RabiesShots} left).");
        }

        // RabiesStatus took over the cure: make sure it actually happened (never leave a paid shot unapplied).
        if (cureWatchAt >= 0f && now >= cureWatchAt)
        {
            cureWatchAt = -1f;
            if (GameSession.HasRabies && System.Math.Abs(GameSession.RabiesInfectedAtHours - cureWatchStamp) < 1e-6)
            {
                Debug.LogWarning("[Shop] RabiesStatus.CureWithShot() didn't cure; curing directly.");
                GameSession.CureRabies();
                ShopEffects.CureJuice();
            }
        }

        if (now >= nextAuraCheck)
        {
            nextAuraCheck = now + 0.5f;
            bool want = ShopEffects.RepellentActive;
            if (want != auraWanted)
            {
                auraWanted = want;
                if (!want && PlayerController.Local != null)
                {
                    GameHUD.Toast("Raccoon-Be-Gone wore off. You smell normal again. Sort of.", ToastStyle.Info);
                    Sfx.Play("pop", null, 0.6f, 0.8f);
                }
            }
            UpdateAura();
        }
    }

    // ---------------- player / truck attachment ----------------

    void AttachToPlayer(bool force)
    {
        var p = PlayerController.Local;
        bool changed = p != lastPlayer;
        lastPlayer = p;
        if (p == null) return;
        if ((changed || force) && ShopEffects.HasHeadlamp && p.GetComponent<ShopHeadlamp>() == null)
            p.gameObject.AddComponent<ShopHeadlamp>();
        // Personal gear (Progression §6.4): one hub that adds boots / Back Sack / wand / UV lamp / Garbage Juice and applies
        // the gloves; each part reads its own level, so it's always attached.
        if (changed || force)
        {
            var gear = p.GetComponent<GearMods>();
            if (gear == null) p.gameObject.AddComponent<GearMods>();
            else gear.Apply();
        }
        if (changed)
        {
            ShopEffects.ApplyInfectionResist(); // re-applied on spawn (InfectionResist01 isn't saved)
            UpdateAura();
        }
    }

    /// <summary>Every truck carries every truck mod; each one reads its own upgrade level (Progression §6.1).</summary>
    void ScanTrucks()
    {
        var trucks = FindObjectsByType<TruckControllerV2>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        foreach (var t in trucks)
        {
            if (t == null) continue;
            var go = t.gameObject;
            if (go.GetComponent<TruckModelVisuals>() == null) go.AddComponent<TruckModelVisuals>();
            if (go.GetComponent<ShopTruckMods>() == null) go.AddComponent<ShopTruckMods>();
            if (go.GetComponent<TruckHandlingMods>() == null) go.AddComponent<TruckHandlingMods>();
            if (go.GetComponent<TruckRecovery>() == null) go.AddComponent<TruckRecovery>();
            if (go.GetComponent<TruckSideLoader>() == null) go.AddComponent<TruckSideLoader>();
            if (go.GetComponent<TruckTipper>() == null) go.AddComponent<TruckTipper>();
            if (go.GetComponent<GoldenTruckFx>() == null) go.AddComponent<GoldenTruckFx>();
        }
    }

    // ---------------- repellent aura (the Critters system spends the bottles) ----------------

    /// <summary>Stink lines follow you around while the repellent is on (you smell awful; that's the point).</summary>
    void UpdateAura()
    {
        var p = PlayerController.Local;
        bool want = ShopEffects.RepellentActive && p != null;
        if (!want)
        {
            if (aura != null) Destroy(aura);
            aura = null;
            return;
        }
        if (aura != null && aura.transform.parent == p.transform) return;
        if (aura != null) Destroy(aura);
        aura = new GameObject("Repellent Aura");
        aura.transform.SetParent(p.transform, false);
        aura.transform.localPosition = new Vector3(0f, 1.1f, 0f);
        Fx.Stink(aura.transform);
    }
}
