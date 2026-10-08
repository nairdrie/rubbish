using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// The depot flow's pure rules (Docs/Multiplayer.md §13.4): which flow a session uses, how each Depot Row lot looks in
/// it, which street door works, and where you arrive out of the legacy garage. No scene, session or network is started.
/// </summary>
public class DepotFlowRulesTests
{
    const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object Rule(string method, params object[] args)
    {
        foreach (var m in Runtime("DepotFlowRules").GetMethods(PublicStatic))
            if (m.Name == method && m.GetParameters().Length == args.Length) return m.Invoke(null, args);
        throw new MissingMethodException("DepotFlowRules." + method);
    }
    static object Flow(string name) => Enum.Parse(Runtime("DepotFlow"), name);
    static object Look(string name) => Enum.Parse(Runtime("DepotLotLook"), name);
    static T Const<T>(string name) => (T)Runtime("DepotFlowRules").GetField(name, PublicStatic).GetRawConstantValue();
    static T Layout<T>(string method, params object[] args) => (T)Runtime("DepotRowLayout").GetMethod(method, PublicStatic).Invoke(null, args);
    static Quaternion Facing => (Quaternion)Runtime("DepotRowLayout").GetProperty("StreetFacing").GetValue(null);
    static int LotCount => (int)Runtime("DepotRowLayout").GetField("LotCount", PublicStatic).GetRawConstantValue();
    static int ShopLot => Const<int>("LegacyShopLot");

    readonly List<Object> owned = new List<Object>();

    [TearDown]
    public void TearDown()
    {
        for (int i = owned.Count - 1; i >= 0; i--) if (owned[i] != null) Object.DestroyImmediate(owned[i]);
        owned.Clear();
    }

    // ------------------------------------------------------------------ which flow

    [TestCase(true, false, "LegacyScenes", TestName = "Solo keeps the scene flow")]
    [TestCase(true, true, "LegacyScenes", TestName = "Solo with the dev hatch keeps the scene flow")]
    [TestCase(false, true, "LegacyScenes", TestName = "Co-op with -depotrow-off keeps the scene flow")]
    [TestCase(false, false, "OneWorld", TestName = "A co-op host uses the one world")]
    public void FlowIsDecidedBySoloAndTheDevHatch(bool offline, bool forceLegacy, string expected) =>
        Assert.AreEqual(Flow(expected), Rule("FlowFor", offline, forceLegacy));

    // ------------------------------------------------------------------ how lots look

    [Test]
    public void SoloPresentsLotOneAsTheShopAndNeverLeavesBareAsphalt()
    {
        Assert.AreEqual(0, ShopLot, "lot 1 is the host's lot in the one world too");
        int shops = 0;
        for (int lot = 0; lot < LotCount; lot++)
        {
            // Claim bits belong to the one world: they must not leak into the legacy presentation.
            foreach (bool claimed in new[] { false, true })
                foreach (bool online in new[] { false, true })
                {
                    object look = Rule("LookFor", Flow("LegacyScenes"), lot, claimed, online);
                    Assert.AreEqual(lot == ShopLot ? Look("Open") : Look("Vacant"), look, "lot " + lot);
                    Assert.IsTrue((bool)Rule("ShowsFacade", look), "a building stands on every solo lot");
                    Assert.AreEqual(lot != ShopLot, (bool)Rule("ShowsLeaseBoard", look), "no FOR LEASE on your own shop");
                    Assert.AreEqual(lot != ShopLot, (bool)Rule("ShowsShutters", look));
                }
            if (Equals(Rule("LookFor", Flow("LegacyScenes"), lot, false, false), Look("Open"))) shops++;
        }
        Assert.AreEqual(1, shops, "exactly one shop in solo");
    }

