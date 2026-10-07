using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// F3 heartbeat (DontDestroyOnLoad, created at startup; Docs/Multiplayer.md §14.3): puts <see cref="PlayerTells"/>,
/// <see cref="GrabberPole"/> and <see cref="BagCannon"/> on every player copy and <see cref="TruckPuddleGrip"/> /
/// <see cref="BagCannonMount"/> on every truck, ticks the puddles (views everywhere; expiry and the leaky-bag juice trail on the
/// authority: solo or the co-op host), and adds the heavy-bag / cannon lines to the carry hint.
/// </summary>
public class TellsRuntime : MonoBehaviour
{
    static TellsRuntime instance;

    /// <summary>A leaky bag leaves a puddle every this many metres it travels.</summary>
    public const float TrailSpacing = 1.1f;
    /// <summary>...and a resting leaky bag refreshes the one under it this often (s).</summary>
    public const float RestingDrip = 50f;

    float nextScan, nextTrail;
    System.Func<TrashCarrier, string> prevHint;
    bool hintHooked;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (instance != null) return;
        var go = new GameObject("[BagTells]");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<TellsRuntime>();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => instance = null;

    void OnEnable()
    {
        CoopPlayerHooks.Spawned += OnPlayerSpawned;
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void OnDisable()
    {
        CoopPlayerHooks.Spawned -= OnPlayerSpawned;
        SceneManager.sceneLoaded -= OnSceneLoaded;
        if (hintHooked && TrashCarrier.CarryHintOverride == (System.Func<TrashCarrier, string>)CarryHint) TrashCarrier.CarryHintOverride = prevHint;
        hintHooked = false;
    }

    void OnSceneLoaded(Scene s, LoadSceneMode mode)
    {
        nextScan = 0f;
        // A whole new world (solo's shop ⇄ town swap, back to the menu): yesterday's puddles stay behind.
        if (mode == LoadSceneMode.Single && !Puddles.Mirror) Puddles.RemoveAll();
    }

    static void OnPlayerSpawned(PlayerController pc) => Ensure(pc);

    static void Ensure(PlayerController pc)
    {
        if (pc == null) return;
        var go = pc.gameObject;
        if (go.GetComponent<PlayerTells>() == null) go.AddComponent<PlayerTells>();
        if (go.GetComponent<GrabberPole>() == null) go.AddComponent<GrabberPole>();
        if (go.GetComponent<CritterMelee>() == null) go.AddComponent<CritterMelee>();
        if (go.GetComponent<BagCannon>() == null) go.AddComponent<BagCannon>();
    }

    void Update()
    {
        float now = Time.unscaledTime;
        if (now >= nextScan)
        {
            nextScan = now + 1f;
            foreach (var pc in FindObjectsByType<PlayerController>(FindObjectsSortMode.None)) Ensure(pc);
            foreach (var t in TruckControllerV2.All)
            {
                if (t == null) continue;
                if (t.GetComponent<TruckPuddleGrip>() == null) t.gameObject.AddComponent<TruckPuddleGrip>();
                if (t.GetComponent<BagCannonMount>() == null) t.gameObject.AddComponent<BagCannonMount>();
            }
            HookHint();
        }
        if (!Puddles.Mirror && Time.time >= nextTrail)
        {
            nextTrail = Time.time + 0.15f;
            LeakyTrail();
        }
        Puddles.Tick();
    }

    /// <summary>Authority: leaky bags drip a trail of juice puddles.</summary>
    static void LeakyTrail()
    {
        var list = BagTellFx.Leaky;
        float t = Time.time;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var fx = list[i];
            if (fx == null || fx.Bag == null || fx.Bag.State == CarryState.Consumed || fx.Bag.HasBurst) continue;
            var bag = fx.Bag;
            Vector3 p = bag.WorldCenter;
            bool resting = bag.State == CarryState.Resting && (bag.Body == null || bag.Body.IsSleeping() || bag.Body.linearVelocity.sqrMagnitude < 0.05f);
            if (resting)
            {
                if (fx.hasPuddle && t - fx.lastPuddleTime < RestingDrip && (p - fx.lastPuddleAt).sqrMagnitude < 0.5f * 0.5f) continue;
            }
            else if (fx.hasPuddle && (p - fx.lastPuddleAt).sqrMagnitude < TrailSpacing * TrailSpacing) continue;
            fx.puddleCount++;
            uint h = (uint)PickupVisuals.StableHash(bag.Contents != null ? bag.Contents.bagId : "leak") + (uint)fx.puddleCount * 2654435761u;
            float r = resting ? 0.75f : 0.42f + (h % 100u) / 100f * 0.25f;
            fx.lastPuddleAt = p;
            fx.lastPuddleTime = t;
            fx.hasPuddle = true;
            Puddles.Spawn(p, r, PuddleKind.Juice);
        }
    }

    // ==================================================================================== carry hint

    void HookHint()
    {
        if (prevHint != null && prevHint.Target is Object o && o == null) prevHint = null; // its owner is gone
        var cur = TrashCarrier.CarryHintOverride;
        if (cur == (System.Func<TrashCarrier, string>)CarryHint) return;
        prevHint = cur;
        TrashCarrier.CarryHintOverride = CarryHint;
        hintHooked = true;
    }

    const string DragHint = "HEAVY! Dragging it alone (slow, no throwing). Someone can press [E] on it to lift together.  [E] Toss  [G] Drop";
    const string LiftHint = "Lifting together! Move together to carry.  [G] Let go";

    string CarryHint(TrashCarrier c)
    {
        string s = null;
        try { s = prevHint?.Invoke(c); }
        catch (System.Exception e) { Debug.LogException(e); }
        if (s != null || c == null) return s;
        var pt = PlayerTells.LocalTells;
        if (pt != null && pt.Dragging) return DragHint;
        if (pt != null && pt.LiftedTogether) return LiftHint;
        var cannon = BagCannon.Local;
        if (cannon != null && (cannon.Ready || cannon.Aiming)) return cannon.Hint;
        return null;
    }
}
