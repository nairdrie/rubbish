using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The shared roulette wheel (<c>rl0</c>) as every player in the room sees it (Docs/Casino.md §11), drawn ONLY from the replicated
/// <see cref="RouletteTableSnapshot"/> and the shared table clock: each participant's chips as stacks in their colour on the layout cells
/// (<see cref="CasinoRouletteLayout.BetCentre"/>), name tags, the betting lamp, the rotor and the ball (a pure function of the pocket, the
/// spin's start and the clock: <see cref="CasinoWheelMath"/>, so every machine animates identically and the ball drops into the true pocket
/// exactly when the spin ends), the winning-number dolly, the history board, each participant's win or loss floating over their spot, chips
/// swept to the dealer and paid out from the tray, the dealer's gestures and the NO MORE BETS beat.
/// </summary>
public sealed class RoulettePresenter : CasinoStationPresenter
{
    const double StepLabels = 0.3, StepSweep = 1.1, StepPay = 1.9, StepCollect = 4.3;

    sealed class OwnerView
    {
        public CasinoLabel tag;
        public int key = -2;
        public bool ready;
        public readonly Dictionary<int, ChipStackView> stacks = new Dictionary<int, ChipStackView>();
        public readonly Dictionary<int, ChipStackView> payouts = new Dictionary<int, ChipStackView>();
        public readonly Dictionary<int, CasinoLabel> amounts = new Dictionary<int, CasinoLabel>();
    }

    readonly CasinoStation st;
    readonly OwnerView[] owners = new OwnerView[RouletteTable.MaxParticipants];
    readonly List<int> scratchKeys = new List<int>(16);
    readonly Dictionary<int, long> sums = new Dictionary<int, long>();
    readonly Dictionary<int, int> ownersAt = new Dictionary<int, int>();
    readonly List<int> historyBuf = new List<int>(12);
    Quaternion rotorBase = Quaternion.identity;
    bool haveRotorBase;
    int[] lastHistory = new int[0];

    RouletteTablePhase phase;
    double phaseDeadline = double.PositiveInfinity, spinStart;
    int pocket = -1, restPocket = -1, round = -1, resultsScheduledRound = -1, epoch;
    bool hasSnapshot;
    int lastLampBulbs = -1;
    double redUntil;
    bool markerShown;

    public RoulettePresenter(CasinoStation station) { st = station; }

    public CasinoStation Station => st;

    public override void Build(CasinoPresenters host)
    {
        this.host = host;
        for (int i = 0; i < owners.Length; i++) owners[i] = new OwnerView();
        if (st.wheelRotor == null) host.WarnOnce("rl.rotor", "the roulette station has no wheel rotor");
        else { rotorBase = st.wheelRotor.localRotation; haveRotorBase = true; }
        if (st.ball == null) host.WarnOnce("rl.ball", "the roulette station has no ball");
        if (st.layoutOrigin == null) host.WarnOnce("rl.layout", "the roulette station has no layout origin: chips cannot be placed");
        if (st.timerLamp != null) st.timerLamp.SetValue(0f);
        if (st.resultBoard != null) st.resultBoard.SetHistory(null);
        st.HideWinMarker();
    }

    // ------------------------------------------------------------------ apply a snapshot

    public void Apply(RouletteTableSnapshot snap, RouletteTableSnapshot prev, bool hasPrev, bool animate)
    {
        double now = CasinoPresenters.Now;
        bool newSpin = !hasSnapshot || snap.round != round;
        if (newSpin) epoch++;
        phase = snap.phase;
        phaseDeadline = snap.phaseDeadline;
        round = snap.round;
        pocket = snap.HasPocket ? snap.pocket : -1;
        spinStart = snap.spinStart;
        hasSnapshot = true;
        if (snap.phase != RouletteTablePhase.Spinning && snap.phase != RouletteTablePhase.Result && snap.history != null && snap.history.Length > 0)
            restPocket = snap.history[snap.history.Length - 1];
        else if (!hasPrev && snap.history != null && snap.history.Length > 0 && snap.phase == RouletteTablePhase.Spinning)
            restPocket = snap.history[snap.history.Length - 1]; // joined mid-spin: the previous result is the newest of the history

        ApplyHistory(snap);
        ApplyOwners(snap, animate, now);
        if (snap.phase != RouletteTablePhase.Result) SyncStacks(snap, animate, now);
        else ScheduleResults(snap, now);
        if (snap.phase != RouletteTablePhase.Result) resultsScheduledRound = -1;

        // the dolly and the beats
        bool wantMarker = snap.phase == RouletteTablePhase.Result && snap.HasPocket;
        if (wantMarker) { st.PlaceWinMarker(snap.pocket); markerShown = true; }
        else if (markerShown) { st.HideWinMarker(); markerShown = false; }

        if (animate && newSpin && snap.phase == RouletteTablePhase.Spinning)
        {
            // betting just closed (a new spin appeared: this also holds when the Betting snapshot was skipped in the same frame)
            redUntil = now + 1.8;
            var dealer = CasinoStaff.Dealer(CasinoStationKind.Roulette);
            dealer.Play(DealerGesture.Spin);
            dealer.Say("No more bets.");
            if (st.dealerFloatText != null)
                host.Labels.Show("NO MORE BETS", st.dealerFloatText.position, new Color(1f, 0.4f, 0.3f), 1.25f, now, 2.2f, 0.06f);
            Sfx.Play("buzz", st.transform.position, 0.35f, 1.2f);
        }
    }

