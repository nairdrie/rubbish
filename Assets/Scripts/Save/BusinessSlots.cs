using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// A player's businesses and the guest hand-back (Docs/Multiplayer.md §13.1, §13.10 #2, package N1).
/// <list type="bullet">
/// <item><b>Slots.</b> The <b>main</b> business is today's save pair (<c>rubbish_save.json</c> + <c>ebae_save.json</c>, which
/// now carry a business id and name). More businesses live in
/// <c>&lt;data root&gt;/accounts/&lt;player&gt;/businesses/&lt;id&gt;.json</c> (a <see cref="Blob"/>: the same GameSession and
/// ebag JSON, side by side). Every slot can be selected for solo, hosting or visiting a friend; no save promotion is needed.</item>
/// <item><b>Joining.</b> When the Town's <see cref="BusinessRegistry"/> reaches a guest, the guest picks one of its businesses
/// or starts a new one (<see cref="BusinessPicker"/>; bots: <c>-botbusiness main|new|&lt;id&gt;</c>). The pick is loaded into
/// GameSession (the world half is kept) and declared to the host, who owns its cash from then on.</item>
/// <item><b>Hand-back.</b> The guest's business is the guest's: <see cref="HandBack"/> writes it to the guest's OWN files
/// (the main pair through <see cref="LegacySave.WriteHandBack"/>, or its slot file) with the guest's own day, every
/// <see cref="CheckpointSeconds"/> and when the session ends (<see cref="SaveGuard.ReloadOwn"/> calls it first). The host never
/// writes it, and the guest never writes the host's Town.</item>
/// </list>
/// </summary>
public static partial class BusinessSlots
{
    /// <summary>One of the local player's businesses.</summary>
    public sealed class Slot
    {
        public string id, name;
        public long cashCents;
        /// <summary>The original legacy save pair; every slot can be played solo, hosted or brought to a friend.</summary>
        public bool isMain;
        /// <summary>The slot file (null for the main business).</summary>
        public string path;
        public DateTime savedUtc;
        public string Label => $"{name}  ·  {Economy.Money(cashCents / 100f)}";
    }

    /// <summary>A non-main business on disk.</summary>
    [Serializable]
    public sealed class Blob
    {
        public string kind = "business";
        public int schema = 1;
        public string id, name, savedUtc;
        public long cashCents;
        /// <summary>The GameSession save JSON (what <c>rubbish_save.json</c> holds).</summary>
        public string game;
        /// <summary>The ebag save JSON (what <c>ebae_save.json</c> holds).</summary>
        public string ebae;
    }

    public const string FolderName = "businesses";
    /// <summary>Real seconds between hand-back checkpoints while a guest (a crash loses at most this much).</summary>
    public const float CheckpointSeconds = 60f;
    /// <summary>Bot flag: which business a bot guest brings (main, new, or an id).</summary>
    public const string BotFlag = "-botbusiness";

    static Slot active;
    static bool declared, captured;
    static int preJoinDay = 1;
    static string mainGameJson, mainEbaeJson, mainId, mainName;
    static long mainCash;
    static Runner runner;

    /// <summary>The host accepted this game's business for the current Town.</summary>
    public static bool Declared => declared;
    /// <summary>The business brought into the current Town (null when hosting, solo, or not picked yet).</summary>
    public static Slot Active => active;
    /// <summary>The pick, the host's answer or a hand-back happened.</summary>
    public static event Action Changed;

    /// <summary>This player's slot folder (under the account folder of the current data root).</summary>
    public static string FolderPath => Path.Combine(AccountService.FolderFor(NetSession.LocalPlayerKey), FolderName);

    static string PathFor(string id) => Path.Combine(FolderPath, id + ".json");

    // ------------------------------------------------------------------ listing

