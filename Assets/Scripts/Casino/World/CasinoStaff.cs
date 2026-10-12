using System;
using System.Collections.Generic;
using UnityEngine;

public enum CasinoRole { Bartender, BlackjackDealer, RouletteDealer, Regular }
public enum CasinoRegularPose { BarStool, AsleepOnBar, SlotPlayer, TableSitter, Leaning, SlotStander }
public enum DealerGesture { Deal, Spin, Pay, Sweep }
public enum BartenderGesture { Pour, Serve }

/// <summary>
/// The people of The Lucky Raccoon (Docs/Casino.md §2): a bartender, a blackjack dealer, a roulette dealer and a handful
/// of regulars. Neighbour Puppets (<see cref="PuppetRig"/> + <see cref="ResidentLook"/>) with custom outfits, procedural
/// idles, heads that turn to a nearby player and the odd bark. <b>Purely cosmetic and local</b>: built on each machine when
/// its player walks in, nothing is networked, and the only dice are <see cref="PuppetDice"/> (cosmetic).
/// <para>Hooks for the screens: <c>CasinoStaff.Dealer(CasinoStationKind.Blackjack).Play(DealerGesture.Deal)</c>,
/// <c>CasinoStaff.Bartender.Play(BartenderGesture.Pour)</c>. Both are safe to call when nobody is built (a silent
/// stand-in answers).</para>
/// </summary>
public class CasinoStaff : MonoBehaviour
{
    [Serializable]
    public class Post
    {
        public string label = "";
        public CasinoRole role;
        public CasinoRegularPose pose;
        [Tooltip("Feet (standing) or the seat's top centre (sitting), facing local +Z.")]
        public Transform anchor;
        [Tooltip("Sitting: the anchor is a seat top, not the floor.")]
        public bool seated;
        public int seed = 1;
    }

    public Post[] posts = new Post[0];

    static CasinoStaff instance;
    static readonly CasinoStaffer nobody = new CasinoStaffer();
    readonly List<CasinoStaffer> people = new List<CasinoStaffer>();
    readonly List<PlayerController> players = new List<PlayerController>(4);
    float nextPlayerScan, nextBarkOk;

    /// <summary>The dealer of a table (<see cref="CasinoStationKind.Blackjack"/> or <see cref="CasinoStationKind.Roulette"/>).
    /// Never null.</summary>
    public static CasinoStaffer Dealer(CasinoStationKind kind) =>
        Find(kind == CasinoStationKind.Roulette ? CasinoRole.RouletteDealer : CasinoRole.BlackjackDealer);

    /// <summary>The bartender. Never null.</summary>
    public static CasinoStaffer Bartender => Find(CasinoRole.Bartender);

    static CasinoStaffer Find(CasinoRole role)
    {
        if (instance != null)
            for (int i = 0; i < instance.people.Count; i++)
                if (instance.people[i].Role == role) return instance.people[i];
        return nobody;
    }

    void OnEnable()
    {
        instance = this;
        if (Application.isPlaying) BuildPeople();
    }

    void OnDisable()
    {
        if (instance == this) instance = null;
        ClearPeople();
    }

