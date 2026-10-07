using System;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Vector product art for Scrapazon (UI Toolkit Painter2D, no textures): chunky ink outlines, googly eyes,
/// derpy props. Every element scales to its layout box. Also the review stars, the logo smile arrow and the
/// little header icons.
/// </summary>
public static class ScrapazonArt
{
    public static readonly Color Ink = new Color32(0x23, 0x1F, 0x35, 0xFF);
    public static readonly Color Orange = new Color32(0xFF, 0x99, 0x00, 0xFF);
    public static readonly Color StarOn = new Color32(0xFF, 0xA4, 0x1C, 0xFF);
    public static readonly Color StarOff = new Color32(0xE3, 0xE6, 0xE6, 0xFF);
    static readonly Color StarEdge = new Color32(0xC4, 0x55, 0x00, 0xFF);
    static readonly Color White = Color.white;
    static readonly Color Cardboard = new Color32(0xD2, 0x9B, 0x5E, 0xFF);
    static readonly Color CardboardDark = new Color32(0xB2, 0x7B, 0x42, 0xFF);
    static readonly Color Tape = new Color32(0xF1, 0xD8, 0x9A, 0xFF);
    static readonly Color Steel = new Color32(0xD0, 0xD6, 0xE0, 0xFF);
    static readonly Color TruckGreen = new Color32(0x6D, 0xA5, 0x44, 0xFF);

    // ---------------------------------------------------------------- factories

    /// <summary>Product illustration for a catalog id (class "sz-art").</summary>
    public static VisualElement Product(string id) => Canvas("sz-art", 200f, 140f, pen => DrawProduct(id, pen));

    /// <summary>The flying delivery box in the hero banner.</summary>
    public static VisualElement HeroBox() => Canvas("sz-hero-box", 240f, 140f, DrawHeroBox);

    /// <summary>A small smiling delivery box (drop-in purchase animation).</summary>
    public static VisualElement Box() => Canvas("sz-drop-box", 100f, 90f, DrawBox);

    /// <summary>Five stars, rating 0..5 (rounded to halves). Size comes from USS (class "sz-stars").</summary>
    public static VisualElement Stars(float rating, string extraClass = null)
    {
        var e = new VisualElement { pickingMode = PickingMode.Ignore };
        e.AddToClassList("sz-stars");
        if (!string.IsNullOrEmpty(extraClass)) e.AddToClassList(extraClass);
        float r = Mathf.Round(Mathf.Clamp(rating, 0f, 5f) * 2f) / 2f;
        e.generateVisualContent += mgc => Safe(() =>
        {
            var rect = e.contentRect;
            float h = rect.height;
            if (h <= 1f) return;
            var pen = new Pen(mgc, new Rect(rect.x, rect.y, h * 5.4f, h), h * 5.4f, h);
            for (int i = 0; i < 5; i++)
            {
                float fill = Mathf.Clamp01(r - i);
                pen.Star(h * 0.5f + i * h * 1.1f, h * 0.53f, h * 0.5f, fill);
            }
        });
        return e;
    }

    /// <summary>The logo's orange smile arrow (stretches to its element).</summary>
    public static VisualElement Smile(string cls = "sz-logo-smile")
    {
        var e = new VisualElement { pickingMode = PickingMode.Ignore };
        e.AddToClassList(cls);
        e.generateVisualContent += mgc => Safe(() =>
        {
            var r = e.contentRect;
            if (r.width < 4f || r.height < 2f) return;
            var p = mgc.painter2D;
            float w = Mathf.Max(2f, r.height * 0.28f);
            var a = new Vector2(r.x + r.width * 0.03f, r.y + r.height * 0.2f);
            var c = new Vector2(r.x + r.width * 0.45f, r.y + r.height * 1.05f);
            var b = new Vector2(r.x + r.width * 0.9f, r.y + r.height * 0.25f);
            p.lineCap = LineCap.Round; p.lineJoin = LineJoin.Round;
            p.strokeColor = Orange; p.lineWidth = w;
            p.BeginPath(); p.MoveTo(a); p.QuadraticCurveTo(c, b); p.Stroke();
            Vector2 dir = (b - c).normalized;
            float head = r.height * 0.55f;
            Vector2 h1 = b - (Vector2)(Quaternion.Euler(0, 0, 38f) * dir) * head;
            Vector2 h2 = b - (Vector2)(Quaternion.Euler(0, 0, -38f) * dir) * head;
            p.BeginPath(); p.MoveTo(h1); p.LineTo(b); p.LineTo(h2); p.Stroke();
        });
        return e;
    }

    public static VisualElement Magnifier() => Canvas("sz-icon-magnifier", 40f, 40f, pen =>
    {
        pen.CircleStroke(17f, 17f, 10f, Ink, 4.5f);
        pen.Line(25f, 25f, 34f, 34f, Ink, 5.5f);
    });

    public static VisualElement Cart() => Canvas("sz-icon-cart", 60f, 44f, pen =>
    {
        pen.Polyline(new[] { new Vector2(3, 6), new Vector2(12, 6), new Vector2(19, 30), new Vector2(48, 30), new Vector2(54, 12), new Vector2(15, 12) }, White, 4f);
        pen.Circle(22f, 38f, 4f, White, 0f);
        pen.Circle(45f, 38f, 4f, White, 0f);
    });

    // ---------------------------------------------------------------- products

    static void DrawProduct(string id, Pen pen)
    {
        switch (id)
        {
            case ShopIds.RabiesShot: Syringe(pen); break;
            case ShopIds.Repellent: SprayBottle(pen); break;
            case ShopIds.TruckCapacity: Truck(pen, true, false); break;
            case ShopIds.TruckTurbo: Truck(pen, false, true); break;
            case ShopIds.AirHorn: AirHorn(pen); break;
            case ShopIds.Headlamp: Headlamp(pen); break;
            case ShopIds.ConveyorBelt: Conveyor(pen); break;
            case ShopIds.MagnusBrain: Brain(pen); break;
            case ShopIds.TruckHandling: Tire(pen); break;
            case ShopIds.TruckArm: GrabbyArm(pen); break;
            case ShopIds.TruckHeadlights: Headlights(pen); break;
            case ShopIds.AutoTipper: Lever(pen); break;
            case ShopIds.SortingTable: Table(pen); break;
            case ShopIds.CompactorMk2: Compactor(pen); break;
            case ShopIds.RecyclingContracts: RecycleBin(pen); break;
            case ShopIds.HazmatContract: Barrel(pen); break;
            case ShopIds.TrophyShelf: Shelf(pen); break;
            case ShopIds.DisplayPlankShelf: PlankShelf(pen); break;
            case ShopIds.DisplayGlassCase: GlassCase(pen); break;
            case ShopIds.DisplayPedestal: Pedestal(pen); break;
            case ShopIds.DisplayVault: Vault(pen); break;
            case ShopIds.MagnusRailMotor: MagnusBot(pen, 0); break;
            case ShopIds.MagnusGripper: MagnusBot(pen, 1); break;
            case ShopIds.MagnusVision: MagnusBot(pen, 2); break;
            case ShopIds.WorkGloves: Glove(pen); break;
            case ShopIds.WorkBoots: Sneaker(pen); break;
            case ShopIds.TreasureWand: Wand(pen); break;
            case ShopIds.RabiesVaccine: Vaccine(pen); break;
            case ShopIds.HardHat: HardHat(pen); break;
            case ShopIds.BagCannon: BagCannonArt(pen); break;
            case ShopIds.GrabberPole: GrabberPoleArt(pen); break;
            case ShopIds.PermitMinivanMeadows: Permit(pen, new Color32(0x9B, 0xD8, 0xF5, 0xFF), "MM"); break;
            case ShopIds.PermitSnootingtonHills: Permit(pen, new Color32(0xC9, 0xE8, 0xB0, 0xFF), "SH"); break;
            case ShopIds.PermitBillionaireBluffs: Permit(pen, new Color32(0xFF, 0xE0, 0x7A, 0xFF), "BB"); break;
            case ShopIds.EbaePowerSeller: Rosette(pen); break;
            case ShopIds.HireKevin: Kevin(pen); break;
            case ShopIds.GoldenTruck: GoldenTruck(pen); break;
            case ShopIds.GarbageJuice: JuiceCan(pen); break;
            case ShopIds.LuckyGnome: Gnome(pen, new Color32(0xE5, 0x32, 0x38, 0xFF), false); break;
            case ShopIds.GoldenGnome: Gnome(pen, new Color32(0xFF, 0xC9, 0x2E, 0xFF), true); break;
            case ShopIds.TruckPaintPrimer: PaintCan(pen, new Color32(0x9A, 0x9E, 0xA6, 0xFF), false); break;
            case ShopIds.TruckPaintFlames: PaintCan(pen, new Color32(0xFF, 0x7A, 0x00, 0xFF), true); break;
            case ShopIds.TruckPaintRaccoonCamo: PaintCan(pen, new Color32(0x55, 0x57, 0x60, 0xFF), false); break;
            case ShopIds.TruckPaintChrome: PaintCan(pen, new Color32(0xE6, 0xEC, 0xF5, 0xFF), false); break;
            case ShopIds.HornPackMusical: AirHorn(pen); Notes(pen); break;
            case ShopIds.TrashFmRadio: Radio(pen); break;
            case ShopIds.GarageDiscoBall: DiscoBall(pen); break;
            case ShopIds.FranchiseMode: ForSale(pen); break;
            default: DrawBox(pen); break;
        }
    }

    // ---------------------------------------------------------------- progression products (Docs/Progression.md §6)

    static readonly Color Red = new Color32(0xE5, 0x32, 0x38, 0xFF);
    static readonly Color Gold = new Color32(0xFF, 0xC9, 0x2E, 0xFF);
    static readonly Color Rubber = new Color32(0x33, 0x33, 0x3C, 0xFF);