    [TestCase(false, false, "ForLease")]
    [TestCase(false, true, "ForLease")]
    [TestCase(true, true, "Open")]
    [TestCase(true, false, "BackSoon")]
    public void OneWorldLooksFollowTheClaim(bool claimed, bool online, string expected)
    {
        for (int lot = 0; lot < LotCount; lot++)
        {
            object look = Rule("LookFor", Flow("OneWorld"), lot, claimed, online);
            Assert.AreEqual(Look(expected), look);
            Assert.AreEqual(claimed, (bool)Rule("ShowsFacade", look));
            Assert.AreEqual(!claimed, (bool)Rule("ShowsLeaseBoard", look));
            Assert.AreEqual(claimed && !online, (bool)Rule("ShowsShutters", look));
        }
    }

    // ------------------------------------------------------------------ one way in and out

    [Test]
    public void ExactlyOneStreetDoorWorksInSolo()
    {
        int usable = 0;
        for (int lot = 0; lot < LotCount; lot++)
        {
            object look = Rule("LookFor", Flow("LegacyScenes"), lot, false, false);
            bool can = (bool)Rule("StreetDoorUsable", Flow("LegacyScenes"), lot, look);
            Assert.AreEqual(lot == ShopLot, can, "lot " + lot);
            if (can) usable++;
        }
        Assert.AreEqual(1, usable);
        // A vacant look on the shop lot (should never happen) still isn't a door.
        Assert.IsFalse((bool)Rule("StreetDoorUsable", Flow("LegacyScenes"), ShopLot, Look("Vacant")));
    }

    [Test]
    public void OneWorldDoorsWorkOnEveryStandingShopAndNeverOnABarePad()
    {
        for (int lot = 0; lot < LotCount; lot++)
        {
            Assert.IsTrue((bool)Rule("StreetDoorUsable", Flow("OneWorld"), lot, Look("Open")));
            Assert.IsTrue((bool)Rule("StreetDoorUsable", Flow("OneWorld"), lot, Look("BackSoon")), "peek inside (BACK SOON)");
            Assert.IsFalse((bool)Rule("StreetDoorUsable", Flow("OneWorld"), lot, Look("ForLease")));
        }
    }

    [Test]
    public void TheLegacyShopDoorLeadsToTheGarageMarkers()
    {
        Assert.AreEqual("SpawnPoint_FromNeighborhood", Rule("GarageMarker", false));
        Assert.AreEqual("SpawnPoint_FromNeighborhood_Truck", Rule("GarageMarker", true));
    }

    // ------------------------------------------------------------------ arrival poses

    static bool TryArrival(string marker, out Pose pose)
    {
        var args = new object[] { marker, default(Pose) };
        bool ok = (bool)Runtime("DepotFlowRules").GetMethod("TryArrivalPose", PublicStatic).Invoke(null, args);
        pose = (Pose)args[1];
        return ok;
    }

    [Test]
    public void LeavingTheGarageArrivesOnTheShopsOwnApronFacingTheRoad()
    {
        Assert.AreEqual("SpawnPoint_FromGarage", Const<string>("TownOnFootMarker"), "ShopScene's garage door contract");
        Assert.AreEqual("SpawnPoint_FromGarage_Truck", Const<string>("TownTruckMarker"), "ShopScene's garage door contract");

        Assert.IsTrue(TryArrival("SpawnPoint_FromGarage", out var foot));
        Assert.IsTrue(TryArrival("SpawnPoint_FromGarage_Truck", out var truck));
        var onFoot = Layout<Pose>("StreetOnFoot", ShopLot);
        var apron = Layout<Pose>("StreetTruck", ShopLot);
        Assert.That(Vector3.Distance(onFoot.position, foot.position), Is.LessThan(.001f), "same pose as the one world's door");
        Assert.That(Vector3.Distance(apron.position, truck.position), Is.LessThan(.001f), "same pose as the one world's door");

        var door = Layout<Vector3>("DoorPoint", ShopLot);
        var road = Layout<Vector3>("RoadPoint", ShopLot);
        Vector3 toRoad = Facing * Vector3.forward;
        foreach (var pose in new[] { foot, truck })
        {
            Assert.That(Quaternion.Angle(Facing, pose.rotation), Is.LessThan(.01f), "facing the road");
            float ahead = Vector3.Dot(pose.position - door, toRoad);
            Assert.That(ahead, Is.GreaterThan(1.5f), "in front of the door line, never inside the building");
            Assert.That(ahead, Is.LessThan(Vector3.Dot(road - door, toRoad)), "still on this side of the road");
            Assert.AreEqual(ShopLot, Layout<int>("PadAt", pose.position), "on the shop's own pad");
            Assert.AreEqual(-1, Layout<int>("InteriorAt", pose.position), "not in a pocket interior");
        }
        // The truck stops beyond the door's 5 m prompt box (z 0.2..5.2 from the door), so it doesn't offer "drive in" again.
        Assert.That(Vector3.Dot(truck.position - door, toRoad), Is.GreaterThan(5.2f));
        // On foot you stand beside the roller door (clear of the truck's lane), not in front of it.
        Assert.That(Mathf.Abs(Vector3.Dot(foot.position - door, Facing * Vector3.right)), Is.GreaterThan(5f));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("SpawnPoint_FromNeighborhood")]
    [TestCase("SpawnPoint_FromNeighborhood_Truck")]
    [TestCase("PlayerSpawner")]
    [TestCase("spawnpoint_fromgarage")]
    public void OtherMarkersAreLeftToTheScene(string marker)
    {
        Assert.IsFalse(TryArrival(marker, out _));
        Assert.AreEqual(Enum.Parse(Runtime("LegacyArrival"), "None"), Rule("ArrivalFor", marker));
    }

