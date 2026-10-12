using System;
using UnityEngine;

/// <summary>
/// The shared roulette wheel <c>rl0</c> (Docs/Casino.md §9, §12). The cursor is free over the felt: hovering a zone shows a see-through chip
/// at the spot a click would bet on and its name and payout on the hint strip ("Corner 17-18-20-21 - pays 8 to 1"); left click stacks the
/// selected chip, right click takes the last chip off that spot; Z undoes, C clears, R brings the last spin's bets back, Space / the READY plate
/// says "spin now". The whole set goes to the host with <c>RouletteSetBets</c> after every change (coalesced to about four a second); the host's
/// accepted set (the snapshot's participant bets) is the truth and replaces the local one whenever nothing of mine is unsent. After the lock
/// (<c>Locked</c> / <c>Spinning</c> / <c>Result</c>) input is closed, the hint strip says NO MORE BETS and the HUD cash stays quiet until the
/// result. The chips, the wheel, the ball and the marker are the presenters'; <c>CasinoRouletteLayout</c> is the zone geometry.
/// </summary>
public sealed class RouletteController : CasinoController
{
    static RouletteBet[] lastSet = new RouletteBet[0];
    static int lastChip = 1;

    readonly ChipSelector chips = new ChipSelector(CasinoLayout.RouletteChipCents, 1);
    readonly RouletteBetBook book = new RouletteBetBook();
    readonly SendGate gate = new SendGate();
    CasinoGhostChip ghost;
    CasinoSelectionRing ring;
    ChipRackGlow rackGlow;

    RouletteTableSnapshot snap;
    bool hasSnap;
    int sentVersion;
    int inFlight;
    bool ready;
    bool absentWarned, forceAdopt;
    int hoveredBin = -1;
    bool hoverSpotValid;
    RouletteBet hoverSpot;
    float savedEdgeBand;
    float releaseHoldAt = -1f;
    int resultRound = -1;
    long lastCash = long.MinValue;
    int lastSecond = int.MinValue;
    bool lastPad;
    RouletteTablePhase lastPhase = RouletteTablePhase.Idle;

    public override string TableId => CasinoTableIds.Roulette;
    protected override CasinoSeatCamera.CursorMode WantedMode => CasinoSeatCamera.CursorMode.Free;

    // ------------------------------------------------------------------ lifecycle

    protected override void OnBegin()
    {
        chips.Select(lastChip);
        savedEdgeBand = cam.edgeBand;
        cam.edgeBand = 0.03f; // the rack and the plates sit near the bottom edge: a thin panning band
        ghost = new CasinoGhostChip(station.transform);
        ring = new CasinoSelectionRing();
        rackGlow = new ChipRackGlow(spot.chipRack);
        SelectChip(chips.Index, false);
        CasinoClient.RouletteSnapshotChanged += OnSnapshot;
        if (!CasinoClient.HasRoulette) CasinoClient.RequestSync();
        else OnSnapshot(CasinoClient.LatestRoulette, CasinoClient.ClockOffset);
        ApplyPlates();
        RefreshStrip();
    }

    protected override void OnEnd(CasinoSeatCamera.ExitReason reason)
    {
        CasinoClient.RouletteSnapshotChanged -= OnSnapshot;
        lastChip = chips.Index;
        if (cam != null) cam.edgeBand = savedEdgeBand;
        // chips already charged stay on the felt and pay: say so
        if (hasSnap && snap.phase >= RouletteTablePhase.Locked && Me(out var me) && me.Has(RouletteParticipantSnapshot.FlagCharged) && !me.Has(RouletteParticipantSnapshot.FlagSettled))
            GameHUD.Toast("Your bets ride. Winnings go to your account.", ToastStyle.Info, 2.5f);
        ghost?.Destroy();
        ring?.Destroy();
        rackGlow?.Clear();
        if (spot != null && spot.buttons != null)
            foreach (var b in spot.buttons) if (b != null) { b.SetEnabled(true); b.SetLit(false); if (b.id == "Ready") b.SetLabel("READY"); }
    }

