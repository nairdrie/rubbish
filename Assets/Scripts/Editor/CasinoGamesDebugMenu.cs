using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Rubbish/Debug/Casino: sit down at any casino game in play mode through the real seat-mode controllers (Docs/Casino.md §12), walking
/// the player into the room first when they are outside, hand yourself some cash, and read each slot machine's exact numbers. Money
/// still moves through the host logic and the Ledger, exactly as at a real table.
/// </summary>
public static class CasinoGamesDebugMenu
{
    const string Root = "Rubbish/Debug/Casino/";

    static bool Playing() => Application.isPlaying;

    static CasinoStationKind waitKind;
    static int waitIndex;
    static double waitUntil;

    static void Open(CasinoStationKind kind, int index)
    {
        if (!Application.isPlaying) { Debug.LogWarning("[Casino] Enter play mode first."); return; }
        if (CasinoControl.Active != null) CasinoControl.Active.RequestExit();
        if (CasinoStation.Find(kind, kind == CasinoStationKind.Slot ? index : 0) == null)
        {
            Debug.LogWarning("[Casino] The casino isn't loaded (Casino.unity in the build? Rubbish/Setup/Casino).");
            return;
        }
        if (!CasinoWorld.LocalInside)
        {
            if (!CasinoWorld.LocalGoInside()) { Debug.LogWarning("[Casino] Couldn't go inside right now (a door fade is running, or you are driving)."); return; }
            waitKind = kind; waitIndex = index; waitUntil = EditorApplication.timeSinceStartup + 8.0;
            EditorApplication.update -= WaitThenSit; EditorApplication.update += WaitThenSit;
            return;
        }
        Sit(kind, index);
    }

    static void WaitThenSit()
    {
        if (!Application.isPlaying || EditorApplication.timeSinceStartup > waitUntil) { EditorApplication.update -= WaitThenSit; return; }
        if (CasinoWorld.Transitioning || !CasinoWorld.LocalInside || CasinoSeatCamera.IsSeated) return;
        EditorApplication.update -= WaitThenSit;
        Sit(waitKind, waitIndex);
    }

    static void Sit(CasinoStationKind kind, int index)
    {
        var st = CasinoStation.Find(kind, kind == CasinoStationKind.Slot ? index : 0);
        if (st == null || !CasinoControl.TryEnter(st)) Debug.LogWarning("[Casino] Couldn't sit down (already seated, or the house said no).");
    }

    [MenuItem(Root + "Play Blackjack", false, 100)] static void Blackjack() => Open(CasinoStationKind.Blackjack, 0);
    [MenuItem(Root + "Play Blackjack", true)] static bool BlackjackOk() => Playing();
    [MenuItem(Root + "Play Roulette", false, 101)] static void Roulette() => Open(CasinoStationKind.Roulette, 0);
    [MenuItem(Root + "Play Roulette", true)] static bool RouletteOk() => Playing();
    [MenuItem(Root + "Order at the Bar", false, 102)] static void Bar() => Open(CasinoStationKind.Bar, 0);
    [MenuItem(Root + "Order at the Bar", true)] static bool BarOk() => Playing();

    [MenuItem(Root + "Slots/0 Lucky Sevens", false, 110)] static void Slot0() => Open(CasinoStationKind.Slot, 0);
    [MenuItem(Root + "Slots/0 Lucky Sevens", true)] static bool Slot0Ok() => Playing();
    [MenuItem(Root + "Slots/1 Trash Panda Bandit", false, 111)] static void Slot1() => Open(CasinoStationKind.Slot, 1);
    [MenuItem(Root + "Slots/1 Trash Panda Bandit", true)] static bool Slot1Ok() => Playing();
    [MenuItem(Root + "Slots/2 Golden Gnome", false, 112)] static void Slot2() => Open(CasinoStationKind.Slot, 2);
    [MenuItem(Root + "Slots/2 Golden Gnome", true)] static bool Slot2Ok() => Playing();
    [MenuItem(Root + "Slots/3 Dumpster Fire", false, 113)] static void Slot3() => Open(CasinoStationKind.Slot, 3);
    [MenuItem(Root + "Slots/3 Dumpster Fire", true)] static bool Slot3Ok() => Playing();
    [MenuItem(Root + "Slots/4 Mystery Bag", false, 114)] static void Slot4() => Open(CasinoStationKind.Slot, 4);
    [MenuItem(Root + "Slots/4 Mystery Bag", true)] static bool Slot4Ok() => Playing();

    [MenuItem(Root + "Give $1000", false, 130)]
    static void Give()
    {
        if (GameSession.IsConnectedGuest) { Debug.LogWarning("[Casino] Only the host (or a solo game) can mint debug cash."); return; }
        using (Ledger.ToBusiness(null)) Ledger.Credit(1000f, "Debug: casino float", LedgerIds.CasinoPayout);
        Debug.Log("[Casino] +$1,000. Cash is now " + CasinoRules.Dollars(GameSession.CashCents));
    }

    [MenuItem(Root + "Give $1000", true)] static bool GiveOk() => Playing();

    [MenuItem(Root + "Sober up", false, 131)]
    static void Sober() => DrunkStatus.SetLevelDirect(0f);

    [MenuItem(Root + "Sober up", true)] static bool SoberOk() => Playing();

    [MenuItem(Root + "Log slot odds (return, hit frequency)", false, 150)]
    static void Odds()
    {
        var sb = new StringBuilder("[Casino] Slot machines, exact over every stop combination:\n");
        for (int i = 0; i < SlotRules.MachineCount; i++)
        {
            var m = SlotRules.Machine(i);
            var s = SlotRules.Stats(i);
            sb.Append(i).Append("  ").Append(m.name.PadRight(20))
              .Append(" stake ").Append(CasinoRules.Dollars(m.stakeCents).PadLeft(4))
              .Append("   return ").Append((SlotRules.Return(i) * 100.0).ToString("0.000")).Append("% (target ").Append((m.targetReturn * 100.0).ToString("0")).Append("%)")
              .Append("   hit frequency ").Append((SlotRules.HitFrequency(i) * 100.0).ToString("0.00")).Append('%')
              .Append("   top prize ").Append(CasinoRules.Dollars((long)m.stakeCents * m.pays[0].multiple)).Append(" at 1 in ").Append(s.topPrizeOneIn.ToString("N0"))
              .Append("   near misses ").Append((s.nearMissFrequency * 100.0).ToString("0.00")).Append("%   volatility ").Append(s.volatility.ToString("0.0")).Append('\n');
        }
        sb.Append("Roulette: 94.74% on every bet (top line 92.11%). Blackjack: about 99.4% with basic strategy.");
        Debug.Log(sb.ToString());
    }
}
