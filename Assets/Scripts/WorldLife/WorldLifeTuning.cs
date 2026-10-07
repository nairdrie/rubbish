using UnityEngine;

/// <summary>
/// WorldLife W0 CONTRACT: every number from Docs/WorldLife.md in one place (§4.2 LOD and caps, §4.4 budget, §6.1
/// knockables, §7 walking and traffic, §8 interaction rules, §8.5 horn, §9.1 consequences). Package G tunes here.
/// Values the host uses to validate claims (thresholds, fines) are const so every machine agrees; presentation-only
/// knobs are static so a play session can tweak them live.
/// </summary>
public static class WorldLifeTuning
{
    /// <summary>Population knob (§13: 0.5× on low-end machines). Presentation density only: D applies it as a
    /// deterministic cut by LifeHash, so a lower density shows a subset of the same timetables, never different ones.</summary>
    public static float Density = 1f;

    /// <summary>§9.1: Trust lost per incident. 0 in v1 (Progression's sim found Trust already self-correcting).</summary>
    public const float IncidentTrustLoss = 0f;

    /// <summary>
    /// Neighbour fines are charged (the default). Off = receipt shame only; Rep, grudges and flops always apply (§9.3).
    /// F sets <see cref="NeighborFinesProvider"/> to read the Town's TownRules.NeighborFines bit; before M1 the company
    /// counter <see cref="WorldLifeIds.FinesOffCounter"/> holds the toggle.
    /// </summary>
    public static bool NeighborFines
    {
        get
        {
            var p = NeighborFinesProvider;
            if (p != null) { try { return p(); } catch (System.Exception e) { Debug.LogException(e); } }
            return GameSession.GetCounter(WorldLifeIds.FinesOffCounter) == 0;
        }
    }
    public static System.Func<bool> NeighborFinesProvider;

    // ================================================================================================ §6.1 knockables

    /// <summary>One row of the §6.1 table.</summary>
    public readonly struct KnockSpec
    {
        /// <summary>Proxy Rigidbody mass (kg).</summary>
        public readonly float massKg;
        /// <summary>The truck knocks it over at or above this speed (m/s); below it the rest collider is solid.</summary>
        public readonly float knockSpeed;
        /// <summary>Fraction of the truck's speed lost once when it knocks this over.</summary>
        public readonly float speedLoss;
        /// <summary>City Works bill multiplier (hydrant ×4).</summary>
        public readonly int cityWorksMult;
        /// <summary>The floater word ("CLANG!").</summary>
        public readonly string word;

        public KnockSpec(float massKg, float knockSpeed, float speedLoss, int cityWorksMult, string word)
        {
            this.massKg = massKg; this.knockSpeed = knockSpeed; this.speedLoss = speedLoss;
            this.cityWorksMult = cityWorksMult; this.word = word;
        }
    }

    /// <summary>Indexed by (int)<see cref="KnockKind"/>.</summary>
    public static readonly KnockSpec[] Knock =
    {
        new KnockSpec(60f, 3.0f, 0.10f, 1, "CLANG!"),            // Lamp
        new KnockSpec(10f, 2.0f, 0.02f, 1, "*mailbox noises*"),  // Mailbox
        new KnockSpec(15f, 2.0f, 0.03f, 1, "BONK!"),             // WheelieBin
        new KnockSpec(8f, 2.0f, 0.01f, 1, "BWOING!"),            // Sign
        new KnockSpec(6f, 3.0f, 0.02f, 1, "CRACK!"),             // PicketPanel (G: A builds 2 m panels, half the doc's 4 m: half the mass and speed loss)
        new KnockSpec(25f, 2.0f, 0.04f, 1, "NOT THE LEMONADE!"), // LemonadeStand
        new KnockSpec(30f, 3.0f, 0.06f, 1, "AIRBALL!"),          // BasketballHoop
        new KnockSpec(6f, 2.0f, 0.01f, 1, "FLAP!"),              // DryingRack
        new KnockSpec(40f, 3.0f, 0.06f, 1, "CRUNCH!"),           // ParkBench
        new KnockSpec(12f, 2.0f, 0.02f, 1, "BONK!"),             // ParkBin
        new KnockSpec(40f, 3.0f, 0.08f, 4, "FWOOSH!"),           // Hydrant
        new KnockSpec(2f, 1.5f, 0.00f, 1, "BOINK!"),             // Cone
        new KnockSpec(6f, 1.5f, 0.01f, 1, "CLATTER!"),           // Sawhorse
        new KnockSpec(1f, 1.0f, 0.00f, 1, "SQUAWK!"),            // Flamingo
    };

