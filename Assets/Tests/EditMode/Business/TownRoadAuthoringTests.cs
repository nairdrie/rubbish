using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

/// <summary>Pure map-editor road contracts; tests never change scenes, JSON assets or player saves.</summary>
public class TownRoadAuthoringTests
{
    static Type R(string n) => Type.GetType(n + ", Assembly-CSharp", true);
    static Type Ops => Type.GetType("TownRoadDraftOperations, Assembly-CSharp-Editor", true);
    static object Call(string method, params object[] args) => Ops.GetMethod(method, BindingFlags.Public | BindingFlags.Static).Invoke(null, args);
    static T Get<T>(object target, string name) => (T)target.GetType().GetField(name).GetValue(target);
    static void Set(object target, string name, object value) => target.GetType().GetField(name).SetValue(target, value);
    static Array Specs(string type, params string[] json)
    {
        Type t = R("CoastalCityLayout+" + type); var values = Array.CreateInstance(t, json.Length);
        for (int i = 0; i < json.Length; i++) values.SetValue(JsonUtility.FromJson(json[i], t), i);
        return values;
    }
    static object Blueprint()
    {
        var source = Resources.Load<TextAsset>("City/CoastalCity"); Assert.NotNull(source);
        var b = JsonUtility.FromJson(source.text, R("CoastalCityLayout+Blueprint"));
        Set(b, "scale", 1f); Set(b, "bounds", new[] { 0f, 0f, 200f, 200f });
        Set(b, "nodes", Specs("NodeSpec", "{\"id\":\"a\",\"x\":20,\"z\":100}", "{\"id\":\"b\",\"x\":180,\"z\":100}",
            "{\"id\":\"river-a\",\"x\":0,\"z\":175}", "{\"id\":\"river-b\",\"x\":200,\"z\":175}",
            "{\"id\":\"coast-a\",\"x\":195,\"z\":0}", "{\"id\":\"coast-b\",\"x\":195,\"z\":200}",
            "{\"id\":\"walk-a\",\"x\":0,\"z\":165}", "{\"id\":\"walk-b\",\"x\":200,\"z\":165}"));
        Set(b, "roads", Specs("RoadSpec", "{\"id\":\"main\",\"name\":\"Main Street\",\"districtId\":\"mildly_pleasant\",\"nodes\":[\"a\",\"b\"],\"halfWidth\":5}"));
        Set(b, "gates", Specs("GateSpec")); Set(b, "lotStrips", Specs("LotStrip"));
        Set(b, "collectionSites", Specs("CollectionSite")); Set(b, "infillSites", Specs("InfillSite"));
        Set(Get<object>(b, "depot"), "roadId", "main");
        var terrain = Get<object>(b, "terrain");
        Set(terrain, "river", new[] { "river-a", "river-b" }); Set(terrain, "coast", new[] { "coast-a", "coast-b" }); Set(terrain, "riverWalk", new[] { "walk-a", "walk-b" });
        return b;
    }
    static object Road(string id = "branch", bool curved = false) => JsonUtility.FromJson(
        "{\"id\":\"" + id + "\",\"name\":\"New Street\",\"districtId\":\"mildly_pleasant\",\"halfWidth\":5,\"curve\":" + (curved ? "true" : "false") + "}", R("CoastalCityLayout+RoadSpec"));
    static object Add(object b, Vector3[] points, bool curved = false) => Call("AddRoad", b, Road("branch", curved), points, 7f);
    static object FindRoad(object b, string id) => Get<Array>(b, "roads").Cast<object>().Single(r => Get<string>(r, "id") == id);
    static object FindNode(object b, string id) => Get<Array>(b, "nodes").Cast<object>().Single(r => Get<string>(r, "id") == id);
    static string[] Nodes(object b, string road) => Get<string[]>(FindRoad(b, road), "nodes");
    static void Clean(object b) => Assert.IsEmpty(((IEnumerable)Call("Validate", b)).Cast<string>().ToArray());

    [Test]
    public void FixtureUsesTheSameSixDistrictTopologyValidationAsPublishing() => Clean(Blueprint());

