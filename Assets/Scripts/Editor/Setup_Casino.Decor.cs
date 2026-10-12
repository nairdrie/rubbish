// Setup_Casino, part 6: doors, pictures, lamps, the jukebox, loose furniture, the haze, and the street front prefab.
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEngine;

public static partial class Setup_Casino
{
    /// <summary>A node on a wall: 'N','S','E','W', the WORLD-axis coordinate along it (x for N/S, z for E/W), height.
    /// Its local +Z points into the room, +X to the right as you look at the wall from inside... mirrored for nobody:
    /// children are modelled facing +Z.</summary>
    static Transform OnWall(Transform parent, string name, char wall, float along, float y, float proud = 0f)
    {
        Vector3 p; float yaw;
        switch (wall)
        {
            case 'N': p = new Vector3(along, y, HZ - proud); yaw = YawN; break;
            case 'S': p = new Vector3(along, y, -HZ + proud); yaw = YawS; break;
            case 'E': p = new Vector3(HX - proud, y, along); yaw = YawE; break;
            default: p = new Vector3(-HX + proud, y, along); yaw = YawW; break;
        }
        return Node(parent, name, p, new Vector3(0, yaw, 0));
    }

    static void DoorLeaf(MB d, float w, float h, string wood = "wood_red")
    {
        // frame, leaf with four raised panels, a brass kick plate
        d.grainAlong = false;
        d.Box("wood_dark", new Vector3(-w * .5f - .06f, h * .5f, .035f), new Vector3(.12f, h + .12f, .07f));
        d.Box("wood_dark", new Vector3(w * .5f + .06f, h * .5f, .035f), new Vector3(.12f, h + .12f, .07f));
        d.Box("wood_dark", new Vector3(0, h + .06f, .035f), new Vector3(w + .24f, .12f, .07f));
        d.Box(wood, new Vector3(0, h * .5f, .02f), new Vector3(w, h, .04f));
        foreach (float px in new[] { -w * .24f, w * .24f })
        {
            d.Box("wood_mid", new Vector3(px, h * .72f, .045f), new Vector3(w * .36f, h * .38f, .014f));
            d.Box("wood_mid", new Vector3(px, h * .28f, .045f), new Vector3(w * .36f, h * .3f, .014f));
        }
        d.Box("brass", new Vector3(0, .1f, .043f), new Vector3(w - .04f, .18f, .006f));
    }

    static void BuildDoors(Transform parent)
    {
        // EXIT: the way back to the street, in the south wall
        var exit = OnWall(parent, "Exit door", 'S', CasinoLayout.ExitDoorLocal.x, 0f);
        var d = new MB { uvScale = 1.2f };
        DoorLeaf(d, 1.1f, 2.1f);
        d.Tube("brass", new[] { new Vector3(-.42f, 1.02f, .06f), new Vector3(-.42f, 1.02f, .11f), new Vector3(.42f, 1.02f, .11f), new Vector3(.42f, 1.02f, .06f) }, .014f, 6);
        d.Box("black_iron", new Vector3(0, 2.4f, .05f), new Vector3(.62f, .26f, .1f));
        Emit(exit, "Door", d, default, 0, true, true);
        var sign = new MB(); sign.Box("exit_green", new Vector3(0, 2.4f, .102f), new Vector3(.56f, .2f, .006f));
        Emit(exit, "Exit sign", sign);
        Label(exit, "Exit text", "EXIT", new Vector3(0, 2.4f, .108f), new Vector3(0, 180, 0), new Vector2(.5f, .17f), new Color(0.92f, 1f, 0.92f), FontStyles.Bold, 8f);
        AddLight(exit, "Exit light", new Vector3(0, 2.3f, .5f), C(0.35f, 1f, 0.5f), 1.3f, 2.8f);
        var door = exit.gameObject.AddComponent<CasinoDoor>(); door.exit = true;
        var zone = exit.gameObject.AddComponent<InteractionZone>();
        zone.center = new Vector3(0, 1.1f, .85f); zone.size = new Vector3(2f, 2.8f, 1.7f); zone.mode = ZoneMode.OnFoot; zone.score = 1.5f; zone.targetOverride = door;
        // a worn mat inside the door
        var mat = new MB(); mat.Box("rubber", new Vector3(0, .008f, .75f), new Vector3(1.3f, .016f, .8f));
        Emit(exit, "Mat", mat);

        // the restrooms: it does not open
        var wc = OnWall(parent, "Restroom door", 'N', 5.9f, 0f);
        var w = new MB { uvScale = 1.2f };
        DoorLeaf(w, .9f, 2.05f, "wood_mid");
        w.Sphere("brass", new Vector3(.34f, 1.0f, .07f), Vector3.one * .03f, 8, 5);
        w.Box("brass", new Vector3(0, 1.62f, .046f), new Vector3(.5f, .11f, .006f));
        Emit(wc, "Door", w, default, 0, true, true);
        Label(wc, "Plaque", "LADIES & GENTS", new Vector3(0, 1.62f, .051f), new Vector3(0, 180, 0), new Vector2(.46f, .07f), new Color(0.12f, 0.08f, 0.03f), FontStyles.Bold, 4f);
        // and one marked PRIVATE behind the bar
        var pr = OnWall(parent, "Private door", 'N', 9.8f, 0f);
        var pm = new MB { uvScale = 1.2f }; DoorLeaf(pm, .85f, 2.05f, "wood_mid"); pm.Sphere("brass", new Vector3(-.32f, 1.0f, .07f), Vector3.one * .03f, 8, 5);
        pm.Box("brass", new Vector3(0, 1.62f, .046f), new Vector3(.36f, .09f, .006f));
        Emit(pr, "Door", pm, default, 0, true, true);
        Label(pr, "Plaque", "PRIVATE", new Vector3(0, 1.62f, .051f), new Vector3(0, 180, 0), new Vector2(.32f, .06f), new Color(0.12f, 0.08f, 0.03f), FontStyles.Bold, 6f);
    }

    static void Picture(Transform parent, char wall, float along, float y, int poster, float w, float h, string frame = "gilt")
    {
        var n = OnWall(parent, "Picture " + poster, wall, along, y, 0.03f);
        Place(n, "frame_" + poster + "_" + frame + "_" + Mathf.RoundToInt(w * 100), m => Frame(m, "poster_" + poster, w, h, frame), Vector3.zero);
        if (poster == 3)
        {
            Label(n, "Caption top", "THE LUCKY RACCOON", new Vector3(0, h * .39f, .02f), new Vector3(0, 180, 0), new Vector2(w * .8f, h * .09f), new Color(0.6f, 0.47f, 0.2f), FontStyles.Bold, 4f);
            Label(n, "Caption bottom", "SINCE 1962", new Vector3(0, -h * .36f, .02f), new Vector3(0, 180, 0), new Vector2(w * .6f, h * .08f), new Color(0.6f, 0.47f, 0.2f), FontStyles.Bold, 10f);
        }
    }

