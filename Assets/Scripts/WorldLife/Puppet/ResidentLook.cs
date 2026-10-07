using UnityEngine;

/// <summary>Who a resident is (§3.1 casts; adults only, no children on streets). Byte order is stable (saved nowhere, but
/// D's routines key off it).</summary>
public enum ResidentArchetype : byte
{
    // Mildly Pleasant Acres
    RobeDad = 0, CurlerGran = 1, Commuter = 2, Jogger = 3, DogPerson = 4, Gardener = 5,
    // Minivan Meadows
    SoccerMom = 6, CargoDad = 7, PowerWalker = 8, HoaLady = 9,
    // Snootington Hills
    MonocleGent = 10, PearlLady = 11, TennisTwin = 12, Pemberton = 13, Housekeeper = 14,
    // Billionaire Bluffs
    Butler = 15, Billionaire = 16, Bodyguard = 17,
}

public enum PuppetBuild : byte { Bean, Ball, Pear, Stick }
public enum PuppetNose : byte { Ball, Long, Potato, Hook }
public enum PuppetHair : byte { Bald, Short, CombOver, Bun, Curlers, Beehive, Afro, Slick, Ponytail, Buzz }
public enum PuppetHat : byte { None, Visor, TopHat, Sweatband, StrawHat, Cap, Bowler, SunHat, Nightcap, Beanie }
public enum PuppetOutfit : byte { None, Robe, Cardigan, Tie, Vest, Apron, Suit, Tux, Tracksuit, Pajamas, GoldRobe }
public enum PuppetProp : byte { None, Mug, Newspaper, Briefcase, Leash, Clipboard, Cane, Tumbler, Weights, Platter, Racket, Duster, Trowel, Cigar, Flashlight, Phone }
public enum PuppetAccent : byte { None, Monocle, Pearls, Earpiece, Sunglasses, Glasses, BowTie, Mustache, PencilMustache }

/// <summary>
/// One resident's look (§3.1): picked from <see cref="LifeHash"/> by <see cref="ResidentLook.StyleFor"/>, so a resident
/// looks the same every day and on every machine. Plain data; <see cref="ResidentLook.Compose"/> turns it into parts.
/// </summary>
public struct ResidentStyle
{
    public ResidentArchetype archetype;
    public byte district;
    /// <summary>Metres to the top of the head (1.45–1.85; Chuck is 1.59).</summary>
    public float height;
    public float headScale;
    public PuppetBuild build;
    public byte skinTone;
    /// <summary>Clothes: top, bottom, trim (mugs, tumblers), hair and shoe colours.</summary>
    public Color top, bottom, trim, hair, shoes;
    public PuppetNose nose;
    public float noseSize;
    public PuppetHair hairKind;
    public PuppetHat hat;
    public Color hatColor;
    public PuppetOutfit outfit;
    public Color outfitColor;
    public PuppetProp prop;
    public PuppetAccent accent, accent2;
    /// <summary>Bare legs (shorts / skirt) and big squashed slippers.</summary>
    public bool bareLegs, slippers;
    /// <summary>The voice (mumble pitch).</summary>
    public float voicePitch;
    /// <summary>First name for chatter / letters ("Gary", "Mrs. Okafor").</summary>
    public string name;

    public Color Skin => PuppetKit.SkinTones[skinTone % PuppetKit.SkinTones.Length];
}

/// <summary>Every renderer slot of the universal puppet rig. A style switches slots on or off and reshapes them; the rig is
/// built once per pooled puppet and restyled on assignment (no allocation).</summary>
public enum PartSlot : byte
{
    Body, Outfit, Skull, Nose, Mouth, EyeL, PupilL, EyeR, PupilR, BrowL, BrowR,
    Hat0, Hat1, Hat2, Accent, Accent2,
    ArmL, MittL, ArmR, MittR, LegL, ShoeL, LegR, ShoeR,
    Prop, Prop2, PropL, Beam,
    Count,
}

/// <summary>One slot's shape, colour and local pose (relative to its fixed parent in the rig).</summary>
public struct PartSpec
{
    public bool on;
    public PuppetKit.Shape shape;
    public Color color;
    public float smooth, emission;
    /// <summary>0 lit, 1 unlit, 2 beam (transparent).</summary>
    public byte matKind;
    public Vector3 pos, euler, scale;
    public bool shadow;
    /// <summary>Kept in the lite LOD (body, head, nose, main hat).</summary>
    public bool lite;
}

/// <summary>Pivot positions for one style (metres, feet frame).</summary>
public struct RigLayout
{
    public float height, headR, hipY, neckY, shoulderY, shoulderX, armLen, legX, legLen, bodyW, bodyD;
    /// <summary>Torso-space anchor for a hugged sack (bottom centre), and a hand's offset from its arm pivot.</summary>
    public Vector3 carry, hand;
    /// <summary>Lite LOD body (a bean down to the feet so the legless lite puppet doesn't float), torso space.</summary>
    public Vector3 litePos, liteScale;
    public PuppetKit.Shape liteShape;
}

/// <summary>A composed rig: the layout plus every slot. Reused (no allocation per compose).</summary>
public sealed class RigSpec
{
    public RigLayout layout;
    public readonly PartSpec[] parts = new PartSpec[(int)PartSlot.Count];
    public Color skin;
    public ResidentStyle style;
    public bool night;
    public ref PartSpec this[PartSlot s] => ref parts[(int)s];
}

/// <summary>
/// WorldLife package C (Docs/WorldLife.md §3.1): the Neighbour Puppets' look in Chuck's grammar (big head ≈ 30% of the
/// height, big nose, googly eyes the SAME size, a round body, stubby limbs), built from primitives. Casts per district:
/// robe dads and curler grans; soccer moms and the HOA lady; monocles and poodles; butlers and billionaires on Segways.
/// <see cref="StyleFor"/> picks a look from LifeHash; <see cref="Compose"/> lays it out as part specs.
/// </summary>
public static class ResidentLook
{
    // ------------------------------------------------------------------ palettes (§3.1 table)

