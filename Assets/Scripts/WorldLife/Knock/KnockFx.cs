using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Package B presentation knobs (local cosmetics only; the host-validated numbers — thresholds, speed loss, timers — are
/// W0's <see cref="WorldLifeTuning.Props"/> and <see cref="WorldLifeTuning.Knock"/>). G tunes here.
/// </summary>
public static class KnockTuning
{
    /// <summary>The sensor box reaches this far below the truck's body box (short props: cones, flamingos).</summary>
    public const float SweepExtendDown = 0.4f;
    /// <summary>Debris is pushed out of the truck's path: (SidePush + SidePushPerSpeed·|v|) × 0.6–1.4 (seeded).</summary>
    public static float SidePush = 2f, SidePushPerSpeed = 0.2f;
    /// <summary>Seeded tumble (rad/s) and the sign propeller spin.</summary>
    public static float SpinMin = 3.5f, SpinMax = 10f, PropellerSpin = 22f;
    /// <summary>A proxy counts as at rest below these speeds (m/s, rad/s).</summary>
    public static float SettleLinear = 0.2f, SettleAngular = 0.4f;
    /// <summary>A settled (kinematic) proxy the truck drives into at ≥ RekickMinSpeed tumbles again.</summary>
    public static float RekickMinSpeed = 1.5f, RekickVelMult = 0.85f, RekickUp = 1.2f, RekickInterval = 0.5f;
    /// <summary>NPC cars bat debris aside (fixer pass): how far (m) beyond the car's box a proxy's pivot may stand and still be
    /// checked (a lamp post lying down reaches ~6 m from its base).</summary>
    public static float CarBatSlack = 6.5f;
    /// <summary>Over the awake cap, the slowest awake proxy freezes if it's under this speed, else it poofs.</summary>
    public static float CapFreezeMaxSpeed = 1f;
    /// <summary>Reset bookkeeping cadence (s) and the clearances that delay a reset (a truck parked on the spot, a player
    /// standing in it), so a re-enabled rest collider never shoves anybody.</summary>
    public const float ResetCheckInterval = 0.25f, ResetTruckClearance = 6f, ResetPlayerClearance = 1.5f;
    public const float ShrinkSeconds = 0.3f;
    /// <summary>Proxies falling below this are parked (the prop stays down until its reset).</summary>
    public const float KillY = -30f;
    /// <summary>Debris (lemons, the hoop's ball): lifetime and cap.</summary>
    public const float DebrisSeconds = 7f;
    public const int MaxDebris = 16, Lemons = 5;
    /// <summary>A knocked cone sticks to the bumper with odds 1 in ConeStickOdds (seeded).</summary>
    public const int ConeStickOdds = 3;
    /// <summary>Hydrant geyser: column radius, launch speed for bags / items / debris (8 m/s ≈ 3 m up), critter launch,
    /// per-object relaunch cooldown, max concurrent geysers.</summary>
    public static float GeyserRadius = 0.85f, GeyserLaunchUp = 8.5f, GeyserCritterUp = 8f, GeyserCooldown = 0.6f, GeyserPlayerCooldown = 1.6f;
    public const int MaxGeysers = 6;
    /// <summary>Lamp stump sparks: fizz this long after the knock.</summary>
    public const float SparkSeconds = 1.6f;
}

/// <summary>Sfx ids WorldLife wants (§11 Feel row) with a known fallback until Feel adds the recipe (no "blip").</summary>
public static class KnockSfx
{
    static readonly Dictionary<string, string> resolved = new Dictionary<string, string>();
    static HashSet<string> known;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { resolved.Clear(); known = null; }

    /// <summary><paramref name="wanted"/> if Feel can play it (a synth recipe or an "sfx/&lt;id&gt;" clip), else the fallback.</summary>
    public static string Pick(string wanted, string fallback)
    {
        if (resolved.TryGetValue(wanted, out var id)) return id;
        if (known == null) known = new HashSet<string>(FeelSfxSynth.KnownIds);
        id = known.Contains(wanted) || RubbishAssets.Clip("sfx/" + wanted) != null ? wanted : fallback;
        resolved[wanted] = id;
        return id;
    }

    public static void Play(string wanted, string fallback, Vector3 pos, float volume = 1f, float pitch = 1f) =>
        Sfx.Play(Pick(wanted, fallback), pos, volume, pitch);
}

/// <summary>
/// The juice of a knock and a reset (§6.1 "Extra" column): sounds, bursts, floaters (one per 0.4 s), the lamp's bulb pop and
/// stump sparks, letter confetti, lemons, the hoop's ball, laundry, the cone on the bumper and the hydrant geyser. Local
/// presentation on every machine; nothing here pays or hurts. Owned by <see cref="KnockableDirector"/>.
/// </summary>
public sealed class KnockFx
{
    readonly KnockableDirector dir;
    double lastFloater = -99;

    struct Fizz { public Vector3 at; public double until, next; }
    readonly Fizz[] fizz = new Fizz[8];

    static readonly Color[] WordColor =
    {
        new Color(1f, 0.85f, 0.25f),  // Lamp
        new Color(0.95f, 0.95f, 0.9f),// Mailbox
        new Color(0.55f, 0.9f, 0.45f),// WheelieBin
        new Color(0.5f, 0.85f, 1f),   // Sign
        new Color(1f, 1f, 1f),        // PicketPanel
        new Color(1f, 0.9f, 0.2f),    // LemonadeStand
        new Color(1f, 0.55f, 0.15f),  // BasketballHoop
        new Color(0.8f, 0.7f, 1f),    // DryingRack
        new Color(0.8f, 0.6f, 0.4f),  // ParkBench
        new Color(0.7f, 0.7f, 0.7f),  // ParkBin
        new Color(0.45f, 0.75f, 1f),  // Hydrant
        new Color(1f, 0.5f, 0.1f),    // Cone
        new Color(1f, 0.6f, 0.2f),    // Sawhorse
        new Color(1f, 0.45f, 0.75f),  // Flamingo
    };
    static readonly Color Sparks = new Color(1f, 0.72f, 0.2f), Bulb = new Color(1f, 0.97f, 0.75f), Moth = new Color(0.85f, 0.8f, 0.65f),
        Paper = new Color(0.98f, 0.97f, 0.9f), Wood = new Color(0.6f, 0.45f, 0.3f), Lemon = new Color(1f, 0.9f, 0.2f),
        Water = new Color(0.55f, 0.8f, 1f), Pink = new Color(1f, 0.5f, 0.75f);
    static readonly Color[] Laundry = { new Color(0.6f, 0.8f, 1f), new Color(1f, 0.7f, 0.75f), new Color(0.75f, 1f, 0.7f) };

    Mesh lemonMesh, ballMesh;
    Material lemonMat, ballMat;
    bool debrisResolved;

    public KnockFx(KnockableDirector dir) { this.dir = dir; }

    public static Color ColorFor(KnockKind k) => WordColor[Mathf.Clamp((int)k, 0, WordColor.Length - 1)];

    /// <summary>A prop just went down on this machine.</summary>
    public void Knocked(Knockable k, KnockableDirector.Down d, KnockProxy p, Vector3 launch, float speed, ulong hash, TruckControllerV2 truck, double now)
    {
        Vector3 at = d.pos;
        Vector3 top = d.top;
        float loud = Mathf.Clamp01(0.55f + speed * 0.05f);
        Fx.Dust(at + Vector3.up * 0.1f, 0.5f + k.Spec.massKg * 0.01f);
        if (now - lastFloater >= WorldLifeTuning.Props.FloaterInterval)
        {
            lastFloater = now;
            GameHUD.FloatingText(top + Vector3.up * 0.6f, k.Spec.word, ColorFor(k.kind), 1.1f);
        }
        switch (k.kind)
        {
            case KnockKind.Lamp:
                KnockSfx.Play(WorldLifeIds.Sfx.Clang, "slam", at + Vector3.up, loud, 1f);
                KnockSfx.Play(WorldLifeIds.Sfx.Sparks, "buzz", at, 0.4f, 1f);
                Sfx.Play("pop", top, 0.7f, 1.2f);
                Fx.Burst(top, Bulb, 10, 3.5f);                     // the bulb pops
                Fx.Burst(at + Vector3.up * 0.3f, Sparks, 14, 4.5f); // sparks at the stump
                StartFizz(at + Vector3.up * 0.25f, now);
                if (StreetLampsLit()) Fx.Burst(top, Moth, 5, 2f);  // moths flee
                break;
            case KnockKind.Mailbox:
                Sfx.Play("crunch", at, loud, 1.3f);
                Sfx.Play("rustle", top, 0.6f, 1.2f);
                Fx.Burst(top, Paper, 18, 4f);                       // letter confetti
                Fx.Burst(top, new Color(0.9f, 0.85f, 0.7f), 8, 3f);
                break;
            case KnockKind.WheelieBin:
                Sfx.Play("thud", at, loud, 0.95f);
                Sfx.Play("slam", at, 0.6f, 1.25f);
                Fx.Burst(top, new Color(0.35f, 0.5f, 0.35f), 6, 3f);
                break;
            case KnockKind.Sign:
                Sfx.Play("squeak", top, loud, 0.6f);   // BWOING
                Sfx.Play("slam", at, 0.5f, 1.6f);
                break;
            case KnockKind.PicketPanel:
                Sfx.Play("crunch", at, loud, 1.15f);
                Fx.Burst(at + Vector3.up * 0.5f, Color.white, 16, 4f);   // planks
                break;
            case KnockKind.LemonadeStand:
                Sfx.Play("crunch", at, loud, 1f);
                Sfx.Play("splat", at, 0.7f, 1.3f);
                Fx.Burst(top, Lemon, 12, 4f);
                SpawnLemons(at + Vector3.up * 1.1f, launch, hash);
                break;
            case KnockKind.BasketballHoop:
                Sfx.Play("slam", top, loud, 0.8f);
                SpawnBall(top, launch, hash);
                break;
            case KnockKind.DryingRack:
                Sfx.Play("rustle", at, loud, 0.9f);
                Sfx.Play("whoosh", top, 0.6f, 1.1f);
                for (int i = 0; i < Laundry.Length; i++) Fx.Burst(top, Laundry[i], 6, 3f);
                break;
            case KnockKind.ParkBench:
                Sfx.Play("crunch", at, loud, 0.8f);
                Sfx.Play("thud", at, 0.7f, 0.85f);
                Fx.Burst(at + Vector3.up * 0.4f, Wood, 10, 3.5f);
                break;
            case KnockKind.ParkBin:
                Sfx.Play("thud", at, loud, 1.05f);
                Fx.Burst(top, new Color(0.5f, 0.5f, 0.5f), 6, 3f);
                break;
            case KnockKind.Hydrant:
                KnockSfx.Play(WorldLifeIds.Sfx.Clang, "slam", at, loud, 0.8f);
                KnockSfx.Play(WorldLifeIds.Sfx.Geyser, "hiss", at, 0.8f, 1.1f);
                Sfx.Play("splat", at, 0.8f, 0.8f);
                Fx.Burst(at + Vector3.up * 0.5f, Water, 18, 5f);
                dir.Geysers.Start(at, now);
                break;
            case KnockKind.Cone:
                Sfx.Play("pop", at, loud, 0.75f);
                Sfx.Play("squeak", at, 0.5f, 1.4f);
                if (truck != null && LifeHash.Chance(hash >> 5, 1, KnockTuning.ConeStickOdds)) dir.StickToBumper(p, truck, hash, now);
                break;
            case KnockKind.Sawhorse:
                Sfx.Play("crunch", at, loud, 1.4f);
                Fx.Burst(top, new Color(1f, 0.55f, 0.15f), 8, 3.5f);
                Fx.Burst(top, Color.white, 6, 3.5f);
                break;
            case KnockKind.Flamingo:
                Sfx.Play("squeak", top, loud, 1.9f);   // SQUAWK
                Fx.Burst(top, Pink, 12, 3f);            // feathers
                break;
        }
    }

    /// <summary>A prop came back (the original pops in where it stood; the proxy shrank away with its own poof).</summary>
    public void Reset(KnockableDirector.Down d, bool byCityWorks)
    {
        Fx.Dust(d.pos + Vector3.up * 0.1f, 0.7f);
        Fx.Burst(d.pos + Vector3.up * 0.4f, byCityWorks ? new Color(1f, 0.8f, 0.2f) : new Color(0.9f, 0.95f, 1f), 8, 2.5f);
        Sfx.Play("pop", d.pos + Vector3.up * 0.5f, 0.55f, 1.15f);
    }

    public void Tick(double now)
    {
        for (int i = 0; i < fizz.Length; i++)
        {
            ref var f = ref fizz[i];
            if (f.until <= now || now < f.next) continue;
            f.next = now + 0.16;
            Fx.Burst(f.at, Sparks, 3, 3f);
        }
    }

    public void Clear() { for (int i = 0; i < fizz.Length; i++) fizz[i] = default; }

    void StartFizz(Vector3 at, double now)
    {
        int slot = 0;
        for (int i = 0; i < fizz.Length; i++) if (fizz[i].until < fizz[slot].until) slot = i;
        fizz[slot] = new Fizz { at = at, until = now + KnockTuning.SparkSeconds, next = now + 0.1 };
    }

    static bool StreetLampsLit() => GameClock.IsNight;

    void ResolveDebris()
    {
        if (debrisResolved) return;
        debrisResolved = true;
        var lemon = KnockShape.PrefabFor(WorldLifeIds.KnockPrefabPrefix + "SM_lemon");
        if (lemon != null && lemon.TryGetComponent<MeshFilter>(out var mf) && lemon.TryGetComponent<MeshRenderer>(out var mr))
        { lemonMesh = mf.sharedMesh; lemonMat = mr.sharedMaterial; }
        if (lemonMesh == null) { lemonMesh = KnockRecipes.MeshOf(KnockRecipes.Prim.Sphere); lemonMat = KnockRecipes.MaterialOf(KnockRecipes.MatYellow); }
        ballMesh = KnockRecipes.MeshOf(KnockRecipes.Prim.Sphere);
        ballMat = KnockRecipes.MaterialOf(KnockRecipes.MatOrange);
    }

    void SpawnLemons(Vector3 at, Vector3 launch, ulong hash)
    {
        ResolveDebris();
        ulong st = hash;
        for (int i = 0; i < KnockTuning.Lemons; i++)
        {
            float a = LifeHash.Float01(LifeHash.Next(ref st)) * Mathf.PI * 2f;
            Vector3 v = launch * 0.6f + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * (2f + 2f * LifeHash.Float01(LifeHash.Next(ref st))) + Vector3.up * 2.5f;
            dir.SpawnDebris(lemonMesh, lemonMat, at + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * 0.2f, 0.13f, 0.15f, v);
        }
    }

    void SpawnBall(Vector3 at, Vector3 launch, ulong hash)
    {
        ResolveDebris();
        float side = (hash & 1) == 0 ? 1f : -1f;
        Vector3 v = launch * 0.8f + Vector3.Cross(Vector3.up, launch.normalized) * side * 2.5f + Vector3.up * 2f;
        dir.SpawnDebris(ballMesh, ballMat, at, 0.24f, 0.6f, v);
    }
}