    static void BuildDecor(Transform parent)
    {
        var decor = Node(parent, "Decor");
        // pictures, hung in clusters the way forty years of regulars' gifts pile up: (wall, along, height, poster, width, dark frame)
        var pics = new (char, float, float, int, float, bool)[]
        {
            ('N', -6.3f, 2.0f, 0, .62f, false), ('N', -5.2f, 2.3f, 5, .44f, true), ('N', -5.2f, 1.55f, 4, .34f, true), ('N', -4.5f, 2.5f, 1, .26f, true), ('N', -4.45f, 1.95f, 2, .3f, false),
            ('N', -1.0f, 2.05f, 3, .7f, false), ('N', -0.1f, 2.5f, 5, .28f, true), ('N', -0.05f, 1.8f, 0, .34f, true), ('N', 0.95f, 2.15f, 1, .5f, true), ('N', 1.65f, 2.55f, 4, .26f, false), ('N', 1.62f, 1.75f, 2, .28f, true),
            ('N', 3.3f, 2.05f, 2, .6f, false), ('N', 4.1f, 2.5f, 0, .3f, true), ('N', 4.05f, 1.75f, 5, .3f, true), ('N', 7.0f, 2.45f, 4, .3f, true), ('N', 8.55f, 2.3f, 1, .34f, true),
            ('S', -9.5f, 2.3f, 5, .3f, true), ('S', -8.7f, 2.0f, 2, .5f, true), ('S', -7.9f, 2.45f, 4, .28f, false), ('S', -2.4f, 2.0f, 5, .62f, false), ('S', -1.5f, 2.35f, 1, .34f, true), ('S', -1.5f, 1.7f, 0, .34f, true),
            ('S', -0.75f, 2.1f, 2, .3f, false), ('S', 0.95f, 2.5f, 4, .28f, true), ('S', 1.7f, 2.05f, 3, .56f, false), ('S', 2.45f, 2.5f, 1, .28f, true), ('S', 2.5f, 1.85f, 5, .3f, true),
            ('S', 4.4f, 2.1f, 0, .7f, false), ('S', 5.3f, 2.55f, 3, .28f, true), ('S', 5.35f, 1.9f, 2, .3f, true), ('S', 6.15f, 2.1f, 4, .44f, true), ('S', 6.95f, 2.5f, 1, .26f, false), ('S', 6.95f, 1.95f, 0, .3f, true),
            ('W', -5.3f, 2.0f, 3, .44f, false), ('W', 3.55f, 2.35f, 4, .3f, true), ('W', 4.4f, 2.05f, 5, .56f, false), ('W', 5.2f, 2.4f, 1, .3f, true),
            ('E', -3.75f, 2.4f, 2, .3f, true), ('E', -5.5f, 2.3f, 0, .32f, true), ('E', -5.5f, 1.7f, 5, .3f, false),
        };
        foreach (var pc in pics) Picture(decor, pc.Item1, pc.Item2, pc.Item3, pc.Item4, pc.Item5, pc.Item5 * 1.5f, pc.Item6 ? "wood_dark" : "gilt");

        // two bar mirrors with gilt lettering (brands nobody has brewed in decades), and the price board
        BarMirror(decor, 'N', 7.75f, 1.95f, 1.0f, .7f, "RUSTY BUCKET", "LAGER", "Brewed downwind since 1962");
        BarMirror(decor, 'S', 9.0f, 2.0f, 1.1f, .75f, "OLD DUMPSTER", "EXTRA STOUT", "It grows on you");
        var pb = OnWall(decor, "Price board", 'E', -4.6f, 1.95f, 0.03f);
        var pbm = new MB();
        pbm.Box("board_black", new Vector3(0, 0, .012f), new Vector3(.86f, 1.1f, .024f));
        foreach (float yy in new[] { -.58f, .58f }) pbm.Box("wood_mid", new Vector3(0, yy, .02f), new Vector3(.98f, .06f, .04f));
        foreach (float xx in new[] { -.46f, .46f }) pbm.Box("wood_mid", new Vector3(xx, 0, .02f), new Vector3(.06f, 1.1f, .04f));
        Emit(pb, "Board", pbm);
        Color chalk = new Color(0.88f, 0.86f, 0.78f), chalkDim = new Color(0.62f, 0.6f, 0.52f);
        Label(pb, "Head", "DRINKS", new Vector3(0, .42f, .026f), new Vector3(0, 180, 0), new Vector2(.6f, .13f), new Color(0.9f, 0.7f, 0.3f), FontStyles.Bold, 14f);
        string[] drinks = { "FLAT BEER", "HOUSE WHISKY", "DUMPSTER FIRE", "GLASS OF WATER" }; string[] prices = { "$6", "$10", "$14", "$1" };
        for (int k = 0; k < drinks.Length; k++)
        {
            var dl = Label(pb, "Drink " + k, drinks[k], new Vector3(.07f, .22f - k * .15f, .026f), new Vector3(0, 180, 0), new Vector2(.56f, .075f), chalk, FontStyles.Bold);
            dl.alignment = TextAlignmentOptions.Left;
            var pl = Label(pb, "Price " + k, prices[k], new Vector3(-.3f, .22f - k * .15f, .026f), new Vector3(0, 180, 0), new Vector2(.16f, .075f), chalk, FontStyles.Bold);
            pl.alignment = TextAlignmentOptions.Right;
        }
        Label(pb, "Foot", "NO TABS  -  NO REFUNDS", new Vector3(0, -.44f, .026f), new Vector3(0, 180, 0), new Vector2(.7f, .05f), chalkDim, FontStyles.Italic);

        // the clock, stopped years ago
        Place(OnWall(decor, "Clock", 'N', -2.6f, 2.5f, 0.03f), "clock", Clock, Vector3.zero);

        // sconces (every one glows; four of them really light the wall)
        var sc = new[] { ('N', -7.5f, true), ('N', -3.8f, false), ('N', 2.3f, true), ('N', 4.8f, false), ('S', -7.2f, false), ('S', -3.3f, true), ('S', 0.2f, false), ('S', 3.0f, true), ('S', 7.6f, true), ('W', 5.8f, false), ('E', -4.1f, false) };
        for (int i = 0; i < sc.Length; i++)
        {
            var n = OnWall(decor, "Sconce " + i, sc[i].Item1, sc[i].Item2, 1.95f, 0.03f);
            Place(n, "sconce", Sconce, Vector3.zero);
            if (sc[i].Item3) AddLight(n, "Light", new Vector3(0, .2f, .35f), C(1f, 0.76f, 0.5f), 3.4f, 4.6f);
        }

        // the jukebox in the north-west corner: an arched cabinet with glowing tubes
        var jb = OnWall(decor, "Jukebox", 'N', -9.9f, 0f, 0.02f);
        var j = new MB { uvScale = 1.5f };
        var arch = new List<Vector2> { new Vector2(-.4f, 0), new Vector2(.4f, 0), new Vector2(.4f, 1.0f) };
        for (int i = 1; i < 12; i++) { float a = i * 15f * Mathf.Deg2Rad; arch.Add(new Vector2(Mathf.Cos(a) * .4f, 1.0f + Mathf.Sin(a) * .42f)); }
        arch.Add(new Vector2(-.4f, 1.0f));
        j.Push(new Vector3(0, 0, .28f), new Vector3(-90, 0, 0)); j.Prism("wood_red", "wood_red", arch, -.28f, .28f, null, true); j.Pop();
        var tubeOut = new List<Vector3>(); var tubeIn = new List<Vector3>();
        for (int i = 0; i <= 12; i++) { float a = i * 15f * Mathf.Deg2Rad; tubeOut.Add(new Vector3(Mathf.Cos(a) * .34f, 1.0f + Mathf.Sin(a) * .36f, .575f)); tubeIn.Add(new Vector3(Mathf.Cos(a) * .27f, 1.0f + Mathf.Sin(a) * .29f, .575f)); }
        tubeOut.Insert(0, new Vector3(.34f, .2f, .575f)); tubeOut.Add(new Vector3(-.34f, .2f, .575f));
        tubeIn.Insert(0, new Vector3(.27f, .62f, .575f)); tubeIn.Add(new Vector3(-.27f, .62f, .575f));
        j.Box("chrome", new Vector3(0, .36f, .565f), new Vector3(.46f, .4f, .012f));
        for (int i = 0; i < 7; i++) j.Box("black_iron", new Vector3(-.18f + i * .06f, .36f, .573f), new Vector3(.014f, .34f, .006f));
        j.Box("lamp_inside", new Vector3(0, .98f, .562f), new Vector3(.44f, .5f, .01f));
        j.Box("ink", new Vector3(0, .82f, .57f), new Vector3(.4f, .1f, .012f));
        for (int i = 0; i < 8; i++) j.Box(i % 2 == 0 ? "label_cream" : "label_red", new Vector3(-.175f + i * .05f, .82f, .578f), new Vector3(.04f, .07f, .004f));
        j.Lathe("ink", new Vector3(0, 1.08f, .57f), new[] { V(0, 0), V(.15f, 0), V(.15f, .006f), V(.045f, .008f), V(0, .008f) }, 24, new Vector3(90, 0, 0));
        j.Lathe("label_red", new Vector3(0, 1.08f, .579f), new[] { V(0, 0), V(.045f, 0), V(0, .002f) }, 12, new Vector3(90, 0, 0));
        var jbody = Emit(jb, "Cabinet", j, default, 0, true, true);
        ColBox(jbody.transform, "Col", new Vector3(0, .75f, .28f), new Vector3(.84f, 1.5f, .6f));
        var jt = new MB(); jt.Tube("neon_amber", tubeOut, .018f, 6); var jtg = new MB(); jtg.Tube("neon_green", tubeIn, .014f, 6);
        var t1 = Emit(jb, "Tubes amber", jt, default, 0, false); var t2 = Emit(jb, "Tubes green", jtg, default, 0, false);
        var jl = AddLight(jb, "Jukebox glow", new Vector3(0, 1.0f, 1.0f), C(1f, 0.78f, 0.45f), 2.4f, 3.6f);
        var jn = t1.AddComponent<CasinoNeon>(); jn.renderers = new[] { t1.GetComponent<Renderer>() }; jn.emission = C(2.5f, 1.1f, 0.12f); jn.flicker = .2f; jn.glow = jl; jn.glowIntensity = 2.4f; jn.seed = 6;
        var jg = t2.AddComponent<CasinoNeon>(); jg.renderers = new[] { t2.GetComponent<Renderer>() }; jg.emission = C(0.25f, 2.2f, 0.45f); jg.flicker = .7f; jg.seed = 12;

        // the cigarette machine beside it
        var cg = OnWall(decor, "Cigarette machine", 'N', -8.6f, 0f, 0.02f);
        var c = new MB();
        c.Box("cabinet_1", new Vector3(0, .82f, .2f), new Vector3(.72f, 1.5f, .4f));
        c.Box("black_iron", new Vector3(0, .04f, .2f), new Vector3(.66f, .08f, .36f));
        c.Box("lamp_inside", new Vector3(0, 1.36f, .402f), new Vector3(.62f, .28f, .008f));
        c.Box("mirror", new Vector3(0, 1.0f, .402f), new Vector3(.62f, .34f, .008f));
        for (int i = 0; i < 8; i++)
        {
            c.Box(i % 3 == 0 ? "label_red" : i % 3 == 1 ? "label_cream" : "bottle_blue", new Vector3(-.266f + i * .076f, 1.0f, .408f), new Vector3(.06f, .26f, .006f));
            c.Lathe("chrome", new Vector3(-.266f + i * .076f, .74f, .4f), new[] { V(0, 0), V(.016f, 0), V(.02f, .03f), V(0, .04f) }, 8, new Vector3(90, 0, 0));
        }
        c.Box("chrome", new Vector3(0, .42f, .41f), new Vector3(.5f, .1f, .03f));
        c.Box("chrome", new Vector3(.27f, 1.36f, .41f), new Vector3(.03f, .12f, .01f));
        var cgb = Emit(cg, "Cabinet", c, default, 0, true, true);
        ColBox(cgb.transform, "Col", new Vector3(0, .8f, .2f), new Vector3(.74f, 1.6f, .42f));
        Label(cg, "Sign", "CIGARETTES", new Vector3(0, 1.36f, .409f), new Vector3(0, 180, 0), new Vector2(.5f, .12f), new Color(0.45f, 0.08f, 0.05f), FontStyles.Bold, 4f);
        posts.Add(new CasinoStaff.Post { label = "Regular by the jukebox", role = CasinoRole.Regular, pose = CasinoRegularPose.Leaning, seed = 16,
            anchor = Node(decor, "Post regular leaning", new Vector3(-8.0f, 0, 4.85f), new Vector3(0, 200, 0)) });

        // two small round tables, their chairs, what was left on them
        var tables = new[] { new Vector3(-1.5f, 0, -3.6f), new Vector3(-6.1f, 0, -1.5f), new Vector3(1.5f, 0, -1.5f) };
        for (int i = 0; i < tables.Length; i++)
        {
            var tn = Node(decor, "Round table " + i, tables[i]);
            var tb = Place(tn, "round_table", RoundTableRecipe, Vector3.zero, 0, true, true);
            ColCapsule(tb.transform, "Col", new Vector3(0, .4f, 0), .3f, .8f);
            float[] yaws = i == 0 ? new[] { 20f, 150f, 265f } : i == 1 ? new[] { 75f, 200f } : new[] { 130f, 250f, 10f };
            for (int k = 0; k < yaws.Length; k++)
            {
                Vector3 dir = Quaternion.Euler(0, yaws[k], 0) * Vector3.forward;
                Place(tn, "chair", ChairRecipe, dir * .72f, yaws[k] + 180f + (k * 13 - 9), true, true);
                if (k == 0 && i == 0)
                    posts.Add(new CasinoStaff.Post { label = "Regular at table " + i, role = CasinoRole.Regular, pose = CasinoRegularPose.TableSitter, seated = true, seed = 21 + i * 11,
                        anchor = Node(tn, "Post regular", dir * .72f + Vector3.up * ChairSeat, new Vector3(0, yaws[k] + 180f, 0)) });
            }
            Place(tn, "pendant_amber_low", m => Pendant(m, 1.05f, "lamp_amber"), new Vector3(0, H - .16f, 0));
            AddLight(tn, "Pendant light", new Vector3(0, H - 1.35f, 0), C(1f, 0.74f, 0.46f), 6f, 4.8f);
            Place(tn, "ashtray", AshtrayRecipe, new Vector3(.08f, .75f, .1f), i * 70f);
            Place(tn, "tumbler", TumblerRecipe, new Vector3(-.16f, .75f, -.05f));
            if (i == 0) { var bm = new MB(); Bottle(bm, new Vector3(.12f, .75f, -.16f), 1, 1); Emit(tn, "Bottle", bm); }
        }

        // three leather booths along the south wall
        float[] boothX = { 1.55f, 3.95f, 6.35f };
        for (int i = 0; i < boothX.Length; i++)
        {
            var bn = OnWall(decor, "Booth " + i, 'S', boothX[i], 0f);
            var bg = Place(bn, "booth", BoothRecipe, Vector3.zero, 0, true, true);
            ColBox(bg.transform, "ColL", new Vector3(-.86f, .65f, .7f), new Vector3(.6f, 1.3f, 1.36f));
            ColBox(bg.transform, "ColR", new Vector3(.86f, .65f, .7f), new Vector3(.6f, 1.3f, 1.36f));
            ColBox(bg.transform, "ColTable", new Vector3(0, .4f, .62f), new Vector3(.8f, .8f, 1.1f));
            Place(bn, "ashtray", AshtrayRecipe, new Vector3(.1f, .755f, .45f), i * 50f);
            if (i != 1) Place(bn, "tumbler", TumblerRecipe, new Vector3(-.22f, .755f, .7f));
            if (i == 2) { var bb = new MB(); Bottle(bb, new Vector3(.2f, .755f, .85f), 0, 3); Bottle(bb, new Vector3(.08f, .755f, .95f), 1, 1); Emit(bn, "Bottles", bb); }
            if (i == 1)
                posts.Add(new CasinoStaff.Post { label = "Regular in a booth", role = CasinoRole.Regular, pose = CasinoRegularPose.TableSitter, seated = true, seed = 32,
                    anchor = Node(bn, "Post regular", new Vector3(-.84f, BoothSeat, .72f), new Vector3(0, 90, 0)) });
            Place(bn, "sconce", Sconce, new Vector3(0, 1.75f, .03f));
        }
        AddLight(decor, "Booth light", new Vector3(3.95f, 1.9f, -HZ + .7f), C(1f, 0.7f, 0.42f), 3.2f, 4.5f);

        // a panelled half-wall with a brass rail: the bar on one side, the gaming floor on the other
        const float hwX = 5.85f, hwZ0 = -0.7f, hwZ1 = 4.5f;
        var hw = new MB { uvScale = 1.2f };
        hw.Box("wood_dark", new Vector3(hwX, .46f, (hwZ0 + hwZ1) * .5f), new Vector3(.12f, .92f, hwZ1 - hwZ0));
        hw.grainAlong = true;
        hw.Box("wood_red", new Vector3(hwX, .94f, (hwZ0 + hwZ1) * .5f), new Vector3(.2f, .05f, hwZ1 - hwZ0 + .08f));
        hw.grainAlong = false;
        for (float z = hwZ0 + .5f; z < hwZ1 - .2f; z += .86f)
            foreach (int sgn in new[] { -1, 1 })
            {
                hw.Box("wood_mid", new Vector3(hwX + sgn * .066f, .5f, z), new Vector3(.014f, .62f, .68f));
                hw.Box("wood_wall", new Vector3(hwX + sgn * .075f, .5f, z), new Vector3(.012f, .48f, .54f));
            }
        var railPost = new[] { V(0, 0), V(.04f, 0), V(.045f, .015f), V(.018f, .03f), V(.016f, .26f), V(.03f, .29f), V(.03f, .31f), V(0, .32f) };
        for (float z = hwZ0 + .08f; z <= hwZ1 - .05f; z += (hwZ1 - hwZ0 - .16f) / 4f) hw.Lathe("brass", new Vector3(hwX, .965f, z), railPost, 10);
        hw.Tube("brass", new[] { new Vector3(hwX, 1.27f, hwZ0), new Vector3(hwX, 1.27f, hwZ1) }, .022f, 8);
        hw.Lathe("wood_red", new Vector3(hwX, 0, hwZ0 - .09f), new[] { V(0, 0), V(.11f, 0), V(.11f, .08f), V(.08f, .12f), V(.075f, .95f), V(.1f, 1.0f), V(.1f, 1.06f), V(.05f, 1.12f), V(.07f, 1.2f), V(0, 1.27f) }, 12);
        hw.Lathe("wood_red", new Vector3(hwX, 0, hwZ1 + .09f), new[] { V(0, 0), V(.11f, 0), V(.11f, .08f), V(.08f, .12f), V(.075f, .95f), V(.1f, 1.0f), V(.1f, 1.06f), V(.05f, 1.12f), V(.07f, 1.2f), V(0, 1.27f) }, 12);
        var hwGo = Emit(decor, "Half wall", hw, default, 0, true, true);
        ColBox(hwGo.transform, "Col", new Vector3(hwX, .64f, (hwZ0 + hwZ1) * .5f), new Vector3(.2f, 1.28f, hwZ1 - hwZ0 + .36f));

        // two plants, both past their best
        var pl1 = Place(decor, "plant", PlantRecipe, new Vector3(10.35f, 0, -5.35f), 0, true, true);
        ColCapsule(pl1.transform, "Col", new Vector3(0, .4f, 0), .22f, .8f);
        var pl2 = Place(decor, "plant", PlantRecipe, new Vector3(-6.7f, 0, -5.45f), 140, true, true);
        ColCapsule(pl2.transform, "Col", new Vector3(0, .4f, 0), .22f, .8f);

        // the coat stand by the door
        var cs = Place(decor, "coat_stand", CoatStand, new Vector3(-3.5f, 0, -5.45f), 30, true, true);
        ColCapsule(cs.transform, "Col", new Vector3(0, .9f, 0), .16f, 1.8f);

        // a brass rail on turned posts between the machines and the floor
        var rail = new MB();
        var post = new[] { V(0, 0), V(.07f, 0), V(.07f, .03f), V(.03f, .06f), V(.022f, .5f), V(.035f, .55f), V(.022f, .6f), V(.022f, .88f), V(.04f, .92f), V(.03f, .97f), V(0, .99f) };
        float rx = -8.15f;
        float[] rz = { -5.6f, -4.9f, 3.4f, 4.5f };
        foreach (float z in rz) rail.Lathe("brass", new Vector3(rx, 0, z), post, 10);
        rail.Tube("velvet", Sag(new Vector3(rx, .9f, rz[0]), new Vector3(rx, .9f, rz[1]), .14f), .02f, 7);
        rail.Tube("velvet", Sag(new Vector3(rx, .9f, rz[2]), new Vector3(rx, .9f, rz[3]), .12f), .02f, 7);
        Emit(decor, "Rope posts", rail, default, 0, true, true);
        foreach (float z in rz) ColCapsule(decor, "Col post", new Vector3(rx, .5f, z), .07f, 1f);
    }

