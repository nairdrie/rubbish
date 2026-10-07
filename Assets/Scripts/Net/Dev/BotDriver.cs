using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>What a bot does (parsed from the command line, or built by <see cref="CoopAutoStart"/> in the Editor).</summary>
public sealed class BotOptions
{
    public bool host, join;
    /// <summary>Over Unity Relay instead of LocalDirect: the host allocates and logs its join code; a guest joins by code.</summary>
    public bool relay;
    /// <summary>Guest over Relay: the code, or (with <see cref="relayCodeFile"/>) read from the host's audit log.</summary>
    public string relayCode, relayCodeFile;
    public string address = "127.0.0.1";
    public ushort port = NetConstants.DefaultPort;
    /// <summary>Session length; ≤ 0 runs until stopped (MPPM).</summary>
    public float minutes = 10f;
    public string brain = "walk";
    /// <summary>A <see cref="NetSimPresets"/> name or "delay,jitter,loss"; null/"none" = no simulation.</summary>
    public string netsim;
    /// <summary>Hard-kill this process this many seconds after boot (a crash); &lt; 0 = never.</summary>
    public float killSelfAt = -1f;
    /// <summary>A guest that must be turned away at the door (exit 0 if rejected, 1 if let in).</summary>
    public bool expectReject;
    /// <summary>"run" | "verify" | null (see <see cref="SaveTorture"/>).</summary>
    public string torture;
    /// <summary>Audit self-test: "exception" or "error", raised once at <see cref="injectAt"/> seconds (<c>-botinject exception@30</c>).</summary>
    public string inject;
    public float injectAt = -1f;
    /// <summary>How long a guest keeps retrying the join (the host may still be booting).</summary>
    public float joinPatienceSeconds = 90f;
    /// <summary>Quit the process (with the bot's exit code) when done. False in the Editor.</summary>
    public bool quitWhenDone = true;
    /// <summary>Who this is, for logs ("dev:bot-2", "mppm-host").</summary>
    public string label = "bot";

    public bool Any => host || join || torture != null;

    public override string ToString() =>
        torture != null ? $"savetorture {torture}" :
        $"{(host ? (relay ? "host over Relay" : $"host :{port}") : relay ? $"join over Relay ({relayCode ?? "code from " + relayCodeFile})" : $"join {address}:{port}")} brain {brain}, {(minutes > 0 ? minutes.ToString("0.##", CultureInfo.InvariantCulture) + " min" : "until stopped")}" +
        (string.IsNullOrEmpty(netsim) ? "" : $", netsim {netsim}") + (expectReject ? ", expects rejection" : "") +
        (killSelfAt >= 0 ? $", kill-self at {killSelfAt:0} s" : "");

