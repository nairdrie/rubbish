using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Package F5 "Poaching" (Docs/Multiplayer.md §14.5), on <c>NetSession.prefab</c>. Host-only bookkeeping: a business is
/// <b>actively routing</b> a street while its truck approaches the nearest trash-bearing stop ahead (up to 70 m), crawls
/// past that street's stops (≤ 4 m/s within 22 m), or collects a curb bag there, within the last <see cref="ActiveSeconds"/>. When another business collects a
/// curb bag on that street, the victim (its owner and anyone on its payroll) gets a "POACHED!" callout with the poacher's
/// name and colour. No other penalty: the bag is simply theirs now. One callout per (victim, poacher) every
/// <see cref="CalloutCooldown"/> s and per victim every <see cref="VictimCooldown"/> s. Solo never poaches (one business).
/// </summary>
[NetSessionComponent(Order = 42)]
public class PoachingNet : NetworkBehaviour
{
    public const float ActiveSeconds = 90f, CalloutCooldown = 25f, VictimCooldown = 10f, TruckRadius = 22f, TruckCrawl = 4f, ScanSeconds = 1f;
    public const float ApproachRadius = 70f, ApproachMinSpeed = 0.5f, ApproachDot = 0.5f;

    public static PoachingNet Instance { get; private set; }

    public struct PoachInfo
    {
        public string victimId, poacherId, poacherName, street;
        public byte poacherColor;
    }

    /// <summary>Raised on the victim's machines (HUD callout, bots).</summary>
    public static event Action<PoachInfo> Poached;
    /// <summary>Host: total callouts sent (bots, audit).</summary>
    public static int CalloutsSent { get; private set; }

    // host: business → street → last server time it was working it
    readonly Dictionary<string, Dictionary<string, double>> working = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
    readonly Dictionary<(string, string), double> lastCallout = new Dictionary<(string, string), double>();
    readonly Dictionary<string, double> lastVictim = new Dictionary<string, double>(StringComparer.Ordinal);
    float nextScan;

    double Now => NetworkManager != null ? NetworkManager.ServerTime.Time : Time.realtimeSinceStartupAsDouble;

    public override void OnNetworkSpawn()
    {
        Instance = this;
        // Retired: curb bags are public, not somebody's turf. Keep the serialized network component slot stable.
        enabled = false;
    }

    public override void OnNetworkDespawn()
    {
        TruckHopper.CurbBagCollected -= OnCollected;
        working.Clear();
        lastCallout.Clear();
        lastVictim.Clear();
        if (Instance == this) Instance = null;
    }

