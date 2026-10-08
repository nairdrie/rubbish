using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Compact-city authoring contracts. Pure blueprint/seed checks; no rebuild, scene, save or gameplay writes.</summary>
public class CoastalCityDensityTests
{
    static Type R(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static Type E(string name) => Type.GetType(name + ", Assembly-CSharp-Editor", true);
    static T Field<T>(object o, string field) => (T)o.GetType().GetField(field).GetValue(o);
    static void Set(object o, string field, object value) => o.GetType().GetField(field).SetValue(o, value);
    static object Blueprint()
    {
        var text = Resources.Load<TextAsset>((string)R("CoastalCityLayout").GetField("ResourcePath").GetRawConstantValue());
        Assert.NotNull(text);
        return JsonUtility.FromJson(text.text, R("CoastalCityLayout+Blueprint"));
    }
    static object Road(object b, string id) => R("CoastalCityLayout").GetMethod("ResolveRoad").Invoke(null, new[] { b, id });
    static float Length(object road) => (float)road.GetType().GetProperty("Length").GetValue(road);
    static Pose At(object road, float u) => (Pose)road.GetType().GetMethod("AtNormalized").Invoke(road, new object[] { u });
    static object[] Strips(object b, string district) => Field<Array>(b, "lotStrips").Cast<object>().Where(s => Field<string>(s, "districtId") == district).ToArray();
    static int Count(object b, object strip)
    {
        var field = strip.GetType().GetField("count");
        Assert.NotNull(field, "The compact blueprint's explicit household quotas must be parsed, not silently ignored.");
        int count = (int)field.GetValue(strip);
        return count > 0 ? count : Mathf.Max(1, Mathf.FloorToInt(Length(Road(b, Field<string>(strip, "roadId"))) *
            (Field<float>(strip, "end") - Field<float>(strip, "start")) / Field<float>(strip, "spacing")));
    }
    static object[] BuiltSeeds(string district) => ((IEnumerable)E("NeighborhoodBuilder")
        .GetMethod("CoastalLotSites", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { district })).Cast<object>().ToArray();

    [TestCase("mildly_pleasant", 37)]
    [TestCase("minivan_meadows", 22)]
    [TestCase("snootington_hills", 14)]
    [TestCase("billionaire_bluffs", 8)]
    public void ExplicitQuotasAreConsumedAndLeaveCapacityForAllPreservedHomes(string district, int preserved)
    {
        var b = Blueprint(); var strips = Strips(b, district);
        int expected = strips.Sum(s => Count(b, s));
        Assert.GreaterOrEqual(expected, preserved, "Compression cannot remove existing household identities.");
        var actual = BuiltSeeds(district);
        Assert.AreEqual(expected, actual.Length, "Builder must use count>0, not shrink quotas with road length.");
        int offset = 0;
        foreach (var strip in strips)
        {
            int count = Count(b, strip);
            for (int i = 0; i < count; i++)
            {
                var site = actual[offset++];
                Assert.AreEqual(Field<string>(strip, "roadId"), Field<string>(site, "road"));
                Assert.AreEqual(Field<int>(strip, "side"), Field<int>(site, "side"));
                Assert.That(Field<float>(site, "u"), Is.EqualTo(Mathf.Lerp(Field<float>(strip, "start"), Field<float>(strip, "end"), (i + .5f) / count)).Within(.00001f));
            }
        }
    }

    [TestCase("carpool", "minivan_meadows", 22)]
    [TestCase("juicebox", "minivan_meadows", 22)]
    [TestCase("monocle", "snootington_hills", 14)]
    [TestCase("estate-spur", "snootington_hills", 14)]
    [TestCase("offshore", "billionaire_bluffs", 8)]
    public void InteriorStreetQuotaIsReachedBeforeTheHouseholdListRunsOut(string road, string district, int preserved)
    {
        Assert.IsTrue(BuiltSeeds(district).Take(preserved).Any(s => Field<string>(s, "road") == road),
            road + " must get actual preserved homes, not only spare candidate slots after all homes are assigned.");
    }

    [Test]
    public void EachUnmovedDepotDoorHasAnOppositeStarterSeedWithin65Metres()
    {
        var b = Blueprint(); var depot = Field<object>(b, "depot"); var depotRoad = Road(b, Field<string>(depot, "roadId"));
        var seeds = BuiltSeeds("mildly_pleasant").Take(37).Where(s => Field<string>(s, "road") == "depot").ToArray();
        Assert.AreEqual(6, seeds.Length, "Six first jobs are deliberately authored across Depot Row.");
        var stations = Field<float[]>(depot, "stations"); Assert.AreEqual(4, stations.Length);
        float[] unchangedZ = { 84.5f, 126.75f, 169f, 211.25f };
        for (int lot = 0; lot < stations.Length; lot++)
        {
            var frame = At(depotRoad, stations[lot]);
            var door = frame.position + frame.rotation * Vector3.right * Field<float>(depot, "lateral");
            Assert.That(Vector3.Distance(door, new Vector3(90.35f, 0, unchangedZ[lot])), Is.LessThan(.01f), "Existing four business aprons stay put.");
            var facing = frame.rotation * Quaternion.Euler(0, Field<float>(depot, "yawOffset"), 0) * Vector3.forward;
            float best = float.PositiveInfinity;
            foreach (var seed in seeds)
            {
                var pose = At(depotRoad, Field<float>(seed, "u"));
                var p = pose.position + pose.rotation * Vector3.right * Field<int>(seed, "side") * (Field<float>(depotRoad, "halfWidth") + .8f);
                Assert.Greater(Vector3.Dot(p - door, facing), 0f, "The starter frontage is across the road, not behind the garage.");
                best = Mathf.Min(best, Vector3.Distance(door, p));
            }
            Assert.Less(best, 65f, "First pickup is too far from depot " + lot);
        }
    }

    [TestCase("west-bridge")]
    [TestCase("town-bridge")]
    [TestCase("coast-bridge")]
    public void FullBridgeAndApproachRoadIsShorterThan145Metres(string id)
    {
        var road = Road(Blueprint(), id);
        Assert.IsTrue(Field<bool>(road, "bridge"));
        Assert.Less(Length(road), 145f);
    }

    [Test]
    public void DensityChangesSpaceBetweenParcelsNotRoadWidthsOrDepotAprons()
    {
        var b = Blueprint();
        Assert.That(Field<float>(b, "scale"), Is.EqualTo(.65f).Within(.00001f));
        foreach (var road in Field<Array>(b, "roads"))
        {
            // Quiet residential courts suppress centre markings using the service flag, but retain a
            // full-width truck carriageway. Only the original delivery lanes are 6.8 m wide.
            string id = Field<string>(road, "id");
            bool residentialCourt = id == "carpool" || id == "juicebox" || id == "carpool-court" || id == "juicebox-court";
            float width = Field<bool>(road, "bridge") ? 6f : Field<bool>(road, "service") && !residentialCourt ? 3.4f : 5f;
            Assert.That(Field<float>(road, "halfWidth"), Is.EqualTo(width).Within(.00001f), Field<string>(road, "id"));
        }
        var depot = Field<object>(b, "depot");
        Assert.AreEqual(15f, Field<float>(depot, "padHalfWidth")); Assert.AreEqual(14f, Field<float>(depot, "padBack"));
        Assert.AreEqual(-33.15f, Field<float>(depot, "lateral"));
    }

    [Test]
    public void MovingAFullSizeParcelOntoTheCompressedRoadDoesNotScaleItsModel()
    {
        var root = new GameObject("Density contract parcel"); root.SetActive(false);
        var child = new GameObject("Existing full size house"); child.transform.SetParent(root.transform, false);
        try
        {
            child.transform.localScale = new Vector3(1.1f, 1.2f, .95f);
            var before = child.transform.lossyScale;
            var anchor = root.AddComponent(R("CityRoadAnchor"));
            Set(anchor, "roadId", "carpool"); Set(anchor, "u", .5f); Set(anchor, "lateral", .8f); Set(anchor, "lateralFromRoadEdge", true);
            var road = Road(Blueprint(), "carpool");
            Assert.IsTrue((bool)anchor.GetType().GetMethod("Apply", new[] { road.GetType() }).Invoke(anchor, new[] { road }));
            Assert.That(Vector3.Distance(before, child.transform.lossyScale), Is.LessThan(.0001f));
            Assert.AreEqual(Vector3.one, root.transform.localScale);
        }
        finally { Object.DestroyImmediate(root); }
    }

    [Test]
    public void CompactBoundsStillContainAllRoadsAndPreserveTheConnectedSixDistrictCity()
    {
        var b = Blueprint(); var raw = Field<float[]>(b, "bounds"); float scale = Field<float>(b, "scale");
        var bounds = Rect.MinMaxRect(raw[0] * scale, raw[1] * scale, raw[2] * scale, raw[3] * scale);
        Assert.Less(bounds.width, 1000f); Assert.Less(bounds.height, 1050f);
        Assert.AreEqual(6, Field<Array>(b, "districts").Length);
        foreach (var spec in Field<Array>(b, "roads"))
            foreach (var p in Field<Vector3[]>(Road(b, Field<string>(spec, "id")), "points"))
                Assert.IsTrue(bounds.Contains(new Vector2(p.x, p.z)), Field<string>(spec, "id") + " leaves the compressed playable bounds.");
        var errors = ((IEnumerable)R("CoastalCityValidation").GetMethod("ValidateBlueprintTopology").Invoke(null, new[] { b })).Cast<string>().ToArray();
        Assert.IsEmpty(errors, string.Join("\n", errors));
        var sites = Field<Array>(b, "collectionSites").Cast<object>().ToArray();
        Assert.AreEqual(17, sites.Length); Assert.AreEqual(17, sites.Select(s => Field<string>(s, "id")).Distinct().Count());
        Assert.AreEqual(9, sites.Count(s => Field<string>(s, "districtId") == "coupon_commons"));
        Assert.AreEqual(8, sites.Count(s => Field<string>(s, "districtId") == "stackington_heights"));
    }
}
