using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>How hard a hit lands (§4.5).</summary>
public enum HitKind : byte
{
    /// <summary>An empty-handed punch: a short stumble, repeatable as part of a combo.</summary>
    Nudge = 0,
    /// <summary>Stumble ~0.45 s; whatever you carry pops loose.</summary>
    Stumble = 1,
    /// <summary>Knocked down: flat with dizzy stars; duration depends on the impact or punch combo.</summary>
    KnockOut = 2,
    /// <summary>The loser of a tug staggers back 0.4 s.</summary>
    Stagger = 3,
}

// Players: bonk, punch, flop, carry and throw one another, hitch a ride (Docs/Multiplayer.md §4.4–4.5, §13.5b). Rule 12 for
// every verb: cooldown / counter / max helpless / detecting machine.
//  - Bonk (thrown item hits a player): thrower detects, host confirms; 4 s per thrower-victim pair; dodge = face it empty-handed
//    (you catch instead); ≤1.2 s helpless; 5 s immunity after.
//  - Punch (RMB): host checks range and 0.58 s cadence. Three landed punches on one target in 2.8 s knock them down;
//    whiffs, target switches and timeouts break the combo. A held-item swing remains slower and is not a combo hit.
//  - Flop (Z) or knockdown allows another player to carry you, regardless of crew. Carried ≤10 s, 5 taps of Space
//    escape, no re-grab for 3 s. A thrown player flies on their own machine and lands knocked down briefly.
//  - Hitch: stand on a truck's rear step (≤4.5 m/s to get on), pinned on every machine to its own copy of the truck; hop off
//    with Space (stumble above 3 m/s). Opt-in, so no helpless limit.
public partial class HandsNet
{
    /// <summary>Host: a knock landed (victim, attacker, kind). F4 night shift listens (additive hook).</summary>
    public static event Action<ulong, ulong, HitKind> HostKnocked;

    NetworkList<BodyNet> bodies;
    readonly Dictionary<ulong, float> lastShoveAt = new Dictionary<ulong, float>();
    struct PunchCombo { public ulong target; public byte hits; public float lastAt; }
    readonly Dictionary<ulong, PunchCombo> punchCombos = new Dictionary<ulong, PunchCombo>();
    readonly Dictionary<ulong, List<float>> wriggles = new Dictionary<ulong, List<float>>();
    float nextTruckScan;

    void AwakePlayers()
    {
        bodies = new NetworkList<BodyNet>();
        tugs = new NetworkList<TugNet>();
    }

    void SpawnPlayers()
    {
        CoopPlayerHooks.Spawned -= OnPlayerSpawned;
        CoopPlayerHooks.Spawned += OnPlayerSpawned;
        foreach (var pc in FindObjectsByType<PlayerController>(FindObjectsSortMode.None)) OnPlayerSpawned(pc);
    }

    void DespawnPlayers() => CoopPlayerHooks.Spawned -= OnPlayerSpawned;

    void OnPlayerSpawned(PlayerController pc)
    {
        if (pc == null || Solo) return;
        if (pc.GetComponent<PlayerBody>() == null) pc.gameObject.AddComponent<PlayerBody>();
    }

    // ==================================================================================== body records

    public BodyNet BodyOf(ulong client)
    {
        if (bodies != null)
            for (int i = 0; i < bodies.Count; i++) if (bodies[i].client == client) return bodies[i];
        return new BodyNet { client = client, other = None, carrying = None };
    }

    int BodyIndex(ulong client)
    {
        for (int i = 0; i < bodies.Count; i++) if (bodies[i].client == client) return i;
        return -1;
    }

    void HostEnsureBody(ulong client)
    {
        if (!IsServer || BodyIndex(client) >= 0) return;
        bodies.Add(new BodyNet { client = client, other = None, carrying = None });
    }

