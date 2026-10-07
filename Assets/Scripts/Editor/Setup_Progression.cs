using UnityEditor;
using UnityEngine;

/// <summary>
/// Progression debug menu (Docs/Progression.md §9.4 A: grant Rep, set rank, unlock district, add cash, skip to midnight,
/// grant Clean Days). Builder A has no scene setup: the HUD and the Career page are built at runtime. Every item calls
/// the runtime debug API on <see cref="Progression"/>, so it works best in play mode (the save is only written there).
/// </summary>
public static class Setup_Progression
{
    const string Root = "Rubbish/Progression/";

    /// <summary>Setup entry point for the integrator's run order: nothing to build (logs a summary).</summary>
    [MenuItem("Rubbish/Setup/Progression (nothing to build)")]
    public static void Run()
    {
        Debug.Log($"[Setup_Progression] Nothing to set up: the rank chip, promotion card, memos and the Career page are built at runtime. " +
                  $"Debug tools live under {Root}*. Rank {Progression.Rank} ({Progression.RankTitle(Progression.Rank)}), Rep {GameSession.Rep:0}, " +
                  $"Clean Days {GameSession.CleanDays}, districts unlocked {Districts.Unlocked.Count}/{Districts.All.Count}.");
    }

    static void Done(string what)
    {
        Debug.Log($"[Progression debug] {what} → Rank {Progression.Rank} ({Progression.RankTitle(Progression.Rank)}), Rep {GameSession.Rep:0}, " +
                  $"Clean Days {GameSession.CleanDays}, cash {Economy.Money(GameSession.Cash)}{(Application.isPlaying ? "" : "  (edit mode: nothing is saved)")}");
    }

    // ---- Rep
    [MenuItem(Root + "Grant 100 Rep", priority = 1)] static void Rep100() { Progression.DebugGrantRep(100f); Done("+100 Rep"); }
    [MenuItem(Root + "Grant 1,000 Rep", priority = 2)] static void Rep1k() { Progression.DebugGrantRep(1000f); Done("+1,000 Rep"); }
    [MenuItem(Root + "Grant 10,000 Rep", priority = 3)] static void Rep10k() { Progression.DebugGrantRep(10000f); Done("+10,000 Rep"); }

    // ---- rank
    [MenuItem(Root + "Promote +1 Rank", priority = 20)] static void PromoteOne() { Progression.DebugSetRank(Mathf.Min(Progression.MaxRank, Progression.Rank + 1)); Done("promote +1"); }
    [MenuItem(Root + "Set Rank/1 Trash Intern", priority = 21)] static void R1() => SetRank(1);
    [MenuItem(Root + "Set Rank/2 Bag Wrangler", priority = 22)] static void R2() => SetRank(2);
    [MenuItem(Root + "Set Rank/3 Curbside Associate", priority = 23)] static void R3() => SetRank(3);
    [MenuItem(Root + "Set Rank/4 Certified Bin Technician", priority = 24)] static void R4() => SetRank(4);
    [MenuItem(Root + "Set Rank/5 Senior Sack Specialist", priority = 25)] static void R5() => SetRank(5);
    [MenuItem(Root + "Set Rank/6 Hopper Whisperer", priority = 26)] static void R6() => SetRank(6);
    [MenuItem(Root + "Set Rank/7 Regional Rubbish Manager", priority = 27)] static void R7() => SetRank(7);
    [MenuItem(Root + "Set Rank/8 Garbage Guru", priority = 28)] static void R8() => SetRank(8);
    [MenuItem(Root + "Set Rank/9 Duke of Dumpsters", priority = 29)] static void R9() => SetRank(9);
    [MenuItem(Root + "Set Rank/10 Trash Tycoon", priority = 30)] static void R10() => SetRank(10);
    [MenuItem(Root + "Set Rank/11 Baron of Bins", priority = 31)] static void R11() => SetRank(11);
    [MenuItem(Root + "Set Rank/12 Supreme Garbage Overlord", priority = 32)] static void R12() => SetRank(12);
    static void SetRank(int r) { Progression.DebugSetRank(r); Done("set rank " + r); }

