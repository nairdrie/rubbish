// Setup_Casino, part 5: the blackjack table, the roulette table and the five slot machines, each with its CasinoStation
// and the transforms a screen animates (see CasinoStation's tooltips for the contract).
using System.Collections.Generic;
using TMPro;
using UnityEngine;

public static partial class Setup_Casino
{
    static readonly Color FeltText = new Color(0.86f, 0.78f, 0.42f), FeltTextDim = new Color(0.72f, 0.66f, 0.38f);

    static void ChipStack(MB d, Vector3 at, int count, string mat)
    {
        const float h = 0.0035f;
        d.Lathe(mat, at, new[] { V(0, 0), V(.02f, 0), V(.02f, count * h), V(0, count * h) }, 10);
        for (int i = 1; i < count; i += 2) d.Lathe("chip_white", at + Vector3.up * (i * h), new[] { V(.0203f, 0), V(.0203f, h * .5f) }, 10);
    }

    /// <summary>A lamp hung low over a table: a brass bar on two rods, green glass shades, a warm pool underneath.</summary>
    static void TableLamp(Transform parent, string name, Vector3 at, int shades, float spacing, float yaw)
    {
        var d = new MB();
        float y = 2.05f - H; // shade rim, relative to the ceiling
        float half = (shades - 1) * spacing * .5f;
        d.Tube("brass", new[] { new Vector3(-half - .12f, y + .42f, 0), new Vector3(half + .12f, y + .42f, 0) }, .014f, 8);
        foreach (float x in new[] { -half * .6f - .05f, half * .6f + .05f })
        {
            d.Tube("brass", new[] { new Vector3(x, y + .42f, 0), new Vector3(x, -.04f, 0) }, .008f, 6);
            d.Lathe("brass", new Vector3(x, -.05f, 0), new[] { V(0, 0), V(.03f, 0), V(.06f, .04f), V(.06f, .05f), V(0, .05f) }, 10);
        }
        for (int i = 0; i < shades; i++)
        {
            Vector3 p = new Vector3(-half + i * spacing, y, 0);
            d.Tube("brass", new[] { p + Vector3.up * .42f, p + Vector3.up * .26f }, .01f, 6);
            d.Lathe("lamp_green", p, new[] { V(.21f, 0), V(.2f, .03f), V(.12f, .16f), V(.05f, .24f), V(.03f, .27f) }, 16);
            d.Lathe("lamp_inside", p, new[] { V(.028f, .265f), V(.048f, .235f), V(.115f, .158f), V(.193f, .03f), V(.203f, .004f) }, 16);
            d.Torus("brass", p, .208f, .006f, 18, 5);
            d.Sphere("bulb", p + Vector3.up * .13f, new Vector3(.035f, .05f, .035f), 8, 5);
        }
        Emit(parent, name, d, new Vector3(at.x, H, at.z), yaw, true, false);
        // The light sits 0.3 m toward the players' side of the shades and a little softer than the felt alone would take:
        // straight above the table's middle it is half a metre from the dealer's head, which then blooms out to white.
        var l = AddLight(parent, name + " light", new Vector3(at.x, 2.12f, at.z - .3f), C(1f, 0.9f, 0.72f), 14f, 6f, true, 112f, true);
        l.transform.SetParent(parent, false);
    }

    // ------------------------------------------------------------------------------------------------ blackjack

