using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

/// <summary>The curb glow must not outlive its trash: when a record has no bag (a yard dog lost it) it is respawned after a
/// grace period, and a dog walled in by scenery lets go. Pure rules reached by reflection (Assembly-CSharp).</summary>
public class CurbBagReconcileTests
{
    const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object Call(string type, string method, params object[] args)
    {
        var m = Runtime(type).GetMethod(method, Static);
        Assert.IsNotNull(m, type + "." + method + " not found");
        return m.Invoke(null, args);
    }
    static float Const(string type, string field) => (float)Runtime(type).GetField(field, Static).GetValue(null);

    [Test]
    public void OnlyTheAuthorityRespawnsAndOnlyForARecordWithNoBag()
    {
        Assert.IsTrue((bool)Call("CurbBagReconciler", "NeedsBag", false, false, false, false));
        Assert.IsFalse((bool)Call("CurbBagReconciler", "NeedsBag", true, false, false, false), "a guest never respawns");
        Assert.IsFalse((bool)Call("CurbBagReconciler", "NeedsBag", false, true, false, false), "dumpster bags have no object");
        Assert.IsFalse((bool)Call("CurbBagReconciler", "NeedsBag", false, false, true, false), "a resident is still walking it out");
        Assert.IsFalse((bool)Call("CurbBagReconciler", "NeedsBag", false, false, false, true), "carried, held or at rest is a live bag");
    }

    [Test]
    public void AnOrphanWaitsOutTheGracePeriod()
    {
        float grace = Const("CurbBagReconciler", "OrphanSeconds");
        Assert.GreaterOrEqual(grace, 5f, "long enough to ride out a hand-off between systems");
        Assert.IsFalse((bool)Call("CurbBagReconciler", "OrphanDue", 100f, 100f + grace - 0.1f));
        Assert.IsTrue((bool)Call("CurbBagReconciler", "OrphanDue", 100f, 100f + grace));
    }

    [Test]
    public void StrandedMeansOutOfTheWorld()
    {
        Assert.IsTrue((bool)Call("CurbBagReconciler", "IsOutOfWorld", new Vector3(0f, -39f, 0f)), "within the kill plane's rescue band");
        Assert.IsTrue((bool)Call("CurbBagReconciler", "IsOutOfWorld", new Vector3(float.NaN, 1f, 0f)));
        Assert.IsTrue((bool)Call("CurbBagReconciler", "IsOutOfWorld", new Vector3(0f, float.PositiveInfinity, 0f)));
        Assert.IsFalse((bool)Call("CurbBagReconciler", "IsOutOfWorld", new Vector3(120f, 0.3f, -80f)));
        Assert.IsTrue((bool)Call("CurbBagReconciler", "StrandedDue", 10f, 10f + Const("CurbBagReconciler", "StrandedSeconds")));
    }

    [Test]
    public void ADogWalledInDropsTheBagBeforeItGivesUpTheChase()
    {
        float drop = Const("YardDogs", "BlockedDrop"), giveUp = Const("YardDogs", "BlockedGiveUp");
        Assert.Less(drop, giveUp);
        Assert.Less(drop, Const("YardDogs", "CarryMax"), "no jittering against a wall for the whole carry");
        Assert.IsFalse((bool)Call("YardDogs", "BlockedTooLong", true, drop - 0.05f));
        Assert.IsTrue((bool)Call("YardDogs", "BlockedTooLong", true, drop));
        Assert.IsFalse((bool)Call("YardDogs", "BlockedTooLong", false, drop), "a chaser keeps trying a little longer");
        Assert.IsTrue((bool)Call("YardDogs", "BlockedTooLong", false, giveUp));
    }
}
