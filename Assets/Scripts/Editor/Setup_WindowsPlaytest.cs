using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Profile;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Windows playtest settings (package W; Docs/WindowsBuild.md). Idempotent: safe to run any number of times; logs what it
/// changed. Run from <c>Rubbish/Setup/Windows Playtest</c>; <see cref="RubbishBuild.BuildWindowsPlaytest"/> also runs it
/// before every Windows playtest build.
/// <list type="bullet">
/// <item>Player settings for a Standalone Windows x64 build that work from a fresh Windows checkout: run in background,
/// borderless fullscreen by default with a resizable 1280x720 window (Alt+Enter), Mono backend, Direct3D 11 first.</item>
/// <item>The placeholder app icon (<see cref="IconPath"/>) as the default icon.</item>
/// <item>The "Windows Playtest" build profile (<see cref="ProfilePath"/>): Windows 64-bit, development build, scripting
/// define <see cref="PlaytestDefine"/>. A Mac without Windows Build Support can only store the define (the Windows
/// platform settings need that module), so the first Editor on Windows finishes it (<see cref="AutoRepairOnWindows"/>).</item>
/// </list>
/// It never changes the product name, company name, bundle id, API compatibility level or the scene list: those are only
/// checked. (The build copy in ~/Dev/Rubbish-build runs as "RubbishBuildCopy" on purpose; see Tools/bots/sync_build.sh.)
/// </summary>
[InitializeOnLoad]
public static class Setup_WindowsPlaytest
{
    public const string IconPath = "Assets/Art/Icon/RubbishIcon.png";
    public const string ProfilePath = "Assets/Settings/Build Profiles/Windows Playtest.asset";
    /// <summary>Compiled into Windows playtest builds only (agreed with package B3B10): the build uses Steam automatically.</summary>
    public const string PlaytestDefine = "RUBBISH_PLAYTEST";
    public const string ExpectedProduct = "Rubbish";
    public const string ExpectedCompany = "Archway Games";
    public const string ExpectedBundleId = "com.archwaygames.rubbish";
    public const int WindowWidth = 1280;
    public const int WindowHeight = 720;

    /// <summary>Written to the profile's .meta userData once Development Build has been switched on, so a later manual
    /// choice in the Build Profiles window is respected.</summary>
    const string DevDefaultMarker = "rubbish:playtest-development-default-applied";
    const string Tag = "[Setup_WindowsPlaytest]";
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    static readonly GraphicsDeviceType[] WindowsApis = { GraphicsDeviceType.Direct3D11, GraphicsDeviceType.Direct3D12 };

    // ------------------------------------------------------------------ entry points

    [MenuItem("Rubbish/Setup/Windows Playtest")]
    public static void Run()
    {
        List<string> changes = Apply();
        List<string> warnings = Check();
        Debug.Log($"{Tag} {(changes.Count == 0 ? "already up to date" : changes.Count + " change(s)")}" +
                  (changes.Count > 0 ? ":\n  - " + string.Join("\n  - ", changes) : "") +
                  (warnings.Count > 0 ? "\nWarnings:\n  - " + string.Join("\n  - ", warnings) : ""));
        foreach (string w in warnings) Debug.LogWarning($"{Tag} {w}");
    }

    /// <summary>Applies everything; returns a line per change (empty when nothing needed changing).</summary>
    public static List<string> Apply()
    {
        var changes = new List<string>();
        ApplyPlayerSettings(changes);
        ApplyIconImport(changes);
        ApplyIcon(changes);
        EnsureProfile(changes);
        if (changes.Count > 0) SaveProjectSettings();
        return changes;
    }