    public override void OnNotice(string table, CasinoNotice notice)
    {
        if (table != CasinoTableIds.Roulette) return;
        switch (notice)
        {
            case CasinoNotice.SetCancelled:
                GameHUD.Toast("Not enough cash: your bets were cancelled.", ToastStyle.Bad, 3f);
                Refuse("Not enough cash for those bets. They were cancelled.", 5f);
                forceAdopt = true; // the host's set is empty now
                break;
            case CasinoNotice.Released:
                GameHUD.Toast("The house stood you up from the wheel.", ToastStyle.Info, 2.2f);
                RequestExit();
                break;
        }
    }

    // ------------------------------------------------------------------ the host's picture

    bool Me(out RouletteParticipantSnapshot me)
    {
        me = default;
        if (!hasSnap || snap.participants == null) return false;
        int key = CasinoClient.LocalKey;
        for (int i = 0; i < snap.participants.Length; i++)
            if (snap.participants[i].key == key && !snap.participants[i].Has(RouletteParticipantSnapshot.FlagAbsent)) { me = snap.participants[i]; return true; }
        return false;
    }

    double SecondsLeft => double.IsInfinity(snap.phaseDeadline) ? double.PositiveInfinity : snap.phaseDeadline - CasinoClient.TableTime();
    bool BettingOpen => hasSnap && RouletteSync.BettingOpen(snap.phase, SecondsLeft);
    bool Dirty => book.Version != sentVersion;

    void OnSnapshot(RouletteTableSnapshot s, double offset)
    {
        if (IsEnded) return;
        var prev = lastPhase;
        snap = s;
        hasSnap = true;
        lastPhase = s.phase;
        if (!Me(out var me))
        {
            if (SecondsSinceBegan > 2.0 && !absentWarned)
            {
                absentWarned = true;
                GameHUD.Toast("You're not at the wheel any more.", ToastStyle.Info, 2f);
                RequestExit();
            }
            return;
        }
        ready = me.Has(RouletteParticipantSnapshot.FlagReady);

        // the host's set is the truth whenever nothing of mine is unsent or in flight, and always once the bets are locked
        if ((forceAdopt || RouletteSync.ShouldAdopt(Dirty, inFlight, s.phase)) && !book.SameAs(me.bets))
        {
            book.Adopt(me.bets, CasinoLayout.RouletteChipCents);
            sentVersion = book.Version;
            ghostRefreshNeeded = true;
        }
        if (forceAdopt && inFlight == 0 && !Dirty) forceAdopt = false;

        // the lock: remember what played (for Rebet), keep the HUD quiet through the spin
        if ((s.phase == RouletteTablePhase.Locked || s.phase == RouletteTablePhase.Spinning) && me.BetCount > 0)
        {
            lastSet = (RouletteBet[])me.bets.Clone();
            if (me.Has(RouletteParticipantSnapshot.FlagCharged)) { HoldCash(true); releaseHoldAt = -1f; }
        }
        if (s.phase == RouletteTablePhase.Locked && prev != RouletteTablePhase.Locked)
        {
            Say("NO MORE BETS", HintTone.Gold, 0f);
            Sfx.Play("whoosh", null, 0.5f, 0.8f);
        }
        if (s.phase == RouletteTablePhase.Result && prev != RouletteTablePhase.Result) OnResult(me);
        if (s.phase == RouletteTablePhase.Idle || s.phase == RouletteTablePhase.Betting)
        {
            if (prev == RouletteTablePhase.Result || prev == RouletteTablePhase.Spinning) { Say("", HintTone.Normal, 0f); } // clears "NO MORE BETS" / the result
            if (HoldingCash && releaseHoldAt < 0f && me.BetCount == 0) HoldCash(false);
        }
        ApplyPlates();
        RefreshStrip();
    }

    bool ghostRefreshNeeded;

