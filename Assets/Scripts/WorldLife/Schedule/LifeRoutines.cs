using System;
using UnityEngine;

/// <summary>What a resident is out doing (§7.1 routine templates). C picks props and animations from it.</summary>
public enum ExcursionKind : byte
{
    /// <summary>Door → car (gets in), or car → door after parking. Linked to a <see cref="LifeTrip"/>.</summary>
    Commute = 0,
    /// <summary>As Commute, for a midday errand or an evening outing by car.</summary>
    Errand = 1,
    DogWalk = 2,
    Jog = 3,
    /// <summary>Hand weights, 1.8 m/s (the Meadows Power Walker; Soccer Moms sometimes).</summary>
    PowerWalk = 4,
    Stroll = 5,
    /// <summary>To the park, a pause (feeding ducks), and back.</summary>
    Park = 6,
    /// <summary>Door → mailbox → door around 11:00.</summary>
    Mail = 7,
    /// <summary>Two neighbours meet at a property line, glance at you and whisper.</summary>
    Gossip = 8,
    /// <summary>Rare late dog walk (22:00–23:30).</summary>
    NightOwl = 9,
    /// <summary>The HOA Lady / Mr. Pemberton inspecting the block with a clipboard.</summary>
    Patrol = 10,
    /// <summary>A billionaire on a Segway along the promenade (2.4 m/s).</summary>
    Segway = 11,
}

/// <summary>How a segment moves (C: walk cycle, jog, standing idle, Segway lean).</summary>
public enum LifeGait : byte { Walk = 0, Jog = 1, Stand = 2, Segway = 3, Carry = 4 }

/// <summary>Why a car leaves home (§7.1 commute / errand).</summary>
public enum TripKind : byte { Commute = 0, Errand = 1, Outing = 2, DawnShift = 3, Late = 4 }

/// <summary>
/// The routine templates (§7.1) and the trip rules (§7.3) as data. Hours are in-game hours; chances are per resident (or
/// per car) per day in ‰. Tuned so the §4.3 population table holds to ±25% over 20 seeded days
/// (<c>Setup_WorldLifeTraffic.SelfTest</c> checks it). Changing a value changes every machine's town the same way.
/// </summary>
public static class LifeRoutines
{
    public struct Row
    {
        public ExcursionKind kind;
        public float from, to;     // start window (in-game hours)
        public int chance;         // ‰ per day
        public Row(ExcursionKind k, float f, float t, int c) { kind = k; from = f; to = t; chance = c; }
    }

    static Row R(ExcursionKind k, float f, float t, int c) => new Row(k, f, t, c);