    static void Tire(Pen pen)
    {
        pen.Circle(92f, 76f, 46f, Rubber, 3.5f);
        for (int i = 0; i < 10; i++)
        {
            float a = i * 36f * Mathf.Deg2Rad;
            pen.Line(92f + Mathf.Cos(a) * 36f, 76f + Mathf.Sin(a) * 36f, 92f + Mathf.Cos(a) * 46f, 76f + Mathf.Sin(a) * 46f, new Color32(0x55, 0x55, 0x60, 0xFF), 4f);
        }
        pen.Circle(92f, 76f, 22f, Steel, 3f);
        pen.Eyes(92f, 74f, 7f, 0.3f, -0.2f);
        // sticky drips
        var goo = new Color32(0xB8, 0xF2, 0x5B, 0xFF);
        pen.Ellipse(70f, 124f, 9f, 6f, goo, 2f);
        pen.Ellipse(112f, 126f, 6f, 4f, goo, 2f);
        // "unflip" arrow
        pen.ArcStroke(150f, 52f, 26f, 200f, 340f, Orange, 5f);
        pen.Poly(new[] { new Vector2(170f, 34f), new Vector2(182f, 54f), new Vector2(162f, 52f) }, Orange, 0f);
    }

    static void GrabbyArm(Pen pen)
    {
        Truck(pen, false, false);
        // arm reaching out of the truck's side with a bag in its claw
        pen.Line(110f, 70f, 76f, 30f, Steel, 9f);
        pen.Line(110f, 70f, 76f, 30f, Ink, 2f);
        pen.Circle(110f, 70f, 6f, Orange, 2.5f);
        pen.Line(76f, 30f, 40f, 34f, Steel, 8f);
        pen.Circle(76f, 30f, 5f, Orange, 2.5f);
        pen.Circle(30f, 46f, 14f, new Color32(0x2B, 0x2B, 0x33, 0xFF), 2.5f);
        pen.Line(40f, 34f, 22f, 30f, Ink, 3.5f);
        pen.Line(40f, 34f, 26f, 44f, Ink, 3.5f);
        pen.Eyes(30f, 46f, 4f, 0.4f, -0.3f);
        Sparkle(pen, 14f, 18f, 6f);
    }

    static void Headlights(Pen pen)
    {
        pen.Poly(new[] { new Vector2(70f, 60f), new Vector2(200f, 14f), new Vector2(200f, 122f) }, new Color(1f, 0.95f, 0.55f, 0.45f), 0f);
        pen.RoundRect(52f, 70f, 46f, 52f, 12f, 0f, Steel, 3.5f);
        pen.Circle(58f, 64f, 16f, new Color32(0xFF, 0xF2, 0x99, 0xFF), 3f);
        pen.Circle(58f, 64f, 6f, White, 0f);
        // a startled raccoon at the end of the beam
        var fur = new Color32(0x9C, 0x9C, 0xA8, 0xFF);
        pen.Circle(168f, 74f, 15f, fur, 2.5f);
        pen.RoundRect(168f, 72f, 26f, 8f, 4f, 0f, Ink, 0f);
        pen.Circle(162f, 72f, 2.5f, White, 0f);
        pen.Circle(174f, 72f, 2.5f, White, 0f);
        pen.Line(150f, 52f, 144f, 44f, Ink, 2.5f);
        pen.Line(186f, 52f, 192f, 44f, Ink, 2.5f);
    }

    static void Lever(Pen pen)
    {
        pen.RoundRect(100f, 116f, 120f, 16f, 4f, 0f, new Color32(0x8A, 0x8F, 0xA0, 0xFF), 3f);
        pen.Line(100f, 112f, 136f, 36f, Steel, 9f);
        pen.Line(100f, 112f, 136f, 36f, Ink, 2f);
        pen.Circle(138f, 32f, 14f, Red, 3f);
        pen.Eyes(138f, 30f, 5f, -0.5f, 0.3f);
        // tiny ghost hand pulling it, creepily
        pen.Ellipse(156f, 44f, 10f, 7f, new Color(1f, 1f, 1f, 0.75f), 2f);
        pen.Line(52f, 60f, 70f, 60f, new Color32(0xB8, 0xBE, 0xC8, 0xFF), 3f);
        pen.Line(46f, 74f, 64f, 74f, new Color32(0xB8, 0xBE, 0xC8, 0xFF), 3f);
    }

    static void Table(Pen pen)
    {
        pen.RoundRect(100f, 64f, 170f, 18f, 5f, 0f, new Color32(0xB0, 0x7A, 0x45, 0xFF), 3.5f);
        pen.RoundRect(30f, 102f, 10f, 58f, 2f, 0f, new Color32(0x8A, 0x5A, 0x30, 0xFF), 3f);
        pen.RoundRect(170f, 102f, 10f, 58f, 2f, 0f, new Color32(0x8A, 0x5A, 0x30, 0xFF), 3f);
        // stuff on top + a sandwich
        pen.RoundRect(60f, 46f, 26f, 18f, 3f, -8f, new Color32(0x6C, 0xC4, 0xFF, 0xFF), 2.5f);
        pen.Circle(96f, 48f, 9f, Steel, 2.5f);
        pen.RoundRect(140f, 48f, 34f, 12f, 4f, 0f, new Color32(0xF2, 0xC9, 0x7E, 0xFF), 2.5f);
        pen.Line(126f, 46f, 154f, 46f, new Color32(0x6D, 0xA5, 0x44, 0xFF), 3f);
        // magnet
        pen.ArcStroke(174f, 30f, 12f, 180f, 360f, Red, 7f);
        pen.Line(162f, 30f, 162f, 20f, Red, 7f);
        pen.Line(186f, 30f, 186f, 20f, Red, 7f);
    }

    static void Compactor(Pen pen)
    {
        pen.RoundRect(96f, 80f, 108f, 96f, 8f, 0f, new Color32(0x8A, 0x8F, 0xA0, 0xFF), 3.5f);
        pen.RoundRect(96f, 46f, 92f, 14f, 3f, 0f, new Color32(0xFF, 0xD4, 0x00, 0xFF), 3f);
        for (int i = 0; i < 4; i++) pen.Line(56f + i * 26f, 40f, 66f + i * 26f, 52f, Ink, 3f);
        pen.Eyes(96f, 80f, 9f, 0.5f, 0f);
        // a big pink nose, mid-sneeze
        pen.Ellipse(148f, 92f, 12f, 9f, new Color32(0xFF, 0x9E, 0xC7, 0xFF), 3f);
        var spray = new Color(0.6f, 0.85f, 1f, 0.8f);
        pen.Circle(170f, 84f, 4f, spray, 0f);
        pen.Circle(182f, 96f, 5f, spray, 0f);
        pen.Circle(176f, 108f, 3f, spray, 0f);
        // a battery flying out
        pen.RoundRect(186f, 62f, 12f, 22f, 2f, 30f, new Color32(0x6D, 0xA5, 0x44, 0xFF), 2.5f);
    }

    static void RecycleBin(Pen pen)
    {
        pen.Poly(new[] { new Vector2(56f, 40f), new Vector2(144f, 40f), new Vector2(134f, 128f), new Vector2(66f, 128f) }, new Color32(0x3B, 0x8E, 0xF0, 0xFF), 3.5f);
        pen.RoundRect(100f, 38f, 100f, 12f, 4f, 0f, new Color32(0x2A, 0x6C, 0xC4, 0xFF), 3f);
        for (int i = 0; i < 3; i++)
        {
            float a = (i * 120f - 90f) * Mathf.Deg2Rad, b = (i * 120f + 10f) * Mathf.Deg2Rad;
            pen.Line(100f + Mathf.Cos(a) * 20f, 86f + Mathf.Sin(a) * 20f, 100f + Mathf.Cos(b) * 20f, 86f + Mathf.Sin(b) * 20f, White, 5f);
        }
        pen.Circle(162f, 44f, 18f, Gold, 3f);
        pen.Line(162f, 32f, 162f, 56f, Ink, 3f);
        pen.Curve(new Vector2(170f, 38f), new Vector2(150f, 34f), new Vector2(162f, 44f), Ink, 3f);
        pen.Curve(new Vector2(162f, 44f), new Vector2(176f, 52f), new Vector2(154f, 50f), Ink, 3f);
    }

    static void Barrel(Pen pen)
    {
        pen.RoundRect(96f, 80f, 76f, 100f, 10f, 0f, new Color32(0xFF, 0xD4, 0x00, 0xFF), 3.5f);
        pen.Line(60f, 50f, 132f, 50f, Ink, 3f);
        pen.Line(60f, 110f, 132f, 110f, Ink, 3f);
        pen.Poly(new[] { new Vector2(96f, 62f), new Vector2(114f, 96f), new Vector2(78f, 96f) }, Ink, 0f);
        pen.Poly(new[] { new Vector2(96f, 70f), new Vector2(108f, 92f), new Vector2(84f, 92f) }, new Color32(0xFF, 0xD4, 0x00, 0xFF), 0f);
        pen.Line(96f, 76f, 96f, 84f, Ink, 3f);
        pen.Circle(96f, 88f, 1.6f, Ink, 0f);
        // laminated license
        pen.RoundRect(160f, 60f, 46f, 32f, 4f, 12f, White, 3f);
        pen.Line(146f, 54f, 172f, 60f, new Color32(0x9A, 0xA3, 0xAE, 0xFF), 2f);
        pen.Line(146f, 62f, 168f, 67f, new Color32(0x9A, 0xA3, 0xAE, 0xFF), 2f);
        Sparkle(pen, 182f, 40f, 6f);
    }

    static void Shelf(Pen pen)
    {
        pen.RoundRect(100f, 112f, 170f, 12f, 3f, 0f, new Color32(0xB0, 0x7A, 0x45, 0xFF), 3f);
        pen.RoundRect(100f, 62f, 170f, 12f, 3f, 0f, new Color32(0xB0, 0x7A, 0x45, 0xFF), 3f);
        // trophies + a ring
        pen.Poly(new[] { new Vector2(40f, 30f), new Vector2(64f, 30f), new Vector2(58f, 48f), new Vector2(46f, 48f) }, Gold, 3f);
        pen.RoundRect(52f, 52f, 18f, 6f, 2f, 0f, Gold, 2.5f);
        pen.CircleStroke(100f, 44f, 10f, Gold, 4f);
        pen.Circle(100f, 33f, 4f, new Color32(0x9F, 0xD3, 0xFF, 0xFF), 2f);
        pen.RoundRect(146f, 42f, 30f, 28f, 3f, 0f, new Color32(0xFF, 0x9E, 0xC7, 0xFF), 3f);
        pen.Eyes(146f, 40f, 4f, 0f, 0f);
        pen.Poly(new[] { new Vector2(60f, 82f), new Vector2(84f, 82f), new Vector2(78f, 100f), new Vector2(66f, 100f) }, Steel, 3f);
        pen.Circle(130f, 92f, 12f, new Color32(0xC2, 0x8A, 0xFF, 0xFF), 3f);
        Sparkle(pen, 170f, 84f, 6f);
    }