    static void BuildBlackjack(Transform parent)
    {
        const float R = BjR, RF = BjRF, zf = BjZf, top = BjTop; // see Setup_Casino.Seats.cs
        var root = Node(parent, "Blackjack", BlackjackAt);
        List<Vector2> Half(float r, float zz, int n = 32)
        {
            var o = new List<Vector2>();
            for (int i = 0; i <= n; i++) { float a = Mathf.Lerp(180f, 360f, i / (float)n) * Mathf.Deg2Rad; o.Add(new Vector2(Mathf.Cos(a) * r, zz + Mathf.Sin(a) * r)); }
            return o;
        }
        var d = new MB { uvScale = 1.3f };
        d.Prism("felt_blackjack", "wood_red", Half(RF, zf), top - .03f, top, new Rect(-RF, zf - RF, RF * 2, RF));
        d.Prism("wood_red", "wood_red", Half(R, zf + .06f), top - .14f, top - .01f, null, true);
        // the padded arm rest round the players' side, a wooden rail along the dealer's
        // (a slimmer, lower roll than before: the old crest at 0.977 m hid the front of the action plates from the eye pose)
        d.Torus("leather_black", new Vector3(0, top - .018f, zf), R - .055f, .052f, 36, 10, new Vector3(0, 90, 0), 180f);
        d.grainAlong = true;
        d.Box("wood_red", new Vector3(0, top - .02f, zf + .045f), new Vector3(R * 2, .06f, .1f));
        d.grainAlong = false;
        // the base: a curved skirt on a plinth
        d.Prism("wood_dark", "wood_dark", Half(1.0f, zf - .12f, 24), .1f, top - .14f);
        d.Prism("rubber", "rubber", Half(.96f, zf - .14f, 24), 0f, .1f);
        for (int i = 0; i < 5; i++)
        {
            float a = Mathf.Lerp(200f, 340f, i / 4f) * Mathf.Deg2Rad;
            d.Box("wood_mid", new Vector3(Mathf.Cos(a) * 1.005f, .45f, zf - .12f + Mathf.Sin(a) * 1.005f), new Vector3(.36f, .5f, .02f), new Vector3(0, 90f - a * Mathf.Rad2Deg, 0));
        }
        // the dealer's kit: chip tray, shoe, discard holder
        d.Box("black_iron", new Vector3(0, top + .012f, zf - .1f), new Vector3(.62f, .024f, .15f));
        string[] chipMats = { "chip_white", "chip_red", "chip_green", "chip_black", "chip_blue", "chip_red", "chip_green", "chip_white" };
        for (int i = 0; i < 8; i++) ChipStack(d, new Vector3(-.245f + i * .07f, top + .024f, zf - .1f), 6 + (int)(Hash01(i, 12) * 12), chipMats[i]);
        d.Push(new Vector3(-.62f, top, zf - .2f), new Vector3(0, 140, 0));
        d.Box("black_iron", new Vector3(0, .05f, 0), new Vector3(.12f, .1f, .26f));
        d.Box("black_iron", new Vector3(0, .028f, .15f), new Vector3(.12f, .012f, .1f), new Vector3(24, 0, 0));
        d.Box("paper", new Vector3(0, .05f, .132f), new Vector3(.09f, .07f, .004f));
        d.Pop();
        d.Push(new Vector3(.66f, top, zf - .2f), new Vector3(0, -20, 0));
        d.Box("label_red", new Vector3(0, .004f, 0), new Vector3(.1f, .008f, .14f));
        d.Box("label_red", new Vector3(-.052f, .03f, 0), new Vector3(.004f, .06f, .14f)); d.Box("label_red", new Vector3(.052f, .03f, 0), new Vector3(.004f, .06f, .14f));
        d.Box("label_red", new Vector3(0, .03f, -.07f), new Vector3(.1f, .06f, .004f));
        d.Box("paper", new Vector3(0, .016f, 0), new Vector3(.064f, .016f, .09f));
        d.Pop();
        var table = Emit(root, "Table", d, default, 0, true, true);
        // collision follows the half disc: radial boxes every 15 degrees (each a little narrower than the chord), plus the flat edge
        for (int ci = -6; ci <= 6; ci++)
        {
            float th = ci * 15f * Mathf.Deg2Rad, len = R - .03f;
            ColBox(table.transform, "Col" + ci, new Vector3(Mathf.Sin(th) * len * .5f, .47f, zf - Mathf.Cos(th) * len * .5f), new Vector3(.36f, .94f, len), LayerDefault, 180f - ci * 15f);
        }
        ColBox(table.transform, "ColFlat", new Vector3(0, .47f, zf + .02f), new Vector3(R * 2 - .1f, .94f, .22f));

        TableLamp(parent, "Blackjack lamp", BlackjackAt + new Vector3(0, 0, -.05f), 2, .9f, 0);

        posts.Add(new CasinoStaff.Post { label = "Blackjack dealer", role = CasinoRole.BlackjackDealer, seed = 2,
            anchor = Node(root, "Post dealer", new Vector3(.02f, 0, zf + .34f), new Vector3(0, 180, 0)) });

        var st = Node(root, "Station Blackjack").gameObject.AddComponent<CasinoStation>();
        st.kind = CasinoStationKind.Blackjack; st.displayName = "Blackjack";
        st.view = LookNode(st.transform, "View", new Vector3(0, 2.05f, zf - R - .75f), new Vector3(0, top, zf - .4f));
        st.shoe = Node(st.transform, "Shoe", new Vector3(-.62f, top + .03f, zf - .2f) + Quaternion.Euler(0, 140, 0) * new Vector3(0, 0, .19f), new Vector3(0, 140, 0));
        st.discard = Node(st.transform, "Discard", new Vector3(.66f, top + .03f, zf - .2f), new Vector3(0, -20, 0));
        st.chipTray = Node(st.transform, "ChipTray", new Vector3(0, top + .05f, zf - .1f));
        BlackjackSeats(root, st); // seat mode: five seats with stools, racks, buttons; the dealer's spots; the sign and lamp; the printing
        Zone(st, new Vector3(0, 1.1f, zf - 1.55f), new Vector3(4.2f, 2.2f, 1.9f), 1.4f);
        stations.Add(st);
    }

    // ------------------------------------------------------------------------------------------------ roulette

