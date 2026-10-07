using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Runtime lookup for prefab/material/audio references that systems can't reach without the
/// AssetDatabase (e.g. trash sack meshes, item props). Lives at Assets/Resources/RubbishAssets.asset.
/// Editor setup scripts register entries by namespaced key ("sack/0", "item/tire", "mat/asphalt").
/// FROZEN CONTRACT.
/// </summary>
[CreateAssetMenu(menuName = "Rubbish/Rubbish Assets")]
public class RubbishAssets : ScriptableObject
{
    [Serializable] public class PrefabEntry { public string key; public GameObject prefab; }
    [Serializable] public class MaterialEntry { public string key; public Material material; }
    [Serializable] public class ClipEntry { public string key; public AudioClip clip; }

    public List<PrefabEntry> prefabs = new List<PrefabEntry>();
    public List<MaterialEntry> materials = new List<MaterialEntry>();
    public List<ClipEntry> clips = new List<ClipEntry>();

    public const string ResourcePath = "RubbishAssets";
    public const string AssetPath = "Assets/Resources/RubbishAssets.asset";

    static RubbishAssets instance;
    public static RubbishAssets Instance
    {
        get
        {
            if (instance == null) instance = Resources.Load<RubbishAssets>(ResourcePath);
            return instance;
        }
    }

    public static GameObject Prefab(string key)
    {
        var i = Instance; if (i == null) return null;
        foreach (var e in i.prefabs) if (e.key == key) return e.prefab;
        return null;
    }

    public static Material Material(string key)
    {
        var i = Instance; if (i == null) return null;
        foreach (var e in i.materials) if (e.key == key) return e.material;
        return null;
    }

    public static AudioClip Clip(string key)
    {
        var i = Instance; if (i == null) return null;
        foreach (var e in i.clips) if (e.key == key) return e.clip;
        return null;
    }

    /// <summary>All prefabs whose key starts with prefix (e.g. "sack/").</summary>
    public static List<GameObject> PrefabsWithPrefix(string prefix)
    {
        var list = new List<GameObject>();
        var i = Instance; if (i == null) return list;
        foreach (var e in i.prefabs) if (e.key.StartsWith(prefix) && e.prefab != null) list.Add(e.prefab);
        return list;
    }

#if UNITY_EDITOR
    public static RubbishAssets EditorGetOrCreate()
    {
        var a = AssetDatabase.LoadAssetAtPath<RubbishAssets>(AssetPath);
        if (a == null)
        {
            if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
            a = CreateInstance<RubbishAssets>();
            AssetDatabase.CreateAsset(a, AssetPath);
            AssetDatabase.SaveAssets();
        }
        instance = a;
        return a;
    }

    /// <summary>Add or replace a prefab entry. Pass an asset path or a loaded prefab.</summary>
    public static void EditorRegisterPrefab(string key, GameObject prefab)
    {
        var a = EditorGetOrCreate();
        a.prefabs.RemoveAll(e => e.key == key);
        a.prefabs.Add(new PrefabEntry { key = key, prefab = prefab });
        EditorUtility.SetDirty(a);
    }

    public static void EditorRegisterPrefab(string key, string assetPath)
    {
        var go = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
        if (go == null) { Debug.LogWarning($"[RubbishAssets] No prefab at {assetPath} for key {key}"); return; }
        EditorRegisterPrefab(key, go);
    }

    public static void EditorRegisterMaterial(string key, Material m)
    {
        var a = EditorGetOrCreate();
        a.materials.RemoveAll(e => e.key == key);
        a.materials.Add(new MaterialEntry { key = key, material = m });
        EditorUtility.SetDirty(a);
    }

    public static void EditorRegisterClip(string key, AudioClip c)
    {
        var a = EditorGetOrCreate();
        a.clips.RemoveAll(e => e.key == key);
        a.clips.Add(new ClipEntry { key = key, clip = c });
        EditorUtility.SetDirty(a);
    }
#endif
}
