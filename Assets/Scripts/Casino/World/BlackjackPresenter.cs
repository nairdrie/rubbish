using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// The blackjack table (<c>bj0</c>) as every player in the room sees it (Docs/Casino.md §11), drawn ONLY from the replicated
/// <see cref="BlackjackTableSnapshot"/>: name tags, stools slid out for occupants, bet chips, the cards on the felt (new cards are found
/// by diffing successive snapshots and slide from the shoe; a late join snaps), hand totals with BUST / BLACKJACK / 21, the active
/// seat's ring and the countdown lamp, the insurance chip, results floating over each hand, chips swept to and paid from the dealer's tray,
/// the dealer's gestures, the cut-card gauge and a 'Shuffling' beat. The local controllers draw nothing here.
/// </summary>
public sealed class BlackjackPresenter : CasinoStationPresenter
{
    // ---- timings (seconds)
    const float DealSlide = 0.30f, ReflowSlide = 0.22f, SweepSeconds = 0.5f, PaySeconds = 0.5f, ResultLabelLife = 4f;
    const double StepLabels = 0.2, StepSweep = 0.9, StepPay = 1.1, StepCollect = 3.6;

    sealed class CardView
    {
        public CardProp prop;
        public byte value = 255;
        public Vector3 pos;
        public Quaternion rot;
    }

    sealed class HandView
    {
        public readonly List<CardView> cards = new List<CardView>(8);
        public CasinoLabel total, result;
    }

    sealed class SeatView
    {
        public CasinoLabel tag, betLabel;
        public ChipStackView bet, extra, insurance, payout;
        public readonly HandView[] hands = new HandView[CasinoLayout.HandsPerSeat];
        public bool stoolAway, hasStoolState;
        public long lastBet, lastExtra, lastIns;
        public int lastKey = -2;
    }

    readonly CasinoStation st;
    readonly SeatView[] seats = new SeatView[CasinoLayout.BlackjackSeats];
    readonly List<CardView> dealer = new List<CardView>(8);
    readonly List<DealtCard> diff = new List<DealtCard>(16);
    CasinoLabel dealerTotal, banner;
    GameObject ring, gaugeRoot, gaugeMark;
    Transform ringT, markT;
    Material ringMat, markMat, markHot;
    bool cutHot;

    // state for the per-frame bits
    BlackjackTablePhase phase;
    double phaseDeadline = double.PositiveInfinity;
    double lampLen = 15;
    int activeSeat = -1, lastLampBulbs = -1;
    double lastGesture = -10;
    int resultsScheduledRound = -1;
    int epoch;
    int shoeLeftPrev = -1;

    public BlackjackPresenter(CasinoStation station) { st = station; }

    public CasinoStation Station => st;

    // ------------------------------------------------------------------ build

    public override void Build(CasinoPresenters host)
    {
        this.host = host;
        for (int i = 0; i < seats.Length; i++)
        {
            var sv = new SeatView();
            sv.bet = new ChipStackView(host.Pool);
            sv.extra = new ChipStackView(host.Pool);
            sv.insurance = new ChipStackView(host.Pool);
            sv.payout = new ChipStackView(host.Pool);
            seats[i] = sv;
            for (int h = 0; h < sv.hands.Length; h++) sv.hands[h] = new HandView();
        }
        if (st.spots == null || st.spots.Length < seats.Length) host.WarnOnce("bj.spots", "the blackjack station has fewer than 5 spots");
        if (st.dealerSpots == null || st.dealerSpots.Length == 0) host.WarnOnce("bj.dealer", "the blackjack station has no dealer card spots");
        BuildRing();
        BuildGauge();
        if (st.timerLamp != null) st.timerLamp.SetValue(0f);
        else host.WarnOnce("bj.lamp", "the blackjack station has no timer lamp");
    }

    void BuildRing()
    {
        ring = new GameObject("ActiveRing") { hideFlags = HideFlags.DontSave };
        ring.transform.SetParent(host.PropRoot, false);
        ringT = ring.transform;
        var mf = ring.AddComponent<MeshFilter>();
        mf.sharedMesh = CasinoPropKit.Ring("CasinoActiveRing", 0.078f, 0.098f, 0.003f, 40);
        var mr = ring.AddComponent<MeshRenderer>();
        ringMat = CasinoPropKit.Glow(new Color(0.95f, 0.62f, 0.12f), 1.1f);
        mr.sharedMaterial = ringMat;
        mr.shadowCastingMode = ShadowCastingMode.Off; mr.receiveShadows = false;
        ring.SetActive(false);
    }