    static void BuildRoulette(Transform parent)
    {
        const float hx = RlHx, hz = RlHz, top = RlTop;
        var root = Node(parent, "Roulette", RouletteAt);
        var d = new MB { uvScale = 1.3f };
        var felt = new List<Vector2> { new Vector2(-RlFeltX, -RlFeltZ), new Vector2(RlFeltX, -RlFeltZ), new Vector2(RlFeltX, RlFeltZ), new Vector2(-RlFeltX, RlFeltZ) };
        d.Prism("felt_roulette", "wood_red", felt, top - .03f, top, new Rect(-RlFeltX, -RlFeltZ, RlFeltX * 2, RlFeltZ * 2));
        // the rail: wood all round, padded leather on the players' long side
        d.grainAlong = true;
        d.Box("wood_red", new Vector3(0, top - .02f, hz - .05f), new Vector3(hx * 2, .1f, .1f));
        d.Box("leather_black", new Vector3(0, top - .01f, -hz + .05f), new Vector3(hx * 2, .11f, .11f));
        d.grainAlong = false;
        d.Box("wood_red", new Vector3(-hx + .05f, top - .02f, 0), new Vector3(.1f, .1f, hz * 2 - .2f));
        d.Box("wood_red", new Vector3(hx - .05f, top - .02f, 0), new Vector3(.1f, .1f, hz * 2 - .2f));
        d.Box("wood_dark", new Vector3(0, top - .14f, 0), new Vector3(hx * 2 - .06f, .16f, hz * 2 - .06f));
        // the player ledge along the near long side: a shelf for the chip racks and the plates (seat mode), padded where the arms rest
        d.grainAlong = true;
        d.Box("wood_red", new Vector3(0, top - .01f, -hz - RlLedge * .5f), new Vector3(hx * 2 + .06f, .08f, RlLedge));
        d.Box("leather_black", new Vector3(0, top + .0155f, -hz - RlLedge + .02f), new Vector3(hx * 2 + .06f, .031f, .04f));
        d.grainAlong = false;
        d.Box("wood_dark", new Vector3(0, top - .14f, -hz - RlLedge * .5f + .02f), new Vector3(hx * 2 - .06f, .16f, RlLedge - .06f));
        // two turned pedestals and a stretcher
        var leg = new[] { V(0, 0), V(.2f, 0), V(.2f, .04f), V(.1f, .08f), V(.07f, .16f), V(.11f, .26f), V(.12f, .36f), V(.07f, .46f), V(.06f, .56f), V(.1f, .64f), V(.13f, .66f), V(0, .66f) };
        d.Lathe("wood_dark", new Vector3(-.85f, 0, 0), leg, 14); d.Lathe("wood_dark", new Vector3(.85f, 0, 0), leg, 14);
        d.Box("wood_dark", new Vector3(0, .2f, 0), new Vector3(1.6f, .07f, .1f));
        // the bowl: a wooden dish with a sloped ball track, sunk a little into the table
        Vector3 wc = new Vector3(RlWheelX, top, 0);
        d.Lathe("wood_red", wc, new[] { V(.43f, 0), V(.445f, .03f), V(.44f, .105f), V(.425f, .115f), V(.405f, .105f), V(.39f, .07f), V(.31f, .035f), V(.285f, .015f), V(.285f, .004f), V(0, .004f) }, 40);
        d.Torus("brass", wc + Vector3.up * .112f, .425f, .006f, 40, 5);
        for (int i = 0; i < 8; i++) // the diamonds (deflectors) on the track
        {
            float a = (i * 45f + 11f) * Mathf.Deg2Rad;
            d.Push(wc + new Vector3(Mathf.Sin(a) * .345f, .055f, Mathf.Cos(a) * .345f), new Vector3(0, a * Mathf.Rad2Deg + (i % 2) * 90f, 0));
            d.Sphere("brass", Vector3.zero, new Vector3(.008f, .006f, .022f), 6, 3);
            d.Pop();
        }
        // the dealer's working stacks, a marker
        string[] cm = { "chip_red", "chip_green", "chip_black", "chip_blue", "chip_white", "chip_red" };
        for (int i = 0; i < 6; i++) for (int r = 0; r < 2; r++)
            ChipStack(d, new Vector3(.25f + i * .05f, top, .36f + r * .05f), 20 - r * 6 - (int)(Hash01(i, r) * 4), cm[(i + r) % cm.Length]);
        d.Lathe("brass", new Vector3(.75f, top, .38f), new[] { V(0, 0), V(.022f, 0), V(.018f, .012f), V(.008f, .02f), V(.008f, .05f), V(.014f, .058f), V(0, .066f) }, 10);
        var table = Emit(root, "Table", d, default, 0, true, true);
        ColBox(table.transform, "Col", new Vector3(0, .46f, -RlLedge * .5f), new Vector3(hx * 2 + .06f, .93f, hz * 2 + RlLedge));

        // the wheel: a fixed centre node, the rotor (spins about Y) and the ball
        var wheel = Node(root, "Wheel", wc + Vector3.up * .012f);
        var rm = new MB();
        const float RR = 0.28f;
        rm.Disc("wheel", new Vector3(0, .012f, 0), RR, 76);
        rm.Lathe("wood_red", Vector3.zero, new[] { V(RR, .012f), V(RR, 0), V(0, 0) }, 38);
        int N = CasinoStation.WheelOrder.Length;
        for (int k = 0; k < N; k++) // pocket separators
        {
            float a = (k + .5f) * 360f / N;
            rm.Push(Vector3.zero, new Vector3(0, a, 0)); rm.Box("brass", new Vector3(0, .02f, RR * .695f), new Vector3(.003f, .016f, RR * .205f)); rm.Pop();
        }
        rm.Torus("brass", new Vector3(0, .02f, 0), RR * .59f, .004f, 38, 5); rm.Torus("brass", new Vector3(0, .02f, 0), RR * .80f, .003f, 38, 5);
        rm.Lathe("wood_red", new Vector3(0, .012f, 0), new[] { V(RR * .585f, 0), V(.09f, .03f), V(.04f, .04f) }, 30);
        rm.Lathe("brass", new Vector3(0, .05f, 0), new[] { V(.045f, 0), V(.05f, .01f), V(.022f, .03f), V(.014f, .09f), V(.03f, .1f), V(.03f, .115f), V(.012f, .13f), V(.014f, .15f), V(0, .16f) }, 14);
        for (int i = 0; i < 4; i++) { float a = i * 90f * Mathf.Deg2Rad; rm.Tube("brass", new[] { new Vector3(0, .115f, 0), new Vector3(Mathf.Sin(a) * .075f, .112f, Mathf.Cos(a) * .075f) }, .006f, 6); rm.Sphere("brass", new Vector3(Mathf.Sin(a) * .08f, .112f, Mathf.Cos(a) * .08f), Vector3.one * .011f, 6, 4); }
        var rotor = Emit(wheel, "WheelRotor", rm, default, 0, false, false);
        for (int k = 0; k < N; k++)
        {
            int num = CasinoStation.WheelOrder[k];
            float a = k * 360f / N, ar = a * Mathf.Deg2Rad;
            Label(rotor.transform, "N" + (num == 37 ? "00" : num.ToString()), num == 37 ? "00" : num.ToString(),
                new Vector3(Mathf.Sin(ar) * RR * .9f, .0135f, Mathf.Cos(ar) * RR * .9f), new Vector3(90, a, 0), new Vector2(.034f, .04f), new Color(0.82f, 0.8f, 0.72f));
        }
        var bm = new MB(); bm.Sphere("ball", Vector3.zero, Vector3.one * .0105f, 10, 6);
        var ball = Emit(wheel, "Ball", bm, new Vector3(0, .03f, RR * .695f), 0, false, false);

        // the layout's lettering (flat, read from the players' side)
        var print = Node(root, "Print");
        Color num0 = new Color(0.95f, 0.92f, 0.82f);
        for (int n = 1; n <= 36; n++)
            Label(print, "L" + n, n.ToString(), new Vector3(RlGridX0 + ((n - 1) / 3 + .5f) * RlCellX, top + .002f, RlGridZ0 + ((n - 1) % 3 + .5f) * RlCellZ), new Vector3(90, 0, 0), new Vector2(.094f, .1f), num0);
        float hm = RlGridZ0 + 1.5f * RlCellZ;
        Label(print, "L0", "0", new Vector3(RlGridX0 - RlCellX * .5f, top + .002f, RlGridZ0 + .75f * RlCellZ), new Vector3(90, 0, 0), new Vector2(.11f, .12f), num0);
        Label(print, "L00", "00", new Vector3(RlGridX0 - RlCellX * .5f, top + .002f, hm + .75f * RlCellZ), new Vector3(90, 0, 0), new Vector2(.11f, .12f), num0);
        for (int r = 0; r < 3; r++)
            Label(print, "Col" + r, "2 to 1", new Vector3(RlGridX0 + 12.5f * RlCellX, top + .002f, RlGridZ0 + (r + .5f) * RlCellZ), new Vector3(90, 90, 0), new Vector2(.16f, .075f), FeltText);
        string[] doz = { "1st 12", "2nd 12", "3rd 12" };
        for (int i = 0; i < 3; i++)
            Label(print, "Doz" + i, doz[i], new Vector3(RlGridX0 + (i * 4 + 2) * RlCellX, top + .002f, RlGridZ0 - RlOutside * .5f), new Vector3(90, 0, 0), new Vector2(.42f, .075f), FeltText);
        string[] even = { "1 to 18", "EVEN", "", "", "ODD", "19 to 36" };
        for (int i = 0; i < 6; i++)
            if (even[i].Length > 0)
                Label(print, "Even" + i, even[i], new Vector3(RlGridX0 + (i * 2 + 1) * RlCellX, top + .002f, RlGridZ0 - RlOutside * 1.5f), new Vector3(90, 0, 0), new Vector2(.23f, .075f), FeltText);

        TableLamp(parent, "Roulette lamp", RouletteAt + new Vector3(.1f, 0, 0), 3, .85f, 0);
        posts.Add(new CasinoStaff.Post { label = "Roulette dealer", role = CasinoRole.RouletteDealer, seed = 5,
            anchor = Node(root, "Post dealer", new Vector3(-.3f, 0, hz + .3f), new Vector3(0, 180, 0)) });

        var st = Node(root, "Station Roulette").gameObject.AddComponent<CasinoStation>();
        st.kind = CasinoStationKind.Roulette; st.displayName = "Roulette";
        st.view = LookNode(st.transform, "View", new Vector3(.15f, 2.3f, -hz - 1.2f), new Vector3(.15f, top, -.02f));
        st.wheelCentre = wheel; st.wheelRotor = rotor.transform; st.ball = ball.transform;
        st.ballTrackRadius = 0.375f; st.ballTrackHeight = 0.075f; st.ballPocketRadius = RR * .695f; st.ballPocketHeight = 0.03f;
        st.layoutOrigin = Node(st.transform, "LayoutOrigin", new Vector3(RlGridX0, top, RlGridZ0));
        st.layoutCell = new Vector2(RlCellX, RlCellZ);
        st.chipTray = Node(st.transform, "ChipTray", new Vector3(.4f, top + .07f, .38f));
        RouletteSeats(root, st, wheel); // seat mode: four spots with racks and plates, the board, the plaque, the dolly, the pick plane
        Zone(st, new Vector3(.47f, 1.1f, RlStandZ + .1f), new Vector3(3.0f, 2.2f, 1.5f), 1.4f);
        stations.Add(st);
    }

