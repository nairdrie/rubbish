using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Transactions operate only on a unique scratch scene/assets; never on the user's actual city.</summary>
public class CityEditTransactionTests
{
    static Type Transaction => Type.GetType("CityEditTransaction, Assembly-CSharp-Editor", true);
    string folder;
    Scene scene;
    SceneSetup[] previousSetup;
    GameObject root;

    [SetUp]
    public void SetUp()
    {
        // Match Unity's scene-based EditMode examples: additive NewScene is prohibited while the runner
        // owns an untitled scene. Never discard user edits; the test runner's empty scene is disposable.
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var loaded = SceneManager.GetSceneAt(i);
            Assert.IsFalse(!string.IsNullOrEmpty(loaded.path) && loaded.isDirty,
                "Transaction tests will not replace a dirty saved scene: " + loaded.path);
        }
        previousSetup = EditorSceneManager.GetSceneManagerSetup();
        string name = "__CityTransactionTest_" + Guid.NewGuid().ToString("N");
        AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
        scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        root = new GameObject("Preserved authored lot"); SceneManager.MoveGameObjectToScene(root, scene);
        root.transform.position = new Vector3(2, 3, 4); root.AddComponent<Light>().intensity = 2;
        Assert.IsTrue(EditorSceneManager.SaveScene(scene, folder + "/Town.unity"));
    }

    [TearDown]
    public void TearDown()
    {
        if (previousSetup != null && previousSetup.Length > 0) EditorSceneManager.RestoreSceneManagerSetup(previousSetup);
        else if (scene.IsValid() && scene.isLoaded) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        if (!string.IsNullOrEmpty(folder) && AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
    }

    IDisposable Begin() => (IDisposable)Activator.CreateInstance(Transaction, new object[] { new[] { scene } });
    static object Call(object transaction, string name, params object[] args) => Transaction.GetMethod(name).Invoke(transaction, args);
    static void Static(string name, params object[] args) => Transaction.GetMethod(name).Invoke(null, args);

    Mesh MeshAsset(string name)
    {
        var mesh = new Mesh { name = name, vertices = new[] { Vector3.zero, Vector3.right, Vector3.forward }, triangles = new[] { 0, 1, 2 } };
        AssetDatabase.CreateAsset(mesh, folder + "/" + name + ".asset");
        AssetDatabase.SaveAssetIfDirty(mesh); return mesh;
    }

    [Test]
    public void FailureRestoresAuthoredTransformsAndFieldsAndRemovesCreatedGroups()
    {
        using (var transaction = Begin())
        {
            root.transform.position = Vector3.one * 90; root.GetComponent<Light>().intensity = 7;
            var created = new GameObject("New generated road"); created.transform.SetParent(root.transform, false);
            Static("RegisterCreated", created);
            new GameObject("Generated road geometry").transform.SetParent(created.transform, false);
        }
        Assert.AreEqual(new Vector3(2, 3, 4), root.transform.position);
        Assert.AreEqual(2, root.GetComponent<Light>().intensity);
        Assert.AreEqual(0, root.transform.childCount);
        Assert.IsNull(Transaction.GetProperty("Current").GetValue(null));
    }

    [Test]
    public void ReplacedGeneratedRoadIsRestoredWithItsChildrenOnFailure()
    {
        var old = new GameObject("Old generated road"); old.transform.SetParent(root.transform, false);
        var detail = new GameObject("Keep geometry"); detail.transform.SetParent(old.transform, false); detail.transform.localPosition = Vector3.forward * 12;
        Assert.IsTrue(EditorSceneManager.SaveScene(scene));
        using (var transaction = Begin()) Static("DestroyGenerated", old);
        var restored = root.transform.Find("Old generated road/Keep geometry");
        Assert.NotNull(restored); Assert.AreEqual(Vector3.forward * 12, restored.localPosition);
    }

    [Test]
    public void FailureAfterWritingRestoresMeshAndSceneBytesAndExactLiveMesh()
    {
        var mesh = MeshAsset("Shared road mesh");
        var filter = root.AddComponent<MeshFilter>(); filter.sharedMesh = mesh;
        Assert.IsTrue(EditorSceneManager.SaveScene(scene));
        string meshPath = AssetDatabase.GetAssetPath(mesh);
        byte[] sceneBytes = File.ReadAllBytes(scene.path), meshBytes = File.ReadAllBytes(meshPath);
        using (var transaction = Begin())
        {
            Call(transaction, "WatchAsset", meshPath);
            mesh.vertices = new[] { Vector3.one * 80, Vector3.one * 90, Vector3.one * 100 };
            EditorUtility.SetDirty(mesh); AssetDatabase.SaveAssetIfDirty(mesh);
            root.transform.position = Vector3.one * 400;
            Assert.IsTrue(EditorSceneManager.SaveScene(scene));
            // A later map bake fails: Dispose must undo already-published outputs too.
        }
        CollectionAssert.AreEqual(sceneBytes, File.ReadAllBytes(scene.path));
        CollectionAssert.AreEqual(meshBytes, File.ReadAllBytes(meshPath));
        CollectionAssert.AreEqual(new[] { Vector3.zero, Vector3.right, Vector3.forward }, mesh.vertices);
        Assert.AreEqual("Shared road mesh", mesh.name);
        // Import/Undo can return a fresh managed wrapper for the same native Unity object.
        Assert.IsTrue(filter.sharedMesh == mesh, "The live MeshFilter must still reference the original native mesh.");
        Assert.AreEqual(mesh.GetInstanceID(), filter.sharedMesh.GetInstanceID());
        Assert.AreEqual(new Vector3(2, 3, 4), root.transform.position);
    }

    [Test]
    public void FailureDeletesOnlyNewWatchedAssetsNotOtherAssets()
    {
        var unrelated = MeshAsset("Unrelated kept asset");
        string added = folder + "/New road.asset";
        using (var transaction = Begin())
        {
            Call(transaction, "WatchAsset", added);
            var mesh = new Mesh { name = "New road" }; AssetDatabase.CreateAsset(mesh, added);
        }
        Assert.IsFalse(File.Exists(added)); Assert.IsFalse(File.Exists(added + ".meta"));
        Assert.AreSame(unrelated, AssetDatabase.LoadAssetAtPath<Mesh>(folder + "/Unrelated kept asset.asset"));
    }

    [Test]
    public void CommitKeepsChangesAndReleasesTheTransaction()
    {
        using (var transaction = Begin())
        {
            root.transform.position = Vector3.one * 15;
            Call(transaction, "Commit");
        }
        Assert.AreEqual(Vector3.one * 15, root.transform.position);
        Assert.IsNull(Transaction.GetProperty("Current").GetValue(null));
    }

    [Test]
    public void DirtySceneIsRejectedBeforeUndoOrMutation()
    {
        root.transform.position = Vector3.one * 20; EditorSceneManager.MarkSceneDirty(scene);
        var error = Assert.Throws<TargetInvocationException>(() => Begin());
        Assert.IsInstanceOf<InvalidOperationException>(error.InnerException);
        Assert.AreEqual(Vector3.one * 20, root.transform.position);
        Assert.IsNull(Transaction.GetProperty("Current").GetValue(null));
    }

    [Test]
    public void WatchedAssetSaveDoesNotFlushUnrelatedDirtyArtwork()
    {
        var road = MeshAsset("Road"); var art = MeshAsset("Unrelated artwork");
        string artPath = AssetDatabase.GetAssetPath(art); byte[] before = File.ReadAllBytes(artPath);
        art.vertices = new[] { Vector3.one, Vector3.right, Vector3.forward }; EditorUtility.SetDirty(art);
        using (var transaction = Begin())
        {
            Call(transaction, "WatchAsset", AssetDatabase.GetAssetPath(road));
            road.vertices = new[] { Vector3.up, Vector3.right, Vector3.forward }; EditorUtility.SetDirty(road);
            Call(transaction, "SaveWatchedAssets"); Call(transaction, "Commit");
        }
        CollectionAssert.AreEqual(before, File.ReadAllBytes(artPath)); Assert.IsTrue(EditorUtility.IsDirty(art));
    }
}
