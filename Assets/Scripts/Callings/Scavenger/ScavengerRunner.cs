using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The Scavenger calling on THIS machine (play mode only, created at startup, DontDestroyOnLoad): drives the local
/// presentation (roof coins, the Itch, the X-ray plate, the door prompt and the lock-pick bar, the fence, the map pips)
/// from this player's own calling levels, and keeps the day roll honest (<see cref="Scavenger.TickDay"/>). It decides
/// nothing: every outcome comes back from <see cref="ScavengerHost"/> through <see cref="Scavenger"/>'s events.
/// </summary>
[DefaultExecutionOrder(-30)]
public sealed class ScavengerRunner : MonoBehaviour
{
    /// <summary>N for "name it" (C is the Hauler's backpack key, and both can be held at once).</summary>
    public const KeyCode CallItKey = KeyCode.N;
    static ScavengerRunner instance;

    readonly ScavengerHud hud = new ScavengerHud();
    readonly ScavNoseFx nose = new ScavNoseFx();
    readonly ScavItchFx itch = new ScavItchFx();
    readonly ScavMapPips pips = new ScavMapPips();
    readonly ScavFence fence = new ScavFence();
    ScavDoor door;
    bool headless, live, sessionDirty;
    int noseLevel, xrayLevel;
    bool itchOn;
    float nextLevels, nextDay, nextTarget;
    int faults;
    TrashBag xrayBag;

    static PlayerController Me => PlayerController.Local; // coop:allow(local) camera, HUD and input of this machine's own player only

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (instance != null || !Application.isPlaying) return;
        var go = new GameObject("[Scavenger]");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<ScavengerRunner>();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => instance = null;

    void Awake()
    {
        if (instance != null && instance != this) { Destroy(gameObject); return; }
        instance = this;
        headless = Application.isBatchMode;
        door = new ScavDoor(transform);
        if (headless) return;
        try { hud.Build(transform); } catch (Exception e) { Debug.LogException(e); headless = true; }
        Scavenger.CaseAnswered += door.OnCase;
        Scavenger.BreakInAnswered += door.OnBreakIn;
        Scavenger.CallAnswered += OnCall;
    }

    void OnDestroy()
    {
        if (door != null) { Scavenger.CaseAnswered -= door.OnCase; Scavenger.BreakInAnswered -= door.OnBreakIn; }
        Scavenger.CallAnswered -= OnCall;
        door?.Dispose(); nose.Clear(); itch.Dispose(); pips.Clear(); fence.Clear(); hud.Destroy();
        if (instance == this) instance = null;
    }

    void Update()
    {
        var me = Me;
        if (me == null || !me.IsSpawned)
        {
            if (live) { live = false; ClearAll(); }
            // The player object can blink out during a transition; only the end of the session forgets its memory.
            var nm = Unity.Netcode.NetworkManager.Singleton;
            if (sessionDirty && (nm == null || !nm.IsListening)) { sessionDirty = false; Scavenger.ResetSession(); ScavengerHost.ResetSession(); }
            return;
        }
        live = sessionDirty = true;
        float now = Time.unscaledTime;
        if (now >= nextDay) { nextDay = now + 2f; try { Scavenger.TickDay(); } catch (Exception e) { Debug.LogException(e); } }
        if (headless) return;
        if (now >= nextLevels)
        {
            nextLevels = now + 0.5f;
            noseLevel = Scavenger.Level(CallingIds.NoseForMoney);
            xrayLevel = Scavenger.Level(CallingIds.XRaySpecs);
            itchOn = Scavenger.HasItch;
        }
        try { Present(me); }
        catch (Exception e)
        {
            // Presentation must never take the game down with it: log, and after a few strikes stop drawing.
            Debug.LogException(e);
            if (++faults >= 5) { headless = true; ClearAll(); Debug.LogWarning("[Scavenger] presentation switched off after repeated errors (rules still run)."); }
        }
    }

