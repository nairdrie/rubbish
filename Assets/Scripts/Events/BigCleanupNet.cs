using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>Where a Big Cleanup is (Docs/Multiplayer.md §14.5).</summary>
public enum CleanupPhase : byte { Idle = 0, Announce = 1, Running = 2, Results = 3 }

/// <summary>The host's word on the current Big Cleanup.</summary>
public struct CleanupState : INetworkSerializable, IEquatable<CleanupState>
{
    public byte phase;
    /// <summary>Bumped per event (1, 2, …); 0 = none yet this session.</summary>
    public int serial;
    /// <summary>Server time the current phase started / ends.</summary>
    public double phaseStart, phaseEnd;
    /// <summary>Results: the winner (empty = nobody scored / no new best).</summary>
    public FixedString64Bytes winnerId;
    public FixedString64Bytes winnerName;
    public byte winnerColor;
    public long prizeCents;
    /// <summary>1 = a solo event (beat your personal best); the PB it had to beat; 1 = a new PB was set.</summary>
    public byte solo, newBest;
    public int bestBefore;
    /// <summary>Host: bumped on every change to the state or the scores (NetAudit versioned domain <c>cleanup</c>).</summary>
    public uint ver;

    public CleanupPhase Phase => (CleanupPhase)phase;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref phase);
        s.SerializeValue(ref serial);
        s.SerializeValue(ref phaseStart);
        s.SerializeValue(ref phaseEnd);
        s.SerializeValue(ref winnerId);
        s.SerializeValue(ref winnerName);
        s.SerializeValue(ref winnerColor);
        s.SerializeValue(ref prizeCents);
        s.SerializeValue(ref solo);
        s.SerializeValue(ref newBest);
        s.SerializeValue(ref bestBefore);
        s.SerializeValue(ref ver);
    }

    public bool Equals(CleanupState o) => phase == o.phase && serial == o.serial && phaseStart.Equals(o.phaseStart) && phaseEnd.Equals(o.phaseEnd) &&
                                          winnerId.Equals(o.winnerId) && winnerName.Equals(o.winnerName) && winnerColor == o.winnerColor &&
                                          prizeCents == o.prizeCents && solo == o.solo && newBest == o.newBest && bestBefore == o.bestBefore && ver == o.ver;
    public override bool Equals(object obj) => obj is CleanupState o && Equals(o);
    public override int GetHashCode() => serial;
}

/// <summary>One business's line on the Big Cleanup scoreboard.</summary>
public struct CleanupScore : INetworkSerializable, IEquatable<CleanupScore>
{
    public FixedString64Bytes businessId;
    public FixedString64Bytes name;
    public byte color;
    public int bags;
    /// <summary>Total mass collected, in grams (whole numbers so every machine agrees exactly).</summary>
    public int grams;
    /// <summary>Server time of the last bag (ties go to whoever got there first).</summary>
    public double lastAt;

    public string Id => businessId.ToString();
    public string Name => name.ToString();
    public float Kg => grams / 1000f;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref businessId);
        s.SerializeValue(ref name);
        s.SerializeValue(ref color);
        s.SerializeValue(ref bags);
        s.SerializeValue(ref grams);
        s.SerializeValue(ref lastAt);
    }

    public bool Equals(CleanupScore o) => businessId.Equals(o.businessId) && name.Equals(o.name) && color == o.color && bags == o.bags &&
                                          grams == o.grams && lastAt.Equals(o.lastAt);
    public override bool Equals(object obj) => obj is CleanupScore o && Equals(o);
    public override int GetHashCode() => businessId.GetHashCode();
}

/// <summary>The numbers of the Big Cleanup in one place (rule 12).</summary>
public static class CleanupTuning
{
    /// <summary>
    /// The daily double-trash rush is OFF for now (Nick, 2026-10-09: "remove that for now"). The contract campaign wants the trash flow to
    /// mean one thing at a time. Nothing else changes: the code, the board, the HUD and the prizes stay, and it comes back by
    /// flipping this to true. Bots and the debug menu can still force one (<see cref="BigCleanupNet.DebugStart"/>).
    /// </summary>
    public static readonly bool Enabled = false;