    void OnResult(RouletteParticipantSnapshot me)
    {
        if (resultRound == snap.round) return;
        resultRound = snap.round;
        if (HoldingCash) releaseHoldAt = Time.unscaledTime + 1.0f; // the ball has dropped: let the marker land first
        if (!snap.HasPocket) return;
        string pocket = RouletteRules.PocketName(snap.pocket);
        string colour = RouletteRules.IsRed(snap.pocket) ? "red" : RouletteRules.IsBlack(snap.pocket) ? "black" : "green";
        if (me.stakedCents <= 0) { Say(pocket + " " + colour + ".", HintTone.Normal, 6f); return; }
        long net = (long)me.returnCents - me.stakedCents;
        if (net > 0)
        {
            Say(pocket + " " + colour + "! You're up " + Dollars(net) + " on the spin.", HintTone.Good, 6f);
            CasinoControl.Celebrate(net, 0, pocket + " at roulette");
            Sfx.Play("ding", null, 0.5f, 1.1f);
        }
        else if (net == 0) Say(pocket + " " + colour + ". You broke even.", HintTone.Normal, 6f);
        else
        {
            Say(pocket + " " + colour + ". The house takes " + Dollars(-net) + (me.returnCents > 0 ? " (" + Dollars(me.returnCents) + " came back)." : "."), HintTone.Bad, 6f);
            Sfx.Play("thud", null, 0.35f, 0.8f);
        }
    }

    // ------------------------------------------------------------------ plates

    void ApplyPlates()
    {
        if (spot == null || !hasSnap) return;
        bool open = BettingOpen;
        bool betting = snap.phase == RouletteTablePhase.Betting && SecondsLeft > 0.0;
        Plate("Ready", betting, ready);
        Plate("Undo", open && !book.IsEmpty, false);
        Plate("Clear", open && !book.IsEmpty, false);
        Plate("Rebet", open && lastSet != null && lastSet.Length > 0, false);
    }

    void Plate(string id, bool enabled, bool lit)
    {
        var b = spot.Button(id);
        if (b == null) return;
        b.SetEnabled(enabled);
        b.SetLit(enabled && (lit || id != "Ready"));
        if (id == "Ready" && b.label != (ready ? "READY!" : "READY")) b.SetLabel(ready ? "READY!" : "READY");
    }

    protected override void OnPlate(CasinoButton b)
    {
        switch (b.id)
        {
            case "Ready": ToggleReady(); break;
            case "Undo": UndoChip(); break;
            case "Clear": ClearAll(); break;
            case "Rebet": Rebet(); break;
        }
    }

    // ------------------------------------------------------------------ input

    protected override void Tick(float dt)
    {
        if (!hasSnap) return;
        double now = Time.realtimeSinceStartupAsDouble;
        ring?.Pulse(Time.unscaledTime);
        rackGlow?.Refresh(Time.unscaledTime);
        if (releaseHoldAt > 0f && Time.unscaledTime >= releaseHoldAt) { releaseHoldAt = -1f; HoldCash(false); }
        if (ghostRefreshNeeded) { ghostRefreshNeeded = false; ApplyPlates(); RefreshStrip(); }

        long cash = Cash;
        bool pad = CasinoInput.UsingPad;
        double left = SecondsLeft;
        int sec = double.IsInfinity(left) ? -1 : (int)Math.Ceiling(Math.Max(0.0, left));
        if (cash != lastCash || pad != lastPad || sec != lastSecond)
        {
            lastCash = cash; lastPad = pad; lastSecond = sec;
            ApplyPlates();
            RefreshStrip();
        }

        UpdateGhost();

        int step = CasinoInput.CycleStep();
        if (step != 0) SelectChip(chips.Step(step), true);
        int digit = CasinoInput.DigitDown();
        if (digit >= 1 && digit <= chips.Count) SelectChip(digit - 1, true);

        if (BettingOpen)
        {
            if (CasinoInput.Down(CasinoInput.UndoKey) || CasinoInput.Down(KeyCode.Backspace) || CasinoInput.PadDpadDown) UndoChip();
            if (CasinoInput.Down(CasinoInput.ClearKey)) ClearAll();
            if (CasinoInput.Down(CasinoInput.RebetKey) || CasinoInput.PadL3) Rebet();
            if (CasinoInput.Down(KeyCode.Space) || CasinoInput.EnterDown() || CasinoInput.PadY) ToggleReady();
        }

        // changes go out as one whole set, about four a second; a last one right before the lock
        if (BettingOpen && Dirty)
        {
            bool urgent = left < 0.7 && now - gate.LastAt >= 0.1;
            if (gate.Due(now, true) || urgent) SendSet(now);
        }
    }

