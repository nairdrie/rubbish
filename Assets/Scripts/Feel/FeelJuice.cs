using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Scale tweens (squash, punch, pop-in, shrink-and-destroy) and hit stop. Each transform has at most one
/// active tween that remembers the transform's REST scale, so overlapping calls restart from the rest scale
/// instead of compounding (no drift, never left scaled). Destroyed targets are dropped silently.
/// Tweens run on unscaled time so they keep moving during a hit stop.
/// </summary>
public sealed class FeelJuice
{
    enum Mode { Squash, Punch, PopIn, Shrink }

    sealed class Tween
    {
        public Transform t;
        public GameObject destroyGo;
        public Vector3 rest;
        public Mode mode;
        public float start, dur, amount;
    }

    readonly Dictionary<Transform, Tween> byTransform = new Dictionary<Transform, Tween>();
    readonly List<Tween> tweens = new List<Tween>();
    float clock; // advanced by Tick (unscaled), so tweens are deterministic and testable

    // hit stop state
    bool hitStopActive;
    float hitStopEnd, savedScale = 1f, savedFixed = 0.02f, appliedScale, appliedFixed;
    /// <summary>The normal physics step, captured before any hit stop touches it (0 = not captured yet).</summary>
    float baseFixed;
    const float DefaultFixed = 0.02f;   // ProjectSettings/TimeManager "Fixed Timestep"
    const float MinSaneFixed = 0.005f;  // anything smaller can only be a hit stop's leftover

    public int ActiveTweens => tweens.Count;
    public bool HitStopActive => hitStopActive;

    /// <summary>
    /// The solo pause registers "is the world frozen?" here (UIPass §5.4: <c>PauseService.Frozen</c>). While it says yes,
    /// <see cref="HitStop"/> does nothing, so a hit stop's timeScale restore can never unpause the game.
    /// </summary>
    public static System.Func<bool> FrozenCheck;

    /// <summary>Below this Settings → Screen effects value, hit stops are off (§6.2).</summary>
    public const float HitStopMinScreenFx = 0.25f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => FrozenCheck = null;

    // ------------------------------------------------------------------ scale tweens

    public void Squash(Transform t, float amount, float duration) =>
        Start(t, Mode.Squash, Mathf.Clamp(amount, -0.9f, 0.9f), Mathf.Clamp(duration, 0.05f, 3f));

    public void Punch(Transform t, float amount, float duration) =>
        Start(t, Mode.Punch, Mathf.Clamp(amount, -0.9f, 3f), Mathf.Clamp(duration, 0.05f, 3f));

    public void PopIn(Transform t, float duration) =>
        Start(t, Mode.PopIn, 0f, Mathf.Clamp(duration, 0.05f, 3f));

    public void ShrinkAndDestroy(GameObject go, float duration)
    {
        if (go == null) return;
        duration = Mathf.Clamp(duration, 0f, 3f);
        if (duration <= 0.01f) { FeelRunner.SafeDestroy(go); return; }
        var t = go.transform;
        if (byTransform.TryGetValue(t, out var existing) && existing.mode == Mode.Shrink) return;

        // stop it interacting while it poofs
        foreach (var c in go.GetComponentsInChildren<Collider>()) c.enabled = false;
        var rb = go.GetComponent<Rigidbody>();
        if (rb != null)
        {
            if (!rb.isKinematic) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            rb.isKinematic = true;
        }
        var tw = Start(t, Mode.Shrink, 0f, duration);
        if (tw != null) tw.destroyGo = go;
        else FeelRunner.SafeDestroy(go);
    }

    Tween Start(Transform t, Mode mode, float amount, float duration)
    {
        if (t == null) return null;
        if (byTransform.TryGetValue(t, out var tw))
        {
            if (tw.mode == Mode.Shrink) return null; // already on its way out
        }
        else
        {
            var rest = t.localScale;
            if (rest.sqrMagnitude < 1e-8f)
            {
                if (mode != Mode.PopIn) return null; // nothing to squash
                rest = Vector3.one;
            }
            tw = new Tween { t = t, rest = rest };
            byTransform[t] = tw;
            tweens.Add(tw);
        }
        tw.mode = mode;
        tw.amount = amount;
        tw.dur = duration;
        tw.start = clock;
        if (mode == Mode.PopIn) t.localScale = Vector3.zero;
        return tw;
    }

