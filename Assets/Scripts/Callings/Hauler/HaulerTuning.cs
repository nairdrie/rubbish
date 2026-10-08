using UnityEngine;

// HAULER ("you are the truck", Docs/Progression.md §6.9 stage 2): Grabby Hand 2-3, Rocket Blades, Compactor Backpack and
// the One-Man Route. EVERY number of the calling lives in HaulerTuning; HaulerRules holds the pure rules built on them
// (no scene, no session: HaulerTests calls them by reflection).

/// <summary>The one place to retune the Hauler calling.</summary>
public static class HaulerTuning
{
    // ------------------------------------------------------------------ GRABBY HAND (grabber_pole 1..3)
    /// <summary>Bag reach in metres by level (index 0 is never used: no pole, no reach). Poke and snatch ranges do not grow:
    /// a longer arm must not become a longer way to annoy your neighbours.</summary>
    public static readonly float[] PoleReach = { 0f, 4.5f, 6.5f, 8.5f };
    /// <summary>From level 2 a grabbed bag is reeled in on a tighter spring for a moment (the YANK).</summary>
    public const int YankLevel = 2;
    public const float YankStiffnessMult = 2.6f, YankSeconds = 0.5f;
    /// <summary>Level 3 squeezes a second bag: it must rest within this many metres of the first.</summary>
    public const int TwoBagLevel = 3;
    public const float TwoBagRadius = 2.4f;
    /// <summary>Host: the second bag must be asked for within this many seconds of the first grab.</summary>
    public const float TwoBagWindow = 0.8f;

    // ------------------------------------------------------------------ ROCKET BLADES (calling_rocket_blades 1..3)
    public const KeyCode BoostKey = KeyCode.Q;
    /// <summary>Skating top speed as a multiple of your sprint speed (which already carries boots, juice, carry weight and
    /// the pack). Stock sprint is 5.3 m/s, so skates alone are 6.5: just under a yard dog (6.6-7.2 m/s).</summary>
    public const float SkateTopMult = 1.22f;
    /// <summary>m/s² pushing up to top speed (a walk reaches its speed almost at once; skates take ~1.3 s).</summary>
    public const float SkateAccel = 4.6f;
    /// <summary>m/s² lost while coasting with no stick: about 5 s of glide from top speed.</summary>
    public const float GlideDecel = 1.25f;
    /// <summary>m/s² lost on the toe stop (stick held, sprint released) and when over the current top speed.</summary>
    public const float BrakeDecel = 8.5f, OverspeedDecel = 3.2f;
    /// <summary>How fast the direction of travel can swing toward the stick (deg/s): nimble when slow, wide when fast.</summary>
    public const float CarveSlowDegPerSec = 540f, CarveFastDegPerSec = 105f, CarveFastSpeed = 9f;
    /// <summary>You start rolling above this speed with sprint held, and step off the wheels below <see cref="SkateStopSpeed"/>.</summary>
    public const float SkateStartSpeed = 2.2f, SkateStopSpeed = 0.9f;

    public const int BoostLevel = 2, AfterburnerLevel = 3;
    /// <summary>Level 2 boost: +m/s on top speed for <see cref="BoostSeconds"/>, an instant kick, then a cooldown. 6.5 + 4.8 =
    /// 11.3 m/s, comfortably faster than a dog and faster than a stock truck (10 m/s) for a second and a half.</summary>
    public const float BoostAdd = 4.8f, BoostKick = 3f, BoostAccel = 14f, BoostSeconds = 1.6f, BoostCooldown = 6f;
    /// <summary>Level 3 afterburner: hold to keep boosting while the heat meter fills (0..1). Full = flame-out.</summary>
    public const float HeatPerSecond = 0.27f, CoolPerSecond = 0.17f, OverheatLockSeconds = 4f;
    /// <summary>Hit something this fast (m/s) and this squarely (1 = dead on) and you wipe out. Skates alone never reach it.</summary>
    public const float WipeoutSpeed = 8.2f, WipeoutHeadOn = 0.55f;
    /// <summary>Level 3: a glancing wall or kerb hit above this speed becomes a grind (no speed lost, heat bleeds off).</summary>
    public const float GrindMinSpeed = 4.5f, GrindMinHeadOn = 0.08f, GrindCoolPerSecond = 0.35f;

