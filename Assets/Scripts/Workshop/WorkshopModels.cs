using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>A renderer-only handmade workshop prop. Gameplay and ownership are attached by WorkshopWorld.</summary>
public sealed class WorkshopModel : MonoBehaviour
{
    public int lot = -1;
    public string recipeId;
    public Transform[] DisplaySlots = new Transform[0];
    public Transform movingPart;
    public float spinDegrees;
    public TMP_Text businessLabel;
    Renderer[] constructionParts;
    float progress = 1f;

    public Transform Anchor(string name)
    {
        foreach (var t in GetComponentsInChildren<Transform>(true)) if (t.name == name) return t;
        return null;
    }

    /// <summary>Foundation first, then structure, details and trim. Reversible; no model or material allocation.</summary>
    public void SetProgress(float value)
    {
        progress = float.IsNaN(value) ? 0f : Mathf.Clamp01(value);
        if (constructionParts == null) constructionParts = GetComponentsInChildren<Renderer>(true);
        int visible = Mathf.Max(1, Mathf.CeilToInt(constructionParts.Length * progress));
        for (int i = 0; i < constructionParts.Length; i++)
            if (constructionParts[i] != null) constructionParts[i].enabled = i < visible;
    }

    public void SetBusinessName(string value)
    {
        if (businessLabel != null) businessLabel.text = BusinessColors.CleanName(value).ToUpperInvariant();
    }

    void Update()
    {
        if (progress >= 1f && movingPart != null && spinDegrees != 0f)
            movingPart.Rotate(Vector3.up, spinDegrees * Time.deltaTime, Space.Self);
    }
}

/// <summary>
/// Metre-scale recycled-wood, enamel and steel workshop models. Every part is visual-only: callers add one deliberate
/// collision/interaction volume where needed, never a collider per screw. +Z is the face and Y=0 is floor/support.
/// DisplaySlotN points forward and marks the support surface, matching DisplayPiece's existing mount convention.
/// </summary>
public static class WorkshopModels
{
    static readonly Color Wood = new Color(.48f, .29f, .14f), WoodLight = new Color(.72f, .51f, .28f);
    static readonly Color Steel = new Color(.53f, .62f, .65f), Dark = new Color(.12f, .17f, .18f);
    static readonly Color Cream = new Color(.93f, .88f, .71f), Gold = new Color(.92f, .64f, .18f);
    static readonly Color Green = new Color(.28f, .55f, .39f), Coral = new Color(.88f, .34f, .22f);
    static readonly Color Blue = new Color(.24f, .48f, .65f);

    public static readonly string[] RecipeIds = {
        "bottle_cabinet", "rotating_pedestal", "wall_display", "grand_display", "business_sign",
        "junk_sculpture", "sculpture_tower", "sculpture_monument", "bag_hoop", "sorting_chute",
        "break_sofa", "scrap_radio", "spring_launcher", "doorbell"
    };

    public static bool IsDisplay(string id) => id == "bottle_cabinet" || id == "rotating_pedestal" || id == "wall_display" || id == "grand_display";
    public static bool IsGadget(string id) => id == "bag_hoop" || id == "sorting_chute" || id == "break_sofa" || id == "scrap_radio" || id == "spring_launcher" || id == "doorbell";

    public static WorkshopModel Build(string id, Transform parent, Color accent, string businessName = null)
    {
        bool known = false;
        foreach (string key in RecipeIds) if (key == id) { known = true; break; }
        if (!known) return null;
        var k = new Kit("Handmade " + id, parent, accent);
        switch (id)
        {
            case "bottle_cabinet": Cabinet(k); break;
            case "rotating_pedestal": Pedestal(k); break;
            case "wall_display": WallDisplay(k); break;
            case "grand_display": GrandDisplay(k); break;
            case "business_sign": Sign(k, businessName); break;
            case "junk_sculpture": Sculpture(k, 1); break;
            case "sculpture_tower": Sculpture(k, 2); break;
            case "sculpture_monument": Sculpture(k, 3); break;
            case "bag_hoop": Hoop(k); break;
            case "sorting_chute": Chute(k); break;
            case "break_sofa": Sofa(k); break;
            case "scrap_radio": Radio(k); break;
            case "spring_launcher": Launcher(k); break;
            case "doorbell": Bell(k); break;
        }
        if (k.root.Find("Action") == null) k.Anchor("Action", new Vector3(0f, 0f, 1f));
        return k.Finish(id);
    }