    public void Tick(float unscaledDt)
    {
        clock += Mathf.Max(0f, unscaledDt);
        float now = clock;
        for (int i = tweens.Count - 1; i >= 0; i--)
        {
            var tw = tweens[i];
            if (tw.t == null) { RemoveAt(i); continue; }
            float p = Mathf.Clamp01((now - tw.start) / tw.dur);
            if (p >= 1f)
            {
                if (tw.mode == Mode.Shrink)
                {
                    tw.t.localScale = Vector3.zero;
                    if (tw.destroyGo != null) FeelRunner.SafeDestroy(tw.destroyGo);
                }
                else tw.t.localScale = tw.rest;
                RemoveAt(i);
                continue;
            }
            tw.t.localScale = Evaluate(tw, p);
        }
    }

    static Vector3 Evaluate(Tween tw, float p)
    {
        switch (tw.mode)
        {
            case Mode.Squash:
            {
                // starts squashed, springs through a stretch, settles (volume-ish preserving)
                float w = Mathf.Cos(p * Mathf.PI * 2.5f) * (1f - p) * (1f - p);
                float sy = 1f - tw.amount * w;
                float sxz = 1f + tw.amount * 0.5f * w;
                return Vector3.Scale(tw.rest, new Vector3(sxz, sy, sxz));
            }
            case Mode.Punch:
            {
                float w = Mathf.Sin(p * Mathf.PI * 2.2f) * Mathf.Pow(1f - p, 1.4f) * 1.39f;
                return tw.rest * (1f + tw.amount * w);
            }
            case Mode.PopIn:
                return tw.rest * FeelEase.OutBack(p, 2.2f);
            case Mode.Shrink:
            {
                float s = p < 0.2f ? Mathf.Lerp(1f, 1.15f, p / 0.2f) : 1.15f * (1f - FeelEase.InQuad((p - 0.2f) / 0.8f));
                return tw.rest * s;
            }
        }
        return tw.rest;
    }

    void RemoveAt(int i)
    {
        var tw = tweens[i];
        tweens.RemoveAt(i);
        if (tw.t != null) byTransform.Remove(tw.t);
        else PurgeDestroyedKeys();
    }

    void PurgeDestroyedKeys()
    {
        // destroyed Transforms compare equal to null but still sit in the dictionary; drop them
        List<Transform> dead = null;
        foreach (var kv in byTransform) if (kv.Key == null) (dead ??= new List<Transform>()).Add(kv.Key);
        if (dead != null) foreach (var k in dead) byTransform.Remove(k);
    }

    // ------------------------------------------------------------------ hit stop

    /// <summary>
    /// Global slow-mo on unscaled time. Never stacks: a call during an active hit stop only extends it.
    /// Physics step is scaled with it so rigidbodies stay smooth. Restored by TickTime / RestoreTime.
    /// </summary>
    public void HitStop(float duration, float timeScale)
    {
        duration = Mathf.Clamp(duration, 0f, 2f);
        if (duration <= 0f) return;
        if (Application.isPlaying && GameSettings.ScreenFx01 < HitStopMinScreenFx) return;
        if (Application.isPlaying && CoopLocalOnly())
        {
            // Co-op (Docs/Multiplayer.md §8.2 M2, rule 4): other players are connected, so global time is never touched
            // (on the host it would slow everyone's physics). A short local camera freeze sells the hit instead.
            LocalFreeze(Mathf.Min(duration, 0.12f));
            return;
        }
        bool frozen = false;
        try { frozen = FrozenCheck != null && FrozenCheck(); }
        catch (System.Exception e) { FeelRunner.Report(e); }
        if (frozen) return;
        float now = Time.realtimeSinceStartup;
        if (hitStopActive) { hitStopEnd = Mathf.Max(hitStopEnd, now + duration); return; }
        if (Time.timeScale <= 0f) return; // paused: leave it alone

        savedScale = Time.timeScale;
        // Always restore to the normal physics step, never to whatever is current. Unity rounds fixedDeltaTime when it's set,
        // so the old "is it still ours?" check failed, the slowed step stuck, and the next hit stop saved that leftover:
        // every slam compounded it (0.02 → 0.0016 → 0.0001 s, 200× the physics work) until sorting bags became unplayable.
        if (baseFixed <= 0f) baseFixed = Time.fixedDeltaTime >= MinSaneFixed ? Time.fixedDeltaTime : DefaultFixed;
        savedFixed = baseFixed;
        appliedScale = Mathf.Min(savedScale, Mathf.Clamp(timeScale, 0.01f, 1f));
        appliedFixed = savedFixed * (appliedScale / savedScale);
        Time.timeScale = appliedScale;
        Time.fixedDeltaTime = appliedFixed;
        hitStopActive = true;
        hitStopEnd = now + duration;
    }

