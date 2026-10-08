public static partial class ItemCatalog
{
    /// <summary>
    /// Story items: things the game hands out on purpose, never the loot tables (weight 0, tag "story"). Appended after
    /// every other def, parts included (DailyStats saves a best find as an index into <see cref="All"/>), so this is
    /// always the last builder <c>Ensure</c> runs.
    /// </summary>
    static void BuildStoryDefs()
    {
        // The SolidWaste 3D floppy (Workshop/DiskItem.cs): a business's first sorted bags hide one, and KEEPing it puts
        // the print tab on its laptop. It never enters an inventory, so the value is only a number.
        Val(DiskItem.DefId, "Floppy Disk (SolidWaste 3D '97)", Rarity.Rare, 0.02f, 0.03f, 5f, 5f, "story_floppy", 0x2F6BD8, 0f, "story",
            "The label says DO NOT THROW AWAY. Somebody threw it away.");
    }
}