    public static WorkshopModel BuildBench(Transform parent, Color accent)
    {
        var k = new Kit("Handmade Workbench", parent, accent);
        foreach (float x in new[] { -.72f, .72f }) foreach (float z in new[] { -.28f, .28f })
            k.Box("Leg", new Vector3(x, .46f, z), new Vector3(.085f, .92f, .085f), Dark);
        k.Box("Lower tool shelf", new Vector3(0, .2f, 0), new Vector3(1.55f, .055f, .66f), Wood);
        k.Box("Thick worktop", new Vector3(0, .96f, 0), new Vector3(1.72f, .09f, .78f), WoodLight);
        k.Box("Front apron", new Vector3(0, .81f, .34f), new Vector3(1.58f, .2f, .045f), accent);
        foreach (float x in new[] { -.75f, .75f }) k.Box("Pegboard support", new Vector3(x, 1.29f, -.34f), new Vector3(.055f, .67f, .05f), Dark);
        k.Box("Pegboard", new Vector3(0, 1.37f, -.36f), new Vector3(1.56f, .54f, .035f), Wood);
        for (int x = -5; x <= 5; x++) for (int y = 0; y < 3; y++)
            k.Cyl("Peg hole", new Vector3(x * .125f, 1.19f + y * .14f, -.337f), .018f, .005f, Dark, new Vector3(90f, 0, 0));
        k.Box("Hammer handle", new Vector3(-.55f, 1.35f, -.3f), new Vector3(.035f, .25f, .035f), WoodLight, new Vector3(0, 0, -12f));
        k.Box("Hammer head", new Vector3(-.525f, 1.48f, -.3f), new Vector3(.14f, .06f, .06f), Steel);
        k.Beam("Wrench", new Vector3(.52f, 1.24f, -.3f), new Vector3(.59f, 1.48f, -.3f), .033f, Steel);
        k.Ring("Wrench jaw", new Vector3(.59f, 1.48f, -.3f), .085f, .018f, Steel, Quaternion.Euler(90, 0, 0));
        k.Box("Blueprint", new Vector3(-.49f, 1.01f, .07f), new Vector3(.34f, .007f, .38f), Blue, new Vector3(0, -12f, 0));
        for (int i = 0; i < 3; i++) k.Box("Blueprint lines", new Vector3(-.5f, 1.015f, -.045f + i * .09f), new Vector3(.22f, .003f, .005f), Cream);
        k.Box("Bench vise base", new Vector3(.62f, 1.045f, .2f), new Vector3(.2f, .08f, .17f), Dark);
        foreach (float x in new[] { .565f, .68f }) k.Box("Vise jaw", new Vector3(x, 1.115f, .2f), new Vector3(.06f, .1f, .18f), Steel);
        k.Cyl("Vise screw", new Vector3(.77f, 1.055f, .2f), .025f, .2f, Steel, new Vector3(0, 0, 90));
        k.Beam("Vise handle", new Vector3(.88f, .98f, .2f), new Vector3(.88f, 1.12f, .2f), .018f, Steel);
        for (int i = 0; i < 3; i++)
        {
            k.Box("Salvage bin", new Vector3(-.46f + i * .46f, .34f, .05f), new Vector3(.38f, .23f, .43f), i == 0 ? Blue : i == 1 ? Green : Coral);
            k.Box("Bin label", new Vector3(-.46f + i * .46f, .36f, .27f), new Vector3(.2f, .07f, .006f), Cream);
        }
        k.Text("Bench title", "MAKE SOMETHING", new Vector3(0, .82f, .368f), new Vector2(1.26f, .14f), Cream);
        k.Anchor("Action", new Vector3(0, 0, 1.05f));
        k.Anchor("Project", new Vector3(.02f, 1.013f, -.01f));
        k.Anchor("ProjectLabel", new Vector3(0, 1.73f, -.32f));
        return k.Finish("workbench");
    }

