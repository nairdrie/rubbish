using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Profile;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Puts <c>steam_appid.txt</c> (the project root's copy, 480 = Spacewar today) next to the executable of every
/// <b>development</b> or <b>playtest</b> (<c>RUBBISH_PLAYTEST</c>) desktop build, so the Steam API can start outside the
/// Steam client (Docs/Multiplayer.md §8.7.4 B3). <b>Release</b> builds never get it (it would stop
/// <c>RestartAppIfNecessary</c> from relaunching through Steam); a stale copy this hook left there earlier is removed.
/// Runs for every build path: menus, <c>-executeMethod</c>, and File &gt; Build Profiles.
/// </summary>
public sealed class SteamBuildPostprocess : BuildPlayerProcessor, IPostprocessBuildWithReport
{
    public const string AppIdFile = "steam_appid.txt";
    const string Tag = "[SteamBuildPostprocess]";

    // What PrepareForBuild saw (BuildPlayerOptions isn't in the report).
    static bool s_playtestDefine;

    public override int callbackOrder => 0;

    public override void PrepareForBuild(BuildPlayerContext context)
    {
        string[] extra = context.BuildPlayerOptions.extraScriptingDefines;
        s_playtestDefine = extra != null && extra.Contains(SteamPolicy.PlaytestDefine);
    }

    public void OnPostprocessBuild(BuildReport report)
    {
        try { Run(report); }
        catch (Exception e) { Debug.LogWarning($"{Tag} Couldn't place {AppIdFile}: {e.Message}"); }
        finally { s_playtestDefine = false; }
    }

    static void Run(BuildReport report)
    {
        BuildTarget target = report.summary.platform;
        if (!IsDesktop(target)) return;
        string exeDir = ExeFolder(report.summary.outputPath, target);
        if (string.IsNullOrEmpty(exeDir) || !Directory.Exists(exeDir)) return;

        bool development = (report.summary.options & BuildOptions.Development) != 0;
        bool playtest = IsPlaytest();
        string src = Path.GetFullPath(AppIdFile);
        string dst = Path.Combine(exeDir, AppIdFile);

        if (development || playtest)
        {
            if (!File.Exists(src))
            {
                Debug.LogWarning($"{Tag} No {AppIdFile} at the project root; Steam won't start outside the Steam client in this build.");
                return;
            }
            File.Copy(src, dst, true);
            Debug.Log($"{Tag} Copied {AppIdFile} ({File.ReadAllText(src).Trim()}) next to the {(playtest ? "playtest" : "development")} build: {dst}");
            return;
        }

        // Release: Steam itself provides the App ID; a leftover file from a dev build in the same folder would mask launch errors.
        if (File.Exists(dst) && File.Exists(src) && File.ReadAllText(dst).Trim() == File.ReadAllText(src).Trim())
        {
            File.Delete(dst);
            Debug.Log($"{Tag} Release build: removed a leftover {AppIdFile} from {exeDir}.");
        }
    }

    /// <summary>The playtest define is on for this build: passed as an extra define, set in the active build profile, or in Player Settings.</summary>
    static bool IsPlaytest()
    {
        if (s_playtestDefine) return true;
        try
        {
            BuildProfile profile = BuildProfile.GetActiveBuildProfile();
            if (profile != null && profile.scriptingDefines != null && profile.scriptingDefines.Contains(SteamPolicy.PlaytestDefine)) return true;
        }
        catch (Exception) { /* no build profiles in this editor */ }
        string defines = PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.Standalone) ?? "";
        return defines.Split(';').Any(d => d.Trim() == SteamPolicy.PlaytestDefine);
    }

    static bool IsDesktop(BuildTarget t) =>
        t == BuildTarget.StandaloneWindows || t == BuildTarget.StandaloneWindows64 || t == BuildTarget.StandaloneOSX ||
        t == BuildTarget.StandaloneLinux64;

    /// <summary>The folder Steam looks in: next to the .exe / Linux binary, or next to the Mac .app bundle.</summary>
    internal static string ExeFolder(string outputPath, BuildTarget target)
    {
        if (string.IsNullOrEmpty(outputPath)) return null;
        string full = Path.GetFullPath(outputPath).TrimEnd('/', '\\');
        if (target == BuildTarget.StandaloneOSX) return Path.GetDirectoryName(full); // next to Foo.app
        return Directory.Exists(full) ? full : Path.GetDirectoryName(full);
    }
}