    // ------------------------------------------------------------------ COMPACTOR BACKPACK (calling_compactor_backpack 1..3)
    public const KeyCode PackKey = KeyCode.C, EjectKey = KeyCode.B;
    /// <summary>Bags on your back by pack level. Work Gloves 2 / 3 still add their old Back Sack slots (1 / 2) on top.</summary>
    public static readonly int[] PackSlots = { 0, 2, 4, 6 };
    public static readonly int[] GloveSlots = { 0, 0, 1, 2 };
    public const int CrushLevel = 2, BaleLevel = 3;
    /// <summary>A slot is two halves: a plain bag fills both, a crushed bag one (so crushing doubles what fits).</summary>
    public const int PlainHalves = 2, CrushedHalves = 1;
    /// <summary>A crushed bag only weighs this much of itself for the slowdown (the real kilograms are untouched).</summary>
    public const float CrushedWeightMult = 0.5f;
    /// <summary>Pack slowdown: up to <see cref="PackMaxSlow"/> of your speed at <see cref="PackSlowKg"/> effective kg (a held
    /// bag slows up to 45% at 40 kg, so the back is the kinder place to carry). Gloves scale it like any carry.</summary>
    public const float PackSlowKg = 60f, PackMaxSlow = 0.22f;
    /// <summary>Hold the pack key this long to start crushing; then one bag per <see cref="CrushSecondsPerBag"/>.</summary>
    public const float CrushHoldSeconds = 0.35f, CrushSecondsPerBag = 0.55f;
    /// <summary>Seconds between bags when unloading into a hopper or the hatch, by pack level (level 3 is the bale).</summary>
    public static readonly float[] UnloadInterval = { 0.32f, 0.32f, 0.24f, 0.07f };

    // ------------------------------------------------------------------ ONE-MAN ROUTE (calling_one_man_route)
    /// <summary>
    /// The foot-hauler bonus: extra pay on a bag's pickup pay (fee x lateness + tip; never the curb-clean bonus) when its
    /// owner walks it into their own walk-up hatch. Why 25%: a full pack plus hands is 12 bags a trip at ~7 m/s against a
    /// starter truck's ~27 bags (160 kg) at 10-17 m/s, so per round trip a foot hauler moves roughly half of what a truck
    /// does. +25% on $4 / $15 / $55 / $120 bags lifts that to about 60-65% of a truck's pickup income: a real living with
    /// no truck, never more than the truck pays. The hatch also skips the TIP IT step, which is the rest of the perk.
    /// </summary>
    public const float FootBonusPct = 0.25f;
    /// <summary>Where the hatch stands, in the lot's street frame (door-local: +X across the shop front, +Z to the road).
    /// Left of the bay door, clear of the truck poses (x 0), the doorbell (x 4.9), the sign (x 8.3) and the spawn apron.</summary>
    public static readonly Vector3 HatchLocal = new Vector3(-5.6f, 0f, 0.85f);
    public const float HatchTossRange = 3.2f;
    /// <summary>Host: how far from a receiver a backpack unload may be asked for (toss range + lag).</summary>
    public const float UnloadRange = 6f;

    // ------------------------------------------------------------------ cosmetic relay kinds (BagTellsNet.Send)
    // High numbers on purpose: BagTellsNet's own kinds are 1..8 and other callings may add theirs.
    public const byte FxBoost = 71, FxBoostEnd = 72, FxWipeout = 73, FxGrind = 74;
}

/// <summary>Pure Hauler rules (reach, pack room and weight, the boost and heat machine, skating, foot pay).</summary>
public static class HaulerRules
{
    static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

    // ------------------------------------------------------------------ Grabby Hand
    /// <summary>Bag reach (m) of a Grabby Hand at this level; 0 without one. Levels above 3 clamp.</summary>
    public static float PoleReach(int level) => level <= 0 ? 0f : HaulerTuning.PoleReach[Clamp(level, 1, HaulerTuning.PoleReach.Length - 1)];
    public static bool PoleYanks(int level) => level >= HaulerTuning.YankLevel;
    public static bool PoleGrabsTwo(int level) => level >= HaulerTuning.TwoBagLevel;

    // ------------------------------------------------------------------ Compactor Backpack
    /// <summary>Back slots: the pack's (0/2/4/6) plus the gloves' old Back Sack slots (0/0/1/2).</summary>
    public static int PackSlots(int packLevel, int glovesLevel) =>
        HaulerTuning.PackSlots[Clamp(packLevel, 0, HaulerTuning.PackSlots.Length - 1)] +
        HaulerTuning.GloveSlots[Clamp(glovesLevel, 0, HaulerTuning.GloveSlots.Length - 1)];

