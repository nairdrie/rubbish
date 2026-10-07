using System;
using NUnit.Framework;
using UnityEngine;

public class TruckDrivePresentationTests
{
    static Type Coast => Type.GetType("TruckCoastRules, Assembly-CSharp", true);
    static Type Cab => Type.GetType("TruckCabVisuals, Assembly-CSharp", true);
    static float Loss(float speed, float throttle = 0f, bool grounded = true, float dt = 0.02f) =>
        (float)Coast.GetMethod("SpeedLoss").Invoke(null, new object[] { speed, throttle, grounded, dt });
    static bool Glass(string renderer, int slot, string material) =>
        (bool)Cab.GetMethod("IsGlassSlot").Invoke(null, new object[] { renderer, slot, material });

    [Test]
    public void NeutralTruckSlowsGraduallyWithoutFlippingItsVelocity()
    {
        float atRoadSpeed = Loss(10f);
        Assert.Greater(atRoadSpeed, 0f);
        Assert.Less(atRoadSpeed, 0.04f, "rolling drag is gentler than deliberate service braking");
        Assert.AreEqual(0.001f, Loss(0.001f), 0.000001f);
        Assert.AreEqual(0f, Loss(0f));
        Assert.AreEqual(0f, Loss(float.NaN));
        Assert.AreEqual(0f, Loss(float.PositiveInfinity));
    }

    [Test]
    public void NeutralDragDoesNotAlterThrottleBrakeOrAirbornePhysics()
    {
        Assert.AreEqual(0f, Loss(10f, 1f));
        Assert.AreEqual(0f, Loss(10f, -1f));
        Assert.AreEqual(0f, Loss(10f, 0f, false));
        Assert.AreEqual(0f, Loss(10f, 0f, true, 0f));
        Assert.Greater(Loss(10f, 0.02f), 0f, "small gamepad drift is still neutral");
    }

    [Test]
    public void NeutralTruckComesToRestAndHighSpeedDragIsCapped()
    {
        float speed = 10f;
        for (int i = 0; i < 750; i++) speed -= Loss(speed);
        Assert.AreEqual(0f, speed, 0.0001f);
        float maximum = (float)Coast.GetField("MaxDeceleration").GetRawConstantValue();
        Assert.LessOrEqual(Loss(100f), maximum * 0.02f + 0.00001f);
    }

    [Test]
    public void CabGlassOnlyReplacesVerifiedWindowMaterialNotTruckPaint()
    {
        Assert.IsTrue(Glass("TruckBody", 2, "Material.004"));
        Assert.IsTrue(Glass("TruckBody", 2, "Material.004 (Instance)"));
        Assert.IsFalse(Glass("TruckBody", 0, "MainBody.003"));
        Assert.IsFalse(Glass("TruckBody", 1, "Cab.003"));
        Assert.IsFalse(Glass("Table", 2, "Material.004"));
        Assert.IsFalse(Glass("TruckBody", 4, "Material.004"));
        Assert.Less((float)Cab.GetField("GlassAlpha").GetRawConstantValue(), 0.2f);
    }

    [Test]
    public void DriverNameClearsTheTruckRoofInsteadOfClippingIntoTheCab()
    {
        Vector3 head = (Vector3)Cab.GetField("DriverHeadMetres").GetValue(null);
        Vector3 name = (Vector3)Cab.GetField("DriverNameMetres").GetValue(null);
        var truck = Type.GetType("TruckControllerV2, Assembly-CSharp", true);
        var bounds = (Bounds)truck.GetProperty("BodyBoxMetres").GetValue(null);
        Assert.GreaterOrEqual(name.y - bounds.max.y, 0.49f);
        Assert.AreEqual(head.x, name.x);
        Assert.AreEqual(head.z, name.z);
    }

    [Test]
    public void DriverFaceFitsWithinImportedWindshieldAndKeepsCorrectFamilyLook()
    {
        Vector3 head = (Vector3)Cab.GetField("DriverHeadMetres").GetValue(null);
        Assert.That(head.y, Is.InRange(1.65f, 2.10f));
        Assert.That(head.z, Is.InRange(2.8f, 3.4f));
        Assert.Less(Mathf.Abs(head.x), 0.7f);
        var parts = Type.GetType("ChuckPartsData, Assembly-CSharp", true);
        int Id(string name) => (int)parts.GetField(name).GetRawConstantValue();
        bool Visible(string name, int variant) => (bool)Cab.GetMethod("PartVisible").Invoke(null, new object[] { Id(name), variant });
        Assert.IsTrue(Visible("Stache_L", 0));
        Assert.IsFalse(Visible("Stache_L", 1), "Dot has no moustache");
        Assert.IsTrue(Visible("Pigtail_L", 1));
        Assert.IsFalse(Visible("Pigtail_L", 0));
        Assert.IsTrue(Visible("Cap_BeanieCuff", 2));
        Assert.IsFalse(Visible("Cap_Brim", 2));
        Assert.IsTrue(Visible("Unibrow", 3));
    }
}
