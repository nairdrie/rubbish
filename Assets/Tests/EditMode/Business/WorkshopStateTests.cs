using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public class WorkshopStateTests
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static Type T(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object New(string name) => Activator.CreateInstance(T(name));
    static object Get(object value, string field) => value.GetType().GetField(field, Flags).GetValue(value);
    static void Set(object value, string field, object data) => value.GetType().GetField(field, Flags).SetValue(value, data);
    static object Call(object value, string method, params object[] args) => value.GetType().GetMethod(method, Flags).Invoke(value, args);
    static object Static(string type, string method, params object[] args) => T(type).GetMethod(method, Flags).Invoke(null, args);
    static object Material(int index) => Enum.ToObject(T("WorkshopMaterial"), index);
    static IEnumerable<object> Recipes => ((IEnumerable)T("WorkshopCatalog").GetProperty("All").GetValue(null)).Cast<object>();
    static bool Donate(object state, int material, int grams) => (bool)Call(state, "Donate", Material(material), grams, null);
    static bool Start(object state, string id) => (bool)Call(state, "Start", id, null);
    static int Amount(object state, string field, int material) => (int)Call(Get(state, field), "Get", Material(material));
    static bool Completed(object state, string id) => (bool)Call(state, "Completed", id);
    static bool Valid(object state) => (bool)Call(state, "Valid");

    [Test]
    public void RecipesHaveStableUniqueBitsBoundedCostsAndExactlyFourDisplayChoices()
    {
        var expected = new[] { "bottle_cabinet", "rotating_pedestal", "wall_display", "grand_display", "business_sign", "junk_sculpture",
            "sculpture_tower", "sculpture_monument", "bag_hoop", "sorting_chute", "break_sofa", "scrap_radio", "spring_launcher", "doorbell" };
        var recipes = Recipes.ToArray(); Assert.AreEqual(14, recipes.Length);
        for (int i = 0; i < recipes.Length; i++) {
            Assert.AreEqual(expected[i], Get(recipes[i], "id")); Assert.AreEqual(i, Get(recipes[i], "index"));
            int total = (int)Get(recipes[i], "cost").GetType().GetProperty("TotalGrams").GetValue(Get(recipes[i], "cost"));
            Assert.That(total, Is.InRange(3000, 45000));
        }
        Assert.AreEqual(4, recipes.Count(r => (bool)Get(r, "isDisplay")));
    }

    [Test]
    public void EveryRecipeCompletesWithExactCostWithoutCashTimersOrExtraMaterials()
    {
        foreach (var recipe in Recipes) {
            var state = New("WorkshopState"); string id = (string)Get(recipe, "id");
            Assert.IsTrue(Start(state, id));
            for (int m = 0; m < 4; m++) {
                int cost = (int)Call(recipe, "Cost", Material(m));
                if (cost > 0) Assert.IsTrue(Donate(state, m, cost));
            }
            Assert.IsTrue(Completed(state, id), id); Assert.AreEqual("", Get(state, "activeRecipe"));
            for (int m = 0; m < 4; m++) { Assert.AreEqual(0, Amount(state, "bank", m)); Assert.AreEqual(0, Amount(state, "allocated", m)); }
            Assert.IsTrue(Valid(state)); Assert.IsFalse(Start(state, id), "Completed recipes cannot charge materials again.");
        }
    }

    [Test]
    public void BankBeforeStartingAllocatesImmediatelyAndLeavesExactSurplus()
    {
        var state = New("WorkshopState");
        foreach (int m in new[] { 0, 1, 2, 3 }) Assert.IsTrue(Donate(state, m, 2000));
        Assert.IsTrue(Start(state, "bottle_cabinet")); Assert.IsTrue(Completed(state, "bottle_cabinet"));
        CollectionAssert.AreEqual(new[] { 1000, 1500, 1000, 1500 }, Enumerable.Range(0, 4).Select(m => Amount(state, "bank", m)).ToArray());
        Assert.IsTrue(Valid(state));
    }

    [Test]
    public void PartialProjectCancellationRefundsExactlyAndCannotMultiplyMaterials()
    {
        var state = New("WorkshopState"); Assert.IsTrue(Donate(state, 0, 2000));
        for (int attempt = 0; attempt < 30; attempt++) {
            Assert.IsTrue(Start(state, "bottle_cabinet"));
            Assert.That((float)state.GetType().GetProperty("Progress").GetValue(state), Is.EqualTo(1f / 3f).Within(0.00001f));
            Assert.IsFalse(Start(state, "doorbell")); Assert.IsTrue((bool)Call(state, "Cancel"));
            Assert.IsFalse((bool)Call(state, "Cancel")); Assert.AreEqual(2000, Amount(state, "bank", 0)); Assert.IsTrue(Valid(state));
        }
    }

    [Test]
    public void CapacityIncludesAllocatedGramsAndRejectsWholeDonationWithoutLoss()
    {
        var state = New("WorkshopState"); int max = (int)T("WorkshopState").GetField("MaxBankGrams").GetRawConstantValue();
        Assert.IsTrue(Donate(state, 0, max)); Assert.IsTrue(Start(state, "bottle_cabinet"));
        string before = JsonUtility.ToJson(state);
        Assert.IsFalse(Donate(state, 0, 1)); Assert.AreEqual(before, JsonUtility.ToJson(state));
        Assert.IsFalse(Donate(state, 0, int.MaxValue)); Assert.IsFalse(Donate(state, 4, 10)); Assert.IsFalse(Donate(state, 0, -1));
        Assert.IsTrue((bool)Call(state, "Cancel")); Assert.AreEqual(max, Amount(state, "bank", 0)); Assert.IsTrue(Valid(state));
    }

    [Test]
    public void ConversionAcceptsOnlyRecyclableDefinitionsAndFinitePositiveMass()
    {
        foreach (var category in Enum.GetValues(T("ItemCategory"))) {
            bool good = (bool)Static("WorkshopCatalog", "TryMaterial", category, Material(0));
            Assert.AreEqual(new[] { "Metal", "Plastic", "Glass", "Paper" }.Contains(category.ToString()), good);
        }
        var defs = ((IEnumerable)T("ItemCatalog").GetProperty("All").GetValue(null)).Cast<object>();
        var def = defs.First(d => Get(d, "category").ToString() == "Metal");
        var item = New("ItemInstance"); Set(item, "defId", Get(def, "id"));
        foreach (float mass in new[] { float.NaN, float.PositiveInfinity, -1f, 0f, 1000f }) {
            Set(item, "massKg", mass); Assert.IsFalse((bool)Static("WorkshopCatalog", "TryGrams", item, Material(0), 0));
        }
        Set(item, "massKg", 1.234f); var args = new[] { item, Material(0), (object)0 };
        Assert.IsTrue((bool)T("WorkshopCatalog").GetMethod("TryGrams").Invoke(null, args)); Assert.AreEqual(1234, args[2]);
        Set(item, "defId", "does_not_exist"); Assert.IsFalse((bool)Static("WorkshopCatalog", "TryGrams", item, Material(0), 0));
    }

    [Test]
    public void InvalidSaveStateCannotUploadUnknownRecipesNegativeBanksOrFreeCompletion()
    {
        foreach (string json in new[] {
            "{\"activeRecipe\":\"made_up\",\"displays\":[]}",
            "{\"completed\":32768,\"displays\":[]}",
            "{\"bank\":{\"metal\":-1},\"displays\":[]}",
            "{\"bank\":{\"metal\":250001},\"displays\":[]}",
            "{\"allocated\":{\"glass\":5},\"displays\":[]}",
            "{\"activeRecipe\":\"bottle_cabinet\",\"allocated\":{\"metal\":1000,\"plastic\":500,\"glass\":1000,\"paper\":500},\"displays\":[]}",
            "{\"colorIndex\":8,\"displays\":[]}", "not json" })
            Assert.IsNull(Static("BusinessRegistry", "ParseWorkshop", json), json);
    }

    [Test]
    public void StateRoundTripRetainsBankProgressAndCompletedWorkAndCloneIsIndependent()
    {
        var state = New("WorkshopState"); Set(state, "completed", (ushort)(1 << 4)); Set(state, "colorIndex", 5);
        Assert.IsTrue(Donate(state, 2, 2222)); Assert.IsTrue(Start(state, "grand_display"));
        string json = JsonUtility.ToJson(state); var restored = Static("BusinessRegistry", "ParseWorkshop", json);
        Assert.NotNull(restored); Assert.AreEqual(json, JsonUtility.ToJson(restored));
        var copy = Call(state, "Clone"); Assert.IsTrue((bool)Call(copy, "Cancel"));
        Assert.AreEqual(json, JsonUtility.ToJson(state)); Assert.AreNotEqual(json, JsonUtility.ToJson(copy));
        Assert.IsTrue(Completed(restored, "business_sign"));
    }

    [Test]
    public void DisplayPinsMoveBetweenSlotsNeverCreateValuableInventoryOrMonetaryCopies()
    {
        var state = New("WorkshopState"); Set(state, "completed", (ushort)15);
        var defs = ((IEnumerable)T("ItemCatalog").GetProperty("All").GetValue(null)).Cast<object>();
        var def = defs.First(d => Get(d, "category").ToString() == "Valuable");
        var item = New("ItemInstance"); Set(item, "uid", "test_owned"); Set(item, "defId", Get(def, "id")); Set(item, "massKg", 1f); Set(item, "value", 999f);
        var pin = Static("WorkshopDisplayData", "From", "bottle_cabinet", item);
        Assert.IsTrue((bool)Call(state, "Pin", "bottle_cabinet", pin));
        var preview = Call(state, "Display", "bottle_cabinet"); Assert.AreEqual(0f, Get(preview, "value")); Assert.AreEqual(999f, Get(item, "value"));
        var moved = Static("WorkshopDisplayData", "From", "wall_display", item);
        Assert.IsTrue((bool)Call(state, "Pin", "wall_display", moved)); Assert.IsNull(Call(state, "Display", "bottle_cabinet"));
        Assert.AreEqual(1, ((IList)Get(state, "displays")).Count); Assert.IsTrue(Valid(state));
        var clone = Call(state, "Clone"); Set(((IList)Get(clone, "displays"))[0], "uid", "changed_copy");
        Assert.AreEqual("test_owned", Get(Call(state, "Display", "wall_display"), "uid"));
    }

    [Test]
    public void BusinessSaveDtoIncludesWorkshopAndOldSaveDefaultsRemainEmpty()
    {
        var dto = T("GameSession").GetNestedType("SaveData", BindingFlags.NonPublic);
        Assert.AreEqual(T("WorkshopState"), dto.GetField("workshop").FieldType);
        var legacy = JsonUtility.FromJson("{\"day\":3,\"cash\":10}", dto);
        // JsonUtility may instantiate omitted nested objects. Loading either that default or null must clear a
        // previously selected business's workshop rather than leak its progress into this legacy business.
        var savedField = T("GameSession").GetField("workshop", Flags);
        object previous = savedField.GetValue(null);
        try {
            var priorBusiness = New("WorkshopState"); Assert.IsTrue(Donate(priorBusiness, 0, 3210));
            Static("GameSession", "LoadWorkshop", priorBusiness);
            Static("GameSession", "LoadWorkshop", dto.GetField("workshop").GetValue(legacy));
            var empty = savedField.GetValue(null);
            Assert.IsTrue(Valid(empty)); Assert.AreEqual((ushort)0, Get(empty, "completed"));
            Assert.IsTrue(string.IsNullOrEmpty((string)Get(empty, "activeRecipe")));
            for (int m = 0; m < 4; m++) { Assert.AreEqual(0, Amount(empty, "bank", m)); Assert.AreEqual(0, Amount(empty, "allocated", m)); }
        }
        finally { savedField.SetValue(null, previous); } // This test never saves, resets profiles or raises game events.
        var state = New("WorkshopState"); Assert.IsTrue(Donate(state, 3, 4231));
        dto.GetField("workshop").SetValue(legacy, state);
        var restored = JsonUtility.FromJson(JsonUtility.ToJson(legacy), dto);
        Assert.AreEqual(4231, Amount(dto.GetField("workshop").GetValue(restored), "bank", 3));
    }

    [Test]
    public void GuestSaveUploadIsOwnAcceptedBusinessOnlyAndCannotReplayOverLiveProgress()
    {
        var business = New("BusinessInfo");
        var idType = business.GetType().GetField("id").FieldType;
        Set(business, "id", Activator.CreateInstance(idType, new object[] { "workshop_test_business" }));
        Set(business, "ownerClientId", 42UL);
        Assert.IsTrue((bool)Static("BusinessRegistry", "MayInitializeWorkshop", business, 42UL, "workshop_test_business", false));
        Assert.IsFalse((bool)Static("BusinessRegistry", "MayInitializeWorkshop", business, 42UL, "workshop_test_business", true), "Duplicate/replayed owner upload must not replace live state.");
        Assert.IsFalse((bool)Static("BusinessRegistry", "MayInitializeWorkshop", business, 43UL, "workshop_test_business", false), "A worker cannot upload their employer's save.");
        Assert.IsFalse((bool)Static("BusinessRegistry", "MayInitializeWorkshop", business, 42UL, "different_business", false));
        Set(business, "flags", (byte)1);
        Assert.IsFalse((bool)Static("BusinessRegistry", "MayInitializeWorkshop", business, 42UL, "workshop_test_business", false), "Host imports directly, never from a guest packet.");
    }
}
