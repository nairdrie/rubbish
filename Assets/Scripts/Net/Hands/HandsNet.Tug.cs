using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

// Legacy Tug names remain on the wire; the phase now means a shared grip with free movement, no rhythm contest.
public partial class HandsNet
{
    NetworkList<TugNet> tugs;
    uint nextTug = 1;
    readonly Dictionary<(ulong, ulong), float> lastPairTug = new Dictionary<(ulong, ulong), float>();
    readonly Dictionary<uint, float> tugEndedAt = new Dictionary<uint, float>();
    readonly Dictionary<uint, GripRuntime> grips = new Dictionary<uint, GripRuntime>();
    readonly List<TugNet> tugsView = new List<TugNet>();
    float nextGripTick;

    sealed class GripRuntime
    {
        public Vector3 intentH, intentC, smoothH, smoothC;
        public float receivedH, receivedC, tick;
        public uint seqH, seqC;
    }

    public static event Action<TugNet> TugEnded;

    public IReadOnlyList<TugNet> TugsView
    {
        get
        {
            tugsView.Clear();
            if (tugs != null) for (int i = 0; i < tugs.Count; i++) tugsView.Add(tugs[i]);
            return tugsView;
        }
    }

    void SpawnTugs()
    {
        if (!IsServer) tugs.OnListChanged += OnTugsChanged;
        SessionService.SetGuard("rb.hands.grip", new NetGuard(25f, 40, 48));
    }

    void DespawnTugs()
    {
        if (tugs != null) tugs.OnListChanged -= OnTugsChanged;
        grips.Clear();
        lastPairTug.Clear();
        tugEndedAt.Clear();
    }

    void OnTugsChanged(NetworkListEvent<TugNet> e)
    {
        if (e.Type == NetworkListEvent<TugNet>.EventType.Value && e.Value.result != 0 && e.PreviousValue.result == 0) RaiseEnded(e.Value);
    }

    void RaiseEnded(TugNet t)
    {
        try { TugEnded?.Invoke(t); }
        catch (Exception e) { Debug.LogException(e); }
    }

    public TugNet? TugForItem(uint item)
    {
        if (tugs != null) for (int i = 0; i < tugs.Count; i++)
            if (tugs[i].item == item && tugs[i].result == 0) return tugs[i];
        return null;
    }

    public TugNet? TugOf(ulong client)
    {
        if (tugs != null) for (int i = 0; i < tugs.Count; i++)
        {
            var t = tugs[i];
            if (t.result == 0 && (t.holder == client || t.challenger == client)) return t;
        }
        return null;
    }

    bool PairFree(ulong a, ulong b)
    {
        var key = a < b ? (a, b) : (b, a);
        return !lastPairTug.TryGetValue(key, out var at) || Time.unscaledTime - at >= 0.8f;
    }

    bool GripReady(ulong client, NetCarry item)
    {
        var p = PlayerBody.For(client);
        var b = BodyOf(client);
        return p != null && p.Player != null && !p.Player.IsDriving && !p.Player.IsPlayingChess &&
            (p.Carrier == null || !p.Carrier.ExternalControl) && b.State == BodyState.Normal && b.carrying == None &&
            !CompactorCube.IsCubed(client) && Permissions.CanWorkIn(client, DepotRowLayout.InteriorAt(item.Item.transform.position));
    }

    void StartTug(NetCarry nc, ulong holder, ulong challenger)
    {
        if (holder == challenger || TugOf(holder).HasValue || TugOf(challenger).HasValue ||
            HostHolding(holder, nc) || HostHolding(challenger, nc) || !GripReady(holder, nc) || !GripReady(challenger, nc))
        {
            HostLose(challenger, nc.Id, null, "Hands busy!");
            if (nc.St.Phase == HandsPhase.Held && nc.St.Has(CarryNetState.FlagHeave))
                HostSet(nc, NewState(nc, HandsPhase.Held, holder));
            return;
        }
        float now = Time.unscaledTime;
        var t = new TugNet { tug = nextTug++, item = nc.Id, holder = holder, challenger = challenger,
            beat0 = ServerNow, meter = 0f, ripAt = 100f };
        grips[t.tug] = new GripRuntime { receivedH = now, receivedC = now, tick = now };
        tugs.Add(t);
        HostSet(nc, NewState(nc, HandsPhase.Tug, holder, 0, challenger));
        NetAudit.Event("grip", new Dictionary<string, object>
        {
            { "op", "start" }, { "grip", t.tug }, { "item", nc.Id }, { "holder", holder }, { "partner", challenger }
        });
    }

