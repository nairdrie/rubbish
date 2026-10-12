// Setup_Casino, part 6: seat mode world prep (Docs/Casino.md §9 / §9.1). Shared helpers (pick colliders, 3D buttons, chip racks, text
// on an arc, bulb rows, lit boards) and the blackjack table's seats. The roulette, slot and bar halves live in
// Setup_Casino.SeatsRoulette.cs and Setup_Casino.SeatsSlotBar.cs.
using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEngine;

public static partial class Setup_Casino
{
    static readonly Color Ink2 = new Color(0.05f, 0.04f, 0.04f), BrassText = new Color(0.95f, 0.78f, 0.38f), AmberText = new Color(1f, 0.74f, 0.28f);

    /// <summary>Chip colours by denomination in cents (the same palette as CasinoArt.ChipColor).</summary>
    static readonly Dictionary<int, Color> ChipColors = new Dictionary<int, Color>
    {
        { 100, new Color(0.93f, 0.90f, 0.84f) }, { 500, new Color(0.78f, 0.125f, 0.17f) }, { 1000, new Color(0.14f, 0.34f, 0.77f) },
        { 2500, new Color(0.12f, 0.54f, 0.29f) }, { 5000, new Color(0.88f, 0.48f, 0.10f) }, { 10000, new Color(0.12f, 0.12f, 0.15f) },
    };

    static void DefineSeatPalette()
    {
        Glow("btn_face", C(0.9f, 0.62f, 0.18f), C(0.55f, 0.28f, 0.06f), 0.45f).noInstancing = true;
        Glow("plate_glass", C(0.03f, 0.045f, 0.08f), C(0.02f, 0.03f, 0.06f), 0.08f);
        Def("tray_dark", C(0.035f, 0.03f, 0.028f), 0.35f);
        Def("chrome_satin", C(0.62f, 0.63f, 0.65f), 0.42f, 0.9f); // a duller chrome for what sits right in front of the eye: no hot highlight to bloom
        foreach (var kv in ChipColors) Def("seat_chip_" + kv.Key, kv.Value, 0.45f);
        Glow("timer_bulb", C(0.3f, 0.28f, 0.25f), C(0.05f, 0.03f, 0.02f), 0.75f).noInstancing = true;
        Glow("result_disc", C(0.1f, 0.1f, 0.1f), C(0.05f, 0.05f, 0.05f), 0.5f).noInstancing = true;
        Glow("tray_glow", C(0.9f, 0.7f, 0.35f), C(0.9f, 0.6f, 0.2f), 0.4f);
        Glow("drink_row", C(0.07f, 0.06f, 0.05f), C(0.12f, 0.06f, 0.02f), 0.3f).noInstancing = true;
        Def("seat_brass", C(0.78f, 0.58f, 0.24f), 0.7f, 1f);
        Glow("sign_panel", C(0.045f, 0.03f, 0.02f), C(0.05f, 0.03f, 0.012f), 0.1f);
        Glow("win_marker", C(0.9f, 0.7f, 0.2f), C(0.5f, 0.35f, 0.05f), 0.8f);
    }

    // ------------------------------------------------------------------------------------------------ pick colliders

    static CasinoPickable AddPick(Transform parent, string name, Vector3 localCenter, Vector3 size, CasinoStation st, int spot, CasinoPickKind kind, int index, string id, string hint, Vector3 euler = default)
    {
        var go = new GameObject(name) { layer = CasinoLayout.PickLayer };
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localCenter; go.transform.localRotation = Quaternion.Euler(euler);
        var c = go.AddComponent<BoxCollider>(); c.size = size; c.isTrigger = true;
        var p = go.AddComponent<CasinoPickable>();
        p.kind = kind; p.station = st; p.spot = spot; p.index = index; p.id = id; p.hint = hint;
        return p;
    }

