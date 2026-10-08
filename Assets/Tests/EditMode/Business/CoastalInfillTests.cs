using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public class CoastalInfillTests
{
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static void Set(object value, string field, object next) => value.GetType().GetField(field).SetValue(value, next);
    static object Blueprint()
    {
        var asset = Resources.Load<TextAsset>("City/CoastalCity"); Assert.NotNull(asset);
        return JsonUtility.FromJson(asset.text, Runtime("CoastalCityLayout+Blueprint"));
    }
    static object Site()
    {
        var s = Activator.CreateInstance(Runtime("CoastalCityLayout+InfillSite"));
        Set(s, "id", "test-frontage"); Set(s, "roadId", "home"); Set(s, "type", "storefront");
        Set(s, "u", .5f); Set(s, "side", 1); Set(s, "width", 24f); Set(s, "depth", 16f); Set(s, "setback", .5f);
        return s;
    }
    static IList Errors(params object[] entries)
    {
        var b = Blueprint(); var sites = Array.CreateInstance(Runtime("CoastalCityLayout+InfillSite"), entries.Length);
        for (int i = 0; i < entries.Length; i++) sites.SetValue(entries[i], i);
        Set(b, "infillSites", sites);
        return (IList)Runtime("CoastalCityLayout").GetMethod("ValidateBlueprint", BindingFlags.Public | BindingFlags.Static).Invoke(null, new[] { b });
    }

    [Test] public void DecorativeBlueprintAcceptsValidFootprintWithoutAddingCollectionSite()
    {
        Assert.IsEmpty(Errors(Site()));
    }

    [TestCase("width", float.NaN)] [TestCase("depth", float.PositiveInfinity)]
    [TestCase("setback", -.1f)] [TestCase("u", 1.1f)] [TestCase("width", 0f)]
    public void DecorativeBlueprintRejectsInvalidDimensionsAndOffsets(string field, float value)
    {
        var s = Site(); Set(s, field, value); Assert.IsNotEmpty(Errors(s));
    }

    [Test] public void DecorativeBlueprintRejectsUnknownTypeAndDuplicateIdentity()
    {
        var s = Site(); Set(s, "type", "mystery"); Assert.IsNotEmpty(Errors(s));
        Assert.IsNotEmpty(Errors(Site(), Site()));
    }

    [Test] public void CourtyardNeedsRoomForTwoWingsAndOpenPedestrianAccess()
    {
        var s = Site(); Set(s, "type", "courtyard"); Set(s, "width", 12f);
        Assert.IsNotEmpty(Errors(s));
    }

    [Test] public void GroveAcceptsAUsableWoodlandFootprintButRejectsTinyTreePatches()
    {
        var s = Site(); Set(s, "type", "grove"); Set(s, "width", 36f); Set(s, "depth", 36f);
        Assert.IsEmpty(Errors(s));
        Set(s, "depth", 12f); Assert.IsNotEmpty(Errors(s));
        Set(s, "depth", 36f); Set(s, "width", 12f); Assert.IsNotEmpty(Errors(s));
    }

    [Test] public void InfillDimensionEditsRequireFullStructuralRebuild()
    {
        var b = Blueprint(); var sites = Array.CreateInstance(Runtime("CoastalCityLayout+InfillSite"), 1);
        var s = Site(); sites.SetValue(s, 0); Set(b, "infillSites", sites);
        var method = Runtime("CityBlueprintStamp").GetMethod("StructuralFingerprint", BindingFlags.Public | BindingFlags.Static);
        string before = (string)method.Invoke(null, new[] { b }); Set(s, "width", 32f);
        Assert.AreNotEqual(before, method.Invoke(null, new[] { b }));
    }

    [Test] public void ReservedFootprintDetectsCrossingsContainmentAndNearMissesAfterRotation()
    {
        var go = new GameObject("Infill footprint test");
        try
        {
            var s = go.AddComponent(Runtime("CoastalInfillSite")); Set(s, "width", 20f); Set(s, "depth", 12f);
            go.transform.SetPositionAndRotation(new Vector3(200, 0, 300), Quaternion.Euler(0, 37, 0));
            var method = s.GetType().GetMethod("IntersectsSegment");
            bool Probe(Vector3 a, Vector3 b, float margin = 0) => (bool)method.Invoke(s, new object[] { go.transform.TransformPoint(a), go.transform.TransformPoint(b), margin });
            Assert.True(Probe(new Vector3(-20, 0, 6), new Vector3(20, 0, 6)), "A long road crossing the footprint is detected even when endpoints are outside.");
            Assert.True(Probe(new Vector3(0, 0, 6), new Vector3(0, 0, 6)), "Contained collection-point probe is detected.");
            Assert.False(Probe(new Vector3(-20, 0, -1), new Vector3(20, 0, -1)));
            Assert.True(Probe(new Vector3(-20, 0, -1), new Vector3(20, 0, -1), 1.1f), "Clearance margin protects sidewalk and drive approaches.");
        }
        finally { Object.DestroyImmediate(go); }
    }

    [Test] public void DecorativeMarkerHasNoGameplayCollectionOrNetworkComponents()
    {
        var go = new GameObject("Decorative-only marker");
        try
        {
            go.AddComponent(Runtime("CoastalInfillSite"));
            Assert.IsNull(go.GetComponent(Runtime("PickupStop")));
            Assert.IsNull(go.GetComponent(Runtime("CoastalHouseRoute")));
            Assert.IsNull(go.GetComponent(Runtime("HouseholdCar")));
            Assert.IsNull(go.GetComponent(Runtime("Knockable")));
        }
        finally { Object.DestroyImmediate(go); }
    }
}
