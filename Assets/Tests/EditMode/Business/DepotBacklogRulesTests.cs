using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public class DepotBacklogRulesTests
{
    static Type Backlog => Type.GetType("DepotBacklog, Assembly-CSharp", true);
    static bool Fits(float used, float incoming, float cap) => (bool)Backlog.GetMethod("Fits").Invoke(null, new object[] { used, incoming, cap });

    [Test]
    public void ExactlyTwoTruckloadsFitButOverflowDoesNot()
    {
        Assert.AreEqual(2f, Backlog.GetField("Truckloads").GetRawConstantValue());
        Assert.IsTrue(Fits(310f, 10f, 320f));
        Assert.IsFalse(Fits(310f, 10.01f, 320f));
        Assert.IsFalse(Fits(400f, 1f, 320f)); // grandfather old stock, refuse more
        Assert.IsTrue(Fits(0f, 320f, 320f));
    }

    [Test]
    public void InvalidMassCannotBypassCapacity()
    {
        Assert.IsFalse(Fits(0f, -1f, 320f));
        Assert.IsFalse(Fits(0f, float.NaN, 320f));
        Assert.IsFalse(Fits(0f, float.PositiveInfinity, 320f));
    }

    [Test]
    public void DisconnectCleanupFollowsDepotOwnershipNotItemOrigin()
    {
        var rule = Backlog.GetMethod("RetireWithOwner");
        Assert.IsFalse((bool)rule.Invoke(null, new object[] { true, 0UL, 1UL }), "employee's work stays in employer depot");
        Assert.IsTrue((bool)rule.Invoke(null, new object[] { true, 1UL, 1UL }), "own depot retires even host-spawned stock");
        Assert.IsFalse((bool)rule.Invoke(null, new object[] { false, 1UL, 1UL }), "never infer ownership outside a claimed depot");
    }

    [Test]
    public void TriggerAndWorldCollidersAreNotSolidActors()
    {
        var type = Type.GetType("LooseTrashCollisions, Assembly-CSharp", true);
        var check = type.GetMethod("IsSolidActor", BindingFlags.Public | BindingFlags.Static);
        var world = new GameObject("TableWorldCollider");
        try
        {
            var box = world.AddComponent<BoxCollider>();
            Assert.IsFalse((bool)check.Invoke(null, new object[] { box }));
            box.isTrigger = true;
            Assert.IsFalse((bool)check.Invoke(null, new object[] { box }));
            Assert.IsFalse((bool)check.Invoke(null, new object[] { null }));
        }
        finally { UnityEngine.Object.DestroyImmediate(world); }
    }
}