    void Present(PlayerController me)
    {
        Vector3 at = me.transform.position;
        bool onFoot = !me.IsDriving && !me.IsPlayingChess;
        bool hands = hud.Layout() && onFoot && !PlayerController.IsUIBlocking && !FullMap.IsOpenInteractive;
        nose.Tick(at, noseLevel, transform);
        itch.Tick(at, itchOn, transform);
        pips.Tick(noseLevel);
        fence.Tick(me, itchOn, transform);
        door.Tick(me, itchOn && onFoot, hands, hud);
        TickXRay(me, hands && !door.Picking);
    }

    void ClearAll()
    {
        nose.Clear(); itch.Clear(); pips.Clear(); fence.Clear(); door.Cancel(hud);
        hud.HideAll();
        xrayBag = null;
    }

    // ------------------------------------------------------------------ x-ray specs + call it

    void TickXRay(PlayerController me, bool allowed)
    {
        bool callOwned = Scavenger.Level(CallingIds.CallIt) > 0;
        if (!allowed || xrayLevel <= 0 && !callOwned) { hud.HideXRay(); xrayBag = null; return; }
        if (Time.unscaledTime >= nextTarget) { nextTarget = Time.unscaledTime + 0.15f; xrayBag = FindTarget(me); }
        if (xrayBag == null || xrayBag.Contents == null || xrayBag.HasBurst) { hud.HideXRay(); return; }
        var carrier = TrashCarrier.Local;
        bool held = carrier != null && carrier.Held == xrayBag;
        bool canCall = held && Scavenger.CanCall(xrayBag);
        string hint = canCall ? "[" + Keybinds.Cap(CallItKey) + "] CALL IT" : "";
        if (xrayLevel > 0)
        {
            bool known = Scavenger.TryPeek(xrayBag, out var summary);
            hud.ShowXRay(summary, !known, hint);
        }
        else if (canCall) hud.ShowXRay(default, true, hint); // Call It without specs: the plate is just the prompt
        else hud.HideXRay();
        if (canCall && Input.GetKeyDown(CallItKey) && !UiStack.AnyOpen) CallItMenu.Show(xrayBag);
    }

    static TrashBag FindTarget(PlayerController me)
    {
        var carrier = TrashCarrier.Local;
        if (carrier != null && carrier.Held is TrashBag held) return held;
        if (carrier != null && carrier.IsCarrying) return null;
        var cam = Camera.main;
        if (cam == null) return null;
        Vector3 eye = cam.transform.position, forward = cam.transform.forward, feet = me.transform.position;
        float range = ScavengerTuning.XRayRange, bestScore = 0f; TrashBag best = null;
        var all = Carryable.All;
        for (int i = 0; i < all.Count; i++)
        {
            if (!(all[i] is TrashBag bag) || bag.State != CarryState.Resting || bag.HasBurst || bag.Contents == null) continue;
            Vector3 centre = bag.WorldCenter;
            if ((centre - feet).sqrMagnitude > range * range) continue;
            Vector3 to = centre - eye; float distance = to.magnitude;
            if (distance < 0.05f) continue;
            float aim = Vector3.Dot(forward, to / distance);
            if (aim < 0.955f) continue;
            float score = aim - distance * 0.004f;
            if (score > bestScore) { bestScore = score; best = bag; }
        }
        return best;
    }

    void OnCall(CallResult result)
    {
        var me = Me;
        Vector3 at = me != null ? me.transform.position + Vector3.up * 2.2f : Vector3.zero;
        string actual = result.actual < ScavengerRules.CallBands ? ScavengerTuning.CallBandNames[result.actual] : "?";
        if (result.status == 2)
        {
            GameHUD.Toast($"CALLED IT!  {actual}.  +{Economy.Money(result.bonusCents / 100f)}", ToastStyle.Legendary, 4.5f);
            Sfx.Play("legendary", null, 0.9f, 1f);
            Juice.Shake(0.35f, 0.3f);
            if (me != null) { GameHUD.FloatingText(at, "CALLED IT!", new Color(1f, 0.84f, 0.2f), 1.8f); Fx.Burst(at, new Color(1f, 0.84f, 0.2f), 30, 6f); }
        }
        else if (result.status == 1)
        {
            GameHUD.Toast($"Wrong call. It was: {actual}. That's today's call gone.", ToastStyle.Bad, 4f);
            Sfx.Play("truck_full", null, 0.7f, 1f);
            if (me != null) GameHUD.FloatingText(at, "...nope", new Color(1f, 0.5f, 0.4f), 1.2f);
        }
        else GameHUD.Toast(string.IsNullOrEmpty(result.why) ? "No call." : result.why, ToastStyle.Bad, 3f);
    }
}