    // ---------------------------------------------------------------- displays (Docs/Progression.md §6.8)

    static readonly Color Velvet = new Color32(0xB0, 0x16, 0x2C, 0xFF);
    static readonly Color DarkWood = new Color32(0x6B, 0x3A, 0x1E, 0xFF);
    static readonly Color Marble = new Color32(0xF2, 0xF0, 0xEA, 0xFF);
    static readonly Color GemPurple = new Color32(0xC2, 0x8A, 0xFF, 0xFF);
    static readonly Color GemBlue = new Color32(0x9F, 0xD3, 0xFF, 0xFF);

    static void Gem(Pen pen, float x, float y, float r, Color c)
    {
        pen.Poly(new[] { new Vector2(x - r, y - r * 0.35f), new Vector2(x - r * 0.55f, y - r), new Vector2(x + r * 0.55f, y - r),
                         new Vector2(x + r, y - r * 0.35f), new Vector2(x, y + r) }, c, 2.5f);
        pen.Line(x - r, y - r * 0.35f, x + r, y - r * 0.35f, Ink, 1.5f);
    }

    /// <summary>One plank, one nail, three finds, zero confidence: it seesaws.</summary>
    static void PlankShelf(Pen pen)
    {
        // cardboard "MY STUFF" sign, taped on crooked (scribbles for letters)
        pen.RoundRect(100f, 28f, 74f, 26f, 2f, -4f, Cardboard, 3f);
        pen.Line(76f, 26f, 96f, 25f, Ink, 3f);
        pen.Line(102f, 24f, 124f, 22f, Ink, 3f);
        pen.Line(84f, 34f, 116f, 32f, Ink, 2f);
        pen.RoundRect(66f, 17f, 14f, 6f, 1f, 35f, Tape, 0f);
        pen.RoundRect(134f, 15f, 14f, 6f, 1f, -35f, Tape, 0f);
        // the "safety" string to a second nail (it holds nothing)
        pen.Line(44f, 102f, 54f, 52f, White, 2f);
        pen.Circle(54f, 52f, 3f, Steel, 2f);
        // the plank, tilted on its one nail, with its finds
        pen.Rotate(new Vector2(100f, 98f), -8f);
        pen.RoundRect(100f, 98f, 156f, 13f, 2f, 0f, new Color32(0xC9, 0x9A, 0x5E, 0xFF), 3f);
        pen.Ellipse(140f, 99f, 6f, 3f, new Color32(0x8A, 0x5A, 0x30, 0xFF), 0f);   // knot
        pen.Eyes(70f, 98f, 4.5f, 0.6f, 0.4f);
        pen.Poly(new[] { new Vector2(40f, 66f), new Vector2(60f, 66f), new Vector2(56f, 82f), new Vector2(44f, 82f) }, Gold, 3f);   // trophy
        pen.RoundRect(50f, 88f, 18f, 6f, 2f, 0f, Gold, 2.5f);
        pen.CircleStroke(102f, 82f, 8f, Gold, 3.5f);                                                                        // ring
        Gem(pen, 102f, 70f, 6f, GemBlue);
        pen.RoundRect(150f, 82f, 28f, 18f, 2f, 0f, Red, 3f);                                                                // cassette
        pen.Circle(143f, 82f, 3.5f, White, 1.5f);
        pen.Circle(157f, 82f, 3.5f, White, 1.5f);
        pen.ResetRotation();
        pen.Circle(100f, 98f, 5f, Steel, 2.5f);   // THE nail
        // wobble lines
        pen.ArcStroke(100f, 98f, 92f, 160f, 172f, Ink, 2.5f);
        pen.ArcStroke(100f, 98f, 92f, -12f, 0f, Ink, 2.5f);
        Sparkle(pen, 178f, 52f, 6f);
    }

    /// <summary>A lit jewelry-store case with two glass shelves (fingerprints sold separately).</summary>
    static void GlassCase(Pen pen)
    {
        pen.RoundRect(100f, 114f, 124f, 30f, 3f, 0f, DarkWood, 3.5f);                    // cabinet
        pen.RoundRect(100f, 100f, 126f, 4f, 1f, 0f, Gold, 0f);                           // trim
        pen.Eyes(100f, 116f, 6f, -0.4f, 0.2f);
        pen.RoundRect(100f, 64f, 116f, 70f, 3f, 0f, new Color(0.85f, 0.95f, 1f, 0.45f), 3f);  // glass box
        pen.RoundRect(100f, 25f, 128f, 10f, 3f, 0f, DarkWood, 3f);                       // cap
        for (int i = 0; i < 3; i++) pen.Circle(64f + i * 36f, 34f, 4f, new Color32(0xFF, 0xF2, 0x99, 0xFF), 1.5f);
        pen.Poly(new[] { new Vector2(64f, 36f), new Vector2(136f, 36f), new Vector2(156f, 98f), new Vector2(44f, 98f) }, new Color(1f, 0.95f, 0.6f, 0.18f), 0f);
        pen.Line(44f, 68f, 156f, 68f, new Color32(0x7F, 0xB8, 0xE8, 0xFF), 3f);          // glass shelf
        // finds: a gem and a ring up top, a gold watch below
        Gem(pen, 76f, 56f, 9f, GemPurple);
        pen.CircleStroke(124f, 58f, 8f, Gold, 3.5f);
        Gem(pen, 124f, 47f, 4f, GemBlue);
        pen.Circle(100f, 86f, 9f, Gold, 3f);
        pen.Circle(100f, 86f, 5f, White, 1.5f);
        pen.Line(100f, 86f, 103f, 83f, Ink, 1.5f);
        pen.RoundRect(100f, 96f, 6f, 6f, 1f, 0f, Gold, 1.5f);
        // glare + a guilty fingerprint
        pen.Line(52f, 44f, 64f, 34f, White, 3f);
        pen.Line(52f, 54f, 74f, 34f, new Color(1f, 1f, 1f, 0.6f), 2f);
        pen.Ellipse(146f, 84f, 6f, 8f, new Color(0.4f, 0.4f, 0.5f, 0.25f), 0f);
        Sparkle(pen, 172f, 30f, 6f);
    }

    /// <summary>A marble pedestal behind a velvet rope, one find under its own spotlight.</summary>
    static void Pedestal(Pen pen)
    {
        pen.Poly(new[] { new Vector2(186f, 0f), new Vector2(196f, 0f), new Vector2(124f, 64f), new Vector2(76f, 64f) }, new Color(1f, 0.95f, 0.6f, 0.4f), 0f);   // beam
        pen.Circle(190f, 4f, 9f, Rubber, 2.5f);                                          // the spotlight
        pen.RoundRect(100f, 126f, 60f, 10f, 2f, 0f, Marble, 3f);                         // foot
        pen.RoundRect(100f, 96f, 40f, 52f, 2f, 0f, Marble, 3f);                          // column
        pen.Line(88f, 74f, 88f, 118f, new Color32(0xD8, 0xD4, 0xCC, 0xFF), 2f);
        pen.Line(112f, 74f, 112f, 118f, new Color32(0xD8, 0xD4, 0xCC, 0xFF), 2f);
        pen.RoundRect(100f, 68f, 56f, 9f, 2f, 0f, Marble, 3f);                           // cap
        pen.RoundRect(100f, 62f, 40f, 5f, 2f, 0f, Velvet, 2f);                           // cushion
        pen.Eyes(100f, 96f, 6f, 0.3f, -0.5f);
        // the find, floating proudly
        pen.Circle(100f, 44f, 15f, Gold, 3f);
        pen.Circle(95f, 39f, 4f, new Color(1f, 1f, 1f, 0.8f), 0f);
        Sparkle(pen, 74f, 30f, 7f);
        Sparkle(pen, 128f, 22f, 6f);
        // velvet rope in front
        foreach (float x in new[] { 34f, 166f })
        {
            pen.RoundRect(x, 104f, 6f, 40f, 2f, 0f, Gold, 2.5f);
            pen.Circle(x, 82f, 6f, Gold, 2.5f);
            pen.Ellipse(x, 126f, 12f, 4f, Gold, 2.5f);
        }
        pen.Curve(new Vector2(34f, 86f), new Vector2(100f, 128f), new Vector2(166f, 86f), Ink, 9f);
        pen.Curve(new Vector2(34f, 86f), new Vector2(100f, 128f), new Vector2(166f, 86f), Velvet, 6f);
    }

