using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Playtest metrics (Docs/Metrics.md): an append-only log of what players actually do, written next to the saves under
/// <c>&lt;data root&gt;/metrics/</c> as one JSON-lines file per play session. It works in every build (the Windows playtest zip
/// included: nothing is gated on the Editor or on development builds) and it never leaves the machine: friends send the
/// folder back by hand. It holds no names, no Steam ids and no chat, only a hashed player tag, so the numbers can be shared.
/// <para>Writing goes through this class only (it lives in Save/ because the co-op lint keeps File I/O there). One line per
/// event: <c>{"e":"bag","t":unix ms,"rt":real seconds,"gh":game hours,"d":game day, ...fields}</c>. Chatty things (sorted items,
/// money by source) are counted with <see cref="Count"/> and written inside the next <c>snap</c> line instead of one line each.</para>
/// <para>Off with <c>-nometrics</c> on the command line. Batch mode (bots, CI) writes nothing unless <c>-metrics</c> is given.
/// A file stops growing at 24 MB; the newest 120 files are kept.</para>
/// </summary>
public static class Metrics
{
    public const int Schema = 1;
    public const long MaxFileBytes = 24L * 1024 * 1024;
    public const int KeepFiles = 120;
    public const string FolderName = "metrics";

    static StreamWriter writer;
    static bool tried, off, capped;
    static long bytes;
    static int lines;
    static double startReal;
    static long startUnix;
    static readonly StringBuilder sb = new StringBuilder(1024);
    static bool building, warnedBuilding;
    static readonly Dictionary<string, double> window = new Dictionary<string, double>(StringComparer.Ordinal);
    static readonly Dictionary<string, double> total = new Dictionary<string, double>(StringComparer.Ordinal);

    public static string Folder { get; private set; }
    public static string FilePath { get; private set; }
    public static string SessionId { get; private set; }
    public static int Lines => lines;
    public static bool Enabled => OpenIfNeeded();

    /// <summary>Real seconds since the log opened.</summary>
    public static double RealSeconds => Time.realtimeSinceStartupAsDouble - startReal;

    // ------------------------------------------------------------------ writing

    /// <summary>Start one event line. Chain fields, then <see cref="Ev.End"/>. A no-op when metrics are off.</summary>
    public static Ev Begin(string name)
    {
        if (!OpenIfNeeded()) return default;
        if (building) { building = false; if (!warnedBuilding) { warnedBuilding = true; Debug.LogWarning("[Metrics] an event was started and never ended; it was dropped."); } }
        building = true;
        sb.Clear();
        sb.Append("{\"e\":");
        AppendString(sb, name, 48);
        sb.Append(",\"t\":").Append(UnixMs().ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"rt\":").Append(RealSeconds.ToString("0.0", CultureInfo.InvariantCulture));
        double gh = SafeGameHours();
        sb.Append(",\"gh\":").Append(gh.ToString("0.00", CultureInfo.InvariantCulture));
        sb.Append(",\"d\":").Append(SafeDay().ToString(CultureInfo.InvariantCulture));
        return new Ev { live = true };
    }

    /// <summary>One event under construction (fields append in call order).</summary>
    public struct Ev
    {
        internal bool live;

        public Ev Str(string key, string value, int max = 120)
        {
            if (!live) return this;
            Key(key); AppendString(sb, value, max); return this;
        }

        public Ev Int(string key, long value)
        {
            if (!live) return this;
            Key(key); sb.Append(value.ToString(CultureInfo.InvariantCulture)); return this;
        }

        public Ev Num(string key, double value, string format = "0.###")
        {
            if (!live) return this;
            Key(key); sb.Append(Finite(value).ToString(format, CultureInfo.InvariantCulture)); return this;
        }

        public Ev Bool(string key, bool value)
        {
            if (!live) return this;
            Key(key); sb.Append(value ? "true" : "false"); return this;
        }

        /// <summary>A pre-built JSON value (an object or array the caller made with <see cref="Json"/>).</summary>
        public Ev Raw(string key, string json)
        {
            if (!live) return this;
            Key(key); sb.Append(string.IsNullOrEmpty(json) ? "null" : json); return this;
        }

        public void End()
        {
            if (!live) return;
            live = false;
            building = false;
            sb.Append('}');
            WriteLine(sb.ToString());
        }

