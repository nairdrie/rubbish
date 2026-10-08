using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Residential recipes preserve known households and district-specific layout contracts.</summary>
public class CoastalResidentialIntegrationTests
{
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static Type Editor(string name) => Type.GetType(name + ", Assembly-CSharp-Editor", true);
    static T Field<T>(object value, string name) => (T)value.GetType().GetField(name).GetValue(value);
    static void Set(object value, string name, object field) => value.GetType().GetField(name).SetValue(value, field);
    readonly List<GameObject> fixtures = new List<GameObject>();

    [TearDown]
    public void Cleanup()
    {
        foreach (var fixture in fixtures) if (fixture != null) Object.DestroyImmediate(fixture);
        fixtures.Clear();
    }

    object Stop(string id, string district = "mildly_pleasant")
    {
        var go = new GameObject("Residential reference fixture");
        go.SetActive(false); fixtures.Add(go);
        go.transform.position = new Vector3(9876, 40, 8765);
        var stop = go.AddComponent(Runtime("PickupStop"));
        Set(stop, "stopId", id); Set(stop, "districtId", district);
        return stop;
    }

    static IDictionary Placements(string id = "known-house", string district = "mildly_pleasant")
    {
        var placementType = Editor("CoastalResidentialLayout").GetNestedType("Placement");
        var result = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), placementType));
        var placement = Activator.CreateInstance(placementType);
        Set(placement, "districtId", district); Set(placement, "roadId", "elm");
        Set(placement, "u", .5f); Set(placement, "side", -1);
        result.Add(id, placement); return result;
    }

    static void Validate(IDictionary placements, params object[] stops)
    {
        var typedStops = Array.CreateInstance(Runtime("PickupStop"), stops.Length);
        for (int i = 0; i < stops.Length; i++) typedStops.SetValue(stops[i], i);
        Editor("NeighborhoodBuilder").GetMethod("ValidateResidentialHouseholds", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] { placements, typedStops });
    }

    static void Reject(IDictionary placements, params object[] stops)
    {
        var error = Assert.Throws<TargetInvocationException>(() => Validate(placements, stops));
        Assert.IsInstanceOf<InvalidOperationException>(error.InnerException);
        StringAssert.Contains("missing, duplicate or wrong-district household known-house", error.InnerException.Message);
    }

    [Test]
    public void KnownHouseholdIsAcceptedWithoutMovingItAndUnlistedPropertiesAreAllowed()
    {
        var stop = Stop("known-house"); var before = ((Component)stop).transform.position;
        Validate(Placements(), stop, Stop("unlisted-house", "minivan_meadows"));
        Assert.AreEqual(before, ((Component)stop).transform.position);
        Assert.AreEqual("known-house", Field<string>(stop, "stopId"));
    }

    [Test]
    public void MissingRecipeHouseholdIsRejectedRatherThanSilentlyIgnored() => Reject(Placements(), Stop("another-house"));

    [Test]
    public void DuplicateRecipeHouseholdIsRejectedBeforeEitherCopyMoves() => Reject(Placements(), Stop("known-house"), Stop("known-house"));

    [Test]
    public void RecipeCannotMoveAHouseholdIntoAnotherDistrictWithoutChangingItsRules() =>
        Reject(Placements(), Stop("known-house", "minivan_meadows"));

    static IDictionary LiveRecipe()
    {
        var recipeText = Resources.Load<TextAsset>("City/ResidentialBlocks");
        var blueprintText = Resources.Load<TextAsset>("City/CoastalCity");
        Assert.NotNull(recipeText); Assert.NotNull(blueprintText);
        var recipe = JsonUtility.FromJson(recipeText.text, Editor("CoastalResidentialLayout").GetNestedType("Recipe"));
        var blueprint = JsonUtility.FromJson(blueprintText.text, Runtime("CoastalCityLayout+Blueprint"));
        return (IDictionary)Editor("CoastalResidentialLayout").GetMethod("Resolve").Invoke(null, new[] { recipe, blueprint });
    }

    [Test]
    public void PublishedAcresRecipePreservesAllThirtySevenOriginalHouseholdIdentities()
    {
        var expected = new[] {
            "birch-18", "birch-19", "birch-27", "birch-28", "birch-36", "birch-39", "birch-46", "birch-47", "birch-56", "birch-57", "birch-8", "birch-9",
            "depot-9", "elm-13", "elm-22", "elm-23", "elm-3", "elm-33", "elm-43", "maple-18", "maple-19", "maple-26", "maple-29", "maple-38", "maple-39",
            "maple-46", "maple-49", "maple-56", "maple-59", "maple-8", "maple-9", "oak-12", "oak-2", "oak-22", "oak-23", "oak-32", "oak-42"
        };
        var recipe = LiveRecipe();
        var actual = recipe.Keys.Cast<string>().Where(id => Field<string>(recipe[id], "districtId") == "mildly_pleasant").ToArray();
        Assert.AreEqual(37, actual.Length);
        CollectionAssert.AreEquivalent(expected, actual);
    }

    [Test]
    public void DepotEdgeIsSixBackyardsWithFrontDoorsOnElmAndNoDepotFacingCurbStops()
    {
        var actual = LiveRecipe();
        var acres = actual.Keys.Cast<string>().Where(id => Field<string>(actual[id], "districtId") == "mildly_pleasant").ToArray();
        var elmWest = acres.Where(id => Field<string>(actual[id], "roadId") == "elm" && Field<int>(actual[id], "side") == -1).ToArray();
        CollectionAssert.AreEquivalent(new[] { "birch-18", "birch-19", "birch-27", "birch-28", "birch-36", "birch-39" }, elmWest);
        Assert.IsFalse(acres.Any(id => Field<string>(actual[id], "roadId") == "depot"));
    }
}