    /// <summary>The vault: a giant round door swung open on a gold-lit niche full of Epic+ finds.</summary>
    static void Vault(Pen pen)
    {
        var steelDark = new Color32(0x5A, 0x5F, 0x6B, 0xFF);
        pen.RoundRect(118f, 70f, 150f, 120f, 6f, 0f, steelDark, 3.5f);                    // wall plate
        pen.RoundRect(136f, 70f, 92f, 84f, 4f, 0f, new Color32(0xFF, 0xD2, 0x6E, 0xFF), 3f);   // gold-lit vault
        pen.Line(92f, 70f, 180f, 70f, Ink, 3f);                                            // niche shelf
        Gem(pen, 116f, 52f, 10f, GemPurple);
        pen.Circle(156f, 52f, 10f, Gold, 3f);
        pen.Circle(152f, 48f, 3f, White, 0f);
        for (int i = 0; i < 3; i++) pen.RoundRect(124f + i * 14f - (i == 2 ? 7f : 0f), i == 2 ? 82f : 92f, 16f, 8f, 1f, 0f, Gold, 2f);   // gold bars
        Gem(pen, 162f, 90f, 7f, GemBlue);
        // the door, swung open toward you (an ellipse: seen from the side), bolts round the rim
        pen.Ellipse(48f, 72f, 34f, 58f, Steel, 4f);
        pen.Ellipse(48f, 72f, 26f, 48f, new Color32(0xB8, 0xBE, 0xC8, 0xFF), 2f);
        for (int i = 0; i < 10; i++)
        {
            float a = i * Mathf.PI * 2f / 10f;
            pen.Circle(48f + Mathf.Cos(a) * 30f, 72f + Mathf.Sin(a) * 53f, 2.5f, Gold, 1.5f);
        }
        pen.Line(48f, 44f, 48f, 100f, Gold, 4f);                                           // spoke wheel
        pen.Line(34f, 58f, 62f, 86f, Gold, 4f);
        pen.Line(34f, 86f, 62f, 58f, Gold, 4f);
        pen.Circle(48f, 72f, 6f, Gold, 2.5f);
        pen.Eyes(48f, 30f, 6f, 0.7f, 0.3f);
        pen.RoundRect(84f, 46f, 8f, 16f, 2f, 0f, steelDark, 2f);                           // hinges
        pen.RoundRect(84f, 98f, 8f, 16f, 2f, 0f, steelDark, 2f);
        Sparkle(pen, 186f, 18f, 7f);
        Sparkle(pen, 104f, 22f, 5f);
    }

    /// <summary>Magnus: a derpy robot arm. part 0 = rail motor (stripes + spoiler), 1 = gripper (lobster claw), 2 = vision (googly eyes + beam).</summary>
    static void MagnusBot(Pen pen, int part)
    {
        var body = new Color32(0xFF, 0xA0, 0x3C, 0xFF);
        pen.RoundRect(100f, 124f, 150f, 10f, 3f, 0f, new Color32(0x8A, 0x8F, 0xA0, 0xFF), 3f); // rail
        pen.RoundRect(80f, 108f, 50f, 22f, 6f, 0f, body, 3f);                                      // base
        if (part == 0)
        {
            pen.Line(60f, 104f, 100f, 104f, White, 4f);
            pen.Line(60f, 112f, 100f, 112f, White, 4f);
            pen.Poly(new[] { new Vector2(54f, 96f), new Vector2(66f, 90f), new Vector2(66f, 98f) }, Red, 2f);
            pen.Line(20f, 104f, 44f, 104f, new Color32(0xB8, 0xBE, 0xC8, 0xFF), 3f);
            pen.Line(26f, 114f, 46f, 114f, new Color32(0xB8, 0xBE, 0xC8, 0xFF), 3f);
        }
        pen.Line(80f, 100f, 96f, 56f, body, 12f);
        pen.Line(96f, 56f, 140f, 44f, body, 10f);
        pen.Circle(96f, 56f, 8f, Steel, 2.5f);
        pen.Circle(80f, 100f, 7f, Steel, 2.5f);
        if (part == 1)
        {
            var claw = new Color32(0xE5, 0x32, 0x38, 0xFF);
            pen.Poly(new[] { new Vector2(140f, 40f), new Vector2(176f, 22f), new Vector2(168f, 40f) }, claw, 3f);
            pen.Poly(new[] { new Vector2(140f, 48f), new Vector2(176f, 64f), new Vector2(166f, 46f) }, claw, 3f);
        }
        else
        {
            pen.Line(140f, 44f, 156f, 34f, Steel, 5f);
            pen.Line(140f, 44f, 158f, 52f, Steel, 5f);
        }
        float er = part == 2 ? 13f : 8f;
        if (part == 2) pen.Poly(new[] { new Vector2(102f, 28f), new Vector2(196f, 6f), new Vector2(196f, 70f) }, new Color(0.55f, 1f, 0.6f, 0.35f), 0f);
        pen.Eyes(96f, 28f, er, 0.6f, 0.2f);
        if (part == 2) Sparkle(pen, 182f, 30f, 6f);
    }

    static void Glove(Pen pen)
    {
        var tan = new Color32(0xE8, 0xB6, 0x5C, 0xFF);
        pen.RoundRect(96f, 92f, 64f, 56f, 14f, 0f, tan, 3.5f);
        for (int i = 0; i < 4; i++) pen.RoundRect(74f + i * 15f, 52f, 13f, 40f, 6f, -4f + i * 3f, tan, 3f);
        pen.RoundRect(56f, 86f, 30f, 14f, 7f, -35f, tan, 3f);
        pen.RoundRect(96f, 120f, 70f, 14f, 3f, 0f, new Color32(0x3B, 0x8E, 0xF0, 0xFF), 3f);
        pen.Eyes(96f, 92f, 7f, 0.4f, 0f);
        // a second bag for the double-fisted levels
        pen.Circle(160f, 96f, 18f, new Color32(0x2B, 0x2B, 0x33, 0xFF), 3f);
        pen.Line(160f, 78f, 158f, 70f, new Color32(0x2B, 0x2B, 0x33, 0xFF), 3f);
    }

    static void Sneaker(Pen pen)
    {
        var grey = new Color32(0xB8, 0xBE, 0xC8, 0xFF);
        pen.Line(10f, 70f, 40f, 70f, grey, 3f);
        pen.Line(4f, 86f, 30f, 86f, grey, 3f);
        pen.Poly(new[] { new Vector2(50f, 60f), new Vector2(98f, 52f), new Vector2(120f, 76f), new Vector2(170f, 84f), new Vector2(176f, 104f), new Vector2(50f, 104f) }, Red, 3.5f);
        pen.RoundRect(113f, 108f, 130f, 10f, 4f, 0f, White, 3f);
        pen.Line(90f, 62f, 104f, 74f, White, 3f);
        pen.Line(84f, 68f, 98f, 80f, White, 3f);
        // spring
        for (int i = 0; i < 3; i++) pen.Ellipse(110f, 122f + i * 6f, 16f, 3f, Color.clear, 2.5f);
        pen.Eyes(150f, 92f, 5f, 0.6f, 0f);
    }

    static void Wand(Pen pen)
    {
        pen.Line(50f, 120f, 120f, 50f, Steel, 7f);
        pen.Line(50f, 120f, 120f, 50f, Ink, 2f);
        pen.RoundRect(52f, 118f, 16f, 28f, 4f, -45f, Rubber, 3f);
        pen.Ellipse(130f, 40f, 26f, 14f, new Color32(0x3B, 0x8E, 0xF0, 0xFF), 3f);
        for (int i = 0; i < 3; i++) pen.ArcStroke(130f, 40f, 30f + i * 12f, -70f, 10f, i == 1 ? Orange : Ink, 3f);
        pen.Circle(150f, 104f, 12f, Gold, 3f);
        pen.CircleStroke(150f, 104f, 6f, new Color32(0xC9, 0x9A, 0x1E, 0xFF), 2f);
        Sparkle(pen, 170f, 92f, 6f);
    }

    static void BagCannonArt(Pen pen)
    {
        // a stubby barrel on a little wheel, lobbing a bag on a dotted arc
        pen.RoundRect(78f, 92f, 86f, 34f, 12f, -28f, Steel, 3.5f);
        pen.Ellipse(112f, 72f, 12f, 18f, Ink, 2f);
        pen.Circle(70f, 116f, 16f, Rubber, 3f);
        for (int i = 0; i < 5; i++) pen.Circle(126f + i * 12f, 56f - i * 7f + i * i * 1.6f, 2.6f, Orange, 0f);
        pen.Ellipse(172f, 52f, 14f, 17f, Ink, 2.5f);
        pen.Line(172f, 34f, 172f, 38f, Ink, 3f);
    }

    static void GrabberPoleArt(Pen pen)
    {
        // a long pole with a claw pinching a sock
        pen.Line(40f, 130f, 140f, 40f, Steel, 7f);
        pen.Line(40f, 130f, 140f, 40f, Ink, 2f);
        pen.RoundRect(48f, 122f, 14f, 30f, 4f, -48f, Orange, 3f);
        pen.Line(140f, 40f, 158f, 30f, Ink, 4f);
        pen.Line(140f, 40f, 156f, 54f, Ink, 4f);
        pen.Ellipse(166f, 40f, 12f, 8f, White, 2.5f);
    }

    static void Vaccine(Pen pen)
    {
        // a smug shield with a syringe on it
        pen.Poly(new[] { new Vector2(100f, 16f), new Vector2(150f, 32f), new Vector2(144f, 92f), new Vector2(100f, 128f), new Vector2(56f, 92f), new Vector2(50f, 32f) }, new Color32(0x6C, 0xC4, 0xFF, 0xFF), 3.5f);
        pen.RoundRect(100f, 70f, 70f, 16f, 5f, -30f, White, 3f);
        pen.Line(126f, 54f, 146f, 42f, new Color32(0x9A, 0xA3, 0xAE, 0xFF), 3f);
        pen.RoundRect(78f, 82f, 10f, 24f, 2f, -30f, Steel, 2.5f);
        pen.Eyes(100f, 52f, 6.5f, -0.6f, 0f);
        pen.Line(88f, 98f, 112f, 94f, Ink, 3f);
        // a defeated raccoon tooth
        pen.Poly(new[] { new Vector2(166f, 100f), new Vector2(178f, 100f), new Vector2(172f, 116f) }, White, 2.5f);
    }

    static void HardHat(Pen pen)
    {
        var yellow = new Color32(0xFF, 0xD4, 0x00, 0xFF);
        pen.Poly(new[] { new Vector2(46f, 96f), new Vector2(54f, 58f), new Vector2(80f, 38f), new Vector2(120f, 38f), new Vector2(146f, 58f), new Vector2(154f, 96f) }, yellow, 3.5f);
        pen.RoundRect(100f, 100f, 150f, 14f, 6f, 0f, yellow, 3.5f);
        pen.RoundRect(100f, 52f, 12f, 30f, 4f, 0f, new Color32(0xFF, 0xE6, 0x66, 0xFF), 2.5f);
        pen.Eyes(100f, 76f, 7f, 0.3f, 0.2f);
        // bonk stars
        Sparkle(pen, 40f, 30f, 7f);
        Sparkle(pen, 164f, 26f, 6f);
        pen.Star(176f, 50f, 8f, 1f);
    }