    void BuildGauge()
    {
        if (st.shoe == null) { host.WarnOnce("bj.shoe", "the blackjack station has no shoe transform"); return; }
        gaugeRoot = new GameObject("CutGauge") { hideFlags = HideFlags.DontSave };
        gaugeRoot.transform.SetParent(host.PropRoot, false);
        var track = new GameObject("Track") { hideFlags = HideFlags.DontSave };
        track.transform.SetParent(gaugeRoot.transform, false);
        track.AddComponent<MeshFilter>().sharedMesh = BoxMesh();
        var tr = track.AddComponent<MeshRenderer>();
        tr.sharedMaterial = CasinoPropKit.Solid(new Color(0.05f, 0.05f, 0.06f), 0.2f);
        tr.shadowCastingMode = ShadowCastingMode.Off; tr.receiveShadows = false;
        track.transform.localScale = new Vector3(0.012f, 0.004f, 0.2f);
        markT = new GameObject("Cut card") { hideFlags = HideFlags.DontSave }.transform;
        markT.SetParent(gaugeRoot.transform, false);
        markT.gameObject.AddComponent<MeshFilter>().sharedMesh = BoxMesh();
        var mr = markT.gameObject.AddComponent<MeshRenderer>();
        markMat = CasinoPropKit.Glow(new Color(1f, 0.82f, 0.2f), 1.1f);
        markHot = CasinoPropKit.Glow(new Color(1f, 0.15f, 0.1f), 2.5f);
        mr.sharedMaterial = markMat;
        mr.shadowCastingMode = ShadowCastingMode.Off; mr.receiveShadows = false;
        markT.localScale = new Vector3(0.04f, 0.02f, 0.006f);
        markMesh = mr;
        PlaceGauge(1f);
    }

    MeshRenderer markMesh;
    static Mesh unitBox;

    static Mesh BoxMesh()
    {
        if (unitBox != null) return unitBox;
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        unitBox = go.GetComponent<MeshFilter>().sharedMesh;
        Object.Destroy(go);
        return unitBox;
    }

    void PlaceGauge(float fractionLeft)
    {
        if (gaugeRoot == null || st.shoe == null) return;
        Transform sh = st.shoe;
        gaugeRoot.transform.SetPositionAndRotation(sh.position + sh.up * 0.078f - sh.forward * 0.19f, sh.rotation);
        // the card sits further along the shoe the more has been dealt (fraction left 1 = at the back)
        float z = Mathf.Lerp(0.09f, -0.09f, Mathf.Clamp01(fractionLeft));
        markT.localPosition = new Vector3(0f, 0.012f, z);
        markT.localRotation = Quaternion.identity;
    }

    // ------------------------------------------------------------------ geometry

    Pose ShoePose()
    {
        if (st.shoe == null) return new Pose(st.transform.position + Vector3.up * 1f, Quaternion.identity);
        return new Pose(st.shoe.position + st.shoe.up * 0.08f, st.shoe.rotation);
    }

    CasinoHandSpot HandSpot(CasinoSpot spot, int hand)
    {
        if (spot == null || spot.handSpots == null || spot.handSpots.Length == 0) return null;
        return spot.handSpots[Mathf.Clamp(hand, 0, spot.handSpots.Length - 1)];
    }

    static bool GetPose(Transform[] spots, int i, out Vector3 pos, out Quaternion rot)
    {
        pos = Vector3.zero; rot = Quaternion.identity;
        if (spots == null || spots.Length == 0) return false;
        if (i < spots.Length && spots[i] != null) { pos = spots[i].position; rot = spots[i].rotation; return true; }
        // beyond the spots: keep laying cards along the row, a little up
        var last = spots[spots.Length - 1];
        Vector3 step = spots.Length >= 2 && spots[spots.Length - 2] != null ? last.position - spots[spots.Length - 2].position : last.right * 0.05f;
        if (last == null) return false;
        pos = last.position + step * (i - (spots.Length - 1)) + Vector3.up * 0.0018f * (i - (spots.Length - 1));
        rot = last.rotation;
        return true;
    }

