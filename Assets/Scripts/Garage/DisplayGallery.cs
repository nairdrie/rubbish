using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The garage's display line (Docs/Progression.md §6.8), on the GarageDisplays root built by Setup_Displays.
/// Keeps every <see cref="DisplayPiece"/> showing <see cref="DisplayPlanner.Plan"/> of the company's kept finds
/// (refreshed shortly after GameSession.Changed / UpgradeChanged), plays the arrival juice (a crowd "ooooh" for a
/// Legendary), and runs the local "look at it" placard: aim the camera at a find within reach and a card shows its
/// rarity, name, where and when it was found, its engraving or flavor, the appraisal and the Rep it earns.
/// Presentation only: the plan is a pure function of company state, Rep is paid by ProgressionRules at midnight
/// through UpgradeEffects.TrophyRepPerDay, and Arrange (pins) only writes on the host / solo.
/// </summary>
public class DisplayGallery : MonoBehaviour
{
    public DisplayPiece[] pieces = new DisplayPiece[0];
    [Tooltip("Seconds after a change before the displays refresh (batches inventory churn).")]
    public float refreshDelay = 0.35f;
    [Tooltip("Max distance (m) from the camera to a find you can read.")]
    public float gazeMaxCamera = 9f;
    [Tooltip("Max distance (m) from your character to a find you can read.")]
    public float gazeMaxPlayer = 5.5f;

    DisplayPlan plan;
    float refreshAt = -1f;
    bool shownOnce;
    DisplayPlacard placard;
    static readonly RaycastHit[] hits = new RaycastHit[16];

    public DisplayPlan CurrentPlan => plan;
    /// <summary>The slot the local camera is looking at (null piece = none).</summary>
    public DisplayPiece GazePiece { get; private set; }
    public int GazeSlot { get; private set; } = -1;

    void OnEnable()
    {
        GameSession.Changed += MarkDirty;
        GameSession.UpgradeChanged += OnUpgrade;
        refreshAt = Time.time;
    }

    void OnDisable()
    {
        GameSession.Changed -= MarkDirty;
        GameSession.UpgradeChanged -= OnUpgrade;
        if (placard != null) placard.Hide(true);
    }

    void OnUpgrade(string id, int level) => MarkDirty();
    void MarkDirty() { if (refreshAt < 0f) refreshAt = Time.time + refreshDelay; }

    /// <summary>A piece just switched on (bought): fill it on the next refresh.</summary>
    public void PieceEnabled(DisplayPiece p) => MarkDirty();

    void Update()
    {
        if (refreshAt >= 0f && Time.time >= refreshAt)
        {
            refreshAt = -1f;
            Refresh(shownOnce);
            shownOnce = true;
        }
        UpdateGaze();
    }

    // ------------------------------------------------------------------ refresh

    /// <summary>Re-plan and hand every piece its finds. <paramref name="celebrate"/> = juice the arrivals.</summary>
    public void Refresh(bool celebrate)
    {
        plan = DisplayPlanner.Plan(GameSession.Inventory);
        ApplyPlan(plan, celebrate);
    }

    /// <summary>Show a given plan (also used by edit-mode previews with sample finds).</summary>
    public void ApplyPlan(DisplayPlan p, bool celebrate)
    {
        plan = p;
        if (pieces == null || p == null) return;
        foreach (var piece in pieces)
        {
            if (piece == null) continue;
            p.byPiece.TryGetValue(piece.pieceKey, out var items);
            p.pinned.TryGetValue(piece.pieceKey, out var pins);
            var arrived = piece.Show(items, pins, celebrate);
            if (!celebrate || !Application.isPlaying) continue;
            foreach (var it in arrived)
                if (it.Def != null && it.Def.rarity == Rarity.Legendary)
                {
                    piece.Legendary();
                    StartCoroutine(Oooh(piece, it));
                    break;
                }
        }
    }