    void SendSet(double now)
    {
        var bets = book.ToBets();
        int version = book.Version;
        sentVersion = version;
        gate.Mark(now);
        inFlight++;
        CasinoClient.RouletteSetBets(bets, bets.Length, r =>
        {
            inFlight = Math.Max(0, inFlight - 1);
            if (IsEnded) return;
            if (r.Ok) return;
            // refused: the host kept its old set. Take it back, and say why.
            forceAdopt = true;
            string why;
            if (r.gate != CasinoError.Ok) why = CasinoBank.Describe(r.gate);
            else if (r.error == CasinoTableError.BadBets) why = CasinoSeatText.Say(r.betError, r.betError == RouletteBetError.AboveSpotMaximum && hoverSpotValid && RouletteRules.IsInside(hoverSpot.type));
            else if (r.error == CasinoTableError.TooLate || r.error == CasinoTableError.WrongPhase) why = "Too late: no more bets.";
            else why = CasinoTables.Describe(r);
            Refuse(why, 3.5f);
            if (hasSnap) OnSnapshot(snap, 0.0);
        });
    }

    protected override void OnClicked(CasinoCursor.Click c)
    {
        if (!hasSnap) return;
        var t = c.hit ? c.target : null;
        if (t == null) return;
        if (t.station != null && t.station != station) return;
        if (t.kind == CasinoPickKind.ChipBin)
        {
            if (!c.secondary && (t.spot == spotIndex || t.spot < 0)) { SelectChip(t.index, true); }
            return;
        }
        if (t.kind == CasinoPickKind.Layout)
        {
            if (!station.LayoutHitLocal(c.point, out float u, out float v)) return;
            var s = CasinoRouletteLayout.SpotAt(u, v);
            if (!s.HasValue) return;
            if (!BettingOpen) { Refuse(snap.phase == RouletteTablePhase.Idle || snap.phase == RouletteTablePhase.Betting ? "Betting is closed." : "No more bets.", 2f); return; }
            if (c.secondary) TakeChipOff(s.Value); else PlaceChip(s.Value);
            return;
        }
        if (t.kind == CasinoPickKind.Button && !t.Clickable && t.spot == spotIndex)
            Say(PlateWhyNot((t as CasinoButton)?.id ?? t.id), HintTone.Normal, 2f);
    }

    protected override void OnHover(CasinoPickable from, CasinoPickable to)
    {
        hoveredBin = to != null && to.kind == CasinoPickKind.ChipBin && (to.spot == spotIndex || to.spot < 0) ? to.index : -1;
        rackGlow?.Set(chips.Index, hoveredBin);
    }

    string PlateWhyNot(string id)
    {
        if (!hasSnap) return "";
        if (!BettingOpen) return "No more bets.";
        switch (id)
        {
            case "Ready": return "Ready works while betting is open.";
            case "Rebet": return "Nothing to rebet yet.";
            default: return "Nothing on the felt.";
        }
    }

    // ------------------------------------------------------------------ the cursor over the felt

    /// <summary>Where would a click bet? The spot under the cursor on the printed layout, or none.</summary>
    bool SpotUnderCursor(out RouletteBet spotBet, out Vector3 world)
    {
        spotBet = default; world = default;
        if (cursor == null || cursor.Hovered == null || cursor.Hovered.kind != CasinoPickKind.Layout) return false;
        if (!station.LayoutHitLocal(cursor.HitPoint, out float u, out float v)) return false;
        var s = CasinoRouletteLayout.SpotAt(u, v);
        if (!s.HasValue) return false;
        spotBet = s.Value;
        if (!CasinoRouletteLayout.BetCentre(spotBet, out float cu, out float cv)) return false;
        world = station.LayoutPoint(cu, cv);
        return true;
    }

    string hoverText = "", hoverPays = "";
    RouletteBetType hoverShownType;
    int hoverShownIndex = -1;
    long hoverShownOn = -1;