    /// <summary>
    /// <c>-bothost [-botport P | -botrelay]</c> | <c>-botjoin host:port | relay:CODE | relay:@host-audit.jsonl</c>, <c>-botminutes M</c>, <c>-botbrain walk|idle|laptop|ritual|grief</c>,
    /// <c>-netsim P</c>, <c>-kill-self-at SEC</c>, <c>-botexpectreject</c>, <c>-savetorture [run|verify]</c>. False with an error for
    /// bad values; true with <c>o.Any == false</c> when no bot flag is present.
    /// </summary>
    public static bool TryParse(string[] args, out BotOptions o, out string error)
    {
        o = new BotOptions();
        error = null;
        if (CoreSmoke.TryGetArg(args, "-playerid", out string who)) o.label = who;
        o.host = CoreSmoke.HasFlag(args, "-bothost");
        if (CoreSmoke.TryGetArg(args, "-botport", out string portText))
        {
            if (!ushort.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out o.port) || o.port == 0)
            { error = $"bad -botport '{portText}'"; return false; }
        }
        if (CoreSmoke.TryGetArg(args, "-botjoin", out string target))
        {
            if (o.host) { error = "-bothost and -botjoin can't both be set"; return false; }
            o.join = true;
            if (target.StartsWith("relay:", StringComparison.OrdinalIgnoreCase))
            {
                string code = target.Substring(6).Trim();
                if (code.Length == 0) { error = "-botjoin relay: needs a code or @file"; return false; }
                o.relay = true;
                if (code[0] == '@') o.relayCodeFile = code.Substring(1);
                else o.relayCode = code;
            }
            else
            {
                int colon = target.LastIndexOf(':');
                string addr = colon > 0 ? target.Substring(0, colon) : target;
                if (colon > 0 && (!ushort.TryParse(target.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out o.port) || o.port == 0))
                { error = $"bad -botjoin port in '{target}'"; return false; }
                if (string.IsNullOrWhiteSpace(addr)) { error = $"bad -botjoin '{target}' (want host:port)"; return false; }
                o.address = addr;
            }
        }
        else if (CoreSmoke.HasFlag(args, "-botjoin")) { error = "-botjoin needs host:port"; return false; }
        if (CoreSmoke.HasFlag(args, "-botrelay"))
        {
            if (!o.host) { error = "-botrelay is for the host (guests use -botjoin relay:CODE)"; return false; }
            o.relay = true;
        }
        if (CoreSmoke.TryGetArg(args, "-botminutes", out string min))
        {
            if (!float.TryParse(min, NumberStyles.Float, CultureInfo.InvariantCulture, out o.minutes)) { error = $"bad -botminutes '{min}'"; return false; }
        }
        if (CoreSmoke.TryGetArg(args, "-botbrain", out string brain)) o.brain = brain.Trim().ToLowerInvariant();
        if (!BotBrains.Exists(o.brain)) { error = $"unknown -botbrain '{o.brain}' (have {string.Join(", ", BotBrains.Names)})"; return false; }
        if (CoreSmoke.TryGetArg(args, NetSimPresets.Flag, out string sim))
        {
            if (!NetSimPresets.TryParse(sim, out _, out string simError)) { error = simError; return false; }
            o.netsim = sim;
        }
        if (CoreSmoke.TryGetArg(args, "-kill-self-at", out string kill))
        {
            if (!float.TryParse(kill, NumberStyles.Float, CultureInfo.InvariantCulture, out o.killSelfAt) || o.killSelfAt < 0)
            { error = $"bad -kill-self-at '{kill}'"; return false; }
        }
        o.expectReject = CoreSmoke.HasFlag(args, "-botexpectreject");
        if (CoreSmoke.TryGetArg(args, "-botinject", out string inj))
        {
            int at = inj.IndexOf('@');
            string what = (at > 0 ? inj.Substring(0, at) : inj).ToLowerInvariant();
            float when = 30f;
            if ((what != "exception" && what != "error") ||
                (at > 0 && !float.TryParse(inj.Substring(at + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out when)))
            { error = $"bad -botinject '{inj}' (exception@SEC or error@SEC)"; return false; }
            o.inject = what;
            o.injectAt = Mathf.Max(0f, when);
        }
        if (CoreSmoke.HasFlag(args, SaveTorture.Flag) || CoreSmoke.TryGetArg(args, SaveTorture.Flag, out _))
        {
            CoreSmoke.TryGetArg(args, SaveTorture.Flag, out string mode);
            mode = string.IsNullOrEmpty(mode) ? "run" : mode.ToLowerInvariant();
            if (mode != "run" && mode != "verify") { error = $"bad {SaveTorture.Flag} mode '{mode}' (run|verify)"; return false; }
            o.torture = mode;
        }
        return true;
    }
}