    /// <summary>A tiny crowd gasps when a Legendary goes on display.</summary>
    IEnumerator Oooh(DisplayPiece piece, ItemInstance it)
    {
        int slot = -1;
        for (int i = 0; i < piece.SlotCount; i++) if (piece.ShownAt(i) == it) { slot = i; break; }
        Vector3 at = slot >= 0 ? piece.SlotCenter(slot) : piece.transform.position;
        Vector3 side = piece.transform.right;
        Sfx.Play("legendary", at, 0.55f, 1.1f);
        string[] gasps = { "ooooh!", "oooooh...", "aaah!" };
        float[] offs = { -0.35f, 0.3f, 0f };
        for (int i = 0; i < gasps.Length; i++)
        {
            GameHUD.FloatingText(at + Vector3.up * (0.35f + 0.12f * i) + side * offs[i], gasps[i],
                                 i == 2 ? RarityInfo.Color(Rarity.Legendary) : new Color(1f, 0.95f, 0.8f), 1.1f - 0.1f * i);
            Sfx.Play("sparkle", at, 0.35f, 1.3f - 0.15f * i);
            yield return new WaitForSeconds(0.28f);
        }
    }

    // ------------------------------------------------------------------ gaze + placard (local presentation)

    void UpdateGaze()
    {
        DisplayPiece best = null;
        int bestSlot = -1;
        var cam = Camera.main;
        if (cam != null && plan != null && !PlayerController.IsUIBlocking && !SortingKit.LocalPlayerDriving)
        {
            Vector3 o = cam.transform.position, f = cam.transform.forward;
            bool hasPlayer = SortingKit.TryGetLocalPlayer(out Vector3 me);
            float bestScore = float.MaxValue;
            foreach (var piece in pieces)
            {
                if (piece == null || !piece.isActiveAndEnabled) continue;
                for (int i = 0; i < piece.SlotCount; i++)
                {
                    Vector3 c = piece.SlotCenter(i);
                    Vector3 d = c - o;
                    float along = Vector3.Dot(d, f);
                    if (along < 0.15f || along > gazeMaxCamera) continue;
                    if (hasPlayer && (c - me).magnitude > gazeMaxPlayer) continue;
                    float perp = (d - f * along).magnitude;
                    float tol = piece.gazeRadius + along * 0.015f;
                    if (perp > tol) continue;
                    // empty slots only count when nothing with a find is close to the aim
                    float score = perp / tol + along * 0.01f + (piece.ShownAt(i) == null ? 0.5f : 0f);
                    if (score < bestScore && Visible(o, c, piece)) { bestScore = score; best = piece; bestSlot = i; }
                }
            }
        }
        GazePiece = best;
        GazeSlot = bestSlot;
        if (best == null) { if (placard != null) placard.Hide(false); return; }
        if (placard == null) placard = DisplayPlacard.Create(transform);
        var item = best.ShownAt(bestSlot);
        placard.Show(PlacardText(best, bestSlot, item), item != null && item.Def != null ? RarityInfo.Color(item.Def.rarity) : new Color(0.6f, 0.6f, 0.65f),
                     best.SlotCenter(bestSlot), cam);
    }

    /// <summary>Nothing solid between the camera and the find (the piece itself, the player and triggers don't count).</summary>
    static bool Visible(Vector3 from, Vector3 to, DisplayPiece piece)
    {
        Vector3 d = to - from;
        float dist = d.magnitude;
        if (dist < 0.3f) return true;
        int n = Physics.RaycastNonAlloc(from, d / dist, hits, dist - 0.2f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var col = hits[i].collider;
            if (col == null) continue;
            if (col.transform.IsChildOf(piece.transform)) continue;
            if (col.GetComponentInParent<PlayerController>() != null) continue;
            return false;
        }
        return true;
    }

