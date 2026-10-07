using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>One display piece in the garage (Docs/Progression.md §6.8): which upgrade owns it, how many finds it shows
/// and what it pays at midnight.</summary>
public sealed class DisplayPieceDef
{
    public string key;              // "pedestal", "vault", ... (DisplayPiece.pieceKey, pin counters)
    public string title;            // placard / prompt name
    public string upgradeId;        // ShopIds.*
    public int minLevel = 1;        // museum wing = trophy_shelf L2
    public int slots;
    public Rarity minRarity;        // the vault only takes Epic+
    public bool fancy;              // pays Showcase Rep for the Rare+ finds it shows
    public float showcaseMult = 1f; // the pedestal doubles it
    public float baseRep;           // per midnight while it shows at least one find (trophy shelf / museum: flat by level instead)

    public bool OwnedAt(Func<string, int> level) => (level ?? UpgradeEffects.Level)(upgradeId) >= minLevel;
    public bool Owned => OwnedAt(null);
    public bool Accepts(ItemInstance it) => it != null && it.Def != null && it.Def.rarity >= minRarity;
}

/// <summary>What the displays show right now: for each piece, one entry per slot (null = empty).</summary>
public sealed class DisplayPlan
{
    public readonly Dictionary<string, ItemInstance[]> byPiece = new Dictionary<string, ItemInstance[]>();
    public readonly Dictionary<string, bool[]> pinned = new Dictionary<string, bool[]>();
    /// <summary>Base Rep of the newer pieces that show something (the trophy shelf's flat Rep is not in here).</summary>
    public float baseRep;
    /// <summary>Showcase Rep for the Rare+ finds in the fancy pieces.</summary>
    public float showcaseRep;
    public int shownCount;

    public float ExtraRep => baseRep + showcaseRep;

    public ItemInstance At(string pieceKey, int slot) =>
        byPiece.TryGetValue(pieceKey, out var arr) && slot >= 0 && slot < arr.Length ? arr[slot] : null;

    public bool IsPinned(string pieceKey, int slot) =>
        pinned.TryGetValue(pieceKey, out var arr) && slot >= 0 && slot < arr.Length && arr[slot];

    /// <summary>Where an item is shown (piece key, slot), or false.</summary>
    public bool Find(string uid, out string pieceKey, out int slot)
    {
        foreach (var kv in byPiece)
            for (int i = 0; i < kv.Value.Length; i++)
                if (kv.Value[i] != null && kv.Value[i].uid == uid) { pieceKey = kv.Key; slot = i; return true; }
        pieceKey = null; slot = -1;
        return false;
    }
}

/// <summary>
/// The display line's rules (Docs/Progression.md §6.8), as pure functions of company state: the kept finds
/// (<see cref="GameSession.Inventory"/>), the upgrade levels and the "Arrange" pins (company counters). Every machine
/// computes the same plan from the same state, so the host decides nothing extra and guests just present it.
/// Best finds go in the best piece: rarity first, then value. Pinned slots (Arrange) keep their pick while it's still
/// in the inventory and allowed there. Selling or listing a find on ebae takes it out of the inventory, so it leaves
/// the display on the next refresh.
/// </summary>
public static class DisplayPlanner
{
    public const string Pedestal = "pedestal", Vault = "vault", GlassCase = "glass_case", TrophyShelfKey = "trophy_shelf",
                        MuseumWing = "museum_wing", PlankShelf = "plank_shelf";
    const string PinPrefix = "display_pin_";

    /// <summary>Every piece, best first (the order finds are handed out in).</summary>
    public static readonly DisplayPieceDef[] Pieces =
    {
        new DisplayPieceDef { key = Pedestal, title = "Pedestal of Honor", upgradeId = ShopIds.DisplayPedestal,
                              slots = UpgradeEffects.PedestalSlots, fancy = true, showcaseMult = UpgradeEffects.PedestalShowcaseMult,
                              baseRep = UpgradeEffects.PedestalRep },
        new DisplayPieceDef { key = Vault, title = "The Legendary Vault", upgradeId = ShopIds.DisplayVault,
                              slots = UpgradeEffects.VaultSlots, minRarity = UpgradeEffects.VaultMinRarity, fancy = true,
                              baseRep = UpgradeEffects.VaultRep },
        new DisplayPieceDef { key = GlassCase, title = "Lit Glass Case", upgradeId = ShopIds.DisplayGlassCase,
                              slots = UpgradeEffects.GlassCaseSlots, fancy = true, baseRep = UpgradeEffects.GlassCaseRep },
        new DisplayPieceDef { key = TrophyShelfKey, title = "Trophy Shelf", upgradeId = ShopIds.TrophyShelf,
                              slots = UpgradeEffects.TrophyShelfSlots },
        new DisplayPieceDef { key = MuseumWing, title = "Museum Wing", upgradeId = ShopIds.TrophyShelf, minLevel = 2,
                              slots = UpgradeEffects.MuseumWingSlots },
        new DisplayPieceDef { key = PlankShelf, title = "Wobbly Plank Shelf", upgradeId = ShopIds.DisplayPlankShelf,
                              slots = UpgradeEffects.PlankShelfSlots, baseRep = UpgradeEffects.PlankShelfRep },
    };