    public static KnockSpec Spec(KnockKind k) => Knock[Mathf.Clamp((int)k, 0, Knock.Length - 1)];

    /// <summary>§6.2 / §6.3 knock lifecycle.</summary>
    public static class Props
    {
        /// <summary>Speed loss from props is capped at this fraction per second (a cone slalom never feels like a wall).</summary>
        public const float SpeedLossCapPerSecond = 0.35f;
        /// <summary>Sensor: the body box is stretched forward by |v| × LookaheadSteps × fixedDeltaTime + LookaheadPad.</summary>
        public const float LookaheadSteps = 2f;
        public const float LookaheadPad = 0.3f;
        /// <summary>Below this truck speed the runner doesn't sweep at all (nothing reacts under 0.8 m/s).</summary>
        public const float SweepMinSpeed = 0.75f;
        /// <summary>Proxy launch: v_truck × LaunchVelMult + up × (LaunchUp + LaunchUpPerSpeed × |v|).</summary>
        public const float LaunchVelMult = 1.1f, LaunchUp = 1.5f, LaunchUpPerSpeed = 0.1f;
        /// <summary>The proxy ignores the truck that hit it for this long (Physics.IgnoreCollision).</summary>
        public const float IgnoreTruckSeconds = 0.3f;
        /// <summary>After this long at rest a proxy goes kinematic + asleep.</summary>
        public const float SettleSeconds = 3f;
        /// <summary>The host resets a prop once it has been down this long AND no player is within ResetClearRadius.</summary>
        public const float ResetAfterSeconds = 120f;
        public const float ResetClearRadius = 25f;
        /// <summary>City Works resets everything at this in-game hour.</summary>
        public const float CityWorksHour = 6f;
        public const int MaxAwakeProxies = 24;
        /// <summary>The 81st knock makes the oldest down prop reset early (with a poof).</summary>
        public const int MaxDown = 80;
        /// <summary>Floaters ("CLANG!") at most one per this many seconds.</summary>
        public static float FloaterInterval = 0.4f;
        public const float HydrantGeyserSeconds = 20f;
        /// <summary>A geyser gives the local player a hop of this height (on their own machine).</summary>
        public const float GeyserPlayerHop = 3f;
        public const float ConeStickSeconds = 10f;
    }

    // ================================================================================================ §8 hits

    /// <summary>§8.1 – §8.4: truck vs people, dogs and cars; thrown things; bumps; boops.</summary>
    public static class Hits
    {
        /// <summary>Truck speed (inside the swept truck box + TruckBoxPad): below Nudge nothing happens; Nudge..Flatten
        /// pushes the NPC aside; at or above Flatten they flop.</summary>
        public const float NudgeSpeed = 0.8f, FlattenSpeed = 2.5f, TruckBoxPad = 0.35f;
        public const float NudgeDistance = 0.6f;

        /// <summary>Flop launch: v0 = flat(v)·0.55 + side·(2 + 0.12 s) + up·clamp(3.5 + 0.3 s, 4, 8); |horizontal| ≤ 10.</summary>
        public const float LaunchForward = 0.55f, LaunchSideBase = 2f, LaunchSidePerSpeed = 0.12f;
        public const float LaunchUpBase = 3.5f, LaunchUpPerSpeed = 0.3f, LaunchUpMin = 4f, LaunchUpMax = 8f;
        public const float LaunchMaxHorizontal = 10f;
        public const float SpinMinDegPerSec = 540f, SpinMaxDegPerSec = 900f;
        public const float BounceRestitution = 0.35f;

