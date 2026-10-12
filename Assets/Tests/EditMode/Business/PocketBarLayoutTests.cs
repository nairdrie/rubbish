using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// The pocket / gear bar's corner (Docs/UIPass.md §8.2). <c>HudLayout</c> is pure math, so these run on numbers: the bar sits in
/// the bottom-right corner, the route panel's column ends above it, the centred dock keeps clear of its width, Station mode has
/// none, and <c>FeelHudHotbar.Measure</c> picks one column or two and drops rows past the cap. Assembly-CSharp is reached by
/// reflection from the EditMode test assembly.
/// </summary>
public sealed class PocketBarLayoutTests
{
    const float Tol = 0.01f;

    static Type Layout => Type.GetType("HudLayout, Assembly-CSharp", true);
    static Type InputsT => Type.GetType("HudLayout+Inputs, Assembly-CSharp", true);
    static Type ModeT => Type.GetType("HudMode, Assembly-CSharp", true);
    static Type Bar => Type.GetType("FeelHudHotbar, Assembly-CSharp", true);

    static object Limits(float w, float h, bool business = false) =>
        Layout.GetMethod("PocketFor", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { new Vector2(w, h), business });

    static Vector2 Measure(int count, object limits, out int columns, out int shown)
    {
        var args = new object[] { count, limits, 0, 0 };
        var size = (Vector2)Bar.GetMethod("Measure", BindingFlags.Public | BindingFlags.Static).Invoke(null, args);
        columns = (int)args[2];
        shown = (int)args[3];
        return size;
    }

    static T Get<T>(object o, string field) => (T)o.GetType().GetField(field).GetValue(o);

    /// <summary>One frame of HudLayout.Compute. <paramref name="dock"/> = the prompt + hint chips height the dock wants.</summary>
    static object Compute(float w, float h, string mode, Vector2 pocket, float dock = 0f, bool business = false, float minimap = 250f, float left = 150f)
    {
        var inputs = Activator.CreateInstance(InputsT); // a boxed struct: SetValue changes the box in place
        void Set(string name, object v) => InputsT.GetField(name).SetValue(inputs, v);
        Set("size", new Vector2(w, h));
        Set("mode", Enum.Parse(ModeT, mode));
        Set("businessContext", business);
        Set("minimapWanted", true);
        Set("minimapDiameter", minimap);
        Set("minimapLabel", true);
        Set("leftWanted", left);
        Set("dockWanted", dock);
        Set("pocketWanted", pocket);
        return Layout.GetMethod("Compute", BindingFlags.Public | BindingFlags.Static).Invoke(null, new[] { inputs });
    }

    static bool Overlap(Rect a, Rect b) =>
        a.width > Tol && a.height > Tol && b.width > Tol && b.height > Tol &&
        a.xMin < b.xMax - Tol && b.xMin < a.xMax - Tol && a.yMin < b.yMax - Tol && b.yMin < a.yMax - Tol;

    // ------------------------------------------------------------------ the grid

    [TestCase(1, 1, 138f, 76f)]
    [TestCase(4, 1, 138f, 262f)]
    [TestCase(6, 1, 138f, 386f)]
    [TestCase(7, 2, 283f, 262f)]
    [TestCase(8, 2, 283f, 262f)]
    public void AtFullHdTheBarStaysOneColumnUntilItWouldGetTall(int count, int columns, float width, float height)
    {
        var size = Measure(count, Limits(1920f, 1080f), out int cols, out int shown);
        Assert.AreEqual(columns, cols, $"{count} slots");
        Assert.AreEqual(count, shown, "nothing is dropped when there is room");
        Assert.AreEqual(width, size.x, Tol);
        Assert.AreEqual(height, size.y, Tol);
    }

    [TestCase(4, 1, 262f)]
    [TestCase(5, 2, 200f)]
    [TestCase(8, 2, 262f)]
    public void ASmallScreenGoesToASecondColumnSooner(int count, int columns, float height)
    {
        var size = Measure(count, Limits(1280f, 720f), out int cols, out int shown); // 150% UI scale at 16:9
        Assert.AreEqual(columns, cols, $"{count} slots");
        Assert.AreEqual(count, shown);
        Assert.AreEqual(height, size.y, Tol);
    }

