using System;
using System.Reflection;
using NUnit.Framework;

public class TruckUpgradeReplicationTests
{
    static Type Net => Type.GetType("BusinessUpgradeNet, Assembly-CSharp", true);
    static object Call(string name, params object[] args) => Net.GetMethod(name, BindingFlags.Public | BindingFlags.Static).Invoke(null, args);

    [Test]
    public void VehiclePackKeepsModelAndModLevelsIndependent()
    {
        ulong pack = 1UL | (4UL << 4) | (2UL << 8) | (3UL << 12) | (1UL << 16);
        Assert.IsTrue((bool)Call("ValidTruckLevels", pack));
        Assert.AreEqual(1, Call("DecodeTruckLevel", pack, "truck_model"));
        Assert.AreEqual(4, Call("DecodeTruckLevel", pack, "truck_capacity"));
        Assert.AreEqual(2, Call("DecodeTruckLevel", pack, "truck_turbo"));
        Assert.AreEqual(3, Call("DecodeTruckLevel", pack, "truck_handling"));
        Assert.AreEqual(1, Call("DecodeTruckLevel", pack, "truck_arm"));
        Assert.AreEqual(0, Call("DecodeTruckLevel", pack, "unknown"));
    }

    [TestCase(2UL)]
    [TestCase(5UL << 4)]
    [TestCase(4UL << 8)]
    [TestCase(1UL << 63)]
    public void InvalidOrReservedVehicleBitsAreRejected(ulong pack) => Assert.IsFalse((bool)Call("ValidTruckLevels", pack));

    [Test]
    public void ModelChangeInvalidatesSnapshotEquality()
    {
        var info = Type.GetType("BusinessUpgradeInfo, Assembly-CSharp", true);
        var mini = Activator.CreateInstance(info);
        var rear = Activator.CreateInstance(info);
        info.GetField("truckLevels").SetValue(rear, 1UL);
        Assert.IsFalse(mini.Equals(rear));
        Assert.AreNotEqual(mini.GetHashCode(), rear.GetHashCode());
    }
}
