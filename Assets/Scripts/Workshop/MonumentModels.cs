using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Turns and bobs parts of a Monument. Every pose is a pure function of the clock (never an accumulated delta), and the
/// pose a part was registered in is its rest pose: a model that never gets an Update (edit mode, a still render)
/// simply stands as it was built.
/// </summary>
public sealed class MonumentMotion : MonoBehaviour
{
    [Serializable] struct Spinner { public Transform part; public Vector3 axis; public float degreesPerSecond; public Quaternion rest; }
    [Serializable] struct Bobber { public Transform part; public float amplitude, hertz, phase; public Vector3 rest; }
    [SerializeField] List<Spinner> spinners = new List<Spinner>();
    [SerializeField] List<Bobber> bobbers = new List<Bobber>();

    public int SpinnerCount => spinners.Count;
    public int BobberCount => bobbers.Count;

    /// <summary>Turn <paramref name="part"/> about its own local <paramref name="axis"/>, starting from the pose it has now.</summary>
    public void AddSpinner(Transform part, Vector3 axis, float degreesPerSecond)
    {
        if (part == null || axis.sqrMagnitude < 1e-8f) return;
        spinners.Add(new Spinner { part = part, axis = axis.normalized, degreesPerSecond = degreesPerSecond, rest = part.localRotation });
    }

    /// <summary>Bob <paramref name="part"/> along its parent's Y around where it is now. <paramref name="phase"/> is in turns (0..1).</summary>
    public void AddBobber(Transform part, float amplitude, float hertz, float phase = 0f)
    {
        if (part == null) return;
        bobbers.Add(new Bobber { part = part, amplitude = amplitude, hertz = hertz, phase = phase, rest = part.localPosition });
    }

    void Update() => Pose(Time.time);

    /// <summary>Pose every registered part for an absolute time in seconds (for a still at a chosen moment). No allocation.</summary>
    public void Pose(float time)
    {
        for (int i = 0; i < spinners.Count; i++)
        {
            var s = spinners[i];
            // doubles: Time.time times a speed loses whole degrees of precision after a few hours in a float
            if (s.part != null) s.part.localRotation = s.rest * Quaternion.AngleAxis((float)((double)time * s.degreesPerSecond % 360.0), s.axis);
        }
        for (int i = 0; i < bobbers.Count; i++)
        {
            var b = bobbers[i];
            if (b.part != null) b.part.localPosition = b.rest + Vector3.up * (b.amplitude * (float)Math.Sin(((double)time * b.hertz + b.phase) * (Math.PI * 2.0)));
        }
    }
}

/// <summary>
/// The Monument (<see cref="MonumentCatalog"/>): ten tiers of one trophy, each the last one grown up. Tiers 1-5 are the
/// scrap pal and what gathers round him in the open (totem, hubcap sunrise, bottle colonnade, bike-wheel orrery); from
/// tier 6 the totem stands in a shrine on a wedding-cake base and the building rises over it (lightbulb spire, can
/// cyclone, angel, cathedral, golden colossus). Same rules as every workshop model: renderer-only parts on layer 2,
/// one cached material per colour, metres, Y = 0 is the ground, +Z is the face.
/// </summary>
public static partial class WorkshopModels
{
    static readonly Color Concrete = new Color(.63f, .64f, .6f);
    // unlit: lightbulbs, lit bottle glass, the tier-10 crown
    static readonly Color BulbGlow = new Color(1f, .87f, .45f), GlassGlow = new Color(.5f, .93f, .8f), GoldGlow = new Color(1f, .77f, .25f);

    // A pal is built in "pal units" under a node that is then scaled: feet at 0, shoulders (the hubcap belt) at .46,
    // head centre at .61, crown of the head at .76, top of a (tilted) saucepan hat at .875. A pal standing on another starts .74 up.
    const float PalPitch = .74f, PalHead = .61f, PalTall = .76f, PalHat = .875f;

    public static WorkshopModel BuildMonument(int tier, Transform parent, Color accent, string businessName = null)
    {
        tier = Mathf.Clamp(tier, 1, MonumentCatalog.MaxLevel);
        var k = new Kit("Monument tier " + tier, parent, accent);
        float top = tier <= 5 ? TotemTiers(k, tier, businessName, out float foot) : ShrineTiers(k, tier, businessName, out foot);
        k.Anchor("Action", new Vector3(0, 0, foot + .8f));
        k.Anchor("Top", new Vector3(0, top, 0));
        return k.Finish("monument");
    }

    // ------------------------------------------------------------------ tiers 1-5: the pal and what gathers round him
    //
    // Vertical stacks (metres, measured from the built parts) against the limits (max height / max footprint radius):
    // T1 Scrap Pal 1.2/.5       plinth 0-.15, manhole cover -.17; pal x1.12: tin .17-.64, belt .62-.69, head .69-1.02
    //                           (grin .75, eyes .93), saucepan .96-1.15. Widest: the low hand, r .48.
    // T2 Tin Totem 1.9/.55      plinth 0-.15, cover -.17; pals x.81/.73/.65 with feet at .17/.77/1.31, heads centred
    //                           .66/1.21/1.70, saucepan 1.76-1.88. Widest: hands r .47, plaque corners r .51.
    // T3 Hubcap Sunrise 2.5/.8  plinth 0-.22 (r .7), step -.3; pals x.86/.77/.69 at .3/.94/1.51, heads centred
    //                           .82/1.41/1.93, saucepan -2.11; halo r .48 behind the top head: spoons to 2.47, r .56.
    //                           Widest: plaque corners r .785.
    // T4 Bottle Colonnade 2.9/1 plinth 0-.28 (r .92), step -.34; pals x1/.9/.8 at .34/1.08/1.75, heads centred
    //                           .95/1.63/2.23, saucepan -2.45; halo r .58: spoons to 2.88, r .66; bottles .28-1.32 on
    //                           r .8 (outer .9), hoop 1.31-1.37: over the bottom head (top 1.10), under the middle chin
    //                           (1.50). Widest: plaque corners r .97.
    // T5 Bike-Wheel Orrery 3.4/1.2  T4's totem and colonnade on an r 1.08 plinth, halo cut to r .42 (reach r .51) so
    //                           the wheels clear it; mast 2.39-3.17, turning hub 3.12, tin sun -3.35; wheels (r .27)
    //                           hang on r .86 at 2.30/1.95/2.72: tyres sweep r .57-1.15 (moons to 1.175), never
    //                           below 1.84, so always over the hoop.
    static float TotemTiers(Kit k, int tier, string businessName, out float foot)
    {
        Transform r = k.root;
        foot = tier == 1 ? .4f : tier == 2 ? .45f : tier == 3 ? .7f : tier == 4 ? .92f : 1.08f;
        // the first step is a little taller than its plaque (two lines of title in T3 and T4, a name line from T4)
        float y = tier == 3 ? .22f : tier >= 4 ? .28f : .15f;
        MCyl(r, "Concrete plinth", new Vector3(0, y * .5f, 0), foot * 2f, y, Concrete);
        Plaque(k, tier, foot, tier == 1 ? .34f : tier == 2 ? .44f : tier == 3 ? .66f : .56f, y, false, businessName);
        if (tier <= 2) { MCyl(r, "Manhole cover", new Vector3(0, y + .01f, 0), foot * 1.55f, .02f, Steel); y += .02f; }
        else { float h = tier == 3 ? .08f : .06f; MCyl(r, "Plinth step", new Vector3(0, y + h * .5f, 0), 1.04f, h, Concrete); y += h; }
        if (tier == 1) { Pal(r, new Vector3(0, y, 0), 1.12f, k.accent, k.accent, 3, 1, 0f, true); return y + PalHat * 1.12f; }

        float top = Totem(r, y, tier == 2 ? .81f : tier == 3 ? .86f : 1f, k.accent, out float headY, out float headScale);
        if (tier >= 3)
        {
            float halo = tier == 3 ? .48f : tier == 4 ? .58f : .42f;
            Halo(r, new Vector3(0, headY, -(.165f * headScale + .05f)), halo, tier == 5 ? 7 : 9, false);
            top = headY + halo + .07f;
        }
        if (tier >= 4) Colonnade(r, .28f, .8f, 1.04f, tier == 4 ? 16 : 12, tier == 4);
        if (tier == 5) top = Orrery(k, headY + .2f * headScale, 3.12f, .86f);
        return top;
    }

