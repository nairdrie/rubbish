/// <summary>
/// The chrome of SolidWaste 3D, the print app on the shop laptop (SolidWasteApp), and the notes that point at it. The
/// software's own name is <see cref="SolidWaste.Name"/>. Blueprint names, level titles, effect lines and how-to-use lines
/// are data and live with the tree in <see cref="CallingCatalog"/>; recipes in <see cref="CallingRecipes"/>; the Monument
/// in <see cref="MonumentCatalog"/>. (In code the blueprint tree is still called "Callings".)
/// </summary>
public static partial class UiText
{
    // ---- the sheet: banners and what a hexagon says (always a shape AND a word, never colour alone)
    public const string PrintBasicsBanner = "BASICS";
    public const string PrintKitWord = "KIT";
    public const string PrintMonumentBanner = "THE MONUMENT";
    public const string PrintMonumentTagline = "A trophy. Ten tiers. No refunds.";
    public const string PrintTagPrinted = "PRINTED";
    public const string PrintTagReady = "PRINT!";
    /// <summary>{0} = how many items the next level is still short of.</summary>
    public const string PrintTagNeed = "NEED {0}";
    public const string PrintTagSoon = "SOON";
    public const string PrintLegendPrinted = "printed";
    public const string PrintLegendReady = "can print now";
    public const string PrintLegendNeed = "needs parts";
    public const string PrintLegendLocked = "locked";

    // ---- inspector: a blueprint
    public const string PrintCapstone = "CAPSTONE";
    /// <summary>{0} = printed levels, {1} = levels in all.</summary>
    public const string PrintVersions = "{0} of {1} printed";
    public const string PrintNotPrinted = "not printed yet";
    public const string PrintNext = "NEXT PRINT";
    public const string PrintHave = "YOU HAVE";
    public const string PrintParts = "PARTS";
    public const string PrintPartsHint = "press a part to hunt for it";
    public const string PrintHunting = "HUNTING";
    public const string PrintOr = "or {0}";
    public const string PrintBlocked = "{0} more {1}: free one first";
    public const string PrintMaxed = "Fully printed. Nothing more to add here.";
    public const string PrintReady = "Every part in hand. Ready to print.";
    /// <summary>{0} = level title, {1} = node name, {2} = level.</summary>
    public const string PrintNeeds = "Needs {0} ({1} v{2}) first.";
    public const string PrintSoon = "Coming soon. Nobody can print this yet.";
    public const string PrintKeepHint = "KEEP them at the sorting table.";
    public const string PrintSyncing = "Still loading your print shop...";

    // ---- the big button
    /// <summary>{0} = what gets printed.</summary>
    public const string PrintButton = "PRINT  {0}";
    /// <summary>{0} = how many kept items the print uses up.</summary>
    public const string PrintConfirm = "CONFIRM: uses up {0} kept part{1}";
    public const string PrintConfirmTreasure = " (treasure worth {0}!)";
    public const string PrintButtonLocked = "LOCKED";
    public const string PrintButtonNeed = "NEED PARTS";
    public const string PrintButtonSoon = "COMING SOON";
    public const string PrintButtonMaxed = "FULLY PRINTED";
    public const string PrintTierDone = "TIER {0} PRINTED";
    public const string PrintButtonBusy = "PRINTING...";

    // ---- inspector: the Monument
    /// <summary>{0} = tier, {1} = tiers in all.</summary>
    public const string PrintTierOf = "TROPHY · TIER {0} OF {1}";
    public const string PrintPreview = "PREVIEW";
    public const string PrintStanding = "STANDING OUTSIDE";
    public const string PrintFeedstock = "FEEDSTOCK";
    /// <summary>{0} = kept items of each sorted type this tier takes.</summary>
    public const string PrintFeedstockEach = "{0} of each sorted type";
    /// <summary>{0} = pieces this press loads.</summary>
    public const string PrintLoad = "LOAD {0} PIECE{1}";
    /// <summary>{0} = tier.</summary>
    public const string PrintTier = "PRINT TIER {0}";
    public const string PrintMonumentNote = "Throw sorted metal, plastic, glass and paper in the KEEP crate. The lightest go first; parts you are hunting for, or that a blueprint still needs, are never used.";
    public const string PrintMonumentEmpty = "Nothing to load. Keep sorted pieces instead of binning them.";
    /// <summary>{0} = the tier before.</summary>
    public const string PrintMonumentLocked = "Print tier {0} first.";
    public const string PrintMonumentDone = "Printed. It is standing outside your depot.";
    public const string PrintMonumentFinished = "The Monument is finished. There is nothing bigger. Go outside and look at it.";

    // ---- status bar
    public static readonly string[] PrintTips =
    {
        "Tip: throw ANYTHING in the KEEP crate to keep it as a part.",
        "Tip: press a part in the list to hunt for it. Magnus will keep hunted parts too.",
        "Tip: every kit needs Work Gloves v1 first.",
        "Tip: the Monument only eats plain sorted scrap. Your hunted parts are safe.",
        "Tip: mouse wheel zooms the build plate. Drag to pan.",
        "Licence: 30-day trial (day 10,402).",
    };

    // ---- Scrapazon's Gear aisle: where the gloves went
    public const string ScrapazonGearMovedTitle = "Looking for gloves, boots or a headlamp?";
    public const string ScrapazonGearMovedBody =
        "Personal gear is not sold: it is 3D-printed from things you find. Throw the parts in the KEEP crate, then open " + SolidWaste.Name +
        " (the third tab on this laptop) and pick a blueprint: it has the shopping list. No disk yet? Keep sorting. One turns up in the trash.";
}