    [Test]
    public void MovingAJunctionIsAPureCopyAndPreservesItsIdentity()
    {
        var original = Blueprint();
        var changed = Call("MoveJunction", original, "a", new Vector3(25, 12, 110));
        Assert.AreEqual(20f, Get<float>(FindNode(original, "a"), "x"));
        Assert.AreEqual(25f, Get<float>(FindNode(changed, "a"), "x"));
        Assert.AreEqual(110f, Get<float>(FindNode(changed, "a"), "z"));
        CollectionAssert.AreEqual(new[] { "a", "b" }, Nodes(changed, "main")); Clean(changed);
    }

    [Test]
    public void TerrainNodesCannotBeDraggedByTheRoadTool()
    {
        var e = Assert.Throws<TargetInvocationException>(() => Call("MoveJunction", Blueprint(), "river-a", new Vector3(0, 0, 150)));
        StringAssert.Contains("terrain", e.InnerException.Message);
    }

    [Test]
    public void ASnappedEndpointReusesExistingJunctionRatherThanMakingALookalike()
    {
        var original = Blueprint();
        var changed = Add(original, new[] { new Vector3(22, 0, 102), new Vector3(30, 0, 140) });
        Assert.AreEqual("a", Nodes(changed, "branch")[0]);
        Assert.AreEqual(8, Get<Array>(original, "nodes").Length, "Original draft must not mutate.");
        Assert.AreEqual(9, Get<Array>(changed, "nodes").Length); Clean(changed);
    }

    [Test]
    public void EndpointOnRoadInsertsOneSharedJunctionWithoutRenamingRoad()
    {
        var changed = Add(Blueprint(), new[] { new Vector3(95, 0, 102), new Vector3(95, 0, 140) });
        var main = Nodes(changed, "main"); var branch = Nodes(changed, "branch");
        Assert.AreEqual(3, main.Length); Assert.AreEqual(main[1], branch[0]);
        Assert.AreEqual("a", main[0]); Assert.AreEqual("b", main[2]);
        Assert.AreEqual(100f, Get<float>(FindNode(changed, main[1]), "z")); Clean(changed);
    }

    [Test]
    public void CrossingStreetAutomaticallySplitsBothRoadsIntoConnectedGraphNodes()
    {
        var changed = Add(Blueprint(), new[] { new Vector3(100, 0, 40), new Vector3(100, 0, 160) });
        var main = Nodes(changed, "main"); var branch = Nodes(changed, "branch");
        Assert.AreEqual(3, main.Length); Assert.AreEqual(3, branch.Length);
        Assert.AreEqual(main[1], branch[1]);
        Assert.AreEqual(100f, Get<float>(FindNode(changed, main[1]), "x"));
        Assert.AreEqual(100f, Get<float>(FindNode(changed, main[1]), "z")); Clean(changed);
    }

    [Test]
    public void CurvedRoadCrossingAlsoCreatesSharedJunction()
    {
        var changed = Add(Blueprint(), new[] { new Vector3(80, 0, 30), new Vector3(120, 0, 130), new Vector3(150, 0, 150) }, true);
        Assert.GreaterOrEqual(Nodes(changed, "main").Length, 3);
        Assert.IsTrue(Nodes(changed, "main").Intersect(Nodes(changed, "branch")).Any()); Clean(changed);
    }

    [Test]
    public void DisconnectedStreetIsRejectedWithoutTouchingTheOriginalDraft()
    {
        var original = Blueprint(); var before = JsonUtility.ToJson(original);
        var e = Assert.Throws<TargetInvocationException>(() => Add(original, new[] { new Vector3(10, 0, 10), new Vector3(40, 0, 30) }));
        StringAssert.Contains("disconnected", e.InnerException.Message);
        Assert.AreEqual(before, JsonUtility.ToJson(original));
    }

