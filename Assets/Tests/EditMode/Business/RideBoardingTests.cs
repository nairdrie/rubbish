using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>F1 riding since 12 Oct 2026 (Nick: "should just be e anywhere near the back", "completely remove the grip
/// mechanic", "solidly on unless they hit something solid"): boarding, switching spots, the crash rule and the kit.</summary>
public class RideBoardingTests
{
    const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static Type Holds => Runtime("RideHolds");
    static Vector3 Feet(int hold) => (Vector3)Holds.GetMethod("FeetMetres").Invoke(null, new object[] { hold });
    static int Nearest(Vector3 local, int taken) =>
        (int)Holds.GetMethod("NearestFreeHold", new[] { typeof(Vector3), typeof(Vector3), typeof(int) })
            .Invoke(null, new object[] { local, Vector3.one, taken });
    static int Next(int from, int taken) => (int)Holds.GetMethod("NextFreeHold").Invoke(null, new object[] { from, taken });
    static float FlingDv(int padded, int bar, bool down) =>
        (float)Runtime("RideTuning").GetMethod("CrashFlingDv").Invoke(null, new object[] { padded, bar, down });
    const int RearLeft = 0, RearRight = 1, RungLeft = 2, RungRight = 3;

    GameObject root;

    [TearDown]
    public void TearDown()
    {
        if (root != null) Object.DestroyImmediate(root);
        root = null;
        Physics.SyncTransforms();
    }

    // ------------------------------------------------------------------ boarding

    [Test]
    public void ShrinkingHardwareDoesNotMoveExistingFourRiderMounts()
    {
        Assert.AreEqual(4, Holds.GetField("Count").GetRawConstantValue());
        Assert.AreEqual(new Vector3(-.5f, .64f, -2.65f), Feet(0));
        Assert.AreEqual(new Vector3(.5f, .64f, -2.65f), Feet(1));
        Assert.AreEqual(new Vector3(-1.52f, .78f, -1.45f), Feet(2));
        Assert.AreEqual(new Vector3(1.52f, .78f, -1.45f), Feet(3));
    }

    /// <summary>The production truck, both bodies: E works from anywhere behind it, round its rear corners and alongside its
    /// ladders, looking anywhere; never from the cab doors, the nose, the front half of a flank or too far away. The one
    /// InteractionZone (in rear-loader metres, shrunk with the Curb Pup) covers that whole area for both bodies.</summary>
    [TestCase(0)]
    [TestCase(1)]
    public void AnywhereNearTheBackOfEitherBodyOffersARide(int model)
    {
        var truck = ProductionTruck(model, out var controller);
        Runtime("HitchStep").GetMethod("Ensure").Invoke(null, new object[] { controller });
        var hitch = root.GetComponentInChildren(Runtime("HitchStep"), true);
        Assert.NotNull(hitch);
        var zones = hitch.GetComponents(Runtime("InteractionZone"));
        Assert.AreEqual(1, zones.Length, "one boarding zone round the back, not a pocket per hold");
        var zone = zones[0];

        var body = (Bounds)Runtime("TruckControllerV2").GetProperty("ModelBodyBoxMetres").GetValue(controller);
        float half = Mathf.Max(-body.min.x, body.max.x), rear = body.min.z, ground = body.min.y;
        float ladder = Map(truck, Feet(RungLeft)).z;
        bool In(Vector3 real) => (bool)Holds.GetMethod("InBoardingArea").Invoke(null, new object[] { controller, World(truck, real), 0f });
        bool Zone(Vector3 real) => (bool)zone.GetType().GetMethod("Contains").Invoke(zone, new object[] { World(truck, real) });

        var near = new List<Vector3>();
        foreach (float x in new[] { 0f, -half, half, -(half + .8f), half + .8f })
            foreach (float back in new[] { .4f, 1.5f, 2.6f })
                near.Add(new Vector3(x, ground, rear - back));
        for (int side = -1; side <= 1; side += 2)
        {
            near.Add(new Vector3(side * (half + 1f), ground, rear + .2f));        // round the rear corner
            near.Add(new Vector3(side * (half + .7f), ground, ladder));           // alongside the ladder
            near.Add(new Vector3(side * (half + .5f), ground, ladder + .7f));     // walking up the flank toward it
            near.Add(new Vector3(side * (half + .9f), ground + .25f, rear - 1f)); // up on a curb
        }
        for (int hold = 0; hold < 4; hold++) near.Add(Map(truck, Feet(hold)));    // riders get "Hop off" from their hold
        foreach (var p in near)
        {
            Assert.IsTrue(In(p), $"model {model}: {p} is near the back");
            Assert.IsTrue(Zone(p), $"model {model}: the zone reaches {p}");
        }

        var far = new List<Vector3>();
        for (int side = -1; side <= 1; side += 2)
        {
            far.Add(new Vector3(side * (half + .6f), ground, body.max.z - 1f));   // the cab door
            far.Add(new Vector3(side * (half + .6f), ground, body.center.z));     // halfway up the flank
            far.Add(new Vector3(side * (half + 2.2f), ground, rear - 1f));        // across the street
        }
        far.Add(new Vector3(0f, ground, body.max.z + 1.5f));                     // in front of the nose
        far.Add(new Vector3(0f, ground, rear - 3.6f));                           // well behind it
        far.Add(new Vector3(0f, body.max.y + .2f, rear - 1f));                   // on the roof of something
        foreach (var p in far) Assert.IsFalse(In(p), $"model {model}: {p} is not a boarding spot");
    }

    [Test]
    public void TheNearestFreeHoldIsYourSideFirstThenRoundTheBack()
    {
        var byLeftLadder = new Vector3(-2.2f, .4f, -1.5f);
        Assert.AreEqual(RungLeft, Nearest(byLeftLadder, 0));
        Assert.AreEqual(RearLeft, Nearest(byLeftLadder, 1 << RungLeft));
        Assert.AreEqual(RearRight, Nearest(byLeftLadder, 1 << RungLeft | 1 << RearLeft));
        Assert.AreEqual(RungRight, Nearest(byLeftLadder, 1 << RungLeft | 1 << RearLeft | 1 << RearRight));
        Assert.AreEqual(-1, Nearest(byLeftLadder, 0xF), "a full truck has no hold to offer");
        Assert.AreEqual(RearLeft, Nearest(new Vector3(-.01f, .4f, -4f), 0), "dead centre: your own side wins the tie");
        Assert.AreEqual(RearRight, Nearest(new Vector3(.01f, .4f, -4f), 0));
        Assert.AreEqual(RungRight, Nearest(new Vector3(2.4f, .4f, -2.9f), 0), "round the right rear corner, the right ladder is closer");
    }

    /// <summary>Nick, 12 Oct 2026: "prioritize bags and trash over boarding ... dont want e to board or e to anything to stop people
    /// from being able to pick up trash". A bag in reach sits above the ride (and above every look-at prompt, "Enter Truck"
    /// included), under the zone actions meant to dominate and under a downed friend.</summary>
    [Test]
    public void BagsAndTrashInReachAlwaysOutrankBoardingButKeepTheirOwnOrder()
    {
        var player = Runtime("PlayerController");
        float Band(float scan) => (float)player.GetMethod("PickUpScore").Invoke(null, new object[] { scan });
        float ride = (float)Runtime("HitchStep").GetField("ZoneScore").GetRawConstantValue();
        Assert.Greater(Band(0f), ride, "even a far, off-axis bag beats 'Hitch a ride'");
        Assert.Greater(Band(0f), 2f, "and any look-at prompt (a look-at hit scores 2 at most), like the truck's own 'Enter Truck'");
        Assert.Less(Band(2f), 2.2f, "but not the dominant zone actions (workshop 2.2+, heavy lift 2.6, the hutch door 2.7...)");
        Assert.Less(Band(2f), 3f, "nor a downed friend (3+)");
        Assert.Greater(Band(1.5f), Band(1f), "the nearer, better aimed piece still comes first among trash");
        Assert.Greater(Band(1f), Band(.2f));
        Assert.AreEqual(Band(2f), Band(5f), "a scan score is never trusted past its 0..2 range");
        Assert.AreEqual(Band(0f), Band(-1f));
    }

    // ------------------------------------------------------------------ switching spots

    [Test]
    public void SwitchingSpotsGoesRoundTheBackAndSkipsTakenHolds()
    {
        Assert.AreEqual(RearLeft, Next(RungLeft, 0));
        Assert.AreEqual(RearRight, Next(RearLeft, 0));
        Assert.AreEqual(RungRight, Next(RearRight, 0));
        Assert.AreEqual(RungLeft, Next(RungRight, 0), "the right ladder wraps round to the left one");
        Assert.AreEqual(RungRight, Next(RearLeft, 1 << RearRight));
        Assert.AreEqual(RungLeft, Next(RearLeft, 1 << RearRight | 1 << RungRight));
        Assert.AreEqual(-1, Next(RearLeft, 1 << RearRight | 1 << RungRight | 1 << RungLeft), "nowhere to go");
        Assert.AreEqual(-1, Next(7, 0));
        var action = Enum.GetNames(Runtime("GameControls").GetNestedType("Action"));
        CollectionAssert.Contains(action, "SwitchSpot");
        Assert.AreEqual(KeyCode.LeftShift, (KeyCode)Runtime("Keybinds").GetNestedType("Defaults").GetField("SwitchSpot").GetRawConstantValue());
    }

    [Test]
    public void TheShuffleRoundTheBackNeverWalksThroughEitherBody()
    {
        float length = (float)Holds.GetProperty("PathLength").GetValue(null);
        Assert.Greater(length, 5f);
        for (int hold = 0; hold < 4; hold++)
        {
            float s = (float)Holds.GetMethod("PathS").Invoke(null, new object[] { hold });
            Assert.Less(Vector3.Distance(Feet(hold), PathPoint(s)), .001f, $"hold {hold} sits on the path");
        }
        // Rear-loader x ±1.3 from z −2.2; the Curb Pup's body in the same (canonical) metres x ±1.31 from z −2.32.
        for (float s = 0f; s <= length; s += .02f)
        {
            var p = PathPoint(s);
            Assert.IsFalse(Mathf.Abs(p.x) < 1.31f && p.z > -2.32f, $"{p} at {s:0.00} m is inside the truck");
        }
    }

    static Vector3 PathPoint(float s) => (Vector3)Holds.GetMethod("PathPoint").Invoke(null, new object[] { s });

    // ------------------------------------------------------------------ the crash rule

    [Test]
    public void OnlyAHeavyHitFlingsAndTheBarAndPaddingRaiseIt()
    {
        Assert.AreEqual(5f, FlingDv(0, 0, true), "a bare truck: 5 m/s");
        Assert.AreEqual(5f, FlingDv(0, 1, false), "Safety Bar up: as bare");
        Assert.AreEqual(8f, FlingDv(0, 1, true), "Safety Bar down: 8 m/s");
        Assert.AreEqual(6.5f, FlingDv(1, 0, true));
        Assert.AreEqual(8f, FlingDv(2, 0, true));
        Assert.AreEqual(11f, FlingDv(2, 1, true), "both, maxed");
        foreach (string gone in new[] { "GripMax", "LatDrain", "BrakeDrain", "DriftDrain", "LeanInto", "LeanWrong", "HoldOnWindow",
                                        "PromptCost", "MissMult", "BumpCostPerDv", "ImpactCostPerDv", "DominoCost", "ShoveCost", "SafetyBarSpeed" })
            Assert.IsEmpty(Runtime("RideTuning").GetMember(gone, Any), "RideTuning." + gone);
        foreach (string gone in new[] { "Grip", "Lean", "RemoteLean", "GripForHud", "HoldOnActive", "OnShoved", "ApplyHit", "ConsumesSpace" })
            Assert.IsEmpty(Runtime("RideRider").GetMember(gone, Any), "RideRider." + gone);
        foreach (string gone in new[] { "LocalRideLean", "LocalRideGrip", "LocalRideShove", "SendRideJolt" })
            Assert.IsEmpty(Runtime("HandsNet").GetMember(gone, Any), "HandsNet." + gone);
        foreach (string gone in new[] { "GripDrainScale", "GripHit" })
            Assert.IsEmpty(Runtime("Riding").GetMember(gone, Any), "Riding." + gone);
    }

    /// <summary>The crash sensor on the machine that simulates the truck: hard braking, a hard corner, a drift and a curb
    /// never read as a crash; driving into something does, and so does a parked truck being rear-ended.</summary>
    [Test]
    public void OrdinaryRoughDrivingNeverReadsAsACrashButAHitDoes()
    {
        var ride = SensorTruck(out var body);
        var step = ride.GetType().GetMethod("FixedUpdate", PrivateInstance);
        void Drive(Vector3 v) { body.linearVelocity = v; step.Invoke(ride, null); }

        // 50 Hz steps: cruise, a locked-wheel stop (~2.1 g), pull away, 10 m/s round a 10 m radius, a 2 rad/s drift, a curb.
        for (int i = 0; i < 5; i++) Drive(new Vector3(0f, 0f, 10f));
        for (float v = 10f; v > 0f; v -= .42f) Drive(new Vector3(0f, 0f, v));
        for (float v = .3f; v <= 10f; v += .2f) Drive(new Vector3(0f, 0f, v));
        for (int i = 0; i < 80; i++) Drive(Quaternion.Euler(0f, i * 1.15f, 0f) * new Vector3(0f, 0f, 10f));
        for (int i = 0; i < 40; i++) Drive(Quaternion.Euler(0f, 92f + i * 2.3f, 0f) * new Vector3(0f, 0f, 10f));
        Vector3 heading = Quaternion.Euler(0f, 92f + 39 * 2.3f, 0f) * new Vector3(0f, 0f, 10f);
        Drive(heading + Vector3.up * 2f);
        Drive(heading * .98f - Vector3.up * 1.5f);
        Drive(heading * .98f);
        Assert.AreEqual(0, LastCrash(ride), "no crash from braking, cornering, drifting or a curb");
        Assert.Less((float)ride.GetType().GetProperty("MaxWindowDv").GetValue(ride), 2.5f, "and nowhere near one");

        Drive(Vector3.zero);                                                                           // into a tree at 9.8 m/s
        Assert.AreEqual(1, LastCrash(ride), "a hit that stops the truck is a crash");
        var crashes = Crashes(ride);
        Assert.AreEqual(1, crashes.Count);
        // The reading is the biggest horizontal drop over the last three steps: the curb step still ran at the full 10 m/s.
        Assert.That((float)crashes[0].GetType().GetField("dv").GetValue(crashes[0]), Is.EqualTo(10f).Within(.01f));

        Object.DestroyImmediate(root);
        ride = SensorTruck(out body);
        step = ride.GetType().GetMethod("FixedUpdate", PrivateInstance);
        for (int i = 0; i < 4; i++) Drive(Vector3.zero);
        Drive(new Vector3(0f, 0f, 3f));
        Assert.AreEqual(0, LastCrash(ride), "a friendly 3 m/s nudge is not a crash");
        for (int i = 0; i < 4; i++) Drive(Vector3.zero);
        Drive(new Vector3(0f, 0f, 6.5f));
        Assert.AreEqual(1, LastCrash(ride), "rear-ended while parked: a crash from any speed");
    }

    Component SensorTruck(out Rigidbody body)
    {
        root = new GameObject("Isolated ride crash sensor");
        root.transform.position = new Vector3(7000f, 1000f, 7000f);
        body = root.AddComponent<Rigidbody>();
        body.useGravity = false;
        var ride = root.AddComponent(Runtime("RideTruck"));
        ride.GetType().GetMethod("Awake", PrivateInstance).Invoke(ride, null);
        return ride;
    }

    static int LastCrash(Component ride) => (int)ride.GetType().GetProperty("LastCrashSerial").GetValue(ride);

    static IList Crashes(Component ride)
    {
        var crash = ride.GetType().GetNestedType("Crash");
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(crash));
        ride.GetType().GetMethod("CrashesSince").Invoke(ride, new object[] { 0, double.MaxValue, new HashSet<int>(), list });
        return list;
    }

    // ------------------------------------------------------------------ the shop and the kit

    [Test]
    public void GripGlovesAreRetiredAndTheTruckUpgradesNowTakeCrashes()
    {
        object Get(string id) => Runtime("ScrapazonCatalog").GetMethod("Get").Invoke(null, new object[] { id });
        Assert.IsNull(Get("grip_gloves"), "nothing to grip any more: not for sale (old saves keep the id)");
        Assert.AreEqual("grip_gloves", Runtime("RideShop").GetField("GripGloves").GetRawConstantValue());
        foreach (string id in new[] { "padded_step", "safety_bar" })
        {
            var item = Get(id);
            Assert.NotNull(item, id);
            foreach (string effect in (string[])item.GetType().GetField("levelEffects").GetValue(item))
            {
                StringAssert.Contains("crash", effect, id);
                StringAssert.DoesNotContain("grip", effect.ToLowerInvariant(), id);
            }
        }
    }

    [Test]
    public void SmallerProductionRailsStillMeetAllEightGripTargetsAndStayAttached()
    {
        var materials = (IDictionary)Runtime("ShopMaterials").GetField("cache", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        var previous = new Dictionary<object, object>();
        foreach (DictionaryEntry entry in materials) previous.Add(entry.Key, entry.Value);
        root = new GameObject("Isolated compact riding kit");
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

    // ------------------------------------------------------------------ helpers

    /// <summary>The production truck prefab with the given body (0 Curb Pup, 1 rear-loader), parked far from everything.</summary>
    Transform ProductionTruck(int model, out Component controller)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Truck V2.prefab");
        Assert.NotNull(prefab);
        root = Object.Instantiate(prefab);
        root.name = "Isolated truck boarding area";
        root.transform.SetPositionAndRotation(new Vector3(-18000f, 1000f, 18000f), Quaternion.Euler(0f, 131f, 0f));
        root.GetComponent<Rigidbody>().isKinematic = true;
        var visuals = root.AddComponent(Runtime("TruckModelVisuals"));
        Runtime("TruckModelVisuals").GetMethod("ApplyModel").Invoke(visuals, new object[] { model });
        controller = root.GetComponent(Runtime("TruckControllerV2"));
        return root.transform;
    }

    static Vector3 Map(Transform truck, Vector3 canonical) =>
        (Vector3)Runtime("TruckModelVisuals").GetMethod("MapMetres").Invoke(null, new object[] { truck, canonical });

    /// <summary>Real truck metres (rotation only, no scale) → world.</summary>
    static Vector3 World(Transform truck, Vector3 real) => truck.position + truck.rotation * real;
}
