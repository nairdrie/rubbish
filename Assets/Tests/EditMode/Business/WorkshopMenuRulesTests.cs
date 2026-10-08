using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

/// <summary>What replaced the workbench screen. The bench, its menu and its room are gone (and so is "save for building",
/// which went earlier): a display piece is given its find by pressing E at the piece, the sign is repainted and renamed at
/// the sign, decor is listed nowhere but Scrapazon, and printing lives on the laptop (BlueprintTreeTests, CallingsTests).</summary>
public sealed class WorkshopMenuRulesTests
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static Type T(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static Type Maybe(string name) => Type.GetType(name + ", Assembly-CSharp", false);
    static object Field(object value, string name) => value.GetType().GetField(name, Flags).GetValue(value);
    static object Static(string type, string method, params object[] args) => T(type).GetMethod(method, Flags).Invoke(null, args);
    static object[] Pieces => ((IEnumerable)T("WorkshopCatalog").GetProperty("All").GetValue(null)).Cast<object>().ToArray();
    static string Id(object recipe) => (string)Field(recipe, "id");
    static bool IsDecor(object recipe) => (bool)recipe.GetType().GetProperty("isDecor").GetValue(recipe);
    static readonly string[] Displays = { "bottle_cabinet", "rotating_pedestal", "wall_display", "grand_display" };
    static object EverythingInstalled()
    {
        var state = Activator.CreateInstance(T("WorkshopState"));
        state.GetType().GetField("completed").SetValue(state, T("WorkshopCatalog").GetField("DecorBits").GetRawConstantValue());
        return state;
    }
    static bool Command(object state, byte action, string recipe, int color, object display, out string why)
    {
        var args = new object[] { state, action, recipe, color, display, null };
        bool ok = (bool)T("Workshop").GetMethod("ApplyCommand", Flags).Invoke(null, args); why = (string)args[5]; return ok;
    }

    [Test]
    public void TheWorkbenchItsMenusAndTheSaveForBuildingActionAreGoneFromTheGameTheStationAndTheWire()
    {
        // the bench menu, the uGUI tree screen, the room behind the east wall and the bench itself
        foreach (string gone in new[] { "WorkshopMenu", "CallingsMenu", "WorkshopAnnex", "WorkshopBench" })
            Assert.IsNull(Maybe(gone), gone + " was removed with the workshop room: printing lives on the laptop");
        // what a player uses instead
        foreach (string here in new[] { "SolidWasteApp", "SolidWasteTree", "WorkshopDisplayPicker", "WorkshopSignMenu", "DiskFoundPopup" }) Assert.NotNull(T(here));
        Assert.IsNull(T("SortStation").GetMethod("SaveForBuilding", Flags));
        var workshop = T("Workshop");
        foreach (string gone in new[] { "DonateHeld", "CanDonate", "MayDonate", "Start", "Cancel", "DebugAddMaterials", "BuildSculpture", "ActionBuildSculpture" })
            Assert.IsEmpty(workshop.GetMember(gone, Flags), "Workshop." + gone);
        var registry = T("BusinessRegistry");
        foreach (string gone in new[] { "WorkshopDonate", "WorkshopDonateRpc", "HostWorkshopDonate" }) Assert.IsEmpty(registry.GetMember(gone, Flags), "BusinessRegistry." + gone);
        Assert.IsNull(T("HandsNet").GetMethod("TryWorkshopHeld", Flags), "the donate hand-check went with it");
        var state = T("WorkshopState");
        foreach (string gone in new[] { "Donate", "Start", "Cancel", "Allocate", "BankCovers", "Progress", "MaxBankGrams", "BuildSculpture" }) Assert.IsEmpty(state.GetMember(gone, Flags), "WorkshopState." + gone);
        foreach (string gone in new[] { "BuildBench" }) Assert.IsEmpty(T("WorkshopModels").GetMember(gone, Flags), "WorkshopModels." + gone);
        foreach (string gone in new[] { "WorkbenchPosition", "BenchLocal", "AssemblyLocal" }) Assert.IsEmpty(T("WorkshopWorld").GetMember(gone, Flags), "WorkshopWorld." + gone);
        Assert.IsEmpty(T("WorkshopModel").GetMember("SetProgress", Flags), "a piece is installed whole: the half-built project look went with the bank");
        Assert.IsEmpty(T("HutchWorld").GetMember("AnnexLocal", Flags), "the hutch has no indoor slot any more");
    }

    [Test]
    public void NeitherControlHelpPageMentionsBuildMaterials()
    {
        var binds = T("Keybinds");
        foreach (string property in new[] { "Groups", "ControllerGroups" })
            foreach (object group in (IEnumerable)binds.GetProperty(property).GetValue(null))
                foreach (object bind in (IEnumerable)group.GetType().GetField("binds").GetValue(group))
                {
                    string action = (string)bind.GetType().GetField("action").GetValue(bind);
                    StringAssert.DoesNotContain("BUILD MATERIALS", action.ToUpperInvariant(), property + " still lists the removed action");
                    StringAssert.DoesNotContain("SAVE HELD", action.ToUpperInvariant());
                    StringAssert.DoesNotContain("WORKBENCH", action.ToUpperInvariant(), property + " still sends players to the bench");
                }
    }

    [Test]
    public void OnlyTheFourDisplayPiecesTakeAFindAndItIsChosenAtThePiece()
    {
        bool Display(string id) => (bool)T("WorkshopModels").GetMethod("IsDisplay").Invoke(null, new object[] { id });
        foreach (string id in Displays) Assert.IsTrue(Display(id), id);
        foreach (string id in new[] { "business_sign", "break_sofa", "spring_launcher", "junk_sculpture", "monument", "unknown", "", null }) Assert.IsFalse(Display(id), id);
        // the catalog and the models agree, piece by piece
        foreach (var piece in Pieces) Assert.AreEqual((bool)Field(piece, "isDisplay"), Display(Id(piece)), Id(piece));
        CollectionAssert.AreEqual(Displays, Pieces.Where(p => (bool)Field(p, "isDisplay")).Select(Id));
        // the piece itself is what you walk up to (E), and the picker is a screen of its own
        var interactable = T("IInteractable");
        Assert.IsTrue(interactable.IsAssignableFrom(T("WorkshopDisplay")), "press E at the display"); Assert.IsTrue(T("UiScreen").IsAssignableFrom(T("WorkshopDisplayPicker")));
        Assert.NotNull(T("WorkshopDisplayPicker").GetMethod("Show", Flags)); Assert.NotNull(T("WorkshopDisplay").GetMethod("Attach", Flags));

        // the host's rule behind the picker: a find goes on a display piece the business owns, and on nothing else
        byte pinDisplay = (byte)T("Workshop").GetField("ActionPinDisplay", Flags).GetRawConstantValue(); Assert.AreEqual(3, pinDisplay, "wire value");
        var valuable = ((IEnumerable)T("ItemCatalog").GetProperty("All").GetValue(null)).Cast<object>().First(d => Field(d, "category").ToString() == "Valuable");
        var gnome = Activator.CreateInstance(T("ItemInstance"));
        foreach (var (field, value) in new (string, object)[] { ("uid", "menu-find"), ("defId", Field(valuable, "id")), ("massKg", Field(valuable, "minMass")), ("condition", 1f) })
            gnome.GetType().GetField(field).SetValue(gnome, value);
        foreach (var piece in Pieces.Where(IsDecor)) {
            string id = Id(piece); var state = EverythingInstalled(); string before = JsonUtility.ToJson(state);
            var pin = Static("WorkshopDisplayData", "From", id, gnome);
            bool ok = Command(state, pinDisplay, id, 0, pin, out string why);
            Assert.AreEqual(Displays.Contains(id), ok, id); Assert.IsNotEmpty(why, id);
            if (!ok) { Assert.AreEqual(before, JsonUtility.ToJson(state), id + " shows nothing"); Assert.IsFalse(Command(state, pinDisplay, id, 0, null, out _), id + " has nothing to clear either"); continue; }
            Assert.AreEqual("menu-find", Field(state.GetType().GetMethod("Display").Invoke(state, new object[] { id }), "uid"));
            Assert.IsTrue(Command(state, pinDisplay, id, 0, null, out why), "clearing " + id); Assert.IsNull(state.GetType().GetMethod("Display").Invoke(state, new object[] { id }));
            Assert.AreEqual(before, JsonUtility.ToJson(state), "pinned and cleared again: exactly as it was");
        }
        // a display you have not bought shows nothing
        var empty = Activator.CreateInstance(T("WorkshopState"));
        Assert.IsFalse(Command(empty, pinDisplay, "bottle_cabinet", 0, Static("WorkshopDisplayData", "From", "bottle_cabinet", gnome), out string notOwned)); Assert.IsNotEmpty(notOwned);
    }

    [Test]
    public void TheSignIsRepaintedAtTheSignInOneOfEightNamedColours()
    {
        int colours = (int)T("WorkshopCatalog").GetField("ColorCount").GetRawConstantValue(); Assert.AreEqual(8, colours);
        var names = Enumerable.Range(0, colours).Select(i => (string)Static("WorkshopWorld", "SignColorName", i)).ToArray();
        foreach (string name in names) Assert.IsNotEmpty(name);
        Assert.AreEqual(colours, names.Distinct().Count(), "eight different names: a colour is never told apart by colour alone");
        var paints = Enumerable.Range(0, colours).Select(i => (Color)Static("WorkshopWorld", "SignColor", i)).ToArray();
        for (int a = 0; a < colours; a++) for (int b = a + 1; b < colours; b++)
            Assert.Greater(Mathf.Abs(paints[a].r - paints[b].r) + Mathf.Abs(paints[a].g - paints[b].g) + Mathf.Abs(paints[a].b - paints[b].b), 0.1f, names[a] + " and " + names[b] + " are the same paint");
        // the sign is something you walk up to, and its menu is a screen of its own
        Assert.IsTrue(T("IInteractable").IsAssignableFrom(T("WorkshopSign"))); Assert.IsTrue(T("UiScreen").IsAssignableFrom(T("WorkshopSignMenu")));
        Assert.IsTrue((bool)Static("WorkshopWorld", "StreetRecipe", "business_sign"), "it stands out on the street");

        byte customize = (byte)T("Workshop").GetField("ActionCustomize", Flags).GetRawConstantValue(); Assert.AreEqual(2, customize, "wire value");
        Assert.IsTrue((bool)Static("Workshop", "AllowedAway", customize, null), "so the owner repaints it from the street, not from inside the shop");
        // no sign, no paint
        var bare = Activator.CreateInstance(T("WorkshopState")); string untouched = JsonUtility.ToJson(bare);
        Assert.IsFalse(Command(bare, customize, "", 3, null, out string noSign)); Assert.IsNotEmpty(noSign); Assert.AreEqual(untouched, JsonUtility.ToJson(bare));
        // with the sign: every one of the eight, and nothing outside them
        var state = EverythingInstalled();
        for (int colour = 0; colour < colours; colour++) {
            Assert.IsTrue(Command(state, customize, "", colour, null, out string painted), names[colour]); Assert.IsNotEmpty(painted);
            Assert.AreEqual(colour, Field(state, "colorIndex")); Assert.IsTrue((bool)state.GetType().GetMethod("Valid").Invoke(state, null));
        }
        string before = JsonUtility.ToJson(state);
        foreach (int bad in new[] { -1, colours, 99 }) { Assert.IsFalse(Command(state, customize, "", bad, null, out _), "colour " + bad); Assert.AreEqual(before, JsonUtility.ToJson(state)); }
    }

    [Test]
    public void DecorIsListedNowhereButScrapazon()
    {
        var decor = Pieces.Where(IsDecor).Select(Id).ToArray(); Assert.AreEqual(11, decor.Length);
        // not a blueprint, not a hexagon on the print app's sheet, not the Monument
        var hexagons = ((IEnumerable<string>)T("SolidWasteLayout").GetProperty("NodeIds").GetValue(null)).ToArray();
        string monument = (string)T("MonumentCatalog").GetField("NodeId").GetRawConstantValue();
        foreach (var piece in Pieces) {
            string id = Id(piece);
            Assert.IsNull(Static("CallingCatalog", "Get", id), id + " is not a blueprint"); CollectionAssert.DoesNotContain(hexagons, id, id + " has no hexagon in SolidWaste 3D");
            Assert.AreNotEqual(monument, id);
            Assert.IsNull(Static("CallingRecipes", "For", id, 1), id + " has no parts recipe: decor is bought, not printed");
        }
        // the eleven pieces the world can show are exactly the eleven the catalog sells
        CollectionAssert.AreEquivalent(decor, (string[])T("WorkshopModels").GetField("RecipeIds").GetValue(null));
        // and Scrapazon has every one of them, in its Decor aisle, saying where it really goes
        foreach (var piece in Pieces.Where(IsDecor)) {
            string id = Id(piece); string product = (string)piece.GetType().GetProperty("upgradeId").GetValue(piece);
            var item = Static("ScrapazonCatalog", "Get", product); Assert.NotNull(item, id + " must be on Scrapazon");
            Assert.IsTrue((bool)item.GetType().GetMethod("HasTag").Invoke(item, new object[] { "decor" }), id);
            string effect = (string)item.GetType().GetMethod("EffectFor").Invoke(item, new object[] { 1 });
            bool street = (bool)Static("WorkshopWorld", "StreetRecipe", id);
            if (street) { StringAssert.Contains("depot", effect, id + " stands outside"); StringAssert.DoesNotContain("in your garage", effect, id + " stands outside"); }
            else StringAssert.Contains("garage", effect, id + " stands inside");
            StringAssert.DoesNotContain("workshop", effect.ToLowerInvariant(), id + ": there is no workshop room any more"); StringAssert.DoesNotContain("bench", effect.ToLowerInvariant(), id);
        }
    }
}