/// <summary>The door in front of you: the "[E] Case the joint / Break in" prompt, the casing card and the lock-pick bar.</summary>
sealed class ScavDoor
{
    readonly GameObject probe;
    readonly InteractionZone zone;
    readonly BurgleDoor target;
    string nearStop, casedStop, pickStop;
    Vector3 nearDoor;
    CaseInfo cased;
    float nextScan, casedAt = -999f, askedAt = -999f;
    // the pick
    public bool Picking { get; private set; }
    float pickStart, duration, zoneCenter, zoneWidth, finishAt, speed;
    int pinCount, pinsSet, misses;
    bool finishing;
    Func<PlayerController, bool> focus;

    public ScavDoor(Transform parent)
    {
        probe = new GameObject("Scavenger Door Probe");
        probe.transform.SetParent(parent, false);
        target = probe.AddComponent<BurgleDoor>();
        target.door = this;
        zone = probe.AddComponent<InteractionZone>();
        zone.mode = ZoneMode.OnFoot;
        zone.center = new Vector3(0f, 1.2f, 0f);
        zone.size = new Vector3(ScavengerTuning.DoorRange * 2f, 5f, ScavengerTuning.DoorRange * 2f);
        zone.score = 1.6f;
        zone.targetOverride = target;
        probe.SetActive(false);
    }

    bool CasedHere => casedStop != null && casedStop == nearStop && Time.unscaledTime - casedAt < ScavengerTuning.CaseValidSeconds - 5f;
    public bool CanUse => nearStop != null && !Picking && Scavenger.HasItch && (TrashCarrier.Local == null || !TrashCarrier.Local.IsCarrying);
    public string Prompt => CasedHere && cased.status == CaseStatus.Ok ? $"Break in ({cased.risk}% risk)" : "Case the joint";

    public void Tick(PlayerController me, bool on, bool hands, ScavengerHud hud)
    {
        if (!on) { Cancel(hud); nearStop = null; if (probe.activeSelf) probe.SetActive(false); return; }
        Vector3 at = me.transform.position;
        if (Time.unscaledTime >= nextScan && !Picking)
        {
            nextScan = Time.unscaledTime + 0.2f;
            string best = null; Vector3 bestDoor = Vector3.zero; float bestD2 = ScavengerTuning.DoorRange * ScavengerTuning.DoorRange;
            var stops = PickupStop.All;
            for (int i = 0; i < stops.Count; i++)
            {
                var stop = stops[i];
                if (stop == null || (stop.transform.position - at).sqrMagnitude > 70f * 70f || !ScavHouses.TryDoor(stop, out var d)) continue;
                Vector3 flat = d - at; float dy = Mathf.Abs(flat.y); flat.y = 0f;
                if (dy > 3f || flat.sqrMagnitude > bestD2) continue;
                bestD2 = flat.sqrMagnitude; best = stop.stopId; bestDoor = d;
            }
            nearStop = best; nearDoor = bestDoor;
            if (best != null) probe.transform.position = bestDoor;
            if (probe.activeSelf != (best != null)) probe.SetActive(best != null);
        }
        if (Picking) { TickPick(me, hud); hud.HideCase(); return; }
        hud.HidePick();
        if (hands && CasedHere && cased.status == CaseStatus.Ok)
        {
            var house = CurbTrashSpawner.FindHouse(casedStop);
            bool full = Scavenger.HotHeld >= Scavenger.SackCapacity;
            hud.ShowCase("CASING  " + (house != null ? house.address : casedStop), ScavengerHud.CaseText(cased),
                full ? "Your sack is full. See the fence first." : "[" + FeelUi.KeyName(IInteractable.InteractKey) + "] Break in      walk away to bail");
        }
        else hud.HideCase();
    }

