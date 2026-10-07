using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Profile;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Windows playtest build (package W; the how-to for Nick is Docs/WindowsBuild.md). The bot builds and the shared helpers
/// (<c>Build</c>, <c>EnabledScenes</c>, <c>RequireSupport</c>, <c>Fail</c>, <c>WriteCrlf</c>, args) live in RubbishBuild.cs (T2).
/// <para>Two ways in, same result:</para>
/// <list type="number">
/// <item>Menu <c>Rubbish/Build/Windows Playtest (zip for friends)</c> or <c>-executeMethod RubbishBuild.BuildWindowsPlaytest</c>:
/// applies <see cref="Setup_WindowsPlaytest"/>, builds <c>Builds/Windows-Playtest/Rubbish.exe</c> (development build,
/// define <c>RUBBISH_PLAYTEST</c>), then finishes the folder and zips it.</item>
/// <item>File &gt; Build Profiles &gt; "Windows Playtest" &gt; Build: <see cref="WindowsPlaytestPostprocess"/> finishes and
/// zips whatever folder was picked.</item>
/// </list>
/// "Finishing" puts <c>steam_appid.txt</c> (480), a friendly <c>README.txt</c> (from Tools/playtest/README_FRIENDS.txt)
/// and <c>BuildInfo.txt</c> next to the exe; the zip <c>Builds/Rubbish-Playtest-&lt;version&gt;-&lt;stamp&gt;.zip</c>
/// holds one folder of the same name.
/// </summary>
public static partial class RubbishBuild
{
    public const string WindowsPlaytestFolder = "Builds/Windows-Playtest";
    public const string WindowsPlaytestExe = "Rubbish.exe";
    public const string ReleaseFlag = "-playtestRelease";
    public const string FriendsReadmeTemplate = "Tools/playtest/README_FRIENDS.txt";
    /// <summary>Marks a folder this script built, so the next build may clear it. Never shipped in the zip.</summary>
    public const string PlaytestMarkerFile = ".rubbish-playtest-build";
    const string SteamAppIdFile = "steam_appid.txt";
    const string FallbackAppId = "480";
    /// <summary>The launcher an earlier version of this script wrote; the playtest build starts Steam by itself now.</summary>
    const string OldSteamLauncher = "Play Rubbish (Steam).bat";

    /// <summary>True while <see cref="BuildWindowsPlaytest"/> is inside BuildPipeline.BuildPlayer.</summary>
    internal static bool s_menuPlaytestBuild;
    /// <summary>True from BuildPlayerProcessor.PrepareForBuild to the postprocess when the build carries the playtest define.</summary>
    internal static bool s_contextPlaytestBuild;

    // ------------------------------------------------------------------ entry points

    /// <summary>
    /// Windows 64-bit playtest build for friends: <c>Builds/Windows-Playtest/Rubbish.exe</c> plus
    /// <c>Builds/Rubbish-Playtest-&lt;version&gt;-&lt;yyyyMMdd-HHmm&gt;.zip</c>. Development build unless
    /// <c>-playtestRelease</c> is on the command line. <c>-buildOutput &lt;folder or .exe&gt;</c> moves it.
    /// </summary>
    [MenuItem("Rubbish/Build/Windows Playtest (zip for friends)", priority = 1)]
    public static void BuildWindowsPlaytest() => BuildWindows(!HasArg(ReleaseFlag));

    [MenuItem("Rubbish/Build/Windows Playtest (release config)", priority = 2)]
    static void BuildWindowsPlaytestReleaseMenu() => BuildWindows(false);

    [MenuItem("Rubbish/Build/Open Builds Folder", priority = 40)]
    static void OpenBuildsFolder()
    {
        string builds = Path.GetFullPath("Builds");
        Directory.CreateDirectory(builds);
        EditorUtility.RevealInFinder(builds);
    }