    // ---- Clean Days
    [MenuItem(Root + "Grant 1 Clean Day", priority = 40)] static void Cd1() { Progression.DebugGrantCleanDays(1); Done("+1 Clean Day"); }
    [MenuItem(Root + "Grant 5 Clean Days", priority = 41)] static void Cd5() { Progression.DebugGrantCleanDays(5); Done("+5 Clean Days"); }

    // ---- districts
    [MenuItem(Root + "Unlock District/Minivan Meadows", priority = 60)] static void UMM() => Unlock(Districts.MinivanMeadows);
    [MenuItem(Root + "Unlock District/Snootington Hills", priority = 61)] static void USH() => Unlock(Districts.SnootingtonHills);
    [MenuItem(Root + "Unlock District/Billionaire Bluffs", priority = 62)] static void UBB() => Unlock(Districts.BillionaireBluffs);
    [MenuItem(Root + "Unlock District/All", priority = 63)]
    static void UAll()
    {
        foreach (var d in Districts.All) if (d.tier > 1) Progression.DebugUnlockDistrict(d.id);
        Done("unlocked every district");
    }
    static void Unlock(string id) { Progression.DebugUnlockDistrict(id); Done("unlocked " + id); }

    // ---- cash
    [MenuItem(Root + "Add $1,000", priority = 80)] static void Cash1k() { Progression.DebugAddCash(1000f); Done("+$1,000"); }
    [MenuItem(Root + "Add $10,000", priority = 81)] static void Cash10k() { Progression.DebugAddCash(10000f); Done("+$10,000"); }
    [MenuItem(Root + "Add $150,000", priority = 82)] static void Cash150k() { Progression.DebugAddCash(150000f); Done("+$150,000"); }

    // ---- time / day
    [MenuItem(Root + "Skip to Midnight", priority = 100)]
    static void Midnight()
    {
        Progression.DebugSkipToMidnight();
        Done($"clock → {GameClock.ClockText} (the midnight review runs from the Trash system's inspection, or the fallback after {ProgressionRunner.MidnightFallbackSeconds:0} s)");
    }

    [MenuItem(Root + "Run Midnight Review Now (synthesized report)", priority = 101)]
    static void MidnightNow()
    {
        Progression.OnMidnight(Progression.SynthesizeMidnight());
        Done("midnight review (synthesized)");
    }

    // ---- extras
    [MenuItem(Root + "Complete Today's Work Orders", priority = 120)] static void Wo() { WorkOrders.DebugCompleteAll(); Done("work orders completed"); }
    [MenuItem(Root + "Grant Key to the City", priority = 121)] static void Key() { Progression.DebugGrantKeyToCity(); Done("Key to the City"); }

    [MenuItem(Root + "Log Progression State", priority = 140)]
    static void Log()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Rank {Progression.Rank} {Progression.RankTitle(Progression.Rank)} · Rep {GameSession.Rep:0} · Clean Days {GameSession.CleanDays} · Key {GameSession.KeyToCity} (streak {Progression.KeyStreak})");
        foreach (var d in Districts.All)
            sb.AppendLine($"  {d.name}: {(Districts.IsUnlocked(d) ? "open" : "locked")} trust {Progression.Trust01(d):P0} subs {Progression.Subscribers(d)}/{Progression.Houses(d)}");
        foreach (var w in WorkOrders.Today) sb.AppendLine($"  WO {w.Id}: {w.Text} [{WorkOrders.ProgressText(w)}]");
        var t = DailyStats.Today;
        sb.AppendLine($"  today: bags {t.bags} ({t.onTime} on time) curbs {t.curbs} items {t.items} contam {t.contamination} treasures {t.treasures} net {Economy.Money(t.Net)} rep {t.rep:0}");
        sb.AppendLine($"  Trash-dex {Progression.DexFound}/{Progression.DexTotal} · Magnus chess tier {UpgradeEffects.MagnusChessTier}");
        Debug.Log("[Progression]\n" + sb);
    }
}
