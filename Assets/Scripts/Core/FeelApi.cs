using System;
using UnityEngine;

// PUBLIC API of the Feel/UI system (HUD, sound, particles, juice). Everyone calls these.
// FROZEN CONTRACT: keep these static classes and signatures. Implementations live in Assets/Scripts/Feel/.
// Safe to call from anywhere, any scene, even before the HUD exists: everything lazily bootstraps a hidden
// DontDestroyOnLoad runner on first use in play mode, and no call ever throws (errors are logged, capped).
// In edit mode the calls are no-ops (ShrinkAndDestroy still destroys).

public enum ToastStyle { Info, Good, Bad, Rare, Legendary }

public static class GameHUD
{
    /// <summary>Center-screen pop message ("Truck full!", "+$8 Stop cleared!").</summary>
    public static void Toast(string text, ToastStyle style = ToastStyle.Info, float duration = 2.5f)
    {
        if (string.IsNullOrEmpty(text)) return;
        Debug.Log($"[Toast/{style}] {text}");
        try { FeelRunner.Get()?.Hud?.Toast(text, style, duration); }
        catch (Exception e) { FeelRunner.Report(e); }
    }

    /// <summary>World-space text that floats up and fades (e.g. "+$0.42" over a bin).</summary>
    public static void FloatingText(Vector3 worldPos, string text, Color color, float scale = 1f)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { FeelRunner.Get()?.Hud?.FloatingText(worldPos, text, color, scale); }
        catch (Exception e) { FeelRunner.Report(e); }
    }

    /// <summary>Persistent small hint line near the bottom (carry controls, etc). null/empty clears.</summary>
    public static void SetContextHint(string text)
    {
        try { FeelRunner.Get()?.Hud?.SetContextHint(text); }
        catch (Exception e) { FeelRunner.Report(e); }
    }

    /// <summary>
    /// Big celebratory card for a treasure find (rarity-colored, name, flavor, value). Plays the rarity
    /// sound itself; Rare+ also triggers a short slow-mo (HitStop never stacks, so callers may add their own).
    /// Auto-hides after ~3 s, queues if one is already showing, never blocks input.
    /// </summary>
    public static void BigReveal(ItemInstance item)
    {
        if (item == null) return;
        try { FeelRunner.Get()?.Hud?.BigReveal(item); }
        catch (Exception e) { FeelRunner.Report(e); }
    }

    /// <summary>A quiet line in the left-hand activity feed, not a centre-screen toast.</summary>
    public static void Activity(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { FeelRunner.Get()?.Hud?.Activity(text); }
        catch (Exception e) { FeelRunner.Report(e); }
    }
}

public static class Sfx
{
    /// <summary>Global volume multiplier for every synthesized sound.</summary>
    public const float MasterVolume = 0.8f;

    /// <summary>
    /// Play a sound by id at a world position (null = 2D UI sound). Known ids:
    /// pickup, drop, throw, whoosh, thud, bag_land, bag_burst, rustle, toss_in, truck_full, ding, cash,
    /// buzz, sparkle, rare, legendary, crunch, compactor, boom, click, door, notify, squeak, pop, combo,
    /// horn, engine_start, beep (reverse beeper), slam, splat,
    /// hiss, chitter, snore, grumble, bite, ow, syringe, rooster, owl, cricket, goblin_angry, raccoon_scream,
    /// clang, sparks, geyser, car_horn, car_alarm, tire_screech, door_creak, oof, yelp, bark, mumble (+ mumble2, mumble3).
    /// (Night ambience — crickets/owls in the Neighborhood at night, a rooster at MorningCame — plays by itself.)
    /// Unknown ids play a generic blip. Sounds are synthesized once per id (no assets needed); a clip
    /// registered in RubbishAssets under "sfx/&lt;id&gt;" overrides the synth. Identical ids within ~30 ms
    /// are throttled; each play gets a little random pitch/volume variation.
    /// </summary>
    public static void Play(string id, Vector3? pos = null, float volume = 1f, float pitch = 1f)
    {
        try { FeelRunner.Get()?.Sfx?.Play(id, pos, volume, pitch); }
        catch (Exception e) { FeelRunner.Report(e); }
    }
}

