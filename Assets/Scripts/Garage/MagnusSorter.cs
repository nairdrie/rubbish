using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Magnus becomes your employee (Docs/Progression.md §6.3, the career path in MagnusCareer). Career step 1, "Magnus Gets
/// a Job" (magnus_brain L1), brings the rail branch, a servo gripper (5 kg) and Brain Module 1: from then on he goes
/// on shift when it's within his hours, nobody is at the sort station or seated at chess, and there's work. He commutes
/// along the rail branch (via the chess stop) to the Sort Stop, then picks revealed items off the table and carries each
/// one toward its bin (recyclables + junk at brain L1, + treasure at L2, + hazards at L3, around the clock at L4).
/// Things he can't handle go to a LEFTOVERS corner. With probability (1 - accuracy) an item goes into the wrong
/// recycling bin: normal contamination, plus an apology. Robots never touch the player's combo; his sorts are credited
/// byMagnus. No conveyor needed: he works the table, the Bag Belt only feeds it.
/// Bags: until the Lobster Pincer (gripper L1, or brain L3) he only sorts what you rip onto the table. With it he feeds
/// himself: the Bag Belt tray's bag first, else the next tipping-pile bag ("YOINK!", vision-flagged ones first, the
/// same lob the station's "Next bag!" uses), and SLAMS it open on the table. He waits for each spray to land and stop
/// rolling, only rips while the table holds fewer than ripTableLimit items, and while he's on shift anything that
/// bounced onto the floor hops back onto the table (SortStation.RescueFloorItems).
///
/// Speed contract: items/min = brain rate × MagnusSpeedMult (rail motor, quad-claw, twin belt). The arm's
/// SpeedMultiplier is tuned from each measured cycle and every cycle is padded to the contract, so the rate lands on it.
/// A player stepping up sees "Magnus is on shift. Shoo him"; he drives back to chess.
/// Host decides (GarageRig.Decides); everything he touches is local garage state like the rest of sorting today.
/// </summary>
public class MagnusSorter : MonoBehaviour, SortStation.IStationWorker
{
    public enum State { Off, Commuting, Working, Returning }

    public GarageRig rig;
    [Tooltip("Optional: the LEFTOVERS corner (world). Default: the table's front-right corner.")]
    public Transform leftoversCorner;
    [Tooltip("Height (m) above the tabletop he carries items at, toward their bin.")]
    public float carryHeight = 0.6f;
    [Tooltip("Nothing he can do for this long at the station: clock out.")]
    public float idleGiveUpSeconds = 8f;
    [Tooltip("After clocking out (idle or shooed) wait this long before the next shift.")]
    public float restSeconds = 20f;
    public float shooRestSeconds = 45f;
    [Tooltip("Seconds one table pick-and-place takes at arm speed ×1 (≈11 s by the arm's own motion maths at the Sort Stop); seeds the tuning.")]
    public float baseCycleSeconds = 11f;
    [Tooltip("Aim each cycle at this fraction of the contract time, then pad: long reaches still land on the contract.")]
    public float speedHeadroom = 0.8f;
    [Tooltip("Fastest the arm may be cranked (MAGNUS PRIME with every speed-up needs ~×15.6 to hit 68 items/min).")]
    public float maxArmSpeed = 16f;
    [Tooltip("He only rips another bag while the table holds fewer items than this (LEFTOVERS count; ×MaxItems/40 with the XL table, never above MaxItems - 8). A crowded table bounces the next spray onto the floor.")]
    public int ripTableLimit = 14;
    [Tooltip("After a rip, wait (at most this long, s) for the spray to land and stop rolling before doing anything else.")]
    public float spraySettleSeconds = 4f;
    [Tooltip("Loose items within this flat radius (m) of the table that are still moving count as the last spray settling.")]
    public float sprayRadius = 3f;

    public State Phase { get; private set; } = State.Off;
    public State VisiblePhase
    {
        get
        {
            int lot = DepotMachineContext.LotOf(this);
            return lot >= 0 && !GarageRig.Decides ? BusinessUpgradeNet.PhaseForLot(lot) : Phase;
        }
    }
    /// <summary>Correct sorts this session (any bin).</summary>
    public int Sorted { get; private set; }
    public int Mistakes { get; private set; }
    public int LeftoversPushed { get; private set; }
    public int BagsRipped { get; private set; }
    /// <summary>Items that fell off the table and hopped back while he was on shift (SortStation's floor rescue).</summary>
    public int FloorRescued { get; private set; }
    /// <summary>Items he sorted per minute of actual sorting (cycle time only; commute and idle excluded).</summary>
    public float MeasuredItemsPerMin => sortSeconds > 0.01f ? itemsTimed / (sortSeconds / 60f) : 0f;
    /// <summary>The contract rate right now: brain items/min × MagnusSpeedMult.</summary>
    public static float ContractItemsPerMin => UpgradeEffects.Brain.itemsPerMin * UpgradeEffects.MagnusSpeedMult;