    // ------------------------------------------------------------------ tiers 6-10: the shrine and what rises over it
    //
    // Every tier: a three-step cake; on its top step the totem (x.84 in T6, x.86 after) between four bottle piers that
    // carry a dustbin-lid roof 6 cm over the saucepan; everything else stands on that roof. Measured stacks:
    // T6 Lightbulb Spire 4.0/1.25   cake to .2/.37/.54 (r 1.17/.93/.69), lamps .2-.74 on r 1.05; totem .54-2.31; roof
    //                               2.37-2.49 (r .66), cap -2.57; four jars -3.57; socket, big bulb 3.63-3.89.
    // T7 The Recyclone 4.6/1.35     cake to .28/.44/.6 (r 1.26/1/.72); totem .6-2.41; roof 2.475-2.595 (r .68), cap
    //                               -2.675; five jars -4.175; bulb 4.235-4.515. Can helix 2.72-4.16, r .62 narrowing
    //                               to .2: outside the jars and their bulbs (r .26), inside the pediment (r .68+), over
    //                               the cap, under the bulb.
    // T8 Garbage Angel 5.2/1.6      cake to .28/.47/.66 (r 1.5/1.14/.78); totem .66-2.47; roof 2.535-2.655 (r .72),
    //                               cap -2.735; three jars -3.725 (helix 2.79-3.65, r .43-.64); gold capital -3.805;
    //                               angel x1.3: head centre 4.60, crown of head 4.79; lid halo 4.85-5.08; wings root
    //                               4.17, out to |x| 1.62 (r 1.68: the 1.7 allowance) and up to 5.15. Body r 1.58.
    // T9 Cathedral of Cans 6.0/1.7  T8's cake at r 1.56/1.1/.8 and shrine; roof 2.535-2.655 (r .74); drum -3.555
    //                               (r .52), cornice -3.655 (r .64); three jars -4.495 (helix 3.71-4.43, r .41-.58,
    //                               over the buttresses, which land on the drum at 3.30); capital -4.575; angel x1.3:
    //                               head centre 5.37, halo to 5.85, wings to 5.92 and r 1.68. Corner spires on r 1.30:
    //                               tins .28-2.6, funnel -3.2, bulb, pennant pole -3.76.
    // T10 Trashcendence 7.0/1.8     cake to .23/.46/.69 (r 1.68/1.14/.82); totem .69-2.50; roof 2.565-2.685 (r .76);
    //                               drum -3.725 (r .55), cornice -3.845; two jars -4.465 (can collar 3.90-4.39,
    //                               r .55-.66); gold lid -4.565; colossus x2.2: belt 5.51, head centre 5.91, crown
    //                               of head 6.24; glowing crown 6.12-6.55; halo rings (r .86 and .7) wobble through
    //                               6.22-6.64 about 6.43; wings up to 6.24 and r 1.77; satellites orbit r 1.25 at
    //                               6.65 and r 1.62 at 3.5 (each +-.17), outside the corner spires (r 1.36: tins
    //                               .23-2.8, funnel -3.4, bulb, pole -3.96) and under their pennants (3.83).
    static readonly float[] CakeFoot = { 1.17f, 1.26f, 1.5f, 1.56f, 1.68f }, CakeMid = { .93f, 1f, 1.14f, 1.1f, 1.14f }, CakeTop = { .69f, .72f, .78f, .8f, .82f };
    // the first step is a little taller than its plaque; the other two share what is left of the cake's height
    static readonly float[] CakeHeight = { .54f, .6f, .66f, .66f, .69f }, CakeStep = { .2f, .28f, .28f, .28f, .23f };
    static readonly float[] RoofRadius = { .66f, .68f, .72f, .74f, .76f }, PlaqueWidth = { .7f, .72f, .8f, 1.1f, 1f };
    static readonly int[] SpireJars = { 4, 5, 3, 3, 2 }, HelixCans = { 0, 22, 18, 14, 15 };
    static readonly float[] JarHeight = { .25f, .3f, .33f, .28f, .31f }, JarWide = { .3f, .32f, .34f, .32f, .38f }, JarNarrow = { .15f, .14f, .28f, .26f, .34f };
    static readonly float[] HelixFoot = { 0f, .56f, .58f, .52f, .6f }, HelixHead = { 0f, .24f, .48f, .46f, .6f }, HelixTurns = { 0f, 1.6f, 1.1f, .9f, .8f };

