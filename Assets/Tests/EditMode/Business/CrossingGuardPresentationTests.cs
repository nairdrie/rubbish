using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Isolated presentation geometry only: no town, session, clocks, or save changes.</summary>
public class CrossingGuardPresentationTests
{
    static Type LookType => Type.GetType("CrossingGuardLook, Assembly-CSharp", true);
    GameObject parent;
    object guard;
    Transform root;

    [SetUp]
    public void SetUp()
    {
        parent = new GameObject("Isolated crossing guard presentation");
        guard = Activator.CreateInstance(LookType, new object[] { parent.transform, 923UL });
        root = (Transform)LookType.GetProperty("Root").GetValue(guard);
    }

    [TearDown]
    public void TearDown()
    {
        if (parent != null) Object.DestroyImmediate(parent);
    }

    void Pose(float seconds, bool stopping, float speed = 0f) => LookType.GetMethod("Pose").Invoke(guard,
        new object[] { new Vector3(2500f, 100f, 2500f), Quaternion.Euler(0f, 31f, 0f), speed, seconds, stopping, .02f });

    Transform Find(string name)
    {
        foreach (var child in root.GetComponentsInChildren<Transform>(true)) if (child.name == name) return child;
        return null;
    }

    [Test]
    public void GuardAndPaddleAreRendererOnlyAndStayOutOfInteractionScans()
    {
        Assert.IsEmpty(root.GetComponentsInChildren<Collider>(true));
        Assert.IsEmpty(root.GetComponentsInChildren<Rigidbody>(true));
        foreach (var child in root.GetComponentsInChildren<Transform>(true)) Assert.AreEqual(2, child.gameObject.layer);
        Assert.NotNull(Find("Reflective vest bands"));
        Assert.NotNull(Find("Hi-vis cap band"));
        var sign = Find("Double-sided STOP octagon").GetComponent<MeshFilter>().sharedMesh;
        Assert.AreEqual(2, sign.subMeshCount, "Shared white border/lettering and red face.");
        Assert.Greater(sign.vertexCount, 100, "STOP is real two-sided mesh lettering, not an absent texture.");
        Assert.Greater(sign.bounds.size.z, .02f, "Both opposing traffic directions get a sign face.");
    }

    [Test]
    public void StoppingRaisesPaddleAndKeepsItUprightAtTheWrist()
    {
        Pose(0f, false);
        float before = Find("Double-sided STOP octagon").position.y;
        for (int i = 0; i < 20; i++) Pose(i * .02f, true);
        Assert.Greater(Find("Double-sided STOP octagon").position.y - before, .4f);
        Assert.That(Vector3.Dot(Find("HandR").up, Find("Torso").up), Is.GreaterThan(.999f));
        Assert.That(Quaternion.Angle(Find("ArmL").localRotation, Quaternion.identity), Is.GreaterThan(50f),
            "Free arm clearly ushers while the paddle hand stops traffic.");
        Assert.That(Vector3.Distance(root.position, new Vector3(2500f, 100f, 2500f)), Is.LessThan(.001f));
    }

    [Test]
    public void WalkingAnimatesLegsAndGuardsSharePresentationMeshes()
    {
        Pose(.2f, false, 1.2f);
        Assert.Greater(Quaternion.Angle(Find("LegL").localRotation, Find("LegR").localRotation), 30f);
        var other = Activator.CreateInstance(LookType, new object[] { parent.transform, 844UL });
        var otherRoot = (Transform)LookType.GetProperty("Root").GetValue(other);
        Mesh paddle = Find("Double-sided STOP octagon").GetComponent<MeshFilter>().sharedMesh;
        Mesh ribbon = Find("Reflective vest bands").GetComponent<MeshFilter>().sharedMesh;
        bool foundPaddle = false, foundRibbon = false;
        foreach (var filter in otherRoot.GetComponentsInChildren<MeshFilter>(true))
        {
            if (filter.name == "Double-sided STOP octagon") { Assert.AreSame(paddle, filter.sharedMesh); foundPaddle = true; }
            if (filter.name == "Reflective vest bands") { Assert.AreSame(ribbon, filter.sharedMesh); foundRibbon = true; }
        }
        Assert.IsTrue(foundPaddle && foundRibbon);
    }

    [Test]
    public void DestroyRemovesTheWholePresentationHierarchy()
    {
        LookType.GetMethod("Destroy").Invoke(guard, null);
        Assert.IsTrue(root == null);
        Assert.AreEqual(0, parent.transform.childCount);
    }

    [TestCase(-1)]
    [TestCase(1)]
    public void BothPaddleFacesReadStopFromTheApproachingDriversView(int side)
    {
        var mesh = Find("Double-sided STOP octagon").GetComponent<MeshFilter>().sharedMesh;
        var glyphVertices = new List<Vector3>();
        foreach (var vertex in mesh.vertices)
            if (Mathf.Abs(vertex.z - .016f * side) < .00001f) glyphVertices.Add(vertex);
        Assert.Greater(glyphVertices.Count, 0);
        Assert.AreEqual(0, glyphVertices.Count % 4);
        var pixels = new char[5, 23];
        for (int row = 0; row < 5; row++)
            for (int column = 0; column < 23; column++) pixels[row, column] = '0';
        for (int i = 0; i < glyphVertices.Count; i += 4)
        {
            Vector3 center = (glyphVertices[i] + glyphVertices[i + 1] + glyphVertices[i + 2] + glyphVertices[i + 3]) * .25f;
            // A camera on +/-Z looking at the board has screen right = local -/+X.
            float screenX = center.x * -side;
            int column = Mathf.RoundToInt((screenX + 23f * .017f * .5f) / .017f - .5f);
            int row = Mathf.RoundToInt(2f - center.y / .017f);
            Assert.That(column, Is.InRange(0, 22));
            Assert.That(row, Is.InRange(0, 4));
            pixels[row, column] = '1';
        }
        string[] expected = {
            "11111" + "0" + "11111" + "0" + "01110" + "0" + "11110",
            "10000" + "0" + "00100" + "0" + "10001" + "0" + "10001",
            "11111" + "0" + "00100" + "0" + "10001" + "0" + "11110",
            "00001" + "0" + "00100" + "0" + "10001" + "0" + "10000",
            "11111" + "0" + "00100" + "0" + "01110" + "0" + "10000",
        };
        for (int row = 0; row < 5; row++)
        {
            var actual = new char[23];
            for (int column = 0; column < 23; column++) actual[column] = pixels[row, column];
            Assert.AreEqual(expected[row], new string(actual), "STOP must not become mirrored POTS on either side.");
        }
    }
}