    int Level(string id) => DepotMachineContext.Level(this, id);
    UpgradeEffects.BrainTier LotBrain => UpgradeEffects.BrainAt(Level(ShopIds.MagnusBrain));
    float LotContractItemsPerMin
    {
        get
        {
            float mult = 1f;
            if (Level(ShopIds.MagnusRailMotor) >= 1) mult *= UpgradeEffects.RailMotorSpeed;
            if (Level(ShopIds.MagnusGripper) >= 2) mult *= UpgradeEffects.QuadClawSpeed;
            if (Level(ShopIds.ConveyorBelt) >= 3) mult *= UpgradeEffects.TwinBeltMagnusSpeed;
            return LotBrain.itemsPerMin * mult;
        }
    }

    float restUntil;
    bool shooed;
    Coroutine job;
    float armSpeed = -1f;
    float cycleAtX1 = -1f;   // running estimate of a ×1 pick-and-place (s)
    int itemsTimed;
    float sortSeconds;
    float nextCheck;
    float nextFloorLine;
    bool everWorked;
    readonly HashSet<string> leftovers = new HashSet<string>();
    readonly HashSet<string> tooHeavySaid = new HashSet<string>();
    readonly Dictionary<string, int> attempts = new Dictionary<string, int>();
    readonly List<Carryable> pileScratch = new List<Carryable>();
    readonly List<Carryable> hopperScratch = new List<Carryable>();
    TrashBag bagInFlight;   // a bag he's lobbing / holding (handed back to physics if he's switched off mid-flight)

    MagnusTrack Track => rig != null ? rig.track : null;
    MagnusArm Arm => rig != null ? rig.arm : null;
    SortingTable Table => rig != null ? rig.table : null;
    SortStation Station => rig != null ? rig.station : null;
    ConveyorTray tray;

    // ------------------------------------------------------------------ IStationWorker

    public bool BlocksStation
    {
        get
        {
            var phase = VisiblePhase;
            if (phase == State.Off) return false;
            if (phase == State.Commuting || phase == State.Working) return true;
            var t = Track;
            return t != null && t.DistanceToSort < 1.5f;
        }
    }

    public string BlockedPrompt => VisiblePhase == State.Returning ? "Magnus is heading back to chess..." : "Magnus is on shift. Shoo him";

    public void Shoo()
    {
        int lot = DepotMachineContext.LotOf(this);
        if (lot >= 0 && !GarageRig.Decides)
        {
            if (VisiblePhase != State.Off && VisiblePhase != State.Returning) BusinessUpgradeNet.RequestRobotShoo(lot);
            return;
        }
        if (Phase == State.Off || Phase == State.Returning) return;
        if (!shooed)
        {
            shooed = true;
            Say(SorterLines.Shoo("shoo" + Time.frameCount));
            Sfx.Play("squeak", Arm != null ? Arm.HeadPosition : transform.position, 0.8f, 0.7f);
        }
    }

    // ------------------------------------------------------------------ lifecycle

    void Awake()
    {
        if (rig == null) rig = GetComponentInParent<GarageRig>();
    }

    void OnEnable()
    {
        if (rig == null) rig = GetComponentInParent<GarageRig>();
        if (rig != null) rig.Resolve();
        if (Station != null) Station.worker = this;
        GameSession.UpgradeChanged += OnUpgradeChanged;
        BusinessUpgradeNet.Changed += OnBusinessUpgradesChanged;
    }

    /// <summary>New brain / gripper: what he left as LEFTOVERS may be his job now; the speed contract changed too.</summary>
    void OnUpgradeChanged(string id, int level)
    {
        if (id == ShopIds.MagnusBrain || id == ShopIds.MagnusGripper)
        {
            leftovers.Clear();
            tooHeavySaid.Clear();
        }
        if (id == ShopIds.MagnusBrain || id == ShopIds.MagnusGripper || id == ShopIds.MagnusRailMotor || id == ShopIds.ConveyorBelt)
            armSpeed = -1f;   // re-derived from the new contract on the next item (cycleAtX1 is kept)
    }

    void OnBusinessUpgradesChanged()
    {
        leftovers.Clear();
        tooHeavySaid.Clear();
        armSpeed = -1f;
    }

    void OnDisable()
    {
        GameSession.UpgradeChanged -= OnUpgradeChanged;
        BusinessUpgradeNet.Changed -= OnBusinessUpgradesChanged;
        if (Station != null && ReferenceEquals(Station.worker, this)) Station.worker = null;
        if (job != null) StopCoroutine(job);
        job = null;
        if (Phase != State.Off)
        {
            if (Track != null) Track.StopDriving();
            if (Arm != null) Arm.CancelPlans();
        }
        if (bagInFlight != null && bagInFlight.State != CarryState.Consumed && bagInFlight.State != CarryState.Carried)
            bagInFlight.EndCarry(Vector3.zero, true);
        bagInFlight = null;
        if (Arm != null) { Arm.SpeedMultiplier = 1f; Arm.Idle = MagnusArm.IdlePose.Think; }
        Phase = State.Off;
        BusinessUpgradeNet.PublishRobotPhase(DepotMachineContext.LotOf(this), Phase);
    }