    void UpdateGhost()
    {
        if (ghost == null) return;
        if (SpotUnderCursor(out var bet, out var world) && BettingOpen)
        {
            hoverSpot = bet; hoverSpotValid = true;
            int down = book.ChipsOnSpot(bet.type, bet.index);
            long on = book.OnSpot(bet.type, bet.index);
            if (bet.type != hoverShownType || bet.index != hoverShownIndex || on != hoverShownOn)
            {
                // only when the cursor moves to another spot (or its stack changes): no strings per frame
                hoverShownType = bet.type; hoverShownIndex = bet.index; hoverShownOn = on;
                hoverPays = CasinoSeatText.Pays(bet.type).Substring(5);
                hoverText = CasinoSeatText.SpotName(bet.type, bet.index) + " \u00b7 " + CasinoSeatText.Pays(bet.type) + (on > 0 ? " \u00b7 " + Dollars(on) + " on it" : "");
                if (strip != null) strip.SetHover(hoverText);
            }
            Vector3 lift = station.layoutOrigin != null ? station.layoutOrigin.up * (CasinoLayout.ChipThickness * 1.05f * down + 0.002f) : Vector3.up * 0.004f;
            var rot = station.layoutOrigin != null ? station.layoutOrigin.rotation : Quaternion.identity;
            ghost.Show(world + lift, rot, chips.Cents, hoverPays);
        }
        else
        {
            if (hoverSpotValid && strip != null) strip.SetHover("");
            hoverSpotValid = false;
            hoverShownIndex = -1; hoverShownOn = -1;
            ghost.Hide();
        }
    }

    protected override string DescribeHover(CasinoPickable p)
    {
        if (p == null || !hasSnap) return "";
        switch (p.kind)
        {
            case CasinoPickKind.ChipBin: return (p.spot == spotIndex || p.spot < 0) ? Dollars(chips.CentsAt(p.index)) + " chips - click to pick them" : "";
            case CasinoPickKind.Layout: return hoverSpotValid ? hoverText : ""; // UpdateGhost keeps it current
            case CasinoPickKind.Button:
            {
                if (p.spot != spotIndex) return "";
                string id = (p as CasinoButton)?.id ?? p.id;
                bool pad = CasinoInput.UsingPad;
                switch (id)
                {
                    case "Ready": return BettingOpen ? "Ready: spin as soon as everyone is ready (" + (pad ? "Y" : "Space") + ")" : PlateWhyNot(id);
                    case "Undo": return book.IsEmpty ? PlateWhyNot(id) : "Undo the last chip (" + (pad ? "D-pad down" : "Z") + ")";
                    case "Clear": return book.IsEmpty ? PlateWhyNot(id) : "Take every chip back (C)";
                    case "Rebet": return lastSet.Length == 0 ? PlateWhyNot(id) : "Same bets as last spin: " + Dollars(RouletteRules.TotalStake(lastSet, lastSet.Length)) + " (" + (pad ? "L3" : "R") + ")";
                }
                return p.hint;
            }
        }
        return p.hint;
    }

    // ------------------------------------------------------------------ chips and the set

    void SelectChip(int i, bool sound = false)
    {
        chips.Select(i);
        lastChip = chips.Index;
        ring?.Place(spot.chipRack != null && chips.Index < spot.chipRack.Length ? spot.chipRack[chips.Index] : null);
        rackGlow?.Set(chips.Index, hoveredBin);
        if (sound) Sfx.Play("click", null, 0.3f, 1.3f);
        RefreshStrip();
    }

    void PlaceChip(RouletteBet spotBet)
    {
        if (!BettingOpen) return;
        var r = book.TryAdd(spotBet.type, spotBet.index, chips.Cents, Cash, out var err);
        if (r == BookAdd.Refused) { Refuse(CasinoSeatText.Say(err, RouletteRules.IsInside(spotBet.type))); return; }
        if (r == BookAdd.CantAfford) { Refuse("You can't cover another " + Dollars(chips.Cents) + " (you have " + Dollars(Cash) + ")."); return; }
        Sfx.Play("click", null, 0.45f, 1.5f);
        Changed();
    }

    void TakeChipOff(RouletteBet spotBet)
    {
        if (!BettingOpen) return;
        if (book.RemoveLastAt(spotBet.type, spotBet.index, out _)) { Sfx.Play("click", null, 0.35f, 1.1f); Changed(); }
    }

    void UndoChip()
    {
        if (!BettingOpen) return;
        if (book.Undo(out _, out _, out _)) { Sfx.Play("click", null, 0.35f, 1.1f); Changed(); }
    }

    void ClearAll()
    {
        if (!BettingOpen) return;
        if (book.Clear()) { Sfx.Play("click", null, 0.35f, 0.9f); Changed(); }
    }