    public void Interact()
    {
        if (!CanUse) return;
        if (CasedHere && cased.status == CaseStatus.Ok)
        {
            if (Scavenger.HotHeld >= Scavenger.SackCapacity) { GameHUD.Toast("Your sack is full. See the fence first.", ToastStyle.Bad, 3f); return; }
            StartPick();
            return;
        }
        if (Time.unscaledTime - askedAt < 0.8f) return;
        askedAt = Time.unscaledTime;
        Sfx.Play("sniff", nearDoor, 0.4f, 0.9f);
        Scavenger.RequestCase(nearStop);
    }

    public void OnCase(string stopId, CaseInfo info)
    {
        cased = info; casedStop = stopId; casedAt = Time.unscaledTime;
        if (info.status == CaseStatus.Ok) { Sfx.Play("click", null, 0.5f, 0.8f); return; }
        casedStop = null;
        GameHUD.Toast(ScavengerHud.CaseRefusal(info), ToastStyle.Bad, 3.5f);
    }

    void StartPick()
    {
        int level = Scavenger.Level(CallingIds.Lockpicks);
        pickStop = nearStop;
        duration = ScavengerRules.PickSeconds(level); pinCount = ScavengerRules.PickPins(level); zoneWidth = ScavengerRules.PickSweetSpot(level);
        speed = level <= 0 ? 1.45f : 1.2f;
        pickStart = Time.time; pinsSet = misses = 0; finishing = false; Picking = true;
        NewZone();
        focus = _ => true;
        PlayerController.InteractionFocusOverride = focus;
        Scavenger.NotifyBreakStart(pickStop);
        Sfx.Play("door_creak", nearDoor, 0.35f, 1.3f);
        GameHUD.FloatingText(nearDoor + Vector3.up * 1.8f, level <= 0 ? "*jiggle jiggle*" : "*click... click*", new Color(0.8f, 0.95f, 1f), 0.9f);
    }

    void NewZone() => zoneCenter = Mathf.Lerp(zoneWidth * 0.5f + 0.04f, 1f - zoneWidth * 0.5f - 0.04f, NightDice.Cosmetic.Value);

    void TickPick(PlayerController me, ScavengerHud hud)
    {
        float t = Time.time - pickStart;
        Vector3 flat = me.transform.position - nearDoor; flat.y = 0f;
        if (flat.sqrMagnitude > ScavengerTuning.BailRange * ScavengerTuning.BailRange || me.IsDriving)
        {
            EndPick(hud); casedStop = null;
            GameHUD.Toast("You bottled it. Nobody the wiser.", ToastStyle.Info, 2.5f);
            return;
        }
        float needle = Mathf.PingPong(t * speed, 1f);
        if (!finishing)
        {
            if (!PlayerController.IsUIBlocking && GameControls.Down(IInteractable.InteractKey, GameControls.Action.Interact))
            {
                if (Mathf.Abs(needle - zoneCenter) <= zoneWidth * 0.5f)
                {
                    pinsSet++;
                    Sfx.Play("click", nearDoor, 0.7f, 1f + 0.12f * pinsSet);
                    NewZone();
                }
                else
                {
                    misses = Mathf.Min(ScavengerTuning.MaxMisses, misses + 1);
                    Sfx.Play("clang", nearDoor, 0.45f, 1.5f);
                    GameHUD.FloatingText(nearDoor + Vector3.up * 1.6f, "*CLATTER*", new Color(1f, 0.6f, 0.3f), 1f);
                    Juice.Shake(0.12f, 0.12f);
                }
            }
            if (pinsSet >= pinCount) { finishing = true; finishAt = Mathf.Max(Time.time, pickStart + duration * 0.8f); }
            else if (t >= duration) { misses = Mathf.Min(ScavengerTuning.MaxMisses, misses + (pinCount - pinsSet)); finishing = true; finishAt = Time.time; }
        }
        if (finishing && Time.time >= finishAt)
        {
            string stop = pickStop; int fumbles = misses;
            EndPick(hud); casedStop = null;
            Scavenger.RequestBreakFinish(stop, fumbles);
            return;
        }
        string caption = finishing ? "Easing it open..." : misses > 0 ? $"PICKING...  {misses} clatter{(misses == 1 ? "" : "s")}" :
            "PICKING...  [" + FeelUi.KeyName(IInteractable.InteractKey) + "] on the green";
        hud.ShowPick(needle, zoneCenter, zoneWidth, pinsSet, pinCount, misses, 1f - Mathf.Clamp01(t / duration), caption);
    }

