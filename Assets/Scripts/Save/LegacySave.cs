using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// The one writer and loader of today's two save files, <c>rubbish_save.json</c> (GameSession) and <c>ebae_save.json</c>
/// (EbaeMarket), until M1 folds them into a company record (hotfix L0; Docs/Accounts.md §5.1, red-team R1, R3, R4).
/// <list type="bullet">
/// <item><b>Writes:</b> <see cref="WriteBoth"/> writes both files with the same <c>gen</c>, at most once per 2 s (a dirty flag is
/// flushed by the runner), and on quit, pause and scene change unless the state equals what the main files already hold (ebae
/// offer timers aside). Atomic, with time-tiered generations (<see cref="SafeFile"/>).
/// Refused while <see cref="SaveGuard"/> is latched, when either file failed to load, and in edit mode (unless a test data
/// root is pushed).</item>
/// <item><b>Loads:</b> both files are resolved together. An unreadable main file is quarantined (<c>.corrupt-&lt;utc&gt;.json</c>);
/// the newest generation present in <b>both</b> files is loaded ("Recovered from a backup"); if a file exists but nothing of
/// it parses, every save is blocked for the session and the player is told. Never defaults over an unreadable file.</item>
/// </list>
/// Lives under <c>SaveRoot.Path</c>. GameSession/EbaeMarket call it; nothing else should.
/// </summary>
internal static class LegacySave
{
    public const string GameSessionFile = "rubbish_save.json";
    public const string EbaeFile = "ebae_save.json";
    /// <summary>Minimum seconds between pair writes (forced flushes ignore it).</summary>
    public const float MinInterval = 2f;

    public const string ToastRecovered = "Recovered your save from a backup.";
    public const string ToastUnreadable = "Your save couldn't be read. A copy is kept and nothing will be overwritten.";

    public enum Part { GameSession, Ebae }

    internal static string OriginalGamePath => System.IO.Path.Combine(SaveRoot.Path, GameSessionFile);
    internal static string OriginalEbaePath => System.IO.Path.Combine(SaveRoot.Path, EbaeFile);
    public static string GameSessionPath => BusinessSlots.SelectedFile ?? OriginalGamePath;
    public static string EbaePath => BusinessSlots.SelectedFile ?? OriginalEbaePath;

