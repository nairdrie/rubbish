using UnityEngine;

/// <summary>
/// A quick wind-up, rubber-hose fist extension and springy recoil on top of Chuck's normal Animator pose.
/// Purely cosmetic: the host still decides whether a shove/punch connects and how far the victim moves.
/// Called on the attacker copy on each machine by HandsNet's punch event.
/// </summary>
public partial class ChuckPuppet
{
    const float PunchSeconds = 0.48f;
    float punchTime = -1f;
    bool punchSwing;
    bool punchLeft;
    int punchNumber;

    public void PlayPunch(bool swing)
    {
        if (!inited) Init();
        punchTime = 0f;
        punchSwing = swing;
        if (!swing) punchLeft = (++punchNumber & 1) == 0;
        else punchLeft = false;
        sChestKick.KickPeak(8f, WChestKick, ZChestKick);
        sCapPitch.KickPeak(-9f, WCap, ZCap);
        Sfx.Play("whoosh", transform.position + Vector3.up * 1.15f, 0.75f, swing ? 0.82f : 1.15f);
    }

    /// <summary>Run after the carry/idle arm pose, so the punch is a distinct silhouette and does not accumulate.</summary>
    void PosePunch(float dt)
    {
        if (punchTime < 0f) return;
        punchTime += dt;
        float t = Mathf.Clamp01(punchTime / PunchSeconds);
        if (t >= 1f) { punchTime = -1f; return; }

        // A short, obvious cocked fist, then an almost straight elbow, followed by a squashy recovery.
        float snap = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((t - 0.24f) / 0.27f));
        float enter = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / 0.10f));
        float leave = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((t - 0.62f) / 0.38f));
        float w = enter * leave;
        if (w <= 0.001f) return;

        // Alternate mitts for a readable one-two. The fist lands slightly across the chest for a big cartoon hook.
        // Swinging a held item uses a shorter
        // extension because the carry system still owns the object and both hands should remain reasonably near it.
        float side = punchLeft ? -1f : 1f;
        Vector3 cocked = transform.TransformPoint(new Vector3(side * 0.50f, 1.21f, -0.16f));
        Vector3 extended = transform.TransformPoint(new Vector3(side * (punchSwing ? 0.30f : 0.10f), 1.25f, punchSwing ? 0.72f : 1.02f));
        Vector3 target = Vector3.Lerp(cocked, extended, snap);
        var upper = punchLeft ? upperArmL : upperArmR;
        var lower = punchLeft ? lowerArmL : lowerArmR;
        var hand = punchLeft ? handL : handR;
        var arm = punchLeft ? armL : armR;
        Vector3 pole = upper.position + transform.right * side * 0.42f - transform.up * 0.18f;
        var upper0 = upper.localRotation;
        var lower0 = lower.localRotation;
        var hand0 = hand.localRotation;
        for (int i = 0; i < 2; i++)
        {
            Vector3 wrist = target - (arm.mitt.position - hand.position);
            TwoBone(upper, lower, hand, wrist, pole, 0.99f);
        }
        upper.localRotation = Quaternion.Slerp(upper0, upper.localRotation, w);
        lower.localRotation = Quaternion.Slerp(lower0, lower.localRotation, w);
        hand.localRotation = Quaternion.Slerp(hand0, hand.localRotation, w);

        // Whole upper body commits to the hit; the head bobble and cap spring through afterward.
        float body = (snap * 13f - (1f - snap) * 9f) * w;
        chest.rotation = Quaternion.AngleAxis(body, transform.right) * chest.rotation;
        if (upperChest != null && upperChest != chest)
            upperChest.rotation = Quaternion.AngleAxis(-body * side * 0.30f, transform.forward) * upperChest.rotation;
    }
}