    static void Permit(Pen pen, Color tint, string code)
    {
        pen.RoundRect(100f, 72f, 150f, 100f, 10f, -4f, tint, 3.5f);
        pen.RoundRect(100f, 72f, 140f, 90f, 8f, -4f, new Color(1f, 1f, 1f, 0.35f), 0f);
        pen.RoundRect(64f, 66f, 40f, 46f, 4f, -4f, White, 2.5f);
        pen.Eyes(64f, 60f, 5f, 0.3f, 0f);
        pen.ArcStroke(64f, 74f, 6f, 20f, 160f, Ink, 2f);
        for (int i = 0; i < 3; i++) pen.Line(98f, 52f + i * 14f, 156f, 48f + i * 14f, new Color32(0x6B, 0x66, 0x80, 0xFF), 3f);
        // red APPROVED stamp ring
        pen.CircleStroke(150f, 104f, 18f, Red, 4f);
        pen.Line(138f, 104f, 162f, 104f, Red, 3f);
        Sparkle(pen, 30f, 24f, 6f);
    }

    static void Rosette(Pen pen)
    {
        pen.Poly(new[] { new Vector2(84f, 92f), new Vector2(70f, 134f), new Vector2(86f, 126f), new Vector2(96f, 138f), new Vector2(102f, 98f) }, new Color32(0x3B, 0x8E, 0xF0, 0xFF), 3f);
        pen.Poly(new[] { new Vector2(116f, 92f), new Vector2(130f, 134f), new Vector2(114f, 126f), new Vector2(104f, 138f), new Vector2(98f, 98f) }, Red, 3f);
        for (int i = 0; i < 12; i++)
        {
            float a = i * 30f * Mathf.Deg2Rad;
            pen.Circle(100f + Mathf.Cos(a) * 34f, 62f + Mathf.Sin(a) * 34f, 11f, Gold, 2.5f);
        }
        pen.Circle(100f, 62f, 32f, Gold, 3f);
        pen.Circle(100f, 62f, 24f, White, 2.5f);
        pen.Star(100f, 62f, 18f, 1f);
        Sparkle(pen, 160f, 26f, 7f);
    }

    static void Kevin(Pen pen)
    {
        var vest = new Color32(0xFF, 0x8A, 0x00, 0xFF);
        pen.RoundRect(100f, 96f, 44f, 50f, 12f, 0f, vest, 3.5f);
        pen.Line(88f, 74f, 88f, 120f, new Color32(0xF1, 0xF1, 0x60, 0xFF), 3f);
        pen.Line(112f, 74f, 112f, 120f, new Color32(0xF1, 0xF1, 0x60, 0xFF), 3f);
        pen.Circle(100f, 50f, 20f, new Color32(0xF6, 0xCF, 0xA8, 0xFF), 3f);
        pen.Eyes(100f, 48f, 6f, -0.7f, 0.5f);
        pen.ArcStroke(100f, 58f, 7f, 20f, 160f, Ink, 2.5f);
        pen.RoundRect(100f, 30f, 42f, 10f, 4f, -8f, new Color32(0x3B, 0x8E, 0xF0, 0xFF), 2.5f);
        pen.Line(90f, 120f, 86f, 134f, Ink, 5f);
        pen.Line(110f, 120f, 114f, 134f, Ink, 5f);
        pen.RoundRect(82f, 136f, 14f, 6f, 2f, 0f, Rubber, 2f); // one shoe
        pen.Ellipse(118f, 136f, 5f, 3f, new Color32(0xF6, 0xCF, 0xA8, 0xFF), 2f); // one sock
        // the lost shoe, far away
        pen.RoundRect(168f, 120f, 16f, 7f, 2f, 20f, Rubber, 2f);
        pen.Line(150f, 112f, 160f, 116f, new Color32(0xB8, 0xBE, 0xC8, 0xFF), 2f);
    }

    static void GoldenTruck(Pen pen)
    {
        TruckBody(pen, Gold, new Color32(0xE0, 0xA8, 0x10, 0xFF));
        Sparkle(pen, 30f, 30f, 8f);
        Sparkle(pen, 176f, 24f, 7f);
        Sparkle(pen, 112f, 22f, 6f);
        pen.Star(60f, 40f, 8f, 1f);
    }

    static void JuiceCan(Pen pen)
    {
        var green = new Color32(0x7C, 0xD9, 0x3A, 0xFF);
        pen.RoundRect(96f, 78f, 60f, 100f, 10f, 4f, green, 3.5f);
        pen.RoundRect(96f, 30f, 52f, 10f, 4f, 4f, Steel, 3f);
        pen.Poly(new[] { new Vector2(100f, 48f), new Vector2(84f, 82f), new Vector2(98f, 82f), new Vector2(90f, 112f), new Vector2(114f, 72f), new Vector2(100f, 72f), new Vector2(108f, 48f) }, new Color32(0xFF, 0xD4, 0x00, 0xFF), 2.5f);
        pen.Eyes(96f, 92f, 6f, 0.5f, -0.4f);
        // hiccup bubbles
        pen.Circle(150f, 44f, 8f, new Color(0.7f, 1f, 0.6f, 0.7f), 2f);
        pen.Circle(166f, 26f, 5f, new Color(0.7f, 1f, 0.6f, 0.7f), 2f);
    }

    static void Gnome(Pen pen, Color hat, bool golden)
    {
        Color skin = golden ? Gold : (Color)new Color32(0xF6, 0xCF, 0xA8, 0xFF);
        Color coat = golden ? (Color)new Color32(0xE0, 0xA8, 0x10, 0xFF) : (Color)new Color32(0x3B, 0x8E, 0xF0, 0xFF);
        pen.Poly(new[] { new Vector2(70f, 128f), new Vector2(76f, 82f), new Vector2(124f, 82f), new Vector2(130f, 128f) }, coat, 3.5f);
        pen.Circle(100f, 76f, 20f, skin, 3f);
        pen.Poly(new[] { new Vector2(80f, 92f), new Vector2(120f, 92f), new Vector2(100f, 124f) }, golden ? White : (Color)new Color32(0xF2, 0xF2, 0xF2, 0xFF), 3f);
        pen.Poly(new[] { new Vector2(76f, 66f), new Vector2(124f, 66f), new Vector2(108f, 14f) }, hat, 3.5f);
        pen.Eyes(100f, 72f, 5f, 0.4f, 0f);
        pen.Circle(100f, 84f, 5f, new Color32(0xFF, 0x9E, 0xC7, 0xFF), 2f);
        if (golden) { Sparkle(pen, 150f, 40f, 8f); Sparkle(pen, 44f, 52f, 6f); pen.Star(160f, 96f, 9f, 1f); }
        else Sparkle(pen, 150f, 50f, 6f);
    }

    static void PaintCan(Pen pen, Color paint, bool flames)
    {
        pen.Poly(new[] { new Vector2(60f, 50f), new Vector2(140f, 50f), new Vector2(136f, 126f), new Vector2(64f, 126f) }, Steel, 3.5f);
        pen.Ellipse(100f, 50f, 40f, 10f, paint, 3f);
        pen.RoundRect(100f, 92f, 64f, 34f, 4f, 0f, paint, 2.5f);
        pen.Poly(new[] { new Vector2(130f, 56f), new Vector2(138f, 56f), new Vector2(136f, 84f), new Vector2(132f, 84f) }, paint, 0f);
        pen.ArcStroke(100f, 44f, 42f, 200f, 340f, Ink, 3f);
        pen.Eyes(100f, 90f, 6f, 0.3f, 0f);
        if (flames)
            pen.Poly(new[] { new Vector2(150f, 120f), new Vector2(156f, 92f), new Vector2(164f, 108f), new Vector2(170f, 84f), new Vector2(178f, 104f), new Vector2(186f, 96f), new Vector2(182f, 120f) }, new Color32(0xFF, 0xD4, 0x00, 0xFF), 2.5f);
        else Sparkle(pen, 168f, 40f, 7f);
    }

    static void Notes(Pen pen)
    {
        pen.Circle(170f, 40f, 6f, Ink, 0f);
        pen.Line(175f, 40f, 175f, 14f, Ink, 3f);
        pen.Circle(186f, 64f, 5f, Orange, 0f);
        pen.Line(190f, 64f, 190f, 42f, Orange, 3f);
    }

    static void Radio(Pen pen)
    {
        pen.RoundRect(100f, 84f, 140f, 76f, 12f, 0f, new Color32(0xC2, 0x8A, 0xFF, 0xFF), 3.5f);
        pen.Line(60f, 46f, 40f, 12f, Steel, 4f);
        pen.Circle(40f, 12f, 4f, Red, 2f);
        pen.Circle(66f, 92f, 22f, Rubber, 3f);
        for (int i = 0; i < 3; i++) pen.CircleStroke(66f, 92f, 6f + i * 6f, new Color32(0x55, 0x55, 0x60, 0xFF), 1.5f);
        pen.RoundRect(132f, 74f, 60f, 22f, 4f, 0f, new Color32(0xB8, 0xF2, 0x5B, 0xFF), 2.5f);
        pen.Line(116f, 74f, 148f, 74f, Ink, 2f);
        pen.Circle(122f, 104f, 6f, Steel, 2f);
        pen.Circle(142f, 104f, 6f, Steel, 2f);
        Notes(pen);
    }

    static void DiscoBall(Pen pen)
    {
        pen.Line(100f, 0f, 100f, 26f, Ink, 3f);
        pen.Circle(100f, 70f, 44f, new Color32(0xC9, 0xD2, 0xE0, 0xFF), 3.5f);
        for (int i = -3; i <= 3; i++) pen.Line(58f, 70f + i * 12f, 142f, 70f + i * 12f, new Color32(0x8A, 0x8F, 0xA0, 0xFF), 1.5f);
        for (int i = -3; i <= 3; i++) pen.Line(100f + i * 12f, 28f, 100f + i * 12f, 112f, new Color32(0x8A, 0x8F, 0xA0, 0xFF), 1.5f);
        pen.CircleStroke(100f, 70f, 44f, Ink, 3.5f);
        pen.Eyes(100f, 66f, 7f, 0.2f, 0.3f);
        var beams = new[] { new Color(1f, 0.4f, 0.6f, 0.6f), new Color(0.4f, 0.8f, 1f, 0.6f), new Color(1f, 0.9f, 0.3f, 0.6f) };
        pen.Line(140f, 90f, 196f, 130f, beams[0], 4f);
        pen.Line(60f, 92f, 6f, 132f, beams[1], 4f);
        pen.Line(146f, 54f, 198f, 40f, beams[2], 4f);
    }