    static float ShrineTiers(Kit k, int tier, string businessName, out float foot)
    {
        Transform r = k.root; Color accent = k.accent; int n = tier - 6;
        bool gilt = tier >= 8, cathedral = tier >= 9, golden = tier == 10;
        foot = CakeFoot[n];
        float step = CakeStep[n], y = CakeHeight[n], rise = (y - step) * .5f, roofR = RoofRadius[n];
        // headroom: the roof sits a hand above the saucepan so the top face is not lost under its rim
        float totemScale = tier == 6 ? .84f : .86f, shrine = 2.11f * totemScale + .06f, roofY = y + shrine;
        float drumR = golden ? .55f : .52f, drumH = golden ? 1.04f : .9f, drumY = roofY + .12f;

        // the cake, its lights and what stands on its ledges
        for (int i = 0; i < 3; i++)
        {
            float lip = step + i * rise, height = i == 0 ? step : rise, radius = i == 0 ? foot : i == 1 ? CakeMid[n] : CakeTop[n];
            MCyl(r, "Cake step", new Vector3(0, lip - height * .5f, 0), radius * 2f, height, golden && i == 2 ? Gold : Concrete);
            // a band just under the lip, not a lid on it: only its outer 2 cm shows
            if (gilt) MCyl(r, "Gold trim", new Vector3(0, lip - .04f, 0), radius * 2f + .04f, .03f, Gold);
        }
        Plaque(k, tier, foot, PlaqueWidth[n], step, gilt, businessName);
        LampPosts(r, step, (foot + CakeMid[n]) * .5f);
        Bulbs(r, step + rise + .045f, (CakeMid[n] + CakeTop[n]) * .5f, 8, 0f, .1f);
        if (cathedral)
        {
            float corner = golden ? .96f : .92f, shoulder = golden ? 2.8f : 2.6f;
            for (int i = 0; i < 4; i++)
            {
                float sx = i % 2 == 0 ? 1f : -1f, sz = i < 2 ? 1f : -1f;
                Vector3 at = new Vector3(sx * corner, step, sz * corner), inward = new Vector3(-sx, 0, -sz) * .7071f;
                CornerSpire(r, at, shoulder - step, accent, golden);
                MBeam(r, "Flying buttress", new Vector3(at.x, shoulder - .12f, at.z) + inward * .12f, new Vector3(0, drumY + drumH * .72f, 0) - inward * (drumR - .02f), .08f, golden ? Gold : Cream);
            }
        }

        // the shrine: the totem between four bottle piers (on the diagonals, so it looks out of all four sides)
        Totem(r, y, totemScale, accent, out _, out _);
        for (int i = 0; i < 4; i++)
        {
            float pier = (roofR - .13f) * .7071f;
            BottlePillar(r, new Vector3((i % 2 == 0 ? 1f : -1f) * pier, y, (i < 2 ? 1f : -1f) * pier), shrine, .17f, i < 2 ? Green : Blue, 3, false, gilt ? Gold : Steel);
        }
        MCyl(r, "Dustbin-lid roof", new Vector3(0, roofY + .06f, 0), roofR * 2f, .12f, golden ? Gold : Steel);
        if (gilt && !golden) MCyl(r, "Gold trim", new Vector3(0, roofY + .06f, 0), roofR * 2f + .04f, .04f, Gold);
        Bulbs(r, roofY + .06f, roofR + .02f, 8, 22.5f, .1f);
        Halo(r, new Vector3(0, roofY + .19f, roofR + .015f), .24f, 7, gilt);   // the sunrise, now a pediment over the portal
        y = roofY + .12f;

        if (cathedral)
        {
            Drum(r, y, drumH, drumR, accent, golden);
            y += drumH;
            float cornice = golden ? .12f : .1f;
            MCyl(r, "Cornice", new Vector3(0, y + cornice * .5f, 0), (drumR + .12f) * 2f, cornice, Gold);
            Bulbs(r, y + cornice * .5f, drumR + .14f, 8, 22.5f, .1f);
            y += cornice;
        }
        else { MCyl(r, "Roof cap", new Vector3(0, y + .04f, 0), roofR * 1.4f, .08f, accent); y += .08f; }

        // the spire of jars, and the cans that circle it
        float spireY = y;
        y = JarStack(r, y, SpireJars[n], JarWide[n], JarNarrow[n], JarHeight[n], gilt ? Gold : Coral);
        if (tier >= 7)
        {
            // the helix turns as one piece, so it keeps to a band of its own: above the roof cap or cornice, below the
            // bulb or capital, outside the jars and their bulbs
            var helix = Helix(r, spireY + .12f, y - (tier == 7 ? .1f : .14f), HelixFoot[n], HelixHead[n], HelixTurns[n], HelixCans[n], accent);
            Motion(k).AddSpinner(helix, Vector3.up, 14f);
        }
        if (!gilt)
        {
            float bulb = tier == 6 ? .26f : .28f;
            MCyl(r, "Bulb socket", new Vector3(0, y + .035f, 0), .1f, .07f, Dark);
            Glow(r, PrimitiveType.Sphere, "Bright idea", new Vector3(0, y + .06f + bulb * .5f, 0), Vector3.one * bulb, BulbGlow);
            return y + .06f + bulb;
        }

        // the figure on top
        float lid = golden ? .1f : .08f, scale = golden ? 2.2f : 1.3f;
        MCyl(r, golden ? "Golden dustbin lid" : "Gold capital", new Vector3(0, y + lid * .5f, 0), golden ? 1.1f : .84f, lid, Gold);
        y += lid;
        float wingTop = Wings(r, new Vector3(0, y + (golden ? .3f : .28f) * scale, -(.15f * scale + .04f)), golden ? 1.4f : 1.34f, accent, golden);
        float crown = y + PalTall * scale;
        if (!golden)
        {
            Pal(r, new Vector3(0, y, 0), scale, accent, accent, 4, 0, 0f, true, Gold);
            // tipped back so its lit underside faces whoever is looking up from the front
            MBeam(r, "Halo wire", new Vector3(0, crown - .1f, -.16f), new Vector3(0, crown + .15f, -.1f), .025f, Dark);
            Glow(r, PrimitiveType.Cylinder, "Dustbin-lid halo", new Vector3(0, crown + .17f, -.04f), new Vector3(.56f, .0175f, .56f), GoldGlow, new Vector3(-20f, 0, 0));
            MBox(r, "Lid handle", new Vector3(0, crown + .2f, -.05f), new Vector3(.14f, .035f, .04f), Gold, new Vector3(-20f, 0, 0));
            return Mathf.Max(crown + .32f, wingTop);
        }
        Pal(r, new Vector3(0, y, 0), scale, Gold, accent, 5, 0, 0f, true, Gold, Gold);
        // the trash still shows: bottle-cap medals on the gold chest, a marquee of bulbs round the hubcap belt
        for (int i = -1; i <= 1; i++) MCyl(r, "Bottle-cap medal", new Vector3(i * .3f, y + .36f * scale, .15f * scale), .16f, .05f, i < 0 ? Coral : i == 0 ? Blue : Green, new Vector3(90, 0, 0));
        Bulbs(r, y + .43f * scale, .33f * scale, 8, 22.5f, .11f);
        Crown(r, new Vector3(0, y + PalHead * scale, 0));
        HaloRings(k, new Vector3(0, crown + .19f, 0));
        Satellites(k, crown + .41f, 1.25f, 4, 22f, 0);
        Satellites(k, 3.5f, 1.62f, 4, -9f, 2);
        return crown + .66f;
    }

    // ------------------------------------------------------------------ the pal

