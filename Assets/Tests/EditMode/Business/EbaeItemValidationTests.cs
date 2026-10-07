using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// The catalog and ItemInstance live in Assembly-CSharp, which an asmdef test cannot reference.
/// Exercise the public production helper through reflection without moving those frozen types.
/// </summary>
public class EbaeItemValidationTests
{
    Type itemType;
    MethodInfo validate, fieldsMatch;

    [SetUp]
    public void SetUp()
    {
        itemType = Type.GetType("ItemInstance, Assembly-CSharp", true);
        var helper = Type.GetType("EbaeItemValidation, Assembly-CSharp", true);
        validate = helper.GetMethod("IsValid", BindingFlags.Public | BindingFlags.Static,
            null, new[] { itemType, typeof(string).MakeByRefType() }, null);
        fieldsMatch = helper.GetMethod("FieldsMatch", BindingFlags.Public | BindingFlags.Static,
            null, new[] { itemType, itemType }, null);
        Assert.NotNull(validate);
        Assert.NotNull(fieldsMatch);
    }

    object Item(string uid = "test-item", string defId = "wedding_ring", float mass = 0.008f,
        float value = 900f, float condition = 0.9f, string foundAt = "Test St", int day = 2, string note = "rolled")
    {
        var item = Activator.CreateInstance(itemType);
        Set(item, "uid", uid);
        Set(item, "defId", defId);
        Set(item, "massKg", mass);
        Set(item, "value", value);
        Set(item, "condition", condition);
        Set(item, "foundAt", foundAt);
        Set(item, "dayFound", day);
        Set(item, "note", note);
        return item;
    }

    void Set(object item, string field, object value) => itemType.GetField(field).SetValue(item, value);

    bool Valid(object item, out string reason)
    {
        var args = new[] { item, null };
        bool ok = (bool)validate.Invoke(null, args);
        reason = (string)args[1];
        return ok;
    }

    bool Same(object a, object b) => (bool)fieldsMatch.Invoke(null, new[] { a, b });

    [Test]
    public void CanonicalRolledDentedEventAndNightItemsPass()
    {
        object[] items = {
            Item(),
            Item(defId: "rollex_watch", mass: 0.18f, value: 1500f, condition: 1f),
            Item(defId: "cursed_doll", mass: 1f, value: 900f, condition: 0.55f),
            Item(defId: "island_deed", mass: 0.02f, value: 80000f, condition: 1f),
            Item(defId: "wedding_ring", mass: 0.008f, value: 1f, condition: 0.2f, note: "Survived the compactor"),
            Item(defId: "banana_peel", mass: 0.2f, value: 1f, condition: 0.3f),
            Item(defId: "pet_raccoon", mass: 4.5f, value: 180f, condition: 1f),
            Item(defId: "raccoon_king_crown", mass: 0.8f, value: 500f, condition: 1f),
            Item(defId: "big_cleanup_cup", mass: 1.4f, value: 250f, condition: 1f),
            Item(defId: "big_cleanup_ribbon", mass: 0.1f, value: 40f, condition: 1f),
        };
        foreach (var item in items)
            Assert.IsTrue(Valid(item, out var reason), $"{itemType.GetField("defId").GetValue(item)}: {reason}");
    }

    [Test]
    public void RejectsUnknownCatalogTypeBadUidAndBadNumericFields()
    {
        Assert.IsFalse(Valid(null, out _));
        Assert.IsFalse(Valid(Item(defId: "not_in_catalog"), out _));
        Assert.IsFalse(Valid(Item(uid: ""), out _));
        Assert.IsTrue(Valid(Item(uid: new string('x', 60)), out _));
        Assert.IsFalse(Valid(Item(uid: new string('x', 61)), out _));
        Assert.IsTrue(Valid(Item(uid: new string('\u20ac', 20)), out _));
        Assert.IsFalse(Valid(Item(uid: new string('\u20ac', 21)), out _));

        foreach (float x in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            Assert.IsFalse(Valid(Item(mass: x), out _));
            Assert.IsFalse(Valid(Item(value: x), out _));
            Assert.IsFalse(Valid(Item(condition: x), out _));
        }
        Assert.IsFalse(Valid(Item(mass: -0.01f), out _));
        Assert.IsFalse(Valid(Item(mass: 0.62f), out _)); // wedding ring max is 0.01 kg
        Assert.IsFalse(Valid(Item(value: -1f), out _));
        Assert.IsFalse(Valid(Item(value: 1400.01f), out _)); // wedding ring catalog max
        Assert.IsFalse(Valid(Item(condition: -0.01f), out _));
        Assert.IsFalse(Valid(Item(condition: 1.01f), out _));
        Assert.IsTrue(Valid(Item(mass: 0f, value: 0f, condition: 0f), out _));
    }

    [Test]
    public void FullSnapshotMatchDetectsEverySerializedFieldChanging()
    {
        var original = Item();
        Assert.IsFalse(Same(null, null));
        Assert.IsTrue(Same(original, Item()));
        Assert.IsTrue(Same(original, JsonUtility.FromJson(JsonUtility.ToJson(original), itemType)),
            "the exact JSON snapshot sent in a hold must round-trip without a false rejection");
        var noNote = Item(note: null, foundAt: null);
        Assert.IsTrue(Same(noNote, JsonUtility.FromJson(JsonUtility.ToJson(noNote), itemType)),
            "optional null text fields must survive the snapshot round-trip");
        var changes = new (string field, object value)[] {
            ("uid", "another"), ("defId", "rollex_watch"), ("massKg", 0.009f),
            ("value", 901f), ("condition", 0.8f), ("foundAt", "Elsewhere"),
            ("dayFound", 3), ("note", "changed")
        };
        foreach (var change in changes)
        {
            var copy = Item();
            Set(copy, change.field, change.value);
            Assert.IsFalse(Same(original, copy), change.field);
        }
    }
}
