using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The Grabby Hand (the grabber pole of F3, Docs/Multiplayer.md §14.3; <see cref="ShopIds.GrabberPole"/>, a Hauler calling
/// since Docs/Progression.md §6.9: level 2 reaches further and YANKS, level 3 further still and grabs two bags; numbers in
/// <see cref="HaulerTuning"/>, the reach checked on the host by the actor's own level). Press <b>V</b> on foot:
/// <list type="number">
/// <item>a friend holding a bag in front of you within <see cref="SnatchRange"/> m: <b>snatch</b> it (co-op): that's an ordinary grab
/// attempt through N2's ownership rules (<see cref="HandsNet.LocalSnatch"/>: it can start a tug, or the host says no);</item>
/// <item>a camera-aimed bag or loose item within <see cref="ReachGrab"/> m (over low fences/hedges, never solid walls):
/// <b>grab</b> it (the usual pickup, with §13.5's shop rule);</item>
/// <item>a friend within <see cref="PokeRange"/> m: <b>poke</b> (a tiny shove, 0.8 m, no drop).</item>
/// </list>
/// Everyone sees the pole swing (relayed). On every player copy (by <see cref="TellsRuntime"/>); only the owner acts.
/// </summary>
[DisallowMultipleComponent]
public class GrabberPole : MonoBehaviour, IInteractable
{
    public const KeyCode Key = KeyCode.V;
    public const float ReachGrab = 4.5f, PokeRange = 3.3f, SnatchRange = 3.2f, Cooldown = 0.6f, Cone = 40f;

    /// <summary>Bag reach (m) of a Grabby Hand at this level: 4.5 / 6.5 / 8.5 (<see cref="ReachGrab"/> is level 1's).</summary>
    public static float ReachFor(int level) => HaulerRules.PoleReach(Mathf.Max(1, level));
    /// <summary>This copy's level: the local player's own upgrade level, or a remote copy's replicated calling level.</summary>
    int Level => Mathf.Clamp(IsLocal ? UpgradeEffects.Level(ShopIds.GrabberPole) : Workshop.CallingLevelForClient(ClientId, CallingIds.GrabberPole), 1, 3);
    float Reach => ReachFor(Level);

    public static int Grabs, Pokes, Snatches;

    PlayerController pc;
    TrashCarrier carrier;
    Transform pole, shaft, claw;
    float nextUse, nextScan;
    string hint;
    Carryable bestItem;
    PlayerTells bestPlayer;
    NetCarry bestHeld;
    Coroutine swing;
    LineRenderer targetRing;
    Transform jawLeft, jawRight, secondClaw;
    float stockStiffness = -1f, yankUntil = -1f;
    readonly Vector3[] ringPoints = new Vector3[33];
    Vector3 Hand => transform.position + Vector3.up * 1.1f + transform.right * 0.25f;

    public bool IsLocal => pc != null && CoopPlayerHooks.IsLocalOwner(pc);
    public ulong ClientId => pc != null && pc.IsSpawned ? pc.OwnerClientId : 0;
    public static bool Owned => UpgradeEffects.Level(ShopIds.GrabberPole) > 0;

    void Awake()
    {
        pc = GetComponent<PlayerController>();
        carrier = GetComponent<TrashCarrier>();
    }

    void OnEnable() => BagTellsNet.FxPlayed += OnFx;

    void OnDisable()
    {
        BagTellsNet.FxPlayed -= OnFx;
        SetHint(null);
        if (pole != null) pole.gameObject.SetActive(false);
        if (targetRing != null) targetRing.enabled = false;
        EndYank();
    }

    void OnDestroy()
    {
        if (pole != null) Destroy(pole.gameObject);
        if (targetRing != null) Destroy(targetRing.gameObject);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { Grabs = Pokes = Snatches = 0; }

    // ==================================================================================== owner

    void Update()
    {
        if (yankUntil >= 0f && Time.time >= yankUntil) EndYank();
        if (!IsLocal) return;
        bool can = Owned && pc != null && !pc.IsDriving && !pc.IsPlayingChess && !PlayerController.IsUIBlocking && !SceneController.IsTransitioning
                   && (carrier == null || !carrier.ExternalControl);
        var me = PlayerTells.LocalTells;
        if (me != null && (me.IsCube || me.Helping)) can = false;
        if (!BodyFree()) can = false;
        if (HandsNet.Instance != null && HandsNet.Instance.TugOf(ClientId).HasValue) can = false;
        if (!can) { SetHint(null); if (targetRing != null) targetRing.enabled = false; return; }
        if (Time.unscaledTime >= nextScan)
        {
            nextScan = Time.unscaledTime + 0.1f;
            Scan();
        }
        bool carrying = carrier != null && carrier.IsCarrying;
        SetHint(carrying ? null : scanHint);
        UpdateTargetRing(carrying ? null : bestHeld != null ? bestHeld.Item : bestItem);
        if (GameControls.Down(Key, GameControls.Action.Pole) && Time.time >= nextUse) Use();
    }

    float Dist(Vector3 p) { Vector3 d = p - transform.position; d.y = 0f; return d.magnitude; }

    Ray Aim => IsLocal && Camera.main != null ? Camera.main.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f))
        : new Ray(Hand, transform.forward);

    string scanHint;

    void Scan()
    {
        ScanTargets();
        string cap = GameControls.HasGamepad ? "[X / D-pad left]" : "[E / V]";
        if (bestHeld != null && bestPlayer != null) scanHint = $"{cap} Grabby-snatch {HandsNet.NameOf(bestPlayer.ClientId)}'s bag!";
        else if (bestItem != null) scanHint = $"{cap} Yoink {bestItem.DisplayName}";
        else if (bestPlayer != null) scanHint = $"{(GameControls.HasGamepad ? "[D-pad left]" : "[V]")} Poke {HandsNet.NameOf(bestPlayer.ClientId)}";
        else scanHint = null;
    }

    void ScanTargets()
    {
        bestItem = null; bestPlayer = null; bestHeld = null;
        float bi = float.MaxValue, bp = float.MaxValue;
        float reach = Reach;
        Ray aim = Aim;
        var hands = HandsNet.Instance != null && HandsNet.Instance.Active ? HandsNet.Instance : null;
        foreach (var p in PlayerTells.All)
        {
            if (p == null || p == PlayerTells.LocalTells || p.Player == null || p.Player.IsDriving) continue;
            Vector3 chest = p.transform.position + Vector3.up * 1.1f;
            if (!PoleTargeting.WithinReach(Hand, chest, PokeRange)) continue;
            float d = PoleTargeting.AimScore(aim, chest, 0.5f);
            if (d >= bp) continue;
            if (!PoleTargeting.ClearPath(Hand, chest, transform, p.transform)) continue;
            bp = d;
            bestPlayer = p;
        }
        if (bestPlayer != null && hands != null && Dist(bestPlayer.transform.position) <= SnatchRange)
            foreach (var nc in hands.Items.Values)
                if (nc != null && nc.HeldBy(bestPlayer.ClientId)) { bestHeld = nc; break; }
        var all = Carryable.All;
        for (int i = 0; i < all.Count; i++)
        {
            var c = all[i];
            if (c == null || !c.isActiveAndEnabled || c.State == CarryState.Carried || c.State == CarryState.Consumed || c.IsInScriptedFlight) continue;
            Vector3 w = c.transform.position;
            if ((w - transform.position).sqrMagnitude > (reach + 0.5f) * (reach + 0.5f)) continue;
            if (!PoleTargeting.WithinReach(Hand, c.WorldCenter, reach) || !c.CanInteract()) continue;
            float d = PoleTargeting.AimScore(aim, c.WorldCenter, 0.45f);
            if (d < bi && PoleTargeting.ClearPath(Hand, c.WorldCenter, transform, c.transform)) { bi = d; bestItem = c; }
        }
        // A bag under the reticle takes precedence over an off-centre bystander.
        if (bestItem != null && bi + 0.08f < bp) { bestPlayer = null; bestHeld = null; }
    }

    public void CollectCandidates(List<InteractionCandidate> candidates)
    {
        if (!CanInteract()) return;
        Scan();
        if (bestHeld == null && (bestItem == null || Dist(bestItem.WorldCenter) < 1.7f &&
            PoleTargeting.ClearSegment(Hand, bestItem.WorldCenter, transform, bestItem.transform))) return;
        candidates.Add(new InteractionCandidate { Target = this, Score = 2.15f });
    }

    public bool CanInteract() => IsLocal && Owned && pc != null && !pc.IsDriving && !pc.IsPlayingChess &&
        !PlayerController.IsUIBlocking && !SceneController.IsTransitioning && Time.time >= nextUse &&
        BodyFree() && carrier != null && !carrier.IsCarrying && !carrier.ExternalControl &&
        (PlayerTells.LocalTells == null || !PlayerTells.LocalTells.IsCube && !PlayerTells.LocalTells.Helping) &&
        (HandsNet.Instance == null || !HandsNet.Instance.TugOf(ClientId).HasValue);
    public string GetInteractionPrompt() => bestHeld != null ? "Grabby-snatch bag" : bestItem != null ? $"Grabby-yoink {bestItem.DisplayName}" : "Use the Grabby Hand";
    public void OnInteract(GameObject interactor) { if (CanInteract()) Use(); }
    public void Highlight(bool highlight) { }

    bool BodyFree()
    {
        if (CompactorCube.IsSuctioning(ClientId) || CompactorCube.IsCubed(ClientId)) return false;
        var hands = HandsNet.Instance;
        if (hands == null || !hands.Active) return true;
        var body = hands.BodyOf(ClientId);
        return body.State == BodyState.Normal && body.carrying == HandsNet.None && !hands.TugOf(ClientId).HasValue;
    }

    /// <summary>Owner: use the pole now (bots; V and the E reach action do the same).</summary>
    public bool UseNow()
    {
        if (!IsLocal || !Owned || Time.time < nextUse || pc.IsDriving || pc.IsPlayingChess || PlayerController.IsUIBlocking ||
            SceneController.IsTransitioning || carrier == null || carrier.ExternalControl || !BodyFree() ||
            PlayerTells.LocalTells != null && PlayerTells.LocalTells.Helping) return false;
        Use();
        return true;
    }

    void Use()
    {
        nextUse = Time.time + Cooldown;
        Scan();
        Vector3 hand = Hand;
        bool carrying = carrier != null && carrier.IsCarrying;
        var hands = HandsNet.Instance != null && HandsNet.Instance.Active ? HandsNet.Instance : null;
        if (!carrying && bestHeld != null && bestPlayer != null && hands != null)
        {
            Snatches++;
            Vector3 at = bestHeld.Item != null ? bestHeld.Item.WorldCenter : bestPlayer.transform.position + Vector3.up;
            BagTellsNet.Send(BagTellsNet.FxPole, bestPlayer.ClientId, hand, at);
            NetAudit.Event("tells", new Dictionary<string, object> { { "op", "pole-snatch" }, { "client", (long)ClientId }, { "target", (long)bestPlayer.ClientId }, { "item", bestHeld.Id } });
            hands.LocalPoleGrab(bestHeld.Item);
            GameHUD.FloatingText(at + Vector3.up * 0.6f, "GIMME!", new Color(1f, 0.5f, 0.3f), 1.1f);
            return;
        }
        if (!carrying && bestItem != null)
        {
            var c = bestItem;
            Vector3 at = c.WorldCenter;
            BagTellsNet.Send(BagTellsNet.FxPole, HandsNet.None, hand, at);
            if (!Permissions.LocalCanWorkAt(c.transform.position)) { Permissions.Deny(c.transform.position); return; }
            if (c is TrashBag && PlayerTells.LocalTells != null && PlayerTells.LocalTells.IsCube) return;
            bool accepted;
            if (hands != null) accepted = hands.LocalPoleGrab(c);
            else accepted = carrier.PickUp(c);
            if (accepted)
            {
                Grabs++;
                int level = Level;
                GameHUD.FloatingText(at + Vector3.up * 0.5f, HaulerRules.PoleYanks(level) ? "YANK!" : "YOINK!", new Color(0.6f, 0.9f, 1f), HaulerRules.PoleYanks(level) ? 1.1f : 0.9f);
                Sfx.Play("squeak", at, 0.6f, 1.4f);
                // Level 3: the same squeeze takes a second bag lying next to the first (into the pack, or to your feet).
                var second = HaulerRules.PoleGrabsTwo(level) ? SecondBag(c) : null;
                if (second != null && HandsNet.Instance != null && HandsNet.Instance.IsSpawned)
                {
                    HandsNet.Instance.LocalPoleSecond(second);
                    GameHUD.FloatingText(second.WorldCenter + Vector3.up * 0.5f, "...AND YOINK!", new Color(0.6f, 0.9f, 1f), 0.9f);
                }
                NetAudit.Event("tells", new Dictionary<string, object> { { "op", "pole-grab" }, { "client", (long)ClientId }, { "dist", Mathf.Round(Dist(at) * 10f) / 10f } });
            }
            return;
        }
        if (bestPlayer != null)
        {
            Pokes++;
            Vector3 dir = bestPlayer.transform.position - transform.position; dir.y = 0f;
            dir = dir.sqrMagnitude > 0.01f ? dir.normalized : transform.forward;
            BagTellsNet.Send(BagTellsNet.FxPole, bestPlayer.ClientId, hand, bestPlayer.transform.position + Vector3.up * 1.1f);
            BagTellsNet.Send(BagTellsNet.FxPoke, bestPlayer.ClientId, bestPlayer.transform.position, dir);
            NetAudit.Event("tells", new Dictionary<string, object> { { "op", "pole-poke" }, { "client", (long)ClientId }, { "target", (long)bestPlayer.ClientId } });
            return;
        }
        // Whiff.
        BagTellsNet.Send(BagTellsNet.FxPole, HandsNet.None, hand, hand + Aim.direction * Reach);
    }

    /// <summary>The bag a two-fisted squeeze takes along: the closest resting bag within <see cref="HaulerTuning.TwoBagRadius"/>
    /// of the first that the hand can also reach (the host checks it again).</summary>
    TrashBag SecondBag(Carryable first)
    {
        if (first == null) return null;
        TrashBag best = null;
        float bestD = HaulerTuning.TwoBagRadius * HaulerTuning.TwoBagRadius, reach = Reach;
        Vector3 at = first.WorldCenter, hand = Hand;
        var all = Carryable.All;
        for (int i = 0; i < all.Count; i++)
        {
            if (!(all[i] is TrashBag b) || b == first || !b.isActiveAndEnabled || b.State != CarryState.Resting || b.Holder != null ||
                b.IsInScriptedFlight || b.HasBurst) continue;
            float d = (b.WorldCenter - at).sqrMagnitude;
            if (d >= bestD || !PoleTargeting.WithinReach(hand, b.WorldCenter, reach) || !Permissions.LocalCanWorkAt(b.transform.position)) continue;
            if (!PoleTargeting.ClearPath(hand, b.WorldCenter, transform, b.transform)) continue;
            bestD = d;
            best = b;
        }
        return best;
    }

    /// <summary>Every copy: put the carrier's hold spring back after a yank.</summary>
    void EndYank()
    {
        yankUntil = -1f;
        if (carrier != null && stockStiffness > 0f) carrier.holdStiffness = stockStiffness;
    }

    void SetHint(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            if (hint == null) return;
            hint = null;
            GameHUD.SetContextHint(null);
            return;
        }
        if (text == hint) return;
        hint = text;
        GameHUD.SetContextHint(text);
    }

    // ==================================================================================== every copy

    void OnFx(byte kind, ulong who, ulong other, Vector3 a, Vector3 b)
    {
        if (pc == null) return;
        if (kind == BagTellsNet.FxPole && who == ClientId)
        {
            if (swing != null) StopCoroutine(swing);
            swing = StartCoroutine(Swing(b));
            Sfx.Play("whoosh", a, 0.6f, 1.5f);
            if (HaulerRules.PoleYanks(Level) && carrier != null)
            {
                // The YANK: for a moment the bag is reeled in on a much tighter spring (each copy poses its own).
                if (stockStiffness < 0f) stockStiffness = carrier.holdStiffness;
                carrier.holdStiffness = stockStiffness * HaulerTuning.YankStiffnessMult;
                yankUntil = Time.time + HaulerTuning.YankSeconds;
            }
        }
        else if (kind == BagTellsNet.FxPoke && other == ClientId)
        {
            Vector3 head = transform.position + Vector3.up * 1.9f;
            GameHUD.FloatingText(head, "BOOP!", new Color(0.6f, 0.9f, 1f), 1.2f);
            Sfx.Play("pop", head, 0.8f, 1.6f);
            if (IsLocal)
            {
                var kb = GetComponent<CritterKnockback>() ?? gameObject.AddComponent<CritterKnockback>();
                kb.Knock(b, 0.8f, 0.12f, 0.22f);
                Juice.Shake(0.06f, 0.1f);
            }
        }
    }

    IEnumerator Swing(Vector3 to)
    {
        if (pole == null) Build();
        pole.gameObject.SetActive(true);
        if (secondClaw != null) secondClaw.gameObject.SetActive(HaulerRules.PoleGrabsTwo(Level));
        for (float t = 0f; t < 0.45f; t += Time.deltaTime)
        {
            float k = t < 0.15f ? t / 0.15f : t < 0.28f ? 1f : 1f - (t - 0.28f) / 0.17f;
            Vector3 from = Hand;
            Vector3 d = to - from;
            float len = Mathf.Clamp(d.magnitude, 0.3f, Reach + 0.75f);
            if (d.sqrMagnitude > 0.001f) pole.rotation = Quaternion.LookRotation(d.normalized, Vector3.up);
            pole.position = from;
            float l = Mathf.Lerp(0.6f, len, Mathf.SmoothStep(0f, 1f, k));
            shaft.localScale = new Vector3(0.05f, l * 0.5f, 0.05f);
            shaft.localPosition = new Vector3(0f, 0f, l * 0.5f);
            claw.localPosition = new Vector3(0f, 0f, l);
            if (jawLeft != null) jawLeft.localRotation = Quaternion.Euler(0f, Mathf.Lerp(-30f, 0f, k), 0f);
            if (jawRight != null) jawRight.localRotation = Quaternion.Euler(0f, Mathf.Lerp(30f, 0f, k), 0f);
            yield return null;
        }
        pole.gameObject.SetActive(false);
        swing = null;
    }

    void Build()
    {
        pole = new GameObject("F3 GrabberPole").transform; // unparented: world-space metres (the player root is scaled)
        var steel = ShopModKit.Lit("mat/f3_pole_steel", new Color(0.8f, 0.82f, 0.86f), 0.7f, 0.6f);
        var orange = ShopModKit.Lit("mat/f3_pole_grip", new Color(1f, 0.55f, 0.1f), 0.4f);
        shaft = ShopModKit.Prim(PrimitiveType.Cylinder, "Shaft", pole, steel, Vector3.zero, new Vector3(0.05f, 1f, 0.05f), new Vector3(90f, 0f, 0f));
        claw = ShopModKit.Pivot("Claw", pole, Vector3.zero);
        jawLeft = ShopModKit.Prim(PrimitiveType.Cube, "JawL", claw, orange, new Vector3(-0.06f, 0f, 0.05f), new Vector3(0.03f, 0.05f, 0.16f), new Vector3(0f, -20f, 0f)).transform;
        jawRight = ShopModKit.Prim(PrimitiveType.Cube, "JawR", claw, orange, new Vector3(0.06f, 0f, 0.05f), new Vector3(0.03f, 0.05f, 0.16f), new Vector3(0f, 20f, 0f)).transform;
        ShopModKit.Prim(PrimitiveType.Cylinder, "Grip", pole, orange, new Vector3(0f, 0f, 0.1f), new Vector3(0.07f, 0.12f, 0.07f), new Vector3(90f, 0f, 0f));
        // Level 3's second fist: a twin claw riding just under the first (it follows it: a child of the claw pivot).
        secondClaw = ShopModKit.Pivot("Claw 2", claw, new Vector3(0f, -0.16f, -0.04f));
        ShopModKit.Prim(PrimitiveType.Cube, "JawL", secondClaw, orange, new Vector3(-0.06f, 0f, 0.05f), new Vector3(0.03f, 0.05f, 0.16f), new Vector3(0f, -20f, 0f));
        ShopModKit.Prim(PrimitiveType.Cube, "JawR", secondClaw, orange, new Vector3(0.06f, 0f, 0.05f), new Vector3(0.03f, 0.05f, 0.16f), new Vector3(0f, 20f, 0f));
        ShopModKit.Prim(PrimitiveType.Cube, "Yoke", secondClaw, steel, new Vector3(0f, 0.08f, -0.02f), new Vector3(0.03f, 0.16f, 0.03f));
        foreach (var r in pole.GetComponentsInChildren<Renderer>(true)) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        pole.gameObject.SetActive(false);
    }

    void UpdateTargetRing(Carryable item)
    {
        if (item == null) { if (targetRing != null) targetRing.enabled = false; return; }
        if (targetRing == null)
        {
            var go = new GameObject("Pole aim ring (local)");
            targetRing = go.AddComponent<LineRenderer>();
            targetRing.sharedMaterial = SortingKit.RuntimeMaterial("mat/pole_target", new Color(0.35f, 0.95f, 1f), true);
            targetRing.positionCount = ringPoints.Length;
            targetRing.useWorldSpace = true;
            targetRing.widthMultiplier = 0.025f;
            targetRing.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }
        targetRing.enabled = true;
        var cam = Camera.main;
        Vector3 right = cam != null ? cam.transform.right : transform.right;
        Vector3 up = cam != null ? cam.transform.up : Vector3.up;
        float radius = Mathf.Clamp(item.LocalBounds().extents.magnitude, 0.23f, 0.6f);
        for (int i = 0; i < ringPoints.Length; i++)
        {
            float a = i * Mathf.PI * 2f / (ringPoints.Length - 1);
            ringPoints[i] = item.WorldCenter + (right * Mathf.Cos(a) + up * Mathf.Sin(a)) * radius;
        }
        targetRing.SetPositions(ringPoints);
    }
}
