using UnityEngine;

/// <summary>
/// The old Workshop's material bank (grams of metal, plastic, glass and paper saved "for building") was retired when
/// blueprints moved to specific kept items and decor moved back to Scrapazon. This is what is left of it:
/// <list type="bullet">
/// <item>The load migration is <see cref="WorkshopState.RetireBank"/>, run by <c>GameSession.LoadWorkshop</c> inside
/// <c>GameSession.ApplySaveJson</c> (so it covers the main save, every extra business slot and a business a guest brings
/// into somebody else's Town). Every banked and allocated gram becomes cash at exactly what its recycling bin pays per
/// kilogram before contracts and combos (<see cref="WorkshopCatalog.BankCents"/>: metal $8.00, plastic $4.40, glass $2.20,
/// paper $1.80, since the 2026-10-12 pace pass), an unfinished project is cancelled, and built pieces stay built. Idempotent with no version gate: once
/// converted, the save has no bank.</item>
/// <item>The cash is added to the balance the save loads with. It is not a Ledger posting: no payroll share, no day
/// stats, no Rep, no cash floater.</item>
/// <item><see cref="TryTell"/> tells the owner once (a Head Office letter and a toast), like <c>KevinSeverance</c>.</item>
/// </list>
/// </summary>
public static class WorkshopBankRefund
{
    /// <summary>Set by the load migration to the cents paid out; cleared when the owner has been told.</summary>
    public const string PendingCounter = "workshop_bank_refund_cents";

    public static void Letter(long cents, out string subject, out string body)
    {
        subject = "Your scrap pile has been cashed in";
        body = $"The Workshop no longer keeps a pile of loose scrap. Head Office weighed yours and paid bin rates: {Economy.Money(cents / 100f)}, already in the account. " +
               "From now on you keep THINGS: throw anything into the KEEP crate and it is yours. Personal gear is 3D-printed from specific finds " +
               "(" + SolidWaste.Name + " on your laptop has the shopping list, once you find its disk), the Monument from a pile of sorted pieces, and the furniture is on Scrapazon under Decor. " +
               "Everything you already built is still built.";
    }

    /// <summary>Tell the owner once and clear the note. Runs on any machine for its OWN business only: never while the
    /// statics may still show somebody else's world (a guest who has not brought a business in yet).</summary>
    public static bool TryTell()
    {
        int cents = GameSession.GetCounter(PendingCounter);
        if (cents <= 0) return false;
        if (SaveGuard.Foreign && !BusinessSlots.Declared) return false;
        GameSession.SetCounter(PendingCounter, 0);
        Letter(cents, out string subject, out string body);
        HeadOffice.Send(subject, body);
        GameHUD.Toast($"Workshop scrap pile cashed in at bin rates: {Economy.Money(cents / 100f)}. Printing takes kept items now.", ToastStyle.Good, 6f);
        return true;
    }
}

/// <summary>
/// DontDestroyOnLoad heartbeat for the workshop's two slow chores (made automatically; never place one in a scene):
/// the one-time bank letter, and, outside a network session, installing decor that was just bought on Scrapazon
/// (in a session <c>BusinessRegistry.TickWorkshop</c> does that through the host).
/// </summary>
public sealed class WorkshopRunner : MonoBehaviour
{
    static WorkshopRunner instance;
    float next;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (instance != null || !Application.isPlaying) return;
        var go = new GameObject("[WorkshopRunner]") { hideFlags = HideFlags.HideInHierarchy };
        DontDestroyOnLoad(go);
        instance = go.AddComponent<WorkshopRunner>();
    }

    void Update()
    {
        if (Time.unscaledTime < next) return;
        next = Time.unscaledTime + 1f;
        if (!SortingKit.TryGetLocalPlayer(out _)) return; // menus: nobody to tell yet
        WorkshopBankRefund.TryTell();
        if (Businesses.Registry != null || GameSession.IsConnectedGuest) return;
        var missing = GameSession.WorkshopSave.DecorToInstall(GameSession.GetUpgradeLevel);
        if (missing.Count > 0) Workshop.InstallDecor(Workshop.OwnLot, missing[0]);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => instance = null;
}