    void Update()
    {
        if (!GarageRig.Decides) return;
        BusinessUpgradeNet.PublishRobotPhase(DepotMachineContext.LotOf(this), Phase);
        if (Phase == State.Working && !Compactor.AutomationPaused(this)) TickFloorRescue();
        if (Phase != State.Off || Time.time < nextCheck) return;
        nextCheck = Time.time + 0.5f;
        // Switched off mid-shift (OnDisable stops the shift wherever he is) and back on: he'd sit at the Sort Stop for
        // good ("not parked at chess"). Drive home first.
        var track = Track;
        if (track != null && Arm != null && track.ChessStop != null && !track.AtChess && !track.IsDriving)
        {
            job = StartCoroutine(GoHome());
            return;
        }
        if (ShouldStartShift(out _)) job = StartCoroutine(Shift());
    }

    IEnumerator GoHome()
    {
        Phase = State.Returning;
        Arm.SpeedMultiplier = 1f;
        yield return WaitArm(4f);
        yield return Track.DriveToChess();
        Arm.Idle = MagnusArm.IdlePose.Think;
        Phase = State.Off;
        job = null;
    }

    /// <summary>
    /// On shift, spray that bounced off the table onto the floor hops back onto it (the sort station's own floor rescue,
    /// which otherwise only runs while a player sorts first-person); he says so now and then.
    /// </summary>
    void TickFloorRescue()
    {
        var st = Station;
        if (st == null) return;
        int hops = st.RescueFloorItems();
        if (hops <= 0) return;
        FloorRescued += hops;
        if (Time.time < nextFloorLine) return;
        nextFloorLine = Time.time + 15f;
        Say(SorterLines.FloorRescue("floor" + FloorRescued));
    }

    /// <summary>Would he go on shift right now? <paramref name="why"/> says what's missing (tests / debug).</summary>
    public bool ShouldStartShift(out string why)
    {
        if (Compactor.AutomationPaused(this)) { why = "bulk compact confirmation"; return false; }
        var brain = LotBrain;
        var track = Track;
        var station = Station;
        if (brain.itemsPerMin <= 0f) { why = "no job yet (career step 1)"; return false; }
        if (track == null || !track.HasSortStop || Level(ShopIds.MagnusBrain) < 1) { why = "no rail branch"; return false; }
        if (Arm == null || Table == null || station == null) { why = "garage not wired"; return false; }
        if (Time.time < restUntil) { why = "resting"; return false; }
        if (!InHours(GameClock.Hours, brain.startHour, brain.endHour)) { why = "off the clock"; return false; }
        if (station.IsActive || station.Occupied) { why = "someone is sorting"; return false; }
        if (SomeoneNear(track.SortStop.position, 1.1f)) { why = "someone is standing at the table"; return false; }
        if (ChessOccupied()) { why = "someone is playing chess"; return false; }
        if (!track.AtChess) { why = "not parked at chess"; return false; }
        if (!HasWork(true)) { why = "nothing to do"; return false; }
        why = null;
        return true;
    }

    /// <summary>Any player (on foot) within <paramref name="radius"/> m of a point? He won't park on anyone.</summary>
    static bool SomeoneNear(Vector3 p, float radius)
    {
        foreach (var pc in FindObjectsByType<PlayerController>(FindObjectsSortMode.None))
        {
            if (pc == null || !pc.isActiveAndEnabled || pc.IsDriving) continue;
            if (BlocksSortStop(pc.transform.position, p, radius)) return true;
        }
        return false;
    }

    /// <summary>A player in the street above this pocket interior must not block its robot's rail stop.</summary>
    public static bool BlocksSortStop(Vector3 player, Vector3 stop, float radius)
    {
        Vector3 d = player - stop;
        if (Mathf.Abs(d.y) >= 1.5f) return false;
        d.y = 0f;
        return d.sqrMagnitude < radius * radius;
    }

    /// <summary>Explain the normal auto-start gates at the table; guests never invent a host-side cooldown.</summary>
    public string IdleStatusHint
    {
        get
        {
            var brain = LotBrain;
            if (brain.itemsPerMin <= 0f || VisiblePhase != State.Off) return null;
            if (!InHours(GameClock.Hours, brain.startHour, brain.endHour))
                return $"Magnus: off duty — starts at {brain.startHour:00}:00.";
            if (ChessOccupied()) return "Magnus: leave the chess game so he can sort.";
            if (GarageRig.Decides && Time.time < restUntil)
                return $"Magnus: short break — resumes automatically in {Mathf.CeilToInt(restUntil - Time.time)}s.";
            return ReadyHint(CanRipBags, brain.startHour, brain.endHour);
        }
    }

    public static string ReadyHint(bool canRip, float start, float end)
    {
        string work = canRip ? "leave bags on the tipping floor, then step away" : "rip a bag onto the table, then step away";
        string hours = end - start >= 23.99f ? "24h" : $"{start:00}:00–{end:00}:00";
        return $"Magnus: {work}. Auto-sorts {hours}.";
    }

