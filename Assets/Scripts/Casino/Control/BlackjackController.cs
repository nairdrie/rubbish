using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Blackjack at the shared table <c>bj0</c> (Docs/Casino.md §9, §12). The player sits at a seat, looks around with the mouse (the cursor
/// is a reticle at the screen centre: look at a thing and click) and plays the physical table:
/// <list type="bullet">
/// <item><b>Betting:</b> pick a chip (Q / E, wheel, D-pad, 1-5, or click the rack), click the bet circle or press Space to stack it (a see-through stack
/// shows what is pending), right click / Backspace takes the last chip off, C clears, R brings back last hand's bet. The host takes ONE stake per
/// round, so the stack stays local and goes out once as <c>BlackjackBet(total)</c> on Enter (pad Y), or by itself one second before the betting
/// clock ends ("bet auto-placed"). O sits the hand out.</item>
/// <item><b>Deciding:</b> the lit 3D plates Hit / Stand / Double / Split / Insurance (enabled exactly by what the host's rules allow this seat, from the
/// public snapshot) or Space / S / D / P / I / N, pad X / Y / RB / LB. H shows basic strategy.</item>
/// </list>
/// The cards, chips, totals and the dealer are the presenters'; this controller only reads the snapshots and sends intents.
/// </summary>
public sealed class BlackjackController : CasinoController
{
    static long lastBet;
    static bool adviceOn;
    static int lastChip;

    readonly ChipSelector chips = new ChipSelector(CasinoLayout.BlackjackChipCents);
    readonly PendingBet pending = new PendingBet();
    CasinoGhostStack ghostStack;
    CasinoWorldLabel pendingLabel;
    CasinoSelectionRing ring;
    ChipRackGlow rackGlow;
    readonly Dictionary<CasinoButton, string> labels = new Dictionary<CasinoButton, string>();

    BlackjackTableSnapshot snap;
    bool hasSnap;
    int mySeat = -1;
    bool sendInFlight, actionInFlight;
    bool lastPad;
    long lastCash = long.MinValue;
    int lastSecond = int.MinValue;
    double legalSince = -100.0;
    BlackjackActions lastLegal;
    int resultRound = -1;
    float releaseHoldAt = -1f;
    bool absentWarned;
    int pickedHoverBin = -1;

    public override string TableId => CasinoTableIds.Blackjack;

    /// <summary>The chip a click would stack (tools, tests).</summary>
    public int SelectedChipCents => chips.Cents;
    /// <summary>The stake built so far and not yet sent (tools, tests).</summary>
    public long PendingCents => pending.Total;

    // ------------------------------------------------------------------ lifecycle

    protected override void OnBegin()
    {
        chips.Select(lastChip);
        var circle = spot.betCircle;
        if (circle != null)
        {
            ghostStack = new CasinoGhostStack("PendingStake", circle, new Vector3(0f, 0.003f, 0f));
            pendingLabel = new CasinoWorldLabel("PendingStakeLabel", circle, new Vector3(0f, 0.12f, -0.03f), 0.34f, 0.07f);
        }
        ring = new CasinoSelectionRing();
        rackGlow = new ChipRackGlow(spot.chipRack);
        SelectChip(chips.Index, false);
        for (int s = 0; s < station.SpotCount; s++)
        {
            var sp = station.Spot(s);
            if (sp == null || sp.buttons == null) continue;
            foreach (var b in sp.buttons) if (b != null) labels[b] = b.label;
        }
        CasinoClient.BlackjackSnapshotChanged += OnSnapshot;
        if (!CasinoClient.HasBlackjack) CasinoClient.RequestSync();
        else OnSnapshot(CasinoClient.LatestBlackjack, CasinoClient.ClockOffset);
        RefreshPending();
        RefreshStrip();
    }

    protected override void OnEnd(CasinoSeatCamera.ExitReason reason)
    {
        CasinoClient.BlackjackSnapshotChanged -= OnSnapshot;
        lastChip = chips.Index;
        // walking away from a live hand: the host stands it and settles it (says so)
        if (hasSnap && mySeat >= 0 && mySeat < snap.seats.Length && snap.seats[mySeat].Has(BlackjackSeatSnapshot.FlagInRound) && snap.phase >= BlackjackTablePhase.Deal && snap.phase < BlackjackTablePhase.Results && snap.seats[mySeat].ownerKey == CasinoClient.LocalKey)
            GameHUD.Toast("The dealer plays your hand out.", ToastStyle.Info, 2.5f);
        ghostStack?.Destroy();
        pendingLabel?.Destroy();
        ring?.Destroy();
        rackGlow?.Clear();
        // the plates go back to how the room was built
        foreach (var kv in labels)
        {
            if (kv.Key == null) continue;
            kv.Key.SetLabel(kv.Value);
            kv.Key.SetEnabled(true);
            kv.Key.SetLit(false);
        }
        labels.Clear();
    }

    public override void OnNotice(string table, CasinoNotice notice)
    {
        if (table != CasinoTableIds.Blackjack) return;
        switch (notice)
        {
            case CasinoNotice.TurnTimedOut:
                GameHUD.Toast("Too slow: the dealer stood your hand.", ToastStyle.Bad, 2.5f);
                Refuse("Time ran out: your hand was stood.", 4f);
                break;
            case CasinoNotice.Released:
                GameHUD.Toast("The house stood you up from the table.", ToastStyle.Info, 2.2f);
                RequestExit();
                break;
        }
    }

    // ------------------------------------------------------------------ the host's picture

    double SecondsLeft => double.IsInfinity(snap.phaseDeadline) ? double.PositiveInfinity : snap.phaseDeadline - CasinoClient.TableTime();

    void OnSnapshot(BlackjackTableSnapshot s, double offset)
    {
        if (IsEnded) return;
        var prevPhase = hasSnap ? snap.phase : BlackjackTablePhase.Idle;
        snap = s;
        hasSnap = true;
        mySeat = BlackjackSnapshotRules.SeatOf(s, CasinoClient.LocalKey);
        if (mySeat < 0)
        {
            // the host says we are not at the table. Give it a moment (a guest's snapshot can trail the Sit answer), then stand up.
            if (SecondsSinceBegan > 2.0 && !absentWarned)
            {
                absentWarned = true;
                GameHUD.Toast("You're not seated any more.", ToastStyle.Info, 2f);
                RequestExit();
            }
            return;
        }

        // a stake the host holds replaces the local stack
        if (BlackjackSnapshotRules.BetPlaced(s, mySeat) && !pending.IsEmpty && !sendInFlight) { pending.Clear(); RefreshPending(); }

        var legal = BlackjackSnapshotRules.Legal(s, mySeat);
        if (legal != BlackjackActions.None && lastLegal == BlackjackActions.None) legalSince = Time.unscaledTime;
        lastLegal = legal;

        if (s.phase == BlackjackTablePhase.Results && prevPhase != BlackjackTablePhase.Results) OnResults();
        else if (s.phase != BlackjackTablePhase.Results && s.phase != BlackjackTablePhase.Dealer && releaseHoldAt < 0f && HoldingCash && !BlackjackSnapshotRules.BetPlaced(s, mySeat) && !sendInFlight)
            HoldCash(false); // a round we are not in (or the next one): nothing to keep secret

        ApplyPlates();
        RefreshStrip();
    }

    void OnResults()
    {
        if (resultRound == snap.round) return;
        resultRound = snap.round;
        var net = BlackjackSnapshotRules.SettledNet(snap, mySeat);
        if (net == null)
        {
            if (HoldingCash) releaseHoldAt = Time.unscaledTime + 0.4f;
            return;
        }
        long total = net.Value;
        var seat = snap.seats[mySeat];
        bool dealerBust = snap.dealer != null && snap.dealer.Length > 0 && BlackjackRules.HandValue(snap.dealer, snap.dealer.Length) > 21;
        bool dealerBj = snap.dealer != null && snap.dealer.Length == 2 && BlackjackRules.IsNatural(snap.dealer, 2);
        string extra = seat.insuranceNetCents > 0 ? " Insurance paid " + Dollars(seat.insuranceNetCents) + "."
                     : seat.insuranceNetCents < 0 ? " Insurance lost " + Dollars(-seat.insuranceNetCents) + "." : "";
        long stake = Math.Max(1, seat.betCents);
        if (total > 0)
        {
            Say((dealerBust ? "Dealer busts. " : "") + "You win " + Dollars(total) + "." + extra, HintTone.Good, 5f);
            CasinoControl.Celebrate(total, stake, "Blackjack");
            Sfx.Play("ding", null, 0.45f);
        }
        else if (total < 0)
        {
            Say((dealerBj ? "Dealer has blackjack. " : "") + "House takes " + Dollars(-total) + "." + extra, HintTone.Bad, 5f);
            Sfx.Play("thud", null, 0.4f, 0.8f);
        }
        else
        {
            Say("Push. Your money back." + extra, HintTone.Normal, 5f);
            Sfx.Play("click", null, 0.4f);
        }
        releaseHoldAt = Time.unscaledTime + 0.7f; // let the last card land before the HUD shows the change
    }

    // ------------------------------------------------------------------ plates

    void ApplyPlates()
    {
        if (!hasSnap || station == null) return;
        long cash = Cash;
        for (int i = 0; i < station.SpotCount; i++)
        {
            var sp = station.Spot(i);
            if (sp == null || sp.buttons == null) continue;
            bool mine = i == mySeat;
            var legal = BlackjackSnapshotRules.Legal(snap, i);
            long stake = snap.seats != null && i < snap.seats.Length ? snap.seats[i].betCents : 0;
            for (int k = 0; k < CasinoLayout.BlackjackButtonIds.Length; k++)
            {
                var b = sp.Button(CasinoLayout.BlackjackButtonIds[k]);
                if (b == null) continue;
                var st = BlackjackPlates.For(CasinoLayout.BlackjackButtonIds[k], legal, stake, mine ? cash : long.MaxValue);
                b.SetEnabled(mine ? st.enabled : true);   // somebody else's plates stay clickable-looking (and lit on their turn) but a press there does nothing here
                b.SetLit(st.lit);
                if (b.label != st.label) b.SetLabel(st.label);
            }
        }
    }

    protected override void OnPlate(CasinoButton b)
    {
        if (!hasSnap || mySeat != spotIndex) return;
        var legal = BlackjackSnapshotRules.Legal(snap, mySeat);
        if (BlackjackPlates.TryAction(b.id, legal, out var a)) Act(a);
    }

    // ------------------------------------------------------------------ input

    protected override void Tick(float dt)
    {
        if (!hasSnap) return;
        ring?.Pulse(Time.unscaledTime);
        rackGlow?.Refresh(Time.unscaledTime);
        pendingLabel?.Face(Camera.main);
        if (releaseHoldAt > 0f && Time.unscaledTime >= releaseHoldAt) { releaseHoldAt = -1f; HoldCash(false); }
        if (mySeat < 0) return;

        long cash = Cash;
        bool pad = CasinoInput.UsingPad;
        double left = SecondsLeft;
        int sec = double.IsInfinity(left) ? -1 : (int)Math.Ceiling(Math.Max(0.0, left));
        if (cash != lastCash || pad != lastPad || sec != lastSecond)
        {
            if (cash != lastCash) { lastCash = cash; ApplyPlates(); }
            lastPad = pad; lastSecond = sec;
            RefreshStrip();
        }

        if (CasinoInput.Down(CasinoInput.HintKey)) { adviceOn = !adviceOn; Say(adviceOn ? "Basic strategy hints on." : "Basic strategy hints off.", HintTone.Normal, 1.8f); RefreshStrip(); }

        bool canBet = BlackjackSnapshotRules.CanBet(snap, mySeat, left) && !sendInFlight;
        var legal = BlackjackSnapshotRules.Legal(snap, mySeat);

        // chips: select (also while waiting, so the next hand is ready)
        int step = legal == BlackjackActions.None ? CasinoInput.CycleStep() : 0;
        if (step != 0) SelectChip(chips.Step(step), true);
        int digit = CasinoInput.DigitDown();
        if (digit >= 1 && digit <= chips.Count) SelectChip(digit - 1, true);

        if (canBet)
        {
            if (CasinoInput.Down(KeyCode.Space)) AddChip();
            if (CasinoInput.Down(KeyCode.Backspace)) RemoveChip();
            if (CasinoInput.Down(CasinoInput.ClearKey)) ClearStack();
            if (CasinoInput.Down(CasinoInput.RebetKey) || CasinoInput.PadDpadDown) SameAgain();
            if (CasinoInput.EnterDown() || CasinoInput.PadY) SendBet(false);
            if (CasinoInput.Down(CasinoInput.SitOutKey) || CasinoInput.PadL3) SitOut();
        }
        else if (snap.phase == BlackjackTablePhase.Betting && !BlackjackSnapshotRules.BetPlaced(snap, mySeat))
        {
            // betting is open but we cannot bet (sent / out of time): allow stack editing for the next hand
            if (CasinoInput.Down(KeyCode.Space)) AddChip();
            if (CasinoInput.Down(CasinoInput.RebetKey)) SameAgain();
        }
        else if (legal == BlackjackActions.None && snap.phase != BlackjackTablePhase.Betting)
        {
            // between hands: build the next stake in peace
            if (CasinoInput.Down(KeyCode.Space)) AddChip();
            if (CasinoInput.Down(CasinoInput.RebetKey)) SameAgain();
            if (CasinoInput.Down(KeyCode.Backspace)) RemoveChip();
            if (CasinoInput.Down(CasinoInput.ClearKey)) ClearStack();
        }

        if (legal != BlackjackActions.None && Time.unscaledTime - legalSince > 0.25f)
        {
            bool window = BlackjackSnapshotRules.Can(legal, BlackjackAction.Insurance) || BlackjackSnapshotRules.Can(legal, BlackjackAction.NoInsurance);
            if (window)
            {
                if (CasinoInput.Down(CasinoInput.InsuranceKey) || CasinoInput.PadX) Act(BlackjackAction.Insurance);
                else if (CasinoInput.Down(CasinoInput.NoInsuranceKey) || CasinoInput.Down(CasinoInput.StandKey) || CasinoInput.PadY) Act(BlackjackAction.NoInsurance);
            }
            else
            {
                if (CasinoInput.Down(CasinoInput.HitKey) || CasinoInput.PadX) Act(BlackjackAction.Hit);
                else if (CasinoInput.Down(CasinoInput.StandKey) || CasinoInput.PadY) Act(BlackjackAction.Stand);
                else if (CasinoInput.Down(CasinoInput.DoubleKey) || CasinoInput.PadRb) Act(BlackjackAction.Double);
                else if (CasinoInput.Down(CasinoInput.SplitKey) || CasinoInput.PadLb) Act(BlackjackAction.Split);
            }
        }

        // the stake goes out by itself a second before the clock ends
        if (BetTiming.ShouldAutoSend(snap.phase, left, pending.Legal && pending.Total <= cash, BlackjackSnapshotRules.BetPlaced(snap, mySeat), snap.seats[mySeat].Has(BlackjackSeatSnapshot.FlagSitOut), sendInFlight))
            SendBet(true);
    }

    protected override void OnClicked(CasinoCursor.Click c)
    {
        if (!hasSnap || mySeat < 0) return;
        if (c.secondary) { if (BlackjackSnapshotRules.Legal(snap, mySeat) == BlackjackActions.None) RemoveChip(); return; }
        var t = c.hit ? c.target : null;
        if (t != null && t.station != null && t.station != station) return;
        if (t == null) return; // a click on nothing does nothing
        switch (t.kind)
        {
            case CasinoPickKind.ChipBin:
                if (t.spot == spotIndex || t.spot < 0) { SelectChip(t.index, true); Sfx.Play("click", null, 0.3f, 1.3f); }
                break;
            case CasinoPickKind.BetCircle:
                if (t.spot == spotIndex || t.spot < 0) AddChip();
                break;
            case CasinoPickKind.Button:
                if (t.spot == spotIndex && !t.Clickable)
                {
                    var b = t as CasinoButton;
                    var st = BlackjackPlates.For(b != null ? b.id : t.id, BlackjackSnapshotRules.Legal(snap, mySeat), snap.seats[mySeat].betCents, Cash);
                    if (!string.IsNullOrEmpty(st.reason)) Refuse(st.reason, 2.2f);
                }
                break;
        }
    }

    protected override void OnHover(CasinoPickable from, CasinoPickable to)
    {
        pickedHoverBin = to != null && to.kind == CasinoPickKind.ChipBin && (to.spot == spotIndex || to.spot < 0) ? to.index : -1;
        rackGlow?.Set(chips.Index, pickedHoverBin);
    }

    protected override string DescribeHover(CasinoPickable p)
    {
        if (p == null || !hasSnap) return "";
        bool mineSpot = p.spot == spotIndex || p.spot < 0;
        switch (p.kind)
        {
            case CasinoPickKind.ChipBin:
                return mineSpot ? Dollars(chips.CentsAt(p.index)) + " chips - click to pick them" : "";
            case CasinoPickKind.BetCircle:
                if (!mineSpot) return "";
                return BlackjackSnapshotRules.CanBet(snap, mySeat, SecondsLeft) ? "Bet circle - click to stack a " + Dollars(chips.Cents) + " chip" : "Betting is closed";
            case CasinoPickKind.Button:
            {
                if (!mineSpot) return "";
                var b = p as CasinoButton;
                string id = b != null ? b.id : p.id;
                var legal = BlackjackSnapshotRules.Legal(snap, mySeat);
                var st = BlackjackPlates.For(id, legal, snap.seats[mySeat].betCents, Cash);
                string name = id == "Stand" && st.label.StartsWith("NO") ? "No insurance" : id;
                return st.enabled ? name + " - click or press " + KeyFor(id) : name + ": " + st.reason;
            }
        }
        return p.hint;
    }

    static string KeyFor(string id)
    {
        bool pad = CasinoInput.UsingPad;
        switch (id)
        {
            case "Hit": return pad ? "X" : "Space";
            case "Stand": return pad ? "Y" : "S";
            case "Double": return pad ? "RB" : "D";
            case "Split": return pad ? "LB" : "P";
            case "Insurance": return pad ? "X" : "I";
        }
        return "";
    }

    // ------------------------------------------------------------------ the stake

    void SelectChip(int i, bool sound = false)
    {
        chips.Select(i);
        lastChip = chips.Index;
        ring?.Place(spot.chipRack != null && chips.Index < spot.chipRack.Length ? spot.chipRack[chips.Index] : null);
        rackGlow?.Set(chips.Index, pickedHoverBin);
        if (sound) Sfx.Play("click", null, 0.3f, 1.3f);
        RefreshStrip();
    }

    void AddChip()
    {
        var r = pending.Add(chips.Cents, Cash);
        if (r != PendingAdd.Added) { Refuse(PendingBet.Say(r, chips.Cents, Cash)); return; }
        Sfx.Play("click", spot.betCircle != null ? spot.betCircle.position : (Vector3?)null, 0.45f, 1.4f + 0.04f * pending.Count);
        RefreshPending();
    }

    void RemoveChip()
    {
        if (!pending.RemoveLast(out _)) return;
        Sfx.Play("click", null, 0.35f, 1.1f);
        RefreshPending();
    }

    void ClearStack()
    {
        if (pending.IsEmpty) return;
        pending.Clear();
        RefreshPending();
    }

    void SameAgain()
    {
        if (lastBet <= 0) { Refuse("No earlier bet to repeat.", 2f); return; }
        if (Cash < lastBet) { Refuse("You can't cover your last bet of " + Dollars(lastBet) + ".", 2.5f); return; }
        pending.SetTotal(lastBet, CasinoLayout.BlackjackChipCents);
        Sfx.Play("click", null, 0.4f, 1.3f);
        RefreshPending();
    }

    void RefreshPending()
    {
        ghostStack?.Set(pending.Chips);
        if (pendingLabel != null) pendingLabel.Text = pending.IsEmpty ? "" : "BET " + Dollars(pending.Total);
        RefreshStrip();
    }

    void SendBet(bool auto)
    {
        if (!hasSnap || mySeat < 0 || sendInFlight) return;
        if (pending.IsEmpty) { if (!auto) Refuse("Stack a chip on the circle first (click it, or Space)."); return; }
        var err = pending.Check(Cash);
        if (err != CasinoTableError.Ok) { if (!auto || err != CasinoTableError.BelowMinimum) Refuse(PendingBet.Say(err, Cash)); return; }
        long total = pending.Total;
        sendInFlight = true;
        HoldCash(true); // the answer may carry a win (a natural settles quickly): keep the HUD quiet until the round shows it
        CasinoClient.BlackjackBet(total, r =>
        {
            sendInFlight = false;
            if (IsEnded) return;
            if (r.Ok)
            {
                lastBet = total;
                pending.Clear();
                Say((auto ? "Bet auto-placed: " : "Bet down: ") + Dollars(total), HintTone.Gold, 2.5f);
                Sfx.Play("click", spot.betCircle != null ? spot.betCircle.position : (Vector3?)null, 0.55f, 0.9f);
                RefreshPending();
                return;
            }
            if (r.gate == CasinoError.Ok && r.error == CasinoTableError.AlreadyBet) { pending.Clear(); RefreshPending(); }
            if (!BlackjackSnapshotRules.BetPlaced(snap, mySeat)) HoldCash(false);
            Refuse(r.gate == CasinoError.Ok ? PendingBet.Say(r.error, Cash) : CasinoTables.Describe(r), 3.5f);
            RefreshStrip();
        });
    }

    void SitOut()
    {
        if (!hasSnap || snap.phase != BlackjackTablePhase.Betting) { Refuse("You can sit a hand out while betting is open.", 2f); return; }
        if (BlackjackSnapshotRules.BetPlaced(snap, mySeat)) { Refuse("Your bet is already down.", 2f); return; }
        CasinoClient.BlackjackSitOut(r =>
        {
            if (IsEnded) return;
            if (r.Ok) { pending.Clear(); RefreshPending(); Say("Sitting this one out.", HintTone.Normal, 2.5f); }
            else Refuse(r.gate == CasinoError.Ok ? PendingBet.Say(r.error, Cash) : CasinoTables.Describe(r), 3f);
        });
    }

    void Act(BlackjackAction a)
    {
        if (!hasSnap || mySeat < 0 || actionInFlight) return;
        var legal = BlackjackSnapshotRules.Legal(snap, mySeat);
        if (!BlackjackSnapshotRules.Can(legal, a)) return;
        long cost = BlackjackSnapshotRules.Cost(snap, mySeat, a);
        if (cost > Cash) { Refuse("You can't cover +" + Dollars(cost) + ".", 2.5f); return; }
        actionInFlight = true;
        HoldCash(true);
        CasinoClient.BlackjackAct(a, r =>
        {
            actionInFlight = false;
            if (IsEnded) return;
            if (!r.Ok) Refuse(r.gate == CasinoError.Ok ? CasinoTables.Describe(r) : CasinoBank.Describe(r.gate), 3f);
            else Sfx.Play(a == BlackjackAction.Hit ? "pop" : "click", null, 0.4f, a == BlackjackAction.Stand ? 0.9f : 1.2f);
        });
    }

    // ------------------------------------------------------------------ the hint strip

    void RefreshStrip()
    {
        if (strip == null || IsEnded) return;
        bool pad = CasinoInput.UsingPad;
        string K(string kb, string pd, string label) => CasinoHintStrip.Key(pad ? pd : kb, label);
        string tail = "  " + K("Shift", "LT", "lean") + "  " + K("V", "D-pad up", "dealer") + "  " + K("Esc", "B", "leave");

        if (!hasSnap || mySeat < 0)
        {
            strip.SetHint(K("Esc", "B", "leave"));
            strip.SetInfo("Taking a seat...");
            return;
        }
        double left = SecondsLeft;
        string cd = CasinoSeatText.Countdown(left);
        var legal = BlackjackSnapshotRules.Legal(snap, mySeat);
        bool betPlaced = BlackjackSnapshotRules.BetPlaced(snap, mySeat);
        bool canBet = BlackjackSnapshotRules.CanBet(snap, mySeat, left);
        string hint, info = "";

        if (legal != BlackjackActions.None)
        {
            bool window = BlackjackSnapshotRules.Can(legal, BlackjackAction.Insurance) || BlackjackSnapshotRules.Can(legal, BlackjackAction.NoInsurance);
            long myStake = snap.seats[mySeat].betCents;
            if (window)
            {
                long cost = myStake / 2;
                hint = (BlackjackSnapshotRules.Can(legal, BlackjackAction.Insurance) && cost <= Cash ? K("I", "X", "insurance +" + Dollars(cost)) + "  " : "") + K("N/S", "Y", "no thanks");
                info = "Dealer shows an ace. Insurance? " + cd;
            }
            else
            {
                var parts = new List<string>();
                parts.Add(K("Space", "X", "hit"));
                parts.Add(K("S", "Y", "stand"));
                if (BlackjackSnapshotRules.Can(legal, BlackjackAction.Double) && Cash >= myStake) parts.Add(K("D", "RB", "double +" + Dollars(myStake)));
                if (BlackjackSnapshotRules.Can(legal, BlackjackAction.Split) && Cash >= myStake) parts.Add(K("P", "LB", "split +" + Dollars(myStake)));
                parts.Add(K("H", "H", "hint"));
                hint = string.Join("  ", parts);
                info = "Your move. " + cd;
            }
            if (adviceOn && BlackjackSnapshotRules.TryAdvice(snap, mySeat, out var advice))
                info += "   Basic strategy: " + (advice == BlackjackAction.NoInsurance ? "no insurance" : advice.ToString().ToLowerInvariant());
            strip.SetHint(hint);
            strip.SetInfo(info);
            return;
        }

        switch (snap.phase)
        {
            case BlackjackTablePhase.Idle:
            case BlackjackTablePhase.Betting:
                if (canBet)
                {
                    hint = K("Q/E", "LB/RB", "chip") + "  " + K("Space", "A", "stack") + "  " + K("RMB", "X", "take off") + "  " + K("Enter", "Y", "deal") + "  " + K("R", "D-pad down", "same bet") + "  " + K("O", "L3", "sit out") + tail;
                    info = pending.IsEmpty
                        ? "Place your bet, " + CasinoRules.Dollars(CasinoRules.BlackjackMinCents) + " to " + CasinoRules.Dollars(CasinoRules.BlackjackMaxCents) + (cd.Length > 0 && snap.phase == BlackjackTablePhase.Betting ? "   Betting closes in " + cd : "")
                        : "Bet " + Dollars(pending.Total) + (pending.Legal ? "" : "  (min " + Dollars(CasinoRules.BlackjackMinCents) + ")") + (cd.Length > 0 && snap.phase == BlackjackTablePhase.Betting ? "   goes down in " + cd : "");
                }
                else
                {
                    hint = tail.TrimStart();
                    bool out_ = snap.seats[mySeat].Has(BlackjackSeatSnapshot.FlagSitOut);
                    info = betPlaced ? "Bet down: " + Dollars(snap.seats[mySeat].betCents) + ". Waiting for the others" + (cd.Length > 0 ? " " + cd : "")
                         : out_ ? "Sitting this hand out." : "Next hand: build your bet.";
                }
                break;
            case BlackjackTablePhase.Deal: hint = tail.TrimStart(); info = betPlaced ? "Dealing..." : "Dealing. You're out of this one."; break;
            case BlackjackTablePhase.Insurance: hint = tail.TrimStart(); info = "Insurance is open for the others. " + cd; break;
            case BlackjackTablePhase.Turns:
            {
                hint = tail.TrimStart();
                int who = snap.activeSeat;
                string name = who >= 0 && who < snap.seats.Length ? CasinoClient.NameOf(snap.seats[who].key) : "";
                info = (string.IsNullOrEmpty(name) ? "Seat " + (who + 1) : name) + " to play. " + cd;
                break;
            }
            case BlackjackTablePhase.Dealer: hint = tail.TrimStart(); info = "Dealer plays..."; break;
            default: hint = K("Q/E", "LB/RB", "chip") + "  " + K("Space", "A", "stack") + tail; info = "Next hand soon."; break;
        }
        strip.SetHint(hint);
        strip.SetInfo(info);
    }
}
