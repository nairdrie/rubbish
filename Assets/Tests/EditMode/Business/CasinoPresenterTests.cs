using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

/// <summary>
/// The pure parts of the 3D casino presenters (Docs/Casino.md §11): chip stacks, which cards are new between two snapshots, how a hand's total
/// is printed, the roulette ball's path (it lands in the true pocket exactly at the end of the spin), the slot drum's travel and face
/// mapping, and the words on the pay plate. Reflection because the code lives in Assembly-CSharp.
/// </summary>
public sealed class CasinoPresenterTests
{
    const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;

    static Type T(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object Call(Type t, string method, params object[] args)
    {
        foreach (var m in t.GetMethods(Static))
        {
            if (m.Name != method || m.GetParameters().Length != args.Length) continue;
            return m.Invoke(null, args);
        }
        Assert.Fail(t.Name + "." + method + "/" + args.Length + " not found");
        return null;
    }

    static object Set(object boxed, string field, object value)
    {
        var f = boxed.GetType().GetField(field);
        Assert.IsNotNull(f, boxed.GetType().Name + "." + field);
        f.SetValue(boxed, value);
        return boxed;
    }

    static object Get(object o, string field) => o.GetType().GetField(field).GetValue(o);

    // ---------------------------------------------------------------- chips

    static List<int> Decompose(long cents, string denomsField)
    {
        var math = T("CasinoChipMath");
        var denoms = (int[])math.GetField(denomsField, Static).GetValue(null);
        var list = new List<int>();
        Call(math, "Decompose", cents, denoms, list);
        return list;
    }

    [Test]
    public void ChipsAddUpAndBigChipsComeFirst()
    {
        foreach (string field in new[] { "BlackjackDenoms", "RouletteDenoms" })
            for (long dollars = 0; dollars <= 1000; dollars++)
            {
                var chips = Decompose(dollars * 100, field);
                long sum = 0;
                for (int i = 0; i < chips.Count; i++)
                {
                    sum += chips[i];
                    if (i > 0) Assert.LessOrEqual(chips[i], chips[i - 1], "big chips at the bottom");
                }
                Assert.AreEqual(dollars * 100, sum, field + " $" + dollars);
            }
    }

    [Test]
    public void AStackStaysShortAtTheTableLimits()
    {
        // inside max $100, outside $500 (roulette); blackjack $200 plus doubles and splits
        for (long dollars = 1; dollars <= 100; dollars++) Assert.LessOrEqual(Decompose(dollars * 100, "RouletteDenoms").Count, 12, "roulette $" + dollars);
        for (long dollars = 100; dollars <= 500; dollars += 7) Assert.LessOrEqual(Decompose(dollars * 100, "RouletteDenoms").Count, 20, "roulette $" + dollars);
        for (long dollars = 5; dollars <= 200; dollars++) Assert.LessOrEqual(Decompose(dollars * 100, "BlackjackDenoms").Count, 12, "blackjack $" + dollars);
        CollectionAssert.AreEqual(new[] { 2500, 2500, 2500, 500, 500, 500, 500, 100, 100, 100, 100 }, Decompose(9900, "RouletteDenoms"));
    }

    // ---------------------------------------------------------------- the deal diff

    static object Hand(params byte[] cards)
    {
        object h = Activator.CreateInstance(T("BlackjackHandSnapshot"));
        Set(h, "cards", cards);
        Set(h, "stakeCents", 1000);
        return h;
    }

    static object Seat(params object[] hands)
    {
        object s = Activator.CreateInstance(T("BlackjackSeatSnapshot"));
        var arr = Array.CreateInstance(T("BlackjackHandSnapshot"), hands.Length);
        for (int i = 0; i < hands.Length; i++) arr.SetValue(hands[i], i);
        Set(s, "hands", arr);
        Set(s, "key", 1);
        return s;
    }

    static object Snapshot(int round, byte[] dealer, params object[] seats)
    {
        object snap = Activator.CreateInstance(T("BlackjackTableSnapshot"));
        Set(snap, "round", round);
        Set(snap, "dealer", dealer);
        var arr = Array.CreateInstance(T("BlackjackSeatSnapshot"), 5);
        for (int i = 0; i < arr.Length; i++) arr.SetValue(i < seats.Length && seats[i] != null ? seats[i] : Activator.CreateInstance(T("BlackjackSeatSnapshot")), i);
        Set(snap, "seats", arr);
        return snap;
    }

    static IList NewCards(object prev, bool hasPrev, object next)
    {
        var listType = typeof(List<>).MakeGenericType(T("DealtCard"));
        var list = (IList)Activator.CreateInstance(listType);
        var args = new object[] { prev, hasPrev, next, list };
        Call(T("BlackjackDiff"), "NewCards", args);
        return list;
    }

    [Test]
    public void NoPreviousSnapshotMakesEveryCardNew()
    {
        var next = Snapshot(3, new byte[] { 12, 255 }, Seat(Hand(5, 6)), null, Seat(Hand(7, 8, 9)));
        var added = NewCards(Snapshot(0, null), false, next);
        Assert.AreEqual(2 + 2 + 3, added.Count);
    }

    [Test]
    public void OnlyTheAppendedCardIsNew()
    {
        var prev = Snapshot(3, new byte[] { 12, 255 }, Seat(Hand(5, 6)));
        var next = Snapshot(3, new byte[] { 12, 255 }, Seat(Hand(5, 6, 20)));
        var added = NewCards(prev, true, next);
        Assert.AreEqual(1, added.Count);
        Assert.AreEqual(0, (int)Get(added[0], "seat"));
        Assert.AreEqual(0, (int)Get(added[0], "hand"));
        Assert.AreEqual(2, (int)Get(added[0], "index"));
        Assert.AreEqual((byte)20, (byte)Get(added[0], "card"));
        Assert.AreEqual(-1, (int)Get(added[0], "fromHand"));
    }

    [Test]
    public void ADealerDrawIsNewAndTheHoleReveal_IsNotACardDeal()
    {
        var prev = Snapshot(3, new byte[] { 12, 255 }, Seat(Hand(5, 6)));
        var revealed = Snapshot(3, new byte[] { 12, 7 }, Seat(Hand(5, 6)));
        Assert.AreEqual(0, NewCards(prev, true, revealed).Count, "turning the hole card over deals nothing");
        Assert.IsTrue((bool)Call(T("BlackjackDiff"), "HoleRevealed", new object[] { prev, true, revealed }));
        Assert.IsFalse((bool)Call(T("BlackjackDiff"), "HoleRevealed", new object[] { revealed, true, revealed }));
        var drew = Snapshot(3, new byte[] { 12, 7, 30 }, Seat(Hand(5, 6)));
        var added = NewCards(revealed, true, drew);
        Assert.AreEqual(1, added.Count);
        Assert.AreEqual(-1, (int)Get(added[0], "seat"));
        Assert.AreEqual(2, (int)Get(added[0], "index"));
    }

    [Test]
    public void ANewRoundMakesEverythingNew_AndASplitCardComesFromItsFirstHand()
    {
        var prev = Snapshot(3, new byte[] { 12, 7 }, Seat(Hand(5, 18)));
        var next = Snapshot(4, new byte[] { 12, 255 }, Seat(Hand(9, 10)));
        Assert.AreEqual(4, NewCards(prev, true, next).Count);

        var before = Snapshot(4, new byte[] { 12, 255 }, Seat(Hand(5, 18)));
        var after = Snapshot(4, new byte[] { 12, 255 }, Seat(Hand(5), Hand(18)));
        var added = NewCards(before, true, after);
        Assert.AreEqual(1, added.Count, "the second hand's card is the one that left the first");
        Assert.AreEqual(1, (int)Get(added[0], "hand"));
        Assert.AreEqual(0, (int)Get(added[0], "fromHand"));
    }

    // ---------------------------------------------------------------- hand text

    static string HandText(byte[] cards, bool natural, out string tone)
    {
        var args = new object[] { cards, cards.Length, natural, null };
        string s = (string)Call(T("BlackjackDiff"), "HandText", args);
        tone = args[3].ToString();
        return s;
    }

    [Test]
    public void HandsReadHardSoftBustAndBlackjack()
    {
        // card byte: rank = card % 13 (0 ace, 1..8 = 2..9, 9 ten, 10 J, 11 Q, 12 K)
        Assert.AreEqual("7/17", HandText(new byte[] { 0, 5 }, false, out _));      // A + 6
        Assert.AreEqual("16", HandText(new byte[] { 9, 5 }, false, out _));        // 10 + 6
        Assert.AreEqual("BUST 26", HandText(new byte[] { 9, 5, 9 }, false, out string tone));
        Assert.AreEqual("Bust", tone);
        Assert.AreEqual("BLACKJACK", HandText(new byte[] { 0, 12 }, true, out tone));
        Assert.AreEqual("Blackjack", tone);
        Assert.AreEqual("21", HandText(new byte[] { 6, 5, 7 }, false, out tone));  // 7 + 6 + 8
        Assert.AreEqual("Twenty1", tone);
        Assert.AreEqual("2/12", HandText(new byte[] { 0, 0 }, false, out _));
    }

    // ---------------------------------------------------------------- the wheel

    static void Ball(int pocket, int rest, double t, float rotor, out float angle, out float track, out float hop)
    {
        var args = new object[] { pocket, rest, t, rotor, 0f, 0f, 0f };
        Call(T("CasinoWheelMath"), "BallAt", args);
        angle = (float)args[4]; track = (float)args[5]; hop = (float)args[6];
    }

    static float PocketAngle(int pocket) => (float)Call(T("CasinoWheelMath"), "PocketAngle", pocket);
    static float RotorAngle(double t) => (float)Call(T("CasinoWheelMath"), "RotorAngle", t);
    static float Fold(float a) { a %= 360f; return a < 0 ? a + 360f : a; }

    [Test]
    public void TheBallLandsInTheTruePocketExactlyAtTheEndOfTheSpin()
    {
        double spin = (double)T("CasinoWheelMath").GetField("SpinSeconds", Static).GetValue(null);
        Assert.AreEqual(7.0, spin);
        int[] order = (int[])T("CasinoStation").GetField("WheelOrder", Static).GetValue(null);
        foreach (int pocket in order)
            foreach (double start in new[] { 0.0, 1234.567, 98765.4321, 3.3e5 })
            {
                float rotorEnd = RotorAngle(start + spin);
                Ball(pocket, 17, spin, rotorEnd, out float angle, out float track, out float hop);
                float rel = Fold(angle - rotorEnd), want = PocketAngle(pocket);
                Assert.AreEqual(want, rel, 1e-3f, "pocket " + pocket + " start " + start);
                Assert.AreEqual(0f, track);
                Assert.AreEqual(0f, hop);
                // a hair before the end it is already on the pocket's floor (the hop and the drift have died away)
                float rotorJust = RotorAngle(start + spin - 0.01);
                Ball(pocket, 17, spin - 0.01, rotorJust, out angle, out track, out hop);
                Assert.Less(Math.Abs(DeltaAngle(Fold(angle - rotorJust), want)), 2.5f, "pocket " + pocket + " just before the end");
                Assert.Less(hop, 0.001f);
            }
    }

    static float DeltaAngle(float a, float b)
    {
        float d = (b - a) % 360f;
        if (d > 180f) d -= 360f;
        if (d < -180f) d += 360f;
        return d;
    }

    [Test]
    public void TheBallRunsAgainstTheWheelSlowsAndDropsOff()
    {
        const int pocket = 11;
        float prevAbs = float.MaxValue, prevSpeed = float.MaxValue;
        const double step = 0.05;
        for (double t = 0.4; t <= 4.8; t += step)
        {
            float rotor = (float)(t * 26.0); // unwrapped, so the angles compare
            Ball(pocket, 3, t, rotor, out float angle, out float track, out _);
            Assert.AreEqual(1f, track, 1e-4f, "on the track at " + t);
            if (prevAbs != float.MaxValue)
            {
                float speed = prevAbs - angle; // degrees per step, positive = turning against the wheel
                Assert.Greater(speed, 0f, "the ball keeps turning against the rotor at " + t);
                Assert.LessOrEqual(speed, prevSpeed + 0.5f, "and slows down at " + t);
                prevSpeed = speed;
            }
            prevAbs = angle;
        }
        Ball(pocket, 3, 6.5, 0f, out _, out float tr, out _);
        Assert.AreEqual(0f, tr, 1e-4f, "down in the pockets by 6.5 s");
        Ball(pocket, 3, 5.3, 0f, out _, out tr, out _);
        Assert.That(tr, Is.GreaterThan(0.01f).And.LessThan(0.99f), "dropping at 5.3 s");
    }

    [Test]
    public void TheBallRidesTheRotorAfterTheSpin()
    {
        foreach (double t in new[] { 7.0, 7.5, 12.0 })
        {
            float rotor = RotorAngle(500.0 + t);
            Ball(26, 5, t, rotor, out float angle, out _, out _);
            Assert.AreEqual(PocketAngle(26), Fold(angle - rotor), 1e-3f);
        }
    }

    // ---------------------------------------------------------------- the reels

    [Test]
    public void ADrumEndsOnItsStopWithTheTrueFaceInTheWindow()
    {
        var reel = T("CasinoReelMath");
        for (int rest = 0; rest < 30; rest += 3)
            for (int stop = 0; stop < 64; stop += 5)
                foreach (float dur in new[] { 1.25f, 1.65f, 2.05f, 3.1f })
                {
                    int n = (int)Call(reel, "TravelStops", rest, stop, dur);
                    Assert.AreEqual(0, (((rest - n - stop) % 12) + 12) % 12, "travel " + n + " from " + rest + " to " + stop);
                    Assert.GreaterOrEqual(n, 12, "a real spin, not a nudge");
                    float end = (float)Call(reel, "AngleDeg", rest, stop, dur, dur);
                    float want = (float)Call(reel, "RestAngleDeg", stop);
                    Assert.Less(Math.Abs(DeltaAngle(Fold(end), Fold(want))), 1e-2f, "rest " + rest + " stop " + stop);
                    // face (-s) mod 12 is the one in the window: its rotation is face * 30 degrees
                    int face = (int)Call(T("CasinoPaint"), "DrumFace", stop, 0);
                    Assert.Less(Math.Abs(DeltaAngle(Fold(end), Fold(face * 30f))), 1e-2f);
                }
    }

    [Test]
    public void TheTwelveFacesCarryTheTwelveStripSymbolsAroundTheStop()
    {
        for (int stop = 0; stop < 40; stop++)
        {
            var faces = new HashSet<int>();
            for (int j = -5; j <= 6; j++) faces.Add((int)Call(T("CasinoPaint"), "DrumFace", stop, j));
            Assert.AreEqual(12, faces.Count, "every face is used once at stop " + stop);
            // the row above the line (j = -1) is the face next to the window's centre face on one side, below (+1) on the other
            int centre = (int)Call(T("CasinoPaint"), "DrumFace", stop, 0);
            Assert.AreEqual((centre + 1) % 12, (int)Call(T("CasinoPaint"), "DrumFace", stop, -1));
            Assert.AreEqual((centre + 11) % 12, (int)Call(T("CasinoPaint"), "DrumFace", stop, 1));
        }
    }

    [Test]
    public void ReelProgressIsMonotoneAndComplete()
    {
        var reel = T("CasinoReelMath");
        float prev = -1f;
        for (int i = 0; i <= 200; i++)
        {
            float p = (float)Call(reel, "Progress", i / 200f);
            Assert.GreaterOrEqual(p, prev - 1e-6f);
            prev = p;
        }
        Assert.AreEqual(0f, (float)Call(reel, "Progress", 0f), 1e-6f);
        Assert.AreEqual(1f, (float)Call(reel, "Progress", 1f), 1e-5f);
        float t0 = (float)Call(reel, "StopTime", 0, false), t2 = (float)Call(reel, "StopTime", 2, false), teased = (float)Call(reel, "StopTime", 2, true);
        Assert.GreaterOrEqual(t2, 1.6f, "the whole spin takes at least 1.6 s");
        Assert.AreEqual(0.4f, ((float)Call(reel, "StopTime", 1, false)) - t0, 1e-4f, "reels stop about 0.4 s apart");
        Assert.Greater(teased, t2);
    }

    // ---------------------------------------------------------------- the pay plate

    [Test]
    public void ThePayPlateListsTheRealPayTable()
    {
        var rules = T("SlotRules");
        var plate = T("SlotPlateText");
        int machines = (int)rules.GetProperty("MachineCount", Static).GetValue(null);
        for (int m = 0; m < machines; m++)
        {
            object machine = Call(rules, "Machine", m);
            var pays = (Array)Get(machine, "pays");
            int stake = (int)Get(machine, "stakeCents");
            Assert.LessOrEqual(pays.Length, 14, "the plate has 14 lines");
            for (int i = 0; i < pays.Length; i++)
            {
                string combo = (string)Call(plate, "Combo", (string)Get(pays.GetValue(i), "name"));
                Assert.IsNotEmpty(combo);
                Assert.LessOrEqual(combo.Length, 24, combo);
                Assert.AreEqual(combo, combo.ToUpperInvariant());
                string payout = (string)Call(plate, "Payout", m, i);
                long expect = (long)stake * (int)Get(pays.GetValue(i), "multiple");
                Assert.AreEqual(CasinoDollars(expect), payout);
            }
            StringAssert.StartsWith("RETURNS ", (string)Call(plate, "Returns", m));
            StringAssert.EndsWith("% over the long run", (string)Call(plate, "Returns", m));
            StringAssert.StartsWith("STAKE $", (string)Call(plate, "Stake", m));
        }
        Assert.AreEqual("RETURNS 95.0% over the long run", (string)Call(plate, "Returns", 0));
        Assert.AreEqual("3 LUCKY SEVENS", (string)Call(plate, "Combo", "Three Lucky Sevens"));
        Assert.AreEqual("ANY 3 BARS", (string)Call(plate, "Combo", "Any Three Bars"));
    }

    // ---------------------------------------------------------------- drinks served

    [Test]
    public void ADrinkServedIsHeardOnceWithAValidSpot_AndABadDrinkIsIgnored()
    {
        var host = T("CasinoHost");
        var ev = host.GetEvent("BarServedHeard", Static);
        Assert.IsNotNull(ev);
        var seen = new List<string>();
        Action<ulong, int, int> handler = (k, d, sp) => seen.Add(k + ":" + d + ":" + sp);
        ev.AddEventHandler(null, handler);
        try
        {
            Call(host, "HearBarServed", 7UL, 2, 1);
            Call(host, "HearBarServed", 7UL, 99, 1);   // not on the menu
            Call(host, "HearBarServed", 8UL, 0, 9);    // spot out of range: clamped to the last bar spot
            CollectionAssert.AreEqual(new[] { "7:2:1", "8:0:2" }, seen);
        }
        finally { ev.RemoveEventHandler(null, handler); }
    }

    // ---------------------------------------------------------------- floating labels face the viewer, never mirrored

    [Test]
    public void FloatingLabelsFaceWhicheverCameraRendersThem_AndAreNeverSeenFromBehind()
    {
        var facing = T("CasinoFacing");
        var go = new UnityEngine.GameObject("label");
        try
        {
            var t = go.transform;
            t.position = new UnityEngine.Vector3(2f, 1.9f, -3f);
            // cameras all round it, high and low (the dealer's side, a spectator beside the table, a still taken from above)
            for (int a = 0; a < 360; a += 15)
                foreach (float h in new[] { 0.4f, 1.3f, 1.9f, 3.2f })
                {
                    var cam = t.position + new UnityEngine.Vector3(UnityEngine.Mathf.Sin(a * UnityEngine.Mathf.Deg2Rad), 0f, UnityEngine.Mathf.Cos(a * UnityEngine.Mathf.Deg2Rad)) * 3.5f;
                    cam.y = h;
                    var toLabel = (t.position - cam).normalized;
                    var flat = new UnityEngine.Vector3(toLabel.x, 0f, toLabel.z).normalized;

                    Call(facing, "Face", t, cam, UnityEngine.Vector3.up, false);
                    Assert.Greater(UnityEngine.Vector3.Dot(t.forward, toLabel), 0.999f, "text reads from its -Z side: +Z must point away from the viewer (full), a=" + a + " h=" + h);

                    Call(facing, "Face", t, cam, UnityEngine.Vector3.up, true);
                    Assert.Greater(UnityEngine.Vector3.Dot(t.forward, flat), 0.999f, "a name tag faces the viewer about the vertical axis, a=" + a + " h=" + h);
                    Assert.Less(UnityEngine.Mathf.Abs(t.forward.y), 1e-4f, "a name tag stays upright");
                    Assert.Greater(t.up.y, 0.9999f);
                    Assert.Greater(UnityEngine.Vector3.Dot(t.forward, toLabel), 0f, "never seen from behind (mirrored)");
                }
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    [Test]
    public void TheHintStripStaysShortSoTheRackAndThePlatesAreNotCovered()
    {
        var strip = T("CasinoHintStrip");
        float margin = (float)strip.GetField("BaseMargin", Static).GetValue(null), pitch = (float)strip.GetField("RowPitch", Static).GetValue(null), shade = (float)strip.GetField("ShadeHeight", Static).GetValue(null);
        float stack = margin + 6f + 3f * pitch; // three rows, above the HUD's own dock (which it rides on)
        Assert.LessOrEqual(stack, 150f, "reference px: at 1080p about one seventh of the picture");
        Assert.LessOrEqual(shade, 220f);
        Assert.GreaterOrEqual(pitch, 34f, "rows stay readable");
    }

    static string CasinoDollars(long cents) => (string)Call(T("CasinoRules"), "Dollars", cents);
}