    string PlacardText(DisplayPiece piece, int slot, ItemInstance it)
    {
        var def = piece.Def;
        if (it == null || it.Def == null)
        {
            string why = def != null && def.minRarity >= Rarity.Epic
                ? "Epic and Legendary finds only.\nNo riff-raff."
                : "Keep a find (don't sell it) and it shows up here.";
            return $"<size=70%><color=#BBBBBB>EMPTY {(def != null ? def.title.ToUpperInvariant() : "SLOT")}</color></size>\n<size=60%>{why}</size>";
        }
        var r = it.Def.rarity;
        string col = SortingKit.Hex(RarityInfo.Color(r));
        var sb = new System.Text.StringBuilder();
        sb.Append($"<size=62%><color=#{col}>{Stars(r)} {RarityInfo.Label(r)} {Stars(r)}</color></size>\n");
        sb.Append($"<b>{it.Def.name}</b>\n");
        string where = string.IsNullOrEmpty(it.foundAt) ? "parts unknown" : it.foundAt;
        string day = it.dayFound > 0 ? $", day {it.dayFound}" : "";
        sb.Append($"<size=55%>Found at {where}{day}</size>\n");
        string note = !string.IsNullOrEmpty(it.note) ? $"\"{it.note}\"" : it.Def.flavor;
        if (!string.IsNullOrEmpty(note)) sb.Append($"<size=50%><i><color=#E8DCC0>{note}</color></i></size>\n");
        if (it.condition < 0.99f) sb.Append($"<size=45%><color=#BBBBBB>Condition {Mathf.RoundToInt(it.condition * 100f)}%</color></size>\n");
        float rep = DisplayPlanner.ShowcaseRepOf(def, it);
        string repTxt = rep > 0f ? $"   <color=#9FE08A>+{rep:0} Rep/night</color>" : "";
        sb.Append($"<size=55%>Appraised <color=#{SortingKit.Hex(SortingKit.Gold)}>{SortingKit.Appraisal(DisplayPlanner.ValueOf(it))}</color>{repTxt}</size>");
        if (def != null) sb.Append($"\n<size=42%><color=#AAAAAA>{def.title}{(plan != null && plan.IsPinned(def.key, slot) ? " (arranged by you)" : "")}</color></size>");
        string arrange = ArrangeHint(piece, slot, it);
        if (arrange != null) sb.Append($"\n<size=42%><color=#E8DCC0>{arrange}</color></size>");
        return sb.ToString();
    }

    /// <summary>The placard's Arrange line ("Swap find: Vinyl Record next"), or null when there's nothing to arrange or
    /// you're a guest. The prompt itself stays short (TooltipUI rows are one line tall).</summary>
    string ArrangeHint(DisplayPiece piece, int slot, ItemInstance current)
    {
        if (plan == null || piece == null || GameSession.IsGuest) return null;
        var def = piece.Def;
        if (!DisplayPlanner.CanArrange(plan, def, slot, current, GameSession.Inventory)) return null;
        var next = DisplayPlanner.NextFor(plan, def, slot, current, GameSession.Inventory);
        return next != null ? $"{SwapPrompt}: {next.Def.name} next" : $"{AutoPrompt}: best finds first";
    }

    const string SwapPrompt = "Swap find", AutoPrompt = "Back to auto";

    // ASCII only: the default TMP font has no star glyphs.
    static string Stars(Rarity r) => r >= Rarity.Legendary ? "**" : r >= Rarity.Epic ? "*" : "";

    // ------------------------------------------------------------------ Arrange (host / solo)

    public bool CanArrange(DisplayPiece piece)
    {
        if (piece == null || GazePiece != piece || GazeSlot < 0 || plan == null) return false;
        if (GameSession.IsGuest) return false;   // guests never mutate the company (Multiplayer.md rule 16)
        return DisplayPlanner.CanArrange(plan, piece.Def, GazeSlot, piece.ShownAt(GazeSlot), GameSession.Inventory);
    }

    public string ArrangePrompt(DisplayPiece piece)
    {
        if (piece == null || GazeSlot < 0 || plan == null) return SwapPrompt;
        // Short on purpose: the placard names the next find (ArrangeHint); a long prompt overflows the tooltip row.
        var next = DisplayPlanner.NextFor(plan, piece.Def, GazeSlot, piece.ShownAt(GazeSlot), GameSession.Inventory);
        return next != null ? SwapPrompt : AutoPrompt;
    }

    public void Arrange(DisplayPiece piece)
    {
        if (!CanArrange(piece)) return;
        string msg = DisplayPlanner.Arrange(piece.Def, GazeSlot, piece.ShownAt(GazeSlot));
        Sfx.Play("click", piece.SlotCenter(GazeSlot), 0.8f);
        GameHUD.Toast(msg, ToastStyle.Info, 2f);
        refreshAt = -1f;
        Refresh(true);
    }
}
