using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using StarterAssets;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Co-op prefab hygiene (Docs/Multiplayer.md §8.7.2 D5, §8.7.4 package B2). Idempotent: every value is compared first and a
/// prefab or asset is saved only when something actually changed, so a second run writes nothing.
/// <list type="number">
/// <item><b>NetSession.prefab</b> (<see cref="SessionPrefabPath"/>): a NetworkObject plus one component for every class marked
/// <see cref="NetSessionComponentAttribute"/> (found with <c>TypeCache</c>, ordered by <c>Order</c>, then name), so packages
/// never edit this script. Registered in <c>RubbishNetworkPerfabsList</c> (the list Bootstrap's NetworkManager uses) and in
/// <c>RubbishAssets</c> as <see cref="SessionAssetKey"/>. The host spawns it with <c>Spawn(destroyWithScene: false)</c> (B1).
/// Session components keep no inspector values: when the set changes, the component list is rebuilt in order.</item>
/// <item><b>Network prefab lists:</b> drops entries whose prefab is gone (the dangling <c>fd2bce17…</c> in
/// <c>DefaultNetworkPrefabs.asset</c>, §8.7.1 finding 7).</item>
/// <item><b>Truck V2.prefab:</b> <c>DontDestroyWithOwner</c> (a guest driver's game dropping hands the truck back to the
/// host instead of destroying it), half-float positions + quaternion sync and compression, and <see cref="TruckNetFeel"/>.</item>
/// <item><b>Player.prefab</b> (after the Chuck build): the same NetworkTransform flags; <c>PlayerInput</c> off by default
/// (<see cref="CoopPlayerHooks"/> turns it on for the owner only); <c>AllowOwnerToParent</c> so a guest can sit in the truck
/// (TruckControllerV2 parents the seated player under the truck; without it NGO reverts that on guests and logs an error);
/// and <see cref="RosterView"/>.</item>
/// </list>
/// Menu <c>Rubbish/Setup/Coop</c>; batch: <c>-executeMethod Setup_Coop.Run</c> (the bot build runs it in the build copy; in
/// batchmode a failure throws, so the build stops). <see cref="Verify"/> reads every flag back without writing anything.
/// Prefab edits only (LoadPrefabContents / SaveAsPrefabAsset; the new NetSession prefab is assembled in a preview scene);
/// never opens, dirties or saves a scene.
/// </summary>
public static class Setup_Coop
{
    public const string PlayerPrefabPath = "Assets/Prefabs/Player.prefab";
    public const string TruckPrefabPath = "Assets/Prefabs/Truck V2.prefab";
    public const string SessionPrefabPath = "Assets/Prefabs/Net/NetSession.prefab";
    public const string SessionAssetKey = "net/session";
    public const string RuntimePrefabListPath = "Assets/Prefabs/RubbishNetworkPerfabsList.asset";
    public const string DefaultPrefabListPath = "Assets/DefaultNetworkPrefabs.asset";
    const string Tag = "[Setup_Coop]";

    /// <summary>Menu and <c>-executeMethod</c> entry. Throws in batchmode when a step failed.</summary>
    [MenuItem("Rubbish/Setup/Coop")]
    public static void Run()
    {
        string summary = Build(out bool failed, out _);
        if (failed && Application.isBatchMode) throw new InvalidOperationException(summary);
    }

    /// <summary>Runs every step and returns the summary it also logs ("no changes." on a second run).</summary>
    public static string Build() => Build(out _, out _);

