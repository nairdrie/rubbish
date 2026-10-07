using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Setup for the Critters system (raccoons, pile goblins, rabies). Critters need NO scene or prefab changes: the
/// runtime bootstraps itself ("[Critters]" DontDestroyOnLoad object) and attaches RabiesStatus/CritterKnockback to
/// the local player when it spawns. This setup only creates the base materials the primitive critters are tinted
/// from and registers them in RubbishAssets, so player builds keep the URP Lit / emissive / Unlit variants.
/// Idempotent: re-running updates the same assets.
/// </summary>
public static class Setup_Critters
{
    public const string MatDir = "Assets/Materials/Generated/Critters";

    [MenuItem("Rubbish/Setup/Critters")]
    public static void Run() => Build(null);

    /// <summary>scenePath is unused (no scene content); kept for parity with the other setup scripts.</summary>
    public static string Build(string scenePath)
    {
        if (Application.isPlaying)
        {
            Debug.LogError("[Setup_Critters] Exit play mode first.");
            return "play mode";
        }
        var sb = new StringBuilder("[Setup_Critters] ");
        EnsureFolder(MatDir);

        var lit = Shader.Find("Universal Render Pipeline/Lit");
        var unlit = Shader.Find("Universal Render Pipeline/Unlit");
        if (lit == null || unlit == null)
        {
            Debug.LogError("[Setup_Critters] URP shaders not found.");
            return "no URP";
        }

        var mLit = GetOrCreate(MatDir + "/Critters_Lit.mat", lit, m =>
        {
            m.SetColor("_BaseColor", Color.white);
            m.SetFloat("_Smoothness", 0.3f);
            m.SetFloat("_Metallic", 0f);
            m.DisableKeyword("_EMISSION");
        });
        var mEmis = GetOrCreate(MatDir + "/Critters_LitEmissive.mat", lit, m =>
        {
            m.SetColor("_BaseColor", Color.white);
            m.SetFloat("_Smoothness", 0.9f);
            m.SetFloat("_Metallic", 0f);
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", Color.white * 0.4f);
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        });
        var mUnlit = GetOrCreate(MatDir + "/Critters_Unlit.mat", unlit, m =>
        {
            m.SetColor("_BaseColor", Color.white);
        });

        RubbishAssets.EditorRegisterMaterial("mat/critters_lit", mLit);
        RubbishAssets.EditorRegisterMaterial("mat/critters_lit_emissive", mEmis);
        RubbishAssets.EditorRegisterMaterial("mat/critters_unlit", mUnlit);
        AssetDatabase.SaveAssets();
        sb.Append("materials ok (mat/critters_lit, mat/critters_lit_emissive, mat/critters_unlit). No scene changes needed.");
        Debug.Log(sb.ToString());
        return sb.ToString();
    }

    static Material GetOrCreate(string path, Shader shader, System.Action<Material> configure)
    {
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        bool created = m == null;
        if (created) m = new Material(shader);
        else if (m.shader != shader) m.shader = shader;
        configure(m);
        if (created) AssetDatabase.CreateAsset(m, path);
        else EditorUtility.SetDirty(m);
        return m;
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
    }

    // =====================================================================
    // Debug menu (play mode)
    // =====================================================================

    const string Dbg = "Rubbish/Debug/Critters/";
    const string DbgRabies = "Rubbish/Debug/Rabies/";

    static bool Playing() => Application.isPlaying;

    [MenuItem(Dbg + "Spawn Raccoon Near Player")]
    static void SpawnRaccoon() => Debug.Log("[Critters] " + CritterSpawner.DebugSpawnRaccoon(false));
    [MenuItem(Dbg + "Spawn Raccoon Near Player", true)] static bool V1() => Playing();

    [MenuItem(Dbg + "Spawn RABID Raccoon Near Player")]
    static void SpawnRabid() => Debug.Log("[Critters] " + CritterSpawner.DebugSpawnRaccoon(true));
    [MenuItem(Dbg + "Spawn RABID Raccoon Near Player", true)] static bool V2() => Playing();

    [MenuItem(Dbg + "Spawn Pile Goblin Near Player")]
    static void SpawnGoblin() => Debug.Log("[Critters] " + CritterSpawner.DebugSpawnGoblin());
    [MenuItem(Dbg + "Spawn Pile Goblin Near Player", true)] static bool V3() => Playing();

