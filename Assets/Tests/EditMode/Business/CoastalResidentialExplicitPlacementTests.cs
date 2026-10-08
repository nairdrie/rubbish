using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

/// <summary>Curved-court attachments share household identity rules with rows without assuming linear parcel pitch.</summary>
public class CoastalResidentialExplicitPlacementTests
{
    static Type Ops => Type.GetType("CoastalResidentialLayout, Assembly-CSharp-Editor", true);
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static T Get<T>(object value, string name) => (T)value.GetType().GetField(name).GetValue(value);
    static void Set(object value, string name, object field) => value.GetType().GetField(name).SetValue(value, field);
    static object Recipe(string placements = null, bool withRow = false) => JsonUtility.FromJson(
        "{\"schema\":1," + (withRow ? "\"rows\":[{\"id\":\"row\",\"districtId\":\"mildly_pleasant\",\"roadId\":\"main\",\"fromNode\":\"a\",\"toNode\":\"b\",\"side\":1,\"firstInset\":20,\"lastInset\":20,\"homes\":[\"row-house\"]}]," : "") +
        "\"placements\":[" + (placements ?? House()) + "]}", Ops.GetNestedType("Recipe"));
    static string House(string id = "court-house") => "{\"stopId\":\"" + id + "\",\"districtId\":\"minivan_meadows\",\"roadId\":\"court\",\"u\":0.4,\"side\":-1}";
    static object Blueprint() => JsonUtility.FromJson(
        "{\"scale\":1,\"districts\":[{\"id\":\"mildly_pleasant\"},{\"id\":\"minivan_meadows\"}]," +
        "\"nodes\":[{\"id\":\"a\",\"x\":0,\"z\":0},{\"id\":\"b\",\"x\":100,\"z\":0},{\"id\":\"c\",\"x\":130,\"z\":35},{\"id\":\"d\",\"x\":160,\"z\":0}]," +
        "\"roads\":[{\"id\":\"main\",\"districtId\":\"mildly_pleasant\",\"nodes\":[\"a\",\"b\"],\"halfWidth\":5},{\"id\":\"court\",\"districtId\":\"minivan_meadows\",\"nodes\":[\"b\",\"c\",\"d\"],\"curve\":true,\"halfWidth\":5}]}", Runtime("CoastalCityLayout+Blueprint"));
    static object First(object recipe) => Get<Array>(recipe, "placements").GetValue(0);
    static object Court(object blueprint) => Get<Array>(blueprint, "roads").GetValue(1);
    static IDictionary Resolve(object recipe, object blueprint) => (IDictionary)Ops.GetMethod("Resolve").Invoke(null, new[] { recipe, blueprint });
    static void Reject(object recipe, object blueprint, string message)
    {
        var error = Assert.Throws<TargetInvocationException>(() => Resolve(recipe, blueprint));
        Assert.IsInstanceOf<InvalidOperationException>(error.InnerException);
        StringAssert.Contains(message, error.InnerException.Message);
    }

    [Test]
    public void ExplicitCurvedHomeNeedsNoRowsAndPreservesInputs()
    {
        var recipe = Recipe(); var blueprint = Blueprint();
        string beforeRecipe = JsonUtility.ToJson(recipe), beforeBlueprint = JsonUtility.ToJson(blueprint);
        var result = Resolve(recipe, blueprint);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("court", Get<string>(result["court-house"], "roadId"));
        Assert.AreEqual("minivan_meadows", Get<string>(result["court-house"], "districtId"));
        Assert.AreEqual(.4f, Get<float>(result["court-house"], "u"));
        Assert.AreEqual(-1, Get<int>(result["court-house"], "side"));
        Assert.AreEqual(beforeRecipe, JsonUtility.ToJson(recipe)); Assert.AreEqual(beforeBlueprint, JsonUtility.ToJson(blueprint));
    }

    [Test]
    public void RowsAndExplicitHomesShareOneResolvedDictionary() =>
        CollectionAssert.AreEquivalent(new[] { "row-house", "court-house" }, Resolve(Recipe(withRow: true), Blueprint()).Keys);

    [Test]
    public void EmptyRowsCanAccompanyExplicitHomes()
    {
        var recipe = Recipe(); Set(recipe, "rows", Array.CreateInstance(Ops.GetNestedType("Row"), 0));
        Assert.AreEqual(1, Resolve(recipe, Blueprint()).Count);
    }

