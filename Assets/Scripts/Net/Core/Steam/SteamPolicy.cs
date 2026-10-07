using System;
using System.Text;

// Pure identity and Steam rules (Docs/Accounts.md §1.2, Docs/Multiplayer.md §8.7.4 B3/B10). Facts in, decisions out, so the
// whole table is unit-tested without Steam. Lives in Rubbish.Net.Core: no Steamworks, no Assembly-CSharp.

/// <summary>Which identity this process uses. Fixed per build and launch, never by whether Steam happens to be running.</summary>
public enum IdentityMode : byte
{
    /// <summary><c>dev:&lt;name&gt;</c>; Steam never initialises.</summary>
    Dev,
    /// <summary><c>steam:&lt;id64&gt;</c>. If Steam can't start, the player sees an error screen; there is no fallback to dev.</summary>
    Steam,
}

/// <summary>What the host does with a joiner's Steam proof (Accounts.md §7.6).</summary>
public enum SteamAuthMode : byte
{
    /// <summary>Don't check (tests only).</summary>
    Off,
    /// <summary>Check and log what would be refused, but let everyone in (development and playtest builds).</summary>
    Warn,
    /// <summary>Refuse on any failure, refuse <c>dev:</c> keys and non-members of the lobby (release builds).</summary>
    Enforce,
}

/// <summary>Everything the identity rule depends on, gathered once at boot by <c>PlayerIdentityBoot</c>.</summary>
public struct BuildFacts
{
    public bool isEditor;
    /// <summary>A development player build (<c>Debug.isDebugBuild</c> outside the Editor).</summary>
    public bool isDevelopmentBuild;
    /// <summary>The <c>RUBBISH_PLAYTEST</c> scripting define (the Windows playtest profile): Steam on by default.</summary>
    public bool isPlaytestBuild;
    /// <summary>The <c>RUBBISH_EDITOR_STEAM</c> scripting define: the Editor opts into Steam.</summary>
    public bool editorSteamDefine;
    public bool isBatchmode;
    /// <summary>A Multiplayer Play Mode additional player (never the main Editor).</summary>
    public bool isMppmClone;
    /// <summary>Steamworks.NET is compiled for this platform (not <c>DISABLESTEAMWORKS</c>).</summary>
    public bool steamworksCompiled;
    public string[] args;

    public bool IsRelease => !isEditor && !isDevelopmentBuild;

    public override string ToString() =>
        $"editor={isEditor} dev={isDevelopmentBuild} playtest={isPlaytestBuild} editorSteam={editorSteamDefine} " +
        $"batch={isBatchmode} mppmClone={isMppmClone} steamworks={steamworksCompiled}";
}

/// <summary>The identity decision for this process and why.</summary>
public readonly struct IdentityDecision
{
    public readonly IdentityMode Mode;
    /// <summary>Short reason for logs: "release build", "-steam", "RUBBISH_PLAYTEST", "batchmode", "Editor default", …</summary>
    public readonly string Reason;
    /// <summary>True when Steam was ruled out by batchmode, an MPPM clone or <c>-nosteam</c> (logs "Steam skipped").</summary>
    public readonly bool SteamSkipped;

    public IdentityDecision(IdentityMode mode, string reason, bool steamSkipped)
    {
        Mode = mode;
        Reason = reason;
        SteamSkipped = steamSkipped;
    }

    public override string ToString() => $"{Mode} ({Reason}{(SteamSkipped ? ", Steam skipped" : "")})";
}

/// <summary>Where a dev name came from (Accounts.md §1.2, in this order).</summary>
public enum DevNameSource : byte { PlayerId, MppmTag, Editor, InstallId }

/// <summary>The identity table of Accounts.md §1.2 plus the playtest rule, and the host's auth mode.</summary>
public static class SteamPolicy
{
    public const string SteamFlag = "-steam";
    public const string NoSteamFlag = "-nosteam";
    public const string PlayerIdArg = "-playerid";
    public const string AuthArg = "-steamauth";
    public const string PlaytestDefine = "RUBBISH_PLAYTEST";
    public const string EditorSteamDefine = "RUBBISH_EDITOR_STEAM";

    /// <summary>Valve's shared test app (Spacewar). Development and playtest builds use it until Rubbish has its own App ID.</summary>
    public const uint DevAppId = 480;

    /// <summary>
    /// Decide the identity mode. Order: the Editor (dev unless <c>RUBBISH_EDITOR_STEAM</c>; MPPM clones never), batchmode
    /// (never Steam), playtest builds (Steam unless <c>-nosteam</c>, in either build config), release builds (always Steam),
    /// development builds (dev unless <c>-steam</c>; <c>-nosteam</c> wins).
    /// </summary>
    public static IdentityDecision Decide(in BuildFacts f)
    {
        string[] a = f.args ?? Array.Empty<string>();
        bool noSteam = HasFlag(a, NoSteamFlag);
        bool steam = HasFlag(a, SteamFlag);

        if (f.isEditor)
        {
            if (f.isMppmClone) return new IdentityDecision(IdentityMode.Dev, "MPPM clone", true);
            if (f.isBatchmode) return new IdentityDecision(IdentityMode.Dev, "batchmode", true);
            if (f.editorSteamDefine && !noSteam) return new IdentityDecision(IdentityMode.Steam, EditorSteamDefine, false);
            return new IdentityDecision(IdentityMode.Dev, "Editor default", false);
        }
        if (f.isBatchmode) return new IdentityDecision(IdentityMode.Dev, "batchmode", true);
        if (f.isPlaytestBuild)
            return noSteam ? new IdentityDecision(IdentityMode.Dev, NoSteamFlag, true) : new IdentityDecision(IdentityMode.Steam, PlaytestDefine, false);
        if (f.IsRelease) return new IdentityDecision(IdentityMode.Steam, "release build", false);
        if (noSteam) return new IdentityDecision(IdentityMode.Dev, NoSteamFlag, true);
        if (steam) return new IdentityDecision(IdentityMode.Steam, SteamFlag, false);
        return new IdentityDecision(IdentityMode.Dev, "development build default (add -steam for Steam)", false);
    }