    /// <summary>The main business first, then the slot files (newest first). Unreadable files are skipped.</summary>
    public static List<Slot> List()
    {
        var all = new List<Slot>();
        if (captured && BusinessIds.IsValid(mainId)) all.Add(new Slot { id = mainId, name = mainName, cashCents = mainCash, isMain = true });
        else if (SelectedId == null && !SaveGuard.Foreign && LegacySave.LoadedCleanly)
            all.Add(new Slot { id = GameSession.EnsureBusinessId(), name = GameSession.BusinessName, cashCents = GameSession.CashCents, isMain = true });
        else if (LegacySave.TryReadOriginalPair(out var originalGame, out _, out _))
        {
            var original = Summary(originalGame);
            if (original != null && BusinessIds.IsValid(original.businessId))
                all.Add(new Slot { id = original.businessId, name = BusinessColors.CleanName(original.businessName), cashCents = CashOf(original), isMain = true });
        }
        var files = new List<Slot>();
        try
        {
            string dir = FolderPath;
            if (Directory.Exists(dir))
            {
                var paths = new HashSet<string>(Directory.GetFiles(dir, "*.json"), StringComparer.Ordinal);
                // A lost main file can still have valid atomic backups; don't hide those businesses from the picker.
                foreach (string backupDir in Directory.GetDirectories(dir, "*.gen")) paths.Add(Path.ChangeExtension(backupDir, ".json"));
                foreach (string f in paths)
                {
                    var b = ReadBlob(f);
                    if (b == null || !BusinessIds.IsValid(b.id) || Path.GetFileNameWithoutExtension(f) != b.id || all.Exists(s => s.id == b.id)) continue;
                    DateTime.TryParse(b.savedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime t);
                    files.Add(new Slot { id = b.id, name = BusinessColors.CleanName(b.name), cashCents = b.cashCents, path = f, savedUtc = t });
                }
            }
        }
        catch (Exception e) { Debug.LogWarning($"[BusinessSlots] Couldn't list your businesses: {e.Message}"); }
        files.Sort((a, b) => b.savedUtc.CompareTo(a.savedUtc));
        all.AddRange(files);
        return all;
    }

    static Blob ReadBlob(string path)
    {
        if (!SafeFile.TryReadNewest(path, ParsesBlob, out byte[] data, out _)) return null;
        try { return JsonUtility.FromJson<Blob>(Encoding.UTF8.GetString(data)); }
        catch (Exception) { return null; }
    }

    static bool ParsesBlob(byte[] data)
    {
        try
        {
            var b = JsonUtility.FromJson<Blob>(Encoding.UTF8.GetString(data));
            return ValidBlob(b);
        }
        catch (Exception) { return false; }
    }

    // ------------------------------------------------------------------ joining

    /// <summary>SaveGuard latched for a join: capture the original slot separately from the selected solo/host business.</summary>
    internal static void OnMarkForeign()
    {
        try
        {
            preJoinDay = GameSession.Day;
            if (SelectedId == null)
            {
                mainId = GameSession.EnsureBusinessId(); mainName = GameSession.BusinessName; mainCash = GameSession.CashCents;
                mainGameJson = GameSession.BuildSaveJson(0); mainEbaeJson = EbaeMarket.BuildSaveJson(0);
            }
            else
            {
                LegacySave.TryReadOriginalPair(out mainGameJson, out mainEbaeJson, out _);
                var original = Summary(mainGameJson);
                mainId = original?.businessId; mainName = original?.businessName; mainCash = CashOf(original);
            }
            captured = true;
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            captured = false;
        }
        active = null;
        declared = false;
    }

    /// <summary>The Town's registry reached this guest: pick a business (bots and batchmode pick by flag, else the main one).</summary>
    internal static void OnJoinedTown(BusinessRegistry registry)
    {
        declared = false;
        // Not from inside the spawn callback: give the session a frame to settle first.
        if (NetBoot.Run(PickNextFrame()) == null) Pick();
    }

