using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

/// <summary>Meadows' authored court layout, without opening scenes or changing live blueprint caches.</summary>
public class CoastalMeadowsLayoutTests
{
    const string District = "minivan_meadows";
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static Type Editor(string name) => Type.GetType(name + ", Assembly-CSharp-Editor", true);
    static T Get<T>(object value, string name) => (T)value.GetType().GetField(name).GetValue(value);

    static object Blueprint()
    {
        var text = Resources.Load<TextAsset>("City/CoastalCity");
        Assert.IsNotNull(text);
        return JsonUtility.FromJson(text.text, Runtime("CoastalCityLayout+Blueprint"));
    }

    static object Road(object blueprint, string id) => Get<Array>(blueprint, "roads").Cast<object>()
        .Single(road => Get<string>(road, "id") == id);

    static object ResolvedRoad(object blueprint, string id) => Runtime("CoastalCityLayout")
        .GetMethod("ResolveRoad").Invoke(null, new[] { blueprint, id });

    static DictionaryEntry[] Placements(object blueprint)
    {
        var text = Resources.Load<TextAsset>("City/ResidentialBlocks");
        Assert.IsNotNull(text);
        var recipe = JsonUtility.FromJson(text.text, Editor("CoastalResidentialLayout").GetNestedType("Recipe"));
        var placements = (IDictionary)Editor("CoastalResidentialLayout").GetMethod("Resolve")
            .Invoke(null, new[] { recipe, blueprint });
        return placements.Keys.Cast<object>().Select(key => new DictionaryEntry(key, placements[key]))
            .Where(entry => Get<string>(entry.Value, "districtId") == District).ToArray();
    }

    [Test]
    public void MeadowsKeepsExactlyItsTwentyTwoExistingHouseholds()
    {
        var expected = new[] {
            "mm-carpool-10", "mm-carpool-22", "mm-carpool-42", "mm-carpool-54",
            "mm-glutenfree-102", "mm-glutenfree-103", "mm-juicebox-11", "mm-juicebox-210",
            "mm-juicebox-224", "mm-juicebox-23", "mm-juicebox-240", "mm-juicebox-254",
            "mm-juicebox-43", "mm-juicebox-55", "mm-playdate-102", "mm-playdate-103",
            "mm-soccermom-21", "mm-soccermom-33", "mm-soccermom-43", "mm-trampoline-20",
            "mm-trampoline-32", "mm-trampoline-42"
        };
        var actual = Placements(Blueprint()).Select(entry => (string)entry.Key).ToArray();
        Assert.AreEqual(22, actual.Length);
        CollectionAssert.AreEquivalent(expected, actual, "Replanning must not change saves, customers or trash-generation identities.");
    }

    [Test]
    public void TwelveHomesFaceTheCourtsAndTenStayOnTheCollector()
    {
        var placements = Placements(Blueprint()).Select(entry => entry.Value).ToArray();
        Assert.AreEqual(6, placements.Count(p => Get<string>(p, "roadId") == "carpool-court"));
        Assert.AreEqual(6, placements.Count(p => Get<string>(p, "roadId") == "juicebox-court"));
        Assert.AreEqual(10, placements.Count(p => Get<string>(p, "roadId") == "meadow-loop"));
        Assert.IsTrue(placements.All(p => Get<int>(p, "side") == 1), "Court houses face inward from the outside curb.");
    }