    public const float AnnounceSeconds = 12f, RunSeconds = 600f, ResultsSeconds = 16f;
    /// <summary>Put-out rate (and caps) while it runs.</summary>
    public const float RateMul = 2f;
    /// <summary>The city's daily start window (in-game hours, hashed per day): 08:00–11:00.</summary>
    public const float StartHourMin = 8f, StartHourMax = 11f;
    /// <summary>A session's first event waits at least this long (real seconds).</summary>
    public const float FirstEventDelay = 150f;
    /// <summary>No new event within this long (real seconds) of the last one ending.</summary>
    public const float Cooldown = 600f;
    /// <summary>Co-op winner: $100 + $4 per bag, capped at $400. Solo personal best: $40 + $2 per bag, capped at $150.</summary>
    public const long WinBaseCents = 10000, WinPerBagCents = 400, WinCapCents = 40000;
    public const long SoloBaseCents = 4000, SoloPerBagCents = 200, SoloCapCents = 15000;
    public const string PrizeLedgerId = "event_prize";
    public const string CupDef = "big_cleanup_cup", RibbonDef = "big_cleanup_ribbon";
    public const string BestCounter = "cleanup_best_bags", WinsCounter = "cleanup_wins", TrophyCounter = "cleanup_trophies";
}

/// <summary>
/// Package F5 "Big Cleanup" (Docs/Multiplayer.md §14.5), on <c>NetSession.prefab</c>. Roughly once per in-game day the city
/// announces a 10-minute rush: a banner and a siren, then <see cref="CleanupTuning.RateMul"/>× the put-out rate while a live
/// scoreboard counts each business's curb bags and kilos (every curb bag paid into a truck, by the truck's business:
/// <see cref="TruckHopper.CurbBagCollected"/>). At the whistle the business with the most bags (then the most kilos, then whoever
/// got there first) wins a cash prize through <see cref="Ledger"/> (once per event, host-guarded) and a trophy display piece
/// (the Big Cleanup Cup, delivered to the owner's own keep crate and shown on their displays). Solo: beat your personal best
/// (saved per business) for a smaller prize and a ribbon.
/// <para>Host-run: the host writes <see cref="CleanupState"/> and the scores; everyone shows them (HUD banner + scoreboard,
/// the board on Depot Row). NetAudit domain <c>cleanup</c> (versioned by <see cref="CleanupState.ver"/>): state + every score
/// line must match on all machines at the same version.
/// Bots / tests: <c>-f5cleanup START,RUN</c> starts one START s after spawn running RUN s; <see cref="DebugStart"/>.</para>
/// </summary>
[NetSessionComponent(Order = 41)]
public class BigCleanupNet : NetworkBehaviour
{
    public static BigCleanupNet Instance { get; private set; }

    /// <summary>Raised on every machine when the phase changes (old, new state).</summary>
    public static event Action<CleanupPhase, CleanupState> PhaseChanged;
    /// <summary>Raised on the machine of a business that just got its trophy / ribbon.</summary>
    public static event Action<ItemInstance> TrophyReceived;

    readonly NetworkVariable<CleanupState> state = new NetworkVariable<CleanupState>();
    NetworkList<CleanupScore> scores;

    // host
    double spawnedAt, debugStartAt = -1;
    float debugRun = -1f;
    int lastScheduledDay = int.MinValue, paidSerial;
    string rewardId;
    CleanupPhase lastPhase;

    public CleanupState State => state.Value;
    public CleanupPhase Phase => state.Value.Phase;
    public bool Running => IsSpawned && state.Value.Phase == CleanupPhase.Running;
    public double ServerNow => NetworkManager != null ? NetworkManager.ServerTime.Time : 0;
    /// <summary>Seconds left in the current phase.</summary>
    public float SecondsLeft => IsSpawned ? Mathf.Max(0f, (float)(state.Value.phaseEnd - ServerNow)) : 0f;

    void Awake() => scores = new NetworkList<CleanupScore>();

