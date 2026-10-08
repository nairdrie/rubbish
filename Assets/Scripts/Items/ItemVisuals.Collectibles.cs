using UnityEngine;

public static partial class ItemVisuals
{
    // Bespoke display-sized silhouettes, built with the same cached meshes/material blocks as all
    // existing junk. These are real in-world models, so shelves and ebag photos agree automatically.
    static bool BuildCollectible(Kit k, string key)
    {
        Color c = k.c;
        switch (key)
        {
            case "find_pigeon":
                k.Cyl(new Vector3(0, -.15f, 0), .28f, .04f, DarkGreen);
                for (int i = 0; i < 4; i++) k.Ring(new Vector3(0, -.12f + i * .018f, 0), .07f, .012f, Silver, s: Surface.Metal);
                k.Ball(Vector3.zero, new Vector3(.16f, .18f, .23f), c);
                k.Ball(new Vector3(0, .15f, .065f), .15f, c);
                k.Ring(new Vector3(0, .073f, .045f), .09f, .018f, Purple);
                k.Cone(new Vector3(0, .13f, .16f), .045f, .085f, Orange, new Vector3(90, 0, 0));
                FindEyes(k, new Vector3(0, .18f, .13f), .05f, .021f);
                k.Box(new Vector3(0, -.015f, -.15f), new Vector3(.085f, .025f, .14f), DarkGrey, new Vector3(-20, 0, 0));
                break;
            case "find_snail":
                k.Cap(new Vector3(0, -.065f, 0), .11f, .35f, Green, new Vector3(90, 0, 0));
                k.Ball(new Vector3(0, .035f, -.045f), .22f, c);
                for (int i = 0; i < 3; i++) k.Ring(new Vector3(.096f + i * .009f, .035f, -.045f), .17f - i * .045f, .018f, Brown, new Vector3(0, 0, 90));
                for (int s = -1; s <= 1; s += 2)
                {
                    k.Cap(new Vector3(s * .038f, .015f, .12f), .018f, .12f, Green, new Vector3(15, 0, s * -14));
                    k.Ball(new Vector3(s * .05f, .074f, .13f), .038f, White);
                    k.Ball(new Vector3(s * .05f, .078f, .147f), .018f, Black);
                }
                k.Box(new Vector3(-.13f, .045f, -.045f), new Vector3(.1f, .016f, .024f), Silver, s: Surface.Metal);
                for (int s = -1; s <= 1; s += 2) k.Ring(new Vector3(-.18f, .045f + s * .025f, -.045f), .058f, .014f, Silver, new Vector3(90, 0, 0), Surface.Metal);
                break;
            case "find_cactus":
                k.Lathe("pot", new Vector3(0, -.11f, 0), new Vector3(.19f, .15f, .19f), Orange);
                k.Cyl(new Vector3(0, -.04f, 0), .16f, .025f, Brown);
                k.Cap(new Vector3(0, .09f, 0), .09f, .28f, c);
                for (int s = -1; s <= 1; s += 2)
                {
                    k.Cap(new Vector3(s * .055f, .055f, 0), .047f, .13f, c, new Vector3(0, 0, 90));
                    k.Cap(new Vector3(s * .102f, .10f, 0), .047f, .13f, c);
                    k.Box(new Vector3(s * .027f, .15f, .042f), new Vector3(.047f, .028f, .018f), Black);
                }
                k.Box(new Vector3(0, .15f, .049f), new Vector3(.026f, .007f, .009f), Gold);
                k.Ball(new Vector3(.025f, .225f, 0), .05f, Pink);
                break;
            case "find_cradle":
                k.Box(new Vector3(0, -.15f, 0), new Vector3(.34f, .04f, .22f), Brown);
                for (int z = -1; z <= 1; z += 2)
                {
                    for (int x = -1; x <= 1; x += 2) k.Cyl(new Vector3(x * .14f, .015f, z * .07f), .014f, .3f, Silver, s: Surface.Metal);
                    k.Cyl(new Vector3(0, .16f, z * .07f), .014f, .29f, Silver, new Vector3(0, 0, 90), Surface.Metal);
                }
                for (int i = 0; i < 5; i++)
                {
                    float x = (i - 2) * .051f;
                    k.Ball(new Vector3(x, -.055f, 0), .05f, c, Surface.Metal);
                    for (int z = -1; z <= 1; z += 2) k.Cap(new Vector3(x, .045f, z * .035f), .004f, .22f, DarkGrey, new Vector3(z * 18, 0, 0));
                }
                break;
            case "find_lighthouse":
                k.Ball(new Vector3(0, -.18f, 0), new Vector3(.31f, .08f, .27f), Grey);
                k.Cone(Vector3.zero, .18f, .34f, White);
                for (int i = 0; i < 3; i++) k.Cyl(new Vector3(0, -.1f + i * .09f, 0), .16f - i * .035f, .038f, c);
                k.Cyl(new Vector3(0, .19f, 0), .12f, .08f, SkyBlue, s: Surface.Emissive, glow: .6f);
                k.Cone(new Vector3(0, .26f, 0), .19f, .085f, c);
                k.Ring(new Vector3(0, .14f, 0), .19f, .018f, Black);
                k.Box(new Vector3(0, -.12f, .085f), new Vector3(.04f, .08f, .014f), Brown);
                break;
            case "find_frog":
                k.Ball(Vector3.zero, new Vector3(.23f, .16f, .19f), c);
                for (int s = -1; s <= 1; s += 2)
                {
                    k.Ball(new Vector3(s * .13f, -.05f, .035f), new Vector3(.1f, .065f, .15f), c);
                    k.Ball(new Vector3(s * .065f, .092f, .047f), .09f, c);
                }
                FindEyes(k, new Vector3(0, .11f, .082f), .13f, .045f);
                k.Box(new Vector3(0, .014f, .092f), new Vector3(.12f, .008f, .012f), DarkGreen);
                k.Cyl(new Vector3(0, .13f, -.01f), .07f, .04f, Gold, s: Surface.Metal);
                for (int i = 0; i < 3; i++) k.Cone(new Vector3((i - 1) * .025f, .169f, -.01f), .023f, .05f, Gold, s: Surface.Metal);
                break;
            case "find_shellclock":
                k.Box(new Vector3(0, -.16f, 0), new Vector3(.25f, .035f, .13f), Tan);
                for (int i = -3; i <= 3; i++)
                    k.Cap(new Vector3(i * .024f, .015f + (3 - Mathf.Abs(i)) * .009f, 0), .068f, .25f, Mix(c, White, (i + 3) * .07f), new Vector3(0, 0, -i * 14));
                FindClock(k, new Vector3(0, -.04f, .043f), .13f);
                break;
            case "find_planetarium":
                k.Cyl(new Vector3(0, -.15f, 0), .23f, .06f, Brown);
                k.Cap(Vector3.zero, .015f, .24f, Silver, new Vector3(0, 0, -22));
                k.Ball(new Vector3(0, .075f, 0), .18f, c);
                k.Ring(new Vector3(0, .075f, 0), .29f, .012f, Gold, new Vector3(25, 0, 35), Surface.Metal);
                for (int i = 0; i < 5; i++)
                {
                    float a = i * 72f * Mathf.Deg2Rad;
                    k.Ball(new Vector3(Mathf.Cos(a) * .14f, .075f + Mathf.Sin(a) * .075f, Mathf.Sin(a) * .08f), .025f, i % 2 == 0 ? Orange : White);
                }
                break;
            case "find_phone":
                k.Box(new Vector3(0, -.08f, 0), new Vector3(.29f, .11f, .24f), c, new Vector3(-12, 0, 0));
                k.Ring(new Vector3(0, -.009f, .037f), .12f, .024f, White, new Vector3(-12, 0, 0));
                for (int i = 0; i < 9; i++)
                {
                    float a = i * 40 * Mathf.Deg2Rad;
                    k.Ball(new Vector3(Mathf.Cos(a) * .049f, .005f, .037f + Mathf.Sin(a) * .049f), .014f, Black);
                }
                k.Cap(new Vector3(0, .075f, -.057f), .052f, .29f, c, new Vector3(0, 0, 90));
                for (int s = -1; s <= 1; s += 2) k.Ball(new Vector3(s * .118f, .052f, -.057f), new Vector3(.08f, .07f, .085f), c);
                for (int i = 0; i < 7; i++) k.Ring(new Vector3(.155f, -.065f + i * .019f, -.025f), .03f, .01f, DarkGrey);
                break;
            case "find_robot":
                k.Box(Vector3.zero, new Vector3(.16f, .18f, .09f), c, s: Surface.Metal);
                k.Box(new Vector3(0, .16f, 0), new Vector3(.17f, .12f, .11f), c, s: Surface.Metal);
                FindEyes(k, new Vector3(0, .18f, .06f), .082f, .04f);
                k.Box(new Vector3(0, .12f, .064f), new Vector3(.09f, .015f, .012f), Red);
                k.Cap(new Vector3(0, .24f, 0), .012f, .06f, Silver);
                k.Ball(new Vector3(0, .275f, 0), .038f, Red);
                for (int s = -1; s <= 1; s += 2)
                {
                    k.Cap(new Vector3(s * .12f, -.01f, 0), .05f, .15f, Silver, new Vector3(0, 0, s * 12));
                    k.Box(new Vector3(s * .051f, -.15f, .015f), new Vector3(.07f, .09f, .12f), c);
                }
                k.Cyl(new Vector3(0, .01f, .051f), .055f, .018f, Orange, new Vector3(90, 0, 0));
                break;
            case "find_bottleship":
                k.Cap(Vector3.zero, .19f, .38f, Clear, new Vector3(0, 0, 90), Surface.Glass);
                k.Cyl(new Vector3(.22f, 0, 0), .078f, .09f, Tan, new Vector3(0, 0, 90));
                k.Box(new Vector3(0, -.13f, 0), new Vector3(.28f, .055f, .13f), Brown);
                k.Ball(new Vector3(0, -.04f, 0), new Vector3(.24f, .055f, .055f), Brown);
                for (int i = 0; i < 2; i++)
                {
                    float x = -.06f + i * .1f;
                    k.Cyl(new Vector3(x, .013f, 0), .009f, .13f, Brown);
                    k.Box(new Vector3(x + .027f, .035f, 0), new Vector3(.048f, .065f, .009f), White, new Vector3(0, 0, -10));
                }
                break;
            case "find_accordion":
                for (int i = 0; i < 7; i++) k.Box(new Vector3((i - 3) * .023f, 0, 0), new Vector3(.011f, .2f + (i % 2) * .02f, .12f), i % 2 == 0 ? Tan : DarkGrey);
                for (int s = -1; s <= 1; s += 2) k.Box(new Vector3(s * .12f, 0, 0), new Vector3(.066f, .23f, .15f), c);
                for (int i = 0; i < 7; i++)
                {
                    k.Box(new Vector3(-.12f, .09f - i * .03f, .081f), new Vector3(.048f, .026f, .015f), White);
                    if (i < 6) k.Box(new Vector3(-.102f, .075f - i * .03f, .095f), new Vector3(.025f, .011f, .013f), Black);
                    k.Ball(new Vector3(.12f, .09f - i * .03f, .084f), .014f, White);
                }
                break;
            case "find_mushroom":
                k.Cyl(new Vector3(0, -.14f, 0), .23f, .045f, DarkGrey);
                k.Cap(new Vector3(0, -.015f, 0), .085f, .23f, White);
                k.Lathe("dome", new Vector3(0, .13f, 0), new Vector3(.34f, .14f, .34f), c, s: Surface.Emissive, glow: .65f);
                for (int i = 0; i < 6; i++)
                {
                    float a = i * 60f * Mathf.Deg2Rad;
                    k.Ball(new Vector3(Mathf.Cos(a) * .09f, .171f, Mathf.Sin(a) * .09f), .043f, White);
                }
                break;
            case "find_helmet":
                k.Ball(Vector3.zero, new Vector3(.28f, .3f, .27f), c);
                k.Ball(new Vector3(0, .01f, .107f), new Vector3(.23f, .19f, .095f), H(0x3B526B), s: Surface.Metal);
                k.Ring(new Vector3(0, -.128f, 0), .22f, .035f, Grey);
                for (int s = -1; s <= 1; s += 2) k.Cyl(new Vector3(s * .15f, 0, 0), .093f, .048f, Silver, new Vector3(0, 0, 90), Surface.Metal);
                k.Box(new Vector3(.032f, .042f, .155f), new Vector3(.07f, .011f, .008f), White, new Vector3(0, 0, 25));
                k.Box(new Vector3(0, -.17f, 0), new Vector3(.21f, .04f, .17f), DarkGrey);
                break;
            case "find_sextant":
                for (int i = 0; i < 13; i++)
                {
                    float a = (-64 + i * 10) * Mathf.Deg2Rad;
                    k.Box(new Vector3(Mathf.Sin(a) * .14f, -.13f + Mathf.Cos(a) * -.14f, 0), new Vector3(.032f, .016f, .018f), c, new Vector3(0, 0, -a * Mathf.Rad2Deg), Surface.Metal);
                }
                for (int s = -1; s <= 1; s += 2) k.Cap(new Vector3(s * .065f, -.11f, 0), .018f, .24f, c, new Vector3(0, 0, s * -35));
                k.Cap(new Vector3(0, -.13f, .012f), .012f, .27f, Silver, new Vector3(0, 0, -12));
                k.Cyl(new Vector3(.075f, -.08f, .035f), .042f, .17f, c, new Vector3(0, 0, 75), Surface.Metal);
                k.Box(new Vector3(0, -.285f, 0), new Vector3(.29f, .035f, .11f), Brown);
                break;
            case "find_jukebox":
                k.Box(new Vector3(0, -.08f, 0), new Vector3(.29f, .25f, .14f), c);
                k.Ball(new Vector3(0, .07f, 0), new Vector3(.29f, .27f, .14f), c);
                k.Ring(new Vector3(0, .072f, .081f), .23f, .026f, SkyBlue, new Vector3(90, 0, 0), Surface.Emissive, .7f);
                k.Box(new Vector3(0, -.08f, .081f), new Vector3(.20f, .16f, .018f), Tan);
                for (int i = -3; i <= 3; i++) k.Box(new Vector3(i * .026f, -.09f, .095f), new Vector3(.009f, .13f, .012f), DarkGrey);
                k.Box(new Vector3(0, .057f, .101f), new Vector3(.15f, .05f, .017f), Black);
                for (int i = -2; i <= 2; i++) k.Box(new Vector3(i * .026f, .055f, .115f), new Vector3(.018f, .035f, .01f), White);
                break;
            case "find_orrery":
                k.Cyl(new Vector3(0, -.15f, 0), .24f, .06f, Brown);
                k.Cyl(new Vector3(0, -.015f, 0), .025f, .24f, c, s: Surface.Metal);
                k.Ball(new Vector3(0, .12f, 0), .105f, Gold, Surface.Metal);
                for (int i = 0; i < 4; i++)
                {
                    float a = i * 103f * Mathf.Deg2Rad, r = .1f + i * .035f, y = .03f - i * .017f;
                    k.Ring(new Vector3(0, y, 0), r * 2f, .009f, c, s: Surface.Metal);
                    k.Cap(new Vector3(Mathf.Cos(a) * r * .5f, y, Mathf.Sin(a) * r * .5f), .008f, r, c, new Vector3(90, -a * Mathf.Rad2Deg + 90, 0));
                    k.Ball(new Vector3(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r), .036f + i * .008f, i % 2 == 0 ? Blue : Orange);
                }
                break;
            case "find_meteorite":
                k.Box(new Vector3(0, -.15f, 0), new Vector3(.30f, .055f, .16f), c);
                for (int s = -1; s <= 1; s += 2)
                {
                    k.Box(new Vector3(s * .12f, .03f, 0), new Vector3(.055f, .31f, .09f), c);
                    k.Box(new Vector3(s * .072f, .155f, 0), new Vector3(.08f, .05f, .09f), Silver, s: Surface.Metal);
                }
                k.Ball(new Vector3(0, .023f, 0), new Vector3(.095f, .105f, .075f), Grey, new Vector3(12, 28, 24), Surface.Metal);
                for (int i = 0; i < 4; i++) k.Ball(new Vector3((i % 2 - .5f) * .037f, (i / 2 - .5f) * .047f + .023f, .04f), .015f, SkyBlue, Surface.Emissive, 1.2f);
                break;
            case "find_skull":
                k.Cyl(new Vector3(0, -.14f, 0), .26f, .035f, Black);
                k.Ball(new Vector3(0, .026f, 0), new Vector3(.23f, .24f, .18f), c, s: Surface.Metal);
                k.Box(new Vector3(0, -.07f, .048f), new Vector3(.14f, .10f, .10f), c);
                for (int s = -1; s <= 1; s += 2) k.Ball(new Vector3(s * .058f, .017f, .083f), new Vector3(.065f, .07f, .023f), H(0x174C50));
                k.Cone(new Vector3(0, -.041f, .105f), .038f, .055f, DarkGrey, new Vector3(0, 0, 180));
                for (int i = 0; i < 6; i++) k.Box(new Vector3((i - 2.5f) * .021f, -.09f, .105f), new Vector3(.017f, .031f, .018f), White);
                break;
            case "find_songbird":
                k.Cyl(new Vector3(0, -.15f, 0), .28f, .045f, Brown);
                k.Cyl(new Vector3(0, -.03f, 0), .016f, .20f, c, s: Surface.Metal);
                k.Cap(new Vector3(0, .06f, 0), .015f, .22f, c, new Vector3(0, 0, 90));
                k.Ball(new Vector3(0, .13f, 0), new Vector3(.16f, .13f, .23f), c, s: Surface.Metal);
                k.Ball(new Vector3(0, .21f, .10f), .10f, c, Surface.Metal);
                k.Cone(new Vector3(0, .20f, .17f), .04f, .075f, Orange, new Vector3(90, 0, 0));
                FindEyes(k, new Vector3(0, .232f, .13f), .051f, .022f);
                for (int s = -1; s <= 1; s += 2)
                    for (int i = 0; i < 4; i++) k.Cap(new Vector3(s * (.09f + i * .025f), .13f + i * .022f, -.015f - i * .015f), .028f, .17f - i * .018f, i % 2 == 0 ? Gold : SkyBlue, new Vector3(65, 0, s * -45));
                break;
            case "find_submersible":
                k.Cap(Vector3.zero, .21f, .38f, c, new Vector3(90, 0, 0));
                k.Ball(new Vector3(0, .018f, .175f), new Vector3(.14f, .13f, .07f), SkyBlue, s: Surface.Metal);
                k.Ring(new Vector3(0, .018f, .197f), .15f, .018f, Grey, new Vector3(90, 0, 0), Surface.Metal);
                k.Cyl(new Vector3(0, .145f, 0), .022f, .13f, Grey);
                k.Cyl(new Vector3(0, .20f, .025f), .035f, .075f, Grey, new Vector3(90, 0, 0));
                for (int s = -1; s <= 1; s += 2)
                {
                    k.Cap(new Vector3(s * .095f, -.125f, 0), .027f, .36f, DarkGrey, new Vector3(90, 0, 0));
                    k.Cyl(new Vector3(s * .13f, 0, -.085f), .075f, .075f, Grey, new Vector3(90, 0, 0));
                    k.Box(new Vector3(s * .13f, 0, -.128f), new Vector3(.064f, .01f, .011f), Black, new Vector3(0, 0, s * 30));
                }
                break;
            case "find_alienteapot":
                k.Ball(Vector3.zero, new Vector3(.28f, .13f, .26f), c, s: Surface.Metal);
                k.Ball(new Vector3(0, .085f, 0), new Vector3(.13f, .095f, .13f), Purple, s: Surface.Metal);
                k.Cone(new Vector3(.17f, .035f, 0), .062f, .16f, c, new Vector3(0, 0, -57), Surface.Metal);
                k.Ring(new Vector3(-.16f, .017f, 0), .15f, .022f, c, new Vector3(90, 0, 0), Surface.Metal);
                for (int i = 0; i < 3; i++)
                {
                    float a = (i * 120 + 30) * Mathf.Deg2Rad;
                    k.Cap(new Vector3(Mathf.Cos(a) * .087f, -.11f, Mathf.Sin(a) * .087f), .018f, .15f, Grey, new Vector3(Mathf.Sin(a) * 22, 0, -Mathf.Cos(a) * 22));
                    k.Ball(new Vector3(Mathf.Cos(a) * .13f, 0, Mathf.Sin(a) * .13f), .033f, Green, Surface.Emissive, .9f);
                }
                break;
            case "find_hourglass":
                for (int s = -1; s <= 1; s += 2)
                {
                    k.Cyl(new Vector3(0, s * .19f, 0), .27f, .045f, c, s: Surface.Metal);
                    k.Cone(new Vector3(0, s * .075f, 0), .15f, .15f, Clear, new Vector3(0, 0, s > 0 ? 180 : 0), Surface.Glass);
                    k.Cone(new Vector3(0, s * .10f, 0), .10f, .055f, Yellow, new Vector3(0, 0, s > 0 ? 0 : 180));
                    for (int x = -1; x <= 1; x += 2) k.Cap(new Vector3(x * .10f, 0, s * .075f), .022f, .36f, c);
                }
                k.Cyl(Vector3.zero, .008f, .16f, Yellow);
                k.Ball(new Vector3(0, .237f, 0), .063f, Red, Surface.Metal);
                break;
            case "find_musicbox":
                k.Box(new Vector3(0, -.10f, 0), new Vector3(.29f, .13f, .22f), c);
                k.Box(new Vector3(0, .05f, -.09f), new Vector3(.29f, .22f, .026f), c, new Vector3(-25, 0, 0));
                k.Box(new Vector3(0, -.022f, 0), new Vector3(.27f, .018f, .20f), Gold, s: Surface.Metal);
                k.Cyl(new Vector3(0, .035f, .025f), .016f, .10f, Gold);
                k.Ball(new Vector3(0, .115f, .025f), .11f, SkyBlue, Surface.Metal);
                k.Ring(new Vector3(0, .115f, .025f), .20f, .018f, Gold, new Vector3(25, 0, 20), Surface.Metal);
                for (int i = 0; i < 5; i++) k.Ball(new Vector3((i - 2) * .05f, .11f + (i % 2) * .06f, -.126f), .019f, White, Surface.Emissive, .7f);
                k.Cap(new Vector3(.19f, -.065f, 0), .016f, .11f, Gold, new Vector3(0, 0, 90));
                k.Ball(new Vector3(.24f, -.065f, 0), .036f, c);
                break;
            case "find_moneytree":
                k.Lathe("pot", new Vector3(0, -.16f, 0), new Vector3(.25f, .15f, .20f), Gold, s: Surface.Metal);
                k.Cyl(new Vector3(0, -.09f, 0), .21f, .02f, Brown);
                k.Cap(new Vector3(0, .035f, 0), .04f, .26f, Brown, new Vector3(0, 0, -16));
                for (int i = 0; i < 7; i++)
                {
                    float a = i * 137f * Mathf.Deg2Rad;
                    var leaf = new Vector3(Mathf.Cos(a) * .13f, .13f + (i % 3) * .04f, Mathf.Sin(a) * .10f);
                    k.Cap((leaf + new Vector3(0, .08f, 0)) * .5f, .012f, .15f, Brown, new Vector3(0, 0, -Mathf.Cos(a) * 60));
                    var bill = k.In(leaf, new Vector3(-30, i * 137, 12));
                    bill.Box(Vector3.zero, new Vector3(.10f, .006f, .055f), c);
                    bill.Ball(new Vector3(0, .005f, 0), new Vector3(.025f, .007f, .03f), DarkGreen);
                }
                break;
            default: return false;
        }
        return true;
    }

    static void FindEyes(Kit k, Vector3 midpoint, float spacing, float size)
    {
        for (int s = -1; s <= 1; s += 2)
        {
            var p = midpoint + Vector3.right * (spacing * .5f * s);
            k.Ball(p, size, White);
            k.Ball(p + Vector3.forward * (size * .40f), size * .52f, Black);
        }
    }

    static void FindClock(Kit k, Vector3 at, float diameter)
    {
        k.Cyl(at, diameter, .018f, Gold, new Vector3(90, 0, 0), Surface.Metal);
        k.Cyl(at + Vector3.forward * .012f, diameter * .83f, .012f, White, new Vector3(90, 0, 0));
        k.Box(at + new Vector3(0, diameter * .12f, .022f), new Vector3(.009f, diameter * .3f, .009f), Black, new Vector3(0, 0, -20));
        k.Box(at + new Vector3(diameter * .12f, 0, .022f), new Vector3(diameter * .25f, .009f, .009f), Black);
    }
}
