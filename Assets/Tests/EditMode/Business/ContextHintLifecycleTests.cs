using System;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public class ContextHintLifecycleTests
{
    static Type HintType => Type.GetType("FeelHudHints, Assembly-CSharp", true);
    GameObject root;
    object hints;

    object Call(string name, params object[] args) => HintType.GetMethod(name).Invoke(hints, args);
    int Chips => (int)HintType.GetProperty("VisibleChips").GetValue(hints);

    [SetUp]
    public void SetUp()
    {
        root = new GameObject("Context hint regression test", typeof(RectTransform));
        hints = Activator.CreateInstance(HintType);
        Call("Build", (RectTransform)root.transform);
        Call("SetText", "W/S drive · A/D steer · H honk");
        Call("SetPrompt", "E", "Exit Truck");
        Call("Sync", 900f);
        Assert.AreEqual(3, Chips);
    }

    [TearDown]
    public void TearDown() { if (root != null) Object.DestroyImmediate(root); }

    [TestCase(null, null)]
    [TestCase("E", "Enter Truck")]
    [TestCase("E", "Hitch a ride")]
    [TestCase("E", "Sort trash")]
    public void ClearingHintWhilePromptChangesRemovesOldDrivingChips(string key, string prompt)
    {
        Call("SetText", new object[] { null });
        Call("SetPrompt", key, prompt);
        Assert.AreEqual(Vector2.zero, (Vector2)Call("Sync", 900f));
        Assert.AreEqual(0, Chips);
        // Same blank state on the next frame stays blank rather than reusing old layout.
        Assert.AreEqual(Vector2.zero, (Vector2)Call("Sync", 900f));
    }

    [Test]
    public void LeavingAndReenteringTruckRestoresThenClearsHintNormally()
    {
        Call("SetText", new object[] { null }); Call("SetPrompt", "E", "Enter Truck"); Call("Sync", 900f);
        Assert.AreEqual(0, Chips);
        Call("SetText", "W/S drive · A/D steer · H honk"); Call("SetPrompt", "E", "Exit Truck"); Call("Sync", 900f);
        Assert.AreEqual(3, Chips);
        Call("SetText", new object[] { null }); Call("SetPrompt", new object[] { null, null }); Call("Sync", 900f);
        Assert.AreEqual(0, Chips);
    }
}
