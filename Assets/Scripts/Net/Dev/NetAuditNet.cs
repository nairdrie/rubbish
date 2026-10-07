using System;
using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>One domain's state hash as the host saw it (sent to each guest by <see cref="NetAuditNet"/>).</summary>
public struct HashSample : INetworkSerializable
{
    public FixedString32Bytes domain;
    /// <summary>0 = versionless (compared by convergence); else the domain's version counter (compared at the same version).</summary>
    public uint version;
    public ulong hash;
    /// <summary>How many things were hashed (diagnostics only).</summary>
    public int count;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref domain);
        s.SerializeValue(ref version);
        s.SerializeValue(ref hash);
        s.SerializeValue(ref count);
    }
}

/// <summary>A probe: sequence number + up to <see cref="MaxSamples"/> samples (capped on read: other players' bytes are hostile, D12).</summary>
public struct HashProbe : INetworkSerializable
{
    public const int MaxSamples = 16;
    public uint seq;
    public HashSample[] samples;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref seq);
        int n = samples?.Length ?? 0;
        s.SerializeValue(ref n);
        if (s.IsReader)
        {
            if (n < 0 || n > MaxSamples) n = 0; // malformed: read nothing more (the RPC is dropped by the sender check anyway)
            samples = new HashSample[n];
        }
        for (int i = 0; i < n; i++) samples[i].NetworkSerialize(s);
    }
}

/// <summary>
/// Cross-machine state hash probes (Docs/Multiplayer.md §8.7.4, package B5). Lives on <c>NetSession.prefab</c>.
/// <para>
/// Every <see cref="ProbeEverySeconds"/> the host hashes each domain and sends each guest its own probe (objects are hashed per
/// guest, over what that guest can see). The guest hashes the same domains and writes a NetAudit <c>hash</c> line
/// <c>{seq, domain, v, match, host, mine, n, waited}</c>; <c>Tools/bots/audit.py</c> counts them and fails on any mismatch.
/// </para>
/// Two kinds of domain:
/// <list type="bullet">
/// <item><b>Versionless</b> (<see cref="RegisterProbe"/>): compared by convergence. Replicated state can trail the probe by a
/// few ticks, so a guest re-checks every 0.25 s for up to <see cref="ConvergeSeconds"/> before calling it a mismatch.
/// Built in: <c>roster</c> (NetRoster entries) and <c>objects</c> (spawned NetworkObjects: id, prefab, owner, player flag).</item>
/// <item><b>Versioned</b> (<see cref="Record"/>): the owner of some replicated state (B6 <c>WorldState.StateVersion</c>, B7
/// <c>CompanyState.SnapshotVersion</c>) calls <c>Record(domain, version, hash)</c> on EVERY machine each time it applies a
/// version. The guest compares the host's hash with its own hash for the same version (a "matched-version compare").</item>
/// </list>
/// Hashes use <see cref="WorldHash"/> (FNV-1a over integers and strings; never floats).
/// </summary>
[NetSessionComponent(Order = 90)]
public class NetAuditNet : NetworkBehaviour
{
    public const float ProbeEverySeconds = 5f;
    public const float ConvergeSeconds = 3f;
    const float RetrySeconds = 0.25f;
    const int HistoryPerDomain = 256;

    public delegate bool Probe(NetworkManager nm, ulong forClient, out ulong hash, out int count);

    sealed class History
    {
        public readonly Dictionary<uint, (ulong hash, int count)> byVersion = new Dictionary<uint, (ulong, int)>();
        public readonly Queue<uint> order = new Queue<uint>();
        public uint latest;
        public ulong latestHash;
        public int latestCount;
        public bool any;
    }

    struct Pending
    {
        public uint seq;
        public HashSample host;
        public float since;
        public int tries;
    }

    static readonly Dictionary<string, Probe> probes = new Dictionary<string, Probe>(StringComparer.Ordinal);
    static readonly Dictionary<ulong, (int compares, int matches, int mismatches)> guestTotals = new Dictionary<ulong, (int, int, int)>();
    static readonly Dictionary<string, History> histories = new Dictionary<string, History>(StringComparer.Ordinal);

    public static NetAuditNet Instance { get; private set; }

    /// <summary>This process's compares since boot (guests only).</summary>
    public static int Compares { get; private set; }
    public static int Matches { get; private set; }
    public static int Mismatches { get; private set; }