    void EndPick(ScavengerHud hud)
    {
        Picking = false;
        if (focus != null && PlayerController.InteractionFocusOverride == focus) PlayerController.InteractionFocusOverride = null;
        focus = null;
        hud?.HidePick();
    }

    public void Cancel(ScavengerHud hud) { if (Picking) EndPick(hud); casedStop = null; hud?.HideCase(); hud?.HidePick(); }

    public void OnBreakIn(BreakInResult result, List<ItemInstance> loot)
    {
        var house = CurbTrashSpawner.FindHouse(result.stopId);
        string where = house != null ? house.address : "the house";
        Vector3 at = ScavHouses.TryDoor(result.stopId, out var doorAt) ? doorAt + Vector3.up * 1.8f : Vector3.zero;
        string names = "";
        ItemInstance best = null;
        foreach (var item in loot)
        {
            var def = item.Def;
            names += (names.Length > 0 ? ", " : "") + (def != null ? def.name : item.defId);
            if (best == null || DisplayPlanner.Compare(item, best) < 0) best = item;
        }
        switch (result.outcome)
        {
            case BreakInOutcome.Denied:
                GameHUD.Toast(string.IsNullOrEmpty(result.why) ? "Not this time." : result.why, ToastStyle.Bad, 3f);
                break;
            case BreakInOutcome.Fumbled:
                GameHUD.Toast("The lock wins. Nobody noticed. Case it again if you dare.", ToastStyle.Info, 3.5f);
                Sfx.Play("buzz", null, 0.5f, 0.8f);
                break;
            case BreakInOutcome.Caught:
                GameHUD.Toast($"SEEN at {where}!  " + (result.fineCents > 0 ? $"Fine {Economy.Money(result.fineCents / 100f)}. " : "") + "The street will remember.", ToastStyle.Bad, 5f);
                Juice.Shake(0.4f, 0.35f);
                break;
            case BreakInOutcome.Clean:
                GameHUD.Toast(loot.Count > 0 ? $"In and out of {where}. HOT: {names}." : $"In and out of {where}. Slim pickings.", ToastStyle.Rare, 5f);
                Sfx.Play("rare", null, 0.8f, 0.9f);
                if (at != Vector3.zero) { GameHUD.FloatingText(at, "+" + loot.Count + " HOT", new Color(1f, 0.55f, 0.2f), 1.5f); Fx.Burst(at, new Color(1f, 0.7f, 0.2f), 18, 4f); }
                break;
            case BreakInOutcome.Spotted:
                GameHUD.Toast($"SEEN leaving {where}! You bolted with {(loot.Count > 0 ? names : "nothing")}. " +
                    (result.fineCents > 0 ? $"Fine {Economy.Money(result.fineCents / 100f)}." : ""), ToastStyle.Bad, 5.5f);
                Juice.Shake(0.4f, 0.35f);
                break;
        }
        if (best != null && best.Def != null && best.Def.rarity >= Rarity.Epic) GameHUD.BigReveal(best);
    }

    public void Dispose() { if (Picking) EndPick(null); if (probe != null) UnityEngine.Object.Destroy(probe); }
}