    void HostSetBody(BodyNet b)
    {
        int i = BodyIndex(b.client);
        if (i < 0) { bodies.Add(b); return; }
        if (!bodies[i].Equals(b)) bodies[i] = b;
    }

    void HostPlayerLeft(ulong client)
    {
        int i = BodyIndex(client);
        if (i >= 0) bodies.RemoveAt(i);
        for (int k = 0; k < bodies.Count; k++)
        {
            var b = bodies[k];
            bool changed = false;
            if (b.carrying == client) { b.carrying = None; changed = true; }
            if (b.State == BodyState.Carried && b.other == client) { b.state = (byte)BodyState.Flopped; b.other = None; b.until = 0; changed = true; }
            if (changed) bodies[k] = b;
        }
        CancelTugsOf(client);
        lastShoveAt.Remove(client);
        punchCombos.Remove(client);
        foreach (var attacker in new List<ulong>(punchCombos.Keys))
            if (punchCombos[attacker].target == client) punchCombos.Remove(attacker);
        wriggles.Remove(client);
        lastGrabAt.Remove(client);
    }

    void HostPlayersTick()
    {
        double now = ServerNow;
        for (int i = 0; i < bodies.Count; i++)
        {
            var b = bodies[i];
            var st = b.State;
            bool changed = false;
            if (b.until > 0 && now >= b.until)
            {
                switch (st)
                {
                    case BodyState.KnockedOut:
                    case BodyState.Thrown:
                        b.state = (byte)BodyState.Normal;
                        b.immuneUntil = Math.Max(b.immuneUntil, now + HandsTuning.Immunity);
                        break;
                    case BodyState.Stunned:
                        b.state = (byte)BodyState.Normal;
                        // Short punches must not grant the five-second hit immunity: otherwise a combo cannot land.
                        // Stumble/bonk immunity was already assigned by HostKnock at impact.
                        break;
                    case BodyState.Carried:
                        HostClearCarrier(b.other, b.client);
                        b.state = (byte)BodyState.Flopped; // dropped after 10 s, still playing dead
                        b.other = None;
                        break;
                }
                b.until = 0;
                changed = true;
            }
            if (b.State == BodyState.Carried)
            {
                var c = BodyOf(b.other);
                if (c.carrying != b.client || (c.State != BodyState.Normal && c.State != BodyState.Riding))
                {
                    b.state = (byte)BodyState.Flopped;
                    b.other = None;
                    b.until = 0;
                    changed = true;
                }
            }
            if (b.State == BodyState.Riding && HitchStep.Find(b.other) == null)
            {
                b.state = (byte)BodyState.Normal;
                b.other = None;
                changed = true;
            }
            if (changed) bodies[i] = b;
        }
    }

    void HostClearCarrier(ulong carrier, ulong target)
    {
        int i = BodyIndex(carrier);
        if (i < 0) return;
        var c = bodies[i];
        if (c.carrying != target) return;
        c.carrying = None;
        bodies[i] = c;
    }

    void PlayersTick()
    {
        if (Time.unscaledTime < nextTruckScan) return;
        nextTruckScan = Time.unscaledTime + 1f;
        foreach (var r in CarryReceivers.All)
        {
            if (!(r is TruckHopper h) || h == null) continue;
            var truck = h.GetComponentInParent<TruckControllerV2>();
            if (truck != null) HitchStep.Ensure(truck);
        }
    }

    // ==================================================================================== knocks

