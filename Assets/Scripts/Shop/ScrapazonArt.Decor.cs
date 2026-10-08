using UnityEngine;

public static partial class ScrapazonArt
{
    /// <summary>Product art for the workshop decor aisle (WorkshopDecorShop). False = not a decor product.</summary>
    static bool DrawDecor(string id, Pen pen)
    {
        var piece = WorkshopCatalog.DecorFor(id);
        if (piece == null) return false;
        Color glass = new Color32(0xBF, 0xE8, 0xFF, 0xFF), wood = new Color32(0xC9, 0x9A, 0x5E, 0xFF), teal = new Color32(0x1E, 0x9C, 0x8B, 0xFF);
        switch (piece.id)
        {
            case "bottle_cabinet":
                pen.RoundRect(100f, 76f, 96f, 120f, 6f, 0f, wood, 3.5f);
                pen.RoundRect(100f, 70f, 76f, 92f, 3f, 0f, glass, 3f);
                pen.Line(62f, 74f, 138f, 74f, Ink, 2.5f);
                pen.RoundRect(84f, 54f, 14f, 30f, 5f, 0f, new Color32(0x6F, 0xD3, 0x7A, 0xFF), 2.5f);
                pen.RoundRect(84f, 34f, 6f, 10f, 2f, 0f, new Color32(0x6F, 0xD3, 0x7A, 0xFF), 2f);
                Gem(pen, 116f, 98f, 9f, GemPurple);
                pen.Circle(142f, 76f, 3.5f, Gold, 2f);
                pen.Line(72f, 30f, 82f, 44f, White, 2f);
                break;
            case "rotating_pedestal":
                pen.Ellipse(100f, 124f, 52f, 12f, Marble, 3f);
                pen.RoundRect(100f, 100f, 60f, 44f, 4f, 0f, Marble, 3f);
                pen.Ellipse(100f, 78f, 40f, 9f, Velvet, 3f);
                pen.Poly(new[] { new Vector2(86f, 40f), new Vector2(114f, 40f), new Vector2(108f, 62f), new Vector2(92f, 62f) }, Gold, 3f);
                pen.RoundRect(100f, 68f, 22f, 7f, 2f, 0f, Gold, 2.5f);
                pen.ArcStroke(100f, 90f, 64f, 200f, 250f, Ink, 3f);
                pen.ArcStroke(100f, 90f, 64f, 290f, 340f, Ink, 3f);
                pen.Poly(new[] { new Vector2(164f, 62f), new Vector2(170f, 78f), new Vector2(154f, 74f) }, Ink, 0f);
                break;
            case "wall_display":
                pen.RoundRect(100f, 70f, 150f, 96f, 4f, 0f, new Color32(0xE8, 0xDC, 0xC4, 0xFF), 0f);
                pen.RoundRect(100f, 66f, 104f, 74f, 4f, 0f, DarkWood, 3.5f);
                pen.RoundRect(100f, 66f, 84f, 54f, 2f, 0f, Velvet, 2.5f);
                pen.Circle(100f, 66f, 17f, Gold, 3f);
                pen.Eyes(100f, 64f, 4f, 0.5f, 0.3f);
                pen.Circle(100f, 24f, 4f, Steel, 2f);
                pen.RoundRect(100f, 116f, 60f, 10f, 2f, 0f, Gold, 2.5f);
                break;
            case "grand_display":
                pen.RoundRect(100f, 128f, 150f, 16f, 3f, 0f, DarkWood, 3f);
                pen.RoundRect(100f, 78f, 120f, 86f, 8f, 0f, glass, 3.5f);
                pen.RoundRect(100f, 30f, 132f, 14f, 4f, 0f, Gold, 3f);
                for (int i = 0; i < 4; i++) pen.Circle(52f + i * 32f, 30f, 4f, White, 1.5f);
                Gem(pen, 100f, 78f, 20f, GemBlue);
                pen.Line(60f, 50f, 72f, 70f, White, 2.5f);
                pen.Line(40f, 12f, 30f, 2f, Gold, 3f); pen.Line(160f, 12f, 170f, 2f, Gold, 3f); pen.Line(100f, 10f, 100f, 0f, Gold, 3f);
                break;
            case "business_sign":
                pen.Line(60f, 10f, 60f, 36f, Ink, 3f); pen.Line(140f, 10f, 140f, 36f, Ink, 3f);
                pen.RoundRect(100f, 72f, 156f, 72f, 10f, -3f, teal, 3.5f);
                for (int i = 0; i < 7; i++) pen.Circle(34f + i * 22f, 44f - i * 1.1f, 4.5f, i % 2 == 0 ? Gold : White, 1.5f);
                pen.Line(44f, 74f, 156f, 68f, White, 6f);
                pen.Line(60f, 92f, 136f, 88f, White, 4f);
                pen.Eyes(150f, 96f, 4f, 0.5f, 0.4f);
                break;
            case "bag_hoop":
                pen.RoundRect(100f, 46f, 92f, 64f, 4f, 0f, White, 3.5f);
                pen.Polyline(new[] { new Vector2(82f, 69f), new Vector2(82f, 43f), new Vector2(118f, 43f), new Vector2(118f, 69f) }, Red, 3f);
                pen.Ellipse(100f, 80f, 30f, 8f, Orange, 3f);
                pen.Ellipse(100f, 80f, 22f, 4.5f, White, 2f);
                for (int i = -2; i <= 2; i++) pen.Line(100f + i * 12f, 86f, 100f + i * 7f, 118f, Ink, 1.8f);
                pen.Circle(150f, 28f, 16f, new Color32(0x3A, 0x3A, 0x44, 0xFF), 3f);
                pen.Line(146f, 14f, 152f, 6f, Ink, 3f);
                pen.ArcStroke(128f, 60f, 34f, 300f, 350f, Ink, 2.5f);
                break;
            case "sorting_chute":
                pen.Poly(new[] { new Vector2(24f, 30f), new Vector2(60f, 22f), new Vector2(176f, 96f), new Vector2(150f, 112f) }, Steel, 3.5f);
                pen.Line(40f, 34f, 160f, 106f, Ink, 2f);
                pen.Line(58f, 26f, 172f, 98f, Ink, 2f);
                pen.Line(60f, 60f, 60f, 132f, Ink, 4f); pen.Line(150f, 112f, 150f, 132f, Ink, 4f);
                pen.Circle(40f, 96f, 14f, wood, 3f);
                pen.Line(40f, 96f, 22f, 78f, Ink, 4f);
                pen.Circle(22f, 78f, 5f, Red, 2f);
                pen.Circle(110f, 56f, 15f, new Color32(0x3A, 0x3A, 0x44, 0xFF), 3f);
                pen.Line(106f, 43f, 112f, 36f, Ink, 3f);
                break;
            case "break_sofa":
                pen.RoundRect(100f, 62f, 150f, 56f, 14f, 0f, new Color32(0x8E, 0x5B, 0xD0, 0xFF), 3.5f);
                pen.RoundRect(100f, 94f, 132f, 34f, 8f, 0f, new Color32(0xA8, 0x78, 0xE0, 0xFF), 3.5f);
                pen.RoundRect(28f, 88f, 26f, 54f, 10f, 0f, new Color32(0x8E, 0x5B, 0xD0, 0xFF), 3.5f);
                pen.RoundRect(172f, 88f, 26f, 54f, 10f, 0f, new Color32(0x8E, 0x5B, 0xD0, 0xFF), 3.5f);
                pen.RoundRect(74f, 58f, 30f, 22f, 2f, 8f, Cardboard, 2.5f);   // a patch
                pen.Line(64f, 52f, 68f, 56f, Ink, 2f); pen.Line(82f, 62f, 86f, 66f, Ink, 2f);
                pen.Line(120f, 96f, 128f, 82f, Steel, 3f);                   // a spring, escaping
                pen.CircleStroke(130f, 78f, 5f, Steel, 2.5f);
                pen.Line(40f, 118f, 40f, 128f, Ink, 4f); pen.Line(160f, 118f, 160f, 128f, Ink, 4f);
                break;
            case "scrap_radio":
                Radio(pen);
                pen.RoundRect(160f, 112f, 26f, 16f, 3f, 0f, Red, 2.5f);      // the one switch
                pen.Line(154f, 112f, 166f, 112f, White, 3f);
                break;
            case "spring_launcher":
                pen.RoundRect(100f, 126f, 150f, 14f, 3f, 0f, DarkWood, 3f);
                for (int i = 0; i < 5; i++) pen.Line(70f + (i % 2) * 60f, 118f - i * 10f, 130f - (i % 2) * 60f, 108f - i * 10f, Steel, 5f);
                pen.RoundRect(100f, 64f, 96f, 12f, 3f, -6f, Red, 3f);
                pen.Circle(104f, 36f, 18f, new Color32(0x3A, 0x3A, 0x44, 0xFF), 3f);
                pen.Line(100f, 20f, 108f, 12f, Ink, 3f);
                pen.Line(60f, 30f, 46f, 14f, Ink, 2.5f); pen.Line(74f, 20f, 66f, 4f, Ink, 2.5f); pen.Line(146f, 30f, 160f, 14f, Ink, 2.5f);
                break;
            case "doorbell":
                pen.RoundRect(100f, 74f, 70f, 110f, 14f, 0f, new Color32(0xFF, 0xD6, 0x40, 0xFF), 3.5f);
                pen.Circle(100f, 62f, 24f, Red, 3.5f);
                pen.Circle(100f, 62f, 12f, White, 2.5f);
                pen.Eyes(100f, 104f, 5f, 0.5f, 0.6f);
                pen.ArcStroke(100f, 62f, 44f, 150f, 210f, Ink, 3f);
                pen.ArcStroke(100f, 62f, 44f, 330f, 390f, Ink, 3f);
                pen.ArcStroke(100f, 62f, 56f, 160f, 200f, Ink, 2.5f);
                pen.ArcStroke(100f, 62f, 56f, 340f, 380f, Ink, 2.5f);
                Notes(pen);
                break;
            default: return false;
        }
        return true;
    }
}