    bool PlayerCardPose(int seat, int hand, int index, int handCount, bool doubledThird, out Vector3 pos, out Quaternion rot)
    {
        pos = Vector3.zero; rot = Quaternion.identity;
        var spot = st.Spot(seat);
        var hs = HandSpot(spot, hand);
        if (hs == null || hs.cards == null || hs.cards.Length == 0) return false;
        if (!GetPose(hs.cards, index, out pos, out rot)) return false;
        if (handCount >= 2 && spot.betCircle != null)
        {
            // split hands sit side by side across the seat's felt
            pos += spot.betCircle.right * ((hand - (handCount - 1) * 0.5f) * 0.17f);
            if (handCount >= 3) pos -= (rot * Vector3.right) * 0.012f * index; // a tighter fan
        }
        if (doubledThird)
        {
            // the doubled hand's last card is laid across
            rot = rot * Quaternion.Euler(0f, 90f, 0f);
            pos += (rot * Vector3.forward) * 0.02f - (rot * Vector3.right) * 0.012f + Vector3.up * 0.002f;
        }
        return true;
    }

    bool DealerCardPose(int index, out Vector3 pos, out Quaternion rot) => GetPose(st.dealerSpots, index, out pos, out rot);

    /// <summary>Where a hand's total floats: just above the middle of its fan of cards (split hands are spread side by side).</summary>
    Vector3 HandLabelPoint(CasinoSpot spot, int hand, int handCount, float lift)
    {
        var hs = HandSpot(spot, hand);
        Vector3 at;
        if (hs != null && hs.root != null) at = hs.root.position;
        else if (hs != null && hs.floatText != null) at = hs.floatText.position - Vector3.up * 0.2f;
        else at = spot.betCircle != null ? spot.betCircle.position : st.transform.position;
        if (handCount >= 2 && spot.betCircle != null) at += spot.betCircle.right * ((hand - (handCount - 1) * 0.5f) * 0.17f);
        return at + Vector3.up * lift;
    }

    // ------------------------------------------------------------------ apply a snapshot

    public void Apply(BlackjackTableSnapshot snap, BlackjackTableSnapshot prev, bool hasPrev, bool animate)
    {
        double now = CasinoPresenters.Now;
        diff.Clear();
        BlackjackDiff.NewCards(prev, hasPrev, snap, diff);
        if (diff.Count > 8) animate = false; // a big jump (rejoining mid-hand, a lost snapshot): snap to the final layout
        bool reveal = BlackjackDiff.HoleRevealed(prev, hasPrev, snap);
        bool newRound = !hasPrev || prev.round != snap.round;
        if (newRound) epoch++;
        phase = snap.phase;
        phaseDeadline = snap.phaseDeadline;
        activeSeat = snap.activeSeat;

        ApplyDealer(snap, animate, reveal, now);
        for (int s = 0; s < seats.Length; s++) ApplySeat(s, snap, animate, now);
        UpdateLampLength(snap);
        UpdateMarker(snap);
        UpdateGauge(snap, prev, hasPrev);
        BeatsAndGestures(snap, prev, hasPrev, animate, now);
        if (snap.phase == BlackjackTablePhase.Results) ScheduleResults(snap, now);
        else resultsScheduledRound = -1;
        if (diff.Count > 0 && animate && now - lastGesture > 0.6 && snap.phase == BlackjackTablePhase.Deal)
        {
            lastGesture = now;
            CasinoStaff.Dealer(CasinoStationKind.Blackjack).Play(DealerGesture.Deal);
        }
    }

    bool IsNew(int seat, int hand, int index)
    {
        for (int i = 0; i < diff.Count; i++)
            if (diff[i].seat == seat && diff[i].hand == hand && diff[i].index == index) return true;
        return false;
    }

    int FromHandOf(int seat, int hand, int index)
    {
        for (int i = 0; i < diff.Count; i++)
            if (diff[i].seat == seat && diff[i].hand == hand && diff[i].index == index) return diff[i].fromHand;
        return -1;
    }

    // ---- the dealer

    void ApplyDealer(BlackjackTableSnapshot snap, bool animate, bool reveal, double now)
    {
        int n = snap.dealer == null ? 0 : snap.dealer.Length;
        ReconcileCards(dealer, n, snap.dealer, -1, 0, animate, now, 1, false);
        // total
        if (n == 0) { HideLabel(ref dealerTotal); return; }
        int shown = snap.HoleHidden ? 1 : n;
        string text = BlackjackDiff.HandText(snap.dealer, shown, false, out var tone);
        if (snap.HoleHidden) text += " + ?";
        Vector3 at = DealerLabelPoint();
        ShowLabel(ref dealerTotal, text, at, ToneColour(tone, false), 0.45f, now);
    }