    static List<Vector3> Sag(Vector3 a, Vector3 b, float sag, int n = 10)
    {
        var l = new List<Vector3>();
        for (int i = 0; i <= n; i++) { float t = i / (float)n; l.Add(Vector3.Lerp(a, b, t) + Vector3.down * (sag * 4f * t * (1f - t))); }
        return l;
    }

    static void BuildAtmosphere(Transform parent)
    {
        var air = Node(parent, "Atmosphere");
        // a few big soft sheets of smoke hanging under the lamps
        var h = new MB();
        var sheets = new[] { new Vector4(3.0f, 2.25f, 2.2f, 3.4f), new Vector4(-3.2f, 2.3f, 2.4f, 3.8f), new Vector4(8.2f, 2.4f, 1.2f, 4.2f), new Vector4(0f, 2.6f, -1.5f, 6.5f), new Vector4(-8.4f, 2.1f, -.8f, 3.6f), new Vector4(-1f, 1.9f, -4f, 3.2f) };
        foreach (var s in sheets)
            h.Quad("haze", new Vector3(s.x - s.w, s.y, s.z - s.w * .7f), new Vector3(s.x - s.w, s.y, s.z + s.w * .7f), new Vector3(s.x + s.w, s.y, s.z + s.w * .7f), new Vector3(s.x + s.w, s.y, s.z - s.w * .7f));
        Emit(air, "Haze", h, default, 0, false);

        // dust in the lamplight
        var go = new GameObject("Dust motes"); go.transform.SetParent(air, false); go.transform.localPosition = new Vector3(0, 1.7f, 0);
        var ps = go.AddComponent<ParticleSystem>();
        var main = ps.main;
        main.loop = true; main.prewarm = true; main.duration = 20f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(10f, 18f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.005f, 0.03f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.012f, 0.035f);
        main.startColor = new Color(1f, 0.88f, 0.66f, 0.55f);
        main.maxParticles = 160; main.simulationSpace = ParticleSystemSimulationSpace.World; main.gravityModifier = 0f;
        var em = ps.emission; em.rateOverTime = 11f;
        var sh = ps.shape; sh.shapeType = ParticleSystemShapeType.Box; sh.scale = new Vector3(HX * 2 - 2, 2.6f, HZ * 2 - 2);
        var noise = ps.noise; noise.enabled = true; noise.strength = 0.03f; noise.frequency = 0.25f; noise.scrollSpeed = 0.05f;
        var col = ps.colorOverLifetime; col.enabled = true;
        var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.2f), new GradientAlphaKey(1f, 0.8f), new GradientAlphaKey(0f, 1f) });
        col.color = g;
        var pr = go.GetComponent<ParticleSystemRenderer>();
        pr.sharedMaterial = Mat("mote"); pr.renderMode = ParticleSystemRenderMode.Billboard;
        pr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; pr.receiveShadows = false;
    }
}

