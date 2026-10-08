using StarterAssets;
using UnityEngine;

/// <summary>
/// work_boots + Garbage Juice speed (Docs/Progression.md §6.4 / §6.6), on the local player:
///   speed  : ThirdPersonController MoveSpeed / SprintSpeed = stock × <see cref="UpgradeEffects.MoveSpeedMult"/>
///            (1.1 / 1.2) × <see cref="GarbageJuice.SpeedMult"/> (1.25 while chugged). While carrying, the same × the
///            carry factor TrashCarrier uses (1 − clamp(kg / slowdownMassKg, 0, maxSlowdown)), so a juice that kicks in
///            mid-carry applies at once. TrashCarrier caches/restores speeds around a carry; we keep the uncarried speed
///            at stock × mult, so its restore lands on the right number.
///   L1     : Squeaky Sneakers of Speed (they squeak when you sprint).
///   L2     : Ctrl STOMP scares critters within 4 m (Critter.Scare), 6 s cooldown.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(50)]
public class BootsMods : MonoBehaviour
{
    public const float StompRadius = 4f;
    public const float StompCooldown = 6f;

    PlayerController pc;
    ThirdPersonController tpc;
    TrashCarrier carrier;
    CharacterController cc;
    float stockMove = -1f, stockSprint = -1f;
    int shownBoots = -1;
    float nextSqueakAt, stompReadyAt, lastCooldownFx = -99f;
    bool stompReadyPinged = true;

    /// <summary>The walk / sprint speed multiplier currently applied (boots × juice).</summary>
    public float AppliedMult { get; private set; } = 1f;

    void Awake()
    {
        pc = GetComponent<PlayerController>();
        tpc = GetComponent<ThirdPersonController>();
        carrier = GetComponent<TrashCarrier>();
        cc = GetComponent<CharacterController>();
    }

    void OnDisable()
    {
        // Hand back stock speeds (when not mid-carry; TrashCarrier restores its own cached base then).
        if (tpc != null && stockMove > 0f && (carrier == null || !carrier.IsCarrying))
        {
            tpc.MoveSpeed = stockMove;
            tpc.SprintSpeed = stockSprint;
        }
    }

    /// <summary>Write walk / sprint speeds for the current boots + juice (and carry factor). Idempotent.</summary>
    public void ApplySpeeds()
    {
        if (tpc == null) return;
        float mult = UpgradeEffects.MoveSpeedMult * FeralStatus.LocalSpeedMult * GarbageJuice.SpeedMult * CompactorPack.LocalSpeedMult;
        AppliedMult = mult;
        bool carrying = carrier != null && carrier.IsCarrying && carrier.Held != null;
        if (!carrying)
        {
            if (stockMove < 0f) { stockMove = tpc.MoveSpeed; stockSprint = tpc.SprintSpeed; }
            tpc.MoveSpeed = stockMove * mult;
            tpc.SprintSpeed = stockSprint * mult;
        }
        else if (stockMove > 0f)
        {
            float f = 1f - Mathf.Clamp(carrier.Held.MassKg / Mathf.Max(1f, carrier.slowdownMassKg), 0f, carrier.maxSlowdown);
            tpc.MoveSpeed = stockMove * mult * f;
            tpc.SprintSpeed = stockSprint * mult * f;
        }
    }

    void LateUpdate()
    {
        if (pc == null || tpc == null || !pc.IsOwner) return;
        ApplySpeeds();

        int boots = Mathf.Clamp(UpgradeEffects.Level(ShopIds.WorkBoots), 0, 2);
        if (shownBoots >= 0 && boots > shownBoots)
        {
            Vector3 feet = transform.position + Vector3.up * 0.1f;
            Fx.Dust(feet, 0.9f);
            Fx.Burst(feet, new Color(0.5f, 0.85f, 1f), 14, 3f);
            Sfx.Play("squeak", feet, 0.8f, 1.5f);
            GameHUD.FloatingText(feet + Vector3.up * 2f, boots >= 2 ? "SPRING-LOADED! (Ctrl to STOMP)" : "SQUEAKY SPEED!", new Color(0.55f, 0.9f, 1f), 1.1f);
        }
        shownBoots = boots;

        bool onFoot = !pc.IsDriving && !pc.IsPlayingChess && !PlayerController.IsUIBlocking && !SceneController.IsTransitioning
                      && cc != null && cc.enabled && (carrier == null || !carrier.ExternalControl);
        if (!onFoot) return;

        // Squeaky Sneakers: squeak-squeak when you sprint.
        if (boots >= 1)
        {
            Vector3 v = cc.velocity; v.y = 0f;
            if (v.magnitude > stockMove * 1.25f && cc.isGrounded && Time.time >= nextSqueakAt)
            {
                nextSqueakAt = Time.time + ShopModKit.Range(0.28f, 0.42f);
                Sfx.Play("squeak", transform.position, 0.13f, ShopModKit.Range(1.5f, 1.9f));
            }
        }

        if (boots >= 2)
        {
            if (!stompReadyPinged && Time.time >= stompReadyAt)
            {
                stompReadyPinged = true;
                Sfx.Play("click", transform.position, 0.35f, 1.6f);
            }
            if (GameControls.Down(KeyCode.LeftControl, GameControls.Action.Stomp) || Input.GetKeyDown(KeyCode.RightControl))
            {
                if (Time.time >= stompReadyAt) Stomp();
                else if (Time.time - lastCooldownFx > 0.8f)
                {
                    lastCooldownFx = Time.time;
                    GameHUD.FloatingText(transform.position + Vector3.up * 2f, $"boots recharging… {stompReadyAt - Time.time:0.0}s", new Color(0.7f, 0.8f, 0.9f), 0.75f);
                }
            }
        }
    }

    static readonly string[] StompWords = { "STOMP!", "KA-THOOM!", "BOING-STOMP!", "SHOO!!" };

    /// <summary>Spring-Loaded Stompers: scare every critter within 4 m (host decides; critters live there).</summary>
    public int Stomp()
    {
        stompReadyAt = Time.time + StompCooldown;
        stompReadyPinged = false;
        Vector3 p = transform.position;
        int scared = 0;
        if (ShopModKit.IsHost)
        {
            foreach (var c in Critter.All)
            {
                if (c == null || c.Despawning) continue;
                Vector3 d = c.transform.position - p; d.y = 0f;
                if (d.sqrMagnitude > StompRadius * StompRadius) continue;
                c.Scare(p, false);
                scared++;
            }
        }
        Sfx.Play("slam", p, 0.8f, 1.2f);
        Sfx.Play("thud", p, 1f, 0.8f);
        Sfx.Play("squeak", p, 0.6f, 0.6f);
        Fx.Dust(p + Vector3.up * 0.05f, 1.8f);
        for (int i = 0; i < 8; i++)
        {
            float a = i * Mathf.PI / 4f;
            Fx.Burst(p + new Vector3(Mathf.Cos(a), 0.1f, Mathf.Sin(a)) * 1.3f, new Color(0.6f, 0.5f, 0.35f), 3, 2.5f);
        }
        Juice.Shake(0.22f, 0.25f);
        GameHUD.FloatingText(p + Vector3.up * 2.2f, ShopModKit.Pick(StompWords), new Color(0.55f, 0.9f, 1f), 1.3f);
        if (scared > 0)
            GameHUD.FloatingText(p + Vector3.up * 2.8f, scared == 1 ? "a critter flees!" : $"{scared} critters flee!", new Color(0.7f, 1f, 0.5f), 0.9f);
        return scared;
    }
}