    static readonly Color[][] DistrictPalette =
    {
        new[] { C(0.86f, 0.72f, 0.78f), C(0.70f, 0.79f, 0.86f), C(0.80f, 0.84f, 0.68f), C(0.92f, 0.83f, 0.60f), C(0.71f, 0.62f, 0.80f),
                C(0.66f, 0.42f, 0.38f), C(0.88f, 0.87f, 0.82f), C(0.55f, 0.66f, 0.60f) },        // faded pastels, plaid, terry
        new[] { C(0.36f, 0.78f, 0.76f), C(0.96f, 0.66f, 0.74f), C(0.99f, 0.86f, 0.48f), C(0.86f, 0.78f, 0.64f), C(0.60f, 0.86f, 0.58f),
                C(0.52f, 0.70f, 0.97f), C(0.98f, 0.62f, 0.42f) },                                // bright pastels, teal, beige
        new[] { C(0.95f, 0.92f, 0.82f), C(0.16f, 0.21f, 0.40f), C(0.10f, 0.33f, 0.21f), C(0.55f, 0.13f, 0.17f), C(0.76f, 0.69f, 0.56f),
                C(0.86f, 0.86f, 0.90f) },                                                         // cream, navy, racing green
        new[] { C(0.97f, 0.97f, 0.95f), C(0.95f, 0.78f, 0.25f), C(0.09f, 0.09f, 0.10f), C(0.62f, 0.62f, 0.65f) }, // white, gold, black
    };
    static readonly Color[] HairColors =
    {
        C(0.10f, 0.08f, 0.07f), C(0.26f, 0.17f, 0.11f), C(0.44f, 0.29f, 0.17f), C(0.56f, 0.24f, 0.13f), C(0.88f, 0.74f, 0.44f),
        C(0.62f, 0.62f, 0.62f), C(0.90f, 0.90f, 0.88f), C(0.80f, 0.42f, 0.18f),
    };
    static readonly Color[] Neon = { C(0.55f, 1f, 0.25f), C(1f, 0.45f, 0.15f), C(1f, 0.25f, 0.65f), C(0.25f, 0.9f, 1f) };
    static readonly Color[] Pajama = { C(0.62f, 0.78f, 0.98f), C(0.98f, 0.72f, 0.82f), C(0.70f, 0.92f, 0.78f), C(0.92f, 0.86f, 0.62f) };
    static readonly Color Black = C(0.08f, 0.08f, 0.09f), White = C(0.96f, 0.96f, 0.94f), Gold = C(0.96f, 0.78f, 0.24f),
        Silver = C(0.80f, 0.82f, 0.86f), Khaki = C(0.74f, 0.66f, 0.46f), Denim = C(0.30f, 0.40f, 0.58f), Grey = C(0.45f, 0.46f, 0.5f),
        Brown = C(0.40f, 0.27f, 0.16f), Straw = C(0.93f, 0.82f, 0.50f), Pink = C(1f, 0.62f, 0.76f), CurlerPink = C(1f, 0.55f, 0.72f);

    static Color C(float r, float g, float b) => new Color(r, g, b, 1f);

    static readonly string[][] Names =
    {
        new[] { "Gary", "Linda", "Doug", "Barb", "Phil", "Mrs. Okafor", "Kevin's mom", "Sal", "Deb", "Ron", "Marge", "Steve", "Pam", "Mr. Kowalski", "Toni", "Hank" },
        new[] { "Brenda", "Chad", "Krystal", "Todd", "Ashleigh", "Brent", "Madison", "Darren", "Kayleigh", "Trent", "Jen", "Bryce" },
        new[] { "Mr. Pemberton", "Lady Fairweather", "Reginald", "Cornelia", "Mr. Ashcombe", "Mrs. Thistlewood", "Percival", "Winifred", "Ambrose", "Henrietta" },
        new[] { "Jeeves", "Mr. Moneybags", "Chip", "Thaddeus", "Biff", "Ms. Goldfarb", "Lance", "Tiffani" },
    };

    // ------------------------------------------------------------------ archetype facts (D's routines read these too)

    /// <summary>Walking speed (§7.1): 1.3 waddle, 0.9 for Curler Gran and the Monocle Gent (cane), 2.8 jogging.</summary>
    public static float WalkSpeed(ResidentArchetype a) => a switch
    {
        ResidentArchetype.CurlerGran or ResidentArchetype.MonocleGent => WorldLifeTuning.Walk.Slow,
        ResidentArchetype.PowerWalker => 1.7f,
        ResidentArchetype.Billionaire => 2.2f,   // Segway
        _ => WorldLifeTuning.Walk.Waddle,
    };

    public static bool IsStaff(ResidentArchetype a) => a == ResidentArchetype.Butler || a == ResidentArchetype.Housekeeper;
    public static bool RidesSegway(ResidentArchetype a) => a == ResidentArchetype.Billionaire;
    public static bool WalksDog(ResidentArchetype a) => a == ResidentArchetype.DogPerson || a == ResidentArchetype.PearlLady;

    /// <summary>The district's archetypes and their integer weights (unique characters are placed by <see cref="ResidentCast"/>).</summary>
    public static readonly ResidentArchetype[][] DistrictCast =
    {
        new[] { ResidentArchetype.RobeDad, ResidentArchetype.CurlerGran, ResidentArchetype.Commuter, ResidentArchetype.Jogger, ResidentArchetype.DogPerson, ResidentArchetype.Gardener },
        new[] { ResidentArchetype.SoccerMom, ResidentArchetype.CargoDad, ResidentArchetype.PowerWalker, ResidentArchetype.Commuter, ResidentArchetype.DogPerson },
        new[] { ResidentArchetype.MonocleGent, ResidentArchetype.PearlLady, ResidentArchetype.TennisTwin },
        new[] { ResidentArchetype.Billionaire, ResidentArchetype.Bodyguard },
    };
    public static readonly int[][] DistrictWeights =
    {
        new[] { 4, 3, 4, 2, 3, 2 },
        new[] { 5, 4, 3, 2, 2 },
        new[] { 3, 3, 2 },
        new[] { 3, 1 },
    };

    // ------------------------------------------------------------------ style