    // ---- state (reset per play session) ----
    static bool gsLoaded, ebaeLoaded;
    static string blockReason;
    static long gen;
    static string sessionId;
    static bool dirty;
    static float lastWriteAt = -999f;
    static int lastWriteFrame = -1;
    static Resolution stash;
    static bool hasStash;
    // W2: what the MAIN files hold right now, gen-free (and ebae offer timers ignored); null = unknown, so the next flush writes.
    // A quit/pause flush with nothing dirty and nothing changed skips the write, so a guest's files (and an idle quit) stay
    // byte-identical (M0 gate 4) instead of being rewritten under a new gen.
    static string canonGs, canonEbae;
    static readonly System.Text.RegularExpressions.Regex GenField =
        new System.Text.RegularExpressions.Regex(@"""gen""\s*:\s*-?\d+");
    static readonly System.Text.RegularExpressions.Regex TimerField =
        new System.Text.RegularExpressions.Regex(@"""remaining""\s*:\s*[-+0-9.eE]+");
    static readonly List<(string text, ToastStyle style)> toasts = new List<(string, ToastStyle)>();
    static Runner runner;

    /// <summary>Both files finished loading cleanly (good parse, recovered backup, or no file yet) and nothing is blocked.</summary>
    public static bool LoadedCleanly => gsLoaded && ebaeLoaded && blockReason == null;
    /// <summary>Why saving is blocked for this session (an unreadable save), or null.</summary>
    public static string BlockReason => blockReason;
    /// <summary>The gen of the pair on disk (last loaded or written).</summary>
    public static long Gen => gen;
    public static string SessionId => sessionId ??= SafeFile.NewSessionId(DateTime.UtcNow);
    public static bool Dirty => dirty;
    /// <summary>Pair writes this play session (tests).</summary>
    public static int Writes { get; private set; }
    /// <summary>What the last load did, in words (tests and logs).</summary>
    public static string LastLoadReport { get; private set; }
    /// <summary>Toasts waiting for the HUD (delivered by the runner a few seconds after the first scene).</summary>
    public static IReadOnlyList<(string text, ToastStyle style)> PendingToasts => toasts;

    // ------------------------------------------------------------------ write

    /// <summary>
    /// Save both files. <paramref name="force"/> = write now (quit, pause, scene change, reset); otherwise the request sets a
    /// dirty flag that the runner writes at the end of the frame, at most once per <see cref="MinInterval"/>. Never throws.
    /// </summary>
    public static void WriteBoth(bool force) => WriteBoth(force, force ? "flush" : "save");

    static void WriteBoth(bool force, string op)
    {
        if (!CanWriteNow(out string why))
        {
            dirty = false; // a refused request is dropped, never replayed after the latch clears
            if (why != null) SaveGuard.ReportBlocked(op);
            return;
        }
        if (!force)
        {
            // Always at the end of the frame (the runner), so changes made together (spend cash, then set the upgrade level)
            // land in one write; and at most once per MinInterval.
            dirty = true;
            EnsureRunner();
            return;
        }
        if (force && !dirty && lastWriteFrame == Time.frameCount) return; // already written this frame
        if (force && !dirty && Unchanged()) return; // quit/pause with nothing new: the files already hold this state
        WriteNow();
    }

    static string Canon(string json, bool ebae)
    {
        if (json == null) return null;
        string s = GenField.Replace(json.Trim(), "\"gen\":0");
        return ebae ? TimerField.Replace(s, "\"remaining\":0") : s;
    }

    /// <summary>Both main files already hold the current state (ignoring gen and ebae offer timers).</summary>
    static bool Unchanged()
    {
        if (canonGs == null || canonEbae == null) return false;
        try
        {
            return Canon(GameSession.BuildSaveJson(0), false) == canonGs && Canon(EbaeMarket.BuildSaveJson(0), true) == canonEbae;
        }
        catch (Exception) { return false; }
    }

    /// <summary>A reset (or other one-off change) must reach disk now, even if a write already happened this frame.</summary>
    public static void WriteChangeNow(string op)
    {
        dirty = true;
        WriteBoth(true, op);
    }

    /// <summary>Write a pending (dirty) change right away, e.g. just before SaveGuard latches.</summary>
    public static void FlushPending(string op)
    {
        if (dirty) WriteBoth(true, op);
    }

    /// <summary>Drop a pending write (its data may already be someone else's).</summary>
    public static void DiscardPending() => dirty = false;

    /// <summary>
    /// May a <c>ResetAll(true)</c> rewrite the files? Refused (and reported) while latched, before a clean load, or in edit mode.
    /// </summary>
    public static bool MayReset(string op)
    {
        if (CanWriteNow(out string why)) return true;
        Debug.LogWarning($"[Save] {op} refused: {why ?? "edit mode (no play session)"}. Nothing was changed.");
        if (why != null) SaveGuard.ReportBlocked(op);
        return false;
    }

    static bool CanWriteNow(out string why)
    {
        why = null;
        if (!Application.isPlaying && !SaveRoot.IsTestOverride) return false; // evals and editor tools never write saves
        if (!SaveGuard.WritesAllowed)
        {
            why = SaveGuard.Foreign ? "saving is paused (" + SaveGuard.ForeignReason + ")"
                : blockReason ?? (gsLoaded && ebaeLoaded ? "blocked" : null); // not loaded yet: silent
            return false;
        }
        return true;
    }

    static bool WriteNow()
    {
        long next = gen + 1;
        string a, b;
        try
        {
            a = GameSession.BuildSaveJson(next);
            b = EbaeMarket.BuildSaveJson(next);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            Debug.LogError("[Save] Couldn't build the save; nothing was written.");
            return false; // never write half a pair
        }

        DateTime nowUtc = DateTime.UtcNow;
        bool slot = BusinessSlots.SelectedId != null;
        bool okA = slot ? BusinessSlots.WriteSlotPair(BusinessSlots.SelectedId, a, b)
            : SafeFile.WriteAtomic(GameSessionPath, Encoding.UTF8.GetBytes(a), nowUtc, SessionId);
        bool okB = slot ? okA : okA && SafeFile.WriteAtomic(EbaePath, Encoding.UTF8.GetBytes(b), nowUtc, SessionId);
        gen = next;
        dirty = false;
        canonGs = okA ? Canon(a, false) : null;
        canonEbae = okA && okB ? Canon(b, true) : null;
        lastWriteAt = Time.realtimeSinceStartup;
        lastWriteFrame = Time.frameCount;
        Writes++;
        if (!okA || !okB) Debug.LogWarning($"[Save] Save gen {next} incomplete ({(okA ? EbaeFile : GameSessionFile)} failed); the next save retries.");
        return okA && okB;
    }

    internal static bool FlushForBusinessSwitch() => CanWriteNow(out _) && WriteNow();

    internal static void PrepareForBusinessLoad()
    {
        dirty = false; hasStash = false; stash = default;
        gsLoaded = ebaeLoaded = false; blockReason = null;
        canonGs = canonEbae = null; lastWriteFrame = -1;
    }

    /// <summary>
    /// Co-op hand-back (Docs/Multiplayer.md §13, N1): a guest's OWN business, played in someone else's Town, is written to the
    /// guest's own pair while <see cref="SaveGuard"/> is latched (the latch is about the host's world; this is the guest's
    /// business, built by <see cref="BusinessSlots"/> with the guest's own day). Same atomic write and generations as
    /// <see cref="WriteBoth"/>; refused when the pair didn't load cleanly this session, and in edit mode.
    /// </summary>
    internal static bool WriteHandBack(Func<long, string> buildGameSession, Func<long, string> buildEbae, string why)
    {
        if (!Application.isPlaying && !SaveRoot.IsTestOverride) return false;
        if (!(gsLoaded && ebaeLoaded && blockReason == null))
        {
            Debug.LogWarning($"[Save] Hand-back ({why}) refused: {blockReason ?? "your save didn't load cleanly"}.");
            return false;
        }
        // This API hands the ORIGINAL business back, even when another slot is selected for solo/hosting.
        if (!TryReadOriginalPair(out _, out _, out long originalGen)) return false;
        long next = Math.Max(gen, originalGen) + 1;
        string a, b;
        try
        {
            a = buildGameSession(next);
            b = buildEbae(next);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            return false;
        }
        DateTime nowUtc = DateTime.UtcNow;
        bool okA = SafeFile.WriteAtomic(OriginalGamePath, Encoding.UTF8.GetBytes(a), nowUtc, SessionId);
        bool okB = okA && SafeFile.WriteAtomic(OriginalEbaePath, Encoding.UTF8.GetBytes(b), nowUtc, SessionId);
        gen = next;
        dirty = false;
        canonGs = canonEbae = null;
        lastWriteAt = Time.realtimeSinceStartup;
        lastWriteFrame = Time.frameCount;
        Writes++;
        Debug.Log($"[Save] Handed your business back to your own save (gen {next}, {why}){(okA && okB ? "" : ": INCOMPLETE")}.");
        NetAudit.Event("save", $"hand-back gen {next} ({why}) ok={okA && okB}");
        return okA && okB;
    }

    // ------------------------------------------------------------------ load

    /// <summary>
    /// Load one half of the pair. <see cref="Part.GameSession"/> always resolves the pair afresh from disk and keeps the ebae half
    /// for <see cref="Part.Ebae"/> (which resolves afresh only when called on its own). <paramref name="apply"/> gets the chosen
    /// JSON (null-free; not called when there's no file). Returns true when that half loaded cleanly.
    /// </summary>
    public static bool Load(Part part, Action<string> apply)
    {
        Resolution r;
        if (part == Part.Ebae && hasStash) r = stash;
        else r = ResolvePair();
        hasStash = part == Part.GameSession;
        stash = r;

        bool ok = part == Part.GameSession ? r.gsOk : r.ebaeOk;
        string text = part == Part.GameSession ? r.gsText : r.ebaeText;
        bool applied = false;
        if (text != null && apply != null)
        {
            try { apply(text); applied = true; }
            catch (Exception e)
            {
                Debug.LogException(e);
                ok = false;
                Block($"{(part == Part.GameSession ? GameSessionFile : EbaeFile)} parsed but couldn't be applied ({e.GetType().Name})");
            }
        }
        if (part == Part.GameSession) gsLoaded = ok;
        else ebaeLoaded = ok;
        // W2: remember the state the main file holds (only when it was loaded from the main file itself; a recovered or missing
        // file must be written at the next flush).
        string canon = null;
        if (ok && applied && (part == Part.GameSession ? r.gsMain : r.ebaeMain))
        {
            try { canon = part == Part.GameSession ? Canon(GameSession.BuildSaveJson(0), false) : Canon(EbaeMarket.BuildSaveJson(0), true); }
            catch (Exception) { canon = null; }
        }
        if (part == Part.GameSession) canonGs = canon;
        else canonEbae = canon;
        return ok;
    }

    struct Cand { public string source, text; public long gen; }
    struct Scan { public string path, file; public bool anyCopy, mainBad; public List<Cand> good; }
    struct Resolution { public string gsText, ebaeText; public bool gsOk, ebaeOk, gsMain, ebaeMain; }

    [Serializable] class GenProbe { public long gen; }

    static Resolution ResolvePair()
    {
        if (BusinessSlots.SelectedId != null)
        {
            bool ok = BusinessSlots.ReadSelectedPair(out string game, out string ebae, out string source);
            blockReason = null;
            gen = ok && TryParse(game, GameSession.SaveJsonParses, out long savedGen) ? savedGen : 0;
            if (!ok) Block("the selected business is missing, mismatched or unreadable; choose another save");
            LastLoadReport = ok ? "selected business: " + source : "selected business unreadable; saving BLOCKED";
            if (ok && source != SafeFile.Main) Toast(ToastRecovered, ToastStyle.Info);
            return new Resolution { gsText = game, ebaeText = ebae, gsOk = ok, ebaeOk = ok,
                gsMain = ok && source == SafeFile.Main, ebaeMain = ok && source == SafeFile.Main };
        }
        DateTime nowUtc = DateTime.UtcNow;
        var a = ScanFile(GameSessionPath, GameSessionFile, GameSession.SaveJsonParses);
        var b = ScanFile(EbaePath, EbaeFile, EbaeMarket.SaveJsonParses);
        var report = new StringBuilder();

        foreach (var s in new[] { a, b })
            if (s.mainBad)
            {
                string copy = SafeFile.Quarantine(s.path, nowUtc);
                Debug.LogWarning($"[Save] {s.file} couldn't be read; kept a copy as {(copy == null ? "(copy failed)" : System.IO.Path.GetFileName(copy))}.");
                report.Append($"{s.file}: unreadable, quarantined. ");
            }

        bool aDead = a.anyCopy && a.good.Count == 0, bDead = b.anyCopy && b.good.Count == 0;
        Cand? ca = null, cb = null;
        if (a.good.Count > 0 && b.good.Count > 0)
        {
            // The newest generation present in BOTH files (a crash between the two writes leaves them one gen apart).
            long best = long.MinValue;
            foreach (var x in a.good)
                foreach (var y in b.good)
                    if (x.gen == y.gen && x.gen > best) best = x.gen;
            if (best != long.MinValue) { ca = FirstWithGen(a.good, best); cb = FirstWithGen(b.good, best); }
            else
            {
                ca = a.good[0];
                cb = b.good[0];
                Debug.LogWarning($"[Save] No backup generation is in both files (gens {ca.Value.gen} / {cb.Value.gen}); loading each file's newest copy.");
            }
        }
        else
        {
            if (a.good.Count > 0) ca = a.good[0];
            if (b.good.Count > 0) cb = b.good[0];
        }

        // Next write uses a gen above everything on disk, so an old copy can never share a gen with new data.
        long maxGen = 0;
        foreach (var x in a.good) maxGen = Math.Max(maxGen, x.gen);
        foreach (var y in b.good) maxGen = Math.Max(maxGen, y.gen);
        gen = maxGen;

        var r = new Resolution
        {
            gsText = ca?.text, ebaeText = cb?.text,
            gsOk = !aDead && !bDead, ebaeOk = !aDead && !bDead, // a pair: one unreadable file blocks both
            gsMain = ca.HasValue && ca.Value.source == SafeFile.Main,
            ebaeMain = cb.HasValue && cb.Value.source == SafeFile.Main,
        };

        blockReason = null;
        if (aDead || bDead)
        {
            string which = aDead && bDead ? $"{GameSessionFile} and {EbaeFile}" : aDead ? GameSessionFile : EbaeFile;
            Block($"{which} can't be read and has no readable backup");
            Toast(ToastUnreadable, ToastStyle.Bad);
        }
        else if ((ca.HasValue && ca.Value.source != SafeFile.Main) || (cb.HasValue && cb.Value.source != SafeFile.Main))
        {
            Debug.LogWarning($"[Save] Recovered from a backup: {GameSessionFile} ← {ca?.source ?? "none"}, {EbaeFile} ← {cb?.source ?? "none"} (gen {ca?.gen ?? cb?.gen ?? 0}).");
            Toast(ToastRecovered, ToastStyle.Info);
        }

        report.Append($"{GameSessionFile}: {(aDead ? "UNREADABLE" : ca.HasValue ? ca.Value.source + " gen " + ca.Value.gen : "none (fresh)")}; ");
        report.Append($"{EbaeFile}: {(bDead ? "UNREADABLE" : cb.HasValue ? cb.Value.source + " gen " + cb.Value.gen : "none (fresh)")}; ");
        report.Append(blockReason != null ? "saving BLOCKED" : "saving allowed");
        LastLoadReport = report.ToString();
        return r;
    }

    /// <summary>Read-only original-save discovery for the all-mode picker; never changes the current save or repairs files.</summary>
    internal static bool TryReadOriginalPair(out string game, out string ebae, out long maxGen)
    {
        game = ebae = null; maxGen = 0;
        var a = ScanFile(OriginalGamePath, GameSessionFile, GameSession.SaveJsonParses);
        var b = ScanFile(OriginalEbaePath, EbaeFile, EbaeMarket.SaveJsonParses);
        if ((a.anyCopy && a.good.Count == 0) || (b.anyCopy && b.good.Count == 0)) return false;
        foreach (var x in a.good) maxGen = Math.Max(maxGen, x.gen);
        foreach (var x in b.good) maxGen = Math.Max(maxGen, x.gen);
        long shared = long.MinValue;
        foreach (var x in a.good) foreach (var y in b.good) if (x.gen == y.gen) shared = Math.Max(shared, x.gen);
        game = a.good.Count == 0 ? null : shared != long.MinValue ? FirstWithGen(a.good, shared).text : a.good[0].text;
        ebae = b.good.Count == 0 ? null : shared != long.MinValue ? FirstWithGen(b.good, shared).text : b.good[0].text;
        return true;
    }

    static Cand FirstWithGen(List<Cand> list, long g)
    {
        foreach (var c in list) if (c.gen == g) return c;
        return list[0];
    }

    static Scan ScanFile(string path, string file, Func<string, bool> parses)
    {
        var s = new Scan { path = path, file = file, good = new List<Cand>() };
        foreach (var c in SafeFile.Candidates(path))
        {
            s.anyCopy = true;
            string text = SafeFile.Utf8(SafeFile.TryReadAll(c.file));
            bool ok = TryParse(text, parses, out long g);
            if (c.source == SafeFile.Main && !ok) s.mainBad = true;
            if (ok) s.good.Add(new Cand { source = c.source, text = text.Trim(), gen = g });
        }
        return s;
    }

    static bool TryParse(string text, Func<string, bool> parses, out long g)
    {
        g = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim();
        if (t.Length < 2 || t[0] != '{' || t[t.Length - 1] != '}') return false;
        try
        {
            var probe = JsonUtility.FromJson<GenProbe>(t);
            if (probe == null) return false;
            g = probe.gen;
            return parses == null || parses(t);
        }
        catch (Exception) { return false; }
    }

    static void Block(string reason)
    {
        if (blockReason == null) blockReason = reason;
        Debug.LogError($"[Save] Saving is OFF this session: {reason}. The file is kept as it is (and copied to *.corrupt-*.json); nothing will be overwritten.");
        NetAudit.Event("save", "blocked: " + reason);
    }

    static void Toast(string text, ToastStyle style)
    {
        foreach (var t in toasts) if (t.text == text) return;
        toasts.Add((text, style));
    }

    // ------------------------------------------------------------------ session lifecycle

    /// <summary>Start of a play session: GameSession.ResetStatics calls this before its first Load.</summary>
    public static void ResetForPlaySession()
    {
        SaveRoot.Refresh();
        BusinessSlots.ResetSelectionForPlaySession();
        SaveGuard.ResetForPlaySession();
        gsLoaded = ebaeLoaded = false;
        blockReason = null;
        gen = 0;
        sessionId = SafeFile.NewSessionId(DateTime.UtcNow);
        dirty = false;
        lastWriteAt = -999f;
        lastWriteFrame = -1;
        hasStash = false;
        stash = default;
        canonGs = canonEbae = null;
        toasts.Clear();
        Writes = 0;
        LastLoadReport = null;
        runner = null;
    }

    /// <summary>The hidden runner: flushes the dirty flag, saves on pause/quit/scene change, delivers load toasts.</summary>
    public static void EnsureRunner()
    {
        if (runner != null || !Application.isPlaying) return;
        var go = new GameObject("[LegacySave]") { hideFlags = HideFlags.HideInHierarchy };
        UnityEngine.Object.DontDestroyOnLoad(go);
        runner = go.AddComponent<Runner>();
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
        SceneManager.activeSceneChanged += OnActiveSceneChanged;
    }

    static void OnActiveSceneChanged(Scene from, Scene to)
    {
        if (dirty) WriteBoth(true, "scene change");
    }

    /// <summary>The runner's pause/quit path (also used by tests): writes when allowed and something changed since the files
    /// were loaded or written (W2: an unchanged state is not rewritten under a new gen).</summary>
    internal static void FlushOn(string op) => WriteBoth(true, op);

    sealed class Runner : MonoBehaviour
    {
        float toastAt;

        void Start() => toastAt = Time.realtimeSinceStartup + 3f;

        void LateUpdate()
        {
            if (dirty && Time.realtimeSinceStartup - lastWriteAt >= MinInterval) WriteBoth(true, "save");
            if (toasts.Count > 0 && Time.realtimeSinceStartup >= toastAt)
            {
                foreach (var (text, style) in toasts) GameHUD.Toast(text, style, 6f);
                toasts.Clear();
            }
        }

        void OnApplicationPause(bool paused)
        {
            if (paused) FlushOn("pause");
        }

        void OnApplicationQuit() => FlushOn("quit");

        void OnDestroy()
        {
            if (runner == this) runner = null;
        }
    }
}
