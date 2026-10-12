// Setup_Casino, part 8: the slot machines' seat mode (pay table plate, deck buttons, lever pick, coin tray light, the one spot)
// and the bar's three spots with a drinks menu card in front of each.
using TMPro;
using UnityEngine;

public static partial class Setup_Casino
{
    /// <summary>Text with a fixed size (not auto-sized), so a line the controller fills later never changes size.</summary>
    static TextMeshPro FixedText(Transform parent, string name, string text, Vector3 localPos, Vector3 euler, Vector2 rect, Color color, float fontSize, TextAlignmentOptions align, FontStyles style = FontStyles.Bold)
    {
        var go = new GameObject(name); go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos; go.transform.localRotation = Quaternion.Euler(euler);
        var t = go.AddComponent<TextMeshPro>();
        t.enableAutoSizing = false; t.fontSize = fontSize; t.fontStyle = style; t.color = color; t.alignment = align;
        t.textWrappingMode = TextWrappingModes.NoWrap; t.overflowMode = TextOverflowModes.Overflow;
        t.rectTransform.sizeDelta = rect; t.text = text; t.ForceMeshUpdate();
        var r = go.GetComponent<MeshRenderer>(); r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
        return t;
    }

    // ------------------------------------------------------------------------------------------------ slots

    /// <summary>Slot machine i in seat mode. <paramref name="lift"/> is how far the topper was raised to make room for the plate.</summary>
    static void SlotSeat(Transform root, CasinoStation st, int i, Renderer leverRenderer)
    {
        Color accent = SlotAccent[i];

        // The pay table plate on the front glass, between the reel window and the topper (readable and lit, controller-filled)
        const float py = 1.60f, pz = .226f;
        var plate = Node(root, "PayPlate", new Vector3(0, py, pz));
        Place(plate, "payplate_frame", m =>
        {
            m.Box("chrome_satin", new Vector3(0, 0, .002f), new Vector3(.43f, .51f, .014f));
            m.Box("plate_glass", new Vector3(0, 0, .0105f), new Vector3(.385f, .465f, .006f));
            m.Box("seat_brass", new Vector3(0, .2f, .0145f), new Vector3(.36f, .004f, .002f)); // rule under the title
            m.Box("seat_brass", new Vector3(0, -.158f, .0145f), new Vector3(.36f, .004f, .002f)); // rule above the returns line
        }, Vector3.zero, 0, true).name = "Frame";
        var pp = plate.gameObject.AddComponent<CasinoPayPlate>();
        Vector3 faceEuler = new Vector3(0, 180, 0);
        const float zt = .0158f;
        pp.title = FixedText(plate, "Title", "PAY TABLE", new Vector3(0, .222f, zt), faceEuler, new Vector2(.36f, .034f), new Color(Mathf.Min(1f, accent.r * 1.1f), Mathf.Min(1f, accent.g * 1.1f), Mathf.Min(1f, accent.b * 1.1f)), 0.24f, TextAlignmentOptions.Center);
        pp.left = new TMP_Text[CasinoPayPlate.Lines]; pp.right = new TMP_Text[CasinoPayPlate.Lines];
        for (int l = 0; l < CasinoPayPlate.Lines; l++)
        {
            float y = .172f - l * .0222f;
            pp.left[l] = FixedText(plate, "L" + l, "", new Vector3(0, y, zt), faceEuler, new Vector2(.35f, .021f), new Color(0.95f, 0.92f, 0.8f), 0.17f, TextAlignmentOptions.Left, FontStyles.Bold);
            pp.right[l] = FixedText(plate, "R" + l, "", new Vector3(0, y, zt), faceEuler, new Vector2(.35f, .021f), new Color(1f, 0.82f, 0.3f), 0.17f, TextAlignmentOptions.Right, FontStyles.Bold);
        }
        pp.returnsLine = FixedText(plate, "Returns", "RETURNS --.-%", new Vector3(0, -.1845f, zt), faceEuler, new Vector2(.36f, .026f), new Color(0.5f, 0.95f, 0.6f), 0.19f, TextAlignmentOptions.Center);
        pp.stakeLine = FixedText(plate, "Stake", "STAKE $-", new Vector3(0, -.2125f, zt), faceEuler, new Vector2(.36f, .024f), new Color(0.95f, 0.92f, 0.8f), 0.17f, TextAlignmentOptions.Center, FontStyles.Normal);
        st.payPlate = pp;
        NoRecv(plate);

        // three deck buttons (player's left = local +X: Bet -, centre Spin, right Bet +), tilted with the deck, text reading toward the machine
        Vector3 deck = new Vector3(0, 0.789f, .262f), tilt = new Vector3(-12f, 180f, 0f);
        st.betDownButton = MakeButton(root, st, 0, "BetDown", "Bet -", "BET -", deck + new Vector3(.155f, 0, 0), tilt, new Vector2(.078f, .078f), true, "Lower the bet");
        st.spinButton = MakeButton(root, st, 0, "Spin", "Spin", "SPIN", deck, tilt, new Vector2(.104f, .104f), true, "Spin");
        st.betUpButton = MakeButton(root, st, 0, "BetUp", "Bet +", "BET +", deck + new Vector3(-.155f, 0, 0), tilt, new Vector2(.078f, .078f), true, "Raise the bet");
        st.spinButton.SetLit(true);

        // the lever: a pick box that rides the arm and knob
        var lp = AddPick(st.lever, "Pick", new Vector3(-.05f, .26f, 0), new Vector3(.13f, .56f, .13f), st, 0, CasinoPickKind.Lever, 0, "Lever", "Pull the lever");
        lp.hoverRenderers = new[] { leverRenderer };
        lp.hoverEmission = new Color(0.9f, 0.7f, 0.2f);
        st.leverPick = lp;

        // a lit coin tray and a soft light over it for payouts
        var tm = new MB();
        tm.Box("tray_glow", new Vector3(0, .1365f, .35f), new Vector3(.40f, .004f, .12f)); // (a dim strip: the light below does the glow)
        Emit(root, "Tray glow", tm, default, 0, true);
        var tl = AddLight(root, "Coin tray light", new Vector3(0, .30f, .5f), C(1f, 0.8f, 0.45f), 0.5f, 1.3f);
        st.coinTrayLight = tl;

        // the spot
        var sp = new CasinoSpot { label = "Machine " + (i + 1), eyeFov = 58f, leanFov = 42f, watchFov = 50f, lookYaw = 50f, lookPitchMin = -70f, lookPitchMax = 42f };
        sp.stand = st.seat;
        sp.eye = LookNode(root, "Eye", new Vector3(0, CasinoLayout.EyeHeight, .78f), new Vector3(0, 1.2f, .05f));
        sp.lean = LookNode(root, "Lean", new Vector3(0, 1.22f, .52f), new Vector3(0, 1.2f, .05f));
        sp.watch = LookNode(root, "Watch", new Vector3(0, CasinoLayout.EyeHeight, .78f), new Vector3(0, py, pz)); // the pay table
        sp.floatText = Node(root, "FloatText", new Vector3(0, 2.0f, .95f));
        sp.buttons = new[] { st.spinButton, st.betUpButton, st.betDownButton };
        st.spots = new[] { sp };
    }