    /// <summary>The look of resident <paramref name="hash"/> (LifeHash of the Town seed and the resident id): same every
    /// day, same on every machine.</summary>
    public static ResidentStyle StyleFor(ResidentArchetype a, byte district, ulong hash)
    {
        var d = new PuppetDice(hash);
        var pal = DistrictPalette[Mathf.Min(district, (byte)3)];
        var s = new ResidentStyle
        {
            archetype = a, district = district,
            height = d.Range(1.5f, 1.8f), headScale = d.Range(0.95f, 1.08f),
            build = (PuppetBuild)d.Range(0, 4), skinTone = (byte)d.Range(0, PuppetKit.SkinTones.Length),
            top = d.Pick(pal), bottom = d.Pick(pal), trim = d.Pick(pal), hair = d.Pick(HairColors), shoes = Brown,
            nose = (PuppetNose)d.Range(0, 4), noseSize = d.Range(0.9f, 1.25f),
            hairKind = (PuppetHair)d.Range(1, 10), hat = PuppetHat.None, hatColor = d.Pick(pal),
            outfit = PuppetOutfit.None, outfitColor = d.Pick(pal), prop = PuppetProp.None,
            voicePitch = d.Range(0.85f, 1.5f),
            name = d.Pick(Names[Mathf.Min(district, (byte)3)]),
        };
        if (s.bottom == s.top) s.bottom = Grey;
        switch (a)
        {
            case ResidentArchetype.RobeDad:
                s.build = d.Value < 0.6f ? PuppetBuild.Ball : PuppetBuild.Bean;
                s.outfit = PuppetOutfit.Robe; s.outfitColor = d.Value < 0.5f ? White : d.Pick(pal);
                s.slippers = true; s.prop = PuppetProp.Mug; s.hairKind = d.Value < 0.5f ? PuppetHair.CombOver : PuppetHair.Bald;
                s.bottom = Grey; s.voicePitch = d.Range(0.75f, 0.95f);
                break;
            case ResidentArchetype.CurlerGran:
                s.height = d.Range(1.45f, 1.56f); s.build = PuppetBuild.Pear;
                s.outfit = PuppetOutfit.Cardigan; s.hairKind = PuppetHair.Curlers; s.hair = HairColors[d.Value < 0.5f ? 5 : 6];
                s.slippers = d.Value < 0.4f; s.trim = s.top; s.voicePitch = d.Range(1.3f, 1.6f);
                if (d.Value < 0.6f) s.accent2 = PuppetAccent.Glasses;
                break;
            case ResidentArchetype.Commuter:
                s.build = d.Value < 0.5f ? PuppetBuild.Stick : PuppetBuild.Bean;
                s.top = White; s.outfit = PuppetOutfit.Tie; s.outfitColor = d.Pick(pal); s.bottom = d.Value < 0.5f ? Grey : C(0.18f, 0.2f, 0.3f);
                s.prop = PuppetProp.Briefcase; s.hairKind = d.Value < 0.6f ? PuppetHair.Slick : PuppetHair.Short; s.shoes = Black;
                break;
            case ResidentArchetype.Jogger:
                s.build = PuppetBuild.Stick; s.top = d.Pick(Neon); s.bottom = Black; s.bareLegs = true;
                s.hat = PuppetHat.Sweatband; s.hatColor = d.Pick(Neon); s.shoes = White; s.hairKind = d.Value < 0.5f ? PuppetHair.Ponytail : PuppetHair.Short;
                break;
            case ResidentArchetype.DogPerson:
                s.prop = PuppetProp.Leash; s.hat = d.Value < 0.5f ? PuppetHat.Beanie : PuppetHat.Cap; s.bottom = Denim;
                break;
            case ResidentArchetype.Gardener:
                s.hat = PuppetHat.StrawHat; s.hatColor = Straw; s.outfit = PuppetOutfit.Apron; s.outfitColor = C(0.35f, 0.55f, 0.3f);
                s.prop = PuppetProp.Trowel; s.bottom = Khaki;
                break;
            case ResidentArchetype.SoccerMom:
                s.hat = PuppetHat.Visor; s.hatColor = White; s.outfit = PuppetOutfit.Vest; s.prop = PuppetProp.Tumbler;
                s.hairKind = d.Value < 0.6f ? PuppetHair.Ponytail : PuppetHair.Short; s.hair = HairColors[d.Value < 0.6f ? 4 : 2];
                s.voicePitch = d.Range(1.2f, 1.45f);
                break;
            case ResidentArchetype.CargoDad:
                s.build = d.Value < 0.6f ? PuppetBuild.Ball : PuppetBuild.Bean; s.bottom = Khaki; s.bareLegs = true;
                s.shoes = White; s.hat = PuppetHat.Cap; s.hairKind = PuppetHair.Short; s.voicePitch = d.Range(0.75f, 0.95f);
                break;
            case ResidentArchetype.PowerWalker:
                s.build = PuppetBuild.Stick; s.outfit = PuppetOutfit.Tracksuit; s.outfitColor = d.Pick(Neon); s.top = s.outfitColor;
                s.bottom = s.outfitColor; s.prop = PuppetProp.Weights; s.hat = PuppetHat.Sweatband; s.hatColor = White; s.shoes = White;
                break;
            case ResidentArchetype.HoaLady:
                s.hairKind = PuppetHair.Beehive; s.hair = HairColors[d.Value < 0.5f ? 3 : 4]; s.outfit = PuppetOutfit.Cardigan;
                s.outfitColor = C(0.62f, 0.30f, 0.52f); s.prop = PuppetProp.Clipboard; s.accent2 = PuppetAccent.Glasses;
                s.name = "The HOA Lady"; s.voicePitch = 1.45f; s.nose = PuppetNose.Long;
                break;
            case ResidentArchetype.MonocleGent:
                s.height = d.Range(1.62f, 1.78f); s.build = PuppetBuild.Ball; s.hat = PuppetHat.TopHat; s.hatColor = Black;
                s.outfit = PuppetOutfit.Suit; s.outfitColor = d.Value < 0.5f ? C(0.16f, 0.21f, 0.40f) : C(0.10f, 0.33f, 0.21f);
                s.prop = PuppetProp.Cane; s.accent = PuppetAccent.Monocle; s.accent2 = PuppetAccent.Mustache; s.hairKind = PuppetHair.Bald;
                s.shoes = Black; s.voicePitch = d.Range(0.7f, 0.85f);
                break;
            case ResidentArchetype.PearlLady:
                s.hat = PuppetHat.SunHat; s.hatColor = d.Pick(pal); s.accent = PuppetAccent.Pearls; s.top = d.Pick(pal);
                s.hairKind = PuppetHair.Bun; s.prop = PuppetProp.Leash; s.voicePitch = d.Range(1.3f, 1.5f);
                break;
            case ResidentArchetype.TennisTwin:
                s.build = PuppetBuild.Stick; s.top = White; s.bottom = White; s.bareLegs = true; s.shoes = White;
                s.hat = PuppetHat.Visor; s.hatColor = d.Value < 0.5f ? C(0.16f, 0.21f, 0.40f) : C(0.10f, 0.33f, 0.21f); s.prop = PuppetProp.Racket;
                break;
            case ResidentArchetype.Pemberton:
                s.height = 1.68f; s.build = PuppetBuild.Bean; s.hat = PuppetHat.Bowler; s.hatColor = Black; s.outfit = PuppetOutfit.Suit;
                s.outfitColor = C(0.16f, 0.21f, 0.40f); s.prop = PuppetProp.Clipboard; s.accent = PuppetAccent.Monocle;
                s.accent2 = PuppetAccent.PencilMustache; s.hairKind = PuppetHair.Slick; s.hair = HairColors[5]; s.shoes = Black;
                s.name = "Mr. Pemberton"; s.nose = PuppetNose.Long; s.voicePitch = 0.8f;
                break;
            case ResidentArchetype.Housekeeper:
                s.outfit = PuppetOutfit.Apron; s.outfitColor = White; s.top = C(0.2f, 0.22f, 0.3f); s.hairKind = PuppetHair.Bun;
                s.prop = PuppetProp.Duster; s.shoes = Black;
                break;
            case ResidentArchetype.Butler:
                s.height = d.Range(1.78f, 1.86f); s.build = PuppetBuild.Bean; s.outfit = PuppetOutfit.Tux; s.outfitColor = Black;
                s.top = White; s.bottom = Black; s.shoes = Black; s.hairKind = PuppetHair.Slick; s.hair = Black;
                s.accent = PuppetAccent.BowTie; s.accent2 = PuppetAccent.Mustache; s.prop = PuppetProp.Platter; s.skinTone = (byte)d.Range(0, 5);
                s.name = d.Value < 0.5f ? "Jeeves" : "Alfred"; s.voicePitch = 0.85f; s.nose = PuppetNose.Long;
                break;
            case ResidentArchetype.Billionaire:
                s.build = PuppetBuild.Ball; s.outfit = PuppetOutfit.GoldRobe; s.outfitColor = Gold; s.accent = PuppetAccent.Sunglasses;
                s.hairKind = PuppetHair.Slick; s.prop = PuppetProp.Cigar; s.slippers = true; s.shoes = Gold;
                break;
            case ResidentArchetype.Bodyguard:
                s.height = d.Range(1.8f, 1.86f); s.build = PuppetBuild.Ball; s.outfit = PuppetOutfit.Suit; s.outfitColor = Black;
                s.top = White; s.bottom = Black; s.shoes = Black; s.accent = PuppetAccent.Sunglasses; s.accent2 = PuppetAccent.Earpiece;
                s.hairKind = PuppetHair.Buzz; s.voicePitch = 0.65f;
                break;
        }
        return s;
    }

