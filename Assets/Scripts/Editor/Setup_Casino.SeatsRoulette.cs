// Setup_Casino, part 7: the roulette table's four player spots (chip racks and Ready / Undo / Clear / Rebet plates on a player
// ledge), the result board with its countdown bulbs, the win marker (the dolly), the limits plaque and the layout's pick plane.
using TMPro;
using UnityEngine;

public static partial class Setup_Casino
{
    const float RlHx = 1.45f, RlHz = 0.65f, RlTop = 0.88f;
    /// <summary>The player ledge along the near long side: z from -RlHz to -(RlHz + RlLedge).</summary>
    const float RlLedge = 0.26f, RlLedgeTop = RlTop + 0.03f;
    static readonly float[] RlSpotX = { -0.40f, 0.18f, 0.76f, 1.34f };
    const float RlStandZ = -1.21f;
    /// <summary>On the player ledge (spot frame z): the action plates on the felt side and the chip rack on the player side, both tilted toward the eye (the rack on a shallow slant).</summary>
    const float RlRackZ = -0.010f, RlPlateZ = 0.095f, RlPlateH = 0.062f, RlPlateTilt = 28f, RlRackTilt = 14f;

    static void RouletteSeats(Transform root, CasinoStation st, Transform wheel)
    {
        const int N = CasinoLayout.RouletteSpots;
        var spots = new CasinoSpot[N];
        Vector3 layoutCentre = new Vector3(0.35f, RlTop, -0.1f);
        for (int i = 0; i < N; i++)
        {
            float x = RlSpotX[i];
            var sp = new CasinoSpot { label = "Spot " + (i + 1), eyeFov = 64f, leanFov = 50f, watchFov = 52f, lookYaw = 85f, lookPitchMin = -75f, lookPitchMax = 40f };
            spots[i] = sp;
            sp.stand = Node(root, "Stand " + (i + 1), new Vector3(x, 0, RlStandZ));
            // about 30 degrees down, from the body's own position: the layout fills the middle and the rack and plates sit above the hint strip
            Vector3 aimEye = new Vector3(x + (layoutCentre.x - x) * 0.5f, RlTop, -0.70f);
            sp.eye = LookNode(root, "Eye " + (i + 1), new Vector3(x, CasinoLayout.EyeHeight, RlStandZ), aimEye);
            Vector3 aimLean = new Vector3(x + (layoutCentre.x - x) * 0.55f, RlTop, -0.05f);
            sp.lean = LookNode(root, "Lean " + (i + 1), new Vector3(x, 1.36f, -0.88f), aimLean);
            sp.watch = LookNode(root, "Watch " + (i + 1), new Vector3(x, CasinoLayout.EyeHeight, RlStandZ), new Vector3(RlWheelX, RlTop + .04f, 0f));
            sp.floatText = Node(root, "FloatText " + (i + 1), new Vector3(x, 1.9f, RlStandZ));

            var frame = Node(root, "Spot frame " + (i + 1), new Vector3(x, RlLedgeTop + .004f, -0.79f));
            sp.chipRack = ChipRack(frame, st, i, "ChipRack", new Vector3(0, .014f, RlRackZ), new Vector3(-RlRackTilt, 0, 0), CasinoLayout.RouletteChipCents, 0.078f);
            sp.betCircle = sp.chipRack.Length > 0 ? sp.chipRack[0].parent : frame;
            string[] ids = CasinoLayout.RouletteButtonIds; // Ready, Undo, Clear, Rebet
            string[] shown = { "READY", "UNDO", "CLEAR", "REBET" };
            float[] bx = { 0.200f, -0.200f, -0.105f, -0.010f };
            sp.buttons = new CasinoButton[ids.Length];
            float lift = RlPlateH * .5f * Mathf.Sin(RlPlateTilt * Mathf.Deg2Rad) + .002f; // front edge on the ledge, back edge up toward the felt
            for (int b = 0; b < ids.Length; b++)
                sp.buttons[b] = MakeButton(frame, st, i, ids[b], ids[b], shown[b], new Vector3(bx[b], lift, RlPlateZ), new Vector3(-RlPlateTilt, 0, 0), b == 0 ? new Vector2(.128f, RlPlateH) : new Vector2(.088f, RlPlateH));
        }
        st.spots = spots;
        st.dealerFloatText = Node(root, "DealerFloatText", new Vector3(-0.3f, 2.0f, RlHz + .3f));

        // the result board (last twelve, newest first) on the dealer's far end, with the countdown bulbs along its top
        var board = Node(root, "Result board", new Vector3(1.0f, RlTop + .03f, 0.50f), new Vector3(52f, 0, 0));
        const float bw = .88f, bh = .40f;
        Place(board, "result_board_frame", m =>
        {
            m.Box("sign_panel", new Vector3(0, bh * .5f, 0), new Vector3(bw, bh, .014f));
            m.Box("seat_brass", new Vector3(0, bh - .007f, -.004f), new Vector3(bw + .014f, .014f, .024f));
            m.Box("seat_brass", new Vector3(0, .007f, -.004f), new Vector3(bw + .014f, .014f, .024f));
            m.Box("seat_brass", new Vector3(-bw * .5f - .002f, bh * .5f, -.004f), new Vector3(.014f, bh, .024f));
            m.Box("seat_brass", new Vector3(bw * .5f + .002f, bh * .5f, -.004f), new Vector3(.014f, bh, .024f));
            m.Box("black_iron", new Vector3(0, bh * .3f, .035f), new Vector3(bw * .5f, bh * .6f, .04f));
        }, Vector3.zero, 0, true).name = "Frame";
        var rb = board.gameObject.AddComponent<CasinoResultBoard>();
        const int cols = 6;
        rb.discs = new Renderer[CasinoLayout.ResultHistory];
        rb.numbers = new TMP_Text[CasinoLayout.ResultHistory];
        for (int k = 0; k < CasinoLayout.ResultHistory; k++)
        {
            int c = k % cols, r = k / cols;
            var at = new Vector3((c - (cols - 1) * .5f) * 0.136f, 0.215f - r * 0.125f, -.009f);
            var disc = Place(board, "result_disc", m => m.Lathe("result_disc", Vector3.zero, new[] { V(0, 0), V(.052f, 0), V(.052f, .006f), V(.045f, .009f), V(0, .009f) }, 24, new Vector3(-90, 0, 0)), at, 0, false);
            disc.name = "Disc" + k;
            rb.discs[k] = disc.GetComponent<Renderer>();
            rb.numbers[k] = Label(board, "Num" + k, "", at + new Vector3(0, 0, -.011f), Vector3.zero, new Vector2(.085f, .068f), Color.white, FontStyles.Bold);
        }
        rb.Clear();
        st.resultBoard = rb;
        var glow = AddLight(board, "Timer glow", new Vector3(0, bh + .12f, -.2f), C(0.4f, 1f, 0.5f), 0.25f, 1.0f);
        st.timerLamp = MakeTimerLamp(board, new Vector3(0, bh - .05f, -.014f), 12, .066f, .0135f, glow);
        st.timerLamp.SetValue(0f);

        // the limits plaque: brass, lying on the felt at the dealer's end, tipped toward the players
        var plaque = SignPanel(root, "Limits plaque", new Vector3(-0.14f, RlTop + .004f, 0.46f), new Vector3(58f, 0, 0), new Vector2(.74f, .125f),
            "MIN $1   INSIDE MAX $100\nOUTSIDE MAX $500   SPIN MAX $1,000", Ink2, "seat_brass");
        st.limitsText = plaque;

        // the dolly: a brass pawn that stands on the winning number
        var marker = Node(st.transform, "WinMarker", new Vector3(0, RlTop, 0));
        Place(marker, "win_pawn", m => m.Lathe("win_marker", Vector3.zero, new[] { V(0, 0), V(.034f, 0), V(.034f, .008f), V(.014f, .028f), V(.009f, .09f), V(.024f, .102f), V(.024f, .118f), V(0, .13f) }, 16), Vector3.zero, 0, false).name = "Pawn";
        marker.gameObject.SetActive(false);
        st.winMarker = marker;

        // the layout's pick plane (cell units: u -1..13, v -2*outside..3)
        var plane = Node(st.transform, "LayoutPick", Vector3.zero);
        plane.gameObject.layer = CasinoLayout.PickLayer;
        float outside = st.layoutOutside;
        var bc = plane.gameObject.AddComponent<BoxCollider>();
        bc.isTrigger = true;
        bc.size = new Vector3(14f * RlCellX, .012f, (3f + 2f * outside) * RlCellZ);
        plane.localPosition = new Vector3(RlGridX0 + 6f * RlCellX, RlTop + .004f, RlGridZ0 + (3f - 2f * outside) * .5f * RlCellZ);
        var pk = plane.gameObject.AddComponent<CasinoPickable>();
        pk.kind = CasinoPickKind.Layout; pk.station = st; pk.spot = -1; pk.id = "Layout"; pk.hint = "";
        st.layoutPlane = bc;

        NoRecv(board); NoRecv(plaque.transform.parent);
        for (int i = 0; i < N; i++) NoRecv(root.Find("Spot frame " + (i + 1)));
        st.seat = spots[1].stand;
        st.chipSpot = Node(st.transform, "ChipSpot", new Vector3(RlSpotX[1], RlLedgeTop + .004f, -0.79f));
    }
}
