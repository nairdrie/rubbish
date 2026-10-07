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

    Component Laptop(float meshScale, float officeScale, float yaw)
    {
        var office = Root("Environment test office");
        office.transform.position = new Vector3(8f, 1.31f, -6f);
        office.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        office.transform.localScale = Vector3.one * officeScale;
        var laptop = Child(office.transform, "laptop");
        laptop.transform.localScale = Vector3.one * meshScale;
        var component = laptop.AddComponent(Runtime("LaptopController"));
        Ensure(component);
        return component;
    }

    static void Ensure(Component laptop) => Runtime("RabiesShotKiosk").GetMethod("Ensure", Static).Invoke(null, new object[] { laptop });

    [TestCase(1f, 1f, 0f)]
    [TestCase(100f, 1.3f, 0f)]
    [TestCase(100f, 1.3f, 90f)]
    public void MedicalBoxAndShelfStaySupportedAtMetreScale(float meshScale, float officeScale, float yaw)
    {
        var laptop = Laptop(meshScale, officeScale, yaw);
        var shelf = laptop.transform.Find("RabiesShotShelf");
        Assert.NotNull(shelf);
        Assert.That(shelf.TransformVector(Vector3.right).magnitude, Is.EqualTo(1f).Within(0.0001f));
        Assert.That(shelf.TransformVector(Vector3.up).magnitude, Is.EqualTo(1f).Within(0.0001f));
        Assert.That(shelf.TransformVector(Vector3.forward).magnitude, Is.EqualTo(1f).Within(0.0001f));

        var anchor = shelf.Find("MedicalShelfAnchor");
        var top = anchor.Find("ShelfTop").GetComponent<Renderer>();
        var box = anchor.Find("Box/Case").GetComponent<Renderer>();
        Assert.That(box.bounds.min.y, Is.EqualTo(top.bounds.max.y).Within(0.0001f), "medicine rests on the tray");
        int legs = 0;
        foreach (Transform child in anchor)
        {
            if (child.name != "ShelfLeg") continue;
            legs++;
            var bounds = child.GetComponent<Renderer>().bounds;
            Assert.That(bounds.min.y, Is.EqualTo(0.005f).Within(0.001f), "authored laptop pivot is 1.31 m above the garage floor");
            Assert.That(bounds.max.y, Is.GreaterThanOrEqualTo(top.bounds.min.y - 0.001f), "legs meet the tray");
        }
        Assert.AreEqual(4, legs);
        Assert.IsEmpty(shelf.GetComponentsInChildren<Collider>(true), "decorative shelf must not add a walking obstacle");
    }

    [Test]
    public void EnsuringAHiddenDepotDoesNotDuplicateItsMedicalShelf()
    {
        var laptop = Laptop(100f, 1.3f, 0f);
        Ensure(laptop);
        Ensure(laptop);
        Assert.AreEqual(1, laptop.GetComponentsInChildren(Runtime("RabiesShotKiosk"), true).Length);
        Assert.AreEqual(1, laptop.GetComponentsInChildren(Runtime("InteractionZone"), true).Length);
    }

    [TestCase(1f, 1f)]
    [TestCase(100f, 1.3f)]
    public void MedicalInteractionZoneKeepsItsReachWhenImportedLaptopIsScaled(float meshScale, float officeScale)
    {
        var laptop = Laptop(meshScale, officeScale, 90f);
        var zone = laptop.GetComponentInChildren(Runtime("InteractionZone"), true);
        var contains = zone.GetType().GetMethod("Contains", Instance);
        Vector3 origin = laptop.transform.position;
        Assert.IsTrue((bool)contains.Invoke(zone, new object[] { origin + Vector3.right * 2f }));
        Assert.IsFalse((bool)contains.Invoke(zone, new object[] { origin + Vector3.right * 2.4f }));
        Assert.AreEqual("OnFoot", zone.GetType().GetField("mode", Instance).GetValue(zone).ToString());
        Assert.AreSame(laptop.GetComponentInChildren(Runtime("RabiesShotKiosk"), true), zone.GetType().GetField("targetOverride", Instance).GetValue(zone));
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
