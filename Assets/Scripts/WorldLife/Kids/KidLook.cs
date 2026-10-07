using UnityEngine;

public enum KidHair : byte { Short, Bowl, Buzz, Afro, Pigtails, Ponytail }
public enum KidHat : byte { None, Cap, BackwardsCap, Propeller, Bucket }
public enum KidOutfit : byte { None, Stripe, Overalls, Cape, Tutu }
public enum KidProp : byte { None, Balloon, ToyTruck, Lollipop, Sign }

/// <summary>One kid's look (§3.1, §12.K): picked from LifeHash by <see cref="KidLook.StyleFor"/>, so a kid looks the same
/// every day and on every machine. Plain data; <see cref="KidLook.Compose"/> turns it into the universal rig's parts.</summary>
public struct KidStyle
{
    /// <summary>Metres to the top of the head (0.95–1.15; Chuck is 1.59, the neighbours 1.45–1.85).</summary>
    public float height;
    public float headScale;
    public byte skinTone;
    public Color shirt, bottoms, shoes, hair, hatColor, outfitColor, propColor;
    public bool shorts;
    public KidHair hairKind;
    public KidHat hat;
    public KidOutfit outfit;
    public KidProp prop;
    public bool glasses, bandAid, tooth;
    /// <summary>Squeaky (mumble pitch).</summary>
    public float voicePitch;
    public string name;

    public Color Skin => PuppetKit.SkinTones[skinTone % PuppetKit.SkinTones.Length];
    /// <summary>Waves with the left hand (the right one holds the balloon string / the toy truck).</summary>
    public bool WavesLeft => prop == KidProp.Balloon || prop == KidProp.ToyTruck || prop == KidProp.Lollipop;
}

/// <summary>
/// WorldLife kids (Docs/WorldLife.md §3.1, §12.K): small Chuck-grammar puppets for the fenced yards and the playground.
/// Built on the residents' universal <see cref="PuppetRig"/> (same 28 slots, restyled from a pool, same lite LOD), with
/// kid proportions: ~1 m tall, a head ~43% of that, googly eyes even bigger, stubby limbs, giant sneakers. Caps (some
/// backwards), propeller beanies, pigtails; balloons, toy garbage trucks, lollipops and cardboard signs ("HI TRUCK!").
/// Looks are LifeHash data (rule 2: no UnityEngine.Random). Text is ASCII only.
/// </summary>
public static class KidLook
{
    static Color C(float r, float g, float b) => new Color(r, g, b, 1f);

    static readonly Color[] Shirts =
    {
        C(0.95f, 0.26f, 0.24f), C(1f, 0.82f, 0.18f), C(0.24f, 0.56f, 0.98f), C(0.36f, 0.82f, 0.36f), C(1f, 0.55f, 0.16f),
        C(0.66f, 0.42f, 0.92f), C(1f, 0.52f, 0.74f), C(0.2f, 0.78f, 0.78f), C(0.98f, 0.98f, 0.96f),
    };
    static readonly Color[] Bottoms = { C(0.30f, 0.40f, 0.62f), C(0.22f, 0.28f, 0.45f), C(0.74f, 0.66f, 0.46f), C(0.86f, 0.24f, 0.26f), C(0.45f, 0.46f, 0.5f), C(0.95f, 0.6f, 0.8f) };
    static readonly Color[] Shoes = { C(0.98f, 0.98f, 0.96f), C(0.92f, 0.22f, 0.2f), C(0.22f, 0.5f, 0.95f), C(0.98f, 0.82f, 0.2f), C(0.36f, 0.86f, 0.42f), C(1f, 0.45f, 0.75f) };
    static readonly Color[] HairColors =
    {
        C(0.10f, 0.08f, 0.07f), C(0.26f, 0.17f, 0.11f), C(0.44f, 0.29f, 0.17f), C(0.62f, 0.32f, 0.14f), C(0.92f, 0.78f, 0.42f), C(0.85f, 0.45f, 0.2f),
    };
    static readonly Color[] Balloons = { C(1f, 0.2f, 0.25f), C(0.25f, 0.6f, 1f), C(1f, 0.85f, 0.15f), C(0.6f, 0.95f, 0.35f), C(1f, 0.45f, 0.85f), C(0.65f, 0.35f, 0.95f) };
    static readonly Color TruckGreen = C(0.16f, 0.55f, 0.27f), TruckYellow = C(1f, 0.82f, 0.18f), White = C(0.97f, 0.97f, 0.95f),
        Cardboard = C(0.78f, 0.6f, 0.38f), Pink = C(1f, 0.62f, 0.78f), BandAid = C(0.96f, 0.76f, 0.62f), Stick = C(0.96f, 0.94f, 0.88f);

