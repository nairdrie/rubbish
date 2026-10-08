using UnityEngine;

public static partial class ItemVisuals
{
    // One hand-built silhouette per findable part (ItemCatalog.Parts.cs, "part_*" keys). Same cached meshes and material
    // blocks as every other piece of junk, so the sorting table, the X-ray plate, ebag photos and the Callings shopping
    // list all show the same thing. Renderer-only: Build adds no colliders.
    static bool BuildPart(Kit k, string key)
    {
        Color c = k.c;
        switch (key)
        {
            case "part_boot": // a tired steel-toe boot with a flapping sole
                k.Box(new Vector3(0, -.13f, .03f), new Vector3(.15f, .035f, .36f), Black);
                k.Box(new Vector3(0, -.145f, -.1f), new Vector3(.15f, .05f, .1f), Black);
                k.Ball(new Vector3(0, -.06f, .12f), new Vector3(.15f, .12f, .2f), c);
                k.Ball(new Vector3(0, -.05f, .18f), new Vector3(.13f, .09f, .1f), Silver, s: Surface.Metal);
                k.Cyl(new Vector3(0, .05f, -.06f), .15f, .24f, c);
                k.Ring(new Vector3(0, .17f, -.06f), .17f, .03f, Mul(c, .7f));
                for (int i = 0; i < 4; i++) k.Box(new Vector3(0, -.01f + i * .04f, .022f), new Vector3(.1f, .008f, .012f), Yellow);
                k.Box(new Vector3(0, -.155f, .19f), new Vector3(.13f, .01f, .1f), Tan, new Vector3(14, 0, 0));
                break;
            case "part_belt": // coiled belt, big buckle
                for (int i = 0; i < 3; i++) k.Ring(new Vector3(0, -.03f + i * .028f, 0), .3f - i * .05f, .03f, Mul(c, 1f - i * .08f));
                k.Box(new Vector3(.16f, -.03f, .06f), new Vector3(.03f, .028f, .16f), c, new Vector3(0, 25, 0));
                k.Box(new Vector3(.2f, -.03f, .13f), new Vector3(.085f, .034f, .07f), Gold, new Vector3(0, 25, 0), Surface.Metal);
                k.Box(new Vector3(.2f, -.03f, .13f), new Vector3(.05f, .04f, .035f), Black, new Vector3(0, 25, 0));
                k.Cap(new Vector3(.2f, -.012f, .13f), .012f, .07f, Silver, new Vector3(90, 25, 0), Surface.Metal);
                for (int i = 0; i < 3; i++) k.Ball(new Vector3(.12f + i * .012f, -.012f, -.05f + i * .035f), .014f, Black);
                break;
            case "part_hose": // a fat green coil with a brass nozzle and one kink
                for (int i = 0; i < 4; i++) k.Ring(new Vector3(0, -.09f + i * .05f, 0), .36f - (i % 2) * .03f, .055f, Mul(c, 1f - (i % 2) * .12f));
                k.Cap(new Vector3(.2f, .09f, .05f), .05f, .2f, c, new Vector3(70, 20, 0));
                k.Cyl(new Vector3(.24f, .12f, .15f), .06f, .06f, Gold, new Vector3(70, 20, 0), Surface.Metal);
                k.Cone(new Vector3(.26f, .135f, .2f), .05f, .07f, Gold, new Vector3(70, 20, 0), Surface.Metal);
                k.Cap(new Vector3(-.19f, -.09f, -.08f), .05f, .17f, c, new Vector3(90, -40, 0));
                k.Ring(new Vector3(-.24f, -.09f, -.15f), .07f, .02f, Silver, new Vector3(90, -40, 0), Surface.Metal);
                break;
            case "part_tube": // limp inner tube, valve and patches
                k.Ring(Vector3.zero, .38f, .07f, c, new Vector3(8, 0, 0));
                k.Ring(new Vector3(.02f, .03f, .01f), .3f, .05f, Mul(c, 1.25f), new Vector3(-6, 30, 4));
                k.Cyl(new Vector3(0, .05f, .17f), .018f, .09f, Gold, new Vector3(20, 0, 0), Surface.Metal);
                k.Ball(new Vector3(0, .1f, .187f), .024f, Black);
                k.Box(new Vector3(.15f, .035f, -.06f), new Vector3(.06f, .012f, .05f), Red, new Vector3(0, 30, 10));
                k.Box(new Vector3(-.13f, .035f, .09f), new Vector3(.05f, .012f, .05f), Orange, new Vector3(0, -40, -8));
                k.Box(new Vector3(-.1f, .03f, -.13f), new Vector3(.04f, .012f, .06f), Yellow, new Vector3(0, 20, 0));
                break;
            case "part_skatewheel": // chunky pink wheel, bearing, glitter flecks
                k.Cyl(Vector3.zero, .26f, .13f, c, new Vector3(0, 0, 90));
                k.Ring(new Vector3(.03f, 0, 0), .27f, .05f, Mul(c, .8f), new Vector3(0, 0, 90));
                k.Ring(new Vector3(-.03f, 0, 0), .27f, .05f, Mul(c, .8f), new Vector3(0, 0, 90));
                k.Cyl(Vector3.zero, .13f, .15f, White, new Vector3(0, 0, 90));
                k.Cyl(Vector3.zero, .075f, .17f, Silver, new Vector3(0, 0, 90), Surface.Metal);
                k.Cyl(Vector3.zero, .03f, .19f, Black, new Vector3(0, 0, 90));
                for (int i = 0; i < 5; i++) { float a = i * 72f * Mathf.Deg2Rad; k.Ball(new Vector3(.068f, Mathf.Cos(a) * .095f, Mathf.Sin(a) * .095f), .02f, Yellow); }
                break;
            case "part_broom": // a long handle with the thread and a sad tuft where the broom was
                k.Cap(Vector3.zero, .035f, .5f, c, new Vector3(0, 0, 72));
                k.Cyl(new Vector3(-.225f, -.073f, 0), .042f, .05f, Red, new Vector3(0, 0, 72));
                k.Cyl(new Vector3(.2f, .065f, 0), .05f, .07f, Silver, new Vector3(0, 0, 72), Surface.Metal);
                for (int i = 0; i < 3; i++) k.Ring(new Vector3(.185f + i * .017f, .06f + i * .0055f, 0), .058f, .012f, DarkGrey, new Vector3(0, 0, -18));
                for (int i = -1; i <= 1; i++) k.Cone(new Vector3(.255f, .085f + i * .012f, i * .02f), .02f, .07f, Tan, new Vector3(i * 25, 0, -108 + i * 10));
                k.Ball(new Vector3(-.245f, -.08f, 0), .045f, Red);
                break;
            case "part_cord": // orange spaghetti, a plug at one end and a socket at the other
                for (int i = 0; i < 3; i++) k.Ring(new Vector3(i * .03f - .03f, -.06f + i * .04f, i * .02f), .3f - i * .04f, .035f, i == 1 ? Mul(c, .82f) : c, new Vector3(i * 9 - 9, 0, i * 6));
                k.Cap(new Vector3(.17f, .03f, .08f), .035f, .16f, c, new Vector3(80, 30, 0));
                k.Box(new Vector3(.2f, .05f, .17f), new Vector3(.07f, .05f, .08f), Black, new Vector3(-10, 30, 0));
                for (int s = -1; s <= 1; s += 2) k.Box(new Vector3(.215f + s * .014f, .06f, .225f), new Vector3(.008f, .028f, .05f), Silver, new Vector3(-10, 30, 0), Surface.Metal);
                k.Box(new Vector3(-.17f, -.07f, -.1f), new Vector3(.075f, .055f, .09f), Black, new Vector3(0, -35, 0));
                k.Ball(new Vector3(-.12f, .0f, .1f), .03f, Yellow, Surface.Emissive, 1.6f);
                break;
            case "part_circuit": // green board, chips, a capacitor skyline and a gold edge
                k.Box(Vector3.zero, new Vector3(.36f, .016f, .26f), c);
                k.Box(new Vector3(0, .001f, -.125f), new Vector3(.24f, .018f, .025f), Gold, s: Surface.Metal);
                k.Box(new Vector3(-.07f, .02f, .01f), new Vector3(.11f, .022f, .11f), Black);
                k.Box(new Vector3(.09f, .018f, -.04f), new Vector3(.07f, .018f, .045f), DarkGrey);
                for (int i = 0; i < 3; i++) k.Cyl(new Vector3(.06f + i * .045f, .04f, .07f), .035f, .065f + i * .012f, i == 1 ? Blue : Black);
                for (int i = 0; i < 4; i++) k.Box(new Vector3(-.15f + i * .028f, .014f, .1f), new Vector3(.015f, .012f, .03f), i % 2 == 0 ? Yellow : Red);
                k.Ball(new Vector3(.15f, .022f, -.09f), .026f, Red, Surface.Emissive, 1.5f);
                k.Cyl(new Vector3(-.15f, .02f, -.08f), .05f, .02f, Silver, s: Surface.Metal);
                break;
            case "part_remote": // long black remote with far too many buttons
                k.Box(Vector3.zero, new Vector3(.12f, .04f, .4f), c);
                k.Ball(new Vector3(0, .0f, .2f), new Vector3(.12f, .04f, .06f), c);
                k.Ball(new Vector3(0, .022f, .15f), .04f, Red);
                for (int r = 0; r < 4; r++) for (int col = -1; col <= 1; col++)
                    k.Box(new Vector3(col * .033f, .022f, .07f - r * .045f), new Vector3(.022f, .012f, .026f), r == 0 ? Grey : White);
                k.Ring(new Vector3(0, .022f, -.13f), .08f, .016f, Silver);
                k.Ball(new Vector3(0, .024f, -.13f), .03f, Green);
                k.Box(new Vector3(0, -.021f, -.1f), new Vector3(.08f, .006f, .12f), DarkGrey);
                break;
            case "part_glasses": // little round readers on a neck cord
                for (int s = -1; s <= 1; s += 2)
                {
                    k.Ring(new Vector3(s * .085f, 0, .1f), .13f, .016f, c, new Vector3(90, 0, 0));
                    k.Cyl(new Vector3(s * .085f, 0, .1f), .11f, .006f, Clear, new Vector3(90, 0, 0), Surface.Glass);
                    k.Cap(new Vector3(s * .15f, .0f, -.03f), .012f, .27f, c, new Vector3(90, s * -4, 0));
                    k.Ball(new Vector3(s * .157f, -.012f, -.16f), .022f, c);
                }
                k.Cap(new Vector3(0, .012f, .1f), .014f, .06f, c, new Vector3(0, 0, 90));
                k.Ring(new Vector3(0, -.03f, -.17f), .3f, .008f, Red, new Vector3(18, 0, 0));
                break;
            case "part_magnifier": // detective-grade lens on a turned handle
                k.Ring(new Vector3(0, .09f, 0), .27f, .028f, Gold, new Vector3(90, 0, 0), Surface.Metal);
                k.Cyl(new Vector3(0, .09f, 0), .23f, .012f, c, new Vector3(90, 0, 0), Surface.Glass);
                k.Ball(new Vector3(-.045f, .14f, .012f), new Vector3(.05f, .02f, .004f), White);
                k.Cyl(new Vector3(0, -.065f, 0), .04f, .04f, Gold, s: Surface.Metal);
                k.Lathe("bottle", new Vector3(0, -.17f, 0), new Vector3(.06f, .19f, .06f), Brown, new Vector3(180, 0, 0));
                k.Ball(new Vector3(0, -.27f, 0), .055f, Brown);
                k.Ring(new Vector3(0, -.1f, 0), .055f, .012f, Black);
                break;
            case "part_backpack": // slumped school bag, front pocket, straps, a keyring
                k.Ball(Vector3.zero, new Vector3(.3f, .36f, .2f), c);
                k.Box(new Vector3(0, -.13f, 0), new Vector3(.29f, .1f, .19f), Mul(c, .85f));
                k.Ball(new Vector3(0, -.06f, .1f), new Vector3(.22f, .17f, .08f), Mul(c, .75f));
                k.Box(new Vector3(0, .0f, .135f), new Vector3(.18f, .012f, .012f), Yellow);
                for (int s = -1; s <= 1; s += 2) k.Cap(new Vector3(s * .09f, -.01f, -.1f), .035f, .32f, Black, new Vector3(8, 0, s * 6));
                k.Ring(new Vector3(0, .19f, -.02f), .09f, .018f, Black, new Vector3(90, 0, 0));
                k.Ring(new Vector3(.12f, -.02f, .13f), .04f, .008f, Silver, new Vector3(0, 0, 90), Surface.Metal);
                k.Ball(new Vector3(.12f, -.06f, .13f), .035f, Green);
                break;
            case "part_bungee": // stripy stretch cord with two hungry hooks
                for (int i = 0; i < 7; i++) k.Cyl(new Vector3(-.15f + i * .05f, Mathf.Sin(i * .9f) * .025f, 0), .04f, .052f, i % 2 == 0 ? c : Black, new Vector3(0, 0, 90));
                for (int s = -1; s <= 1; s += 2)
                {
                    k.Cyl(new Vector3(s * .195f, 0, 0), .05f, .04f, Silver, new Vector3(0, 0, 90), Surface.Metal);
                    k.Ring(new Vector3(s * .25f, .0f, 0), .085f, .016f, Silver, new Vector3(90, 0, 0), Surface.Metal);
                    k.Box(new Vector3(s * .25f, .04f, 0), new Vector3(.05f, .03f, .03f), DarkGrey);
                }
                break;
            case "part_screwjar": // glass jar, rusty lid, a heap of mismatched screws inside
                k.Cyl(Vector3.zero, .2f, .26f, Clear, s: Surface.Glass);
                k.Cyl(new Vector3(0, -.06f, 0), .18f, .13f, c, s: Surface.Metal);
                k.Cyl(new Vector3(0, .15f, 0), .21f, .045f, Copper, s: Surface.Metal);
                for (int i = 0; i < 6; i++)
                {
                    float a = i * 60f * Mathf.Deg2Rad;
                    k.Cap(new Vector3(Mathf.Cos(a) * .05f, .02f + (i % 3) * .012f, Mathf.Sin(a) * .05f), .014f, .08f, i % 2 == 0 ? Silver : Gold, new Vector3(70 + i * 9, i * 50, 0), Surface.Metal);
                    k.Cyl(new Vector3(Mathf.Cos(a) * .075f, .035f, Mathf.Sin(a) * .075f), .03f, .008f, DarkGrey, new Vector3(i * 30, 0, 20));
                }
                k.Box(new Vector3(0, -.02f, .101f), new Vector3(.1f, .06f, .004f), OffWhite);
                break;
            case "part_hairpins": // a card of bobby pins, two already bent into picks
                k.Box(Vector3.zero, new Vector3(.2f, .012f, .3f), Pink);
                k.Box(new Vector3(0, .008f, .12f), new Vector3(.2f, .006f, .05f), White);
                for (int i = 0; i < 5; i++)
                {
                    float x = -.07f + i * .035f;
                    k.Cap(new Vector3(x, .014f, -.02f), .01f, .2f, c, new Vector3(90, 0, 0), Surface.Metal);
                    k.Cap(new Vector3(x + .009f, .02f, -.02f), .008f, .19f, c, new Vector3(90, 3, 0), Surface.Metal);
                    k.Ball(new Vector3(x + .005f, .016f, -.12f), .016f, c);
                }
                k.Cap(new Vector3(.03f, .035f, .0f), .01f, .21f, Silver, new Vector3(75, 40, 0), Surface.Metal);
                k.Cap(new Vector3(-.03f, .04f, .03f), .01f, .17f, Silver, new Vector3(60, -55, 0), Surface.Metal);
                break;
            case "part_hinge": // butt hinge mid-squeak, screws half out
                k.Box(new Vector3(-.085f, 0, 0), new Vector3(.15f, .016f, .26f), c, new Vector3(0, 0, 14), Surface.Metal);
                k.Box(new Vector3(.085f, 0, 0), new Vector3(.15f, .016f, .26f), c, new Vector3(0, 0, -14), Surface.Metal);
                for (int i = 0; i < 5; i++) k.Cyl(new Vector3(0, .02f, -.104f + i * .052f), .04f, .05f, i % 2 == 0 ? c : Mul(c, .8f), new Vector3(90, 0, 0), Surface.Metal);
                k.Cyl(new Vector3(0, .02f, 0), .018f, .3f, Silver, new Vector3(90, 0, 0), Surface.Metal);
                for (int s = -1; s <= 1; s += 2) for (int z = -1; z <= 1; z++)
                    k.Cyl(new Vector3(s * .1f, .02f - s * s * .004f + (z == 0 ? .012f : 0f), z * .085f), .03f, .014f, DarkGrey, s: Surface.Metal);
                break;
            case "part_fan": // little desk fan: cage, three blades, a wobbly neck
                k.Cyl(new Vector3(0, -.2f, 0), .22f, .04f, DarkGrey);
                k.Cyl(new Vector3(0, -.12f, -.02f), .04f, .14f, Silver, new Vector3(-8, 0, 0), Surface.Metal);
                k.Ball(new Vector3(0, .03f, -.04f), new Vector3(.11f, .11f, .14f), c);
                k.Ring(new Vector3(0, .03f, .04f), .34f, .014f, Silver, new Vector3(90, 0, 0), Surface.Metal);
                k.Ring(new Vector3(0, .03f, .07f), .22f, .012f, Silver, new Vector3(90, 0, 0), Surface.Metal);
                for (int i = 0; i < 3; i++) { float a = i * 120f; k.Ball(new Vector3(Mathf.Cos(a * Mathf.Deg2Rad) * .075f, .03f + Mathf.Sin(a * Mathf.Deg2Rad) * .075f, .035f), new Vector3(.15f, .075f, .012f), White, new Vector3(0, 18, a)); }
                k.Ball(new Vector3(0, .03f, .055f), .05f, c);
                k.Box(new Vector3(.06f, -.19f, .08f), new Vector3(.04f, .03f, .03f), Red);
                break;
            case "part_helmet": // dino-sticker bike helmet with vents and a chin strap
                k.Ball(Vector3.zero, new Vector3(.3f, .22f, .36f), c);
                k.Box(new Vector3(0, -.075f, 0), new Vector3(.31f, .08f, .37f), Black);
                for (int i = -1; i <= 1; i++) k.Ball(new Vector3(i * .075f, .085f, .02f), new Vector3(.03f, .03f, .2f), Black);
                k.Box(new Vector3(0, -.03f, .19f), new Vector3(.2f, .02f, .07f), Mul(c, .7f), new Vector3(-12, 0, 0));
                for (int i = 0; i < 4; i++) k.Cone(new Vector3(0, .12f, -.1f + i * .06f), .045f, .06f, Green);
                for (int s = -1; s <= 1; s += 2) k.Cap(new Vector3(s * .12f, -.15f, .02f), .014f, .14f, DarkGrey, new Vector3(0, 0, s * 28));
                k.Box(new Vector3(0, -.2f, .02f), new Vector3(.05f, .025f, .03f), Yellow);
                break;
            case "part_firstaid": // white tin, red cross, one plaster hanging out
                k.Box(Vector3.zero, new Vector3(.32f, .12f, .22f), c, s: Surface.Metal);
                k.Box(new Vector3(0, .065f, 0), new Vector3(.33f, .02f, .23f), Mul(c, .9f), s: Surface.Metal);
                k.Box(new Vector3(0, .078f, 0), new Vector3(.12f, .006f, .04f), Red);
                k.Box(new Vector3(0, .078f, 0), new Vector3(.04f, .006f, .12f), Red);
                k.Box(new Vector3(0, .02f, .112f), new Vector3(.05f, .03f, .012f), Silver, s: Surface.Metal);
                for (int s = -1; s <= 1; s += 2) k.Cyl(new Vector3(s * .1f, .055f, -.112f), .02f, .04f, Silver, new Vector3(0, 0, 90), Surface.Metal);
                k.Box(new Vector3(.13f, .05f, .08f), new Vector3(.1f, .006f, .03f), Skin, new Vector3(0, 30, -25));
                k.Box(new Vector3(.13f, .053f, .08f), new Vector3(.03f, .006f, .025f), White, new Vector3(0, 30, -25));
                break;
            case "part_collar": // red collar chewed half through, bone tag
                k.Ring(Vector3.zero, .3f, .045f, c);
                k.Box(new Vector3(0, 0, .15f), new Vector3(.07f, .05f, .03f), Silver, s: Surface.Metal);
                k.Box(new Vector3(0, 0, .15f), new Vector3(.035f, .055f, .02f), Black);
                k.Ring(new Vector3(.06f, -.03f, .14f), .04f, .008f, Silver, new Vector3(0, 0, 90), Surface.Metal);
                k.Cap(new Vector3(.06f, -.08f, .14f), .03f, .09f, Gold, new Vector3(0, 0, 90), Surface.Metal);
                for (int s = -1; s <= 1; s += 2) for (int t = -1; t <= 1; t += 2) k.Ball(new Vector3(.06f + s * .04f, -.08f + t * .012f, .14f), .03f, Gold, Surface.Metal);
                for (int i = 0; i < 3; i++) k.Cone(new Vector3(-.13f + i * .02f, .02f, -.07f - i * .012f), .02f, .035f, Mul(c, .6f), new Vector3(180, 0, i * 20));
                break;
            case "part_carrier": // pet carrier: grille door, top handle, air slots, scratch marks
                k.Box(new Vector3(0, -.03f, 0), new Vector3(.3f, .22f, .4f), c);
                k.Ball(new Vector3(0, .07f, 0), new Vector3(.3f, .12f, .4f), Mul(c, .9f));
                k.Box(new Vector3(0, -.03f, .201f), new Vector3(.2f, .16f, .01f), Black);
                for (int i = -2; i <= 2; i++) k.Cyl(new Vector3(i * .04f, -.03f, .207f), .012f, .16f, Silver, s: Surface.Metal);
                k.Cyl(new Vector3(0, -.03f, .21f), .012f, .2f, Silver, new Vector3(0, 0, 90), Surface.Metal);
                k.Ring(new Vector3(0, .13f, 0), .12f, .022f, DarkGrey, new Vector3(0, 0, 90));
                for (int s = -1; s <= 1; s += 2) for (int i = -1; i <= 1; i++) k.Box(new Vector3(s * .151f, 0, i * .09f), new Vector3(.006f, .09f, .03f), Black);
                for (int i = 0; i < 3; i++) k.Box(new Vector3(-.05f + i * .025f, -.02f, .214f), new Vector3(.006f, .1f, .004f), White, new Vector3(0, 0, 18));
                break;
            case "part_chickenwire": // a roll of wire mesh with a ragged end
                k.Cyl(Vector3.zero, .2f, .34f, new Color(c.r, c.g, c.b, .35f), new Vector3(0, 0, 90), Surface.Glass);
                for (int i = 0; i < 6; i++) k.Ring(new Vector3(-.15f + i * .06f, 0, 0), .21f, .012f, c, new Vector3(0, 0, 90), Surface.Metal);
                for (int i = 0; i < 6; i++) { float a = i * 60f * Mathf.Deg2Rad; k.Cyl(new Vector3(0, Mathf.Cos(a) * .1f, Mathf.Sin(a) * .1f), .01f, .34f, c, new Vector3(0, 0, 90), Surface.Metal); }
                k.Ring(new Vector3(0, 0, 0), .1f, .012f, DarkGrey, new Vector3(0, 0, 90), Surface.Metal);
                for (int i = 0; i < 4; i++) k.Cap(new Vector3(-.13f + i * .085f, -.105f - (i % 2) * .02f, .06f), .008f, .09f, c, new Vector3(70, i * 30, 0), Surface.Metal);
                k.Box(new Vector3(.02f, .0f, .1f), new Vector3(.05f, .05f, .006f), Yellow, new Vector3(0, 0, 8));
                break;
            case "part_tongs": // springy kitchen tongs with scalloped ends
                for (int s = -1; s <= 1; s += 2)
                {
                    k.Box(new Vector3(0, s * .03f, 0), new Vector3(.4f, .012f, .05f), c, new Vector3(0, 0, s * 7), Surface.Metal);
                    k.Ball(new Vector3(.21f, s * .055f, 0), new Vector3(.07f, .02f, .07f), c, new Vector3(0, 0, s * 7), Surface.Metal);
                    for (int i = -1; i <= 1; i++) k.Ball(new Vector3(.245f, s * .058f, i * .022f), .02f, c, Surface.Metal);
                    k.Box(new Vector3(-.1f, s * .02f, 0), new Vector3(.14f, .016f, .055f), Red, new Vector3(0, 0, s * 7));
                }
                k.Cyl(new Vector3(-.2f, 0, 0), .05f, .055f, DarkGrey, new Vector3(90, 0, 0));
                k.Ring(new Vector3(-.23f, 0, 0), .05f, .01f, Silver, new Vector3(90, 0, 0), Surface.Metal);
                break;
            case "part_mitt": // lobster-claw oven mitt with a scorch mark
                k.Ball(new Vector3(0, .05f, 0), new Vector3(.22f, .28f, .1f), c);
                k.Ball(new Vector3(.13f, .0f, 0), new Vector3(.1f, .16f, .09f), c, new Vector3(0, 0, -35));
                k.Box(new Vector3(0, -.13f, 0), new Vector3(.2f, .1f, .09f), White);
                for (int i = 0; i < 3; i++) k.Box(new Vector3(0, -.1f - i * .03f, .046f), new Vector3(.2f, .008f, .004f), Mul(c, .7f));
                k.Ball(new Vector3(-.03f, .11f, .05f), new Vector3(.09f, .07f, .01f), Black);
                for (int s = -1; s <= 1; s += 2) k.Ball(new Vector3(s * .04f, .02f, .052f), .025f, White);
                for (int s = -1; s <= 1; s += 2) k.Ball(new Vector3(s * .04f, .02f, .062f), .012f, Black);
                k.Ring(new Vector3(.08f, -.18f, 0), .05f, .01f, Yellow, new Vector3(90, 0, 0));
                break;
            case "part_bucket": // blue bucket, wire handle, a crack and a drip
                k.Lathe("cup", Vector3.zero, new Vector3(.3f, .28f, .3f), c);
                k.Ring(new Vector3(0, .14f, 0), .31f, .018f, Mul(c, .8f));
                k.Cyl(new Vector3(0, .1f, 0), .26f, .01f, SkyBlue);
                k.Ring(new Vector3(0, .15f, 0), .3f, .01f, Silver, new Vector3(0, 0, 90), Surface.Metal);
                for (int s = -1; s <= 1; s += 2) k.Ball(new Vector3(s * .15f, .13f, 0), .03f, Silver, Surface.Metal);
                k.Box(new Vector3(.05f, -.02f, .125f), new Vector3(.008f, .15f, .01f), Black, new Vector3(-9, 0, 14));
                k.Box(new Vector3(.065f, -.06f, .12f), new Vector3(.008f, .06f, .01f), Black, new Vector3(-9, 0, -30));
                k.Ball(new Vector3(.07f, -.14f, .12f), new Vector3(.03f, .045f, .03f), SkyBlue);
                break;
            case "part_strollerwheel": // fat grey tyre, bright hub, a snapped axle stub
                k.Ring(Vector3.zero, .36f, .09f, c, new Vector3(0, 0, 90));
                k.Cyl(Vector3.zero, .2f, .05f, White, new Vector3(0, 0, 90));
                for (int i = 0; i < 5; i++) k.Box(Vector3.zero, new Vector3(.03f, .26f, .03f), Orange, new Vector3(i * 36f, 0, 0));
                k.Cyl(Vector3.zero, .08f, .09f, Orange, new Vector3(0, 0, 90));
                k.Cyl(new Vector3(.09f, 0, 0), .03f, .12f, Silver, new Vector3(0, 0, 90), Surface.Metal);
                k.Ball(new Vector3(.15f, 0, 0), .04f, Silver, Surface.Metal);
                for (int i = 0; i < 8; i++) { float a = i * 45f * Mathf.Deg2Rad; k.Box(new Vector3(0, Mathf.Cos(a) * .178f, Mathf.Sin(a) * .178f), new Vector3(.1f, .012f, .03f), Black, new Vector3(i * 45f, 0, 0)); }
                break;
            case "part_scooter": // kick scooter: deck, two wheels, T-bar with tassels
                k.Box(new Vector3(0, -.19f, 0), new Vector3(.1f, .02f, .42f), c, s: Surface.Metal);
                k.Box(new Vector3(0, -.178f, -.02f), new Vector3(.085f, .006f, .28f), Black);
                for (int s = -1; s <= 1; s += 2)
                {
                    k.Cyl(new Vector3(0, -.2f, s * .22f), .1f, .03f, Orange, new Vector3(0, 0, 90));
                    k.Cyl(new Vector3(0, -.2f, s * .22f), .04f, .04f, Silver, new Vector3(0, 0, 90), Surface.Metal);
                    k.Cap(new Vector3(s * .11f, .27f, .2f), .03f, .09f, Pink, new Vector3(0, 0, 90));
                    for (int i = 0; i < 3; i++) k.Cap(new Vector3(s * .16f, .24f - i * .004f, .2f + (i - 1) * .012f), .006f, .07f, i == 1 ? Yellow : Pink, new Vector3((i - 1) * 14, 0, s * 12));
                }
                k.Cyl(new Vector3(0, .04f, .2f), .028f, .48f, Silver, new Vector3(-6, 0, 0), Surface.Metal);
                k.Cyl(new Vector3(0, .27f, .2f), .024f, .22f, Silver, new Vector3(0, 0, 90), Surface.Metal);
                k.Box(new Vector3(0, -.16f, -.2f), new Vector3(.07f, .03f, .08f), c, new Vector3(-30, 0, 0), Surface.Metal);
                break;
            case "part_yogamat": // rolled mat with a spiral end and a carry strap
                k.Cyl(Vector3.zero, .2f, .42f, c, new Vector3(0, 0, 90));
                for (int s = -1; s <= 1; s += 2)
                {
                    k.Ring(new Vector3(s * .212f, 0, 0), .19f, .014f, Mul(c, .7f), new Vector3(0, 0, 90));
                    k.Ring(new Vector3(s * .212f, 0, 0), .12f, .012f, Mul(c, .7f), new Vector3(0, 0, 90));
                    k.Ball(new Vector3(s * .212f, 0, 0), .05f, Mul(c, .7f));
                    k.Ring(new Vector3(s * .12f, 0, 0), .215f, .02f, Black, new Vector3(0, 0, 90));
                }
                k.Ring(new Vector3(0, .1f, 0), .26f, .016f, Black, new Vector3(90, 0, 0));
                k.Box(new Vector3(.02f, -.0f, .101f), new Vector3(.3f, .012f, .012f), Mul(c, 1.3f), new Vector3(0, 0, 0));
                break;
            case "part_babymonitor": // egg-shaped monitor, speaker grille, antenna, one green light
                k.Ball(Vector3.zero, new Vector3(.22f, .3f, .14f), c);
                k.Cyl(new Vector3(0, -.15f, 0), .16f, .03f, Grey);
                k.Cyl(new Vector3(0, .03f, .06f), .13f, .03f, Grey, new Vector3(90, 0, 0));
                for (int i = -1; i <= 1; i++) k.Box(new Vector3(0, .03f + i * .03f, .078f), new Vector3(.09f - Mathf.Abs(i) * .03f, .01f, .004f), DarkGrey);
                k.Cyl(new Vector3(.07f, .2f, 0), .016f, .16f, DarkGrey, new Vector3(0, 0, -10));
                k.Ball(new Vector3(.084f, .28f, 0), .03f, DarkGrey);
                k.Ball(new Vector3(0, -.08f, .062f), .03f, Green, Surface.Emissive, 2f);
                for (int i = 0; i < 3; i++) k.Ball(new Vector3(-.05f + i * .05f, .115f, .058f), .016f, i == 0 ? Red : Yellow, Surface.Emissive, 1.2f);
                break;
            case "part_tracker": // wrist band with a tiny judgemental screen
                k.Ring(Vector3.zero, .3f, .05f, c, new Vector3(90, 0, 0));
                k.Box(new Vector3(0, .14f, 0), new Vector3(.14f, .035f, .08f), Black);
                k.Box(new Vector3(0, .16f, 0), new Vector3(.11f, .006f, .06f), SkyBlue, s: Surface.Emissive, glow: 1.4f);
                for (int i = 0; i < 3; i++) k.Box(new Vector3(-.03f + i * .03f, .166f, -.012f + i * .006f), new Vector3(.016f, .004f, .012f + i * .012f), Green);
                k.Box(new Vector3(0, -.14f, 0), new Vector3(.05f, .03f, .07f), Silver, s: Surface.Metal);
                for (int i = 0; i < 4; i++) { float a = (200f + i * 14f) * Mathf.Deg2Rad; k.Ball(new Vector3(Mathf.Cos(a) * .15f, Mathf.Sin(a) * .15f, .028f), .016f, DarkGrey); }
                k.Ball(new Vector3(.08f, .145f, 0), .02f, Red);
                break;
            default: return false;
        }
        return true;
    }
}