    void ApplyHistory(RouletteTableSnapshot snap)
    {
        if (st.resultBoard == null) return;
        int n = snap.history == null ? 0 : snap.history.Length;
        bool same = n == lastHistory.Length;
        for (int i = 0; same && i < n; i++) same = lastHistory[i] == snap.history[i];
        if (same) return;
        lastHistory = new int[n];
        historyBuf.Clear();
        for (int i = n - 1; i >= 0; i--) { historyBuf.Add(snap.history[i]); lastHistory[i] = snap.history[i]; } // newest first for the board
        st.resultBoard.SetHistory(historyBuf);
    }

    void ApplyOwners(RouletteTableSnapshot snap, bool animate, double now)
    {
        var seen = new bool[owners.Length];
        if (snap.participants != null)
            for (int i = 0; i < snap.participants.Length; i++)
            {
                var p = snap.participants[i];
                int c = Mathf.Clamp(p.colour, 0, owners.Length - 1);
                seen[c] = true;
                var ov = owners[c];
                bool ready = p.Has(RouletteParticipantSnapshot.FlagReady);
                string name = CasinoClient.NameOf(p.key) + (p.Has(RouletteParticipantSnapshot.FlagAbsent) ? " (away)" : "");
                string text = ready && snap.phase == RouletteTablePhase.Betting ? name + "\n<size=70%>READY</size>" : name;
                var spot = st.Spot(c);
                if (spot == null || spot.floatText == null) continue;
                Color col = Color.Lerp(CasinoPaint.OwnerColour(c), Color.white, 0.45f);
                // neighbouring spots alternate between two heights so two tags never sit on one line
                Vector3 tagAt = spot.floatText.position + Vector3.up * ((c & 1) * TagStagger);
                if (ov.tag != null && ov.tag.Shown) { ov.tag.SetText(text); ov.tag.SetColour(col); ov.tag.SetPosition(tagAt); }
                else ov.tag = host.Labels.Show(text, tagAt, col, TagSize, now, 0f, 0f, 0.6f, true);
                ov.key = p.key; ov.ready = ready;
            }
        for (int c = 0; c < owners.Length; c++)
        {
            if (seen[c]) continue;
            var ov = owners[c];
            if (ov.tag != null) { host.Labels.Hide(ov.tag, now); ov.tag = null; }
            ov.key = -2;
        }
    }

    static int KeyOf(RouletteBet b) => ((int)b.type << 8) | b.index;

