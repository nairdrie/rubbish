using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Visual-only heavy sack regression checks; no world, session, or save is created.</summary>
public class HeavyBagVisualTests
{
    const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    readonly List<GameObject> roots = new List<GameObject>();

    [TearDown]
    public void TearDown()
    {
        foreach (var root in roots) if (root != null) Object.DestroyImmediate(root);
        roots.Clear();
    }

    object Contents(int variant, bool heavy)
    {
        var type = Runtime("BagContents");
        var contents = Activator.CreateInstance(type);
        type.GetField("bagId").SetValue(contents, "heavy-visual-test#tell=" + (heavy ? "heavy" : "none"));
        type.GetField("sackVariant").SetValue(contents, variant);
        type.GetField("bagMassKg").SetValue(contents, 22f);
        return contents;
    }

    Component Spawn(object contents)
    {
        var bag = (Component)Runtime("TrashBag").GetMethod("Spawn", Static).Invoke(null,
            new object[] { contents, Vector3.zero, Quaternion.identity, Vector3.zero });
        roots.Add(bag.gameObject);
        return bag;
    }

    Transform Cosmetic(object contents)
    {
        var parent = new GameObject("Cosmetic sack test");
        roots.Add(parent);
        return (Transform)Runtime("TrashBag").GetMethod("BuildSackVisual", Static).Invoke(null,
            new object[] { contents, parent.transform });
    }

    static Bounds BoundsInParent(Transform model) => (Bounds)Runtime("BagTellFx").GetMethod("LocalBoundsOf", Static,
        null, new[] { typeof(Transform), typeof(Transform) }, null).Invoke(null, new object[] { model, model.parent });

    static void Near(Vector3 actual, Vector3 expected) => Assert.That(Vector3.Distance(actual, expected), Is.LessThan(.0002f));

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void HeavyIsTheSameSackMadeMuchLargerWithoutBoxPartsOrChangedPhysics(int variant)
    {
        var plain = Spawn(Contents(variant, false));
        var contents = Contents(variant, true);
        var heavy = Spawn(contents);
        var plainModel = plain.transform.Find("Visual/Model");
        var heavyModel = heavy.transform.Find("Visual/Model");
        var plainMeshes = plainModel.GetComponentsInChildren<MeshFilter>();
        var heavyMeshes = heavyModel.GetComponentsInChildren<MeshFilter>();
        Assert.AreEqual(plainMeshes.Length, heavyMeshes.Length);
        for (int i = 0; i < plainMeshes.Length; i++) Assert.AreSame(plainMeshes[i].sharedMesh, heavyMeshes[i].sharedMesh);
        Assert.AreEqual(1, heavyModel.parent.childCount, "No solid strap, box, or other silhouette-changing add-on");
        Near(heavyModel.localScale, plainModel.localScale * 1.65f);
        var a = BoundsInParent(plainModel);
        var b = BoundsInParent(heavyModel);
        Near(b.size, a.size * 1.65f);
        Near(new Vector3(b.center.x, b.min.y, b.center.z), new Vector3(a.center.x, a.min.y, a.center.z));
        Near(heavy.transform.localScale, Vector3.one);
        Assert.AreEqual(plain.GetComponent<Rigidbody>().mass, heavy.GetComponent<Rigidbody>().mass);
        var plainCollider = plain.GetComponent<BoxCollider>();
        var heavyCollider = heavy.GetComponent<BoxCollider>();
        Near(heavyCollider.center, plainCollider.center);
        Near(heavyCollider.size, plainCollider.size);
        Assert.AreEqual(1, heavy.GetComponentsInChildren<Collider>().Length);
        Assert.NotNull(heavy.GetComponent(Runtime("HeavyLiftHandle")), "Two-person lift remains available");
        var cosmetic = BoundsInParent(Cosmetic(contents));
        Near(cosmetic.center, b.center);
        Near(cosmetic.size, b.size);
    }

    [Test]
    public void ReattachingHeavyTellCannotEnlargeItAgainOrAddHandles()
    {
        var bag = Spawn(Contents(0, true));
        var model = bag.transform.Find("Visual/Model");
        Vector3 scale = model.localScale, position = model.localPosition;
        var attach = Runtime("BagTellFx").GetMethod("Attach", Static);
        attach.Invoke(null, new object[] { bag });
        attach.Invoke(null, new object[] { bag });
        Near(model.localScale, scale);
        Near(model.localPosition, position);
        Assert.AreEqual(1, bag.GetComponents(Runtime("HeavyLiftHandle")).Length);
        Assert.AreEqual(1, model.parent.childCount);
    }

    [Test]
    public void EnlargementPreservesRestingPointWithOffsetImportedPivotAndUnrelatedParentMeshes()
    {
        var parent = new GameObject("Offset imported sack test");
        roots.Add(parent);
        parent.transform.SetPositionAndRotation(new Vector3(12, 4, -8), Quaternion.Euler(0, 57, 0));
        var sibling = GameObject.CreatePrimitive(PrimitiveType.Cube);
        sibling.transform.SetParent(parent.transform, false);
        sibling.transform.localPosition = new Vector3(50, 12, 30);
        var model = GameObject.CreatePrimitive(PrimitiveType.Sphere).transform;
        model.SetParent(parent.transform, false);
        model.localPosition = new Vector3(.3f, .4f, -.2f);
        model.localScale = new Vector3(.9f, 1.2f, .8f);
        var before = BoundsInParent(model);
        Runtime("BagTellFx").GetMethod("EnlargeHeavySack", Static).Invoke(null, new object[] { model });
        var after = BoundsInParent(model);
        Near(after.size, before.size * 1.65f);
        Near(new Vector3(after.center.x, after.min.y, after.center.z),
            new Vector3(before.center.x, before.min.y, before.center.z));
    }

    [Test]
    public void LoosePileDoesNotAcquireHeavyCosmeticScaleFromItsIdentifier()
    {
        var plain = BoundsInParent(Cosmetic(Contents(-1, false)));
        var forcedHeavy = BoundsInParent(Cosmetic(Contents(-1, true)));
        Near(forcedHeavy.size, plain.size);
        Near(forcedHeavy.center, plain.center);
    }
}