        /// <summary>Flop phases (seconds).</summary>
        public const float BounceSeconds = 0.3f, LieSeconds = 1.6f, GetUpSeconds = 0.6f, AngrySeconds = 1.4f;
        public const float ChaseMaxSeconds = 3f, ChaseSpeed = 4.5f, ChaseGiveUpDistance = 15f;
        /// <summary>The timetable resumes with this host-fixed delay after a flatten / shunt.</summary>
        public const float RejoinDelaySeconds = 10f;
        /// <summary>Immunity: until this long after rejoining (~12 s in total); the same driver + NPC pair 20 s.</summary>
        public const float ImmuneAfterRejoinSeconds = 2f, SameDriverCooldownSeconds = 20f;

        /// <summary>Dogs (§8.2): a sideways leap this high.</summary>
        public const float DogLeapHeight = 1.2f;

        /// <summary>Cars (§8.3): shunt at ≥ ShuntSpeed (below it the kinematic box is a wall).</summary>
        public const float ShuntSpeed = 2.5f, ShuntColliderOffSeconds = 1f;
        public const float ShuntYawKickMinDeg = 20f, ShuntYawKickMaxDeg = 70f, ShuntHop = 0.3f, ShuntSlideMax = 2f;
        public const float CarAlarmSeconds = 6f, CarImmunitySeconds = 10f;
        public const float ShuntTruckSpeedLoss = 0.35f, ShuntTruckYawKickDegPerSec = 6f;
        public const float DriverPopOutDelay = 0.5f, DriverFistSeconds = 3f;

        /// <summary>Thrown bag / item vs NPC (§8.4): &lt; Boop..Stumble boop; Stumble..Flop stumble; ≥ Flop a short flop.</summary>
        public const float ThrownStumbleSpeed = 6f, ThrownFlopSpeed = 11f, ThrownShortLieSeconds = 1f, ThrownCooldownSeconds = 4f;
        /// <summary>A player on foot: sprinting at ≥ this pushes an NPC back 0.5 m (they spill their mug).</summary>
        public const float PlayerBumpStumbleSpeed = 4.5f, PlayerBumpPush = 0.5f, PlayerBumpCooldownSeconds = 1f;

        /// <summary>NPC car vs a player on foot (§7.3): a gentle local stumble, no bag drop.</summary>
        public const float BoopStumbleSeconds = 0.4f, BoopImmunitySeconds = 5f;

        /// <summary>Prediction vs the host's event: keep the local animation if the launch matches within this fraction.</summary>
        public const float PredictionTolerance = 0.15f;

        /// <summary>Host validation slack (§10.2): the NPC within this of the rewound truck box; speed ≤ tier max + 15%.</summary>
        public const float HostPositionSlack = 2.5f, HostSpeedSlack = 0.15f, HostKnockSlack = 3f;

        /// <summary>The canonical flop launch (§8.1) for a truck velocity and the truck's side vector toward the NPC.</summary>
        public static Vector3 FlopLaunch(Vector3 truckVelocity, Vector3 side)
        {
            Vector3 flat = new Vector3(truckVelocity.x, 0f, truckVelocity.z);
            float s = flat.magnitude;
            side.y = 0f;
            side = side.sqrMagnitude > 1e-6f ? side.normalized : Vector3.zero;
            Vector3 h = flat * LaunchForward + side * (LaunchSideBase + LaunchSidePerSpeed * s);
            if (h.magnitude > LaunchMaxHorizontal) h = h.normalized * LaunchMaxHorizontal;
            return h + Vector3.up * Mathf.Clamp(LaunchUpBase + LaunchUpPerSpeed * s, LaunchUpMin, LaunchUpMax);
        }

        /// <summary>The result kind for a truck speed against a person (dogs always leap; cars use ShuntSpeed).</summary>
        public static NpcHitKind PersonResult(float truckSpeed) =>
            truckSpeed >= FlattenSpeed ? NpcHitKind.Flatten : truckSpeed >= NudgeSpeed ? NpcHitKind.Nudge : NpcHitKind.None;
    }

    // ================================================================================================ §8.5 horn

    public static class Horn
    {
        /// <summary>People within the horn radius AND ahead of the truck, or within this radius in any direction, startle.</summary>
        public const float AnyDirectionRadius = 6f;
        public const float StartleHop = 0.4f, StepOffMinSeconds = 2f, StepOffMaxSeconds = 3f;
        /// <summary>Cars ahead in the same lane pull to the kerb for this long.</summary>
        public const float PullOverSeconds = 3f;
        /// <summary>A honk within this distance of any house between NightFrom and NightTo is a noise complaint.</summary>
        public const float NoiseRadius = 30f, NightFromHour = 22f, NightToHour = 6f;
        /// <summary>Horn rate limit (TruckControllerV2 already enforces 0.3 s).</summary>
        public const float MinInterval = 0.3f;

