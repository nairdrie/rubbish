using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

public class OnFootTruckCameraTests
{
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static readonly Vector3 Origin = new Vector3(24000, 1000, 24000);
    GameObject obstacle;
    Component truck;
    readonly List<GameObject> pipelineRoots = new List<GameObject>();
    static Type CameraType => Type.GetType("Cinemachine.CinemachineVirtualCamera, Cinemachine", true);
    const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    Component camera;
    Transform cameraTarget;

    [SetUp]
    public void SetUp()
    {
        obstacle = new GameObject("Camera truck regression");
        obstacle.transform.position = Origin + Vector3.back * 3f;
        obstacle.AddComponent<Rigidbody>().isKinematic = true;
        truck = obstacle.AddComponent(Runtime("TruckControllerV2"));
        ((Behaviour)truck).enabled = false;
        obstacle.AddComponent<BoxCollider>().size = new Vector3(4, 4, 2);
        Physics.SyncTransforms();
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var go in pipelineRoots) if (go != null) Object.DestroyImmediate(go);
        pipelineRoots.Clear();
        Object.DestroyImmediate(obstacle);
        Physics.SyncTransforms();
    }

    float Resolve(Component ignore = null) => (float)Runtime("OnFootTruckCamera").GetMethod("ClearDistance")
        .Invoke(null, new object[] { Origin, Vector3.back, 7f, ignore });

    [Test]
    public void CameraStopsBeforeTruckAndProtectsItsNearPlane()
    {
        Assert.That(Resolve(), Is.InRange(1.60f, 1.70f));
    }

    [Test]
    public void TruckCanBeIgnoredForItsMountedRider()
    {
        Assert.That(Resolve(truck), Is.EqualTo(7f));
    }

    [Test]
    public void MovingTruckAwayRestoresFullCameraDistance()
    {
        obstacle.transform.position += Vector3.right * 10f;
        Physics.SyncTransforms();
        Assert.That(Resolve(), Is.EqualTo(7f));
    }

    [Test]
    public void BagsAndUnrelatedDefaultLayerPropsDoNotBecomeCameraWalls()
    {
        Object.DestroyImmediate(truck);
        Assert.That(Resolve(), Is.EqualTo(7f));
    }

    [Test]
    public void TruckInteractionTriggersDoNotPushTheCamera()
    {
        obstacle.GetComponent<BoxCollider>().isTrigger = true;
        Physics.SyncTransforms();
        Assert.That(Resolve(), Is.EqualTo(7f));
    }

    void BuildPipeline(float yaw = 0f, Component player = null)
    {
        // Copy only the real camera, not a player prefab: no gameplay, network session, input or save is created.
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab");
        Assert.NotNull(prefab);
        Component authored = null;
        foreach (var candidate in prefab.GetComponentsInChildren(CameraType, true))
            if (candidate.name == "PlayerFollowCamera") authored = candidate;
        Assert.NotNull(authored);
        var go = Object.Instantiate(authored.gameObject); pipelineRoots.Add(go);
        go.name = "Isolated walking camera pipeline"; camera = go.GetComponent(CameraType);
        var target = new GameObject("Isolated walking camera target"); pipelineRoots.Add(target); cameraTarget = target.transform;
        cameraTarget.SetPositionAndRotation(Origin, Quaternion.Euler(0f, yaw, 0f));
        CameraType.GetProperty("Follow").SetValue(camera, cameraTarget);
        CameraType.GetProperty("LookAt").SetValue(camera, cameraTarget);
        Runtime("OnFootTruckCamera").GetMethod("Ensure").Invoke(null, new object[] { camera, player });
        // EditMode does not promise MonoBehaviour.Awake. Register extensions in authored order, with the new
        // walking-truck extension last, exactly as adding it to the already-awake player does at network spawn.
        var extensionType = Type.GetType("Cinemachine.CinemachineExtension, Cinemachine", true);
        foreach (var extension in go.GetComponents(extensionType))
            CameraType.GetMethod("AddExtension").Invoke(camera, new object[] { extension });
    }

    Vector3 Evaluate(float dt = -1f)
    {
        Physics.SyncTransforms();
        CameraType.GetMethod("InternalUpdateCameraState").Invoke(camera, new object[] { Vector3.up, dt });
        object state = CameraType.GetProperty("State").GetValue(camera);
        return (Vector3)state.GetType().GetProperty("FinalPosition").GetValue(state);
    }

    Component Player()
    {
        var go = new GameObject("Inactive isolated camera owner"); pipelineRoots.Add(go); go.SetActive(false);
        return go.AddComponent(Runtime("PlayerController"));
    }

    static void Driving(Component player, bool driving)
    {
        // Seed only this unspawned fixture's backing value. Do not invoke NGO writes or create a NetworkManager.
        var variable = Runtime("PlayerController").GetField("isDriving", Private).GetValue(player);
        variable.GetType().GetField("m_InternalValue", Private).SetValue(variable, driving);
        Assert.AreEqual(driving, Runtime("PlayerController").GetProperty("IsDriving").GetValue(player));
    }

    [TestCase(0f)]
    [TestCase(90f)]
    [TestCase(-90f)]
    public void ActualCameraPipelineImmediatelyStopsAtTruckFromRearAndBothSides(float yaw)
    {
        BuildPipeline(yaw);
        Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
        obstacle.transform.SetPositionAndRotation(Origin + rotation * Vector3.right * 20f, rotation);
        Vector3 wide = Evaluate();
        obstacle.transform.position = Origin + rotation * Vector3.back * 3f;
        Vector3 stopped = Evaluate(1f / 60f);
        Assert.Less(Vector3.Distance(stopped, Origin), Vector3.Distance(wide, Origin) - 1f,
            "Body-stage callback must change the real camera state on the first blocked frame");
        Assert.GreaterOrEqual(Vector3.Distance(stopped, obstacle.GetComponent<Collider>().ClosestPoint(stopped)), .29f,
            "The final state keeps sphere/near-plane clearance, not merely a clear centre ray");
        Assert.Greater(Vector3.Dot((stopped - Origin).normalized, (wide - Origin).normalized), .999f,
            "Collision pull-in preserves the player's selected orbit direction");
    }

    [Test]
    public void ActualCameraPipelineEasesOutAfterTruckClearsInsteadOfPoppingOrStayingZoomed()
    {
        BuildPipeline();
        float close = Vector3.Distance(Evaluate(), Origin);
        obstacle.GetComponent<Collider>().enabled = false;
        float next = Vector3.Distance(Evaluate(1f / 60f), Origin);
        Assert.Greater(next, close + .01f);
        float previous = next;
        for (int i = 0; i < 120; i++)
        {
            float distance = Vector3.Distance(Evaluate(1f / 60f), Origin);
            Assert.GreaterOrEqual(distance, previous - .003f); previous = distance;
        }
        float wide = Vector3.Distance(Evaluate(), Origin);
        Assert.Less(next, wide - .5f, "The first clear frame must still be easing outward");
        Assert.That(previous, Is.EqualTo(wide).Within(.025f), "The authored full walking view returns");
    }

    [Test]
    public void DrivingBypassesWalkingCorrectionAndResetsItsDampingHistory()
    {
        var player = Player(); BuildPipeline(player: player);
        float blocked = Vector3.Distance(Evaluate(), Origin);
        Driving(player, true);
        float driving = Vector3.Distance(Evaluate(1f / 60f), Origin);
        Assert.Greater(driving, blocked + 1f);
        obstacle.GetComponent<Collider>().enabled = false;
        Driving(player, false);
        Assert.That(Vector3.Distance(Evaluate(1f / 60f), Origin), Is.EqualTo(driving).Within(.025f),
            "Leaving the truck must not inherit the pre-driving zoom-in history");
    }

    [Test]
    public void MountedRiderIgnoresOnlyTheirOwnTruckAndDismountRestoresObstruction()
    {
        var player = Player();
        var rider = player.gameObject.AddComponent(Runtime("RideRider"));
        Runtime("RideRider").GetProperty("Player").SetValue(rider, player);
        var ridingTruck = obstacle.AddComponent(Runtime("RideTruck"));
        Runtime("RideTruck").GetProperty("Truck").SetValue(ridingTruck, truck);
        Runtime("RideRider").GetField("soloTruck", Private).SetValue(rider, ridingTruck);
        Runtime("RideRider").GetField("soloRiding", Private).SetValue(rider, true);
        BuildPipeline(player: player);
        float mounted = Vector3.Distance(Evaluate(), Origin);
        Assert.Greater(mounted, 4f, "Mounted own-truck body must not collapse the view");

        var other = new GameObject("Other truck still blocks rider camera"); pipelineRoots.Add(other);
        other.transform.position = Origin + Vector3.back * 2f;
        other.AddComponent<Rigidbody>().isKinematic = true;
        ((Behaviour)other.AddComponent(Runtime("TruckControllerV2"))).enabled = false;
        other.AddComponent<BoxCollider>().size = new Vector3(2f, 3f, .5f);
        Assert.Less(Vector3.Distance(Evaluate(1f / 60f), Origin), mounted - 1f);
        other.SetActive(false);
        Runtime("RideRider").GetField("soloRiding", Private).SetValue(rider, false);
        Assert.Less(Vector3.Distance(Evaluate(), Origin), mounted - 1f,
            "Once on foot, the former mounted truck is a camera obstruction again");
    }
}
