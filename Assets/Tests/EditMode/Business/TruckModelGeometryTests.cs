using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Uses the production prefab and runtime construction, never mutates a save or starts networking.</summary>
public class TruckModelGeometryTests
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    GameObject root;
    Component model;
    Renderer original;
    Vector3 hopperPosition, hopperScale;
    float mass, speed;

    [SetUp]
    public void SetUp()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Truck V2.prefab");
        Assert.NotNull(prefab);
        root = Object.Instantiate(prefab);
        root.name = "Isolated truck model geometry";
        root.transform.SetPositionAndRotation(new Vector3(18000f, 1000f, 18000f), Quaternion.Euler(0f, 73f, 0f));
        root.GetComponent<Rigidbody>().isKinematic = true;
        mass = root.GetComponent<Rigidbody>().mass;
        speed = (float)Runtime("TruckControllerV2").GetField("maxSpeed").GetValue(root.GetComponent(Runtime("TruckControllerV2")));
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            if (renderer.name == "TruckBody") { original = renderer; break; }
        Assert.NotNull(original);
        var hopper = root.GetComponentInChildren(Runtime("TruckHopper")).transform;
        hopperPosition = hopper.localPosition;
        hopperScale = hopper.localScale;
        model = root.AddComponent(Runtime("TruckModelVisuals"));
    }

    [TearDown]
    public void TearDown()
    {
        if (root != null)
        {
            // RideTruck's runtime OnDestroy uses deferred destruction; EditMode owns the complete test hierarchy.
            var ride = root.GetComponent(Runtime("RideTruck"));
            if (ride != null) Runtime("RideTruck").GetField("kit", Private).SetValue(ride, null);
            Object.DestroyImmediate(root);
        }
        Physics.SyncTransforms();
    }

    void Apply(int value) => Runtime("TruckModelVisuals").GetMethod("ApplyModel").Invoke(model, new object[] { value });
    Vector3 Map(Vector3 value) => (Vector3)Runtime("TruckModelVisuals").GetMethod("MapMetres")
        .Invoke(null, new object[] { root.transform, value });
    Vector3 World(Vector3 value) => (Vector3)Runtime("ShopModKit").GetMethod("TruckPoint")
        .Invoke(null, new object[] { root.transform, value });
    Vector3 Unmap(Vector3 value) => (Vector3)Runtime("ShopModKit").GetMethod("ToTruckMetres")
        .Invoke(null, new object[] { root.transform, value });

    [Test]
    public void MiniHasItsOwnSmallerPhysicalShellAndFourWheelsAtUnchangedCruiseSpeed()
    {
        Apply(0);
        Physics.SyncTransforms();
        Assert.IsFalse(original.gameObject.activeSelf, "The full-size mesh AND its convex collider must be disabled.");
        var mini = root.transform.Find("Mini cab-over truck");
        Assert.NotNull(mini);
        Assert.GreaterOrEqual(mini.GetComponentsInChildren<BoxCollider>().Length, 8, "This is a physical body, not just a smaller skin.");
        int activeWheels = 0;
        foreach (var wheel in root.GetComponentsInChildren<WheelCollider>(true)) if (wheel.enabled) activeWheels++;
        Assert.AreEqual(4, activeWheels);
        var controller = root.GetComponent(Runtime("TruckControllerV2"));
        var bounds = (Bounds)Runtime("TruckControllerV2").GetProperty("ModelBodyBoxMetres").GetValue(controller);
        Assert.Less(bounds.size.z, 4.7f);
        Assert.Less(bounds.size.x, 2.2f);
        Assert.AreEqual(speed, Runtime("TruckControllerV2").GetField("maxSpeed").GetValue(controller));
        Assert.That(root.GetComponent<Rigidbody>().mass, Is.EqualTo(mass * .60f).Within(.001f));
        Assert.IsEmpty(mini.Find("Visible load bag 0").GetComponentsInChildren<Collider>(true));
    }

    [Test]
    public void ModelSwitchRoundTripRestoresAuthoredBodyHopperWheelsAndMassWithoutAccumulatedScale()
    {
        for (int i = 0; i < 3; i++) { Apply(0); Apply(0); Apply(1); Apply(1); }
        Assert.IsTrue(original.gameObject.activeSelf);
        Assert.IsFalse(root.transform.Find("Mini cab-over truck").gameObject.activeSelf);
        Assert.AreEqual(mass, root.GetComponent<Rigidbody>().mass);
        var hopper = root.GetComponentInChildren(Runtime("TruckHopper")).transform;
        Assert.That(Vector3.Distance(hopperPosition, hopper.localPosition), Is.LessThan(.0001f));
        Assert.That(Vector3.Distance(hopperScale, hopper.localScale), Is.LessThan(.0001f));
        int activeWheels = 0;
        foreach (var wheel in root.GetComponentsInChildren<WheelCollider>(true)) if (wheel.enabled) activeWheels++;
        Assert.AreEqual(6, activeWheels);
        Apply(0);
        Assert.That(Vector3.Distance(Vector3.Scale(hopperPosition,
            (Vector3)Runtime("TruckModelVisuals").GetField("MiniScale").GetValue(null)), hopper.localPosition), Is.LessThan(.0001f));
    }

    [Test]
    public void ModelCoordinatesRoundTripUnderTruckRotationAndScale()
    {
        foreach (int variant in new[] { 0, 1 })
        {
            Apply(variant);
            foreach (var point in new[] { new Vector3(.8f, 1.5f, -2.4f), new Vector3(-1.92f, .65f, -1.45f), new Vector3(-.5f, 2.02f, 3.15f) })
                Assert.That(Vector3.Distance(point, Unmap(World(point))), Is.LessThan(.004f));
        }
    }

    [Test]
    public void AllFourMiniRiderFeetAndHandsStillMeetTheirAttachedKit()
    {
        Apply(0);
        var ride = root.AddComponent(Runtime("RideTruck"));
        Runtime("RideTruck").GetMethod("BuildKit", Private).Invoke(ride, new object[] { 0, 0, true });
        var kit = (Transform)Runtime("RideTruck").GetField("kit", Private).GetValue(ride);
        Physics.SyncTransforms();
        var holds = Runtime("RideHolds");
        for (int hold = 0; hold < 4; hold++)
        {
            Vector3 feet = (Vector3)holds.GetMethod("FeetMetres").Invoke(null, new object[] { hold });
            var pose = (Pose)holds.GetMethod("PoseOn").Invoke(null, new object[] { root.transform, hold });
            Assert.That(Vector3.Distance(World(feet), pose.position), Is.LessThan(.001f));
            for (int hand = 0; hand < 2; hand++)
            {
                Vector3 grip = World((Vector3)holds.GetMethod("GripMetres").Invoke(null, new object[] { hold, hand == 0 }));
                float closest = float.MaxValue;
                foreach (var rail in kit.GetComponentsInChildren<Renderer>())
                    if (rail.name == "LadderRail" || rail.name == "RearGripBar")
                        closest = Mathf.Min(closest, Vector3.Distance(grip, rail.bounds.ClosestPoint(grip)));
                Assert.Less(closest, .035f, $"Mini hold {hold} hand {hand} contacts a visible grab rail.");
            }
        }
        Apply(1);
        Assert.That(Vector3.Distance(kit.lossyScale, Vector3.one), Is.LessThan(.001f), "Already-created attachments resize on trade-in too.");
    }

    [Test]
    public void AttachmentsCreatedBeforeModelAndAfterModelUseExactlyOneScaleConversion()
    {
        // The scene may create riding/hopper hardware before ShopEffectsRunner first resolves business ownership.
        Object.DestroyImmediate(model);
        var attachment = (Transform)Runtime("ShopModKit").GetMethod("MetreRoot").Invoke(null, new object[] { root.transform, "Early attachment" });
        model = root.AddComponent(Runtime("TruckModelVisuals"));
        Apply(0);
        var scale = (Vector3)Runtime("TruckModelVisuals").GetField("MiniScale").GetValue(null);
        Assert.That(Vector3.Distance(attachment.lossyScale, scale), Is.LessThan(.001f));
        var later = (Transform)Runtime("ShopModKit").GetMethod("MetreRoot").Invoke(null, new object[] { root.transform, "Late attachment" });
        Assert.That(Vector3.Distance(later.lossyScale, scale), Is.LessThan(.001f));
        Apply(1);
        Assert.That(Vector3.Distance(attachment.lossyScale, Vector3.one), Is.LessThan(.001f));
        Assert.That(Vector3.Distance(later.lossyScale, Vector3.one), Is.LessThan(.001f));
    }

    [Test]
    public void RearLoaderTradeInChecksTheLargerNoseButIgnoresTriggersAndItsOwnColliders()
    {
        Apply(0);
        var controller = root.GetComponent(Runtime("TruckControllerV2"));
        var check = Runtime("TruckProgression").GetMethod("HasReplacementClearance");
        bool Clear() => (bool)check.Invoke(null, new object[] { controller });
        Physics.SyncTransforms();
        Assert.IsTrue(Clear(), "The mini's own shell, wheels and hopper must not prevent a trade-in.");

        var obstruction = new GameObject("Isolated larger-truck nose obstruction");
        try
        {
            obstruction.layer = root.layer;
            Assert.IsFalse(Physics.GetIgnoreLayerCollision(root.layer, obstruction.layer), "Use a solid vehicle-collision layer.");
            obstruction.transform.SetPositionAndRotation(root.transform.position +
                root.transform.rotation * new Vector3(0f, 1.8f, 3.5f), root.transform.rotation);
            var box = obstruction.AddComponent<BoxCollider>();
            box.size = new Vector3(.3f, .8f, .3f);
            var miniBounds = (Bounds)Runtime("TruckControllerV2").GetProperty("ModelBodyBoxMetres").GetValue(controller);
            Assert.Greater(3.5f - box.size.z * .5f, miniBounds.max.z,
                "The obstacle clears the mini; only the larger replacement nose reaches it.");
            Physics.SyncTransforms();
            Assert.IsFalse(Clear(), "A nearby solid wall/vehicle in the replacement footprint must block growth.");

            box.isTrigger = true;
            Physics.SyncTransforms();
            Assert.IsTrue(Clear(), "An interaction or depot trigger does not obstruct a larger truck.");

            box.isTrigger = false;
            obstruction.transform.SetParent(root.transform, true);
            Physics.SyncTransforms();
            Assert.IsTrue(Clear(), "Even a solid attachment in the growth area is ignored when it belongs to this truck.");
        }
        finally
        {
            Object.DestroyImmediate(obstruction);
            Physics.SyncTransforms();
        }
    }
}
