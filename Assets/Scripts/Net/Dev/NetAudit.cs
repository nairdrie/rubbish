using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// The test and bot audit trail (Docs/Multiplayer.md §8.7.4, package B5; C1 contract kept as is).
/// <list type="bullet">
/// <item>Counts events per kind and keeps the last 64 in memory for <see cref="SnapshotJson"/> (MPPM probes, evals).</item>
/// <item>With <c>-auditlog &lt;file&gt;</c> on the command line (bots; never the Editor), every event is also appended to that
/// file as one JSON line: <c>{"t":12.345,"utc":"…","kind":"hash","who":"dev:bot-2", …fields | "msg":"…"}</c>.
/// <c>Tools/bots/audit.py</c> fails a run on any line of kind <c>error</c> or <c>exception</c> and on hash mismatches.</item>
/// <item>Every Unity log error, assert and exception becomes an <c>error</c> / <c>exception</c> event (any thread).</item>
/// <item>Every <see cref="Ledger.Posted"/> becomes a <c>ledger</c> event (D1: NetAudit only observes the ledger).</item>
/// </list>
/// Kinds in use: error | exception | roster | hash | probe | ledger | ritual | save | reject | kick | guestwrite | bot | netsim.
/// Payload: a string (written as <c>msg</c>), or an <c>IDictionary</c> / <c>IEnumerable&lt;KeyValuePair&lt;string, object&gt;&gt;</c>
/// whose entries become fields (strings, numbers, bools, null). Never pass secrets (tickets, tokens): audit logs and snapshots end
/// up in logs players paste on Discord.
/// </summary>
public static class NetAudit
{
    public const string LogFlag = "-auditlog";
    const int Keep = 64;
    const int MaxMsg = 600;

    struct Row { public double t; public string kind, payload; }

    static readonly object gate = new object();
    static readonly Dictionary<string, int> counts = new Dictionary<string, int>();
    static readonly Queue<Row> recent = new Queue<Row>();
    static readonly Stopwatch clock = Stopwatch.StartNew();
    static StreamWriter writer;
    static string logPath, who;
    static bool logFailed;
    [ThreadStatic] static bool inEvent;

    /// <summary>GameSession mutations refused on a guest (C2's <c>BlockGuestWrite</c>).</summary>
    public static int GuestWritesDropped { get; private set; }

    /// <summary>
    /// Fired for every event with (kind, text) where text is the message or the fields as compact JSON. Log-derived events
    /// (<c>error</c>/<c>exception</c>) can fire on a background thread.
    /// </summary>
    public static event Action<string, string> Recorded;

    /// <summary>The JSONL file this process appends to, or null (no <c>-auditlog</c>).</summary>
    public static string LogPath => logPath;

    /// <summary>Seconds since this process's audit clock started (the <c>t</c> field). Safe on any thread.</summary>
    public static double Now => clock.Elapsed.TotalSeconds;

