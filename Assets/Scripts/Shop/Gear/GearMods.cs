using UnityEngine;

/// <summary>
/// Personal gear hub (Docs/Progression.md §6.4, builder B), added to the local player by <see cref="ShopEffectsRunner"/>.
/// Ensures the gear components (<see cref="BootsMods"/>, <see cref="BackSack"/>, <see cref="TreasureWand"/>,
/// <see cref="UvLamp"/>, <see cref="GarbageJuice"/>) and applies the work_gloves numbers through TrashCarrier's public
/// fields (no TrashCarrier edits): maxSlowdown 0.45 × <see cref="UpgradeEffects.CarrySlowdownMult"/> (L1/L2 0.225, L3 0)
/// and maxThrowSpeed = <see cref="UpgradeEffects.MaxThrowSpeed"/> (14 → 15.5 m/s). The slowdown is read by TrashCarrier
/// when you pick something up, so a new level applies from the next pickup.
/// </summary>
[DisallowMultipleComponent]
public class GearMods : MonoBehaviour
{
    PlayerController pc;
    TrashCarrier carrier;
    float stockMaxSlowdown = -1f;
    int shownGloves = -1;
    float lastGripFx = -99f;

    void Awake()
    {
        pc = GetComponent<PlayerController>();
        carrier = GetComponent<TrashCarrier>();
        Ensure<BootsMods>();
        Ensure<BackSack>();
        Ensure<TreasureWand>();
        Ensure<UvLamp>();
        Ensure<GarbageJuice>();
    }

    void Ensure<T>() where T : Component
    {
        if (GetComponent<T>() == null) gameObject.AddComponent<T>();
    }

    void OnEnable()
    {
        GameSession.UpgradeChanged += OnUpgradeChanged;
        if (carrier != null) carrier.PickedUp += OnPickedUp;
        Apply();
    }

    void OnDisable()
    {
        GameSession.UpgradeChanged -= OnUpgradeChanged;
        if (carrier != null) carrier.PickedUp -= OnPickedUp;
    }

    void OnUpgradeChanged(string id, int level)
    {
        if (id == ShopIds.WorkGloves) Apply();
    }

    /// <summary>Re-apply the gloves to the carrier. Idempotent.</summary>
    public void Apply()
    {
        if (carrier == null) return;
        if (stockMaxSlowdown < 0f) stockMaxSlowdown = carrier.maxSlowdown;
        carrier.maxSlowdown = stockMaxSlowdown * UpgradeEffects.CarrySlowdownMult;
        carrier.maxThrowSpeed = UpgradeEffects.MaxThrowSpeed;
        int gloves = Mathf.Clamp(UpgradeEffects.Level(ShopIds.WorkGloves), 0, 3);
        if (shownGloves >= 0 && gloves > shownGloves && Application.isPlaying && pc != null && pc.IsOwner)
        {
            Vector3 hands = transform.position + Vector3.up * 1.1f + transform.forward * 0.4f;
            Fx.Burst(hands, new Color(1f, 0.85f, 0.2f), 14, 3f);
            Sfx.Play("squeak", hands, 0.6f, 1.4f);
            GameHUD.FloatingText(hands + Vector3.up * 0.6f, gloves >= 3 ? "GORILLA GRIP" : gloves == 2 ? "DOUBLE-FISTED!" : "grippy!", new Color(1f, 0.85f, 0.3f), 1.1f);
        }
        shownGloves = gloves;
    }

    void OnPickedUp(Carryable c)
    {
        if (c == null || pc == null || !pc.IsOwner) return;
        int gloves = Mathf.Clamp(UpgradeEffects.Level(ShopIds.WorkGloves), 0, 3);
        if (gloves <= 0 || c.MassKg < 9f || Time.time - lastGripFx < 25f) return;
        lastGripFx = Time.time;
        GameHUD.FloatingText(transform.position + Vector3.up * 2f, gloves >= 3 ? "*effortless gorilla lift*" : "grippy gloves: easy!", new Color(1f, 0.85f, 0.3f), 0.85f);
    }
}
