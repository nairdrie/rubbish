using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Pure block recipes keep household identities stable without changing real scenes or recipes.</summary>
public class CoastalResidentialLayoutTests
{
    static Type Ops => Type.GetType("CoastalResidentialLayout, Assembly-CSharp-Editor", true);
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static T Get<T>(object target, string name) => (T)target.GetType().GetField(name).GetValue(target);
    static void Set(object target, string name, object value) => target.GetType().GetField(name).SetValue(target, value);
    static object Call(string method, params object[] args) => Ops.GetMethod(method, BindingFlags.Public | BindingFlags.Static).Invoke(null, args);
    static object Recipe(string rows = null) => JsonUtility.FromJson("{\"schema\":1,\"rows\":[" + (rows ?? Row()) + "]}", Ops.GetNestedType("Recipe"));
    static string Row(string id = "south", int side = 1, string homes = "\"house-a\",\"house-b\",\"house-c\"", float first = 20, float last = 20) =>
        "{\"id\":\"" + id + "\",\"districtId\":\"mildly_pleasant\",\"roadId\":\"main\",\"fromNode\":\"a\",\"toNode\":\"b\",\"side\":" + side +
        ",\"firstInset\":" + first.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"lastInset\":" + last.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"homes\":[" + homes + "]}";
    static object Blueprint() => JsonUtility.FromJson(
        "{\"scale\":1,\"districts\":[{\"id\":\"mildly_pleasant\"}],\"nodes\":[{\"id\":\"before\",\"x\":-100,\"z\":0},{\"id\":\"a\",\"x\":0,\"z\":0},{\"id\":\"b\",\"x\":100,\"z\":0},{\"id\":\"after\",\"x\":100,\"z\":100}]," +
        "\"roads\":[{\"id\":\"main\",\"districtId\":\"mildly_pleasant\",\"nodes\":[\"before\",\"a\",\"b\",\"after\"],\"halfWidth\":5}]}", Runtime("CoastalCityLayout+Blueprint"));
    static object FirstRow(object recipe) => Get<Array>(recipe, "rows").GetValue(0);
    static IDictionary Resolve(object recipe, object blueprint) => (IDictionary)Call("Resolve", recipe, blueprint);
    static void Reject(object recipe, object blueprint, string detail)
    {
        var exception = Assert.Throws<TargetInvocationException>(() => Resolve(recipe, blueprint));
        Assert.IsInstanceOf<InvalidOperationException>(exception.InnerException);
        StringAssert.Contains(detail, exception.InnerException.Message);
    }

    [Test]
    public void ExactStableIdsReceiveEvenStationsOnTheRequestedBlockWithoutChangingInputs()
    {
        var recipe = Recipe(); var blueprint = Blueprint();
        string originalRecipe = JsonUtility.ToJson(recipe), originalBlueprint = JsonUtility.ToJson(blueprint);
        var result = Resolve(recipe, blueprint);
        CollectionAssert.AreEquivalent(new[] { "house-a", "house-b", "house-c" }, result.Keys);
        Assert.That(Get<float>(result["house-a"], "u"), Is.EqualTo(.4f).Within(.00001f));
        Assert.That(Get<float>(result["house-b"], "u"), Is.EqualTo(.5f).Within(.00001f));
        Assert.That(Get<float>(result["house-c"], "u"), Is.EqualTo(.6f).Within(.00001f));
        Assert.AreEqual("main", Get<string>(result["house-a"], "roadId"));
        Assert.AreEqual("mildly_pleasant", Get<string>(result["house-a"], "districtId"));
        Assert.AreEqual(1, Get<int>(result["house-a"], "side"));
        Assert.AreEqual(originalRecipe, JsonUtility.ToJson(recipe)); Assert.AreEqual(originalBlueprint, JsonUtility.ToJson(blueprint));
    }

    [Test]
    public void SingleHomeUsesTheCentreOfTheAvailableFrontage()
    {
        var result = Resolve(Recipe(Row(homes: "\"only\"", first: 10, last: 30)), Blueprint());
        Assert.That(Get<float>(result["only"], "u"), Is.EqualTo(140f / 300f).Within(.00001f));
    }

    [Test]
    public void ClearancesAreMetresAndDoNotShrinkWithBlueprintScale()
    {
        var blueprint = Blueprint(); Set(blueprint, "scale", .5f);
        var result = Resolve(Recipe(Row(homes: "\"one\",\"two\"", first: 10, last: 10)), blueprint);
        Assert.That(Get<float>(result["one"], "u"), Is.EqualTo(60f / 150f).Within(.00001f));
        Assert.That(Get<float>(result["two"], "u"), Is.EqualTo(90f / 150f).Within(.00001f));
    }

    [Test]
    public void MovingBlockNodesPreservesStopIdsAndFollowsTheNewSegment()
    {
        var blueprint = Blueprint();
        var nodes = Get<Array>(blueprint, "nodes").Cast<object>().ToArray();
        Set(nodes.Single(n => Get<string>(n, "id") == "a"), "x", 10f);
        Set(nodes.Single(n => Get<string>(n, "id") == "b"), "x", 150f);
        var result = Resolve(Recipe(), blueprint);
        var road = Runtime("CoastalCityLayout").GetMethod("ResolveRoad").Invoke(null, new[] { blueprint, "main" });
        var expected = new[] { 30f, 80f, 130f }; var ids = new[] { "house-a", "house-b", "house-c" };
        for (int i = 0; i < ids.Length; i++)
        {
            var pose = (Pose)road.GetType().GetMethod("AtNormalized").Invoke(road, new object[] { Get<float>(result[ids[i]], "u") });
            Assert.That(pose.position.x, Is.EqualTo(expected[i]).Within(.0001f));
            Assert.That(pose.position.z, Is.EqualTo(0).Within(.0001f));
        }
    }

