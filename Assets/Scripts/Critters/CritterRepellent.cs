using UnityEngine;

/// <summary>
/// "Eau de Raccoon-Be-Gone" — the Scrapazon 'repellent' consumable. Used on demand: the first time a critter
/// would bite or bonk you and you own one, a bottle is spritzed (consumable -1) and no critter touches you until
/// the next morning (GameClock.DayStart). Critters that come close while it's active sniff, gag and leave.
/// </summary>
public static class CritterRepellent
{
    public const string ConsumableId = "repellent";

    static double activeUntilHours = -1;
    static float lastWorkingToast = -99f;

    /// <summary>Protection is active right now.</summary>
    public static bool IsActive => GameClock.TotalHours < activeUntilHours;

    /// <summary>In-game hours of protection left (0 when inactive).</summary>
    public static float HoursLeft => IsActive ? (float)(activeUntilHours - GameClock.TotalHours) : 0f;

    /// <summary>Would a critter be repelled right now? (active, or a bottle is available to spritz)</summary>
    public static bool WouldProtect => IsActive || GameSession.GetConsumable(ConsumableId) > 0;

    /// <summary>
    /// Called when a critter is about to hurt the player. Returns true if repellent protected them (spritzing a
    /// new bottle if needed, with a toast), false if they're unprotected.
    /// </summary>
    public static bool TryProtect()
    {
        if (IsActive)
        {
            if (Time.unscaledTime - lastWorkingToast > 20f)
            {
                lastWorkingToast = Time.unscaledTime;
                GameHUD.Toast("Eau de Raccoon-Be-Gone is working! (critters think you stink)", ToastStyle.Info, 2.2f);
            }
            Puff();
            return true;
        }
        if (GameSession.GetConsumable(ConsumableId) <= 0) return false;
        GameSession.AddConsumable(ConsumableId, -1);
        activeUntilHours = NextMorning(GameClock.TotalHours);
        lastWorkingToast = Time.unscaledTime;
        GameHUD.Toast("*pssht pssht* Eau de Raccoon-Be-Gone! No bites until morning.", ToastStyle.Good, 3f);
        Sfx.Play("sparkle", null, 0.8f, 1.2f);
        Puff();
        return true;
    }

    static void Puff()
    {
        var pc = PlayerController.Local;
        if (pc == null) return;
        Vector3 p = pc.transform.position + Vector3.up * 1.2f;
        Fx.Burst(p, new Color(0.75f, 1f, 0.55f), 16, 2.5f);
        Fx.Dust(p, 0.7f);
        Sfx.Play("whoosh", p, 0.6f, 1.4f);
    }

    /// <summary>TotalHours of the next DayStart (6:30 AM) after t.</summary>
    public static double NextMorning(double totalHours)
    {
        double dayBase = System.Math.Floor(totalHours / 24.0) * 24.0;
        double morning = dayBase + GameClock.DayStart;
        return totalHours < morning ? morning : morning + 24.0;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        activeUntilHours = -1;
        lastWorkingToast = -99f;
    }
}
