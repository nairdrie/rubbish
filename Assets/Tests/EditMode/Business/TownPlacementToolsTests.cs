using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public class TownPlacementToolsTests
{
    static Type Tools => Type.GetType("TownPlacementTools, Assembly-CSharp-Editor", true);
    static object Call(string name, params object[] args) => Tools.GetMethod(name).Invoke(null, args);

    [Test] public void GridSnapKeepsGroundHeightAndHandlesNegativeCoordinates()
    {
        var p = (Vector3)Call("Snap", new Vector3(-3.6f, 2.75f, 7.3f), 2f);
        Assert.AreEqual(new Vector3(-4, 2.75f, 8), p);
    }
    [Test] public void LineIncludesBothEndsWithoutOvershooting()
    {
        var a = new Vector3(2, 0, 4); var b = new Vector3(17, 0, 4);
        var points = (Vector3[])Call("LinePoints", a, b, 5f, 200);
        Assert.AreEqual(4, points.Length); Assert.AreEqual(a, points[0]); Assert.AreEqual(b, points[3]);
        for (int i = 1; i < points.Length; i++) Assert.That(Vector3.Distance(points[i], points[i - 1]), Is.EqualTo(5).Within(.001));
    }
    [Test] public void LineSafetyCapPreventsUnboundedObjectCreation()
    {
        var points = (Vector3[])Call("LinePoints", Vector3.zero, Vector3.right * 100000, .01f, 200);
        Assert.AreEqual(200, points.Length); Assert.AreEqual(Vector3.right * 99.5f, points[199]);
    }
    [Test] public void FenceLineKeepsExactModuleSpacingInsteadOfStretchingGapsToReachTheEnd()
    {
        var points = (Vector3[])Call("LinePoints", Vector3.zero, Vector3.right * 20, 6f, 200);
        CollectionAssert.AreEqual(new[] { Vector3.zero, Vector3.right * 6, Vector3.right * 12, Vector3.right * 18 }, points);
    }
    [Test] public void ZeroLengthLineProducesOneStamp()
    {
        var p = Vector3.one;
        CollectionAssert.AreEqual(new[] { p }, (Vector3[])Call("LinePoints", p, p, 3f, 200));
    }
    [Test] public void WideFenceHeadingMatchesTheLineAndPreview()
    {
        Assert.AreEqual(0f, (float)Call("LineYaw", Vector3.zero, Vector3.right * 20, new Vector3(6, 2, .2f), 0f));
        Assert.AreEqual(-75f, (float)Call("LineYaw", Vector3.zero, Vector3.forward * 20, new Vector3(6, 2, .2f), 15f));
    }
    [Test] public void MixedBrushIncludesInitialSelectionAndOrdinaryClickResetsIt()
    {
        var window = Type.GetType("TownEditorWindow, Assembly-CSharp-Editor", true);
        var method = window.GetMethod("UpdateBrushSelection");
        var mix = new System.Collections.Generic.List<string>();
        var active = (string)method.Invoke(null, new object[] { mix, "tree-a", "tree-b", true });
        CollectionAssert.AreEqual(new[] { "tree-a", "tree-b" }, mix);
        active = (string)method.Invoke(null, new object[] { mix, active, "tree-a", true });
        Assert.AreEqual("tree-b", active); CollectionAssert.AreEqual(new[] { "tree-b" }, mix);
        active = (string)method.Invoke(null, new object[] { mix, active, "tree-c", false });
        Assert.AreEqual("tree-c", active); Assert.IsEmpty(mix);
    }
    [Test] public void BoundsNormalizeOffCentreModelRootsWithoutFlatteningItsHeight()
    {
        var root = new GameObject("Bounds test");
        try
        {
            root.transform.position = new Vector3(100, 50, -20);
            var visual = GameObject.CreatePrimitive(PrimitiveType.Cube); visual.transform.SetParent(root.transform, false);
            visual.transform.localPosition = new Vector3(5, 4, -3);
            visual.transform.localScale = new Vector3(2, 6, 4);
            var b = (Bounds)Call("PrefabBounds", root);
            Assert.AreEqual(new Vector3(5, 4, -3), b.center);
            Assert.AreEqual(new Vector3(2, 6, 4), b.size);
            Assert.AreEqual(1, b.min.y);
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }
}
