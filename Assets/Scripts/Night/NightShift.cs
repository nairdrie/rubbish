using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Package F4 "Night shift together" (Docs/Multiplayer.md §14.4): the rules in one place.
/// <list type="bullet">
/// <item><b>Night pay ×1.5</b>: every curb pickup between <see cref="GameClock.NightStart"/> and dawn pays a 50% bonus on top,
/// posted through the <see cref="Ledger"/> to the same business as the pickup (the bonus is credited inside the
/// <c>Ledger.ToBusiness</c> scope the truck's hopper opened, so the truck's business gets it, N1).</item>
/// <item><b>Critter immunity</b>: after any critter knock or latch a player can't be bitten, bonked or latched for 5 s (the
/// stun-chain rule: never helpless for more than 1.5 s without consent, §12 G3).</item>
/// <item><b>Rabies Tag</b> (host Town rule, on by default; Esc menu): a rabid player's knockdown can pass rabies on (50%).</item>
/// </list>
/// Everything works solo; <see cref="NightNet"/> carries it between machines in co-op.
/// </summary>
public static class NightShift
{
    public const float NightPayBonus = 0.5f;               // ×1.5 total
    public const string LedgerNight = "night_shift";
    public const string LedgerBounty = "animal_control";
    public const string LedgerKing = "raccoon_king";
    public const string LedgerShot = "rabies_shot";

    public const float CritterImmunity = 5f;
    public const float RabiesShotPrice = 35f;
    public const float TrapBounty = 40f;
    public const int CoopRaccoonCap = 8;
    public const float RabiesTagChance = 0.5f;

    /// <summary>True between nightfall and dawn (the raccoon hours).</summary>
    public static bool IsNight => GameClock.IsNight;

    // ------------------------------------------------------------------ Rabies Tag (host setting)

    /// <summary>Rabies Tag is on in this session. Guests read the host's replicated NightNet value, never their own preference.</summary>
    public static bool RabiesTag => NightNet.Instance != null && NightNet.Instance.IsSpawned
        ? NightNet.Instance.RabiesTagOn : GameSettings.Values.townRules.rabiesTag;

    /// <summary>Only a host (or an offline player preparing to host) may change this Town rule.</summary>
    public static bool CanChangeRabiesTag
    {
        get
        {
            var n = NightNet.Instance;
            if (GameSession.IsConnectedGuest || NetSession.State == NetSessionState.Connected ||
                (n != null && n.IsSpawned && !n.IsServer)) return false;
            // In a live shared Town, don't pretend a saved preference was applied if the rule replicator is absent.
            return !NetSession.InSession || NetSession.IsSolo || (n != null && n.IsSpawned && n.IsServer);
        }
    }

    /// <summary>Host only: change the live rule and save the preference for the next Town this machine hosts.</summary>
    public static bool TrySetRabiesTag(bool on)
    {
        if (!CanChangeRabiesTag) return false;
        var n = NightNet.Instance;
        if (n != null && n.IsSpawned) n.HostSetRabiesTag(on);
        GameSettings.Edit(GameSettings.TownRules, d => d.townRules.rabiesTag = on);
        bool saved = GameSettings.SaveNow();
        GameHUD.Toast(saved
                ? on ? "RABIES TAG is ON: a rabid knockdown may pass it on." : "Rabies Tag is off."
                : "Rule changed for this Town, but couldn't be saved for next time.",
            !saved || on ? ToastStyle.Bad : ToastStyle.Info, 3f);
        return true;
    }

    /// <summary>Host-only shortcut (also used by the editor debug menu). Returns the resulting live value.</summary>
    public static bool ToggleRabiesTag()
    {
        TrySetRabiesTag(!RabiesTag);
        return RabiesTag;
    }

    public static string RabiesTagLabel => RabiesTag ? "RABIES TAG: ON" : "RABIES TAG: OFF";

    // ------------------------------------------------------------------ critter immunity (host)

    static readonly Dictionary<ulong, float> immuneUntil = new Dictionary<ulong, float>();

    public static bool CritterImmune(ulong client) => immuneUntil.TryGetValue(client, out float t) && Time.time < t;

    public static void GrantCritterImmunity(ulong client, float seconds = CritterImmunity)
    {
        float until = Time.time + seconds;
        if (!immuneUntil.TryGetValue(client, out float t) || t < until) immuneUntil[client] = until;
    }

    // ------------------------------------------------------------------ night pay

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        immuneUntil.Clear();
    }

    // ProgressionEvents clears its handlers at SubsystemRegistration, so hook after that.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Hook()
    {
        ProgressionEvents.BagCollected -= OnBagCollected;
        ProgressionEvents.BagCollected += OnBagCollected;
    }

    /// <summary>Night pay. Raised by CurbTrash inside the truck business's Ledger scope (host and solo only), so the bonus
    /// lands on the same business as the pickup itself.</summary>
    static void OnBagCollected(BagCollectedFact f)
    {
        if (!IsNight || f.pay <= 0f || GameSession.IsConnectedGuest) return;
        float bonus = Mathf.Round(f.pay * NightPayBonus * 100f) / 100f;
        if (bonus <= 0f) return;
        Ledger.Credit(bonus, "Night shift ×1.5", LedgerNight, null, f.districtId);
        GameHUD.FloatingText(f.pos + Vector3.up * 2.2f, $"+{Economy.Money(bonus)} NIGHT ×1.5", new Color(0.55f, 0.75f, 1f), 1f);
        NightAudit.Event("nightpay", new Dictionary<string, object>
        {
            { "bag", f.bagId }, { "pay", Mathf.RoundToInt(f.pay * 100f) }, { "bonus", Mathf.RoundToInt(bonus * 100f) },
            { "business", Ledger.TargetBusiness ?? "local" },
        });
    }
}

/// <summary>NetAudit lines for F4 (kind "night.*"), so bot runs can check the gates.</summary>
public static class NightAudit
{
    public static void Event(string kind, Dictionary<string, object> d) => NetAudit.Event("night." + kind, d);
}
