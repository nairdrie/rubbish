using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// SolidWaste 3D's honeycomb (Workshop/SolidWasteTree.cs): where every blueprint and Monument tier sits on the sheet
/// (SolidWasteLayout, pure geometry) and what each hexagon shows for a business's state (SolidWasteTree, built here
/// without a panel). The rule the layout exists for: every prerequisite inside a kit is a NEIGHBOURING cell, so a link is
/// always a shared edge. The tree's own rules (who needs what, in which order) are CallingsTests.
/// Game code is reached by reflection (this assembly cannot reference Assembly-CSharp), like CallingsTests.
/// </summary>
public class BlueprintTreeTests
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static Type T(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object New(string name) => Activator.CreateInstance(T(name));
    static object Val(object value, string member)
    {
        var type = value as Type ?? value.GetType(); object target = value is Type ? null : value;
        var field = type.GetField(member, Flags); if (field != null) return field.GetValue(target);
        var property = type.GetProperty(member, Flags); Assert.NotNull(property, type.Name + "." + member);
        return property.GetValue(target);
    }
    static void Set(object value, string field, object data) => value.GetType().GetField(field, Flags).SetValue(value, data);
    static object Call(object value, string method, params object[] args) => value.GetType().GetMethod(method, Flags).Invoke(value, args);
    static object Static(string type, string method, params object[] args) => T(type).GetMethod(method, Flags).Invoke(null, args);
    static float F(string name) => (float)T("SolidWasteLayout").GetField(name, Flags).GetRawConstantValue();

    static object[] Nodes => ((IEnumerable)Val(T("CallingCatalog"), "All")).Cast<object>().ToArray();
    static string Id(object node) => (string)Val(node, "id");
    static string Kit(object node) => Val(node, "calling").ToString();
    static int Tiers => (int)T("MonumentCatalog").GetField("MaxLevel").GetRawConstantValue();
    static object CellOf(string id)
    {
        var args = new object[] { id, null };
        Assert.IsTrue((bool)T("SolidWasteLayout").GetMethod("TryCell").Invoke(null, args), id + " has no cell on the sheet: the app would silently leave it out");
        return args[1];
    }
    static object TierCell(int tier) => Static("SolidWasteLayout", "MonumentCell", tier);
    static (int q, int r2) At(object cell) => ((int)Val(cell, "q"), (int)Val(cell, "r2"));
    static bool IsValid(object cell) => (bool)Val(cell, "Valid");
    static bool Adjacent(object a, object b) => (bool)Call(a, "Adjacent", b);
    static Vector2 Center(object cell) => (Vector2)Static("SolidWasteLayout", "Center", cell);
    static bool InHex(Vector2 p, Vector2 c, float r) => (bool)Static("SolidWasteLayout", "InHex", p, c, r);
    static object NewCell(int q, int r2) => Activator.CreateInstance(T("SolidWasteLayout+Cell"), q, r2);
    /// <summary>Every cell in use: the blueprints by node id, the Monument tiers as "monument:N".</summary>
    static Dictionary<string, object> AllCells()
    {
        var cells = new Dictionary<string, object>();
        foreach (var node in Nodes) cells[Id(node)] = CellOf(Id(node));
        for (int tier = 1; tier <= Tiers; tier++) cells["monument:" + tier] = TierCell(tier);
        return cells;
    }

    // ------------------------------------------------------------------ the lattice

    [Test]
    public void EveryBlueprintAndEveryMonumentTierHasItsOwnValidCell()
    {
        var cells = AllCells();
        Assert.AreEqual(Nodes.Length + 10, cells.Count);
        foreach (var kv in cells) Assert.IsTrue(IsValid(kv.Value), kv.Key + " sits at " + At(kv.Value) + ": column + half-rows must be even (a real cell of the honeycomb)");
        foreach (var group in cells.GroupBy(kv => At(kv.Value))) Assert.AreEqual(1, group.Count(), "two hexagons in cell " + group.Key + ": " + string.Join(", ", group.Select(kv => kv.Key)));
        // no cell is authored for something that is not on the tree
        CollectionAssert.AreEquivalent(Nodes.Select(Id), ((IEnumerable<string>)Val(T("SolidWasteLayout"), "NodeIds")).ToArray(), "every authored cell is a blueprint, and every blueprint has a cell");
        foreach (string unknown in new[] { "monument", "made_up", "bottle_cabinet", "", null }) {
            var args = new object[] { unknown, null }; Assert.IsFalse((bool)T("SolidWasteLayout").GetMethod("TryCell").Invoke(null, args), "'" + unknown + "'");
        }
        // what a valid cell is, and what its neighbours are: the six that share an edge, nothing else
        Assert.IsTrue(IsValid(NewCell(0, 0))); Assert.IsTrue(IsValid(NewCell(3, 1))); Assert.IsTrue(IsValid(NewCell(12, 2))); Assert.IsTrue(IsValid(NewCell(-1, -1)));
        Assert.IsFalse(IsValid(NewCell(0, 1))); Assert.IsFalse(IsValid(NewCell(3, 4))); Assert.IsFalse(IsValid(NewCell(-1, 2)));
        var home = NewCell(4, 6);
        foreach (var (dq, dr) in new[] { (0, 2), (0, -2), (1, 1), (1, -1), (-1, 1), (-1, -1) }) {
            var neighbour = NewCell(4 + dq, 6 + dr);
            Assert.IsTrue(Adjacent(home, neighbour), "(" + dq + ", " + dr + ")"); Assert.IsTrue(Adjacent(neighbour, home), "adjacency is mutual");
            Assert.That(Vector2.Distance(Center(home), Center(neighbour)), Is.EqualTo(1.7320508f * F("A")).Within(0.01f), "neighbours are one lattice step apart");
        }
        foreach (var (dq, dr) in new[] { (0, 0), (0, 4), (2, 0), (2, 2), (-2, 0), (1, 3), (0, 1), (1, 0), (3, 1) })
            Assert.IsFalse(Adjacent(home, NewCell(4 + dq, 6 + dr)), "(" + dq + ", " + dr + ") does not share an edge");
        // the lattice is wider than the hexagons: neighbours leave a gap for the bridge
        Assert.Greater(F("A"), F("R")); Assert.AreEqual(2f * F("R"), F("HexW"), 0.001f); Assert.AreEqual(1.7320508f * F("R"), F("HexH"), 0.001f);
    }

    [Test]
    public void EveryPrerequisiteInsideAKitIsANeighbouringCell()
    {
        int links = 0;
        foreach (var node in Nodes)
            foreach (string text in (IEnumerable)Val(node, "prereqs")) {
                var args = new object[] { text, null, 0 };
                Assert.IsTrue((bool)T("CallingCatalog").GetMethod("TryParsePrereq", Flags).Invoke(null, args), text);
                var other = args[1];
                if (Kit(other) != Kit(node)) continue; // the trunk: drawn as a line from Work Gloves to the kit's banner, not as a bridge
                links++;
                Assert.IsTrue(Adjacent(CellOf(Id(other)), CellOf(Id(node))),
                    Id(node) + " " + At(CellOf(Id(node))) + " needs " + Id(other) + " " + At(CellOf(Id(other))) + ": a prerequisite in the same kit must share an edge");
            }
        Assert.GreaterOrEqual(links, 15, "the three kits are chains and diamonds of prerequisites");
        var cells = Nodes.ToDictionary(Id, n => CellOf(Id(n)));
        // Basics on top: gloves, boots and hat sit above every kit
        float lowestBasic = Nodes.Where(n => Kit(n) == "Basics").Max(n => Center(cells[Id(n)]).y);
        foreach (var node in Nodes.Where(n => Kit(n) != "Basics")) Assert.Greater(Center(cells[Id(node)]).y, lowestBasic, Id(node) + " must hang below the Basics");
    }

    [Test]
    public void TheMonumentIsAOneWaySpineOnTheRightThatClimbsFromTheBottom()
    {
        Assert.AreEqual(10, Tiers);
        for (int tier = 1; tier < Tiers; tier++) {
            Assert.IsTrue(Adjacent(TierCell(tier), TierCell(tier + 1)), "tier " + tier + " " + At(TierCell(tier)) + " and tier " + (tier + 1) + " " + At(TierCell(tier + 1)) + " must share an edge");
            Assert.Less(Center(TierCell(tier + 1)).y, Center(TierCell(tier)).y, "tier " + (tier + 1) + " is printed ABOVE tier " + tier);
        }
        var cells = AllCells();
        float bottom = cells.Values.Max(c => Center(c).y), top = Enumerable.Range(1, Tiers).Min(t => Center(TierCell(t)).y);
        Assert.AreEqual(bottom, Center(TierCell(1)).y, 0.001f, "tier 1 stands on the bottom row of the sheet");
        Assert.AreEqual(top, Center(TierCell(Tiers)).y, 0.001f, "tier 10 is the top of the spine");
        // the rightmost thing on the sheet
        float rightmostBlueprint = Nodes.Max(n => Center(CellOf(Id(n))).x);
        for (int tier = 1; tier <= Tiers; tier++)
            Assert.Greater(Center(TierCell(tier)).x, rightmostBlueprint, "tier " + tier + " must stand right of every blueprint");
        // out-of-range tiers clamp onto the spine instead of pointing off the sheet
        Assert.AreEqual(At(TierCell(1)), At(TierCell(0))); Assert.AreEqual(At(TierCell(1)), At(TierCell(-7))); Assert.AreEqual(At(TierCell(Tiers)), At(TierCell(Tiers + 1)));
    }

    [Test]
    public void EveryHexagonLiesOnTheSheetAndNoTwoOverlap()
    {
        var size = (Vector2)Val(T("SolidWasteLayout"), "Size"); float r = F("R"), halfHeight = F("HexH") * 0.5f;
        Assert.Greater(size.x, 0f); Assert.Greater(size.y, 0f);
        var cells = AllCells().ToArray();
        foreach (var kv in cells) {
            var c = Center(kv.Value);
            Assert.That(c.x, Is.InRange(r, size.x - r), kv.Key + " hangs over the side of the sheet");
            Assert.That(c.y, Is.InRange(halfHeight, size.y - halfHeight), kv.Key + " hangs over the top or bottom of the sheet");
        }
        // two same-way-up hexagons of circumradius R overlap exactly when one centre is inside a hexagon of 2R around the other
        for (int a = 0; a < cells.Length; a++) for (int b = a + 1; b < cells.Length; b++)
            Assert.IsFalse(InHex(Center(cells[b].Value), Center(cells[a].Value), 2f * r - 0.01f), cells[a].Key + " overlaps " + cells[b].Key);
    }

    [Test]
    public void AHexagonIsHitInsideItsSixCornersAndNowhereElse()
    {
        var corners = new Vector2[6];
        foreach (var (centre, r) in new[] { (new Vector2(0f, 0f), 56f), (new Vector2(310f, 145.5f), 58f), (new Vector2(-40f, 900f), 10f) }) {
            T("SolidWasteLayout").GetMethod("Corners").Invoke(null, new object[] { centre, r, corners });
            Assert.IsTrue(InHex(centre, centre, r), "the centre");
            for (int i = 0; i < 6; i++) {
                Assert.AreEqual(r, Vector2.Distance(centre, corners[i]), 0.001f, "corner " + i + " is on the circumradius");
                Assert.AreEqual(r, Vector2.Distance(corners[i], corners[(i + 1) % 6]), 0.001f, "a regular hexagon: every side is one radius long");
                Assert.IsTrue(InHex(Vector2.Lerp(centre, corners[i], 0.97f), centre, r), "just inside corner " + i);
                Assert.IsFalse(InHex(Vector2.LerpUnclamped(centre, corners[i], 1.03f), centre, r), "just outside corner " + i);
                Vector2 edge = (corners[i] + corners[(i + 1) % 6]) * 0.5f;
                Assert.IsTrue(InHex(Vector2.Lerp(centre, edge, 0.97f), centre, r), "just inside edge " + i);
                Assert.IsFalse(InHex(Vector2.LerpUnclamped(centre, edge, 1.03f), centre, r), "just outside edge " + i);
            }
            // flat-top: a corner straight to the right and left, a flat edge above and below
            Assert.IsTrue(corners.Any(c => Mathf.Abs(c.y - centre.y) < 0.001f && c.x > centre.x)); Assert.IsTrue(corners.Any(c => Mathf.Abs(c.y - centre.y) < 0.001f && c.x < centre.x));
            Assert.IsFalse(InHex(centre + new Vector2(0f, r * 0.95f), centre, r), "the bounding circle's top is outside a flat-top hexagon");
            Assert.IsFalse(InHex(centre + new Vector2(r * 0.8f, r * 0.8f), centre, r), "the bounding box's corner is outside");
            Assert.IsFalse(InHex(centre + new Vector2(r * 3f, 0f), centre, r)); Assert.IsFalse(InHex(centre, centre + new Vector2(0f, r * 2f), r));
        }
    }

    // ------------------------------------------------------------------ the tree the app draws

    static string Look(object tree, string id) { var hex = Call(tree, "Find", id); Assert.NotNull(hex, id + " has no hexagon"); return Val(hex, "look").ToString(); }
    static void Refresh(object tree, object state, Func<string, int> have, bool monumentReady = false) => Call(tree, "Refresh", state, have, null, monumentReady);
    static object Printer() { var state = New("WorkshopState"); Set(state, "disk", 1); return state; }
    static void SetLevel(object state, object node, int level) => Call(state, "SetCallingLevel", Val(node, "index"), level);

    [Test]
    public void TheTreeHasAHexagonForEveryBlueprintAndTierAndABridgeForEveryNeighbourLink()
    {
        var tree = Activator.CreateInstance(T("SolidWasteTree"), new VisualElement());
        var hexes = ((IEnumerable)Val(tree, "nodes")).Cast<object>().ToArray();
        Assert.AreEqual(Nodes.Length + Tiers, hexes.Length); Assert.AreEqual(hexes.Length, hexes.Select(h => (string)Val(h, "id")).Distinct().Count());
        foreach (var node in Nodes) {
            var hex = Call(tree, "Find", Id(node)); Assert.NotNull(hex, Id(node));
            Assert.AreSame(node, Val(hex, "node")); Assert.AreEqual(0, (int)Val(hex, "tier"));
        }
        for (int tier = 1; tier <= Tiers; tier++) {
            string id = (string)Static("SolidWasteTree", "MonumentId", tier); Assert.AreEqual("monument:" + tier, id);
            var hex = Call(tree, "Find", id); Assert.NotNull(hex, id); Assert.AreEqual(tier, (int)Val(hex, "tier")); Assert.IsNull(Val(hex, "node"));
        }
        Assert.IsNull(Call(tree, "Find", "made_up")); Assert.IsNull(Call(tree, "Find", new object[] { null }));
        int sameKit = Nodes.Sum(node => ((IEnumerable)Val(node, "prereqs")).Cast<string>().Count(text => {
            var args = new object[] { text, null, 0 }; T("CallingCatalog").GetMethod("TryParsePrereq", Flags).Invoke(null, args); return Kit(args[1]) == Kit(node);
        }));
        var sheet = Val(tree, "sheet");
        Assert.AreEqual(sameKit + Tiers - 1, ((ICollection)Val(sheet, "links")).Count, "one bridge per prerequisite inside a kit, and nine up the Monument");
        Assert.AreEqual(3, ((ICollection)Val(sheet, "trunks")).Count, "the trunk reaches the three kits");
        // every bridge joins two hexagons that are one lattice step apart (a shared edge)
        foreach (var link in (IEnumerable)Val(sheet, "links"))
            Assert.That(Vector2.Distance((Vector2)Val(link, "a"), (Vector2)Val(link, "b")), Is.EqualTo(1.7320508f * F("A")).Within(0.01f));
    }

    [Test]
    public void TheLaptopDocumentHostsThePrintAppAndEveryElementItsControllerLooksFor()
    {
        var document = UnityEditor.AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/UI/ebae/ebae.uxml");
        Assert.NotNull(document, "the laptop's document (Assets/UI/ebae/ebae.uxml)");
        Assert.NotNull(UnityEditor.AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/UI/print/solidwaste.uxml"), "the print app's own page");
        Assert.NotNull(UnityEditor.AssetDatabase.LoadAssetAtPath<StyleSheet>("Assets/UI/print/solidwaste.uss"), "and its sheet");
        var root = document.CloneTree();
        // the third tab, after ebag and Scrapazon, hidden until the disk is in
        var strip = root.Q<VisualElement>("appStrip"); Assert.NotNull(strip);
        var tabs = new[] { "appTabEbae", "appTabShop", "appTabPrint" }.Select(name => root.Q<VisualElement>(name)).ToArray();
        foreach (var tab in tabs) { Assert.NotNull(tab); Assert.AreSame(strip, tab.parent); }
        Assert.Less(strip.IndexOf(tabs[0]), strip.IndexOf(tabs[1])); Assert.Less(strip.IndexOf(tabs[1]), strip.IndexOf(tabs[2]), "SolidWaste 3D is the THIRD tab");
        // (the tab and the page are authored hidden, style="display: none", and EbaeUI shows them once the disk is in; a UXML
        //  inline style cannot be read back without a panel, so that is the laptop's own business)
        Assert.NotNull(root.Q<Label>("appTabPrintLabel")); Assert.NotNull(root.Q<Label>("appTabPrintBadge"));
        // the page, and every element SolidWasteApp.Wire asks for by name
        var page = root.Q<VisualElement>("solidWastePage"); Assert.NotNull(page, "the laptop document must instance the print page as 'solidWastePage'");
        foreach (string name in new[] { "swPage", "swViewport", "swCanvas", "swLegend", "swZoom", "swShelf", "swPrinter", "swLogo", "swLogoMark", "swMenus" })
            Assert.NotNull(page.Q<VisualElement>(name), "solidwaste.uxml has no element named " + name);
        foreach (string name in new[] { "swPrinterLabel", "swStatusLeft", "swStatusRight" }) Assert.NotNull(page.Q<Label>(name), name + " must be a Label");
        Assert.NotNull(page.Q<ScrollView>("swInspector"), "the inspector must be a ScrollView");
        Assert.IsTrue(page.Q<VisualElement>("swViewport").Contains(page.Q<VisualElement>("swCanvas")), "the sheet is panned and zoomed inside the viewport");

        // the controller wires itself onto that document (no laptop behind it here: nothing is shown or locked)
        var app = Static("SolidWasteApp", "Create", null, root);
        Assert.NotNull(app, "SolidWasteApp.Create found no page to drive");
        Assert.IsFalse((bool)Val(app, "IsActive")); Assert.AreEqual(DisplayStyle.None, page.style.display.value, "wired, and hidden until the host opens the tab");
        Assert.AreEqual(Nodes.Length + Tiers, page.Q<VisualElement>("swCanvas").Query<VisualElement>(className: "sw-node").ToList().Count, "one hexagon per blueprint and per Monument tier on the sheet");
        Assert.AreEqual(4, page.Q<VisualElement>("swLegend").childCount, "printed / can print now / needs parts / locked");
        Assert.AreEqual(5, page.Q<VisualElement>("swShelf").childCount, "the shelf and the four sorted types");
        Assert.GreaterOrEqual(page.Q<VisualElement>("swZoom").childCount, 3, "zoom out, zoom in, fit"); Assert.Greater(page.Q<VisualElement>("swMenus").childCount, 0);
        Assert.AreEqual(1, page.Q<ScrollView>("swInspector").Query<VisualElement>("swInspectorContent").ToList().Count);
        // the sheet is as big as the layout says, so FIT and the pan clamp agree with what is drawn
        var size = (Vector2)Val(T("SolidWasteLayout"), "Size");
        Assert.AreEqual(size.x, page.Q<VisualElement>("swCanvas").style.width.value.value, 0.01f); Assert.AreEqual(size.y, page.Q<VisualElement>("swCanvas").style.height.value.value, 0.01f);
        // a document without the page: no app, no exception
        Assert.IsNull(Static("SolidWasteApp", "Create", null, new VisualElement())); Assert.IsNull(Static("SolidWasteApp", "Create", null, null));
        // the saved "has been opened" flag the NEW badge reads
        Assert.AreEqual("solidwaste_seen", T("SolidWasteApp").GetField("SeenCounter").GetRawConstantValue());
    }

    [Test]
    public void AHexagonShowsWhereItsBlueprintStandsForTheBusiness()
    {
        var tree = Activator.CreateInstance(T("SolidWasteTree"), new VisualElement());
        Func<string, int> plenty = id => 99;
        var basics = Nodes.Where(n => Kit(n) == "Basics").ToArray(); var kits = Nodes.Where(n => Kit(n) != "Basics").ToArray();
        var sheet = Val(tree, "sheet");
        bool TrunkLit() => ((IEnumerable)Val(sheet, "trunks")).Cast<object>().All(t => (bool)Val(t, "met"));
        int LitLinks() => ((IEnumerable)Val(sheet, "links")).Cast<object>().Count(l => (bool)Val(l, "met"));

        // a new print shop with an empty shelf: the Basics want parts, everything behind the gloves is locked
        Refresh(tree, Printer(), null);
        foreach (var node in basics) Assert.AreEqual("Need", Look(tree, Id(node)), Id(node));
        foreach (var node in kits) Assert.AreEqual("Locked", Look(tree, Id(node)), Id(node));
        Assert.AreEqual("Need", Look(tree, "monument:1"), "the next tier is open, with nothing to load");
        for (int tier = 2; tier <= Tiers; tier++) Assert.AreEqual("Locked", Look(tree, "monument:" + tier), "tier " + tier);
        Assert.IsFalse(TrunkLit()); Assert.AreEqual(0, LitLinks());
        // the same shop with every part in hand, and scrap for the Monument
        Refresh(tree, Printer(), plenty, true);
        foreach (var node in basics) Assert.AreEqual("Ready", Look(tree, Id(node)), Id(node));
        foreach (var node in kits) Assert.AreEqual("Locked", Look(tree, Id(node)), Id(node) + ": parts never open a lock");
        Assert.AreEqual("Ready", Look(tree, "monument:1")); Assert.AreEqual("Locked", Look(tree, "monument:2"));
        // nothing passed for the state at all (still syncing): the tree shows an empty one instead of throwing
        Refresh(tree, null, null); Assert.AreEqual("Need", Look(tree, "work_gloves")); Assert.AreEqual("Need", Look(tree, "monument:1"));

        // gloves printed once: the trunk lights up, the kits' first blueprints open, the gloves read "partly printed"
        var gloved = Printer(); SetLevel(gloved, Static("CallingCatalog", "Get", "work_gloves"), 1);
        Refresh(tree, gloved, null);
        Assert.AreEqual("Partial", Look(tree, "work_gloves")); Assert.AreEqual(1, (int)Val(Call(tree, "Find", "work_gloves"), "level")); Assert.AreEqual(3, (int)Val(Call(tree, "Find", "work_gloves"), "maxLevel"));
        Assert.IsTrue(TrunkLit());
        foreach (var node in kits) {
            bool first = ((IEnumerable)Val(node, "prereqs")).Cast<string>().All(text => text.StartsWith("work_gloves", StringComparison.Ordinal));
            Assert.AreEqual(first ? "Need" : "Locked", Look(tree, Id(node)), Id(node));
        }
        Refresh(tree, gloved, plenty);
        Assert.AreEqual("Ready", Look(tree, "work_gloves"), "the next version can be printed"); Assert.AreEqual("Ready", Look(tree, "headlamp")); Assert.AreEqual("Locked", Look(tree, "calling_nose_for_money"));

        // the Monument part-way: printed tiers are full, the next one fills from the bottom with what is loaded, the rest wait
        var builder = Printer(); Set(builder, "monument", 3); Set(builder, "fed", Activator.CreateInstance(T("WorkshopMaterials"), 60, 30, 0, 30));
        Refresh(tree, builder, null, false);
        for (int tier = 1; tier <= 3; tier++) Assert.AreEqual("Full", Look(tree, "monument:" + tier), "tier " + tier);
        Assert.AreEqual("Need", Look(tree, "monument:4")); Assert.AreEqual(0.5f, (float)Val(Call(tree, "Find", "monument:4"), "fill"), 0.001f, "120 of 240 loaded");
        for (int tier = 5; tier <= Tiers; tier++) { Assert.AreEqual("Locked", Look(tree, "monument:" + tier)); Assert.AreEqual(0f, (float)Val(Call(tree, "Find", "monument:" + tier), "fill")); }
        Assert.AreEqual(3, LitLinks(), "the spine is lit as far as it is printed");

        // everything printed: every hexagon is full, every bridge lit
        var done = Printer(); foreach (var node in Nodes) SetLevel(done, node, (int)Val(node, "implementedLevels")); Set(done, "monument", Tiers);
        Refresh(tree, done, null);
        foreach (var node in Nodes.Where(n => (int)Val(n, "implementedLevels") == (int)Val(n, "maxLevel"))) Assert.AreEqual("Full", Look(tree, Id(node)), Id(node));
        for (int tier = 1; tier <= Tiers; tier++) Assert.AreEqual("Full", Look(tree, "monument:" + tier));
        Assert.AreEqual(((ICollection)Val(sheet, "links")).Count, LitLinks()); Assert.IsTrue(TrunkLit());
        // and back to nothing: a hexagon is only ever a picture of the state it was last given
        Refresh(tree, Printer(), null);
        Assert.AreEqual("Need", Look(tree, "work_gloves")); Assert.AreEqual("Locked", Look(tree, "headlamp")); Assert.AreEqual("Locked", Look(tree, "monument:2")); Assert.AreEqual(0, LitLinks());
        CollectionAssert.AreEqual(new[] { "Locked", "Need", "Ready", "Partial", "Full" }, Enum.GetNames(T("HexLook")));
    }
}
