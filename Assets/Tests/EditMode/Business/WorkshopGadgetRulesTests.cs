using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public sealed class WorkshopGadgetRulesTests
{
    static Type Gadget => Type.GetType("WorkshopGadget, Assembly-CSharp", true);
    static Type Lounge => Type.GetType("WorkshopLounge, Assembly-CSharp", true);
    static bool Cross(Vector3 from, Vector3 to) => (bool)Gadget.GetMethod("CrossedHoop").Invoke(null,
        new object[] { from, to, Vector3.zero, .62f });

    [Test]
    public void HoopScoresOnlyDownwardTravelThroughItsOpening()
    {
        Assert.IsTrue(Cross(new Vector3(0, 1, 0), new Vector3(0, -1, 0)));
        Assert.IsTrue(Cross(new Vector3(.6f, 1, 0), new Vector3(.6f, -1, 0)));
        Assert.IsTrue(Cross(new Vector3(-1, 1, 0), new Vector3(1, -1, 0)), "A fast diagonal uses the plane intersection, not its final position");
        Assert.IsFalse(Cross(new Vector3(.7f, 1, 0), new Vector3(.7f, -1, 0)));
        Assert.IsFalse(Cross(new Vector3(0, -1, 0), new Vector3(0, 1, 0)));
        Assert.IsFalse(Cross(new Vector3(0, 2, 0), new Vector3(0, 1, 0)));
        Assert.IsFalse(Cross(new Vector3(0, 5, 0), new Vector3(0, -1, 0)), "A teleport is not a basket");
    }

    [Test]
    public void LoungePoseIsAbsoluteFromCapturedRestNotAccumulatedPerFrame()
    {
        var rest = Quaternion.Euler(4, 11, -7);
        var method = Lounge.GetMethod("SeatedRotation");
        for (int leg = 0; leg < 4; leg++)
        {
            Quaternion expected = (Quaternion)method.Invoke(null, new object[] { rest, leg });
            Quaternion frame = expected;
            for (int i = 0; i < 600; i++) frame = (Quaternion)method.Invoke(null, new object[] { rest, leg });
            Assert.That(Quaternion.Angle(expected, frame), Is.LessThan(.01f));
        }
    }

    [TestCase("Normal", true)]
    [TestCase("Stunned", true)]
    [TestCase("KnockedOut", true)]
    [TestCase("Flopped", true)]
    [TestCase("Thrown", true)]
    [TestCase("Carried", false)]
    [TestCase("Riding", false)]
    public void StandingNeverReenablesControllerOwnedByCarrierOrTruck(string state, bool expected)
    {
        var type = Type.GetType("BodyState, Assembly-CSharp", true);
        Assert.AreEqual(expected, Lounge.GetMethod("RestoreControllerFor").Invoke(null, new[] { Enum.Parse(type, state) }));
    }

    [TestCase("bag_hoop")]
    [TestCase("sorting_chute")]
    [TestCase("spring_launcher")]
    [TestCase("break_sofa")]
    [TestCase("scrap_radio")]
    [TestCase("doorbell")]
    public void OnlyFunctionalRecipesAreGadgets(string id)
    {
        Assert.IsTrue((bool)Gadget.GetMethod("IsGadget").Invoke(null, new object[] { id }));
        Assert.IsFalse((bool)Gadget.GetMethod("IsGadget").Invoke(null, new object[] { "bottle_cabinet" }));
        Assert.IsFalse((bool)Gadget.GetMethod("IsGadget").Invoke(null, new object[] { null }));
    }

    [Test]
    public void DisplayFitHonoursShallowCurtainClearanceWithoutShrinkingEveryGrandFind()
    {
        var display = Type.GetType("WorkshopDisplay, Assembly-CSharp", true);
        float Fit(string recipe, Vector3 size) => (float)display.GetMethod("FitScale").Invoke(null, new object[] { recipe, size, false });
        Assert.That(Fit("grand_display", Vector3.one), Is.EqualTo(.34f).Within(.0001f), "A cube must fit the stage depth, not just the .85m size cap");
        Assert.That(Fit("grand_display", new Vector3(.2f, 1f, .2f)), Is.EqualTo(.85f).Within(.0001f), "A tall narrow find can still use the grand display's height");
        Assert.That(Fit("rotating_pedestal", Vector3.one) * Mathf.Sqrt(2f), Is.LessThanOrEqualTo(.4501f), "Spinning corners stay within the platter");
        foreach (string recipe in new[] { "bottle_cabinet", "wall_display", "grand_display", "rotating_pedestal" })
        {
            var volume = (Vector3)display.GetMethod("AvailableVolume").Invoke(null, new object[] { recipe });
            var size = new Vector3(3f, 2f, 4f) * Fit(recipe, new Vector3(3f, 2f, 4f));
            Assert.LessOrEqual(size.x, volume.x + .0001f);
            Assert.LessOrEqual(size.y, volume.y + .0001f);
            Assert.LessOrEqual(size.z, volume.z + .0001f);
        }
    }

    [Test]
    public void MachineLaunchResetsAnOldBagsFlightClockAndStillness()
    {
        var carryType = Type.GetType("Carryable, Assembly-CSharp", true);
        var handsType = Type.GetType("HandsNet, Assembly-CSharp", true);
        var bagType = Type.GetType("TrashBag, Assembly-CSharp", true);
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var go = new GameObject("Workshop old-bag launch regression");
        try
        {
            var bag = go.AddComponent(bagType);
            var body = go.GetComponent<Rigidbody>();
            carryType.GetField("thrownSince", hidden).SetValue(bag, Time.time - 99f);
            carryType.GetField("settleTimer", hidden).SetValue(bag, 1f);
            body.isKinematic = true;
            Vector3 velocity = new Vector3(2.69f, 5.35f, 0f);
            handsType.GetMethod("WorkshopBeginPhysicalFlight", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { bag, new Vector3(0, 10, 0), velocity });
            Assert.That((float)carryType.GetField("thrownSince", hidden).GetValue(bag), Is.EqualTo(Time.time).Within(.001f));
            Assert.AreEqual(0f, carryType.GetField("settleTimer", hidden).GetValue(bag));
            Assert.IsFalse(body.isKinematic);
            Assert.That(Vector3.Distance(body.linearVelocity, velocity), Is.LessThan(.001f));
            // This was the regression: an otherwise fast airborne bag instantly became Resting because its clock was stale.
            carryType.GetMethod("FixedUpdate", hidden).Invoke(bag, null);
            Assert.AreEqual("Thrown", carryType.GetProperty("State").GetValue(bag).ToString());
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }
}