    // Looks, by index: 0 the bearer (bottom of the totem, not amused), 1 the middle one, 2 the one on top, 3 waving
    // (tier 1, arms kept inside its .5 m footprint), 4 the angel, 5 the colossus. Degrees; arms are measured up from level.
    static readonly float[] LookArmRight = { -16f, 24f, 58f, 74f, 22f, 62f }, LookArmLeft = { -20f, -14f, 32f, -70f, 22f, 62f };
    static readonly float[] LookTurn = { -8f, 9f, -4f, 0f, 0f, 0f }, LookLean = { 5f, -6f, 4f, -5f, 0f, 0f };
    // the top pal's head rolls to its handle side so the saucepan handle droops instead of poking up
    static readonly float[] LookHeadTurn = { 4f, -5f, 3f, 6f, 0f, 0f }, LookHeadRoll = { 3f, -4f, -5f, -4f, 3f, 0f };

    /// <summary>
    /// One scrap pal with its feet at <paramref name="at"/>: tin torso, hubcap belt, scrap arms with washer hands, teapot
    /// head with mismatched button eyes, a spout for a nose and a mouth. <paramref name="hat"/>: 0 bare, 1 saucepan,
    /// 2 saucepan with its handle. <paramref name="rider"/>: scale of the pal underneath over this one's (0 = stands on
    /// the ground), which gives it stilt legs down to those shoulders. <paramref name="fine"/> adds the close-up detail.
    /// </summary>
    static Transform Pal(Transform parent, Vector3 at, float scale, Color body, Color trim, int look, int hat = 0, float rider = 0f, bool fine = false, Color? limbs = null, Color? skin = null)
    {
        Color limb = limbs ?? WoodLight, face = skin ?? Cream; bool gilded = skin.HasValue;
        var pal = Node(parent, "Scrap pal", at);
        pal.localScale = Vector3.one * scale; pal.localRotation = Quaternion.Euler(0, LookTurn[look], 0);
        var lean = new Vector3(0, 0, LookLean[look]);
        MBox(pal, "Tin torso", new Vector3(0, .21f, 0), new Vector3(.54f, .42f, .3f), body, lean);
        if (fine) MBox(pal, "Tin label", new Vector3(0, .2f, .151f), new Vector3(.38f, .16f, .012f), gilded ? trim : Cream, lean);
        if (rider > 0f) for (int side = -1; side <= 1; side += 2)
            MBeam(pal, "Stilt leg", new Vector3(side * .2f, .04f, 0), new Vector3(side * .29f * rider, .02f - .28f * rider, 0), .07f, Dark);
        MCyl(pal, "Hubcap belt", new Vector3(0, .43f, 0), .66f, .06f, Steel);
        for (int side = -1; side <= 1; side += 2)
        {
            // a raised arm is a longer arm: it has to clear the belt to read as a wave
            float a = (side > 0 ? LookArmRight[look] : LookArmLeft[look]) * Mathf.Deg2Rad, reach = .24f + .06f * Mathf.Max(0f, Mathf.Sin(a));
            Vector3 shoulder = new Vector3(side * .28f, .3f, 0), hand = shoulder + new Vector3(side * Mathf.Cos(a) * reach, Mathf.Sin(a) * reach, .03f);
            MBeam(pal, "Scrap arm", shoulder, hand, .055f, limb);
            if (fine) MBall(pal, "Arm bolt", shoulder, Vector3.one * .08f, Steel);
            MCyl(pal, "Washer hand", hand, .13f, .035f, Dark, new Vector3(90, 0, 0));
            if (fine) MCyl(pal, "Washer hole", hand, .05f, .045f, Steel, new Vector3(90, 0, 0));
        }

        var head = Node(pal, "Head", new Vector3(0, PalHead, 0));
        head.localRotation = Quaternion.Euler(0, LookHeadTurn[look], LookHeadRoll[look]);
        MBall(head, "Teapot head", Vector3.zero, new Vector3(.42f, .3f, .33f), face);
        MCyl(head, "Spout nose", new Vector3(0, -.03f, .19f), .07f, .12f, face, new Vector3(70, 0, 0));
        if (fine) MCyl(head, "Spout cork", new Vector3(0, -.009f, .247f), .05f, .03f, WoodLight, new Vector3(70, 0, 0));
        for (int side = -1; side <= 1; side += 2)
        {
            // one eye a size up, alternating down the totem: that is the whole of the derp
            float d = side == (look % 2 == 0 ? 1 : -1) ? .105f : .085f;
            Vector3 eye = new Vector3(side * .092f, .058f, .137f);
            if (gilded) MCyl(head, "Eye white", eye + new Vector3(0, 0, -.004f), d + .04f, .05f, Cream, new Vector3(90, 0, 0));
            MCyl(head, "Button eye", eye, d, .05f, Dark, new Vector3(90, 0, 0));
            MBall(head, "Eye glint", eye + new Vector3(.013f, .016f, .028f), Vector3.one * (d * .3f), Cream);
        }
        if (look == 0) MBox(head, "Flat mouth", new Vector3(0, -.085f, .125f), new Vector3(.14f, .026f, .06f), Coral, new Vector3(0, 0, 3f));
        else if (look == 1) MBox(head, "Crooked grin", new Vector3(0, -.085f, .125f), new Vector3(.17f, .028f, .06f), Coral, new Vector3(0, 0, -8f));
        else
        {
            MBox(head, "Big grin", new Vector3(0, -.095f, .125f), new Vector3(.11f, .03f, .06f), Coral);
            for (int side = -1; side <= 1; side += 2) MBox(head, "Grin corner", new Vector3(side * .078f, -.077f, .115f), new Vector3(.065f, .03f, .06f), Coral, new Vector3(0, 0, side * 34f));
        }
        if (hat > 0)
        {
            // an upturned saucepan: the pot is the crown of the hat, its rim the brim, and the handle leaves from the rim
            MCyl(head, "Saucepan hat", new Vector3(0, .185f, 0), .36f, .13f, trim);
            MCyl(head, "Saucepan rim", new Vector3(0, .125f, 0), .48f, .025f, trim);
            if (hat > 1) MBeam(head, "Hat handle", new Vector3(.2f, .13f, 0), new Vector3(.45f, .19f, 0), .04f, Dark);
        }
        return pal;
    }

    /// <summary>Three pals on each other's shoulders, scaled 1 : .9 : .8 from <paramref name="scale"/>, feet at
    /// <paramref name="y"/>. Returns the top of the saucepan; headY / headScale are the top pal's head centre and scale.</summary>
    static float Totem(Transform parent, float y, float scale, Color accent, out float headY, out float headScale)
    {
        Friends(accent, out Color bottom, out Color middle);
        Pal(parent, new Vector3(0, y, 0), scale, bottom, accent, 0);
        Pal(parent, new Vector3(0, y + PalPitch * scale, 0), scale * .9f, middle, accent, 1, 0, 1f / .9f);
        float feet = y + PalPitch * scale * 1.9f;
        headScale = scale * .8f; headY = feet + PalHead * headScale;
        Pal(parent, new Vector3(0, feet, 0), headScale, accent, accent, 2, 2, .9f / .8f);
        return feet + PalHat * headScale;
    }