    [Test]
    public void DuplicateRoadIdentityIsRejected()
    {
        var e = Assert.Throws<TargetInvocationException>(() => Call("AddRoad", Blueprint(), Road("main"), new[] { new Vector3(20, 0, 100), new Vector3(20, 0, 140) }, 7f));
        StringAssert.Contains("unique", e.InnerException.Message);
    }

    [Test]
    public void RoadCannotLeaveAuthoredTerrainBounds()
    {
        var e = Assert.Throws<TargetInvocationException>(() => Add(Blueprint(), new[] { new Vector3(20, 0, 100), new Vector3(-20, 0, 140) }));
        StringAssert.Contains("boundary", e.InnerException.Message);
    }

    [Test]
    public void MultipleClicksOnSameJunctionCannotCreateZeroLengthRoad()
    {
        var e = Assert.Throws<TargetInvocationException>(() => Add(Blueprint(), new[] { new Vector3(20, 0, 100), new Vector3(21, 0, 100) }));
        StringAssert.Contains("one junction", e.InnerException.Message);
    }

    [Test]
    public void MovingSharedJunctionMovesEveryIncidentRoadUsingOneStableNode()
    {
        var added = Add(Blueprint(), new[] { new Vector3(95, 0, 100), new Vector3(95, 0, 140) });
        string shared = Nodes(added, "main")[1];
        var moved = Call("MoveJunction", added, shared, new Vector3(100, 0, 110));
        Assert.AreEqual(shared, Nodes(moved, "branch")[0]);
        Assert.AreEqual(shared, Nodes(moved, "main")[1]);
        Assert.AreEqual(110f, Get<float>(FindNode(moved, shared), "z")); Clean(moved);
    }

    [Test]
    public void SplittingExistingCurvedRoadPreservesLengthAndEveryNormalizedHouseAnchor()
    {
        var b = Blueprint(); var oldNodes = Get<Array>(b, "nodes");
        var expanded = Array.CreateInstance(oldNodes.GetType().GetElementType(), oldNodes.Length + 1);
        Array.Copy(oldNodes, expanded, oldNodes.Length);
        expanded.SetValue(Specs("NodeSpec", "{\"id\":\"bend\",\"x\":100,\"z\":125}").GetValue(0), oldNodes.Length);
        Set(b, "nodes", expanded);
        var main = FindRoad(b, "main"); Set(main, "nodes", new[] { "a", "bend", "b" }); Set(main, "curve", true);
        object Resolve(object bp) => R("CoastalCityLayout").GetMethod("ResolveRoad").Invoke(null, new[] { bp, "main" });
        Pose At(object road, float u) => (Pose)road.GetType().GetMethod("AtNormalized").Invoke(road, new object[] { u });
        var before = Resolve(b); var splitAt = At(before, .3f).position;
        var changed = Add(b, new[] { splitAt, splitAt + new Vector3(0, 0, 30) });
        var after = Resolve(changed);
        Assert.IsFalse(Get<bool>(FindRoad(changed, "main"), "curve"), "The sampled original curve becomes an explicit polyline so insertion cannot reshape it.");
        Assert.That((float)after.GetType().GetProperty("Length").GetValue(after), Is.EqualTo((float)before.GetType().GetProperty("Length").GetValue(before)).Within(.001f));
        for (int i = 0; i <= 30; i++)
        {
            var originalPose = At(before, i / 30f); var updatedPose = At(after, i / 30f);
            Assert.That(Vector3.Distance(originalPose.position, updatedPose.position), Is.LessThan(.001f), "An existing normalized-u house anchor moved at " + i);
            Assert.That(Quaternion.Angle(originalPose.rotation, updatedPose.rotation), Is.LessThan(.05f), "House facing changed at " + i);
        }
        Clean(changed);
    }

    [Test]
    public void DrawingOverExistingAsphaltDoesNotCreateADuplicateStreet()
    {
        var e = Assert.Throws<TargetInvocationException>(() => Add(Blueprint(), new[] { new Vector3(40, 0, 100), new Vector3(100, 0, 100) }));
        StringAssert.Contains("overlaps", e.InnerException.Message);
    }