    Vector3 DealerLabelPoint()
    {
        Vector3 sum = Vector3.zero; int c = 0;
        if (st.dealerSpots != null)
            for (int i = 0; i < st.dealerSpots.Length && i < 4; i++) if (st.dealerSpots[i] != null) { sum += st.dealerSpots[i].position; c++; }
        if (c == 0) return st.dealerFloatText != null ? st.dealerFloatText.position : st.transform.position + Vector3.up * 1.5f;
        Vector3 mid = sum / c;
        // on the player's side of the dealer's cards, just above the felt (not over the chip tray)
        var middle = st.Spot(2);
        Vector3 toPlayers = middle != null && middle.stand != null ? middle.stand.position - mid : Vector3.back;
        toPlayers.y = 0f;
        if (toPlayers.sqrMagnitude < 1e-4f) toPlayers = Vector3.back;
        return mid + toPlayers.normalized * 0.17f + Vector3.up * 0.075f;
    }

    // ---- a seat

    void ApplySeat(int s, BlackjackTableSnapshot snap, bool animate, double now)
    {
        var spot = st.Spot(s);
        var sv = seats[s];
        if (spot == null || snap.seats == null || s >= snap.seats.Length) return;
        var seat = snap.seats[s];

        // who sits here: the name tag and the stool
        int key = seat.key;
        bool occupied = key >= 0;
        string tag = occupied ? CasinoClient.NameOf(key) : seat.ownerKey >= 0 && seat.Has(BlackjackSeatSnapshot.FlagInRound) ? CasinoClient.NameOf(seat.ownerKey) + " (away)" : "";
        if (sv.lastKey != key || (sv.tag != null && sv.tag.Text != tag))
        {
            sv.lastKey = key;
            if (string.IsNullOrEmpty(tag)) HideLabel(ref sv.tag);
            else if (spot.floatText != null) ShowLabel(ref sv.tag, tag, spot.floatText.position, new Color(1f, 0.93f, 0.7f), 1.15f, now, true);
        }
        if (!sv.hasStoolState || sv.stoolAway != occupied)
        {
            sv.hasStoolState = true; sv.stoolAway = occupied;
            spot.SetStoolAway(occupied, !animate);
        }

        // the hands
        int handCount = seat.HandCount;
        long handsTotal = 0;
        for (int h = 0; h < sv.hands.Length; h++)
        {
            var hv = sv.hands[h];
            bool has = h < handCount;
            int n = has ? seat.hands[h].cards.Length : 0;
            byte[] cards = has ? seat.hands[h].cards : null;
            bool doubled = has && seat.hands[h].Has(BlackjackHandSnapshot.FlagDoubled);
            if (has) handsTotal += seat.hands[h].stakeCents;
            ReconcileCards(hv.cards, n, cards, s, h, animate, now, handCount, doubled);
            // total label
            if (n == 0) { HideLabel(ref hv.total); HideLabel(ref hv.result); continue; }
            if (HandSpot(spot, h) == null) continue;
            bool active = phase == BlackjackTablePhase.Turns && snap.activeSeat == s && seat.hands[h].Has(BlackjackHandSnapshot.FlagActive);
            string text = BlackjackDiff.HandText(cards, n, seat.hands[h].Has(BlackjackHandSnapshot.FlagBlackjack), out var tone);
            if (doubled && tone == HandTone.Plain) text += " x2";
            ShowLabel(ref hv.total, text, HandLabelPoint(spot, h, handCount, 0.1f), ToneColour(tone, active), active ? 0.5f : 0.4f, now);
        }
        for (int h = handCount; h < sv.hands.Length; h++) { HideLabel(ref sv.hands[h].total); }

        // the chips
        if (phase != BlackjackTablePhase.Results)
        {
            long bet = seat.betCents;
            long extra = Mathf.Max(0, (int)(handsTotal - bet));
            var circle = spot.betCircle;
            if (circle != null)
            {
                Vector3 at = circle.position + Vector3.up * 0.003f;
                if (sv.bet.Cents != bet || sv.bet.Origin != at)
                {
                    sv.bet.SetOrigin(at, circle.rotation);
                    sv.bet.Set(bet, -1, now, animate);
                }
                if (sv.extra.Cents != extra)
                {
                    sv.extra.SetOrigin(at + circle.right * 0.075f, circle.rotation);
                    sv.extra.Set(extra, -1, now, animate);
                }
                string betText = bet > 0 ? CasinoRules.Dollars(bet + extra) : "";
                if (betText.Length == 0) HideLabel(ref sv.betLabel);
                else ShowLabel(ref sv.betLabel, betText, sv.bet.TopPoint + Vector3.up * 0.035f, new Color(0.98f, 0.9f, 0.55f), 0.3f, now);
            }
            long ins = seat.insuranceCents;
            if (spot.insuranceSpot != null && sv.insurance.Cents != ins)
            {
                sv.insurance.SetOrigin(spot.insuranceSpot.position + Vector3.up * 0.003f, spot.insuranceSpot.rotation);
                sv.insurance.Set(ins, -1, now, animate);
            }
            if (sv.payout.Cents != 0) sv.payout.Clear();
        }
    }

