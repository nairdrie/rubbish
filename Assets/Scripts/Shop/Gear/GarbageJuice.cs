using System.Collections;
using UnityEngine;

/// <summary>
/// Garbage Juice™ Energy Drink (consumable garbage_juice, Docs/Progression.md §6.6): press J to chug one from your pocket:
/// +25% move and carry speed for 2 in-game hours (applied by <see cref="BootsMods"/>), plus hiccups. Drinking another
/// while it's active refreshes the timer (no stacking). The buff is personal: a per-machine static for the local player
/// (it survives the respawn on every scene change; it isn't saved: 2 in-game hours is 100 real seconds).
/// </summary>
[DisallowMultipleComponent]
public class GarbageJuice : MonoBehaviour
{
    public KeyCode chugKey = KeyCode.J;
    public const float SpeedBoost = 1.25f;
    public const double DurationHours = 2.0;

    /// <summary>GameClock.TotalHours when the local player's juice wears off (-1 = none).</summary>
    public static double ActiveUntilHours = -1;

    /// <summary>True while the local player is juiced (a stale stamp from an old clock is ignored).</summary>
    public static bool Active
    {
        get
        {
            if (ActiveUntilHours < 0) return false;
            double left = ActiveUntilHours - GameClock.TotalHours;
            return left > 0 && left <= DurationHours + 0.01;
        }
    }

    /// <summary>Speed multiplier for the local player: 1.25 while juiced, else 1.</summary>
    public static float SpeedMult => Active ? SpeedBoost : 1f;

    PlayerController pc;
    bool wasActive;
    float nextHicAt;

    void Awake() => pc = GetComponent<PlayerController>();

    void Update()
    {
        if (pc == null || !pc.IsOwner) return;
        bool active = Active;
        if (wasActive && !active)
        {
            GameHUD.Toast("The Garbage Juice wore off. You feel... normal-ish. *hic*", ToastStyle.Info, 2.5f);
            Sfx.Play("pop", transform.position, 0.5f, 0.7f);
        }
        wasActive = active;

        if (!PlayerController.IsUIBlocking && !SceneController.IsTransitioning && GameControls.Down(chugKey, GameControls.Action.Chug)) TryChug();

        if (active && Time.time >= nextHicAt)
        {
            nextHicAt = Time.time + ShopModKit.Range(3.5f, 8f);
            Hiccup();
        }
    }

    /// <summary>Drink one from your pocket. Returns false (with a toast) when you have none.</summary>
    public bool TryChug()
    {
        if (GameSession.GetConsumable(ShopIds.GarbageJuice) <= 0)
        {
            GameHUD.Toast($"No Garbage Juice™ in your pocket. {Scrapazon.Name} sells it ($15). It's fine. Probably.", ToastStyle.Info, 2.5f);
            Sfx.Play("buzz", transform.position, 0.4f, 1.2f);
            return false;
        }
        GameSession.AddConsumable(ShopIds.GarbageJuice, -1);
        ActiveUntilHours = GameClock.TotalHours + DurationHours;
        wasActive = true;
        nextHicAt = Time.time + 2.2f;
        if (isActiveAndEnabled) StartCoroutine(ChugFx());
        return true;
    }

    Vector3 Head => pc != null && pc.IsDriving && pc.currentTruck != null
        ? pc.currentTruck.transform.position + Vector3.up * 3.8f
        : transform.position + Vector3.up * 1.45f;

    IEnumerator ChugFx()
    {
        bool driving = pc != null && pc.IsDriving;
        Transform can = null;
        if (!driving)
        {
            var green = ShopMaterials.Get("mat/shop_juice_green", new Color(0.45f, 0.85f, 0.15f), 0.6f, 0.4f);
            var brown = ShopMaterials.Get("mat/shop_juice_brown", new Color(0.4f, 0.28f, 0.12f), 0.3f);
            var chrome = ShopMaterials.Get("mat/shop_chrome", new Color(0.86f, 0.88f, 0.92f), 0.85f, 0.9f);
            can = new GameObject("Garbage Juice Can").transform;
            can.SetParent(transform, false);
            ShopModKit.Prim(PrimitiveType.Cylinder, "Can", can, green, Vector3.zero, new Vector3(0.09f, 0.08f, 0.09f));
            ShopModKit.Prim(PrimitiveType.Cylinder, "Label", can, brown, Vector3.zero, new Vector3(0.093f, 0.035f, 0.093f));
            ShopModKit.Prim(PrimitiveType.Cylinder, "Lid", can, chrome, new Vector3(0f, 0.082f, 0f), new Vector3(0.085f, 0.004f, 0.085f));
            Juice.PopIn(can, 0.2f);
        }
        Sfx.Play("pop", Head, 0.8f, 1.3f);
        GameHUD.FloatingText(Head + Vector3.up * 0.4f, "*crack*", new Color(0.6f, 1f, 0.3f), 0.8f);
        float t = 0f;
        while (t < 1.1f)
        {
            t += Time.deltaTime;
            if (can != null)
            {
                float tilt = Mathf.SmoothStep(0f, 150f, Mathf.Clamp01(t / 0.35f));
                can.localPosition = new Vector3(0.05f, 1.32f + 0.1f * Mathf.Clamp01(t / 0.35f), 0.28f);
                can.localRotation = Quaternion.Euler(-tilt, 0f, 0f);
            }
            if (t > 0.35f && t - Time.deltaTime <= 0.35f) GameHUD.FloatingText(Head + Vector3.up * 0.5f, "GLUG GLUG GLUG", new Color(0.55f, 0.95f, 0.25f), 1f);
            yield return null;
        }
        if (can != null) Juice.ShrinkAndDestroy(can.gameObject, 0.2f);
        Vector3 h = Head;
        Sfx.Play("splat", h, 0.5f, 1.4f);
        Sfx.Play("legendary", h, 0.25f, 1.8f);
        GameHUD.FloatingText(h + Vector3.up * 0.7f, "BURRRRP! +25% SPEED", new Color(0.55f, 1f, 0.3f), 1.4f);
        Fx.Burst(h, new Color(0.5f, 0.95f, 0.2f), 22, 4f);
        Juice.Shake(0.12f, 0.2f);
        GameHUD.Toast("GARBAGE JUICE™: +25% speed for 2 hours. Side effects include hiccups and speed.", ToastStyle.Good, 2.5f);
    }

    static readonly string[] Hics = { "*hic*", "*HIC*", "*hic!*", "hic." };

    void Hiccup()
    {
        Vector3 h = Head;
        Sfx.Play("pop", h, 0.45f, ShopModKit.Range(1.6f, 2.1f));
        GameHUD.FloatingText(h + Vector3.up * 0.3f, ShopModKit.Pick(Hics), new Color(0.6f, 1f, 0.35f), 0.8f);
        Fx.Burst(h, new Color(0.6f, 1f, 0.4f), 4, 1.5f);
        Juice.Shake(0.04f, 0.08f);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => ActiveUntilHours = -1;
}