    static void Cabinet(Kit k)
    {
        foreach (float x in new[] { -.48f, .48f }) foreach (float z in new[] { -.15f, .15f })
        {
            k.Cyl("Bottle foot", new Vector3(x, .12f, z), .115f, .24f, Green);
            k.Cyl("Bottle neck", new Vector3(x, .27f, z), .057f, .08f, Green);
        }
        k.Box("Cabinet floor", new Vector3(0, .32f, 0), new Vector3(1.14f, .06f, .43f), WoodLight);
        k.Box("Salvaged back", new Vector3(0, .87f, -.185f), new Vector3(1.08f, 1.07f, .055f), k.accent);
        foreach (float x in new[] { -.53f, .53f }) k.Box("Frame upright", new Vector3(x, .86f, .015f), new Vector3(.065f, 1.06f, .43f), Wood);
        k.Box("Display shelf", new Vector3(0, .82f, .025f), new Vector3(1.03f, .045f, .41f), WoodLight);
        k.Box("Cabinet top", new Vector3(0, 1.43f, 0), new Vector3(1.18f, .065f, .46f), WoodLight);
        k.Box("Brass shelf lip", new Vector3(0, .807f, .243f), new Vector3(1.12f, .035f, .025f), Gold);
        k.Slot(new Vector3(0, .845f, .04f));
        for (int i = 0; i < 4; i++) k.Cyl("Bottle mosaic", new Vector3(-.35f + i * .23f, .59f, -.135f), .105f, .23f, i % 2 == 0 ? Green : Blue);
        k.Text("Label", "BOTTLE-BUILT", new Vector3(0, 1.432f, .242f), new Vector2(.74f, .05f), Dark);
    }

    static void Pedestal(Kit k)
    {
        k.Cyl("Tyre foot", new Vector3(0, .075f, 0), .6f, .15f, Dark);
        k.Cyl("Hubcap", new Vector3(0, .15f, 0), .49f, .055f, Steel);
        k.Cyl("Salvaged drum", new Vector3(0, .56f, 0), .29f, .79f, k.accent);
        foreach (float y in new[] { .21f, .9f }) k.Cyl("Drum band", new Vector3(0, y, 0), .33f, .05f, Steel);
        for (int i = 0; i < 12; i++)
        {
            float a = i * 30f * Mathf.Deg2Rad;
            k.Box("Gear tooth", new Vector3(Mathf.Cos(a) * .24f, .99f, Mathf.Sin(a) * .24f), new Vector3(.075f, .065f, .05f), Gold, new Vector3(0, -i * 30, 0));
        }
        var turn = Node(k.root, "Turntable", new Vector3(0, 1.035f, 0));
        Part(turn, PrimitiveType.Cylinder, "Rotating platter", Vector3.zero, new Vector3(.52f, .028f, .52f), Steel);
        Part(turn, PrimitiveType.Cylinder, "Felt", new Vector3(0, .03f, 0), new Vector3(.44f, .006f, .44f), k.accent);
        k.Slot(new Vector3(0, .037f, 0), turn);
        k.moving = turn; k.spin = 14f;
    }

    static void WallDisplay(Kit k)
    {
        for (int i = 0; i < 5; i++) k.Box("Pallet board", new Vector3((i - 2) * .24f, .63f, -.1f), new Vector3(.22f, 1.22f, .05f), i % 2 == 0 ? Wood : WoodLight);
        foreach (float x in new[] { -.4f, .4f }) k.Beam("Angled bracket", new Vector3(x, .22f, -.055f), new Vector3(x, .43f, .23f), .04f, Steel);
        k.Box("Shelf", new Vector3(0, .455f, .07f), new Vector3(1.28f, .055f, .38f), k.accent);
        k.Box("Top banner", new Vector3(0, 1.12f, -.055f), new Vector3(1.29f, .16f, .05f), k.accent);
        k.Text("Banner", "SECOND LIFE", new Vector3(0, 1.12f, -.022f), new Vector2(1.02f, .12f), Cream);
        foreach (float x in new[] { -.49f, .49f }) foreach (float y in new[] { .12f, 1.12f }) k.Ball("Bolt", new Vector3(x, y, -.058f), .035f, Steel);
        k.Slot(new Vector3(0, .484f, .07f));
    }