    static void ForSale(Pen pen)
    {
        pen.Line(70f, 40f, 70f, 136f, new Color32(0x8A, 0x5A, 0x30, 0xFF), 6f);
        pen.RoundRect(110f, 64f, 120f, 64f, 6f, -3f, White, 3.5f);
        pen.RoundRect(110f, 50f, 100f, 14f, 3f, -3f, Red, 0f);
        pen.Star(110f, 84f, 14f, 1f);
        pen.Line(78f, 104f, 142f, 100f, new Color32(0x6B, 0x66, 0x80, 0xFF), 3f);
        Sparkle(pen, 178f, 30f, 6f);
    }

    static void Syringe(Pen pen)
    {
        var c = new Vector2(100f, 74f);
        const float rot = -24f;
        Vector2 L(float dx, float dy) => Rot(c + new Vector2(dx, dy), c, rot);

        // red-cross badge
        pen.Circle(52f, 44f, 15f, White, 3f);
        pen.RoundRect(52f, 44f, 18f, 6.5f, 1.5f, 0f, new Color32(0xE5, 0x32, 0x38, 0xFF), 0f);
        pen.RoundRect(52f, 44f, 6.5f, 18f, 1.5f, 0f, new Color32(0xE5, 0x32, 0x38, 0xFF), 0f);

        // needle + hub
        pen.Line(L(-52f, 0f), L(-94f, 0f), new Color32(0x9A, 0xA3, 0xAE, 0xFF), 4f);
        var hub = L(-55f, 0f);
        pen.RoundRect(hub.x, hub.y, 12f, 16f, 3f, rot, Steel, 3f);
        var tip = L(-97f, 3f);
        pen.Circle(tip.x, tip.y + 3f, 3.5f, new Color32(0x8E, 0xF0, 0x6A, 0xFF), 0f);

        // plunger rod + thumb pad + flange
        var rod = L(66f, 0f);
        pen.RoundRect(rod.x, rod.y, 34f, 10f, 2f, rot, Steel, 3f);
        var pad = L(84f, 0f);
        pen.RoundRect(pad.x, pad.y, 9f, 40f, 4f, rot, new Color32(0xFF, 0x6B, 0x6B, 0xFF), 3f);
        var flange = L(51f, 0f);
        pen.RoundRect(flange.x, flange.y, 10f, 60f, 4f, rot, Steel, 3f);

        // barrel with glowing cure
        pen.RoundRect(c.x, c.y, 100f, 36f, 9f, rot, new Color32(0xE8, 0xF4, 0xFF, 0xFF), 3.5f);
        var liquid = L(-14f, 2f);
        pen.RoundRect(liquid.x, liquid.y, 68f, 24f, 6f, rot, new Color32(0xB8, 0xF2, 0x5B, 0xFF), 0f);
        for (int i = 0; i < 6; i++)
        {
            float dx = -36f + i * 12f;
            pen.Line(L(dx, -18f), L(dx, -11f), Ink, 2f);
        }
        var eyes = L(-12f, -1f);
        pen.Eyes(eyes.x, eyes.y, 8.5f, 0.5f, -0.3f);

        Sparkle(pen, 168f, 30f, 8f);
        Sparkle(pen, 146f, 122f, 6f);
        Sparkle(pen, 22f, 78f, 5f);
    }

    static void SprayBottle(Pen pen)
    {
        var cloud = new Color(0.62f, 0.92f, 0.35f, 0.6f);
        pen.Circle(46f, 40f, 15f, cloud, 0f);
        pen.Circle(30f, 52f, 11f, cloud, 0f);
        pen.Circle(58f, 56f, 12f, cloud, 0f);
        pen.Circle(26f, 32f, 9f, cloud, 0f);
        pen.Curve(new Vector2(20f, 80f), new Vector2(30f, 70f), new Vector2(22f, 62f), new Color(0.4f, 0.7f, 0.2f), 2.5f);
        pen.Curve(new Vector2(40f, 84f), new Vector2(50f, 74f), new Vector2(42f, 66f), new Color(0.4f, 0.7f, 0.2f), 2.5f);

        var purple = new Color32(0x7A, 0x5C, 0xFF, 0xFF);
        pen.Poly(new[] { new Vector2(100f, 42f), new Vector2(113f, 42f), new Vector2(107f, 62f), new Vector2(97f, 60f) }, new Color32(0x5B, 0x3F, 0xD9, 0xFF), 3f);
        pen.RoundRect(79f, 32f, 18f, 9f, 2f, 0f, purple, 3f);
        pen.RoundRect(102f, 55f, 22f, 16f, 2f, 0f, new Color32(0xED, 0xED, 0xED, 0xFF), 3f);
        pen.RoundRect(101f, 33f, 38f, 20f, 5f, 0f, purple, 3f);
        pen.Eyes(101f, 32f, 5.5f, -0.6f, 0.1f);

        pen.RoundRect(102f, 96f, 58f, 78f, 13f, 0f, new Color32(0x9B, 0x7B, 0xFF, 0xFF), 3.5f);
        pen.RoundRect(102f, 97f, 44f, 48f, 6f, 0f, White, 2.5f);

        // raccoon face...
        var fur = new Color32(0x9C, 0x9C, 0xA8, 0xFF);
        pen.Poly(new[] { new Vector2(92f, 88f), new Vector2(94f, 78f), new Vector2(99f, 86f) }, fur, 2f);
        pen.Poly(new[] { new Vector2(112f, 88f), new Vector2(110f, 78f), new Vector2(105f, 86f) }, fur, 2f);
        pen.Circle(102f, 98f, 12f, fur, 2f);
        pen.RoundRect(102f, 96f, 24f, 7f, 3.5f, 0f, Ink, 0f);
        pen.Circle(97f, 96f, 1.8f, White, 0f);
        pen.Circle(107f, 96f, 1.8f, White, 0f);
        pen.Circle(102f, 103f, 2.2f, Ink, 0f);
        // ...with a big NO on it
        var no = new Color32(0xE5, 0x32, 0x38, 0xFF);
        pen.CircleStroke(102f, 97f, 18f, no, 4f);
        pen.Line(89f, 84f, 115f, 110f, no, 4f);
    }

    static void TruckBody(Pen pen, Color body, Color trim)
    {
        var wheel = new Color32(0x33, 0x33, 0x3C, 0xFF);
        pen.Circle(64f, 110f, 15f, wheel, 3f);
        pen.Circle(64f, 110f, 6f, Gold, 2f);
        pen.Circle(152f, 110f, 15f, wheel, 3f);
        pen.Circle(152f, 110f, 6f, Gold, 2f);
        pen.RoundRect(86f, 82f, 106f, 50f, 8f, 0f, body, 3.5f);
        pen.Line(52f, 64f, 52f, 100f, trim, 3f);
        pen.Line(120f, 64f, 120f, 100f, trim, 3f);
        pen.Poly(new[] { new Vector2(139f, 60f), new Vector2(170f, 60f), new Vector2(186f, 84f), new Vector2(186f, 106f), new Vector2(139f, 106f) }, body, 3.5f);
        pen.Poly(new[] { new Vector2(146f, 66f), new Vector2(167f, 66f), new Vector2(178f, 83f), new Vector2(146f, 83f) }, new Color32(0x9F, 0xD3, 0xFF, 0xFF), 2.5f);
        pen.Eyes(158f, 75f, 5.5f, 0.6f, 0.2f);
        pen.RoundRect(183f, 104f, 10f, 6f, 2f, 0f, Steel, 2f);
        pen.Circle(184f, 92f, 3f, new Color32(0xFF, 0xE0, 0x66, 0xFF), 1.5f);
    }

    static void Truck(Pen pen, bool pile, bool turbo)
    {
        if (turbo)
        {
            // speed lines + a derpy wheelie
            var grey = new Color32(0xB8, 0xBE, 0xC8, 0xFF);
            pen.Line(4f, 44f, 34f, 44f, grey, 3f);
            pen.Line(0f, 58f, 22f, 58f, grey, 3f);
            pen.Line(8f, 100f, 30f, 100f, grey, 3f);
            pen.Rotate(new Vector2(110f, 110f), -7f);
            var flameO = new Color32(0xFF, 0x8A, 0x00, 0xFF);
            var flameY = new Color32(0xFF, 0xD4, 0x00, 0xFF);
            pen.Poly(new[] { new Vector2(34f, 64f), new Vector2(8f, 56f), new Vector2(20f, 72f), new Vector2(2f, 82f), new Vector2(22f, 88f), new Vector2(10f, 102f), new Vector2(34f, 96f) }, flameO, 2.5f);
            pen.Poly(new[] { new Vector2(34f, 70f), new Vector2(20f, 68f), new Vector2(26f, 78f), new Vector2(16f, 86f), new Vector2(34f, 90f) }, flameY, 0f);
        }

        var wheel = new Color32(0x33, 0x33, 0x3C, 0xFF);
        pen.Circle(64f, 110f, 15f, wheel, 3f);
        pen.Circle(64f, 110f, 6f, Steel, 2f);
        pen.Circle(152f, 110f, 15f, wheel, 3f);
        pen.Circle(152f, 110f, 6f, Steel, 2f);

        if (pile)
        {
            var bag = new Color32(0x2B, 0x2B, 0x33, 0xFF);
            pen.Circle(80f, 38f, 12f, bag, 2.5f);
            pen.Circle(50f, 50f, 13f, bag, 2.5f);
            pen.Circle(72f, 48f, 15f, bag, 2.5f);
            pen.Circle(96f, 49f, 14f, bag, 2.5f);
            pen.Circle(118f, 52f, 12f, bag, 2.5f);
            pen.Line(80f, 26f, 80f, 21f, bag, 3f);
            pen.Line(72f, 33f, 70f, 28f, bag, 3f);
        }

        pen.RoundRect(86f, 82f, 106f, 50f, 8f, 0f, TruckGreen, 3.5f);
        pen.Line(52f, 64f, 52f, 100f, new Color32(0x4E, 0x80, 0x2E, 0xFF), 3f);
        pen.Line(120f, 64f, 120f, 100f, new Color32(0x4E, 0x80, 0x2E, 0xFF), 3f);
        if (pile)
        {
            var wood = new Color32(0xB0, 0x7A, 0x45, 0xFF);
            pen.RoundRect(86f, 56f, 110f, 8f, 2f, -1.5f, wood, 2.5f);
        }
        if (turbo)
        {
            pen.RoundRect(132f, 48f, 7f, 30f, 2f, 0f, Steel, 2.5f);
            pen.RoundRect(132f, 33f, 10f, 4f, 1f, 0f, Ink, 0f);
            pen.Circle(129f, 26f, 5f, new Color(0.55f, 0.55f, 0.6f, 0.7f), 0f);
            pen.Circle(122f, 18f, 7f, new Color(0.55f, 0.55f, 0.6f, 0.5f), 0f);
        }
        pen.Poly(new[] { new Vector2(139f, 60f), new Vector2(170f, 60f), new Vector2(186f, 84f), new Vector2(186f, 106f), new Vector2(139f, 106f) }, new Color32(0xE8, 0xE8, 0xEE, 0xFF), 3.5f);
        pen.Poly(new[] { new Vector2(146f, 66f), new Vector2(167f, 66f), new Vector2(178f, 83f), new Vector2(146f, 83f) }, new Color32(0x9F, 0xD3, 0xFF, 0xFF), 2.5f);
        pen.Eyes(158f, 75f, 5.5f, 0.6f, 0.2f);
        pen.RoundRect(183f, 104f, 10f, 6f, 2f, 0f, Steel, 2f);
        pen.Circle(184f, 92f, 3f, new Color32(0xFF, 0xE0, 0x66, 0xFF), 1.5f);
        pen.ResetRotation();
    }

