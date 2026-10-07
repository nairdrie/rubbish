using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

/// <summary>
/// The data root every save lives under (Docs/Accounts.md §5.1, hotfix L0). In priority order:
/// <list type="number">
/// <item>a test override pushed by code (<see cref="PushTestOverride"/>: evals and unit tests only);</item>
/// <item><c>-dataroot &lt;dir&gt;</c> on the command line (bots, test builds);</item>
/// <item>Editor only: <c>UserSettings/RubbishDev.json</c> <c>{dataRoot, owner, expiresUtc}</c> while unexpired and at most
/// 2 h ahead (per project, never EditorPrefs: red-team R24);</item>
/// <item><c>Application.persistentDataPath</c> (the real saves).</item>
/// </list>
/// Resolved once per play session (at SubsystemRegistration) and fixed until the next one, so a root can't change under a
/// running game. Any override shows a "TEST DATA ROOT" watermark in play mode.
/// </summary>
public static class SaveRoot
{
    /// <summary>The override file, relative to the project folder (Editor only).</summary>
    public const string DevSettingsFile = "UserSettings/RubbishDev.json";
    public static readonly TimeSpan MaxDevOverride = TimeSpan.FromHours(2);

    static string path, source;
    static bool overridden;
    static readonly List<string> testStack = new List<string>();

    /// <summary>Absolute data root. Resolve it on the main thread first (it is, at play start).</summary>
    public static string Path
    {
        get
        {
            if (path == null) Resolve();
            return path;
        }
    }

    /// <summary>True when <see cref="Path"/> is not the real <c>persistentDataPath</c>.</summary>
    public static bool IsOverridden
    {
        get
        {
            if (path == null) Resolve();
            return overridden;
        }
    }

    /// <summary>"persistentDataPath", "-dataroot", "UserSettings/RubbishDev.json (owner …)" or "test override".</summary>
    public static string Source
    {
        get
        {
            if (path == null) Resolve();
            return source;
        }
    }

    /// <summary>True when the root is a code-pushed test override (evals/unit tests may write in edit mode).</summary>
    public static bool IsTestOverride => testStack.Count > 0;

    /// <summary>Re-read the sources (play-session start; after a test override changes).</summary>
    public static void Refresh()
    {
        path = null;
        Resolve();
    }

    /// <summary>
    /// Point the root at <paramref name="dir"/> until the returned handle is disposed. For edit-mode evals and unit tests
    /// only; while one is active, <c>LegacySave</c> may write in edit mode (never to the real root).
    /// </summary>
    public static IDisposable PushTestOverride(string dir)
    {
        if (string.IsNullOrEmpty(dir)) throw new ArgumentException("test data root is empty", nameof(dir));
        string full = System.IO.Path.GetFullPath(dir);
        if (SamePath(full, Application.persistentDataPath))
            throw new ArgumentException("a test data root can't be the real persistentDataPath", nameof(dir));
        testStack.Add(full);
        Refresh();
        return new Pop(full);
    }

    sealed class Pop : IDisposable
    {
        string dir;
        public Pop(string d) { dir = d; }
        public void Dispose()
        {
            if (dir == null) return;
            int i = testStack.LastIndexOf(dir);
            if (i >= 0) testStack.RemoveAt(i);
            dir = null;
            Refresh();
        }
    }

    static void Resolve()
    {
        if (testStack.Count > 0)
        {
            Set(testStack[testStack.Count - 1], "test override", true);
            return;
        }

        string arg = CommandLineValue("-dataroot");
        if (!string.IsNullOrEmpty(arg))
        {
            Set(System.IO.Path.GetFullPath(arg), "-dataroot", true);
            return;
        }

#if UNITY_EDITOR
        if (TryDevSettings(out string devRoot, out string owner))
        {
            Set(devRoot, $"{DevSettingsFile} (owner {owner})", true);
            return;
        }
#endif
        Set(Application.persistentDataPath, "persistentDataPath", false);
    }

    static void Set(string p, string src, bool isOverride)
    {
        string before = path;
        path = p;
        source = src;
        overridden = isOverride && !SamePath(p, Application.persistentDataPath);
        if (overridden && before != p && Application.isPlaying)
            Debug.LogWarning($"[SaveRoot] TEST DATA ROOT: saves go to {p} ({src}).");
    }