    public static readonly string[] Names =
    {
        "Timmy", "Mia", "Jojo", "Ava", "Leo", "Zoe", "Max", "Lily", "Sam", "Ruby", "Finn", "Ivy", "Theo", "Nina", "Kai", "Pip",
        "Benny", "Rosie", "Ollie", "Dot",
    };

    /// <summary>Cardboard signs (ASCII only; a line break = two lines on the board).</summary>
    public static readonly string[] SignTexts = { "HI TRUCK!", "FEED ME!", "HONK!!", "HONK\nPLZ", "TRUCK\n#1", "BEEP\nBEEP!", "I LOVE\nTRASH" };

    /// <summary>The look of kid <paramref name="hash"/> (LifeHash of the Town seed, the pen and the kid): same every day,
    /// same on every machine.</summary>
    public static KidStyle StyleFor(ulong hash)
    {
        var d = new PuppetDice(hash);
        var s = new KidStyle
        {
            height = d.Range(0.95f, 1.15f), headScale = d.Range(0.97f, 1.08f), skinTone = (byte)d.Range(0, PuppetKit.SkinTones.Length),
            shirt = d.Pick(Shirts), bottoms = d.Pick(Bottoms), shoes = d.Pick(Shoes), hair = d.Pick(HairColors),
            hatColor = d.Pick(Shirts), outfitColor = d.Pick(Shirts), propColor = d.Pick(Balloons),
            shorts = d.Value < 0.55f, hairKind = (KidHair)d.Range(0, 6), voicePitch = d.Range(1.75f, 2.2f), name = d.Pick(Names),
        };
        if (s.bottoms == s.shirt) s.bottoms = Bottoms[0];
        float hatRoll = d.Value;
        s.hat = hatRoll < 0.18f ? KidHat.Cap : hatRoll < 0.3f ? KidHat.BackwardsCap : hatRoll < 0.42f ? KidHat.Propeller : hatRoll < 0.5f ? KidHat.Bucket : KidHat.None;
        if (s.hairKind == KidHair.Pigtails && s.hat != KidHat.None) s.hat = KidHat.None;    // pigtails use Hat1/Hat2
        if (s.hairKind == KidHair.Afro && s.hat != KidHat.Propeller) s.hat = KidHat.None;   // nothing sits on an afro but a beanie
        if (s.hairKind == KidHair.Ponytail && s.hat == KidHat.Propeller) s.hat = KidHat.Cap;
        float outfitRoll = d.Value;
        s.outfit = outfitRoll < 0.2f ? KidOutfit.Stripe : outfitRoll < 0.34f ? KidOutfit.Overalls : outfitRoll < 0.44f ? KidOutfit.Cape : outfitRoll < 0.54f ? KidOutfit.Tutu : KidOutfit.None;
        float propRoll = d.Value;
        s.prop = propRoll < 0.3f ? KidProp.Balloon : propRoll < 0.55f ? KidProp.ToyTruck : propRoll < 0.68f ? KidProp.Lollipop : KidProp.None;
        s.glasses = d.Value < 0.18f;
        s.bandAid = d.Value < 0.15f;
        s.tooth = d.Value < 0.3f;
        return s;
    }

    // ------------------------------------------------------------------ compose