    /// <summary>
    /// True while a Windows playtest build is running (menu, command line, or the "Windows Playtest" build profile). For
    /// other build hooks, e.g. B3B10's SteamBuildPostprocess (steam_appid.txt next to development and playtest builds).
    /// </summary>
    public static bool IsWindowsPlaytestBuild(BuildTarget target) =>
        target == BuildTarget.StandaloneWindows64 && (s_menuPlaytestBuild || s_contextPlaytestBuild || PlaytestProfileIsActive());

    public static bool PlaytestProfileIsActive()
    {
        BuildProfile active = BuildProfile.GetActiveBuildProfile();
        return active != null && AssetDatabase.GetAssetPath(active) == Setup_WindowsPlaytest.ProfilePath;
    }

    // ------------------------------------------------------------------ build

    static void BuildWindows(bool development)
    {
        if (!RequireSupport(BuildTarget.StandaloneWindows64, "Windows Build Support (Mono)")) return;

        var applied = Setup_WindowsPlaytest.Apply();
        if (applied.Count > 0)
            Debug.Log($"{Tag} Windows playtest settings applied before building:\n  - " + string.Join("\n  - ", applied));
        foreach (string w in Setup_WindowsPlaytest.Check()) Debug.LogWarning($"{Tag} {w}");

        // W2: never ship a stale NetSession prefab or stale player/truck network flags. Setup_Coop is idempotent and changes
        // nothing when everything is up to date (the normal case); if it does change something, those files need a check-in.
        string coop = Setup_Coop.Build(out bool coopFailed, out int coopChanges);
        if (coopFailed)
        {
            Fail("Setup_Coop failed, so the co-op prefabs may be stale. See the Console, run Rubbish > Setup > Coop, then build " +
                 "again.\n" + coop);
            return;
        }
        if (coopChanges > 0)
            Debug.LogWarning($"{Tag} Setup_Coop updated the co-op prefabs before building ({coopChanges} change(s)); check them in.\n{coop}");

        string[] scenes = EnabledScenes();
        if (scenes == null) return;
        if (!scenes[0].EndsWith("/Bootstrap.unity", StringComparison.Ordinal))
        {
            Fail($"The first enabled scene must be Assets/Scenes/Bootstrap.unity (it is {scenes[0]}). Fix the order in Build Settings.");
            return;
        }

        string arg = ArgValue("-buildOutput");
        string folder, exe;
        if (arg != null && arg.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            exe = Path.GetFullPath(arg);
            folder = Path.GetDirectoryName(exe);
        }
        else
        {
            folder = Path.GetFullPath(arg ?? WindowsPlaytestFolder);
            exe = Path.Combine(folder, WindowsPlaytestExe);
        }
        if (!PrepareOutputFolder(folder)) return;

        var opts = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = exe,
            target = BuildTarget.StandaloneWindows64,
            targetGroup = BuildTargetGroup.Standalone,
            options = development ? BuildOptions.Development : BuildOptions.None,
            extraScriptingDefines = new[] { Setup_WindowsPlaytest.PlaytestDefine },
        };
        BuildReport report;
        s_menuPlaytestBuild = true;
        try { report = Build(opts, development ? "Windows playtest (development)" : "Windows playtest (release config)"); }
        finally { s_menuPlaytestBuild = false; s_contextPlaytestBuild = false; }
        if (report == null) return;