    static IEnumerator PickNextFrame()
    {
        yield return null;
        if (GameSession.IsConnectedGuest && !declared) Pick();
    }

    static void Pick()
    {
        string[] args = Environment.GetCommandLineArgs();
        // Bots (headless, or the Editor's MPPM autostart) never see the picker.
        if (CoreSmoke.TryGetArg(args, BotFlag, out string pick) || Application.isBatchMode || BotDriver.Instance != null)
        {
            ChooseByName(string.IsNullOrEmpty(pick) ? "main" : pick);
            return;
        }
        BusinessPicker.Show(null);
    }

    /// <summary>"main", "new", or a business id (bots, tools).</summary>
    public static void ChooseByName(string pick)
    {
        pick = (pick ?? "main").Trim().ToLowerInvariant();
        if (pick == "new") { ChooseNew(null); return; }
        foreach (var s in List())
            if ((pick == "main" && s.isMain) || s.id == pick) { Choose(s); return; }
        Debug.LogWarning($"[BusinessSlots] No business '{pick}'; bringing your main one.");
        var choices = List();
        if (choices.Count > 0) Choose(choices[0]); else ChooseNew(null);
    }

    /// <summary>Bring <paramref name="s"/> into this Town: load it into GameSession and declare it to the host.</summary>
    public static void Choose(Slot s)
    {
        if (s == null) return;
        if (!SaveGuard.Foreign || !GameSession.IsConnectedGuest) return;
        try
        {
            if (s.isMain)
            {
                if (!captured || mainId != s.id || string.IsNullOrEmpty(mainGameJson))
                {
                    BusinessPicker.Show("That original business could not be read. Pick another one."); return;
                }
                GameSession.LoadBusinessJson(mainGameJson);
                EbaeMarket.LoadBusinessJson(mainEbaeJson);
                preJoinDay = Mathf.Max(1, Summary(mainGameJson)?.day ?? 1);
            }
            else
            {
                var b = BusinessIds.IsValid(s.id) ? ReadBlob(PathFor(s.id)) : null;
                if (b == null || b.id != s.id) { BusinessPicker.Show($"Couldn't read {s.name}. Pick another one."); return; }
                GameSession.LoadBusinessJson(b.game);
                EbaeMarket.LoadBusinessJson(b.ebae);
                preJoinDay = Mathf.Max(1, Summary(b.game)?.day ?? 1);
            }
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            BusinessPicker.Show($"Couldn't open {s.name}. Pick another one.");
            return;
        }
        active = s;
        DailyStats.Reload();
        WorkOrders.Reload();
        Declare();
    }

    /// <summary>Start a brand-new business in this Town (saved as a new slot right away).</summary>
    public static void ChooseNew(string name)
    {
        if (!SaveGuard.Foreign || !GameSession.IsConnectedGuest) return;
        string id = BusinessIds.New();
        GameSession.BeginNewBusiness(id, string.IsNullOrEmpty(name) ? BusinessColors.DefaultBusinessName : name);
        EbaeMarket.LoadBusinessJson(null);
        DailyStats.Reload();
        WorkOrders.Reload();
        active = new Slot { id = id, name = GameSession.BusinessName, cashCents = GameSession.CashCents, path = PathFor(id) };
        preJoinDay = 1;
        if (!WriteBlob("new business")) { BusinessPicker.Show("Couldn't save the new business. Nothing else was overwritten."); return; }
        Declare();
    }

    static void Declare()
    {
        var r = Businesses.Registry;
        if (r == null || r.IsServer) return;
        Debug.Log($"[BusinessSlots] Bringing {GameSession.BusinessName} ({GameSession.BusinessId}, {Economy.Money(GameSession.Cash)}) into this Town.");
        r.Declare(GameSession.EnsureBusinessId(), GameSession.BusinessName, GameSession.CashCents, GameSession.TruckCapacityKg);
        RaiseChanged();
    }

