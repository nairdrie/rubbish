using System;
using System.Reflection;
using NUnit.Framework;

/// <summary>Gameplay HUD classes live in Assembly-CSharp, outside this test asmdef's references.</summary>
public class CleanupPresentationTests
{
    static Type Mode => Type.GetType("HudMode, Assembly-CSharp", true);
    static object M(string name) => Enum.Parse(Mode, name);

    [Test]
    public void CleanupOverlayOnlyAppearsInUnobstructedGameplay()
    {
        var type = Type.GetType("BigCleanupPresentationRules, Assembly-CSharp", true);
        var method = type.GetMethod("CanShow", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        bool Show(string mode, bool sort = false, bool ui = false, bool moving = false) =>
            (bool)method.Invoke(null, new object[] { M(mode), sort, ui, moving });
        Assert.IsTrue(Show("Street"));
        Assert.IsTrue(Show("Driving"));
        Assert.IsTrue(Show("Depot"));
        Assert.IsFalse(Show("Station"));
        Assert.IsFalse(Show("Laptop"));
        Assert.IsFalse(Show("Map"));
        Assert.IsFalse(Show("Hidden"));
        Assert.IsFalse(Show("Street", sort: true));
        Assert.IsFalse(Show("Street", ui: true));
        Assert.IsFalse(Show("Street", moving: true));
    }

    [Test]
    public void BothShopSchemesHideRouteGuidance()
    {
        var type = Type.GetType("FeelHudRoute, Assembly-CSharp", true);
        var method = type.GetMethod("HideGuidanceIndoors", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        bool Hide(bool depot, bool legacy) => (bool)method.Invoke(null, new object[] { depot, legacy });
        Assert.IsFalse(Hide(false, false));
        Assert.IsTrue(Hide(true, false));
        Assert.IsTrue(Hide(false, true));
    }
}
