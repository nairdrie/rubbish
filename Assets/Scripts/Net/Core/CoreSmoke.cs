using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;

/// <summary>
/// Smoke checks for the <c>Rubbish.Net.Core</c> assembly (Docs/Multiplayer.md §8.7.4, package T2). They prove, before any
/// wave-1 code depends on it, that the pure assembly compiles into both the Editor and players, that its Netcode and
/// Collections references resolve, and that the runtime pieces later packages lean on (gzip, FNV-1a, UTF-8, command-line
/// parsing) behave the same in a Mac or Windows player as in the Editor.
/// <list type="bullet">
/// <item><c>CoreSmokeTests</c> runs <see cref="RunAll"/> under NUnit (the W0 blocker: <c>Tools/bots/sync_build.sh --tests</c>).</item>
/// <item>A player started with <c>-botsmoke</c> (e.g. <c>RubbishBots -batchmode -nographics -botsmoke</c>) runs the same
/// checks at boot, logs <see cref="PassLine"/> or <see cref="FailLine"/>, and quits with exit code 0 or 1.</item>
/// </list>
/// No files, saves or network. The gzip and FNV helpers here are smoke-only; real code uses <c>BoundedGzip</c> and
/// <c>WorldHash</c>.
/// </summary>
public static class CoreSmoke
{
    public const string Flag = "-botsmoke";
    public const string PassLine = "BOTSMOKE OK";
    public const string FailLine = "BOTSMOKE FAIL";

    /// <summary>One named check and its outcome.</summary>
    public readonly struct Result
    {
        public readonly string Name;
        public readonly bool Ok;
        public readonly string Detail;

        public Result(string name, bool ok, string detail) { Name = name; Ok = ok; Detail = detail; }

        public override string ToString() => $"{(Ok ? "ok  " : "FAIL")} {Name}{(string.IsNullOrEmpty(Detail) ? "" : ": " + Detail)}";
    }

    /// <summary>Run every check. Never throws: a check that throws is reported as failed.</summary>
    public static List<Result> RunAll()
    {
        var results = new List<Result>();
        Run(results, "netcode-linked", NetcodeLinked);
        Run(results, "collections-linked", CollectionsLinked);
        Run(results, "gzip-roundtrip", GzipRoundTrip);
        Run(results, "fnv1a64-vectors", FnvVectors);
        Run(results, "utf8-roundtrip", Utf8RoundTrip);
        Run(results, "args-parse", ArgsParse);
        return results;
    }

    public static bool AllOk(IReadOnlyList<Result> results)
    {
        if (results == null || results.Count == 0) return false;
        for (int i = 0; i < results.Count; i++)
            if (!results[i].Ok) return false;
        return true;
    }

    /// <summary>One line per check, for logs and assertion messages.</summary>
    public static string Describe(IReadOnlyList<Result> results)
    {
        var sb = new StringBuilder();
        foreach (var r in results) sb.Append("\n  ").Append(r);
        return sb.ToString();
    }

    static void Run(List<Result> into, string name, Func<string> check)
    {
        try
        {
            string error = check();
            into.Add(new Result(name, error == null, error));
        }
        catch (Exception e)
        {
            into.Add(new Result(name, false, e.GetType().Name + ": " + e.Message));
        }
    }

    // ------------------------------------------------------------------ checks (null = pass, else what went wrong)

    static string NetcodeLinked()
    {
        string asm = typeof(Unity.Netcode.NetworkManager).Assembly.GetName().Name;
        return asm == "Unity.Netcode.Runtime" ? null : $"NetworkManager came from '{asm}'";
    }

    static string CollectionsLinked()
    {
        var key = new Unity.Collections.FixedString64Bytes("dev:bot-1");
        return key.ToString() == "dev:bot-1" && key.Length == 9 ? null : $"FixedString64Bytes round trip gave '{key}'";
    }