    int TugIndex(uint tug)
    {
        if (tugs != null) for (int i = 0; i < tugs.Count; i++) if (tugs[i].tug == tug) return i;
        return -1;
    }

    public void LocalGripIntent(uint grip, uint sequence, Vector3 intendedVelocity)
    {
        if (!IsSpawned) return;
        if (IsServer) HostGripIntent(LocalId, grip, sequence, intendedVelocity);
        else GripIntentRpc(grip, sequence, intendedVelocity);
    }

    [Rpc(SendTo.Server, Delivery = RpcDelivery.Unreliable, InvokePermission = RpcInvokePermission.Everyone)]
    void GripIntentRpc(uint grip, uint sequence, Vector3 intent, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (Allowed("rb.hands.grip", sender, 32)) HostGripIntent(sender, grip, sequence, intent);
    }

    void HostGripIntent(ulong client, uint id, uint sequence, Vector3 intent)
    {
        int i = TugIndex(id);
        if (i < 0 || tugs[i].result != 0 || !grips.TryGetValue(id, out var g) || !SharedGripRules.Finite(intent)) return;
        var t = tugs[i];
        intent.y = 0f;
        intent = Vector3.ClampMagnitude(intent, SharedGripRules.RunEffort);
        if (client == t.holder && sequence > g.seqH)
        { g.seqH = sequence; g.intentH = intent; g.receivedH = Time.unscaledTime; }
        else if (client == t.challenger && sequence > g.seqC)
        { g.seqC = sequence; g.intentC = intent; g.receivedC = Time.unscaledTime; }
    }

