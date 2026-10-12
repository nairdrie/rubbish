using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

/// <summary>The outdoor camera rule's pure parts: what counts as a tree, where its canopy is, and where a line of sight
/// first meets it (WorldCameraOcclusion.cs).</summary>
public class WorldCameraOcclusionTests
{
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object Call(string type, string method, params object[] args) =>
        Runtime(type).GetMethod(method, BindingFlags.Public | BindingFlags.Static).Invoke(null, args);
    static float RayBall(Vector3 origin, Vector3 dir, float distance, Vector3 centre, float radius, float pad) =>
        (float)Call("WorldCanopies", "RayBall", origin, dir, distance, centre, radius, pad);

    [TestCase("TL_kit.tree.maple.l", true)]
    [TestCase("TL_streets.tree.blossom.s", true)]
    [TestCase("SM_tree_town_12", true)]
    [TestCase("TL_kit.clump.mixed.3", true)]
    [TestCase("TL_edges.wood.a", true)]
    [TestCase("SM_streetlamp_01", false)]
    [TestCase("TL_streets.namepost", false)]
    [TestCase("SM_house_suburb_04", false)]
    [TestCase("", false)]
    public void OnlyTreesAreTrees(string name, bool expected) => Assert.AreEqual(expected, (bool)Call("WorldCanopies", "IsTreeName", name));

    [Test]
    public void TheCanopySitsInTheTopOfTheTreeAndLeavesTheGroundClear()
    {
        var tree = new Bounds(new Vector3(0f, 3.5f, 0f), new Vector3(6f, 7f, 6f));
        var args = new object[] { tree, null, null };
        Assert.IsTrue((bool)Runtime("WorldCanopies").GetMethod("CanopyOf").Invoke(null, args));
        var centre = (Vector3)args[1];
        float radius = (float)args[2];
        Assert.AreEqual(2.7f, radius, 0.01f);
        Assert.LessOrEqual(centre.y + radius, 7.2f);
        Assert.Greater(centre.y - radius, 1.5f, "a truck under the tree is not inside its canopy");
    }

    [Test]
    public void TrunksAndWholeWoodsAreNotCanopies()
    {
        foreach (var size in new[] { new Vector3(0.7f, 6f, 0.7f), new Vector3(38f, 16f, 40f), new Vector3(5f, 2f, 5f) })
        {
            var args = new object[] { new Bounds(Vector3.up * 4f, size), null, null };
            Assert.IsFalse((bool)Runtime("WorldCanopies").GetMethod("CanopyOf").Invoke(null, args), size.ToString());
        }
    }

    [Test]
    public void ALineOfSightStopsWhereItEntersTheLeaves()
    {
        var centre = new Vector3(0f, 0f, 10f);
        Assert.AreEqual(7f, RayBall(Vector3.zero, Vector3.forward, 20f, centre, 3f, 0f), 1e-4f);
        Assert.AreEqual(6.5f, RayBall(Vector3.zero, Vector3.forward, 20f, centre, 3f, 0.5f), 1e-4f, "the camera has a radius");
        Assert.AreEqual(5f, RayBall(Vector3.zero, Vector3.forward, 5f, centre, 3f, 0f), "the camera is already nearer than the tree");
        Assert.AreEqual(20f, RayBall(Vector3.zero, Vector3.back, 20f, centre, 3f, 0f), "the tree is behind");
        Assert.AreEqual(20f, RayBall(Vector3.zero, Vector3.right, 20f, centre, 3f, 0f), "the line misses");
        Assert.AreEqual(20f, RayBall(new Vector3(0f, 0f, 9f), Vector3.forward, 20f, centre, 3f, 0f), "under the canopy: nothing to pull in front of");
    }
}
