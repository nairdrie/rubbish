using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Decides and fixes this process's identity at startup (Docs/Accounts.md §1.2, Docs/Multiplayer.md §8.7.4 B3). The rule is
/// <see cref="SteamPolicy.Decide"/>; in short:
/// <list type="bullet">
/// <item><b>Editor:</b> <c>dev:editor</c>, whether Steam is open or closed. The <c>RUBBISH_EDITOR_STEAM</c> define opts in.</item>
/// <item><b>Windows playtest build</b> (<c>RUBBISH_PLAYTEST</c>, app 480): Steam, automatically (<c>-nosteam</c> opts out).</item>
/// <item><b>Development build:</b> <c>dev:&lt;name&gt;</c> unless started with <c>-steam</c>.</item>
/// <item><b>Release build:</b> Steam, always.</item>
/// <item><b>Batchmode bots, MPPM clones, <c>-nosteam</c>:</b> dev, and Steam never starts (logs "Steam skipped").</item>
/// </list>
/// When Steam is required but can't start, the player gets the "Rubbish needs Steam" screen and <b>no identity</b>
/// (<see cref="PlayerIdentity.Local"/> is null): never a silent fallback to <c>dev:</c>, so saves can't flip between account
/// folders from one day to the next (red-team R5).
/// <para>Providers are registered at SubsystemRegistration (so any later reader gets the right one); Steam and its features
/// start at BeforeSceneLoad.</para>
/// </summary>
public static class PlayerIdentityBoot
{
    const int DevPriority = 0, SteamPriority = 100;

    public static BuildFacts Facts { get; private set; }
    public static IdentityDecision Decision { get; private set; }
    public static SteamAuthMode AuthMode { get; private set; }
    /// <summary>The MPPM player tag (clones only), or null.</summary>
    public static string MppmTag { get; private set; }
    public static bool Installed { get; private set; }
    public static bool Booted { get; private set; }