    // ---- card views

    /// <summary>
    /// Make the views of one row of cards match the snapshot's cards: new ones (found by the snapshot diff) slide in from the shoe when
    /// <paramref name="animate"/>, the rest are placed; extras are swept to the discard. <paramref name="seat"/> -1 = the dealer.
    /// </summary>
    void ReconcileCards(List<CardView> views, int n, byte[] cardsIn, int seat, int hand, bool animate, double now, int handCount, bool doubled)
    {
        // remove what is no longer there
        for (int i = views.Count - 1; i >= n; i--)
        {
            var v = views[i];
            views.RemoveAt(i);
            if (v.prop == null) continue;
            if (animate && st.discard != null)
                v.prop.MoveTo(st.discard.position + Vector3.up * 0.03f, st.discard.rotation * Quaternion.Euler(0f, 0f, 180f), now, 0.45f, 0.05f, 1f, false, p => host.Pool.Release(p));
            else host.Pool.Release(v.prop);
        }
        int newIndex = 0;
        for (int i = 0; i < n; i++)
        {
            byte value = cardsIn[i];
            bool down = value == BlackjackTable.Hidden;
            bool isNew = i >= views.Count;
            bool third = doubled && n == 3 && i == 2;
            Vector3 pos; Quaternion rot;
            bool ok = seat < 0 ? DealerCardPose(i, out pos, out rot) : PlayerCardPose(seat, hand, i, handCount, third, out pos, out rot);
            if (!ok) continue; // a missing spot: nothing to draw on (logged at build)
            if (isNew)
            {
                var cv = new CardView { prop = host.Pool.Card(), value = value, pos = pos, rot = rot };
                cv.prop.Show(value, true);
                views.Add(cv);
                bool fresh = isNew && (seat < 0 ? IsNewDealer(i) : IsNew(seat, hand, i));
                if (animate && fresh)
                {
                    int from = seat < 0 ? -1 : FromHandOf(seat, hand, i);
                    Pose start = ShoePose();
                    if (from >= 0 && PlayerCardPose(seat, from, 1, handCount, false, out var fp, out var fr)) start = new Pose(fp, fr);
                    float delay = newIndex++ * 0.14f;
                    cv.prop.MoveFromTo(start.position, start.rotation * Quaternion.Euler(0f, 0f, 180f), pos, rot, now + delay, DealSlide, 0.07f, 1f);
                    if (!down) cv.prop.FlipUp(value, now + delay + 0.05, 0.28f);
                    Sfx.Play("click", pos, 0.35f, 1.2f);
                }
                else
                {
                    cv.prop.Snap(pos, rot);
                    cv.prop.Show(value, down);
                }
                continue;
            }
            var view = views[i];
            if (view.value != value)
            {
                bool wasHidden = view.value == BlackjackTable.Hidden;
                view.value = value;
                if (wasHidden && !down && animate && view.prop != null)
                {
                    view.prop.FlipUp(value, now, 0.45f);   // the dealer turns the hole card over
                    Sfx.Play("click", pos, 0.45f, 0.9f);
                }
                else if (view.prop != null) view.prop.Show(value, down);
            }
            if (view.prop != null && (view.pos != pos || view.rot != rot))
            {
                view.pos = pos; view.rot = rot;
                if (animate) view.prop.MoveTo(pos, rot, now, ReflowSlide, 0.02f);
                else view.prop.Snap(pos, rot);
            }
        }
    }

    bool IsNewDealer(int index)
    {
        for (int i = 0; i < diff.Count; i++) if (diff[i].seat == -1 && diff[i].index == index) return true;
        return false;
    }

    // ------------------------------------------------------------------ the felt's other signs

    static Color ToneColour(HandTone tone, bool active)
    {
        switch (tone)
        {
            case HandTone.Bust: return CasinoPaint.Lose;
            case HandTone.Blackjack: return new Color(1f, 0.85f, 0.3f);
            case HandTone.Twenty1: return new Color(1f, 0.9f, 0.45f);
            default: return active ? new Color(1f, 0.88f, 0.4f) : new Color(0.96f, 0.95f, 0.88f);
        }
    }

