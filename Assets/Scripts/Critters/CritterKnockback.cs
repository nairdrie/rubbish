using UnityEngine;

/// <summary>
/// Added at runtime to the local player (by CritterSpawner; no prefab edit). Bites and bonks shove the player's
/// CharacterController over ~0.25 s with CharacterController.Move — so walls and the truck still block it (never a
/// teleport) — with a little hop. <see cref="HitLocalPlayer"/> is the one-stop "critter got you" moment: drops
/// whatever you carry (it pops out of your arms), knockback, camera shake, hit-stop, "OW!", sounds, dizzy stars.
/// </summary>
[DisallowMultipleComponent]
public class CritterKnockback : MonoBehaviour
{
    public const float Duration = 0.25f;

    CharacterController cc;
    Vector3 dir;
    float distance, hop, t = 1f, dur = Duration;
    float lastHitTime = -99f;

    /// <summary>Time.time of the last critter hit (for i-frames).</summary>
    public float LastHitTime => lastHitTime;

    static readonly string[] OwWords = { "OW!", "OWIE!", "OUCH!", "YOWCH!" };

    void Awake() => cc = GetComponent<CharacterController>();

    /// <summary>Start a shove: distance metres along dir over duration seconds, with a hop (m).</summary>
    public void Knock(Vector3 direction, float dist, float hopHeight = 0.25f, float duration = Duration)
    {
        direction.y = 0f;
        dir = direction.sqrMagnitude > 0.0001f ? direction.normalized : -transform.forward;
        distance = Mathf.Max(0f, dist);
        hop = Mathf.Max(0f, hopHeight);
        dur = Mathf.Max(0.05f, duration);
        t = 0f;
    }

    void Update()
    {
        if (t >= 1f) return;
        if (cc == null) cc = GetComponent<CharacterController>();
        var pc = GetComponent<PlayerController>();
        if (cc == null || !cc.enabled || (pc != null && (pc.IsDriving || pc.IsPlayingChess)))
        {
            t = 1f;
            return;
        }
        float dt = Time.deltaTime;
        float t0 = t;
        t = Mathf.Min(1f, t + dt / dur);
        // Ease-out: fast shove that settles (integral of 2(1-u) du = 1 → covers exactly `distance`).
        float s0 = 2f * t0 - t0 * t0, s1 = 2f * t - t * t;
        Vector3 step = dir * distance * (s1 - s0);
        // Hop up in the first half; ThirdPersonController's gravity brings us back down.
        if (t0 < 0.5f) step += Vector3.up * hop * (Mathf.Min(t, 0.5f) - t0) * 2f;
        cc.Move(step);
    }

    /// <summary>True while a knockback is still moving the player.</summary>
    public bool IsKnocking => t < 1f;

    // ------------------------------------------------------------------ the full "critter got you" moment

    /// <summary>
    /// Hit the local player: repellent check, drop the held thing, knockback, juice. Returns false (and does
    /// nothing) if there is no on-foot local player, they were just hit (i-frames), or repellent protected them.
    /// </summary>
    public static bool HitLocalPlayer(Vector3 dir, float distance, float hopHeight, string word, Color wordColor, string sfx, float shake, Vector3 sourcePos)
    {
        var pc = PlayerController.Local;
        if (pc == null || pc.IsDriving || pc.IsPlayingChess || SceneController.IsTransitioning) return false;
        var kb = pc.GetComponent<CritterKnockback>();
        if (kb == null) kb = pc.gameObject.AddComponent<CritterKnockback>();
        if (Time.time - kb.lastHitTime < 0.9f) return false;
        if (CritterRepellent.TryProtect()) return false;
        kb.lastHitTime = Time.time;

        Vector3 head = pc.transform.position + Vector3.up * 1.6f;

        // Your bag goes flying out of your arms (a gentle drop, then a pop — never a burst).
        var carrier = TrashCarrier.Local;
        // Hard hat (Progression §6.4, builder B): a deterministic roll < KeepBagOnBonk keeps the bag; knockback × BonkKnockbackMult.
        bool keepBag = UpgradeEffects.KeepBagOnBonk > 0f && UpgradeEffects.DetectRoll(((long)(GameClock.TotalHours * 1000.0)).ToString(), "hard_hat", UpgradeEffects.KeepBagOnBonk);
        if (carrier != null && carrier.IsCarrying && !carrier.ExternalControl && !keepBag)
        {
            var held = carrier.Held;
            carrier.Drop();
            if (held != null && held.Body != null && !held.Body.isKinematic)
                held.Body.linearVelocity += dir.normalized * 1.5f + Vector3.up * 3.2f;
            GameHUD.FloatingText(head + Vector3.up * 0.3f, "*drops bag*", new Color(0.85f, 0.85f, 0.85f), 0.7f);
        }
        else if (keepBag && carrier != null && carrier.IsCarrying)
            GameHUD.FloatingText(head + Vector3.up * 0.3f, "*CLONK* hard hat! still got it", new Color(1f, 0.85f, 0.3f), 0.8f);

        kb.Knock(dir, distance * UpgradeEffects.BonkKnockbackMult, hopHeight);

        Sfx.Play(sfx, sourcePos, 1f, Random.Range(0.95f, 1.1f));
        Sfx.Play("ow", head, 1f, Random.Range(0.95f, 1.15f));
        Sfx.Play("thud", pc.transform.position, 0.6f, 0.9f);
        Juice.Shake(shake, 0.25f);
        Juice.HitStop(0.06f, 0.1f);
        GameHUD.FloatingText(head + Vector3.up * 0.2f, OwWords[Random.Range(0, OwWords.Length)], new Color(1f, 0.3f, 0.25f), 1.5f);
        if (!string.IsNullOrEmpty(word)) GameHUD.FloatingText(sourcePos + Vector3.up * 0.5f, word, wordColor, 1.2f);
        Fx.Burst(head - Vector3.up * 0.4f, new Color(1f, 0.9f, 0.35f), 12, 3.5f);
        DizzyStars.Attach(pc.transform, 1.2f, 0.35f, 2.15f); // player-local: ~1.72 m at Chuck's 0.8 root scale, above his cap
        return true;
    }
}
