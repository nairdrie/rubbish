using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Feeds <see cref="Metrics"/> (Docs/Metrics.md): listens to the game's existing events (progression facts, the Ledger, purchases,
/// sessions, night and day, the campaign) and takes a snapshot of the world every 30 real seconds: money and rank, each open
/// district's cleanliness and curb stock, where the player spends their time (driving / on foot / in the shop / sorting / laptop /
/// menus), frame times and errors. It only READS: no gameplay code calls into it except the campaign's own events.
/// Hidden DontDestroyOnLoad object, play mode only, never in batch mode unless <c>-metrics</c> is given.
/// </summary>
public sealed class MetricsRunner : MonoBehaviour
{
    public const float SnapSeconds = 30f, FlushSeconds = 5f;
    static readonly float[] FrameBinsMs = { 8.4f, 11.2f, 16.8f, 22.4f, 33.4f, 50f, 100f, 250f };

    static MetricsRunner instance;

    // frame statistics since the last snapshot
    readonly int[] frameBins = new int[FrameBinsMs.Length + 1];
    double frameSum, frameMax;
    int frameCount, hitches;
    // seconds in each HUD mode since the last snapshot
    readonly double[] modeSeconds = new double[Enum.GetValues(typeof(HudMode)).Length];
    float nextSnap, nextFlush, nextIdentity;
    bool identityLogged;
    int newErrors;