/// <summary>
/// Headless bots and the MPPM autostart (Docs/Multiplayer.md §8.4, §8.7.4 package B5, §8.7.6 BOTS).
/// <para>
/// A development build started with <c>-bothost</c> or <c>-botjoin host:port</c> (see <see cref="BotOptions.TryParse"/>) boots
/// as usual, then hosts or joins over LocalDirect, plays a <see cref="BotBrains">brain</see> on its own player only (it writes
/// <c>StarterAssetsInputs</c>, never another player's), and leaves and quits after <c>-botminutes</c>. Bots always run with
/// <c>-dataroot &lt;run&gt;/&lt;bot&gt; -playerid dev:bot-N -nosteam</c> (Tools/bots/run_bots.sh) and record to NetAudit.
/// </para>
/// Sessions go through <see cref="NetSession"/> once B1's LocalDirect adapter is registered (with the port pinned to
/// <c>-botport</c>); until then a raw path does what the old menu did (UTP 127.0.0.1, StartHost/StartClient) and spawns
/// <c>NetSession.prefab</c> itself if nobody else did, so roster and hash probes run either way.
/// Exit codes: 0 done (or rejected as expected), 1 failed (couldn't host or join, crashed boot), 3 rejected at the door.
/// </summary>
public sealed class BotDriver : MonoBehaviour
{
    public const int ExitOk = 0, ExitFailed = 1, ExitRejected = 3;
    const string SessionAssetKey = "net/session";

    public static BotDriver Instance { get; private set; }
    public static BotOptions Options { get; private set; }

    /// <summary>"booting" | "hosting" | "joining" | "playing" | "offline" | "leaving" | "done" | "failed".</summary>
    public string Phase { get; private set; } = "booting";
    public bool ViaNetSession { get; private set; }
    public int ExitCode { get; private set; }
    /// <summary>Metres the owned player moved while a brain ran (all sessions).</summary>
    public float Walked => ctx?.walked ?? 0f;

    static NetworkManager manager;
    BotContext ctx;
    IBotBrain brain;
    float bootTime;
    bool connectedOnce, sessionLost, rejected;
    string rejectReason;

    // ------------------------------------------------------------------ entry points