public static partial class Setup_Casino
{
    // ------------------------------------------------------------------------------------------------ the street front

    static void Bulbs(MB a, MB b, IList<Vector3> at, float r, ref int n)
    {
        foreach (var p in at) { ((n++ & 1) == 0 ? a : b).Sphere("bulb", p, Vector3.one * r, 6, 4); }
    }

    static List<Vector3> RectPath(Vector3 centre, float hw, float hh, float step)
    {
        var l = new List<Vector3>();
        int nx = Mathf.Max(2, Mathf.RoundToInt(hw * 2 / step)), ny = Mathf.Max(2, Mathf.RoundToInt(hh * 2 / step));
        for (int i = 0; i < nx; i++) l.Add(centre + new Vector3(-hw + i * hw * 2 / nx, hh, 0));
        for (int i = 0; i < ny; i++) l.Add(centre + new Vector3(hw, hh - i * hh * 2 / ny, 0));
        for (int i = 0; i < nx; i++) l.Add(centre + new Vector3(hw - i * hw * 2 / nx, -hh, 0));
        for (int i = 0; i < ny; i++) l.Add(centre + new Vector3(-hw, -hh + i * hh * 2 / ny, 0));
        return l;
    }

    /// <summary>
    /// Resources/Casino/CasinoFront.prefab: everything the runtime hangs on the centre bay of the storefront. Prefab space:
    /// origin on the ground at the middle of the shop face, +Z INTO the building, the street toward -Z, +X to the right as
    /// you face the shop. The bay it dresses (NeighborhoodBuilder.InfillStorefront): 10.4 m wide, entrance 1.7 x 3 m,
    /// display windows 3.74 x 2.25 m centred 3.02 m either side at 1.7 m, a canopy 0.25 m thick at 3.25 m reaching 1.8 m
    /// out, the fascia board at 4.15 m. No colliders anywhere.
    /// </summary>
    static void BuildFrontPrefab(StringBuilder log)
    {
        EnsureFolder("Assets/Resources/Casino");
        var rootGo = new GameObject("CasinoFront");
        var root = rootGo.transform;
        var front = rootGo.AddComponent<CasinoFront>();
        var bulbsA = new MB(); var bulbsB = new MB(); int bn = 0;

        // ---- the name, in red tube on a dark board over the fascia, ringed with bulbs
        var sign = new MB { uvScale = 1f };
        Vector3 sc = new Vector3(0, 4.5f, -.3f);
        sign.Box("front_board", sc, new Vector3(9.0f, 1.7f, .16f));
        foreach (float yy in new[] { -.82f, .82f }) sign.Box("gilt", sc + new Vector3(0, yy, -.07f), new Vector3(9.06f, .07f, .06f));
        foreach (float xx in new[] { -4.5f, 4.5f }) sign.Box("gilt", sc + new Vector3(xx, 0, -.07f), new Vector3(.07f, 1.7f, .06f));
        // the mascot's roundel on top
        sign.Lathe("front_board", new Vector3(0, 5.75f, -.3f), new[] { V(0, 0), V(.78f, 0), V(.78f, .12f), V(0, .12f) }, 28, new Vector3(-90, 0, 0));
        sign.Torus("gilt", new Vector3(0, 5.75f, -.42f), .76f, .035f, 28, 6, new Vector3(90, 0, 0));
        Emit(root, "Sign board", sign, default, 0, false, true);
        var neon = new MB();
        NeonText(neon, "neon_red", "THE LUCKY RACCOON", sc + new Vector3(0, .1f, -.13f), Vector3.right, Vector3.up, .52f, .03f);
        var signNeon = Emit(root, "Neon name", neon, default, 0, false);
        var trim = new MB();
        var wave = new List<Vector3>();
        for (int i = 0; i <= 40; i++) { float t = i / 40f; wave.Add(sc + new Vector3(Mathf.Lerp(-3.6f, 3.6f, t), -.52f + Mathf.Sin(t * Mathf.PI * 6f) * .05f, -.13f)); }
        trim.Tube("neon_amber", wave, .022f, 6);
        foreach (float sx in new[] { -4.08f, 4.08f })
        {
            var star = new List<Vector3>();
            for (int i = 0; i < 10; i++) { float a = i * 36f * Mathf.Deg2Rad, r = (i & 1) == 0 ? .27f : .11f; star.Add(sc + new Vector3(sx + Mathf.Sin(a) * r, .1f + Mathf.Cos(a) * r, -.13f)); }
            trim.Tube("neon_amber", star, .018f, 6, true);
        }
        // the raccoon: head, ears, the mask, in tube
        Vector3 rc = new Vector3(0, 5.72f, -.46f);
        var head = new List<Vector3>(); for (int i = 0; i < 28; i++) { float a = i * 360f / 28f * Mathf.Deg2Rad; head.Add(rc + new Vector3(Mathf.Cos(a) * .5f, Mathf.Sin(a) * .36f - .04f, 0)); }
        var mask = new MB();
        mask.Tube("neon_blue", head, .02f, 6, true);
        foreach (int sgn in new[] { -1, 1 })
        {
            mask.Tube("neon_blue", new[] { rc + new Vector3(sgn * .44f, .16f, 0), rc + new Vector3(sgn * .5f, .46f, 0), rc + new Vector3(sgn * .22f, .3f, 0) }, .02f, 6);
            var eye = new List<Vector3>(); for (int i = 0; i < 16; i++) { float a = i * 22.5f * Mathf.Deg2Rad; eye.Add(rc + new Vector3(sgn * .2f + Mathf.Cos(a) * .15f, .03f + Mathf.Sin(a) * .09f, 0)); }
            trim.Tube("neon_amber", eye, .018f, 6, true);
        }
        mask.Tube("neon_blue", new[] { rc + new Vector3(-.06f, -.2f, 0), rc + new Vector3(0, -.26f, 0), rc + new Vector3(.06f, -.2f, 0) }, .018f, 6);
        var trimGo = Emit(root, "Neon trim", trim, default, 0, false);
        var maskGo = Emit(root, "Neon raccoon", mask, default, 0, false);
        Bulbs(bulbsA, bulbsB, RectPath(sc + new Vector3(0, 0, -.1f), 4.36f, .7f, .29f), .04f, ref bn);

        // ---- the marquee on the canopy's edge, and bulbs under the canopy over the door
        var mq = new MB();
        Vector3 mc = new Vector3(0, 3.12f, -1.9f);
        mq.Box("marquee", mc, new Vector3(6.2f, .62f, .16f));
        foreach (float yy in new[] { -.33f, .33f }) mq.Box("gilt", mc + new Vector3(0, yy, -.02f), new Vector3(6.3f, .05f, .2f));
        foreach (float xx in new[] { -3.12f, 3.12f }) mq.Box("gilt", mc + new Vector3(xx, 0, -.02f), new Vector3(.05f, .7f, .2f));
        mq.Box("front_dark", new Vector3(0, 3.1f, -.9f), new Vector3(5.4f, .05f, 1.7f));       // a dark soffit for the bulbs to sit in
        Emit(root, "Marquee", mq, default, 0, false, true);
        Label(root, "Marquee text", "SLOTS - BLACKJACK - ROULETTE - COCKTAILS", mc + new Vector3(0, 0, -.085f), Vector3.zero, new Vector2(5.5f, .3f), new Color(0.98f, 0.9f, 0.62f), FontStyles.Bold, 4f);
        Bulbs(bulbsA, bulbsB, RectPath(mc + new Vector3(0, 0, -.1f), 2.96f, .25f, .27f), .035f, ref bn);
        var soffit = new List<Vector3>();
        for (int r = 0; r < 3; r++) for (int k = 0; k < 11; k++) soffit.Add(new Vector3(-2.25f + k * .45f, 3.06f, -.35f - r * .55f));
        Bulbs(bulbsA, bulbsB, soffit, .04f, ref bn);

        // ---- the doors: padded leather, brass, two amber portholes
        var dm = new MB { uvScale = 1f };
        dm.Box("front_dark", new Vector3(-1.1f, 1.4f, -.14f), new Vector3(.2f, 2.8f, .2f)); dm.Box("front_dark", new Vector3(1.1f, 1.4f, -.14f), new Vector3(.2f, 2.8f, .2f));
        dm.Box("front_dark", new Vector3(0, 2.72f, -.14f), new Vector3(2.4f, .34f, .2f));
        dm.Box("brass", new Vector3(0, 2.72f, -.245f), new Vector3(.9f, .16f, .012f));
        foreach (int sgn in new[] { -1, 1 })
        {
            float x = sgn * .5f;
            dm.Box("leather_red", new Vector3(x, 1.28f, -.16f), new Vector3(.98f, 2.52f, .07f));
            dm.Box("brass", new Vector3(x, .14f, -.198f), new Vector3(.94f, .24f, .008f));
            dm.Lathe("brass", new Vector3(x, 1.62f, -.195f), new[] { V(.15f, 0), V(.2f, 0), V(.2f, .02f), V(.17f, .03f), V(.15f, .02f) }, 20, new Vector3(-90, 0, 0));
            dm.Lathe("porthole", new Vector3(x, 1.62f, -.2f), new[] { V(0, 0), V(.155f, 0), V(0, .012f) }, 20, new Vector3(-90, 0, 0));
            dm.Tube("brass", new[] { new Vector3(sgn * .1f, .95f, -.2f), new Vector3(sgn * .1f, .95f, -.26f), new Vector3(sgn * .1f, 1.4f, -.26f), new Vector3(sgn * .1f, 1.4f, -.2f) }, .016f, 7);
        }
        dm.Box("brass", new Vector3(0, 1.28f, -.2f), new Vector3(.02f, 2.52f, .01f));
        // the display windows, painted out
        foreach (int sgn in new[] { -1, 1 })
        {
            Vector3 wc = new Vector3(sgn * 3.02f, 1.7f, -.13f);
            dm.Box("front_dark", wc, new Vector3(3.7f, 2.2f, .04f));
            foreach (float yy in new[] { -1.1f, 1.1f }) dm.Box("gilt", wc + new Vector3(0, yy, -.02f), new Vector3(3.76f, .05f, .05f));
            foreach (float xx in new[] { -1.86f, 1.86f }) dm.Box("gilt", wc + new Vector3(xx, 0, -.02f), new Vector3(.05f, 2.2f, .05f));
            dm.Box("velvet", wc + new Vector3(0, -.75f, -.025f), new Vector3(3.5f, .5f, .01f));
        }
        Emit(root, "Doors and windows", dm, default, 0, false, true);
        Label(root, "Plaque", "EST. 1962", new Vector3(0, 2.72f, -.253f), Vector3.zero, new Vector2(.8f, .1f), new Color(0.14f, 0.09f, 0.03f), FontStyles.Bold, 8f);
        var wn = new MB(); var wb = new MB();
        Vector3 lw = new Vector3(-3.02f, 1.95f, -.17f), rw = new Vector3(3.02f, 1.95f, -.17f);
        NeonText(wn, "neon_pink", "SLOTS", lw, Vector3.right, Vector3.up, .5f, .022f);
        NeonText(wn, "neon_pink", "BAR", rw, Vector3.right, Vector3.up, .5f, .022f);
        foreach (var c in new[] { lw, rw })
            wb.Tube("neon_blue", new[] { c + new Vector3(-1.5f, .5f, 0), c + new Vector3(1.5f, .5f, 0), c + new Vector3(1.5f, -.5f, 0), c + new Vector3(-1.5f, -.5f, 0) }, .016f, 6, true);
        var winNeon = Emit(root, "Neon windows", wn, default, 0, false); var winBorder = Emit(root, "Neon window borders", wb, default, 0, false);
        Label(root, "Window L", "LOOSEST SLOTS ON COUPON STREET", new Vector3(-3.02f, .95f, -.165f), Vector3.zero, new Vector2(3.2f, .2f), new Color(0.95f, 0.82f, 0.45f), FontStyles.Bold);
        Label(root, "Window R", "COLD BEER - WARM WELCOME", new Vector3(3.02f, .95f, -.165f), Vector3.zero, new Vector2(3.2f, .2f), new Color(0.95f, 0.82f, 0.45f), FontStyles.Bold);

        // lanterns either side of the doors
        Place(root, "sconce", Sconce, new Vector3(-1.5f, 2.05f, -.05f), 180, false);
        Place(root, "sconce", Sconce, new Vector3(1.5f, 2.05f, -.05f), 180, false);

        // ---- the blade sign up the party wall
        var bl = new MB();
        Vector3 bc = new Vector3(4.86f, 5.5f, -.95f);
        bl.Box("marquee", bc, new Vector3(.14f, 2.3f, .72f));
        bl.Box("gilt", bc + new Vector3(0, 1.17f, 0), new Vector3(.18f, .05f, .78f)); bl.Box("gilt", bc + new Vector3(0, -1.17f, 0), new Vector3(.18f, .05f, .78f));
        bl.Tube("black_iron", new[] { bc + new Vector3(0, 1.0f, .9f), bc + new Vector3(0, 1.0f, .36f) }, .025f, 6);
        bl.Tube("black_iron", new[] { bc + new Vector3(0, -1.0f, .9f), bc + new Vector3(0, -1.0f, .36f) }, .025f, 6);
        Emit(root, "Blade sign", bl, default, 0, false, true);
        foreach (int sgn in new[] { -1, 1 })
        {
            Label(root, "Blade " + sgn, "C\nA\nS\nI\nN\nO", bc + new Vector3(sgn * .075f, .22f, 0), new Vector3(0, -sgn * 90f, 0), new Vector2(.6f, 1.7f), new Color(1f, 0.9f, 0.6f), FontStyles.Bold);
            Label(root, "Blade bar " + sgn, "BAR", bc + new Vector3(sgn * .075f, -.9f, 0), new Vector3(0, -sgn * 90f, 0), new Vector2(.6f, .26f), new Color(1f, 0.55f, 0.4f), FontStyles.Bold);
            var edge = new List<Vector3>();
            for (int k = 0; k < 9; k++) edge.Add(bc + new Vector3(sgn * .08f, -1.05f + k * .2625f, -.33f));
            for (int k = 0; k < 9; k++) edge.Add(bc + new Vector3(sgn * .08f, -1.05f + k * .2625f, .33f));
            Bulbs(bulbsA, bulbsB, edge, .03f, ref bn);
        }

        // ---- on the apron: a red carpet and two rope runs (each pivots at the shop end and is laid on the relief at runtime)
        // (the carpet is laid in three short lengths so it follows a bulge in the ground instead of cutting through it)
        var grounded = new List<Transform>();
        const float seg = 0.88f;
        for (int k = 0; k < 3; k++)
        {
            var carpet = Node(root, "Carpet " + k, new Vector3(0, 0, -.22f - k * seg));
            var cm = new MB();
            cm.Box("carpet_red", new Vector3(0, .012f, -seg * .5f), new Vector3(1.9f, .024f, seg + .04f));
            cm.Box("gilt", new Vector3(-.9f, .014f, -seg * .5f), new Vector3(.06f, .026f, seg + .04f)); cm.Box("gilt", new Vector3(.9f, .014f, -seg * .5f), new Vector3(.06f, .026f, seg + .04f));
            if (k == 0) cm.Box("rubber", new Vector3(0, .02f, -.42f), new Vector3(1.5f, .02f, .62f));
            Emit(carpet, "Mesh", cm, default, 0, false);
            Node(carpet, "Far", new Vector3(0, 0, -seg));
            grounded.Add(carpet);
        }
        var stanchion = new[] { V(0, 0), V(.16f, 0), V(.16f, .02f), V(.05f, .05f), V(.026f, .1f), V(.024f, .86f), V(.045f, .9f), V(.05f, .95f), V(.03f, 1.0f), V(0, 1.02f) };
        foreach (int sgn in new[] { -1, 1 })
        {
            var run = Node(root, "Rope run " + (sgn < 0 ? "L" : "R"), new Vector3(sgn * 1.22f, 0, -.55f));
            var rm = new MB();
            rm.Lathe("brass", Vector3.zero, stanchion, 12); rm.Lathe("brass", new Vector3(0, 0, -1.9f), stanchion, 12);
            rm.Tube("velvet", Sag(new Vector3(0, .93f, 0), new Vector3(0, .93f, -1.9f), .2f, 12), .028f, 8);
            Emit(run, "Mesh", rm, default, 0, false, true);
            Node(run, "Far", new Vector3(0, 0, -1.9f));
            grounded.Add(run);
        }

        // ---- on the roof: two searchlights that sweep the sky at night
        var beams = new List<Transform>(); var beamRenderers = new List<Renderer>();
        foreach (int sgn in new[] { -1, 1 })
        {
            Vector3 at = new Vector3(sgn * 3.3f, 9.3f, 2.6f);
            var hm = new MB();
            hm.Tube("black_iron", new[] { new Vector3(at.x, 6.9f, at.z), at + Vector3.down * .25f }, .05f, 8);
            hm.Lathe("black_iron", new Vector3(at.x, 6.9f, at.z), new[] { V(0, 0), V(.3f, 0), V(.3f, .06f), V(.06f, .1f), V(0, .1f) }, 10);
            Emit(root, "Searchlight stand", hm, default, 0, false);
            var pivot = Node(root, "Searchlight " + (sgn < 0 ? "L" : "R"), at, new Vector3(-16f, 0, -sgn * 12f));
            var lamp = new MB();
            lamp.Lathe("black_iron", new Vector3(0, -.22f, 0), new[] { V(0, 0), V(.2f, .03f), V(.26f, .2f), V(.27f, .44f), V(.25f, .44f) }, 14);
            lamp.Lathe("bulb", new Vector3(0, .2f, 0), new[] { V(0, 0), V(.245f, 0) }, 14);
            Emit(pivot, "Lamp", lamp, default, 0, false);
            var bm = new MB();
            bm.Lathe("beam", new Vector3(0, .24f, 0), new[] { V(.22f, 0), V(2.6f, 46f) }, 16);
            var beam = Emit(pivot, "Beam", bm, default, 0, false);
            beams.Add(pivot); beamRenderers.Add(beam.GetComponent<Renderer>());
        }

        // ---- every bulb, in two alternating sets
        var ba = Emit(root, "Bulbs A", bulbsA, default, 0, false); var bb = Emit(root, "Bulbs B", bulbsB, default, 0, false);

        // ---- the only two real lights: the warm pool from the doorway, and the sign's red wash
        var doorLight = AddLight(root, "Doorway light", new Vector3(0, 3.0f, -1.0f), C(1f, 0.72f, 0.42f), 2.4f, 7.5f, true, 128f);
        var signGlow = AddLight(root, "Sign glow", new Vector3(0, 4.2f, -2.4f), C(1f, 0.22f, 0.2f), 3f, 11f);

        var n1 = signNeon.AddComponent<CasinoNeon>(); n1.renderers = new[] { signNeon.GetComponent<Renderer>() }; n1.emission = C(2.7f, 0.2f, 0.14f);
        n1.flicker = .5f; n1.nightOnly = true; n1.dayLevel = .5f; n1.glow = signGlow; n1.glowIntensity = 3f; n1.seed = 21;
        var n2 = trimGo.AddComponent<CasinoNeon>(); n2.renderers = new[] { trimGo.GetComponent<Renderer>() }; n2.emission = C(2.5f, 1.1f, 0.12f); n2.flicker = .15f; n2.nightOnly = true; n2.dayLevel = .45f; n2.seed = 22;
        var n3 = maskGo.AddComponent<CasinoNeon>(); n3.renderers = new[] { maskGo.GetComponent<Renderer>(), winBorder.GetComponent<Renderer>() }; n3.emission = C(0.25f, 0.85f, 2.7f); n3.flicker = .3f; n3.nightOnly = true; n3.dayLevel = .45f; n3.seed = 23;
        var n4 = winNeon.AddComponent<CasinoNeon>(); n4.renderers = new[] { winNeon.GetComponent<Renderer>() }; n4.emission = C(2.5f, 0.3f, 0.95f); n4.flicker = .6f; n4.nightOnly = true; n4.dayLevel = .5f; n4.seed = 24;
        var n5 = ba.AddComponent<CasinoNeon>(); n5.mode = CasinoNeon.Mode.Chase; n5.renderers = new[] { ba.GetComponent<Renderer>(), bb.GetComponent<Renderer>() };
        n5.emission = C(2.6f, 1.8f, 0.7f); n5.chaseRate = 2.5f; n5.nightOnly = true; n5.dayLevel = .06f;

        // ---- the door itself
        var doorNode = Node(root, "Door");
        var door = doorNode.gameObject.AddComponent<CasinoDoor>(); door.exit = false;
        var zone = doorNode.gameObject.AddComponent<InteractionZone>();
        // Tall and deep: the apron slopes with the relief, so the player's feet can be well below (or above) the door's base.
        zone.center = new Vector3(0, 1f, -1.1f); zone.size = new Vector3(2.6f, 4f, 2.3f); zone.mode = ZoneMode.OnFoot; zone.score = 1.5f; zone.targetOverride = door;

        front.door = doorNode; front.onGround = grounded.ToArray();
        front.beams = beams.ToArray(); front.beamRenderers = beamRenderers.ToArray();
        front.doorLight = doorLight; front.musicAnchor = Node(root, "Music", new Vector3(0, 1.6f, -.3f));

        AssetDatabase.SaveAssets(); // the meshes must be on disk before the prefab points at them
        PrefabUtility.SaveAsPrefabAsset(rootGo, FrontPrefabPath);
        Object.DestroyImmediate(rootGo);
        log.Append("front prefab saved; ");
    }
}

