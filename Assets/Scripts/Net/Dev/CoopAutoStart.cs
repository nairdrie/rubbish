#if UNITY_EDITOR
using System;
using System.Globalization;
using System.IO;
using Unity.Multiplayer.PlayMode;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Multiplayer Play Mode autostart (Docs/Multiplayer.md §8.7.4 package B5, §8.7.6 MPPM2). Editor only.
/// <para>
/// MPPM 2.0 needs no tags or scenario assets for this: <see cref="CurrentPlayer.IsMainEditor"/> tells the main Editor from
/// its virtual players. With the main project's <c>UserSettings/RubbishDev.json</c> armed for autostart
/// (<c>Tools/dev/test_data_root.sh on --autohost</c>: a test data root plus <c>"autoHost": true</c>, expiring within 2 h; never
/// EditorPrefs), entering play mode with Player 2 active does this:
/// </para>
/// <list type="bullet">
/// <item><b>Main Editor</b>: hosts on 127.0.0.1:7777 (LocalDirect) through <see cref="BotDriver"/>, idle (you drive).</item>
/// <item><b>Each virtual player</b>: joins 127.0.0.1:7777 (retrying while the host boots) and walks around on its own (AutoWalk).</item>
/// </list>
/// Not armed, nothing happens: MPPM behaves exactly as before. MPPM player tags still work if someone sets them up
/// (Window ▸ Multiplayer Play Mode): <c>AutoHost</c> (main), <c>AutoJoin</c> and <c>AutoWalk</c> (players) override the defaults.
/// <para>
/// Data safety: a virtual player shares the main project's ProjectSettings, so it would otherwise save into Nick's real
/// <c>persistentDataPath</c>. Before every play session a virtual player points its own <c>UserSettings/RubbishDev.json</c> at
/// a throwaway root, <c>&lt;project&gt;/Logs/TestDataRoots/mppm-&lt;clone&gt;</c> (watermarked "TEST DATA ROOT"); a file someone
/// else wrote there is left alone. The main Editor autohosts only while its own test data root is active.
/// </para>
/// Probe either side with <c>Tools/bots/eval/mppm_probe.cs</c> (NetAudit snapshot, roster, players, hash compares).
/// </summary>
public static class CoopAutoStart
{
    public const string TagAutoHost = "AutoHost", TagAutoJoin = "AutoJoin", TagAutoWalk = "AutoWalk";
    public const string CloneOwner = "mppm-autostart";
    const string DevSettingsFile = "UserSettings/RubbishDev.json";

    [Serializable]
    class DevSettings
    {
        public string dataRoot;
        public string owner;
        public string expiresUtc;
        public bool autoHost;
    }

    /// <summary>"main" / "player" and what autostart decided this play session (for probes).</summary>
    public static string Decision { get; private set; } = "not run";

    static bool IsMain
    {
        get
        {
            try { return CurrentPlayer.IsMainEditor; }
            catch (Exception) { return true; } // MPPM off or unavailable: this is the only editor
        }
    }

    static string[] Tags
    {
        get
        {
            try
            {
                var tags = CurrentPlayer.Tags;
                if (tags == null) return Array.Empty<string>();
                var copy = new string[tags.Count];
                for (int i = 0; i < copy.Length; i++) copy[i] = tags[i];
                return copy;
            }
            catch (Exception) { return Array.Empty<string>(); }
        }
    }

    /// <summary>This editor's project folder (a virtual player's is <c>&lt;main&gt;/Library/VP/&lt;clone&gt;</c>).</summary>
    static string OwnProject => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

    /// <summary>The main project folder, also from inside a virtual player.</summary>
    public static string MainProject
    {
        get
        {
            string own = OwnProject;
            string vp = Path.GetDirectoryName(own);                        // <main>/Library/VP
            if (vp != null && Path.GetFileName(vp) == "VP" && Path.GetFileName(Path.GetDirectoryName(vp)) == "Library")
                return Path.GetDirectoryName(Path.GetDirectoryName(vp));
            return own;
        }
    }

