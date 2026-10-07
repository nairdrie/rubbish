using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The Back Sack (work_gloves L2/L3, Docs/Progression.md §6.4): carrying a bag, press E on a second bag and the first
/// one goes onto your back; one E at the hopper tosses them all. L2 holds 1 on your back (2 bags total), L3 holds 2.
/// Built as a player-side IInteractable published through an <see cref="InteractionZone"/> that rides on the player
/// (the PlayerController scans zones), plus TrashCarrier's public API (ReleaseToScript / PickUp / TossInto): no
/// TrashCarrier edits. Stashed bags stay in their own scene (they follow your back each frame, never parented to the
/// player) as kinematic, collider-less "in flight" carryables; they slide off when you drive, sit down at chess or the
/// sort station, or lose the slot. With empty hands, E takes the top bag back off your back.
/// Prompt scores: "Toss N bags into truck" 3.2 (beats the carrier's 3), "Stash & grab" 2.4, "Take bag off back" 0.3.
/// </summary>
[DisallowMultipleComponent]
public class BackSack : MonoBehaviour, IInteractable, IInteractionProvider
{
    enum Act { None, StashGrab, TossAll, TakeOff }

    PlayerController pc;
    TrashCarrier carrier;
    InteractionZone zone;
    readonly List<Carryable> stash = new List<Carryable>();
    readonly Dictionary<Carryable, float> stashedAt = new Dictionary<Carryable, float>();
    readonly Dictionary<Carryable, Vector3> stashFrom = new Dictionary<Carryable, Vector3>();
    readonly Dictionary<Carryable, Vector3> centerLocal = new Dictionary<Carryable, Vector3>();
    Act act;
    TrashBag grabTarget, highlighted;
    ICarryReceiver tossTarget;
    bool tossing;

    static readonly Collider[] overlap = new Collider[24];

    /// <summary>Bags on your back right now.</summary>
    public int Count => stash.Count;
    public IReadOnlyList<Carryable> Stashed => stash;

    void Awake()
    {
        pc = GetComponent<PlayerController>();
        carrier = GetComponent<TrashCarrier>();
        var go = new GameObject("Back Sack Zone");
        go.transform.SetParent(transform, false);
        zone = go.AddComponent<InteractionZone>();
        zone.mode = ZoneMode.OnFoot;
        zone.center = Vector3.up;
        zone.size = Vector3.one * 6f;
        zone.score = 0f;
        zone.targetOverride = this;
    }

    void OnDisable() => DropAll(true);
    void OnDestroy() => DropAll(true);

    // =====================================================================
    // Decide what E means right now
    // =====================================================================

    void Update()
    {
        if (pc == null || carrier == null || !pc.IsOwner) { act = Act.None; return; }
        int slots = UpgradeEffects.BackSackSlots;
        Prune();
        if (stash.Count > 0 && (pc.IsDriving || pc.IsPlayingChess || carrier.ExternalControl || SceneController.IsTransitioning || stash.Count > slots))
            DropAll(false);
        Decide(slots);
        zone.score = act == Act.TossAll ? 3.2f : act == Act.StashGrab ? 2.4f : act == Act.TakeOff ? 0.3f : 0f;
    }

    void Prune()
    {
        for (int i = stash.Count - 1; i >= 0; i--)
        {
            var c = stash[i];
            if (c == null || c.State == CarryState.Consumed || c.State == CarryState.Carried || (c is TrashBag b && b.HasBurst))
            {
                stash.RemoveAt(i);
                if (c != null) { stashedAt.Remove(c); stashFrom.Remove(c); centerLocal.Remove(c); }
            }
        }
    }

    void Decide(int slots)
    {
        act = Act.None;
        grabTarget = null;
        tossTarget = null;
        if (slots <= 0 || tossing || pc.IsDriving || pc.IsPlayingChess || carrier.ExternalControl) return;

        if (stash.Count > 0)
        {
            Carryable probe = carrier.Held != null ? carrier.Held : stash[stash.Count - 1];
            var r = CarryReceivers.FindBest(transform.position, transform.forward, probe);
            if (r is TruckHopper) { act = Act.TossAll; tossTarget = r; return; }
        }
        if (carrier.IsCarrying && carrier.Held is TrashBag && stash.Count < slots)
        {
            grabTarget = FindNearbyBag();
            if (grabTarget != null) { act = Act.StashGrab; return; }
        }
        if (!carrier.IsCarrying && stash.Count > 0) act = Act.TakeOff;
    }

