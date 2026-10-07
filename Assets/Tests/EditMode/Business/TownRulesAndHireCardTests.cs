using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UObj = UnityEngine.Object;

/// <summary>Assembly-CSharp UI and settings are reached by reflection from the EditMode test assembly.</summary>
public sealed class TownRulesAndHireCardTests
{
    static Type Settings => Type.GetType("GameSettings, Assembly-CSharp", true);
    static Type SettingsData => Type.GetType("GameSettingsData, Assembly-CSharp", true);
    static Type Hire => Type.GetType("HireCard, Assembly-CSharp", true);
    static Type Stack => Type.GetType("UiStack, Assembly-CSharp", true);

    static object Get(object o, string field) => o.GetType().GetField(field,
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetValue(o);

    static object Parse(string json)
    {
        var args = new object[] { json, null };
        return Settings.GetMethod("FromJson", BindingFlags.Public | BindingFlags.Static).Invoke(null, args);
    }

    static bool Tag(object data) => (bool)Get(Get(data, "townRules"), "rabiesTag");

    [TearDown]
    public void TearDown()
    {
        Settings.GetMethod("Unload", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
    }

    [Test]
    public void NewAndOlderSettingsDefaultRabiesTagOnButExplicitOffSurvives()
    {
        Assert.IsTrue(Tag(Activator.CreateInstance(SettingsData)));
        Assert.IsTrue(Tag(Parse("{\"schema\":1,\"audio\":{\"master\":37}}")), "an old file has no Town Rules section");
        Assert.IsTrue(Tag(Parse("{\"townRules\":null}")), "a null section is repaired to the default");
        Assert.IsFalse(Tag(Parse("{\"townRules\":{\"rabiesTag\":false}}")), "the host's deliberate off choice persists");
    }

    [Test]
    public void RulesCardOffersOnlyTheImplementedRule()
    {
        var card = Type.GetType("TownRulesCard, Assembly-CSharp", true);
        Assert.AreEqual(1, card.GetField("RowCount").GetRawConstantValue());
    }

    [Test]
    public void ResetTownRulesRestoresTagWithoutResettingOtherSettings()
    {
        var data = Activator.CreateInstance(SettingsData);
        Get(data, "townRules").GetType().GetField("rabiesTag").SetValue(Get(data, "townRules"), false);
        Get(data, "audio").GetType().GetField("master").SetValue(Get(data, "audio"), 37);
        Settings.GetField("data", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, data);
        Settings.GetMethod("Reset", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { "townRules" });
        Assert.IsTrue(Tag(data));
        Assert.AreEqual(37, Get(Get(data, "audio"), "master"));
    }

    [Test]
    public void HireCardBackPopsItsScreenAndDisposesItsCanvas()
    {
        var go = new GameObject("HireCard Esc test");
        try
        {
            var card = go.AddComponent(Hire);
            Hire.GetField("current", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, card);
            Hire.GetMethod("Open", BindingFlags.Public | BindingFlags.Instance).Invoke(card, new object[] { false });
            Assert.IsTrue((bool)Stack.GetMethod("Contains").Invoke(null, new object[] { card }));
            Assert.IsTrue((bool)Hire.GetMethod("OnBack").Invoke(card, null));
            Assert.IsFalse((bool)Stack.GetMethod("Contains").Invoke(null, new object[] { card }));
            Assert.IsNull(Hire.GetField("current", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null));
            Assert.IsTrue(go == null, "Esc must also remove the visible canvas");
        }
        finally { if (go != null) UObj.DestroyImmediate(go); }
    }

    [Test]
    public void ExternalStackPopAlsoDisposesHireCardCanvas()
    {
        var go = new GameObject("HireCard Pop test");
        try
        {
            var card = go.AddComponent(Hire);
            Hire.GetField("current", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, card);
            Hire.GetMethod("Open", BindingFlags.Public | BindingFlags.Instance).Invoke(card, new object[] { false });
            Stack.GetMethod("Pop", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { card });
            Assert.IsNull(Hire.GetField("current", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null));
            Assert.IsTrue(go == null, "PopAll or another screen owner must not strand the canvas");
        }
        finally { if (go != null) UObj.DestroyImmediate(go); }
    }
}
