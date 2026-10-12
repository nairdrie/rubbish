using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

/// <summary>Terrain relief (Docs/CityAuthoring.md, "Terrain relief"): the height grid, laying meshes onto it, the shape
/// of the land around roads and pads, and how the truck treats a hill. Pure code only; the bake itself is checked by
/// publishing the town.</summary>
public class TownReliefTests
{
    static readonly Type FieldType = Type.GetType("TownReliefField, Assembly-CSharp", true);
    static readonly Type DesignType = Type.GetType("TownReliefDesign, Assembly-CSharp", true);
    static readonly Type MeshType = Type.GetType("TownReliefMesh, Assembly-CSharp", true);
    static readonly Type ReliefType = Type.GetType("TownRelief, Assembly-CSharp", true);
    static readonly Type GradeType = Type.GetType("TruckGradeRules, Assembly-CSharp", true);

    static object NewField(float x0, float z0, float cell, int nx, int nz, Func<float, float, float> height)
    {
        var field = Activator.CreateInstance(FieldType, x0, z0, cell, nx, nz);
        var h = (float[])FieldType.GetField("h").GetValue(field);
        for (int iz = 0; iz < nz; iz++) for (int ix = 0; ix < nx; ix++) h[iz * nx + ix] = height(x0 + ix * cell, z0 + iz * cell);
        FieldType.GetMethod("Quantise").Invoke(field, null);
        return field;
    }
    static float Height(object field, float x, float z) => (float)FieldType.GetMethod("Height").Invoke(field, new object[] { x, z });
    static T Get<T>(object o, string name) => (T)o.GetType().GetField(name).GetValue(o);

    [TearDown] public void Forget() => ReliefType.GetMethod("Reload").Invoke(null, null);

    [Test]
    public void AGridPointReadsItsOwnHeightAndTheSurfaceHasNoSteps()
    {
        var field = NewField(-6f, 9f, 3f, 12, 10, (x, z) => Mathf.Sin(x * 0.3f) * 2f + z * 0.1f);
        var h = Get<float[]>(field, "h");
        Assert.AreEqual(h[4 * 12 + 7], Height(field, -6f + 7 * 3f, 9f + 4 * 3f), 1e-5f);
        // crossing a cell edge or a diagonal never jumps
        for (float x = -5.9f; x < 26f; x += 0.37f)
            for (float z = 9.1f; z < 35f; z += 0.41f)
            {
                float here = Height(field, x, z);
                Assert.Less(Mathf.Abs(Height(field, x + 0.01f, z) - here), 0.02f, $"step in x at {x},{z}");
                Assert.Less(Mathf.Abs(Height(field, x, z + 0.01f) - here), 0.02f, $"step in z at {x},{z}");
            }
    }

    [Test]
    public void TheStoredGridLoadsBackExactly()
    {
        var field = NewField(0f, 0f, 3f, 9, 7, (x, z) => x * 0.21f - z * 0.13f + Mathf.Cos(x) * 3f);
        var bytes = (byte[])FieldType.GetMethod("ToBytes").Invoke(field, null);
        var loaded = FieldType.GetMethod("FromBytes").Invoke(null, new object[] { bytes });
        Assert.IsNotNull(loaded);
        CollectionAssert.AreEqual(Get<float[]>(field, "h"), Get<float[]>(loaded, "h"));
        Assert.IsNull(FieldType.GetMethod("FromBytes").Invoke(null, new object[] { new byte[40] }), "garbage is refused, not read");
    }