    /// <summary>Tin colours for the two pals under the business-coloured one: of coral, blue and green, drop the hue
    /// nearest the business colour and put the furthest one next to it.</summary>
    static void Friends(Color accent, out Color bottom, out Color middle)
    {
        Color[] tin = { Coral, Blue, Green }; var gap = new float[3]; int near = 0;
        Color.RGBToHSV(accent, out float hue, out _, out _);
        for (int i = 0; i < 3; i++)
        {
            Color.RGBToHSV(tin[i], out float h, out _, out _);
            gap[i] = Mathf.Abs(h - hue); if (gap[i] > .5f) gap[i] = 1f - gap[i];
            if (gap[i] < gap[near]) near = i;
        }
        int a = (near + 1) % 3, b = (near + 2) % 3;
        middle = tin[gap[a] >= gap[b] ? a : b]; bottom = tin[gap[a] >= gap[b] ? b : a];
    }

    // ------------------------------------------------------------------ pieces

    /// <summary>The hubcap sunrise: a hubcap standing in the XY plane at <paramref name="centre"/> with a fan of spoon
    /// rays over its upper 200 degrees; long rays end in a spoon bowl, short ones in a hubcap stud.</summary>
    static void Halo(Transform parent, Vector3 centre, float radius, int rays, bool gilt)
    {
        MCyl(parent, "Hubcap sun", centre, radius * .9f, .04f, Steel, new Vector3(90, 0, 0));
        for (int i = 0; i < rays; i++)
        {
            float degrees = Mathf.Lerp(-100f, 100f, i / (rays - 1f)), a = degrees * Mathf.Deg2Rad; bool spoon = i % 2 == 0;
            Vector3 dir = new Vector3(Mathf.Sin(a), Mathf.Cos(a), 0), tip = centre + dir * (spoon ? radius : radius * .8f);
            MBeam(parent, "Spoon sunray", centre + dir * (radius * .4f), tip, .03f, gilt ? Gold : WoodLight);
            if (spoon) MBall(parent, "Spoon bowl", tip, new Vector3(.1f, .13f, .04f), Steel, new Vector3(0, 0, -degrees));
            else MCyl(parent, "Hubcap stud", tip, .11f, .035f, Cream, new Vector3(90, 0, 0));
        }
    }

    /// <summary>A pillar of <paramref name="bottles"/> bottles standing neck-up on each other from <paramref name="at"/>.</summary>
    static void BottlePillar(Transform parent, Vector3 at, float height, float diameter, Color glass, int bottles, bool label, Color? cap)
    {
        float h = (height - (cap.HasValue ? .04f : 0f)) / bottles;
        for (int i = 0; i < bottles; i++)
        {
            Vector3 foot = at + Vector3.up * (i * h);
            MCyl(parent, "Bottle body", foot + Vector3.up * (h * .34f), diameter, h * .68f, glass);
            if (label) MCyl(parent, "Bottle label", foot + Vector3.up * (h * .32f), diameter + .012f, h * .2f, Cream);
            MCyl(parent, "Bottle neck", foot + Vector3.up * (h * .84f), diameter * .42f, h * .32f, glass);
        }
        if (cap.HasValue) MCyl(parent, "Bottle cap", at + Vector3.up * (height - .02f), diameter * .6f, .04f, cap.Value);
    }

    // pillar bearings from the front, mirrored left and right: the widest gap is the portal the faces look out of
    static readonly float[] ColonnadeBearing = { 32f, 72f, 108f, 148f };

    /// <summary>Eight bottle pillars on a ring, joined by a hoop resting on their caps.</summary>
    static void Colonnade(Transform parent, float y, float radius, float height, int hoopSegments, bool caps)
    {
        for (int i = 0; i < 8; i++)
        {
            float a = ColonnadeBearing[i / 2] * (i % 2 == 0 ? 1f : -1f) * Mathf.Deg2Rad;
            BottlePillar(parent, new Vector3(Mathf.Sin(a) * radius, y, Mathf.Cos(a) * radius), height, .19f, i / 2 % 2 == 0 ? Green : Blue, 1, true, caps ? Steel : (Color?)null);
        }
        Loop(parent, "Colonnade hoop", new Vector3(0, y + height + .02f, 0), radius * 2f, .06f, Steel, hoopSegments);
    }

    /// <summary>A ring of box segments in the node's XZ plane (Kit.Ring with a segment count). Returns the ring node.</summary>
    static Transform Loop(Transform parent, string name, Vector3 at, float diameter, float thickness, Color color, int segments, Vector3 euler = default)
    {
        var ring = Node(parent, name, at); ring.localRotation = Quaternion.Euler(euler);
        float step = Mathf.PI * 2f / segments, middle = diameter * .5f * Mathf.Cos(step * .5f), length = diameter * Mathf.Sin(step * .5f) + thickness * .3f;
        for (int i = 0; i < segments; i++)
        {
            float a = (i + .5f) * step;
            MBox(ring, "Rim segment", new Vector3(Mathf.Cos(a) * middle, 0, Mathf.Sin(a) * middle), new Vector3(thickness, thickness, length), color, new Vector3(0, -a * Mathf.Rad2Deg, 0));
        }
        return ring;
    }

    /// <summary>A bicycle wheel: tyre in the node's XZ plane (the axle is local Y), three spokes right across. Returns the node.</summary>
    static Transform BikeWheel(Transform parent, string name, Vector3 at, float diameter, Vector3 euler, int segments)
    {
        var wheel = Node(parent, name, at); wheel.localRotation = Quaternion.Euler(euler);
        Loop(wheel, "Tyre", Vector3.zero, diameter, .04f, Dark, segments);
        for (int i = 0; i < 3; i++)
        {
            float a = i * 60f * Mathf.Deg2Rad; Vector3 reach = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * (diameter * .5f);
            MBeam(wheel, "Spokes", -reach, reach, .02f, Steel);
        }
        return wheel;
    }

    /// <summary>The orrery: a mast from <paramref name="mastFoot"/> with a tin sun on top, and under it a hub whose three
    /// arms carry bike wheels on wires. The arms turn as one (so they can never cross); each wheel also spins on its own
    /// tilted axle, a tin planet at its hub and a moon riding the tyre. Returns the top of the sun.</summary>
    static float Orrery(Kit k, float mastFoot, float hubY, float reach)
    {
        Transform r = k.root; Color[] planet = { k.accent, Coral, Blue };
        MCyl(r, "Orrery mast", new Vector3(0, (mastFoot + hubY + .05f) * .5f, 0), .05f, hubY + .05f - mastFoot, WoodLight);
        MBall(r, "Tin sun", new Vector3(0, hubY + .14f, 0), Vector3.one * .18f, k.accent);
        var arms = Node(r, "Orrery arms", new Vector3(0, hubY, 0));
        MCyl(arms, "Orrery hub", Vector3.zero, .26f, .06f, Steel);
        for (int i = 0; i < 3; i++)
        {
            // two wheels flank the totem at the front, the high one rides behind: nothing hangs in front of a face at rest
            float a = (30f + i * 120f) * Mathf.Deg2Rad, drop = i == 0 ? .82f : i == 1 ? 1.17f : .4f;
            Vector3 end = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * reach, hub = end + Vector3.down * drop;
            MBeam(arms, "Orrery arm", Vector3.zero, end, .04f, Dark);
            MCyl(arms, "Hanger wire", (end + hub) * .5f, .025f, drop, Dark);
            var wheel = BikeWheel(arms, "Bike wheel", hub, .54f, new Vector3(18f + i * 4f, i * 50f, 12f - i * 9f), 10);
            MBall(wheel, "Tin planet", Vector3.zero, Vector3.one * .17f, planet[i]);
            MCyl(wheel, "Tin moon", new Vector3(.27f, .04f, 0), .07f, .09f, Cream);
            Motion(k).AddSpinner(wheel, Vector3.up, (i % 2 == 0 ? 1f : -1f) * (34f + i * 9f));
        }
        Motion(k).AddSpinner(arms, Vector3.up, 8f);
        return hubY + .23f;
    }

