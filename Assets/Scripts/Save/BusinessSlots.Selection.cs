using System;
using System.IO;
using System.Text;
using UnityEngine;

// All modes share the same business files. The original pair stays where it was; no destructive promotion/copy migration.
public static partial class BusinessSlots
{
    [Serializable] sealed class Selection { public string kind = "business-selection"; public int schema = 1; public string id; }
    [Serializable] sealed class SaveSummary { public string businessId, businessName; public long cashCents, gen; public float cash; public int saveVersion, day; }
    static string selectedId, selectionRoot, selectionError;
    static bool selectionLoaded;
    static string SelectionPath => Path.Combine(AccountService.FolderFor(NetSession.LocalPlayerKey), "selected-business.json");
    public static string SelectedId { get { CheckSelectionRoot(); return selectedId; } }
    public static string SelectionError => selectionError;
    public static string SelectedFile => SelectedId == null ? null : PathFor(SelectedId);
    public static bool CanPlaySelected => selectionError == null && LegacySave.LoadedCleanly;

    static void CheckSelectionRoot()
    {
        if (selectionRoot == SaveRoot.Path) return;
        selectedId = selectionError = null;
        selectionLoaded = false;
        selectionRoot = SaveRoot.Path;
    }

    internal static void ResetSelectionForPlaySession()
    {
        selectedId = selectionError = selectionRoot = null;
        selectionLoaded = false;
    }

    static SaveSummary Summary(string game)
    {
        try { return string.IsNullOrEmpty(game) ? null : JsonUtility.FromJson<SaveSummary>(game); }
        catch (Exception) { return null; }
    }
    static long CashOf(SaveSummary s) => s == null ? 0 : s.saveVersion >= 3 ? s.cashCents : GameSession.ToCents(s.cash);
    static bool ValidBlob(Blob b) => b != null && b.kind == "business" && b.schema == 1 && BusinessIds.IsValid(b.id) &&
        GameSession.BusinessJsonParses(b.game) && Summary(b.game)?.businessId == b.id &&
        (string.IsNullOrEmpty(b.ebae) || EbaeMarket.SaveJsonParses(b.ebae));

    /// <summary>Called after identity boot, in the main menu—not during GameSession's earlier subsystem reset.</summary>
    public static bool RestoreSelection(out string error)
    {
        CheckSelectionRoot();
        if (selectionLoaded) { error = selectionError; return error == null; }
        selectionLoaded = true;
        error = null;
        if (!SafeFile.TryReadNewest(SelectionPath, ValidSelection, out var data, out _))
        {
            if (SafeFile.Candidates(SelectionPath).Count > 0) error = "Your selected-business record couldn't be read. Choose a business; no save was overwritten.";
            selectionError = error;
            return error == null;
        }
        var selection = JsonUtility.FromJson<Selection>(Encoding.UTF8.GetString(data));
        selectedId = string.IsNullOrEmpty(selection.id) ? null : selection.id;
        if (selectedId != null)
        {
            LegacySave.DiscardPending();
            ReloadSelected();
            if (!LegacySave.LoadedCleanly) error = "The selected business couldn't be read. Choose another business; its files are untouched.";
        }
        selectionError = error;
        return error == null;
    }

    static bool ValidSelection(byte[] bytes)
    {
        try
        {
            var s = JsonUtility.FromJson<Selection>(Encoding.UTF8.GetString(bytes));
            return s != null && s.kind == "business-selection" && s.schema == 1 && (string.IsNullOrEmpty(s.id) || BusinessIds.IsValid(s.id));
        }
        catch (Exception) { return false; }
    }

    internal static bool ReadSelectedPair(out string game, out string ebae, out string source)
    {
        game = ebae = source = null;
        string id = SelectedId;
        if (id == null || !SafeFile.TryReadNewest(PathFor(id), ParsesBlob, out var bytes, out source)) return false;
        var b = JsonUtility.FromJson<Blob>(Encoding.UTF8.GetString(bytes));
        if (b.id != id) return false;
        game = b.game; ebae = string.IsNullOrEmpty(b.ebae) ? "{}" : b.ebae;
        return true;
    }