    [Test]
    public void ASlabLaidOnBumpyGroundTouchesItEverywhere()
    {
        var field = NewField(0f, 0f, 3f, 14, 14, (x, z) => Mathf.Sin(x * 0.5f) * 1.5f + Mathf.Cos(z * 0.4f));
        // one flat 20 x 16 m quad, 2 cm over the old ground
        var vertices = new List<Vector3> { new Vector3(5f, 0.02f, 4f), new Vector3(5f, 0.02f, 20f), new Vector3(25f, 0.02f, 20f), new Vector3(25f, 0.02f, 4f) };
        var normals = new List<Vector3> { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
        var triangles = new List<IList<int>> { new List<int> { 0, 1, 2, 0, 2, 3 } };
        var draped = MeshType.GetMethod("Drape").Invoke(null, new object[] { field, vertices, normals, null, triangles, Matrix4x4.identity, Matrix4x4.identity });
        var v = Get<List<Vector3>>(draped, "vertices"); var t = Get<List<List<int>>>(draped, "triangles")[0];
        Assert.Greater(t.Count, 6, "the quad was cut along the terrain's facets");
        float area = 0f;
        for (int i = 0; i < t.Count; i += 3)
        {
            Vector3 a = v[t[i]], b = v[t[i + 1]], c = v[t[i + 2]];
            foreach (var p in new[] { a, b, c, (a + b + c) / 3f, (a + b) * 0.5f })
                Assert.AreEqual(Height(field, p.x, p.z) + 0.02f, p.y, 2e-3f, "a piece bridges a crease instead of lying on the ground");
            area += Mathf.Abs((b.x - a.x) * (c.z - a.z) - (c.x - a.x) * (b.z - a.z)) * 0.5f;
            Assert.Greater(Vector3.Cross(b - a, c - a).y, 0f, "still faces up");
        }
        Assert.AreEqual(20f * 16f, area, 0.01f, "nothing lost, nothing doubled");
    }

    static object Layout(float strength)
    {
        var input = DesignType.GetMethod("FromLayout").Invoke(null, null);
        input.GetType().GetField("strength").SetValue(input, strength);
        return input;
    }
    static object Build(object input) => DesignType.GetMethod("Build").Invoke(null, new[] { input });

    [Test]
    public void TheTownsLandIsLevelWhereItMustBe()
    {
        var input = Layout(1f);
        // a pad on the side of the Hills' dome
        var padType = DesignType.GetNestedType("Pad"); var pad = Activator.CreateInstance(padType);
        padType.GetField("centre").SetValue(pad, new Vector2(150f, 660f)); padType.GetField("half").SetValue(pad, new Vector2(7f, 5f));
        padType.GetField("feather").SetValue(pad, 5f);
        ((IList)input.GetType().GetField("pads").GetValue(input)).Add(pad);
        var result = Build(input); var field = Get<object>(result, "field");

        Assert.AreEqual(0f, Height(field, 91f, 150f), 0.01f, "Depot Row stays where it was");
        Assert.AreEqual(0f, Height(field, 124f, 100f), 0.01f, "and so does the depot road");
        Assert.Greater(Height(field, 235f, 690f), 6f, "the Hills are hills");
        Assert.Greater(Height(field, 700f, 800f), 12f, "the Bluffs are high");
        Assert.Less(Height(field, 312f, 250f), Height(field, 312f, 120f) - 1f, "Acres falls towards the river");

        // a street is level from kerb to kerb, however the hillside under it tilted
        Assert.AreEqual(Height(field, 312f, 120f), Height(field, 316f, 120f), 0.03f, "cross-fall on Park Street (runs north)");
        Assert.AreEqual(Height(field, 312f, 120f), Height(field, 308f, 120f), 0.03f, "cross-fall on Park Street, other kerb");
        Assert.AreEqual(Height(field, 150f, 469f), Height(field, 150f, 473f), 0.03f, "cross-fall on the shopping street (runs east)");
        float lo = float.MaxValue, hi = float.MinValue;
        for (float x = 144f; x <= 156f; x += 1.5f) for (float z = 656f; z <= 664f; z += 1.5f) { float y = Height(field, x, z); lo = Mathf.Min(lo, y); hi = Mathf.Max(hi, y); }
        Assert.Less(hi - lo, 0.02f, "a pad is level");

        foreach (var road in (IEnumerable)result.GetType().GetField("roads").GetValue(result))
            Assert.Less(Get<float>(road, "grade"), 0.18f, Get<string>(road, "id") + " is too steep to be a street");
    }

    [Test]
    public void SwitchedOffTheTownIsTheOldFlatTown()
    {
        var field = Get<object>(Build(Layout(0f)), "field");
        foreach (var at in new[] { new Vector2(235f, 690f), new Vector2(700f, 800f), new Vector2(312f, 120f), new Vector2(91f, 150f) })
            Assert.AreEqual(0f, Height(field, at.x, at.y), 1e-4f, $"at {at}");
    }

    [Test]
    public void BetweenTwoWaypointsAWalkerStaysOnTheGround()
    {
        var field = NewField(0f, 0f, 3f, 20, 20, (x, z) => Mathf.Sin(x * 0.2f) * 4f);
        ReliefType.GetMethod("Use").Invoke(null, new[] { field });
        Vector3 a = new Vector3(6f, Height(field, 6f, 30f) + 0.15f, 30f), b = new Vector3(48f, Height(field, 48f, 30f) + 0.15f, 30f);
        var between = ReliefType.GetMethod("Between");
        for (float u = 0f; u <= 1f; u += 0.1f)
        {
            var p = (Vector3)between.Invoke(null, new object[] { a, b, u });
            Assert.AreEqual(Height(field, p.x, p.z) + 0.15f, p.y, 1e-3f, $"u = {u}");
        }
        var indoors = (Vector3)between.Invoke(null, new object[] { new Vector3(6f, -20f, 30f), new Vector3(48f, -20f, 30f), 0.5f });
        Assert.AreEqual(-20f, indoors.y, 1e-4f, "the depot interiors under the map are left alone");
        Assert.AreEqual(0f, (float)ReliefType.GetMethod("Height", new[] { typeof(float), typeof(float) }).Invoke(null, new object[] { 30f, -400f }), 1e-6f, "and so is everything outside the grid");
    }

    [Test]
    public void TheTruckFeelsAHillButIsNotBeatenByIt()
    {
        float Cancel(bool neutral) => (float)GradeType.GetMethod("Cancel").Invoke(null, new object[] { neutral });
        float Over(float speed, float max) => (float)GradeType.GetMethod("OverspeedDecel").Invoke(null, new object[] { speed, max });
        Assert.AreEqual(1f, Cancel(true), "stick centred: it coasts to a stop and stays, as on the flat");
        Assert.That(Cancel(false), Is.GreaterThan(0.3f).And.LessThan(1f), "driving: the hill still counts, only less");
        Assert.AreEqual(0f, Over(10f, 10f)); Assert.AreEqual(0f, Over(-10f, 10f));
        Assert.Greater(Over(12f, 10f), 0f); Assert.Greater(Over(-12f, 10f), 0f, "backwards too");
        Assert.LessOrEqual(Over(60f, 10f), (float)GradeType.GetField("OverspeedMax").GetValue(null));
        var pull = GradeType.GetMethod("SlopePull");
        Assert.AreEqual(0f, ((Vector3)pull.Invoke(null, new object[] { Vector3.down * 9.81f, Vector3.up })).magnitude, 1e-5f);
        var tenPercent = new Vector3(-0.1f, 1f, 0f).normalized;
        var along = (Vector3)pull.Invoke(null, new object[] { Vector3.down * 9.81f, tenPercent });
        Assert.AreEqual(9.81f * Mathf.Sin(Mathf.Atan(0.1f)), along.magnitude, 1e-3f);
        Assert.Less(along.x, 0f, "and it pulls downhill");
    }
}