    void Rebet()
    {
        if (!BettingOpen) return;
        if (lastSet == null || lastSet.Length == 0) { Refuse("Nothing to rebet yet.", 2f); return; }
        long placed = book.Replace(lastSet, Cash, CasinoLayout.RouletteChipCents);
        long want = RouletteRules.TotalStake(lastSet, lastSet.Length);
        if (placed < want) Say("Rebet: " + Dollars(placed) + " of " + Dollars(want) + " (that's what you can cover).", HintTone.Normal, 3f);
        else Say("Rebet: " + Dollars(placed) + ".", HintTone.Normal, 2f);
        Sfx.Play("click", null, 0.4f, 1.3f);
        Changed();
    }

    void ToggleReady()
    {
        if (!hasSnap || snap.phase != RouletteTablePhase.Betting) { if (hasSnap && snap.phase == RouletteTablePhase.Idle) Refuse("Place a bet first.", 2f); return; }
        if (Dirty) SendSet(Time.realtimeSinceStartupAsDouble); // the set first (changing a set takes Ready back, so the order matters)
        bool want = !ready;
        ready = want;
        ApplyPlates();
        CasinoClient.RouletteReady(want, r =>
        {
            if (IsEnded) return;
            if (!r.Ok) { ready = !want; ApplyPlates(); Refuse(r.gate == CasinoError.Ok ? CasinoTables.Describe(r) : CasinoBank.Describe(r.gate), 2.5f); }
            RefreshStrip();
        });
        Sfx.Play("click", null, 0.4f, want ? 1.4f : 1.0f);
    }

    void Changed()
    {
        ApplyPlates();
        RefreshStrip();
    }

    // ------------------------------------------------------------------ the hint strip

    void RefreshStrip()
    {
        if (strip == null || IsEnded) return;
        bool pad = CasinoInput.UsingPad;
        string K(string kb, string pd, string label) => CasinoHintStrip.Key(pad ? pd : kb, label);
        string look = "  " + K("Shift", "LT", "lean") + "  " + K("V", "D-pad up", "wheel") + "  " + K("Esc", "B", "leave");
        if (!hasSnap) { strip.SetHint(K("Esc", "B", "leave")); strip.SetInfo("Joining the wheel..."); return; }
        double left = SecondsLeft;
        string cd = CasinoSeatText.Countdown(left);
        string hint, info;
        switch (snap.phase)
        {
            case RouletteTablePhase.Idle:
            case RouletteTablePhase.Betting:
                if (!BettingOpen) { hint = look.TrimStart(); info = "Betting is closed."; break; }
                hint = K("1-4/Q/E", "LB/RB", "chip") + "  " + K("LMB", "A", "bet") + "  " + K("RMB", "X", "take off") + "  " + K("Z", "D-pad down", "undo") + "  " + K("C", "C", "clear") + "  " + K("R", "L3", "rebet") + "  " + K("Space", "Y", ready ? "not ready" : "ready") + look;
                info = book.IsEmpty
                    ? "Place your bets. " + Dollars(CasinoRules.RouletteMinSpotCents) + " a spot, " + Dollars(CasinoRules.RouletteMaxInsideCents) + " inside, " + Dollars(CasinoRules.RouletteMaxOutsideCents) + " outside"
                    : "On the table " + Dollars(book.Total) + " (" + book.SpotCount + (book.SpotCount == 1 ? " spot" : " spots") + ")" + (ready ? "   Ready: waiting for the others" : "");
                if (snap.phase == RouletteTablePhase.Betting && cd.Length > 0) info += "   Spin in " + cd;
                break;
            case RouletteTablePhase.Locked:
            case RouletteTablePhase.Spinning:
                hint = K("V", "D-pad up", "watch the wheel") + "  " + K("Shift", "LT", "lean") + "  " + K("Esc", "B", "leave");
                info = "NO MORE BETS" + (Me(out var me) && me.TotalCents > 0 ? "   " + Dollars(me.TotalCents) + " on the table" : "");
                break;
            default:
                hint = look.TrimStart();
                info = "Next spin soon. " + cd;
                break;
        }
        strip.SetHint(hint);
        strip.SetInfo(info);
    }
}
