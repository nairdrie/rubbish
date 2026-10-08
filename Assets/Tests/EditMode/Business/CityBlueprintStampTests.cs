using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public class CityBlueprintStampTests
{
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static T Field<T>(object o, string field) => (T)o.GetType().GetField(field).GetValue(o);
    static void Set(object o, string field, object value) => o.GetType().GetField(field).SetValue(o, value);
    static object Blueprint()
    {
        var text = Resources.Load<TextAsset>((string)Runtime("CoastalCityLayout").GetField("ResourcePath").GetRawConstantValue());
        Assert.NotNull(text);
        return JsonUtility.FromJson(text.text, Runtime("CoastalCityLayout+Blueprint"));
    }
    static string Fingerprint(object b) => (string)Runtime("CityBlueprintStamp").GetMethod("StructuralFingerprint", BindingFlags.Public | BindingFlags.Static).Invoke(null, new[] { b });

    [Test]
    public void RoadOnlyJunctionMoveDoesNotRequireAFullStructuralRebuild()
    {
        var b = Blueprint(); var before = Fingerprint(b);
        var terrain = Field<object>(b, "terrain");
        var terrainIds = Field<string[]>(terrain, "river").Concat(Field<string[]>(terrain, "coast")).Concat(Field<string[]>(terrain, "riverWalk")).ToArray();
        var roadIds = Field<Array>(b, "roads").Cast<object>().SelectMany(r => Field<string[]>(r, "nodes")).ToArray();
        var node = Field<Array>(b, "nodes").Cast<object>().First(n => roadIds.Contains(Field<string>(n, "id")) && !terrainIds.Contains(Field<string>(n, "id")));
        Set(node, "x", Field<float>(node, "x") + 20f);
        Assert.AreEqual(before, Fingerprint(b));
    }

    [Test]
    public void RiverNodeMoveRequiresFullStructuralRebuild()
    {
        var b = Blueprint(); var before = Fingerprint(b);
        string id = Field<string[]>(Field<object>(b, "terrain"), "river")[0];
        var node = Field<Array>(b, "nodes").Cast<object>().First(n => Field<string>(n, "id") == id);
        Set(node, "z", Field<float>(node, "z") + 12f);
        Assert.AreNotEqual(before, Fingerprint(b));
    }

    [Test]
    public void ScaleChangeRequiresFullStructuralRebuild()
    {
        var b = Blueprint(); var before = Fingerprint(b);
        Set(b, "scale", Field<float>(b, "scale") * 1.1f);
        Assert.AreNotEqual(before, Fingerprint(b));
    }

    [Test]
    public void LandmarkMoveRequiresFullStructuralRebuild()
    {
        var b = Blueprint(); var before = Fingerprint(b);
        var landmark = Field<Array>(b, "landmarks").GetValue(0);
        Set(landmark, "x", Field<float>(landmark, "x") + 6f);
        Assert.AreNotEqual(before, Fingerprint(b));
    }
}