    /// <summary>A lit plate (or a round button) lying on a surface: face up (+Y), text reading with its top toward +Z. A trigger
    /// collider on the pick layer, a <see cref="CasinoButton"/> on the root, a moving cap, a printed label.</summary>
    static CasinoButton MakeButton(Transform parent, CasinoStation st, int spot, string id, string label, string printed, Vector3 localPos, Vector3 euler, Vector2 size, bool round = false, string hint = null)
    {
        var go = new GameObject("Btn " + id) { layer = CasinoLayout.PickLayer };
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos; go.transform.localRotation = Quaternion.Euler(euler);
        string key = (round ? "btnr_" : "btn_") + Mathf.RoundToInt(size.x * 1000) + "x" + Mathf.RoundToInt(size.y * 1000);
        var frame = Place(go.transform, key + "_frame", m =>
        {
            if (round) m.Lathe("seat_brass", Vector3.zero, new[] { V(size.x * .5f + .009f, 0), V(size.x * .5f + .009f, .008f), V(0, .008f) }, 22);
            else m.Box("seat_brass", new Vector3(0, .004f, 0), new Vector3(size.x + .016f, .008f, size.y + .016f));
        }, Vector3.zero, 0, false);
        frame.name = "Frame";
        var cap = Place(go.transform, key + "_cap", m =>
        {
            if (round) m.Lathe("btn_face", Vector3.zero, new[] { V(0, 0), V(size.x * .5f, 0), V(size.x * .5f, .012f), V(size.x * .43f, .016f), V(0, .017f) }, 22);
            else m.Box("btn_face", new Vector3(0, .007f, 0), new Vector3(size.x, .014f, size.y));
        }, new Vector3(0, .002f, 0), 0, false);
        cap.name = "Cap";
        // small flat parts that sit under the eye: no shadow receiving (the table lamp's shadow map striped them up close in the game)
        frame.GetComponent<MeshRenderer>().receiveShadows = false; cap.GetComponent<MeshRenderer>().receiveShadows = false;
        TMP_Text text = null;
        if (!string.IsNullOrEmpty(printed))
            text = Label(cap.transform, "Label", printed, new Vector3(0, round ? .0178f : .0148f, 0), new Vector3(90, 0, 0), new Vector2(size.x * .94f, size.y * .8f), Ink2, FontStyles.Bold);
        var col = go.AddComponent<BoxCollider>(); col.isTrigger = true;
        col.size = new Vector3(size.x + .03f, .06f, size.y + .03f); col.center = new Vector3(0, .02f, 0);
        var b = go.AddComponent<CasinoButton>();
        b.kind = CasinoPickKind.Button; b.station = st; b.spot = spot; b.id = id; b.label = label; b.hint = hint ?? label;
        b.labelText = text; b.cap = cap.transform; b.pressDepth = .007f;
        b.lit = new[] { cap.GetComponent<Renderer>() };
        return b;
    }

    // ------------------------------------------------------------------------------------------------ chip rack

    /// <summary>A rack of bins lying on a surface (face up, player frame: +X right, +Z away): returns the bin transforms.
    /// Each bin is a dished brass-and-colour ring with the denomination printed in front, and a pick box.</summary>
    static Transform[] ChipRack(Transform parent, CasinoStation st, int spot, string name, Vector3 localPos, Vector3 euler, int[] cents, float pitch)
    {
        var rack = Node(parent, name, localPos, euler);
        int n = cents.Length;
        float w = (n - 1) * pitch + .09f;
        var tray = Place(rack, "chiprack_tray_" + n + "_" + Mathf.RoundToInt(pitch * 1000), m =>
        {
            m.Box("tray_dark", new Vector3(0, .003f, .004f), new Vector3(w, .006f, .108f));
            m.Box("seat_brass", new Vector3(0, .0035f, -.054f), new Vector3(w + .008f, .007f, .006f));
            m.Box("seat_brass", new Vector3(0, .0035f, .058f), new Vector3(w + .008f, .007f, .006f));
            m.Box("seat_brass", new Vector3(-w * .5f - .001f, .0035f, .002f), new Vector3(.006f, .007f, .116f));
            m.Box("seat_brass", new Vector3(w * .5f + .001f, .0035f, .002f), new Vector3(.006f, .007f, .116f));
        }, Vector3.zero, 0, true);
        tray.name = "Tray";
        tray.GetComponent<MeshRenderer>().receiveShadows = false;
        var bins = new Transform[n];
        for (int j = 0; j < n; j++)
        {
            int c = cents[j];
            var bin = Node(rack, "Chip" + c, new Vector3((j - (n - 1) * .5f) * pitch, .006f, .004f));
            var vis = Place(bin, "chipbin_" + c, m =>
            {
                m.Torus("seat_chip_" + c, new Vector3(0, .0015f, 0), .0345f, .0042f, 20, 6);
                m.Disc("tray_dark", new Vector3(0, .0004f, 0), .034f, 20);
            }, Vector3.zero, 0, true);
            vis.name = "Bin";
            vis.GetComponent<MeshRenderer>().receiveShadows = false;
            string txt = c >= 100 ? "$" + (c / 100) : "";
            Label(bin, "Denom", txt, new Vector3(0, .003f, -.047f), new Vector3(90, 0, 0), new Vector2(.06f, .028f), BrassText, FontStyles.Bold);
            AddPick(bin, "Pick", new Vector3(0, .02f, -.01f), new Vector3(pitch * .96f, .05f, .1f), st, spot, CasinoPickKind.ChipBin, j, "Chip", "$" + (c / 100) + " chips");
            bins[j] = bin;
        }
        return bins;
    }