    public static DisplayPieceDef Get(string key)
    {
        foreach (var p in Pieces) if (p.key == key) return p;
        return null;
    }

    /// <summary>Total slots owned right now.</summary>
    public static int OwnedSlots(Func<string, int> level = null)
    {
        int n = 0;
        foreach (var p in Pieces) if (p.OwnedAt(level)) n += p.slots;
        return n;
    }

    // ------------------------------------------------------------------ ranking

    /// <summary>Display value of a find: ebae's appraisal (rolled value; curiosities a couple of bucks).</summary>
    public static float ValueOf(ItemInstance it) => it == null ? 0f : EbaeMarket.Appraise(it);

    /// <summary>Best first: rarity, then value, then uid (stable on every machine).</summary>
    public static int Compare(ItemInstance a, ItemInstance b)
    {
        int ra = a.Def != null ? (int)a.Def.rarity : -1, rb = b.Def != null ? (int)b.Def.rarity : -1;
        if (ra != rb) return rb.CompareTo(ra);
        int v = ValueOf(b).CompareTo(ValueOf(a));
        if (v != 0) return v;
        return string.CompareOrdinal(a.uid ?? "", b.uid ?? "");
    }

    /// <summary>The inventory's finds, best first (null / unknown defs skipped).</summary>
    public static List<ItemInstance> Ranked(IReadOnlyList<ItemInstance> inventory)
    {
        var list = new List<ItemInstance>();
        if (inventory != null)
            foreach (var it in inventory) if (it != null && it.Def != null && !string.IsNullOrEmpty(it.uid)) list.Add(it);
        list.Sort(Compare);
        return list;
    }

    // ------------------------------------------------------------------ pins (Arrange)

    public static string PinKey(string pieceKey, int slot) => PinPrefix + pieceKey + "_" + slot;

    /// <summary>Non-zero 32-bit tag of a find's uid (pins live in int counters).</summary>
    public static int PinTag(string uid)
    {
        int h = PickupVisuals.StableHash(uid ?? "");
        return h == 0 ? 1 : h;
    }

    // ------------------------------------------------------------------ the plan

    /// <summary>Who goes where. <paramref name="level"/> / <paramref name="counter"/> default to the company's
    /// upgrade levels and counters (tests pass their own).</summary>
    public static DisplayPlan Plan(IReadOnlyList<ItemInstance> inventory, Func<string, int> level = null, Func<string, int> counter = null)
    {
        level ??= UpgradeEffects.Level;
        counter ??= GameSession.GetCounter;
        var plan = new DisplayPlan();
        var ranked = Ranked(inventory);
        var used = new HashSet<string>();

        // 1. Pinned slots keep their pick (if it's still here and allowed there).
        foreach (var p in Pieces)
        {
            var arr = new ItemInstance[p.slots];
            var pin = new bool[p.slots];
            plan.byPiece[p.key] = arr;
            plan.pinned[p.key] = pin;
            if (!p.OwnedAt(level)) continue;
            for (int i = 0; i < p.slots; i++)
            {
                int tag = counter(PinKey(p.key, i));
                if (tag == 0) continue;
                foreach (var it in ranked)
                {
                    if (used.Contains(it.uid) || !p.Accepts(it) || PinTag(it.uid) != tag) continue;
                    arr[i] = it; pin[i] = true; used.Add(it.uid);
                    break;
                }
            }
        }
        // 2. Everything else best-first into the best piece that takes it.
        int next = 0;
        foreach (var p in Pieces)
        {
            if (!p.OwnedAt(level)) continue;
            var arr = plan.byPiece[p.key];
            for (int i = 0; i < arr.Length; i++)
            {
                if (arr[i] != null) continue;
                // the vault skips finds below its floor without using them up
                for (int k = next; k < ranked.Count; k++)
                {
                    var it = ranked[k];
                    if (used.Contains(it.uid) || !p.Accepts(it)) continue;
                    arr[i] = it; used.Add(it.uid);
                    if (k == next) next++;
                    break;
                }
                while (next < ranked.Count && used.Contains(ranked[next].uid)) next++;
            }
        }
        // 3. Rep.
        foreach (var p in Pieces)
        {
            if (!p.OwnedAt(level)) continue;
            bool any = false;
            foreach (var it in plan.byPiece[p.key])
            {
                if (it == null) continue;
                any = true;
                plan.shownCount++;
                if (p.fancy) plan.showcaseRep += UpgradeEffects.ShowcaseRep(it.Def.rarity) * p.showcaseMult;
            }
            if (any) plan.baseRep += p.baseRep;
        }
        return plan;
    }