    // ------------------------------------------------------------------ compose

    /// <summary>
    /// Lay a style out as the universal rig's parts. <paramref name="night"/>: pajamas, slippers and a flashlight (§7.2);
    /// <paramref name="carryProp"/> false hides the hand prop (both hands on a bag). Fills <paramref name="o"/> in place.
    /// </summary>
    public static void Compose(in ResidentStyle st, bool night, bool carryProp, RigSpec o)
    {
        o.style = st;
        o.night = night;
        for (int i = 0; i < o.parts.Length; i++) o.parts[i] = default;
        Color skin = st.Skin;
        o.skin = skin;

        // ---- layout (feet frame; +Z forward)
        float H = Mathf.Clamp(st.height, 1.4f, 1.9f);
        float R = 0.162f * H * Mathf.Clamp(st.headScale, 0.9f, 1.1f);   // big head: ~1/3 of the height with the hair
        float headCY = H - R;
        float neckY = headCY - 0.72f * R;
        float hipY = 0.27f * H;
        float bodyBottom = hipY - 0.05f * H, bodyTop = neckY + 0.14f * R;
        float span = bodyTop - bodyBottom;
        float w, dpt;
        switch (st.build)
        {
            case PuppetBuild.Ball: w = 0.37f * H; dpt = 0.33f * H; break;
            case PuppetBuild.Pear: w = 0.35f * H; dpt = 0.29f * H; break;
            case PuppetBuild.Stick: w = 0.24f * H; dpt = 0.21f * H; break;
            default: w = 0.30f * H; dpt = 0.25f * H; break;
        }
        ref var L = ref o.layout;
        L.height = H; L.headR = R; L.hipY = hipY; L.neckY = neckY; L.bodyW = w; L.bodyD = dpt;
        L.shoulderY = bodyTop - 0.11f * H; L.shoulderX = w * 0.5f - 0.015f;
        L.armLen = 0.26f * H; L.legLen = hipY; L.legX = Mathf.Min(w * 0.24f, 0.12f);
        L.hand = new Vector3(0f, -L.armLen, 0f);
        L.carry = new Vector3(0f, 0.30f * H - hipY, dpt * 0.5f + 0.17f);

        float midY = (bodyBottom + bodyTop) * 0.5f - hipY;   // torso space
        Color top = night ? Pajama[st.skinTone % Pajama.Length] : st.top;
        Color bottom = night ? top * 0.92f : st.bottom;
        bottom.a = 1f;

        // ---- body
        bool round = st.build == PuppetBuild.Ball || st.build == PuppetBuild.Pear;
        Set(o, PartSlot.Body, round ? PuppetKit.Shape.Sphere : PuppetKit.Shape.Capsule, top,
            new Vector3(0f, midY - (st.build == PuppetBuild.Pear ? 0.03f : 0f), 0f), Vector3.zero,
            round ? new Vector3(w, span, dpt) : new Vector3(w, span * 0.5f, dpt), shadow: true, lite: true);
        L.liteShape = PuppetKit.Shape.Capsule;
        L.litePos = new Vector3(0f, (bodyTop - hipY - hipY) * 0.5f, 0f);       // from the feet (−hipY) to the body top
        L.liteScale = new Vector3(w * 0.92f, (bodyTop) * 0.5f, dpt * 0.92f);

        // ---- outfit layer
        PuppetOutfit outfit = night ? (st.outfit == PuppetOutfit.Robe || st.outfit == PuppetOutfit.GoldRobe ? st.outfit : PuppetOutfit.Pajamas) : st.outfit;
        Color oc = st.outfitColor;
        switch (outfit)
        {
            case PuppetOutfit.Robe:
            case PuppetOutfit.GoldRobe:
                Set(o, PartSlot.Outfit, PuppetKit.Shape.Capsule, oc, new Vector3(0f, midY - 0.06f * H, 0f), Vector3.zero,
                    new Vector3(w * 1.07f, span * 0.56f, dpt * 1.07f), smooth: outfit == PuppetOutfit.GoldRobe ? 0.7f : 0.1f,
                    emission: outfit == PuppetOutfit.GoldRobe ? 0.15f : 0f, shadow: true);
                break;
            case PuppetOutfit.Cardigan:
                Set(o, PartSlot.Outfit, round ? PuppetKit.Shape.Sphere : PuppetKit.Shape.Capsule, oc, new Vector3(0f, midY + 0.03f * H, -0.012f), Vector3.zero,
                    round ? new Vector3(w * 1.04f, span * 0.82f, dpt * 0.98f) : new Vector3(w * 1.04f, span * 0.41f, dpt * 0.98f));
                break;
            case PuppetOutfit.Vest:
                Set(o, PartSlot.Outfit, round ? PuppetKit.Shape.Sphere : PuppetKit.Shape.Capsule, oc, new Vector3(0f, midY + 0.04f * H, -0.01f), Vector3.zero,
                    round ? new Vector3(w * 1.05f, span * 0.7f, dpt * 1.02f) : new Vector3(w * 1.05f, span * 0.35f, dpt * 1.02f), smooth: 0.45f);
                break;
            case PuppetOutfit.Tie:
                Set(o, PartSlot.Outfit, PuppetKit.Shape.Cube, oc, new Vector3(0f, midY + span * 0.12f, dpt * 0.51f), new Vector3(-8f, 0f, 0f),
                    new Vector3(0.07f * H * 0.6f, span * 0.5f, 0.02f));
                break;
            case PuppetOutfit.Apron:
                Set(o, PartSlot.Outfit, PuppetKit.Shape.Cube, oc, new Vector3(0f, midY - span * 0.1f, dpt * 0.5f + 0.01f), new Vector3(-4f, 0f, 0f),
                    new Vector3(w * 0.62f, span * 0.6f, 0.03f));
                break;
            case PuppetOutfit.Suit:
            case PuppetOutfit.Tux:
                Set(o, PartSlot.Outfit, round ? PuppetKit.Shape.Sphere : PuppetKit.Shape.Capsule, oc, new Vector3(0f, midY, -0.015f), Vector3.zero,
                    round ? new Vector3(w * 1.03f, span * 1.0f, dpt * 0.96f) : new Vector3(w * 1.03f, span * 0.5f, dpt * 0.96f), smooth: 0.4f, shadow: true);
                break;
            case PuppetOutfit.Tracksuit:
                Set(o, PartSlot.Outfit, PuppetKit.Shape.Cube, White, new Vector3(0f, midY, dpt * 0.47f), Vector3.zero,
                    new Vector3(0.025f, span * 0.8f, 0.02f));   // the zip stripe
                break;
            case PuppetOutfit.Pajamas:
                Set(o, PartSlot.Outfit, PuppetKit.Shape.Cube, Color.Lerp(top, White, 0.55f), new Vector3(0f, midY + span * 0.32f, dpt * 0.42f),
                    new Vector3(-20f, 0f, 0f), new Vector3(w * 0.5f, span * 0.12f, 0.03f));   // collar
                break;
        }

        // ---- head
        Vector3 skullC = new Vector3(0f, 0.72f * R, 0f);
        Set(o, PartSlot.Skull, PuppetKit.Shape.Sphere, skin, skullC, Vector3.zero, Vector3.one * (2f * R), shadow: true, lite: true);
        float ns = st.noseSize * R * 1.3f;   // a BIG nose (Chuck's grammar)
        Vector3 noseP = skullC + new Vector3(0f, -0.1f * R, 0.96f * R);
        switch (st.nose)
        {
            case PuppetNose.Long:
                Set(o, PartSlot.Nose, PuppetKit.Shape.Capsule, skin * 0.97f, noseP + new Vector3(0f, 0f, 0.12f * R), new Vector3(80f, 0f, 0f),
                    new Vector3(0.32f * ns, 0.32f * ns, 0.32f * ns), lite: true);
                break;
            case PuppetNose.Potato:
                Set(o, PartSlot.Nose, PuppetKit.Shape.Sphere, skin * 0.96f, noseP + new Vector3(0f, -0.04f * R, 0.02f * R), Vector3.zero,
                    new Vector3(0.66f * ns, 0.5f * ns, 0.55f * ns), lite: true);
                break;
            case PuppetNose.Hook:
                Set(o, PartSlot.Nose, PuppetKit.Shape.Capsule, skin * 0.97f, noseP + new Vector3(0f, -0.06f * R, 0.1f * R), new Vector3(125f, 0f, 0f),
                    new Vector3(0.3f * ns, 0.28f * ns, 0.3f * ns), lite: true);
                break;
            default:
                Set(o, PartSlot.Nose, PuppetKit.Shape.Sphere, skin * 0.97f, noseP, Vector3.zero, Vector3.one * (0.52f * ns), lite: true);
                break;
        }
        o[PartSlot.Nose].color.a = 1f;
        Set(o, PartSlot.Mouth, PuppetKit.Shape.Cube, PuppetKit.MouthDark, skullC + new Vector3(0f, -0.46f * R, 0.86f * R), new Vector3(-28f, 0f, 0f),
            new Vector3(0.38f * R, 0.06f * R, 0.08f * R));

        // Googly eyes: both the SAME size (mismatched eyes are Chuck's signature).
        float e = 0.5f * R;
        for (int s = -1; s <= 1; s += 2)
        {
            Vector3 dir = new Vector3(0.36f * s, 0.2f, 0.91f).normalized;
            var eyeSlot = s < 0 ? PartSlot.EyeL : PartSlot.EyeR;
            Set(o, eyeSlot, PuppetKit.Shape.Sphere, PuppetKit.EyeWhite, skullC + dir * (R * 0.93f), new Vector3(-dir.y * 50f, dir.x * 40f, 0f),
                new Vector3(e, e, e * 0.55f), smooth: 0.9f);
            o[eyeSlot].matKind = 0;
            Set(o, s < 0 ? PartSlot.PupilL : PartSlot.PupilR, PuppetKit.Shape.Sphere, PuppetKit.Pupil, new Vector3(0f, -0.1f, 0.34f), Vector3.zero,
                new Vector3(0.5f, 0.5f, 0.45f), smooth: 0.95f);
            Set(o, s < 0 ? PartSlot.BrowL : PartSlot.BrowR, PuppetKit.Shape.Cube, st.hair * 0.8f, skullC + new Vector3(0.36f * s * R, 0.5f * R, 0.86f * R),
                new Vector3(-20f, 0f, -12f * s), new Vector3(0.36f * R, 0.07f * R, 0.08f * R));
            o[s < 0 ? PartSlot.BrowL : PartSlot.BrowR].on = false;   // brows show when angry / startled
        }

        // ---- hair and hats (Hat0..2 under the hat pivot at the skull top; y up from there)
        ComposeHead(st, night, R, o);
        ComposeAccents(st, R, skullC, o);

        // ---- arms (pivot at the shoulder, hanging down)
        Color sleeve = outfit == PuppetOutfit.Robe || outfit == PuppetOutfit.GoldRobe || outfit == PuppetOutfit.Suit || outfit == PuppetOutfit.Tux
            || outfit == PuppetOutfit.Cardigan || outfit == PuppetOutfit.Tracksuit ? oc : top;
        if (st.archetype == ResidentArchetype.Jogger || st.archetype == ResidentArchetype.TennisTwin || st.archetype == ResidentArchetype.CargoDad)
            sleeve = skin;   // t-shirts: bare arms
        float armR = 0.045f * H, mitt = 0.09f * H;
        for (int s = -1; s <= 1; s += 2)
        {
            Set(o, s < 0 ? PartSlot.ArmL : PartSlot.ArmR, PuppetKit.Shape.Capsule, sleeve, new Vector3(0f, -L.armLen * 0.48f, 0f), Vector3.zero,
                new Vector3(2f * armR, L.armLen * 0.5f, 2f * armR));
            Color glove = st.archetype == ResidentArchetype.Butler ? White : skin;
            Set(o, s < 0 ? PartSlot.MittL : PartSlot.MittR, PuppetKit.Shape.Sphere, glove, new Vector3(0f, -L.armLen, 0f), Vector3.zero,
                new Vector3(mitt, mitt * 1.05f, mitt));
        }

        // ---- legs (pivot at the hip)
        float legR = 0.052f * H;
        Color legC = st.bareLegs && !night ? skin : bottom;
        bool slippers = st.slippers || night;
        Color shoeC = night ? Color.Lerp(top, White, 0.4f) : st.shoes;
        for (int s = -1; s <= 1; s += 2)
        {
            Set(o, s < 0 ? PartSlot.LegL : PartSlot.LegR, PuppetKit.Shape.Capsule, legC, new Vector3(0f, -hipY * 0.5f, 0f), Vector3.zero,
                new Vector3(2f * legR, hipY * 0.5f + legR * 0.3f, 2f * legR), shadow: true);
            Vector3 shoeS = slippers ? new Vector3(0.12f * H, 0.055f * H, 0.17f * H) : new Vector3(0.085f * H, 0.06f * H, 0.135f * H);
            Set(o, s < 0 ? PartSlot.ShoeL : PartSlot.ShoeR, PuppetKit.Shape.Sphere, shoeC, new Vector3(0f, -hipY + shoeS.y * 0.45f, 0.035f * H), Vector3.zero,
                shoeS, smooth: slippers ? 0.05f : 0.5f);
        }

        // ---- props (right hand; PropL left hand), night flashlight
        PuppetProp prop = night ? PuppetProp.Flashlight : st.prop;
        if (carryProp) ComposeProp(st, prop, H, o);
        if (night)
        {
            Set(o, PartSlot.Beam, PuppetKit.Shape.Cone, new Color(1f, 0.95f, 0.6f, 0.1f),   // linear-space blend: 0.1 reads as a soft glow
                new Vector3(0.12f, L.shoulderY - hipY - 0.05f * H, dpt * 0.5f + 0.05f), new Vector3(28f, 0f, 0f), new Vector3(0.55f, 0.55f, 3.2f));
            o[PartSlot.Beam].matKind = 2;
        }
    }