    static void GrandDisplay(Kit k)
    {
        k.Box("Showcase foot", new Vector3(0, .07f, 0), new Vector3(1.7f, .14f, .46f), Dark);
        k.Box("Cabinet", new Vector3(0, .4f, 0), new Vector3(1.56f, .56f, .41f), Wood);
        foreach (float x in new[] { -.39f, .39f })
        {
            k.Box("Enamel panel", new Vector3(x, .4f, .215f), new Vector3(.7f, .42f, .035f), k.accent);
            k.Ball("Pull", new Vector3(x * .3f, .46f, .25f), .035f, Gold);
        }
        k.Box("Velvet stage", new Vector3(0, .725f, .02f), new Vector3(1.64f, .09f, .43f), k.accent);
        k.Box("Curtain back", new Vector3(0, 1.25f, -.19f), new Vector3(1.5f, 1.02f, .05f), Dark);
        foreach (float x in new[] { -.77f, .77f }) k.Cyl("Showcase column", new Vector3(x, 1.31f, 0), .075f, 1.17f, Gold);
        k.Box("Crown cornice", new Vector3(0, 1.94f, 0), new Vector3(1.76f, .12f, .48f), WoodLight);
        for (int i = -4; i <= 4; i++) k.Ball("Marquee lamp", new Vector3(i * .18f, 1.95f, .25f), .055f, Cream);
        k.Cyl("Centre plinth", new Vector3(0, .83f, .03f), .46f, .12f, Gold);
        k.Slot(new Vector3(0, .894f, .03f));
        k.Text("Marquee", "THE GOOD STUFF", new Vector3(0, 2.09f, .08f), new Vector2(1.18f, .16f), Cream);
    }

    static void Sign(Kit k, string name)
    {
        foreach (float x in new[] { -.63f, .63f })
        {
            k.Box("Sign footing", new Vector3(x, .055f, 0), new Vector3(.34f, .11f, .45f), Dark);
            k.Box("Salvaged sign post", new Vector3(x, .9f, 0), new Vector3(.08f, 1.7f, .09f), Wood);
        }
        k.Box("Sign frame", new Vector3(0, 1.61f, 0), new Vector3(1.84f, .79f, .12f), WoodLight);
        k.Box("Enamel sign", new Vector3(0, 1.61f, .068f), new Vector3(1.72f, .67f, .025f), k.accent);
        k.label = k.Text("Business name", BusinessColors.CleanName(name).ToUpperInvariant(), new Vector3(0, 1.67f, .087f), new Vector2(1.53f, .39f), Cream);
        k.Text("Handmade", "BUILT FROM GOOD RUBBISH", new Vector3(0, 1.385f, .086f), new Vector2(1.48f, .08f), Cream);
        for (int i = -3; i <= 3; i++) k.Ball("Sign bulb", new Vector3(i * .245f, 2.035f, .05f), .075f, Gold);
        foreach (float x in new[] { -.79f, .79f }) k.Ball("Rivet", new Vector3(x, 1.6f, .09f), .04f, Steel);
    }