public static partial class Setup_Casino
{
    const float BoothSeat = 0.47f;

    /// <summary>A booth against a wall (the wall at z = 0, open toward +Z): two high-backed leather benches facing each
    /// other across a fixed table.</summary>
    static void BoothRecipe(MB d)
    {
        d.uvScale = 1.2f;
        foreach (int sgn in new[] { -1, 1 })
        {
            float x = sgn * .86f;
            d.Box("wood_dark", new Vector3(x, .19f, .7f), new Vector3(.56f, .38f, 1.32f));                       // bench box
            d.Box("leather_seat", new Vector3(x - sgn * .03f, .425f, .7f), new Vector3(.5f, .09f, 1.26f));         // cushion
            d.Box("wood_dark", new Vector3(sgn * 1.11f, .66f, .7f), new Vector3(.07f, 1.32f, 1.36f));             // the high back
            d.Box("leather_red", new Vector3(sgn * 1.05f, .86f, .7f), new Vector3(.07f, .78f, 1.24f));            // tufted back pad
            d.grainAlong = true;
            d.Box("wood_red", new Vector3(sgn * 1.11f, 1.34f, .7f), new Vector3(.12f, .05f, 1.4f));
            d.grainAlong = false;
            d.Sphere("brass", new Vector3(sgn * 1.11f, 1.4f, 1.36f), Vector3.one * .04f, 8, 5);
        }
        d.Lathe("black_iron", new Vector3(0, 0, .62f), new[] { V(0, 0), V(.2f, 0), V(.2f, .02f), V(.05f, .05f), V(.04f, .69f), V(.1f, .71f), V(0, .71f) }, 12);
        d.grainAlong = true;
        d.Box("wood_red", new Vector3(0, .73f, .62f), new Vector3(.78f, .045f, 1.12f));
        d.grainAlong = false;
        d.Box("brass", new Vector3(0, .73f, 1.185f), new Vector3(.78f, .05f, .012f));
    }

