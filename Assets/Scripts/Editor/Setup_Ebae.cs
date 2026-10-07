using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

/// <summary>
/// Idempotent setup for ebae: panel settings (drawn above the HUD, scales with screen), a theme
/// that doesn't double-import ebae.uss, and a sanity check of the ShopScene wiring
/// (UIManager.EbaeUI.ebae → UIDocument with ebae.uxml). Also a few play-mode debug menu items.
/// </summary>
public static class Setup_Ebae
{
    const string Folder = "Assets/UI/ebae";
    const string PanelPath = Folder + "/ebae Panel Settings.asset";
    const string ThemePath = Folder + "/ebae Theme File.tss";
    const string UxmlPath = Folder + "/ebae.uxml";
    const string ScenePath = "Assets/Scenes/ShopScene.unity";

    /// <summary>ebae is a full-screen app: draw it above the HUD. Unity 6 sorts overlay UI Toolkit
    /// panels together with uGUI overlay canvases; the Feel HUD canvas uses sortingOrder 100.</summary>
    public const float SortingOrder = 200f;

    const string ThemeContents = "@import url(\"unity-theme://default\");\n\nVisualElement {}\n";

    [MenuItem("Rubbish/Setup/Ebae")]
    public static void Run() => Build(ScenePath);

    /// <summary>Same as Run() against any copy of the shop scene (scratch-scene testing).</summary>
    public static void Build(string shopScenePath)
    {
        var log = new StringBuilder("[Setup_Ebae] ");

        // 1) Theme: default runtime theme only. ebae.uss is attached by ebae.uxml itself.
        var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
        string themeText = File.Exists(ThemePath) ? File.ReadAllText(ThemePath) : null;
        if (themeText == null || themeText.Contains("ebae.uss"))
        {
            File.WriteAllText(ThemePath, ThemeContents);
            AssetDatabase.ImportAsset(ThemePath, ImportAssetOptions.ForceUpdate);
            theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
            log.Append("theme rewritten; ");
        }
        else log.Append("theme ok; ");

        // 2) Panel settings.
        var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelPath);
        if (panel == null)
        {
            panel = ScriptableObject.CreateInstance<PanelSettings>();
            AssetDatabase.CreateAsset(panel, PanelPath);
            log.Append("panel settings created; ");
        }
        bool panelChanged = false;
        void Set<T>(T current, T wanted, System.Action<T> apply)
        {
            if (Equals(current, wanted)) return;
            apply(wanted);
            panelChanged = true;
        }
        if (theme != null) Set(panel.themeStyleSheet, theme, v => panel.themeStyleSheet = v);
        else log.Append("WARNING: theme missing, left panel theme as-is; ");
        Set(panel.sortingOrder, SortingOrder, v => panel.sortingOrder = v);
        Set(panel.scaleMode, PanelScaleMode.ScaleWithScreenSize, v => panel.scaleMode = v);
        Set(panel.referenceResolution, new Vector2Int(1280, 800), v => panel.referenceResolution = v);
        Set(panel.screenMatchMode, PanelScreenMatchMode.MatchWidthOrHeight, v => panel.screenMatchMode = v);
        Set(panel.match, 0.5f, v => panel.match = v);
        if (panelChanged)
        {
            EditorUtility.SetDirty(panel);
            AssetDatabase.SaveAssets();
            log.Append($"panel settings updated (sort {SortingOrder}, 1280x800 match 0.5); ");
        }
        else log.Append("panel settings ok; ");

        // 3) ShopScene wiring check (only saves if something had to be fixed).
        log.Append(CheckShopScene(panel, shopScenePath));

