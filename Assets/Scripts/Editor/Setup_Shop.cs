using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Idempotent setup for the Shop system (Scrapazon + upgrade effects):
///  1) URP Lit materials for runtime Shop props (headlamp, delivery boxes, truck mods) under
///     Assets/Materials/Generated/Shop/, registered in RubbishAssets as "mat/shop_*" (runtime falls back to
///     building them itself if this never ran).
///  2) Validates the laptop UI: scrapazon.uxml/.uss import cleanly and ebae.uxml instances the Scrapazon page.
///  3) Runs Setup_Ebae (panel settings + ShopScene laptop wiring) — the laptop document hosts both apps.
/// No scene objects are added: the effect runner bootstraps itself at runtime, and trucks / players get their
/// Shop components when they spawn.
/// </summary>
public static class Setup_Shop
{
    const string MatFolder = "Assets/Materials/Generated/Shop";
    const string ShopUxml = "Assets/UI/shop/scrapazon.uxml";
    const string ShopUss = "Assets/UI/shop/scrapazon.uss";
    const string EbaeUxml = "Assets/UI/ebae/ebae.uxml";
    const string ShopScenePath = "Assets/Scenes/ShopScene.unity";

    [MenuItem("Rubbish/Setup/Shop")]
    public static void Run() => Build(ShopScenePath);

    /// <summary>Same as Run() against any copy of the shop scene (scratch-scene testing).</summary>
    public static void Build(string shopScenePath)
    {
        var log = new StringBuilder("[Setup_Shop] ");
        log.Append(EnsureMaterials()).Append(' ');
        log.Append(ValidateUi()).Append(' ');
        Debug.Log(log.ToString());
        Setup_Ebae.Build(shopScenePath);
    }

    // ---------------------------------------------------------------- materials

    static string EnsureMaterials()
    {
        EnsureFolder(MatFolder);
        int made = 0;
        made += Mat("shop_headlamp_body", new Color(0.16f, 0.16f, 0.2f), 0.4f, 0f);
        made += Mat("shop_headlamp_lens", new Color(1f, 0.93f, 0.72f), 0.8f, 0f, new Color(1f, 0.93f, 0.72f) * 0.4f);
        made += Mat("shop_chrome", new Color(0.86f, 0.88f, 0.92f), 0.85f, 0.9f);
        made += Mat("shop_red", new Color(0.9f, 0.18f, 0.15f), 0.5f, 0f);
        made += Mat("shop_wood", new Color(0.66f, 0.46f, 0.26f), 0.2f, 0f);
        made += Mat("shop_tape", new Color(0.72f, 0.74f, 0.78f), 0.55f, 0.3f);
        made += Mat("shop_box", new Color(0.78f, 0.58f, 0.36f), 0.15f, 0f);
        made += Mat("shop_box_tape", new Color(0.93f, 0.8f, 0.55f), 0.3f, 0f);
        made += Mat("shop_orange", new Color(1f, 0.6f, 0f), 0.4f, 0f);
        made += Mat("shop_white", new Color(0.97f, 0.97f, 0.97f), 0.6f, 0f);
        EditorUtility.SetDirty(RubbishAssets.EditorGetOrCreate());
        AssetDatabase.SaveAssets();
        return $"materials ok ({made} created, 10 registered as mat/shop_*);";
    }

    static int Mat(string name, Color color, float smoothness, float metallic, Color? emission = null)
    {
        string path = $"{MatFolder}/{name}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        int made = 0;
        if (m == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            m = new Material(shader) { name = name };
            AssetDatabase.CreateAsset(m, path);
            made = 1;
        }
        m.SetColor("_BaseColor", color);
        m.SetFloat("_Smoothness", smoothness);
        m.SetFloat("_Metallic", metallic);
        if (emission.HasValue)
        {
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", emission.Value);
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        }
        EditorUtility.SetDirty(m);
        RubbishAssets.EditorRegisterMaterial("mat/" + name, m);
        return made;
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }

    // ---------------------------------------------------------------- UI validation (read-only)

    static readonly string[] ShopNames =
    {
        "szPage", "szHeader", "szLogo", "szLogoWord", "szLogoSmileHost", "szDeliver", "szSearch", "szSearchButton",
        "szAccount", "szReturns", "szBalanceLabel", "szCart", "szCartIconHost", "szCartCount", "szNav", "szNavButtons",
        "szScroll", "szHero", "szHeroArtHost", "szHeroKicker", "szHeroTitle", "szHeroSub", "szResultsLabel", "szGrid",
        "szAlsoRow", "szBackToTop", "szFooterLabel",
    };

    static readonly string[] LaptopNames =
    {
        "ebaeScreen", "window", "appStrip", "appTabEbae", "appTabShop", "osClockLabel", "chrome", "addressLabel",
        "ebaePage", "scrapazonPage", "contentScroll", "toastLayer", "fxLayer", "closeButton", "logoffButton",
    };