    /// <summary>Host: hit <paramref name="victim"/> (bonk, shove, tug loss). False when they're immune or can't be hit.</summary>
    bool HostKnock(ulong victim, ulong attacker, Vector3 dir, HitKind kind, bool dropItem, string word, float durationOverride = 0f)
    {
        int i = BodyIndex(victim);
        if (i < 0) return false;
        var b = bodies[i];
        double now = ServerNow;
        if (kind != HitKind.Stagger && b.immuneUntil > now) return false;
        var st = b.State;
        if (st != BodyState.Normal && st != BodyState.Stunned) return false; // flopped, carried, riding, thrown, out: no chains (G3)
        if (kind != HitKind.Stagger && TugOf(victim).HasValue)
        {
            CancelTugsOf(victim); // knocked out of a tug: the other side keeps the bag
            b = bodies[i];
        }
        if (b.carrying != None)
        {
            // Whoever they were carrying tumbles out (still playing dead).
            int t = BodyIndex(b.carrying);
            if (t >= 0) { var tb = bodies[t]; tb.state = (byte)BodyState.Flopped; tb.other = None; tb.until = 0; bodies[t] = tb; }
            b.carrying = None;
        }
        float stun = kind == HitKind.KnockOut ? HandsTuning.KnockoutSeconds : kind == HitKind.Stagger ? 0.4f
                   : kind == HitKind.Stumble ? HandsTuning.StumbleSeconds : 0.25f;
        if (durationOverride > 0f) stun = durationOverride;
        float dist = kind == HitKind.KnockOut ? 1.6f : kind == HitKind.Stumble ? 1.2f : kind == HitKind.Stagger ? 0.6f : 0.8f;
        b.state = (byte)(kind == HitKind.KnockOut ? BodyState.KnockedOut : BodyState.Stunned);
        b.until = now + stun;
        if (kind != HitKind.Stagger && kind != HitKind.Nudge) b.immuneUntil = now + stun + HandsTuning.Immunity;
        bodies[i] = b;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f && HostPlayerPos(victim, out var vp) && HostPlayerPos(attacker, out var ap)) dir = vp - ap;
        dir.y = 0f;
        var w = new FixedString32Bytes();
        w.CopyFromTruncated(word ?? "");
        KnockRpc(victim, attacker, dir.sqrMagnitude > 0.0001f ? dir.normalized : Vector3.forward, dist, stun, (byte)kind, dropItem, w);
        HostKnocked?.Invoke(victim, attacker, kind); // F4: Rabies Tag, bonking a latched raccoon off a friend
        NetAudit.Event("knock", new Dictionary<string, object>
        {
            { "victim", victim }, { "attacker", attacker }, { "kind", kind.ToString() }, { "stun", stun }, { "word", word ?? "" },
        });
        return true;
    }

    [Rpc(SendTo.ClientsAndHost)]
    void KnockRpc(ulong victim, ulong attacker, Vector3 dir, float dist, float stun, byte kind, bool drop, FixedString32Bytes word)
    {
        var pb = PlayerBody.For(victim);
        if (pb == null) return;
        pb.OnKnocked(dir, dist, stun, (HitKind)kind, drop, word.ToString());
    }

    // ==================================================================================== punch / swing

    /// <summary>The local player punches (empty hands) or swings what they hold at <paramref name="target"/>.
    /// None is a whiff: it still plays the animation for everyone, but cannot hit anyone.</summary>
    public void LocalShove(ulong target, bool swing, ushort critterId = 0)
    {
        var actor = PlayerBody.For(LocalId);
        if (actor != null) actor.GetComponent<ChuckPuppet>()?.PlayPunch(swing); // local anticipation; the host relays to other screens
        if (IsServer) HostShove(LocalId, target, swing, critterId);
        else ShoveRpc(target, swing, critterId);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ShoveRpc(ulong target, bool swing, ushort critterId, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!Allowed("rb.hands.shove", sender, 24)) return;
        HostShove(sender, target, swing, critterId);
    }

    void HostShove(ulong attacker, ulong target, bool swing, ushort critterId = 0)
    {
        string why = null;
        byte comboHits = 0;
        bool landed = false;
        var a = BodyOf(attacker);
        var attackerBody = PlayerBody.For(attacker);
        var critter = CritterMelee.ById(critterId);
        bool whiff = target == None && critterId == 0;
        float now = Time.unscaledTime;
        float cooldown = swing ? HandsTuning.SwingCooldown : HandsTuning.ShoveCooldown;
        Vector3 ap = Vector3.zero, tp = Vector3.zero;
        if (critterId != 0 && (target != None || critter == null || critter.Despawning)) why = "critter-missing";
        else if (attacker == target) why = "self";
        else if (a.State != BodyState.Normal || a.carrying != None || attackerBody == null || attackerBody.Player == null ||
            attackerBody.Player.IsDriving || attackerBody.Player.IsPlayingChess || CompactorCube.IsCubed(attacker) ||
            CompactorCube.IsSuctioning(attacker) || TugOf(attacker).HasValue ||
            attackerBody.Carrier != null && attackerBody.Carrier.ExternalControl) why = "attacker-busy";
        else if (lastShoveAt.TryGetValue(attacker, out var last) && now - last < cooldown - 0.08f) why = "cooldown";
        else if (!HostPlayerPos(attacker, out ap) || (!whiff && critter == null && !HostPlayerPos(target, out tp))) why = "missing";
        else if (critter != null && !CritterMelee.InReach(ap, attackerBody.transform.forward, critter.MeleePoint,
            (swing ? HandsTuning.SwingRange : HandsTuning.ShoveRange) + 0.65f)) why = "critter-far";
        else if (critter != null && !PoleTargeting.ClearSegment(ap + Vector3.up * 1.1f, critter.MeleePoint,
            attackerBody.transform, critter.transform)) why = "wall";
        else if (critter == null && !whiff && !PoleTargeting.ClearSegment(ap + Vector3.up * 1.1f, tp + Vector3.up * 1.1f,
            PlayerBody.For(attacker)?.transform, PlayerBody.For(target)?.transform)) why = "wall";
        else if (critter == null && !whiff && (ap - tp).sqrMagnitude > Sq(HandsTuning.HostShoveRange + (swing ? 0.6f : 0f))) why = "far";
        if (why == null)
        {
            lastShoveAt[attacker] = now;
            Vector3 hitAt = ap + Vector3.up * 1.2f + (attackerBody != null ? attackerBody.transform.forward : Vector3.forward) * 1.2f;
            if (critter != null)
            {
                punchCombos.Remove(attacker);
                hitAt = critter.MeleePoint;
                Vector3 direction = critter.transform.position - ap; direction.y = 0f;
                landed = critter.TryMeleeBonk(direction.normalized * (swing ? 6f : 4f), attacker);
                if (!landed) why = "critter-immune";
            }
            else if (!whiff)
            {
                hitAt = tp + Vector3.up * 1.2f;
                var previous = punchCombos.TryGetValue(attacker, out var combo) ? combo : default;
                if (previous.hits > 0 && previous.target != target) punchCombos.Remove(attacker);
                comboHits = swing ? (byte)0 : PunchComboRules.NextHit(previous.target, previous.hits, previous.lastAt,
                    target, now, HandsTuning.PunchComboWindow);
                bool finisher = !swing && comboHits >= PunchComboRules.KnockdownHit;
                HitKind kind = swing ? HitKind.Stumble : finisher ? HitKind.KnockOut : HitKind.Nudge;
                string word = swing ? "BONK!" : finisher ? "KAPOW!" : comboHits == 2 ? "BOP! 2/3" : "BAP! 1/3";
                landed = HostKnock(target, attacker, tp - ap, kind, true, word,
                    finisher ? HandsTuning.PunchKnockdownSeconds : 0f);
                if (!landed) why = "immune";
                else if (!swing)
                {
                    if (finisher) punchCombos.Remove(attacker);
                    else punchCombos[attacker] = new PunchCombo { target = target, hits = comboHits, lastAt = now };
                }
            }
            else punchCombos.Remove(attacker);
            if (swing) punchCombos.Remove(attacker);
            PunchPlayedRpc(attacker, swing, landed, hitAt, comboHits);
        }
        NetAudit.Event("shove", new Dictionary<string, object> {
            { "attacker", attacker }, { "target", target }, { "swing", swing }, { "ok", why == null },
            { "critter", critterId },
            { "why", why }, { "hit", landed }, { "comboHits", comboHits },
            { "knockdown", landed && !swing && comboHits >= PunchComboRules.KnockdownHit },
        });
    }

    [Rpc(SendTo.ClientsAndHost)]
    void PunchPlayedRpc(ulong attacker, bool swing, bool hit, Vector3 at, byte comboHits)
    {
        if (attacker != LocalId)
            PlayerBody.For(attacker)?.GetComponent<ChuckPuppet>()?.PlayPunch(swing);
        if (!hit) return;
        bool finisher = !swing && comboHits >= PunchComboRules.KnockdownHit;
        Fx.Burst(at, finisher ? new Color(1f, 0.35f, 0.1f) : swing ? new Color(1f, 0.55f, 0.25f) : new Color(1f, 0.84f, 0.25f),
            finisher ? 26 : 12, finisher ? 4.5f : 3f);
        Sfx.Play(finisher ? "thud" : "pop", at, finisher ? 1f : 0.75f, finisher ? 0.8f : swing ? 0.75f : 0.95f);
    }

    // ==================================================================================== flop / carry / throw / wriggle

    public void LocalFlop(bool on)
    {
        if (IsServer) HostFlop(LocalId, on);
        else FlopRpc(on);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void FlopRpc(bool on, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!Allowed("rb.hands.flop", sender, 16)) return;
        HostFlop(sender, on);
    }

    void HostFlop(ulong client, bool on)
    {
        int i = BodyIndex(client);
        if (i < 0) return;
        var b = bodies[i];
        if (on && b.State == BodyState.Normal && b.carrying == None)
        {
            b.state = (byte)BodyState.Flopped;
            b.until = 0;
            CancelTugsOf(client);
        }
        else if (!on && b.State == BodyState.Flopped) b.state = (byte)BodyState.Normal;
        else return;
        bodies[i] = b;
        NetAudit.Event("flop", new Dictionary<string, object> { { "client", client }, { "on", on } });
    }

    public void LocalCarry(ulong target)
    {
        if (IsServer) HostCarry(LocalId, target);
        else CarryPlayerRpc(target);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void CarryPlayerRpc(ulong target, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!Allowed("rb.hands.carry", sender, 16)) return;
        HostCarry(sender, target);
    }

    void HostCarry(ulong carrier, ulong target)
    {
        double now = ServerNow;
        int ci = BodyIndex(carrier), ti = BodyIndex(target);
        string why = null;
        if (ci < 0 || ti < 0 || carrier == target) why = "missing";
        else
        {
            var c = bodies[ci];
            var t = bodies[ti];
            var carrierBody = PlayerBody.For(carrier);
            if (c.State != BodyState.Normal || c.carrying != None || HostHolding(carrier, null)) why = "hands-busy";
            else if (carrierBody == null || carrierBody.Player == null || carrierBody.Player.IsDriving || carrierBody.Player.IsPlayingChess) why = "carrier-busy";
            else if (t.State != BodyState.Flopped && t.State != BodyState.KnockedOut) why = "not-down";
            else if (t.noGrabUntil > now) why = "no-regrab";
            else if (!HostPlayerPos(carrier, out var cp) || !HostPlayerPos(target, out var tp) || (cp - tp).sqrMagnitude > Sq(2.5f + HandsTuning.LagSlack)) why = "far";
            if (why == null)
            {
                c.carrying = target;
                t.state = (byte)BodyState.Carried;
                t.other = carrier;
                t.until = now + HandsTuning.CarryMax;
                bodies[ci] = c;
                bodies[ti] = t;
                wriggles.Remove(target);
            }
        }
        NetAudit.Event("carry", new Dictionary<string, object> { { "carrier", carrier }, { "target", target }, { "ok", why == null }, { "why", why } });
    }

    public void LocalThrowCarried(Vector3 vel)
    {
        var me = BodyOf(LocalId);
        if (me.carrying == None) return;
        if (IsServer) HostThrowPlayer(LocalId, me.carrying, vel);
        else ThrowPlayerRpc(me.carrying, vel);
    }

    public void LocalDropCarried()
    {
        var me = BodyOf(LocalId);
        if (me.carrying == None) return;
        if (IsServer) HostDropPlayer(LocalId, me.carrying);
        else DropPlayerRpc(me.carrying);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ThrowPlayerRpc(ulong target, Vector3 vel, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!Allowed("rb.hands.throwplayer", sender, 32)) return;
        HostThrowPlayer(sender, target, vel);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void DropPlayerRpc(ulong target, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!Allowed("rb.hands.dropplayer", sender, 16)) return;
        HostDropPlayer(sender, target);
    }

    void HostThrowPlayer(ulong carrier, ulong target, Vector3 vel)
    {
        int ci = BodyIndex(carrier), ti = BodyIndex(target);
        if (ci < 0 || ti < 0) return;
        var c = bodies[ci];
        var t = bodies[ti];
        if (c.carrying != target || t.State != BodyState.Carried || t.other != carrier) return;
        double now = ServerNow;
        if (vel.magnitude > 9f) vel = vel.normalized * 9f;
        c.carrying = None;
        t.state = (byte)BodyState.Thrown;
        t.other = carrier;
        t.until = now + 3.0; // safety: the target's machine reports the landing long before this
        bodies[ci] = c;
        bodies[ti] = t;
        var pb = PlayerBody.For(carrier);
        Vector3 from = pb != null ? pb.HandPoint : Vector3.zero;
        ThrownRpc(target, from, vel);
        NetAudit.Event("throwplayer", new Dictionary<string, object> { { "carrier", carrier }, { "target", target }, { "speed", Math.Round(vel.magnitude, 2) } });
    }

    [Rpc(SendTo.ClientsAndHost)]
    void ThrownRpc(ulong target, Vector3 from, Vector3 vel)
    {
        var pb = PlayerBody.For(target);
        if (pb != null) pb.OnThrown(from, vel);
    }

    void HostDropPlayer(ulong carrier, ulong target)
    {
        int ci = BodyIndex(carrier), ti = BodyIndex(target);
        if (ci < 0 || ti < 0) return;
        var c = bodies[ci];
        var t = bodies[ti];
        if (c.carrying != target) return;
        c.carrying = None;
        bodies[ci] = c;
        if (t.State == BodyState.Carried) { t.state = (byte)BodyState.Flopped; t.other = None; t.until = 0; bodies[ti] = t; }
    }

    public void LocalWriggle()
    {
        if (IsServer) HostWriggle(LocalId);
        else WriggleRpc();
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void WriggleRpc(RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!Allowed("rb.hands.wriggle", sender, 8)) return;
        HostWriggle(sender);
    }

    void HostWriggle(ulong client)
    {
        int i = BodyIndex(client);
        if (i < 0) return;
        var b = bodies[i];
        if (b.State != BodyState.Carried) return;
        float now = Time.unscaledTime;
        if (!wriggles.TryGetValue(client, out var taps)) wriggles[client] = taps = new List<float>();
        taps.Add(now);
        taps.RemoveAll(x => now - x > 2.5f);
        if (taps.Count < HandsTuning.EscapeTaps) return;
        wriggles.Remove(client);
        double sn = ServerNow;
        HostClearCarrier(b.other, client);
        b.state = (byte)BodyState.Normal;
        b.other = None;
        b.until = 0;
        b.noGrabUntil = sn + HandsTuning.NoRegrab;
        b.immuneUntil = Math.Max(b.immuneUntil, sn + HandsTuning.NoRegrab);
        bodies[i] = b;
        NetAudit.Event("wriggle", new Dictionary<string, object> { { "client", client }, { "free", true } });
    }

    public void LocalLanded()
    {
        if (IsServer) HostLanded(LocalId);
        else LandedRpc();
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void LandedRpc(RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!Allowed("rb.hands.landed", sender, 8)) return;
        HostLanded(sender);
    }

    void HostLanded(ulong client)
    {
        int i = BodyIndex(client);
        if (i < 0) return;
        var b = bodies[i];
        if (b.State != BodyState.Thrown) return;
        double now = ServerNow;
        b.state = (byte)BodyState.KnockedOut;
        b.other = None;
        b.until = now + HandsTuning.ThrownKnockout;
        b.immuneUntil = now + HandsTuning.ThrownKnockout + HandsTuning.Immunity;
        bodies[i] = b;
    }

    // ==================================================================================== hitching

    public void LocalHitch(HitchStep hs, int step)
    {
        if (hs == null || hs.NetObject == null) return;
        if (IsServer) HostHitch(LocalId, hs.NetObject.NetworkObjectId, step);
        else HitchRpc(new NetworkObjectReference(hs.NetObject), (byte)step);
    }

    public void LocalUnhitch()
    {
        if (IsServer) HostUnhitch(LocalId);
        else UnhitchRpc();
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void HitchRpc(NetworkObjectReference truck, byte step, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!Allowed("rb.hands.hitch", sender, 24)) return;
        if (!truck.TryGet(out NetworkObject no, NetworkManager)) return;
        HostHitch(sender, no.NetworkObjectId, step);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void UnhitchRpc(RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!Allowed("rb.hands.unhitch", sender, 8)) return;
        HostUnhitch(sender);
    }

    /// <summary>Who rides step <paramref name="step"/> of the truck <paramref name="truckId"/> (None = free).</summary>
    public ulong RiderOn(ulong truckId, int step)
    {
        for (int i = 0; i < bodies.Count; i++)
            if (bodies[i].State == BodyState.Riding && bodies[i].other == truckId && bodies[i].step == step) return bodies[i].client;
        return None;
    }

    void HostHitch(ulong client, ulong truckId, int step)
    {
        int i = BodyIndex(client);
        string why = null;
        var hs = HitchStep.Find(truckId);
        if (i < 0 || hs == null || step < 0 || step >= RideHolds.Count) why = "missing";
        else
        {
            var b = bodies[i];
            if (b.State != BodyState.Normal || b.carrying != None) why = "busy";
            else if (RiderOn(truckId, step) != None) why = "taken";
            else if (hs.Speed > HandsTuning.HitchMaxSpeed + 1f) why = "too-fast";
            // The local prompt uses tight per-mount pockets and view facing. Authority allows only the usual
            // replication slack around that same pocket, not the old 5.5m teleport onto any free truck hold.
            else if (!HostPlayerPos(client, out var p) || !RideHolds.WithinBoardingBounds(step,
                ShopModKit.ToTruckMetres(hs.Truck.transform, p), HandsTuning.LagSlack)) why = "far";
            if (why == null)
            {
                b.state = (byte)BodyState.Riding;
                b.other = truckId;
                b.step = (byte)step;
                b.until = 0;
                bodies[i] = b;
                CancelTugsOf(client);
            }
        }
        NetAudit.Event("hitch", new Dictionary<string, object> { { "client", client }, { "truck", truckId }, { "step", step }, { "ok", why == null }, { "why", why } });
    }

    void HostUnhitch(ulong client)
    {
        int i = BodyIndex(client);
        if (i < 0) return;
        var b = bodies[i];
        if (b.State != BodyState.Riding) return;
        b.state = (byte)BodyState.Normal;
        b.other = None;
        bodies[i] = b;
        NetAudit.Event("hitch", new Dictionary<string, object> { { "client", client }, { "off", true } });
    }
}
