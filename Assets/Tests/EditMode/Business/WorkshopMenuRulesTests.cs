using System;
using NUnit.Framework;

public sealed class WorkshopMenuRulesTests
{
    static Type Menu => Type.GetType("WorkshopMenu, Assembly-CSharp", true);
    static Type Category => Type.GetType("ItemCategory, Assembly-CSharp", true);
    static bool Allows(string category) => (bool)Menu.GetMethod("IsBuildingCategory").Invoke(null,
        new[] { Enum.Parse(Category, category) });

    [TestCase("Metal")]
    [TestCase("Plastic")]
    [TestCase("Glass")]
    [TestCase("Paper")]
    public void OnlyExplicitRecyclablesOfferBuildMaterials(string category) => Assert.IsTrue(Allows(category));

    [TestCase("Valuable")]
    [TestCase("Hazard")]
    [TestCase("Junk")]
    public void TreasureHazardsAndJunkCannotBeSavedForBuilding(string category) => Assert.IsFalse(Allows(category));

    [Test]
    public void NoHeldPieceCannotCreateABuildingDonation()
    {
        Assert.IsFalse((bool)Menu.GetMethod("CanSaveHeld").Invoke(null, new object[] { null }));
        Assert.IsFalse((bool)Menu.GetMethod("SaveHeldForBuilding").Invoke(null, new object[] { null }));
    }

    [Test]
    public void OnlyTheFourActualDisplayRecipesOpenTheInventoryPicker()
    {
        bool Display(string id) => (bool)Menu.GetMethod("IsDisplayRecipe").Invoke(null, new object[] { id });
        foreach (string id in new[] { "bottle_cabinet", "rotating_pedestal", "wall_display", "grand_display" }) Assert.IsTrue(Display(id));
        foreach (string id in new[] { "business_sign", "break_sofa", "spring_launcher", "unknown", "", null }) Assert.IsFalse(Display(id));
    }

    [Test]
    public void BothControlHelpPagesExplicitlyLabelBuildMaterials()
    {
        var binds = Type.GetType("Keybinds, Assembly-CSharp", true);
        foreach (string property in new[] { "Groups", "ControllerGroups" })
        {
            bool found = false;
            foreach (object group in (System.Collections.IEnumerable)binds.GetProperty(property).GetValue(null))
                foreach (object bind in (System.Collections.IEnumerable)group.GetType().GetField("binds").GetValue(group))
                {
                    string action = (string)bind.GetType().GetField("action").GetValue(bind);
                    if (!action.Contains("BUILD MATERIALS")) continue;
                    Assert.AreEqual(property == "Groups" ? "F" : "Y", bind.GetType().GetField("keys").GetValue(bind));
                    found = true;
                }
            Assert.IsTrue(found, property + " must show the alternative explicitly");
        }
    }
}