/// <summary>The prompt on the door probe (one object that follows whichever front door you are standing at).</summary>
[DisallowMultipleComponent]
sealed class BurgleDoor : MonoBehaviour, IInteractable
{
    [NonSerialized] public ScavDoor door;
    public bool CanInteract() => door != null && door.CanUse;
    public string GetInteractionPrompt() => door != null ? door.Prompt : "";
    public void OnInteract(GameObject interactor) => door?.Interact();
    public void Highlight(bool highlight) { }
}

/// <summary>
/// The fence himself: a raccoon in a trench coat and a hat, in the alley on Depot Row, nights only. Built from primitives
/// with the critter kit; he is NOT a Critter (no AI, no spawn-cap slot, nothing networked): every player with The Itch
/// sees him at the same fixed spot, and the host checks you are standing there when you sell.
/// </summary>
public sealed class ScavFence
{
    /// <summary>This machine's player is close enough to trade (the trade screen closes when you wander off).</summary>
    public static bool LocalInReach { get; private set; }

    GameObject go;
    CritterRig rig;
    Vector3 ground;
    float nextCheck;

    public void Tick(PlayerController me, bool itch, Transform parent)
    {
        if (Time.unscaledTime >= nextCheck)
        {
            nextCheck = Time.unscaledTime + 1f;
            bool want = itch && ScavengerRules.FenceOpen(GameClock.Hours) && PickupStop.All.Count > 0;
            if (want && go == null) Spawn(parent);
            else if (!want && go != null) Leave();
        }
        if (go == null) { LocalInReach = false; return; }
        Vector3 to = me.transform.position - ground; to.y = 0f;
        LocalInReach = to.sqrMagnitude <= ScavengerTuning.FenceRange * ScavengerTuning.FenceRange;
        float t = Time.time;
        if (rig.visual != null) rig.visual.localPosition = new Vector3(0f, 0.015f * Mathf.Sin(t * 2.1f), 0f);
        if (rig.head != null)
        {
            // shifty: glances about, then fixes on you when you come near
            float look = to.sqrMagnitude < 64f ? Mathf.DeltaAngle(go.transform.eulerAngles.y, Quaternion.LookRotation(to.sqrMagnitude > 0.01f ? to : go.transform.forward).eulerAngles.y) : 35f * Mathf.Sin(t * 0.9f);
            rig.head.localRotation = Quaternion.Slerp(rig.head.localRotation, Quaternion.Euler(52f, Mathf.Clamp(look, -60f, 60f), 0f), Time.deltaTime * 5f);
        }
    }

    void Spawn(Transform parent)
    {
        ground = FenceSpot.Position;
        if (Physics.Raycast(ground + Vector3.up * 6f, Vector3.down, out var hit, 20f, ~(1 << CritterWorld.CritterLayer), QueryTriggerInteraction.Ignore)) ground.y = hit.point.y;
        go = new GameObject("The Fence");
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(ground, FenceSpot.Facing);
        rig = BuildLook(go);
        var contact = go.AddComponent<FenceContact>();
        var zone = go.AddComponent<InteractionZone>();
        zone.mode = ZoneMode.OnFoot;
        zone.center = new Vector3(0f, 0.7f, 0f);
        zone.size = new Vector3(ScavengerTuning.FenceRange * 2f, 4f, ScavengerTuning.FenceRange * 2f) / go.transform.localScale.x;
        zone.score = 1.5f;
        zone.targetOverride = contact;
        // a cigar-end of light, so he reads as a shape in the alley rather than a rumour
        var glow = new GameObject("Alley Glow").AddComponent<Light>();
        glow.transform.SetParent(go.transform, false);
        glow.transform.localPosition = new Vector3(0.25f, 0.75f, 0.45f);
        glow.type = LightType.Point; glow.color = new Color(1f, 0.62f, 0.3f); glow.range = 4.5f; glow.intensity = 1.3f; glow.shadows = LightShadows.None;
        Juice.PopIn(go.transform, 0.35f);
        Fx.Dust(ground, 1.2f);
    }