    static void ComposeHead(in ResidentStyle st, bool night, float R, RigSpec o)
    {
        Color hc = st.hair;
        PuppetHair hair = st.hairKind;
        PuppetHat hat = night ? (st.hairKind == PuppetHair.Curlers ? PuppetHat.None : (st.skinTone % 3 == 0 ? PuppetHat.Nightcap : PuppetHat.None)) : st.hat;
        // Hair goes in Hat0 unless a hat covers it all.
        bool hatCovers = hat == PuppetHat.TopHat || hat == PuppetHat.Bowler || hat == PuppetHat.Beanie || hat == PuppetHat.Nightcap;
        switch (hair)
        {
            case PuppetHair.Short:
                Set(o, PartSlot.Hat0, PuppetKit.Shape.Dome, hc, new Vector3(0f, -0.75f * R, -0.08f * R), new Vector3(-12f, 0f, 0f), new Vector3(2.08f * R, 1.15f * R, 2.08f * R), lite: true);
                break;
            case PuppetHair.Buzz:
                Set(o, PartSlot.Hat0, PuppetKit.Shape.Dome, hc, new Vector3(0f, -0.62f * R, -0.04f * R), Vector3.zero, new Vector3(2.03f * R, 0.95f * R, 2.03f * R), lite: true);
                break;
            case PuppetHair.CombOver:
                Set(o, PartSlot.Hat0, PuppetKit.Shape.Sphere, hc, new Vector3(0.18f * R, -0.08f * R, 0f), new Vector3(0f, 0f, -14f), new Vector3(1.4f * R, 0.22f * R, 1.3f * R), lite: true);
                break;
            case PuppetHair.Bun:
                Set(o, PartSlot.Hat0, PuppetKit.Shape.Dome, hc, new Vector3(0f, -0.7f * R, -0.08f * R), new Vector3(-14f, 0f, 0f), new Vector3(2.06f * R, 1.12f * R, 2.06f * R), lite: true);
                Set(o, PartSlot.Hat1, PuppetKit.Shape.Sphere, hc, new Vector3(0f, -0.05f * R, -0.62f * R), Vector3.zero, Vector3.one * (0.8f * R));
                break;
            case PuppetHair.Curlers:
                Set(o, PartSlot.Hat0, PuppetKit.Shape.Dome, hc, new Vector3(0f, -0.68f * R, -0.06f * R), Vector3.zero, new Vector3(2.08f * R, 1.15f * R, 2.08f * R), lite: true);
                Set(o, PartSlot.Hat1, PuppetKit.Shape.Cylinder, CurlerPink, new Vector3(-0.35f * R, 0.12f * R, 0.15f * R), new Vector3(0f, 0f, 90f), new Vector3(0.32f * R, 0.42f * R, 0.32f * R));
                Set(o, PartSlot.Hat2, PuppetKit.Shape.Cylinder, CurlerPink, new Vector3(0.35f * R, 0.12f * R, 0.15f * R), new Vector3(0f, 0f, 90f), new Vector3(0.32f * R, 0.42f * R, 0.32f * R));
                break;
            case PuppetHair.Beehive:
                Set(o, PartSlot.Hat0, PuppetKit.Shape.Sphere, hc, new Vector3(0f, 0.45f * R, -0.1f * R), Vector3.zero, new Vector3(1.75f * R, 2.5f * R, 1.75f * R), lite: true);
                break;
            case PuppetHair.Afro:
                Set(o, PartSlot.Hat0, PuppetKit.Shape.Sphere, hc, new Vector3(0f, -0.3f * R, -0.15f * R), Vector3.zero, Vector3.one * (2.6f * R), lite: true);
                break;
            case PuppetHair.Slick:
                Set(o, PartSlot.Hat0, PuppetKit.Shape.Dome, hc, new Vector3(0f, -0.62f * R, -0.1f * R), new Vector3(-18f, 0f, 0f), new Vector3(2.06f * R, 1.0f * R, 2.1f * R), smooth: 0.85f, lite: true);
                break;
            case PuppetHair.Ponytail:
                Set(o, PartSlot.Hat0, PuppetKit.Shape.Dome, hc, new Vector3(0f, -0.7f * R, -0.06f * R), new Vector3(-10f, 0f, 0f), new Vector3(2.08f * R, 1.12f * R, 2.08f * R), lite: true);
                Set(o, PartSlot.Hat1, PuppetKit.Shape.Capsule, hc, new Vector3(0f, -0.55f * R, -0.95f * R), new Vector3(30f, 0f, 0f), new Vector3(0.38f * R, 0.55f * R, 0.38f * R));
                break;
        }
        if (hatCovers) o[PartSlot.Hat0].on = false;

        Color hatC = st.hatColor;
        switch (hat)
        {
            case PuppetHat.Visor:
                Set(o, PartSlot.Hat2, PuppetKit.Shape.Cylinder, hatC, new Vector3(0f, -0.45f * R, 0.62f * R), new Vector3(-8f, 0f, 0f), new Vector3(1.3f * R, 0.03f * R, 0.9f * R), lite: true);
                Set(o, PartSlot.Hat1, PuppetKit.Shape.Cylinder, hatC, new Vector3(0f, -0.4f * R, 0f), new Vector3(-8f, 0f, 0f), new Vector3(2.04f * R, 0.12f * R, 2.04f * R));
                break;
            case PuppetHat.TopHat:
                Set(o, PartSlot.Hat0, PuppetKit.Shape.Cylinder, hatC, new Vector3(0f, 0.55f * R, -0.05f * R), new Vector3(-6f, 0f, 0f), new Vector3(1.35f * R, 0.75f * R, 1.35f * R), smooth: 0.5f, lite: true);
                Set(o, PartSlot.Hat1, PuppetKit.Shape.Cylinder, hatC, new Vector3(0f, -0.15f * R, -0.03f * R), new Vector3(-6f, 0f, 0f), new Vector3(2.2f * R, 0.04f * R, 2.2f * R), smooth: 0.5f);
                Set(o, PartSlot.Hat2, PuppetKit.Shape.Cylinder, C(0.55f, 0.12f, 0.15f), new Vector3(0f, 0.02f * R, -0.04f * R), new Vector3(-6f, 0f, 0f), new Vector3(1.38f * R, 0.12f * R, 1.38f * R));
                break;
            case PuppetHat.Bowler:
                Set(o, PartSlot.Hat0, PuppetKit.Shape.Dome, hatC, new Vector3(0f, -0.3f * R, -0.04f * R), new Vector3(-6f, 0f, 0f), new Vector3(1.65f * R, 1.3f * R, 1.65f * R), smooth: 0.5f, lite: true);
                Set(o, PartSlot.Hat1, PuppetKit.Shape.Cylinder, hatC, new Vector3(0f, -0.3f * R, -0.04f * R), new Vector3(-6f, 0f, 0f), new Vector3(2.3f * R, 0.04f * R, 2.3f * R), smooth: 0.5f);
                break;
            case PuppetHat.Sweatband:
                Set(o, PartSlot.Hat2, PuppetKit.Shape.Cylinder, hatC, new Vector3(0f, -0.5f * R, 0f), new Vector3(-8f, 0f, 0f), new Vector3(2.08f * R, 0.14f * R, 2.08f * R), lite: hair == PuppetHair.Bald);
                break;
            case PuppetHat.StrawHat:
            case PuppetHat.SunHat:
                Set(o, PartSlot.Hat1, PuppetKit.Shape.Cylinder, hatC, new Vector3(0f, -0.3f * R, 0f), new Vector3(-5f, 0f, 0f), new Vector3(3.6f * R, 0.04f * R, 3.6f * R), lite: true);
                Set(o, PartSlot.Hat2, PuppetKit.Shape.Dome, hatC, new Vector3(0f, -0.32f * R, 0f), new Vector3(-5f, 0f, 0f), new Vector3(1.75f * R, 1.1f * R, 1.75f * R));
                o[PartSlot.Hat0].lite = false;
                break;
            case PuppetHat.Cap:
                Set(o, PartSlot.Hat1, PuppetKit.Shape.Dome, hatC, new Vector3(0f, -0.42f * R, -0.02f * R), new Vector3(-8f, 0f, 0f), new Vector3(2.08f * R, 1.25f * R, 2.08f * R), lite: true);
                Set(o, PartSlot.Hat2, PuppetKit.Shape.Cylinder, hatC, new Vector3(0f, -0.4f * R, 0.75f * R), new Vector3(-6f, 0f, 0f), new Vector3(1.3f * R, 0.03f * R, 1.0f * R));
                o[PartSlot.Hat0].lite = false;
                break;
            case PuppetHat.Beanie:
                Set(o, PartSlot.Hat1, PuppetKit.Shape.Dome, hatC, new Vector3(0f, -0.52f * R, -0.03f * R), new Vector3(-8f, 0f, 0f), new Vector3(2.1f * R, 1.5f * R, 2.1f * R), lite: true);
                Set(o, PartSlot.Hat2, PuppetKit.Shape.Sphere, Color.Lerp(hatC, White, 0.6f), new Vector3(0f, 0.25f * R, -0.1f * R), Vector3.zero, Vector3.one * (0.42f * R));
                break;
            case PuppetHat.Nightcap:
                Set(o, PartSlot.Hat1, PuppetKit.Shape.Dome, C(0.5f, 0.6f, 0.95f), new Vector3(0f, -0.5f * R, -0.03f * R), new Vector3(-10f, 0f, 0f), new Vector3(2.08f * R, 1.4f * R, 2.08f * R), lite: true);
                Set(o, PartSlot.Hat2, PuppetKit.Shape.Sphere, White, new Vector3(0.45f * R, 0.1f * R, -0.55f * R), Vector3.zero, Vector3.one * (0.4f * R));
                break;
        }
    }