    /// <summary>Eight little bottle lamps round a ledge, a lit bulb in each neck; none stands in front of the portal.</summary>
    static void LampPosts(Transform parent, float y, float radius)
    {
        for (int i = 0; i < 8; i++)
        {
            float a = (22.5f + i * 45f) * Mathf.Deg2Rad; Vector3 at = new Vector3(Mathf.Sin(a) * radius, y, Mathf.Cos(a) * radius);
            Color glass = (i + 1) / 2 % 2 == 0 ? Green : Blue;   // pairs, so left mirrors right
            MCyl(parent, "Lamp bottle", at + Vector3.up * .15f, .15f, .3f, glass);
            MCyl(parent, "Lamp neck", at + Vector3.up * .36f, .065f, .12f, glass);
            Glow(parent, PrimitiveType.Sphere, "Lightbulb", at + Vector3.up * .47f, Vector3.one * .13f, BulbGlow);
        }
    }

    static void Bulbs(Transform parent, float y, float radius, int count, float bearing, float size)
    {
        for (int i = 0; i < count; i++)
        {
            float a = (bearing + i * 360f / count) * Mathf.Deg2Rad;
            Glow(parent, PrimitiveType.Sphere, "Lightbulb", new Vector3(Mathf.Sin(a) * radius, y, Mathf.Cos(a) * radius), Vector3.one * size, BulbGlow);
        }
    }

    /// <summary>The spire: jam jars stacked from <paramref name="y"/>, tapering, a lit bulb stuck to each side. Returns its top.</summary>
    static float JarStack(Transform parent, float y, int jars, float wide, float narrow, float jarHeight, Color lid)
    {
        for (int i = 0; i < jars; i++)
        {
            float d = jars > 1 ? Mathf.Lerp(wide, narrow, i / (jars - 1f)) : wide;
            MCyl(parent, "Jam jar", new Vector3(0, y + (jarHeight - .04f) * .5f, 0), d, jarHeight - .04f, i % 2 == 0 ? Blue : Green);
            MCyl(parent, "Jar lid", new Vector3(0, y + jarHeight - .02f, 0), d + .03f, .04f, lid);
            for (int side = 0; side < 2; side++)
            {
                float a = (i * 70f + side * 180f) * Mathf.Deg2Rad, reach = d * .5f + .03f;
                Glow(parent, PrimitiveType.Sphere, "Lightbulb", new Vector3(Mathf.Sin(a) * reach, y + jarHeight * .45f, Mathf.Cos(a) * reach), Vector3.one * .1f, BulbGlow);
            }
            y += jarHeight;
        }
        return y;
    }

    /// <summary>The Recyclone: two interleaved spirals of cans round the Y axis of one node, which turns as a whole
    /// (nothing holds a can up: they are the weather). Returns that node.</summary>
    static Transform Helix(Transform parent, float from, float to, float radiusFrom, float radiusTo, float turns, int perStrand, Color accent)
    {
        var helix = Node(parent, "Recyclone", Vector3.zero);
        for (int strand = 0; strand < 2; strand++) for (int i = 0; i < perStrand; i++)
        {
            // even steps along the spiral, not up it: a narrowing spiral covers less ground per turn near the top
            float u = i / (perStrand - 1f), t = Mathf.Abs(radiusTo - radiusFrom) < .001f ? u : (Mathf.Sqrt(radiusFrom * radiusFrom + u * (radiusTo * radiusTo - radiusFrom * radiusFrom)) - radiusFrom) / (radiusTo - radiusFrom);
            float a = (t * turns + strand * .5f) * Mathf.PI * 2f, radius = Mathf.Lerp(radiusFrom, radiusTo, t);
            float climb = Mathf.Atan2(to - from, turns * Mathf.PI * 2f * radius) * Mathf.Rad2Deg;
            Color tin = strand == 0 ? (i % 2 == 0 ? accent : Cream) : (i % 2 == 0 ? Steel : Coral);
            // each can lies along the spiral (X tips its axis over to the climb angle, Y swings it round to the tangent)
            MCyl(helix, "Cyclone can", new Vector3(Mathf.Cos(a) * radius, Mathf.Lerp(from, to, t), Mathf.Sin(a) * radius), .11f, .15f, tin, new Vector3(90f - climb, -a * Mathf.Rad2Deg, 0));
        }
        return helix;
    }

    // feather bearings up from level, and lengths as a share of the reach: the long primaries low, shorter ones climbing
    static readonly float[] FeatherBearing = { -18f, 4f, 26f, 48f, 68f }, FeatherLength = { .7f, .94f, 1f, .8f, .56f };

    /// <summary>Fanned wings behind a figure, swept back toward the tips. <paramref name="root"/> is the point between
    /// the shoulder blades, just behind the back; <paramref name="reach"/> the longest feather. Each feather is two
    /// licence plates end to end; the three long ones carry a shovel blade. Returns the height of the highest blade.</summary>
    static float Wings(Transform parent, Vector3 root, float reach, Color accent, bool golden)
    {
        Color[] inner = { Cream, Blue, Cream, Coral, Cream }, outer = { Coral, Cream, accent, Cream, Blue }; float top = root.y;
        for (int side = -1; side <= 1; side += 2)
        {
            Vector3 hub = root + new Vector3(side * .14f, 0, 0);
            MCyl(parent, "Wing hubcap", hub + new Vector3(0, 0, -.025f), .3f, .15f, golden ? Gold : Steel, new Vector3(90, 0, 0));
            for (int i = 0; i < 5; i++)
            {
                float a = FeatherBearing[i] * Mathf.Deg2Rad, length = FeatherLength[i] * reach;
                // each feather starts a little behind the one below it: overlapping plates, never two faces in one plane
                Vector3 from = hub + new Vector3(0, 0, -.022f * i), dir = new Vector3(side * Mathf.Cos(a), Mathf.Sin(a), -.14f), middle = from + dir * (length * .5f), tip = from + dir * length;
                // MBeam's width runs across the wing (z here) and its height across the feather
                MBeam(parent, "Licence plate feather", from + dir * .08f, middle, .03f, golden ? Gold : inner[i], .2f);
                MBeam(parent, "Licence plate feather", middle, tip, .03f, outer[i], .17f);
                if (i >= 1 && i <= 3) MBall(parent, "Shovel blade", tip + dir * .07f, new Vector3(.22f, .3f, .05f), Gold, new Vector3(0, 0, side * (FeatherBearing[i] - 90f)));
                top = Mathf.Max(top, tip.y + .2f);
            }
        }
        return top;
    }