    /// <summary>Lay a kid out as the universal rig's parts (fills <paramref name="o"/> in place; no allocation).
    /// <paramref name="sign"/>: this kid holds a cardboard sign (both hands; no hand prop).</summary>
    public static void Compose(in KidStyle st, bool sign, RigSpec o)
    {
        o.night = false;
        for (int i = 0; i < o.parts.Length; i++) o.parts[i] = default;
        Color skin = st.Skin;
        o.skin = skin;

        float H = Mathf.Clamp(st.height, 0.85f, 1.25f);
        float R = 0.215f * H * Mathf.Clamp(st.headScale, 0.95f, 1.1f);   // a BIG head: ~43% of the height
        float headCY = H - R;
        float neckY = headCY - 0.72f * R;                                   // the rig puts the skull 0.72 R above the neck
        float hipY = 0.27f * H;
        float bodyBottom = hipY - 0.05f * H, bodyTop = neckY + 0.12f * R;
        float span = bodyTop - bodyBottom;
        float w = 0.36f * H, dpt = 0.3f * H;
        ref var L = ref o.layout;
        L.height = H; L.headR = R; L.hipY = hipY; L.neckY = neckY; L.bodyW = w; L.bodyD = dpt;
        L.shoulderY = bodyTop - 0.1f * H; L.shoulderX = w * 0.5f - 0.02f;
        L.armLen = 0.24f * H; L.legLen = hipY; L.legX = Mathf.Min(w * 0.24f, 0.09f);
        L.hand = new Vector3(0f, -L.armLen, 0f);
        L.carry = new Vector3(0f, 0.3f * H - hipY, dpt * 0.5f + 0.12f);
        L.liteShape = PuppetKit.Shape.Capsule;
        L.litePos = new Vector3(0f, (bodyTop - hipY - hipY) * 0.5f, 0f);
        L.liteScale = new Vector3(w * 0.92f, bodyTop * 0.5f, dpt * 0.92f);

        float midY = (bodyBottom + bodyTop) * 0.5f - hipY;   // torso space

        // ---- body: a round tummy in the t-shirt colour
        Set(o, PartSlot.Body, PuppetKit.Shape.Sphere, st.shirt, new Vector3(0f, midY, 0f), Vector3.zero, new Vector3(w, span * 1.02f, dpt), shadow: true, lite: true);

        // ---- outfit layer
        Color oc = st.outfitColor;
        switch (st.outfit)
        {
            case KidOutfit.Stripe:
                Set(o, PartSlot.Outfit, PuppetKit.Shape.Cylinder, oc == st.shirt ? White : oc, new Vector3(0f, midY + 0.02f * H, 0f), Vector3.zero,
                    new Vector3(w * 1.015f, span * 0.09f, dpt * 1.015f));
                break;
            case KidOutfit.Overalls:
                Set(o, PartSlot.Outfit, PuppetKit.Shape.Sphere, st.bottoms, new Vector3(0f, midY - span * 0.16f, 0.004f), Vector3.zero,
                    new Vector3(w * 1.02f, span * 0.74f, dpt * 1.02f));
                break;
            case KidOutfit.Cape:
                Set(o, PartSlot.Outfit, PuppetKit.Shape.Cube, oc, new Vector3(0f, midY + span * 0.05f, -dpt * 0.5f - 0.02f), new Vector3(12f, 0f, 0f),
                    new Vector3(w * 0.95f, span * 1.05f, 0.025f), shadow: true);
                break;
            case KidOutfit.Tutu:
                Set(o, PartSlot.Outfit, PuppetKit.Shape.Cylinder, Pink, new Vector3(0f, bodyBottom + span * 0.18f - hipY, 0f), Vector3.zero,
                    new Vector3(w * 1.45f, 0.018f * H, dpt * 1.45f));
                break;
        }

        // ---- head
        Vector3 skullC = new Vector3(0f, 0.72f * R, 0f);
        Set(o, PartSlot.Skull, PuppetKit.Shape.Sphere, skin, skullC, Vector3.zero, Vector3.one * (2f * R), shadow: true, lite: true);
        Set(o, PartSlot.Nose, PuppetKit.Shape.Sphere, skin * 0.96f, skullC + new Vector3(0f, -0.12f * R, 0.97f * R), Vector3.zero, Vector3.one * (0.34f * R), lite: true);
        o[PartSlot.Nose].color.a = 1f;
        Set(o, PartSlot.Mouth, PuppetKit.Shape.Cube, PuppetKit.MouthDark, skullC + new Vector3(0f, -0.44f * R, 0.88f * R), new Vector3(-28f, 0f, 0f),
            new Vector3(0.38f * R, 0.06f * R, 0.08f * R));
        float e = 0.58f * R;   // even bigger googly eyes than the grown-ups
        for (int side = -1; side <= 1; side += 2)
        {
            Vector3 dir = new Vector3(0.36f * side, 0.22f, 0.9f).normalized;
            var eyeSlot = side < 0 ? PartSlot.EyeL : PartSlot.EyeR;
            Set(o, eyeSlot, PuppetKit.Shape.Sphere, PuppetKit.EyeWhite, skullC + dir * (R * 0.92f), new Vector3(-dir.y * 50f, dir.x * 40f, 0f),
                new Vector3(e, e, e * 0.55f), smooth: 0.9f);
            Set(o, side < 0 ? PartSlot.PupilL : PartSlot.PupilR, PuppetKit.Shape.Sphere, PuppetKit.Pupil, new Vector3(0f, -0.1f, 0.34f), Vector3.zero,
                new Vector3(0.52f, 0.52f, 0.45f), smooth: 0.95f);
            Set(o, side < 0 ? PartSlot.BrowL : PartSlot.BrowR, PuppetKit.Shape.Cube, st.hair * 0.8f, skullC + new Vector3(0.36f * side * R, 0.5f * R, 0.86f * R),
                new Vector3(-20f, 0f, -12f * side), new Vector3(0.34f * R, 0.07f * R, 0.08f * R));
            o[side < 0 ? PartSlot.BrowL : PartSlot.BrowR].on = false;   // kids don't do angry
        }

        ComposeHair(st, R, o);
        ComposeAccents(st, R, skullC, o);

        // ---- arms (pivot at the shoulder, hanging down): t-shirt sleeves = bare arms
        float armR = 0.05f * H, mitt = 0.1f * H;
        for (int side = -1; side <= 1; side += 2)
        {
            Set(o, side < 0 ? PartSlot.ArmL : PartSlot.ArmR, PuppetKit.Shape.Capsule, skin, new Vector3(0f, -L.armLen * 0.48f, 0f), Vector3.zero,
                new Vector3(2f * armR, L.armLen * 0.5f, 2f * armR));
            Set(o, side < 0 ? PartSlot.MittL : PartSlot.MittR, PuppetKit.Shape.Sphere, skin, new Vector3(0f, -L.armLen, 0f), Vector3.zero,
                new Vector3(mitt, mitt * 1.05f, mitt));
        }

        // ---- legs (pivot at the hip) and giant sneakers
        float legR = 0.06f * H;
        Color legC = st.shorts ? skin : st.bottoms;
        for (int side = -1; side <= 1; side += 2)
        {
            Set(o, side < 0 ? PartSlot.LegL : PartSlot.LegR, PuppetKit.Shape.Capsule, legC, new Vector3(0f, -hipY * 0.5f, 0f), Vector3.zero,
                new Vector3(2f * legR, hipY * 0.5f + legR * 0.3f, 2f * legR), shadow: true);
            Vector3 shoeS = new Vector3(0.11f * H, 0.075f * H, 0.17f * H);
            Set(o, side < 0 ? PartSlot.ShoeL : PartSlot.ShoeR, PuppetKit.Shape.Sphere, st.shoes, new Vector3(0f, -hipY + shoeS.y * 0.45f, 0.035f * H), Vector3.zero,
                shoeS, smooth: 0.5f);
        }

        // ---- the hand prop (right hand; hand space: +Y up the arm, +Z in front). The balloon is re-posed every frame by
        // the director so it floats above the hand whatever the arm does.
        if (!sign) ComposeProp(st, o);
    }