    bool ChessOccupied()
    {
        int lot = DepotMachineContext.LotOf(this);
        return lot >= 0 ? DepotChessNet.Instance != null && DepotChessNet.Instance.HasHumanPlayer(lot)
            : rig.chess != null && rig.chess.HasHumanPlayer;
    }

    public static bool InHours(float hour, float start, float end)
    {
        if (end - start >= 23.99f) return true;
        return start <= end ? hour >= start && hour < end : hour >= start || hour < end;
    }

    /// <summary>Lobster Pincer (gripper L1) or brain L3: he fetches and rips bags himself.</summary>
    bool CanRipBags => Level(ShopIds.MagnusBrain) >= 1 && (Level(ShopIds.MagnusGripper) >= 1 || LotBrain.ripsBags);

    ConveyorTray Tray
    {
        get
        {
            if (tray == null && Station != null) tray = Station.tray;
            return tray;
        }
    }

    bool HasWork(bool fromChess)
    {
        if (NextItem(fromChess, out _) != null) return true;
        if (fromChess && Station != null && Station.HasFloorItems()) return true;   // spray on the floor: go hop it back
        if (!CanRipBags || !TableHasRoom()) return false;
        var t = Tray;
        return (t != null && t.HasBag) || NextPileBag() != null;
    }

    /// <summary>
    /// Room on the table for another bag's worth of items: nobody mid-rip, the last spray has landed, and fewer items than
    /// the rip limit lie on it (LEFTOVERS count). Ripping into a crowded or still-bouncing table sprayed half the next
    /// bag onto the floor (verifier, 2026-10-01).
    /// </summary>
    bool TableHasRoom()
    {
        var table = Table;
        return table != null && !table.IsRipping && table.MaxItems - table.AvailableItemSlots < RipLimit(table) && !SprayUnsettled();
    }

    int RipLimit(SortingTable table) =>
        Mathf.Min(table.MaxItems - 8, Mathf.Max(4, Mathf.RoundToInt(ripTableLimit * table.MaxItems / 40f)));

    /// <summary>Something near the table is still flying or rolling (the last spray, a bounce, a rescue hop landing).</summary>
    bool SprayUnsettled()
    {
        var table = Table;
        if (table == null) return false;
        if (table.IsRipping) return true;
        Vector3 c = table.transform.position;
        float r2 = sprayRadius * sprayRadius;
        var all = LooseItem.All;
        for (int i = 0; i < all.Count; i++)
        {
            var li = all[i];
            // kinematic / scripted = in his claw, hopping back, arcing into a bin: not spray
            if (li == null || !SortingKit.IsLoose(li) || li.Body == null || li.Body.isKinematic || li.IsInScriptedFlight) continue;
            if (FlatDist2(li, c) <= r2 && li.Body.linearVelocity.sqrMagnitude > 0.09f) return true;
        }
        return false;
    }

    static float FlatDist2(LooseItem li, Vector3 c)
    {
        Vector3 d = li.transform.position - c;
        d.y = 0f;
        return d.sqrMagnitude;
    }

    /// <summary>After a rip: wait for the spray to pop out, land and stop rolling (at most spraySettleSeconds).</summary>
    IEnumerator WaitSpraySettled(int sprayCount)
    {
        var table = Table;
        float t0 = Time.time;
        float popOut = 0.35f + (table != null ? table.spillInterval : 0.05f) * sprayCount;
        yield return new WaitForSeconds(0.3f);
        while (Time.time - t0 < spraySettleSeconds && (Time.time - t0 < popOut || SprayUnsettled()))
            yield return new WaitForSeconds(0.1f);
    }

    // ------------------------------------------------------------------ the shift

    IEnumerator Shift()
    {
        var track = Track;
        var arm = Arm;
        var table = Table;
        shooed = false;
        Phase = State.Commuting;
        arm.WorkFocus = table.TopPoint(0f, 0f);
        arm.Idle = MagnusArm.IdlePose.Work;
        Say(everWorked ? SorterLines.Start("start" + GameClock.Day) : "I AM HELPING.");
        Sfx.Play("beep", arm.HeadPosition, 0.6f, 1.2f);
        yield return track.DriveToSort();

        float idleSince = Time.time;
        if (track.AtSort && !shooed)
        {
            Phase = State.Working;
            everWorked = true;
            Fx.Dust(track.transform.position, 0.6f);
            Sfx.Play("thud", track.transform.position, 0.5f, 1.3f);
            while (true)
            {
                if (ShouldLeave()) break;
                if (Compactor.AutomationPaused(this))
                {
                    idleSince = Time.time;
                    yield return new WaitForSecondsRealtime(0.1f);
                    continue;
                }
                arm.WorkFocus = table.TopPoint(0f, 0f);

                if (NeedsSmash()) { yield return PokeSmash(); idleSince = Time.time; continue; }

                var li = NextItem(false, out bool leftover);
                if (li != null)
                {
                    yield return leftover ? PushLeftover(li) : SortItem(li);
                    idleSince = Time.time;
                    continue;
                }
                if (CanRipBags && TableHasRoom())
                {
                    // Feed himself: the Bag Belt tray first, else the next bag off the tipping pile.
                    var t = Tray;
                    if (t != null && t.HasBag) { yield return RipBag(t.Take(), false); idleSince = Time.time; continue; }
                    var pileBag = NextPileBag();
                    if (pileBag != null) { yield return RipBag(pileBag, true); idleSince = Time.time; continue; }
                }
                if (Time.time - idleSince > idleGiveUpSeconds) break;
                yield return new WaitForSeconds(0.3f);
            }
        }

        // Clock out: back to the board.
        Phase = State.Returning;
        Say(shooed ? null : SorterLines.End("end" + GameClock.Day + Sorted));
        restUntil = Time.time + (shooed ? shooRestSeconds : restSeconds);
        arm.SpeedMultiplier = 1f;
        yield return WaitArm(4f);
        yield return track.DriveToChess();
        arm.Idle = MagnusArm.IdlePose.Think;
        Phase = State.Off;
        job = null;
    }