    static void ComposeAccents(in ResidentStyle st, float R, Vector3 skullC, RigSpec o)
    {
        for (int k = 0; k < 2; k++)
        {
            var slot = k == 0 ? PartSlot.Accent : PartSlot.Accent2;
            switch (k == 0 ? st.accent : st.accent2)
            {
                case PuppetAccent.Monocle:
                    Set(o, slot, PuppetKit.Shape.Cylinder, Gold, skullC + new Vector3(0.36f * R, 0.2f * R, 0.95f * R), new Vector3(70f, 0f, 0f), new Vector3(0.58f * R, 0.02f * R, 0.58f * R), smooth: 0.9f);
                    break;
                case PuppetAccent.Pearls:
                    Set(o, slot, PuppetKit.Shape.Cylinder, White, skullC + new Vector3(0f, -1.0f * R, -0.02f * R), Vector3.zero, new Vector3(1.6f * R, 0.06f * R, 1.45f * R), smooth: 0.95f);
                    break;
                case PuppetAccent.Earpiece:
                    Set(o, slot, PuppetKit.Shape.Sphere, Black, skullC + new Vector3(1.0f * R, -0.05f * R, 0f), Vector3.zero, Vector3.one * (0.2f * R));
                    break;
                case PuppetAccent.Sunglasses:
                    Set(o, slot, PuppetKit.Shape.Cube, Black, skullC + new Vector3(0f, 0.2f * R, 0.95f * R), new Vector3(-8f, 0f, 0f), new Vector3(1.3f * R, 0.32f * R, 0.12f * R), smooth: 0.95f);
                    break;
                case PuppetAccent.Glasses:
                    Set(o, slot, PuppetKit.Shape.Cube, C(0.2f, 0.15f, 0.12f), skullC + new Vector3(0f, 0.3f * R, 0.97f * R), new Vector3(-10f, 0f, 0f), new Vector3(1.25f * R, 0.06f * R, 0.06f * R));
                    break;
                case PuppetAccent.BowTie:
                    Set(o, slot, PuppetKit.Shape.Cube, Black, skullC + new Vector3(0f, -0.98f * R, 0.62f * R), new Vector3(0f, 0f, 0f), new Vector3(0.6f * R, 0.2f * R, 0.12f * R));
                    break;
                case PuppetAccent.Mustache:
                    Set(o, slot, PuppetKit.Shape.Sphere, st.hair * 0.85f, skullC + new Vector3(0f, -0.3f * R, 0.95f * R), Vector3.zero, new Vector3(0.95f * R, 0.2f * R, 0.25f * R));
                    o[slot].color.a = 1f;
                    break;
                case PuppetAccent.PencilMustache:
                    Set(o, slot, PuppetKit.Shape.Cube, st.hair * 0.7f, skullC + new Vector3(0f, -0.3f * R, 0.97f * R), new Vector3(-20f, 0f, 0f), new Vector3(0.6f * R, 0.05f * R, 0.06f * R));
                    o[slot].color.a = 1f;
                    break;
            }
        }
    }