    /// <summary>Steam is this process's identity and it's running.</summary>
    public static bool SteamActive => Decision.Mode == IdentityMode.Steam && SteamBootstrap.IsInitialized;
    /// <summary>Steam is required here but isn't running (the blocking screen is up).</summary>
    public static bool SteamMissing => Booted && Decision.Mode == IdentityMode.Steam && !SteamBootstrap.IsInitialized;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void OnSubsystemRegistration()
    {
        Installed = Booted = false;
        Install();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void OnBeforeSceneLoad() => Boot();

    /// <summary>
    /// Decide the identity and register its provider (resets <see cref="PlayerIdentity"/>). No Steam calls happen here: the
    /// Steam provider starts Steam when the identity is first read. Safe in edit mode (evals): in the Editor it registers
    /// <c>dev:editor</c>.
    /// </summary>
    public static void Install()
    {
        PlayerIdentity.Reset();
        Facts = GatherFacts(out string tag);
        MppmTag = tag;
        Decision = SteamPolicy.Decide(Facts);
        AuthMode = SteamPolicy.AuthModeFor(Facts);
        var facts = Facts;
        if (Decision.Mode == IdentityMode.Dev) PlayerIdentity.Register(() => DevIdentity.ForThisProcess(facts, tag), DevPriority);
        else PlayerIdentity.Register(SteamProvider, SteamPriority);
        Installed = true;
    }

    /// <summary>Fix the identity now, start Steam's features when Steam is the identity, and log one line.</summary>
    public static void Boot()
    {
        if (Booted) return;
        if (!Installed) Install();
        Booted = true;

        if (Decision.SteamSkipped) Debug.Log($"[Steam] Steam skipped ({Decision.Reason}).");
        // Release rules at the door (B1's approval reads this): no dev: keys. Development and playtest builds accept them.
        SessionService.RejectDevKeys = AuthMode == SteamAuthMode.Enforce;
        IPlayerIdentity id = PlayerIdentity.Local; // first read: fixes the identity for this process

        if (Decision.Mode == IdentityMode.Steam)
        {
            if (SteamBootstrap.IsInitialized) SteamBootstrap.StartFeatures(AuthMode);
            else
            {
                string err = SteamBootstrap.InitError ?? "Steam couldn't start.";
                Debug.LogWarning($"[Identity] Steam is required here ({Decision.Reason}) but didn't start: {err} No identity until it does (no dev fallback).");
                SteamHud.ShowSteamRequired(err, RetrySteam);
            }
        }

        string where = Application.isEditor ? "Editor" : Application.isBatchMode ? "batchmode player" : Debug.isDebugBuild ? "development build" : "release build";
        Debug.Log(id != null
            ? $"[Identity] {id.Key} as \"{id.DisplayName}\" ({where}; {Decision}; Steam {(SteamBootstrap.IsInitialized ? "on" : "off")})."
            : $"[Identity] none yet ({where}; {Decision}): waiting for Steam.");
        NetAudit.Event("identity", id != null ? id.Key.Value : "none (Steam required)");
    }

    /// <summary>
    /// The "Try again" button on the Steam screen: start Steam, then fix the Steam identity and start its features. True when
    /// Steam is up and the identity exists.
    /// </summary>
    public static bool RetrySteam()
    {
        if (Decision.Mode != IdentityMode.Steam) return PlayerIdentity.Local != null;
        if (!SteamBootstrap.TryInit(out string err))
        {
            Debug.Log($"[Identity] Steam still isn't up: {err}");
            return false;
        }
        PlayerIdentity.Reset();
        PlayerIdentity.Register(SteamProvider, SteamPriority);
        IPlayerIdentity id = PlayerIdentity.Local;
        SteamBootstrap.StartFeatures(AuthMode);
        Debug.Log($"[Identity] {(id != null ? id.Key.ToString() : "none")} (Steam started on retry).");
        NetAudit.Event("identity", id != null ? id.Key.Value : "none (Steam required)");
        return id != null;
    }

    /// <summary>One line describing the decision, for evals and the HUD (no side effects).</summary>
    public static string Describe()
    {
        var f = Installed ? Facts : GatherFacts(out _);
        var d = Installed ? Decision : SteamPolicy.Decide(f);
        return $"{d} | {f} | auth {SteamPolicy.AuthModeFor(f)}";
    }

    static IPlayerIdentity SteamProvider()
    {
        if (!SteamBootstrap.TryInit(out _)) return null; // the Steam screen explains; never a dev fallback
        return SteamIdentity.TryCreate();
    }

    static BuildFacts GatherFacts(out string mppmTag)
    {
        mppmTag = null;
        bool clone = false;
#if UNITY_EDITOR
        clone = ReadMppm(out mppmTag);
#endif
        return new BuildFacts
        {
            isEditor = Application.isEditor,
            isDevelopmentBuild = !Application.isEditor && Debug.isDebugBuild,
#if RUBBISH_PLAYTEST
            isPlaytestBuild = true,
#else
            isPlaytestBuild = false,
#endif
#if RUBBISH_EDITOR_STEAM
            editorSteamDefine = true,
#else
            editorSteamDefine = false,
#endif
            isBatchmode = Application.isBatchMode,
            isMppmClone = clone,
            steamworksCompiled = SteamBootstrap.Compiled,
            args = SafeArgs(),
        };
    }

    static string[] SafeArgs()
    {
        try { return Environment.GetCommandLineArgs() ?? Array.Empty<string>(); }
        catch (Exception) { return Array.Empty<string>(); }
    }

#if UNITY_EDITOR
    /// <summary>
    /// Multiplayer Play Mode, read by reflection so there's no hard dependency: an additional player is a clone (never the main
    /// Editor); its first tag names it. Falls back to the clone's project path (<c>Library/VP/</c>).
    /// </summary>
    static bool ReadMppm(out string tag)
    {
        tag = null;
        bool clone = false;
        try
        {
            Type t = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                t = asm.GetType("Unity.Multiplayer.Playmode.CurrentPlayer", false) ?? asm.GetType("Unity.Multiplayer.PlayMode.CurrentPlayer", false);
                if (t != null) break;
            }
            if (t != null)
            {
                var isMain = t.GetProperty("IsMainEditor", BindingFlags.Public | BindingFlags.Static);
                if (isMain != null && isMain.GetValue(null) is bool main) clone = !main;
                var tags = t.GetMethod("ReadOnlyTags", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                object v = tags?.Invoke(null, null);
                if (v is string s1) tag = s1;
                else if (v is IEnumerable<string> many) foreach (var s2 in many) { if (!string.IsNullOrEmpty(s2)) { tag = s2; break; } }
            }
        }
        catch (Exception e) { Debug.LogWarning($"[Identity] Couldn't read the MPPM player: {e.Message}"); }
        if (!clone && (Application.dataPath ?? "").Replace('\\', '/').Contains("/Library/VP/")) clone = true;
        if (!clone) tag = null;
        return clone;
    }
#endif
}