    bool ShouldLeave()
    {
        var brain = LotBrain;
        if (shooed || brain.itemsPerMin <= 0f) return true;
        if (!InHours(GameClock.Hours, brain.startHour, brain.endHour)) return true;
        if (ChessOccupied()) return true;   // someone wants a game
        var st = Station;
        return st != null && (st.IsActive || st.Occupied);
    }

    // ------------------------------------------------------------------ choosing work

    /// <summary>
    /// The next table item to handle: one he can sort (nearest the claw first), or failing that one to push to the
    /// LEFTOVERS corner. <paramref name="fromChess"/> = judge reach as if the cart were already at the Sort Stop.
    /// </summary>
    LooseItem NextItem(bool fromChess, out bool leftover)
    {
        leftover = false;
        var table = Table;
        var arm = Arm;
        var track = Track;
        if (table == null || arm == null || track == null || track.SortStop == null) return null;
        Vector3 shift = fromChess ? track.transform.position - track.SortStop.position : Vector3.zero;
        var brain = LotBrain;
        float maxKg = brain.itemsPerMin <= 0f ? 0f : Level(ShopIds.MagnusGripper) >= 1 ? 25f : 5f;
        var compactor = rig.compactor;
        Vector3 claw = fromChess ? table.TopPoint(0f, 0f) : arm.ClawPosition;

        LooseItem best = null, bestLeft = null;
        float bestD = float.MaxValue, bestLeftD = float.MaxValue;
        var all = LooseItem.All;
        for (int i = 0; i < all.Count; i++)
        {
            var li = all[i];
            if (!Pickable(li) || !table.IsOnTop(li.transform.position)) continue;
            string uid = Uid(li);
            if (leftovers.Contains(uid)) continue;
            Vector3 p = li.Box != null ? li.Box.bounds.center : li.transform.position;
            if (!arm.CanReach(p + shift)) continue;
            float d = (p - claw).sqrMagnitude;
            bool can = CanSort(li.Category, brain) && li.MassKg <= maxKg;
            if (can && li.Category == ItemCategory.Junk && compactor != null && compactor.Busy) continue;
            if (can) { if (d < bestD) { bestD = d; best = li; } }
            else if (li.MassKg <= maxKg) { if (d < bestLeftD) { bestLeftD = d; bestLeft = li; } }
            else if (!fromChess && tooHeavySaid.Add(uid))
            {
                leftovers.Add(uid);
                GameHUD.FloatingText(p + Vector3.up * 0.4f, SorterLines.Heavy(uid), ChessBanter.BubbleColor, 0.8f);
            }
        }
        if (best != null) return best;
        leftover = bestLeft != null;
        return bestLeft;
    }

    static bool CanSort(ItemCategory c, UpgradeEffects.BrainTier brain)
    {
        switch (c)
        {
            case ItemCategory.Valuable: return brain.valuables;
            case ItemCategory.Hazard: return brain.hazards;
            default: return brain.recyclablesAndJunk;
        }
    }

    static bool Pickable(LooseItem li) =>
        li != null && li.Revealed && SortingKit.IsLoose(li) && !li.IsInScriptedFlight && li.Body != null && !li.Body.isKinematic
        && li.Body.linearVelocity.sqrMagnitude < 0.04f && li.Item != null;

    static string Uid(LooseItem li) => li.Item != null && !string.IsNullOrEmpty(li.Item.uid) ? li.Item.uid : li.GetInstanceID().ToString();

    // ------------------------------------------------------------------ doing it

