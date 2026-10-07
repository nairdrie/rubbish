using System.Collections.Generic;

/// <summary>
/// Trophy Shelf (trophy_shelf L1, Docs/Progression.md §6.2 / §6.8): the 6-slot shelf over the sort station, now one
/// piece of the display line (<see cref="DisplayPiece"/>, key "trophy_shelf"; the L2 Museum Wing above it is its own
/// piece). Fancier displays get first pick of your finds; <see cref="DisplayGallery"/> fills it. Its Rep (+40 / +160 at
/// midnight) is unchanged and paid through UpgradeEffects.TrophyRepPerDay.
/// </summary>
public class TrophyShelf : DisplayPiece
{
    void Reset() => pieceKey = DisplayPlanner.TrophyShelfKey;

    /// <summary>The finds on the trophy shelf + museum wing right now (best first), capped at <paramref name="count"/>.
    /// Kept for old callers; the whole line is <see cref="DisplayPlanner.Plan"/>.</summary>
    public static List<ItemInstance> Picks(int count)
    {
        var plan = DisplayPlanner.Plan(GameSession.Inventory);
        var list = new List<ItemInstance>();
        foreach (var key in new[] { DisplayPlanner.TrophyShelfKey, DisplayPlanner.MuseumWing })
            if (plan.byPiece.TryGetValue(key, out var arr))
                foreach (var it in arr) if (it != null && list.Count < count) list.Add(it);
        return list;
    }
}