    /// <summary>Make every owner's chip stacks match the bets in the snapshot.</summary>
    void SyncStacks(RouletteTableSnapshot snap, bool animate, double now)
    {
        // how many owners share each spot (their stacks are spread a little)
        ownersAt.Clear();
        if (snap.participants != null)
            for (int i = 0; i < snap.participants.Length; i++)
            {
                var p = snap.participants[i];
                if (p.bets == null) continue;
                scratchKeys.Clear();
                for (int b = 0; b < p.bets.Length; b++)
                {
                    int k = KeyOf(p.bets[b]);
                    if (scratchKeys.Contains(k)) continue;
                    scratchKeys.Add(k);
                    ownersAt.TryGetValue(k, out int cnt);
                    ownersAt[k] = cnt + 1;
                }
            }
        var rank = new Dictionary<int, int>();
        for (int c = 0; c < owners.Length; c++)
        {
            var ov = owners[c];
            RouletteParticipantSnapshot? part = null;
            if (snap.participants != null)
                for (int i = 0; i < snap.participants.Length; i++) if (snap.participants[i].colour == c) part = snap.participants[i];
            sums.Clear();
            if (part.HasValue && part.Value.bets != null)
                for (int b = 0; b < part.Value.bets.Length; b++)
                {
                    int k = KeyOf(part.Value.bets[b]);
                    sums.TryGetValue(k, out long cur);
                    sums[k] = cur + part.Value.bets[b].cents;
                }
            // update or create the stacks that have chips
            foreach (var kv in sums)
            {
                int k = kv.Key;
                rank.TryGetValue(k, out int r);
                rank[k] = r + 1;
                ownersAt.TryGetValue(k, out int share);
                if (!ov.stacks.TryGetValue(k, out var stack))
                {
                    stack = new ChipStackView(host.Pool);
                    ov.stacks[k] = stack;
                }
                if (BetWorldPoint(k, r, share, out Vector3 at, out Quaternion rot))
                {
                    if (stack.Origin != at) stack.SetOrigin(at, rot);
                    if (stack.Cents != kv.Value) stack.Set(kv.Value, c, now, animate);
                }
                UpdateAmountLabel(ov, k, stack, now);
            }
            // remove the stacks whose bets are gone
            scratchKeys.Clear();
            foreach (var kv in ov.stacks) if (!sums.ContainsKey(kv.Key)) scratchKeys.Add(kv.Key);
            for (int i = 0; i < scratchKeys.Count; i++)
            {
                int k = scratchKeys[i];
                ov.stacks[k].Clear();
                ov.stacks.Remove(k);
                DropAmountLabel(ov, k);
            }
        }
    }

    bool BetWorldPoint(int key, int rank, int share, out Vector3 at, out Quaternion rot)
    {
        at = Vector3.zero; rot = Quaternion.identity;
        if (st.layoutOrigin == null) return false;
        var bet = new RouletteBet((RouletteBetType)(key >> 8), key & 0xFF, 0);
        if (!CasinoRouletteLayout.BetCentre(bet, out float u, out float v)) return false;
        at = st.LayoutPoint(u, v);
        rot = st.layoutOrigin.rotation;
        if (share > 1)
        {
            // several owners on one spot: small offsets so every colour shows
            float ox = (rank % 2 == 0 ? -1f : 1f) * 0.02f, oz = (rank / 2 == 0 ? -1f : 1f) * 0.016f;
            if (share == 2) oz = 0f;
            at += st.layoutOrigin.right * ox + st.layoutOrigin.forward * oz;
        }
        return true;
    }

    void UpdateAmountLabel(OwnerView ov, int key, ChipStackView stack, double now)
    {
        // the amount shows only on tall stacks (kept clean)
        bool tall = stack.Count >= 6;
        ov.amounts.TryGetValue(key, out var l);
        if (!tall)
        {
            if (l != null) { host.Labels.Hide(l, now); ov.amounts.Remove(key); }
            return;
        }
        string text = CasinoRules.Dollars(stack.Cents);
        if (l != null && l.Shown) { l.SetText(text); l.SetPosition(stack.TopPoint + Vector3.up * 0.03f); }
        else ov.amounts[key] = host.Labels.Show(text, stack.TopPoint + Vector3.up * 0.03f, new Color(1f, 0.95f, 0.7f), 0.35f, now);
    }

    void DropAmountLabel(OwnerView ov, int key)
    {
        if (ov.amounts.TryGetValue(key, out var l)) { host.Labels.Hide(l, CasinoPresenters.Now); ov.amounts.Remove(key); }
    }

    // ------------------------------------------------------------------ the result: labels, sweep, payouts

    void ScheduleResults(RouletteTableSnapshot snap, double now)
    {
        if (resultsScheduledRound == snap.round) return;
        resultsScheduledRound = snap.round;
        int myEpoch = epoch;
        // the bets must be on the felt for whatever part of the result still shows them
        SyncStacks(snap, false, now);
        double startLocal = CasinoClient.ToLocalTime(snap.phaseDeadline - RouletteTable.ResultSeconds);
        var parts = snap.participants;
        int won = snap.pocket;
        host.At(startLocal + StepLabels, late => { if (myEpoch == epoch) StepLabelsNow(parts, late); });
        host.At(startLocal + StepSweep, late => { if (myEpoch == epoch) StepSweepNow(parts, won, late); });
        host.At(startLocal + StepPay, late => { if (myEpoch == epoch) StepPayNow(parts, won, late); });
        host.At(startLocal + StepCollect, late => { if (myEpoch == epoch) StepCollectNow(parts, late); });
    }

