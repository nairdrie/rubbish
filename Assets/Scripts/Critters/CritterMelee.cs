using UnityEngine;

/// <summary>Melee acquisition does not depend on projectile-only critter colliders or their IgnoreRaycast layer.</summary>
public sealed class CritterMelee : MonoBehaviour
{
    PlayerController player;
    TrashCarrier carrier;
    float nextSwing, nextHint;
    bool hinted;
    void Awake() { player = GetComponent<PlayerController>(); carrier = GetComponent<TrashCarrier>(); }

    public static bool InReach(Vector3 feet, Vector3 forward, Vector3 point, float range)
    {
        Vector3 d = point - feet;
        if (d.y < -0.75f || d.y > 2.8f) return false;
        d.y = 0f; forward.y = 0f;
        return PoleTargeting.WithinReach(Vector3.zero, d, range) &&
            (d.sqrMagnitude < 0.09f || Vector3.Angle(forward, d) <= HandsTuning.ShoveCone);
    }

    public static Critter Target(PlayerController player, float range)
    {
        if (player == null) return null;
        Critter best = null;
        float nearest = float.PositiveInfinity;
        foreach (var c in Critter.All)
        {
            if (c == null || !c.isActiveAndEnabled || c.Despawning || !InReach(player.transform.position, player.transform.forward, c.MeleePoint, range)) continue;
            float d = (c.MeleePoint - (player.transform.position + Vector3.up * 0.6f)).sqrMagnitude;
            if (d >= nearest || !PoleTargeting.ClearSegment(player.transform.position + Vector3.up * 1.1f, c.MeleePoint, player.transform, c.transform)) continue;
            best = c; nearest = d;
        }
        return best;
    }

    public static Critter ById(ushort id)
    {
        if (id == 0) return null;
        foreach (var c in Critter.All) if (c != null && c.NetId == id && !c.NetPuppet) return c;
        return null;
    }

    void Update()
    {
        if (player == null || !CoopPlayerHooks.IsLocalOwner(player)) return;
        bool free = !player.IsDriving && !player.IsPlayingChess && !PlayerController.IsUIBlocking && !SceneController.IsTransitioning &&
            (carrier == null || !carrier.ExternalControl) && !CompactorCube.IsSuctioning(NightPlayers.ClientOf(player));
        var tells = GetComponent<PlayerTells>();
        free &= tells == null || !tells.IsCube && !tells.Helping;
        var hands = HandsNet.Instance;
        if (hands != null && hands.Active && hands.BodyOf(hands.LocalId).State != BodyState.Normal) free = false;
        // Solo riders too (co-op's are Riding above): on a truck's back RB switches spot, and nobody punches from the step.
        var rider = RideRider.LocalRider;
        if (rider != null && rider.Player == player && (rider.IsRiding || rider.IsFlying)) free = false;
        if (!free) { ClearHint(); return; }
        bool swing = carrier != null && carrier.IsCarrying;
        var target = Target(player, swing ? HandsTuning.SwingRange : HandsTuning.ShoveRange);
        if (Time.unscaledTime >= nextHint)
        {
            nextHint = Time.unscaledTime + 0.15f;
            if (target != null)
            {
                GameHUD.SetContextHint($"[{(GameControls.UsingGamepad ? "RB" : "RMB")}] {(swing ? "Bonk" : "Punch")} {target.DisplayName}");
                hinted = true;
            }
            else ClearHint();
        }
        // Active multiplayer uses PlayerBody -> HandsNet's same punch transaction/cadence.
        if (hands != null && hands.Active) return;
        if (!GameControls.Down(KeyCode.Mouse1, GameControls.Action.Shove) || Time.time < nextSwing) return;
        nextSwing = Time.time + (swing ? HandsTuning.SwingCooldown : HandsTuning.ShoveCooldown);
        GetComponent<ChuckPuppet>()?.PlayPunch(swing);
        if (target == null) return;
        Vector3 direction = target.MeleePoint - transform.position; direction.y = 0f;
        if (target.TryMeleeBonk(direction.normalized * (swing ? 6f : 4f), NightPlayers.ClientOf(player)))
        {
            Fx.Burst(target.MeleePoint, new Color(1f, 0.8f, 0.25f), 12, 3f);
            Sfx.Play("thud", target.MeleePoint, 0.7f, 1.2f);
            GameHUD.FloatingText(target.MeleePoint + Vector3.up * 0.3f, "BONK!", SortingKit.ComboOrange);
        }
    }

    void ClearHint() { if (!hinted) return; hinted = false; GameHUD.SetContextHint(null); }
    void OnDisable() => ClearHint();
}
