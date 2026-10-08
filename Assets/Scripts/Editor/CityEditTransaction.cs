using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>
/// One explicitly committed city edit. Undo restores live scene objects/meshes; watched files restore generated
/// assets and scene bytes if a late bake/save fails. Builders must watch a path BEFORE writing it, register newly
/// created root groups, and destroy replaced roots through DestroyGenerated. Unrelated paths are never touched.
/// </summary>
public sealed class CityEditTransaction : IDisposable
{
    public static CityEditTransaction Current { get; private set; }
    public const string UndoName = "Apply coastal city blueprint";
    readonly int group;
    readonly Dictionary<string, FileState> files = new Dictionary<string, FileState>(StringComparer.Ordinal);
    readonly List<LiveState> live = new List<LiveState>();
    readonly List<Scene> scenes = new List<Scene>();
    readonly HashSet<int> originalRoots = new HashSet<int>();
    bool committed, disposed;

    sealed class FileState { public string path; public byte[] bytes, meta; }
    sealed class LiveState { public Object original, copy; public bool dirty; }

    public CityEditTransaction(params Scene[] targets) : this(false, targets) { }

    /// <summary>The Town Editor publishes an unsaved draft. Its live hierarchy is still snapshotted by Undo,
    /// while watched files retain the last published bytes, so failure preserves the editable draft.</summary>
    public CityEditTransaction(bool allowDirtyDraft, params Scene[] targets)
    {
        if (Current != null) throw new InvalidOperationException("A city edit transaction is already active.");
        foreach (var scene in targets)
            if (!scene.IsValid() || !scene.isLoaded || (!allowDirtyDraft && scene.isDirty))
                throw new InvalidOperationException("Save or revert unsaved changes in " + scene.name + " before applying the city blueprint.");
        Undo.IncrementCurrentGroup(); group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName(UndoName);
        Current = this;
        try
        {
            foreach (var scene in targets)
            {
                scenes.Add(scene);
                if (!string.IsNullOrEmpty(scene.path)) WatchFile(scene.path);
                foreach (var root in scene.GetRootGameObjects())
                {
                    originalRoots.Add(root.GetInstanceID());
                    Undo.RegisterFullObjectHierarchyUndo(root, UndoName);
                }
            }
        }
        catch { Dispose(); throw; }
    }

    /// <summary>Protect an exact generated asset path, including importer metadata and unsaved live asset values.</summary>
    public void WatchAsset(string path)
    {
        if (files.ContainsKey(path)) return;
        WatchFile(path);
        foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path) ?? Array.Empty<Object>())
        {
            if (asset == null || asset is SceneAsset || asset is GameObject || asset is Component) continue;
            if (asset is Mesh || asset is Material || asset is ScriptableObject)
            {
                Undo.RegisterCompleteObjectUndo(asset, UndoName);
                var copy = Object.Instantiate(asset); copy.name = asset.name;
                live.Add(new LiveState { original = asset, copy = copy, dirty = EditorUtility.IsDirty(asset) });
            }
        }
    }

    public void WatchFile(string path)
    {
        if (files.ContainsKey(path)) return;
        if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.Ordinal) || path.Contains("..") || Directory.Exists(path))
            throw new ArgumentException("City transactions require an explicit file below Assets: " + path);
        files.Add(path, new FileState { path = path, bytes = Read(path), meta = Read(path + ".meta") });
    }

    static byte[] Read(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;

    public static void RegisterCreated(GameObject root)
    {
        if (Current != null && root != null) Undo.RegisterCreatedObjectUndo(root, UndoName);
    }

    public static void DestroyGenerated(GameObject root)
    {
        if (root == null) return;
        if (Current != null) Undo.DestroyObjectImmediate(root);
        else Object.DestroyImmediate(root);
    }

    public void Commit()
    {
        if (disposed) throw new ObjectDisposedException(nameof(CityEditTransaction));
        Undo.CollapseUndoOperations(group);
        committed = true;
    }

    /// <summary>Publish only assets this operation owns; never flush unrelated unsaved editor artwork.</summary>
    public void SaveWatchedAssets()
    {
        foreach (var file in files.Values)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(file.path);
            if (asset != null && !(asset is SceneAsset)) AssetDatabase.SaveAssetIfDirty(asset);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            if (!committed)
            {
                Undo.RevertAllDownToGroup(group);
                // The map baker may add its scene-level hash marker late in publishing. It is not a Group()
                // child; remove only new top-level objects in the explicitly owned target scenes.
                foreach (var scene in scenes)
                    if (scene.IsValid() && scene.isLoaded)
                        foreach (var root in scene.GetRootGameObjects())
                            if (!originalRoots.Contains(root.GetInstanceID())) Object.DestroyImmediate(root);
                foreach (var file in files.Values)
                {
                    if (file.bytes == null)
                    {
                        // Only paths proven absent at transaction start may be removed.
                        if (File.Exists(file.path)) AssetDatabase.DeleteAsset(file.path);
                        if (File.Exists(file.path)) File.Delete(file.path);
                        if (file.meta == null && File.Exists(file.path + ".meta")) File.Delete(file.path + ".meta");
                    }
                    else File.WriteAllBytes(file.path, file.bytes);
                    if (file.meta != null) File.WriteAllBytes(file.path + ".meta", file.meta);
                }
                foreach (var file in files.Values)
                    if (file.bytes != null) AssetDatabase.ImportAsset(file.path, ImportAssetOptions.ForceSynchronousImport);
                foreach (var asset in live)
                {
                    if (asset.original == null || asset.copy == null) continue;
                    EditorUtility.CopySerialized(asset.copy, asset.original);
                    if (asset.dirty) EditorUtility.SetDirty(asset.original);
                    else EditorUtility.ClearDirty(asset.original);
                }
                Physics.SyncTransforms();
                // A reverted open scene can remain marked dirty by Unity's Undo implementation. Keep it open
                // for inspection; its serialized file has nevertheless been restored byte-for-byte above.
            }
        }
        finally
        {
            foreach (var asset in live) if (asset.copy != null) Object.DestroyImmediate(asset.copy);
            if (Current == this) Current = null;
        }
    }
}