    /// <summary>Per archetype (index = (int)ResidentArchetype, package C's enum). Rows are tried in order; one that overlaps something
    /// already planned that day is shifted later inside its window, or dropped.</summary>
    public static readonly Row[][] ByArchetype =
    {
        /* RobeDad     */ new[] { R(ExcursionKind.Mail, 7.0f, 9.5f, 700), R(ExcursionKind.Stroll, 9.0f, 18.0f, 200) },
        /* CurlerGran  */ new[] { R(ExcursionKind.Mail, 10.0f, 12.0f, 650), R(ExcursionKind.Stroll, 9.0f, 15.5f, 280), R(ExcursionKind.Park, 12.5f, 16.0f, 200) },
        /* Commuter    */ new[] { R(ExcursionKind.Stroll, 17.3f, 18.8f, 150), R(ExcursionKind.Mail, 17.0f, 19.0f, 250) },
        /* Jogger      */ new[] { R(ExcursionKind.Jog, 5.0f, 7.2f, 650), R(ExcursionKind.Jog, 16.0f, 18.0f, 300) },
        /* DogPerson   */ new[] { R(ExcursionKind.DogWalk, 5.8f, 8.6f, 720), R(ExcursionKind.DogWalk, 16.6f, 18.6f, 600), R(ExcursionKind.NightOwl, 22.0f, 23.0f, 70) },
        /* Gardener    */ new[] { R(ExcursionKind.Mail, 10.0f, 12.5f, 500), R(ExcursionKind.Park, 9.5f, 15.0f, 350), R(ExcursionKind.Stroll, 14.5f, 18.0f, 300) },
        /* SoccerMom   */ new[] { R(ExcursionKind.PowerWalk, 8.5f, 11.0f, 550), R(ExcursionKind.Mail, 11.0f, 13.0f, 300), R(ExcursionKind.Stroll, 16.0f, 18.3f, 250), R(ExcursionKind.Stroll, 19.3f, 21.0f, 60) },
        /* CargoDad    */ new[] { R(ExcursionKind.Mail, 7.0f, 9.0f, 400), R(ExcursionKind.Stroll, 10.0f, 15.0f, 500), R(ExcursionKind.Stroll, 16.5f, 18.3f, 200), R(ExcursionKind.Stroll, 19.5f, 21.0f, 80) },
        /* PowerWalker */ new[] { R(ExcursionKind.PowerWalk, 5.1f, 7.3f, 900), R(ExcursionKind.PowerWalk, 16.0f, 18.0f, 450) },
        /* HoaLady     */ new[] { R(ExcursionKind.Patrol, 9.0f, 11.0f, 900), R(ExcursionKind.Patrol, 14.0f, 17.5f, 800) },
        /* MonocleGent */ new[] { R(ExcursionKind.Stroll, 9.0f, 12.0f, 320), R(ExcursionKind.Mail, 11.0f, 13.0f, 300), R(ExcursionKind.Stroll, 17.0f, 19.0f, 350) },
        /* PearlLady   */ new[] { R(ExcursionKind.DogWalk, 7.0f, 9.0f, 600), R(ExcursionKind.DogWalk, 15.0f, 17.8f, 550) },
        /* TennisTwin  */ new[] { R(ExcursionKind.Jog, 6.3f, 8.3f, 450), R(ExcursionKind.Stroll, 16.3f, 18.3f, 350) },
        /* Pemberton   */ new[] { R(ExcursionKind.Patrol, 8.0f, 11.0f, 900), R(ExcursionKind.Patrol, 15.0f, 17.5f, 700) },
        /* Housekeeper */ new[] { R(ExcursionKind.Mail, 10.5f, 12.5f, 420), R(ExcursionKind.Stroll, 13.0f, 15.5f, 200) },
        /* Butler      */ new[] { R(ExcursionKind.Mail, 9.5f, 12.0f, 300), R(ExcursionKind.Stroll, 13.0f, 15.5f, 70) },
        /* Billionaire */ new[] { R(ExcursionKind.Segway, 6.6f, 8.4f, 480), R(ExcursionKind.Segway, 15.8f, 18.0f, 500), R(ExcursionKind.Segway, 19.5f, 21.0f, 120) },
        /* Bodyguard   */ new[] { R(ExcursionKind.Stroll, 9.0f, 17.5f, 60) },
    };

    /// <summary>Residents with a dog walk it instead of their first two walk rows (RobeDad / CargoDad / SoccerMom with a
    /// dog, the Billionaire's poodle rides the Segway): chance of a morning and an evening walk.</summary>
    public static Row ExtraDogMorning = new Row(ExcursionKind.DogWalk, 6.5f, 8.5f, 600);
    public static Row ExtraDogEvening = new Row(ExcursionKind.DogWalk, 17.5f, 19.8f, 550);

    /// <summary>Density by district (scales every chance; the §4.3 tuning knob).</summary>
    public static readonly float[] DistrictScale = { 1f, 1f, 1f, 1f, 0.8f, 1f };

    // ------------------------------------------------------------------ excursion shapes

    /// <summary>The whole walk for a kind, door to door (m): [min, max]. Round the nearest block loop when it's close
    /// enough to fit, else out along the sidewalk and back.</summary>
    public static Vector2 WalkLength(ExcursionKind k)
    {
        switch (k)
        {
            case ExcursionKind.DogWalk: return new Vector2(80f, 150f);
            case ExcursionKind.NightOwl: return new Vector2(50f, 90f);
            case ExcursionKind.Jog: return new Vector2(240f, 360f);
            case ExcursionKind.PowerWalk: return new Vector2(140f, 240f);
            case ExcursionKind.Patrol: return new Vector2(100f, 180f);
            case ExcursionKind.Segway: return new Vector2(220f, 360f);
            default: return new Vector2(60f, 140f);     // Stroll
        }
    }

    /// <summary>Turn-around pause of an out-and-back walk (real seconds).</summary>
    public const float TurnPauseMin = 1.5f, TurnPauseMax = 4f;