    static void Sculpture(Kit k, int tier)
    {
        k.Cyl("Concrete plinth", new Vector3(0, .075f, 0), tier == 3 ? 1.08f : .8f, .15f, Steel);
        float y = .2f;
        for (int level = 0; level < tier; level++)
        {
            float width = .54f - level * .07f;
            k.Box("Tin torso", new Vector3(0, y + .23f, 0), new Vector3(width, .42f, .3f), level % 2 == 0 ? k.accent : Coral, new Vector3(0, level * 19f, (level % 2 == 0 ? 1 : -1) * 7f));
            k.Cyl("Hubcap belt", new Vector3(0, y + .45f, 0), width + .12f, .06f, Steel);
            foreach (float s in new[] { -1f, 1f })
            {
                Vector3 hand = new Vector3(s * (width * .5f + .18f), y + .3f + s * .09f, .03f);
                k.Beam("Scrap arm", new Vector3(s * width * .5f, y + .28f, 0), hand, .055f, Gold);
                k.Ring("Washer hand", hand, .14f, .035f, Dark, Quaternion.Euler(90, 0, 0));
            }
            y += .52f;
        }
        k.Ball("Teapot head", new Vector3(0, y + .11f, 0), new Vector3(.42f, .3f, .33f), Cream);
        foreach (float x in new[] { -.092f, .092f })
        {
            k.Cyl("Button eye", new Vector3(x, y + .16f, .153f), .085f, .023f, Dark, new Vector3(90, 0, 0));
            k.Ball("Eye glint", new Vector3(x + .013f, y + .176f, .17f), .026f, Cream);
        }
        k.Box("Crooked grin", new Vector3(0, y + .035f, .161f), new Vector3(.16f, .025f, .025f), Coral, new Vector3(0, 0, -8f));
        k.Cyl("Saucepan hat", new Vector3(0, y + .29f, 0), .48f, .095f, k.accent);
        if (tier >= 2) k.Beam("Hat handle", new Vector3(.18f, y + .31f, 0), new Vector3(.43f, y + .39f, 0), .04f, Dark);
        if (tier == 3)
        {
            for (int i = 0; i < 7; i++)
            {
                float a = (i * 26f - 78f) * Mathf.Deg2Rad;
                Vector3 tip = new Vector3(Mathf.Sin(a) * .53f, y + .29f + Mathf.Cos(a) * .44f, -.12f);
                k.Beam("Spoon sunray", new Vector3(0, y + .26f, -.12f), tip, .022f, Gold);
                k.Ball("Spoon bowl", tip, new Vector3(.10f, .13f, .035f), Steel);
            }
        }
        k.Box("Art plaque", new Vector3(0, .135f, .37f), new Vector3(.38f, .1f, .025f), Gold);
        k.Text("Art title", tier == 1 ? "SCRAP PAL" : tier == 2 ? "HIGHER PURPOSE" : "TRASHCENDENCE", new Vector3(0, .137f, .386f), new Vector2(.35f, .07f), Dark);
    }

    static void Hoop(Kit k)
    {
        k.Box("Weighted foot", new Vector3(0, .07f, -.33f), new Vector3(.9f, .14f, .52f), Dark);
        k.Cyl("Hoop post", new Vector3(0, 1.0f, -.33f), .085f, 1.88f, Steel);
        k.Box("Pallet backboard", new Vector3(0, 1.72f, -.31f), new Vector3(1.13f, .69f, .065f), k.accent);
        k.Box("Backboard square", new Vector3(0, 1.68f, -.273f), new Vector3(.43f, .30f, .009f), Cream);
        k.Box("Backboard inner", new Vector3(0, 1.68f, -.266f), new Vector3(.37f, .24f, .008f), k.accent);
        Vector3 ring = new Vector3(0, 1.52f, .28f);
        k.Beam("Hoop bracket", new Vector3(0, 1.52f, -.31f), new Vector3(0, 1.52f, -.15f), .075f, Steel);
        k.Ring("Bag hoop rim", ring, .9f, .045f, Coral, Quaternion.identity);
        for (int i = 0; i < 12; i++)
        {
            float a = i * 30f * Mathf.Deg2Rad;
            Vector3 upper = ring + new Vector3(Mathf.Cos(a) * .43f, -.015f, Mathf.Sin(a) * .43f);
            Vector3 lower = ring + new Vector3(Mathf.Cos(a + .24f) * .30f, -.43f, Mathf.Sin(a + .24f) * .30f);
            k.Beam("Rope net", upper, lower, .012f, Cream);
        }
        k.Ring("Net bottom", ring + Vector3.down * .43f, .62f, .012f, Cream, Quaternion.identity);
        k.Anchor("RingCenter", ring);
        k.Anchor("Action", new Vector3(0, 0, 1.5f));
        k.Text("Hoop label", "BIN IT TO WIN IT", new Vector3(0, 1.96f, -.266f), new Vector2(.96f, .1f), Cream);
    }

