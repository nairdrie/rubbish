using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Player builds (Docs/Multiplayer.md §8.7.4 package T2, §8.7.6).
/// <list type="bullet">
/// <item><see cref="DevHeadless"/>: a Mac development build named <c>RubbishBots</c> (its own product name and bundle id, so its
/// default save folder and PlayerPrefs never touch the real game's) for the headless bot harness in <c>Tools/bots</c>.</item>
/// <item><see cref="BotBuild"/>: the bot pipeline entry used by <c>Tools/bots/sync_build.sh --build</c> in the build copy:
/// <c>Setup_Coop.Run</c> (once it exists), then <see cref="DevHeadless"/>.</item>
/// <item><see cref="BuildWindowsPlaytest"/> (package W, in RubbishBuild.Windows.cs): the Windows 64-bit playtest build for
/// friends, zipped and ready to send. Run it on a Windows PC; see Docs/WindowsBuild.md.</item>
/// </list>
/// Every entry point honours <c>-buildOutput &lt;path&gt;</c>. In batchmode a failure exits with code 1.
/// </summary>
public static partial class RubbishBuild
{
    public const string BotsProductName = "RubbishBots";
    public const string BotsBundleId = "com.archwaygames.rubbishbots";
    public const string DevHeadlessPath = "Builds/DevHeadless/RubbishBots.app";
    public const string BuildCopyFlag = "-rubbishBuildCopy";
    const string Tag = "[RubbishBuild]";

    // ------------------------------------------------------------------ bots (Mac)

    [MenuItem("Rubbish/Build/Bots: DevHeadless (Mac)", priority = 20)]
    static void DevHeadlessMenu()
    {
        if (!EditorUtility.DisplayDialog("Bot build",
                "Bot builds normally run in the build copy (Tools/bots/sync_build.sh --build) so they never lock this Editor.\n\n" +
                "Build RubbishBots.app here anyway?", "Build here", "Cancel")) return;
        DevHeadless();
    }

    /// <summary>Builds <see cref="DevHeadlessPath"/>: StandaloneOSX, development, product "RubbishBots".</summary>
    public static void DevHeadless()
    {
        string output = ArgValue("-buildOutput") ?? DevHeadlessPath;
        if (!output.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) output = Path.Combine(output, BotsProductName + ".app");
        if (!RequireSupport(BuildTarget.StandaloneOSX, "Mac Build Support (Mono)")) return;
        var opts = new BuildPlayerOptions
        {
            scenes = EnabledScenes(),
            locationPathName = output,
            target = BuildTarget.StandaloneOSX,
            targetGroup = BuildTargetGroup.Standalone,
            options = BuildOptions.Development,
        };
        if (opts.scenes == null) return;
        WithIdentity(BotsProductName, BotsBundleId, () => Build(opts, "DevHeadless"));
    }

    /// <summary>
    /// Bot pipeline entry (Tools/bots/sync_build.sh --build). Only runs in the build copy, because <c>Setup_Coop</c> edits
    /// prefabs and saves them.
    /// </summary>
    public static void BotBuild()
    {
        if (!HasArg(BuildCopyFlag))
        {
            Fail($"BotBuild runs Setup_Coop and saves prefabs, so it only runs in the build copy (pass {BuildCopyFlag}; " +
                 "use Tools/bots/sync_build.sh --build).");
            return;
        }
        Type setup = FindType("Setup_Coop");
        MethodInfo run = setup?.GetMethod("Run", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
        if (run != null)
        {
            Debug.Log($"{Tag} Setup_Coop.Run before the bot build");
            try { run.Invoke(null, null); }
            catch (TargetInvocationException e)
            {
                Fail($"Setup_Coop.Run threw: {e.InnerException}");
                return;
            }
        }
        else Debug.Log($"{Tag} Setup_Coop isn't in the project yet (package B2); building without it.");
        DevHeadless();
    }

    // ------------------------------------------------------------------ helpers

    static BuildReport Build(BuildPlayerOptions opts, string label)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(opts.locationPathName));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string config = (opts.options & BuildOptions.Development) != 0 ? "development" : "release config";
        Debug.Log($"{Tag} {label}: building {opts.target} -> {opts.locationPathName} ({opts.scenes.Length} scenes, {config}, " +
                  $"product '{PlayerSettings.productName}')");
        BuildReport report = BuildPipeline.BuildPlayer(opts);
        BuildSummary s = report.summary;
        if (s.result != BuildResult.Succeeded)
        {
            Fail($"{label} failed: {s.result}, {s.totalErrors} error(s). See the build log above.");
            return null;
        }
        Debug.Log($"{Tag} {label}: OK in {s.totalTime.TotalSeconds:0} s, {s.totalSize / (1024f * 1024f):0.0} MB -> {s.outputPath}");
        return report;
    }

    /// <summary>Builds under another product name and bundle id, then puts the project's own back (even on failure).</summary>
    static void WithIdentity(string productName, string bundleId, Action build)
    {
        string oldName = PlayerSettings.productName;
        string oldId = PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Standalone);
        try
        {
            PlayerSettings.productName = productName;
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Standalone, bundleId);
            build();
        }
        finally
        {
            PlayerSettings.productName = oldName;
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Standalone, oldId);
        }
    }

    static string[] EnabledScenes()
    {
        string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        if (scenes.Length == 0)
        {
            Fail("No enabled scenes in Build Settings.");
            return null;
        }
        return scenes;
    }

    static bool RequireSupport(BuildTarget target, string module)
    {
        if (BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, target)) return true;
        Fail($"{target} isn't installed in this Unity ({Application.unityVersion}). Unity Hub -> Installs -> {Application.unityVersion} " +
             $"-> Add modules -> {module}, then try again.");
        return false;
    }

    static void WriteCrlf(string path, params string[] lines) =>
        File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));

    static Type FindType(string name)
    {
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type t = asm.GetType(name, false);
            if (t != null) return t;
        }
        return null;
    }

    static bool HasArg(string flag) =>
        Environment.GetCommandLineArgs().Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    static string ArgValue(string flag)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    static void Fail(string message)
    {
        Debug.LogError($"{Tag} {message}");
        if (Application.isBatchMode) EditorApplication.Exit(1);
        else EditorUtility.DisplayDialog("Rubbish build", message, "OK");
    }
}