    /// <summary>A corner spire from <paramref name="at"/>: three stacked tins <paramref name="shaft"/> tall, a funnel
    /// roof of shrinking pots, a bulb, and a pennant on a pole.</summary>
    static void CornerSpire(Transform parent, Vector3 at, float shaft, Color accent, bool golden)
    {
        float y = at.y, h = shaft / 3f, face = at.z > 0 ? 1f : -1f;   // the front pair match and look front, the back pair match and look back
        for (int i = 0; i < 3; i++)
        {
            MCyl(parent, "Spire tin", new Vector3(at.x, y + h * .5f, at.z), .36f - i * .03f, h, i == 1 ? (face > 0 ? Coral : Blue) : Steel);
            MCyl(parent, "Tin rim", new Vector3(at.x, y + h - .02f, at.z), .4f - i * .03f, .04f, golden ? Gold : Dark);
            y += h;
        }
        Glow(parent, PrimitiveType.Cube, "Bottle-glass window", new Vector3(at.x, at.y + shaft * .62f, at.z + face * .15f), new Vector3(.11f, .3f, .04f), face > 0 ? GlassGlow : BulbGlow);
        for (int i = 0; i < 3; i++)
        {
            float height = .16f + i * .04f;
            MCyl(parent, "Funnel roof", new Vector3(at.x, y + height * .5f, at.z), .44f - i * .14f, height, golden ? Gold : accent);
            y += height;
        }
        Glow(parent, PrimitiveType.Sphere, "Lightbulb", new Vector3(at.x, y + .05f, at.z), Vector3.one * .14f, BulbGlow);
        MCyl(parent, "Pennant pole", new Vector3(at.x, y + .33f, at.z), .03f, .46f, Dark);
        MBox(parent, "Pennant", new Vector3(at.x + .145f, y + .49f, at.z), new Vector3(.26f, .13f, .02f), accent);
    }

    /// <summary>The cathedral drum from <paramref name="y"/>: one great tin with banded rims, pairs of lancet windows
    /// left, right and behind (one of each pair lit), and at the front a bike wheel over lit glass for a rose window.
    /// The diagonals are left bare: the buttresses land there.</summary>
    static void Drum(Transform parent, float y, float height, float radius, Color accent, bool golden)
    {
        MCyl(parent, "Cathedral drum", new Vector3(0, y + height * .5f, 0), radius * 2f, height, Cream);
        MCyl(parent, "Drum band", new Vector3(0, y + .05f, 0), radius * 2f + .04f, .1f, golden ? Gold : accent);
        MCyl(parent, "Drum band", new Vector3(0, y + height - .05f, 0), radius * 2f + .04f, .1f, golden ? Gold : accent);
        for (int face = 1; face < 4; face++) for (int side = -1; side <= 1; side += 2)
        {
            float bearing = face * 90f + side * 12f, a = bearing * Mathf.Deg2Rad;
            Vector3 at = new Vector3(Mathf.Sin(a) * radius, y + height * .5f, Mathf.Cos(a) * radius), size = new Vector3(.15f, height * .5f, .05f), turn = new Vector3(0, bearing, 0);
            if (side > 0) Glow(parent, PrimitiveType.Cube, "Bottle-glass window", at, size, face == 2 ? BulbGlow : GlassGlow, turn);
            else MBox(parent, "Bottle-glass window", at, size, face == 2 ? Blue : Coral, turn);
        }
        float rose = height * .5f;
        // glass first and thick: a flat disc on a round wall has to sink in at the middle to meet it at the edges
        Glow(parent, PrimitiveType.Cylinder, "Rose glass", new Vector3(0, y + height * .5f, radius - .03f), new Vector3(rose - .02f, .05f, rose - .02f), GlassGlow, new Vector3(90, 0, 0));
        BikeWheel(parent, "Rose window", new Vector3(0, y + height * .5f, radius + .03f), rose, new Vector3(90, 0, 0), 10);
    }