        string zip = FinishPlaytestFolder(exe, development);
        if (zip != null && !Application.isBatchMode) EditorUtility.RevealInFinder(zip);
    }

    /// <summary>
    /// Makes sure <paramref name="folder"/> is empty before a build: creates it, or clears it when this script built it
    /// before (<see cref="PlaytestMarkerFile"/>). Refuses a non-empty folder it didn't make, so it never deletes anything else.
    /// </summary>
    internal static bool PrepareOutputFolder(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
                return true;
            }
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) return true;
            if (!File.Exists(Path.Combine(folder, PlaytestMarkerFile)))
            {
                Fail($"{folder} already has files in it that this build script didn't make. Empty it (or pass " +
                     "-buildOutput <another folder>), then build again.");
                return false;
            }
            Directory.Delete(folder, true);
            Directory.CreateDirectory(folder);
            return true;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            Fail($"Couldn't clear the previous build in {folder}: {e.Message}\nClose Rubbish (and any Explorer window " +
                 "showing that folder), then build again.");
            return false;
        }
    }

    // ------------------------------------------------------------------ finish + zip

    /// <summary>
    /// Puts steam_appid.txt, README.txt and BuildInfo.txt next to <paramref name="exePath"/>, marks the folder as this
    /// script's, and zips it next to the folder (in <c>Builds/</c> when the folder sits at the project root). With
    /// <paramref name="checkFolder"/> (a folder picked in the Build Profiles window) it only marks and zips a folder that
    /// holds nothing but the build. Returns the zip's path, or null when it didn't zip (reported).
    /// </summary>
    internal static string FinishPlaytestFolder(string exePath, bool development, bool checkFolder = false)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(exePath));
        string exeName = Path.GetFileName(exePath);
        string version = PlayerSettings.bundleVersion;
        DateTime now = DateTime.Now;
        string name = $"Rubbish-Playtest-{version}-{now:yyyyMMdd-HHmm}{(development ? "" : "-release")}";
        // Unity can report "Succeeded" without writing the player, e.g. when Windows Build Support was installed while this
        // Editor was already open (it only loads modules at startup). Never zip a folder without the game in it.
        string dataDir = Path.Combine(folder, Path.GetFileNameWithoutExtension(exeName) + "_Data");
        if (!File.Exists(Path.GetFullPath(exePath)) || !Directory.Exists(dataDir))
        {
            Fail($"Unity said the build worked, but {exeName} (or its _Data folder) isn't in {folder}, so there's nothing to zip. " +
                 "If you just installed Windows Build Support, quit Unity, open the project again and build again: Unity only " +
                 "picks up new modules when it starts. Otherwise look for the first red error in the Console above.");
            return null;
        }
        try
        {
            WritePlaytestExtras(folder, exeName, development, version, now, name);
            if (checkFolder && !LooksLikeBuildFolder(folder, exeName, out string stray))
            {
                Debug.LogWarning($"{Tag} Added steam_appid.txt, README.txt and BuildInfo.txt to {folder}, but didn't zip it: " +
                                 $"'{stray}' in there isn't part of the build. Build into an empty folder (e.g. " +
                                 $"{WindowsPlaytestFolder}) to get a zip.");
                return null;
            }
            string zip = ZipPathFor(folder, name);
            if (zip == null)
            {
                Debug.LogWarning($"{Tag} Finished {folder} but didn't zip it (the zip would land inside the folder itself). " +
                                 $"Build into a subfolder such as {WindowsPlaytestFolder} to get a zip.");
                return null;
            }
            File.WriteAllText(Path.Combine(folder, PlaytestMarkerFile), name + "\n");
            int files = ZipFolder(folder, zip, name);
            Debug.Log($"{Tag} Windows playtest ready: {zip} ({files} files, {new FileInfo(zip).Length / (1024f * 1024f):0.0} MB). " +
                      $"Send the zip; friends unzip it and double-click {exeName} with Steam running (README.txt inside).");
            return zip;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            Fail($"The build worked, but finishing {folder} failed: {e.Message}");
            return null;
        }
    }

    internal static void WritePlaytestExtras(string folder, string exeName, bool development, string version, DateTime built, string name)
    {
        // Steam needs steam_appid.txt next to the exe when the game isn't started by the Steam client (480 = Spacewar,
        // Valve's shared test app, until Rubbish has its own AppID). B3B10's SteamBuildPostprocess may have copied it already.
        string appIdDst = Path.Combine(folder, SteamAppIdFile);
        if (!File.Exists(appIdDst))
        {
            string appIdSrc = Path.GetFullPath(SteamAppIdFile);
            if (File.Exists(appIdSrc)) File.Copy(appIdSrc, appIdDst);
            else
            {
                Debug.LogWarning($"{Tag} No {SteamAppIdFile} at the project root; writing {FallbackAppId} next to the exe.");
                File.WriteAllText(appIdDst, FallbackAppId);
            }
        }
        string appId = File.ReadAllText(appIdDst).Trim();

        string oldLauncher = Path.Combine(folder, OldSteamLauncher);
        if (File.Exists(oldLauncher)) File.Delete(oldLauncher);

        string config = development ? "development build" : "release config";
        string builtText = built.ToString("ddd d MMM yyyy, HH:mm");
        string template = File.Exists(FriendsReadmeTemplate) ? File.ReadAllText(FriendsReadmeTemplate) : FallbackReadme;
        string readme = template
            .Replace("{VERSION}", version)
            .Replace("{BUILT}", builtText)
            .Replace("{EXE}", exeName)
            .Replace("{APPID}", appId)
            .Replace("{CONFIG}", config)
            .Replace("{NAME}", name);
        WriteCrlf(Path.Combine(folder, "README.txt"), readme.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'));

        WriteCrlf(Path.Combine(folder, "BuildInfo.txt"),
            $"Rubbish playtest {version}",
            $"build      {name}",
            $"built      {builtText} (local), {built.ToUniversalTime():yyyy-MM-dd HH:mm} UTC",
            $"config     {config}, define {Setup_WindowsPlaytest.PlaytestDefine}",
            $"unity      {Application.unityVersion}",
            $"steam app  {appId}",
            $"protocol   {NetVersion.Protocol} (content {NetVersion.Content})",
            "",
            "Everyone in a session needs the same build. If someone sees \"Update your game\", send them the newest zip.");
    }

    /// <summary>Where the zip for <paramref name="folder"/> goes: next to it, or in <c>Builds/</c> when the folder is at the
    /// project root. Null if that would be inside the folder.</summary>
    internal static string ZipPathFor(string folder, string name)
    {
        string root = Path.GetFullPath(folder).TrimEnd('/', '\\');
        string project = Path.GetFullPath(Directory.GetCurrentDirectory()).TrimEnd('/', '\\');
        string parent = Path.GetDirectoryName(root);
        string dir = string.IsNullOrEmpty(parent) || SamePath(parent, project) ? Path.Combine(project, "Builds") : parent;
        string zip = Path.Combine(dir, name + ".zip");
        if (IsInside(zip, root)) return null;
        Directory.CreateDirectory(dir);
        return zip;
    }

    /// <summary>
    /// True when the folder's top level holds only what a Unity Windows build (plus this script) puts there, so it's safe
    /// to zip and to clear next time. <paramref name="stray"/> names the first thing that isn't.
    /// </summary>
    internal static bool LooksLikeBuildFolder(string folder, string exeName, out string stray)
    {
        string exeBase = Path.GetFileNameWithoutExtension(exeName);
        string[] dirs = { exeBase + "_Data", "MonoBleedingEdge", "D3D12" };
        string[] fileExts = { ".exe", ".dll", ".pdb", ".txt", ".bat" };
        foreach (string d in Directory.GetDirectories(folder))
        {
            string n = Path.GetFileName(d);
            if (dirs.Contains(n, StringComparer.OrdinalIgnoreCase) || n.EndsWith("_DoNotShip", StringComparison.Ordinal) ||
                n.EndsWith("_ButDontShipItWithYourGame", StringComparison.Ordinal)) continue;
            stray = n + "/";
            return false;
        }
        foreach (string f in Directory.GetFiles(folder))
        {
            string n = Path.GetFileName(f);
            if (n == PlaytestMarkerFile || fileExts.Contains(Path.GetExtension(n), StringComparer.OrdinalIgnoreCase)) continue;
            stray = n;
            return false;
        }
        stray = null;
        return true;
    }

    static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('/', '\\'), Path.GetFullPath(b).TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);

    static bool IsInside(string path, string folder) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(folder).TrimEnd('/', '\\') + Path.DirectorySeparatorChar,
                                          StringComparison.OrdinalIgnoreCase);

    /// <summary>Zips <paramref name="folder"/> into <paramref name="zipPath"/> as one top folder <paramref name="topName"/>,
    /// leaving out Unity's don't-ship folders and the marker file. Returns the number of files.</summary>
    internal static int ZipFolder(string folder, string zipPath, string topName)
    {
        string root = Path.GetFullPath(folder).TrimEnd('/', '\\');
        if (IsInside(zipPath, root))
            throw new IOException($"The zip can't go inside the folder it zips ({zipPath}).");
        if (File.Exists(zipPath)) File.Delete(zipPath);
        int count = 0;
        using (var stream = new FileStream(zipPath, FileMode.CreateNew))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                string rel = file.Substring(root.Length + 1).Replace('\\', '/');
                if (rel == PlaytestMarkerFile) continue;
                // Unity's "don't ship" folders (Burst debug info, IL2CPP backups) stay out of the zip.
                if (rel.Split('/').Any(p => p.EndsWith("_DoNotShip", StringComparison.Ordinal) ||
                                            p.EndsWith("_ButDontShipItWithYourGame", StringComparison.Ordinal))) continue;
                ZipArchiveEntry entry = zip.CreateEntry(topName + "/" + rel, System.IO.Compression.CompressionLevel.Optimal);
                entry.LastWriteTime = File.GetLastWriteTime(file);
                using (Stream src = File.OpenRead(file))
                using (Stream dst = entry.Open()) src.CopyTo(dst);
                count++;
            }
        }
        return count;
    }

    const string FallbackReadme =
        "RUBBISH - playtest build {VERSION} ({BUILT})\n\n" +
        "1. Open Steam and sign in. Steam will say you're playing \"Spacewar\"; that's expected.\n" +
        "2. Unzip the whole folder, then double-click {EXE}. \"Rubbish needs Steam\"? Start Steam, then Try again.\n" +
        "3. \"Windows protected your PC\"? Click More info, then Run anyway.\n" +
        "4. One person clicks Host with Friends; friends use Join Game in Steam, accept the invite, or type the invite code.\n\n" +
        "Problems? Send Player.log from %USERPROFILE%\\AppData\\LocalLow\\Archway Games\\Rubbish\n";
}

