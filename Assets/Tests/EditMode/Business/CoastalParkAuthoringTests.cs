using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Preserved park fittings follow road edits without rebuilding the playground or surrounding parcels.</summary>
public class CoastalParkAuthoringTests
{
    static Type Crossing => Type.GetType("CoastalParkRoutes+Crossing, Assembly-CSharp", true);
    static Type Builder => Type.GetType("NeighborhoodBuilder, Assembly-CSharp-Editor", true);
    GameObject root;
    Transform park, path, southSign, northSign, stand, playground, yard, infill;
    readonly Vector3 centre = new Vector3(200, 0, 200);

    [SetUp]
    public void Setup()
    {
        root = new GameObject("Park fitting scratch town");
        park = Child(root.transform, "Park", centre);
        path = Child(park, "ParkPath_NS", centre + Vector3.up * .04f);
        path.localScale = new Vector3(2.5f, .06f, 100f);
        southSign = Child(park, "ParkSign", centre + new Vector3(-2.65f, .1f, -40));
        northSign = Child(park, "ParkSign", centre + new Vector3(2.65f, .1f, 40));
        stand = Child(park, "SM_lemonade_shop", centre + new Vector3(2.75f, .2f, -40));
        playground = Child(park, "Preserved playground", centre + new Vector3(-12, 0, 3));
        playground.localScale = new Vector3(3, 2, 4);
        yard = Child(root.transform, "CohesiveBackyards", centre + Vector3.right * 30);
        infill = Child(root.transform, "CoastalInfill", centre + Vector3.left * 40);
    }

    [TearDown]
    public void Cleanup() { if (root != null) Object.DestroyImmediate(root); }

    static Transform Child(Transform parent, string name, Vector3 at)
    {
        var t = new GameObject(name).transform; t.SetParent(parent, false); t.position = at; return t;
    }
    Array Crossings(Vector3 south, Vector3 north)
    {
        var list = Array.CreateInstance(Crossing, 2);
        for (int i = 0; i < 2; i++)
        {
            var crossing = Activator.CreateInstance(Crossing);
            Crossing.GetField("roadId").SetValue(crossing, i == 0 ? "south" : "north");
            Crossing.GetField("position").SetValue(crossing, i == 0 ? south : north);
            Crossing.GetField("forward").SetValue(crossing, Vector3.right);
            Crossing.GetField("halfWidth").SetValue(crossing, 5f); list.SetValue(crossing, i);
        }
        return list;
    }
    void Fit(Array crossings) => Builder.GetMethod("FitCoastalParkEntrances", BindingFlags.NonPublic | BindingFlags.Static, null,
        new[] { typeof(Transform), typeof(Vector3), Crossing.MakeArrayType() }, null).Invoke(null, new object[] { park, centre, crossings });
    Array OrdinaryCrossings() => Crossings(centre + Vector3.back * 40, centre + Vector3.forward * 40);
    void Near(Vector3 a, Vector3 b) => Assert.That(Vector3.Distance(a, b), Is.LessThan(.0001f));

    [Test]
    public void FittingPlacesPathAtBothSidewalksAndKeepsSignAndLemonadeHeights()
    {
        Fit(OrdinaryCrossings());
        Near(centre + new Vector3(0, .04f, -32), path.TransformPoint(Vector3.back * .5f));
        Near(centre + new Vector3(0, .04f, 32), path.TransformPoint(Vector3.forward * .5f));
        Near(centre + new Vector3(-2.65f, .1f, -29), southSign.position);
        Near(centre + new Vector3(2.65f, .1f, 29), northSign.position);
        Near(centre + new Vector3(2.75f, .2f, -28.6f), stand.position);
        Assert.AreEqual(.06f, path.localScale.y); Assert.AreEqual(2.5f, path.localScale.x);
    }

    [Test]
    public void ASecondRoadEditRefitsTheSameObjectsWithoutMovingPlaygroundYardsOrInfill()
    {
        Fit(OrdinaryCrossings());
        var unchanged = new[] { park, playground, yard, infill };
        var positions = unchanged.Select(t => t.position).ToArray();
        var scales = unchanged.Select(t => t.localScale).ToArray();
        var ids = root.GetComponentsInChildren<Transform>(true).Select(t => t.GetInstanceID()).ToArray();
        Fit(Crossings(centre + Vector3.back * 33, centre + Vector3.forward * 44));
        Near(centre + new Vector3(0, .04f, -25), path.TransformPoint(Vector3.back * .5f));
        Near(centre + new Vector3(0, .04f, 36), path.TransformPoint(Vector3.forward * .5f));
        for (int i = 0; i < unchanged.Length; i++) { Near(positions[i], unchanged[i].position); Assert.AreEqual(scales[i], unchanged[i].localScale); }
        CollectionAssert.AreEqual(ids, root.GetComponentsInChildren<Transform>(true).Select(t => t.GetInstanceID()).ToArray());
    }

    [Test]
    public void RepeatingTheSameRoadEditIsIdempotent()
    {
        var crossings = OrdinaryCrossings(); Fit(crossings);
        var transforms = root.GetComponentsInChildren<Transform>(true);
        var positions = transforms.Select(t => t.position).ToArray();
        var rotations = transforms.Select(t => t.rotation).ToArray();
        var scales = transforms.Select(t => t.localScale).ToArray();
        Fit(crossings);
        for (int i = 0; i < transforms.Length; i++)
        { Near(positions[i], transforms[i].position); Assert.AreEqual(rotations[i], transforms[i].rotation); Assert.AreEqual(scales[i], transforms[i].localScale); }
    }

    [Test]
    public void FullHierarchyUndoRestoresFittingsOnTheSameRollbackMechanismUsedByCityTransactions()
    {
        var transforms = root.GetComponentsInChildren<Transform>(true);
        var positions = transforms.Select(t => t.position).ToArray();
        var scales = transforms.Select(t => t.localScale).ToArray();
        Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup();
        try
        {
            Undo.RegisterFullObjectHierarchyUndo(root, "Park fitting scratch rollback");
            Fit(OrdinaryCrossings());
            Assert.Greater(Vector3.Distance(positions[Array.IndexOf(transforms, southSign)], southSign.position), 1);
        }
        finally { Undo.RevertAllDownToGroup(group); }
        for (int i = 0; i < transforms.Length; i++) { Near(positions[i], transforms[i].position); Assert.AreEqual(scales[i], transforms[i].localScale); }
    }

    [Test]
    public void MissingCrossingFailsBeforeMovingAnyFitting()
    {
        var transforms = root.GetComponentsInChildren<Transform>(true); var positions = transforms.Select(t => t.position).ToArray();
        var error = Assert.Throws<TargetInvocationException>(() => Fit(Array.CreateInstance(Crossing, 1)));
        Assert.IsInstanceOf<InvalidOperationException>(error.InnerException);
        for (int i = 0; i < transforms.Length; i++) Near(positions[i], transforms[i].position);
    }
}