public static class Fx
{
    /// <summary>Burst of little cubes/confetti.</summary>
    public static void Burst(Vector3 pos, Color color, int count = 20, float speed = 4f)
    {
        try { FeelRunner.Get()?.Fx?.Burst(pos, color, count, speed); }
        catch (Exception e) { FeelRunner.Report(e); }
    }

    /// <summary>Sparkles attached to a transform for a while (rare items).</summary>
    public static void Sparkle(Transform target, Color color, float duration = 2f)
    {
        if (target == null) return;
        try { FeelRunner.Get()?.Fx?.Sparkle(target, color, duration); }
        catch (Exception e) { FeelRunner.Report(e); }
    }

    /// <summary>Puff of grey dust (landing bags, compactor slam).</summary>
    public static void Dust(Vector3 pos, float size = 1f)
    {
        try { FeelRunner.Get()?.Fx?.Dust(pos, size); }
        catch (Exception e) { FeelRunner.Report(e); }
    }

    /// <summary>Stink lines / flies above a trash pile (cosmetic, looping until target destroyed).</summary>
    public static void Stink(Transform target)
    {
        if (target == null) return;
        try { FeelRunner.Get()?.Fx?.Stink(target); }
        catch (Exception e) { FeelRunner.Report(e); }
    }
}

public static class Juice
{
    /// <summary>Squash & stretch pulse on a transform (scale returns to original).</summary>
    public static void Squash(Transform t, float amount = 0.25f, float duration = 0.25f)
    {
        if (t == null) return;
        try { FeelRunner.Get()?.Juice?.Squash(t, amount, duration); }
        catch (Exception e) { FeelRunner.Report(e); }
    }

    /// <summary>Scale punch (bigger then settle).</summary>
    public static void Punch(Transform t, float amount = 0.2f, float duration = 0.25f)
    {
        if (t == null) return;
        try { FeelRunner.Get()?.Juice?.Punch(t, amount, duration); }
        catch (Exception e) { FeelRunner.Report(e); }
    }

    /// <summary>Camera shake on the main camera.</summary>
    public static void Shake(float intensity = 0.3f, float duration = 0.2f)
    {
        try { if (FeelRunner.Get() != null) CameraShaker.ShakeMain(intensity, duration); }
        catch (Exception e) { FeelRunner.Report(e); }
    }

    /// <summary>Scale from 0 to original with overshoot.</summary>
    public static void PopIn(Transform t, float duration = 0.25f)
    {
        if (t == null) return;
        try { FeelRunner.Get()?.Juice?.PopIn(t, duration); }
        catch (Exception e) { FeelRunner.Report(e); }
    }

    /// <summary>Shrink to zero then Destroy.</summary>
    public static void ShrinkAndDestroy(GameObject go, float duration = 0.2f)
    {
        if (go == null) return;
        try
        {
            var juice = FeelRunner.Get()?.Juice;
            if (juice != null) juice.ShrinkAndDestroy(go, duration);
            else if (Application.isPlaying) UnityEngine.Object.Destroy(go);
            else UnityEngine.Object.DestroyImmediate(go);
        }
        catch (Exception e)
        {
            FeelRunner.Report(e);
            if (go != null) { if (Application.isPlaying) UnityEngine.Object.Destroy(go); else UnityEngine.Object.DestroyImmediate(go); }
        }
    }

    /// <summary>Brief global slow-mo (big reveals). timeScale restored automatically.</summary>
    public static void HitStop(float duration = 0.08f, float timeScale = 0.05f)
    {
        try { FeelRunner.Get()?.Juice?.HitStop(duration, timeScale); }
        catch (Exception e) { FeelRunner.Report(e); }
    }
}