    static void AirHorn(Pen pen)
    {
        for (int i = 0; i < 3; i++)
            pen.ArcStroke(152f, 74f, 22f + i * 14f, -42f, 42f, i == 1 ? Orange : Ink, 4f);

        var brass = new Color32(0xFF, 0xD8, 0x4D, 0xFF);
        pen.Poly(new[]
        {
            new Vector2(70f, 68f), new Vector2(108f, 64f), new Vector2(134f, 44f), new Vector2(142f, 44f),
            new Vector2(142f, 104f), new Vector2(134f, 104f), new Vector2(108f, 84f), new Vector2(70f, 80f)
        }, brass, 3.5f);
        pen.Ellipse(142f, 74f, 7f, 30f, new Color32(0xC9, 0x9A, 0x1E, 0xFF), 3f);

        var red = new Color32(0xE5, 0x32, 0x38, 0xFF);
        pen.RoundRect(52f, 44f, 14f, 10f, 2f, 0f, Steel, 3f);
        pen.Circle(52f, 36f, 7f, new Color32(0xFF, 0xD4, 0x00, 0xFF), 3f);
        pen.RoundRect(52f, 86f, 38f, 76f, 12f, 0f, red, 3.5f);
        pen.RoundRect(52f, 108f, 38f, 8f, 0f, 0f, new Color32(0xFF, 0x8A, 0x8E, 0xFF), 0f);
        pen.Eyes(52f, 74f, 7.5f, 0.5f, -0.4f);
        pen.ArcStroke(52f, 88f, 7f, 20f, 160f, Ink, 2.5f);
    }

    static void Headlamp(Pen pen)
    {
        pen.Poly(new[] { new Vector2(116f, 42f), new Vector2(204f, 4f), new Vector2(204f, 96f) }, new Color(1f, 0.95f, 0.55f, 0.45f), 0f);
        pen.Poly(new[] { new Vector2(116f, 42f), new Vector2(204f, 20f), new Vector2(204f, 70f) }, new Color(1f, 0.97f, 0.7f, 0.55f), 0f);

        pen.Circle(90f, 88f, 44f, new Color32(0xF6, 0xCF, 0xA8, 0xFF), 3.5f);
        pen.Curve(new Vector2(70f, 48f), new Vector2(74f, 34f), new Vector2(84f, 44f), Ink, 3f);
        pen.Curve(new Vector2(84f, 45f), new Vector2(90f, 30f), new Vector2(98f, 44f), Ink, 3f);
        pen.RoundRect(90f, 56f, 92f, 12f, 5f, -9f, new Color32(0x2C, 0x2C, 0x38, 0xFF), 0f);
        pen.Circle(116f, 48f, 12f, new Color32(0xFF, 0xE0, 0x66, 0xFF), 3.5f);
        pen.Circle(116f, 48f, 5f, White, 0f);
        pen.Eyes(90f, 86f, 11f, 0.4f, 0.1f);
        pen.ArcStroke(90f, 102f, 13f, 25f, 155f, Ink, 3f);

        // a moth, obviously
        pen.Circle(166f, 44f, 6f, new Color32(0xC9, 0xC2, 0xB5, 0xFF), 2f);
        pen.Circle(176f, 44f, 6f, new Color32(0xC9, 0xC2, 0xB5, 0xFF), 2f);
        pen.Ellipse(171f, 46f, 3f, 7f, new Color32(0x8A, 0x80, 0x70, 0xFF), 1.5f);
    }

    static void Conveyor(Pen pen)
    {
        var web = new Color(0.55f, 0.55f, 0.6f, 0.8f);
        for (int i = 0; i < 4; i++) pen.Line(0f, 0f, 42f - i * 10f, i * 12f, web, 1.2f);
        pen.ArcStroke(0f, 0f, 16f, 0f, 90f, web, 1.2f);
        pen.ArcStroke(0f, 0f, 28f, 0f, 90f, web, 1.2f);

        var grey = new Color32(0x8A, 0x8F, 0xA0, 0xFF);
        pen.RoundRect(44f, 114f, 8f, 30f, 2f, 0f, grey, 3f);
        pen.RoundRect(156f, 114f, 8f, 30f, 2f, 0f, grey, 3f);
        pen.RoundRect(100f, 96f, 164f, 24f, 12f, 0f, new Color32(0x3A, 0x3A, 0x46, 0xFF), 3.5f);
        for (int i = 0; i < 8; i++) pen.Circle(30f + i * 20f, 96f, 6f, grey, 2f);

        pen.RoundRect(68f, 70f, 36f, 28f, 3f, -4f, Cardboard, 3f);
        pen.Line(52f, 64f, 84f, 62f, Tape, 4f);
        pen.RoundRect(120f, 68f, 32f, 32f, 3f, 7f, CardboardDark, 3f);
        pen.Eyes(120f, 66f, 5f, -0.5f, 0.2f);
    }

    static void Brain(Pen pen)
    {
        var pink = new Color32(0xFF, 0x9E, 0xC7, 0xFF);
        var lobes = new[] { new Vector3(80f, 70f, 26f), new Vector3(104f, 56f, 26f), new Vector3(128f, 70f, 24f), new Vector3(102f, 86f, 26f), new Vector3(76f, 92f, 18f), new Vector3(126f, 94f, 18f) };
        foreach (var l in lobes) pen.CircleStroke(l.x, l.y, l.z, Ink, 7f);
        foreach (var l in lobes) pen.Circle(l.x, l.y, l.z, pink, 0f);
        var fold = new Color32(0xE0, 0x67, 0x9E, 0xFF);
        pen.Curve(new Vector2(68f, 60f), new Vector2(78f, 52f), new Vector2(86f, 62f), fold, 3f);
        pen.Curve(new Vector2(118f, 52f), new Vector2(128f, 46f), new Vector2(136f, 58f), fold, 3f);
        pen.Curve(new Vector2(92f, 100f), new Vector2(104f, 92f), new Vector2(114f, 102f), fold, 3f);
        pen.Curve(new Vector2(128f, 80f), new Vector2(138f, 74f), new Vector2(140f, 86f), fold, 3f);
        pen.Eyes(103f, 72f, 9f, 0.3f, -0.4f);
        pen.Poly(new[] { new Vector2(166f, 20f), new Vector2(150f, 56f), new Vector2(163f, 56f), new Vector2(152f, 92f), new Vector2(182f, 46f), new Vector2(168f, 46f), new Vector2(180f, 20f) }, new Color32(0xFF, 0xD4, 0x00, 0xFF), 3f);
    }

    static void DrawHeroBox(Pen pen)
    {
        var grey = new Color32(0xFF, 0xFF, 0xFF, 0xAA);
        pen.Line(8f, 56f, 58f, 56f, grey, 4f);
        pen.Line(0f, 76f, 44f, 76f, grey, 4f);
        pen.Line(14f, 96f, 54f, 96f, grey, 4f);
        // wings
        pen.Rotate(new Vector2(78f, 58f), -24f);
        pen.Ellipse(78f, 58f, 20f, 11f, White, 3f);
        pen.ResetRotation();
        pen.Rotate(new Vector2(176f, 58f), 24f);
        pen.Ellipse(176f, 58f, 20f, 11f, White, 3f);
        pen.ResetRotation();
        pen.Rotate(new Vector2(128f, 80f), -6f);
        DrawBoxAt(pen, 128f, 84f, 1.25f);
        pen.ResetRotation();
    }

    static void DrawBox(Pen pen) => DrawBoxAt(pen, 50f, 50f, 1f);