    [Test]
    public void CrossDistrictShortcutCannotSilentlyBypassPermitGates()
    {
        var road = Road(); Set(road, "districtId", "minivan_meadows");
        var e = Assert.Throws<TargetInvocationException>(() => Call("AddRoad", Blueprint(), road, new[] { new Vector3(95, 0, 100), new Vector3(95, 0, 140) }, 7f));
        StringAssert.Contains("permit gates", e.InnerException.Message);
    }

    [Test]
    public void SubMillimetreAccumulationNoiseDoesNotFlipAnExactCornerAnchor()
    {
        var road = Activator.CreateInstance(R("CoastalCityLayout+RoadDef"));
        Set(road, "points", new[] { Vector3.zero, new Vector3(10, 0, 0), new Vector3(10, 0, 10) });
        Pose At(float metres) => (Pose)road.GetType().GetMethod("At").Invoke(road, new object[] { metres });
        var atCorner = At(10f); var noisyCorner = At(10.00003f); var beyondCorner = At(10.001f);
        Assert.That(Quaternion.Angle(atCorner.rotation, noisyCorner.rotation), Is.LessThan(.001f));
        Assert.That(Vector3.Distance(atCorner.position, noisyCorner.position), Is.LessThan(.0001f));
        Assert.That(Quaternion.Angle(atCorner.rotation, beyondCorner.rotation), Is.GreaterThan(89f), "Ordinary positions after the corner still use the outgoing segment.");
    }

    static ScriptableObject Draft() => ScriptableObject.CreateInstance(Type.GetType("TownRoadDraft, Assembly-CSharp-Editor", true));
    static object GatedBlueprint()
    {
        var b = Blueprint();
        Set(b, "gates", Specs("GateSpec", "{\"id\":\"main-permit\",\"roadId\":\"main\",\"districtId\":\"minivan_meadows\",\"u\":0.5,\"forward\":1}"));
        return b;
    }

    [Test]
    public void UnfinishedStrokePreventsPublishAndItsPointsArePreserved()
    {
        var draft = Draft();
        try
        {
            var points = Get<System.Collections.Generic.List<Vector3>>(draft, "points"); points.Add(new Vector3(20, 0, 30));
            var error = Assert.Throws<TargetInvocationException>(() => Call("RequireFinishedStroke", draft));
            StringAssert.Contains("Nothing was written", error.InnerException.Message);
            Assert.AreEqual(1, points.Count);
            points.Clear(); Assert.DoesNotThrow(() => Call("RequireFinishedStroke", draft));
        }
        finally { UnityEngine.Object.DestroyImmediate(draft); }
    }