    [Test]
    public void NothingOwnedReservesNothing()
    {
        var size = Measure(0, Limits(1920f, 1080f), out int cols, out int shown);
        Assert.AreEqual(Vector2.zero, size);
        Assert.AreEqual(0, cols);
        Assert.AreEqual(0, shown);
    }

    [Test]
    public void RowsPastTheRouteHeaderCapAreDroppedLastSlotsFirst()
    {
        // 32:9 at 150% UI scale: 1810 x 509 reference px leaves the bar 193 px under the route panel's header
        var limits = Limits(1810f, 509f);
        Assert.AreEqual(193f, Get<float>(limits, "maxHeight"), Tol);
        var size = Measure(8, limits, out int cols, out int shown);
        Assert.AreEqual(2, cols);
        Assert.AreEqual(4, shown, "two rows of two: the AUTO counters at the end are the ones that go");
        Assert.LessOrEqual(size.y, 193f + Tol);
    }

    [Test]
    public void NoRoomForEvenOneRowHidesTheBar()
    {
        var size = Measure(3, Limits(1810f, 380f), out int cols, out int shown);
        Assert.AreEqual(Vector2.zero, size);
        Assert.AreEqual(0, shown);
        Assert.AreEqual(0, cols);
    }

    [Test]
    public void TheBarIsNeverWiderThanTheRightLane()
    {
        foreach (var screen in new[] { new Vector2(1073f, 859f), new Vector2(1280f, 720f), new Vector2(1920f, 1080f) })
        {
            var limits = Limits(screen.x, screen.y);
            for (int n = 1; n <= 8; n++)
                Assert.LessOrEqual(Measure(n, limits, out _, out _).x, Get<float>(limits, "maxWidth") + Tol, $"{screen} {n} slots");
        }
    }

    [Test]
    public void ABarThatIsNotAllowedReservesNothing()
    {
        // chess, a station, the laptop: FeelHud passes allowed = false and HudLayout gets no block to reserve
        var bar = Activator.CreateInstance(Bar);
        var sync = Bar.GetMethod("Sync", BindingFlags.Public | BindingFlags.Instance);
        var mode = Enum.Parse(ModeT, "Street");
        var limits = Limits(1920f, 1080f);
        var shown = (Vector2)sync.Invoke(bar, new[] { limits, mode, true, true }); // preview: the demo set of four
        Assert.AreEqual(138f, shown.x, Tol);
        Assert.AreEqual(262f, shown.y, Tol);
        var hidden = (Vector2)sync.Invoke(bar, new[] { limits, mode, true, false });
        Assert.AreEqual(Vector2.zero, hidden);
    }

    // ------------------------------------------------------------------ the corner

    [Test]
    public void TheBarSitsInTheBottomRightCornerAndTheRouteColumnEndsAboveIt()
    {
        var f = Compute(1920f, 1080f, "Street", new Vector2(138f, 262f));
        var pocket = Get<Rect>(f, "pocket");
        Assert.AreEqual(1920f - 24f, pocket.xMax, Tol);
        Assert.AreEqual(1080f - 24f, pocket.yMax, Tol);
        Assert.AreEqual(138f, pocket.width, Tol);
        Assert.AreEqual(262f, pocket.height, Tol);
        var column = Get<Rect>(f, "rightColumn");
        Assert.AreEqual(pocket.yMin - 16f, column.yMax, Tol, "the route / memo column stops 16 px above the bar");
        Assert.AreEqual(Get<Rect>(Compute(1920f, 1080f, "Street", Vector2.zero), "rightColumn").yMax, 1080f - 24f, Tol, "and runs to the margin without it");
    }

    [Test]
    public void StationModeHasNoPocketBarEvenIfAsked()
    {
        var f = Compute(1920f, 1080f, "Station", new Vector2(138f, 262f));
        Assert.AreEqual(0f, Get<Rect>(f, "pocket").width);
    }