    IEnumerator SortItem(LooseItem li)
    {
        float start = Time.time;
        float target = 60f / Mathf.Max(1f, LotContractItemsPerMin);
        var arm = Arm;
        var table = Table;
        arm.SpeedMultiplier = ArmSpeed(target);
        float speedUsed = arm.SpeedMultiplier;

        string uid = Uid(li);
        attempts.TryGetValue(uid, out int tries);
        attempts[uid] = tries + 1;
        if (attempts.Count > 256) attempts.Clear();
        float accuracy = LotBrain.accuracy;
        bool mistake = MistakeRoll(uid, tries, accuracy);
        var category = li.Category;
        var bin = mistake ? WrongBin(category, uid) : (category == ItemCategory.Junk ? null : rig.BinFor(category));
        if (mistake && bin == null) mistake = false;
        Vector3 binPos = bin != null ? bin.TossTargetPosition : rig.compactor != null ? rig.compactor.TossTargetPosition : table.TopPoint(0f, 0f);
        Vector3 release = ReleasePoint(binPos);

        bool delivered = false;
        var step = GripStep(li, release);
        step.canGrab = () => Pickable(li) || (li != null && li.State == CarryState.Thrown && !li.Body.isKinematic);
        step.onGrab = () => { if (li != null) li.SetInFlight(); };
        step.onPlaced = () =>
        {
            delivered = true;
            if (li == null || li.State == CarryState.Consumed) return;
            Vector3 spit = table.RandomTopPoint(0.3f);
            if (category == ItemCategory.Junk && !mistake && rig.compactor != null) rig.compactor.AcceptFromRobot(li);
            else if (bin != null) bin.AcceptFromRobot(li, spit);
            else li.EndCarry(Vector3.down, true);
            if (mistake) Say(SorterLines.Mistake(uid + tries));
            else if (category == ItemCategory.Valuable) Say(SorterLines.Kept(uid));
        };
        arm.PlayPlan(-1, new List<MagnusArm.Step> { step });
        yield return WaitArm(20f);

        if (!delivered)
        {
            // He closed on air (someone took it): don't count it, don't pad.
            if (li != null && li.State == CarryState.Thrown && li.Body != null && li.Body.isKinematic && !li.IsInScriptedFlight) li.EndCarry(Vector3.zero, true);
            yield break;
        }
        if (mistake) Mistakes++; else Sorted++;

        float took = Time.time - start;
        LearnSpeed(took, speedUsed);
        // Never faster than the contract: pad the cycle.
        if (took < target) yield return new WaitForSeconds(target - took);
        itemsTimed++;
        sortSeconds += Mathf.Max(took, target);
    }

    /// <summary>Leftovers: shove it to the corner and say so (once per item).</summary>
    IEnumerator PushLeftover(LooseItem li)
    {
        var arm = Arm;
        var table = Table;
        string uid = Uid(li);
        leftovers.Add(uid);
        Vector3 corner = LeftoversSpot(LeftoversPushed);
        arm.SpeedMultiplier = Mathf.Max(1f, ArmSpeed(60f / Mathf.Max(1f, LotContractItemsPerMin)));
        var step = GripStep(li, corner);
        bool placed = false;
        step.canGrab = () => Pickable(li);
        step.onGrab = () => { if (li != null) li.SetInFlight(); };
        step.onPlaced = () =>
        {
            placed = true;
            if (li == null || li.State == CarryState.Consumed) return;
            li.EndCarry(Vector3.down * 0.3f, false);
            Juice.Squash(li.JuiceTarget, 0.3f, 0.2f);
        };
        arm.PlayPlan(-1, new List<MagnusArm.Step> { step });
        yield return WaitArm(20f);
        if (!placed) yield break;
        LeftoversPushed++;
        GameHUD.FloatingText(corner + Vector3.up * 0.5f, "LEFTOVERS", SortingKit.ComboOrange, 0.9f);
        Say(SorterLines.Leftovers(uid));
    }