        /// <summary>The horn radius for the current upgrades (UpgradeEffects.HornScareRadius: 12 / 25 / 40 m).</summary>
        public static float Radius => UpgradeEffects.HornScareRadius;
    }

    // ================================================================================================ §9.1 consequences

    /// <summary>Indexed by district byte (0 Mildly Pleasant .. 3 Bluffs). Rep multipliers come from DistrictDef.repMult
    /// (Ledger.Rep applies them); Rep base values are RepValues.Neighbor* in ProgressionIds.cs.</summary>
    public static class Fines
    {
        public static readonly float[] Flattened = { 3f, 12f, 40f, 120f };
        public static readonly float[] CarShunted = { 5f, 20f, 60f, 200f };
        /// <summary>City Works, per prop (hydrant × KnockSpec.cityWorksMult).</summary>
        public static readonly float[] PropKnocked = { 0.5f, 2f, 5f, 15f };
        /// <summary>City Works cap per district per day (one invoice per district at midnight).</summary>
        public static readonly float[] CityWorksDailyCap = { 5f, 20f, 60f, 200f };

        /// <summary>The n-th flatten or shunt (1-based) in a district on one in-game day costs × min(3, 1 + 0.25 (n − 1)).
        /// Cash only: Rep is never escalated (§9.2's numbers).</summary>
        public const float EscalationStep = 0.25f, EscalationMax = 3f;
        public static float Escalation(int n) => Mathf.Min(EscalationMax, 1f + EscalationStep * Mathf.Max(0, n - 1));

        /// <summary>Grudge: until the next midnight that house's bags pay as if this many in-game hours older, and the
        /// on-time tip is void. Collecting every bag at the curb clears it ("Apology accepted").</summary>
        public const float GrudgeExtraHours = 1f;

        /// <summary>Letters: at most one pop-up per in-game hour; the rest go to the inbox.</summary>
        public const float LetterPopupIntervalHours = 1f;

        static float At(float[] t, int district) => t[Mathf.Clamp(district, 0, t.Length - 1)];
        public static float FlattenFine(int district) => At(Flattened, district);
        public static float ShuntFine(int district) => At(CarShunted, district);
        public static float PropFine(int district, KnockKind kind) => At(PropKnocked, district) * Spec(kind).cityWorksMult;
        public static float CityWorksCap(int district) => At(CityWorksDailyCap, district);
    }

    // ================================================================================================ §4.2 relevance

    public static class Lod
    {
        /// <summary>Puppets: full ≤ FullDistance (every frame), lite ≤ LiteDistance (LiteHz), hidden beyond.</summary>
        public const float FullDistance = 35f, LiteDistance = 90f, LiteHz = 15f;
        /// <summary>Cars: moving cars shown ≤ MovingCarDistance (15 Hz beyond LiteDistance); parked cars always (static).</summary>
        public const float MovingCarDistance = 160f;
        /// <summary>Agents are evaluated only when their current leg's bounds come within this of a camera.</summary>
        public const float RelevanceDistance = 170f;
        /// <summary>Walk-outs are presented only when the house is within this of the camera (else the bag just appears).</summary>
        public const float WalkOutDistance = 120f;
        /// <summary>Neglected-pile witness: shown while the camera is within this.</summary>
        public const float WitnessDistance = 60f;
        public const int MaxFullPuppets = 24, MaxLitePuppets = 24, MaxMovingCars = 16;
        /// <summary>Chatter: one line per NPC per this many seconds, at most ChatterOnScreen at once.</summary>
        public const float ChatterInterval = 6f;
        public const int ChatterOnScreen = 3;
    }

    // ================================================================================================ §3.1 / §12.K kids