    static void ComposeHair(in KidStyle st, float R, RigSpec o)
    {
        Color hc = st.hair;
        bool covers = st.hat == KidHat.Propeller;
        // Hair caps (Hat0, from the skull top): a dome whose base sits just above the googly eyes and whose top clears the
        // skull, tilted back so the hairline rises at the front (a lower, flatter dome only shows as a ring round the head).
        Vector3 capPos = new Vector3(0f, -0.6f * R, -0.05f * R), capEuler = new Vector3(-10f, 0f, 0f), capScale = new Vector3(1.98f * R, 1.45f * R, 1.98f * R);
        switch (st.hairKind)
        {
            case KidHair.Short:
                Set(o, PartSlot.Hat0, PuppetKit.Shape.Dome, hc, capPos, capEuler, capScale, lite: true);
                break;
            case KidHair.Bowl:   // a lower, rounder cap: bangs down to the top of the eyes
                Set(o, PartSlot.Hat0, PuppetKit.Shape.Dome, hc, new Vector3(0f, -0.64f * R, -0.02f * R), new Vector3(-5f, 0f, 0f), new Vector3(2.12f * R, 1.5f * R, 2.12f * R), lite: true);
                break;
            case KidHair.Buzz:
                Set(o, PartSlot.Hat0, PuppetKit.Shape.Dome, hc, new Vector3(0f, -0.55f * R, -0.04f * R), new Vector3(-8f, 0f, 0f), new Vector3(1.95f * R, 1.22f * R, 1.95f * R), lite: true);
                break;
            case KidHair.Afro:   // big and round, sitting back on the head so the eyes peek out
                Set(o, PartSlot.Hat0, PuppetKit.Shape.Sphere, hc, new Vector3(0f, -0.2f * R, -0.35f * R), Vector3.zero, Vector3.one * (2.3f * R), lite: true);
                break;
            case KidHair.Pigtails:
                Set(o, PartSlot.Hat0, PuppetKit.Shape.Dome, hc, capPos, capEuler, capScale, lite: true);
                Set(o, PartSlot.Hat1, PuppetKit.Shape.Sphere, hc, new Vector3(-1.02f * R, -0.62f * R, -0.1f * R), new Vector3(0f, 0f, 20f), new Vector3(0.62f * R, 0.8f * R, 0.62f * R));
                Set(o, PartSlot.Hat2, PuppetKit.Shape.Sphere, hc, new Vector3(1.02f * R, -0.62f * R, -0.1f * R), new Vector3(0f, 0f, -20f), new Vector3(0.62f * R, 0.8f * R, 0.62f * R));
                break;
            case KidHair.Ponytail:
                Set(o, PartSlot.Hat0, PuppetKit.Shape.Dome, hc, capPos, capEuler, capScale, lite: true);
                Set(o, PartSlot.Hat1, PuppetKit.Shape.Capsule, hc, new Vector3(0f, -0.5f * R, -1.0f * R), new Vector3(30f, 0f, 0f), new Vector3(0.42f * R, 0.6f * R, 0.42f * R));
                break;
        }
        if (covers) o[PartSlot.Hat0].on = false;

        Color hat = st.hatColor;
        switch (st.hat)
        {
            case KidHat.Cap:
            case KidHat.BackwardsCap:
            {
                float back = st.hat == KidHat.BackwardsCap ? -1f : 1f;
                Set(o, PartSlot.Hat1, PuppetKit.Shape.Dome, hat, new Vector3(0f, -0.42f * R, -0.02f * R * back), new Vector3(-8f * back, 0f, 0f), new Vector3(2.1f * R, 1.25f * R, 2.1f * R), lite: true);
                Set(o, PartSlot.Hat2, PuppetKit.Shape.Cylinder, hat, new Vector3(0f, -0.4f * R, 0.78f * R * back), new Vector3(-6f * back, 0f, 0f), new Vector3(1.25f * R, 0.03f * R, 1.0f * R));
                o[PartSlot.Hat0].lite = false;
                break;
            }
            case KidHat.Propeller:
                Set(o, PartSlot.Hat0, PuppetKit.Shape.Dome, hat, new Vector3(0f, -0.5f * R, -0.03f * R), new Vector3(-6f, 0f, 0f), new Vector3(2.12f * R, 1.45f * R, 2.12f * R), lite: true);
                Set(o, PartSlot.Hat1, PuppetKit.Shape.Cylinder, TruckYellow, new Vector3(0f, 0.24f * R, -0.03f * R), Vector3.zero, new Vector3(0.12f * R, 0.14f * R, 0.12f * R));
                Set(o, PartSlot.Hat2, PuppetKit.Shape.Cube, st.propColor, new Vector3(0f, 0.38f * R, -0.03f * R), Vector3.zero, new Vector3(1.6f * R, 0.03f * R, 0.22f * R));
                break;
            case KidHat.Bucket:
                Set(o, PartSlot.Hat1, PuppetKit.Shape.Cylinder, hat, new Vector3(0f, -0.38f * R, 0f), new Vector3(-4f, 0f, 0f), new Vector3(2.9f * R, 0.04f * R, 2.9f * R), lite: true);
                Set(o, PartSlot.Hat2, PuppetKit.Shape.Dome, hat, new Vector3(0f, -0.4f * R, 0f), new Vector3(-4f, 0f, 0f), new Vector3(1.9f * R, 1.2f * R, 1.9f * R));
                o[PartSlot.Hat0].lite = false;
                break;
        }
    }