    static void Chute(Kit k)
    {
        foreach (float x in new[] { -.28f, .28f })
        {
            k.Box("Tall chute leg", new Vector3(x, .57f, -.37f), new Vector3(.055f, 1.14f, .055f), Dark);
            k.Box("Short chute leg", new Vector3(x, .31f, .5f), new Vector3(.055f, .62f, .055f), Dark);
            k.Beam("Chute edge", new Vector3(x, 1.15f, -.44f), new Vector3(x, .67f, .59f), .07f, k.accent);
        }
        Vector3 from = new Vector3(0, 1.1f, -.43f), to = new Vector3(0, .62f, .58f);
        var slide = k.Box("Smooth chute", (from + to) * .5f, new Vector3(.53f, .035f, Vector3.Distance(from, to)), Steel);
        slide.rotation = k.root.rotation * Quaternion.LookRotation(to - from, Vector3.up);
        k.Box("Feed sign", new Vector3(0, 1.32f, -.43f), new Vector3(.58f, .22f, .04f), k.accent);
        k.Text("Feed label", "SORT THIS WAY", new Vector3(0, 1.32f, -.405f), new Vector2(.51f, .14f), Cream);
        k.Anchor("Input", from + Vector3.up * .16f); k.Anchor("Output", to + Vector3.up * .1f);
        k.Anchor("Action", new Vector3(0, 0, 1.25f));
    }

    static void Sofa(Kit k)
    {
        foreach (float x in new[] { -.57f, .57f }) foreach (float z in new[] { -.24f, .24f })
            k.Box("Crate foot", new Vector3(x, .10f, z), new Vector3(.13f, .2f, .13f), Wood);
        for (int i = 0; i < 4; i++) k.Box("Pallet slat", new Vector3(0, .24f, -.27f + i * .18f), new Vector3(1.46f, .09f, .145f), i % 2 == 0 ? WoodLight : Wood);
        foreach (float x in new[] { -.35f, .35f }) k.Box("Patchwork cushion", new Vector3(x, .43f, .03f), new Vector3(.66f, .25f, .64f), x < 0 ? k.accent : Blue);
        k.Box("Back rest", new Vector3(0, .72f, -.28f), new Vector3(1.48f, .7f, .17f), k.accent, new Vector3(-8f, 0, 0));
        foreach (float x in new[] { -.75f, .75f }) k.Box("Arm rest", new Vector3(x, .61f, .03f), new Vector3(.17f, .38f, .72f), WoodLight);
        k.Box("Sewn patch", new Vector3(-.43f, .56f, .05f), new Vector3(.19f, .007f, .17f), Cream, new Vector3(0, 19f, 0));
        for (int i = -2; i <= 2; i++) k.Box("Patch stitch", new Vector3(-.43f + i * .027f, .565f, .133f), new Vector3(.008f, .003f, .032f), Dark);
        k.Anchor("Seat", new Vector3(0, .555f, .05f)); k.Anchor("Action", new Vector3(0, 0, 1f));
    }

    static void Radio(Kit k)
    {
        foreach (float x in new[] { -.3f, .3f }) foreach (float z in new[] { -.14f, .14f }) k.Box("Radio stand leg", new Vector3(x, .32f, z), new Vector3(.04f, .64f, .04f), Dark);
        k.Box("Side table", new Vector3(0, .665f, 0), new Vector3(.73f, .05f, .39f), WoodLight);
        k.Box("Radio case", new Vector3(0, .875f, 0), new Vector3(.65f, .36f, .28f), k.accent);
        k.Cyl("Speaker", new Vector3(-.16f, .87f, .15f), .235f, .025f, Dark, new Vector3(90, 0, 0));
        for (int i = -2; i <= 2; i++) k.Box("Speaker grille", new Vector3(-.16f, .87f + i * .036f, .166f), new Vector3(.19f, .009f, .008f), Steel);
        k.Box("Tuning window", new Vector3(.16f, .955f, .145f), new Vector3(.21f, .065f, .015f), Cream);
        k.Box("Tuning needle", new Vector3(.20f, .955f, .155f), new Vector3(.008f, .052f, .005f), Coral);
        foreach (float x in new[] { .10f, .24f }) k.Cyl("Radio knob", new Vector3(x, .79f, .16f), .065f, .04f, Gold, new Vector3(90, 0, 0));
        k.Beam("Aerial", new Vector3(.22f, 1.06f, -.03f), new Vector3(.35f, 1.44f, -.03f), .012f, Steel);
        k.Ball("Aerial tip", new Vector3(.35f, 1.44f, -.03f), .028f, Coral);
        k.Anchor("MusicSource", new Vector3(0, .9f, .18f)); k.Anchor("Action", new Vector3(0, 0, .85f));
    }

