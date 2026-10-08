using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Front-facing previews for the authored town kits (-Z frontage). Unity's default AssetPreview
/// tends to show their blank rear walls. These textures are editor-only memory, never project assets.</summary>
[InitializeOnLoad]
public static class TownAssetThumbnails
{
    sealed class Cached
    {
        public Hash128 dependencyHash;
        public Texture2D texture;
    }
    static readonly Dictionary<int, Cached> Cache = new Dictionary<int, Cached>();
    const string KitRoot = "Assets/Prefabs/TownAuthoring/";

    static TownAssetThumbnails()
    {
        AssemblyReloadEvents.beforeAssemblyReload += Clear;
        EditorApplication.quitting += Clear;
    }

    /// <summary>Returns null for other assets so callers can use Unity AssetPreview. Cache hits are safe
    /// during any IMGUI event; missing/changed previews are rendered only during Repaint.</summary>
    public static Texture2D Get(TownAssetEntry entry)
    {
        if (entry == null || !entry.prefab) return null;
        string path = AssetDatabase.GetAssetPath(entry.prefab);
        if (!path.StartsWith(KitRoot, StringComparison.Ordinal)) return null;
        int id = entry.prefab.GetInstanceID();
        bool repaint = Event.current != null && Event.current.type == EventType.Repaint;
        if (!repaint) return Cache.TryGetValue(id, out var old) ? old.texture : null;

        Hash128 dependency = AssetDatabase.GetAssetDependencyHash(path);
        if (Cache.TryGetValue(id, out var cached) && cached.dependencyHash == dependency) return cached.texture;
        if (cached?.texture) Object.DestroyImmediate(cached.texture);
        // A failed renderer also gets cached for this dependency revision, avoiding a repaint-error loop.
        cached = new Cached { dependencyHash = dependency }; Cache[id] = cached;
        PreviewRenderUtility preview = null;
        GameObject clone = null;
        try
        {
            preview = new PreviewRenderUtility();
            clone = Object.Instantiate(entry.prefab);
            clone.hideFlags = HideFlags.HideAndDontSave;
            clone.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            clone.transform.localScale = Vector3.one;
            preview.AddSingleGO(clone);
            var bounds = TownPlacementTools.PrefabBounds(entry.prefab);
            float diagonal = Mathf.Max(1, bounds.size.magnitude);
            preview.camera.transform.position = bounds.center + new Vector3(1.2f, .85f, -1.4f).normalized * diagonal * 1.2f;
            preview.camera.transform.LookAt(bounds.center);
            preview.camera.fieldOfView = 38; preview.camera.nearClipPlane = .05f;
            preview.camera.farClipPlane = Mathf.Max(500, diagonal * 5);
            preview.camera.clearFlags = CameraClearFlags.SolidColor;
            preview.camera.backgroundColor = new Color(.18f, .22f, .24f);
            preview.lights[0].intensity = 1.8f;
            preview.lights[0].transform.rotation = Quaternion.Euler(45, -30, 0);
            preview.lights[1].intensity = .7f;
            preview.ambientColor = new Color(.5f, .5f, .5f);
            preview.BeginStaticPreview(new Rect(0, 0, 192, 160));
            preview.Render(true);
            cached.texture = preview.EndStaticPreview();
            if (cached.texture)
            {
                cached.texture.name = "Town palette preview · " + entry.id;
                cached.texture.hideFlags = HideFlags.HideAndDontSave;
            }
            return cached.texture;
        }
        catch (Exception e)
        {
            Debug.LogWarning("Town preview could not render " + entry.displayName + ": " + e.Message);
            return null;
        }
        finally
        {
            preview?.Cleanup();
            // Cleanup normally owns this clone; also handle an exception before AddSingleGO finished.
            if (clone) Object.DestroyImmediate(clone);
        }
    }

    public static void Clear()
    {
        foreach (var cached in Cache.Values) if (cached.texture) Object.DestroyImmediate(cached.texture);
        Cache.Clear();
    }
}