    static void ComposeAccents(in KidStyle st, float R, Vector3 skullC, RigSpec o)
    {
        if (st.glasses)
            Set(o, PartSlot.Accent, PuppetKit.Shape.Cube, C(0.15f, 0.12f, 0.1f), skullC + new Vector3(0f, 0.28f * R, 0.98f * R), new Vector3(-10f, 0f, 0f), new Vector3(1.3f * R, 0.07f * R, 0.06f * R));
        else if (st.bandAid)
            Set(o, PartSlot.Accent, PuppetKit.Shape.Cube, BandAid, skullC + new Vector3(0.45f * R, -0.18f * R, 0.86f * R), new Vector3(0f, 25f, 30f), new Vector3(0.36f * R, 0.12f * R, 0.04f * R));
        if (st.tooth)
            Set(o, PartSlot.Accent2, PuppetKit.Shape.Cube, White, skullC + new Vector3(0.05f * R, -0.405f * R, 0.9f * R), new Vector3(-28f, 0f, 0f), new Vector3(0.1f * R, 0.1f * R, 0.06f * R));
    }

    static void ComposeProp(in KidStyle st, RigSpec o)
    {
        switch (st.prop)
        {
            case KidProp.Balloon:
                // String + balloon (the director re-poses both every frame: the balloon floats up whatever the arm does).
                Set(o, PartSlot.Prop, PuppetKit.Shape.Cylinder, Stick, new Vector3(0f, 0.4f, 0.04f), Vector3.zero, new Vector3(0.008f, 0.4f, 0.008f));
                Set(o, PartSlot.Prop2, PuppetKit.Shape.Sphere, st.propColor, new Vector3(0f, 0.95f, 0.04f), Vector3.zero, new Vector3(0.3f, 0.36f, 0.3f), smooth: 0.75f);
                break;
            case KidProp.ToyTruck:
                // A pocket-sized garbage truck: the green body and the yellow cab in front.
                Set(o, PartSlot.Prop, PuppetKit.Shape.Cube, TruckGreen, new Vector3(0f, 0.02f, 0.06f), Vector3.zero, new Vector3(0.11f, 0.11f, 0.2f), smooth: 0.5f);
                Set(o, PartSlot.Prop2, PuppetKit.Shape.Cube, TruckYellow, new Vector3(0f, 0.0f, 0.2f), Vector3.zero, new Vector3(0.1f, 0.085f, 0.08f), smooth: 0.5f);
                break;
            case KidProp.Lollipop:
                Set(o, PartSlot.Prop, PuppetKit.Shape.Cylinder, Stick, new Vector3(0f, 0.1f, 0.04f), Vector3.zero, new Vector3(0.015f, 0.1f, 0.015f));
                Set(o, PartSlot.Prop2, PuppetKit.Shape.Cylinder, st.propColor, new Vector3(0f, 0.22f, 0.04f), new Vector3(90f, 0f, 0f), new Vector3(0.16f, 0.02f, 0.16f), smooth: 0.8f);
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
}

/// <summary>What the kids say (floating text; ASCII only). Picked by the cosmetic dice, rate-limited by the director.</summary>
public static class KidLines
{
    public static readonly Color Cheer = new Color(1f, 0.92f, 0.3f);
    public static readonly Color Hi = new Color(0.6f, 0.95f, 1f);
    public static readonly Color Fan = new Color(1f, 0.55f, 0.85f);

    public static readonly string[] Wave = { "HI TRUCK!", "GARBAGE TRUCK!!", "TRUCK! TRUCK!", "HI MISTER!", "WOW!!", "IT'S HERE!", "THE TRUCK!!", "HONK HONK!", "BYE TRASH!" };
    public static readonly string[] Yay = { "YAAAY!", "YAAAAAY!!", "WOOOO!", "AGAIN! AGAIN!", "BEST TRUCK!", "YAY!!" };
    public static readonly string[] Bye = { "BYE TRUCK!", "COME BACK!", "awww", "BYEEE!" };
    public static readonly string[] Fans = { "BEST. DAY. EVER!", "HE HONKED AT US!!", "I'M GONNA BE A TRASH MAN!", "BIGGEST FAN!" };
    public static readonly string[] Eek = { "EEK!", "MOMMY!", "TOO CLOSE!" };
}