    [TestCase("carpool-court", "carpool", "carpool-2")]
    [TestCase("juicebox-court", "juicebox", "juicebox-2")]
    public void CourtIsAClosedTwentyTwoMetreTurningLoopWithOnlyOneStreetEntrance(string courtId, string stemId, string entrance)
    {
        var blueprint = Blueprint(); var court = Road(blueprint, courtId);
        Assert.AreEqual(District, Get<string>(court, "districtId"));
        Assert.IsTrue(Get<bool>(court, "service"), "Quiet courts should not receive collector-road centre dashes.");
        Assert.IsFalse(Get<bool>(court, "bridge"));
        Assert.IsFalse(Get<bool>(court, "curve"), "Authored circular samples must not be re-smoothed into an oval.");
        Assert.AreEqual(5f, Get<float>(court, "halfWidth"));
        var ids = Get<string[]>(court, "nodes");
        Assert.AreEqual(entrance, ids[0]); Assert.AreEqual(entrance, ids.Last());
        Assert.AreEqual(ids.Length - 1, ids.Distinct().Count(), "Only the loop-closing node may repeat.");
        CollectionAssert.Contains(Get<string[]>(Road(blueprint, stemId), "nodes"), entrance);
        var shared = Get<Array>(blueprint, "roads").Cast<object>().Where(road => Get<string>(road, "id") != courtId)
            .SelectMany(road => Get<string[]>(road, "nodes")).Intersect(ids).ToArray();
        CollectionAssert.AreEquivalent(new[] { entrance }, shared, "A court must not become another through-road.");

        var points = Get<Vector3[]>(ResolvedRoad(blueprint, courtId), "points");
        Assert.That(Vector3.Distance(points[0], points.Last()), Is.LessThan(.001f));
        var unique = points.Take(points.Length - 1).ToArray();
        Vector3 center = unique.Aggregate(Vector3.zero, (sum, point) => sum + point) / unique.Length;
        foreach (var point in unique)
            Assert.That(Vector3.Distance(center, point), Is.EqualTo(22f).Within(.002f), "Truck turning radius is measured in world metres.");
        Assert.That(22f - Get<float>(court, "halfWidth") - 3f, Is.GreaterThan(10f), "Preserve a useful planted centre beyond the inner sidewalk.");
    }

    [TestCase("carpool-court")]
    [TestCase("juicebox-court")]
    public void CourtFrontagesRemainCompactAndLeaveTheEntranceOpen(string courtId)
    {
        var blueprint = Blueprint(); var road = ResolvedRoad(blueprint, courtId);
        float length = (float)road.GetType().GetProperty("Length").GetValue(road);
        var stations = Placements(blueprint).Select(entry => entry.Value)
            .Where(p => Get<string>(p, "roadId") == courtId).Select(p => Get<float>(p, "u")).OrderBy(u => u).ToArray();
        Assert.AreEqual(6, stations.Length);
        for (int i = 1; i < stations.Length; i++)
        {
            // The first Juice Box property has a sideways play pen: its attachment is shifted away from
            // the entry lane. Explicit curb stations may be closer while their outward-fanning roofs stay clear.
            bool entranceAdjustment = courtId == "juicebox-court" && i == 1;
            Assert.That((stations[i] - stations[i - 1]) * length,
                Is.InRange(entranceAdjustment ? 13f : 20f, 22f), "Keep compact frontage without pulling the play pen into the entrance.");
        }
        Assert.That(stations[0] * length, Is.GreaterThan(16f));
        Assert.That((1f - stations.Last()) * length, Is.GreaterThan(16f));
    }

    [Test]
    public void AllThreeAcresConnectionsAndTheCoastBridgeKeepTheirExistingJunctions()
    {
        var blueprint = Blueprint();
        var expectedIds = new[] { "home-5", "maple-5", "birch-4", "meadow-loop-4", "coast-approach-1", "coast-bridge-1", "coast-bridge-2" };
        var expectedPoints = new[] { new Vector2(750, 100), new Vector2(753.75f, 295), new Vector2(855, 390),
            new Vector2(1147.5f, 370), new Vector2(1110, 432), new Vector2(1015.18018018f, 530), new Vector2(1015.18018018f, 560) };
        var nodes = Get<Array>(blueprint, "nodes").Cast<object>().ToArray();
        for (int i = 0; i < expectedIds.Length; i++)
        {
            var node = nodes.Single(n => Get<string>(n, "id") == expectedIds[i]);
            Assert.That(Get<float>(node, "x"), Is.EqualTo(expectedPoints[i].x).Within(.0001f), expectedIds[i]);
            Assert.That(Get<float>(node, "z"), Is.EqualTo(expectedPoints[i].y).Within(.0001f), expectedIds[i]);
        }
        var gates = Get<Array>(blueprint, "gates").Cast<object>().Where(g => Get<string>(g, "districtId") == District).ToArray();
        CollectionAssert.AreEquivalent(new[] { "meadows-home", "meadows-maple", "meadows-birch" }, gates.Select(g => Get<string>(g, "id")));
        foreach (var expected in new[] { ("meadows-home", "home", .935121f), ("meadows-maple", "maple", .88431f), ("meadows-birch", "birch", .711265165f) })
        {
            var gate = gates.Single(g => Get<string>(g, "id") == expected.Item1);
            Assert.AreEqual(expected.Item2, Get<string>(gate, "roadId"));
            Assert.That(Get<float>(gate, "u"), Is.EqualTo(expected.Item3).Within(.000001f));
            Assert.AreEqual(1, Get<int>(gate, "forward"));
        }
    }
}
