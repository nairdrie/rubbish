using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Full-build repair must reject everything the final lane validator would reject.</summary>
public class CoastalParcelPlacementTests
{
    const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    static Type R(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static Type Repair => Type.GetType("CoastalParcelPlacement, Assembly-CSharp-Editor", true);
    static T Field<T>(object value, string name) => (T)value.GetType().GetField(name).GetValue(value);
    static void Set(object value, string name, object field) => value.GetType().GetField(name).SetValue(value, field);
    static object Envelopes(object roads) => Repair.GetMethod("RoadEnvelopes", PrivateStatic, null,
        new[] { typeof(IReadOnlyList<>).MakeGenericType(R("CoastalCityLayout+RoadDef")) }, null).Invoke(null, new[] { roads });

    [Test]
    public void EveryFinalValidatorStationAndAllThreeLaneBoxesFitInsideARepairEnvelope()
    {
        var layout = R("CoastalCityLayout");
        var roads = layout.GetProperty("Roads").GetValue(null);
        var envelopes = ((IEnumerable)Envelopes(roads)).Cast<object>().ToLookup(e => Field<string>(e, "road"));
        float laneOffset = (float)R("StreetGraph").GetField("LaneOffset").GetRawConstantValue();
        foreach (var road in (IEnumerable)roads)
        {
            float length = (float)road.GetType().GetProperty("Length").GetValue(road);
            int count = Mathf.Max(1, Mathf.CeilToInt(length / 2.5f));
            float lane = Mathf.Min(laneOffset, Field<float>(road, "halfWidth") * .5f);
            var own = envelopes[Field<string>(road, "id")].ToArray();
            for (int i = 0; i <= count; i++)
            {
                var pose = (Pose)road.GetType().GetMethod("At").Invoke(road, new object[] { length * i / count });
                var center = pose.position + Vector3.up * 1.8f;
                Assert.IsTrue(own.Any(e =>
                    Vector3.SqrMagnitude(Field<Vector3>(e, "center") - center) < .000001f &&
                    Quaternion.Angle(Field<Quaternion>(e, "rotation"), pose.rotation) < .01f &&
                    Field<Vector3>(e, "half").x >= lane + .85f &&
                    Field<Vector3>(e, "half").y >= 1.5f && Field<Vector3>(e, "half").z >= 1.2f),
                    Field<string>(road, "id") + " validator station " + i + "/" + count + " is not covered by repair.");
            }
        }
    }

    object SyntheticRoads()
    {
        var roadType = R("CoastalCityLayout+RoadDef");
        var road = Activator.CreateInstance(roadType);
        Set(road, "id", "isolated-fixture"); Set(road, "halfWidth", 5f);
        Set(road, "points", new[] { new Vector3(10000, 0, 10000), new Vector3(10000, 0, 10010) });
        var roads = Array.CreateInstance(roadType, 1); roads.SetValue(road, 0); return roads;
    }

    bool RepairAccepts(Collider collider, object envelopes, out string reason)
    {
        var lot = Activator.CreateInstance(Repair.GetNestedType("Lot", BindingFlags.NonPublic), true);
        Set(lot, "colliders", new HashSet<Collider> { collider });
        var args = new[] { lot, envelopes, new Collider[512], null };
        bool clear = (bool)Repair.GetMethod("RoadClear", PrivateStatic).Invoke(null, args);
        reason = args[3] as string; return clear;
    }

    [Test]
    public void HouseCornerBetween105And120CentimetresPastAnEndpointIsRejected()
    {
        var go = new GameObject("Endpoint regression house corner");
        try
        {
            go.transform.position = new Vector3(10002.5f, 1.8f, 10011.175f);
            var collider = go.AddComponent<BoxCollider>(); collider.size = new Vector3(.02f, .2f, .02f);
            Physics.SyncTransforms();
            var finalHits = Physics.OverlapBox(new Vector3(10002.5f, 1.8f, 10010), new Vector3(.85f, 1.5f, 1.2f));
            CollectionAssert.Contains(finalHits, collider, "The final validator's exact last right-lane probe catches this corner.");
            Assert.IsFalse(RepairAccepts(collider, Envelopes(SyntheticRoads()), out var reason));
            StringAssert.Contains("isolated-fixture", reason);
        }
        finally { Object.DestroyImmediate(go); Physics.SyncTransforms(); }
    }

    [TestCase(4.6f, false)]
    [TestCase(6f, true)]
    public void FullRoadWidthRemainsProtectedWithoutRejectingClearOffRoadLots(float side, bool expectedClear)
    {
        var go = new GameObject("Road shoulder fixture");
        try
        {
            go.transform.position = new Vector3(10000 + side, 1.8f, 10005);
            var collider = go.AddComponent<BoxCollider>(); collider.size = new Vector3(.1f, .2f, .1f);
            Physics.SyncTransforms();
            Assert.AreEqual(expectedClear, RepairAccepts(collider, Envelopes(SyntheticRoads()), out _));
        }
        finally { Object.DestroyImmediate(go); Physics.SyncTransforms(); }
    }
}