    /// <summary>Speed (m/s) for a kind and a resident's own walking speed.</summary>
    public static float Speed(ExcursionKind k, float walkSpeed)
    {
        switch (k)
        {
            case ExcursionKind.Jog: return WorldLifeTuning.Walk.Jog;
            case ExcursionKind.PowerWalk: return 1.8f;
            case ExcursionKind.Segway: return 2.4f;
            case ExcursionKind.DogWalk: case ExcursionKind.NightOwl: return walkSpeed * 0.9f;   // sniffing
            case ExcursionKind.Patrol: return walkSpeed * 0.85f;                              // clipboard
            default: return walkSpeed;
        }
    }

    public static LifeGait Gait(ExcursionKind k) =>
        k == ExcursionKind.Jog ? LifeGait.Jog : k == ExcursionKind.Segway ? LifeGait.Segway : LifeGait.Walk;

    // ------------------------------------------------------------------ gossip (§7.1)

    /// <summary>A neighbouring pair (same district, walk joins within this distance) may gossip; chance per pair per day.</summary>
    public const float GossipPairDistance = 40f;
    public static int GossipChance = 260;
    public const float GossipFrom = 9.5f, GossipTo = 18.5f;
    /// <summary>Stand together this long (real seconds).</summary>
    public const float GossipMinSeconds = 25f, GossipMaxSeconds = 50f;

    /// <summary>Park pause / mailbox pause / dog sniff stops (real seconds).</summary>
    public const float ParkPauseMin = 12f, ParkPauseMax = 30f, MailPause = 2.5f, SniffMin = 2f, SniffMax = 4.5f;
    /// <summary>~1 stroll in 6 takes jaywalks ("headphones guy").</summary>
    public const int JaywalkOneIn = 6;

    // ------------------------------------------------------------------ car trips (§7.1 commute / errand; §7.3)

    /// <summary>Per district: commute chance ‰ (car 0 of the house), errand chance (no commute), outing, dawn shift, late return.
    /// Fixer pass: ~25 % fewer commuters than D's first cut, so the rush is a plateau inside §4.3's 8–12 moving cars rather than
    /// a 22–26 car peak at 08:00 (a trip lasts ~64 real s ≈ 1.3 in-game h, so N commuters keep ≈ N × 1.3 / window cars moving).
    /// The residents who now stay home walk more, so a few walk rows above were trimmed (Monocle Gent, Housekeeper, Butler,
    /// Bodyguard) and the Billionaire's morning Segway moved earlier; outings went up for the 20–23 band.</summary>
    public static readonly int[] CommuteChance = { 360, 400, 320, 300, 240, 400 };
    /// <summary>Added to a two-car household's commute chance (‰).</summary>
    public static int CommuteTwoCarBonus = 140;
    public static readonly int[] ErrandChance = { 150, 150, 150, 150, 240, 150 };
    public static int ErrandWhenCommuting = 40;
    public static readonly int[] OutingChance = { 170, 170, 170, 170, 120, 190 };
    public static int DawnShiftChance = 28, LateChance = 18;
    /// <summary>The second car of a two-car house (it parks nearer the street): it leaves first and comes back last, so
    /// the deeper car only drives inside its away windows.</summary>
    public static int NestedErrandChance = 200;

    /// <summary>Commute departures (out) and returns to town (back) are drawn on a trapezoid over these windows: flat in the
    /// middle, with ramps of <see cref="CommuteRamp"/> × the window at each end. (D drew a triangle; with ~1.3 h trips that
    /// stacked into a peak twice the §4.3 band.) Statics so an eval can tune them; every machine uses the shipped values.</summary>
    public static float CommuteFrom = 5.85f, CommuteTo = 8.75f, CommuteBackFrom = 16.0f, CommuteBackTo = 19.2f, CommuteRamp = 0.15f;
    public const float ErrandFrom = 10.0f, ErrandTo = 15.2f, ErrandAwayMin = 0.5f, ErrandAwayMax = 1.5f;
    public const float OutingFrom = 19.3f, OutingTo = 21.3f, OutingAwayMin = 0.8f, OutingAwayMax = 1.8f;
    public const float DawnFrom = 5.0f, DawnTo = 5.9f, DawnBackFrom = 13.0f, DawnBackTo = 15.0f;
    public const float LateFrom = 21.3f, LateTo = 22.4f, LateBackFrom = 23.0f, LateBackTo = 23.75f;
    /// <summary>Nothing is scheduled past this hour, so no excursion or trip ever crosses midnight.</summary>
    public const float DayEndHour = 23.95f;

    /// <summary>Seconds between getting out of the car and the walk starting / the walk ending and the car moving.</summary>
    public const float GetInSeconds = 1.5f, GetOutSeconds = 1.0f;
}