        static void Key(string key)
        {
            sb.Append(',');
            AppendString(sb, key, 40);
            sb.Append(':');
        }
    }

    /// <summary>Tiny JSON helpers for nested values.</summary>
    public static class Json
    {
        /// <summary>{"a":1,"b":2.5} from a name → number map (sorted, so lines diff cleanly).</summary>
        public static string Map(IDictionary<string, double> map, string format = "0.###")
        {
            if (map == null || map.Count == 0) return "{}";
            var keys = new List<string>(map.Keys);
            keys.Sort(StringComparer.Ordinal);
            var s = new StringBuilder(keys.Count * 20);
            s.Append('{');
            for (int i = 0; i < keys.Count; i++)
            {
                if (i > 0) s.Append(',');
                AppendString(s, keys[i], 48);
                s.Append(':').Append(Finite(map[keys[i]]).ToString(format, CultureInfo.InvariantCulture));
            }
            return s.Append('}').ToString();
        }

        /// <summary>[1,2,3] from numbers.</summary>
        public static string Array(IList<double> values, string format = "0.##")
        {
            if (values == null || values.Count == 0) return "[]";
            var s = new StringBuilder(values.Count * 6);
            s.Append('[');
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0) s.Append(',');
                s.Append(Finite(values[i]).ToString(format, CultureInfo.InvariantCulture));
            }
            return s.Append(']').ToString();
        }
    }

    // ------------------------------------------------------------------ counters (cheap, written with the next snapshot)

    /// <summary>Add to a named running counter. Written (and reset) with every <c>snap</c> line, and summed for <c>session_end</c>.</summary>
    public static void Count(string key, double add = 1.0)
    {
        if (off || string.IsNullOrEmpty(key)) return;
        window[key] = (window.TryGetValue(key, out double w) ? w : 0.0) + add;
        total[key] = (total.TryGetValue(key, out double t) ? t : 0.0) + add;
    }

    /// <summary>The counters since the last snapshot as a JSON object, then clears them.</summary>
    public static string TakeWindow()
    {
        string json = Json.Map(window);
        window.Clear();
        return json;
    }

    /// <summary>Session totals of every counter (for <c>session_end</c>).</summary>
    public static string TotalsJson() => Json.Map(total);

    // ------------------------------------------------------------------ lifecycle

    /// <summary>Make sure the file is open (once per session). False when metrics are off.</summary>
    static bool OpenIfNeeded()
    {
        if (writer != null) return !capped;
        if (tried) return false;
        tried = true;
        try
        {
            if (!Application.isPlaying) { off = true; return false; }
            var args = Environment.GetCommandLineArgs();
            bool Has(string flag) { foreach (var a in args) if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true; return false; }
            if (Has("-nometrics") || (Application.isBatchMode && !Has("-metrics"))) { off = true; return false; }
            return OpenAt(Path.Combine(SaveRoot.Path, FolderName));
        }
        catch (Exception e)
        {
            off = true;
            writer = null;
            Debug.LogWarning("[Metrics] off: " + e.Message);
            return false;
        }
    }

    /// <summary>Tests: open a log in <paramref name="folder"/> even outside play mode. Returns the file's path.</summary>
    internal static string OpenForTests(string folder)
    {
        ResetStatics();
        tried = true;
        if (!OpenAt(folder)) throw new IOException("metrics did not open in " + folder);
        return FilePath;
    }

    static bool OpenAt(string folder)
    {
        Folder = folder;
        Directory.CreateDirectory(Folder);
        Prune(Folder);
        var now = DateTime.UtcNow;
        // A session tag from the clock (not a Guid: ids come from the host service, and this is not a game id). Two game
        // copies started in the same second get different tags; CreateNew refuses a name that exists, so try again.
        FileStream fs = null;
        for (int attempt = 0; attempt < 6 && fs == null; attempt++)
        {
            ulong t = (ulong)now.Ticks ^ ((ulong)Environment.TickCount << 20) ^ ((ulong)attempt * 0x9E3779B97F4A7C15UL);
            t ^= t >> 31; t *= 0xBF58476D1CE4E5B9UL; t ^= t >> 29;
            SessionId = ((uint)t).ToString("x8");
            FilePath = Path.Combine(Folder, $"{now:yyyyMMdd-HHmmss}-{SessionId}.jsonl");
            try { fs = new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read); }
            catch (IOException) { if (attempt == 5) throw; }
        }
        writer = new StreamWriter(fs, new UTF8Encoding(false), 16 * 1024) { AutoFlush = false, NewLine = "\n" };
        startReal = Time.realtimeSinceStartupAsDouble;
        startUnix = new DateTimeOffset(now).ToUnixTimeMilliseconds();
        Debug.Log("[Metrics] playtest metrics go to " + FilePath + " (local only; -nometrics turns them off)");
        return true;
    }

    static void WriteLine(string line)
    {
        if (writer == null) return;
        try
        {
            if (bytes + line.Length + 1 > MaxFileBytes)
            {
                if (!capped)
                {
                    capped = true;
                    string last = "{\"e\":\"capped\",\"t\":" + UnixMs().ToString(CultureInfo.InvariantCulture) + ",\"bytes\":" + bytes.ToString(CultureInfo.InvariantCulture) + "}";
                    writer.WriteLine(last);
                    writer.Flush();
                }
                return;
            }
            writer.WriteLine(line);
            bytes += line.Length + 1;
            lines++;
        }
        catch (Exception e)
        {
            off = true;
            Debug.LogWarning("[Metrics] stopped: " + e.Message);
            try { writer.Dispose(); } catch { /* the disk is the problem */ }
            writer = null;
        }
    }

    /// <summary>Push buffered lines to disk (the runner does this every few seconds and on pause or quit).</summary>
    public static void Flush()
    {
        if (writer == null) return;
        try { writer.Flush(); }
        catch (Exception e) { off = true; writer = null; Debug.LogWarning("[Metrics] stopped: " + e.Message); }
    }

    /// <summary>Flush and close (application quit).</summary>
    public static void Close()
    {
        if (writer == null) return;
        try { writer.Flush(); writer.Dispose(); } catch { /* quitting anyway */ }
        writer = null;
        off = true;
    }

    static void Prune(string folder)
    {
        try
        {
            var files = Directory.GetFiles(folder, "*.jsonl");
            if (files.Length < KeepFiles) return;
            Array.Sort(files, StringComparer.Ordinal);
            for (int i = 0; i < files.Length - KeepFiles + 1; i++) File.Delete(files[i]);
        }
        catch (Exception e) { Debug.LogWarning("[Metrics] prune: " + e.Message); }
    }

    // ------------------------------------------------------------------ helpers

    static long UnixMs() => startUnix + (long)(RealSeconds * 1000.0);

    static double SafeGameHours() { try { return GameClock.TotalHours; } catch { return 0; } }
    static int SafeDay() { try { return GameClock.Day; } catch { return 0; } }

    static double Finite(double v) => double.IsNaN(v) || double.IsInfinity(v) ? 0.0 : v;

    internal static void AppendString(StringBuilder s, string v, int max)
    {
        if (v == null) { s.Append("null"); return; }
        s.Append('"');
        int n = Math.Min(v.Length, max);
        for (int i = 0; i < n; i++)
        {
            char c = v[i];
            switch (c)
            {
                case '"': s.Append("\\\""); break;
                case '\\': s.Append("\\\\"); break;
                case '\n': s.Append("\\n"); break;
                case '\r': s.Append("\\r"); break;
                case '\t': s.Append("\\t"); break;
                default:
                    if (c < 0x20) s.Append("\\u").Append(((int)c).ToString("x4"));
                    else s.Append(c);
                    break;
            }
        }
        if (v.Length > max) s.Append('…');
        s.Append('"');
    }

    /// <summary>A short stable tag for a player or business: 8 hex characters of a hash, never the id itself.</summary>
    public static string Tag(string id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        unchecked
        {
            ulong h = 1469598103934665603UL;
            for (int i = 0; i < id.Length; i++) { h ^= id[i]; h *= 1099511628211UL; }
            h ^= h >> 29; h *= 0xBF58476D1CE4E5B9UL; h ^= h >> 32;
            return ((uint)h).ToString("x8");
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        try { writer?.Dispose(); } catch { /* fine */ }
        writer = null; tried = false; off = false; capped = false; bytes = 0; lines = 0; building = false; warnedBuilding = false;
        window.Clear(); total.Clear();
        Folder = FilePath = SessionId = null;
    }
}
