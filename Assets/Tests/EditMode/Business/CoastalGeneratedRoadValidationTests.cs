using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Generated road revisions must be checked even when no house/fixture references that road.</summary>
public class CoastalGeneratedRoadValidationTests
{
    static Type R(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object Get(object o, string name) => o.GetType().GetField(name).GetValue(o);
    static void Set(object o, string name, object v) => o.GetType().GetField(name).SetValue(o, v);
    static object[] Roads => ((IEnumerable)R("CoastalCityLayout").GetProperty("Roads").GetValue(null)).Cast<object>().ToArray();
    readonly List<GameObject> owned = new List<GameObject>();
    GameObject town;

    [SetUp]
    public void Setup()
    {
        town = new GameObject("Generated road validation fixture"); owned.Add(town);
        var marker = new GameObject((string)R("CoastalCityLayout").GetField("MarkerName").GetRawConstantValue());
        marker.transform.SetParent(town.transform, false);
        foreach (var road in Roads) Add((string)Get(road, "id"), (string)road.GetType().GetProperty("Fingerprint").GetValue(road));
    }
    [TearDown]
    public void Cleanup() { foreach (var go in owned) if (go != null) Object.DestroyImmediate(go); owned.Clear(); }

    Component Add(string id, string fingerprint)
    {
        var go = new GameObject("Road " + id); go.transform.SetParent(town.transform, false);
        var marker = go.AddComponent(R("CityGeneratedRoad")); Set(marker, "roadId", id); Set(marker, "fingerprint", fingerprint);
        var roads = R("CoastalCityLayout").GetProperty("Roads").GetValue(null);
        Set(marker, "surfaceFingerprint", R("CityGeneratedRoad").GetMethod("SurfaceFingerprint").Invoke(null, new[] { roads }));
        return marker;
    }
    Component First => town.GetComponentsInChildren(R("CityGeneratedRoad"), true)[0];
    string[] Errors(object expected = null) => ((IEnumerable)R("CoastalCityValidation").GetMethod("ValidateGeneratedRoads")
        .Invoke(null, new[] { (object)town.transform, expected })).Cast<string>().ToArray();

    [Test]
    public void CompleteCurrentInventoryPassesWithoutMovingOrRewritingAnything()
    {
        var markers = town.GetComponentsInChildren(R("CityGeneratedRoad"), true);
        var before = markers.Select(m => ((string)Get(m, "roadId"), (string)Get(m, "fingerprint"), m.transform.position)).ToArray();
        Assert.IsEmpty(Errors());
        CollectionAssert.AreEqual(before, markers.Select(m => ((string)Get(m, "roadId"), (string)Get(m, "fingerprint"), m.transform.position)).ToArray());
    }

    [Test]
    public void MissingRoadIsDetectedEvenWithoutAnyHouseOrAnchor()
    {
        var marker = First; string id = (string)Get(marker, "roadId"); Object.DestroyImmediate(marker.gameObject);
        StringAssert.Contains("Missing generated road '" + id + "'", string.Join("\n", Errors()));
        Assert.IsEmpty(town.GetComponentsInChildren(R("CityRoadAnchor"), true));
    }

    [Test]
    public void DuplicateRoadIncludingAnInactiveGroupIsRejected()
    {
        string id = (string)Get(First, "roadId"), fingerprint = (string)Get(First, "fingerprint");
        Add(id, fingerprint).gameObject.SetActive(false);
        StringAssert.Contains("Duplicate generated road '" + id + "'", string.Join("\n", Errors()));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("deleted-road")]
    public void UnnamedRemovedOrRenamedGeneratedRoadsAreReported(string id)
    {
        Add(id, "irrelevant");
        StringAssert.Contains("Unexpected generated road", string.Join("\n", Errors()));
    }

    [Test]
    public void OutdatedFingerprintIsReportedWithoutSilentlyUpdatingIt()
    {
        var marker = First; string id = (string)Get(marker, "roadId"); Set(marker, "fingerprint", "old revision");
        string errors = string.Join("\n", Errors());
        StringAssert.Contains("Outdated generated road '" + id + "'", errors);
        StringAssert.Contains("Apply Blueprint Changes", errors);
        Assert.AreEqual("old revision", Get(marker, "fingerprint"));
    }

    [Test]
    public void UnappliedNamedJunctionEditMarksExactlyItsIncidentRoadsStaleWithoutLotAnchors()
    {
        var layout = R("CoastalCityLayout");
        var json = Resources.Load<TextAsset>((string)layout.GetField("ResourcePath").GetRawConstantValue());
        var blueprint = JsonUtility.FromJson(json.text, R("CoastalCityLayout+Blueprint"));
        var roadSpecs = ((Array)Get(blueprint, "roads")).Cast<object>().ToArray();
        var node = ((Array)Get(blueprint, "nodes")).Cast<object>().First(n =>
            roadSpecs.Count(r => ((string[])Get(r, "nodes")).Contains((string)Get(n, "id"))) >= 2);
        string junction = (string)Get(node, "id"); Set(node, "x", (float)Get(node, "x") + 8f);
        var expected = Array.CreateInstance(R("CoastalCityLayout+RoadDef"), roadSpecs.Length);
        for (int i = 0; i < roadSpecs.Length; i++)
            expected.SetValue(layout.GetMethod("ResolveRoad").Invoke(null, new[] { blueprint, Get(roadSpecs[i], "id") }), i);
        var actual = Errors(expected);
        var affected = roadSpecs.Where(r => ((string[])Get(r, "nodes")).Contains(junction)).Select(r => (string)Get(r, "id")).ToArray();
        Assert.AreEqual(affected.Length + 1, actual.Length);
        Assert.That(actual, Has.Some.Contains("Outdated connected road surfaces"));
        foreach (string road in affected) Assert.That(actual, Has.Some.Contains("Outdated generated road '" + road + "'"));
        Assert.IsEmpty(Errors(), "A preview must not mutate the loaded blueprint or generated fingerprints.");
    }

    [Test]
    public void LegacySurfaceRevisionRequiresABakeEvenWhenTheRoadLayoutIsUnchanged()
    {
        Set(First, "surfaceFingerprint", "");
        var errors = Errors();
        Assert.AreEqual(1, errors.Length);
        StringAssert.Contains("Outdated connected road surfaces", errors[0]);
        Assert.AreEqual("", Get(First, "surfaceFingerprint"), "Validation must remain read-only.");
    }

    [Test]
    public void FullSceneValidationCallsTheGeneratedRoadRevisionCheck()
    {
        string id = (string)Get(First, "roadId"); Set(First, "fingerprint", "stale");
        var errors = ((IEnumerable)R("CoastalCityValidation").GetMethod("Validate").Invoke(null, new object[] { town.transform, null })).Cast<string>();
        StringAssert.Contains("Outdated generated road '" + id + "'", string.Join("\n", errors));
    }
}
