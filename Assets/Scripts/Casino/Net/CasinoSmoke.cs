#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// Dev tool, not part of the game: a bot brain called <c>casino</c> for the headless bot harness (Tools/bots,
/// <c>run_bots.sh --brain casino</c>) that plays the shared casino tables through <see cref="CasinoClient"/> over a real
/// co-op session, and checks what it sees. Bot 1 is the host, bot 2 sits at blackjack, walks away mid-hand, then joins the
/// wheel and a machine, bot 3 only watches blackjack and plays the wheel and a machine. A bot build without Casino.unity has no stations, so
/// each bot is parked at the room's world position (the host's proximity check has nothing else to look at); with the scene in the build
/// each bot walks to its slot machine before it sits.
/// Every bot logs <c>[CasinoSmoke] bot-N: ...</c> lines and one <c>RESULT</c> line; the money each bot ends with must equal
/// the nets the PUBLIC snapshots and slot events said it won and lost.
/// <para>Flags: <c>-botbrain casino</c> and optionally <c>-casinoclients N</c> (default 3).</para>
/// </summary>
public static class CasinoSmoke
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Register() => BotBrains.Register("casino", () => new Brain());

    struct Wait
    {
        public Func<bool> cond;
        public float seconds;
        public string what;
        public static Wait Until(Func<bool> c, float timeout, string what) => new Wait { cond = c, seconds = timeout, what = what };
        public static Wait For(float seconds) => new Wait { seconds = seconds };
        public static Wait Frame => new Wait { seconds = 0f };
    }

    sealed class Brain : IBotBrain
    {
        BotContext c;
        IEnumerator<Wait> script;
        Wait waiting;
        float waitStarted;
        bool hasWait, finished;
        ulong me;
        int clients = 3;
        string tag;
        Vector3 hold;
        bool holding;
        float nextHold;
        readonly List<string> fails = new List<string>();
        long startCash, bjNet, rlNet, slotNet;
        int lastBjRound = -1, lastRlRound = -1, bjSnaps, rlSnaps, spinsSeen, ownSpins, notices, bjResults, rlResults;
        float nextAct;
        bool leftMid, sawBjResults, sawRlResult, holeLeak;
        readonly Dictionary<int, int> spinsByKey = new Dictionary<int, int>();

        public void Begin(BotContext ctx)
        {
            c = ctx;
            c.Stop();
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "-casinoclients" && int.TryParse(args[i + 1], out int n)) clients = Mathf.Clamp(n, 1, 4);
            CasinoClient.BlackjackSnapshotChanged += OnBj;
            CasinoClient.RouletteSnapshotChanged += OnRl;
            CasinoClient.SlotSpun += OnSpun;
            CasinoClient.SlotOccupancyChanged += OnOccupancy;
            CasinoClient.Notice += OnNotice;
        }

        public void End(BotContext ctx)
        {
            CasinoClient.BlackjackSnapshotChanged -= OnBj;
            CasinoClient.RouletteSnapshotChanged -= OnRl;
            CasinoClient.SlotSpun -= OnSpun;
            CasinoClient.SlotOccupancyChanged -= OnOccupancy;
            CasinoClient.Notice -= OnNotice;
        }

        public void Tick(BotContext ctx, float dt)
        {
            c.Stop();
            if (script == null)
            {
                if (c.Nm == null) return; // the driver hands the session over on the first tick
                me = c.Nm.LocalClientId;
                tag = "bot-" + (me + 1);
                script = Script();
                Log("begin as " + (c.IsHost ? "host" : "guest") + " client " + me + ", expecting " + clients + " players");
            }
            Hold();
            if (finished) return;
            if (hasWait)
            {
                float waited = Time.unscaledTime - waitStarted;
                if (waiting.cond != null)
                {
                    bool ok;
                    try { ok = waiting.cond(); } catch (Exception e) { Log("wait threw " + e.Message); ok = false; }
                    if (!ok && waited < waiting.seconds) return;
                    if (!ok) Fail("timed out waiting for " + waiting.what);
                }
                else if (waited < waiting.seconds) return;
                hasWait = false;
            }
            bool more;
            try { more = script.MoveNext(); }
            catch (Exception e) { Fail("script threw " + e); more = false; }
            if (!more) { finished = true; Report(); return; }
            waiting = script.Current;
            waitStarted = Time.unscaledTime;
            hasWait = true;
        }

        // ---------------------------------------------------------------- bookkeeping

        void Log(string s) => Debug.Log("[CasinoSmoke] " + tag + ": " + s);

        void Fail(string s)
        {
            fails.Add(s);
            Debug.Log("[CasinoSmoke] " + tag + ": FAIL " + s);
        }

        void Check(bool ok, string what)
        {
            if (ok) Log("ok: " + what);
            else Fail(what);
        }

        void Report()
        {
            long delta = GameSession.CashCents - startCash;
            long expected = bjNet + rlNet + slotNet;
            Check(delta == expected, $"cash moved {delta} cents, the public pictures say {expected} (blackjack {bjNet}, roulette {rlNet}, slots {slotNet})");
            Check(!holeLeak, "the hole card was never visible in a snapshot before the dealer turned it");
            Check(bjSnaps > 5 && rlSnaps > 3, $"snapshots arrived (blackjack {bjSnaps}, roulette {rlSnaps})");
            var sb = new StringBuilder();
            foreach (var kv in spinsByKey) sb.Append(kv.Key).Append('=').Append(kv.Value).Append(' ');
            Log($"saw {spinsSeen} slot spins ({sb}), {notices} notices, blackjack results {bjResults}, roulette results {rlResults}");
            Debug.Log("[CasinoSmoke] " + tag + ": RESULT " + (fails.Count == 0 ? "PASS" : "FAIL (" + fails.Count + "): " + string.Join(" | ", fails)));
        }

        /// <summary>With stations in the build (Casino.unity), stand at the station's seat: the host's proximity check is 5 m. Returns false when there is none.</summary>
        bool Walk(CasinoStationKind kind, int index = 0)
        {
            var st = CasinoStation.Find(kind, index);
            if (st == null || st.Seat == null) return false;
            hold = st.Seat.position + Vector3.up * 0.05f;
            return true;
        }

        void Hold()
        {
            if (!holding || c.Player == null || Time.unscaledTime < nextHold) return;
            nextHold = Time.unscaledTime + 0.08f;
            var pc = c.Player.GetComponent<PlayerController>();
            if (pc == null) return;
            // no floor in a bot build: stop the third-person controller's gravity or the bot falls out of the room
            var tpc = pc.GetComponent<StarterAssets.ThirdPersonController>();
            if (tpc != null && tpc.enabled) tpc.enabled = false;
            if ((pc.transform.position - hold).sqrMagnitude < 0.01f) return;
            DepotRowWorld.TeleportPlayer(pc, hold, Quaternion.identity);
        }

        // ---------------------------------------------------------------- what the public pictures say

        void OnBj(BlackjackTableSnapshot s, double offset)
        {
            bjSnaps++;
            if (s.dealer != null && s.dealer.Length >= 2 &&
                (s.phase == BlackjackTablePhase.Deal || s.phase == BlackjackTablePhase.Insurance || s.phase == BlackjackTablePhase.Turns) && s.dealer[1] != 255)
                holeLeak = true;
            if (s.phase != BlackjackTablePhase.Results || s.round == lastBjRound) return;
            lastBjRound = s.round;
            sawBjResults = true;
            for (int i = 0; i < s.seats.Length; i++)
            {
                var seat = s.seats[i];
                if (!seat.Has(BlackjackSeatSnapshot.FlagSettled) || seat.ownerKey != CasinoClient.LocalKey) continue;
                bjNet += seat.totalNetCents;
                bjResults++;
                Log($"blackjack round {s.round}: my seat {i} net {seat.totalNetCents} ({(seat.Has(BlackjackSeatSnapshot.FlagAbsent) ? "in absentia" : "present")})");
            }
        }

        void OnRl(RouletteTableSnapshot s, double offset)
        {
            rlSnaps++;
            if (s.phase != RouletteTablePhase.Result || s.round == lastRlRound) return;
            lastRlRound = s.round;
            sawRlResult = true;
            for (int i = 0; i < s.participants.Length; i++)
            {
                var p = s.participants[i];
                if (p.key != CasinoClient.LocalKey || !p.Has(RouletteParticipantSnapshot.FlagCharged)) continue;
                rlNet += p.returnCents - p.stakedCents;
                rlResults++;
                Log($"roulette spin {s.round}: pocket {s.pocket} (history {s.history.Length}); my staked {p.stakedCents} back {p.returnCents}");
            }
        }

        void OnSpun(SlotEventInfo e)
        {
            spinsSeen++;
            spinsByKey[e.key] = (spinsByKey.TryGetValue(e.key, out int n) ? n : 0) + 1;
            if (e.key != CasinoClient.LocalKey) return;
            ownSpins++;
            slotNet += e.winCents - e.stakeCents;
        }

        void OnOccupancy(int machine, int key, string name) => Log($"slot {machine} occupant {key} '{name}'");

        void OnNotice(string table, CasinoNotice n)
        {
            notices++;
            Log("notice " + table + " " + n);
        }

        // ---------------------------------------------------------------- the script

        static bool Seated(BlackjackTableSnapshot s, int key)
        {
            if (s.seats == null) return false;
            for (int i = 0; i < s.seats.Length; i++) if (s.seats[i].key == key) return true;
            return false;
        }

        static int SeatedCount(BlackjackTableSnapshot s)
        {
            int n = 0;
            if (s.seats != null) for (int i = 0; i < s.seats.Length; i++) if (s.seats[i].key >= 0) n++;
            return n;
        }

        void BlackjackPolicy()
        {
            var s = CasinoClient.LatestBlackjack;
            int seat = CasinoClient.LocalSeatAt(CasinoTableIds.Blackjack);
            if (me == 1 && !leftMid && seat >= 0 && s.phase == BlackjackTablePhase.Turns)
            {
                leftMid = true;
                Log("walking away mid-hand from seat " + seat);
                CasinoClient.Leave(CasinoTableIds.Blackjack, r => Log("leave bj -> " + r));
                return;
            }
            if (seat < 0 || Time.unscaledTime < nextAct) return;
            if (s.phase == BlackjackTablePhase.Insurance && !s.seats[seat].Has(BlackjackSeatSnapshot.FlagInsuranceDecided))
            {
                nextAct = Time.unscaledTime + 0.6f;
                CasinoClient.BlackjackAct(BlackjackAction.NoInsurance, r => { if (!r.Ok) Log("no insurance -> " + r); });
            }
            else if (s.phase == BlackjackTablePhase.Turns && s.activeSeat == seat)
            {
                nextAct = Time.unscaledTime + 0.6f;
                var hands = s.seats[seat].hands;
                int h = Mathf.Clamp(s.activeHand, 0, hands.Length - 1);
                var act = hands[h].Value < 15 ? BlackjackAction.Hit : BlackjackAction.Stand;
                Log($"seat {seat} hand {hands[h].Value}: {act}");
                CasinoClient.BlackjackAct(act, r => { if (!r.Ok) Log(act + " -> " + r); });
            }
        }

        IEnumerator<Wait> Script()
        {
            yield return Wait.Until(() => c.Player != null, 60, "the player to spawn");
            hold = CasinoLayout.Origin + new Vector3(-3f + me * 3f, 0.5f, 0f);
            holding = true;
            yield return Wait.For(2f);
            CasinoClient.RequestSync();
            yield return Wait.Until(() => CasinoClient.HasBlackjack && CasinoClient.HasRoulette, 20, "the sync");
            Log($"synced: blackjack seated {SeatedCount(CasinoClient.LatestBlackjack)}, roulette players {CasinoClient.LatestRoulette.participants.Length}");

            // everybody needs money in a business
            if (c.IsHost)
            {
                yield return Wait.Until(() => Businesses.OnlineCount >= clients, 90, "every bot to join");
                foreach (var b in Businesses.All)
                {
                    if (b.IsHostBusiness) continue;
                    using (Ledger.ToBusiness(b.Id)) Ledger.Credit(600f, "Casino smoke stake", LedgerIds.PickupFee);
                }
                Ledger.Credit(600f, "Casino smoke stake", LedgerIds.PickupFee);
            }
            yield return Wait.Until(() => GameSession.CashCents >= 50000, 90, "the stake in my business");
            yield return Wait.For(3f);
            startCash = GameSession.CashCents;
            Log("start cash " + startCash);

            // ---- blackjack: host + bot 2 play, bot 3 watches
            if (Walk(CasinoStationKind.Blackjack)) yield return Wait.For(1f);
            if (me == 0)
            {
                CasinoClient.Sit(CasinoTableIds.Blackjack, 0, r => Log("sit bj -> " + r + " seat " + r.seat));
                yield return Wait.Until(() => CasinoClient.LocalSeatAt(CasinoTableIds.Blackjack) == 0, 15, "my blackjack seat");
                if (clients >= 2) yield return Wait.Until(() => SeatedCount(CasinoClient.LatestBlackjack) >= 2, 90, "bot 2 to sit");
                CasinoClient.BlackjackBet(1000, r => Log("bet $10 -> " + r));
            }
            else if (me == 1)
            {
                yield return Wait.Until(() => Seated(CasinoClient.LatestBlackjack, 0), 90, "the host to sit");
                CasinoClient.Sit(CasinoTableIds.Blackjack, 0, r => Log("sit bj -> " + r + " seat " + r.seat));
                yield return Wait.Until(() => CasinoClient.LocalSeatAt(CasinoTableIds.Blackjack) >= 0, 15, "my blackjack seat");
                CasinoClient.BlackjackBet(2000, r => Log("bet $20 -> " + r));
            }
            float end = Time.unscaledTime + 150f;
            while (!sawBjResults && Time.unscaledTime < end)
            {
                if (me <= 1) BlackjackPolicy();
                yield return Wait.Frame;
            }
            Check(sawBjResults, "a blackjack round settled and everybody saw it");
            yield return Wait.Until(() => CasinoClient.LatestBlackjack.phase == BlackjackTablePhase.Idle, 20, "the blackjack table to clear");

            // ---- roulette: all three
            if (Walk(CasinoStationKind.Roulette)) yield return Wait.For(1f);
            CasinoClient.RouletteJoin(r => Log("join wheel -> " + r + " colour " + r.seat));
            yield return Wait.Until(() => CasinoClient.LocalSeatAt(CasinoTableIds.Roulette) >= 0, 15, "my chip colour");
            yield return Wait.Until(() => CasinoClient.LatestRoulette.participants.Length >= clients, 90, "everybody at the wheel");
            var mine = me == 0 ? new RouletteBet(RouletteBetType.Red, 0, 500)
                : me == 1 ? new RouletteBet(RouletteBetType.Straight, 17, 1000) : new RouletteBet(RouletteBetType.Black, 0, 500);
            CasinoClient.RouletteSetBets(new[] { mine }, 1, r => Log("set bets -> " + r));
            yield return Wait.Until(() => MyBetsDown(), 15, "my bets on the felt");
            yield return Wait.For(1.5f);
            CasinoClient.RouletteReady(true, r => Log("ready -> " + r));
            sawRlResult = false;
            yield return Wait.Until(() => sawRlResult, 60, "the spin to settle");
            yield return Wait.Until(() => CasinoClient.LatestRoulette.phase == RouletteTablePhase.Idle, 20, "the wheel to clear");
            CasinoClient.Leave(CasinoTableIds.Roulette, r => Log("leave wheel -> " + r));

            // ---- slot machines
            int machine = me == 0 ? 0 : me == 1 ? 1 : 2;
            // a build that carries Casino.unity has real stations: walk to the machine (the host wants you within 5 m of it)
            if (Walk(CasinoStationKind.Slot, me == 2 ? 0 : machine)) yield return Wait.For(1f);
            if (me == 2)
            {
                yield return Wait.Until(() => CasinoClient.OccupantOfSlot(0) == 0, 60, "the host on machine 0");
                CasinoClient.Sit("slot0", -1, r => Log("sit slot0 (taken) -> " + r));
                yield return Wait.For(2f);
                Check(CasinoClient.OccupantOfSlot(0) == 0, "machine 0 stayed the host's");
                if (Walk(CasinoStationKind.Slot, machine)) yield return Wait.For(1f);
            }
            CasinoClient.Sit("slot" + machine, -1, r => Log("sit slot" + machine + " -> " + r));
            if (me != 2) yield return Wait.Until(() => CasinoClient.OccupantOfSlot(machine) == (int)me, 15, "my machine");
            else yield return Wait.Until(() => CasinoClient.OccupantOfSlot(2) == 2, 15, "my machine");
            yield return Wait.For(3f);
            for (int i = 0; i < 3; i++)
            {
                int before = ownSpins;
                CasinoClient.SlotSpin(machine, r => { if (!r.Ok) Log("spin -> " + r); });
                yield return Wait.Until(() => ownSpins > before, 10, "my spin to come back");
                yield return Wait.For(1.3f);
            }
            yield return Wait.Until(() => spinsSeen >= 3 * clients, 30, "everybody's spins");
            CasinoClient.Leave("slot" + machine, r => Log("leave slot -> " + r));
            yield return Wait.Until(() => CasinoClient.OccupantOfSlot(machine) == -1, 15, "my machine to free");
            yield return Wait.For(4f);
        }

        bool MyBetsDown()
        {
            var s = CasinoClient.LatestRoulette;
            for (int i = 0; i < s.participants.Length; i++)
                if (s.participants[i].key == CasinoClient.LocalKey && s.participants[i].BetCount > 0) return true;
            return false;
        }
    }
}
#endif
