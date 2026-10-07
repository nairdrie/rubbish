using System;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// <c>&lt;data root&gt;/settings.json</c>: the per-machine settings file (Docs/UIPass.md §6.4, D4; Accounts.md §4.1, §5.1). It
/// holds text only; <see cref="GameSettings"/> owns the model and keeps unknown fields. Written through
/// <see cref="SafeFile.WriteAtomic(string, byte[], DateTime, string)"/> (temp → rename, generations in <c>settings.gen/</c>), so a
/// crash mid-write never leaves a half file. Steam Auto-Cloud syncs <c>settings.json</c> only (Nick's dashboard step 16).
/// <para><b>Damaged file.</b> A main file that isn't a JSON object is copied to <c>settings.corrupt-&lt;utc&gt;.json</c> (the
/// original bytes are left alone until the next save), then the newest readable generation is used if there is one; else
/// <see cref="TryLoad"/> returns false and the game runs on defaults. <see cref="LastLoad"/> says which.</para>
/// <para><b>Never in edit mode on the real root.</b> Writes need play mode or a pushed test root
/// (<see cref="SaveRoot.PushTestOverride"/>), like every other save. The test data root redirects it automatically.</para>
/// </summary>
public static class SettingsStore
{
    public const string FileName = "settings.json";

    /// <summary>How the last <see cref="TryLoad"/> went.</summary>
    public enum LoadResult { NotLoaded, Missing, Main, RecoveredFromBackup, DamagedUsingDefaults }

    /// <summary>The last load's outcome (for the "Settings were reset" toast and tests).</summary>
    public static LoadResult LastLoad { get; private set; } = LoadResult.NotLoaded;

    /// <summary>Where the last damaged file was kept (<c>settings.corrupt-*.json</c>), or null.</summary>
    public static string LastQuarantine { get; private set; }

    /// <summary>Successful writes this process (tests).</summary>
    public static int Writes { get; private set; }

    /// <summary>Absolute path of <c>settings.json</c> under the current data root.</summary>
    public static string FilePath => Path.Combine(SaveRoot.Path, FileName);

    /// <summary>
    /// The settings JSON text: the main file if it's a JSON object, else the newest readable backup. False when there's no
    /// file (fresh install) or nothing readable (defaults). Reads never write, except the quarantine copy of a damaged file.
    /// </summary>
    public static bool TryLoad(out string json)
    {
        json = null;
        LastQuarantine = null;
        string path = FilePath;
        try
        {
            byte[] main = SafeFile.TryReadAll(path);
            bool mainDamaged = main != null && !Parses(main);
            // Keep the damaged bytes (a copy; the main file itself is only replaced by the next save). Edit-mode reads of the
            // real root never write, not even this copy.
            if (mainDamaged && (Application.isPlaying || SaveRoot.IsTestOverride))
                LastQuarantine = SafeFile.Quarantine(path, DateTime.UtcNow);

            if (SafeFile.TryReadNewest(path, Parses, out byte[] data, out string source))
            {
                json = SafeFile.Utf8(data);
                LastLoad = source == SafeFile.Main ? LoadResult.Main : LoadResult.RecoveredFromBackup;
                if (LastLoad == LoadResult.RecoveredFromBackup)
                    Debug.LogWarning($"[Settings] settings.json was unreadable; using the copy from {source}.");
                return true;
            }
            if (mainDamaged || SafeFile.AnyCopyExists(path))
            {
                LastLoad = LoadResult.DamagedUsingDefaults;
                Debug.LogWarning("[Settings] settings.json was unreadable and no backup parses; using defaults " +
                                 "(the damaged file was kept as settings.corrupt-*.json).");
                return false;
            }
            LastLoad = LoadResult.Missing;
            return false;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Settings] Couldn't read settings.json: {e.Message}. Using defaults.");
            LastLoad = LoadResult.DamagedUsingDefaults;
            return false;
        }
    }

    /// <summary>Replace <c>settings.json</c> with <paramref name="json"/> (atomic). False when it couldn't, or isn't allowed to, write.</summary>
    public static bool Write(string json)
    {
        if (json == null) return false;
        if (!Application.isPlaying && !SaveRoot.IsTestOverride)
        {
            Debug.LogWarning("[Settings] Not saving settings.json in edit mode without a test data root.");
            return false;
        }
        bool ok = SafeFile.WriteAtomic(FilePath, Encoding.UTF8.GetBytes(json), DateTime.UtcNow, SafeFile.ProcessSessionId);
        if (ok) Writes++;
        return ok;
    }

    /// <summary>A UTF-8 JSON object (what <see cref="GameSettings"/> can read).</summary>
    public static bool Parses(byte[] data)
    {
        string text = SafeFile.Utf8(data);
        if (string.IsNullOrWhiteSpace(text)) return false;
        try { return JToken.Parse(text) is JObject; }
        catch (Exception) { return false; }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        LastLoad = LoadResult.NotLoaded;
        LastQuarantine = null;
    }
}