    static string CommandLineValue(string flag)
    {
        try
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == null) continue;
                if (a.Length > flag.Length + 1 && a[flag.Length] == '=' && a.StartsWith(flag, StringComparison.OrdinalIgnoreCase))
                    return a.Substring(flag.Length + 1); // -dataroot=<dir>
                if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length &&
                    args[i + 1] != null && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                    return args[i + 1];                  // -dataroot <dir>
            }
        }
        catch (Exception) { /* no command line on this platform */ }
        return null;
    }

#if UNITY_EDITOR
    [Serializable]
    class DevSettings
    {
        public string dataRoot;
        public string owner;
        public string expiresUtc;
    }

    static bool TryDevSettings(out string root, out string owner)
    {
        root = owner = null;
        try
        {
            string file = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..", DevSettingsFile));
            if (!File.Exists(file)) return false;
            var s = JsonUtility.FromJson<DevSettings>(File.ReadAllText(file));
            if (s == null || string.IsNullOrWhiteSpace(s.dataRoot)) return false;
            if (!DateTime.TryParse(s.expiresUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime expires))
            {
                Debug.LogWarning($"[SaveRoot] {DevSettingsFile} has no valid expiresUtc; ignored (real saves in use).");
                return false;
            }
            DateTime now = DateTime.UtcNow;
            if (expires <= now)
            {
                Debug.Log($"[SaveRoot] {DevSettingsFile} expired at {expires:u}; using the real data root.");
                return false;
            }
            if (expires - now > MaxDevOverride + TimeSpan.FromMinutes(5))
            {
                Debug.LogWarning($"[SaveRoot] {DevSettingsFile} expires more than 2 h ahead ({expires:u}); ignored.");
                return false;
            }
            root = System.IO.Path.GetFullPath(s.dataRoot);
            owner = string.IsNullOrEmpty(s.owner) ? "?" : s.owner;
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SaveRoot] Couldn't read {DevSettingsFile}: {e.Message}. Using the real data root.");
            return false;
        }
    }
#endif

    static bool SamePath(string a, string b)
    {
        if (a == null || b == null) return false;
        string na = System.IO.Path.GetFullPath(a).TrimEnd('/', '\\');
        string nb = System.IO.Path.GetFullPath(b).TrimEnd('/', '\\');
        return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
    }

    // A new play session re-reads the sources (a test root switched on/off between sessions takes effect at once).
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetForPlaySession()
    {
        if (testStack.Count == 0) Refresh();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void ShowWatermark()
    {
        if (!IsOverridden || IsTestOverride || !Application.isPlaying || SaveRootWatermark.Instance != null) return;
        var go = new GameObject("[TEST DATA ROOT]") { hideFlags = HideFlags.HideInHierarchy };
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<SaveRootWatermark>();
    }
}

/// <summary>Draws "TEST DATA ROOT" while saves are redirected, so nobody mistakes a test session for real progress.</summary>
public sealed class SaveRootWatermark : MonoBehaviour
{
    internal static SaveRootWatermark Instance;
    GUIStyle style, small;

    void Awake() => Instance = this;
    void OnDestroy() { if (Instance == this) Instance = null; }

    void OnGUI()
    {
        if (!SaveRoot.IsOverridden) return;
        // UIPass H10: a 14 px line on the bottom edge, right-aligned (it used to sit top-centre, over the HUD's rank chip)
        if (style == null)
        {
            style = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.LowerRight, clipping = TextClipping.Clip, richText = true };
            style.normal.textColor = new Color(1f, 0.3f, 0.25f, 0.9f);
            small = new GUIStyle(GUI.skin.label) { fontSize = 11, alignment = TextAnchor.LowerRight, clipping = TextClipping.Clip };
            small.normal.textColor = new Color(1f, 0.6f, 0.55f, 0.8f);
        }
        GUI.depth = -1000;
        GUI.Label(new Rect(0, Screen.height - 18, Screen.width - 8, 16), "TEST DATA ROOT  <size=11>" + SaveRoot.Path + "</size>", style);
    }
}
