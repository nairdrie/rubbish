using System;
using NUnit.Framework;
using UnityEngine;

public class CompactorPlayerRulesTests
{
    static Type Cube => Type.GetType("CompactorCube, Assembly-CSharp", true);
    static Type Layout => Type.GetType("DepotRowLayout, Assembly-CSharp", true);
    static Vector3 Origin(int lot) => (Vector3)Layout.GetMethod("InteriorOrigin").Invoke(null, new object[] { lot });
    static bool SameShop(Vector3 machine, Vector3 player, bool oneWorld) =>
        (bool)Cube.GetMethod("SameShop").Invoke(null, new object[] { machine, player, oneWorld });

    [Test]
    public void BulkVacuumContainsOnlyPhysicalOccupantsOfItsOwnShop()
    {
        var machine = Origin(0) + new Vector3(6f, 1f, 0f);
        Assert.IsTrue(SameShop(machine, Origin(0) + new Vector3(-7f, 0f, 7f), true));
        Assert.IsFalse(SameShop(machine, Origin(1), true), "a neighbouring business is not part of the sweep");
        Assert.IsFalse(SameShop(machine, Vector3.zero, true), "street players and truck riders outside are untouched");
        Assert.IsFalse(SameShop(Vector3.zero, Vector3.one, true), "invalid street machine is not a global vacuum");
    }

    [Test]
    public void LegacySoloVacuumIsAlsoSpatiallyBounded()
    {
        Assert.IsTrue(SameShop(new Vector3(5f, 0f, 0f), new Vector3(-8f, 1f, 8f), false));
        Assert.IsFalse(SameShop(Vector3.zero, new Vector3(100f, 0f, 0f), false));
        Assert.IsFalse(SameShop(Vector3.zero, new Vector3(0f, 30f, 0f), false));
    }

    [Test]
    public void CubeLastsTwentyFiveSecondsForNormalAndBulkCycles()
    {
        Assert.AreEqual(25f, Cube.GetField("Seconds").GetRawConstantValue());
        Assert.AreEqual(2.6f, Cube.GetField("SuctionSeconds").GetRawConstantValue());
    }

    [Test]
    public void PlayerPullHasExactClampedEndpointsAndLiftsOverClutter()
    {
        var pull = Type.GetType("CompactorPlayerPull, Assembly-CSharp", true).GetMethod("PositionAlong");
        var from = new Vector3(-5f, 0f, 2f);
        var to = new Vector3(6f, 1f, 0f);
        Vector3 Point(float progress) => (Vector3)pull.Invoke(null, new object[] { from, to, progress, 1f });
        Assert.Less(Vector3.Distance(from, Point(-1f)), 0.00001f);
        Assert.Less(Vector3.Distance(to, Point(2f)), 0.00001f);
        Assert.Greater(Point(0.5f).y, 1.5f);
        for (int i = 0; i <= 20; i++)
        {
            var p = Point(i / 20f);
            Assert.IsFalse(float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z));
        }
    }
}