    public static int PackHalvesUsed(int plain, int crushed) =>
        System.Math.Max(0, plain) * HaulerTuning.PlainHalves + System.Math.Max(0, crushed) * HaulerTuning.CrushedHalves;
    /// <summary>Does one more PLAIN bag fit (bags always go in plain; crushing happens on your back)?</summary>
    public static bool PackHasRoom(int slots, int plain, int crushed) =>
        slots > 0 && PackHalvesUsed(plain, crushed) + HaulerTuning.PlainHalves <= slots * HaulerTuning.PlainHalves;
    /// <summary>The most bags that can ride at once: with a crusher, every bag but the last one in is crushed (a bag goes in
    /// plain and needs a whole free slot), so 4 slots carry 7 and 6 slots carry 11.</summary>
    public static int PackMaxBags(int slots, bool canCrush) =>
        slots <= 0 ? 0 : canCrush ? (slots * HaulerTuning.PlainHalves - HaulerTuning.PlainHalves) / HaulerTuning.CrushedHalves + 1 : slots;
    public static bool PackCanCrush(int packLevel) => packLevel >= HaulerTuning.CrushLevel;
    public static bool PackEjectsBale(int packLevel) => packLevel >= HaulerTuning.BaleLevel;
    /// <summary>
    /// THE CRUSH RULE: crushing squeezes the sack, never its contents. A crushed bag keeps every item (valuables included),
    /// its bag id and its curb record, so it pays, counts for Rep and sorts exactly as before; it only takes half a slot and
    /// weighs half as much on your back, and it puffs back up when it leaves the pack. Heavy (two-person) bags and loose
    /// piles never go in the pack at all.
    /// </summary>
    public const bool CrushKeepsContents = true;

    /// <summary>Kilograms the pack slows you by: plain bags in full, crushed bags at half.</summary>
    public static float PackCarryKg(float plainKg, float crushedKg) =>
        Mathf.Max(0f, plainKg) + Mathf.Max(0f, crushedKg) * HaulerTuning.CrushedWeightMult;
    /// <summary>Walk / sprint / skate multiplier for a loaded pack. <paramref name="glovesCarryMult"/> is the gloves' carry
    /// factor (1, 0.5 or 0: UpgradeEffects.CarrySlowdownMult), so Gorilla Grip carries a full pack for free.</summary>
    public static float PackSpeedMult(float carryKg, float glovesCarryMult) =>
        1f - Mathf.Clamp01(carryKg / HaulerTuning.PackSlowKg) * HaulerTuning.PackMaxSlow * Mathf.Clamp01(glovesCarryMult);
    public static float UnloadInterval(int packLevel) =>
        HaulerTuning.UnloadInterval[Clamp(packLevel, 0, HaulerTuning.UnloadInterval.Length - 1)];

    // ------------------------------------------------------------------ One-Man Route
    /// <summary>The foot bonus in cents for a bag whose pickup pay was <paramref name="pickupPayCents"/> (never negative).</summary>
    public static long FootBonusCents(long pickupPayCents) =>
        pickupPayCents <= 0 ? 0 : (long)System.Math.Round(pickupPayCents * (double)HaulerTuning.FootBonusPct, System.MidpointRounding.AwayFromZero);
    /// <summary>What a bag earns at the walk-up hatch: exactly what a truck's hopper pays for it, plus the foot bonus.</summary>
    public static long FootDeliveryCents(long hopperPayCents) => hopperPayCents + FootBonusCents(hopperPayCents);

    // ------------------------------------------------------------------ Rocket Blades: boost and heat
    public struct Boost
    {
        /// <summary>0..1 (level 3 only).</summary>
        public float heat;
        /// <summary>Seconds of a level-2 burst left; seconds until the next one; seconds locked out after a flame-out.</summary>
        public float burstLeft, cooldownLeft, lockLeft;
        /// <summary>The rockets are lit this tick.</summary>
        public bool active;
        /// <summary>One-tick events: the rockets just lit, just went out, or just flamed out (overheat).</summary>
        public bool started, ended, overheated;
        public bool Ready(int level) => level >= HaulerTuning.BoostLevel && !active && lockLeft <= 0f &&
            (level >= HaulerTuning.AfterburnerLevel ? heat < 0.95f : cooldownLeft <= 0f);
    }

    /// <summary>
    /// One tick of the boost. Level 2: a press lights a fixed burst, then a cooldown. Level 3: lit for as long as the key is
    /// held, heat rises while lit and falls otherwise; full heat is a flame-out and a lock-out. <paramref name="allowed"/>
    /// false (driving, indoors, flopped...) puts the rockets out but still cools them.
    /// </summary>
    public static Boost BoostTick(Boost s, int level, bool pressed, bool held, bool allowed, float dt)
    {
        bool was = s.active;
        s.started = s.ended = s.overheated = false;
        dt = Mathf.Max(0f, dt);
        s.cooldownLeft = Mathf.Max(0f, s.cooldownLeft - dt);
        s.lockLeft = Mathf.Max(0f, s.lockLeft - dt);
        if (level < HaulerTuning.BoostLevel || !allowed) { s.active = false; s.burstLeft = 0f; }
        else if (level >= HaulerTuning.AfterburnerLevel)
        {
            if (s.active) s.active = held && s.lockLeft <= 0f;
            else if (pressed && s.lockLeft <= 0f && s.heat < 0.95f) s.active = true;
            if (s.active)
            {
                s.heat += HaulerTuning.HeatPerSecond * dt;
                if (s.heat >= 1f) { s.heat = 1f; s.active = false; s.overheated = true; s.lockLeft = HaulerTuning.OverheatLockSeconds; }
            }
        }
        else
        {
            if (s.active)
            {
                s.burstLeft -= dt;
                if (s.burstLeft <= 0f) { s.active = false; s.burstLeft = 0f; }
            }
            else if (pressed && s.cooldownLeft <= 0f && s.lockLeft <= 0f)
            {
                s.active = true;
                s.burstLeft = HaulerTuning.BoostSeconds;
                s.cooldownLeft = HaulerTuning.BoostSeconds + HaulerTuning.BoostCooldown;
            }
        }
        if (!s.active) s.heat = Mathf.Max(0f, s.heat - HaulerTuning.CoolPerSecond * dt);
        s.started = s.active && !was;
        s.ended = !s.active && was;
        return s;
    }

