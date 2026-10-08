using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// King's Rival (calling_kings_rival), the capstone: the Raccoon King is the mafia boss of the competition, and a Rival is
/// a business problem. Rules: <see cref="KingsRivalRules"/>. This is the host's side: it only WATCHES the existing King
/// event (it never spawns him or changes his fight) and adds, for players who built the capstone:
/// he calls them out by name when he arrives, hunts them before anybody else (<see cref="TryPickTarget"/>, read by
/// RaccoonKing), pays a Rival who helped dethrone him a turf bonus plus a King's Collar for their hutch, and takes
/// tribute from every Rival in town when he walks away undefeated.
/// </summary>
public static class KingsRival
{
    static RaccoonKing seen;
    static bool resolved;
    static int lastHp;
    static float nextLineAt, rivalsAt = -99f;
    static readonly List<ulong> rivals = new List<ulong>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { seen = null; resolved = false; lastHp = 0; nextLineAt = 0f; rivalsAt = -99f; rivals.Clear(); }

    /// <summary>Clients in town who built King's Rival (host; refreshed about once a second).</summary>
    static List<ulong> Rivals()
    {
        if (Time.unscaledTime - rivalsAt < 1f) return rivals;
        rivalsAt = Time.unscaledTime;
        rivals.Clear();
        foreach (var b in Businesses.All)
            if (b.IsValid && KingsRivalRules.IsRival(Hutch.LevelFor(b.ownerClientId, CallingIds.KingsRival))) rivals.Add(b.ownerClientId);
        return rivals;
    }

    /// <summary>Host, for the King's brain: the nearest Rival on foot inside his focus range, if any.</summary>
    public static bool TryPickTarget(Vector3 from, out NightTarget best)
    {
        best = default;
        if (GameSession.IsConnectedGuest) return false;
        float bestDist = float.MaxValue;
        foreach (ulong client in Rivals())
        {
            var pc = NightPlayers.ForClient(client);
            if (pc == null || NightPlayers.IsIndoors(pc)) continue;
            var t = NightPlayers.Describe(pc, from);
            if (!KingsRivalRules.PrefersRival(t.onFoot, t.dist) || t.dist >= bestDist) continue;
            bestDist = t.dist; best = t;
        }
        return best.Valid;
    }

    static Vector3 Head(RaccoonKing king) => king.transform.position + Vector3.up * (RaccoonKing.Scale * 0.55f + 1.4f);

    internal static void HostTick()
    {
        var king = RaccoonKingEvent.Active;
        if (king != seen)
        {
            seen = king; resolved = false;
            if (king != null) Arrive(king);
        }
        if (king == null || resolved) return;
        if (king.Phase == RaccoonKing.State.Dethroned) { resolved = true; Resolve(king, true); return; }
        if (king.Phase == RaccoonKing.State.Leaving || king.Despawning) { resolved = true; Resolve(king, false); return; }
        if (king.Hp < lastHp && Time.time >= nextLineAt && Rivals().Count > 0)
        {
            nextLineAt = Time.time + 9f;
            Say(king, KingsRivalRules.HitLine(NightDice.Cosmetic.Range(0, 64)));
        }
        lastHp = king.Hp;
    }

    static void Say(RaccoonKing king, string line) => NightNet.RaccoonFx(RaccoonCalling.FxKingLine, 0, Head(king), Vector3.zero, line);

    static void Arrive(RaccoonKing king)
    {
        lastHp = king.Hp; nextLineAt = Time.time + 6f;
        var list = Rivals();
        if (list.Count == 0) return;
        ulong pick = list[NightDice.Cosmetic.Range(0, list.Count)];
        Say(king, KingsRivalRules.ArriveLine(HandsNet.NameOf(pick), NightDice.Cosmetic.Range(0, 64)));
        foreach (ulong client in list)
            NightNet.RaccoonTell(client, false, $"The Raccoon King is here for YOU, Rival. Dethrone him: +{Economy.Money(RaccoonTuning.RivalBonusCents / 100f)} and a King's Collar. Let him walk: {Economy.Money(RaccoonTuning.TributeCents / 100f)} tribute.");
        NightAudit.Event("rival", new Dictionary<string, object> { { "op", "arrive" }, { "rivals", list.Count } });
    }

    static void Resolve(RaccoonKing king, bool beaten)
    {
        var list = new List<ulong>(Rivals());
        if (list.Count == 0) return;
        if (beaten) Say(king, KingsRivalRules.WinLine(NightDice.Cosmetic.Range(0, 64)));
        foreach (ulong client in list)
        {
            if (!Hutch.TryBusinessOf(client, out var business)) continue;
            int level = Hutch.LevelFor(client, CallingIds.KingsRival);
            if (beaten)
            {
                int hits = king.Hits.TryGetValue(client, out int n) ? n : 0;
                int bonus = KingsRivalRules.BonusCents(level, hits);
                if (bonus <= 0) { NightNet.RaccoonTell(client, false, "The King fell, but not to you, Rival. No collar for watching."); continue; }
                using (Ledger.ToBusiness(business.Id))
                    Ledger.Credit(bonus / 100f, "Turf money: dethroned the King as his Rival", RaccoonTuning.LedgerRival, king.transform.position);
                Hutch.HostEdit(business.Id, h => { h.collars = Mathf.Min(999, h.collars + 1); return true; });
                NightNet.RaccoonTell(client, true, $"RIVAL WINS! +{Economy.Money(bonus / 100f)} turf money and a KING'S COLLAR for your hutch. The bins are yours. For now.");
                NightAudit.Event("rival", new Dictionary<string, object> { { "op", "win" }, { "client", (long)client }, { "hits", hits }, { "cents", bonus } });
            }
            else
            {
                int tribute = KingsRivalRules.TributeCents(level);
                if (tribute <= 0) continue;
                using (Ledger.ToBusiness(business.Id))
                    Ledger.Charge(tribute / 100f, "Tribute to the Raccoon King", RaccoonTuning.LedgerTribute);
                NightNet.RaccoonTell(client, false, $"The King walked. His collectors took {Economy.Money(tribute / 100f)} in tribute. \"{KingsRivalRules.LeaveLine(HandsNet.NameOf(client), NightDice.Cosmetic.Range(0, 64))}\"");
                NightAudit.Event("rival", new Dictionary<string, object> { { "op", "tribute" }, { "client", (long)client }, { "cents", tribute } });
            }
        }
    }

    /// <summary>Every machine: the King speaks (mob-boss voice, big gold letters over his head, and a toast if you are near).</summary>
    internal static void PlayLine(Vector3 at, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        GameHUD.FloatingText(at, "\"" + text + "\"", new Color(1f, 0.82f, 0.25f), 1.7f);
        Sfx.Play("chitter", at, 1f, 0.45f);
        var me = NightPlayers.LocalPlayer;
        if (me != null && CritterWorld.Flat(me.transform.position - at).magnitude <= 90f)
            GameHUD.Toast("THE RACCOON KING: \"" + text + "\"", ToastStyle.Bad, 4.5f);
    }
}