    /// <summary><c>SteamAPI.RestartAppIfNecessary</c> runs only in release players with a real App ID (never 480, never 0).</summary>
    public static bool ShouldRestartApp(bool isEditor, bool isDevelopmentBuild, uint appId) =>
        !isEditor && !isDevelopmentBuild && appId != 0 && appId != DevAppId;

    /// <summary>
    /// A shipped release build (not the Editor, not development, not a playtest): enforce, always. Everything else warns,
    /// unless <c>-steamauth enforce|warn|off</c> says otherwise.
    /// </summary>
    public static SteamAuthMode AuthModeFor(in BuildFacts f)
    {
        if (f.IsRelease && !f.isPlaytestBuild) return SteamAuthMode.Enforce;
        if (TryGetArg(f.args, AuthArg, out string v))
        {
            switch ((v ?? "").Trim().ToLowerInvariant())
            {
                case "enforce": case "release": case "on": return SteamAuthMode.Enforce;
                case "off": case "none": return SteamAuthMode.Off;
                case "warn": return SteamAuthMode.Warn;
            }
        }
        return SteamAuthMode.Warn;
    }

    /// <summary>
    /// The dev name, in order: <c>-playerid &lt;name&gt;</c> (a leading <c>dev:</c> is stripped), the MPPM player tag (clones
    /// only, as <c>mppm-&lt;tag&gt;</c>), <c>editor</c> in the Editor, else the per-install id. Returns a valid dev name.
    /// A <c>-playerid steam:&lt;id&gt;</c> is not a dev name; see <see cref="TryGetSpoofedSteamKey"/>.
    /// </summary>
    public static string ResolveDevName(string[] args, bool isEditor, bool isMppmClone, string mppmTag, Func<string> installId,
                                        out DevNameSource source)
    {
        if (TryGetArg(args, PlayerIdArg, out string pid))
        {
            string raw = pid.Trim();
            if (raw.StartsWith(PlayerKey.DevPrefix, StringComparison.OrdinalIgnoreCase)) raw = raw.Substring(PlayerKey.DevPrefix.Length);
            if (!raw.StartsWith(PlayerKey.SteamPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string n = SanitizeDevName(raw);
                if (n != null) { source = DevNameSource.PlayerId; return n; }
            }
        }
        if (isMppmClone)
        {
            string tag = SanitizeDevName(mppmTag);
            source = DevNameSource.MppmTag;
            return tag != null ? Clip("mppm-" + tag) : "mppm";
        }
        if (isEditor) { source = DevNameSource.Editor; return "editor"; }
        source = DevNameSource.InstallId;
        string id = SanitizeDevName(installId?.Invoke());
        return id ?? "install";
    }

    /// <summary>
    /// <c>-playerid steam:&lt;id&gt;</c> in a non-release build: a deliberately forged key for the door test (MP §8.7.7 M1-10f).
    /// The identity carries no Steam proof, so a host enforcing auth must refuse it.
    /// </summary>
    public static bool TryGetSpoofedSteamKey(string[] args, bool isRelease, out PlayerKey key)
    {
        key = default;
        if (isRelease || !TryGetArg(args, PlayerIdArg, out string pid)) return false;
        string raw = pid.Trim();
        return raw.StartsWith(PlayerKey.SteamPrefix, StringComparison.OrdinalIgnoreCase)
               && PlayerKey.TryParse(PlayerKey.SteamPrefix + raw.Substring(PlayerKey.SteamPrefix.Length), out key) && key.IsSteam;
    }

    /// <summary>Lower-case, anything outside <c>[a-z0-9._-]</c> becomes '-', trimmed to 48. Null when nothing usable is left.</summary>
    public static string SanitizeDevName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var sb = new StringBuilder(raw.Length);
        foreach (char ch in raw.Trim().ToLowerInvariant())
        {
            bool ok = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '.' || ch == '_' || ch == '-';
            sb.Append(ok ? ch : '-');
        }
        string s = Clip(sb.ToString().Trim('-'));
        if (s.Length == 0 || s == "." || s == "..") return null;
        return s;
    }

    static string Clip(string s) => s.Length <= PlayerKey.MaxDevNameLength ? s : s.Substring(0, PlayerKey.MaxDevNameLength);

    // ------------------------------------------------------------------ args (same rules as CoreSmoke: -x v or -x=v)

    public static bool HasFlag(string[] args, string flag)
    {
        if (args == null) return false;
        foreach (var a in args)
            if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

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
}
