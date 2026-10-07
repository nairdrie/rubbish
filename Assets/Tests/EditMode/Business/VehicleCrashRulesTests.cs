using System;
using System.Reflection;
using NUnit.Framework;
using Unity.Netcode;
using UnityEngine;
using Object = UnityEngine.Object;

public class VehicleCrashRulesTests
{
    const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object Call(string method, params object[] args) => Runtime("VehicleCrashRules").GetMethod(method, PublicStatic).Invoke(null, args);
    static Vector3 Kick(Vector3 away, float speed, float a = 6000f, float b = 6000f) => (Vector3)Call("TruckKick", away, speed, a, b);

    [TestCase(0f)] [TestCase(2f)] [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)]
    public void ParkingTouchesAndInvalidSpeedsCannotCreateAnImpulse(float speed) => Assert.AreEqual(Vector3.zero, Kick(Vector3.right, speed));

    [Test]
    public void EqualMassTruckImpulsesAreOpposedAndBoundedEvenAtExtremeSpeed()
    {
        var a = Kick(Vector3.back, 1000f); var b = Kick(Vector3.forward, 1000f);
        Assert.AreEqual(Vector3.zero, Vector3.ProjectOnPlane(a + b, Vector3.up));
        Assert.That(new Vector2(a.x, a.z).magnitude, Is.EqualTo(6f).Within(.0001f));
        Assert.That(a.y, Is.InRange(0f, .55f));
    }

    [Test]
    public void HeavyVehiclesReceiveLessKickWithoutIgnoringTheLighterVehicle()
    {
        float heavy = Kick(Vector3.right, 10f, 9000f, 3000f).x;
        float light = Kick(Vector3.right, 10f, 3000f, 9000f).x;
        Assert.That(heavy, Is.GreaterThan(0f));
        Assert.That(light, Is.GreaterThan(heavy));
        Assert.AreEqual(Vector3.zero, Kick(new Vector3(float.NaN, 0, 0), 10f));
    }

    [Test]
    public void ProductionTruckResponseCommitsTheBoundedDeltaVelocityImmediately()
    {
        var own = new GameObject("Impact owner regression");
        var other = new GameObject("Impact peer regression");
        try
        {
            own.AddComponent<NetworkObject>(); other.AddComponent<NetworkObject>();
            var rb = own.AddComponent<Rigidbody>(); var peerBody = other.AddComponent<Rigidbody>();
            rb.mass = peerBody.mass = 6000f;
            rb.linearVelocity = new Vector3(1f, -.5f, 2f);
            var ownTruck = own.AddComponent(Runtime("TruckControllerV2"));
            var peerTruck = other.AddComponent(Runtime("TruckControllerV2"));
            var apply = Runtime("TruckImpact").GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Static);
            var expected = rb.linearVelocity + Kick(Vector3.back, 14f);
            apply.Invoke(null, new object[] { ownTruck, peerTruck, Vector3.back, 14f });
            Assert.That(Vector3.Distance(rb.linearVelocity, expected), Is.LessThan(.0001f),
                "The confirmed owner impulse must take effect now, rather than being absorbed by stationary tire constraints.");
            Assert.AreEqual(Vector3.zero, peerBody.linearVelocity, "Only the explicitly simulated body is touched.");
            var impact = own.GetComponent(Runtime("TruckImpact"));
            Assert.AreEqual(1, impact.GetType().GetProperty("AppliedImpacts").GetValue(impact));
            rb.isKinematic = true;
            apply.Invoke(null, new object[] { ownTruck, peerTruck, Vector3.back, 14f });
            Assert.AreEqual(1, impact.GetType().GetProperty("AppliedImpacts").GetValue(impact), "A kinematic replica cannot apply the impulse.");
        }
        finally { Object.DestroyImmediate(own); Object.DestroyImmediate(other); }
    }

    [Test]
    public void ProximityRejectsDistantTrucksOtherFloorsAndNonfinitePositions()
    {
        bool Near(Vector3 p, Quaternion rotation) => (bool)Call("TrucksNear", Vector3.zero, Quaternion.identity, p, rotation, .8f);
        Assert.IsTrue(Near(new Vector3(0, 0, 6.5f), Quaternion.identity));
        Assert.IsTrue(Near(new Vector3(4, 0, 0), Quaternion.Euler(0, 90, 0)));
        Assert.IsFalse(Near(new Vector3(0, 0, 10), Quaternion.identity));
        Assert.IsFalse(Near(new Vector3(0, 20, 0), Quaternion.identity));
        Assert.IsFalse(Near(new Vector3(float.NaN, 0, 0), Quaternion.identity));
    }

    [Test]
    public void TrafficProximityCannotHitCarsAcrossTownOrInAnOffscreenDepot()
    {
        bool Near(Vector3 car) => (bool)Call("CarNearTruck", car, new Vector3(1, .8f, 2.2f), Vector3.zero, Quaternion.identity);
        Assert.IsTrue(Near(new Vector3(0, 0, 4)));
        Assert.IsFalse(Near(new Vector3(20, 0, 0)));
        Assert.IsFalse(Near(new Vector3(0, -20, 0)));
    }

    [Test]
    public void TimetableDelayCannotTeleportAnActiveOrVisibleCrash()
    {
        bool Snap(bool shunt, bool hold, float slide, float distance) => (bool)Call("MaySnapTraffic", shunt, hold, slide, distance);
        Assert.IsFalse(Snap(true, false, 0, 200));
        Assert.IsFalse(Snap(false, true, 0, 200));
        Assert.IsFalse(Snap(false, false, 4, 200));
        Assert.IsFalse(Snap(false, false, 0, 30));
        Assert.IsTrue(Snap(false, false, 0, 200));
    }

    [Test]
    public void GuestCarClaimsAreBoundedToTheirOwnRouteTimeWindowAndFloor()
    {
        var points = new[] { Vector3.zero, Vector3.forward * 100f, Vector3.forward * 200f };
        var arcs = new[] { 0f, 100f, 200f };
        bool Allowed(Vector3 point) => (bool)Call("TryRoutePoint", point, points, arcs, 50f, 130f, 0f);
        Assert.IsTrue(Allowed(new Vector3(0, 0, 90)), "A guest car delayed behind the host timetable stays hittable.");
        Assert.IsTrue(Allowed(new Vector3(-5, 0, 90)), "The normal oncoming-lane overtake is valid.");
        Assert.IsFalse(Allowed(new Vector3(50, 0, 90)), "Cannot invent a car in a different street or shop.");
        Assert.IsFalse(Allowed(new Vector3(0, -20, 90)), "Cannot hit an offscreen depot copy.");
        Assert.IsFalse(Allowed(new Vector3(0, 0, 160)), "Cannot move the car ahead of its schedule.");
        Assert.IsFalse(Allowed(new Vector3(0, 0, 20)), "Cannot rewind it beyond the bounded lag window.");
        Assert.IsFalse(Allowed(new Vector3(float.NaN, 0, 90)));
    }

    [Test]
    public void RouteLagWindowUsesRealSecondsDespiteTheAcceleratedTownClock()
    {
        float realSecondsPerHour = (float)Runtime("GameClock").GetProperty("SecondsPerGameHour").GetValue(null);
        var dayType = Runtime("LifeDay"); var day = Activator.CreateInstance(dayType);
        dayType.GetField("day").SetValue(day, 0);
        var ms = dayType.GetMethod("MsOf");
        double start = (double)ms.Invoke(day, new object[] { 10.0 });
        double later = (double)ms.Invoke(day, new object[] { 10.0 + 35.0 / realSecondsPerHour });
        Assert.That(later - start, Is.EqualTo(35000.0).Within(.001),
            "LifeDay's 50,000ms/gamehour matches GameClock's 50 real seconds/gamehour; 35,000ms is 35 real seconds.");
    }

    [Test]
    public void CommutePhaseChangeCannotHideACrashBeforeItsRecoveryFinishes()
    {
        bool Release(bool leaving, float far, bool shunt, bool hold) => (bool)Call("MayReleaseParkedCrash", leaving, far, shunt, hold);
        Assert.IsFalse(Release(true, 0, true, false));
        Assert.IsFalse(Release(true, 0, false, true));
        Assert.IsFalse(Release(false, 30, true, false));
        Assert.IsTrue(Release(true, 0, false, false));
        Assert.IsTrue(Release(false, 11, false, false));
    }

    [Test]
    public void SkidIsContinuousAndSpinStartsFromRestThenRecovers()
    {
        float previous = 0;
        for (int i = 0; i <= 200; i++)
        {
            float fraction = (float)Call("CarSlideFraction", i * .01f);
            Assert.That(fraction, Is.InRange(previous, 1f));
            previous = fraction;
        }
        Assert.AreEqual(1f, previous);
        Assert.AreEqual(0f, (float)Call("CarYaw", 0f, 135f));
        Assert.That((float)Call("CarYaw", .6f, 135f), Is.EqualTo(135f).Within(.0001f));
        Assert.AreEqual(0f, (float)Call("CarYaw", 4f, 135f));
        Assert.That((float)Call("CarSlideDistance", 20f), Is.GreaterThan((float)Call("CarSlideDistance", 4f)));
    }

    [Test]
    public void ThirtyHertzRemoteUpdatesDoNotBecomeFiftyHertzSpeedSpikes()
    {
        var type = Runtime("VehicleMotionWindow"); var window = Activator.CreateInstance(type);
        var sample = type.GetMethod("Sample");
        for (int i = 0; i < 200; i++)
        {
            float time = i * .02f;
            // Network poses change 30 times/sec while the physics sampler runs 50 times/sec.
            var position = Vector3.forward * (Mathf.Floor(time * 30f + .0001f) / 30f * 20f);
            var velocity = (Vector3)sample.Invoke(window, new object[] { time, position });
            if (time > .3f) Assert.That(velocity.z, Is.InRange(16f, 24f), "No 33–50m/s one-frame aliases or zero gaps.");
        }
    }

    [Test]
    public void TeleportClearsMotionWindowAndNewStationarySamplesDoNotReuseOldSpeed()
    {
        var type = Runtime("VehicleMotionWindow"); var window = Activator.CreateInstance(type);
        var sample = type.GetMethod("Sample");
        for (int i = 0; i < 30; i++) sample.Invoke(window, new object[] { i * .02f, Vector3.forward * i * .4f });
        Assert.AreEqual(Vector3.zero, sample.Invoke(window, new object[] { .6f, Vector3.right * 100f }));
        Assert.IsTrue((bool)type.GetProperty("ResetOccurred").GetValue(window));
        for (int i = 31; i < 70; i++)
            Assert.AreEqual(Vector3.zero, sample.Invoke(window, new object[] { i * .02f, Vector3.right * 100f }));
    }

    [Test]
    public void ProductionShuntKeepsTheCarVisibleAndBuildsASkidInsteadOfInstantlyRotatingIt()
    {
        var shell = new GameObject("Crash presentation regression");
        try
        {
            var agentType = Runtime("CarAgent"); var a = Activator.CreateInstance(agentType);
            agentType.GetField("go").SetValue(a, shell);
            agentType.GetField("box").SetValue(a, shell.AddComponent<BoxCollider>());
            agentType.GetField("distance").SetValue(a, 999f); // no sound/UI in this pure presentation probe
            var hitType = Runtime("NpcHit"); var hit = Activator.CreateInstance(hitType);
            hitType.GetField("truckVelocity").SetValue(hit, Vector3.forward * 20f);
            var directorType = Runtime("TrafficDirector"); var director = Activator.CreateInstance(directorType);
            directorType.GetMethod("StartShunt", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(director,
                new object[] { a, hit, 100f, true });
            Assert.IsTrue(shell.activeSelf);
            Assert.That(((Vector3)agentType.GetField("shuntSlide").GetValue(a)).magnitude, Is.InRange(5f, 7f));
            Assert.AreEqual(Vector3.zero, agentType.GetField("slide").GetValue(a));
            Assert.AreEqual(0f, agentType.GetField("shuntYaw").GetValue(a));
            directorType.GetMethod("TickShunt", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(director,
                new object[] { a, 100.4f, .4f });
            Assert.IsTrue(shell.activeSelf);
            Assert.That(((Vector3)agentType.GetField("slide").GetValue(a)).magnitude, Is.GreaterThan(1f));
            Assert.That(Mathf.Abs((float)agentType.GetField("shuntYaw").GetValue(a)), Is.GreaterThan(70f));
        }
        finally { Object.DestroyImmediate(shell); }
    }
}
