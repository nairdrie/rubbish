using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Isolated prop hierarchies only: no scene-wide cleanup, gameplay, account or save writes.</summary>
public class DepotEnvironmentPresentationTests
{
    const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    readonly List<GameObject> roots = new List<GameObject>();
    readonly Dictionary<IDictionary, Dictionary<object, object>> materialCaches = new Dictionary<IDictionary, Dictionary<object, object>>();

    [SetUp]
    public void SetUp()
    {
        foreach (string name in new[] { "litCache", "unlitCache" })
        {
            var cache = (IDictionary)Runtime("CritterLook").GetField(name, Static).GetValue(null);
            var before = new Dictionary<object, object>();
            foreach (DictionaryEntry entry in cache) before.Add(entry.Key, entry.Value);
            materialCaches.Add(cache, before);
        }
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var root in roots) if (root != null) Object.DestroyImmediate(root);
        roots.Clear();
        foreach (var pair in materialCaches)
        {
            var addedKeys = new List<object>();
            foreach (DictionaryEntry entry in pair.Key)
            {
                if (pair.Value.TryGetValue(entry.Key, out object old) && ReferenceEquals(old, entry.Value)) continue;
                if (entry.Value is Material material && material != null) Object.DestroyImmediate(material);
                addedKeys.Add(entry.Key);
            }
            foreach (object key in addedKeys) pair.Key.Remove(key);
            foreach (var entry in pair.Value) pair.Key[entry.Key] = entry.Value;
        }
        materialCaches.Clear();
    }

    GameObject Root(string name)
    {
        var root = new GameObject(name);
        root.SetActive(false);
        roots.Add(root);
        return root;
    }

    static GameObject Child(Transform parent, string name)
    {
        var child = new GameObject(name);
        child.transform.SetParent(parent, false);
        return child;
    }

    [Test]
    public void TheLaptopDeskHasNoMedicalShelfAndShotsAreNotTiedToAKioskAnyMore()
    {
        Assert.IsNull(Type.GetType("RabiesShotKiosk, Assembly-CSharp", false), "the first-aid shelf by the laptop was removed");
        // Nor is a shot something a friend can give you on the street: the jab on a downed player was removed (2026-10-12), so
        // everybody buys their own on Scrapazon.
        Assert.IsNull(Type.GetType("RabiesJab, Assembly-CSharp", false), "no interaction that cures another player");

        // Nothing adds a shelf to a laptop: a bare laptop object keeps no children and no interaction zone.
        var office = Root("Environment test office");
        var laptop = Child(office.transform, "laptop");
        laptop.AddComponent(Runtime("LaptopController"));
        Assert.AreEqual(0, laptop.transform.childCount);
        Assert.IsEmpty(laptop.GetComponentsInChildren(Runtime("InteractionZone"), true));
    }

    [Test]
    public void LegacyCommuteLabelStaysHiddenWhenRailUpgradeVisibilityChanges()
    {
        var root = Root("Environment test upgrades");
        var rail = Child(root.transform, "RailBranch");
        var label = Child(rail.transform, "LaneText");
        var parking = Child(rail.transform, "ParkText");
        var type = Runtime("GarageUpgradeVisuals");
        var visuals = root.AddComponent(type);
        var entryType = type.GetNestedType("Entry");
        object entry = Activator.CreateInstance(entryType);
        entryType.GetField("id").SetValue(entry, "environment_test_unowned_upgrade");
        entryType.GetField("minLevel").SetValue(entry, 0);
        entryType.GetField("maxLevel").SetValue(entry, 0);
        entryType.GetField("target").SetValue(entry, rail);
        var entries = Array.CreateInstance(entryType, 1);
        entries.SetValue(entry, 0);
        type.GetField("entries").SetValue(visuals, entries);

        // The root stays inactive, so invoke the lifecycle explicitly and always detach its event listeners.
        try
        {
            type.GetMethod("OnEnable", Instance).Invoke(visuals, null);
            Assert.IsFalse(label.activeSelf);
            Assert.IsTrue(parking.activeSelf, "hide only the commute annotation, not the useful parking sign");
            rail.SetActive(false);
            type.GetMethod("Apply", Instance).Invoke(visuals, new object[] { false });
            Assert.IsTrue(rail.activeSelf);
            Assert.IsFalse(label.activeSelf, "re-enabling the upgrade must not restore its old annotation");
        }
        finally { type.GetMethod("OnDisable", Instance).Invoke(visuals, null); }
    }
}