    /// <summary>Build everyone at their posts (play mode: when the interior switches on; also the edit-mode preview).</summary>
    public void BuildPeople()
    {
        ClearPeople();
        for (int i = 0; i < posts.Length; i++)
        {
            var p = posts[i];
            if (p == null || p.anchor == null) continue;
            try { people.Add(new CasinoStaffer(this, p)); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }

    public void ClearPeople()
    {
        for (int i = 0; i < people.Count; i++) people[i].Destroy();
        people.Clear();
    }

    /// <summary>Edit-mode preview: pose everyone once at time <paramref name="t"/>.</summary>
    public void PosePreview(float t)
    {
        for (int i = 0; i < people.Count; i++) people[i].Tick(0.016f, t, null, false);
    }

    void Update()
    {
        if (Time.unscaledTime >= nextPlayerScan)
        {
            nextPlayerScan = Time.unscaledTime + 0.5f;
            players.Clear();
            var all = FindObjectsByType<PlayerController>(FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && CasinoLayout.Contains(all[i].transform.position)) players.Add(all[i]);
        }
        float dt = Mathf.Min(Time.deltaTime, 0.1f), t = Time.time;
        for (int i = 0; i < people.Count; i++)
        {
            var s = people[i];
            Transform near = null;
            float best = 5.5f * 5.5f;
            for (int k = 0; k < players.Count; k++)
            {
                if (players[k] == null) continue;
                float d = (players[k].transform.position - s.Position).sqrMagnitude;
                if (d < best) { best = d; near = players[k].transform; }
            }
            bool mayBark = t >= nextBarkOk;
            if (s.Tick(dt, t, near, mayBark)) nextBarkOk = t + 5f; // at most one line in the room every 5 s
        }
    }
}

/// <summary>One person in the casino: the look, the idle, the gestures and the lines. See <see cref="CasinoStaff"/>.</summary>
public sealed class CasinoStaffer
{
    static readonly Color White = new Color(0.93f, 0.92f, 0.88f), Black = new Color(0.07f, 0.07f, 0.08f);
    static readonly Color Glass = new Color(0.80f, 0.90f, 0.92f), Amber = new Color(0.78f, 0.47f, 0.13f), Rag = new Color(0.85f, 0.83f, 0.75f);

    static readonly string[] BartenderLines = { "What'll it be?", "Same again?", "We're out of the good stuff.", "Tab's still open, pal.", "Water's a dollar." };
    static readonly string[] BlackjackLines = { "Card?", "Hit or stand?", "House always wins, pal.", "Dealer hits soft seventeen.", "Feeling lucky?" };
    static readonly string[] RouletteLines = { "Place your bets.", "No more bets.", "Round and round she goes.", "Red or black?", "House always wins, pal." };
    static readonly string[] RegularLines = { "Any minute now...", "It's due. It's DUE.", "*hic*", "One more spin.", "I had a system.", "Don't bet the truck, kid.", "Been here since '62." };
    static readonly string[] SleepLines = { "z z z", "...zzz...", "*snore*", "...double or nothing..." };

    public CasinoRole Role { get; private set; } = CasinoRole.Regular;
    public Vector3 Position { get; private set; }
    /// <summary>False for the stand-in returned when nobody is built.</summary>
    public bool Exists => rig != null && rig.go != null;
    /// <summary>The hand that deals / pours (a screen can spawn a card or a bottle here).</summary>
    public Transform Hand => Exists ? rig.handR : null;

    readonly PuppetRig rig;
    readonly CasinoStaff.Post post;
    readonly ResidentStyle style;
    PuppetDice dice;
    readonly float seatDrop;
    float headYaw, headPitch, nextLine, talk, gestureT = -1f, gestureLen = 1f, idleSeed;
    int gesture = -1;
    Transform propR;

    /// <summary>The silent stand-in.</summary>
    public CasinoStaffer() { }

    public CasinoStaffer(CasinoStaff owner, CasinoStaff.Post p)
    {
        post = p;
        Role = p.role;
        ulong seed = LifeHash.Mix(0xCA5100UL + (ulong)(p.seed * 7919));
        dice = new PuppetDice(seed);
        style = StyleFor(p, seed);
        var spec = new RigSpec();
        ResidentLook.Compose(in style, false, true, spec);
        rig = PuppetRig.Build(p.anchor, "Casino " + (string.IsNullOrEmpty(p.label) ? p.role.ToString() : p.label));
        rig.go.hideFlags = HideFlags.DontSave; // never part of Casino.unity
        rig.Apply(spec);
        Dress(spec);
        // Sitting: hips on the seat top. Legs have no knees, so they point forward and down from the stool.
        seatDrop = p.seated ? rig.layout.hipY - 0.07f : 0f;
        if (p.seated) BuildSeatedLegs(Mathf.Max(0.2f, p.anchor.position.y - CasinoLayout.Origin.y));
        rig.root.localPosition = new Vector3(0f, -seatDrop, 0f);
        rig.root.localRotation = Quaternion.identity;
        Position = p.anchor.position;
        idleSeed = dice.Range(0f, 100f);
        nextLine = Time.time + dice.Range(4f, 14f);
        Tick(0.016f, Time.time, null, false);
    }

    static ResidentStyle StyleFor(CasinoStaff.Post p, ulong seed)
    {
        var d = new PuppetDice(seed ^ 0x51AFFUL);
        bool staff = p.role != CasinoRole.Regular;
        var arch = staff ? ResidentArchetype.Commuter
            : new[] { ResidentArchetype.RobeDad, ResidentArchetype.CargoDad, ResidentArchetype.CurlerGran, ResidentArchetype.Commuter, ResidentArchetype.MonocleGent, ResidentArchetype.DogPerson }[p.seed % 6];
        var s = ResidentLook.StyleFor(arch, 0, seed);
        s.prop = PuppetProp.None;
        if (staff)
        {
            // white shirt, black waistcoat, bow tie
            s.top = White; s.bottom = Black; s.shoes = Black;
            s.outfit = PuppetOutfit.Vest; s.outfitColor = Black;
            s.accent = PuppetAccent.BowTie; s.accent2 = PuppetAccent.None;
            s.hat = PuppetHat.None; s.bareLegs = s.slippers = false;
            switch (p.role)
            {
                case CasinoRole.Bartender:
                    s.build = PuppetBuild.Ball; s.height = 1.74f; s.hairKind = PuppetHair.CombOver; s.accent2 = PuppetAccent.Mustache;
                    s.nose = PuppetNose.Potato; s.voicePitch = 0.78f; s.name = "Sal";
                    break;
                case CasinoRole.BlackjackDealer:
                    s.build = PuppetBuild.Stick; s.height = 1.72f; s.hairKind = PuppetHair.Slick; s.accent2 = PuppetAccent.PencilMustache;
                    s.nose = PuppetNose.Long; s.voicePitch = 0.95f; s.name = "Vince";
                    break;
                default:
                    s.build = PuppetBuild.Pear; s.height = 1.6f; s.hairKind = PuppetHair.Beehive; s.accent2 = PuppetAccent.Glasses;
                    s.nose = PuppetNose.Ball; s.voicePitch = 1.3f; s.name = "Dolores";
                    break;
            }
        }
        else
        {
            // regulars: whatever they came in wearing, a bit washed out, no outdoor props
            s.top = Color.Lerp(s.top, new Color(0.45f, 0.38f, 0.33f), 0.35f);
            if (s.hat == PuppetHat.StrawHat || s.hat == PuppetHat.SunHat || s.hat == PuppetHat.TopHat) s.hat = d.Value < 0.5f ? PuppetHat.Cap : PuppetHat.None;
            if (p.pose == CasinoRegularPose.AsleepOnBar) s.hat = PuppetHat.Bowler;
        }
        return s;
    }

    void Dress(RigSpec spec)
    {
        switch (Role)
        {
            case CasinoRole.Bartender:
                // a glass in the right hand, a rag in the left: he is always polishing
                propR = Prim(rig.handR, "Glass", PuppetKit.Shape.Cylinder, Glass, new Vector3(0f, -0.02f, 0.05f), new Vector3(0.07f, 0.055f, 0.07f), 0.85f);
                Prim(rig.handL, "Rag", PuppetKit.Shape.Sphere, Rag, new Vector3(0f, -0.02f, 0.05f), new Vector3(0.12f, 0.05f, 0.1f), 0.05f);
                break;
            case CasinoRole.Regular:
                if (post.pose == CasinoRegularPose.BarStool || post.pose == CasinoRegularPose.TableSitter || post.pose == CasinoRegularPose.Leaning)
                {
                    propR = Prim(rig.handR, "Tumbler", PuppetKit.Shape.Cylinder, Glass, new Vector3(0f, -0.02f, 0.05f), new Vector3(0.075f, 0.05f, 0.075f), 0.85f);
                    Prim(propR, "Whisky", PuppetKit.Shape.Cylinder, Amber, new Vector3(0f, -0.35f, 0f), new Vector3(0.86f, 0.55f, 0.86f), 0.6f);
                }
                break;
        }
    }

    // ------------------------------------------------------------------ sitting

    Transform kneeL, kneeR;

    /// <summary>
    /// The rig's legs are one segment (no knee). For someone sitting, each leg becomes two: the rig's own leg part is
    /// shortened into a thigh, and a knee pivot at its end carries a new shin and the rig's shoe. <see cref="SeatLegs"/>
    /// then lays the thighs forward and hangs the shins down to the floor (a chair) or the foot ring (a stool).
    /// </summary>
    void BuildSeatedLegs(float seatHeight)
    {
        float hip = rig.layout.hipY;
        float thigh = Mathf.Clamp(hip * 0.75f, 0.16f, 0.3f);
        float shin = Mathf.Clamp(seatHeight - 0.02f, 0.16f, Mathf.Max(0.2f, hip * 1.15f));
        kneeL = Knee(PartSlot.LegL, PartSlot.ShoeL, rig.legL, thigh, shin);
        kneeR = Knee(PartSlot.LegR, PartSlot.ShoeR, rig.legR, thigh, shin);
    }

    Transform Knee(PartSlot legSlot, PartSlot shoeSlot, Transform pivot, float thigh, float shin)
    {
        var leg = rig.t[(int)legSlot];
        var shoe = rig.t[(int)shoeSlot];
        Vector3 ls = leg.localScale;
        leg.localPosition = new Vector3(0f, -thigh * 0.5f, 0f);
        leg.localScale = new Vector3(ls.x, thigh * 0.5f + ls.x * 0.25f, ls.z);
        var knee = PuppetKit.Node(pivot, "Knee");
        knee.localPosition = new Vector3(0f, -thigh, 0f);
        var r = PuppetKit.Part(knee, "Shin", out var f);
        f.sharedMesh = rig.f[(int)legSlot].sharedMesh;
        r.sharedMaterial = rig.r[(int)legSlot].sharedMaterial;
        r.transform.localPosition = new Vector3(0f, -shin * 0.5f, 0f);
        r.transform.localScale = new Vector3(ls.x, shin * 0.5f + ls.x * 0.25f, ls.z);
        Vector3 shoeLocal = shoe.localPosition;
        shoe.SetParent(knee, false);
        shoe.localPosition = new Vector3(0f, -shin + shoe.localScale.y * 0.3f, shoeLocal.z);
        return knee;
    }

    /// <summary>Thighs forward (pitch degrees below... above the vertical), shins hanging, feet tucked back a little.</summary>
    void SeatLegs(float left, float right, float spread, float tuck)
    {
        rig.Legs(-left, -right, spread);
        if (kneeL != null) kneeL.localRotation = Quaternion.Euler(left + tuck, 0f, spread);
        if (kneeR != null) kneeR.localRotation = Quaternion.Euler(right + tuck, 0f, -spread);
    }

    static Transform Prim(Transform parent, string name, PuppetKit.Shape shape, Color color, Vector3 pos, Vector3 scale, float smooth)
    {
        var renderer = PuppetKit.Part(parent, name, out var filter);
        var part = new PartSpec { on = true, shape = shape, color = color, pos = pos, scale = scale, smooth = smooth, shadow = false };
        PuppetRig.ApplyPart(renderer, filter, in part);
        return renderer.transform;
    }

    public void Destroy()
    {
        if (rig != null) rig.Destroy();
    }

    // ------------------------------------------------------------------ hooks

    /// <summary>Dealer: a short hand gesture (cosmetic; the screen times its own cards and chips).</summary>
    public void Play(DealerGesture g)
    {
        if (!Exists) return;
        gesture = (int)g;
        gestureT = 0f;
        gestureLen = g == DealerGesture.Spin ? 1.4f : g == DealerGesture.Sweep ? 1.1f : 0.8f;
    }

    /// <summary>Bartender: pour (about 1.6 s) or slide the drink over (0.8 s).</summary>
    public void Play(BartenderGesture g)
    {
        if (!Exists) return;
        gesture = 10 + (int)g;
        gestureT = 0f;
        gestureLen = g == BartenderGesture.Pour ? 1.6f : 0.8f;
    }

    // ---- the slot regulars (SlotRegulars drives these; cosmetic and local)

    /// <summary>True while the person is walking (the legs swing); set by whoever moves their anchor.</summary>
    public bool Walking;

    /// <summary>The anchor the rig is parented to (move it to walk the person somewhere).</summary>
    public Transform Anchor => post != null ? post.anchor : null;

    /// <summary>Show or hide the whole person.</summary>
    public void SetVisible(bool visible) { if (Exists) rig.go.SetActive(visible); }

    /// <summary>A regular at a slot machine: reach for the lever and haul it (about 1 s).</summary>
    public void Pull() { if (Exists) { gesture = 20; gestureT = 0f; gestureLen = 1.0f; } }

    /// <summary>A regular at a slot machine: arms up, a little hop (about 1.6 s).</summary>
    public void Cheer() { if (Exists) { gesture = 21; gestureT = 0f; gestureLen = 1.6f; } }

    /// <summary>A regular at a slot machine: a shrug at a loss (about 1 s).</summary>
    public void Shrug() { if (Exists) { gesture = 22; gestureT = 0f; gestureLen = 1.0f; } }

    /// <summary>Say a line over the head now (cosmetic; ignores the rate limit).</summary>
    public void Say(string line)
    {
        if (!Exists || string.IsNullOrEmpty(line)) return;
        Vector3 top = rig.head.position + Vector3.up * (rig.layout.headR * 2f + 0.2f);
        GameHUD.FloatingText(top, line, ResidentLines.Chat, 0.85f);
        Sfx.Play(dice.Value < 0.5f ? "mumble" : "mumble2", top, 0.4f, style.voicePitch);
        talk = 0.9f;
    }

    // ------------------------------------------------------------------ the idle

    /// <summary>Pose this frame. Returns true when a bark was said (the room's rate limit).</summary>
    public bool Tick(float dt, float t, Transform near, bool mayBark)
    {
        if (!Exists) return false;
        Position = post.anchor.position;
        float s1 = Mathf.Sin(t * 1.3f + idleSeed), s2 = Mathf.Sin(t * 0.57f + idleSeed * 1.7f), breathe = Mathf.Sin(t * 1.9f + idleSeed);
        bool asleep = Role == CasinoRole.Regular && post.pose == CasinoRegularPose.AsleepOnBar;

        // the head turns to whoever is near (never the sleeper)
        float wantYaw = 12f * s2, wantPitch = 0f;
        if (near != null && !asleep)
        {
            Vector3 to = rig.root.InverseTransformPoint(near.position + Vector3.up * 1.3f) - rig.root.InverseTransformPoint(rig.head.position);
            if (to.sqrMagnitude > 0.01f)
            {
                wantYaw = Mathf.Clamp(Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg, -70f, 70f);
                wantPitch = Mathf.Clamp(-Mathf.Atan2(to.y, new Vector2(to.x, to.z).magnitude) * Mathf.Rad2Deg, -20f, 20f);
            }
        }
        headYaw = PuppetKit.DampAngle(headYaw, wantYaw, 5f, dt);
        headPitch = PuppetKit.DampAngle(headPitch, wantPitch, 5f, dt);

        float g = -1f;
        if (gestureT >= 0f)
        {
            gestureT += dt;
            g = gestureT / gestureLen;
            if (g >= 1f) { gestureT = -1f; gesture = -1; g = -1f; }
        }
        float swing = g >= 0f ? Mathf.Sin(g * Mathf.PI) : 0f;       // 0 → 1 → 0
        float wave = g >= 0f ? Mathf.Sin(g * Mathf.PI * 2f) : 0f;   // side to side

        rig.Body(new Vector3(0f, 0.004f * breathe, 0f), Quaternion.identity, 0.006f * breathe);
        switch (Role)
        {
            case CasinoRole.Bartender:
                rig.Legs(0f, 0f, 3f);
                if (gesture == 10) // pour: the right hand up and tipped, the left steadies the glass
                {
                    rig.Torso(6f * swing, 0f, -8f * swing);
                    rig.Arms(-55f * swing - 20f, 14f, -110f * swing - 20f, 30f - 40f * swing);
                }
                else if (gesture == 11) // serve: slide it across
                {
                    rig.Torso(14f * swing, 0f);
                    rig.Arms(-20f, 12f, -35f - 55f * swing, 6f);
                }
                else // polishing a glass, leaning now and then
                {
                    float lean = Mathf.Max(0f, s2) * 6f;
                    rig.Torso(4f + lean, 1.5f * s1);
                    float rub = Mathf.Sin(t * 6.5f + idleSeed) * 7f;
                    rig.Arms(-52f + rub, 26f, -50f - rub, 24f, 18f, -18f);
                }
                break;

            case CasinoRole.BlackjackDealer:
            case CasinoRole.RouletteDealer:
                rig.Legs(0f, 0f, 2f);
                rig.Torso(11f + 3f * swing, 1.2f * s1);
                if (gesture == (int)DealerGesture.Deal) rig.Arms(-40f, 10f, -48f - 38f * swing, 8f + 22f * wave);
                else if (gesture == (int)DealerGesture.Spin) rig.Arms(-40f, 10f, -60f - 30f * swing, 30f * Mathf.Sin(g * Mathf.PI * 3f));
                else if (gesture == (int)DealerGesture.Pay) rig.Arms(-48f - 34f * swing, 6f, -42f, 10f);
                else if (gesture == (int)DealerGesture.Sweep) rig.Arms(-62f, 18f + 34f * wave, -62f, 18f - 34f * wave);
                else rig.Arms(-60f + 1.5f * s1, 16f, -60f - 1.5f * s1, 16f); // hands resting on the felt
                break;

            default:
                switch (post.pose)
                {
                    case CasinoRegularPose.AsleepOnBar:
                        // face down on folded arms; the back rises and falls
                        SeatLegs(84f, 80f, 7f, 14f);
                        rig.Torso(58f + 2.5f * breathe, 4f);
                        rig.Arms(-118f, 34f, -112f, 30f, 30f, -30f);
                        wantYaw = 28f; headYaw = 28f; headPitch = 24f;
                        break;
                    case CasinoRegularPose.SlotPlayer:
                        // perched at a machine, one hand on the lever side, pulling now and then
                        SeatLegs(84f, 86f, 6f, 8f);
                        float pull = Mathf.Max(0f, Mathf.Sin(t * 0.9f + idleSeed)); pull = pull * pull * pull;
                        rig.Torso(10f + 4f * pull, 2f * s1);
                        rig.Arms(-55f, 12f, -60f - 45f * pull, 30f);
                        break;
                    case CasinoRegularPose.SlotStander:
                    {
                        // standing at a machine: weight shifting, a hand on the cabinet; pulls, cheers and shrugs on cue; strides when Walking
                        if (Walking)
                        {
                            float ph = t * 8f, sw = Mathf.Sin(ph) * 30f;
                            rig.Legs(sw, -sw, 3f);
                            rig.Torso(5f, 1.5f * Mathf.Sin(ph * 0.5f));
                            rig.Arms(-sw * 0.8f, 8f, sw * 0.8f, 8f);
                            rig.Body(new Vector3(0f, 0.014f * Mathf.Abs(Mathf.Sin(ph)), 0f), Quaternion.identity, 0.006f * breathe);
                        }
                        else if (gesture == 21)
                        {
                            rig.Legs(0f, 0f, 5f);
                            rig.Torso(-4f, 0f);
                            rig.Arms(-150f + 12f * wave, 22f, -150f - 12f * wave, 22f);
                            rig.Body(new Vector3(0f, 0.035f * Mathf.Abs(Mathf.Sin(g * Mathf.PI * 3f)), 0f), Quaternion.identity, 0.006f * breathe);
                        }
                        else if (gesture == 22)
                        {
                            rig.Legs(1f, -1f, 4f);
                            rig.Torso(2f, 0f);
                            rig.Arms(-22f, 10f + 30f * swing, -22f, 10f + 30f * swing);
                            headPitch += 8f * swing * dt * 8f; headYaw += 10f * Mathf.Sin(g * Mathf.PI * 2f) * dt * 8f;
                        }
                        else
                        {
                            rig.Legs(2f * s1, -2f * s1, 4f);
                            float pulls = gesture == 20 ? swing : 0f;
                            rig.Torso(8f + 4f * s2 + 8f * pulls, 2f * s1 - 4f * pulls);
                            rig.Arms(-40f, 14f, -52f - 58f * pulls, 22f - 8f * pulls);
                        }
                        break;
                    }
                    case CasinoRegularPose.Leaning:
                        rig.Legs(4f, -6f, 4f);
                        rig.Torso(-4f, 5f + 1.5f * s1);
                        rig.Arms(6f, 12f, -62f - 16f * Mathf.Max(0f, s2), 12f);
                        break;
                    default: // BarStool, TableSitter: nursing a drink
                        SeatLegs(85f, 82f, 7f, post.pose == CasinoRegularPose.BarStool ? 16f : 2f);
                        float sip = Mathf.Max(0f, Mathf.Sin(t * 0.33f + idleSeed)); sip = Mathf.Pow(sip, 6f);
                        rig.Torso(12f - 8f * sip, 2f * s1);
                        rig.Arms(-58f, 22f, -62f - 62f * sip, 14f - 10f * sip, 14f, -10f - 20f * sip);
                        headPitch -= 14f * sip * dt * 8f;
                        break;
                }
                break;
        }
        rig.Head(headPitch, headYaw, asleep ? 12f : 0f);

        if (asleep) rig.SetMouth(0.25f + 0.15f * breathe, 0.8f);
        else if (talk > 0f) { talk -= dt; rig.SetMouth(0.35f + 0.3f * Mathf.Sin(t * 22f), 1f); }
        else rig.SetMouth(0.05f, 1.1f);
        rig.TickEyes(dt, ref dice);

        // barks: only with somebody close, one per person every 14-30 s, one in the room every 5 s (the caller)
        if (!Application.isPlaying || !mayBark || near == null || t < nextLine) return false;
        float dist = (near.position - Position).magnitude;
        if (dist > (asleep ? 3f : 4.2f)) return false;
        nextLine = t + dice.Range(14f, 30f);
        string[] lines = asleep ? SleepLines : Role == CasinoRole.Bartender ? BartenderLines : Role == CasinoRole.BlackjackDealer ? BlackjackLines
            : Role == CasinoRole.RouletteDealer ? RouletteLines : RegularLines;
        if (asleep)
        {
            Vector3 top = rig.head.position + Vector3.up * 0.45f;
            GameHUD.FloatingText(top, lines[dice.Range(0, lines.Length)], ResidentLines.Chat, 0.8f);
            Sfx.Play("snore", top, 0.35f, 0.9f);
        }
        else Say(lines[dice.Range(0, lines.Length)]);
        return true;
    }
}