    public void LocalLetGo(uint item)
    {
        if (!IsSpawned) return;
        if (IsServer) HostLetGoGrip(LocalId, item);
        else LetGoGripRpc(item);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void LetGoGripRpc(uint item, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (Allowed("rb.hands.take", sender, 16)) HostLetGoGrip(sender, item);
    }

    internal void HostLetGoGrip(ulong client, uint item)
    {
        var t = TugForItem(item);
        if (t.HasValue && (t.Value.holder == client || t.Value.challenger == client))
            EndTug(TugIndex(t.Value.tug), t.Value.holder == client ? (byte)2 : (byte)1, true, false);
        else
        {
            // Both pressed G before the state round trip: the second release drops the remaining grip.
            var nc = Get(item);
            if (nc != null && nc.Item != null && nc.HeldBy(client))
                HostRelease(client, item, nc.Item.transform.position, nc.Item.transform.rotation, Vector3.zero, false);
        }
    }

    void HostTugsTick()
    {
        float now = Time.unscaledTime;
        if (tugs == null || now < nextGripTick) return;
        nextGripTick = now + 0.05f;
        for (int i = tugs.Count - 1; i >= 0; i--)
        {
            var t = tugs[i];
            if (t.result != 0)
            {
                if (tugEndedAt.TryGetValue(t.tug, out var at) && now - at > 0.8f)
                { tugs.RemoveAt(i); tugEndedAt.Remove(t.tug); }
                continue;
            }
            var nc = Get(t.item);
            if (nc == null || nc.Item == null) { EndTug(i, 4, false); continue; }
            bool h = GripReady(t.holder, nc), c = GripReady(t.challenger, nc);
            if (!h || !c) { EndTug(i, h ? (byte)1 : c ? (byte)2 : (byte)4, true, false); continue; }
            if (!grips.TryGetValue(t.tug, out var g)) { EndTug(i, 4); continue; }
            float dt = Mathf.Min(0.1f, now - g.tick);
            g.tick = now;
            HostPlayerPos(t.holder, out var a);
            HostPlayerPos(t.challenger, out var b);
            // A teleport/scene change must not stretch the bag across the world or manufacture a rip.
            if ((a - b).sqrMagnitude > SharedGripRules.MaxSeparation * SharedGripRules.MaxSeparation)
            { EndTug(i, 4, true, false); continue; }
            float ageH = now - g.receivedH, ageC = now - g.receivedC;
            if (ageH > SharedGripRules.InputTimeout || ageC > SharedGripRules.InputTimeout)
            {
                byte result = ageH > SharedGripRules.InputTimeout && ageC > SharedGripRules.InputTimeout ? (byte)4 :
                    ageH > SharedGripRules.InputTimeout ? (byte)2 : (byte)1;
                EndTug(i, result, true, false);
                continue;
            }
            // Missing packets pause judgement instead of counting as surrender.
            if (ageH > SharedGripRules.InputLifetime || ageC > SharedGripRules.InputLifetime) continue;
            float ease = 1f - Mathf.Exp(-12f * dt);
            g.smoothH = Vector3.Lerp(g.smoothH, g.intentH, ease);
            g.smoothC = Vector3.Lerp(g.smoothC, g.intentC, ease);
            if (ServerNow - t.beat0 < SharedGripRules.GraceSeconds) continue;
            var load = SharedGripRules.Measure(b - a, g.smoothH, g.smoothC);
            float strain = (t.strainH + t.strainC) / t.ripAt;
            byte outcome = SharedGripRules.Step(ref t.meter, ref strain, load, dt, BagTells.Of(nc.Item) == BagTell.Heavy);
            t.strainH = t.strainC = strain * t.ripAt * 0.5f;
            if (!tugs[i].Equals(t)) tugs[i] = t;
            if (outcome != 0) EndTug(i, outcome);
        }
    }

    void EndTug(int i, byte result, bool moveItem = true, bool pulledAway = true)
    {
        if (i < 0 || i >= tugs.Count || tugs[i].result != 0) return;
        var t = tugs[i];
        t.result = result;
        tugs[i] = t;
        tugEndedAt[t.tug] = Time.unscaledTime;
        grips.Remove(t.tug);
        var pair = t.holder < t.challenger ? (t.holder, t.challenger) : (t.challenger, t.holder);
        lastPairTug[pair] = Time.unscaledTime;
        RaiseEnded(t);
        var nc = Get(t.item);
        if (moveItem && nc != null && nc.Item != null)
        {
            if (result == 3 && nc.Item is TrashBag bag)
            {
                nc.GoneFlags |= CarryNetState.FlagBurst | CarryNetState.FlagRip;
                bag.NetBurstAt(bag.WorldCenter, Vector3.up, Vector3.zero);
            }
            else if (result == 3 || result == 4)
            {
                var s = NewState(nc, HandsPhase.Flight, NetworkManager.ServerClientId);
                s.vel = Vector3.zero;
                HostSet(nc, s);
            }
            else
            {
                ulong winner = result == 2 ? t.challenger : t.holder;
                HostSet(nc, NewState(nc, HandsPhase.Held, winner, pulledAway ? CarryNetState.FlagYoink : (byte)0));
            }
        }
        NetAudit.Event("grip", new Dictionary<string, object>
        {
            { "op", "end" }, { "grip", t.tug }, { "item", t.item }, { "result", result },
            { "advantage", Math.Round(t.meter, 2) }, { "strain", Math.Round(t.strainH + t.strainC, 1) }
        });
    }

    void CancelTugFor(uint item, string why)
    {
        var t = TugForItem(item);
        if (t.HasValue) EndTug(TugIndex(t.Value.tug), 4, false, false);
    }

    void CancelTugsOf(ulong client)
    {
        if (tugs == null || !IsServer) return;
        var t = TugOf(client);
        if (t.HasValue) EndTug(TugIndex(t.Value.tug), t.Value.holder == client ? (byte)2 : (byte)1, true, false);
    }
}