    void ShowLabel(ref CasinoLabel l, string text, Vector3 at, Color c, float size, double now, bool yawOnly = false)
    {
        if (l != null && l.Shown)
        {
            l.SetText(text); l.SetColour(c); l.SetPosition(at);
            if (!Mathf.Approximately(l.tmp.fontSize, size)) l.tmp.fontSize = size;
            return;
        }
        l = host.Labels.Show(text, at, c, size, now, 0f, 0f, 0.6f, yawOnly);
    }

    void HideLabel(ref CasinoLabel l)
    {
        if (l == null) return;
        host.Labels.Hide(l, CasinoPresenters.Now);
        l = null;
    }

    void UpdateLampLength(BlackjackTableSnapshot snap)
    {
        switch (snap.phase)
        {
            case BlackjackTablePhase.Betting: lampLen = BlackjackTable.BettingSeconds; break;
            case BlackjackTablePhase.Insurance: lampLen = BlackjackTable.InsuranceSeconds; break;
            case BlackjackTablePhase.Turns:
            {
                int inRound = 0;
                if (snap.seats != null) for (int i = 0; i < snap.seats.Length; i++) if (snap.seats[i].Has(BlackjackSeatSnapshot.FlagInRound) && !snap.seats[i].Has(BlackjackSeatSnapshot.FlagAbsent)) inRound++;
                lampLen = inRound >= 2 ? BlackjackTable.TurnSeconds : BlackjackTable.SoloTurnSeconds;
                break;
            }
            default: lampLen = 1; break;
        }
        double left = snap.phaseDeadline - CasinoClient.TableTime();
        if (!double.IsInfinity(left) && left > lampLen) lampLen = left;
        lastLampBulbs = -1;
    }

    void UpdateMarker(BlackjackTableSnapshot snap)
    {
        if (ring == null) return;
        bool show = snap.phase == BlackjackTablePhase.Turns && snap.activeSeat >= 0 && snap.activeSeat < CasinoLayout.BlackjackSeats;
        var spot = show ? st.Spot(snap.activeSeat) : null;
        if (spot == null || spot.betCircle == null) { ring.SetActive(false); return; }
        ringT.SetPositionAndRotation(spot.betCircle.position + Vector3.up * 0.004f, spot.betCircle.rotation);
        ring.SetActive(true);
    }

    void UpdateGauge(BlackjackTableSnapshot snap, BlackjackTableSnapshot prev, bool hasPrev)
    {
        if (gaugeRoot == null) return;
        PlaceGauge(snap.ShoeFraction);
        bool hot = snap.CutReached;
        if (hot != cutHot) { cutHot = hot; markMesh.sharedMaterial = hot ? markHot : markMat; }
    }

    void BeatsAndGestures(BlackjackTableSnapshot snap, BlackjackTableSnapshot prev, bool hasPrev, bool animate, double now)
    {
        if (animate && hasPrev && shoeLeftPrev >= 0 && snap.shoeLeft > shoeLeftPrev + 30 && st.dealerFloatText != null)
        {
            // the shoe was refilled and shuffled before this deal
            host.Labels.Show("SHUFFLING...", st.dealerFloatText.position, new Color(0.9f, 0.9f, 1f), 1.1f, now, 2.6f, 0.05f);
            Sfx.Play("shuffle", st.dealerFloatText.position, 0.5f);
        }
        shoeLeftPrev = snap.shoeLeft;
        if (animate && hasPrev && snap.phase == BlackjackTablePhase.Insurance && prev.phase != BlackjackTablePhase.Insurance && st.dealerFloatText != null)
        {
            host.Labels.Show("INSURANCE?", st.dealerFloatText.position, new Color(1f, 0.85f, 0.4f), 1.0f, now, 3f, 0.05f);
            CasinoStaff.Dealer(CasinoStationKind.Blackjack).Say("Insurance?");
        }
    }

    // ------------------------------------------------------------------ the results: floating nets, chips swept and paid