    // ------------------------------------------------------------------ co-op: local-only hit stop

    Behaviour frozenBrain;
    float freezeEnd;

    /// <summary>True while other players are connected (a guest, or a host with guests): hit stops stay local.</summary>
    public static bool CoopLocalOnly()
    {
        var nm = Unity.Netcode.NetworkManager.Singleton;
        if (nm == null || !nm.IsListening) return false;
        return !nm.IsServer || nm.ConnectedClientsIds.Count > 1;
    }

    /// <summary>Hold this machine's camera still for a moment (the world keeps running).</summary>
    void LocalFreeze(float duration)
    {
        var cam = Camera.main;
        if (cam == null) return;
        var brain = cam.GetComponent<Cinemachine.CinemachineBrain>();
        if (brain == null) return;
        if (frozenBrain == null)
        {
            if (!brain.enabled) return;
            frozenBrain = brain;
            brain.enabled = false;
        }
        freezeEnd = Mathf.Max(freezeEnd, Time.realtimeSinceStartup + duration);
    }

    void EndLocalFreeze(bool force)
    {
        if (frozenBrain == null) return;
        if (!force && Time.realtimeSinceStartup < freezeEnd) return;
        frozenBrain.enabled = true;
        frozenBrain = null;
    }

    /// <summary>True while a local (co-op) camera freeze is holding.</summary>
    public bool LocalFreezeActive => frozenBrain != null;

    /// <summary>Called every frame: ends the hit stop once its real-time duration is up.</summary>
    public void TickTime()
    {
        EndLocalFreeze(false);
        if (hitStopActive)
        {
            if (Time.realtimeSinceStartup >= hitStopEnd) RestoreTime();
            return;
        }
        // Safety net: a hit stop's slowed physics step never outlives the hit stop (nothing else in the game sets it).
        if (baseFixed > 0f && appliedFixed < baseFixed && SameStep(Time.fixedDeltaTime, appliedFixed)) Time.fixedDeltaTime = baseFixed;
    }

    /// <summary>fixedDeltaTime reads back rounded to Unity's internal time tick, so exact/Approximately compares fail.</summary>
    static bool SameStep(float a, float b) => Mathf.Abs(a - b) <= Mathf.Max(a, b) * 0.01f;

    /// <summary>Restore timeScale/fixedDeltaTime if a hit stop is active (also on teardown).</summary>
    public void RestoreTime()
    {
        EndLocalFreeze(true);
        if (!hitStopActive) return;
        hitStopActive = false;
        // Only undo our own change: if someone else set timeScale meanwhile (e.g. a pause menu), respect it.
        if (Mathf.Approximately(Time.timeScale, appliedScale)) Time.timeScale = savedScale;
        if (SameStep(Time.fixedDeltaTime, appliedFixed)) Time.fixedDeltaTime = savedFixed;
    }
}

/// <summary>Easing helpers shared by the Feel system.</summary>
public static class FeelEase
{
    public static float OutBack(float p, float s = 1.70158f)
    {
        p = Mathf.Clamp01(p) - 1f;
        return p * p * ((s + 1f) * p + s) + 1f;
    }

    public static float OutElastic(float p)
    {
        p = Mathf.Clamp01(p);
        if (p <= 0f || p >= 1f) return p;
        return Mathf.Pow(2f, -10f * p) * Mathf.Sin((p * 10f - 0.75f) * (2f * Mathf.PI / 3f)) + 1f;
    }

    public static float InQuad(float p) { p = Mathf.Clamp01(p); return p * p; }
    public static float OutQuad(float p) { p = Mathf.Clamp01(p); return 1f - (1f - p) * (1f - p); }
    public static float OutCubic(float p) { p = 1f - Mathf.Clamp01(p); return 1f - p * p * p; }
}
