using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Published-world identity from saved source dependencies, not machine/target-specific imported
/// artifacts. The signature resource is explicitly excluded, so publishing twice cannot hash its own output.</summary>
public static class TownWorldSignatureBuilder
{
    public const string AssetPath = "Assets/Resources/City/PublishedTownSignature.asset";
    static readonly string[] Seeds = {
        CoastalCityLayout.BlueprintPath, "Assets/Resources/City/CoastalYards.json",
        NeighborhoodBuilder.DefaultScenePath, DepotRowLayout.ScenePath,
        StreetGraph.AssetPath, HouseDirectory.AssetPath, TownMapData.AssetPath, TownMapData.TexturePath,
        TownRelief.AssetPath
    };

    /// <summary>Call after validated scene/graph/directory/map saves, inside the publisher's rollback scope.</summary>
    public static string Publish()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode before publishing the town signature.");
        if (!TryCompute(out string digest, out int dependencyCount, out string reason)) throw new InvalidOperationException(reason);
        var asset = AssetDatabase.LoadAssetAtPath<PublishedTownSignature>(AssetPath);
        if (!asset)
        {
            TownAssetKitBuilder.Folder("Assets/Resources/City");
            asset = ScriptableObject.CreateInstance<PublishedTownSignature>();
            AssetDatabase.CreateAsset(asset, AssetPath);
        }
        asset.schema = PublishedTownSignature.Schema; asset.sha256 = digest; asset.dependencyCount = dependencyCount;
        EditorUtility.SetDirty(asset); AssetDatabase.SaveAssetIfDirty(asset); return digest;
    }

    /// <summary>Read-only preflight for Play Mode and player builds. Also catches a scene saved by Ctrl-S
    /// without the required derived-data bake, and unsaved prefab/material edits used by the town.</summary>
    public static bool IsCurrent(out string reason)
    {
        var asset = AssetDatabase.LoadAssetAtPath<PublishedTownSignature>(AssetPath);
        if (!asset || asset.schema != PublishedTownSignature.Schema || !PublishedTownSignature.TryParse(asset.sha256, out _))
        { reason = "The town has no valid published map signature. Open Rubbish > Map Editor and use Save & bake first."; return false; }
        if (EditorUtility.IsDirty(asset))
        { reason = "The published town signature was modified outside the publisher. Open Rubbish > Map Editor and use Save & bake."; return false; }
        if (!TryCompute(out string digest, out _, out reason)) return false;
        if (!string.Equals(digest, asset.sha256, StringComparison.OrdinalIgnoreCase))
        { reason = "The town scene, blueprint or a referenced asset changed since the last map bake. Open Rubbish > Map Editor and use Save & bake before testing or building."; return false; }
        reason = null; return true;
    }

    static bool TryCompute(out string digest, out int dependencyCount, out string reason)
    {
        digest = null; dependencyCount = 0; reason = null;
        try
        {
            foreach (string scenePath in new[] { NeighborhoodBuilder.DefaultScenePath, DepotRowLayout.ScenePath })
            {
                Scene scene = SceneManager.GetSceneByPath(scenePath);
                if (scene.IsValid() && scene.isLoaded && scene.isDirty)
                { reason = "Unsaved town scene changes: " + scenePath + ". Open Rubbish > Map Editor and use Save & bake."; return false; }
            }
            foreach (string seed in Seeds)
                if (!File.Exists(seed)) { reason = "Missing published-town input: " + seed; return false; }

            var paths = new SortedSet<string>(AssetDatabase.GetDependencies(Seeds, true), StringComparer.Ordinal);
            foreach (string seed in Seeds) paths.Add(seed);
            paths.Remove(AssetPath); paths.Remove(AssetPath + ".meta");
            var hashes = new List<KeyValuePair<string, string>>();
            foreach (string path in paths)
            {
                if (AssetDatabase.IsValidFolder(path)) continue;
                var importer = AssetImporter.GetAtPath(path);
                if (importer && EditorUtility.IsDirty(importer))
                { reason = "Apply the changed import settings before baking: " + path; return false; }
                // LoadAllAssetsAtPath is not a scene API: calling it for .unity logs thousands of
                // ReadObjectThreaded errors. Imported model/texture outputs also need not be loaded
                // merely to ignore their dirty flags. Check only authorable native asset containers.
                if (ShouldInspectNativeObjects(path, importer ? importer.GetType().Name : null))
                    foreach (UnityEngine.Object asset in AssetDatabase.LoadAllAssetsAtPath(path))
                        if (TracksUnsavedSourceObject(asset) && EditorUtility.IsDirty(asset))
                        { reason = "Save the changed town dependency before baking: " + path; return false; }
                string resolved = SourcePath(path);
                if (!File.Exists(resolved))
                {
                    // Unity built-in resources have no project source file and are shared by the game
                    // editor version. Record their stable identity, never an import-cache/platform hash.
                    if (path.StartsWith("Resources/", StringComparison.Ordinal) || path.StartsWith("Library/", StringComparison.Ordinal))
                    { hashes.Add(new KeyValuePair<string,string>(path, HashUtf8("unity-built-in:" + path))); continue; }
                    reason = "Cannot fingerprint missing town dependency: " + path; return false;
                }
                string fileHash;
                if (path == CoastalCityLayout.BlueprintPath || path == "Assets/Resources/City/CoastalYards.json")
                    fileHash = HashUtf8(JToken.Parse(File.ReadAllText(resolved)).ToString(Formatting.None));
                else fileHash = HashFile(resolved);
                hashes.Add(new KeyValuePair<string,string>(path, fileHash));
                if (File.Exists(resolved + ".meta")) hashes.Add(new KeyValuePair<string,string>(path + ".meta", HashFile(resolved + ".meta")));
            }
            dependencyCount = hashes.Count; digest = HashManifest(hashes); return true;
        }
        catch (Exception e) { reason = "Cannot verify published town inputs: " + e.Message; return false; }
    }

    /// <summary>Imported FBX meshes/materials and texture outputs are importer-owned caches, not saveable
    /// source assets. Unity can mark those NotEditable outputs dirty merely while preparing shaders/previews.
    /// Their source file and importer .meta still participate in the digest; actual unapplied importer
    /// settings are checked separately. Native .mat/.asset/.prefab edits remain blocking and untouched.</summary>
    public static bool TracksUnsavedSourceObject(UnityEngine.Object asset) =>
        asset && EditorUtility.IsPersistent(asset) &&
        (AssetDatabase.IsNativeAsset(asset) || AssetDatabase.GetAssetPath(asset).EndsWith(".prefab", StringComparison.OrdinalIgnoreCase));

    /// <summary>Classify from importer metadata before loading any asset objects. Scenes are handled by
    /// SceneManager dirty checks and source-file hashing only; foreign import outputs are never loaded.</summary>
    public static bool ShouldInspectNativeObjects(string assetPath, string importerTypeName)
    {
        if (string.IsNullOrEmpty(assetPath) || assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) return false;
        // Unity 6's native .mat/.asset importer has no specialised managed wrapper: GetType() reports
        // AssetImporter even though its .meta heading says NativeFormatImporter. Regular prefab assets
        // likewise report IsNativeAsset=false, so their explicit PrefabImporter path stays included.
        return importerTypeName == "AssetImporter" || importerTypeName == "NativeFormatImporter" || importerTypeName == "PrefabImporter";
    }

    static string SourcePath(string assetPath)
    {
        if (File.Exists(assetPath)) return assetPath;
        if (!assetPath.StartsWith("Packages/", StringComparison.Ordinal)) return assetPath;
        var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
        if (package == null) return assetPath;
        string prefix = "Packages/" + package.name + "/";
        return assetPath.StartsWith(prefix, StringComparison.Ordinal)
            ? Path.Combine(package.resolvedPath, assetPath.Substring(prefix.Length)) : assetPath;
    }

    /// <summary>Stable framed digest of dependency source hashes. Public to permit deterministic unit tests
    /// without touching a scene, asset database or the user's publication resource.</summary>
    public static string HashManifest(IEnumerable<KeyValuePair<string,string>> sources)
    {
        if (sources == null) throw new ArgumentNullException(nameof(sources));
        var entries = sources.Where(p => p.Key != AssetPath && p.Key != AssetPath + ".meta")
            .OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
        using (var buffer = new MemoryStream())
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, true))
        {
            writer.Write("Rubbish.PublishedTown"); writer.Write(PublishedTownSignature.Schema);
            string previous = null;
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.Key) || entry.Value == null) throw new ArgumentException("Manifest entries need a path and source hash.");
                if (entry.Key == previous) throw new ArgumentException("Duplicate town dependency " + entry.Key);
                writer.Write(entry.Key); writer.Write(entry.Value); previous = entry.Key;
            }
            writer.Flush(); buffer.Position = 0;
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(buffer));
        }
    }

    static string HashFile(string path)
    { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(stream)); }
    static string HashUtf8(string text)
    { using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text))); }
    static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
}