    [MenuItem(Dbg + "Honk (scare critters around player)")]
    static void Honk()
    {
        var pc = PlayerController.Local;
        if (pc == null) return;
        Debug.Log($"[Critters] Honk scared {CritterSpawner.Honk(pc.transform.position)} critters.");
    }
    [MenuItem(Dbg + "Honk (scare critters around player)", true)] static bool V4() => Playing();

    [MenuItem(Dbg + "Poof All Critters")]
    static void PoofAll() => Debug.Log("[Critters] " + CritterSpawner.DebugClearCritters());
    [MenuItem(Dbg + "Poof All Critters", true)] static bool V5() => Playing();

    [MenuItem(Dbg + "Make It Night (21:00)")]
    static void Night() { GameClock.DebugSetHour(21f); Debug.Log("[Critters] It's 9 PM. Raccoon o'clock."); }
    [MenuItem(Dbg + "Make It Night (21:00)", true)] static bool V6() => Playing();

    [MenuItem(Dbg + "Make It Morning (6:45)")]
    static void Morning() { GameClock.DebugSetHour(6.75f); Debug.Log("[Critters] Good morning."); }
    [MenuItem(Dbg + "Make It Morning (6:45)", true)] static bool V7() => Playing();

    [MenuItem(Dbg + "Give 1 Repellent")]
    static void GiveRepellent() { GameSession.AddConsumable(CritterRepellent.ConsumableId, 1); Debug.Log("[Critters] +1 repellent."); }
    [MenuItem(Dbg + "Give 1 Repellent", true)] static bool V8() => Playing();

    [MenuItem(Dbg + "Toggle Air Horn Upgrade")]
    static void ToggleAirHorn()
    {
        int lvl = GameSession.GetUpgradeLevel(CritterSpawner.AirHornUpgradeId) > 0 ? 0 : 1;
        GameSession.SetUpgradeLevel(CritterSpawner.AirHornUpgradeId, lvl);
        Debug.Log($"[Critters] air_horn = {lvl}");
    }
    [MenuItem(Dbg + "Toggle Air Horn Upgrade", true)] static bool V9() => Playing();

    [MenuItem(DbgRabies + "Infect Player")]
    static void Infect() { GameSession.Infect(GameClock.TotalHours); Debug.Log("[Rabies] Infected."); }
    [MenuItem(DbgRabies + "Infect Player", true)] static bool R1() => Playing();

    [MenuItem(DbgRabies + "Skip To Symptoms")]
    static void SkipSymptoms()
    {
        if (!GameSession.HasRabies) GameSession.Infect(GameClock.TotalHours);
        GameSession.RabiesInfectedAtHours = GameClock.TotalHours - (Economy.RabiesIncubationHours + 0.05);
        Debug.Log("[Rabies] Symptoms start now.");
    }
    [MenuItem(DbgRabies + "Skip To Symptoms", true)] static bool R2() => Playing();

    [MenuItem(DbgRabies + "Skip To Full Symptoms")]
    static void SkipFull()
    {
        if (!GameSession.HasRabies) GameSession.Infect(GameClock.TotalHours);
        GameSession.RabiesInfectedAtHours = GameClock.TotalHours - (Economy.RabiesPassOutHours - 0.75);
        Debug.Log("[Rabies] 45 in-game minutes until you pass out.");
    }
    [MenuItem(DbgRabies + "Skip To Full Symptoms", true)] static bool R3() => Playing();

    [MenuItem(DbgRabies + "Pass Out Now")]
    static void PassOut()
    {
        if (!GameSession.HasRabies) GameSession.Infect(GameClock.TotalHours);
        GameSession.RabiesInfectedAtHours = GameClock.TotalHours - Economy.RabiesPassOutHours - 0.01;
        Debug.Log("[Rabies] Passing out...");
    }
    [MenuItem(DbgRabies + "Pass Out Now", true)] static bool R4() => Playing();

    [MenuItem(DbgRabies + "Cure With Shot")]
    static void Cure() => Debug.Log("[Rabies] CureWithShot -> " + RabiesStatus.CureWithShot());
    [MenuItem(DbgRabies + "Cure With Shot", true)] static bool R5() => Playing();

    [MenuItem(DbgRabies + "Give 1 Rabies Shot (auto-used on infection)")]
    static void GiveShot() { GameSession.AddConsumable(CritterSpawner.RabiesShotId, 1); Debug.Log("[Rabies] +1 rabies_shot."); }
    [MenuItem(DbgRabies + "Give 1 Rabies Shot (auto-used on infection)", true)] static bool R6() => Playing();
}