    public override void OnNetworkSpawn()
    {
        Instance = this;
        state.OnValueChanged += OnState;
        if (IsServer)
        {
            spawnedAt = ServerNow;
            var arg = Arg("-f5cleanup");
            if (!string.IsNullOrEmpty(arg))
            {
                var parts = arg.Split(',');
                if (parts.Length >= 1 && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var st))
                    debugStartAt = spawnedAt + st;
                if (parts.Length >= 2 && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var run))
                    debugRun = run;
            }
            TruckHopper.CurbBagCollected += OnCollected;
            // A session begun after 08:00 can still catch today's event if its hashed start is later.
            // Only skip today's event when that particular start time has already passed.
            lastScheduledDay = ScheduledDayAtSessionStart(GameClock.DayIndex, GameClock.Day, GameClock.Hours);
        }
        lastPhase = state.Value.Phase;
        CleanupBoot.Ensure();
    }

    public override void OnNetworkDespawn()
    {
        state.OnValueChanged -= OnState;
        TruckHopper.CurbBagCollected -= OnCollected;
        if (IsServer) CurbTrashSpawner.EventRateMul = 1f;
        if (Instance == this) Instance = null;
    }

    public override void OnDestroy()
    {
        TruckHopper.CurbBagCollected -= OnCollected;
        if (Instance == this) Instance = null;
        scores?.Dispose();
        base.OnDestroy();
    }

    void OnState(CleanupState a, CleanupState b)
    {
        if (b.ver != a.ver) recordPending = true; // recorded next Update, once the whole tick (state + scores) has landed
        if (a.phase == b.phase && a.serial == b.serial) return;
        lastPhase = b.Phase;
        NetAudit.Event("cleanup", new Dictionary<string, object>
        {
            { "op", "phase" }, { "phase", b.Phase.ToString() }, { "serial", b.serial }, { "winner", b.winnerId.ToString() },
            { "prize", b.prizeCents }, { "server", IsServer }, { "hash", (long)Hash() },
        });
        if (b.Phase == CleanupPhase.Results) finalLogAt = Time.unscaledTime + 1.5f;
        try { PhaseChanged?.Invoke(a.Phase, b); }
        catch (Exception e) { Debug.LogException(e); }
    }

    float finalLogAt = -1f;
    bool recordPending;

    /// <summary>Host: publish a change (state or scores) as the next audit version.</summary>
    void Bump(ref CleanupState s) { s.ver++; if (s.ver == 0) s.ver = 1; }

    /// <summary>Every machine, 1.5 s into the results: the final board as this machine has it (the bot checker compares them).</summary>
    void LogFinal()
    {
        finalLogAt = -1f;
        var sb = new System.Text.StringBuilder();
        foreach (var r in Ranked()) sb.Append(r.Id).Append(':').Append(r.bags).Append(':').Append(r.grams).Append(';');
        var s = state.Value;
        NetAudit.Event("cleanup", new Dictionary<string, object>
        {
            { "op", "final" }, { "serial", s.serial }, { "rows", sb.ToString() }, { "winner", s.winnerId.ToString() }, { "prize", s.prizeCents },
            { "server", IsServer }, { "hash", (long)Hash() },
        });
    }

    /// <summary>The scoreboard, best first (bags, then kilos, then who got there first).</summary>
    public List<CleanupScore> Ranked(List<CleanupScore> into = null)
    {
        into ??= new List<CleanupScore>(4);
        into.Clear();
        if (scores == null || !IsSpawned) return into;
        for (int i = 0; i < scores.Count; i++) into.Add(scores[i]);
        into.Sort(Compare);
        return into;
    }

    /// <summary>The live board only shows businesses still in the Town. The full score history remains in
    /// <see cref="Ranked"/> for results, audit, and reconnect-independent event records.</summary>
    public List<CleanupScore> RankedLive(List<CleanupScore> into = null)
    {
        Ranked(into ??= new List<CleanupScore>(4));
        into.RemoveAll(r => !Businesses.TryGet(r.Id, out _));
        return into;
    }

    static int Compare(CleanupScore a, CleanupScore b)
    {
        if (a.bags != b.bags) return b.bags.CompareTo(a.bags);
        if (a.grams != b.grams) return b.grams.CompareTo(a.grams);
        if (!a.lastAt.Equals(b.lastAt)) return a.lastAt.CompareTo(b.lastAt);
        return string.CompareOrdinal(a.Id, b.Id);
    }

    // ==================================================================================== host

    void Update()
    {
        if (!IsSpawned) return;
        if (finalLogAt > 0f && Time.unscaledTime >= finalLogAt) LogFinal();
        if (recordPending)
        {
            recordPending = false;
            if (NetAuditNet.Enabled) NetAuditNet.Record("cleanup", state.Value.ver, Hash(), scores.Count);
        }
        if (!IsServer) return;
        double now = ServerNow;
        var s = state.Value;
        switch (s.Phase)
        {
            case CleanupPhase.Idle:
                if (debugStartAt > 0 && now >= debugStartAt) { debugStartAt = -1; HostAnnounce(debugRun > 0 ? debugRun : CleanupTuning.RunSeconds); }
                else if (debugStartAt < 0 && ScheduledNow(now)) HostAnnounce(CleanupTuning.RunSeconds);
                break;
            case CleanupPhase.Announce:
                if (now >= s.phaseEnd) HostStartRunning();
                break;
            case CleanupPhase.Running:
                CurbTrashSpawner.EventRateMul = CleanupTuning.RateMul;
                if (now >= s.phaseEnd) HostFinish();
                break;
            case CleanupPhase.Results:
                if (now >= s.phaseEnd) { lastEndedAt = now; HostSetPhase(CleanupPhase.Idle, 0); }
                break;
        }
    }

    /// <summary>Today's event: once per in-game day, at an hour hashed from the day (08–11), never in the first minutes.</summary>
    bool ScheduledNow(double now)
    {
        if (!CleanupTuning.Enabled) return false;
        if (now - spawnedAt < CleanupTuning.FirstEventDelay) return false;
        if (lastEndedAt > 0 && now - lastEndedAt < CleanupTuning.Cooldown) return false;
        int day = GameClock.DayIndex;
        if (day <= lastScheduledDay) return false;
        float h = StartHourFor(GameClock.Day);
        if (GameClock.Hours < h || GameClock.Hours > 20f) return false;
        lastScheduledDay = day;
        return true;
    }

    public static float StartHourFor(int day)
    {
        ulong x = WorldHash.Fnv(WorldHash.Seed, day * 7919L + 0xB16C);
        return CleanupTuning.StartHourMin + (x % 1000UL) / 1000f * (CleanupTuning.StartHourMax - CleanupTuning.StartHourMin);
    }

    /// <summary>Allow a newly hosted session to catch today's scheduled event if it has not started yet.</summary>
    public static int ScheduledDayAtSessionStart(int dayIndex, int day, float hour) =>
        hour >= StartHourFor(day) ? dayIndex : dayIndex - 1;

    float runSeconds = CleanupTuning.RunSeconds;
    double lastEndedAt = -1;

    void HostAnnounce(float run)
    {
        lastScheduledDay = Math.Max(lastScheduledDay, GameClock.DayIndex); // a forced one counts as today's
        runSeconds = Mathf.Max(10f, run);
        // Session identity and event serial keep keepsakes distinct even when two hosts run an event on the same day.
        rewardId = $"{(NetSession.SessionId ?? SafeFile.ProcessSessionId)}-{state.Value.serial + 1}";
        scores.Clear();
        var s = state.Value;
        s.serial++;
        s.winnerId = default;
        s.winnerName = default;
        s.winnerColor = 0;
        s.prizeCents = 0;
        s.newBest = 0;
        s.solo = (byte)(Businesses.OnlineCount <= 1 ? 1 : 0);
        s.bestBefore = s.solo != 0 ? GameSession.GetCounter(CleanupTuning.BestCounter) : 0;
        Bump(ref s);
        state.Value = s;
        HostSetPhase(CleanupPhase.Announce, CleanupTuning.AnnounceSeconds);
    }

    void HostStartRunning()
    {
        CurbTrashSpawner.EventRateMul = CleanupTuning.RateMul;
        // Everyone online starts on the board at 0 (so "nobody yet" still shows the field).
        foreach (var b in Businesses.All) EnsureRow(b.Id);
        if (scores.Count == 0) EnsureRow(Businesses.LocalId);
        HostSetPhase(CleanupPhase.Running, runSeconds);
    }

    void HostSetPhase(CleanupPhase p, float seconds)
    {
        var s = state.Value;
        s.phase = (byte)p;
        s.phaseStart = ServerNow;
        s.phaseEnd = ServerNow + seconds;
        if (p != CleanupPhase.Running) CurbTrashSpawner.EventRateMul = 1f;
        Bump(ref s);
        state.Value = s;
    }

    int EnsureRow(string businessId)
    {
        if (string.IsNullOrEmpty(businessId)) return -1;
        for (int i = 0; i < scores.Count; i++) if (scores[i].Id == businessId) return i;
        var row = new CleanupScore { lastAt = 0 };
        row.businessId.CopyFromTruncated(businessId);
        NameOf(businessId, out string name, out byte color);
        row.name.CopyFromTruncated(name);
        row.color = color;
        scores.Add(row);
        return scores.Count - 1;
    }

    static void NameOf(string id, out string name, out byte color)
    {
        if (Businesses.TryGet(id, out var b))
        {
            string owner = b.OwnerName;
            name = string.IsNullOrEmpty(owner) ? b.Name : owner + "'s " + b.Name;
            color = b.color;
            return;
        }
        name = GameSession.BusinessName;
        color = 0;
    }

    /// <summary>Host / solo: a curb bag was paid into a truck (one call per bag, <see cref="TruckHopper.CollectCurbBag"/>).</summary>
    void OnCollected(string businessId, BagContents contents, string stopId, Vector3 at)
    {
        if (!IsServer || state.Value.Phase != CleanupPhase.Running || contents == null) return;
        if (string.IsNullOrEmpty(businessId)) businessId = Businesses.LocalId;
        int i = EnsureRow(businessId);
        if (i < 0) return;
        var row = scores[i];
        row.bags++;
        row.grams += Mathf.RoundToInt(contents.TotalMassKg * 1000f);
        row.lastAt = ServerNow;
        scores[i] = row;
        var st = state.Value;
        Bump(ref st);
        state.Value = st;
        NetAudit.Event("cleanup", new Dictionary<string, object>
        {
            { "op", "score" }, { "serial", state.Value.serial }, { "business", businessId }, { "bag", contents.bagId ?? "" },
            { "bags", row.bags }, { "grams", row.grams },
        });
    }

    void HostFinish()
    {
        CurbTrashSpawner.EventRateMul = 1f;
        var s = state.Value;
        var ranked = Ranked();
        // Keep the event's announced rules if players join or leave during the rush.
        bool solo = s.solo != 0;
        CleanupScore? win = null;
        foreach (var r in ranked)
        {
            if (r.bags <= 0) break;
            if (solo && r.Id != Businesses.LocalId) continue; // a late joiner's score is not the host's personal best
            if (!solo && !Businesses.TryGet(r.Id, out _)) continue; // left the Town before the whistle
            win = r;
            break;
        }
        long prize = 0;
        string def = null;
        if (win.HasValue)
        {
            var w = win.Value;
            if (solo)
            {
                int best = GameSession.GetCounter(CleanupTuning.BestCounter);
                s.bestBefore = best;
                if (w.bags > best)
                {
                    prize = Math.Min(CleanupTuning.SoloCapCents, CleanupTuning.SoloBaseCents + CleanupTuning.SoloPerBagCents * w.bags);
                    def = CleanupTuning.RibbonDef;
                    s.newBest = 1;
                    GameSession.SetCounter(CleanupTuning.BestCounter, w.bags);
                }
                else win = null;
            }
            else
            {
                prize = Math.Min(CleanupTuning.WinCapCents, CleanupTuning.WinBaseCents + CleanupTuning.WinPerBagCents * w.bags);
                def = CleanupTuning.CupDef;
            }
        }
        if (win.HasValue)
        {
            var w = win.Value;
            s.winnerId = w.businessId;
            s.winnerName = w.name;
            s.winnerColor = w.color;
            s.prizeCents = prize;
            if (paidSerial != s.serial)
            {
                paidSerial = s.serial;
                PayPrize(w.Id, prize, s.serial);
                GiveTrophy(w.Id, def, s.serial, w.bags, w.grams, solo);
            }
        }
        else
        {
            s.winnerId = default;
            s.winnerName = default;
            s.prizeCents = 0;
        }
        Bump(ref s);
        state.Value = s;
        HostSetPhase(CleanupPhase.Results, CleanupTuning.ResultsSeconds);
    }

    void PayPrize(string businessId, long cents, int serial)
    {
        if (cents <= 0) return;
        bool local = businessId == Businesses.LocalId;
        using (Ledger.ToBusiness(local ? null : businessId))
            Ledger.Credit(cents / 100f, "Big Cleanup prize", CleanupTuning.PrizeLedgerId);
        NetAudit.Event("cleanup", new Dictionary<string, object>
        {
            { "op", "prize" }, { "serial", serial }, { "business", businessId }, { "cents", cents },
        });
    }

    void GiveTrophy(string businessId, string defId, int serial, int bags, int grams, bool solo)
    {
        if (string.IsNullOrEmpty(defId)) return;
        ulong owner = NetworkManager.ServerClientId;
        if (!solo && Businesses.TryGet(businessId, out var b)) owner = b.ownerClientId;
        var key = new FixedString64Bytes();
        key.CopyFromTruncated(businessId);
        var def = new FixedString32Bytes();
        def.CopyFromTruncated(defId);
        var eventId = new FixedString64Bytes();
        eventId.CopyFromTruncated(rewardId ?? "");
        if (owner == NetworkManager.ServerClientId) ReceiveTrophy(key.ToString(), defId, eventId.ToString(), serial, bags, grams, GameClock.Day);
        else TrophyRpc(key, def, eventId, serial, bags, grams, GameClock.Day, RpcTarget.Single(owner, RpcTargetUse.Temp));
        NetAudit.Event("cleanup", new Dictionary<string, object>
        {
            { "op", "trophy" }, { "serial", serial }, { "business", businessId }, { "def", defId }, { "owner", (long)owner },
        });
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void TrophyRpc(FixedString64Bytes businessId, FixedString32Bytes defId, FixedString64Bytes eventId, int serial, int bags, int grams, int day, RpcParams p = default)
    {
        if (IsServer) return;
        ReceiveTrophy(businessId.ToString(), defId.ToString(), eventId.ToString(), serial, bags, grams, day);
    }

    /// <summary>The winner's machine: the trophy goes into its own keep crate (saved with the business, shown on its displays).</summary>
    static void ReceiveTrophy(string businessId, string defId, string eventId, int serial, int bags, int grams, int day)
    {
        var d = ItemCatalog.Get(defId);
        if (d == null) return;
        string uid = RewardUid(eventId, businessId);
        foreach (var it in GameSession.Inventory) if (it != null && it.uid == uid) return; // already have it
        var item = new ItemInstance
        {
            uid = uid,
            defId = defId,
            massKg = d.minMass,
            value = d.minValue,
            condition = 1f,
            foundAt = "City Hall",
            dayFound = day,
            note = $"Big Cleanup, day {day}: {bags} bags, {grams / 1000f:0.#} kg",
        };
        EbaeMarket.ReceiveItem(item);
        GameSession.SetCounter(CleanupTuning.TrophyCounter, GameSession.GetCounter(CleanupTuning.TrophyCounter) + 1);
        if (defId == CleanupTuning.CupDef) GameSession.SetCounter(CleanupTuning.WinsCounter, GameSession.GetCounter(CleanupTuning.WinsCounter) + 1);
        NetAudit.Event("cleanup", new Dictionary<string, object> { { "op", "trophy-received" }, { "def", defId }, { "uid", uid } });
        try { TrophyReceived?.Invoke(item); }
        catch (Exception e) { Debug.LogException(e); }
    }

    /// <summary>One stable keepsake key per event and business, even if a new host starts another event the same game day.</summary>
    public static string RewardUid(string eventId, string businessId)
    {
        businessId ??= "";
        return $"evt-{eventId}-{businessId.Substring(0, Math.Min(8, businessId.Length))}";
    }

    // ==================================================================================== debug / tests

    /// <summary>Host / solo: announce a Big Cleanup now, running <paramref name="runSeconds"/>.</summary>
    public static bool DebugStart(float runSeconds = CleanupTuning.RunSeconds)
    {
        var n = Instance;
        if (n == null || !n.IsSpawned || !n.IsServer || n.Phase != CleanupPhase.Idle) return false;
        n.HostAnnounce(runSeconds);
        return true;
    }

    /// <summary>Host / solo: end the running event now (results + prize).</summary>
    public static bool DebugFinish()
    {
        var n = Instance;
        if (n == null || !n.IsSpawned || !n.IsServer || n.Phase != CleanupPhase.Running) return false;
        n.HostFinish();
        return true;
    }

    static string Arg(string name)
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    // ==================================================================================== audit

    ulong Hash()
    {
        ulong h = WorldHash.Seed;
        var s = state.Value;
        h = WorldHash.Fnv(h, s.phase);
        h = WorldHash.Fnv(h, s.serial);
        h = WorldHash.Fnv(h, s.winnerId.ToString());
        h = WorldHash.Fnv(h, s.prizeCents);
        var list = Ranked();
        foreach (var r in list)
        {
            h = WorldHash.Fnv(h, r.Id);
            h = WorldHash.Fnv(h, r.bags);
            h = WorldHash.Fnv(h, r.grams);
        }
        return h;
    }


    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Instance = null;
        PhaseChanged = null;
        TrophyReceived = null;
    }
}