    /// <summary>The kids in the fenced yards and the playground (presentation only: never bodies, never hit).</summary>
    public static class Kids
    {
        /// <summary>Kids wave (and run along the fence after the truck) while a truck is within this; they stop at +Hysteresis.</summary>
        public static float WaveDistance = 25f, WaveHysteresis = 3f;
        /// <summary>A honk within this (or the horn's own radius when the air-horn upgrade makes it bigger) is a jump-cheer.</summary>
        public static float HonkDistance = 30f;
        public static float HonkRadius => Mathf.Max(HonkDistance, Horn.Radius);
        /// <summary>Running along the fence / strolling back home (m/s); kids keep this far apart on the run loop (m).</summary>
        public static float RunSpeed = 2.4f, StrollSpeed = 1.1f, Spread = 0.8f;
        /// <summary>The jump-cheer (s); the first honk of the day at a pen makes it a fan moment (longer, +1 Rep on the host).</summary>
        public static float CheerSeconds = 2.2f, FanCheerSeconds = 3.4f;
        /// <summary>Pens farther than this from the camera aren't simulated (kids snap home when it comes back).</summary>
        public const float RelevanceDistance = 130f;
        /// <summary>Safety net: a truck body box (+ this pad) reaching a kid sends them indoors ("EEK!") until the truck is
        /// ShyClearDistance from the pen. The fence makes it unreachable; this only covers physics glitches.</summary>
        public const float ShyPad = 0.3f, ShyClearDistance = 10f, ShySeconds = 4f;
        /// <summary>Pool caps (on top of the residents' 24 + 24): full ≤ Lod.FullDistance, lite ≤ Lod.LiteDistance.</summary>
        public const int MaxFull = 8, MaxLite = 8;
    }

    // ================================================================================================ §7 people + traffic

    public static class Walk
    {
        public const float Waddle = 1.3f, Carrying = 1.1f, Jog = 2.8f, Slow = 0.9f;
        /// <summary>Walk-out (§7.2): bend + set down, then a beat (stretch / wave / glare) before walking back.</summary>
        public const float SetDownSeconds = 1f, BeatSeconds = 1.5f, WaveDistance = 30f;
        /// <summary>A raccoon this close at night: "RACCOON!", drop the bag, sprint inside.</summary>
        public const float RaccoonPanicDistance = 6f;
        /// <summary>Local avoidance may push a pedestrian off its line by at most this (it may diverge between machines).</summary>
        public const float MaxAvoidOffset = 1.2f;
        /// <summary>The commuter carries the bag out when their commute starts within this many in-game minutes of T.</summary>
        public const float CommuteCarryWindowMinutes = 10f;
    }

    public static class Traffic
    {
        public const float StraightSpeed = 9f, TurnSpeed = 4.5f, DrivewaySpeed = 2.5f, PadSpeed = 2.5f, ReverseSpeed = 1.5f;
        public const float ReverseOutSeconds = 4f;
        public const float FollowGap = 6f, IntersectionSpeed = 3f;
        /// <summary>Stuck behind a truck: brake BrakeShort m short, stop StopShort m short, honk after HonkAfter s, overtake
        /// when the opposite lane is clear for OvertakeClear m.</summary>
        public const float BrakeShort = 8f, StopShort = 3f, HonkAfter = 3f, OvertakeClear = 30f;
        /// <summary>Re-convergence: a delayed car catches up at up to this × its speed.</summary>
        public const float CatchUpFactor = 1.4f;
        /// <summary>Spatial-hash radius for following / yield checks.</summary>
        public const float YieldQueryRadius = 16f;
    }

    // ================================================================================================ §4.4 budget

    /// <summary>Per-machine budget (ms of main thread at 60 fps), checked by WorldLifeProfiler and the acceptance evals.</summary>
    public static class Budget
    {
        public const float ScheduleMs = 0.15f, PuppetsMs = 0.60f, CarsMs = 0.20f, SensorMs = 0.05f, KnockMs = 0.05f;
        public const float TotalAvgMs = 1.1f, TotalP99Ms = 2.0f, PhysicsMs = 0.4f;
        public const float FullPuppetMs = 0.02f, LitePuppetMs = 0.005f, CarMs = 0.012f;
        /// <summary>Extra renderers, worst case visible (≈750) and scene GameObjects added (≈+600, under ~5.5k total).</summary>
        public const int ExtraRenderers = 750, ExtraGameObjects = 700;
    }
}