    public override void OnDestroy()
    {
        TruckHopper.CurbBagCollected -= OnCollected;
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    /// <summary>"14 Maple St" → "Maple St"; unknown → the stop id's prefix.</summary>
    public static string StreetOf(string stopId)
    {
        if (string.IsNullOrEmpty(stopId)) return "";
        var stop = PickupStop.Find(stopId);
        string a = stop != null ? stop.address : null;
        if (string.IsNullOrEmpty(a)) return stopId;
        int i = 0;
        while (i < a.Length && (char.IsDigit(a[i]) || a[i] == '-' || char.IsLetter(a[i]) && i > 0 && char.IsDigit(a[i - 1]))) i++;
        return a.Substring(i).Trim();
    }

    void Mark(string businessId, string street, double t)
    {
        if (string.IsNullOrEmpty(businessId) || string.IsNullOrEmpty(street)) return;
        if (!working.TryGetValue(businessId, out var m)) working[businessId] = m = new Dictionary<string, double>(StringComparer.Ordinal);
        m[street] = t;
    }

    void Update()
    {
        if (!IsSpawned || !IsServer || Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + ScanSeconds;
        if (Businesses.OnlineCount <= 1) return;
        double now = Now;
        // Trucks crawling past stops with trash mark that street as theirs for a while.
        foreach (var r in CarryReceivers.All)
        {
            if (!(r is TruckHopper h) || h == null || !h.isActiveAndEnabled) continue;
            var rb = h.GetComponentInParent<Rigidbody>();
            if (rb != null && rb.linearVelocity.magnitude > TruckCrawl) continue;
            Vector3 p = h.transform.position;
            string biz = h.ResolveBusiness();
            foreach (var stop in PickupStop.All)
            {
                if (stop == null || !stop.HasTrash) continue;
                if ((stop.transform.position - p).sqrMagnitude > TruckRadius * TruckRadius) continue;
                Mark(biz, StreetOf(stop.stopId), now);
            }
        }
        MarkApproaching(now);
    }

    /// <summary>Pure approach rule: the truck has left the yard and is moving toward an as-yet-unreached curb.</summary>
    public static bool Approaching(Vector3 truckPos, Vector3 velocity, Vector3 stopPos)
    {
        Vector3 to = stopPos - truckPos;
        to.y = 0f;
        velocity.y = 0f;
        float metres = to.magnitude, speed = velocity.magnitude;
        return metres > TruckRadius && metres <= ApproachRadius && speed >= ApproachMinSpeed &&
               Vector3.Dot(velocity / speed, to / metres) > ApproachDot;
    }

    void MarkApproaching(double now)
    {
        var depot = DepotRowNet.Instance;
        if (depot == null) return;
        foreach (var business in Businesses.All)
        {
            if (!business.IsValid || business.lot < 0) continue;
            var truck = depot.TruckOf(business.lot);
            if (truck == null || truck.Body == null) continue;
            Vector3 pos = truck.transform.position;
            if (DepotRowLayout.PadAt(pos) >= 0 || DepotRowLayout.InteriorAt(pos) >= 0) continue;
            Vector3 velocity = truck.Body.linearVelocity;
            PickupStop nearest = null;
            float best = ApproachRadius * ApproachRadius;
            foreach (var stop in PickupStop.All)
            {
                if (stop == null || !stop.HasTrash || !Approaching(pos, velocity, stop.transform.position)) continue;
                Vector3 to = stop.transform.position - pos;
                to.y = 0f;
                float d2 = to.sqrMagnitude;
                if (d2 >= best) continue;
                best = d2;
                nearest = stop;
            }
            if (nearest != null) Mark(business.Id, StreetOf(nearest.stopId), now);
        }
    }

    void OnCollected(string businessId, BagContents contents, string stopId, Vector3 at)
    {
        if (!IsServer || Businesses.OnlineCount <= 1) return;
        string street = StreetOf(stopId);
        if (string.IsNullOrEmpty(street) || string.IsNullOrEmpty(businessId)) return;
        double now = Now;
        foreach (var kv in working)
        {
            string victim = kv.Key;
            if (victim == businessId) continue;
            if (!kv.Value.TryGetValue(street, out double t) || now - t > ActiveSeconds) continue;
            // The poacher was there first (it had been working this street before the victim): not a poach.
            if (working.TryGetValue(businessId, out var mine) && mine.TryGetValue(street, out double tm) && now - tm <= ActiveSeconds && tm < t - 30.0)
                continue;
            var key = (victim, businessId);
            if (lastCallout.TryGetValue(key, out double last) && now - last < CalloutCooldown) continue;
            if (lastVictim.TryGetValue(victim, out double lv) && now - lv < VictimCooldown) continue;
            lastCallout[key] = now;
            lastVictim[victim] = now;
            SendCallout(victim, businessId, street, contents != null ? contents.bagId : "");
        }
        Mark(businessId, street, now);
    }

    void SendCallout(string victimId, string poacherId, string street, string bagId)
    {
        if (!Businesses.TryGet(victimId, out var victim)) return;
        Businesses.TryGet(poacherId, out var poacher);
        string pname = poacher.IsValid ? (poacher.OwnerName.Length > 0 ? poacher.OwnerName + "'s " + poacher.Name : poacher.Name) : "Someone";
        var targets = new List<ulong> { victim.ownerClientId };
        foreach (var job in Payroll.EmployeesOf(victim.ownerClientId))
            if (!targets.Contains(job.employeeClientId)) targets.Add(job.employeeClientId);
        var v = new FixedString64Bytes(); v.CopyFromTruncated(victimId);
        var pid = new FixedString64Bytes(); pid.CopyFromTruncated(poacherId);
        var pn = new FixedString64Bytes(); pn.CopyFromTruncated(pname);
        var st = new FixedString64Bytes(); st.CopyFromTruncated(street);
        CalloutsSent++;
        NetAudit.Event("poach", new Dictionary<string, object>
        {
            { "op", "callout" }, { "victim", victimId }, { "poacher", poacherId }, { "street", street }, { "bag", bagId ?? "" }, { "to", targets.Count },
        });
        foreach (var c in targets)
        {
            if (c == NetworkManager.ServerClientId) Show(v, pid, pn, poacher.color, st);
            else PoachedRpc(v, pid, pn, poacher.color, st, RpcTarget.Single(c, RpcTargetUse.Temp));
        }
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void PoachedRpc(FixedString64Bytes victim, FixedString64Bytes poacher, FixedString64Bytes poacherName, byte color, FixedString64Bytes street, RpcParams p = default)
    {
        if (IsServer) return;
        Show(victim, poacher, poacherName, color, street);
    }

    static void Show(FixedString64Bytes victim, FixedString64Bytes poacher, FixedString64Bytes poacherName, byte color, FixedString64Bytes street)
    {
        var info = new PoachInfo
        {
            victimId = victim.ToString(), poacherId = poacher.ToString(), poacherName = poacherName.ToString(), street = street.ToString(), poacherColor = color,
        };
        NetAudit.Event("poach", new Dictionary<string, object> { { "op", "shown" }, { "victim", info.victimId }, { "poacher", info.poacherId }, { "street", info.street } });
        try { Poached?.Invoke(info); }
        catch (Exception e) { Debug.LogException(e); }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Instance = null;
        Poached = null;
        CalloutsSent = 0;
    }
}