    // ------------------------------------------------------------------------------------------------ slots

    const float ReelR = 0.16f, ReelW = 0.125f, ReelY = 1.2f, ReelZ = 0.05f;
    /// <summary>The topper is raised this far to make room for the lit pay table plate between the reel window and the marquee.</summary>
    const float TopLift = 0.30f;

    /// <summary>A reel drum: axis X, ReelFaces faces; face k is centred at angle k * 360/N above the front.</summary>
    static void ReelDrum(MB d, string mat)
    {
        int N = CasinoLayout.ReelFaces; const int sub = 3;
        float step = 360f / N;
        for (int k = 0; k < N; k++)
            for (int s = 0; s < sub; s++)
            {
                float a0 = ((k - .5f) + s / (float)sub) * step * Mathf.Deg2Rad, a1 = ((k - .5f) + (s + 1) / (float)sub) * step * Mathf.Deg2Rad;
                Vector3 n0 = new Vector3(0, Mathf.Sin(a0), Mathf.Cos(a0)), n1 = new Vector3(0, Mathf.Sin(a1), Mathf.Cos(a1));
                float v0 = 1f - (k + 1f) / N + (s / (float)sub) / N, v1 = 1f - (k + 1f) / N + ((s + 1) / (float)sub) / N;
                Vector3 l = Vector3.left * (ReelW * .5f), r = Vector3.right * (ReelW * .5f);
                // seen from the front (+Z): left edge is +X? The viewer stands at +Z looking -Z, so their left is +X.
                d.QuadSmooth(mat, r + n0 * ReelR, r + n1 * ReelR, l + n1 * ReelR, l + n0 * ReelR, n0, n1, n1, n0,
                    new Vector2(0, v0), new Vector2(0, v1), new Vector2(1, v1), new Vector2(1, v0));
            }
        d.Lathe("black_iron", Vector3.left * (ReelW * .5f), new[] { V(ReelR, 0), V(0, 0) }, 16, new Vector3(0, 0, 90));
        d.Lathe("black_iron", Vector3.right * (ReelW * .5f), new[] { V(0, 0), V(ReelR, 0) }, 16, new Vector3(0, 0, 90));
    }

