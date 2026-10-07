using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

/// <summary>Presentation contracts; runtime resides in Assembly-CSharp.</summary>
public class CompactorFxTests
{
    static Type Fx => Type.GetType("CompactorFx, Assembly-CSharp", true);
    static object Call(string method, params object[] args) => Fx.GetMethod(method, BindingFlags.Public | BindingFlags.Static).Invoke(null, args);
    static Vector3 Point(Vector3 from, Vector3 to, float t, int ordinal = 0) =>
        (Vector3)Call("SuctionPoint", from, to, t, ordinal);
    static float Progress(float seconds, int ordinal, int total) =>
        (float)Call("TravelProgress", seconds, ordinal, total, 2.6f);

    [Test]
    public void SuctionHasExactEndpointsAndLiftInsteadOfShrinkingInPlace()
    {
        var from = new Vector3(5f, 0.2f, 3f);
        var mouth = new Vector3(-2f, 1.2f, -1f);
        Assert.AreEqual(from, Point(from, mouth, 0f));
        Assert.AreEqual(mouth, Point(from, mouth, 1f));
        var mid = Point(from, mouth, 0.5f);
        Assert.Greater(Vector3.Distance(mid, from), 1f);
        Assert.Less(Vector3.Distance(mid, mouth), Vector3.Distance(from, mouth));
        Assert.Greater(mid.y, 0.8f);
    }

    [Test]
    public void LargePileAndLastItemFinishInsideSameFixedWindow()
    {
        Assert.AreEqual(0f, Progress(0.2f, 0, 10000));
        Assert.AreEqual(0f, Progress(0.2f, 9999, 10000));
        Assert.Greater(Progress(1.1f, 0, 10000), Progress(1.1f, 9999, 10000));
        Assert.AreEqual(1f, Progress(2.6f, 0, 10000));
        Assert.AreEqual(1f, Progress(2.6f, 9999, 10000));
        Assert.AreEqual(1f, Progress(2.6f, 0, 1));
    }

    [Test]
    public void PathIsDeterministicDistinctAndSafeForVerticalOrZeroTravel()
    {
        var from = Vector3.up;
        var to = Vector3.up * 5f;
        Assert.AreEqual(Point(from, to, 0.43f, 15), Point(from, to, 0.43f, 15));
        Assert.AreNotEqual(Point(from, to, 0.43f, 15), Point(from, to, 0.43f, 16));
        var p = Point(from, from, 0.5f, 10);
        Assert.IsFalse(float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z));
        Assert.AreEqual(from, Point(from, to, float.NaN));
    }

    [Test]
    public void OnlyExplicitCosmeticObjectsQualifyForBaleRetirement()
    {
        var ordinary = new GameObject("Bale");
        var networked = new GameObject("Networked player or stock");
        try
        {
            networked.AddComponent<Unity.Netcode.NetworkObject>();
            Assert.IsTrue((bool)Call("CanFloatBale", ordinary));
            Assert.IsFalse((bool)Call("CanFloatBale", networked));
            Assert.IsFalse((bool)Call("CanFloatBale", new object[] { null }));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(ordinary);
            UnityEngine.Object.DestroyImmediate(networked);
        }
    }

    [Test]
    public void OverviewCameraSitsAboveTruckAndInFrontOfMachine()
    {
        var camera = Type.GetType("CompactorOverviewCamera, Assembly-CSharp", true);
        Vector3 mouth = new Vector3(-5.5f, 1.35f, 2.5f);
        var pose = (Pose)camera.GetMethod("OverviewPose").Invoke(null, new object[] { mouth, Vector3.back, Vector3.left });
        Assert.Greater(pose.position.y, mouth.y + 4f);
        Assert.Less(pose.position.z, mouth.z - 4f);
        Assert.Greater(pose.position.x, -8f, "Stay inside the shop's west wall");
        Vector3 view = pose.rotation * Vector3.forward;
        Assert.Less(view.y, -0.2f, "Look down across airborne trash, not through the compactor sign");
        Assert.Greater(Vector3.Dot(view, mouth - pose.position), 0f);
    }
}