    /// <summary>Start a bot in this process (one at a time). The Editor's MPPM autostart calls this too.</summary>
    public static BotDriver Launch(BotOptions o)
    {
        if (o == null || !o.Any) return null;
        if (Instance != null)
        {
            Debug.LogWarning($"[Bot] Already running ({Options}); ignoring a second launch.");
            return Instance;
        }
        Options = o;
        var go = new GameObject("[BotDriver]");
        DontDestroyOnLoad(go);
        return go.AddComponent<BotDriver>();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void BootFromCommandLine()
    {
        NetworkManager.OnInstantiated -= Capture;
        NetworkManager.OnInstantiated += Capture;
        if (Application.isEditor) return; // the Editor uses CoopAutoStart (MPPM)
        string[] args = Environment.GetCommandLineArgs();
        if (!BotOptions.TryParse(args, out var o, out string error))
        {
            Debug.LogError("[Bot] " + error);
            if (Application.isBatchMode) Application.Quit(ExitFailed);
            return;
        }
        if (o.Any) Launch(o);
        else if (o.killSelfAt >= 0) KillSelfTimer.Arm(o.killSelfAt);
    }

    static void Capture(NetworkManager nm) => manager = nm;

    static NetworkManager Nm
    {
        get
        {
            var nm = NetBoot.Manager; // B1: the configured game NetworkManager
            if (nm != null) manager = nm;
            return manager;
        }
    }

    // ------------------------------------------------------------------ lifetime

    void Awake()
    {
        Instance = this;
        bootTime = Time.realtimeSinceStartup;
        if (Application.isBatchMode)
        {
            Application.targetFrameRate = 30; // headless players spin at 100% CPU otherwise; 4 bots share one Mac
            QualitySettings.vSyncCount = 0;
        }
        Application.runInBackground = true;
        if (Options.killSelfAt >= 0) KillSelfTimer.Arm(Options.killSelfAt);
        Debug.Log($"[Bot] {Options.label}: {Options}");
        NetAudit.Event("bot", new Dictionary<string, object>
        {
            { "phase", "boot" }, { "role", Options.torture != null ? "torture" : Options.host ? "host" : "guest" },
            { "brain", Options.brain }, { "port", (int)Options.port }, { "netsim", Options.netsim }, { "minutes", Options.minutes },
        });
    }

    void Start() => StartCoroutine(Options.torture != null ? SaveTorture.Run(Options, this) : Run());

    void Update()
    {
        if (Options.inject == null || Options.injectAt < 0f || Time.realtimeSinceStartup - bootTime < Options.injectAt) return;
        string what = Options.inject;
        Options.injectAt = -1f;
        if (what == "error") Debug.LogError("[Bot] Injected test error (-botinject): the audit must fail this run.");
        else throw new InvalidOperationException("[Bot] Injected test exception (-botinject): the audit must fail this run.");
    }

    void OnDestroy()
    {
        Unhook();
        if (Instance == this) Instance = null;
    }

    bool DeadlineReached => Options.minutes > 0 && Time.realtimeSinceStartup - bootTime >= Options.minutes * 60f;

    IEnumerator Run()
    {
        // 1. Wait for Bootstrap's NetworkManager and the menu (GameStartup loads it in Awake).
        float waitUntil = Time.realtimeSinceStartup + 30f;
        while (Nm == null && Time.realtimeSinceStartup < waitUntil) yield return null;
        if (Nm == null) { Fail("no NetworkManager after 30 s (is Bootstrap the first scene?)"); yield break; }
        yield return new WaitForSecondsRealtime(1f);
        Hook();

        // 2. Bad network, before the transport starts.
        if (!string.IsNullOrEmpty(Options.netsim))
            NetSimPresets.Apply(Nm, Options.netsim, unchecked((uint)WorldHash.Fnv(WorldHash.Seed, Options.label)));

        // 3. Host or join.
        if (Options.host) yield return Host();
        else yield return Join();
        if (Phase != "hosting" && Phase != "joining") yield break; // failed, rejected, or finished already

        // 4. Play until the deadline.
        Phase = connectedOnce ? "playing" : "offline";
        ctx = new BotContext(this, Options);
        brain = BotBrains.Create(Options.brain);
        brain.Begin(ctx);
        while (!DeadlineReached)
        {
            if (sessionLost && Phase == "playing")
            {
                Phase = "offline";
                brain.End(ctx);
                brain = BotBrains.Create("idle"); // the host is gone: wait out the clock in our own (reloaded) game
                brain.Begin(ctx);
            }
            ctx.Tick(Nm, Time.deltaTime);
            brain.Tick(ctx, Time.deltaTime);
            yield return null;
        }
        brain.End(ctx);
        yield return Finish(ExitOk);
    }

    // ------------------------------------------------------------------ host

    IEnumerator Host()
    {
        Phase = "hosting";
        var nm = Nm;
        bool ok = false;
        NetMode mode = Options.relay ? NetMode.UnityRelay : NetMode.LocalDirect;
        if (NetSession.TryGetAdapter(mode, out _))
        {
            ViaNetSession = true;
            var task = NetSession.HostAsync(new HostOptions
            {
                mode = mode, port = Options.port, listenAddress = "127.0.0.1", maxPlayers = NetConstants.TownCap,
            });
            yield return Await(task);
            ok = task.Status == TaskStatus.RanToCompletion && task.Result;
            if (!ok) { Fail($"NetSession.HostAsync({mode}) failed: {NetSession.LastError}"); yield break; }
        }
        else if (Options.relay) { Fail("this build has no Unity Relay adapter (B1)"); yield break; }
        else
        {
            // Raw path (a build without B1's adapters): the old menu's host flow on LocalDirect.
            if (!(nm.NetworkConfig.NetworkTransport is UnityTransport ut)) { Fail("the NetworkManager has no UnityTransport"); yield break; }
            ut.SetConnectionData(true, "127.0.0.1", Options.port, "127.0.0.1");
            SceneManager.LoadScene(new HostOptions().firstScene);
            yield return null;
            yield return null;
            ok = nm.StartHost();
        }
        if (!ok) { Fail("couldn't host"); yield break; }

        float until = Time.realtimeSinceStartup + 20f;
        while (!(nm.IsListening && nm.IsServer) && Time.realtimeSinceStartup < until) yield return null;
        if (!nm.IsServer) { Fail("the host never started listening"); yield break; }
        connectedOnce = true;
        string code = Options.relay ? NetSession.JoinCode : null;
        Debug.Log(Options.relay
            ? $"[Bot] Hosting over Relay, join code {code}."
            : $"[Bot] Hosting on 127.0.0.1:{ListeningPort(nm)} ({(ViaNetSession ? "NetSession" : "raw")}).");
        NetAudit.Event("bot", new Dictionary<string, object>
        {
            { "phase", "hosting" }, { "mode", mode }, { "port", ListeningPort(nm) }, { "joinCode", code }, { "via", ViaNetSession ? "NetSession" : "raw" },
        });

        yield return new WaitForSecondsRealtime(2f);
        EnsureSessionObject(nm);
    }

    static int ListeningPort(NetworkManager nm) =>
        nm.NetworkConfig.NetworkTransport is UnityTransport ut ? ut.ConnectionData.Port : 0;

    /// <summary>Spawn NetSession.prefab (roster, hash probes, …) if B1's host flow didn't.</summary>
    static void EnsureSessionObject(NetworkManager nm)
    {
        if (nm == null || !nm.IsServer) return;
        foreach (var no in nm.SpawnManager.SpawnedObjectsList)
            if (no != null && no.GetComponent<NetAuditNet>() != null) return;
        var prefab = RubbishAssets.Prefab(SessionAssetKey);
        if (prefab == null || prefab.GetComponent<NetworkObject>() == null)
        {
            Debug.Log("[Bot] NetSession.prefab isn't built (Setup_Coop hasn't run): no roster or hash probes this session.");
            NetAudit.Event("probe", "no NetSession prefab: hash probes off");
            return;
        }
        var inst = Instantiate(prefab);
        DontDestroyOnLoad(inst);
        inst.GetComponent<NetworkObject>().Spawn(destroyWithScene: false);
        Debug.Log("[Bot] Spawned NetSession.prefab (nobody else had).");
    }

    // ------------------------------------------------------------------ join

    IEnumerator Join()
    {
        Phase = "joining";
        var nm = Nm;
        float giveUp = Time.realtimeSinceStartup + Mathf.Max(10f, Options.joinPatienceSeconds);
        int attempt = 0;
        NetMode mode = Options.relay ? NetMode.UnityRelay : NetMode.LocalDirect;
        ViaNetSession = NetSession.TryGetAdapter(mode, out _);
        if (Options.relay && !ViaNetSession) { Fail("this build has no Unity Relay adapter (B1)"); yield break; }
        string code = Options.relayCode;
        while (Options.relay && string.IsNullOrEmpty(code) && Time.realtimeSinceStartup < giveUp)
        {
            code = ReadJoinCode(Options.relayCodeFile);
            if (string.IsNullOrEmpty(code)) yield return new WaitForSecondsRealtime(1f);
        }
        if (Options.relay && string.IsNullOrEmpty(code)) { Fail($"no Relay join code appeared in {Options.relayCodeFile}"); yield break; }
        while (Time.realtimeSinceStartup < giveUp)
        {
            attempt++;
            rejected = false;
            rejectReason = null;
            if (ViaNetSession)
            {
                var target = Options.relay ? JoinTarget.Relay(code)
                    : new JoinTarget { mode = NetMode.LocalDirect, address = Options.address, port = Options.port };
                var task = NetSession.JoinAsync(target);
                yield return Await(task);
                if (task.Status == TaskStatus.RanToCompletion && task.Result) { yield return Connected(); yield break; }
                // NetSession.LastError is the readable sentence ("Update your game: …"); rejectReason is NGO's raw wire reason.
                if (rejected || IsRejection(NetSession.LastError)) { yield return Rejected(NetSession.LastError ?? rejectReason); yield break; }
                Debug.Log($"[Bot] Join attempt {attempt} failed: {NetSession.LastError}");
            }
            else
            {
                if (!(nm.NetworkConfig.NetworkTransport is UnityTransport ut)) { Fail("the NetworkManager has no UnityTransport"); yield break; }
                SaveGuard.MarkForeign("join");
                ut.SetConnectionData(true, Options.address, Options.port);
                bool started = nm.StartClient();
                float until = Time.realtimeSinceStartup + 15f;
                while (started && !nm.IsConnectedClient && !rejected && nm.IsListening && Time.realtimeSinceStartup < until) yield return null;
                if (started && nm.IsConnectedClient) { yield return Connected(); yield break; }
                if (rejected) { yield return Rejected(rejectReason); yield break; }
                if (nm.IsListening) nm.Shutdown();
                while (nm.ShutdownInProgress) yield return null;
                SaveGuard.ReloadOwn("join attempt failed");
                Debug.Log($"[Bot] Join attempt {attempt} to {Options.address}:{Options.port} didn't connect; retrying.");
            }
            yield return new WaitForSecondsRealtime(3f);
        }
        Fail($"couldn't join {Options.address}:{Options.port} in {Options.joinPatienceSeconds:0} s ({attempt} attempts)");
    }

    static bool IsRejection(string error) =>
        !string.IsNullOrEmpty(error) && (error.IndexOf("update your game", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         error.IndexOf("full", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         error.IndexOf("reject", StringComparison.OrdinalIgnoreCase) >= 0);

    IEnumerator Connected()
    {
        connectedOnce = true;
        var nm = Nm;
        Debug.Log($"[Bot] Joined {(Options.relay ? "over Relay" : $"{Options.address}:{Options.port}")} as client {nm.LocalClientId} ({(ViaNetSession ? "NetSession" : "raw")}).");
        NetAudit.Event("bot", new Dictionary<string, object> { { "phase", "joined" }, { "client", nm.LocalClientId }, { "via", ViaNetSession ? "NetSession" : "raw" } });
        if (Options.expectReject)
        {
            Debug.LogError($"[Bot] Expected to be turned away at the door, but {Options.address}:{Options.port} let this bot in.");
            yield return Finish(ExitFailed);
        }
    }

    IEnumerator Rejected(string reason)
    {
        reason = string.IsNullOrEmpty(reason) ? "(no reason given)" : reason;
        Debug.Log($"[Bot] Turned away at the door: {reason}");
        NetAudit.Event("reject", new Dictionary<string, object> { { "side", "guest" }, { "reason", reason }, { "expected", Options.expectReject } });
        if (!Options.expectReject) Debug.LogWarning("[Bot] This bot wasn't expected to be rejected (pass -botexpectreject for rejection tests).");
        yield return Finish(Options.expectReject ? ExitOk : ExitRejected);
    }

    // ------------------------------------------------------------------ NGO callbacks

    NetworkManager hooked;

    void Hook()
    {
        Unhook();
        hooked = Nm;
        if (hooked == null) return;
        hooked.OnClientDisconnectCallback += OnDisconnect;
    }

    void Unhook()
    {
        if (hooked != null) hooked.OnClientDisconnectCallback -= OnDisconnect;
        hooked = null;
    }

    void OnDisconnect(ulong clientId)
    {
        var nm = hooked;
        if (nm == null) return;
        if (nm.IsServer)
        {
            if (clientId != NetworkManager.ServerClientId)
                NetAudit.Event("bot", new Dictionary<string, object> { { "phase", "guest-left" }, { "client", clientId } });
            return;
        }
        string reason = nm.DisconnectReason;
        if (!connectedOnce || Phase == "joining")
        {
            if (!string.IsNullOrEmpty(reason)) { rejected = true; rejectReason = reason; }
            return;
        }
        if (Phase == "leaving" || Phase == "done") return;
        sessionLost = true;
        string why = string.IsNullOrEmpty(reason) ? "the host went away" : reason;
        Debug.Log($"[Bot] Lost the session: {why}. Staying in our own game until the clock runs out.");
        NetAudit.Event("disconnect", new Dictionary<string, object> { { "reason", why } });
        if (!ViaNetSession) SaveGuard.ReloadOwn("host lost");
    }

    // ------------------------------------------------------------------ finish

    IEnumerator Finish(int code)
    {
        if (Phase == "done") yield break;
        var nm = Nm;
        Phase = "leaving";
        if (nm != null && nm.IsServer && nm.IsListening)
        {
            // Let the guests leave first (their clocks started later), so nobody logs a lost host at the end of a clean run.
            float until = Time.realtimeSinceStartup + 60f;
            while (nm.IsListening && nm.ConnectedClientsIds.Count > 1 && Time.realtimeSinceStartup < until) yield return null;
        }
        if (nm != null && nm.IsListening)
        {
            if (ViaNetSession) NetSession.Leave("bot finished");
            else
            {
                nm.Shutdown();
                while (nm.ShutdownInProgress) yield return null;
                SaveGuard.ReloadOwn("bot finished");
            }
        }
        ExitCode = code;
        Phase = code == ExitFailed ? "failed" : "done";
        NetAudit.Event("bot", new Dictionary<string, object>
        {
            { "phase", "end" }, { "exit", code }, { "walked", Math.Round(Walked, 1) }, { "connected", connectedOnce }, { "lost", sessionLost },
            { "compares", NetAuditNet.Compares }, { "mismatches", NetAuditNet.Mismatches }, { "guestWritesDropped", NetAudit.GuestWritesDropped },
        });
        Debug.Log($"[Bot] Done (exit {code}): walked {Walked:0.0} m, {NetAuditNet.Compares} hash compares, {NetAuditNet.Mismatches} mismatches.");
        yield return new WaitForSecondsRealtime(0.5f);
        if (Options.quitWhenDone) Application.Quit(code);
    }

    void Fail(string why)
    {
        // Bots fail their audit on this error; in the Editor (MPPM) a warning is enough.
        if (Options.quitWhenDone) Debug.LogError($"[Bot] {Options.label} failed: {why}");
        else Debug.LogWarning($"[Bot] {Options.label} failed: {why}");
        StartCoroutine(Finish(ExitFailed));
    }

    /// <summary>For SaveTorture: end the process with a code.</summary>
    internal void End(int code)
    {
        ExitCode = code;
        Phase = code == ExitOk ? "done" : "failed";
        if (Options.quitWhenDone) Application.Quit(code);
    }

    static IEnumerator Await(Task t)
    {
        while (!t.IsCompleted) yield return null;
        if (t.IsFaulted) Debug.LogException(t.Exception?.GetBaseException() ?? new Exception("task faulted"));
    }

    /// <summary>The host bot's Relay join code from its NetAudit JSONL (a "joincode" line, or a "bot" hosting line).</summary>
    static string ReadJoinCode(string file)
    {
        if (string.IsNullOrEmpty(file)) return null;
        try
        {
            using (var fs = new System.IO.FileStream(file, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
            using (var r = new System.IO.StreamReader(fs))
            {
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(line,
                        "\"kind\":\"joincode\".*?\"msg\":\"([A-Za-z0-9]+)\"|\"joinCode\":\"([A-Za-z0-9]+)\"");
                    if (m.Success) return m.Groups[1].Success && m.Groups[1].Length > 0 ? m.Groups[1].Value : m.Groups[2].Value;
                }
            }
        }
        catch (Exception) { /* not written yet */ }
        return null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Instance = null;
        Options = null;
        manager = null;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>A hard kill at a set time after boot (a crash: no quit handlers, no flushes).</summary>
    sealed class KillSelfTimer : MonoBehaviour
    {
        float at;

        public static void Arm(float seconds)
        {
            var go = new GameObject("[KillSelfAt]");
            DontDestroyOnLoad(go);
            go.AddComponent<KillSelfTimer>().at = Time.realtimeSinceStartup + Mathf.Max(0f, seconds);
        }

        void Update()
        {
            if (Time.realtimeSinceStartup < at) return;
            enabled = false;
            Debug.Log($"[Bot] -kill-self-at reached: killing this process now (a simulated crash).");
            NetAudit.Event("bot", new Dictionary<string, object> { { "phase", "kill-self" } });
            NetAudit.CloseLog();
            System.Diagnostics.Process.GetCurrentProcess().Kill();
        }
    }
}