    // errors: unique signatures with counts (the first of each is written at once)
    struct ErrorLog { public string type, message, stack; }
    readonly ConcurrentQueue<ErrorLog> errorQueue = new ConcurrentQueue<ErrorLog>();
    readonly Dictionary<string, int> errorCounts = new Dictionary<string, int>(StringComparer.Ordinal);
    const int MaxErrorSignatures = 60;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => instance = null;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (instance != null || !Application.isPlaying || !Metrics.Enabled) return;
        var go = new GameObject("[Metrics]");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<MetricsRunner>();
    }

    // ------------------------------------------------------------------ lifecycle

    void Awake()
    {
        if (instance != null && instance != this) { Destroy(gameObject); return; }
        instance = this;
        WriteHeader();
        Hook();
        nextSnap = Time.unscaledTime + SnapSeconds;
        nextFlush = Time.unscaledTime + FlushSeconds;
    }

    void OnDestroy()
    {
        if (instance != this) return;
        Finish("destroyed");          // leaving play mode in the Editor never calls OnApplicationQuit
        Unhook();
        instance = null;
    }

    void OnApplicationPause(bool paused) { if (paused) Metrics.Flush(); }

    void OnApplicationQuit() => Finish("quit");

    bool finished;

    /// <summary>The last snapshot and the session summary, once.</summary>
    void Finish(string why)
    {
        if (finished) return;
        finished = true;
        DrainErrors();
        WriteSnapshot(why);
        Metrics.Begin("session_end").Str("why", why, 16).Num("minutes", Metrics.RealSeconds / 60.0, "0.##").Raw("totals", Metrics.TotalsJson())
               .Raw("errors", ErrorsJson()).Int("error_count", TotalErrors()).Int("lines", Metrics.Lines).End();
        Metrics.Close();
    }

    void Update()
    {
        float dt = Time.unscaledDeltaTime;
        SampleFrame(dt);
        DrainErrors();
        float now = Time.unscaledTime;
        if (!identityLogged && now >= nextIdentity) { nextIdentity = now + 5f; TryLogIdentity(); }
        if (now >= nextSnap) { nextSnap = now + SnapSeconds; WriteSnapshot("tick"); }
        if (now >= nextFlush) { nextFlush = now + FlushSeconds; Metrics.Flush(); }
    }

    void SampleFrame(float dt)
    {
        if (dt <= 0f || dt > 5f) return;     // a scene load or a breakpoint is not a frame
        double ms = dt * 1000.0;
        int bin = 0;
        while (bin < FrameBinsMs.Length && ms >= FrameBinsMs[bin]) bin++;
        frameBins[bin]++;
        frameSum += ms;
        if (ms > frameMax) frameMax = ms;
        frameCount++;
        if (ms >= 100.0) hitches++;
        int mode = (int)HudLayout.Mode;
        if (mode >= 0 && mode < modeSeconds.Length) modeSeconds[mode] += dt;
    }

    // ------------------------------------------------------------------ header

    void WriteHeader()
    {
        var ev = Metrics.Begin("session").Int("schema", Metrics.Schema).Str("sid", Metrics.SessionId)
            .Str("app", Application.version).Str("unity", Application.unityVersion).Str("platform", Application.platform.ToString())
            .Str("build", Application.buildGUID).Bool("editor", Application.isEditor).Bool("dev_build", Debug.isDebugBuild)
            .Int("protocol", NetVersion.Protocol).Int("content", NetVersion.Content)
            .Str("os", SystemInfo.operatingSystem, 80).Str("cpu", SystemInfo.processorType, 80).Int("cores", SystemInfo.processorCount)
            .Int("ram_mb", SystemInfo.systemMemorySize).Str("gpu", SystemInfo.graphicsDeviceName, 80).Int("vram_mb", SystemInfo.graphicsMemorySize)
            .Int("w", Screen.width).Int("h", Screen.height).Bool("fullscreen", Screen.fullScreen)
            .Int("quality", QualitySettings.GetQualityLevel()).Int("target_fps", Application.targetFrameRate)
            .Bool("test_root", SaveRoot.IsOverridden);
        ev.End();
    }

    void TryLogIdentity()
    {
        try
        {
            var id = PlayerIdentity.Local;
            if (id == null || !id.Key.IsValid) return;
            identityLogged = true;
            Metrics.Begin("identity").Str("pid", Metrics.Tag(id.Key.Value)).Bool("steam", id.Key.IsSteam).End();
        }
        catch (Exception e) { identityLogged = true; Debug.LogWarning("[Metrics] identity: " + e.Message); }
    }

    // ------------------------------------------------------------------ the snapshot

    void WriteSnapshot(string why)
    {
        if (!Metrics.Enabled) return;
        double avg = frameCount > 0 ? frameSum / frameCount : 0.0;
        var ev = Metrics.Begin("snap").Str("why", why)
            .Num("cash", GameSession.Cash, "0.00").Int("rep", Mathf.RoundToInt(GameSession.Rep)).Int("rank", Progression.Rank)
            .Int("clean_days", GameSession.CleanDays).Int("bags_total", GameSession.TotalBagsCollected)
            .Int("treasures_total", GameSession.TotalTreasuresFound).Num("earned_total", GameSession.TotalCashEarned, "0.00")
            .Num("load_kg", GameSession.TruckLoadKg, "0.#").Num("cap_kg", GameSession.TruckCapacityKg, "0")
            .Int("players", Mathf.Max(1, SessionService.PlayerCount)).Int("biz", Businesses.OnlineCount)
            .Bool("rabid", GameSession.HasRabies).Bool("night", GameClock.IsNight).Num("hour", GameClock.Hours, "0.0")
            .Str("mode", HudLayout.Mode.ToString())
            .Raw("dist", DistrictsJson()).Raw("camp", CampaignJson()).Raw("act", ActivityJson()).Raw("c", Metrics.TakeWindow())
            .Num("fps", avg > 0 ? 1000.0 / avg : 0, "0.#").Num("ms_max", frameMax, "0.#").Int("hitches", hitches)
            .Raw("ft", Metrics.Json.Array(ToDoubles(frameBins), "0"))
            .Num("gc_mb", GC.GetTotalMemory(false) / 1048576.0, "0.#").Int("errs", newErrors);
        ev.End();
        System.Array.Clear(frameBins, 0, frameBins.Length);
        System.Array.Clear(modeSeconds, 0, modeSeconds.Length);
        frameSum = frameMax = 0; frameCount = hitches = newErrors = 0;
    }

    static double[] ToDoubles(int[] v) { var d = new double[v.Length]; for (int i = 0; i < v.Length; i++) d[i] = v[i]; return d; }

    /// <summary>[{id, cl (clean %), bags, late, neg (neglected curbs), old (oldest bag, hours), tr (Trust %), sub (customers), hp (old heap bags)}] for each open district.</summary>
    internal static string DistrictsJson()
    {
        var s = new StringBuilder(256);
        s.Append('[');
        bool first = true;
        var order = Districts.ProgressionOrder;
        double now = GameClock.TotalHours;
        var bags = GameSession.CurbBags;
        for (int i = 0; i < order.Count; i++)
        {
            var d = order[i];
            if (!DistrictAccess.OpenInTown(d)) continue;
            int n = 0, late = 0, heap = 0;
            double oldest = 0;
            for (int k = 0; k < bags.Count; k++)
            {
                var b = bags[k];
                if (b == null || Districts.ForStopId(b.stopId) != d) continue;
                double age = Math.Max(0, now - b.putOutAtHours);
                n++;
                if (Districts.IsLate(d, age)) late++;
                if (age > oldest) oldest = age;
                if (CampaignRules.IsBacklogBagId(b.bagId)) heap++;
            }
            if (!first) s.Append(',');
            first = false;
            s.Append("{\"id\":\"").Append(d.id).Append("\",\"cl\":").Append(Mathf.RoundToInt(CityCleanliness.District01(d.id) * 100f))
             .Append(",\"bags\":").Append(n).Append(",\"late\":").Append(late)
             .Append(",\"neg\":").Append(CurbTrash.NeglectedStopsIn(d))
             .Append(",\"old\":").Append(oldest.ToString("0.#", CultureInfo.InvariantCulture))
             .Append(",\"tr\":").Append(Mathf.RoundToInt(DistrictAccess.TownTrust(d) * 100f))
             .Append(",\"sub\":").Append(DistrictAccess.TownSubscribers(d))
             .Append(",\"hp\":").Append(heap).Append('}');
        }
        return s.Append(']').ToString();
    }

    static string CampaignJson()
    {
        var c = Campaign.Display;
        if (!c.Active) return "null";
        return "{\"dist\":\"" + Districts.All[Mathf.Clamp(c.district, 0, Districts.All.Count - 1)].id + "\",\"left\":" + c.backlog +
               ",\"start\":" + c.backlogStart + ",\"hold\":" + c.HoldHoursAt(GameClock.TotalHours).ToString("0.0", CultureInfo.InvariantCulture) +
               ",\"cl\":" + c.cleanPct + ",\"ok\":" + c.qualifying + "}";
    }

    string ActivityJson()
    {
        var s = new StringBuilder(96);
        s.Append('{');
        bool first = true;
        for (int i = 0; i < modeSeconds.Length; i++)
        {
            if (modeSeconds[i] < 0.05) continue;
            if (!first) s.Append(',');
            first = false;
            s.Append('"').Append(((HudMode)i).ToString()).Append("\":").Append(modeSeconds[i].ToString("0.#", CultureInfo.InvariantCulture));
        }
        return s.Append('}').ToString();
    }

    // ------------------------------------------------------------------ errors

    void OnLog(string message, string stack, LogType type)
    {
        if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
        errorQueue.Enqueue(new ErrorLog { type = type.ToString(), message = message, stack = stack });
    }

    void DrainErrors()
    {
        int guard = 0;
        while (guard++ < 50 && errorQueue.TryDequeue(out var e))
        {
            newErrors++;
            string msg = e.message ?? "";
            string firstFrame = FirstLine(e.stack);
            string sig = e.type + "|" + Trim(msg, 90) + "|" + Trim(firstFrame, 70);
            errorCounts.TryGetValue(sig, out int n);
            if (n == 0 && errorCounts.Count >= MaxErrorSignatures) { Metrics.Count("errors_dropped"); continue; }
            errorCounts[sig] = n + 1;
            if (n == 0)
                Metrics.Begin("err").Str("kind", e.type, 16).Str("msg", msg, 240).Str("at", firstFrame, 160).End();
        }
    }

    static string FirstLine(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        int i = s.IndexOf('\n');
        return (i < 0 ? s : s.Substring(0, i)).Trim();
    }

    static string Trim(string s, int n) => s == null ? "" : (s.Length <= n ? s : s.Substring(0, n));

    int TotalErrors() { int n = 0; foreach (var kv in errorCounts) n += kv.Value; return n; }

    string ErrorsJson()
    {
        if (errorCounts.Count == 0) return "[]";
        var list = new List<KeyValuePair<string, int>>(errorCounts);
        list.Sort((a, b) => b.Value.CompareTo(a.Value));
        var s = new StringBuilder(256);
        s.Append('[');
        for (int i = 0; i < list.Count && i < 20; i++)
        {
            if (i > 0) s.Append(',');
            s.Append("{\"n\":").Append(list[i].Value).Append(",\"sig\":");
            Metrics.AppendString(s, list[i].Key, 200);
            s.Append('}');
        }
        return s.Append(']').ToString();
    }

    // ------------------------------------------------------------------ the events

    void Hook()
    {
        Application.logMessageReceivedThreaded += OnLog;
        SceneManager.sceneLoaded += OnScene;
        ProgressionEvents.BagCollected += OnBag;
        ProgressionEvents.CurbCleared += OnCurbCleared;
        ProgressionEvents.ItemSorted += OnSorted;
        ProgressionEvents.TreasureKept += OnTreasure;
        ProgressionEvents.LoadCrushed += OnCrushed;
        ProgressionEvents.RankChanged += OnRank;
        ProgressionEvents.DistrictUnlocked += OnDistrictUnlocked;
        ProgressionEvents.DistrictEntered += OnDistrictEntered;
        ProgressionEvents.NeighborIncident += OnIncident;
        ProgressionEvents.ChessGameEnded += OnChess;
        Progression.MidnightReviewed += OnMidnight;
        Progression.KeyToCityGranted += OnKey;
        Ledger.Posted += OnLedger;
        ScrapazonStore.Purchased += OnPurchased;
        WorkOrders.Completed += OnWorkOrder;
        EbaeMarket.ItemSold += OnEbaeSale;
        GameSession.RabiesChanged += OnRabies;
        GameSession.InfectionResisted += OnInfectionResisted;
        GameClock.NightFell += OnNight;
        GameClock.MorningCame += OnMorning;
        SessionService.PlayerJoined += OnPlayerJoined;
        SessionService.PlayerLeft += OnPlayerLeft;
        NetSession.StateChanged += OnNetState;
        BigCleanupNet.PhaseChanged += OnCleanupPhase;
        Weather.Changed += OnWeather;
        RaccoonCrew.Swept += OnCrewSweep;
        Campaign.DisplayChanged += OnCampaignDisplay;
    }

    void Unhook()
    {
        Application.logMessageReceivedThreaded -= OnLog;
        SceneManager.sceneLoaded -= OnScene;
        ProgressionEvents.BagCollected -= OnBag;
        ProgressionEvents.CurbCleared -= OnCurbCleared;
        ProgressionEvents.ItemSorted -= OnSorted;
        ProgressionEvents.TreasureKept -= OnTreasure;
        ProgressionEvents.LoadCrushed -= OnCrushed;
        ProgressionEvents.RankChanged -= OnRank;
        ProgressionEvents.DistrictUnlocked -= OnDistrictUnlocked;
        ProgressionEvents.DistrictEntered -= OnDistrictEntered;
        ProgressionEvents.NeighborIncident -= OnIncident;
        ProgressionEvents.ChessGameEnded -= OnChess;
        Progression.MidnightReviewed -= OnMidnight;
        Progression.KeyToCityGranted -= OnKey;
        Ledger.Posted -= OnLedger;
        ScrapazonStore.Purchased -= OnPurchased;
        WorkOrders.Completed -= OnWorkOrder;
        EbaeMarket.ItemSold -= OnEbaeSale;
        GameSession.RabiesChanged -= OnRabies;
        GameSession.InfectionResisted -= OnInfectionResisted;
        GameClock.NightFell -= OnNight;
        GameClock.MorningCame -= OnMorning;
        SessionService.PlayerJoined -= OnPlayerJoined;
        SessionService.PlayerLeft -= OnPlayerLeft;
        NetSession.StateChanged -= OnNetState;
        BigCleanupNet.PhaseChanged -= OnCleanupPhase;
        Weather.Changed -= OnWeather;
        RaccoonCrew.Swept -= OnCrewSweep;
        Campaign.DisplayChanged -= OnCampaignDisplay;
    }

    void OnScene(Scene scene, LoadSceneMode mode) =>
        Metrics.Begin("scene").Str("name", scene.name, 40).Str("mode", mode.ToString(), 12).End();

    void OnBag(BagCollectedFact f)
    {
        Metrics.Begin("bag").Str("dist", f.districtId, 32).Num("age", f.ageHours, "0.##").Bool("on", f.onTime).Bool("arm", f.byArm)
               .Bool("rac", f.byRaccoon).Num("pay", f.pay, "0.00").Bool("heap", CampaignRules.IsBacklogBagId(f.bagId)).End();
        Metrics.Count(f.byRaccoon ? "bags.crew" : f.onTime ? "bags.ontime" : "bags.late");
        if (f.byArm) Metrics.Count("bags.arm");
    }

    void OnCurbCleared(string stopId, string districtId, Vector3 pos) => Metrics.Count("curbs_cleared");

    void OnSorted(ItemSortedFact f)
    {
        string bin = f.bin.ToString();
        Metrics.Count((f.correct ? "sort.ok." : "sort.bad.") + bin);
        if (f.byMagnus) Metrics.Count("sort.magnus");
    }

    void OnTreasure(ItemInstance item, bool byMagnus)
    {
        var def = item?.Def;
        Metrics.Begin("treasure").Str("item", item?.defId, 40).Str("rarity", def != null ? def.rarity.ToString() : "?", 12)
               .Num("value", item != null ? item.value : 0f, "0").Bool("magnus", byMagnus).End();
        Metrics.Count("treasure." + (def != null ? def.rarity.ToString() : "?"));
    }

    void OnCrushed(int bags, float kg, int treasuresLost) =>
        Metrics.Begin("crush").Int("bags", bags).Num("kg", kg, "0.#").Int("treasures_lost", treasuresLost).End();

    void OnRank(int from, int to) =>
        Metrics.Begin("rank").Int("from", from).Int("to", to).Int("rep", Mathf.RoundToInt(GameSession.Rep)).Int("clean_days", GameSession.CleanDays)
               .Num("cash", GameSession.Cash, "0").Int("players", Businesses.OnlineCount).End();

    void OnDistrictUnlocked(string id) =>
        Metrics.Begin("district_unlock").Str("dist", id, 32).Int("rank", Progression.Rank).Num("cash", GameSession.Cash, "0").Int("players", Businesses.OnlineCount).End();

    void OnDistrictEntered(string id) => Metrics.Begin("district_enter").Str("dist", id, 32).End();

    void OnIncident(NeighborIncidentFact f) =>
        Metrics.Begin("incident").Str("kind", f.kind.ToString(), 20).Str("dist", f.districtId, 32).End();

    void OnChess(bool humanWon, int plies, int tier) => Metrics.Begin("chess").Bool("won", humanWon).Int("plies", plies).Int("tier", tier).End();

    void OnMidnight(bool cleanDay, float repEarned, MidnightReport r)
    {
        var clean = new Dictionary<string, double>();
        var neglected = new Dictionary<string, double>();
        var bonus = new Dictionary<string, double>();
        foreach (var kv in r.clean01) clean[kv.Key] = Mathf.RoundToInt(kv.Value * 100f);
        foreach (var kv in r.neglectedCurbs) neglected[kv.Key] = kv.Value;
        foreach (var kv in r.cleanBonusPaid) bonus[kv.Key] = kv.Value;
        Metrics.Begin("midnight").Int("night_of", r.day).Bool("clean_day", cleanDay).Num("rep_earned", repEarned, "0.#")
               .Int("bags_into_truck", r.bagsIntoTruckToday).Num("fines", r.finesPaid, "0.00")
               .Raw("clean", Metrics.Json.Map(clean, "0")).Raw("neglected", Metrics.Json.Map(neglected, "0")).Raw("bonus", Metrics.Json.Map(bonus, "0.00"))
               .Num("cash", GameSession.Cash, "0").Int("rank", Progression.Rank).Int("rep", Mathf.RoundToInt(GameSession.Rep))
               .Int("players", Businesses.OnlineCount).End();
    }

    void OnKey() => Metrics.Begin("key_to_city").Int("players", Businesses.OnlineCount).End();

    void OnLedger(LedgerEntry e)
    {
        string id = string.IsNullOrEmpty(e.id) ? "other" : e.id;
        switch (e.kind)
        {
            case Ledger.KindCredit: Metrics.Count("inc." + id, e.amount); break;
            case Ledger.KindCharge: Metrics.Count("exp." + id, -e.amount); break;
            case Ledger.KindSpend: Metrics.Count("spend." + id, -e.amount); break;
            case Ledger.KindRep: Metrics.Count("rep." + id, e.amount); break;
        }
    }

    void OnPurchased(ScrapazonItem it, int level)
    {
        if (it == null) return;
        float price = it.prices != null && it.prices.Length > 0 ? it.prices[Mathf.Clamp(level - 1, 0, it.prices.Length - 1)] : 0f;
        Metrics.Begin("buy").Str("id", it.id, 40).Int("lv", level).Num("price", price, "0.##").Num("cash_after", GameSession.Cash, "0")
               .Int("rank", Progression.Rank).Int("players", Businesses.OnlineCount).End();
    }

    void OnWorkOrder(WorkOrder w, float pay, float rep) =>
        Metrics.Begin("workorder").Str("id", w?.Id, 32).Num("pay", pay, "0.00").Num("rep", rep, "0.#").End();

    void OnEbaeSale(EbaeSale s)
    {
        if (s == null) return;
        Metrics.Begin("ebae_sale").Str("rarity", s.rarity.ToString(), 12).Num("price", s.price, "0.00").Num("net", s.net, "0.00").Bool("pawned", s.pawned).End();
    }

    void OnRabies(bool on) => Metrics.Begin("rabies").Bool("on", on).End();
    void OnInfectionResisted() => Metrics.Count("rabies.resisted");

    void OnNight() => Metrics.Begin("night_fell").Raw("dist", DistrictsJson()).Num("cash", GameSession.Cash, "0").End();
    void OnMorning() => Metrics.Begin("morning").Raw("dist", DistrictsJson()).Num("cash", GameSession.Cash, "0").End();

    void OnPlayerJoined(PlayerKey key, ulong clientId, int slot) =>
        Metrics.Begin("player_join").Int("players", SessionService.PlayerCount).Int("biz", Businesses.OnlineCount).End();

    void OnPlayerLeft(PlayerKey key, ulong clientId) =>
        Metrics.Begin("player_leave").Int("players", Mathf.Max(0, SessionService.PlayerCount - 1)).End();

    void OnNetState(NetSessionState state) =>
        Metrics.Begin("net_state").Str("state", state.ToString(), 20).Bool("solo", NetSession.IsSolo).Bool("host", NetSession.IsHost).End();

    void OnCleanupPhase(CleanupPhase from, CleanupState s) =>
        Metrics.Begin("big_cleanup").Str("phase", s.Phase.ToString(), 12).Int("serial", s.serial).Bool("solo", s.solo != 0).End();

    void OnWeather(WeatherKind from, WeatherKind to) => Metrics.Begin("weather").Str("from", from.ToString(), 12).Str("to", to.ToString(), 12).End();

    void OnCrewSweep(string businessId, int delivered, int eaten, float fees)
    {
        Metrics.Count("crew.bags", delivered);
        Metrics.Count("crew.eaten", eaten);
        Metrics.Count("crew.fees", fees);
    }

    // The contract's own numbers (hold progress, backlog) are in every snapshot; this notes the moments it changes shape.
    int lastDistrict = -2;
    void OnCampaignDisplay(CampaignDisplay d)
    {
        if (d.district != lastDistrict)
        {
            lastDistrict = d.district;
            Metrics.Begin("contract_focus").Int("district", d.district).Int("left", d.backlog).Int("start", d.backlogStart).End();
        }
    }
}