    /// <summary>Who this process is (written as <c>who</c> on every line): the <c>-playerid</c> value, else "local".</summary>
    public static string Who
    {
        get => who ?? "local";
        set => who = string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>Record an event. <paramref name="kind"/>: error | exception | roster | hash | ledger | ritual | save | reject | …</summary>
    public static void Event(string kind, object payload)
    {
        if (inEvent) return; // a Recorded handler or the writer logged an error while recording: don't loop
        inEvent = true;
        try
        {
            kind = string.IsNullOrEmpty(kind) ? "misc" : kind;
            double t = Now;
            string text;
            string line = BuildLine(t, kind, payload, out text);
            lock (gate)
            {
                counts.TryGetValue(kind, out int n);
                counts[kind] = n + 1;
                recent.Enqueue(new Row { t = t, kind = kind, payload = text });
                while (recent.Count > Keep) recent.Dequeue();
                WriteLine(line);
            }
            try { Recorded?.Invoke(kind, text); }
            catch (Exception e) { Debug.LogException(e); }
        }
        finally { inEvent = false; }
    }

    /// <summary>Count a refused guest mutation (<paramref name="op"/> = the GameSession member).</summary>
    public static void GuestWriteDropped(string op)
    {
        lock (gate) GuestWritesDropped++;
        Event("guestwrite", op);
    }

    public static int Count(string kind)
    {
        if (kind == null) return 0;
        lock (gate) return counts.TryGetValue(kind, out int n) ? n : 0;
    }

    /// <summary><c>{"who":"…","t":…,"guestWritesDropped":0,"log":"…|null","counts":{…},"recent":[{t,kind,payload}…]}</c></summary>
    public static string SnapshotJson()
    {
        var sb = new StringBuilder(512);
        lock (gate)
        {
            sb.Append("{\"who\":");
            Str(sb, Who);
            sb.Append(",\"t\":").Append(Num(Now));
            sb.Append(",\"guestWritesDropped\":").Append(GuestWritesDropped.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"log\":");
            if (logPath == null) sb.Append("null");
            else Str(sb, logPath);
            sb.Append(",\"counts\":{");
            bool first = true;
            foreach (var kv in counts)
            {
                if (!first) sb.Append(',');
                first = false;
                Str(sb, kv.Key).Append(':').Append(kv.Value.ToString(CultureInfo.InvariantCulture));
            }
            sb.Append("},\"recent\":[");
            first = true;
            foreach (var r in recent)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"t\":").Append(Num(r.t)).Append(",\"kind\":");
                Str(sb, r.kind).Append(",\"payload\":");
                if (r.payload == null) sb.Append("null");
                else Str(sb, r.payload);
                sb.Append('}');
            }
        }
        return sb.Append("]}").ToString();
    }

    /// <summary>
    /// Start appending to <paramref name="path"/> (bots pass <c>-auditlog</c>; done automatically at boot). Refuses a path inside
    /// the real save folder. Returns false if the file can't be opened (the reason is logged once as a warning).
    /// </summary>
    public static bool OpenLog(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception e) { Debug.LogWarning($"[NetAudit] Bad {LogFlag} path '{path}': {e.Message}"); return false; }
        string real = Application.persistentDataPath; // coop:allow(save) read-only: refuse to put the audit log inside the real save folder
        if (!string.IsNullOrEmpty(real) && full.StartsWith(Path.GetFullPath(real).TrimEnd('/', '\\') + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            Debug.LogWarning($"[NetAudit] Refusing {LogFlag} inside the real save folder ({full}); audit stays in memory.");
            return false;
        }
        lock (gate)
        {
            CloseLogLocked();
            try
            {
                string dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                // Diagnostics only (never game data), opened only with -auditlog: not a save, so no SaveStore.
                var fs = new FileStream(full, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                logPath = full;
                logFailed = false;
            }
            catch (Exception e)
            {
                writer = null;
                logPath = null;
                Debug.LogWarning($"[NetAudit] Couldn't open the audit log {full}: {e.Message}");
                return false;
            }
        }
        return true;
    }

    public static void CloseLog()
    {
        lock (gate) CloseLogLocked();
    }

    static void CloseLogLocked()
    {
        if (writer == null) return;
        try { writer.Flush(); writer.Dispose(); }
        catch (Exception) { /* closing a diagnostics file */ }
        writer = null;
        logPath = null;
    }

    static void WriteLine(string line)
    {
        if (writer == null || logFailed) return;
        try { writer.WriteLine(line); }
        catch (Exception e)
        {
            logFailed = true; // stop trying; warn once (inEvent stops the warning from re-entering)
            Debug.LogWarning($"[NetAudit] Writing {logPath} failed; audit continues in memory only: {e.Message}");
        }
    }

    // ------------------------------------------------------------------ JSON

    static string BuildLine(double t, string kind, object payload, out string text)
    {
        var sb = new StringBuilder(160);
        sb.Append("{\"t\":").Append(Num(t));
        sb.Append(",\"utc\":\"").Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)).Append('"');
        sb.Append(",\"kind\":");
        Str(sb, kind);
        sb.Append(",\"who\":");
        Str(sb, Who);
        int fieldsStart = sb.Length;
        if (payload == null)
        {
            text = null;
        }
        else if (payload is string s)
        {
            text = Clip(s);
            sb.Append(",\"msg\":");
            Str(sb, text);
        }
        else if (TryFields(payload, sb))
        {
            text = "{" + sb.ToString(fieldsStart + 1, sb.Length - fieldsStart - 1) + "}";
        }
        else
        {
            text = Clip(Convert.ToString(payload, CultureInfo.InvariantCulture));
            sb.Append(",\"msg\":");
            Str(sb, text);
        }
        return sb.Append('}').ToString();
    }

    static bool TryFields(object payload, StringBuilder sb)
    {
        if (payload is IDictionary dict)
        {
            foreach (DictionaryEntry e in dict) Field(sb, Convert.ToString(e.Key, CultureInfo.InvariantCulture), e.Value);
            return true;
        }
        if (payload is IEnumerable<KeyValuePair<string, object>> pairs)
        {
            foreach (var kv in pairs) Field(sb, kv.Key, kv.Value);
            return true;
        }
        return false;
    }

    static void Field(StringBuilder sb, string key, object value)
    {
        if (string.IsNullOrEmpty(key) || key == "t" || key == "utc" || key == "kind" || key == "who") key = "_" + key;
        sb.Append(',');
        Str(sb, key).Append(':');
        Value(sb, value);
    }

    static void Value(StringBuilder sb, object v)
    {
        switch (v)
        {
            case null: sb.Append("null"); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case string s: Str(sb, Clip(s)); break;
            case float f: sb.Append(float.IsNaN(f) || float.IsInfinity(f) ? "null" : f.ToString("R", CultureInfo.InvariantCulture)); break;
            case double d: sb.Append(double.IsNaN(d) || double.IsInfinity(d) ? "null" : d.ToString("R", CultureInfo.InvariantCulture)); break;
            case ulong u: Str(sb, u.ToString("x16", CultureInfo.InvariantCulture)); break; // hashes: hex strings, no float rounding in JSON readers
            case int _:
            case long _:
            case uint _:
            case short _:
            case ushort _:
            case byte _:
            case sbyte _:
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); break;
            case Enum e: Str(sb, e.ToString()); break;
            case Vector3 p: sb.Append('[').Append(Num(p.x)).Append(',').Append(Num(p.y)).Append(',').Append(Num(p.z)).Append(']'); break;
            case IEnumerable seq when !(v is IDictionary):
                sb.Append('[');
                bool first = true;
                foreach (var x in seq)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Value(sb, x);
                }
                sb.Append(']');
                break;
            default: Str(sb, Clip(Convert.ToString(v, CultureInfo.InvariantCulture))); break;
        }
    }

    static string Num(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

    static string Clip(string s) => s == null || s.Length <= MaxMsg ? s : s.Substring(0, MaxMsg) + "…";

    static StringBuilder Str(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s ?? "")
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"');
    }

    // ------------------------------------------------------------------ sources: Unity log, Ledger, process lifetime

    static void OnLog(string condition, string stackTrace, LogType type)
    {
        if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
        string msg = condition ?? "";
        if (type == LogType.Exception && !string.IsNullOrEmpty(stackTrace))
        {
            int nl = stackTrace.IndexOf('\n');
            msg += " @ " + (nl > 0 ? stackTrace.Substring(0, nl) : stackTrace).Trim();
        }
        Event(type == LogType.Exception ? "exception" : "error", msg);
    }

    static void OnLedger(LedgerEntry e)
    {
        Event("ledger", new Dictionary<string, object>
        {
            { "op", e.kind }, { "id", e.id }, { "amount", e.amount }, { "cents", e.cents }, { "cash", GameSession.Cash },
            { "cashCents", GameSession.CashCents }, { "business", e.businessId }, { "reason", e.reason },
        });
    }

    static void OnQuitting()
    {
        Event("end", new Dictionary<string, object>
        {
            { "guestWritesDropped", GuestWritesDropped }, { "errors", Count("error") }, { "exceptions", Count("exception") },
            { "hash", Count("hash") },
        });
        CloseLog();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        lock (gate)
        {
            counts.Clear();
            recent.Clear();
            GuestWritesDropped = 0;
        }
        Recorded = null;
        Application.logMessageReceivedThreaded -= OnLog;
        Application.logMessageReceivedThreaded += OnLog;
        Application.quitting -= OnQuitting;
        Application.quitting += OnQuitting;
    }

    // Ledger clears its own event at SubsystemRegistration, so subscribe after that. The log opens here too, before any scene.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Boot()
    {
        Ledger.Posted -= OnLedger;
        Ledger.Posted += OnLedger;
        if (Application.isEditor) return; // the Editor's command line is the Editor's; MPPM reads SnapshotJson instead
        string[] args = Environment.GetCommandLineArgs();
        if (CoreSmoke.TryGetArg(args, "-playerid", out string id)) Who = id;
        if (writer == null && CoreSmoke.TryGetArg(args, LogFlag, out string path) && OpenLog(path))
        {
            Event("start", new Dictionary<string, object>
            {
                { "pid", Process.GetCurrentProcess().Id }, { "platform", Application.platform.ToString() },
                { "build", Debug.isDebugBuild ? "development" : "release" }, { "batchmode", Application.isBatchMode },
                { "unity", Application.unityVersion }, { "args", string.Join(" ", RedactArgs(args)) },
            });
        }
    }

    /// <summary>The command line with anything after a ticket/token-like flag blanked (players paste these logs).</summary>
    static IEnumerable<string> RedactArgs(string[] args)
    {
        bool hide = false;
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i] ?? "";
            if (hide) { hide = false; yield return "<redacted>"; continue; }
            string lower = a.ToLowerInvariant();
            hide = lower.Contains("ticket") || lower.Contains("token") || lower.Contains("secret") || lower.Contains("password");
            yield return a;
        }
    }
}
