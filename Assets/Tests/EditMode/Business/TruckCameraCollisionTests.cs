using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Exercises the authored driving camera and its actual Cinemachine collision pipeline,
/// without creating a truck, player, network session or save. The driving camera still
/// keeps its wide, freely orbiting shot wherever the view is unobstructed.
/// </summary>
public class TruckCameraCollisionTests
{
    static Type CameraType => Type.GetType("Cinemachine.CinemachineVirtualCamera, Cinemachine", true);
    static Type ColliderType => Type.GetType("Cinemachine.CinemachineCollider, Cinemachine", true);
    static readonly Vector3 TestOrigin = new Vector3(15000f, 1000f, 15000f);
    readonly List<GameObject> roots = new List<GameObject>();
    Component camera;
    Component obstacleAvoidance;
    Transform target;

    [SetUp]
    public void SetUp()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Truck V2.prefab");
        Assert.NotNull(prefab);
        Component authoredCamera = null;
        foreach (var candidate in prefab.GetComponentsInChildren(CameraType, true))
            if (candidate.name == "TruckFollowCamera") authoredCamera = candidate;
        Assert.NotNull(authoredCamera);
        var rig = Object.Instantiate(authoredCamera.gameObject);
        rig.name = "Isolated authored truck camera";
        roots.Add(rig);
        camera = rig.GetComponent(CameraType);
        obstacleAvoidance = rig.GetComponent(ColliderType);
        Assert.NotNull(obstacleAvoidance);