    void ScheduleResults(BlackjackTableSnapshot snap, double now)
    {
        if (resultsScheduledRound == snap.round) return;
        resultsScheduledRound = snap.round;
        int myEpoch = epoch;
        double startLocal = CasinoClient.ToLocalTime(snap.phaseDeadline - BlackjackTable.ResultsSeconds);
        // the facts the steps need, captured now (the snapshot struct is a value, its arrays are never mutated by the table)
        var seatsCopy = snap.seats;
        // make sure the bets are on the felt first (a late join sees them for the part of the Results that still has them)
        double t = CasinoClient.TableTime() - (snap.phaseDeadline - BlackjackTable.ResultsSeconds);
        for (int s = 0; s < seats.Length && s < seatsCopy.Length; s++)
        {
            var spot = st.Spot(s);
            if (spot == null || spot.betCircle == null) continue;
            var sv = seats[s];
            long bet = seatsCopy[s].betCents;
            long handsTotal = 0;
            if (seatsCopy[s].hands != null) for (int h = 0; h < seatsCopy[s].hands.Length; h++) handsTotal += seatsCopy[s].hands[h].stakeCents;
            long extra = Mathf.Max(0, (int)(handsTotal - bet));
            Vector3 at = spot.betCircle.position + Vector3.up * 0.003f;
            if (sv.bet.Cents != bet) { sv.bet.SetOrigin(at, spot.betCircle.rotation); sv.bet.Set(bet, -1, now, false); }
            if (sv.extra.Cents != extra) { sv.extra.SetOrigin(at + spot.betCircle.right * 0.075f, spot.betCircle.rotation); sv.extra.Set(extra, -1, now, false); }
        }
        host.At(startLocal + StepLabels, late => { if (myEpoch == epoch) StepLabelsNow(seatsCopy, late); });
        host.At(startLocal + StepSweep, late => { if (myEpoch == epoch) StepSweepNow(seatsCopy, late); });
        host.At(startLocal + StepPay, late => { if (myEpoch == epoch) StepPayNow(seatsCopy, late); });
        host.At(startLocal + StepCollect, late => { if (myEpoch == epoch) StepCollectNow(seatsCopy, late); });
    }

    static long NetOf(BlackjackSeatSnapshot seat)
    {
        if (!seat.Has(BlackjackSeatSnapshot.FlagSettled)) return 0;
        return seat.totalNetCents;
    }

    void StepLabelsNow(BlackjackSeatSnapshot[] snapSeats, bool late)
    {
        double now = CasinoPresenters.Now;
        if (late) return;
        for (int s = 0; s < seats.Length && s < snapSeats.Length; s++)
        {
            var seat = snapSeats[s];
            var spot = st.Spot(s);
            if (spot == null || !seat.Has(BlackjackSeatSnapshot.FlagSettled) || seat.hands == null) continue;
            for (int h = 0; h < seat.hands.Length; h++)
            {
                if (HandSpot(spot, h) == null) continue;
                var hand = seat.hands[h];
                string text; Color c;
                switch (hand.outcome)
                {
                    case BlackjackOutcome.Blackjack: text = "BLACKJACK " + CasinoChipMath.Signed(hand.netCents); c = new Color(1f, 0.85f, 0.3f); break;
                    case BlackjackOutcome.Win: text = CasinoChipMath.Signed(hand.netCents); c = CasinoPaint.Win; break;
                    case BlackjackOutcome.Push: text = "PUSH"; c = Color.white; break;
                    case BlackjackOutcome.Bust: text = "BUST " + CasinoChipMath.Signed(hand.netCents); c = CasinoPaint.Lose; break;
                    default: text = CasinoChipMath.Signed(hand.netCents); c = CasinoPaint.Lose; break;
                }
                host.Labels.Show(text, HandLabelPoint(spot, h, seat.HandCount, 0.14f), c, 0.5f, now, ResultLabelLife, 0.14f);
            }
            if (seat.insuranceNetCents != 0 && spot.insuranceSpot != null)
                host.Labels.Show("INSURANCE " + CasinoChipMath.Signed(seat.insuranceNetCents), spot.insuranceSpot.position + Vector3.up * 0.09f,
                    seat.insuranceNetCents > 0 ? CasinoPaint.Win : CasinoPaint.Lose, 0.4f, now, 3.2f, 0.1f);
        }
    }

    void StepSweepNow(BlackjackSeatSnapshot[] snapSeats, bool late)
    {
        double now = CasinoPresenters.Now;
        bool any = false;
        Vector3 tray = st.chipTray != null ? st.chipTray.position : st.transform.position;
        for (int s = 0; s < seats.Length && s < snapSeats.Length; s++)
        {
            var seat = snapSeats[s];
            if (!seat.Has(BlackjackSeatSnapshot.FlagSettled) || seat.totalNetCents >= 0) continue;
            var sv = seats[s];
            if (sv.bet.Cents == 0 && sv.extra.Cents == 0) continue;
            any = true;
            // a lost hand: its chips go to the dealer's tray (a push or a win keeps them down for now)
            if (late) { sv.bet.Clear(); sv.extra.Clear(); sv.insurance.Clear(); }
            else { sv.bet.SlideAway(tray, now, SweepSeconds); sv.extra.SlideAway(tray, now, SweepSeconds, 0.03f, 0.1f); sv.insurance.SlideAway(tray, now, SweepSeconds, 0.03f, 0.15f); }
            HideLabel(ref sv.betLabel);
        }
        if (any && !late) { CasinoStaff.Dealer(CasinoStationKind.Blackjack).Play(DealerGesture.Sweep); Sfx.Play("click", tray, 0.4f, 0.8f); }
    }

