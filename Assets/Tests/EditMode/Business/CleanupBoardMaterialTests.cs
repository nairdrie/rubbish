using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>The street board must use a player-included URP material, not a primitive's built-in default.</summary>
public class CleanupBoardMaterialTests
{
    const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    static Type BoardType => Type.GetType("CleanupBoard, Assembly-CSharp", true);

    [Test]
    public void BoardMaterialIsSerializedUrpResourceWithAnExplicitShaderDependency()
    {
        var material = Resources.Load<Material>("CleanupBoard");
        Assert.NotNull(material, "A Resources asset retains the board's material and shader in Windows builds.");
        Assert.IsTrue(EditorUtility.IsPersistent(material));
        Assert.AreEqual("Assets/Resources/CleanupBoard.mat", AssetDatabase.GetAssetPath(material));
        Assert.NotNull(material.shader);
        Assert.AreEqual("Universal Render Pipeline/Lit", material.shader.name);
        Assert.AreEqual("UniversalPipeline", material.GetTag("RenderPipeline", false));
        CollectionAssert.Contains(AssetDatabase.GetDependencies(AssetDatabase.GetAssetPath(material), true),
            AssetDatabase.GetAssetPath(material.shader));
        Assert.That(material.GetFloat("_Surface"), Is.Zero, "The board stays opaque.");
        Assert.That(material.GetColor("_BaseColor"), Is.EqualTo(Color.white), "Each board part supplies its own tint.");
    }

    [Test]
    public void ProductionBoardUsesIncludedMaterialAndPreservesItsAuthoredColours()
    {
        var host = new GameObject("Cleanup board material regression test");
        var component = host.AddComponent(BoardType);
        var boardField = BoardType.GetField("board", PrivateInstance);
        try
        {
            BoardType.GetMethod("Build", PrivateInstance).Invoke(component, null);
            var board = (GameObject)boardField.GetValue(component);
            Assert.NotNull(board);
            var material = Resources.Load<Material>("CleanupBoard");
            var names = new[] { "PostL", "PostR", "Panel", "Trim", "Lamp" };
            var colours = new[]
            {
                new Color(.35f, .35f, .38f), new Color(.35f, .35f, .38f),
                new Color(.12f, .42f, .18f), new Color(1f, .82f, .2f), new Color(1f, .3f, .2f)
            };
            for (int i = 0; i < names.Length; i++)
            {
                var renderer = board.transform.Find(names[i]).GetComponent<Renderer>();
                Assert.AreSame(material, renderer.sharedMaterial, names[i] + " cannot inherit the default cube material.");
                var properties = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(properties);
                var tint = properties.GetColor("_BaseColor");
                Assert.That(Vector4.Distance((Vector4)colours[i], (Vector4)tint), Is.LessThan(.0001f), names[i]);
            }
            foreach (string name in new[] { "Title", "Body" })
            {
                var renderer = board.transform.Find(name).GetComponent<Renderer>();
                Assert.NotNull(renderer.sharedMaterial, name + " retains its separate text material.");
                Assert.AreNotSame(material, renderer.sharedMaterial);
            }
        }
        finally
        {
            var board = boardField.GetValue(component) as GameObject;
            if (board != null) Object.DestroyImmediate(board);
            Object.DestroyImmediate(host);
        }
    }
}
