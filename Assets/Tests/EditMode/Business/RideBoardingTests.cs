using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public class RideBoardingTests
{
    const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static Type Holds => Runtime("RideHolds");
    static Bounds Pocket(int hold) => (Bounds)Holds.GetMethod("BoardingBounds").Invoke(null, new object[] { hold });
    static Vector3 Feet(int hold) => (Vector3)Holds.GetMethod("FeetMetres").Invoke(null, new object[] { hold });
    static bool Within(int hold, Vector3 point, float slack = 0f) =>
        (bool)Holds.GetMethod("WithinBoardingBounds").Invoke(null, new object[] { hold, point, slack });
    static bool Facing(int hold, Vector3 point, Vector3 forward) =>
        (bool)Holds.GetMethod("FacesBoardingHold").Invoke(null, new object[] { hold, point, forward });

    [Test]
    public void FourMountsHaveSmallSeparateReachableBoardingPockets()
    {
        Assert.AreEqual(4, Holds.GetField("Count").GetRawConstantValue());
        for (int hold = 0; hold < 4; hold++)
        {
            Bounds pocket = Pocket(hold);
            Assert.Less(pocket.size.x * pocket.size.z, .6f, "Old rear pocket covered 4.53 square metres.");
            Assert.IsTrue(Within(hold, pocket.center));
            Assert.IsTrue(Facing(hold, pocket.center, Feet(hold) - pocket.center));
            for (int other = 0; other < 4; other++)
                if (other != hold) Assert.IsFalse(Within(other, pocket.center), "An occupied mount cannot redirect to another side.");
        }
    }

    [TestCase(-2.6f)]
    [TestCase(-3.0f)]
    [TestCase(-3.4f)]
    [TestCase(-4f)]
    public void TossingAtTheHopperCentreDoesNotBecomeHitchARide(float z)
    {
        for (int hold = 0; hold < 4; hold++) Assert.IsFalse(Within(hold, new Vector3(0f, .4f, z)));
    }

    [TestCase(-.5f)]
    [TestCase(0f)]
    [TestCase(1f)]
    [TestCase(2.3f)]
    public void WalkingForwardToEitherCabDoorDoesNotOfferALadder(float z)
    {
        for (int side = -1; side <= 1; side += 2)
            for (int hold = 0; hold < 4; hold++)
                Assert.IsFalse(Within(hold, new Vector3(side * 1.92f, .4f, z)));
    }

    [Test]
    public void PlayerMustLookTowardTheirMountNotAlongTheTruckOrAway()
    {
        for (int hold = 0; hold < 4; hold++)
        {
            var at = Pocket(hold).center;
            var toward = Feet(hold) - at;
            toward.y = 0f;
            Assert.IsTrue(Facing(hold, at, toward + Vector3.down * 3f), "Camera pitch is ignored; avatar rotation is not required.");
            Assert.IsFalse(Facing(hold, at, -toward));
            Assert.IsFalse(Facing(hold, at, Vector3.Cross(Vector3.up, toward)));
        }
    }

    [Test]
    public void HostLagAllowanceStillRejectsCabAndOppositeSideTeleports()
    {
        const float lagSlack = 1.5f;
        for (int hold = 0; hold < 4; hold++)
        {
            Assert.IsTrue(Within(hold, Pocket(hold).center + Vector3.forward * .7f, lagSlack));
            Assert.IsFalse(Within(hold, new Vector3(0f, .4f, 2.3f), lagSlack));
        }
        Assert.IsFalse(Within(2, Pocket(3).center, lagSlack));
        Assert.IsFalse(Within(3, Pocket(2).center, lagSlack));
        Assert.IsFalse(Within(-1, Vector3.zero, lagSlack));
        Assert.IsFalse(Within(4, Vector3.zero, lagSlack));
    }

    [Test]
    public void ShrinkingHardwareDoesNotMoveExistingFourRiderMounts()
    {
        Assert.AreEqual(new Vector3(-.5f, .64f, -2.65f), Feet(0));
        Assert.AreEqual(new Vector3(.5f, .64f, -2.65f), Feet(1));
        Assert.AreEqual(new Vector3(-1.52f, .78f, -1.45f), Feet(2));
        Assert.AreEqual(new Vector3(1.52f, .78f, -1.45f), Feet(3));
    }

    [Test]
    public void SmallerProductionRailsStillMeetAllEightGripTargetsAndStayAttached()
    {
        var materials = (IDictionary)Runtime("ShopMaterials").GetField("cache", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        var previous = new Dictionary<object, object>();
        foreach (DictionaryEntry entry in materials) previous.Add(entry.Key, entry.Value);
        var root = new GameObject("Isolated compact riding kit");
        root.transform.SetPositionAndRotation(new Vector3(5000f, 1000f, 5000f), Quaternion.Euler(0f, 90f, 0f));
        root.transform.localScale = Vector3.one * .67f;
        var rideType = Runtime("RideTruck");
        var ride = root.AddComponent(rideType);
        var kitField = rideType.GetField("kit", PrivateInstance);
        try
        {
            rideType.GetMethod("BuildKit", PrivateInstance).Invoke(ride, new object[] { 0, 0, true });
            var kit = (Transform)kitField.GetValue(ride);
            Assert.NotNull(kit);
            Assert.That(Vector3.Distance(kit.lossyScale, Vector3.one), Is.LessThan(.001f));
            Assert.IsEmpty(kit.GetComponentsInChildren<Collider>(), "Hardware must not snag bags or the truck camera.");
            var rails = new List<Renderer>();
            int rungs = 0, plates = 0;
            foreach (var renderer in kit.GetComponentsInChildren<Renderer>())
            {
                Assert.IsTrue(renderer.transform.IsChildOf(root.transform));
                if (renderer.name == "LadderRail")
                {
                    rails.Add(renderer);
                    Assert.That(renderer.bounds.size.y, Is.EqualTo(.8f).Within(.001f));
                }
                if (renderer.name == "RearGripBar") rails.Add(renderer);
                if (renderer.name == "LadderRung") rungs++;
                if (renderer.name == "RearFootplate" || renderer.name == "SideFootplate") plates++;
            }
            Assert.AreEqual(4, rungs, "Two compact rungs on each flank.");
            Assert.AreEqual(4, plates);
            for (int hold = 0; hold < 4; hold++)
                for (int hand = 0; hand < 2; hand++)
                {
                    var localGrip = (Vector3)Holds.GetMethod("GripMetres").Invoke(null, new object[] { hold, hand == 0 });
                    var worldGrip = root.transform.position + root.transform.rotation * localGrip;
                    float closest = float.MaxValue;
                    foreach (var rail in rails) closest = Mathf.Min(closest, Vector3.Distance(worldGrip, rail.bounds.ClosestPoint(worldGrip)));
                    Assert.Less(closest, .025f, $"Hold {hold}, hand {hand} still contacts a visible rail.");
                }
        }
        finally
        {
            kitField.SetValue(ride, null); // Edit-mode teardown owns the kit, not RideTruck.OnDestroy's runtime Destroy.
            Object.DestroyImmediate(root);
            var added = new List<object>();
            foreach (DictionaryEntry entry in materials)
            {
                if (previous.ContainsKey(entry.Key)) continue;
                if (entry.Value is Material material && material != null) Object.DestroyImmediate(material);
                added.Add(entry.Key);
            }
            foreach (object key in added) materials.Remove(key);
        }
    }
}