    /// <summary>
    /// Lobster Pincer (gripper L1) / brain L3: take a bag (the tray's, already in flight, or one off the tipping pile),
    /// carry it over the table and SLAM it open himself.
    /// </summary>
    IEnumerator RipBag(TrashBag bag, bool fromPile)
    {
        if (bag == null) yield break;
        var arm = Arm;
        var table = Table;
        Vector3 spot = table.ClampToTop(table.TopPoint(-table.topSize.x * 0.15f, 0f), 0.6f);
        bagInFlight = bag;
        Vector3 c = bag.WorldCenter;
        if (fromPile)
        {
            // "NEXT BAG!": it lobs across the garage into his claw's reach (the station's own Next bag! does the same).
            bag.SetInFlight();
            Vector3 catchAt = arm.ClampToReach(spot + Vector3.up * 0.55f);
            float dist = Vector3.Distance(c, catchAt);
            GameHUD.FloatingText(c + Vector3.up * 0.8f, "YOINK!", ChessBanter.BubbleColor, 1f);
            Sfx.Play("whoosh", c, 0.8f, 0.85f);
            Sfx.Play("beep", arm.HeadPosition, 0.4f, 1.5f);
            Fx.Dust(c, 0.6f);
            yield return SortingKit.Arc(bag.transform, catchAt - (bag.WorldCenter - bag.transform.position),
                                        Mathf.Clamp(0.45f + dist * 0.05f, 0.5f, 1.1f), Mathf.Clamp(dist * 0.22f, 1f, 2.4f));
            if (bag == null || bag.State == CarryState.Consumed) { bagInFlight = null; yield break; }
            Juice.Squash(bag.JuiceTarget, 0.3f, 0.2f);
        }
        else if (!arm.CanReach(c + Vector3.up * 0.1f))
        {
            // Out of reach on the tray? Yoink it over first (the animation can flex).
            Vector3 near = arm.ClampToReach(new Vector3(c.x, table.TopWorldY + 0.5f, c.z));
            GameHUD.FloatingText(c + Vector3.up * 0.5f, "YOINK!", ChessBanter.BubbleColor, 0.9f);
            Sfx.Play("whoosh", c, 0.6f, 1.5f);
            yield return SortingKit.Arc(bag.transform, near - (bag.WorldCenter - bag.transform.position), 0.35f, 0.4f);
            if (bag == null) { bagInFlight = null; yield break; }
        }
        arm.SpeedMultiplier = Mathf.Max(1.2f, armSpeed > 0f ? armSpeed : 1.2f);
        int sprayCount = bag.Contents != null && bag.Contents.items != null ? bag.Contents.items.Count : 0;
        var lb = bag.LocalBounds();
        var step = new MagnusArm.Step
        {
            piece = bag.transform,
            to = spot + Vector3.up * 0.45f,
            gripHeight = lb.center.y * bag.transform.lossyScale.y,
            gripWidth = Mathf.Clamp(Mathf.Min(lb.size.x, lb.size.z) * bag.transform.lossyScale.x, 0.15f, 0.6f),
            canGrab = () => bag != null && bag.State != CarryState.Consumed && bag.State != CarryState.Carried,
        };
        bool slammed = false;
        step.onPlaced = () =>
        {
            if (bag == null || bag.State == CarryState.Consumed) return;
            slammed = table.Slam(bag, spot, machine: true);
            if (!slammed) bag.EndCarry(Vector3.down, true);
        };
        arm.PlayPlan(-1, new List<MagnusArm.Step> { step });
        yield return WaitArm(20f);
        bagInFlight = null;
        if (!slammed)
        {
            // Never leave a bag hanging in the air (the arm timed out / closed on air): back to physics.
            if (bag != null && bag.State != CarryState.Consumed && bag.State != CarryState.Carried && bag.Body != null && bag.Body.isKinematic)
                bag.EndCarry(Vector3.down, true);
            yield break;
        }
        BagsRipped++;
        Say(SorterLines.Ripped(bag != null ? bag.name : BagsRipped.ToString()));
        yield return WaitSpraySettled(sprayCount);   // let the spray land, reveal and stop rolling
    }

    /// <summary>
    /// The next tipping-floor bag he'd yoink, like the station's "Next bag!": vision-flagged treasure bags first, then the
    /// top of the heap. Never one the compactor is about to eat, the tray's, a curb bag or one already on the table.
    /// Null while CRUSH ALL / SMASH is running.
    /// </summary>
    TrashBag NextPileBag()
    {
        var c = rig != null ? rig.compactor : null;
        if (c == null || c.Busy) return null;
        SortingKit.CarryablesInBox(c.pileCenter, c.pileSize * 0.5f + new Vector3(0.7f, 0.5f, 0.7f), Quaternion.identity, pileScratch);
        if (pileScratch.Count == 0) return null;
        c.Contents(hopperScratch);
        var table = Table;
        var tray = Tray;
        TrashBag best = null;
        float bestScore = float.MinValue;
        foreach (var cc in pileScratch)
        {
            if (!(cc is TrashBag b) || !Grabbable(b) || b.SourceStop != null || hopperScratch.Contains(b)) continue;
            if (tray != null && tray.Owns(b)) continue;
            Vector3 p = b.WorldCenter;
            if (table != null && table.IsOnTop(p)) continue;
            if (NearHopper(c, p)) continue;
            float score = (MagnusVisionFlags.IsFlagged(b.Contents, Level(ShopIds.MagnusVision)) ? 500f : 0f) + p.y * 10f;
            if (score > bestScore) { bestScore = score; best = b; }
        }
        return best;
    }

    static bool Grabbable(TrashBag b) =>
        b != null && b.State != CarryState.Carried && b.State != CarryState.Consumed && !b.IsInScriptedFlight
        && b.Body != null && !b.Body.isKinematic && !b.HasBurst;

    /// <summary>In or right above the compactor's chamber (including perched on the hopper rim): the compactor's.</summary>
    static bool NearHopper(Compactor c, Vector3 p)
    {
        var ch = c.chamber;
        if (ch == null) return false;
        Vector3 l = ch.InverseTransformPoint(p);
        Vector3 h = c.chamberSize * 0.5f;
        return Mathf.Abs(l.x) <= h.x + 0.3f && Mathf.Abs(l.z) <= h.z + 0.3f && l.y >= -h.y - 0.2f && l.y <= h.y + 1.0f;
    }

    bool NeedsSmash()
    {
        var c = rig.compactor;
        // Mk II auto-bales by itself; without it Magnus bonks SMASH when the hopper's full (never with an unopened bag in).
        return c != null && Level(ShopIds.CompactorMk2) < 1 && !c.Busy && c.UnconfirmedBags == 0 && c.HopperCount > 0 && c.Fill01 >= c.autoBaleFill;
    }