    static void DrawBoxAt(Pen pen, float cx, float cy, float k)
    {
        Vector2 V(float x, float y) => new Vector2(cx + x * k, cy + y * k);
        pen.Poly(new[] { V(-36f, -22f), V(-24f, -36f), V(36f, -36f), V(36f, -22f) }, CardboardDark, 3f);   // back flap
        pen.Poly(new[] { V(-36f, -22f), V(36f, -22f), V(36f, 32f), V(-36f, 32f) }, Cardboard, 3.5f);      // front face
        pen.Poly(new[] { V(-36f, -22f), V(-50f, -34f), V(-20f, -34f), V(-6f, -22f) }, Cardboard, 3f);    // left flap
        pen.Poly(new[] { V(36f, -22f), V(50f, -34f), V(20f, -34f), V(6f, -22f) }, Cardboard, 3f);        // right flap
        pen.RoundRect(cx, cy - 22f * k + 1f, 16f * k, 6f * k, 1f, 0f, Tape, 0f);
        pen.Eyes(cx, cy - 4f * k, 7.5f * k, 0.4f, -0.2f);
        var p0 = V(-20f, 12f); var p1 = V(0f, 26f); var p2 = V(22f, 11f);
        pen.Curve(p0, p1, p2, Orange, 4.5f * k);
        pen.Line(p2, p2 + new Vector2(-8f, -1f) * k, Orange, 4.5f * k);
        pen.Line(p2, p2 + new Vector2(-2f, 7f) * k, Orange, 4.5f * k);
    }

    static void Sparkle(Pen pen, float x, float y, float r)
    {
        var c = new Color32(0xFF, 0xD4, 0x00, 0xFF);
        pen.Line(x - r, y, x + r, y, c, 3f);
        pen.Line(x, y - r, x, y + r, c, 3f);
    }

    // ---------------------------------------------------------------- plumbing

    static VisualElement Canvas(string cls, float vw, float vh, Action<Pen> draw)
    {
        var e = new VisualElement { pickingMode = PickingMode.Ignore };
        e.AddToClassList(cls);
        e.generateVisualContent += mgc => Safe(() =>
        {
            var pen = new Pen(mgc, e.contentRect, vw, vh);
            if (pen.Ok) draw(pen);
        });
        return e;
    }

    static int errors;
    static void Safe(Action a)
    {
        try { a(); }
        catch (Exception e) { if (errors++ < 5) Debug.LogException(e); }
    }

    static Vector2 Rot(Vector2 p, Vector2 c, float deg)
    {
        float r = deg * Mathf.Deg2Rad, cs = Mathf.Cos(r), sn = Mathf.Sin(r);
        Vector2 d = p - c;
        return c + new Vector2(d.x * cs - d.y * sn, d.x * sn + d.y * cs);
    }

    /// <summary>Tiny Painter2D wrapper: a virtual canvas (vw × vh) fitted into the element, ink outlines.</summary>
    sealed class Pen
    {
        readonly Painter2D p;
        readonly Vector2 o;
        readonly float s;
        bool rotating;
        Vector2 rotCenter;
        float rotDeg;

        public bool Ok => s > 0.01f && !float.IsNaN(s) && !float.IsInfinity(s);

        public Pen(MeshGenerationContext mgc, Rect r, float vw, float vh)
        {
            p = mgc.painter2D;
            s = Mathf.Min(r.width / vw, r.height / vh);
            o = new Vector2(r.x + (r.width - vw * s) * 0.5f, r.y + (r.height - vh * s) * 0.5f);
            p.lineJoin = LineJoin.Round;
            p.lineCap = LineCap.Round;
        }

        public void Rotate(Vector2 center, float deg) { rotating = true; rotCenter = center; rotDeg = deg; }
        public void ResetRotation() => rotating = false;

        Vector2 P(Vector2 v) => o + (rotating ? Rot(v, rotCenter, rotDeg) : v) * s;
        Vector2 P(float x, float y) => P(new Vector2(x, y));

        void FillStroke(Color fill, float outline, Color stroke)
        {
            if (fill.a > 0.001f) { p.fillColor = fill; p.Fill(FillRule.NonZero); }
            if (outline > 0f) { p.strokeColor = stroke; p.lineWidth = outline * s; p.Stroke(); }
        }

        public void Poly(Vector2[] pts, Color fill, float outline = 3f) => Poly(pts, fill, outline, Ink);

        public void Poly(Vector2[] pts, Color fill, float outline, Color stroke)
        {
            if (pts == null || pts.Length < 3) return;
            p.BeginPath();
            p.MoveTo(P(pts[0]));
            for (int i = 1; i < pts.Length; i++) p.LineTo(P(pts[i]));
            p.ClosePath();
            FillStroke(fill, outline, stroke);
        }

        public void Polyline(Vector2[] pts, Color color, float width)
        {
            if (pts == null || pts.Length < 2) return;
            p.BeginPath();
            p.MoveTo(P(pts[0]));
            for (int i = 1; i < pts.Length; i++) p.LineTo(P(pts[i]));
            p.strokeColor = color; p.lineWidth = width * s; p.Stroke();
        }

        public void Circle(float x, float y, float r, Color fill, float outline = 3f)
        {
            p.BeginPath();
            p.Arc(P(x, y), r * s, Angle.Degrees(0f), Angle.Degrees(360f), ArcDirection.Clockwise);
            p.ClosePath();
            FillStroke(fill, outline, Ink);
        }

        public void CircleStroke(float x, float y, float r, Color color, float width)
        {
            p.BeginPath();
            p.Arc(P(x, y), r * s, Angle.Degrees(0f), Angle.Degrees(360f), ArcDirection.Clockwise);
            p.ClosePath();
            p.strokeColor = color; p.lineWidth = width * s; p.Stroke();
        }

        public void ArcStroke(float x, float y, float r, float fromDeg, float toDeg, Color color, float width)
        {
            float rot = rotating ? rotDeg : 0f;
            p.BeginPath();
            p.Arc(P(x, y), r * s, Angle.Degrees(fromDeg + rot), Angle.Degrees(toDeg + rot), ArcDirection.Clockwise);
            p.strokeColor = color; p.lineWidth = width * s; p.Stroke();
        }

        public void Ellipse(float x, float y, float rx, float ry, Color fill, float outline = 3f)
        {
            const int n = 32;
            var pts = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                float a = i * Mathf.PI * 2f / n;
                pts[i] = new Vector2(x + Mathf.Cos(a) * rx, y + Mathf.Sin(a) * ry);
            }
            Poly(pts, fill, outline);
        }

        public void RoundRect(float cx, float cy, float w, float h, float rad, float rotDeg, Color fill, float outline = 3f)
        {
            rad = Mathf.Clamp(rad, 0f, Mathf.Min(w, h) * 0.5f);
            const int seg = 5;
            var pts = new Vector2[rad > 0.01f ? 4 * (seg + 1) : 4];
            var c = new Vector2(cx, cy);
            if (rad <= 0.01f)
            {
                pts[0] = new Vector2(cx - w / 2, cy - h / 2); pts[1] = new Vector2(cx + w / 2, cy - h / 2);
                pts[2] = new Vector2(cx + w / 2, cy + h / 2); pts[3] = new Vector2(cx - w / 2, cy + h / 2);
            }
            else
            {
                var corners = new[]
                {
                    (new Vector2(cx + w / 2 - rad, cy - h / 2 + rad), -90f),
                    (new Vector2(cx + w / 2 - rad, cy + h / 2 - rad), 0f),
                    (new Vector2(cx - w / 2 + rad, cy + h / 2 - rad), 90f),
                    (new Vector2(cx - w / 2 + rad, cy - h / 2 + rad), 180f),
                };
                int k = 0;
                foreach (var (cc, start) in corners)
                    for (int i = 0; i <= seg; i++)
                    {
                        float a = (start + 90f * i / seg) * Mathf.Deg2Rad;
                        pts[k++] = cc + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rad;
                    }
            }
            if (Mathf.Abs(rotDeg) > 0.01f)
                for (int i = 0; i < pts.Length; i++) pts[i] = Rot(pts[i], c, rotDeg);
            Poly(pts, fill, outline);
        }

        public void Line(float x1, float y1, float x2, float y2, Color color, float width) =>
            Line(new Vector2(x1, y1), new Vector2(x2, y2), color, width);

        public void Line(Vector2 a, Vector2 b, Color color, float width)
        {
            p.BeginPath();
            p.MoveTo(P(a));
            p.LineTo(P(b));
            p.strokeColor = color; p.lineWidth = width * s; p.Stroke();
        }

        public void Curve(Vector2 a, Vector2 control, Vector2 b, Color color, float width)
        {
            p.BeginPath();
            p.MoveTo(P(a));
            p.QuadraticCurveTo(P(control), P(b));
            p.strokeColor = color; p.lineWidth = width * s; p.Stroke();
        }

        /// <summary>Two googly eyes centered at (x, y); look = pupil offset (-1..1).</summary>
        public void Eyes(float x, float y, float r, float lookX, float lookY)
        {
            Eye(x - r * 1.05f, y, r, lookX, lookY);
            Eye(x + r * 1.05f, y + r * 0.08f, r * 0.92f, lookX * 0.7f, -lookY);
        }

        public void Eye(float x, float y, float r, float lookX, float lookY)
        {
            Circle(x, y, r, White, Mathf.Max(1.5f, r * 0.28f));
            Circle(x + lookX * r * 0.42f, y + lookY * r * 0.42f, r * 0.5f, Ink, 0f);
            Circle(x + lookX * r * 0.42f - r * 0.18f, y + lookY * r * 0.42f - r * 0.18f, r * 0.14f, White, 0f);
        }

        /// <summary>A five-point star; fill 0..1 (half stars fill the left half).</summary>
        public void Star(float cx, float cy, float R, float fill)
        {
            float r = R * 0.47f;
            var pts = new Vector2[10];
            for (int i = 0; i < 10; i++)
            {
                float a = (-90f + i * 36f) * Mathf.Deg2Rad;
                float rr = i % 2 == 0 ? R : r;
                pts[i] = new Vector2(cx + Mathf.Cos(a) * rr, cy + Mathf.Sin(a) * rr);
            }
            if (fill >= 0.75f) { Poly(pts, StarOn, 0f); Poly(pts, Color.clear, R * 0.12f, StarEdge); return; }
            Poly(pts, StarOff, 0f);
            if (fill >= 0.25f)
            {
                // left half: top point, down the left side, to the bottom inner vertex (on the center line)
                var half = new[] { pts[0], pts[9], pts[8], pts[7], pts[6], pts[5] };
                Poly(half, StarOn, 0f);
            }
            Poly(pts, Color.clear, R * 0.12f, fill >= 0.25f ? StarEdge : new Color32(0xB7, 0xBC, 0xBC, 0xFF));
        }
    }
}