    void StepPayNow(BlackjackSeatSnapshot[] snapSeats, bool late)
    {
        double now = CasinoPresenters.Now;
        bool any = false;
        Vector3 tray = st.chipTray != null ? st.chipTray.position : st.transform.position;
        for (int s = 0; s < seats.Length && s < snapSeats.Length; s++)
        {
            var seat = snapSeats[s];
            var spot = st.Spot(s);
            if (spot == null || spot.betCircle == null || !seat.Has(BlackjackSeatSnapshot.FlagSettled) || seat.totalNetCents <= 0) continue;
            any = true;
            var sv = seats[s];
            Vector3 at = spot.betCircle.position + Vector3.up * 0.003f - spot.betCircle.right * 0.075f;
            sv.payout.SetOrigin(at, spot.betCircle.rotation);
            // the winnings are counted out from the tray and slide across to the winner's circle
            sv.payout.Set(seat.totalNetCents, -1, now, !late, 0f, tray);
        }
        if (any && !late) CasinoStaff.Dealer(CasinoStationKind.Blackjack).Play(DealerGesture.Pay);
    }

    void StepCollectNow(BlackjackSeatSnapshot[] snapSeats, bool late)
    {
        double now = CasinoPresenters.Now;
        for (int s = 0; s < seats.Length; s++)
        {
            var sv = seats[s];
            var spot = st.Spot(s);
            if (spot == null || spot.betCircle == null) continue;
            Vector3 toPlayer = spot.betCircle.position - spot.betCircle.forward * 0.32f;
            if (late) { sv.bet.Clear(); sv.extra.Clear(); sv.insurance.Clear(); sv.payout.Clear(); }
            else
            {
                sv.bet.SlideAway(toPlayer, now, SweepSeconds);
                sv.extra.SlideAway(toPlayer, now, SweepSeconds, 0.03f, 0.05f);
                sv.insurance.SlideAway(toPlayer, now, SweepSeconds, 0.03f, 0.05f);
                sv.payout.SlideAway(toPlayer, now, SweepSeconds, 0.03f, 0.1f);
            }
            HideLabel(ref sv.betLabel);
        }
    }

    // ------------------------------------------------------------------ per frame

    public override void Tick(double now)
    {
        // the countdown lamp
        if (st.timerLamp != null)
        {
            float v = 0f;
            if ((phase == BlackjackTablePhase.Betting || phase == BlackjackTablePhase.Insurance || phase == BlackjackTablePhase.Turns) && !double.IsInfinity(phaseDeadline))
            {
                double left = phaseDeadline - CasinoClient.TableTime();
                v = (float)System.Math.Max(0.0, System.Math.Min(1.0, left / lampLen));
            }
            int bulbs = Mathf.CeilToInt(v * 36f);
            if (bulbs != lastLampBulbs) { lastLampBulbs = bulbs; st.timerLamp.SetValue(bulbs / 36f); }
        }
        // the active seat's ring breathes
        if (ring != null && ring.activeSelf)
        {
            float k = 1f + 0.05f * Mathf.Sin((float)now * 5f);
            ringT.localScale = new Vector3(k, 1f, k);
        }
        if (cutHot && markT != null)
        {
            // the red cut card throbs slowly (well under 2.5 Hz)
            float p = GameSettings.ReduceFlashing ? 1f : 1f + 0.12f * Mathf.Sin((float)now * 6f);
            markT.localScale = new Vector3(0.04f, 0.02f, 0.006f) * p;
        }
    }

    public override void Teardown()
    {
        if (ring != null) Object.Destroy(ring);
        if (gaugeRoot != null) Object.Destroy(gaugeRoot);
        ring = null; gaugeRoot = null;
        if (st != null && st.timerLamp != null) { try { st.timerLamp.SetValue(0f); } catch { } }
        for (int i = 0; i < seats.Length; i++) if (seats[i] != null && st != null && st.Spot(i) != null) st.Spot(i).SetStoolAway(false, true);
    }
}