    /// <summary>Clone the laptop documents and look for every element the code queries. Returns a summary.</summary>
    public static string ValidateUi()
    {
        var sb = new StringBuilder();
        var shopTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(ShopUxml);
        var shopUss = AssetDatabase.LoadAssetAtPath<StyleSheet>(ShopUss);
        var laptopTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(EbaeUxml);
        if (shopTree == null || shopUss == null || laptopTree == null)
            return $"WARNING: missing UI assets (scrapazon.uxml {shopTree != null}, scrapazon.uss {shopUss != null}, ebae.uxml {laptopTree != null});";

        int missing = 0;
        var shopRoot = new VisualElement();
        shopTree.CloneTree(shopRoot);
        foreach (var n in ShopNames)
            if (shopRoot.Q(n) == null) { missing++; sb.Append($"WARNING: scrapazon.uxml has no '{n}'; "); }

        var laptopRoot = new VisualElement();
        laptopTree.CloneTree(laptopRoot);
        foreach (var n in LaptopNames)
            if (laptopRoot.Q(n) == null) { missing++; sb.Append($"WARNING: ebae.uxml has no '{n}'; "); }
        var page = laptopRoot.Q("scrapazonPage");
        if (page != null && page.Q("szGrid") == null) { missing++; sb.Append("WARNING: scrapazonPage doesn't contain the Scrapazon template; "); }

        if (missing == 0) sb.Append($"laptop UI ok ({ShopNames.Length + LaptopNames.Length} elements found, Scrapazon instanced in ebae.uxml);");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- play-mode debug helpers

    [MenuItem("Rubbish/Debug/Shop/Give $1000")]
    static void GiveCash()
    {
        if (!Application.isPlaying) { Debug.LogWarning("[Shop] Enter play mode first."); return; }
        GameSession.AddCash(1000f, "debug: shop money");
    }

    [MenuItem("Rubbish/Debug/Shop/Open Scrapazon")]
    static void OpenShop()
    {
        if (!Application.isPlaying) { Debug.LogWarning("[Shop] Enter play mode first."); return; }
        if (EbaeUI.Instance != null) EbaeUI.Instance.Show(LaptopApp.Scrapazon);
        else Debug.LogWarning("[Shop] No laptop UI in the loaded scenes (it lives in ShopScene).");
    }

    [MenuItem("Rubbish/Debug/Shop/Buy Everything")]
    static void BuyEverything()
    {
        if (!Application.isPlaying) { Debug.LogWarning("[Shop] Enter play mode first."); return; }
        foreach (var it in ScrapazonCatalog.All)
        {
            if (it.IsTeaser) continue;
            while (!ScrapazonStore.IsMaxed(it) && (!it.IsConsumable || ScrapazonStore.Owned(it) < 2))
            {
                GameSession.AddCash(ScrapazonStore.NextPrice(it), "debug: shop money");
                if (ScrapazonStore.TryBuy(it.id) != ScrapazonPurchase.Ok) break;
            }
        }
        // Personal gear is printed in SolidWaste 3D now (blueprints), not bought: give that too (and the disk with it).
        bool callings = Workshop.DebugSetCallings(true, out string why);
        ShopEffects.ApplyAll();
        Debug.Log("[Shop] Bought everything (2 of each consumable)" + (callings ? " and printed every available blueprint." : ". Blueprints NOT printed: " + why));
    }

    [MenuItem("Rubbish/Debug/Shop/Reset Upgrades + Pockets")]
    static void ResetUpgrades()
    {
        if (!Application.isPlaying) { Debug.LogWarning("[Shop] Enter play mode first."); return; }
        foreach (var it in ScrapazonCatalog.All)
        {
            if (it.IsUpgrade) GameSession.SetUpgradeLevel(it.id, 0);
            else if (it.IsConsumable) GameSession.AddConsumable(it.id, -GameSession.GetConsumable(it.id));
        }
        bool callings = ResetCallingsAndGear(out string why);
        ShopEffects.ApplyAll();
        Debug.Log("[Shop] Upgrades + consumables reset" + (callings ? ", blueprints and personal gear too." : ". Blueprints NOT reset: " + why));
    }

    /// <summary>The tree only ever raises upgrade levels, so a reset clears the tree first and then the gear ids it drives.</summary>
    static bool ResetCallingsAndGear(out string why)
    {
        if (!Workshop.DebugSetCallings(false, out why)) return false;
        foreach (var node in CallingCatalog.All)
            if (!string.IsNullOrEmpty(node.upgradeId)) GameSession.SetUpgradeLevel(node.upgradeId, 0);
        return true;
    }

    // ---- SolidWaste 3D (the laptop's third tab): the disk, the blueprint tree, the Monument

    [MenuItem("Rubbish/Debug/Shop/SolidWaste: Install It (skip the floppy)")]
    static void InstallDisk()
    {
        if (!Application.isPlaying) { Debug.LogWarning("[Shop] Enter play mode first."); return; }
        Debug.Log(Workshop.DebugSetDisk(true, out string why) ? "[Shop] SolidWaste 3D installed: the laptop has its third tab." : "[Shop] Not installed: " + why);
    }

    [MenuItem("Rubbish/Debug/Shop/SolidWaste: Remove It (clears prints, restarts the 10-bag hunt)")]
    static void RemoveDisk()
    {
        if (!Application.isPlaying) { Debug.LogWarning("[Shop] Enter play mode first."); return; }
        bool ok = Workshop.DebugSetDisk(false, out string why);
        if (ok) foreach (var node in CallingCatalog.All) if (!string.IsNullOrEmpty(node.upgradeId)) GameSession.SetUpgradeLevel(node.upgradeId, 0);
        GameSession.SetCounter(SolidWasteApp.SeenCounter, 0);
        ShopEffects.ApplyAll();
        Debug.Log(ok ? "[Shop] Disk removed: no print tab, no blueprints, no Monument. A floppy turns up within the next ten sorted bags." : "[Shop] Not removed: " + why);
    }

    [MenuItem("Rubbish/Debug/Shop/SolidWaste: Drop A Floppy On The Sorting Table")]
    static void DropDisk()
    {
        if (!Application.isPlaying) { Debug.LogWarning("[Shop] Enter play mode first."); return; }
        Debug.Log(DiskItem.DebugDrop() ? "[Shop] A floppy disk popped out on the sorting table." : "[Shop] No sorting table in the loaded scenes (go to your garage).");
    }

    [MenuItem("Rubbish/Debug/Shop/Blueprints: Print Everything Available (free)")]
    static void BuildAllCallings()
    {
        if (!Application.isPlaying) { Debug.LogWarning("[Shop] Enter play mode first."); return; }
        bool ok = Workshop.DebugSetCallings(true, out string why);
        ShopEffects.ApplyAll();
        Debug.Log(ok ? "[Shop] Every available blueprint printed (no parts charged)." : "[Shop] Blueprints not printed: " + why);
    }

    [MenuItem("Rubbish/Debug/Shop/Blueprints: Reset Tree + Personal Gear")]
    static void ResetCallings()
    {
        if (!Application.isPlaying) { Debug.LogWarning("[Shop] Enter play mode first."); return; }
        bool ok = ResetCallingsAndGear(out string why);
        ShopEffects.ApplyAll();
        Debug.Log(ok ? "[Shop] Blueprint tree and personal gear reset." : "[Shop] Blueprints not reset: " + why);
    }

    [MenuItem("Rubbish/Debug/Shop/Blueprints: Give 3 Of Every Recipe Part")]
    static void GiveMaterials()
    {
        if (!Application.isPlaying) { Debug.LogWarning("[Shop] Enter play mode first."); return; }
        Debug.Log(Workshop.DebugAddParts(3, out string why) ? "[Shop] +3 of every part a blueprint recipe names (and of a can, a bottle, a beer bottle and a newspaper) in the inventory." : "[Shop] Parts not added: " + why);
    }

    [MenuItem("Rubbish/Debug/Shop/Monument: +1 Tier (free)")]
    static void MonumentUp()
    {
        if (!Application.isPlaying) { Debug.LogWarning("[Shop] Enter play mode first."); return; }
        int tier = Mathf.Min(MonumentCatalog.MaxLevel, GameSession.WorkshopMonument + 1);
        Debug.Log(Workshop.DebugSetMonument(tier, out string why) ? $"[Shop] Monument is tier {tier}: {MonumentCatalog.Tier(tier)?.name}. Walk outside to watch it print." : "[Shop] Monument not changed: " + why);
    }

    [MenuItem("Rubbish/Debug/Shop/Monument: Reset To Nothing")]
    static void MonumentReset()
    {
        if (!Application.isPlaying) { Debug.LogWarning("[Shop] Enter play mode first."); return; }
        Debug.Log(Workshop.DebugSetMonument(0, out string why) ? "[Shop] Monument removed." : "[Shop] Monument not changed: " + why);
    }

    [MenuItem("Rubbish/Debug/Shop/Monument: Give 60 Scrap Of Each Type")]
    static void GiveScrap()
    {
        if (!Application.isPlaying) { Debug.LogWarning("[Shop] Enter play mode first."); return; }
        Debug.Log(Workshop.DebugAddScrap(60, out string why) ? "[Shop] +60 cans, bottles, beer bottles and newspapers in the inventory (ignores the shelf cap)." : "[Shop] Scrap not added: " + why);
    }

    [MenuItem("Rubbish/Debug/Shop/Get Rabies (test the shot)")]
    static void Infect()
    {
        if (!Application.isPlaying) { Debug.LogWarning("[Shop] Enter play mode first."); return; }
        GameSession.Infect(GameClock.TotalHours);
        Debug.Log($"[Shop] Infected. Rabies Shots in pocket: {ShopEffects.RabiesShots}.");
    }

    [MenuItem("Rubbish/Debug/Shop/Skip To Nightfall")]
    static void Night()
    {
        if (!Application.isPlaying) { Debug.LogWarning("[Shop] Enter play mode first."); return; }
        GameClock.DebugSetHour(GameClock.NightStart + 0.05f);
    }
}
