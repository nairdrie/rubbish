using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public class PoleAndCritterTargetingTests
{
    static Type Pole => Type.GetType("PoleTargeting, Assembly-CSharp", true);
    static object Call(string name, params object[] args) => Pole.GetMethod(name, BindingFlags.Public | BindingFlags.Static).Invoke(null, args);
    static float Score(Ray ray, Vector3 target) => (float)Call("AimScore", ray, target, 0.45f);

    [Test]
    public void CameraAimPrefersCenteredFarBagOverNearOffAxisBag()
    {
        var view = new Ray(new Vector3(0f, 1.5f, -3f), Vector3.forward);
        Assert.Less(Score(view, new Vector3(0f, 1.5f, 4f)), Score(view, new Vector3(1.6f, 0.3f, 1f)));
        Assert.IsFalse(float.IsInfinity(Score(view, new Vector3(0.3f, 0.25f, 3f))), "Ground bags get generous vertical assistance");
        Assert.IsTrue(float.IsInfinity(Score(view, new Vector3(0f, 1.5f, -4f))));
    }

    [Test]
    public void ReachIsThreeDimensionalAndRejectsInvalidCoordinates()
    {
        Assert.IsTrue((bool)Call("WithinReach", Vector3.zero, Vector3.forward * 4.5f, 4.5f));
        Assert.IsFalse((bool)Call("WithinReach", Vector3.zero, new Vector3(0f, 5f, 0f), 4.5f));
        Assert.IsFalse((bool)Call("WithinReach", Vector3.zero, new Vector3(float.NaN, 0f, 1f), 4.5f));
    }

    [Test]
    public void PoleClearsLowFenceButNeverAFullHeightWall()
    {
        var wall = new GameObject("Pole test barrier");
        Vector3 origin = new Vector3(30000f, 10000f, 30000f);
        Vector3 from = origin + Vector3.up * 1.1f;
        Vector3 to = origin + Vector3.forward * 4f + Vector3.up * 0.3f;
        try
        {
            var collider = wall.AddComponent<BoxCollider>();
            collider.size = new Vector3(4f, 1.5f, 0.25f);
            wall.transform.position = origin + new Vector3(0f, 0.75f, 2f);
            Physics.SyncTransforms();
            Assert.IsFalse((bool)Call("ClearSegment", from, to, null, null));
            Assert.IsTrue((bool)Call("ClearPath", from, to, null, null));
            collider.size = new Vector3(4f, 5f, 0.25f);
            wall.transform.position = origin + new Vector3(0f, 2.5f, 2f);
            Physics.SyncTransforms();
            Assert.IsFalse((bool)Call("ClearPath", from, to, null, null));
            collider.isTrigger = true;
            Assert.IsTrue((bool)Call("ClearPath", from, to, null, null), "Interaction triggers never form a wall");
        }
        finally { UnityEngine.Object.DestroyImmediate(wall); }
    }

    [Test]
    public void CritterMeleeReachesLowSmallBodiesButNotBehindOrUpstairs()
    {
        var type = Type.GetType("CritterMelee, Assembly-CSharp", true);
        var check = type.GetMethod("InReach", BindingFlags.Public | BindingFlags.Static);
        Func<Vector3, bool> hit = point => (bool)check.Invoke(null, new object[] { Vector3.zero, Vector3.forward, point, 1.6f });
        Assert.IsTrue(hit(new Vector3(0.4f, 0.45f, 1.4f)));
        Assert.IsFalse(hit(new Vector3(0f, 0.45f, -1.4f)));
        Assert.IsFalse(hit(new Vector3(0f, 4f, 1f)));
        Assert.IsFalse(hit(new Vector3(0f, 0.45f, 3f)));
    }

    [Test]
    public void BuyingPoleChangesReplicatedBusinessSnapshot()
    {
        var type = Type.GetType("BusinessUpgradeInfo, Assembly-CSharp", true);
        var without = Activator.CreateInstance(type);
        var with = Activator.CreateInstance(type);
        type.GetField("gearBits").SetValue(with, (byte)1);
        Assert.IsFalse(without.Equals(with), "Gear purchases must echo even when all garage levels stay the same");
    }
}