        var pivot = new GameObject("Isolated truck camera pivot");
        roots.Add(pivot);
        target = pivot.transform;
        target.position = TestOrigin + Vector3.up * 2f;
        CameraType.GetProperty("Follow").SetValue(camera, target);
        CameraType.GetProperty("LookAt").SetValue(camera, target);
        rig.transform.position = target.position;
        Physics.SyncTransforms();
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var root in roots) if (root != null) Object.DestroyImmediate(root);
        roots.Clear();
        Physics.SyncTransforms();
    }

    object Field(string name) => ColliderType.GetField(name).GetValue(obstacleAvoidance);

    Vector3 Evaluate(float deltaTime = -1f)
    {
        Physics.SyncTransforms();
        CameraType.GetMethod("InternalUpdateCameraState").Invoke(camera, new object[] { Vector3.up, deltaTime });
        object state = CameraType.GetProperty("State").GetValue(camera);
        return (Vector3)state.GetType().GetProperty("FinalPosition").GetValue(state);
    }

    BoxCollider Box(string name, Vector3 center, Vector3 size, string layer = "Walls")
    {
        var go = new GameObject(name);
        roots.Add(go);
        go.layer = LayerMask.NameToLayer(layer);
        go.transform.position = TestOrigin + center;
        var box = go.AddComponent<BoxCollider>();
        box.size = size;
        return box;
    }

    void AssertClearOf(BoxCollider wall, Vector3 position)
    {
        float radius = (float)Field("m_CameraRadius");
        Assert.GreaterOrEqual(Vector3.Distance(position, wall.ClosestPoint(position)), radius - .02f,
            "Camera must retain clearance for the visible near-plane corners, not only its center.");
    }

    [Test]
    public void AuthoredDrivingCameraImmediatelyPullsForwardAndProtectsNearPlaneCorners()
    {
        Assert.AreEqual(LayerMask.GetMask("Walls"), ((LayerMask)Field("m_CollideAgainst")).value);
        Assert.IsTrue((bool)Field("m_AvoidObstacles"));
        Assert.AreEqual("PullCameraForward", Field("m_Strategy").ToString());
        Assert.AreEqual(0f, Field("m_DistanceLimit"), "Use the whole wide driving view, not a short feeler.");
        Assert.AreEqual(0f, Field("m_MinimumOcclusionTime"));
        Assert.AreEqual(0f, Field("m_DampingWhenOccluded"), "Never ease inward through a solid wall.");
        Assert.Greater((float)Field("m_Damping"), 0f, "Ease back out when the obstacle clears.");

        object lens = CameraType.GetField("m_Lens").GetValue(camera);
        float near = (float)lens.GetType().GetField("NearClipPlane").GetValue(lens);
        float fov = (float)lens.GetType().GetField("FieldOfView").GetValue(lens);
        float halfHeight = Mathf.Tan(fov * Mathf.Deg2Rad * .5f) * near;
        float halfWidth = halfHeight * (16f / 9f);
        float nearCornerRadius = new Vector3(halfWidth, halfHeight, near).magnitude;
        Assert.Greater((float)Field("m_CameraRadius"), nearCornerRadius + .02f);
    }

    [Test]
    public void ClearDrivingViewKeepsItsWideCoopFraming()
    {
        var position = Evaluate();
        Assert.That(position.x - target.position.x, Is.EqualTo(0f).Within(.01f));
        Assert.That(position.y - target.position.y, Is.EqualTo(8.5f).Within(.01f));
        Assert.That(position.z - target.position.z, Is.EqualTo(-14.5f).Within(.01f));
    }

    [Test]
    public void NewlyEncounteredRearWallPullsInOnTheFirstFrame()
    {
        var widePosition = Evaluate();
        var wall = Box("Rear shop wall", new Vector3(0f, 10f, -7f), new Vector3(40f, 40f, .3f));
        var position = Evaluate(1f / 60f);
        Assert.Greater(position.z - TestOrigin.z, -6.5f);
        Assert.Less(Vector3.Distance(position, target.position), Vector3.Distance(widePosition, target.position) - 5f);
        AssertClearOf(wall, position);
    }

    [TestCase(-90f, 1f)]
    [TestCase(90f, -1f)]
    public void FreeOrbitCannotPassThroughEitherSideWall(float yaw, float side)
    {
        target.rotation = Quaternion.Euler(0f, yaw, 0f);
        var wall = Box("Workshop side wall", new Vector3(side * 7f, 10f, 0f), new Vector3(.3f, 40f, 40f));
        var position = Evaluate();
        Assert.Less(Mathf.Abs(position.x - TestOrigin.x), 6.5f);
        Assert.Greater((position.x - TestOrigin.x) * side, 1f, "Orbit direction must survive collision correction.");
        AssertClearOf(wall, position);
    }

    [Test]
    public void LowRoofPullsDrivingCameraInsideInsteadOfAboveTheBuilding()
    {
        var roof = Box("Workshop roof", new Vector3(0f, 5f, 0f), new Vector3(40f, .2f, 40f));
        var position = Evaluate();
        Assert.Less(position.y - TestOrigin.y, 4.6f);
        Assert.Greater(position.y, target.position.y);
        AssertClearOf(roof, position);
    }

    [Test]
    public void ClearingTheWallRestoresTheWholeDrivingView()
    {
        var widePosition = Evaluate();
        var wall = Box("Temporary wall", new Vector3(0f, 10f, -7f), new Vector3(40f, 40f, .3f));
        Assert.Less(Vector3.Distance(Evaluate(), target.position), 10f);
        Object.DestroyImmediate(wall.gameObject);
        // A cut eliminates damping history: the authored desired shot itself is never shortened.
        Assert.That(Vector3.Distance(Evaluate(), widePosition), Is.LessThan(.01f));
    }

    [Test]
    public void OwnTruckAndRidersDoNotCollapseTheDrivingCamera()
    {
        var widePosition = Evaluate();
        Box("Own truck body", new Vector3(0f, 2f, -1f), new Vector3(3f, 4f, 6f), "Default");
        Box("Rear rider", new Vector3(0f, 4f, -4f), new Vector3(1f, 3f, 1f), "Default");
        Box("Held bag", new Vector3(0f, 5f, -6f), new Vector3(2f, 2f, 2f), "Interactable");
        Assert.That(Vector3.Distance(Evaluate(), widePosition), Is.LessThan(.01f));
    }
}
