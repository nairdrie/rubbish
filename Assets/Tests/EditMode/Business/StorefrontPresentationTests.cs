using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>UI-only contracts. Runtime UI lives in Assembly-CSharp, accessed without a save/session.</summary>
public class StorefrontPresentationTests
{
    const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object Field(object owner, string name) => owner.GetType().GetField(name, Instance).GetValue(owner);
    static object Product(string kind = "Upgrade")
    {
        var type = Runtime("ScrapazonItem");
        var item = Activator.CreateInstance(type);
        type.GetField("id").SetValue(item, "presentation_test");
        type.GetField("title").SetValue(item, "Test upgrade");
        type.GetField("brand").SetValue(item, "Test brand");
        type.GetField("kind").SetValue(item, Enum.Parse(Runtime("ShopItemKind"), kind));
        return item;
    }

    [Test]
    public void CollapsedCardsKeepPriceEffectRequirementsAndBuyOutsideDetails()
    {
        var type = Runtime("ScrapazonApp");
        var app = Activator.CreateInstance(type, Instance, null, new object[] { null, new VisualElement() }, null);
        var cardView = type.GetMethod("BuildCard", Instance).Invoke(app, new[] { Product(), (object)0 });
        var card = (VisualElement)Field(cardView, "card");
        var details = (VisualElement)Field(cardView, "details");
        Assert.AreEqual(DisplayStyle.None, details.style.display.value);
        foreach (var name in new[] { "priceRow", "effect", "lockChip", "buy" })
        {
            var element = (VisualElement)Field(cardView, name);
            Assert.IsTrue(card.Contains(element), name + " must remain on the card");
            Assert.IsFalse(details.Contains(element), name + " must never be hidden in details");
        }
        Assert.IsTrue(details.Contains((VisualElement)Field(cardView, "blurb")));
        type.GetMethod("SetDetails", Static).Invoke(null, new[] { cardView, (object)true });
        Assert.AreEqual(DisplayStyle.Flex, details.style.display.value);
        StringAssert.Contains("Hide", ((Label)Field(cardView, "detailsToggle")).text);
        type.GetMethod("SetDetails", Static).Invoke(null, new[] { cardView, (object)false });
        Assert.AreEqual(DisplayStyle.None, details.style.display.value);
    }

    [Test]
    public void RequirementSummaryIncludesRankAndEveryPrerequisiteAtTheRequestedLevel()
    {
        var item = Product();
        item.GetType().GetField("rankRequired").SetValue(item, new[] { 2, 7 });
        item.GetType().GetField("prereqs").SetValue(item,
            new[] { new[] { "truck_capacity:1" }, new[] { "truck_capacity:2", "key_to_the_city" } });
        string text = (string)Runtime("ScrapazonApp").GetMethod("RequirementDescription", Static).Invoke(null, new[] { item, (object)2 });
        StringAssert.Contains("Rank 7", text);
        StringAssert.Contains("Lv 2", text);
        StringAssert.Contains("Key to the City", text);
    }

    [TestCase("Upgrade", "Installs automatically")]
    [TestCase("Consumable", "Added to your supplies")]
    public void DeliveryCopyExplainsImmediateEffectWithoutAnyPackaging(string kind, string expectation)
    {
        string text = (string)Runtime("ScrapazonApp").GetMethod("DeliveryDescription", Static).Invoke(null, new[] { Product(kind) });
        StringAssert.Contains(expectation, text);
        StringAssert.DoesNotContain("box", text, "the delivery box is gone: purchases simply take effect");
    }

    [Test]
    public void TeasersNeverPromiseDelivery()
    {
        string text = (string)Runtime("ScrapazonApp").GetMethod("DeliveryDescription", Static).Invoke(null, new[] { Product("Teaser") });
        StringAssert.Contains("Not available", text);
    }

    [Test]
    public void PhotoRequestsDeduplicateByDefinitionAndDisposeClearsPendingWork()
    {
        var type = Runtime("EbaeItemThumbnails");
        var photos = Activator.CreateInstance(type);
        try
        {
            var request = type.GetMethod("Request");
            var a = request.Invoke(photos, new object[] { "banana_peel", "Banana Peel" });
            var b = request.Invoke(photos, new object[] { "banana_peel", "Another banana" });
            Assert.AreNotSame(a, b); // each card has its own UI node, but one render job
            Assert.AreEqual(1, ((ICollection)Field(photos, "queue")).Count);
            request.Invoke(photos, new object[] { "missing_presentation_item", "Unknown" });
            Assert.AreEqual(1, ((ICollection)Field(photos, "queue")).Count);
        }
        finally { ((IDisposable)photos).Dispose(); }
        Assert.AreEqual(0, ((ICollection)Field(photos, "queue")).Count);
        Assert.AreEqual(0, ((IDictionary)Field(photos, "waiting")).Count);
    }

    [Test]
    public void PhotoFramingFitsEveryCornerWithPadding()
    {
        var frame = Runtime("EbaeItemThumbnails").GetMethod("FrameSize");
        foreach (var size in new[] { Vector3.one, new Vector3(0.12f, 3f, 0.12f), new Vector3(4f, 0.1f, 2f) })
            Assert.Greater((float)frame.Invoke(null, new object[] { size }), size.magnitude * 0.5f);
    }

    [Test]
    public void PurchasesNoLongerSpawnADeliveryBox()
    {
        Assert.IsNull(System.Type.GetType("ScrapazonBox, Assembly-CSharp", false), "the smiling delivery box was removed");
        Assert.IsNull(Runtime("ScrapazonArt").GetMethod("Box", Static), "and so was its in-laptop drop animation");
    }
}