    // ------------------------------------------------------------------------------------------------ bar

    static readonly float[] BarSpotZ = { -1.0f, 0.6f, 2.2f };
    static readonly string[] BarDefaultNames = { "Flat Beer", "House Whisky", "Dumpster Fire", "Glass of Water" };
    static readonly string[] BarDefaultPrices = { "$6", "$10", "$14", "$1" };
    static readonly string[] BarDefaultEffects = { "+0.12 drunk", "+0.20 drunk", "+0.30 drunk", "sobers 0.10" };

    static void BarSeats(Transform bar, CasinoStation st)
    {
        const int N = CasinoLayout.BarSpots;
        var spots = new CasinoSpot[N];
        for (int i = 0; i < N; i++)
        {
            float z = BarSpotZ[i];
            var sp = new CasinoSpot { label = "Bar " + (i + 1), eyeFov = 60f, leanFov = 46f, watchFov = 52f, lookYaw = 62f, lookPitchMin = -70f, lookPitchMax = 35f };
            spots[i] = sp;
            sp.stand = Node(bar, "Bar stand " + (i + 1), new Vector3(BarX - .75f, 0, z), new Vector3(0, 90, 0));
            sp.eye = LookNode(bar, "Bar eye " + (i + 1), new Vector3(BarX - .68f, CasinoLayout.EyeHeight, z), new Vector3(BarX - .21f, 1.17f, z - .33f)); // the menu card (0.5 m wide, centred 0.36 m to the right of the spot) fills the right-centre of the view, whole
            sp.lean = LookNode(bar, "Bar lean " + (i + 1), new Vector3(BarX - .50f, 1.20f, z - .06f), new Vector3(BarX - .20f, 1.17f, z - .34f));
            sp.watch = LookNode(bar, "Bar watch " + (i + 1), new Vector3(BarX - .66f, CasinoLayout.EyeHeight, z), new Vector3(BarX + 1.05f, 1.32f, 0.7f));
            sp.floatText = Node(bar, "Bar FloatText " + (i + 1), new Vector3(BarX - .75f, 1.9f, z));
            sp.counterSpot = Node(bar, "Counter " + (i + 1), new Vector3(BarX - .12f, 1.1f, z), new Vector3(0, 90, 0));
            sp.pourSpot = Node(bar, "Pour " + (i + 1), new Vector3(BarX + .2f, 1.1f, z + .1f), new Vector3(0, 90, 0));
            BarMenuCard(bar, st, sp, i, new Vector3(BarX - .21f, 1.09f, z - .36f));
        }
        st.spots = spots;
        st.seat = spots[1].stand; // legacy: where the old screen's player stood (the middle spot)
        st.counterSpot = spots[1].counterSpot; st.pourSpot = spots[1].pourSpot;
    }