    /// <summary>Height (metres) between the two rows the per-seat result labels alternate on.</summary>
    /// <summary>Font size of the participants' name tags: the four spots are 0.58 m apart, so a tag must stay under that wide (four of them overlapped at 1.15).</summary>
    const float TagSize = 0.8f, TagStagger = 0.17f;
    const float ResultStagger = 0.12f;
    /// <summary>Where a seat's result label floats (station local): this far above the felt and at this z (the near edge of the layout).</summary>
    /// <summary>Largest font size of a result label (the local player's sits under a metre from their eye).</summary>
    const float ResultMaxSize = 0.45f;
    const float ResultHeight = 0.20f, ResultDepth = -0.36f;

    void StepLabelsNow(RouletteParticipantSnapshot[] parts, bool late)
    {
        if (late || parts == null) return;
        double now = CasinoPresenters.Now;
        int shown = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (!p.Has(RouletteParticipantSnapshot.FlagCharged) || p.stakedCents <= 0) continue;
            var spot = st.Spot(Mathf.Clamp(p.colour, 0, owners.Length - 1));
            if (spot == null) continue;
            long net = (long)p.returnCents - p.stakedCents;
            string text = net > 0 ? CasinoChipMath.Signed(net) : net < 0 ? CasinoChipMath.Signed(net) : "EVEN";
            Color c = net > 0 ? CasinoPaint.Win : net < 0 ? CasinoPaint.Lose : Color.white;
            // above the near edge of the felt in front of the participant (about 0.9 m from their eye, not on top of it: a label over the rack
            // is half a metre from the camera and shouts): the player sees it from their own seat, the room sees it too
            Vector3 at;
            if (spot.stand != null)
            {
                Vector3 local = st.transform.InverseTransformPoint(spot.stand.position);
                float y = spot.betCircle != null ? st.transform.InverseTransformPoint(spot.betCircle.position).y : 0.92f;
                at = st.transform.TransformPoint(new Vector3(local.x, y + ResultHeight, ResultDepth));
            }
            else at = spot.floatText != null ? spot.floatText.position : st.transform.position + Vector3.up * 1.4f;
            // neighbouring seats alternate between two heights so two results never sit on one line, and the text is capped (long totals shrink, never grow)
            int colour = Mathf.Clamp(p.colour, 0, owners.Length - 1);
            at += Vector3.up * ((colour & 1) * ResultStagger);
            float size = text.Length > 7 ? 0.36f : text.Length > 5 ? 0.4f : 0.45f;
            host.Labels.Show(text, at, c, Mathf.Min(size, ResultMaxSize), now + shown * 0.01, 4.5f, 0.12f);
            shown++;
        }
    }

    bool Covers(RouletteBet b, int won) => won >= 0 && RouletteRules.Covers(b, won);

    void StepSweepNow(RouletteParticipantSnapshot[] parts, int won, bool late)
    {
        if (parts == null) return;
        double now = CasinoPresenters.Now;
        Vector3 tray = st.chipTray != null ? st.chipTray.position : st.transform.position;
        bool any = false;
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            int c = Mathf.Clamp(p.colour, 0, owners.Length - 1);
            var ov = owners[c];
            if (p.bets == null) continue;
            for (int b = 0; b < p.bets.Length; b++)
            {
                int k = KeyOf(p.bets[b]);
                if (Covers(p.bets[b], won)) continue;
                if (!ov.stacks.TryGetValue(k, out var stack) || stack.Empty) continue;
                any = true;
                if (late) stack.Clear(); else stack.SlideAway(tray, now, 0.55f, 0.02f);
                DropAmountLabel(ov, k);
            }
        }
        if (any && !late)
        {
            CasinoStaff.Dealer(CasinoStationKind.Roulette).Play(DealerGesture.Sweep);
            Sfx.Play("click", tray, 0.4f, 0.8f);
        }
    }

    void StepPayNow(RouletteParticipantSnapshot[] parts, int won, bool late)
    {
        if (parts == null) return;
        double now = CasinoPresenters.Now;
        Vector3 tray = st.chipTray != null ? st.chipTray.position : st.transform.position;
        bool any = false;
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            int c = Mathf.Clamp(p.colour, 0, owners.Length - 1);
            var ov = owners[c];
            if (p.bets == null) continue;
            for (int b = 0; b < p.bets.Length; b++)
            {
                if (!Covers(p.bets[b], won)) continue;
                int k = KeyOf(p.bets[b]);
                if (!ov.stacks.TryGetValue(k, out var stack) || stack.Empty) continue;
                long pay = RouletteRules.Payout(p.bets[b], won);
                if (pay <= 0) continue;
                any = true;
                if (!ov.payouts.TryGetValue(k, out var ps)) { ps = new ChipStackView(host.Pool); ov.payouts[k] = ps; }
                ownersAt.TryGetValue(k, out int share);
                if (BetWorldPoint(k, c, Mathf.Max(2, share), out Vector3 at, out Quaternion rot))
                {
                    at += st.layoutOrigin.right * 0.035f + st.layoutOrigin.forward * 0.04f;
                    ps.SetOrigin(at, rot);
                    ps.Set(pay, c, now, !late, 0f, tray);
                }
            }
        }
        if (any && !late) { CasinoStaff.Dealer(CasinoStationKind.Roulette).Play(DealerGesture.Pay); Sfx.Play("click", tray, 0.4f, 1f); }
    }

    void StepCollectNow(RouletteParticipantSnapshot[] parts, bool late)
    {
        double now = CasinoPresenters.Now;
        for (int c = 0; c < owners.Length; c++)
        {
            var ov = owners[c];
            var spot = st.Spot(c);
            Vector3 dest = spot != null && spot.betCircle != null ? spot.betCircle.position : st.transform.position;
            foreach (var kv in ov.stacks) { if (late) kv.Value.Clear(); else kv.Value.SlideAway(dest, now, 0.6f, 0.025f); }
            foreach (var kv in ov.payouts) { if (late) kv.Value.Clear(); else kv.Value.SlideAway(dest, now, 0.6f, 0.025f, 0.1f); }
            ov.stacks.Clear();
            ov.payouts.Clear();
            scratchKeys.Clear();
            foreach (var kv in ov.amounts) scratchKeys.Add(kv.Key);
            for (int i = 0; i < scratchKeys.Count; i++) DropAmountLabel(ov, scratchKeys[i]);
        }
    }

    // ------------------------------------------------------------------ per frame: wheel, ball, lamp

    public override void Tick(double now)
    {
        double T = CasinoClient.TableTime();
        TickWheel(T);
        TickLamp(now, T);
    }

    void TickWheel(double T)
    {
        float rotor = CasinoWheelMath.RotorAngle(T);
        if (st.wheelRotor != null)
            st.wheelRotor.localRotation = (haveRotorBase ? rotorBase : Quaternion.identity) * Quaternion.Euler(0f, rotor, 0f);
        if (st.ball == null) return;
        float angle, track01 = 0f, hop = 0f;
        bool flying = pocket >= 0 && (phase == RouletteTablePhase.Spinning || phase == RouletteTablePhase.Result);
        if (flying)
        {
            double t = T - spinStart;
            CasinoWheelMath.BallAt(pocket, restPocket, t, rotor, out angle, out track01, out hop);
        }
        else
        {
            // at rest in the last pocket, turning with the wheel
            int rest = restPocket >= 0 ? restPocket : 0;
            angle = rotor + Mathf.Max(0f, CasinoWheelMath.PocketAngle(rest));
        }
        float r = Mathf.Lerp(st.ballPocketRadius, st.ballTrackRadius, track01);
        float h = Mathf.Lerp(st.ballPocketHeight, st.ballTrackHeight, track01) + hop;
        float a = angle * Mathf.Deg2Rad;
        st.ball.localPosition = new Vector3(Mathf.Sin(a) * r, h, Mathf.Cos(a) * r);
    }

    void TickLamp(double now, double T)
    {
        var lamp = st.timerLamp;
        if (lamp == null) return;
        float v = 0f;
        if (phase == RouletteTablePhase.Betting && !double.IsInfinity(phaseDeadline))
            v = (float)System.Math.Max(0.0, System.Math.Min(1.0, (phaseDeadline - T) / RouletteTable.BettingSeconds));
        else if (now < redUntil) v = 0.25f; // NO MORE BETS: a few red bulbs
        int bulbs = Mathf.CeilToInt(v * 36f);
        if (bulbs != lastLampBulbs) { lastLampBulbs = bulbs; lamp.SetValue(bulbs / 36f); }
    }

    public override void Teardown()
    {
        if (st == null) return;
        try { if (st.timerLamp != null) st.timerLamp.SetValue(0f); st.HideWinMarker(); } catch { }
    }
}