    static void ComposeProp(in ResidentStyle st, PuppetProp prop, float H, RigSpec o)
    {
        // Hand space: the mitt centre is the origin; the arm hangs along −Y, so +Y is "up the arm" and +Z is in front.
        switch (prop)
        {
            case PuppetProp.Mug:
                Set(o, PartSlot.Prop, PuppetKit.Shape.Cylinder, st.trim, new Vector3(0f, 0.02f, 0.07f), Vector3.zero, new Vector3(0.1f, 0.06f, 0.1f));
                break;
            case PuppetProp.Newspaper:
                Set(o, PartSlot.Prop, PuppetKit.Shape.Cube, C(0.92f, 0.9f, 0.84f), new Vector3(0f, 0f, 0.06f), new Vector3(0f, 0f, 70f), new Vector3(0.28f, 0.04f, 0.2f));
                break;
            case PuppetProp.Briefcase:
                Set(o, PartSlot.Prop, PuppetKit.Shape.Cube, Brown, new Vector3(0f, -0.17f, 0f), Vector3.zero, new Vector3(0.09f, 0.26f, 0.36f), smooth: 0.55f);
                break;
            case PuppetProp.Leash:
                Set(o, PartSlot.Prop, PuppetKit.Shape.Cylinder, C(0.85f, 0.2f, 0.2f), new Vector3(0f, 0f, 0.04f), new Vector3(90f, 0f, 0f), new Vector3(0.05f, 0.06f, 0.05f));
                break;
            case PuppetProp.Clipboard:
                Set(o, PartSlot.Prop, PuppetKit.Shape.Cube, C(0.6f, 0.45f, 0.28f), new Vector3(0f, 0.05f, 0.08f), new Vector3(60f, 0f, 0f), new Vector3(0.24f, 0.32f, 0.02f));
                Set(o, PartSlot.Prop2, PuppetKit.Shape.Cube, White, new Vector3(0f, 0.04f, 0.095f), new Vector3(60f, 0f, 0f), new Vector3(0.2f, 0.26f, 0.01f));
                break;
            case PuppetProp.Cane:
                Set(o, PartSlot.Prop, PuppetKit.Shape.Cylinder, Black, new Vector3(0f, -0.33f, 0.06f), Vector3.zero, new Vector3(0.035f, 0.35f, 0.035f), smooth: 0.7f);
                Set(o, PartSlot.Prop2, PuppetKit.Shape.Sphere, Gold, new Vector3(0f, 0.04f, 0.06f), Vector3.zero, Vector3.one * 0.07f, smooth: 0.9f);
                break;
            case PuppetProp.Tumbler:
                Set(o, PartSlot.Prop, PuppetKit.Shape.Cylinder, st.trim, new Vector3(0f, 0.1f, 0.08f), Vector3.zero, new Vector3(0.12f, 0.17f, 0.12f), smooth: 0.6f);
                Set(o, PartSlot.Prop2, PuppetKit.Shape.Cylinder, White, new Vector3(0.02f, 0.33f, 0.08f), new Vector3(0f, 0f, 10f), new Vector3(0.02f, 0.08f, 0.02f));
                break;
            case PuppetProp.Weights:
                Set(o, PartSlot.Prop, PuppetKit.Shape.Capsule, C(0.85f, 0.2f, 0.6f), new Vector3(0f, 0f, 0.03f), new Vector3(0f, 0f, 90f), new Vector3(0.09f, 0.12f, 0.09f));
                Set(o, PartSlot.PropL, PuppetKit.Shape.Capsule, C(0.85f, 0.2f, 0.6f), new Vector3(0f, 0f, 0.03f), new Vector3(0f, 0f, 90f), new Vector3(0.09f, 0.12f, 0.09f));
                break;
            case PuppetProp.Platter:
                // Held level: the butler's right arm is always PlatterTilt forward, and the platter is tilted back by it.
                Set(o, PartSlot.Prop, PuppetKit.Shape.Cylinder, Silver, PlatterPos, new Vector3(PlatterTilt, 0f, 0f), new Vector3(0.62f, 0.012f, 0.62f), smooth: 0.9f);
                Set(o, PartSlot.Prop2, PuppetKit.Shape.Dome, Silver, ClochePos, new Vector3(PlatterTilt, 0f, 0f), new Vector3(0.52f, 0.42f, 0.52f), smooth: 0.92f);
                break;
            case PuppetProp.Racket:
                Set(o, PartSlot.Prop, PuppetKit.Shape.Cylinder, White, new Vector3(0f, -0.1f, 0.05f), Vector3.zero, new Vector3(0.03f, 0.12f, 0.03f));
                Set(o, PartSlot.Prop2, PuppetKit.Shape.Cylinder, C(0.3f, 0.75f, 0.35f), new Vector3(0f, -0.38f, 0.05f), new Vector3(0f, 0f, 90f), new Vector3(0.26f, 0.012f, 0.3f));
                break;
            case PuppetProp.Duster:
                Set(o, PartSlot.Prop, PuppetKit.Shape.Cylinder, Brown, new Vector3(0f, 0.15f, 0.05f), Vector3.zero, new Vector3(0.025f, 0.15f, 0.025f));
                Set(o, PartSlot.Prop2, PuppetKit.Shape.Sphere, Pink, new Vector3(0f, 0.36f, 0.05f), Vector3.zero, new Vector3(0.16f, 0.2f, 0.16f));
                break;
            case PuppetProp.Trowel:
                Set(o, PartSlot.Prop, PuppetKit.Shape.Cube, Silver, new Vector3(0f, -0.12f, 0.05f), Vector3.zero, new Vector3(0.08f, 0.14f, 0.015f), smooth: 0.8f);
                break;
            case PuppetProp.Cigar:
                Set(o, PartSlot.Prop, PuppetKit.Shape.Capsule, Brown, new Vector3(0f, 0.04f, 0.08f), new Vector3(70f, 0f, 0f), new Vector3(0.035f, 0.07f, 0.035f));
                break;
            case PuppetProp.Flashlight:
                Set(o, PartSlot.Prop, PuppetKit.Shape.Cylinder, C(0.95f, 0.8f, 0.2f), new Vector3(0f, 0f, 0.08f), new Vector3(90f, 0f, 0f), new Vector3(0.06f, 0.1f, 0.06f), emission: 0.6f);
                break;
            case PuppetProp.Phone:
                Set(o, PartSlot.Prop, PuppetKit.Shape.Cube, Black, new Vector3(0f, 0.05f, 0.06f), Vector3.zero, new Vector3(0.07f, 0.14f, 0.015f));
                break;
        }
    }