    /// <summary>A brass-framed menu card leaning on the counter in front of a bar spot: four drink rows (hover / click), each with a
    /// name, a price and an effect line.</summary>
    static void BarMenuCard(Transform bar, CasinoStation st, CasinoSpot sp, int spotIndex, Vector3 at)
    {
        const float w = .50f, h = .27f;
        var card = Node(bar, "Menu card " + (spotIndex + 1), at, new Vector3(30f, 90f, 0f)); // face toward -X (the customers), leaning back 30 degrees
        Place(card, "menu_card_frame", m =>
        {
            m.Box("board_black", new Vector3(0, h * .5f, 0), new Vector3(w, h, .014f));
            m.Box("seat_brass", new Vector3(0, h - .006f, -.004f), new Vector3(w + .012f, .012f, .022f));
            m.Box("seat_brass", new Vector3(0, .006f, -.004f), new Vector3(w + .012f, .012f, .022f));
            m.Box("seat_brass", new Vector3(-w * .5f - .002f, h * .5f, -.004f), new Vector3(.012f, h, .022f));
            m.Box("seat_brass", new Vector3(w * .5f + .002f, h * .5f, -.004f), new Vector3(.012f, h, .022f));
            m.Box("wood_dark", new Vector3(0, .02f, .055f), new Vector3(w * .6f, .04f, .09f)); // the foot
        }, Vector3.zero, 0, true).name = "Frame";
        FixedText(card, "Header", "THE LUCKY RACCOON  -  DRINKS", new Vector3(0, h - .024f, -.0085f), Vector3.zero, new Vector2(w - .04f, .022f), new Color(0.95f, 0.8f, 0.4f), 0.17f, TextAlignmentOptions.Center);
        sp.drinkSlots = new CasinoDrinkSlot[CasinoLayout.DrinkSlots];
        for (int r = 0; r < sp.drinkSlots.Length; r++)
        {
            float y = h - .074f - r * .0535f;
            var go = new GameObject("Drink" + r) { layer = CasinoLayout.PickLayer };
            go.transform.SetParent(card, false);
            go.transform.localPosition = new Vector3(0, y, -.008f);
            var row = Place(go.transform, "drink_row_bar", m => m.Box("drink_row", Vector3.zero, new Vector3(w - .03f, .05f, .004f)), Vector3.zero, 0, false);
            row.name = "Row";
            var col = go.AddComponent<BoxCollider>(); col.isTrigger = true; col.size = new Vector3(w - .02f, .052f, .06f); col.center = new Vector3(0, 0, -.02f);
            var b = go.AddComponent<CasinoButton>();
            b.kind = CasinoPickKind.DrinkSlot; b.station = st; b.spot = spotIndex; b.index = r; b.id = "Drink" + r; b.label = BarDefaultNames[r]; b.hint = BarDefaultNames[r];
            b.lit = new[] { row.GetComponent<Renderer>() };
            b.litColor = new Color(0.62f, 0.34f, 0.07f); b.dimColor = new Color(0.16f, 0.08f, 0.02f); b.disabledColor = new Color(0.03f, 0.03f, 0.03f);
            b.litBase = new Color(0.26f, 0.17f, 0.07f); b.dimBase = new Color(0.14f, 0.11f, 0.08f); b.disabledBase = new Color(0.08f, 0.08f, 0.08f);
            b.hoverEmission = new Color(1.1f, 0.65f, 0.15f);
            b.pressDepth = .004f; b.pressAxisZ = true; b.cap = row.transform;
            var d = new CasinoDrinkSlot { button = b };
            d.nameText = FixedText(go.transform, "Name", BarDefaultNames[r], new Vector3(-.0f, .008f, -.0035f), Vector3.zero, new Vector2(w - .04f, .026f), new Color(0.98f, 0.95f, 0.85f), 0.25f, TextAlignmentOptions.Left);
            d.priceText = FixedText(go.transform, "Price", BarDefaultPrices[r], new Vector3(0, .008f, -.0035f), Vector3.zero, new Vector2(w - .04f, .026f), new Color(1f, 0.95f, 0.72f), 0.25f, TextAlignmentOptions.Right);
            d.effectText = FixedText(go.transform, "Effect", BarDefaultEffects[r], new Vector3(0, -.014f, -.0035f), Vector3.zero, new Vector2(w - .04f, .018f), new Color(0.96f, 0.9f, 0.76f), 0.16f, TextAlignmentOptions.Left, FontStyles.Italic);
            b.labelText = d.nameText;
            sp.drinkSlots[r] = d;
        }
        NoRecv(card);
    }
}