    static void PlantRecipe(MB d)
    {
        d.Lathe("terracotta", Vector3.zero, new[] { V(0, 0), V(.15f, 0), V(.2f, .3f), V(.22f, .3f), V(.22f, .34f), V(.19f, .34f), V(.18f, .3f), V(0, .3f) }, 12);
        d.Lathe("frame_inner", new Vector3(0, .3f, 0), new[] { V(.18f, 0), V(0, .01f) }, 12);
        // drooping fronds: most of them brown
        for (int i = 0; i < 9; i++)
        {
            float a = (i * 40f + (i % 3) * 11f) * Mathf.Deg2Rad, reach = .3f + Hash01(i, 61) * .28f, top = .75f + Hash01(i, 62) * .45f;
            Vector3 dir = new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a));
            var stem = new List<Vector3>();
            for (int k = 0; k <= 6; k++) { float t = k / 6f; stem.Add(dir * (reach * t) + Vector3.up * (.3f + top * Mathf.Sin(t * Mathf.PI * .62f) - .12f * t * t)); }
            string m = Hash01(i, 63) < .55f ? "leaf_dead" : "leaf";
            d.Tube(m, stem, .008f, 5);
            for (int k = 2; k <= 6; k++)
            {
                d.Push(stem[k], new Vector3(18f * k, a * Mathf.Rad2Deg, 0));
                d.Sphere(m, Vector3.zero, new Vector3(.035f, .008f, .1f - k * .006f), 6, 3);
                d.Pop();
            }
        }
    }

    /// <summary>A pub mirror: bevelled glass in a dark frame, with a brand in gilt letters.</summary>
    static void BarMirror(Transform parent, char wall, float along, float y, float w, float h, string line1, string line2, string small)
    {
        var n = OnWall(parent, "Mirror " + line1, wall, along, y, 0.03f);
        var d = new MB();
        d.Box("mirror", new Vector3(0, 0, .012f), new Vector3(w, h, .024f));
        foreach (float yy in new[] { -h * .5f - .035f, h * .5f + .035f }) d.Box("wood_dark", new Vector3(0, yy, .025f), new Vector3(w + .14f, .07f, .05f));
        foreach (float xx in new[] { -w * .5f - .035f, w * .5f + .035f }) d.Box("wood_dark", new Vector3(xx, 0, .025f), new Vector3(.07f, h, .05f));
        d.Box("gilt", new Vector3(0, -h * .12f, .0245f), new Vector3(w * .7f, .008f, .002f));
        Emit(n, "Glass", d);
        Color gold = new Color(0.86f, 0.68f, 0.26f), red = new Color(0.62f, 0.12f, 0.08f);
        Label(n, "Brand", line1, new Vector3(0, h * .2f, .027f), new Vector3(0, 180, 0), new Vector2(w * .88f, h * .26f), gold, FontStyles.Bold, 2f);
        Label(n, "Kind", line2, new Vector3(0, -h * .01f, .027f), new Vector3(0, 180, 0), new Vector2(w * .7f, h * .15f), red, FontStyles.Bold, 12f);
        Label(n, "Small", small, new Vector3(0, -h * .28f, .027f), new Vector3(0, 180, 0), new Vector2(w * .8f, h * .1f), gold, FontStyles.Italic);
    }
}