/// <summary>Notes whether the build that's starting carries the playtest define (used when building from a build profile).</summary>
class WindowsPlaytestPrepare : BuildPlayerProcessor
{
    public override int callbackOrder => 0;

    public override void PrepareForBuild(BuildPlayerContext ctx)
    {
        BuildPlayerOptions o = ctx.BuildPlayerOptions;
        RubbishBuild.s_contextPlaytestBuild = o.target == BuildTarget.StandaloneWindows64 && o.extraScriptingDefines != null &&
                                              o.extraScriptingDefines.Contains(Setup_WindowsPlaytest.PlaytestDefine);
    }
}

/// <summary>
/// Finishes a Windows playtest built from File &gt; Build Profiles ("Windows Playtest"): steam_appid.txt, README.txt,
/// BuildInfo.txt and the zip, exactly like the menu build. The menu build finishes its own folder, so this skips it.
/// Runs late (order 1000) so Steamworks.NET's RedistCopy and B3B10's SteamBuildPostprocess go first.
/// </summary>
class WindowsPlaytestPostprocess : IPostprocessBuildWithReport
{
    public int callbackOrder => 1000;

    public void OnPostprocessBuild(BuildReport report)
    {
        BuildSummary s = report.summary;
        bool playtest = RubbishBuild.IsWindowsPlaytestBuild(s.platform);
        RubbishBuild.s_contextPlaytestBuild = false;
        if (!playtest || RubbishBuild.s_menuPlaytestBuild) return;
        if (string.IsNullOrEmpty(s.outputPath) || !s.outputPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
        RubbishBuild.FinishPlaytestFolder(s.outputPath, (s.options & BuildOptions.Development) != 0, checkFolder: true);
    }
}