    public static string Build(out bool failed, out int changes)
    {
        var log = new StringBuilder(Tag + " ");
        changes = 0;
        failed = false;
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            failed = true;
            Debug.LogError(Tag + " Exit play mode first.");
            return Tag + " play mode";
        }
        var openScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        bool sceneWasDirty = openScene.isDirty;
        try
        {
            var session = EnsureSessionPrefab(log, ref changes, ref failed);
            if (session != null)
            {
                RegisterInList(RuntimePrefabListPath, session, log, ref changes, ref failed);
                RegisterInRubbishAssets(session, log, ref changes);
            }
            DropMissingEntries(DefaultPrefabListPath, log, ref changes);
            DropMissingEntries(RuntimePrefabListPath, log, ref changes);
            EditPrefab(TruckPrefabPath, ApplyTruck, log, ref changes, ref failed);
            EditPrefab(PlayerPrefabPath, ApplyPlayer, log, ref changes, ref failed);
            if (changes > 0) AssetDatabase.SaveAssets();
        }
        catch (Exception e)
        {
            failed = true;
            log.Append("EXCEPTION ").Append(e.Message).Append("; ");
            Debug.LogException(e);
        }
        if (!sceneWasDirty && openScene.IsValid() && openScene.isDirty)
            log.Append($"WARNING the open scene {openScene.name} got marked dirty during the run; don't save it; ");
        log.Append(changes == 0 ? "no changes." : $"{changes} change(s) saved.");
        if (failed) Debug.LogError(log.ToString());
        else Debug.Log(log.ToString());
        return log.ToString();
    }

    // =====================================================================
    // NetSession.prefab
    // =====================================================================

    /// <summary>Every <see cref="NetSessionComponentAttribute"/> NetworkBehaviour, in prefab order.</summary>
    public static List<Type> SessionComponentTypes(StringBuilder log = null)
    {
        var list = new List<Type>();
        foreach (var t in TypeCache.GetTypesWithAttribute<NetSessionComponentAttribute>())
        {
            if (t.IsAbstract || t.IsGenericTypeDefinition || !typeof(NetworkBehaviour).IsAssignableFrom(t))
            {
                log?.Append($"skipped [NetSessionComponent] {t.FullName} (not a concrete NetworkBehaviour); ");
                continue;
            }
            list.Add(t);
        }
        list.Sort((a, b) =>
        {
            int c = OrderOf(a).CompareTo(OrderOf(b));
            return c != 0 ? c : string.CompareOrdinal(a.FullName, b.FullName);
        });
        return list;
    }

    static int OrderOf(Type t)
    {
        var a = (NetSessionComponentAttribute)Attribute.GetCustomAttribute(t, typeof(NetSessionComponentAttribute), false);
        return a != null ? a.Order : 0;
    }

    static GameObject EnsureSessionPrefab(StringBuilder log, ref int changes, ref bool failed)
    {
        var types = SessionComponentTypes(log);
        EnsureFolder(System.IO.Path.GetDirectoryName(SessionPrefabPath).Replace('\\', '/'));
        bool exists = AssetDatabase.LoadAssetAtPath<GameObject>(SessionPrefabPath) != null;

        // A brand-new prefab is assembled in a preview scene, so the open scene (Bootstrap) is never dirtied. (Not
        // ObjectFactory.CreateGameObject: it registers an Undo, and that marks the active scene dirty.)
        var preview = default(UnityEngine.SceneManagement.Scene);
        GameObject root;
        if (exists) root = PrefabUtility.LoadPrefabContents(SessionPrefabPath);
        else
        {
            preview = EditorSceneManager.NewPreviewScene();
            root = new GameObject("NetSession");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, preview);
        }
        GameObject result = null;
        try
        {
            bool dirty = !exists;
            var no = root.GetComponent<NetworkObject>();
            if (no == null) { no = root.AddComponent<NetworkObject>(); dirty = true; }
            // Lives for the whole session (destroyWithScene: false), owned by the host, never parented.
            dirty |= Set(ref no.DontDestroyWithOwner, true);
            dirty |= Set(ref no.AutoObjectParentSync, false);
            dirty |= Set(ref no.SpawnWithObservers, true);
            dirty |= Set(ref no.ActiveSceneSynchronization, false);
            if (root.transform.localPosition != Vector3.zero) { root.transform.localPosition = Vector3.zero; dirty = true; }

            // Current session components (anything but the NetworkObject; a null entry is a missing script).
            var current = root.GetComponents<MonoBehaviour>().Where(c => !(c is NetworkObject)).ToList();
            bool same = current.Count == types.Count;
            for (int i = 0; same && i < types.Count; i++) same = current[i] != null && current[i].GetType() == types[i];
            if (!same)
            {
                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(root);
                foreach (var c in root.GetComponents<MonoBehaviour>())
                    if (c != null && !(c is NetworkObject)) UnityEngine.Object.DestroyImmediate(c, true);
                foreach (var t in types) root.AddComponent(t);
                dirty = true;
            }
            log.Append($"NetSession [{string.Join(", ", types.Select(t => t.Name))}]");
            if (!dirty)
            {
                log.Append(" ok; ");
                result = AssetDatabase.LoadAssetAtPath<GameObject>(SessionPrefabPath);
            }
            else
            {
                var saved = PrefabUtility.SaveAsPrefabAsset(root, SessionPrefabPath, out bool ok);
                if (!ok || saved == null)
                {
                    failed = true;
                    log.Append(" SAVE FAILED; ");
                    return null;
                }
                changes++;
                log.Append(exists ? " updated; " : " created; ");
                result = saved;
            }
        }
        finally
        {
            if (exists) PrefabUtility.UnloadPrefabContents(root);
            else
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                if (preview.IsValid()) EditorSceneManager.ClosePreviewScene(preview);
            }
        }
        EnsurePrefabHash(result, log, ref changes, ref failed);
        return result;
    }

    /// <summary>
    /// The prefab's <c>GlobalObjectIdHash</c> must be the one NGO derives from the asset's GlobalObjectId, or clients can't
    /// match the spawn. NGO sets it in <c>OnValidate</c> when the asset loads, but for a prefab created by script that value
    /// can stay in memory only (the file keeps 0, and a build would ship 0). So the value in the file is checked, and the
    /// prefab is re-saved when it differs.
    /// </summary>
    static void EnsurePrefabHash(GameObject prefab, StringBuilder log, ref int changes, ref bool failed)
    {
        if (prefab == null) return;
        var no = prefab.GetComponent<NetworkObject>();
        uint want = ExpectedPrefabHash(no);
        if (no == null || want == 0)
        {
            log.Append("WARNING couldn't check the NetSession prefab hash; ");
            return;
        }
        if (no.PrefabIdHash != want)
            typeof(NetworkObject).GetMethod("OnValidate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                ?.Invoke(no, null);
        if (no.PrefabIdHash != want)
        {
            failed = true;
            log.Append($"NetSession prefab hash {no.PrefabIdHash} != expected {want}; ");
            return;
        }
        string path = AssetDatabase.GetAssetPath(prefab);
        if (HashOnDisk(path) == want) return;
        EditorUtility.SetDirty(no);
        PrefabUtility.SavePrefabAsset(prefab, out bool ok);
        uint disk = HashOnDisk(path);
        if (!ok || disk != want)
        {
            failed = true;
            log.Append($"NetSession prefab hash on disk {disk} != expected {want}; ");
            return;
        }
        changes++;
        log.Append($"NetSession prefab hash {want} saved; ");
    }

    /// <summary>The first <c>GlobalObjectIdHash</c> written in a prefab file (0 if none).</summary>
    public static uint HashOnDisk(string assetPath)
    {
        string full = System.IO.Path.GetFullPath(assetPath);
        if (!System.IO.File.Exists(full)) return 0;
        var m = System.Text.RegularExpressions.Regex.Match(System.IO.File.ReadAllText(full), @"\n\s*GlobalObjectIdHash:\s*(\d+)");
        return m.Success && uint.TryParse(m.Groups[1].Value, out uint v) ? v : 0;
    }

    /// <summary>NGO's hash for a prefab asset: XXHash32 of its GlobalObjectId string (0 if it can't be computed).</summary>
    public static uint ExpectedPrefabHash(NetworkObject no)
    {
        if (no == null) return 0;
        var id = GlobalObjectId.GetGlobalObjectIdSlow(no);
        var hash32 = typeof(NetworkObject).Assembly.GetType("Unity.Netcode.XXHash")
            ?.GetMethod("Hash32", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
        if (hash32 == null) return 0;
        return (uint)hash32.Invoke(null, new object[] { id.ToString() });
    }

    static void RegisterInList(string listPath, GameObject prefab, StringBuilder log, ref int changes, ref bool failed)
    {
        var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(listPath);
        if (list == null)
        {
            failed = true;
            log.Append($"MISSING {listPath}; ");
            return;
        }
        if (list.Contains(prefab)) return;
        list.Add(new NetworkPrefab { Prefab = prefab });
        EditorUtility.SetDirty(list);
        changes++;
        log.Append($"registered {prefab.name} in {System.IO.Path.GetFileNameWithoutExtension(listPath)}; ");
    }

    static void RegisterInRubbishAssets(GameObject prefab, StringBuilder log, ref int changes)
    {
        var assets = RubbishAssets.EditorGetOrCreate();
        int hits = 0;
        bool match = false;
        foreach (var e in assets.prefabs)
            if (e.key == SessionAssetKey) { hits++; match = e.prefab == prefab; }
        if (hits == 1 && match) return;
        RubbishAssets.EditorRegisterPrefab(SessionAssetKey, prefab);
        changes++;
        log.Append($"RubbishAssets {SessionAssetKey}; ");
    }

    /// <summary>
    /// Remove plain entries whose prefab asset no longer exists (overrides are left alone). NGO's prefab processor may already
    /// have dropped such an entry from the loaded list without marking it dirty, so the file on disk is checked too: a
    /// <c>Prefab:</c> GUID that no longer resolves gets the list re-saved.
    /// </summary>
    static void DropMissingEntries(string listPath, StringBuilder log, ref int changes)
    {
        var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(listPath);
        if (list == null) return;
        var dead = list.PrefabList.Where(p => p != null && p.Override == NetworkPrefabOverride.None && p.Prefab == null).ToList();
        foreach (var p in dead) list.Remove(p);
        var staleOnDisk = DanglingPrefabGuids(listPath);
        if (dead.Count == 0 && staleOnDisk.Count == 0) return;
        EditorUtility.SetDirty(list);
        changes++;
        log.Append($"dropped {Math.Max(dead.Count, staleOnDisk.Count)} missing prefab(s) from " +
                   $"{System.IO.Path.GetFileNameWithoutExtension(listPath)}" +
                   (staleOnDisk.Count > 0 ? $" ({string.Join(", ", staleOnDisk)})" : "") + "; ");
    }

    /// <summary><c>Prefab:</c> GUIDs in a prefab list's file that no longer resolve to an asset.</summary>
    public static List<string> DanglingPrefabGuids(string listPath)
    {
        var result = new List<string>();
        string full = System.IO.Path.GetFullPath(listPath);
        if (!System.IO.File.Exists(full)) return result;
        var rx = new System.Text.RegularExpressions.Regex(@"^\s*Prefab:\s*\{[^}]*guid:\s*([0-9a-f]{32})");
        foreach (var line in System.IO.File.ReadAllLines(full))
        {
            var m = rx.Match(line);
            if (!m.Success) continue;
            string guid = m.Groups[1].Value;
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path) || AssetDatabase.LoadMainAssetAtPath(path) == null) result.Add(guid);
        }
        return result;
    }

    // =====================================================================
    // Truck and player
    // =====================================================================

    delegate bool PrefabEdit(GameObject root, StringBuilder log);

    static void EditPrefab(string path, PrefabEdit edit, StringBuilder log, ref int changes, ref bool failed)
    {
        if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null)
        {
            failed = true;
            log.Append($"MISSING {path}; ");
            return;
        }
        var root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            if (!edit(root, log)) return;
            PrefabUtility.SaveAsPrefabAsset(root, path, out bool ok);
            if (!ok)
            {
                failed = true;
                log.Append($"SAVE FAILED {path}; ");
                return;
            }
            changes++;
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }

    /// <summary>Half-float positions with quaternion sync + compression (§8.7.1 finding 8). Returns true if anything changed.</summary>
    static bool ApplyTransformFlags(GameObject root, StringBuilder log)
    {
        var nt = root.GetComponent<NetworkTransform>();
        if (nt == null)
        {
            log.Append($"WARNING {root.name} has no NetworkTransform; ");
            return false;
        }
        bool dirty = false;
        dirty |= Set(ref nt.UseHalfFloatPrecision, true);
        dirty |= Set(ref nt.UseQuaternionSynchronization, true);
        dirty |= Set(ref nt.UseQuaternionCompression, true);
        dirty |= Set(ref nt.Interpolate, true);
        return dirty;
    }

    static bool ApplyTruck(GameObject root, StringBuilder log)
    {
        bool dirty = ApplyTransformFlags(root, log);
        var no = root.GetComponent<NetworkObject>();
        if (no != null) dirty |= Set(ref no.DontDestroyWithOwner, true);
        else log.Append("WARNING truck has no NetworkObject; ");
        if (root.GetComponent<TruckNetFeel>() == null) { root.AddComponent<TruckNetFeel>(); dirty = true; }
        log.Append(dirty ? "truck updated; " : "truck ok; ");
        return dirty;
    }

    static bool ApplyPlayer(GameObject root, StringBuilder log)
    {
        if (root.GetComponent<ChuckPuppet>() == null)
            log.Append("WARNING Player has no ChuckPuppet (run Rubbish/Setup/Character first for the Dumpling variants); ");
        bool dirty = ApplyTransformFlags(root, log);
        var no = root.GetComponent<NetworkObject>();
        if (no != null) dirty |= Set(ref no.AllowOwnerToParent, true);
        else log.Append("WARNING Player has no NetworkObject; ");
        var pi = root.GetComponent<PlayerInput>();
        if (pi != null && pi.enabled) { pi.enabled = false; dirty = true; }
        if (root.GetComponent<RosterView>() == null) { root.AddComponent<RosterView>(); dirty = true; }
        log.Append(dirty ? "player updated; " : "player ok; ");
        return dirty;
    }

    // =====================================================================
    // Verify (read-only)
    // =====================================================================

    /// <summary>
    /// Reads every flag this setup owns back from the assets on disk. Returns one line per check, "OK …" or "FAIL …";
    /// <paramref name="ok"/> is false if any check failed. Writes nothing.
    /// </summary>
    public static string Verify(out bool ok)
    {
        var sb = new StringBuilder();
        bool all = true;
        void Check(bool pass, string what)
        {
            all &= pass;
            sb.Append(pass ? "OK   " : "FAIL ").AppendLine(what);
        }

        var session = AssetDatabase.LoadAssetAtPath<GameObject>(SessionPrefabPath);
        Check(session != null, $"{SessionPrefabPath} exists");
        if (session != null)
        {
            var no = session.GetComponent<NetworkObject>();
            Check(no != null, "NetSession has a NetworkObject");
            if (no != null)
            {
                Check(no.DontDestroyWithOwner && !no.AutoObjectParentSync && no.SpawnWithObservers && !no.ActiveSceneSynchronization,
                    $"NetSession NetworkObject flags (DDWO={no.DontDestroyWithOwner} parentSync={no.AutoObjectParentSync} " +
                    $"observers={no.SpawnWithObservers} sceneSync={no.ActiveSceneSynchronization})");
                uint want = ExpectedPrefabHash(no);
                uint disk = HashOnDisk(SessionPrefabPath);
                Check(want != 0 && no.PrefabIdHash == want && disk == want,
                    $"NetSession prefab hash loaded={no.PrefabIdHash} file={disk} expected={want}");
            }
            var types = SessionComponentTypes();
            var have = session.GetComponents<MonoBehaviour>().Where(c => !(c is NetworkObject)).Select(c => c == null ? null : c.GetType()).ToList();
            Check(have.SequenceEqual(types),
                $"NetSession components [{string.Join(", ", have.Select(t => t == null ? "<missing>" : t.Name))}] == " +
                $"[NetSessionComponent] types [{string.Join(", ", types.Select(t => t.Name))}]");
            var runtime = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(RuntimePrefabListPath);
            Check(runtime != null && runtime.Contains(session), "NetSession registered in RubbishNetworkPerfabsList");
            var assets = AssetDatabase.LoadAssetAtPath<RubbishAssets>(RubbishAssets.AssetPath);
            int keyHits = assets == null ? 0 : assets.prefabs.Count(e => e.key == SessionAssetKey && e.prefab == session);
            int keyAll = assets == null ? 0 : assets.prefabs.Count(e => e.key == SessionAssetKey);
            Check(keyHits == 1 && keyAll == 1, $"RubbishAssets '{SessionAssetKey}' → NetSession (entries {keyAll})");
        }
        foreach (var listPath in new[] { DefaultPrefabListPath, RuntimePrefabListPath })
        {
            var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(listPath);
            int dead = list == null ? -1 : list.PrefabList.Count(p => p != null && p.Override == NetworkPrefabOverride.None && p.Prefab == null);
            var onDisk = DanglingPrefabGuids(listPath);
            Check(dead == 0 && onDisk.Count == 0,
                $"{System.IO.Path.GetFileName(listPath)}: no missing prefabs (loaded={dead}, on disk={onDisk.Count}" +
                (onDisk.Count > 0 ? " " + string.Join(",", onDisk) : "") + ")");
        }

        var truck = AssetDatabase.LoadAssetAtPath<GameObject>(TruckPrefabPath);
        Check(truck != null, $"{TruckPrefabPath} exists");
        if (truck != null)
        {
            VerifyTransform(truck, Check);
            var no = truck.GetComponent<NetworkObject>();
            Check(no != null && no.DontDestroyWithOwner, $"truck DontDestroyWithOwner={no != null && no.DontDestroyWithOwner}");
            Check(truck.GetComponent<TruckNetFeel>() != null, "truck has TruckNetFeel");
            VerifyHash(truck, TruckPrefabPath, Check);
            var rb = truck.GetComponent<Rigidbody>();
            Check(rb != null && rb.interpolation == RigidbodyInterpolation.None,
                $"truck prefab Rigidbody interpolation {(rb != null ? rb.interpolation.ToString() : "-")} (None; TruckNetFeel turns it on for the driver)");
        }

        var player = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
        Check(player != null, $"{PlayerPrefabPath} exists");
        if (player != null)
        {
            VerifyTransform(player, Check);
            var no = player.GetComponent<NetworkObject>();
            Check(no != null && no.AllowOwnerToParent && !no.DontDestroyWithOwner,
                $"player AllowOwnerToParent={no != null && no.AllowOwnerToParent} DontDestroyWithOwner={no != null && no.DontDestroyWithOwner}");
            var pi = player.GetComponent<PlayerInput>();
            Check(pi != null && !pi.enabled, $"player PlayerInput present and off by default (enabled={pi != null && pi.enabled})");
            Check(player.GetComponent<StarterAssetsInputs>() != null, "player has StarterAssetsInputs");
            Check(player.GetComponent<RosterView>() != null, "player has RosterView");
            VerifyHash(player, PlayerPrefabPath, Check);
            Check(player.GetComponent<ChuckPuppet>() != null, "player has ChuckPuppet (Chuck integration landed)");
        }
        ok = all;
        return sb.ToString();
    }

    static void VerifyHash(GameObject root, string path, Action<bool, string> check)
    {
        var no = root.GetComponent<NetworkObject>();
        uint want = ExpectedPrefabHash(no), disk = HashOnDisk(path);
        check(want != 0 && no.PrefabIdHash == want && disk == want,
            $"{root.name} prefab hash loaded={(no != null ? no.PrefabIdHash : 0)} file={disk} expected={want}");
    }

    static void VerifyTransform(GameObject root, Action<bool, string> check)
    {
        var nt = root.GetComponent<NetworkTransform>();
        check(nt != null, $"{root.name} has a NetworkTransform");
        if (nt == null) return;
        check(nt.UseHalfFloatPrecision && nt.UseQuaternionSynchronization && nt.UseQuaternionCompression && nt.Interpolate,
            $"{root.name} NetworkTransform half={nt.UseHalfFloatPrecision} quatSync={nt.UseQuaternionSynchronization} " +
            $"quatComp={nt.UseQuaternionCompression} interpolate={nt.Interpolate}");
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    static bool Set(ref bool field, bool value)
    {
        if (field == value) return false;
        field = value;
        return true;
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
    }
}