    /// <summary>Things this script checks but never changes. Empty when all is well.</summary>
    public static List<string> Check()
    {
        var warnings = new List<string>();
        if (PlayerSettings.productName != ExpectedProduct)
            warnings.Add($"Product name is '{PlayerSettings.productName}', expected '{ExpectedProduct}' (it decides the save " +
                         "folder and the exe's data folder). Fine in the build copy (RubbishBuildCopy); anywhere else, fix it in Player settings.");
        if (PlayerSettings.companyName != ExpectedCompany)
            warnings.Add($"Company name is '{PlayerSettings.companyName}', expected '{ExpectedCompany}' (it decides the save folder).");
        string id = PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Standalone);
        if (id != ExpectedBundleId && id != RubbishBuild.BotsBundleId)
            warnings.Add($"Standalone bundle id is '{id}', expected '{ExpectedBundleId}'.");
        EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).ToArray();
        if (scenes.Length == 0 || !scenes[0].path.EndsWith("/Bootstrap.unity", StringComparison.Ordinal))
            warnings.Add("The first enabled scene in Build Settings must be Assets/Scenes/Bootstrap.unity " +
                         $"(it is '{(scenes.Length > 0 ? scenes[0].path : "none")}').");
        return warnings;
    }

    // ------------------------------------------------------------------ player settings

    static void ApplyPlayerSettings(List<string> changes)
    {
        // Multiplayer: the host must keep simulating while its window is in the background.
        if (!PlayerSettings.runInBackground) { PlayerSettings.runInBackground = true; changes.Add("Run In Background on"); }
        if (!PlayerSettings.visibleInBackground) { PlayerSettings.visibleInBackground = true; changes.Add("Visible In Background on"); }
        if (PlayerSettings.fullScreenMode != FullScreenMode.FullScreenWindow)
        {
            PlayerSettings.fullScreenMode = FullScreenMode.FullScreenWindow;
            changes.Add("Fullscreen Mode = Fullscreen Window (borderless)");
        }
        if (!PlayerSettings.defaultIsNativeResolution) { PlayerSettings.defaultIsNativeResolution = true; changes.Add("Default Is Native Resolution on"); }
        if (!PlayerSettings.allowFullscreenSwitch) { PlayerSettings.allowFullscreenSwitch = true; changes.Add("Allow Fullscreen Switch on (Alt+Enter)"); }
        if (!PlayerSettings.resizableWindow) { PlayerSettings.resizableWindow = true; changes.Add("Resizable Window on"); }
        if (PlayerSettings.defaultScreenWidth != WindowWidth || PlayerSettings.defaultScreenHeight != WindowHeight)
        {
            PlayerSettings.defaultScreenWidth = WindowWidth;
            PlayerSettings.defaultScreenHeight = WindowHeight;
            changes.Add($"Default window size {WindowWidth}x{WindowHeight}");
        }
        if (!PlayerSettings.usePlayerLog) { PlayerSettings.usePlayerLog = true; changes.Add("Player.log on"); }

        // Mono builds on any Windows PC with just the Unity editor; IL2CPP would need Visual Studio's C++ tools (optional).
        if (PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone) != ScriptingImplementation.Mono2x)
        {
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
            changes.Add("Standalone scripting backend = Mono");
        }

        // Direct3D 11 first: the widest hardware support for friends' PCs, and the Steam overlay attaches to it reliably
        // when the game isn't started by the Steam client (which is how a playtest zip is run). Direct3D 12 stays as fallback.
        BuildTarget win = BuildTarget.StandaloneWindows64;
        if (PlayerSettings.GetUseDefaultGraphicsAPIs(win) || !PlayerSettings.GetGraphicsAPIs(win).SequenceEqual(WindowsApis))
        {
            PlayerSettings.SetUseDefaultGraphicsAPIs(win, false);
            PlayerSettings.SetGraphicsAPIs(win, WindowsApis);
            changes.Add("Windows graphics APIs = Direct3D11, Direct3D12");
        }
    }

    // ------------------------------------------------------------------ icon

    static void ApplyIconImport(List<string> changes)
    {
        if (!(AssetImporter.GetAtPath(IconPath) is TextureImporter ti))
        {
            changes.Add($"(icon missing at {IconPath}; skipped)");
            return;
        }
        bool dirty = false;
        if (ti.textureType != TextureImporterType.Default) { ti.textureType = TextureImporterType.Default; dirty = true; }
        if (ti.mipmapEnabled) { ti.mipmapEnabled = false; dirty = true; }
        if (!ti.alphaIsTransparency) { ti.alphaIsTransparency = true; dirty = true; }
        if (ti.npotScale != TextureImporterNPOTScale.None) { ti.npotScale = TextureImporterNPOTScale.None; dirty = true; }
        if (ti.textureCompression != TextureImporterCompression.Uncompressed) { ti.textureCompression = TextureImporterCompression.Uncompressed; dirty = true; }
        if (ti.maxTextureSize != 1024) { ti.maxTextureSize = 1024; dirty = true; }
        if (!dirty) return;
        ti.SaveAndReimport();
        changes.Add("Icon import settings (no mipmaps, uncompressed, alpha is transparency, 1024)");
    }

    static void ApplyIcon(List<string> changes)
    {
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(IconPath);
        if (tex == null) return;
        Texture2D[] current = PlayerSettings.GetIcons(NamedBuildTarget.Unknown, IconKind.Any);
        if (current.Length > 0 && current[0] == tex) return;
        PlayerSettings.SetIcons(NamedBuildTarget.Unknown, new[] { tex }, IconKind.Any);
        changes.Add($"Default icon = {IconPath}");
    }

    // ------------------------------------------------------------------ build profile

    /// <summary>
    /// Creates or completes the "Windows Playtest" build profile. Returns it, or null if Unity's build-profile API isn't
    /// reachable (the menu build doesn't need the profile: it passes the same define and options itself).
    /// </summary>
    public static BuildProfile EnsureProfile(List<string> changes)
    {
        var profile = AssetDatabase.LoadAssetAtPath<BuildProfile>(ProfilePath);
        if (profile == null)
        {
            if (!CreateProfileAsset())
            {
                changes.Add("(couldn't create the Windows Playtest build profile through Unity's API; " +
                            "create it by hand: File > Build Profiles > Add Build Profile > Windows; see Docs/WindowsBuild.md)");
                return null;
            }
            profile = AssetDatabase.LoadAssetAtPath<BuildProfile>(ProfilePath);
            if (profile == null) return null;
            changes.Add($"Created build profile {ProfilePath}");
        }

        string[] defines = profile.scriptingDefines ?? Array.Empty<string>();
        if (!defines.Contains(PlaytestDefine))
        {
            profile.scriptingDefines = defines.Concat(new[] { PlaytestDefine }).ToArray();
            EditorUtility.SetDirty(profile);
            changes.Add($"Build profile scripting define {PlaytestDefine}");
        }

        // Development Build lives in the platform settings, which exist only where Windows Build Support is installed.
        object platform = PlatformSettings(profile);
        if (platform == null && WindowsSupportInstalled())
        {
            Invoke(profile, "TryCreatePlatformSettings");
            platform = PlatformSettings(profile);
        }
        AssetImporter importer = AssetImporter.GetAtPath(ProfilePath);
        bool markerSet = importer != null && (importer.userData ?? "").Contains(DevDefaultMarker);
        if (platform != null && !markerSet)
        {
            PropertyInfo dev = platform.GetType().GetProperty("development", Any);
            if (dev != null && dev.CanWrite)
            {
                if (!(bool)dev.GetValue(platform))
                {
                    dev.SetValue(platform, true);
                    changes.Add("Build profile: Development Build on");
                }
                EditorUtility.SetDirty(profile);
                if (importer != null)
                {
                    importer.userData = string.IsNullOrEmpty(importer.userData) ? DevDefaultMarker : importer.userData + ";" + DevDefaultMarker;
                    changes.Add("Build profile: development default recorded (later changes in the window are kept)");
                }
            }
        }

        if (EditorUtility.IsDirty(profile)) AssetDatabase.SaveAssetIfDirty(profile);
        if (importer != null && changes.Any(c => c.StartsWith("Build profile: development default", StringComparison.Ordinal)))
            importer.SaveAndReimport();
        return profile;
    }

    /// <summary>The profile's Development Build flag, or null when its platform settings don't exist on this machine.</summary>
    public static bool? ProfileDevelopment(BuildProfile profile)
    {
        object platform = PlatformSettings(profile);
        PropertyInfo dev = platform?.GetType().GetProperty("development", Any);
        return dev == null ? (bool?)null : (bool)dev.GetValue(platform);
    }

    public static bool WindowsSupportInstalled() =>
        BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64);

    static bool CreateProfileAsset()
    {
        try
        {
            string dir = System.IO.Path.GetDirectoryName(ProfilePath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
                AssetDatabase.CreateFolder(System.IO.Path.GetDirectoryName(dir)?.Replace('\\', '/'), System.IO.Path.GetFileName(dir));

            // Unity 6's "Add Build Profile" path: BuildProfile.CreateInstance(GUID platformId, string assetPath) (internal).
            Type util = typeof(BuildProfile).Assembly.GetType("UnityEditor.Build.Profile.BuildProfileModuleUtil");
            MethodInfo getId = util?.GetMethod("GetPlatformId", Any, null,
                new[] { typeof(BuildTarget), typeof(StandaloneBuildSubtarget) }, null);
            MethodInfo create = typeof(BuildProfile).GetMethod("CreateInstance", Any, null,
                new[] { typeof(GUID), typeof(string) }, null);
            if (getId == null || create == null) return false;
            object platformId = getId.Invoke(null, new object[] { BuildTarget.StandaloneWindows64, StandaloneBuildSubtarget.Player });
            create.Invoke(null, new[] { platformId, ProfilePath });
            AssetDatabase.ImportAsset(ProfilePath);
            return AssetDatabase.LoadAssetAtPath<BuildProfile>(ProfilePath) != null;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"{Tag} Creating the build profile failed: {(e as TargetInvocationException)?.InnerException ?? e}");
            return false;
        }
    }

    static object PlatformSettings(BuildProfile profile) =>
        profile == null ? null : typeof(BuildProfile).GetProperty("platformBuildProfile", Any)?.GetValue(profile);

    static void Invoke(object target, string method)
    {
        try { target.GetType().GetMethod(method, Any, null, Type.EmptyTypes, null)?.Invoke(target, null); }
        catch (Exception e) { Debug.LogWarning($"{Tag} {method} failed: {(e as TargetInvocationException)?.InnerException ?? e}"); }
    }

    static void SaveProjectSettings()
    {
        // AssetDatabase.SaveAssetIfDirty doesn't write ProjectSettings/ProjectSettings.asset, so this is the same save as
        // File > Save Project. It only runs when Apply changed something (normally once per machine).
        AssetDatabase.SaveAssets();
    }

    // ------------------------------------------------------------------ first open on Windows

    /// <summary>
    /// The profile is authored on a Mac, where Windows Build Support isn't installed, so it carries only the define. The
    /// first time an Editor with Windows Build Support loads the project, switch Development Build on (once; the marker in
    /// the .meta keeps a later manual change). Does nothing on a Mac without the module, in batchmode or in play mode.
    /// </summary>
    static Setup_WindowsPlaytest()
    {
        if (Application.isBatchMode) return;
        EditorApplication.delayCall += AutoRepairOnWindows;
    }

    static void AutoRepairOnWindows()
    {
        try
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (!WindowsSupportInstalled()) return;
            AssetImporter importer = AssetImporter.GetAtPath(ProfilePath);
            if (importer == null || (importer.userData ?? "").Contains(DevDefaultMarker)) return;
            var changes = new List<string>();
            EnsureProfile(changes);
            if (changes.Count > 0)
                Debug.Log($"{Tag} Finished the Windows Playtest build profile on this PC:\n  - " + string.Join("\n  - ", changes));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"{Tag} Couldn't finish the Windows Playtest build profile automatically ({e.Message}). " +
                             "Rubbish > Setup > Windows Playtest does it by hand.");
        }
    }
}