    /// <summary>The glowing crown on the colossus (head x2.2 centred at <paramref name="head"/>): a band on the brow,
    /// eight leaning points, a bottle-cap jewel on each.</summary>
    static void Crown(Transform parent, Vector3 head)
    {
        Color[] jewel = { Coral, Blue, Green, Cream };
        Glow(parent, PrimitiveType.Cylinder, "Crown band", head + Vector3.up * .27f, new Vector3(.7f, .06f, .7f), GoldGlow);
        for (int i = 0; i < 8; i++)
        {
            float bearing = i * 45f, a = bearing * Mathf.Deg2Rad; Vector3 dir = new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a));
            Glow(parent, PrimitiveType.Cube, "Crown point", head + dir * .33f + Vector3.up * .44f, new Vector3(.1f, .28f, .05f), GoldGlow, new Vector3(14f, bearing, 0));
            MBall(parent, "Crown jewel", head + dir * .365f + Vector3.up * .6f, Vector3.one * .09f, jewel[i % 4]);
        }
    }

    /// <summary>Two gold hoops round the crown, tilted opposite ways, each on its own node turning about the vertical in
    /// opposite directions: they wobble through each other's band but, being different sizes, never touch.</summary>
    static void HaloRings(Kit k, Vector3 centre)
    {
        for (int i = 0; i < 2; i++)
        {
            var turn = Node(k.root, "Halo ring", centre);
            float diameter = i == 0 ? 1.72f : 1.4f;
            var ring = Loop(turn, "Golden hoop", Vector3.zero, diameter, .05f, Gold, 16, i == 0 ? new Vector3(12f, 0, 0) : new Vector3(-10f, 0, 9f));
            for (int j = 0; j < 6; j++)
            {
                float a = (j * 60f + i * 30f) * Mathf.Deg2Rad;
                Glow(ring, PrimitiveType.Sphere, "Lightbulb", new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * (diameter * .5f), Vector3.one * .09f, BulbGlow);
            }
            Motion(k).AddSpinner(turn, Vector3.up, i == 0 ? 26f : -34f);
        }
    }

    /// <summary>Small trash in orbit: nothing holds it up. One node turns the ring; each piece bobs on its own.</summary>
    static void Satellites(Kit k, float y, float radius, int count, float degreesPerSecond, int first)
    {
        var orbit = Node(k.root, "Satellite orbit", new Vector3(0, y, 0));
        for (int i = 0; i < count; i++)
        {
            float a = (i * 360f / count + first * 37f) * Mathf.Deg2Rad;
            var sat = Node(orbit, "Trash satellite", new Vector3(Mathf.Cos(a) * radius, 0, Mathf.Sin(a) * radius));
            sat.localRotation = Quaternion.Euler(i * 25f - 30f, i * 70f, 20f - i * 15f);
            switch ((i + first) % 4)
            {
                case 0: MCyl(sat, "Orbiting can", Vector3.zero, .11f, .15f, k.accent); MCyl(sat, "Can lid", new Vector3(0, .08f, 0), .12f, .02f, Steel); break;
                case 1: MCyl(sat, "Orbiting bottle", Vector3.zero, .09f, .15f, Green); MCyl(sat, "Bottle neck", new Vector3(0, .1f, 0), .04f, .06f, Green); break;
                case 2: MBox(sat, "Orbiting carton", Vector3.zero, new Vector3(.1f, .15f, .1f), Cream); MBox(sat, "Carton cap", new Vector3(0, .09f, 0), new Vector3(.1f, .04f, .03f), Coral); break;
                default: MBall(sat, "Orbiting teapot", Vector3.zero, new Vector3(.16f, .12f, .13f), Blue); MCyl(sat, "Teapot spout", new Vector3(.09f, .02f, 0), .04f, .08f, Blue, new Vector3(0, 0, -55f)); break;
            }
            Motion(k).AddBobber(sat, .07f, .22f + i * .03f, i * .23f);
        }
        Motion(k).AddSpinner(orbit, Vector3.up, degreesPerSecond);
    }

    /// <summary>
    /// The gold plate on the front of a round base of radius <paramref name="foot"/>, centred on its first step: the
    /// tier's plaque text (long ones on two lines) and, from tier 4, a line for the business name. That label is made
    /// even when the name is empty and becomes the kit's label, so SetBusinessName can fill it in later.
    /// </summary>
    static void Plaque(Kit k, int tier, float foot, float width, float step, bool trimmed, string businessName)
    {
        string title = MonumentCatalog.Tier(tier).plaque; int cut = -1;
        if (title.Length > 16) for (int i = 1; i < title.Length - 1; i++)
            if (title[i] == ' ' && (cut < 0 || Mathf.Abs(i - title.Length / 2) < Mathf.Abs(cut - title.Length / 2))) cut = i;
        if (cut > 0) title = title.Substring(0, cut) + "\n" + title.Substring(cut + 1);
        bool named = tier >= 4;
        float titleHeight = cut > 0 ? .17f : tier == 10 ? .12f : .085f, nameHeight = named ? .06f : 0f, height = titleHeight + nameHeight + .03f, bottom = (step - height) * .5f;
        // a flat plate on a round wall: proud of it at the middle (and of the gold trim band, 2 cm off the step from
        // tier 8), and deep enough that its ends are still buried where the wall curves away
        float face = foot + (trimmed ? .03f : .012f), depth = face - Mathf.Sqrt(foot * foot - width * width * .25f) + .02f;
        k.Box("Monument plaque", new Vector3(0, bottom + height * .5f, face - depth * .5f), new Vector3(width, height, depth), Gold);
        k.Text("Monument title", title, new Vector3(0, bottom + height - .015f - titleHeight * .5f, face + .003f), new Vector2(width - .05f, titleHeight), Dark);
        if (!named) return;
        string name = string.IsNullOrWhiteSpace(businessName) ? "" : BusinessColors.CleanName(businessName).ToUpperInvariant();
        k.label = k.Text("Business name", name, new Vector3(0, bottom + .04f, face + .003f), new Vector2(width - .05f, .05f), Dark);
    }

    // ------------------------------------------------------------------ parts under any node

    static Transform MBox(Transform parent, string name, Vector3 at, Vector3 size, Color color, Vector3 euler = default) => MPart(parent, PrimitiveType.Cube, name, at, size, color, euler, false);
    static Transform MBall(Transform parent, string name, Vector3 at, Vector3 size, Color color, Vector3 euler = default) => MPart(parent, PrimitiveType.Sphere, name, at, size, color, euler, false);
    static Transform MCyl(Transform parent, string name, Vector3 at, float diameter, float height, Color color, Vector3 euler = default) => MPart(parent, PrimitiveType.Cylinder, name, at, new Vector3(diameter, height * .5f, diameter), color, euler, false);

    /// <summary>A box from one point to another. <paramref name="height"/> (0 = square) is its second cross-section side.</summary>
    static Transform MBeam(Transform parent, string name, Vector3 from, Vector3 to, float width, Color color, float height = 0f)
    {
        var t = MBox(parent, name, (from + to) * .5f, new Vector3(width, height > 0f ? height : width, Vector3.Distance(from, to)), color);
        t.localRotation = Quaternion.LookRotation(to - from, Vector3.up);
        return t;
    }

    /// <summary>A part that glows: the same renderer-only layer-2 part with an unlit material per colour.</summary>
    static Transform Glow(Transform parent, PrimitiveType primitive, string name, Vector3 at, Vector3 size, Color color, Vector3 euler = default) => MPart(parent, primitive, name, at, size, color, euler, true);

    /// <summary>
    /// The Monument's own part maker (WorkshopModels.Part with two differences). Every size in this file assumes the
    /// 1 m primitives: sphere 1 m across, cylinder 1 m across and 2 m tall. Those are the builtin "New-Sphere.fbx" /
    /// "New-Cylinder.fbx" / "New-Capsule.fbx"; the bare "Sphere.fbx" and "Cylinder.fbx" that Part loads are legacy meshes
    /// 2 m across. Cube and Quad are 1 m under their plain names. And it can give the part an unlit material.
    /// </summary>
    static Transform MPart(Transform parent, PrimitiveType primitive, string name, Vector3 at, Vector3 size, Color color, Vector3 euler, bool glow)
    {
        var go = new GameObject(name) { layer = 2 };
        go.transform.SetParent(parent, false); go.transform.localPosition = at;
        go.transform.localRotation = Quaternion.Euler(euler); go.transform.localScale = size;
        bool round = primitive == PrimitiveType.Sphere || primitive == PrimitiveType.Cylinder || primitive == PrimitiveType.Capsule;
        go.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>((round ? "New-" : "") + primitive + ".fbx");
        string hex = ColorUtility.ToHtmlStringRGB(color);
        go.AddComponent<MeshRenderer>().sharedMaterial = glow ? SortingKit.RuntimeMaterial("mat/monument_glow_" + hex, color, true) : SortingKit.RuntimeMaterial("mat/workshop_" + hex, color);
        return go.transform;
    }

    static MonumentMotion Motion(Kit k)
    {
        var motion = k.root.GetComponent<MonumentMotion>();
        return motion != null ? motion : k.root.gameObject.AddComponent<MonumentMotion>();
    }
}