        Debug.Log(log.ToString());
    }

    static string CheckShopScene(PanelSettings panel, string scenePath)
    {
        var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
        var scene = SceneManager.GetSceneByPath(scenePath);
        bool opened = false;
        if (!scene.isLoaded)
        {
            if (!File.Exists(scenePath)) return "ShopScene not found; skipped scene check.";
            scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            opened = true;
        }

        var sb = new StringBuilder();
        bool changed = false;
        EbaeUI ui = null;
        LaptopController laptop = null;
        foreach (var root in scene.GetRootGameObjects())
        {
            if (ui == null) ui = root.GetComponentInChildren<EbaeUI>(true);
            if (laptop == null) laptop = root.GetComponentInChildren<LaptopController>(true);
        }

        if (ui == null) sb.Append("WARNING: no EbaeUI in ShopScene (expected on CommonWorldElements/UIManager); ");
        else
        {
            var so = new SerializedObject(ui);
            var field = so.FindProperty("ebae");
            var go = field.objectReferenceValue as GameObject;
            if (go == null || go.GetComponent<UIDocument>() == null)
            {
                UIDocument found = null;
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var d in root.GetComponentsInChildren<UIDocument>(true))
                        if (d.visualTreeAsset == uxml || d.gameObject.name.ToLowerInvariant().Contains("ebae")) { found = d; break; }
                    if (found != null) break;
                }
                if (found != null)
                {
                    field.objectReferenceValue = found.gameObject;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    go = found.gameObject;
                    changed = true;
                    sb.Append("EbaeUI.ebae re-linked; ");
                }
                else sb.Append("WARNING: EbaeUI.ebae has no UIDocument and none was found; ");
            }

            var doc = go != null ? go.GetComponent<UIDocument>() : null;
            if (doc != null)
            {
                if (doc.visualTreeAsset != uxml) { doc.visualTreeAsset = uxml; EditorUtility.SetDirty(doc); changed = true; sb.Append("UIDocument uxml fixed; "); }
                if (doc.panelSettings != panel) { doc.panelSettings = panel; EditorUtility.SetDirty(doc); changed = true; sb.Append("UIDocument panel settings fixed; "); }
            }
        }

        if (laptop == null) sb.Append("WARNING: no LaptopController in ShopScene; ");
        else if (laptop.GetComponentInChildren<Collider>(true) == null)
            sb.Append("WARNING: laptop has no collider on the Interactable layer, it can't be targeted; ");

        if (changed)
        {
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            sb.Append("ShopScene saved.");
        }
        else sb.Append("ShopScene wiring ok.");

        if (opened) EditorSceneManager.CloseScene(scene, true);
        return sb.ToString();
    }

    // ---------------------------------------------------------------- play-mode debug helpers

    [MenuItem("Rubbish/Debug/ebae/Give Test Treasures")]
    static void GiveTestTreasures()
    {
        if (!Application.isPlaying) { Debug.LogWarning("[ebae] Enter play mode first."); return; }
        var rng = new System.Random();
        int given = 0;
        foreach (var def in ItemCatalog.All)
        {
            if (def.category != ItemCategory.Valuable) continue;
            GameSession.AddToInventory(ItemCatalog.RollItem(def, rng, "742 Evergreen Terrace"));
            if (++given >= 4) break;
        }
        if (given == 0 && ItemCatalog.All.Count > 0)
        {
            GameSession.AddToInventory(ItemCatalog.RollItem(ItemCatalog.All[0], rng, "742 Evergreen Terrace"));
            given = 1;
        }
        Debug.Log($"[ebae] Gave {given} test item(s).");
    }

    [MenuItem("Rubbish/Debug/ebae/Force Offers Now")]
    static void ForceOffers()
    {
        if (!Application.isPlaying) { Debug.LogWarning("[ebae] Enter play mode first."); return; }
        EbaeMarket.DebugForceOffers();
    }

    [MenuItem("Rubbish/Debug/ebae/Open Laptop")]
    static void OpenLaptop()
    {
        if (!Application.isPlaying) { Debug.LogWarning("[ebae] Enter play mode first."); return; }
        if (EbaeUI.Instance != null) EbaeUI.Instance.Show(LaptopApp.Ebae);
        else Debug.LogWarning("[ebae] No EbaeUI in the loaded scenes (it lives in ShopScene).");
    }

    [MenuItem("Rubbish/Debug/ebae/Wipe ebae Save")]
    static void WipeSave()
    {
        if (!Application.isPlaying)
        {
            // Saves go through LegacySave (the pair rubbish_save.json + ebae_save.json, the data root, the backups). Deleting one
            // file by hand in edit mode would split the pair and skip the backups, so the wipe only runs in play mode.
            Debug.LogWarning("[ebae] Enter play mode first: the wipe goes through the save system (your old ebae data stays " +
                             "in the backups under ebae_save.gen/).");
            return;
        }
        EbaeMarket.ResetAll(true);
    }
}