    /// <summary>The host's answer to the declaration.</summary>
    internal static void OnDeclareAnswer(bool ok, string text)
    {
        declared = ok;
        if (ok)
        {
            NetAudit.Event("business", new Dictionary<string, object>
            {
                { "op", "brought" }, { "id", GameSession.BusinessId }, { "main", active != null && active.isMain }, { "cash", GameSession.CashCents },
            });
            GameHUD.Toast($"{GameSession.BusinessName} is open for business next door.", ToastStyle.Good, 3.5f);
            EnsureRunner();
        }
        else if (Application.isBatchMode) ChooseNew(null);
        else BusinessPicker.Show(text);
        RaiseChanged();
    }

    // ------------------------------------------------------------------ hand-back

    /// <summary>
    /// Write the business this guest brought back to its own files (never the host's), with the guest's own day. Called by
    /// <see cref="SaveGuard.ReloadOwn"/> before the statics are reset, and every <see cref="CheckpointSeconds"/>. A no-op when
    /// nothing was brought.
    /// </summary>
    internal static void HandBack(string why)
    {
        if (active == null || !declared) return;
        if (GameSession.BusinessId != active.id)
        {
            Debug.LogWarning($"[BusinessSlots] Hand-back skipped: the game holds {GameSession.BusinessId}, not {active.id}.");
            return;
        }
        bool ok = active.isMain
            ? LegacySave.WriteHandBack(g => GameSession.BuildSaveJson(g, preJoinDay), g => EbaeMarket.BuildSaveJson(g), why)
            : WriteBlob(why);
        NetAudit.Event("business", new Dictionary<string, object>
        {
            { "op", "handback" }, { "why", why }, { "id", active.id }, { "main", active.isMain }, { "cash", GameSession.CashCents }, { "ok", ok },
        });
        RaiseChanged();
    }

    static bool WriteBlob(string why)
    {
        if (active == null || active.isMain) return false;
        try
        {
            var b = new Blob
            {
                id = active.id, name = GameSession.BusinessName, cashCents = GameSession.CashCents,
                savedUtc = DateTime.UtcNow.ToString("o"),
                game = GameSession.BuildSaveJson(0, preJoinDay), ebae = EbaeMarket.BuildSaveJson(0),
            };
            string path = PathFor(active.id);
            bool ok = WriteSlotPair(active.id, b.game, b.ebae);
            Debug.Log($"[BusinessSlots] Saved {b.name} to {path} ({why}){(ok ? "" : ": FAILED")}.");
            return ok;
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            return false;
        }
    }

    /// <summary>The Town is behind us (SaveGuard reloaded the player's own save): nothing is brought any more.</summary>
    internal static void EndTown()
    {
        active = null;
        declared = false;
        captured = false;
        mainGameJson = mainEbaeJson = null;
        BusinessPicker.Hide();
        RaiseChanged();
    }

    static void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception e) { Debug.LogException(e); }
    }

    static void EnsureRunner()
    {
        if (runner != null || !Application.isPlaying) return;
        var go = new GameObject("[BusinessSlots]") { hideFlags = HideFlags.HideInHierarchy };
        UnityEngine.Object.DontDestroyOnLoad(go);
        runner = go.AddComponent<Runner>();
    }

    sealed class Runner : MonoBehaviour
    {
        float next;

        void Start() => next = Time.realtimeSinceStartup + CheckpointSeconds;

        void Update()
        {
            if (Time.realtimeSinceStartup < next) return;
            next = Time.realtimeSinceStartup + CheckpointSeconds;
            if (declared && GameSession.IsConnectedGuest) HandBack("checkpoint");
        }

        void OnDestroy()
        {
            if (runner == this) runner = null;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        active = null;
        declared = captured = false;
        preJoinDay = 1;
        mainGameJson = mainEbaeJson = mainId = mainName = null;
        mainCash = 0;
        runner = null;
        Changed = null;
    }
}