    IEnumerator PokeSmash()
    {
        var c = rig.compactor;
        CompactorButton smash = null;
        foreach (var b in c.GetComponentsInChildren<CompactorButton>()) if (b.mode == CompactorButton.Mode.Smash) { smash = b; break; }
        if (smash == null) yield break;
        var arm = Arm;
        var poke = new GameObject("MagnusPoke").transform;
        poke.position = arm.ClampToReach(smash.transform.position + smash.transform.forward * 0.12f);
        var step = new MagnusArm.Step { piece = poke, to = poke.position, gripHeight = 0f, gripWidth = 0.02f };
        step.onGrab = () => { smash.Press(); Say(SorterLines.Smashed("smash" + Sorted)); };
        arm.PlayPlan(-1, new List<MagnusArm.Step> { step });
        yield return WaitArm(10f);
        Destroy(poke.gameObject);
        yield return new WaitForSeconds(0.8f);
    }

    // ------------------------------------------------------------------ helpers

    MagnusArm.Step GripStep(LooseItem li, Vector3 to)
    {
        Bounds b = li.Box != null ? li.Box.bounds : SortingKit.RendererBounds(li.gameObject);
        float gripH = Mathf.Max(0.01f, b.center.y - li.transform.position.y);
        float width = Mathf.Clamp(Mathf.Min(b.size.x, b.size.z), 0.02f, 0.5f);
        return new MagnusArm.Step { piece = li.transform, to = to, gripHeight = gripH, gripWidth = width };
    }

    /// <summary>"Carries each item 0.6 m above toward its bin": as far toward the bin as his reach allows.</summary>
    Vector3 ReleasePoint(Vector3 binMouth)
    {
        var table = Table;
        Vector3 p = new Vector3(binMouth.x, Mathf.Max(table.TopWorldY + carryHeight, binMouth.y + 0.25f), binMouth.z);
        return Arm.ClampToReach(p);
    }

    Vector3 LeftoversSpot(int i)
    {
        var table = Table;
        if (leftoversCorner != null) return Arm.ClampToReach(leftoversCorner.position + new Vector3((i % 3 - 1) * 0.12f, 0f, (i / 3 % 2 - 0.5f) * 0.12f));
        // Front-right corner (the KEEP-crate end, nearest you when you step up).
        Vector3 p = table.TopPoint(table.topSize.x * 0.5f - 0.25f - (i % 3) * 0.14f, -table.topSize.y * 0.5f + 0.18f + (i / 3 % 2) * 0.14f);
        return Arm.ClampToReach(p + Vector3.up * 0.02f);
    }

    SortBin WrongBin(ItemCategory right, string uid)
    {
        var options = new List<SortBin>();
        foreach (var cat in new[] { ItemCategory.Metal, ItemCategory.Plastic, ItemCategory.Glass, ItemCategory.Paper })
        {
            if (cat == right) continue;
            var b = rig.BinFor(cat);
            if (b != null) options.Add(b);
        }
        if (options.Count == 0) return null;
        return options[GarageRoll.Index(uid + "/wrongbin", options.Count)];
    }

    /// <summary>Does this attempt at this item go in the wrong bin? Deterministic per (item, attempt): 1 - accuracy.</summary>
    public static bool MistakeRoll(string uid, int attempt, float accuracy) => GarageRoll.Chance($"{uid}/{attempt}/magnus_oops", 1f - accuracy);

    /// <summary>Arm speed so an average pick-and-place takes speedHeadroom × the contract cycle (then it's padded).</summary>
    float ArmSpeed(float targetCycle)
    {
        if (cycleAtX1 <= 0f) cycleAtX1 = baseCycleSeconds;
        armSpeed = Mathf.Clamp(cycleAtX1 / Mathf.Max(0.2f, targetCycle * speedHeadroom), 0.6f, maxArmSpeed);
        return armSpeed;
    }

    /// <summary>Learn how long a pick-and-place really takes at ×1 (running average of measured cycle × speed used).</summary>
    void LearnSpeed(float took, float speedUsed)
    {
        if (took <= 0.05f || speedUsed <= 0f) return;
        if (cycleAtX1 <= 0f) cycleAtX1 = baseCycleSeconds;
        cycleAtX1 = Mathf.Lerp(cycleAtX1, took * speedUsed, 0.3f);
    }

    IEnumerator WaitArm(float timeout)
    {
        var arm = Arm;
        float until = Time.time + timeout;
        yield return null;
        while (arm != null && arm.HasPendingPlans && Time.time < until) yield return null;
    }

    void Say(string line)
    {
        if (string.IsNullOrEmpty(line) || Arm == null) return;
        GameHUD.FloatingText(Arm.HeadPosition + Vector3.up * 0.35f, line, ChessBanter.BubbleColor, 0.85f);
        Sfx.Play("beep", Arm.HeadPosition, 0.3f, 1.7f);
    }
}