    static void Launcher(Kit k)
    {
        k.Box("Launcher foot", new Vector3(0, .07f, 0), new Vector3(.77f, .14f, .7f), Dark);
        foreach (float x in new[] { -.24f, .24f })
        {
            k.Cyl("Piston", new Vector3(x, .43f, -.15f), .075f, .68f, Steel);
            for (int i = 0; i < 7; i++) k.Ring("Coil", new Vector3(x, .19f + i * .078f, -.15f), .17f, .024f, Gold, Quaternion.identity);
        }
        k.Box("Spring plate", new Vector3(0, .78f, 0), new Vector3(.68f, .09f, .62f), k.accent, new Vector3(-16f, 0, 0));
        for (int i = -2; i <= 2; i++) k.Box("Grip strip", new Vector3(i * .105f, .833f, .025f), new Vector3(.045f, .012f, .40f), Cream, new Vector3(-16f, 0, 0));
        var muzzle = k.Anchor("Muzzle", new Vector3(0, .98f, .14f));
        muzzle.localRotation = Quaternion.LookRotation(new Vector3(0, .9f, .45f).normalized, Vector3.up);
        // The host's 1.1-second ballistic arc descends through the adjacent hoop, below the annex roof.
        // This is an aiming point, not a second launch surface; the bag keeps its normal physics afterwards.
        k.Anchor("Landing", new Vector3(0, .93f, 3.10f));
        k.Anchor("Action", new Vector3(0, 0, 1f));
        k.Text("Warning", "BOING!", new Vector3(0, .18f, .357f), new Vector2(.55f, .13f), Gold);
    }

    static void Bell(Kit k)
    {
        k.Box("Bell base", new Vector3(0, .04f, 0), new Vector3(.42f, .08f, .36f), Dark);
        k.Box("Bell post", new Vector3(0, .75f, 0), new Vector3(.095f, 1.42f, .085f), Wood);
        k.Box("Bell board", new Vector3(0, 1.29f, .03f), new Vector3(.37f, .51f, .065f), k.accent);
        k.Ball("Salvaged bell", new Vector3(0, 1.38f, .11f), new Vector3(.29f, .24f, .14f), Gold);
        k.Beam("Striker", new Vector3(.17f, 1.18f, .13f), new Vector3(.17f, 1.37f, .13f), .022f, Steel);
        k.Ball("Striker head", new Vector3(.17f, 1.38f, .13f), .046f, Dark);
        k.Cyl("Doorbell button", new Vector3(0, 1.1f, .08f), .09f, .055f, Coral, new Vector3(90, 0, 0));
        k.Text("Bell label", "DING DONG", new Vector3(0, .96f, .059f), new Vector2(.43f, .10f), Cream);
        k.Anchor("Action", new Vector3(0, 0, .85f));
    }

    public static Transform Node(Transform parent, string name, Vector3 position)
    {
        var go = new GameObject(name) { layer = 2 };
        go.transform.SetParent(parent, false); go.transform.localPosition = position;
        return go.transform;
    }

    static Transform Part(Transform parent, PrimitiveType primitive, string name, Vector3 at, Vector3 size, Color color, Vector3 euler = default)
    {
        var go = new GameObject(name) { layer = 2 };
        go.transform.SetParent(parent, false); go.transform.localPosition = at;
        go.transform.localRotation = Quaternion.Euler(euler); go.transform.localScale = size;
        go.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>(primitive + ".fbx");
        go.AddComponent<MeshRenderer>().sharedMaterial = SortingKit.RuntimeMaterial("mat/workshop_" + ColorUtility.ToHtmlStringRGB(color), color);
        return go.transform;
    }

    public static Transform BoxVisual(Transform parent, string name, Vector3 at, Vector3 size, Color color) =>
        Part(parent, PrimitiveType.Cube, name, at, size, color);