    static void Set(RigSpec o, PartSlot slot, PuppetKit.Shape shape, Color color, Vector3 pos, Vector3 euler, Vector3 scale,
        float smooth = 0.25f, float emission = 0f, bool shadow = false, bool lite = false)
    {
        ref var p = ref o.parts[(int)slot];
        p.on = true; p.shape = shape; color.a = color.a <= 0f ? 1f : color.a; p.color = color; p.smooth = smooth; p.emission = emission;
        p.matKind = 0; p.pos = pos; p.euler = euler; p.scale = scale; p.shadow = shadow; p.lite = lite;
    }

    /// <summary>The butler's platter: the right arm is held this many degrees forward and the platter tilted back by it,
    /// so it stays level (hand space: platter centre, and the cloche's rest on its top).</summary>
    public const float PlatterTilt = 62f;
    public static readonly Vector3 PlatterPos = new Vector3(0f, 0.06f, 0.18f);
    /// <summary>Platter-up in hand space (Euler(PlatterTilt, 0, 0) · up).</summary>
    public static readonly Vector3 PlatterUp = new Vector3(0f, Mathf.Cos(PlatterTilt * Mathf.Deg2Rad), Mathf.Sin(PlatterTilt * Mathf.Deg2Rad));
    public static readonly Vector3 ClochePos = PlatterPos + PlatterUp * 0.006f;

    // ------------------------------------------------------------------ the car driver's head (D)

    /// <summary>
    /// The lite head D shows through a windscreen (§3.2: head, nose, hat; 3 renderers, no colliders) for a resident.
    /// The returned transform's origin is the neck; parent it to the car and destroy it with the car.
    /// </summary>
    public static Transform BuildDriverHead(Transform parent, in ResidentStyle st)
    {
        var spec = new RigSpec();
        Compose(st, false, false, spec);
        var root = PuppetKit.Node(parent, "DriverHead");
        var hatPivot = PuppetKit.Node(root, "Hat");
        hatPivot.localPosition = new Vector3(0f, 0.72f * spec.layout.headR + spec.layout.headR, 0f);
        PartSlot[] keep = { PartSlot.Skull, PartSlot.Nose, PartSlot.Hat0, PartSlot.Hat1 };
        int made = 0;
        foreach (var slot in keep)
        {
            ref var p = ref spec.parts[(int)slot];
            if (!p.on || made >= 3) continue;
            var parentT = slot == PartSlot.Hat0 || slot == PartSlot.Hat1 ? hatPivot : root;
            var r = PuppetKit.Part(parentT, slot.ToString(), out var f);
            PuppetRig.ApplyPart(r, f, in p);
            made++;
        }
        return root;
    }
}