    /// <summary>The displays' Rep per midnight on top of the trophy shelf's flat Rep (UpgradeEffects.TrophyRepPerDay).</summary>
    public static float ExtraRepPerDay() => Plan(GameSession.Inventory).ExtraRep;

    /// <summary>Showcase Rep a find earns where it stands now (placard line), or 0.</summary>
    public static float ShowcaseRepOf(DisplayPieceDef p, ItemInstance it) =>
        p != null && p.fancy && it != null && it.Def != null ? UpgradeEffects.ShowcaseRep(it.Def.rarity) * p.showcaseMult : 0f;

    /// <summary>
    /// Arrange: the next find this slot should show after <paramref name="current"/> (the find shown there now; null =
    /// empty), best first, skipping finds pinned to other slots; null = back to automatic. A pinned slot at the end of
    /// the list goes back to automatic; an automatic slot at the end starts over from the best find (finds keep their
    /// spot after a pin is cleared, so the last find of a cycle usually stays put, and the slot must stay arrangeable).
    /// Pure; <see cref="Arrange"/> writes the pin.
    /// </summary>
    public static ItemInstance NextFor(DisplayPlan plan, DisplayPieceDef p, int slot, ItemInstance current, IReadOnlyList<ItemInstance> inventory)
    {
        if (plan == null || p == null) return null;
        var candidates = new List<ItemInstance>();
        foreach (var it in Ranked(inventory))
        {
            if (!p.Accepts(it)) continue;
            if (plan.Find(it.uid, out string pk, out int s) && plan.IsPinned(pk, s) && !(pk == p.key && s == slot)) continue;
            candidates.Add(it);
        }
        int idx = current == null ? -1 : candidates.FindIndex(x => x.uid == current.uid);
        if (idx + 1 < candidates.Count) return candidates[idx + 1];
        if (!plan.IsPinned(p.key, slot) && candidates.Count > 0 && candidates[0].uid != current?.uid) return candidates[0];
        return null;
    }

    /// <summary>Is there anything to arrange in this slot (another find it could show, or a pin to clear)?</summary>
    public static bool CanArrange(DisplayPlan plan, DisplayPieceDef p, int slot, ItemInstance current, IReadOnlyList<ItemInstance> inventory)
    {
        if (plan == null || p == null) return false;
        if (plan.IsPinned(p.key, slot)) return true;   // can always go back to automatic
        return NextFor(plan, p, slot, current, inventory) != null;
    }

    /// <summary>Host / solo: pin the next find into this slot (or clear the pin). Returns the toast line.</summary>
    public static string Arrange(DisplayPieceDef p, int slot, ItemInstance current)
    {
        var plan = Plan(GameSession.Inventory);
        var next = NextFor(plan, p, slot, current, GameSession.Inventory);
        // A find pinned somewhere else that moves here would leave a stale pin behind: clear the old one.
        if (next != null && plan.Find(next.uid, out string pk, out int s) && plan.IsPinned(pk, s) && !(pk == p.key && s == slot))
            GameSession.SetCounter(PinKey(pk, s), 0);
        GameSession.SetCounter(PinKey(p.key, slot), next != null ? PinTag(next.uid) : 0);
        return next != null ? $"Arranged: {next.Def.name} on the {p.title}" : $"{p.title}: back to automatic (best finds first)";
    }
}
