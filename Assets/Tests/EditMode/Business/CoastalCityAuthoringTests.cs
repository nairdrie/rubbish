using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public class CoastalCityAuthoringTests
{
    const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;
    static Type R(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object Call(string type, string name, params object[] args) => R(type).GetMethod(name, Static).Invoke(null, args);
    static T Field<T>(object o, string name) => (T)o.GetType().GetField(name).GetValue(o);
    static void Set(object o, string name, object value) => o.GetType().GetField(name).SetValue(o, value);
    static object Blueprint()
    {
        var asset = Resources.Load<TextAsset>((string)R("CoastalCityLayout").GetField("ResourcePath").GetRawConstantValue());
        Assert.NotNull(asset);
        return JsonUtility.FromJson(asset.text, R("CoastalCityLayout+Blueprint"));
    }
    static string[] Errors(object b) => ((IEnumerable)Call("CoastalCityValidation", "ValidateBlueprintTopology", b)).Cast<string>().ToArray();
    static string Fingerprint(object b, string id) => (string)Call("CoastalCityLayout", "ResolveRoad", b, id).GetType().GetProperty("Fingerprint").GetValue(Call("CoastalCityLayout", "ResolveRoad", b, id));
    static object CloneNode(object node, string id)
    {
        var clone = Activator.CreateInstance(node.GetType());
        Set(clone, "id", id); Set(clone, "x", Field<float>(node, "x")); Set(clone, "z", Field<float>(node, "z"));
        return clone;
    }
    static void AppendNodes(object blueprint, params object[] additions)
    {
        var before = Field<Array>(blueprint, "nodes");
        var after = Array.CreateInstance(before.GetType().GetElementType(), before.Length + additions.Length);
        Array.Copy(before, after, before.Length);
        for (int i = 0; i < additions.Length; i++) after.SetValue(additions[i], before.Length + i);
        Set(blueprint, "nodes", after);
    }

    [Test]
    public void ShippedBlueprintHasStableReferencesConnectedRoadsAndBridges()
    {
        var errors = Errors(Blueprint());
        Assert.IsEmpty(errors, string.Join("\n", errors));
    }

    [Test]
    public void MovingOneNamedJunctionUpdatesEveryIncidentRoadAndNoUnrelatedRoad()
    {
        var blueprint = Blueprint();
        var roads = Field<Array>(blueprint, "roads").Cast<object>().ToArray();
        var nodes = Field<Array>(blueprint, "nodes").Cast<object>().ToArray();
        var junction = nodes.First(n => roads.Count(r => Field<string[]>(r, "nodes").Contains(Field<string>(n, "id"))) >= 3);
        string id = Field<string>(junction, "id");
        var before = roads.ToDictionary(r => Field<string>(r, "id"), r => Fingerprint(blueprint, Field<string>(r, "id")));
        Set(junction, "x", Field<float>(junction, "x") + 12.5f);
        Set(junction, "z", Field<float>(junction, "z") - 7.5f);
        int changed = 0;
        foreach (var road in roads)
        {
            string roadId = Field<string>(road, "id");
            bool incident = Field<string[]>(road, "nodes").Contains(id);
            bool didChange = before[roadId] != Fingerprint(blueprint, roadId);
            Assert.AreEqual(incident, didChange, roadId + " dependency on " + id);
            if (didChange) changed++;
        }
        Assert.GreaterOrEqual(changed, 3);
        Assert.IsEmpty(Errors(blueprint), "Coordinate edits must not break reference connectivity.");
    }

    [Test]
    public void RenamingReferencedJunctionIsRejectedInsteadOfLeavingRoadStubs()
    {
        var b = Blueprint();
        var road = Field<Array>(b, "roads").GetValue(0);
        Field<string[]>(road, "nodes")[0] = "deleted-junction";
        StringAssert.Contains("missing junction", string.Join("\n", Errors(b)));
    }

    [Test]
    public void AVisuallyTouchingBridgeWithDifferentJunctionIdsIsReported()
    {
        var b = Blueprint();
        var bridge = Field<Array>(b, "roads").Cast<object>().First(r => Field<bool>(r, "bridge"));
        string[] path = Field<string[]>(bridge, "nodes");
        var original = Field<Array>(b, "nodes").Cast<object>().First(n => Field<string>(n, "id") == path[0]);
        AppendNodes(b, CloneNode(original, "unlinked-bridge-bank"));
        path[0] = "unlinked-bridge-bank";
        StringAssert.Contains("without a connected land road", string.Join("\n", Errors(b)));
    }

    [Test]
    public void DisconnectedRoadAndNonFiniteMapBoundsAreReported()
    {
        var b = Blueprint();
        var road = Field<Array>(b, "roads").Cast<object>().First(r => Field<bool>(r, "service"));
        string[] path = Field<string[]>(road, "nodes");
        var existing = Field<Array>(b, "nodes").Cast<object>().ToDictionary(n => Field<string>(n, "id"));
        var clones = path.Select((id, i) => CloneNode(existing[id], "orphan-" + i)).ToArray();
        AppendNodes(b, clones);
        Set(road, "nodes", clones.Select(n => Field<string>(n, "id")).ToArray());
        Field<float[]>(b, "bounds")[0] = float.NaN;
        string errors = string.Join("\n", Errors(b));
        StringAssert.Contains("disconnected from the depot", errors);
        StringAssert.Contains("Non-finite map bounds", errors);
    }

    [Test]
    public void SceneValidationFindsDuplicatedStopsAndUnanchoredLotsWithoutMutatingThem()
    {
        var town = new GameObject("NeighborhoodGen");
        try
        {
            var marker = new GameObject((string)R("CoastalCityLayout").GetField("MarkerName").GetRawConstantValue());
            marker.transform.SetParent(town.transform);
            var children = new GameObject[2];
            for (int i = 0; i < children.Length; i++)
            {
                children[i] = new GameObject("Test stop " + i);
                children[i].transform.SetParent(town.transform);
                children[i].transform.position = new Vector3(i, 0f, 0f);
                var stop = children[i].AddComponent(R("PickupStop")); Set(stop, "stopId", "duplicated-stop");
            }
            var before = children.Select(c => c.transform.position).ToArray();
            string errors = string.Join("\n", ((IEnumerable)Call("CoastalCityValidation", "Validate", town.transform, null)).Cast<string>());
            StringAssert.Contains("duplicate collection stopId", errors);
            StringAssert.Contains("no CityRoadAnchor", errors);
            CollectionAssert.AreEqual(before, children.Select(c => c.transform.position));
        }
        finally { UnityEngine.Object.DestroyImmediate(town); }
    }
}