    static string GzipRoundTrip()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 1500; i++) sb.Append("{\"id\":\"curb-").Append(i % 97).Append("\",\"kg\":").Append(i % 13).Append("},");
        byte[] raw = Encoding.UTF8.GetBytes(sb.ToString());
        byte[] gz = Gzip(raw);
        byte[] back = Gunzip(gz);
        if (gz.Length * 4 > raw.Length) return $"gzip barely shrank {raw.Length} B to {gz.Length} B";
        if (back.Length != raw.Length) return $"inflated {back.Length} B, expected {raw.Length} B";
        for (int i = 0; i < raw.Length; i++)
            if (back[i] != raw[i]) return $"byte {i} differs after the round trip";
        return null;
    }

    static string FnvVectors()
    {
        // Standard FNV-1a 64-bit test vectors.
        if (Fnv1a64("") != 0xcbf29ce484222325UL) return "FNV-1a(\"\") is wrong";
        if (Fnv1a64("a") != 0xaf63dc4c8601ec8cUL) return "FNV-1a(\"a\") is wrong";
        if (Fnv1a64("foobar") != 0x85944171f73967e8UL) return "FNV-1a(\"foobar\") is wrong";
        return null;
    }

    static string Utf8RoundTrip()
    {
        const string s = "Dot’s Rubbish Co. — café \U0001F5D1";
        return Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(s)) == s ? null : "UTF-8 round trip changed the text";
    }

    static string ArgsParse()
    {
        string[] args = { "RubbishBots", "-batchmode", "-playerid", "dev:bot-2", "-dataroot=/tmp/run/bot-2", "-botsmoke" };
        if (!HasFlag(args, "-BOTSMOKE")) return "HasFlag missed -botsmoke";
        if (HasFlag(args, "-bot")) return "HasFlag matched a prefix";
        if (!TryGetArg(args, "-playerid", out string id) || id != "dev:bot-2") return $"-playerid gave '{id}'";
        if (!TryGetArg(args, "-dataroot", out string root) || root != "/tmp/run/bot-2") return $"-dataroot= gave '{root}'";
        if (TryGetArg(args, "-batchmode", out _)) return "a flag followed by another flag was read as a value";
        if (TryGetArg(args, "-botsmoke", out _)) return "a trailing flag was read as having a value";
        return null;
    }

    // ------------------------------------------------------------------ helpers (public for CoreSmokeTests)

    /// <summary>True when <paramref name="flag"/> appears as a whole argument (case-insensitive).</summary>
    public static bool HasFlag(string[] args, string flag)
    {
        if (args == null) return false;
        foreach (var a in args)
            if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Reads <c>name value</c> or <c>name=value</c>. A following token that starts with '-' isn't a value.</summary>
    public static bool TryGetArg(string[] args, string name, out string value)
    {
        value = null;
        if (args == null) return false;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == null) continue;
            if (a.Length > name.Length + 1 && a[name.Length] == '=' && a.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            {
                value = a.Substring(name.Length + 1);
                return true;
            }
            if (!string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 < args.Length && args[i + 1] != null && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
            {
                value = args[i + 1];
                return true;
            }
            return false;
        }
        return false;
    }

    public static ulong Fnv1a64(string s)
    {
        ulong h = 0xcbf29ce484222325UL;
        foreach (byte b in Encoding.UTF8.GetBytes(s ?? ""))
        {
            h ^= b;
            h *= 0x100000001b3UL;
        }
        return h;
    }

    public static byte[] Gzip(byte[] raw)
    {
        using (var ms = new MemoryStream())
        {
            using (var gz = new GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, true)) gz.Write(raw, 0, raw.Length);
            return ms.ToArray();
        }
    }

    public static byte[] Gunzip(byte[] gz)
    {
        using (var input = new MemoryStream(gz))
        using (var unzip = new GZipStream(input, CompressionMode.Decompress))
        using (var output = new MemoryStream())
        {
            unzip.CopyTo(output);
            return output.ToArray();
        }
    }

    // ------------------------------------------------------------------ player boot hook

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void BootSmoke()
    {
        if (Application.isEditor) return;
        if (!HasFlag(Environment.GetCommandLineArgs(), Flag)) return;
        var results = RunAll();
        bool ok = AllOk(results);
        string line = $"{(ok ? PassLine : FailLine)} ({results.Count} checks; {Application.platform}; Unity {Application.unityVersion}; " +
                      $"{(Debug.isDebugBuild ? "development" : "release")} build; batchmode {Application.isBatchMode}){Describe(results)}";
        if (ok) Debug.Log(line);
        else Debug.LogError(line);
        Application.Quit(ok ? 0 : 1);
    }
}