    [Test]
    public void ExplicitCurbStationsDoNotPretendThatBulbParcelsHaveLinearPitch()
    {
        var recipe = Recipe(House("first") + "," + House("second"));
        Set(Get<Array>(recipe, "placements").GetValue(1), "u", .44f);
        Assert.AreEqual(2, Resolve(recipe, Blueprint()).Count, "Actual footprint clearance is checked by physical authoring validation.");
    }

    [TestCase(0f)]
    [TestCase(1f)]
    public void FiniteEndpointStationsAreAccepted(float station)
    {
        var recipe = Recipe(); Set(First(recipe), "u", station);
        Assert.AreEqual(station, Get<float>(Resolve(recipe, Blueprint())["court-house"], "u"));
    }

    [TestCase(-.01f)]
    [TestCase(1.01f)]
    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    [TestCase(float.NegativeInfinity)]
    public void InvalidStationsAreRejected(float station)
    {
        var recipe = Recipe(); Set(First(recipe), "u", station); Reject(recipe, Blueprint(), "finite normalized");
    }

    [TestCase(0)]
    [TestCase(2)]
    public void InvalidCurbSideIsRejected(int side)
    {
        var recipe = Recipe(); Set(First(recipe), "side", side); Reject(recipe, Blueprint(), "side must be");
    }

    [TestCase("")]
    [TestCase(" ")]
    [TestCase(" padded")]
    public void InvalidHouseIdentityIsRejected(string id) => Reject(Recipe(House(id)), Blueprint(), "duplicate/invalid stop");

    [Test]
    public void DuplicateExplicitIdentityIsRejected() => Reject(Recipe(House() + "," + House()), Blueprint(), "unique across rows and explicit");

    [Test]
    public void ExplicitHomeCannotDuplicateARowIdentity() => Reject(Recipe(House("row-house"), true), Blueprint(), "unique across rows and explicit");

    [Test]
    public void NullPlacementIsRejected()
    {
        var recipe = Recipe(); Set(recipe, "placements", Array.CreateInstance(Ops.GetNestedType("ExplicitHouse"), 1));
        Reject(recipe, Blueprint(), "must not be null");
    }

    [Test]
    public void EmptyRecipeIsRejected() => Reject(Recipe(""), Blueprint(), "at least one residential row or explicit placement");

    [Test]
    public void UnknownRoadIsRejected()
    {
        var recipe = Recipe(); Set(First(recipe), "roadId", "unknown"); Reject(recipe, Blueprint(), "unknown road");
    }

    [TestCase("unknown")]
    [TestCase("mildly_pleasant")]
    public void UnknownOrMismatchedDistrictIsRejected(string district)
    {
        var recipe = Recipe(); Set(First(recipe), "districtId", district); Reject(recipe, Blueprint(), "district must exist and match");
    }

    [Test]
    public void BridgePlacementIsRejected()
    {
        var blueprint = Blueprint(); Set(Court(blueprint), "bridge", true); Reject(Recipe(), blueprint, "bridge");
    }

    [Test]
    public void ServiceCourtIsAllowed()
    {
        var blueprint = Blueprint(); Set(Court(blueprint), "service", true); Assert.AreEqual(1, Resolve(Recipe(), blueprint).Count);
    }

    [Test]
    public void MalformedCurvedRoadGeometryIsRejectedBeforeScenePlacement()
    {
        var blueprint = Blueprint(); Set(Court(blueprint), "nodes", new[] { "b", "missing" });
        Reject(Recipe(), blueprint, "missing junction");
    }

    [Test]
    public void ExplicitHomeLimitIsBounded()
    {
        var recipe = Recipe();
        Set(recipe, "placements", Array.CreateInstance(Ops.GetNestedType("ExplicitHouse"), 2001));
        Reject(recipe, Blueprint(), "at most 2000");
    }

    [Test]
    public void HomeLimitCountsRowsAndExplicitHomesTogether()
    {
        var recipe = Recipe(withRow: true);
        Set(recipe, "placements", Array.CreateInstance(Ops.GetNestedType("ExplicitHouse"), 2000));
        Reject(recipe, Blueprint(), "at most 2000");
    }
}