    [Test]
    public void ZoneLookupFindsThePocket()
    {
        var f = Compute(1920f, 1080f, "Driving", new Vector2(283f, 262f));
        Layout.GetMethod("Publish", BindingFlags.Public | BindingFlags.Static).Invoke(null, new[] { f });
        try
        {
            var zone = Type.GetType("HudZone, Assembly-CSharp", true);
            var rect = (Rect)Layout.GetMethod("Zone", BindingFlags.Public | BindingFlags.Static).Invoke(null, new[] { Enum.Parse(zone, "Pocket") });
            Assert.AreEqual(Get<Rect>(f, "pocket"), rect);
            Assert.AreEqual(283f, rect.width, Tol);
        }
        finally // put the published frame back to "no frame yet" so other tests see what they would have
        {
            var none = Activator.CreateInstance(Type.GetType("HudLayout+Frame, Assembly-CSharp", true));
            Layout.GetMethod("Publish", BindingFlags.Public | BindingFlags.Static).Invoke(null, new[] { none });
        }
    }

    // ------------------------------------------------------------------ the dock

    [TestCase(1920f, 250f, 138f)]
    [TestCase(1920f, 200f, 283f)]
    [TestCase(1280f, 250f, 283f)]
    [TestCase(1073f, 300f, 283f)]
    public void TheCentredDockKeepsClearOfTheWiderOfTheMinimapAndTheBar(float refW, float minimap, float bar)
    {
        float side = Mathf.Max(Mathf.Max(200f, minimap), bar);
        float expected = Mathf.Clamp(Mathf.Min(1100f, refW - 2f * (24f + side + 16f)), Mathf.Min(360f, refW - 48f), 1100f);
        float width = (float)Layout.GetMethod("DockWidth", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { refW, minimap, bar });
        Assert.AreEqual(expected, width, Tol);
        float minimapOnly = (float)Layout.GetMethod("DockWidth", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { refW, minimap, 0f });
        Assert.LessOrEqual(width, minimapOnly + Tol, "a bar never makes the dock wider");
        if (bar <= Mathf.Max(200f, minimap)) Assert.AreEqual(minimapOnly, width, Tol, "a bar narrower than the minimap block changes nothing");
    }

    // ------------------------------------------------------------------ nothing overlaps it

    [Test]
    public void TheBarNeverTouchesAnythingAcrossScreensCountsAndMinimapSizes()
    {
        var screens = new[]
        {
            new Vector2(1920f, 1080f), new Vector2(1280f, 720f), new Vector2(2400f, 1350f), new Vector2(1663f, 1247f),
            new Vector2(1610f, 1288f), new Vector2(1073f, 859f), new Vector2(2216f, 935f), new Vector2(1478f, 623f),
            new Vector2(2715f, 764f), new Vector2(1152f, 720f),
        };
        foreach (var screen in screens)
        foreach (bool business in new[] { false, true })
        {
            var limits = Limits(screen.x, screen.y, business);
            for (int n = 1; n <= 8; n++)
            {
                var block = Measure(n, limits, out _, out _);
                if (block.x <= 0f) continue;
                foreach (float minimap in new[] { 200f, 250f, 300f })
                foreach (var mode in new[] { "Street", "Driving", "Depot" })
                {
                    var f = Compute(screen.x, screen.y, mode, block, dock: 120f, business: business, minimap: minimap);
                    string tag = $"{screen} business={business} n={n} minimap={minimap} {mode}";
                    var p = Get<Rect>(f, "pocket");
                    Assert.AreEqual(block.x, p.width, Tol, tag);
                    Assert.GreaterOrEqual(p.xMin, 0f, tag);
                    Assert.GreaterOrEqual(p.yMin, 0f, tag);
                    foreach (var other in new[] { "leftColumn", "rightColumn", "bottomDock", "toastLane", "centerStage", "cash", "clock", "rank", "truck" })
                        Assert.IsFalse(Overlap(p, Get<Rect>(f, other)), $"{tag}: pocket {p} overlaps {other} {Get<Rect>(f, other)}");
                    if (Get<bool>(f, "minimapVisible")) Assert.IsFalse(Overlap(p, Get<Rect>(f, "minimap")), $"{tag}: pocket overlaps the minimap");
                    Assert.GreaterOrEqual(Get<Rect>(f, "rightColumn").height, 132f - Tol, $"{tag}: the route panel's header lost its room");
                }
            }
        }
    }
}