    // ------------------------------------------------------------------------------------------------ text on an arc

    /// <summary>Advance of a bold capital as a fraction of the em height (close enough to space the letters on an arc).</summary>
    static float GlyphAdv(char c)
    {
        if (c == ' ') return 0.30f;
        if ("I1.,'!:;|l".IndexOf(c) >= 0) return 0.34f;
        if ("MW".IndexOf(c) >= 0) return 0.90f;
        if (char.IsDigit(c) || c == '$') return 0.60f;
        return 0.72f;
    }

    /// <summary>Lettering laid flat on a table along a circle about <paramref name="centre"/> (parent space), reading left to right
    /// for a player standing outside the circle at angle <paramref name="centreTheta"/> (degrees from -Z toward +X), letter tops
    /// pointing at the centre. <paramref name="capHeight"/> is the capital letter height in metres (fixed font size, em = cap / 0.72).</summary>
    static Transform ArcText(Transform parent, string name, string text, Vector3 centre, float radius, float centreTheta, float capHeight, Color color, FontStyles style = FontStyles.Bold, float tracking = 1.04f)
    {
        var group = Node(parent, name, centre);
        float em = capHeight / 0.72f;
        float total = 0f;
        foreach (char ch in text) total += GlyphAdv(ch) * em * tracking;
        float a = centreTheta * Mathf.Deg2Rad - total / radius * .5f, cur = 0f;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            float adv = GlyphAdv(ch) * em * tracking;
            float ang = a + (cur + adv * .5f) / radius;
            cur += adv;
            if (ch == ' ') continue;
            FixedText(group, "c" + i, ch.ToString(), new Vector3(Mathf.Sin(ang) * radius, 0.002f, -Mathf.Cos(ang) * radius),
                new Vector3(90, -ang * Mathf.Rad2Deg, 0), new Vector2(em * 1.2f, em * 1.2f), color, em * 10f, TextAlignmentOptions.Center, style);
        }
        return group.transform;
    }

    // ------------------------------------------------------------------------------------------------ bulbs, boards

    /// <summary>A row of bulbs along local X (renderers returned left to right), each its own object so a property block can light it.</summary>
    static Renderer[] BulbRow(Transform parent, string name, Vector3 centre, int count, float pitch, float radius)
    {
        var row = Node(parent, name, centre);
        var rs = new Renderer[count];
        for (int i = 0; i < count; i++)
        {
            var b = Place(row, "timer_bulb_" + Mathf.RoundToInt(radius * 1000), m => m.Sphere("timer_bulb", Vector3.zero, Vector3.one * radius, 10, 6), new Vector3((i - (count - 1) * .5f) * pitch, 0, 0), 0, false);
            b.name = "Bulb" + i;
            rs[i] = b.GetComponent<Renderer>();
        }
        return rs;
    }

    static CasinoTimerLamp MakeTimerLamp(Transform bulbParent, Vector3 centre, int count, float pitch, float radius, Light glow = null)
    {
        var row = BulbRow(bulbParent, "Timer lamp", centre, count, pitch, radius);
        var lamp = row[0].transform.parent.gameObject.AddComponent<CasinoTimerLamp>();
        lamp.bulbs = row; lamp.glow = glow;
        return lamp;
    }

    /// <summary>A lit sign: dark panel with a brass frame, one or two lines of lettering. Reads from -Z (identity), lean it with the parent.</summary>
    static TextMeshPro SignPanel(Transform parent, string name, Vector3 pos, Vector3 euler, Vector2 size, string text, Color color, string panelMat = "sign_panel")
    {
        var node = Node(parent, name, pos, euler);
        var fr = Place(node, "sign_" + panelMat + Mathf.RoundToInt(size.x * 1000) + "x" + Mathf.RoundToInt(size.y * 1000), m =>
        {
            m.Box(panelMat, new Vector3(0, size.y * .5f, 0), new Vector3(size.x, size.y, .012f));
            float t = .012f;
            m.Box("seat_brass", new Vector3(0, size.y - t * .5f, -.003f), new Vector3(size.x + .01f, t, .02f));
            m.Box("seat_brass", new Vector3(0, t * .5f, -.003f), new Vector3(size.x + .01f, t, .02f));
            m.Box("seat_brass", new Vector3(-size.x * .5f - .002f, size.y * .5f, -.003f), new Vector3(t, size.y, .02f));
            m.Box("seat_brass", new Vector3(size.x * .5f + .002f, size.y * .5f, -.003f), new Vector3(t, size.y, .02f));
            m.Box("black_iron", new Vector3(0, size.y * .3f, .03f), new Vector3(size.x * .5f, size.y * .6f, .03f));
        }, Vector3.zero, 0, true);
        fr.name = "Frame";
        return Label(node, "Text", text, new Vector3(0, size.y * .5f, -.0085f), Vector3.zero, new Vector2(size.x - .03f, size.y - .03f), color, FontStyles.Bold);
    }

    /// <summary>Flat parts that sit right under the eye (plates, boards, signs) don't receive shadows: the table lamps' shadow maps
    /// striped them with acne at close range in the game.</summary>
    static void NoRecv(Transform t)
    {
        if (t == null) return;
        foreach (var r in t.GetComponentsInChildren<MeshRenderer>(true)) r.receiveShadows = false;
    }

    static Color ChipInkFor(int cents) => cents == 100 ? Ink2 : Color.white;

    // ------------------------------------------------------------------------------------------------ blackjack

    const float BjR = 1.52f, BjRF = 1.42f, BjZf = 0.5f, BjTop = 0.92f;
    static readonly float[] BjTheta = { 62f, 31f, 0f, -31f, -62f };
    const float BjStand = 1.84f, BjStool = 1.95f, BjEye = 1.76f, BjLeanR = 1.58f;
    const float BjBet = 1.12f, BjRack = 1.26f, BjBtn = 1.372f, BjIns = 0.60f, BjDealerRow = 0.40f, BjEyeAim = 1.20f;
    /// <summary>The action plates stand tilted toward the eye on a slant (degrees), 0.112 x 0.052 m, 0.122 m apart.</summary>
    const float BjPlateTilt = 26f, BjPlateW = 0.112f, BjPlateH = 0.052f, BjPlatePitch = 0.122f;
    static readonly float[] BjHandR = { 0.94f, 0.80f, 0.66f };
    const float BjHandTilt = 14f, BjDealerTilt = 26f;

    /// <summary>Table-local point at radius r from the dealer's arc centre, angle theta (degrees from -Z toward +X; seat 0 = +62).</summary>
    static Vector3 BjPoint(float r, float thetaDeg, float y = 0f)
    {
        float a = thetaDeg * Mathf.Deg2Rad;
        return new Vector3(Mathf.Sin(a) * r, y, BjZf - Mathf.Cos(a) * r);
    }

    static void BlackjackPrints(Transform root, CasinoStation st)
    {
        var print = Node(root, "Print", new Vector3(0, BjTop + .002f, BjZf));
        Color gold = new Color(0.97f, 0.88f, 0.48f);
        ArcText(print, "Pays", "BLACKJACK PAYS 3 TO 2", Vector3.zero, 0.84f, 0f, 0.052f, gold, FontStyles.Bold);
        ArcText(print, "Soft17", "DEALER MUST HIT SOFT 17", Vector3.zero, 0.745f, 0f, 0.032f, gold, FontStyles.Bold);
        ArcText(print, "Insurance", "INSURANCE PAYS 2 TO 1", Vector3.zero, 0.60f, 0f, 0.038f, gold, FontStyles.Bold);
        st.printRoot = print;
    }

    static void BlackjackSeats(Transform root, CasinoStation st)
    {
        const int N = CasinoLayout.BlackjackSeats;
        var spots = new CasinoSpot[N];
        var platesMb = new MB();
        for (int k = 0; k < N; k++)
        {
            float th = BjTheta[k];
            var sp = new CasinoSpot { label = "Seat " + (k + 1), eyeFov = 66f, leanFov = 54f, watchFov = 50f, lookYaw = 58f, lookPitchMin = -72f, lookPitchMax = 32f };
            spots[k] = sp;
            sp.stand = Node(root, "Stand " + (k + 1), BjPoint(BjStand, th), new Vector3(0, -th, 0));
            // aimed just beyond the bet circle (about 34 degrees down): the chip rack and the lit plates then sit in the picture above the hint strip
            sp.eye = LookNode(root, "Eye " + (k + 1), BjPoint(BjEye, th, CasinoLayout.EyeHeight), BjPoint(BjEyeAim, th, BjTop));
            sp.lean = LookNode(root, "Lean " + (k + 1), BjPoint(BjLeanR, th, 1.25f), BjPoint(1.12f, th, BjTop)); // sees the rack (the plates are the eye pose's), the bet and the hand
            sp.watch = LookNode(root, "Watch " + (k + 1), BjPoint(BjEye, th, CasinoLayout.EyeHeight), new Vector3(0f, 1.22f, BjZf + .36f));
            sp.floatText = Node(root, "FloatText " + (k + 1), BjPoint(BjStand, th, 1.9f));

            // the stool: stays where it was, slides straight out (away from the table) under an occupant
            var stool = Place(root, "stool", StoolRecipe, BjPoint(BjStool, th), k * 50f, false, true); // not static: it slides
            stool.name = "Stool " + (k + 1);
            ColCapsule(stool.transform, "Col", new Vector3(0, .4f, 0), .2f, .8f);
            sp.stool = stool.transform;
            sp.stoolAway = new Vector3(Mathf.Sin(th * Mathf.Deg2Rad), 0f, -Mathf.Cos(th * Mathf.Deg2Rad)) * 0.66f;

            // the seat's felt frame (player's: +X right, +Z toward the dealer), origin on the bet circle
            var seat = Node(root, "Seat frame " + (k + 1), BjPoint(BjBet, th, BjTop + .004f), new Vector3(0, -th, 0));
            sp.betCircle = Node(seat, "BetCircle");
            AddPick(sp.betCircle, "Pick", new Vector3(0, .015f, 0), new Vector3(.2f, .03f, .2f), st, k, CasinoPickKind.BetCircle, 0, "BetCircle", "Place a bet");
            sp.insuranceSpot = Node(seat, "InsuranceSpot", new Vector3(0, 0, BjBet - BjIns));
            sp.handSpots = new CasinoHandSpot[CasinoLayout.HandsPerSeat];
            for (int h = 0; h < sp.handSpots.Length; h++)
            {
                var hs = new CasinoHandSpot { cards = new Transform[CasinoLayout.CardsPerHand] };
                hs.root = Node(seat, "Hand" + h, new Vector3(0, 0.001f, BjBet - BjHandR[h]), new Vector3(-BjHandTilt, 0, 0));
                for (int c = 0; c < hs.cards.Length; c++)
                    hs.cards[c] = Node(hs.root, "Card" + c, new Vector3((c - 2) * 0.052f, c * 0.0018f, 0), new Vector3(0, -(c - 2) * 3.5f, 0));
                hs.floatText = Node(seat, "HandFloat" + h, new Vector3(0, .24f, BjBet - BjHandR[h] + .02f));
                sp.handSpots[h] = hs;
            }
            sp.chipRack = ChipRack(seat, st, k, "ChipRack", new Vector3(0, 0, BjBet - BjRack), Vector3.zero, CasinoLayout.BlackjackChipCents, 0.078f);

            // Hit / Stand / Double / Split / Insurance, lit plates in a row at the edge of the felt
            string[] ids = CasinoLayout.BlackjackButtonIds;
            string[] shown = { "HIT", "STAND", "DOUBLE", "SPLIT", "INSURE" };
            sp.buttons = new CasinoButton[ids.Length];
            float lift = BjPlateH * .5f * Mathf.Sin(BjPlateTilt * Mathf.Deg2Rad) + .002f; // the front edge rests on the felt, the back edge rises toward the dealer's side
            for (int b = 0; b < ids.Length; b++)
                sp.buttons[b] = MakeButton(seat, st, k, ids[b], ids[b], shown[b], new Vector3((b - 2) * BjPlatePitch, lift, BjBet - BjBtn), new Vector3(-BjPlateTilt, 0, 0), new Vector2(BjPlateW, BjPlateH));

            // the numbered brass plate on the arm rest, and a dim gold numeral inside the bet circle
            var pn = Node(root, "Plate " + (k + 1), BjPoint(BjR - .055f, th, BjTop + .034f), new Vector3(0, -th, 0));
            Place(pn, "seat_plate", m => m.Box("seat_brass", new Vector3(0, .002f, 0), new Vector3(.07f, .004f, .042f)), Vector3.zero, 0, true).name = "Plate";
            Label(pn, "Number", (k + 1).ToString(), new Vector3(0, .0045f, 0), new Vector3(90, 0, 0), new Vector2(.04f, .03f), Ink2, FontStyles.Bold);
            sp.plate = pn;
            Label(seat, "Numeral", (k + 1).ToString(), new Vector3(0, .001f, 0), new Vector3(90, 0, 0), new Vector2(.09f, .09f), new Color(0.62f, 0.52f, 0.26f), FontStyles.Bold);
        }
        st.spots = spots;

        // the dealer's six card spots, tilted toward the players
        st.dealerSpots = new Transform[CasinoLayout.DealerCardSpots];
        for (int i = 0; i < st.dealerSpots.Length; i++)
            st.dealerSpots[i] = Node(root, "Dealer" + i, new Vector3((i - 2.5f) * 0.082f, BjTop + .004f + i * 0.0018f, BjZf - BjDealerRow), new Vector3(-BjDealerTilt, 0, 0));
        st.dealerFloatText = Node(root, "DealerFloatText", new Vector3(0, 2.0f, BjZf + .34f));

        // the older single-seat members keep pointing at the middle seat (the QA'd uGUI screen lays its cards there)
        st.seat = spots[2].stand; // legacy: where a player stands
        st.dealerCards = st.dealerSpots[0];
        st.playerHands = new[] { spots[2].handSpots[0].root, spots[2].handSpots[1].root, spots[2].handSpots[2].root };
        st.chipSpot = spots[2].betCircle;

        // the MIN / MAX sign with the countdown bulbs above it, on the dealer's right-hand end of the rail
        var stand = Node(root, "Sign stand", new Vector3(1.0f, BjTop + .03f, BjZf + .02f), new Vector3(48f, 0, 0)); // +X tips the top away from the players
        var sign = SignPanel(stand, "Limits sign", Vector3.zero, Vector3.zero, new Vector2(.40f, .17f), "BLACKJACK\n<size=70%>MIN $5   MAX $200</size>", AmberText);
        st.limitsText = sign;
        var light = AddLight(stand, "Timer glow", new Vector3(0, .32f, -.2f), C(0.4f, 1f, 0.5f), 0.25f, 1.0f);
        st.timerLamp = MakeTimerLamp(stand, new Vector3(0, .215f, -.012f), 12, .033f, .0125f, light);
        Place(stand, "timer_rail_bj", m => m.Box("seat_brass", Vector3.zero, new Vector3(.42f, .018f, .02f)), new Vector3(0, .215f, 0), 0, true).name = "Timer rail";
        st.timerLamp.SetValue(0f);
        BlackjackPrints(root, st);
        NoRecv(stand);
        for (int k = 0; k < N; k++) { NoRecv(root.Find("Seat frame " + (k + 1))); NoRecv(root.Find("Plate " + (k + 1))); }
    }
}
