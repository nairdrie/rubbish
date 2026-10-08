using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Cinemachine;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The FIRST-PERSON sort station. In third person the station only offers "Step up to the sorting table" (see
/// SortStationEntry + its InteractionZone); press E and the camera swoops into a first-person view at the table,
/// with whatever you were carrying held in your hands. From here you work the whole station:
///   empty hands  E/LMB item or bag in reach = grab it (flies into your hands; the Bag Belt's queue rack stands beside
///                the table for exactly this) · E/LMB SMASH / CRUSH ALL buttons. There is no "next bag" key: carry
///                bags over from the dump pile, or buy the Bag Belt.
///   holding bag  E/LMB table = SLAM it open (rips + sprays) · compactor = compact it WHOLE (warning, press again)
///                · a bin = "Rip it open on the table first!"
///   holding item E/LMB bin / keep crate / hazard barrel / compactor = toss it in · table or nothing = set it down
///   RMB = chuck the held thing along the view · Q / Esc = step away
/// Local player only (no netcode). Everything it touches on the player (locomotion, camera, renderers, cursor,
/// carrier hooks, interaction focus, HUD hint, crosshair) is restored on exit, OnDisable, despawn or UI lock.
/// Testers can drive it without real input: Enter(), Exit(), SetLook(), LookAt(), PrimaryAction(),
/// SecondaryThrow().
/// A table has one sorting position, or two with the Double-Wide table (sorting_table L2): <see cref="seat"/> 0 is the
/// original one (Magnus parks there on shift), seat 1 stands at the table's left end with its own camera, focus and hints, so a
/// player can sort beside Magnus or beside another player (SortSeats). Seat 0's entry zones hand out both seats.
/// </summary>
[DisallowMultipleComponent]
public class SortStation : MonoBehaviour
{
    /// <summary>The local player's active station, or any enabled station while idle (null = none).</summary>
    public static SortStation Current { get; private set; }
    /// <summary>True while the local player is working the station in first person.</summary>
    public static bool LocalInSortMode => Current != null && Current.IsActive;
    static readonly List<SortStation> stations = new List<SortStation>();

    [Header("Parts (built by Setup_SortingStation; missing ones are found/made at runtime)")]
    public SortingTable table;
    public Compactor compactor;
    public SortBin[] bins;
    [Tooltip("Feet position + facing (+Z toward the bins) while sorting.")]
    public Transform standPoint;
    [Tooltip("First-person virtual camera (component disabled, priority 0 while idle).")]
    public CinemachineVirtualCamera sortCam;
    public InteractionZone entryZone;
    [Tooltip("Which sorting position this is: 0 = the original one, 1 = the second one (needs sorting_table L2).")]
    public int seat;
    /// <summary>The other sorting position at the same table (GarageRig wires both ways; null = single-position table).</summary>
    [System.NonSerialized] public SortStation partner;

    /// <summary>
    /// Something working the station when no player is (Magnus on shift). While it blocks the stand point, stepping up
    /// shoos it instead (it heads off, and you can step up once it's clear).
    /// </summary>
    public interface IStationWorker
    {
        /// <summary>Standing on (or still too near) the stand point.</summary>
        bool BlocksStation { get; }
        /// <summary>Third-person prompt while it blocks, e.g. "Magnus is on shift. Shoo him".</summary>
        string BlockedPrompt { get; }
        /// <summary>A player wants the station: clear off.</summary>
        void Shoo();
    }

    /// <summary>The station's robot worker, if any (Magnus's sort job registers itself).</summary>
    [System.NonSerialized] public IStationWorker worker;

    bool WorkerBlocks => worker != null && worker.BlocksStation;

    [Header("View")]
    public float eyeHeight = 1.67f;
    [Tooltip("The eye leans a little over the table, in front of the feet pivot.")]
    public float eyeForward = 0.1f;
    public float fieldOfView = 64f;
    public float nearClip = 0.03f;
    [Tooltip("Yaw limits relative to the stand point's facing (compactor on the left, keep crate on the right).")]
    public float yawMin = -80f, yawMax = 72f;
    [Tooltip("Pitch limits in degrees, up positive.")]
    public float pitchMin = -68f, pitchMax = 15f;
    public float lookSensitivity = 1f;
    [Tooltip("Blend into / out of the first-person view (just for this camera; the brain's default blend is untouched).")]
    public float blendIn = 0.45f, blendOut = 0.4f;
    [Tooltip("Seconds the player glides onto the stand point after stepping up.")]
    public float approachTime = 0.3f;
    public int livePriority = 50;

    [Header("Interaction")]
    public float reach = 3.5f;
    [Tooltip("A second press within this many seconds compacts a whole, unopened bag.")]
    public float compactConfirmWindow = 3f;
    public float chuckSpeed = 8.5f;
    public KeyCode exitKey = KeyCode.Q;

    public enum TargetKind { None, Item, Table, Bin, Hopper, Button }

    /// <summary>True while the local player is in the first-person station view.</summary>
    public bool IsActive { get; private set; }
    public TargetKind CurrentTargetKind => target.kind;
    /// <summary>The E prompt being shown right now (null = none).</summary>
    public string CurrentPrompt { get; private set; }
    /// <summary>View yaw (relative to the station facing) and pitch (up positive), degrees.</summary>
    public float Yaw => yaw;
    public float Pitch => pitch;
    /// <summary>Holding a whole bag and the compactor's "press again" confirm is armed.</summary>
    public bool CompactArmed => carrier != null && CompactArmedFor(carrier.Held);
    public Transform Hand => hand;
    public Vector3 EyePosition => Eye;

    // ------------------------------------------------------------------ state

    struct Target
    {
        public TargetKind kind;
        public Carryable item;
        public SortBin bin;
        public CompactorButton button;
    }

    enum Mark { None, Grab, Toss, Slam, Warn, Bad, Button, Neutral }

    enum HintKind { None, Empty, EmptyItem, EmptyButton, BagTable, BagHopper, BagBin, BagButton, ItemToss, ItemSetDown, ItemButton, Other }

    Target target;
    Mark mark;
    CompactorButton[] buttons;

    PlayerController player;
    TrashCarrier carrier;
    StarterAssets.ThirdPersonController tpc;
    CharacterController cc;
    Animator animator;
    StarterAssets.StarterAssetsInputs inputs;
    bool tpcWasEnabled, ccWasEnabled;
    readonly List<Renderer> hiddenRenderers = new List<Renderer>();
    CursorLockMode prevLock;
    bool prevCursorVisible;

    float yaw, pitch, baseYaw;
    float kickPitch, kickTarget, kickDrop, dropTarget, fovKick, fovTarget;
    int enteredFrame = -1;
    float enterTime, lastExitTime = -99f;
    Vector3 approachFromPos, approachVia;
    Quaternion approachFromRot;
    bool approachHasVia;

    Transform hand;
    Carryable handFor;
    Vector3 handLocal;

    float compactArmedUntil = -1f, compactArmedAt = -99f; // unscaled time (hit-stops mustn't stretch the window)
    Carryable compactArmedFor;
    TrashBag pendingSlamBag;
    readonly List<Carryable> settingDown = new List<Carryable>();

    CompactorButton litButton;
    Carryable litItem;
    SortBin litBin;
    Object lastTargetObj;

    float pendingShowAt = -1f, pendingCamOffAt = -1f;
    bool viewReady; // crosshair + prompt wait until the swoop is mostly done
    string hintShown;
    HintKind hintKind = HintKind.None;
    Carryable hintFor;
    bool hintRevealed;
    Crosshair crosshair;
    readonly HashSet<LooseItem> seenReveals = new HashSet<LooseItem>();
    // Items that fall off the table onto the floor around the station are out of reach from the first-person
    // stand point (pitch/yaw clamps). Once one has rested there a moment, it hops back onto the table.
    readonly Dictionary<LooseItem, float> restingSince = new Dictionary<LooseItem, float>();
    readonly List<LooseItem> rescueScratch = new List<LooseItem>();
    const float RescueDelay = 1.5f;
    const float RescueRadius = 3f;
    float nextRescueScan;

    static readonly RaycastHit[] hits = new RaycastHit[32];
    static readonly RaycastHit[] losHits = new RaycastHit[16];
    static readonly List<Carryable> scratch = new List<Carryable>();
    static readonly IComparer<RaycastHit> ByDistance = Comparer<RaycastHit>.Create((a, b) => a.distance.CompareTo(b.distance));
    const int RayMask = ~(1 << 5); // everything but UI (receiver mouths live on Ignore Raycast, so no DefaultRaycastLayers)


    // ------------------------------------------------------------------ lifecycle

    void OnEnable()
    {
        if (!stations.Contains(this)) stations.Add(this);
        if (Current == null) Current = this;
        InstallBlendHook();
        TrashCarrier.CarryHintOverride = StationCarryHint;
    }

    void OnDisable()
    {
        if (IsActive) ExitInternal(true);
        FinishPendingRestore();
        FinishSetDowns();
        StopAllCoroutines();
        if (crosshair != null) crosshair.SetVisible(false);
        stations.Remove(this);
        if (Current == this) Current = stations.Find(s => s != null && s.isActiveAndEnabled);
        if (stations.Count == 0)
        {
            UninstallBlendHook();
            if (TrashCarrier.CarryHintOverride == (System.Func<TrashCarrier, string>)StationCarryHint)
                TrashCarrier.CarryHintOverride = null;
        }
    }

    // A click into the Editor's Game view (or back into the window) to refocus it isn't a slam / toss.
    float focusGainedAt = -99f;
    void OnApplicationFocus(bool focus)
    {
        if (focus) focusGainedAt = Time.unscaledTime;
    }

    const string StepUpCarryHint = "[E] Step up to sort   [Hold LMB] Throw   [G] Drop";
    const string BusyCarryHint = "[Hold LMB] Throw   [G] Drop";
    const string ShooCarryHint = "[E] Shoo Magnus   [Hold LMB] Throw   [G] Drop";
    const string PadStepUpCarryHint = "[X] Step up to sort   [Hold RT] Throw   [B] Drop";
    const string PadBusyCarryHint = "[Hold RT] Throw   [B] Drop";
    const string PadShooCarryHint = "[X] Shoo Magnus   [Hold RT] Throw   [B] Drop";
    InteractionZone[] entryZones;

    /// <summary>
    /// The carrier's third-person hint while you stand in a step-up zone: E steps up there (third-person tossing into
    /// the station is off), so "[E] Toss" would lie. Null = the carrier's default. No allocations (runs every frame).
    /// </summary>
    static string StationCarryHint(TrashCarrier c)
    {
        if (c == null || c.Player == null) return null;
        for (int i = 0; i < stations.Count; i++)
        {
            var s = stations[i];
            if (s == null || s.IsActive || !s.InEntryZone(c.Player.transform.position) || !s.CanOfferEntry) continue;
            if (!Permissions.LocalCanWorkAt(s.transform.position)) return GameControls.UsingGamepad ? "[X] Sorting table (not your shop)" : "[E] Sorting table (not your shop)";
            bool pad = GameControls.UsingGamepad;
            if (s.AssignSeat(c.Player.transform.position) != null) return pad ? PadStepUpCarryHint : StepUpCarryHint;
            return s.WorkerBlocks && !s.Occupied ? (pad ? PadShooCarryHint : ShooCarryHint) : (pad ? PadBusyCarryHint : BusyCarryHint);
        }
        return null;
    }

    /// <summary>Is this world position inside one of the station's "Step up" zones?</summary>
    public bool InEntryZone(Vector3 world)
    {
        if (entryZones == null)
        {
            var list = new List<InteractionZone>();
            if (entryZone != null) list.Add(entryZone);
            foreach (var e in GetComponentsInChildren<SortStationEntry>(true))
            {
                var z = e.GetComponent<InteractionZone>();
                if (z != null && !list.Contains(z)) list.Add(z);
            }
            entryZones = list.ToArray();
        }
        for (int i = 0; i < entryZones.Length; i++)
            if (entryZones[i] != null && entryZones[i].isActiveAndEnabled && entryZones[i].Contains(world)) return true;
        return false;
    }

    void OnDestroy()
    {
        if (crosshair != null) { crosshair.Destroy(); crosshair = null; }
    }

    void Update()
    {
        UpdatePendingRestore();
        if (!IsActive) return;

        if (!StillValid(out bool immediate))
        {
            ExitInternal(immediate);
            return;
        }

        float udt = Time.unscaledDeltaTime;
        UpdateApproach();
        bool inputOk = Time.frameCount != enteredFrame; // the E that stepped us up must not also act

        if (inputOk && (GameControls.Down(exitKey, GameControls.Action.StationExit) || Input.GetKeyDown(KeyCode.Escape) || GameControls.PadDown(GameControls.Action.Pause)))
        {
            Exit();
            return;
        }
        if (inputOk) ReadLook();

        DecayKicks(udt);
        PoseCamera();

        if (inputOk)
        {
            bool mouseOk = Time.unscaledTime - focusGainedAt > 0.2f;
            if (Input.GetKeyDown(IInteractable.InteractKey) || (mouseOk && (Input.GetMouseButtonDown(0) || GameControls.PadDown(GameControls.Action.SortPrimary)))) PrimaryAction();
            else if (mouseOk && (Input.GetMouseButtonDown(1) || GameControls.PadDown(GameControls.Action.SortSecondary))) SecondaryThrow();
        }
        if (!IsActive) return;

        PoseHand();
        UpdateTarget();
        // The "press again" confirm only holds while you keep the crosshair on the hopper with that bag.
        if (compactArmedFor != null && (target.kind != TargetKind.Hopper || carrier.Held != compactArmedFor)) DisarmCompact();
        WatchReveals();
        RescueTreasures();
        UpdateViewReady();
        UpdatePrompt();
        UpdateHint();
        if (crosshair != null) crosshair.Tick(mark, target.kind == TargetKind.Hopper && CompactArmed, lastTargetObj, udt);
    }

    /// <summary>
    /// The same floor rescue while nobody is at the station but its robot worker is (Magnus on shift, host): spray that
    /// bounces off the table while he works hops back instead of piling up on the floor. Rate-limited like the
    /// first-person one; returns how many hops started. No-op while a player is sorting here (their loop runs it).
    /// </summary>
    public int RescueFloorItems() => IsActive ? 0 : RescueTreasures();

    /// <summary>Any settled item lying on the floor around the table (the rescue's targets)? Lets Magnus clock in for it.</summary>
    public bool HasFloorItems()
    {
        if (table == null) return false;
        float floorLine = table.TopWorldY - 0.4f;
        Vector3 center = table.transform.position;
        var all = LooseItem.All;
        for (int i = 0; i < all.Count; i++)
            if (all[i] != null && OnFloorNearTable(all[i], floorLine, center) && Settled(all[i])) return true;
        return false;
    }

    bool OnFloorNearTable(LooseItem li, float floorLine, Vector3 center)
    {
        if (!SortingKit.IsLoose(li) || li.Body == null) return false;
        Vector3 p = li.transform.position;
        Vector3 d = p - center; d.y = 0f;
        return d.sqrMagnitude <= RescueRadius * RescueRadius && p.y < floorLine && !NearHopper(p) && !settingDown.Contains(li);
    }

    static bool Settled(LooseItem li) =>
        li != null && !li.IsInScriptedFlight && li.State == CarryState.Resting &&
        (RestingNetworkCopy(li) || (li.Body != null && !li.Body.isKinematic && li.Body.linearVelocity.sqrMagnitude < 0.05f));

    /// <summary>
    /// Hop items resting on the floor around the station back onto the table (see restingSince). Never touches
    /// what's in the compactor hopper (that's junk waiting for SMASH) or anything held / flying / being eaten.
    /// </summary>
    int RescueTreasures()
    {
        if (table == null || Time.time < nextRescueScan) return 0;
        nextRescueScan = Time.time + 0.25f;

        int hops = 0;
        float floorLine = table.TopWorldY - 0.4f;
        Vector3 center = table.transform.position;
        var all = LooseItem.All;
        for (int i = 0; i < all.Count; i++)
        {
            var li = all[i];
            if (li == null) continue;
            if (!OnFloorNearTable(li, floorLine, center) || !Settled(li)) { restingSince.Remove(li); continue; }
            if (!restingSince.TryGetValue(li, out float since)) { restingSince[li] = Time.time; continue; }
            if (Time.time - since < RescueDelay) continue;

            restingSince.Remove(li);
            StartCoroutine(RescueHop(li, table.RandomTopPoint(0.25f)));
            hops++;
        }

        // Drop entries for items that were destroyed / consumed meanwhile.
        if (restingSince.Count > 32)
        {
            rescueScratch.Clear();
            foreach (var kv in restingSince) if (kv.Key == null) rescueScratch.Add(kv.Key);
            for (int i = 0; i < rescueScratch.Count; i++) restingSince.Remove(rescueScratch[i]);
        }
        return hops;
    }

    /// <summary>
    /// Scripted (kinematic) hop from the floor onto the tabletop: straight up past the lip / splash guard first, then
    /// over onto the top — a physics launch would just bonk into the table's apron when the item lies against it.
    /// </summary>
    IEnumerator RescueHop(LooseItem li, Vector3 spot)
    {
        if (li == null || table == null) yield break;
        li.SetInFlight();
        settingDown.Add(li);
        Vector3 from = li.transform.position;
        Sfx.Play("pop", from, 0.5f, 1.3f);
        Fx.Dust(from, 0.3f);
        if (li.JuiceTarget != null) Juice.Squash(li.JuiceTarget, -0.3f, 0.2f);
        Vector3 up = from;
        up.y = table.TopWorldY + 0.45f;
        yield return SortingKit.Arc(li.transform, up, 0.2f, 0f);
        if (li == null || li.State == CarryState.Consumed || li.State == CarryState.Carried) { settingDown.Remove(li); yield break; }
        Quaternion rot = Quaternion.Euler(0f, li.transform.eulerAngles.y + Random.Range(-60f, 60f), 0f);
        Vector3 pivot = SetDownPivot(li, spot, rot);
        float dist = Vector3.Distance(li.transform.position, pivot);
        yield return SortingKit.Arc(li.transform, pivot, Mathf.Clamp(0.18f + dist * 0.12f, 0.2f, 0.45f), 0.25f, rot);
        settingDown.Remove(li);
        if (li == null || li.State == CarryState.Consumed || li.State == CarryState.Carried) yield break;
        li.EndCarry(Vector3.down * 0.4f, false);
        Sfx.Play("thud", spot, 0.4f, 1.3f);
        if (li.JuiceTarget != null) Juice.Squash(li.JuiceTarget, 0.2f, 0.18f);
    }

    /// <summary>The crosshair, prompt and hint line appear once the swoop into first person is mostly done.</summary>
    void UpdateViewReady()
    {
        if (viewReady) return;
        if (Time.time - enterTime < blendIn * 0.6f) return;
        viewReady = true;
        EnsureCrosshair();
        crosshair.SetVisible(true);
    }

    bool StillValid(out bool immediate)
    {
        immediate = true;
        if (player == null || !player.isActiveAndEnabled) return false; // despawned / destroyed
        if (player != PlayerController.Local || (player.NetworkManager != null && !player.IsSpawned)) return false;
        if (sortCam == null || standPoint == null) return false;
        immediate = false;
        // A fired/quit employee loses the station on the next frame, before any more local takes can be predicted.
        if (!Permissions.LocalCanWorkAt(standPoint.position)) { Permissions.Deny(standPoint.position); return false; }
        if (player.IsDriving || player.IsPlayingChess) return false;
        if (PlayerController.IsUIBlocking) return false;
        if (SceneController.IsTransitioning) return false;
        return true;
    }

    // ------------------------------------------------------------------ enter / exit

    /// <summary>Can the local player step up right now? (False while someone else, or Magnus, is sorting here.)</summary>
    public bool CanEnter => CanOfferEntry && !Occupied && !WorkerBlocks;

    /// <summary>Does this position exist at the table's level? Position 1 always; position 2 with sorting_table L2.</summary>
    public bool SeatOpen => SortSeats.SeatExists(seat, DepotMachineContext.Level(this, ShopIds.SortingTable));

    /// <summary>The local player's crosshair is on this item at some sorting position on this machine (Magnus leaves it).</summary>
    public static bool AnyPlayerAimsAt(Carryable item)
    {
        if (item == null) return false;
        for (int i = 0; i < stations.Count; i++)
        {
            var s = stations[i];
            if (s != null && s.IsActive && s.target.kind == TargetKind.Item && s.target.item == item) return true;
        }
        return false;
    }

    /// <summary>
    /// Which sorting position a player walking up from <paramref name="from"/> gets: this one or its partner (the nearer
    /// free one; SortSeats.Assign), or null when every position is taken. Call it on position 1 (it owns the entry zones).
    /// </summary>
    public SortStation AssignSeat(Vector3 from)
    {
        var first = seat == 0 ? this : partner;
        var second = seat == 0 ? partner : this;
        if (first == null) return CanEnter && SeatOpen ? this : null;
        int level = DepotMachineContext.Level(first, ShopIds.SortingTable);
        bool hasSecond = second != null && second.isActiveAndEnabled && second.standPoint != null;
        int preferred = 0;
        if (hasSecond && first.standPoint != null)
            preferred = (from - second.standPoint.position).sqrMagnitude < (from - first.standPoint.position).sqrMagnitude ? 1 : 0;
        int pick = SortSeats.Assign(preferred, hasSecond ? level : 0, first.Occupied, first.WorkerBlocks, hasSecond && second.Occupied);
        return pick == 0 ? first : pick == 1 ? second : null;
    }

    /// <summary>Should the entry zone offer "Step up" at all? (It still shows while the spot is taken, to say so.)</summary>
    public bool CanOfferEntry
    {
        get
        {
            if (IsActive || !isActiveAndEnabled) return false;
            if (partner != null && partner.IsActive) return false; // already sorting at the other position
            var p = PlayerController.Local;
            if (p == null || !p.IsOwner || p.IsDriving || p.IsPlayingChess) return false;
            if (PlayerController.IsUIBlocking || SceneController.IsTransitioning) return false;
            if (Time.unscaledTime - lastExitTime < 0.3f) return false; // don't bounce straight back in
            return true;
        }
    }

    float occupiedCheckAt = -1f;
    bool occupiedCached;

    /// <summary>Another player is standing on the stand point (co-op: they're sorting). Checked a few times a second.</summary>
    public bool Occupied
    {
        get
        {
            if (Time.unscaledTime < occupiedCheckAt) return occupiedCached;
            occupiedCheckAt = Time.unscaledTime + 0.25f;
            occupiedCached = false;
            if (standPoint == null) return false;
            var local = PlayerController.Local;
            Vector3 sp = standPoint.position;
            foreach (var pc in FindObjectsByType<PlayerController>(FindObjectsSortMode.None))
            {
                if (pc == null || pc == local || !pc.isActiveAndEnabled || pc.IsDriving || pc.IsPlayingChess) continue;
                Vector3 d = pc.transform.position - sp;
                if (Mathf.Abs(d.y) < 1.5f && new Vector2(d.x, d.z).sqrMagnitude < 0.7f * 0.7f) { occupiedCached = true; break; }
            }
            return occupiedCached;
        }
    }

    /// <summary>Third-person prompt for the entry zone, depending on what you carry.</summary>
    public string EntryPrompt
    {
        get
        {
            var pick = AssignSeat(SortingKit.TryGetLocalPlayer(out Vector3 me) ? me : transform.position);
            if (pick == null)
            {
                bool twoSpots = partner != null && partner.SeatOpen;
                if (Occupied) return twoSpots ? "Both sorting spots are taken!" : "Someone's sorting here!";
                return WorkerBlocks ? worker.BlockedPrompt : "Both sorting spots are taken!";
            }
            var held = SortingKit.LocalHeld;
            string prompt = held is TrashBag ? "Step up with the bag" : held != null ? "Step up to sort it" : "Step up to the sorting table";
            if (pick != this) prompt += WorkerBlocks && !Occupied ? " (beside Magnus)" : Occupied ? " (second spot)" : " (left-hand spot)";
            string robot = (worker as MagnusSorter)?.IdleStatusHint;
            return string.IsNullOrEmpty(robot) ? prompt : prompt + "\n" + robot;
        }
    }

    /// <summary>E in the entry zone: step up, or say why not.</summary>
    public void TryEnterFromZone()
    {
        var pick = AssignSeat(SortingKit.TryGetLocalPlayer(out Vector3 me) ? me : transform.position);
        if (pick != null)
        {
            if (pick.CanEnter && pick.SeatOpen) pick.Enter();
            return;
        }
        if (Occupied)
        {
            GameHUD.Toast(partner != null && partner.SeatOpen ? "Both sorting spots are taken! Wait your turn" : "Someone's already sorting! Wait your turn", ToastStyle.Bad, 1.6f);
            if (standPoint != null) Sfx.Play("buzz", standPoint.position + Vector3.up, 0.5f, 1.2f);
            return;
        }
        if (WorkerBlocks) worker.Shoo();
    }

    /// <summary>Step up: swoop into the first-person station view (keeps whatever you carry, now in your hands).</summary>
    public bool Enter()
    {
        if (IsActive) return true;
        if (!CanEnter || !SeatOpen) return false;
        EnsureParts();
        if (sortCam == null || standPoint == null)
        {
            Debug.LogWarning("[SortStation] Missing SortCam / stand point - can't step up.");
            return false;
        }
        // SortStationEntry denies visitors, but Enter is also a public tester/automation method.
        if (!Permissions.LocalCanWorkAt(standPoint.position)) { Permissions.Deny(standPoint.position); return false; }
        // Depot Row has four enabled stations. The one this player entered owns camera blending, focus and sort hints.
        Current = this;
        FinishPendingRestore();

        var p = PlayerController.Local;
        player = p;
        carrier = p.GetComponent<TrashCarrier>();
        tpc = p.GetComponent<StarterAssets.ThirdPersonController>();
        cc = p.GetComponent<CharacterController>();
        animator = p.GetComponent<Animator>();
        inputs = p.GetComponent<StarterAssets.StarterAssetsInputs>();

        // Freeze locomotion + third-person look (like the truck). The animator keeps idling for other players.
        tpcWasEnabled = tpc != null && tpc.enabled;
        if (tpc != null) tpc.enabled = false;
        ccWasEnabled = cc != null && cc.enabled;
        if (cc != null) cc.enabled = false;
        IdleAnimator();
        ClearInputs();

        // Hide our own body (locally only: other players still see you standing at the table).
        hiddenRenderers.Clear();
        foreach (var r in p.GetComponentsInChildren<Renderer>(true))
            if (r != null && r.enabled) { r.enabled = false; hiddenRenderers.Add(r); }

        // View: facing the bins, crosshair on the middle of the table.
        baseYaw = standPoint.eulerAngles.y;
        yaw = 0f;
        pitch = DefaultPitch();
        kickPitch = kickTarget = kickDrop = dropTarget = fovKick = fovTarget = 0f;
        PoseCamera();
        handFor = null;
        PoseHand();
        sortCam.enabled = true;
        sortCam.Priority = livePriority; // the brain blends in via our blend override (blendIn)

        prevLock = Cursor.lockState;
        prevCursorVisible = Cursor.visible;
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        if (carrier != null)
        {
            carrier.SetExternalControl(true);
            carrier.SetHoldOverride(hand, sortCam.transform, 1.7f);
        }
        PlayerController.InteractionFocusOverride = FocusOverride;
        var tt = TooltipUI.Instance;
        if (tt != null) tt.Hide();
        GameHUD.SetContextHint(null); // the carrier's third-person hint; ours shows once the view is in

        approachFromPos = p.transform.position;
        approachFromRot = p.transform.rotation;
        PlanApproach();
        enterTime = Time.time;
        enteredFrame = Time.frameCount;
        DisarmCompact();
        target = default;
        mark = Mark.None;
        CurrentPrompt = null;
        hintShown = null;
        hintKind = HintKind.None;
        hintFor = null;
        lastTargetObj = null;
        viewReady = false;
        restingSince.Clear();
        IsActive = true;

        seenReveals.Clear();
        var all = LooseItem.All;
        for (int i = 0; i < all.Count; i++) if (all[i] != null && all[i].Revealed) seenReveals.Add(all[i]);

        EnsureCrosshair();
        crosshair.SetVisible(false); // UpdateViewReady shows it once the swoop is mostly in
        Sfx.Play("whoosh", Eye, 0.35f, 1.35f);
        return true;
    }

    /// <summary>Step away: back to third person. A held item is set down on the table; a held bag stays in your arms.</summary>
    public void Exit() => ExitInternal(false);

    void ExitInternal(bool immediate)
    {
        if (!IsActive) return;
        IsActive = false;
        if (pendingSlamBag != null && table != null) table.CancelAdmission(pendingSlamBag);
        pendingSlamBag = null;
        lastExitTime = Time.unscaledTime;
        if (!isActiveAndEnabled) immediate = true;

        // Held stuff: loose items go back on the table, bags stay in your arms (normal chest hold).
        if (carrier != null)
        {
            // A just-picked network item is still in its heave window. Keep it in normal third-person hands
            // instead of scripting a set-down before the host has acknowledged the grab.
            if (carrier.Held is LooseItem && ReleaseConfirmed())
            {
                var c = carrier.ReleaseToScript();
                if (c != null)
                {
                    if (immediate) PlaceOnTableNow(c, DefaultTableSpot());
                    else StartCoroutine(SetDownRoutine(c, DefaultTableSpot()));
                }
            }
            carrier.ClearHoldOverride();
            carrier.SetExternalControl(false);
        }
        DisarmCompact();
        ClearHighlights();
        target = default;
        mark = Mark.None;
        CurrentPrompt = null;

        // Seated in the truck / at chess (that's why we're leaving): they own the body, camera and locomotion now.
        bool seated = player != null && (player.IsDriving || player.IsPlayingChess);

        // Camera: back to the third-person camera, looking where we looked.
        if (!seated) AlignThirdPerson();
        if (sortCam != null) sortCam.Priority = 0;
        if (immediate)
        {
            if (sortCam != null) sortCam.enabled = false;
            pendingCamOffAt = -1f;
        }
        else pendingCamOffAt = Time.unscaledTime + blendOut + 0.25f;

        // Player: locomotion back on (the CharacterController syncs to where we stand) — not when seated: an enabled
        // CharacterController inside the cab shoves the truck around.
        if (!seated)
        {
            if (cc != null) cc.enabled = ccWasEnabled;
            if (tpc != null) tpc.enabled = tpcWasEnabled;
        }
        ClearInputs();
        // Body reappears a beat into the blend so the camera doesn't pull out through the head.
        if (immediate) ShowHiddenRenderers();
        else pendingShowAt = Time.unscaledTime + 0.12f;

        if (!PlayerController.IsUIBlocking)
        {
            Cursor.lockState = prevLock;
            Cursor.visible = prevCursorVisible;
        }
        if (PlayerController.InteractionFocusOverride == (System.Func<PlayerController, bool>)FocusOverride)
            PlayerController.InteractionFocusOverride = null;
        var tt = TooltipUI.Instance;
        if (tt != null) tt.Hide();
        SetHint(null);
        viewReady = false;
        restingSince.Clear();
        if (crosshair != null) crosshair.SetVisible(false);
        if (!immediate && standPoint != null) Sfx.Play("whoosh", Eye, 0.3f, 1.1f);
    }

    static bool FocusOverride(PlayerController p) => Current != null && Current.IsActive && Current.player == p;

    /// <summary>
    /// Stepping up from round the table's end (the east zone by the KEEP crate): glide round the table's front corner
    /// instead of straight through it (other players see the body; locally it's hidden).
    /// </summary>
    void PlanApproach()
    {
        approachHasVia = false;
        if (table == null || standPoint == null) return;
        var tt = table.transform;
        Vector3 a = tt.InverseTransformPoint(approachFromPos), b = tt.InverseTransformPoint(standPoint.position);
        float hx = table.topSize.x * 0.5f, hz = table.topSize.y * 0.5f;
        Vector2 off = table.topOffset; // the XL leaf moves the top's centre toward the sorter
        bool crosses = false;
        for (int i = 1; i < 12 && !crosses; i++)
        {
            Vector3 q = Vector3.Lerp(a, b, i / 12f);
            crosses = Mathf.Abs(q.x - off.x) < hx + 0.25f && Mathf.Abs(q.z - off.y) < hz + 0.25f;
        }
        if (!crosses) return;
        float side = a.x >= off.x ? 1f : -1f;
        approachVia = tt.TransformPoint(off.x + side * (hx + 0.45f), 0f, table.FrontEdgeZ - 0.45f);
        approachVia.y = standPoint.position.y;
        approachHasVia = true;
    }

    void UpdateApproach()
    {
        if (player == null || standPoint == null) return;
        float time = approachHasVia ? approachTime * 1.6f : approachTime;
        float k = time <= 0f ? 1f : Mathf.Clamp01((Time.time - enterTime) / time);
        if (k >= 1f && (player.transform.position - standPoint.position).sqrMagnitude < 1e-6f) return;
        float s = k * k * (3f - 2f * k);
        Vector3 pos;
        if (!approachHasVia) pos = Vector3.Lerp(approachFromPos, standPoint.position, s);
        else
        {
            // Two straight legs, split by length.
            float l1 = Vector3.Distance(approachFromPos, approachVia), l2 = Vector3.Distance(approachVia, standPoint.position);
            float d = s * (l1 + l2);
            pos = d <= l1 ? Vector3.Lerp(approachFromPos, approachVia, l1 > 1e-4f ? d / l1 : 1f)
                          : Vector3.Lerp(approachVia, standPoint.position, l2 > 1e-4f ? (d - l1) / l2 : 1f);
        }
        player.transform.SetPositionAndRotation(pos, Quaternion.Slerp(approachFromRot, Quaternion.Euler(0f, baseYaw, 0f), s));
    }

    static FieldInfo tpcYawField, tpcPitchField;
    static bool tpcFieldsLooked;

    /// <summary>
    /// Face the body where the first-person view was looking, and put the third-person camera behind it — turned or
    /// raised a bit if that spot is inside something (the parked truck: the follow camera only collides with walls).
    /// </summary>
    void AlignThirdPerson()
    {
        if (player == null) return;
        float worldYaw = baseYaw + yaw;
        player.transform.rotation = Quaternion.Euler(0f, worldYaw, 0f);
        if (tpc == null) return;
        float camYaw = worldYaw, pitch3P = 12f;
        ClearThirdPersonView(ref camYaw, ref pitch3P);
        if (!tpcFieldsLooked)
        {
            tpcFieldsLooked = true;
            var t = typeof(StarterAssets.ThirdPersonController);
            tpcYawField = t.GetField("_cinemachineTargetYaw", BindingFlags.NonPublic | BindingFlags.Instance);
            tpcPitchField = t.GetField("_cinemachineTargetPitch", BindingFlags.NonPublic | BindingFlags.Instance);
        }
        try
        {
            if (tpcYawField != null && tpcYawField.FieldType == typeof(float)) tpcYawField.SetValue(tpc, camYaw);
            if (tpcPitchField != null && tpcPitchField.FieldType == typeof(float)) tpcPitchField.SetValue(tpc, pitch3P);
        }
        catch (System.Exception e) { Debug.LogWarning("[SortStation] Couldn't align the third-person camera: " + e.Message); }
        if (tpc.CinemachineCameraTarget != null)
            tpc.CinemachineCameraTarget.transform.rotation = Quaternion.Euler(pitch3P + tpc.CameraAngleOverride, camYaw, 0f);
    }

    static readonly float[] ClearYawOffsets = { 0f, 25f, -25f, 50f, -50f, 75f, -75f, 105f, -105f };
    static readonly float[] ClearPitches = { 12f, 24f, 36f };
    static readonly RaycastHit[] camHits = new RaycastHit[16];

    /// <summary>
    /// Find a third-person camera yaw/pitch (starting from the given one) whose rig line — follow target → shoulder →
    /// camera, as Cinemachine3rdPersonFollow lays it out — isn't blocked by Default/Interactable geometry (the truck).
    /// Leaves the values alone when there's no follow rig or nothing is clear.
    /// </summary>
    void ClearThirdPersonView(ref float camYaw, ref float camPitch)
    {
        var root = tpc != null ? tpc.CinemachineCameraTarget : null;
        var rig = root != null ? FollowRigFor(root.transform) : null;
        if (rig == null) return;
        Vector3 r = root.transform.position;
        foreach (float p in ClearPitches)
            foreach (float dy in ClearYawOffsets)
            {
                float y = camYaw + dy;
                if (!RigBlocked(rig, r, y, p)) { camYaw = y; camPitch = p; return; }
            }
    }

    static Cinemachine3rdPersonFollow FollowRigFor(Transform followTarget)
    {
        var vcams = FindObjectsByType<CinemachineVirtualCamera>(FindObjectsSortMode.None);
        foreach (var v in vcams)
        {
            if (v == null || v.Follow != followTarget) continue;
            var f = v.GetCinemachineComponent<Cinemachine3rdPersonFollow>();
            if (f != null) return f;
        }
        return null;
    }

    bool RigBlocked(Cinemachine3rdPersonFollow f, Vector3 root, float yawDeg, float pitchDeg)
    {
        Quaternion heading = Quaternion.Euler(0f, yawDeg, 0f);
        Quaternion rot = Quaternion.Euler(pitchDeg + (tpc != null ? tpc.CameraAngleOverride : 0f), yawDeg, 0f);
        Vector3 shoulderOffset = f.ShoulderOffset;
        shoulderOffset.x = Mathf.Lerp(-shoulderOffset.x, shoulderOffset.x, f.CameraSide);
        Vector3 shoulder = root + heading * shoulderOffset;
        Vector3 hand = shoulder + rot * new Vector3(0f, f.VerticalArmLength, 0f);
        Vector3 cam = hand - rot * Vector3.forward * f.CameraDistance;
        return SegmentBlocked(root, shoulder) || SegmentBlocked(shoulder, hand) || SegmentBlocked(hand, cam);
    }

    bool SegmentBlocked(Vector3 a, Vector3 b)
    {
        Vector3 d = b - a;
        float len = d.magnitude;
        if (len < 0.01f) return false;
        const int mask = (1 << 0) | (1 << 3); // Default + Interactable (the truck); walls are the follow camera's job
        int n = Physics.SphereCastNonAlloc(a, 0.25f, d / len, camHits, len, mask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var col = camHits[i].collider;
            if (col == null || col is CharacterController) continue;
            if (player != null && col.transform.IsChildOf(player.transform)) continue;
            if (col.GetComponentInParent<Carryable>() != null) continue; // junk doesn't block a camera
            return true;
        }
        return false;
    }

    void IdleAnimator()
    {
        if (animator == null || !animator.isActiveAndEnabled || animator.runtimeAnimatorController == null) return;
        foreach (var prm in animator.parameters)
        {
            if (prm.type == AnimatorControllerParameterType.Float)
            {
                if (prm.name == "Speed") animator.SetFloat(prm.nameHash, 0f);
                else if (prm.name == "MotionSpeed") animator.SetFloat(prm.nameHash, 1f);
            }
            else if (prm.type == AnimatorControllerParameterType.Bool)
            {
                if (prm.name == "Grounded") animator.SetBool(prm.nameHash, true);
                else if (prm.name == "Jump" || prm.name == "FreeFall") animator.SetBool(prm.nameHash, false);
            }
        }
    }

    /// <summary>
    /// Drop the look delta and a pending jump (so neither view jumps). Move/sprint stay as the input system has them:
    /// PlayerInput uses SendMessages, which only reports changes, so zeroing them would leave a still-held W dead.
    /// (The ThirdPersonController is disabled while sorting, so a held key does nothing in here.)
    /// </summary>
    void ClearInputs()
    {
        if (inputs == null) return;
        inputs.look = Vector2.zero;
        inputs.jump = false;
    }

    void ShowHiddenRenderers()
    {
        pendingShowAt = -1f;
        bool stayHidden = player != null && (player.IsDriving || player.IsPlayingChess); // the truck/chess own them now
        foreach (var r in hiddenRenderers)
            if (r != null && !stayHidden) r.enabled = true;
        hiddenRenderers.Clear();
    }

    void UpdatePendingRestore()
    {
        if (pendingShowAt > 0f && Time.unscaledTime >= pendingShowAt) ShowHiddenRenderers();
        if (pendingCamOffAt > 0f && Time.unscaledTime >= pendingCamOffAt)
        {
            pendingCamOffAt = -1f;
            if (!IsActive && sortCam != null) sortCam.enabled = false;
        }
    }

    void FinishPendingRestore()
    {
        if (!IsActive && (pendingShowAt > 0f || hiddenRenderers.Count > 0)) ShowHiddenRenderers();
        if (pendingCamOffAt > 0f)
        {
            pendingCamOffAt = -1f;
            if (!IsActive && sortCam != null) sortCam.enabled = false;
        }
    }

    // ------------------------------------------------------------------ view

    Vector3 Eye => standPoint != null ? standPoint.TransformPoint(0f, eyeHeight, eyeForward) : transform.position + Vector3.up * eyeHeight;

    void ReadLook()
    {
        Vector2 d;
        var pi = player != null ? player.GetComponent<UnityEngine.InputSystem.PlayerInput>() : null;
        if (pi != null && pi.currentControlScheme == "Gamepad" && UnityEngine.InputSystem.Gamepad.current != null)
        {
            // The walking Look action is 300× stick for a per-second third-person camera. The table reads
            // degrees per frame, so use the raw stick and apply its own sensible turn rate here.
            d = UnityEngine.InputSystem.Gamepad.current.rightStick.ReadValue() * (130f * Time.unscaledDeltaTime * GameSettings.LookSensitivity);
            if (GameSettings.InvertY) d.y = -d.y;
        }
        else if (inputs != null && inputs.cursorInputForLook) d = inputs.look; // mouse; LookApplier already scaled it
        else d = new Vector2(Input.GetAxis("Mouse X"), -Input.GetAxis("Mouse Y") * (GameSettings.InvertY ? -1f : 1f)) * (0.5f * GameSettings.LookSensitivity);
        if (d.sqrMagnitude < 1e-8f) return;
        d *= lookSensitivity;
        yaw = Mathf.Clamp(yaw + d.x, yawMin, yawMax);
        pitch = Mathf.Clamp(pitch - d.y, pitchMin, pitchMax);
    }

    /// <summary>Tester hook: aim the view (degrees; yaw relative to the station facing, pitch up positive).</summary>
    public void SetLook(float newYaw, float newPitch)
    {
        yaw = Mathf.Clamp(newYaw, yawMin, yawMax);
        pitch = Mathf.Clamp(newPitch, pitchMin, pitchMax);
        if (!IsActive) return;
        PoseCamera();
        PoseHand();
        UpdateTarget();
        UpdatePrompt();
    }

    /// <summary>Tester hook: aim the crosshair at a world point (clamped to the look limits).</summary>
    public void LookAt(Vector3 world)
    {
        Vector3 d = world - Eye;
        float flat = new Vector2(d.x, d.z).magnitude;
        float worldYaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        SetLook(Mathf.DeltaAngle(baseYaw, worldYaw), Mathf.Atan2(d.y, Mathf.Max(0.001f, flat)) * Mathf.Rad2Deg);
    }

    float DefaultPitch()
    {
        if (table == null) return -28f;
        Vector3 eye = Eye;
        Vector3 l = table.transform.InverseTransformPoint(eye);
        float hx = table.topSize.x * 0.4f;
        Vector3 aim = table.transform.TransformPoint(Mathf.Clamp(l.x, -hx, hx), table.topHeight, 0f);
        Vector3 d = aim - eye;
        float flat = new Vector2(d.x, d.z).magnitude;
        return Mathf.Clamp(Mathf.Atan2(d.y, Mathf.Max(0.01f, flat)) * Mathf.Rad2Deg, pitchMin, pitchMax);
    }

    void PoseCamera()
    {
        if (sortCam == null || standPoint == null) return;
        sortCam.transform.SetPositionAndRotation(Eye + Vector3.down * kickDrop, Quaternion.Euler(-pitch + kickPitch, baseYaw + yaw, 0f));
        var lens = sortCam.m_Lens;
        lens.FieldOfView = fieldOfView + GameSettings.FovDelta + fovKick * GameSettings.ScreenFx01; // Settings: FOV + screen effects
        lens.NearClipPlane = nearClip;
        sortCam.m_Lens = lens;
    }

    /// <summary>Hands follow the view partway (half the pitch) so the held thing sits low in the frame.</summary>
    Quaternion HandRotation => Quaternion.Euler(-pitch * 0.5f + kickPitch * 0.6f, baseYaw + yaw, 0f);

    void PoseHand()
    {
        if (hand == null || standPoint == null) return;
        var held = carrier != null ? carrier.Held : null;
        Vector3 local = HandLocalFor(held);
        float t = Time.time;
        local += new Vector3(Mathf.Sin(t * 1.3f) * 0.004f, Mathf.Sin(t * 2.1f) * 0.006f, 0f); // breathing
        Quaternion rot = HandRotation;
        hand.SetPositionAndRotation(Eye + Vector3.down * kickDrop + rot * local, rot);
    }

    /// <summary>Where the held thing's center sits relative to the eye (by its size: bags low & centered, bits low-right).</summary>
    Vector3 HandLocalFor(Carryable c)
    {
        if (c == null) return new Vector3(0.12f, -0.3f, 0.5f);
        if (c == handFor) return handLocal;
        handFor = c;
        var lb = c.LocalBounds();
        Vector3 s = c.transform.lossyScale;
        Vector3 ext = new Vector3(Mathf.Abs(lb.extents.x * s.x), Mathf.Abs(lb.extents.y * s.y), Mathf.Abs(lb.extents.z * s.z));
        float flat = Mathf.Max(ext.x, ext.z);
        if (c is TrashBag) handLocal = new Vector3(0.04f, -(0.24f + ext.y), 0.34f + flat);
        else handLocal = new Vector3(0.17f, -(0.15f + Mathf.Max(ext.y, flat * 0.5f)), 0.36f + flat);
        return handLocal;
    }

    /// <summary>Camera punch. +pitch nods down (slam / catch), − flicks up (wind-up / toss). drop = metres the view dips.</summary>
    public void Kick(float pitchDeg, float drop = 0f)
    {
        kickTarget = Mathf.Clamp(kickTarget + pitchDeg, -8f, 8f);
        dropTarget = Mathf.Clamp(dropTarget + drop, 0f, 0.08f);
    }

    /// <summary>Brief field-of-view widening (big reveals).</summary>
    public void FovKick(float degrees) => fovTarget = Mathf.Max(fovTarget, degrees);

    void DecayKicks(float udt)
    {
        kickPitch = Mathf.Lerp(kickPitch, kickTarget, 1f - Mathf.Exp(-35f * udt));
        kickTarget *= Mathf.Exp(-10f * udt);
        kickDrop = Mathf.Lerp(kickDrop, dropTarget, 1f - Mathf.Exp(-35f * udt));
        dropTarget *= Mathf.Exp(-10f * udt);
        fovKick = Mathf.Lerp(fovKick, fovTarget, 1f - Mathf.Exp(-14f * udt));
        fovTarget *= Mathf.Exp(-2.8f * udt);
    }

    void WatchReveals()
    {
        var all = LooseItem.All;
        Vector3 eye = Eye;
        for (int i = 0; i < all.Count; i++)
        {
            var li = all[i];
            if (li == null || !li.Revealed || !seenReveals.Add(li)) continue;
            if (!li.IsTreasure || li.Rarity < Rarity.Epic) continue;
            if ((li.transform.position - eye).sqrMagnitude > 64f) continue;
            FovKick(li.Rarity == Rarity.Legendary ? 6f : 3.5f);
        }
        if (seenReveals.Count > 256) seenReveals.RemoveWhere(x => x == null);
    }

    // ------------------------------------------------------------------ targeting

    void UpdateTarget()
    {
        var t = ComputeTarget();
        ApplyHighlight(t);
        target = t;
    }

    Target ComputeTarget()
    {
        var t = new Target();
        if (sortCam == null) return t;
        Vector3 eye = sortCam.transform.position;
        Vector3 fwd = sortCam.transform.forward;
        var held = carrier != null ? carrier.Held : null;
        bool empty = held == null;

        int n = Physics.RaycastNonAlloc(new Ray(eye, fwd), hits, reach, RayMask, QueryTriggerInteraction.Collide);
        System.Array.Sort(hits, 0, n, ByDistance);
        Collider solid = null;
        RaycastHit solidHit = default;
        ICarryReceiver mouth = null; // a bin/hopper opening the ray dropped through before hitting anything solid
        for (int i = 0; i < n; i++)
        {
            var col = hits[i].collider;
            if (col == null || IsIgnored(col, held)) continue;
            if (col.isTrigger)
            {
                if (mouth == null)
                {
                    var crt = col.GetComponent<CarryReceiverTrigger>();
                    var r = crt != null ? crt.Receiver : null;
                    if (r is SortBin || r is Compactor) mouth = r;
                }
                continue;
            }
            solid = col;
            solidHit = hits[i];
            break;
        }

        bool overTable = false; // hit the table's splash guard / rails: most likely aiming at the bins behind
        if (solid != null)
        {
            var button = solid.GetComponentInParent<CompactorButton>();
            var c = button == null ? solid.GetComponentInParent<Carryable>() : null;
            if (button != null) { t.kind = TargetKind.Button; t.button = button; }
            else if (c != null)
            {
                if (empty) { if (IsGrabbable(c)) { t.kind = TargetKind.Item; t.item = c; } }
                else ResolveContainer(c, ref t);
            }
            else
            {
                var bin = solid.GetComponentInParent<SortBin>();
                if (bin != null) { t.kind = TargetKind.Bin; t.bin = bin; }
                else if (compactor != null && solid.transform.IsChildOf(compactor.transform)) t.kind = TargetKind.Hopper;
                else if (table != null && solid.transform.IsChildOf(table.transform))
                {
                    t.kind = TargetKind.Table;
                    overTable = solidHit.point.y > table.TopWorldY + 0.1f;
                }
            }
        }

        if (mouth != null && t.kind != TargetKind.Button && t.kind != TargetKind.Item)
        {
            if (mouth is SortBin mb) { t.kind = TargetKind.Bin; t.bin = mb; }
            else { t.kind = TargetKind.Hopper; t.bin = null; }
        }

        if (empty)
        {
            if (t.kind != TargetKind.Item && t.kind != TargetKind.Button)
            {
                var c = AssistItem(eye, fwd, held);
                if (c != null) { t.kind = TargetKind.Item; t.item = c; t.bin = null; }
                else
                {
                    var b = AssistButton(eye, fwd);
                    if (b != null) { t.kind = TargetKind.Button; t.button = b; t.bin = null; }
                }
            }
            // Empty-handed, the table / bins / hopper aren't actions.
            if (t.kind != TargetKind.Item && t.kind != TargetKind.Button) t = new Target();
        }
        else if (t.kind == TargetKind.None || (t.kind == TargetKind.Table && overTable))
        {
            var r = AssistReceiver(eye, fwd);
            if (r.kind != TargetKind.None) t = r;
            else if (t.kind == TargetKind.None)
            {
                var b = AssistButton(eye, fwd);
                if (b != null) { t.kind = TargetKind.Button; t.button = b; }
            }
        }
        return t;
    }

    /// <summary>Holding something and looking at a loose item: what is that item lying in?</summary>
    void ResolveContainer(Carryable c, ref Target t)
    {
        Vector3 p = c.transform.position;
        if (table != null && table.IsOnTop(p)) t.kind = TargetKind.Table;
        else if (compactor != null && InChamber(p)) t.kind = TargetKind.Hopper;
    }

    bool IsIgnored(Collider col, Carryable held)
    {
        if (col is CharacterController) return true; // players
        var tr = col.transform;
        if (player != null && tr.IsChildOf(player.transform)) return true;
        if (held != null && tr.IsChildOf(held.transform)) return true;
        return col.GetComponentInParent<PlayerController>() != null;
    }

    /// <summary>A guest's host-owned REST copy is kinematic until local pickup/fetch claims simulation.</summary>
    static bool RestingNetworkCopy(Carryable c)
    {
        if (c == null || c.Body == null || !c.Body.isKinematic || !c.NetRemote) return false;
        var nc = c.GetComponent<NetCarry>();
        return nc != null && !nc.PendingGone && nc.St.Phase == HandsPhase.Rest;
    }

    static bool IsGrabbable(Carryable c) =>
        c != null && c.State != CarryState.Carried && c.State != CarryState.Consumed && !c.IsInScriptedFlight
        && c.Body != null && (!c.Body.isKinematic || RestingNetworkCopy(c)) && !(c is TrashBag tb && tb.HasBurst);

    /// <summary>Small items are fiddly at arm's length: snap to the loose item nearest the crosshair (visible, in reach).</summary>
    Carryable AssistItem(Vector3 eye, Vector3 fwd, Carryable held)
    {
        LooseItem best = null;
        float bestScore = float.MaxValue;
        var all = LooseItem.All;
        for (int i = 0; i < all.Count; i++)
        {
            var li = all[i];
            if (li == null || li.Box == null || !li.Box.enabled || !IsGrabbable(li)) continue;
            Bounds b = li.Box.bounds;
            Vector3 to = b.center - eye;
            float dist = to.magnitude;
            if (dist > reach || dist < 0.05f) continue;
            float ang = Vector3.Angle(fwd, to);
            float allow = Mathf.Atan2(b.extents.magnitude * 0.7f, dist) * Mathf.Rad2Deg + 2.5f;
            if (ang > allow) continue;
            float score = ang / allow;
            if (score < bestScore && Visible(eye, b.center, li, held)) { bestScore = score; best = li; }
        }
        return best;
    }

    bool Visible(Vector3 eye, Vector3 point, Carryable c, Carryable held)
    {
        Vector3 d = point - eye;
        float len = d.magnitude;
        if (len < 0.01f) return true;
        int k = Physics.RaycastNonAlloc(eye, d / len, losHits, len, RayMask & ~(1 << 2), QueryTriggerInteraction.Ignore);
        for (int i = 0; i < k; i++)
        {
            var col = losHits[i].collider;
            if (col == null || col.transform.IsChildOf(c.transform) || IsIgnored(col, held)) continue;
            if (col.GetComponentInParent<Carryable>() != null) continue; // other junk doesn't hide it
            if (losHits[i].distance < len - 0.08f) return false;
        }
        return true;
    }

    /// <summary>Bin mouths hide behind the table's splash guard: aiming near one (or at its sign) counts.</summary>
    Target AssistReceiver(Vector3 eye, Vector3 fwd)
    {
        var t = new Target();
        float best = float.MaxValue;
        if (bins != null)
            foreach (var b in bins)
            {
                if (b == null || !b.isActiveAndEnabled) continue;
                float ang = Vector3.Angle(fwd, b.TossTargetPosition - eye);
                if (ang < 7f && ang < best) { best = ang; t.kind = TargetKind.Bin; t.bin = b; }
            }
        if (compactor != null && compactor.isActiveAndEnabled)
        {
            float ang = Vector3.Angle(fwd, compactor.TossTargetPosition - eye);
            if (ang < 11f && ang < best) { t.kind = TargetKind.Hopper; t.bin = null; }
        }
        return t;
    }

    CompactorButton AssistButton(Vector3 eye, Vector3 fwd)
    {
        if (buttons == null) return null;
        CompactorButton best = null;
        float bestAng = 5f;
        foreach (var b in buttons)
        {
            if (b == null || !b.isActiveAndEnabled) continue;
            Vector3 to = b.transform.position - eye;
            if (to.magnitude > reach) continue;
            float ang = Vector3.Angle(fwd, to);
            if (ang < bestAng) { bestAng = ang; best = b; }
        }
        return best;
    }

    bool InChamber(Vector3 p)
    {
        var ch = compactor != null ? compactor.chamber : null;
        if (ch == null) return false;
        Vector3 l = ch.InverseTransformPoint(p);
        Vector3 h = compactor.chamberSize * 0.5f;
        return Mathf.Abs(l.x) <= h.x + 0.05f && Mathf.Abs(l.y) <= h.y + 0.3f && Mathf.Abs(l.z) <= h.z + 0.05f;
    }

    void ApplyHighlight(Target t)
    {
        if (litButton != null && litButton != t.button) { litButton.Highlight(false); litButton = null; }
        if (litItem != null && litItem != t.item) { litItem.Highlight(false); litItem = null; }
        if (litBin != null && litBin != t.bin) { litBin.SetAimed(false); litBin = null; }
        if (t.kind == TargetKind.Button && t.button != null) { t.button.Highlight(true); litButton = t.button; }
        if (t.kind == TargetKind.Item && t.item != null) { t.item.Highlight(true); litItem = t.item; }
        if (t.kind == TargetKind.Bin && t.bin != null) { t.bin.SetAimed(true); litBin = t.bin; }

        Object key = t.kind == TargetKind.Item ? (Object)t.item
            : t.kind == TargetKind.Button ? (Object)t.button
            : t.kind == TargetKind.Bin ? (Object)t.bin
            : t.kind == TargetKind.Hopper ? (Object)compactor
            : t.kind == TargetKind.Table ? (Object)table : null;
        if (key == lastTargetObj) return;
        lastTargetObj = key;
        // A little "hover" nudge on the receptacle you're about to toss into.
        if (t.kind == TargetKind.Bin && t.bin != null && t.bin.visualRoot != null) Juice.Punch(t.bin.visualRoot, 0.04f, 0.12f);
        else if (t.kind == TargetKind.Hopper && compactor != null && compactor.visualRoot != null) Juice.Punch(compactor.visualRoot, 0.02f, 0.12f);
    }

    void ClearHighlights()
    {
        if (litButton != null) { litButton.Highlight(false); litButton = null; }
        if (litItem != null) { litItem.Highlight(false); litItem = null; }
        if (litBin != null) { litBin.SetAimed(false); litBin = null; }
        lastTargetObj = null;
    }

    // ------------------------------------------------------------------ prompt / hint

    void UpdatePrompt()
    {
        string p = PromptFor(target, out mark);
        CurrentPrompt = p;
        var tt = TooltipUI.Instance;
        if (tt == null) return;
        if (string.IsNullOrEmpty(p) || !viewReady) tt.Hide();
        else tt.Show(IInteractable.InteractKey, p);
    }

    string PromptFor(Target t, out Mark m)
    {
        m = Mark.None;
        var held = carrier != null ? carrier.Held : null;
        if (t.kind == TargetKind.Button && t.button != null)
        {
            m = !t.button.CanPress ? Mark.Bad : ButtonWantsConfirm(t.button) ? Mark.Warn : Mark.Button;
            return t.button.GetInteractionPrompt();
        }
        if (held == null)
        {
            if (t.kind != TargetKind.Item || t.item == null) return null;
            m = Mark.Grab;
            return t.item.GetInteractionPrompt();
        }
        if (held is TrashBag bag)
        {
            switch (t.kind)
            {
                case TargetKind.Hopper:
                    if (compactor == null) return null;
                    if (!compactor.CanAccept(bag, out string busy)) { m = Mark.Bad; return busy; }
                    m = Mark.Warn;
                    return CompactArmedFor(bag) ? "Press again: compact it UNOPENED!" : "Compact whole bag (unopened!)";
                case TargetKind.Bin:
                    if (t.bin == null) return null;
                    m = Mark.Bad;
                    return t.bin.CanAccept(bag, out string no) ? t.bin.GetTossPrompt(bag) : no;
                default:
                    if (table == null) return null;
                    if (!table.CanAccept(bag, out string full)) { m = Mark.Bad; return full; }
                    m = Mark.Slam;
                    return "Slam it open!";
            }
        }
        switch (t.kind)
        {
            case TargetKind.Bin:
                if (t.bin == null) return null;
                if (t.bin.CanAccept(held, out string r1)) { m = Mark.Toss; return t.bin.GetTossPrompt(held); }
                m = Mark.Bad;
                return r1;
            case TargetKind.Hopper:
                if (compactor == null) return null;
                if (compactor.CanAccept(held, out string r2)) { m = Mark.Toss; return compactor.GetTossPrompt(held); }
                m = Mark.Bad;
                return r2;
            default:
                if (table == null) return null;
                m = Mark.Neutral;
                return "Set it down";
        }
    }

    /// <summary>
    /// The context hint line follows what's in your hands AND what the crosshair is on (slam / compact whole / set
    /// down / press ...). Rebuilt only when that changes (no per-frame string building).
    /// </summary>
    void UpdateHint()
    {
        if (!viewReady) return;
        var held = carrier != null ? carrier.Held : null;
        HintKind k = HintKindFor(held);
        bool revealed = held is LooseItem hl && hl.Revealed;
        if (k == hintKind && held == hintFor && revealed == hintRevealed && hintShown != null) return;
        hintKind = k;
        hintFor = held;
        hintRevealed = revealed;
        SetHint(BuildHint(k, held));
    }

    HintKind HintKindFor(Carryable held)
    {
        if (held == null)
        {
            if (target.kind == TargetKind.Item) return HintKind.EmptyItem;
            if (target.kind == TargetKind.Button) return HintKind.EmptyButton;
            return HintKind.Empty;
        }
        if (held is TrashBag)
        {
            switch (target.kind)
            {
                case TargetKind.Hopper: return HintKind.BagHopper;
                case TargetKind.Bin: return HintKind.BagBin;
                case TargetKind.Button: return HintKind.BagButton;
                default: return HintKind.BagTable;
            }
        }
        if (held is LooseItem)
        {
            switch (target.kind)
            {
                case TargetKind.Bin:
                case TargetKind.Hopper: return HintKind.ItemToss;
                case TargetKind.Button: return HintKind.ItemButton;
                default: return HintKind.ItemSetDown;
            }
        }
        return HintKind.Other;
    }

    static string BuildHint(HintKind k, Carryable held)
    {
        bool pad = GameControls.UsingGamepad;
        string primary = pad ? "[RT]" : "[E/LMB]";
        string exit = pad ? "[B]" : "[Q]";
        string tail = pad ? "   [RB] Chuck   [B] Step away" : "   [RMB] Chuck   [Q] Step away";
        if (held is LooseItem part && part.Revealed && Workshop.IsWanted(part.Item)) tail += "   WANTED PART: KEEP crate!";
        string name = held is LooseItem li ? li.LabelText() : held != null ? held.DisplayName : null;
        switch (k)
        {
            case HintKind.Empty: return $"{primary} Grab an item or a bag in reach   {exit} Step away";
            case HintKind.EmptyItem: return $"{primary} Grab   {exit} Step away";
            case HintKind.EmptyButton: return $"{primary} Press   {exit} Step away";
            case HintKind.BagTable: return $"{name}   ·   {primary} Slam it open" + tail + "   (aim at the compactor to crush it unopened)";
            case HintKind.BagHopper: return $"{name}   ·   {primary} x2 Compact it UNOPENED (treasures too!)" + tail;
            case HintKind.BagBin: return $"{name}   ·   Rip it open on the table first!" + tail;
            case HintKind.BagButton:
            case HintKind.ItemButton: return $"{name}   ·   {primary} Press" + tail;
            case HintKind.ItemToss: return $"{name}   ·   {primary} Toss it in" + tail;
            case HintKind.ItemSetDown: return $"{name}   ·   {primary} Set it down" + tail;
            default: return $"{primary} Toss" + tail;
        }
    }

    void SetHint(string h)
    {
        if (h == hintShown) return;
        hintShown = h;
        GameHUD.SetContextHint(h);
    }

    /// <summary>Tester/debug: what the crosshair is on and the prompt it shows.</summary>
    public string DescribeTarget()
    {
        string what;
        switch (target.kind)
        {
            case TargetKind.Item: what = target.item != null ? target.item.DisplayName : "item"; break;
            case TargetKind.Bin: what = target.bin != null ? target.bin.displayName : "bin"; break;
            case TargetKind.Button: what = target.button != null ? target.button.mode.ToString() : "button"; break;
            case TargetKind.Hopper: what = "compactor hopper"; break;
            case TargetKind.Table: what = "sorting table"; break;
            default: what = "nothing"; break;
        }
        return $"{target.kind}:{what} prompt='{CurrentPrompt}'";
    }

    // ------------------------------------------------------------------ actions


    /// <summary>E / LMB: grab, press, slam, compact, toss or set down — whatever the crosshair is on.</summary>
    public void PrimaryAction()
    {
        if (!IsActive || carrier == null) return;
        PoseCamera();
        UpdateTarget();
        var t = target;
        var held = carrier.Held;
        if (pendingSlamBag != null && held == pendingSlamBag) return;

        if (t.kind == TargetKind.Button && t.button != null)
        {
            if (!t.button.CanPress) { Sfx.Play("buzz", t.button.transform.position, 0.5f, 1.2f); return; }
            t.button.Press();
            Kick(1.2f, 0.01f);
            return;
        }

        if (held == null)
        {
            if (t.kind != TargetKind.Item || t.item == null) return;
            if (carrier.PickUp(t.item)) Kick(-1.2f);
            return;
        }

        if (!ReleaseConfirmed()) { NetReleaseWait(); return; }

        if (!(held is TrashBag && t.kind == TargetKind.Hopper)) DisarmCompact();

        if (held is TrashBag bag)
        {
            if (t.kind == TargetKind.Hopper) CompactBag(bag);
            else if (t.kind == TargetKind.Bin && t.bin != null) carrier.TossInto(t.bin); // "Rip it open on the table first!"
            else SlamBag(bag);
            return;
        }

        if (t.kind == TargetKind.Bin && t.bin != null) TossHeld(t.bin);
        else if (t.kind == TargetKind.Hopper && compactor != null) TossHeld(compactor);
        else SetDownHeld();
    }

    void TossHeld(ICarryReceiver r)
    {
        carrier.TossInto(r);
        if (carrier.Held == null)
        {
            Kick(-1.6f);
            Juice.Shake(0.05f, 0.08f);
        }
    }

    void SlamBag(TrashBag bag)
    {
        if (table == null) return;
        if (pendingSlamBag != null) return;
        if (!table.CanAccept(bag, out _)) { carrier.TossInto(table); return; } // "Table's full!" nope + shake
        Vector3 aim = TablePointFromView();
        var selectedTable = table;
        pendingSlamBag = bag;
        selectedTable.RequestAdmission(bag, (accepted, reason) =>
        {
            if (!accepted)
            {
                if (pendingSlamBag == bag) pendingSlamBag = null;
                if (IsActive && !string.IsNullOrEmpty(reason)) GameHUD.Toast(reason, ToastStyle.Bad, 1.6f);
                return;
            }
            if (!IsActive || pendingSlamBag != bag || selectedTable == null || !selectedTable.isActiveAndEnabled)
            {
                selectedTable?.CancelAdmission(bag);
                return;
            }
            StartCoroutine(CompleteSlam(bag, selectedTable, aim));
        });
    }

    IEnumerator CompleteSlam(TrashBag bag, SortingTable selectedTable, Vector3 aim)
    {
        float until = Time.unscaledTime + 2f;
        while (IsActive && pendingSlamBag == bag && carrier != null && carrier.Held == bag &&
               !ReleaseConfirmed() && Time.unscaledTime < until) yield return null;
        if (!IsActive || pendingSlamBag != bag || carrier == null || carrier.Held != bag ||
            !ReleaseConfirmed() || selectedTable == null || !selectedTable.isActiveAndEnabled ||
            !Permissions.LocalCanWorkAt(selectedTable.transform.position))
        {
            selectedTable?.CancelAdmission(bag);
            if (pendingSlamBag == bag) pendingSlamBag = null;
            yield break;
        }
        var released = carrier.ReleaseToScript() as TrashBag;
        pendingSlamBag = null;
        if (released == null) { selectedTable.CancelAdmission(bag); yield break; }
        selectedTable.SlamReserved(released, aim, OnSlamImpact);
        Sfx.Play("whoosh", released.WorldCenter, 0.7f, 0.85f);
        Kick(-2f); // the swing lifts the view a touch before it comes down
    }

    void OnSlamImpact()
    {
        if (!IsActive) return;
        Kick(5f, 0.035f);
    }

    /// <summary>
    /// Whole-bag compact: the first press warns (treasures inside get crushed too), a second, deliberate press
    /// (not a double-tap: ≥ Compactor.ConfirmMinDelay later, within compactConfirmWindow, crosshair kept on the
    /// hopper) feeds it in whole. Confirmed bags don't make SMASH ask again.
    /// </summary>
    void CompactBag(TrashBag bag)
    {
        if (compactor == null) return;
        if (!compactor.CanAccept(bag, out _)) { carrier.TossInto(compactor); return; } // "SMASHING! Hold on"
        if (!CompactArmedFor(bag))
        {
            compactArmedFor = bag;
            compactArmedAt = Time.unscaledTime;
            compactArmedUntil = Time.unscaledTime + compactConfirmWindow;
            GameHUD.Toast("Treasures inside get crushed too! Press again to compact it unopened", ToastStyle.Bad, compactConfirmWindow);
            Sfx.Play("beep", compactor.TossTargetPosition, 0.8f, 1f);
            if (compactor.visualRoot != null) Juice.Punch(compactor.visualRoot, 0.05f, 0.15f);
            return;
        }
        if (Time.unscaledTime - compactArmedAt < Compactor.ConfirmMinDelay) return; // a double-tap isn't a decision
        DisarmCompact();
        compactor.CompactWholeBag(bag); // host accepts the still-held bag and completes one compact cycle
        Kick(-1.8f);
    }

    bool CompactArmedFor(Carryable c) => c != null && c == compactArmedFor && Time.unscaledTime < compactArmedUntil;

    void DisarmCompact()
    {
        compactArmedUntil = -1f;
        compactArmedFor = null;
    }

    /// <summary>The button's next press is a "really?" confirm (SMASH with an unopened bag inside, CRUSH ALL).</summary>
    bool ButtonWantsConfirm(CompactorButton b)
    {
        if (b == null || compactor == null) return false;
        return b.mode == CompactorButton.Mode.Smash ? compactor.SmashNeedsConfirm : true;
    }

    void SetDownHeld()
    {
        if (table == null) { carrier.Drop(); return; }
        Vector3 spot = TablePointFromView();
        var c = carrier.ReleaseToScript();
        if (c != null) StartCoroutine(SetDownRoutine(c, spot));
    }

    /// <summary>Where the view ray meets the tabletop (clamped onto it).</summary>
    Vector3 TablePointFromView()
    {
        Vector3 eye = sortCam != null ? sortCam.transform.position : Eye;
        Vector3 fwd = sortCam != null ? sortCam.transform.forward : Vector3.forward;
        float y = table.TopWorldY;
        Vector3 p;
        if (fwd.y < -0.05f) p = eye + fwd * Mathf.Min((eye.y - y) / -fwd.y, reach + 1f);
        else
        {
            Vector3 flat = new Vector3(fwd.x, 0f, fwd.z);
            p = eye + (flat.sqrMagnitude > 0.001f ? flat.normalized : Vector3.forward) * 1.3f;
        }
        return table.ClampToTop(p, table.edgeMargin);
    }

    /// <summary>The tabletop right in front of the stand point.</summary>
    Vector3 DefaultTableSpot()
    {
        if (table == null) return transform.position;
        Vector3 l = table.transform.InverseTransformPoint(Eye);
        return table.ClampToTop(table.TopPoint(l.x - table.topOffset.x + Random.Range(-0.2f, 0.2f), -table.topSize.y * 0.15f), table.edgeMargin);
    }

    static Vector3 SetDownPivot(Carryable c, Vector3 spot, Quaternion rot)
    {
        var lb = c.LocalBounds();
        Vector3 bottom = Vector3.Scale(new Vector3(lb.center.x, lb.min.y, lb.center.z), c.transform.lossyScale);
        return spot + Vector3.up * 0.02f - rot * bottom;
    }

    IEnumerator SetDownRoutine(Carryable c, Vector3 spot)
    {
        if (c == null) yield break;
        settingDown.Add(c);
        Quaternion rot = Quaternion.Euler(0f, c.transform.eulerAngles.y, 0f);
        Vector3 pivot = SetDownPivot(c, spot, rot);
        float dist = Vector3.Distance(c.transform.position, pivot);
        yield return SortingKit.Arc(c.transform, pivot, SortingKit.ArcDuration(dist), Mathf.Min(0.3f, 0.08f + dist * 0.1f), rot);
        settingDown.Remove(c);
        if (c == null || c.State == CarryState.Consumed || c.State == CarryState.Carried) yield break;
        c.EndCarry(Vector3.down * 0.4f, false);
        Sfx.Play("thud", spot, 0.45f, 1.3f);
        Juice.Squash(c.JuiceTarget, 0.2f, 0.18f);
    }

    void PlaceOnTableNow(Carryable c, Vector3 spot)
    {
        if (c == null || c.State == CarryState.Consumed) return;
        Quaternion rot = Quaternion.Euler(0f, c.transform.eulerAngles.y, 0f);
        c.transform.SetPositionAndRotation(SetDownPivot(c, spot, rot), rot);
        c.EndCarry(Vector3.zero, false);
    }

    void FinishSetDowns()
    {
        foreach (var c in settingDown)
            if (c != null && c.State != CarryState.Consumed && c.State != CarryState.Carried && c.Body != null && c.Body.isKinematic)
                c.EndCarry(Vector3.zero, false);
        settingDown.Clear();
    }

    /// <summary>
    /// RMB: chuck the held thing along the view (a real physics throw; bins/table/hopper triggers still catch it).
    /// Aimed at the table / a bin / the hopper it's lobbed there (ballistic onto the aim point); otherwise straight
    /// out along the view. Big things (bags) are hefted above the table lip first so they don't just hit the apron.
    /// A bag at the hopper arcs straight in (unconfirmed: SMASH asks again); a bag at a bin crosses the table's catch
    /// zone and rips open there (bins don't take bags anyway).
    /// </summary>
    public void SecondaryThrow()
    {
        if (!IsActive || carrier == null || carrier.Held == null) return;
        if (pendingSlamBag != null && carrier.Held == pendingSlamBag) return;
        if (!ReleaseConfirmed()) { NetReleaseWait(); return; }
        DisarmCompact();
        PoseCamera();
        UpdateTarget();
        var held = carrier.Held;
        if (held is TrashBag && target.kind == TargetKind.Hopper && compactor != null)
        {
            // A physical heave at the hopper passes right over the table's front corner, where the table's catch zone
            // grabs it and rips it open instead. Arc it into the hopper (not confirmed: the compactor warns
            // "UNOPENED!" when it lands, and SMASH asks again before crushing it).
            carrier.TossInto(compactor); // "SMASHING! Hold on" if it's busy
            if (carrier.Held == null)
            {
                Juice.Shake(0.06f, 0.1f);
                Kick(-2.2f);
            }
            return;
        }
        Vector3 dir = sortCam != null ? sortCam.transform.forward : standPoint.forward;
        float heavy = Mathf.InverseLerp(2f, 20f, held.MassKg);
        float speed = Mathf.Lerp(chuckSpeed, chuckSpeed * 0.65f, heavy);
        Vector3? aim = ChuckAimPoint(held);
        var c = carrier.ReleaseToScript();
        if (c == null) return;

        Vector3 center = c.WorldCenter;
        Vector3 launch = ChuckLaunchCenter(c, center, dir);
        if ((launch - center).sqrMagnitude > 1e-6f) c.transform.position += launch - center;
        Vector3 v;
        if (aim.HasValue)
        {
            float dist = Vector3.Distance(launch, aim.Value);
            // Onto the table: a quick flat toss. Into a bin / the hopper: a lob, so it clears the splash guard / hopper
            // walls and drops steeply through the mouth (a fast flat throw skims over it and out the far side).
            float time = target.kind == TargetKind.Table
                ? Mathf.Clamp(dist / speed + 0.12f, 0.28f, 0.75f)
                : Mathf.Clamp(0.4f + dist * 0.14f, 0.55f, 0.9f);
            v = SortingKit.Ballistic(launch, aim.Value, time);
        }
        else v = dir * speed + Vector3.up * 1.2f;
        c.EndCarry(v, true);
        c.Body.angularVelocity = Vector3.Cross(Vector3.up, dir) * Random.Range(3f, 7f) + Random.insideUnitSphere * 2f;
        Vector3 at = c.WorldCenter;
        Sfx.Play("throw", at, 0.9f, Mathf.Lerp(1.1f, 0.85f, heavy));
        Sfx.Play("whoosh", at, 0.8f, 1.1f);
        Juice.Squash(c.JuiceTarget, -0.3f, 0.25f);
        Juice.Shake(0.06f, 0.1f);
        Kick(-2.2f);
    }

    bool ReleaseConfirmed()
    {
        var gate = TrashCarrier.NetCanRelease;
        if (gate == null || carrier == null) return true;
        try { return gate(carrier); }
        catch (System.Exception e) { Debug.LogException(e); return true; } // same fallback as TrashCarrier's own input
    }

    float nextNetWaitHintAt;

    void NetReleaseWait()
    {
        if (Time.unscaledTime < nextNetWaitHintAt) return;
        nextNetWaitHintAt = Time.unscaledTime + 0.5f;
        GameHUD.Toast("Hold on — grab syncing!", ToastStyle.Info, 0.7f);
        Sfx.Play("buzz", Eye, 0.3f, 1.4f);
    }

    /// <summary>Where an RMB chuck is lobbed to: the table spot / bin mouth / hopper under the crosshair (null = straight out).</summary>
    Vector3? ChuckAimPoint(Carryable held)
    {
        switch (target.kind)
        {
            case TargetKind.Table:
                if (table == null) return null;
                // Bags land in the table's catch trigger (it rips them); items just land on the top.
                return TablePointFromView() + Vector3.up * (held is TrashBag ? 0.3f : 0.12f);
            case TargetKind.Bin:
                return target.bin != null ? target.bin.TossTargetPosition + Vector3.up * 0.12f : (Vector3?)null;
            case TargetKind.Hopper:
                return compactor != null ? compactor.TossTargetPosition + Vector3.up * 0.1f : (Vector3?)null;
            default:
                return null;
        }
    }

    /// <summary>
    /// Where the chucked thing's center starts: bags (held low in both hands) are hefted up so their bottom clears
    /// the table's front lip and the hopper walls; small things go from the hand.
    /// </summary>
    Vector3 ChuckLaunchCenter(Carryable c, Vector3 center, Vector3 dir)
    {
        if (!(c is TrashBag)) return center;
        var lb = c.LocalBounds();
        Vector3 s = c.transform.lossyScale;
        float halfY = Mathf.Max(Mathf.Abs(lb.extents.x * s.x), Mathf.Abs(lb.extents.y * s.y), Mathf.Abs(lb.extents.z * s.z));
        Vector3 eye = Eye;
        float clearY = table != null ? table.TopWorldY + 0.14f + halfY : center.y;
        if (compactor != null && target.kind == TargetKind.Hopper) clearY = Mathf.Max(clearY, compactor.TossTargetPosition.y - 0.1f + halfY * 0.5f);
        Vector3 flat = new Vector3(dir.x, 0f, dir.z);
        flat = flat.sqrMagnitude > 1e-4f ? flat.normalized : (standPoint != null ? standPoint.forward : Vector3.forward);
        Vector3 p = center;
        p.y = Mathf.Min(Mathf.Max(center.y, clearY), eye.y - 0.05f);
        // Keep it out in front of the face (it's a heave, not a headbutt).
        Vector3 fromEye = p - eye; fromEye.y = 0f;
        float ahead = Vector3.Dot(fromEye, flat);
        float want = 0.3f + halfY;
        if (ahead < want) p += flat * (want - ahead);
        return p;
    }

    /// <summary>In or right above the compactor's chamber (including perched on the hopper rim).</summary>
    bool NearHopper(Vector3 p)
    {
        var ch = compactor != null ? compactor.chamber : null;
        if (ch == null) return false;
        Vector3 l = ch.InverseTransformPoint(p);
        Vector3 h = compactor.chamberSize * 0.5f;
        return Mathf.Abs(l.x) <= h.x + 0.3f && Mathf.Abs(l.z) <= h.z + 0.3f && l.y >= -h.y - 0.2f && l.y <= h.y + 1.0f;
    }

    // ------------------------------------------------------------------ parts

    void EnsureParts()
    {
        Transform root = transform.parent != null ? transform.parent : transform;
        if (table == null) table = root.GetComponentInChildren<SortingTable>();
        if (table == null) table = FindFirstObjectByType<SortingTable>();
        if (compactor == null) compactor = root.GetComponentInChildren<Compactor>();
        if (compactor == null) compactor = FindFirstObjectByType<Compactor>();
        if (bins == null || bins.Length == 0) bins = root.GetComponentsInChildren<SortBin>();
        if (buttons == null || buttons.Length == 0)
            buttons = compactor != null ? compactor.GetComponentsInChildren<CompactorButton>() : new CompactorButton[0];

        if (standPoint == null)
        {
            // In front of the table's long side, facing it.
            var sp = new GameObject("StandPoint").transform;
            sp.SetParent(transform, false);
            if (table != null)
            {
                Vector3 p = table.transform.TransformPoint(0f, 0f, -(table.topSize.y * 0.5f + 1.0f));
                sp.SetPositionAndRotation(p, Quaternion.LookRotation(Flat(table.transform.forward), Vector3.up));
            }
            standPoint = sp;
        }
        if (sortCam == null)
        {
            var go = new GameObject("SortCam");
            go.transform.SetParent(transform, false);
            sortCam = go.AddComponent<CinemachineVirtualCamera>();
            sortCam.Priority = 0;
            sortCam.enabled = false;
        }
        if (hand == null)
        {
            hand = new GameObject("SortHand").transform;
            hand.SetParent(transform, false);
        }
    }

    static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude > 0.0001f ? v.normalized : Vector3.forward;
    }

    void EnsureCrosshair()
    {
        if (crosshair == null) crosshair = new Crosshair(transform);
    }

    // ------------------------------------------------------------------ blend override (this camera only)

    static CinemachineCore.GetBlendOverrideDelegate prevBlendOverride;
    static bool blendHookInstalled;

    static void InstallBlendHook()
    {
        if (blendHookInstalled) return;
        prevBlendOverride = CinemachineCore.GetBlendOverride;
        CinemachineCore.GetBlendOverride = BlendOverride;
        blendHookInstalled = true;
    }

    static void UninstallBlendHook()
    {
        if (!blendHookInstalled) return;
        // Only unhook when nobody chained after us; otherwise stay in the chain as a pass-through.
        if (CinemachineCore.GetBlendOverride == (CinemachineCore.GetBlendOverrideDelegate)BlendOverride)
        {
            CinemachineCore.GetBlendOverride = prevBlendOverride;
            prevBlendOverride = null;
            blendHookInstalled = false;
        }
    }

    static CinemachineBlendDefinition BlendOverride(ICinemachineCamera from, ICinemachineCamera to,
        CinemachineBlendDefinition blend, MonoBehaviour owner)
    {
        if (prevBlendOverride != null) blend = prevBlendOverride(from, to, blend, owner);
        var s = Current;
        if (s == null || s.sortCam == null || !(owner is CinemachineBrain)) return blend;
        ICinemachineCamera cam = s.sortCam;
        if (to == cam) return new CinemachineBlendDefinition(CinemachineBlendDefinition.Style.EaseInOut, s.blendIn);
        if (from == cam) return new CinemachineBlendDefinition(CinemachineBlendDefinition.Style.EaseInOut, s.blendOut);
        return blend;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Current = null;
        stations.Clear();
        if (blendHookInstalled && CinemachineCore.GetBlendOverride == (CinemachineCore.GetBlendOverrideDelegate)BlendOverride)
            CinemachineCore.GetBlendOverride = prevBlendOverride;
        prevBlendOverride = null;
        blendHookInstalled = false;
    }

    // ------------------------------------------------------------------ crosshair (runtime canvas, sort mode only)

    sealed class Crosshair
    {
        /// <summary>Above the scene's TooltipUI canvas (0), below the Feel HUD (100) so toasts sit on top.</summary>
        public const int SortingOrder = 90;

        readonly GameObject root;
        readonly RectTransform ring, dot;
        readonly Image ringImg, dotImg;
        float scale = 0.6f, alpha, pop;
        Color color = Color.white;
        Object lastKey;

        static Sprite discSprite, ringSprite;

        public Crosshair(Transform parent)
        {
            root = new GameObject("SortCrosshair", typeof(RectTransform));
            root.layer = 5;
            root.transform.SetParent(parent, false);
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = SortingOrder;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            // deliberately no GraphicRaycaster: never eats clicks

            if (discSprite == null) discSprite = MakeCircle(false);
            if (ringSprite == null) ringSprite = MakeCircle(true);
            MakeImage("DotShadow", discSprite, 13f, new Color(0f, 0f, 0f, 0.45f), out _);
            dot = MakeImage("Dot", discSprite, 8f, Color.white, out dotImg);
            ring = MakeImage("Ring", ringSprite, 40f, new Color(1f, 1f, 1f, 0f), out ringImg);
            root.SetActive(false);
        }

        RectTransform MakeImage(string name, Sprite sprite, float size, Color c, out Image img)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = 5;
            var rt = (RectTransform)go.transform;
            rt.SetParent(root.transform, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(size, size);
            img = go.AddComponent<Image>();
            img.sprite = sprite;
            img.color = c;
            img.raycastTarget = false;
            return rt;
        }

        static Sprite MakeCircle(bool hollow)
        {
            const int S = 64;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false)
            {
                name = hollow ? "SortCrosshairRing" : "SortCrosshairDot",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            float r = S * 0.5f - 1f, inner = r - 7f;
            var px = new Color32[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(S * 0.5f, S * 0.5f));
                    float a = Mathf.Clamp01(r - d + 0.5f);
                    if (hollow) a *= Mathf.Clamp01(d - inner + 0.5f);
                    px[y * S + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100f);
        }

        public void SetVisible(bool on)
        {
            if (root == null) return;
            if (root.activeSelf != on) root.SetActive(on);
            if (!on) { lastKey = null; scale = 0.6f; alpha = 0f; pop = 0f; }
        }

        public void Tick(Mark m, bool pulse, Object key, float dt)
        {
            if (root == null || !root.activeSelf) return;
            Color want;
            float wantScale, wantAlpha = 1f;
            switch (m)
            {
                case Mark.Grab: want = new Color(1f, 0.9f, 0.5f); wantScale = 1f; break;
                case Mark.Toss: want = new Color(0.6f, 0.93f, 1f); wantScale = 1f; break;
                case Mark.Slam: want = new Color(1f, 0.75f, 0.2f); wantScale = 1.15f; break;
                case Mark.Warn: want = new Color(1f, 0.55f, 0.1f); wantScale = 1.15f; break;
                case Mark.Bad: want = new Color(1f, 0.3f, 0.25f); wantScale = 0.85f; break;
                case Mark.Button: want = new Color(1f, 0.38f, 0.2f); wantScale = 1.1f; break;
                case Mark.Neutral: want = Color.white; wantScale = 0.8f; wantAlpha = 0.55f; break;
                default: want = Color.white; wantScale = 0.6f; wantAlpha = 0f; break;
            }
            if (key != lastKey)
            {
                lastKey = key;
                if (key != null) pop = 0.35f;
            }
            float k = 1f - Mathf.Exp(-16f * dt);
            color = Color.Lerp(color, want, k);
            scale = Mathf.Lerp(scale, wantScale, k);
            alpha = Mathf.Lerp(alpha, wantAlpha, k);
            pop = Mathf.MoveTowards(pop, 0f, dt * 2.2f);
            float pulseK = pulse ? 1f + 0.12f * Mathf.Sin(Time.unscaledTime * 14f) : 1f;
            ring.localScale = Vector3.one * (scale * (1f + pop) * pulseK);
            ringImg.color = new Color(color.r, color.g, color.b, alpha);
            dotImg.color = Color.Lerp(Color.white, color, alpha * 0.6f);
            dot.localScale = Vector3.one * (1f + pop * 0.5f);
        }

        public void Destroy()
        {
            if (root != null) Object.Destroy(root);
        }
    }
}