    TrashBag FindNearbyBag()
    {
        Vector3 fwd = transform.forward;
        Vector3 feet = transform.position;
        float r = pc.proximityRadius;
        Vector3 center = feet + Vector3.up * 0.6f + fwd * (r * 0.7f);
        int n = Physics.OverlapSphereNonAlloc(center, r, overlap, 1 << Carryable.InteractableLayer, QueryTriggerInteraction.Ignore);
        TrashBag best = null;
        float bestScore = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            var col = overlap[i];
            if (col == null || col.transform.IsChildOf(transform)) continue;
            var b = col.GetComponentInParent<TrashBag>();
            if (b == null || b == carrier.Held || stash.Contains(b) || b.HasBurst) continue;
            if (b.State != CarryState.Resting && b.State != CarryState.Thrown) continue;
            if (b.Holder != null || b.IsInScriptedFlight) continue;
            Vector3 flat = col.ClosestPoint(center) - feet; flat.y = 0f;
            float dist = flat.magnitude;
            float facing = dist < 0.3f ? 1f : Vector3.Dot(fwd, flat / dist);
            if (facing < -0.2f) continue;
            float score = (1f - Mathf.Clamp01(dist / (r * 2f))) + facing;
            if (score > bestScore) { bestScore = score; best = b; }
        }
        return best;
    }

    // =====================================================================
    // IInteractable (published by the zone) / IInteractionProvider
    // =====================================================================

    public bool CanInteract() => act != Act.None;

    public string GetInteractionPrompt()
    {
        switch (act)
        {
            case Act.TossAll:
            {
                int n = stash.Count + (carrier.Held != null ? 1 : 0);
                float kg = carrier.Held != null ? carrier.Held.MassKg : 0f;
                foreach (var c in stash) if (c != null) kg += c.MassKg;
                return n == 1 ? $"Toss the back bag into truck (+{kg:0.#} kg)" : $"Toss {n} bags into truck (+{kg:0.#} kg)";
            }
            case Act.StashGrab:
                return grabTarget != null ? $"Stash bag on back & grab {grabTarget.DisplayName} ({grabTarget.MassKg:0.#} kg)" : "Stash & grab";
            case Act.TakeOff:
                return "Take a bag off your back";
            default:
                return "";
        }
    }

    public void OnInteract(GameObject interactor)
    {
        switch (act)
        {
            case Act.StashGrab: StashAndGrab(grabTarget); break;
            case Act.TossAll: if (tossTarget != null) StartCoroutine(TossAll(tossTarget)); break;
            case Act.TakeOff: TakeOff(); break;
        }
    }

    public void Highlight(bool highlight)
    {
        var want = highlight && act == Act.StashGrab ? grabTarget : null;
        if (highlighted != null && highlighted != want) highlighted.Highlight(false);
        highlighted = want;
        if (want != null) want.Highlight(true);
    }

    public void CollectCandidates(PlayerController player, List<InteractionCandidate> results)
    {
        if (player != pc || !CanInteract()) return;
        results.Add(new InteractionCandidate { Target = this, Score = zone != null ? zone.score : 1f });
    }

    // =====================================================================
    // Actions
    // =====================================================================

    /// <summary>Put the held bag on your back and pick up <paramref name="next"/>. Returns false if it can't.</summary>
    public bool StashAndGrab(TrashBag next)
    {
        if (next == null || carrier == null || !(carrier.Held is TrashBag) || stash.Count >= UpgradeEffects.BackSackSlots) return false;
        if (next.Holder != null || next.IsInScriptedFlight || next.State == CarryState.Consumed) return false;
        var held = carrier.ReleaseToScript();
        if (held == null) return false;
        Stash(held);
        if (!carrier.PickUp(next))
        {
            // Couldn't grab the new one: take the stashed bag back into your hands.
            Unstash(held);
            carrier.PickUp(held);
            return false;
        }
        Vector3 p = transform.position + Vector3.up * 1.4f;
        Sfx.Play("whoosh", p, 0.6f, 1.3f);
        GameHUD.FloatingText(p + Vector3.up * 0.6f, stash.Count >= 2 ? "*stacks it on the pile on your back*" : "*slings it over shoulder*", new Color(1f, 0.85f, 0.4f), 0.85f);
        return true;
    }

    void Stash(Carryable c)
    {
        c.SetInFlight(); // kinematic, collider-less, no holder: we pose it
        stash.Add(c);
        stashedAt[c] = Time.time;
        stashFrom[c] = c.transform.position;
        Juice.Squash(c.JuiceTarget, 0.25f, 0.25f);
        Sfx.Play("rustle", c.WorldCenter, 0.7f, 0.9f);
    }

    void Unstash(Carryable c)
    {
        stash.Remove(c);
        stashedAt.Remove(c);
        stashFrom.Remove(c);
        centerLocal.Remove(c);
    }

    IEnumerator TossAll(ICarryReceiver receiver)
    {
        tossing = true;
        if (carrier.Held != null)
        {
            if (!receiver.CanAccept(carrier.Held, out _))
            {
                carrier.TossInto(receiver); // plays the carrier's "nope" + the hopper's reason
                tossing = false;
                yield break;
            }
            carrier.TossInto(receiver);
            yield return new WaitForSeconds(0.16f);
        }
        int n = 0;
        while (stash.Count > 0)
        {
            var c = stash[stash.Count - 1];
            if (receiver is Object o && o == null) break;
            if (!receiver.CanAccept(c, out string reason))
            {
                if (!string.IsNullOrEmpty(reason)) GameHUD.Toast(reason, ToastStyle.Bad);
                Sfx.Play("buzz", c.WorldCenter, 0.7f, 1f);
                break;
            }
            Unstash(c);
            c.TossArcInto(receiver, 0.42f);
            Sfx.Play("whoosh", c.WorldCenter, 0.7f, 1.1f + 0.1f * n);
            n++;
            yield return new WaitForSeconds(0.16f);
        }
        if (n > 0)
            GameHUD.FloatingText(receiver.TossTargetPosition + Vector3.up * 1.2f, n >= 2 ? "DOUBLE-FISTED DUNK!" : "and one from the back!", new Color(1f, 0.8f, 0.3f), 1.1f);
        tossing = false;
    }

    void TakeOff()
    {
        if (stash.Count == 0 || carrier.IsCarrying) return;
        var c = stash[stash.Count - 1];
        Unstash(c);
        if (!carrier.PickUp(c)) DropOne(c, false);
    }

    void DropOne(Carryable c, bool quiet)
    {
        if (c == null || c.State == CarryState.Consumed || c.Holder != null) return;
        Vector3 back = -transform.forward; back.y = 0f;
        c.EndCarry(back.normalized * 1.2f + Vector3.up * 0.5f, false);
        if (!quiet) Sfx.Play("drop", c.WorldCenter, 0.6f, 0.9f);
    }

    /// <summary>All back bags slide off behind you (never Thrown, so they can't burst).</summary>
    public void DropAll(bool quiet)
    {
        if (stash.Count == 0) return;
        var copy = new List<Carryable>(stash);
        stash.Clear();
        stashedAt.Clear();
        stashFrom.Clear();
        centerLocal.Clear();
        foreach (var c in copy) DropOne(c, quiet);
        if (!quiet && copy.Count > 0 && pc != null)
            GameHUD.FloatingText(transform.position + Vector3.up * 2f, copy.Count > 1 ? "*bags slide off your back*" : "*bag slides off your back*", new Color(0.85f, 0.85f, 0.85f), 0.8f);
    }

    // =====================================================================
    // Pose: the bags ride on your back
    // =====================================================================

    void LateUpdate()
    {
        if (stash.Count == 0) return;
        float s = Mathf.Abs(transform.lossyScale.y);
        Vector3 fwd = transform.forward; fwd.y = 0f;
        fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
        Vector3 rightV = Vector3.Cross(Vector3.up, fwd);
        float bob = Mathf.Sin(Time.time * 7f) * 0.025f;
        for (int i = 0; i < stash.Count; i++)
        {
            var c = stash[i];
            if (c == null || c.State == CarryState.Carried) continue;
            Vector3 slot = transform.position + Vector3.up * ((1.05f + 0.42f * i) * s + bob) - fwd * (0.42f * s) + rightV * ((i % 2 == 0 ? 0.06f : -0.08f) * s);
            Quaternion rot = Quaternion.LookRotation(fwd, Vector3.up) * Quaternion.Euler(-12f, 90f, 8f * (i % 2 == 0 ? 1f : -1f));
            if (!centerLocal.TryGetValue(c, out var cl)) { cl = c.LocalBounds().center; centerLocal[c] = cl; }
            Vector3 centerOff = rot * Vector3.Scale(cl, c.transform.lossyScale);
            float k = stashedAt.TryGetValue(c, out float t0) ? Mathf.Clamp01((Time.time - t0) / 0.25f) : 1f;
            Vector3 from = stashFrom.TryGetValue(c, out var f) ? f : slot;
            Vector3 target = slot - centerOff;
            Vector3 p = k < 1f ? Vector3.Lerp(from, target, k) + Vector3.up * Mathf.Sin(k * Mathf.PI) * 0.5f : target;
            c.transform.SetPositionAndRotation(p, k < 1f ? Quaternion.Slerp(c.transform.rotation, rot, k) : rot);
        }
    }
}