    static void BuildSlots(Transform parent)
    {
        var group = Node(parent, "Slots");
        for (int i = 0; i < CasinoLayout.SlotCount; i++)
        {
            string cab = "cabinet_" + i, name = CasinoLayout.SlotName(i);
            var root = Node(group, "Slot " + i + " " + name, new Vector3(SlotX, 0, SlotZ0 + i * SlotStep), new Vector3(0, 90, 0));
            var d = new MB();
            // lower cabinet, the button deck, the upper cabinet with the reel window, the topper
            d.Box(cab, new Vector3(0, .375f, 0), new Vector3(.62f, .75f, .56f));
            d.Box("black_iron", new Vector3(0, .03f, .0f), new Vector3(.64f, .06f, .58f));
            d.Box("chrome", new Vector3(0, .42f, .283f), new Vector3(.5f, .46f, .012f));                    // belly frame
            d.Box("chrome", new Vector3(0, .13f, .35f), new Vector3(.44f, .012f, .16f));                     // coin tray
            d.Box("chrome", new Vector3(0, .155f, .43f), new Vector3(.44f, .06f, .012f));
            d.Box("chrome", new Vector3(-.22f, .155f, .35f), new Vector3(.012f, .06f, .16f)); d.Box("chrome", new Vector3(.22f, .155f, .35f), new Vector3(.012f, .06f, .16f));
            d.Box("black_iron", new Vector3(0, .2f, .284f), new Vector3(.3f, .06f, .01f));                   // the payout chute
            d.Box("chrome", new Vector3(0, .79f, .06f), new Vector3(.64f, .08f, .7f), new Vector3(12, 0, 0)); // the deck
            // (the three deck buttons are CasinoButtons now: see SlotSeat)
            d.Box("black_iron", new Vector3(.24f, .835f, .2f), new Vector3(.03f, .004f, .07f), new Vector3(12, 0, 0)); // coin slot
            // upper cabinet as a frame round the window (the reels sit inside)
            const float wy = ReelY, wh = .25f, ww = .46f;
            d.Box(cab, new Vector3(0, (.83f + wy - wh * .5f) * .5f, -.03f), new Vector3(.62f, wy - wh * .5f - .83f, .5f));
            d.Box(cab, new Vector3(0, (wy + wh * .5f + 1.55f + TopLift) * .5f, -.03f), new Vector3(.62f, 1.55f + TopLift - wy - wh * .5f, .5f));
            d.Box(cab, new Vector3(-.27f, wy, -.03f), new Vector3(.08f, wh, .5f)); d.Box(cab, new Vector3(.27f, wy, -.03f), new Vector3(.08f, wh, .5f));
            d.Box("ink", new Vector3(0, wy, -.2f), new Vector3(ww, wh, .02f));                               // the back of the reel box
            d.Box("ink", new Vector3(-.0725f, wy, .1f), new Vector3(.02f, wh, .24f)); d.Box("ink", new Vector3(.0725f, wy, .1f), new Vector3(.02f, wh, .24f));
            foreach (float yy in new[] { wy + wh * .5f + .012f, wy - wh * .5f - .012f }) d.Box("chrome_satin", new Vector3(0, yy, .222f), new Vector3(ww + .05f, .024f, .014f));
            foreach (float xx in new[] { -ww * .5f - .012f, ww * .5f + .012f }) d.Box("chrome_satin", new Vector3(xx, wy, .222f), new Vector3(.024f, wh, .014f));
            d.Box("payline", new Vector3(0, wy, .226f), new Vector3(ww, .004f, .004f));
            d.Box("chrome", new Vector3(-.315f, .9f + TopLift * .5f, .0f), new Vector3(.012f, 1.3f + TopLift, .04f)); d.Box("chrome", new Vector3(.315f, .9f + TopLift * .5f, .0f), new Vector3(.012f, 1.3f + TopLift, .04f));
            // the marquee topper: a deep lit box, crowned with the machine's own emblem
            d.Push(Vector3.up * TopLift);
            d.Box("black_iron", new Vector3(0, 1.81f, .0f), new Vector3(.74f, .52f, .28f));
            d.Box("chrome", new Vector3(0, 2.08f, .0f), new Vector3(.78f, .025f, .32f));
            d.Box("chrome", new Vector3(0, 1.555f, .01f), new Vector3(.78f, .02f, .32f));
            d.Pop();
            d.Box("button_lit", new Vector3(0, .735f, .285f), new Vector3(.5f, .022f, .008f));            // a lit strip under the deck
            for (int k = 0; k < 5; k++) d.Lathe("brass", new Vector3(-.12f + k * .06f + (k % 2) * .01f, .137f, .31f + (k % 3) * .02f), new[] { V(0, 0), V(.017f, 0), V(.017f, .004f), V(0, .004f) }, 8, new Vector3(k * 7f, 0, k * 5f));
            Vector3 cr = new Vector3(0, 2.09f, 0);
            d.Push(Vector3.up * TopLift);
            switch (i)
            {
                case 0: // three fat sevens
                    for (int k = -1; k <= 1; k++)
                    {
                        d.Push(cr + new Vector3(k * .23f, 0, 0));
                        d.Box("payline", new Vector3(0, .25f, 0), new Vector3(.18f, .05f, .06f));
                        d.Box("payline", new Vector3(-.015f, .12f, 0), new Vector3(.055f, .27f, .06f), new Vector3(0, 0, 22f));
                        d.Pop();
                    }
                    break;
                case 1: // a raccoon peering over the top: head, ears, the mask, two bright eyes
                    d.Sphere("fur", cr + new Vector3(0, .13f, 0), new Vector3(.21f, .16f, .14f), 14, 8);
                    foreach (int sgn in new[] { -1, 1 })
                    {
                        d.Lathe("fur", cr + new Vector3(sgn * .15f, .2f, 0), new[] { V(0, 0), V(.06f, 0), V(.02f, .1f), V(0, .12f) }, 8, new Vector3(0, 0, -sgn * 18f));
                        d.Sphere("ink", cr + new Vector3(sgn * .085f, .15f, .1f), new Vector3(.085f, .05f, .05f), 10, 6);
                        d.Sphere("topper_1", cr + new Vector3(sgn * .085f, .152f, .135f), new Vector3(.028f, .028f, .02f), 8, 5);
                    }
                    d.Sphere("paper", cr + new Vector3(0, .08f, .11f), new Vector3(.07f, .05f, .05f), 8, 5);
                    d.Sphere("ink", cr + new Vector3(0, .095f, .155f), Vector3.one * .02f, 6, 4);
                    break;
                case 2: // the gnome: a tall red hat over a nose and a beard
                    d.Lathe("label_red", cr + new Vector3(0, .12f, 0), new[] { V(0, 0), V(.17f, 0), V(.15f, .08f), V(.05f, .34f), V(.015f, .42f), V(0, .44f) }, 14);
                    d.Sphere("paper", cr + new Vector3(0, .06f, .05f), new Vector3(.15f, .11f, .09f), 10, 6);
                    d.Sphere("topper_2", cr + new Vector3(0, .12f, .13f), Vector3.one * .04f, 8, 5);
                    d.Torus("brass", cr + new Vector3(0, .125f, 0), .165f, .014f, 18, 6);
                    break;
                case 3: // a dumpster on fire
                    d.Box("cabinet_2", cr + new Vector3(0, .07f, 0), new Vector3(.42f, .14f, .2f));
                    d.Box("black_iron", cr + new Vector3(0, .145f, 0), new Vector3(.45f, .02f, .22f));
                    for (int k = 0; k < 7; k++)
                    {
                        float fh = .14f + ((k * 5) % 4) * .06f;
                        d.Lathe(k % 2 == 0 ? "flame" : "topper_3", cr + new Vector3(-.18f + k * .06f, .15f, (k % 3 - 1) * .04f), new[] { V(0, 0), V(.035f, 0), V(.05f, fh * .35f), V(.02f, fh * .8f), V(0, fh) }, 8);
                    }
                    break;
                default: // the mystery sack, tied at the neck
                    d.Lathe("sack", cr, new[] { V(0, 0), V(.13f, 0), V(.19f, .08f), V(.19f, .2f), V(.1f, .3f), V(.045f, .33f), V(.045f, .345f), V(.09f, .4f), V(.07f, .42f), V(0, .4f) }, 14);
                    d.Torus("topper_4", cr + new Vector3(0, .337f, 0), .05f, .012f, 12, 6);
                    break;
            }
            d.Pop();
            if (i == 3) for (int k = 0; k < 6; k++) d.Box(k % 2 == 0 ? "topper_3" : "ink", new Vector3(-.26f + k * .104f, .7f, .283f), new Vector3(.1f, .06f, .006f));
            var body = Emit(root, "Cabinet", d, default, 0, true, true);
            // On the walls layer: the cabinets are head-high and stand against the wall, so the third-person camera must
            // stop at them like it does at the wall (it only collides with that layer), not sink into the row.
            ColBox(body.transform, "Col", new Vector3(0, 1.05f + TopLift * .5f, 0), new Vector3(.74f, 2.1f + TopLift, .6f), LayerWalls);

            var bm = new MB(); bm.Panel("belly_" + i, new Vector3(0, .42f, .291f), Vector3.forward, .46f, .42f);
            var belly = Emit(root, "Belly", bm, default, 0, false);
            var tm = new MB(); tm.Box("marq_" + i, new Vector3(0, 1.81f + TopLift, .142f), new Vector3(.68f, .46f, .012f));
            var topper = Emit(root, "Topper", tm, default, 0, false);
            // the name, big, in the machine's bright colour (TMP is unlit: it reads as lit lettering)
            int cut = name.LastIndexOf(' ');
            string two = cut > 0 ? name.Substring(0, cut) + "\n" + name.Substring(cut + 1) : name;
            Color ac = SlotAccent[i];
            var nameLabel = Label(root, "Name", two.ToUpperInvariant(), new Vector3(0, 1.81f + TopLift, .1495f), new Vector3(0, 180, 0), new Vector2(.58f, .36f), new Color(Mathf.Min(1f, ac.r * 1.05f), Mathf.Min(1f, ac.g * 1.05f), Mathf.Min(1f, ac.b * 1.05f)), FontStyles.Bold);
            nameLabel.lineSpacing = -18f; nameLabel.ForceMeshUpdate();
            if (i == 4) Label(root, "Sack mark", "?", new Vector3(0, 2.24f + TopLift, .195f), new Vector3(0, 180, 0), new Vector2(.2f, .2f), ac, FontStyles.Bold);
            var ta = new MB(); var tb = new MB(); int tn = 0;
            Bulbs(ta, tb, RectPath(new Vector3(0, 1.81f + TopLift, .15f), .325f, .215f, .09f), .014f, ref tn);
            var tga = Emit(root, "Topper bulbs A", ta, default, 0, false); var tgb = Emit(root, "Topper bulbs B", tb, default, 0, false);
            var tch = tga.AddComponent<CasinoNeon>(); tch.mode = CasinoNeon.Mode.Chase; tch.renderers = new[] { tga.GetComponent<Renderer>(), tgb.GetComponent<Renderer>() };
            tch.emission = C(2.6f, 1.8f, 0.7f); tch.chaseRate = 1.5f + i * .2f; tch.seed = 30 + i;

            var reels = new Transform[3];
            for (int r = 0; r < 3; r++)
            {
                // The player faces the machine's -Z, so their left is local +X: reel 0 goes there, like the screen's left reel.
                var reel = Place(root, "reel_" + i, m => ReelDrum(m, "reel_" + i), new Vector3(.145f - r * .145f, ReelY, ReelZ), 0, false);
                reel.name = "Reel" + r;
                reel.transform.localRotation = Quaternion.Euler((r * 5 + i * 3) % CasinoLayout.ReelFaces * (360f / CasinoLayout.ReelFaces), 0, 0);
                reels[r] = reel.transform;
            }
            var lm = new MB();
            // on the player's right (local -X), where the screen draws it
            lm.Tube("chrome", new[] { Vector3.zero, new Vector3(-.05f, 0, 0), new Vector3(-.05f, .42f, .0f) }, .014f, 8);
            lm.Sphere("knob_" + i, new Vector3(-.05f, .45f, 0), Vector3.one * .042f, 12, 7);
            var lever = Emit(root, "Lever", lm, new Vector3(-.32f, .98f, .08f), 0, false);
            var hub = new MB(); hub.Lathe("chrome", new Vector3(-.31f, .98f, .08f), new[] { V(0, 0), V(.045f, 0), V(.045f, .03f), V(0, .035f) }, 12, new Vector3(0, 0, 90));
            Emit(root, "Lever hub", hub);

            var st = root.gameObject.AddComponent<CasinoStation>();
            st.kind = CasinoStationKind.Slot; st.index = i; st.displayName = name;
            st.seat = Node(root, "Seat", new Vector3(0, 0, .95f), new Vector3(0, 180, 0));
            st.view = LookNode(root, "View", new Vector3(.02f, 1.42f, 1.15f), new Vector3(0, 1.12f, .2f));
            st.reels = reels; st.lever = lever.transform;
            st.coinTray = Node(root, "CoinTray", new Vector3(0, .15f, .33f));
            st.glow = new[] { belly.GetComponent<Renderer>(), topper.GetComponent<Renderer>() };
            Zone(st, new Vector3(0, 1.1f, .95f), new Vector3(1.05f, 2.2f, 1.2f), 1.4f);
            SlotSeat(root, st, i, lever.GetComponent<Renderer>()); // seat mode: pay plate, deck buttons, lever pick, tray light, the spot
            stations.Add(st);
        }
        // (three lights high up over the row, not one at head height in front of it: a point light 0.5 m from a player's eye washed the
        // chrome and the pay plate out when the camera sits at the machine)
        foreach (float lz in new[] { SlotZ0 - .6f, SlotZ0 + 2 * SlotStep, SlotZ0 + 4 * SlotStep + .6f })
            AddLight(group, "Slots glow", new Vector3(-8.7f, 2.75f, lz), C(1f, 0.82f, 0.62f), 2.6f, 5.5f);

        // SLOTS in red tube above the row
        var neon = new MB();
        Vector3 nc = new Vector3(-HX + .08f, 2.72f, SlotZ0 + 2 * SlotStep);
        NeonText(neon, "neon_red", "SLOTS", nc, Vector3.forward, Vector3.up, .42f, .017f);
        var red = Emit(group, "Neon SLOTS", neon, default, 0, false);
        var bulbsA = new MB(); var bulbsB = new MB();
        float nw = NeonWidth("SLOTS", .42f) * .5f + .22f, nh = .36f;
        // an oval of marquee bulbs round the word
        for (int k = 0; k < 30; k++)
        {
            float a = k * 12f * Mathf.Deg2Rad;
            (k % 2 == 0 ? bulbsA : bulbsB).Sphere("bulb", nc + new Vector3(0, Mathf.Sin(a) * nh, Mathf.Cos(a) * nw), Vector3.one * .022f, 6, 4);
        }
        var back = new MB(); back.Box("front_dark", nc + new Vector3(-.05f, 0, 0), new Vector3(.03f, nh * 2 + .14f, nw * 2 + .14f));
        Emit(group, "Neon backing", back);
        var a0 = Emit(group, "Bulbs A", bulbsA, default, 0, false); var b0 = Emit(group, "Bulbs B", bulbsB, default, 0, false);
        var glow = AddLight(group, "Neon SLOTS light", nc + new Vector3(.6f, -.2f, 0), C(1f, 0.3f, 0.24f), 2.2f, 4.5f);
        var cn = red.AddComponent<CasinoNeon>(); cn.renderers = new[] { red.GetComponent<Renderer>() }; cn.emission = C(2.7f, 0.2f, 0.14f); cn.glow = glow; cn.glowIntensity = 2.2f; cn.flicker = .6f; cn.seed = 2;
        var ch = a0.AddComponent<CasinoNeon>(); ch.mode = CasinoNeon.Mode.Chase; ch.renderers = new[] { a0.GetComponent<Renderer>(), b0.GetComponent<Renderer>() }; ch.emission = C(2.6f, 1.8f, 0.7f); ch.chaseRate = 2f;
    }
}