    /// <summary>Probes run in the Editor, development builds, and any build started with <c>-auditlog</c>.</summary>
    public static bool Enabled => Application.isEditor || Debug.isDebugBuild || NetAudit.LogPath != null;

    readonly List<Pending> pending = new List<Pending>();
    readonly List<HashSample> scratch = new List<HashSample>();
    float nextProbe, nextRetry, nextReport;
    int reportedCompares = -1;
    uint seq;
    NetRoster roster;

    // ------------------------------------------------------------------ registration (static; survives sessions)

    /// <summary>Add a versionless domain. <paramref name="probe"/> returns false when the state isn't there (skipped).</summary>
    public static void RegisterProbe(string domain, Probe probe)
    {
        if (string.IsNullOrEmpty(domain) || domain.Length > 29) throw new ArgumentException("domain must be 1-29 chars", nameof(domain));
        probes[domain] = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    /// <summary>
    /// A versioned domain applied <paramref name="version"/> with <paramref name="hash"/>: call on every machine (host and guests)
    /// each time the version changes. Keeps the last 256 versions per domain.
    /// </summary>
    public static void Record(string domain, uint version, ulong hash, int count = 0)
    {
        if (string.IsNullOrEmpty(domain) || domain.Length > 29 || version == 0) return;
        if (!histories.TryGetValue(domain, out var h)) histories[domain] = h = new History();
        if (!h.byVersion.ContainsKey(version))
        {
            h.order.Enqueue(version);
            while (h.order.Count > HistoryPerDomain) h.byVersion.Remove(h.order.Dequeue());
        }
        h.byVersion[version] = (hash, count);
        h.latest = version;
        h.latestHash = hash;
        h.latestCount = count;
        h.any = true;
    }

    // ------------------------------------------------------------------ lifetime

    public override void OnNetworkSpawn()
    {
        Instance = this;
        roster = GetComponent<NetRoster>();
        if (roster != null) roster.Changed += OnRosterChanged;
        nextProbe = Time.realtimeSinceStartup + 2f;
        NetAudit.Event("probe", IsServer ? "hash probes armed (host)" : "hash probes armed (guest)");
    }

    public override void OnNetworkDespawn()
    {
        if (roster != null) roster.Changed -= OnRosterChanged;
        roster = null;
        pending.Clear();
        if (Instance == this) Instance = null;
    }

    public override void OnDestroy()
    {
        if (roster != null) roster.Changed -= OnRosterChanged;
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    void Update()
    {
        if (!IsSpawned || !Enabled) return;
        float now = Time.realtimeSinceStartup;
        if (IsServer && now >= nextProbe)
        {
            nextProbe = now + ProbeEverySeconds;
            SendProbes();
        }
        if (!IsServer && pending.Count > 0 && now >= nextRetry)
        {
            nextRetry = now + RetrySeconds;
            ResolvePending(now);
        }
        if (!IsServer && now >= nextReport && Compares != reportedCompares)
        {
            nextReport = now + ProbeEverySeconds;
            reportedCompares = Compares;
            ReportRpc(Compares, Matches, Mismatches);
        }
    }

    /// <summary>"client 1: 24/24 matched; client 2: …" (host only: guests report their totals every probe interval).</summary>
    public static string GuestTotalsText()
    {
        if (guestTotals.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var kv in guestTotals)
            sb.Append(sb.Length > 0 ? "; " : "").Append($"client {kv.Key}: {kv.Value.matches}/{kv.Value.compares} matched, {kv.Value.mismatches} mismatched");
        return sb.ToString();
    }

    /// <summary>A guest's running totals, so the host (and MPPM probes in the main Editor) can see both sides.</summary>
    [Rpc(SendTo.Server)]
    void ReportRpc(int compares, int matches, int mismatches, RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        if (!SessionService.GuardFor("rb.audit.report").Allow(sender, 12, Time.realtimeSinceStartupAsDouble)) return;
        if (compares < 0 || matches < 0 || mismatches < 0 || matches + mismatches > compares) return; // other players' bytes are hostile
        guestTotals[sender] = (compares, matches, mismatches);
    }

    // ------------------------------------------------------------------ host side

    void SendProbes()
    {
        var nm = NetworkManager;
        if (nm == null || !nm.IsServer) return;
        seq++;
        int guests = 0;
        foreach (ulong clientId in nm.ConnectedClientsIds)
        {
            if (clientId == NetworkManager.ServerClientId) continue;
            scratch.Clear();
            foreach (var kv in probes)
            {
                if (scratch.Count >= HashProbe.MaxSamples) break;
                if (Run(kv.Value, nm, clientId, out ulong hash, out int count))
                    scratch.Add(new HashSample { domain = kv.Key, version = 0, hash = hash, count = count });
            }
            foreach (var kv in histories)
            {
                if (scratch.Count >= HashProbe.MaxSamples) break;
                if (kv.Value.any)
                    scratch.Add(new HashSample { domain = kv.Key, version = kv.Value.latest, hash = kv.Value.latestHash, count = kv.Value.latestCount });
            }
            if (scratch.Count == 0) continue;
            ProbeRpc(new HashProbe { seq = seq, samples = scratch.ToArray() }, RpcTarget.Single(clientId, RpcTargetUse.Temp));
            guests++;
        }
        if (seq == 1 || seq % 12 == 0) NetAudit.Event("probe", $"host probe {seq} sent to {guests} guest(s)");
    }

    static bool Run(Probe p, NetworkManager nm, ulong forClient, out ulong hash, out int count)
    {
        try { return p(nm, forClient, out hash, out count); }
        catch (Exception e)
        {
            Debug.LogWarning($"[NetAuditNet] A hash probe threw: {e.Message}");
            hash = 0;
            count = 0;
            return false;
        }
    }

    // ------------------------------------------------------------------ guest side

    [Rpc(SendTo.SpecifiedInParams)]
    void ProbeRpc(HashProbe probe, RpcParams rpcParams)
    {
        if (IsServer) return;
        if (rpcParams.Receive.SenderClientId != NetworkManager.ServerClientId) return; // only the host's probes count
        if (probe.samples == null) return;
        float now = Time.realtimeSinceStartup;
        foreach (var s in probe.samples) pending.Add(new Pending { seq = probe.seq, host = s, since = now });
        ResolvePending(now);
    }

    void ResolvePending(float now)
    {
        var nm = NetworkManager;
        for (int i = pending.Count - 1; i >= 0; i--)
        {
            var p = pending[i];
            p.tries++;
            string domain = p.host.domain.ToString();
            bool timedOut = now - p.since >= ConvergeSeconds;
            if (p.host.version == 0)
            {
                if (!probes.TryGetValue(domain, out var probe) || !Run(probe, nm, nm != null ? nm.LocalClientId : 0, out ulong mine, out int n))
                {
                    if (timedOut) { Skip(p, domain, "domain unavailable here"); pending.RemoveAt(i); }
                    else pending[i] = p;
                    continue;
                }
                if (mine == p.host.hash) { Compare(p, domain, true, mine, n, now); pending.RemoveAt(i); }
                else if (timedOut) { Compare(p, domain, false, mine, n, now); pending.RemoveAt(i); }
                else pending[i] = p;
            }
            else
            {
                histories.TryGetValue(domain, out var h);
                if (h != null && h.byVersion.TryGetValue(p.host.version, out var got))
                {
                    Compare(p, domain, got.hash == p.host.hash, got.hash, got.count, now);
                    pending.RemoveAt(i);
                }
                else if (h != null && h.any && h.latest > p.host.version && !h.byVersion.ContainsKey(p.host.version))
                {
                    Skip(p, domain, $"version {p.host.version} not kept here (latest {h.latest})");
                    pending.RemoveAt(i);
                }
                else if (timedOut)
                {
                    Skip(p, domain, h == null || !h.any ? "no local versions" : $"still at version {h.latest} after {ConvergeSeconds:0} s");
                    pending.RemoveAt(i);
                }
                else pending[i] = p;
            }
        }
    }

    void Compare(Pending p, string domain, bool match, ulong mine, int n, float now)
    {
        Compares++;
        if (match) Matches++;
        else Mismatches++;
        NetAudit.Event("hash", new Dictionary<string, object>
        {
            { "seq", p.seq }, { "domain", domain }, { "v", p.host.version }, { "match", match },
            { "host", p.host.hash }, { "mine", mine }, { "hostN", p.host.count }, { "n", n },
            { "waited", Math.Round(now - p.since, 2) },
        });
        if (!match)
            Debug.LogWarning($"[NetAuditNet] HASH MISMATCH {domain} (probe {p.seq}, v{p.host.version}): host {p.host.hash:x16} " +
                             $"({p.host.count}) vs mine {mine:x16} ({n}) after {ConvergeSeconds:0} s. {Describe(domain)}");
    }

    static void Skip(Pending p, string domain, string why) =>
        NetAudit.Event("hashskip", new Dictionary<string, object> { { "seq", p.seq }, { "domain", domain }, { "v", p.host.version }, { "why", why } });

    string Describe(string domain)
    {
        var nm = NetworkManager;
        if (domain == "objects" && nm != null && nm.SpawnManager != null)
            return $"Spawned here: {nm.SpawnManager.SpawnedObjectsList.Count}.";
        if (domain == "roster" && roster != null && roster.Entries != null)
        {
            var sb = new StringBuilder("Roster here: ");
            for (int i = 0; i < roster.Entries.Count; i++) sb.Append(roster.Entries[i]).Append("; ");
            return sb.ToString();
        }
        return "";
    }

    void OnRosterChanged()
    {
        if (roster == null || roster.Entries == null) return;
        var list = new List<string>(roster.Entries.Count);
        for (int i = 0; i < roster.Entries.Count; i++)
        {
            var e = roster.Entries[i];
            list.Add($"{e.playerKey}#c{e.company}s{e.slot}@{e.clientId}");
        }
        NetAudit.Event("roster", new Dictionary<string, object> { { "count", list.Count }, { "entries", list }, { "host", IsServer } });
    }

    // ------------------------------------------------------------------ built-in domains

    static bool RosterProbe(NetworkManager nm, ulong forClient, out ulong hash, out int count)
    {
        hash = WorldHash.Seed;
        count = 0;
        var r = NetRoster.Instance;
        if (r == null || r.Entries == null) return false;
        var rows = new List<RosterEntry>(r.Entries.Count);
        for (int i = 0; i < r.Entries.Count; i++) rows.Add(r.Entries[i]);
        rows.Sort((a, b) => a.clientId.CompareTo(b.clientId));
        foreach (var e in rows)
        {
            hash = WorldHash.Fnv(hash, unchecked((long)e.clientId));
            hash = WorldHash.Fnv(hash, e.playerKey.ToString());
            hash = WorldHash.Fnv(hash, e.displayName.ToString());
            hash = WorldHash.Fnv(hash, e.company);
            hash = WorldHash.Fnv(hash, e.slot);
            hash = WorldHash.Fnv(hash, e.flags);
        }
        count = rows.Count;
        return true;
    }

    static readonly List<NetworkObject> objScratch = new List<NetworkObject>();

    /// <summary>Spawned objects the guest can see: id, prefab hash, owner, player flag. On the host, filtered to <paramref name="forClient"/>.</summary>
    static bool ObjectsProbe(NetworkManager nm, ulong forClient, out ulong hash, out int count)
    {
        hash = WorldHash.Seed;
        count = 0;
        if (nm == null || nm.SpawnManager == null || !nm.IsListening) return false;
        objScratch.Clear();
        foreach (var no in nm.SpawnManager.SpawnedObjectsList)
        {
            if (no == null) continue;
            if (nm.IsServer && !no.IsNetworkVisibleTo(forClient)) continue;
            objScratch.Add(no);
        }
        objScratch.Sort((a, b) => a.NetworkObjectId.CompareTo(b.NetworkObjectId));
        foreach (var no in objScratch)
        {
            hash = WorldHash.Fnv(hash, unchecked((long)no.NetworkObjectId));
            hash = WorldHash.Fnv(hash, no.PrefabIdHash);
            hash = WorldHash.Fnv(hash, unchecked((long)no.OwnerClientId));
            hash = WorldHash.Fnv(hash, no.IsPlayerObject ? 1 : 0);
        }
        count = objScratch.Count;
        objScratch.Clear();
        return true;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Instance = null;
        Compares = Matches = Mismatches = 0;
        guestTotals.Clear();
        probes.Clear();
        histories.Clear();
        probes["roster"] = RosterProbe;
        probes["objects"] = ObjectsProbe;
    }
}