    internal static bool WriteSlotPair(string id, string game, string ebae)
    {
        if ((!Application.isPlaying && !SaveRoot.IsTestOverride) || !BusinessIds.IsValid(id)) return false;
        var summary = Summary(game);
        var b = new Blob { id = id, name = summary?.businessName, cashCents = CashOf(summary), game = game, ebae = ebae,
            savedUtc = DateTime.UtcNow.ToString("o") };
        if (!ValidBlob(b)) return false;
        return SafeFile.WriteAtomic(PathFor(id), Encoding.UTF8.GetBytes(JsonUtility.ToJson(b, true)), DateTime.UtcNow, LegacySave.SessionId);
    }

    /// <summary>Select for Continue, Solo and Host. Flush old progress before switching; only commit the pointer after a clean load.</summary>
    public static bool SelectForPlay(Slot slot, out string error)
    {
        error = null;
        if (slot == null || (!Application.isPlaying && !SaveRoot.IsTestOverride) || SaveGuard.Foreign || GameSession.IsConnectedGuest ||
            (!SaveRoot.IsTestOverride && (!NetSession.MenuOpen || NetSession.Busy)))
        { error = "Return to the main menu before changing business."; return false; }
        if (!BusinessIds.IsValid(slot.id)) { error = "That business id is invalid."; return false; }
        string next = slot.isMain ? null : slot.id;
        if (next != null)
        {
            var b = ReadBlob(PathFor(next));
            if (b == null || b.id != next) { error = "That business couldn't be read. Nothing was changed."; return false; }
        }
        else if (SelectedId != null && !LegacySave.TryReadOriginalPair(out _, out _, out _))
        { error = "The original business couldn't be read. Nothing was changed."; return false; }
        if (LegacySave.LoadedCleanly && selectionError == null && !LegacySave.FlushForBusinessSwitch())
        { error = "Your current business couldn't be saved. Please retry before switching."; return false; }
        string previous = SelectedId;
        selectedId = next;
        LegacySave.DiscardPending();
        ReloadSelected();
        bool ok = LegacySave.LoadedCleanly;
        if (ok)
        {
            var pick = new Selection { id = next };
            ok = SafeFile.WriteAtomic(SelectionPath, Encoding.UTF8.GetBytes(JsonUtility.ToJson(pick)), DateTime.UtcNow, LegacySave.SessionId);
        }
        if (!ok)
        {
            selectedId = previous;
            ReloadSelected();
            error = "Couldn't switch businesses safely. Your previous selection and all save files were kept.";
            return false;
        }
        selectionLoaded = true; selectionError = null;
        RaiseChanged();
        return true;
    }

    public static bool CreateForPlay(string name, out string error)
    {
        error = null;
        if ((!Application.isPlaying && !SaveRoot.IsTestOverride) || SaveGuard.Foreign ||
            (!SaveRoot.IsTestOverride && (!NetSession.MenuOpen || NetSession.Busy)))
        { error = "Return to the main menu before creating a business."; return false; }
        if (LegacySave.LoadedCleanly && selectionError == null && !LegacySave.FlushForBusinessSwitch())
        { error = "Your current business couldn't be saved. Please retry."; return false; }
        string priorGame = GameSession.BuildSaveJson(0), priorEbae = EbaeMarket.BuildSaveJson(0);
        string id = BusinessIds.New();
        GameSession.ResetAll(false);
        GameSession.BeginNewBusiness(id, name);
        EbaeMarket.LoadBusinessJson(null);
        bool saved = WriteSlotPair(id, GameSession.BuildSaveJson(0, 1), EbaeMarket.BuildSaveJson(0));
        GameSession.LoadBusinessJson(priorGame);
        EbaeMarket.LoadBusinessJson(priorEbae);
        // Reload before SelectForPlay's flush so the previous business's day/clock is restored too.
        ReloadSelected();
        if (!saved) { error = "Couldn't save a new business. Your existing business is unchanged."; return false; }
        return SelectForPlay(new Slot { id = id, name = BusinessColors.CleanName(name), path = PathFor(id) }, out error);
    }

    static void ReloadSelected()
    {
        LegacySave.PrepareForBusinessLoad();
        GameSession.ResetAll(false);
        GameSession.Load();
        EbaeMarket.Load();
        DailyStats.Reload(); WorkOrders.Reload();
        LegacySave.DiscardPending();
    }
}