    // ------------------------------------------------------------------ the lot presents the look

    [Test]
    public void ALegacyLotShowsTheShopOrAShutteredUnitAndNeverItsPocketInterior()
    {
        var lotType = Runtime("DepotLot");
        var doorType = Runtime("GarageDoorController");
        for (int k = 0; k < 2; k++)
        {
            var root = new GameObject("Lot test " + k);
            owned.Add(root);
            GameObject Child(string name, Transform parent = null)
            {
                var go = new GameObject(name);
                go.transform.SetParent(parent != null ? parent : root.transform, false);
                return go;
            }
            var lot = root.AddComponent(lotType);
            var facade = Child("Shop");
            var shutters = Child("Shutters", facade.transform);
            var backSoon = Child("BackSoon", facade.transform);
            var lease = Child("ForLease");
            var interior = Child("Interior");
            var floor = Child("PocketFloor");
            var door = Child("StreetDoor", facade.transform).AddComponent(doorType);
            doorType.GetField("depotLot").SetValue(door, k);
            doorType.GetField("targetSceneName").SetValue(door, "");
            lotType.GetField("lot").SetValue(lot, k);
            lotType.GetField("facade").SetValue(lot, facade.transform);
            lotType.GetField("shutters").SetValue(lot, shutters);
            lotType.GetField("backSoonSign").SetValue(lot, backSoon);
            lotType.GetField("forLeaseSign").SetValue(lot, lease);
            lotType.GetField("interior").SetValue(lot, interior);
            lotType.GetField("pocketFloor").SetValue(lot, floor);
            lotType.GetField("streetDoor").SetValue(lot, door);
            facade.SetActive(false);
            interior.SetActive(true); // even if something left it on
            floor.SetActive(true);

            object look = Rule("LookFor", Flow("LegacyScenes"), k, false, false);
            lotType.GetMethod("PresentLegacy", Any).Invoke(lot, new[] { look, "Nick", "RUBBISH CO.", (object)0 });

            bool shop = k == ShopLot;
            Assert.IsTrue(facade.activeSelf, "the building stands");
            Assert.AreEqual(!shop, shutters.activeSelf);
            Assert.AreEqual(!shop, lease.activeSelf);
            Assert.IsFalse(backSoon.activeSelf);
            Assert.IsFalse(interior.activeSelf, "the garage is ShopScene in the legacy flow");
            Assert.IsFalse(floor.activeSelf);
            Assert.AreEqual(shop, ((Component)door).gameObject.activeSelf, "only the shop has a door");
            Assert.AreEqual(shop ? "ShopScene" : "", doorType.GetField("targetSceneName").GetValue(door), "the route arrow follows the garage door");
        }
    }
}