    static bool Has(string[] tags, string tag) => Array.Exists(tags, t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ play mode: decide and launch

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoStart()
    {
        bool main = IsMain;
        string[] tags = Tags;
        var dev = ReadDev(Path.Combine(MainProject, DevSettingsFile), out string why);
        bool armed = dev != null && dev.autoHost;
        if (main)
        {
            if (!armed && !Has(tags, TagAutoHost)) { Decision = "main: not armed"; return; }
            if (!SaveRoot.IsOverridden)
            {
                Decision = "main: armed but no test data root";
                Debug.LogWarning("[CoopAutoStart] Autohost is armed but this session uses your real saves; not hosting. " +
                                 "Run Tools/dev/test_data_root.sh on --autohost before entering play mode. " + (why ?? ""));
                return;
            }
            Decision = "main: host";
            BotDriver.Launch(new BotOptions
            {
                host = true, port = NetConstants.DefaultPort, minutes = 0f, brain = Has(tags, TagAutoWalk) ? "walk" : "idle",
                quitWhenDone = false, label = "mppm-main",
            });
            return;
        }

        bool join = Has(tags, TagAutoJoin) || (tags.Length == 0 && armed);
        if (!join) { Decision = "player: not armed"; return; }
        Decision = "player: join";
        BotDriver.Launch(new BotOptions
        {
            join = true, address = "127.0.0.1", port = NetConstants.DefaultPort, minutes = 0f,
            brain = tags.Length == 0 || Has(tags, TagAutoWalk) ? "walk" : "idle", joinPatienceSeconds = 120f, quitWhenDone = false,
            label = "mppm-" + Path.GetFileName(OwnProject),
        });
    }

    static DevSettings ReadDev(string file, out string why)
    {
        why = null;
        try
        {
            if (!File.Exists(file)) { why = $"({file} not found)"; return null; }
            var s = JsonUtility.FromJson<DevSettings>(File.ReadAllText(file));
            if (s == null || string.IsNullOrWhiteSpace(s.dataRoot)) { why = "(no dataRoot)"; return null; }
            if (!DateTime.TryParse(s.expiresUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime expires))
            { why = "(no valid expiresUtc)"; return null; }
            DateTime now = DateTime.UtcNow;
            if (expires <= now) { why = $"(expired {expires:u})"; return null; }
            if (expires - now > SaveRoot.MaxDevOverride + TimeSpan.FromMinutes(5)) { why = "(expires more than 2 h ahead)"; return null; }
            return s;
        }
        catch (Exception e)
        {
            why = $"({e.Message})";
            return null;
        }
    }

    // ------------------------------------------------------------------ edit mode: keep virtual players off the real saves

    [InitializeOnLoadMethod]
    static void EditorHooks()
    {
        if (IsMain) return;
        EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
    }

    static void OnPlayModeChanged(PlayModeStateChange change)
    {
        if (change == PlayModeStateChange.ExitingEditMode) PointPlayerAtTestRoot();
    }

    /// <summary>Virtual players only: (re)write this clone's RubbishDev.json to its own throwaway data root, valid 2 h.</summary>
    static void PointPlayerAtTestRoot()
    {
        if (IsMain) return;
        string own = OwnProject;
        if (own == MainProject) return; // not inside Library/VP: never touch a real project's settings
        string file = Path.Combine(own, DevSettingsFile);
        try
        {
            var current = ReadDev(file, out _);
            if (current != null && current.owner != CloneOwner) return; // someone set a root here on purpose
            string clone = Path.GetFileName(own);
            string root = Path.Combine(MainProject, "Logs", "TestDataRoots", "mppm-" + clone);
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            var s = new DevSettings
            {
                dataRoot = root, owner = CloneOwner, autoHost = false,
                expiresUtc = DateTime.UtcNow.Add(SaveRoot.MaxDevOverride).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            };
            File.WriteAllText(file, JsonUtility.ToJson(s, true)); // coop:allow(save) per-clone dev settings (UserSettings/RubbishDev.json) in Library/VP, Editor only; never game data
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[CoopAutoStart] Couldn't point this virtual player at a test data root ({e.Message}); " +
                             "it may share the real save folder this session.");
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Decision = "not run";
}
#endif