    [Test]
    public void CrowdedRowsFailWithActionablePitchMessage() => Reject(Recipe(Row(first: 35, last: 35)), Blueprint(), "pitch");

    [Test]
    public void EighteenMetrePitchIsAccepted() => Assert.AreEqual(3, Resolve(Recipe(Row(first: 32, last: 32)), Blueprint()).Count);

    [Test]
    public void UnknownNodesFailBeforeAnyPlacement()
    {
        var recipe = Recipe(); Set(FirstRow(recipe), "fromNode", "missing"); Reject(recipe, Blueprint(), "unknown fromNode/toNode");
    }

    [TestCase("b", "a")]
    [TestCase("before", "b")]
    [TestCase("a", "a")]
    public void ReversedSkippedAndRepeatedNodePairsAreRejected(string from, string to)
    {
        var recipe = Recipe(); Set(FirstRow(recipe), "fromNode", from); Set(FirstRow(recipe), "toNode", to);
        Reject(recipe, Blueprint(), "consecutive");
    }

    [Test]
    public void DuplicateHomeIdentityAcrossOppositeCurbsIsRejected() =>
        Reject(Recipe(Row() + "," + Row("north", -1)), Blueprint(), "duplicate/invalid stop");

    [Test]
    public void DuplicateRowIdentityIsRejected() => Reject(Recipe(Row() + "," + Row(side: -1)), Blueprint(), "unique, nonempty ID");

    [Test]
    public void TwoDifferentRowsCannotClaimTheSameCurbFrontage() =>
        Reject(Recipe(Row() + "," + Row("overlap", homes: "\"d\",\"e\",\"f\"")), Blueprint(), "frontage ranges");

    [Test]
    public void OppositeCurbsCanUseTheSameStations() =>
        Assert.AreEqual(6, Resolve(Recipe(Row() + "," + Row("north", -1, "\"d\",\"e\",\"f\"")), Blueprint()).Count);

    [TestCase(0f)]
    [TestCase(-1f)]
    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    public void InvalidClearancesAreRejected(float clearance)
    {
        var recipe = Recipe(); Set(FirstRow(recipe), "firstInset", clearance); Reject(recipe, Blueprint(), "finite positive");
    }

    [Test]
    public void InsetsCannotExceedTheirSegment() => Reject(Recipe(Row(first: 60, last: 60)), Blueprint(), "insets exceed");

    [Test]
    public void CurvedRoadCannotPretendToBeAStraightBlock()
    {
        var blueprint = Blueprint(); Set(Get<Array>(blueprint, "roads").GetValue(0), "curve", true);
        Reject(Recipe(), blueprint, "straight, non-bridge");
    }

    [Test]
    public void DistrictMustMatchItsRoad()
    {
        var recipe = Recipe(); Set(FirstRow(recipe), "districtId", "wrong-district"); Reject(recipe, Blueprint(), "district must exist and match");
    }

    [Test]
    public void UnknownRoadIsRejected()
    {
        var recipe = Recipe(); Set(FirstRow(recipe), "roadId", "missing-road"); Reject(recipe, Blueprint(), "unknown road");
    }

    [Test]
    public void UnsupportedSchemaIsRejected()
    {
        var recipe = Recipe(); Set(recipe, "schema", 2); Reject(recipe, Blueprint(), "schema 1");
    }

    [Test]
    public void EmptyHomeListIsRejected() => Reject(Recipe(Row(homes: "")), Blueprint(), "at least one home");

    [Test]
    public void InvalidSideIsRejected() => Reject(Recipe(Row(side: 0)), Blueprint(), "side must be");

    [Test]
    public void NonFiniteBlueprintNodeIsRejected()
    {
        var blueprint = Blueprint(); Set(Get<Array>(blueprint, "nodes").GetValue(0), "x", float.NaN);
        Reject(Recipe(), blueprint, "non-finite");
    }

    [TestCase(-1, 90f)]
    [TestCase(1, -90f)]
    public void ConfigureSetsCurbRelativeFieldsButPreservesIdentityAndDoesNotMoveTheLot(int side, float yaw)
    {
        var go = new GameObject("Residential configuration fixture");
        try
        {
            go.transform.SetPositionAndRotation(new Vector3(9876, 42, 8765), Quaternion.Euler(0, 34, 0));
            var position = go.transform.position; var rotation = go.transform.rotation;
            var anchor = go.AddComponent(Runtime("CityRoadAnchor")); Set(anchor, "stableId", "existing-save-identity");
            var placement = Resolve(Recipe(Row(side: side)), Blueprint())["house-a"];
            Call("Configure", anchor, placement);
            Assert.AreEqual("existing-save-identity", Get<string>(anchor, "stableId"));
            Assert.AreEqual("main", Get<string>(anchor, "roadId"));
            Assert.That(Get<float>(anchor, "u"), Is.EqualTo(.4f).Within(.00001f));
            Assert.AreEqual(side * .8f, Get<float>(anchor, "lateral"));
            Assert.IsTrue(Get<bool>(anchor, "lateralFromRoadEdge"));
            Assert.AreEqual(yaw, Get<float>(anchor, "yawOffset")); Assert.AreEqual(.15f, Get<float>(anchor, "height"));
            Assert.AreEqual(position, go.transform.position); Assert.AreEqual(rotation, go.transform.rotation);
        }
        finally { Object.DestroyImmediate(go); }
    }
}