    [Test]
    public void UnfinishedStrokeRecoveryIncludesDistrictWidthAndAllStreetSettings()
    {
        var before = Draft(); var recovered = Draft();
        try
        {
            Set(before, "streetName", "Recovered Hills Lane"); Set(before, "streetDistrict", 3);
            Set(before, "streetWidth", 12f); Set(before, "streetCurve", true);
            Set(before, "streetService", true); Set(before, "streetBridge", true);
            Get<System.Collections.Generic.List<Vector3>>(before, "points").Add(new Vector3(70, 0, 140));
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(before), recovered);
            Assert.AreEqual("Recovered Hills Lane", Get<string>(recovered, "streetName"));
            Assert.AreEqual(3, Get<int>(recovered, "streetDistrict")); Assert.AreEqual(12f, Get<float>(recovered, "streetWidth"));
            Assert.IsTrue(Get<bool>(recovered, "streetCurve")); Assert.IsTrue(Get<bool>(recovered, "streetService")); Assert.IsTrue(Get<bool>(recovered, "streetBridge"));
            Assert.AreEqual(new Vector3(70, 0, 140), Get<System.Collections.Generic.List<Vector3>>(recovered, "points").Single());
        }
        finally { UnityEngine.Object.DestroyImmediate(before); UnityEngine.Object.DestroyImmediate(recovered); }
    }

    [Test]
    public void PointsOnlyDraftCountsAsUnpublishedWorkForReloadConfirmationAndSaveRouting()
    {
        var draft = Draft();
        try
        {
            Set(draft, "json", "same"); Set(draft, "baseline", "same");
            Assert.IsFalse((bool)Call("HasDraftChanges", draft));
            Get<System.Collections.Generic.List<Vector3>>(draft, "points").Add(Vector3.one);
            Assert.IsTrue((bool)Call("HasDraftChanges", draft));
        }
        finally { UnityEngine.Object.DestroyImmediate(draft); }
    }

    [Test]
    public void SameDistrictLabelsCannotHideADetourAroundAGatedRoad()
    {
        var error = Assert.Throws<TargetInvocationException>(() => Add(GatedBlueprint(), new[] {
            new Vector3(60, 0, 100), new Vector3(60, 0, 130), new Vector3(140, 0, 130), new Vector3(140, 0, 100) }));
        StringAssert.Contains("opposite sides of a permit gate", error.InnerException.Message);
    }

    [Test]
    public void JoiningDifferentApproachesCannotBypassTheSameDistrictsGates()
    {
        var b = GatedBlueprint();
        void Append(string field, Array extra)
        {
            var current = Get<Array>(b, field); var combined = Array.CreateInstance(current.GetType().GetElementType(), current.Length + extra.Length);
            Array.Copy(current, combined, current.Length); Array.Copy(extra, 0, combined, current.Length, extra.Length); Set(b, field, combined);
        }
        Append("nodes", Specs("NodeSpec", "{\"id\":\"c\",\"x\":20,\"z\":140}", "{\"id\":\"d\",\"x\":180,\"z\":140}"));
        Append("roads", Specs("RoadSpec", "{\"id\":\"parallel\",\"name\":\"Parallel Road\",\"districtId\":\"mildly_pleasant\",\"nodes\":[\"c\",\"d\"],\"halfWidth\":5}",
            "{\"id\":\"link\",\"name\":\"Existing Link\",\"districtId\":\"mildly_pleasant\",\"nodes\":[\"a\",\"c\"],\"halfWidth\":5}"));
        Append("gates", Specs("GateSpec", "{\"id\":\"parallel-permit\",\"roadId\":\"parallel\",\"districtId\":\"minivan_meadows\",\"u\":0.5,\"forward\":1}"));
        Clean(b);
        var error = Assert.Throws<TargetInvocationException>(() => Add(b, new[] { new Vector3(60, 0, 100), new Vector3(70, 0, 115), new Vector3(140, 0, 140) }));
        StringAssert.Contains("opposite sides of a permit gate", error.InnerException.Message);
    }

    [Test]
    public void NewJunctionsKeepClearOfThePhysicalPermitGate()
    {
        var error = Assert.Throws<TargetInvocationException>(() => Add(GatedBlueprint(), new[] { new Vector3(102, 0, 100), new Vector3(102, 0, 140) }));
        StringAssert.Contains("at least 8 m clear", error.InnerException.Message);
    }

    [TestCase(60f)]
    [TestCase(140f)]
    public void AnOrdinaryBranchOnEitherSideOfAGateRemainsAllowed(float x)
    {
        var changed = Add(GatedBlueprint(), new[] { new Vector3(x, 0, 100), new Vector3(x, 0, 140) });
        Clean(changed); Assert.AreEqual(2, Get<Array>(changed, "roads").Length);
    }

    [Test]
    public void SequentialDeadEndsCannotBeJoinedToBypassAnExistingGate()
    {
        var first = Add(GatedBlueprint(), new[] { new Vector3(140, 0, 100), new Vector3(140, 0, 140) });
        Clean(first);
        var error = Assert.Throws<TargetInvocationException>(() => Call("AddRoad", first, Road("second-branch"),
            new[] { new Vector3(60, 0, 100), new Vector3(60, 0, 140), new Vector3(140, 0, 140) }, 7f));
        StringAssert.Contains("bypass existing permit gates", error.InnerException.Message);
        Assert.AreEqual(2, Get<Array>(first, "roads").Length, "Failed second stroke cannot mutate the valid first draft.");
    }
}