    // ------------------------------------------------------------------ Rocket Blades: momentum
    public enum SkateMode { Glide = 0, Power = 1, Brake = 2 }

    /// <summary>Top speed on skates for a sprint speed (m/s), with the rockets lit or not.</summary>
    public static float SkateTop(float sprintSpeed, bool boosting) =>
        Mathf.Max(0f, sprintSpeed) * HaulerTuning.SkateTopMult + (boosting ? HaulerTuning.BoostAdd : 0f);

    /// <summary>
    /// One step of skating momentum (flat, y ignored). Power: carve toward <paramref name="wishDir"/> and push toward
    /// <paramref name="top"/>. Brake: toe stop, still steering. Glide: roll on, slowly losing speed. Above <paramref name="top"/>
    /// (a boost just ended) speed bleeds off gently instead of snapping back.
    /// </summary>
    public static Vector3 SkateStep(Vector3 vel, Vector3 wishDir, float top, SkateMode mode, bool boosting, float dt)
    {
        vel.y = 0f; wishDir.y = 0f;
        dt = Mathf.Max(0f, dt);
        float speed = vel.magnitude;
        bool steer = wishDir.sqrMagnitude > 1e-4f && mode != SkateMode.Glide;
        Vector3 dir = speed > 0.05f ? vel / speed : steer ? wishDir.normalized : Vector3.zero;
        if (steer)
        {
            wishDir.Normalize();
            float rate = Mathf.Lerp(HaulerTuning.CarveSlowDegPerSec, HaulerTuning.CarveFastDegPerSec, Mathf.Clamp01(speed / HaulerTuning.CarveFastSpeed));
            dir = speed < 0.6f ? wishDir : Vector3.RotateTowards(dir, wishDir, rate * Mathf.Deg2Rad * dt, 0f);
            dir.y = 0f;
            if (dir.sqrMagnitude > 1e-6f) dir.Normalize();
        }
        switch (mode)
        {
            case SkateMode.Power:
                if (speed < top) speed = Mathf.Min(top, speed + (boosting ? HaulerTuning.BoostAccel : HaulerTuning.SkateAccel) * dt);
                else speed = Mathf.Max(top, speed - HaulerTuning.OverspeedDecel * dt);
                break;
            case SkateMode.Brake:
                speed = Mathf.Max(0f, speed - HaulerTuning.BrakeDecel * dt);
                break;
            default:
                speed = Mathf.Max(0f, speed - HaulerTuning.GlideDecel * dt);
                break;
        }
        return dir * speed;
    }

    /// <summary>How squarely a move was blocked: 0 = went where it meant to, 1 = stopped dead. (Sliding along a wall at
    /// angle a from its normal loses cos a of the move.)</summary>
    public static float HeadOn(Vector3 intended, Vector3 actual)
    {
        intended.y = 0f; actual.y = 0f;
        float want = intended.magnitude;
        if (want < 1e-5f) return 0f;
        return Mathf.Clamp01((intended - actual).magnitude / want);
    }

    public enum Impact { None = 0, Bump = 1, Grind = 2, Wipeout = 3 }

    /// <summary>What hitting something does at this speed, squareness and blade level.</summary>
    public static Impact ImpactOf(float speed, float headOn, int level)
    {
        if (headOn < HaulerTuning.GrindMinHeadOn) return Impact.None;
        if (speed >= HaulerTuning.WipeoutSpeed && headOn >= HaulerTuning.WipeoutHeadOn) return Impact.Wipeout;
        if (level >= HaulerTuning.AfterburnerLevel && speed >= HaulerTuning.GrindMinSpeed && headOn < HaulerTuning.WipeoutHeadOn) return Impact.Grind;
        return Impact.Bump;
    }
}