    /// <summary>The look alone (no prompt, no light): a raccoon up on his hind legs in a trench coat and a hat. Also used
    /// by the edit-mode preview.</summary>
    public static CritterRig BuildLook(GameObject root)
    {
        root.transform.localScale = Vector3.one * 1.7f;
        var rig = CritterLook.BuildRaccoon(root, false);
        // He is set dressing with a prompt, not a critter: drop the kit's trigger body.
        var body = root.GetComponent<Rigidbody>(); if (body != null) UnityEngine.Object.Destroy(body);
        var trigger = root.GetComponent<Collider>(); if (trigger != null) UnityEngine.Object.Destroy(trigger);
        if (rig.rear != null) rig.rear.localRotation = Quaternion.Euler(-64f, 0f, 0f); // up on his hind legs
        if (rig.tail.Count > 0 && rig.tail[0] != null) rig.tail[0].localRotation = Quaternion.Euler(40f, 0f, 0f); // tail along the ground, not through it
        var coat = CritterLook.Mat(new Color(0.47f, 0.37f, 0.24f), 0.2f);
        var dark = CritterLook.Mat(new Color(0.16f, 0.13f, 0.11f), 0.25f);
        if (rig.rear != null)
        {
            CritterLook.Part(rig.rear, PrimitiveType.Capsule, "Trench Coat", new Vector3(0f, 0.2f, 0.2f), new Vector3(0.56f, 0.34f, 0.5f), coat, new Vector3(90f, 0f, 0f));
            CritterLook.Part(rig.rear, PrimitiveType.Cube, "Collar", new Vector3(0f, 0.27f, 0.47f), new Vector3(0.44f, 0.2f, 0.07f), coat, new Vector3(-18f, 0f, 0f));
            for (int b = 0; b < 3; b++)
                CritterLook.Part(rig.rear, PrimitiveType.Sphere, "Button", new Vector3(0f, -0.075f, 0.34f - b * 0.12f), Vector3.one * 0.045f, dark);
        }
        if (rig.head != null)
        {
            CritterLook.Part(rig.head, PrimitiveType.Cylinder, "Hat Brim", new Vector3(0f, 0.16f, -0.01f), new Vector3(0.5f, 0.012f, 0.5f), dark);
            CritterLook.Part(rig.head, PrimitiveType.Cylinder, "Hat Crown", new Vector3(0f, 0.22f, -0.01f), new Vector3(0.3f, 0.06f, 0.3f), dark);
            rig.head.localRotation = Quaternion.Euler(52f, 0f, 0f);
        }
        return rig;
    }

    void Leave()
    {
        if (go != null) { Fx.Dust(ground, 1.2f); UnityEngine.Object.Destroy(go); }
        go = null; rig = null; LocalInReach = false;
        FenceMenu.Hide();
    }

    public void Clear()
    {
        if (go != null) UnityEngine.Object.Destroy(go);
        go = null; rig = null; LocalInReach = false;
    }
}

[DisallowMultipleComponent]
sealed class FenceContact : MonoBehaviour, IInteractable
{
    float lastGrumble = -99f;
    public bool CanInteract() => !FenceMenu.IsShowing;
    public string GetInteractionPrompt()
    {
        if (Scavenger.Level(CallingIds.TheFence) <= 0) return "\"Who sent you?\"";
        int hot = Scavenger.HotHeld;
        return hot > 0 ? $"Talk to the fence ({hot} hot)" : "Talk to the fence";
    }
    public void OnInteract(GameObject interactor)
    {
        if (Scavenger.Level(CallingIds.TheFence) > 0) { Sfx.Play("chitter", transform.position, 0.5f, 0.75f); FenceMenu.Show(); return; }
        if (Time.unscaledTime - lastGrumble < 1.5f) return;
        lastGrumble = Time.unscaledTime;
        Sfx.Play("chitter", transform.position, 0.5f, 0.6f);
        GameHUD.FloatingText(transform.position + Vector3.up * 1.6f, "\"Never seen you before.\"", new Color(0.9f, 0.85f, 0.7f), 1f);
        GameHUD.Toast("He doesn't know you. Print The Fence in " + SolidWaste.Name + " first.", ToastStyle.Info, 3f);
    }
    public void Highlight(bool highlight) { }
}