    /// <summary>
    /// TMP caches its renderer and font in Awake. Keep the default shared font material;
    /// outline setters instantiate a material and must not be used for these plain labels.
    /// Hidden depot interiors and inactive construction bays are normal, so initialize the label
    /// unparented, then attach it synchronously. No inactive gameplay hierarchy is ever activated.
    /// </summary>
    public static TextMeshPro MakeLabel(Transform parent, string name, string value, float size, Color color)
    {
        var go = new GameObject(name) { layer = 2 };
        var label = go.AddComponent<TextMeshPro>();
        label.text = value;
        label.fontSize = size;
        label.color = color;
        label.alignment = TextAlignmentOptions.Center;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.richText = false;
        label.fontStyle = FontStyles.Bold;
        label.rectTransform.sizeDelta = new Vector2(4f, 1f);
        if (label.font != null)
        {
            label.fontSharedMaterial = label.font.material;
            go.GetComponent<Renderer>().sharedMaterial = label.font.material;
        }
        label.ForceMeshUpdate();
        label.transform.SetParent(parent, false);
        return label;
    }

    sealed class Kit
    {
        public readonly Transform root;
        public readonly Color accent;
        public readonly List<Transform> slots = new List<Transform>();
        public Transform moving;
        public float spin;
        public TMP_Text label;
        public Kit(string name, Transform parent, Color accent) { root = Node(parent, name, Vector3.zero); this.accent = accent; }
        public Transform Box(string name, Vector3 at, Vector3 size, Color c, Vector3 euler = default) => Part(root, PrimitiveType.Cube, name, at, size, c, euler);
        public Transform Ball(string name, Vector3 at, float d, Color c) => Ball(name, at, Vector3.one * d, c);
        public Transform Ball(string name, Vector3 at, Vector3 size, Color c) => Part(root, PrimitiveType.Sphere, name, at, size, c);
        public Transform Cyl(string name, Vector3 at, float diameter, float height, Color c, Vector3 euler = default) => Part(root, PrimitiveType.Cylinder, name, at, new Vector3(diameter, height * .5f, diameter), c, euler);
        public Transform Beam(string name, Vector3 from, Vector3 to, float width, Color c)
        {
            var t = Box(name, (from + to) * .5f, new Vector3(width, width, Vector3.Distance(from, to)), c);
            t.localRotation = Quaternion.LookRotation(to - from, Vector3.up); return t;
        }
        public void Ring(string name, Vector3 at, float diameter, float thickness, Color c, Quaternion rotation)
        {
            var ring = Node(root, name, at); ring.localRotation = rotation;
            const int segments = 20;
            for (int i = 0; i < segments; i++)
            {
                float a = i * Mathf.PI * 2f / segments, b = (i + 1) * Mathf.PI * 2f / segments;
                Vector3 p = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * diameter * .5f;
                Vector3 q = new Vector3(Mathf.Cos(b), 0, Mathf.Sin(b)) * diameter * .5f;
                var t = Part(ring, PrimitiveType.Cube, "Rim segment", (p + q) * .5f, new Vector3(thickness, thickness, Vector3.Distance(p, q) + thickness * .25f), c);
                t.localRotation = Quaternion.LookRotation(q - p, Vector3.up);
            }
        }
        public Transform Anchor(string name, Vector3 p) => Node(root, name, p);
        public void Slot(Vector3 p, Transform parent = null) { slots.Add(Node(parent != null ? parent : root, "DisplaySlot" + slots.Count, p)); }
        public TextMeshPro Text(string name, string value, Vector3 p, Vector2 size, Color color)
        {
            var text = MakeLabel(root, name, value, 3f, color);
            text.transform.localPosition = p; text.transform.localRotation = Quaternion.Euler(0, 180, 0);
            text.rectTransform.sizeDelta = size; text.enableAutoSizing = true; text.fontSizeMin = .3f; text.fontSizeMax = 3f;
            text.alignment = TextAlignmentOptions.Center; text.richText = false;
            return text;
        }
        public WorkshopModel Finish(string id)
        {
            var model = root.gameObject.AddComponent<WorkshopModel>(); model.recipeId = id;
            model.DisplaySlots = slots.ToArray(); model.movingPart = moving; model.spinDegrees = spin; model.businessLabel = label;
            return model;
        }
    }
}
