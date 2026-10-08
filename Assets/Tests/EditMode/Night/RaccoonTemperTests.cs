using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Pure encounter rules and the actual non-repelling headlight reaction, without a running network/session.</summary>
public class RaccoonTemperTests
{
    const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static Type Rules => Runtime("RaccoonTemper");
    static object Call(string name, params object[] args) => Rules.GetMethod(name).Invoke(null, args);
    static float Constant(string name) => (float)Rules.GetField(name).GetRawConstantValue();
    GameObject made;

    [TearDown]
    public void TearDown()
    {
        if (made == null) return;
        var raccoon = made.GetComponent(Runtime("Raccoon"));
        // Match the project reflection fixtures: registry cleanup is explicit even when EditMode skips OnDisable.
        Runtime("Critter").GetMethod("OnDisable", Fields).Invoke(raccoon, null);
        Object.DestroyImmediate(made);
        made = null;
    }

    Component Raccoon()
    {
        made = new GameObject("RaccoonTemperTest");
        return made.AddComponent(Runtime("Raccoon")); // no rig needed for the pure light/provocation decisions
    }

    static bool Dazzle(Component r) => (bool)r.GetType().GetMethod("Dazzle").Invoke(r, new object[] { Vector3.back * 3f });

    [TestCase(false, false, 0.6f)]
    [TestCase(true, false, 0.8f)]
    [TestCase(false, true, 0.8f)]
    public void EveryRaccoonTypeCanInfectButNoTypeGuaranteesInfection(bool rabid, bool king, float expected)
    {
        float chance = (float)Call("InfectionChance", rabid, king);
        Assert.AreEqual(expected, chance, 0.00001f);
        Assert.That(chance, Is.GreaterThan(0f).And.LessThan(1f));
    }

    [TestCase(false, 0.3f)]
    [TestCase(true, 0.4f)]
    public void BothPlainAndFoamyRaccoonsCanLatch(bool rabid, float expected)
    {
        Assert.AreEqual(expected, (float)Call("LatchChance", rabid), 0.00001f);
    }

    [TestCase(true, 1f, false, 0, true)]
    [TestCase(true, 22f, false, 1, true)]
    [TestCase(false, 1f, false, 0, false)]
    [TestCase(true, 22.1f, false, 0, false)]
    [TestCase(true, 1f, true, 0, false)]
    [TestCase(true, 1f, false, 2, false)]
    public void RetaliationRespectsCabRepellentRangeAndEncounterBudget(bool onFoot, float distance, bool repelled, int bites, bool expected)
    {
        Assert.AreEqual(expected, (bool)Call("CanRetaliate", onFoot, distance, repelled, bites));
    }

    [TestCase(true, false, 1f, 0f, true)]
    [TestCase(false, false, 1f, 0f, false)]
    [TestCase(true, true, 1f, 0f, false)]
    [TestCase(true, false, 1.5f, 0f, false)]
    [TestCase(true, false, 1f, 2.5f, false)]
    [TestCase(true, false, 1f, -2.5f, false)]
    [TestCase(true, false, -1f, 0f, false)]
    public void BitesRequireAnActualReachableUnprotectedFootTarget(bool onFoot, bool immune, float distance, float height, bool expected)
    {
        Assert.AreEqual(expected, (bool)Call("CanConnect", onFoot, immune, distance, height, 1.4f));
    }

    [Test]
    public void AttackTuningHasTelegraphAndRecoveryWindows()
    {
        float immunity = (float)Runtime("NightShift").GetField("CritterImmunity").GetRawConstantValue();
        Assert.GreaterOrEqual(Constant("BiteCooldown"), immunity);
        Assert.GreaterOrEqual(Constant("WindupSeconds"), 0.35f);
        Assert.GreaterOrEqual(Constant("StartleCooldown"), 5f);
        Assert.AreEqual(2, Rules.GetField("BitesPerEncounter").GetRawConstantValue());
    }

    [Test]
    public void CalmHeadlightFlinchNeverChangesStateAndCannotBeRepeatedImmediately()
    {
        var r = Raccoon();
        object initial = r.GetType().GetProperty("Current").GetValue(r);
        Assert.IsTrue(Dazzle(r));
        Assert.AreEqual(initial, r.GetType().GetProperty("Current").GetValue(r), "a headlight must not enter Flee");
        Assert.IsFalse((bool)r.GetType().GetProperty("Angry").GetValue(r), "light alone does not provoke");
        Assert.IsFalse(Dazzle(r), "per-animal cooldown prevents overlapping truck beams stun-locking it");
    }

    [TestCase("Angry")]
    [TestCase("Rabid")]
    [TestCase("Puppet")]
    public void AngryFoamyAndGuestPuppetRaccoonsIgnoreHeadlightFear(string mode)
    {
        var r = Raccoon();
        if (mode == "Angry") r.GetType().GetField("angryUntil", Fields).SetValue(r, Time.time + 10f);
        else if (mode == "Rabid") r.GetType().GetProperty("Rabid").SetValue(r, true);
        else Runtime("Critter").GetMethod("MakePuppet").Invoke(r, new object[] { (ushort)4 });
        Assert.IsFalse(Dazzle(r));
        Assert.AreEqual("Hiding", r.GetType().GetProperty("Current").GetValue(r).ToString());
    }

    [Test]
    public void GuestPuppetCannotAcquireHostAggression()
    {
        var r = Raccoon();
        Runtime("Critter").GetMethod("MakePuppet").Invoke(r, new object[] { (ushort)4 });
        r.GetType().GetMethod("Provoke", Fields).Invoke(r, new object[] { 99UL, Vector3.zero });
        Assert.IsFalse((bool)r.GetType().GetProperty("Angry").GetValue(r));
    }

    [TestCase(0, 0f)]
    [TestCase(1, 20f)]
    [TestCase(2, 30f)]
    [TestCase(-1, 0f)]
    [TestCase(99, 30f)]
    public void BaseTruckHasNoFearBeamAndOwnedLightTierSetsItsRange(int level, float expected)
    {
        Assert.AreEqual(expected, (float)Runtime("UpgradeEffects").GetMethod("BeamScareRadiusAtLevel").Invoke(null, new object[] { level }));
    }

    [TestCase(0, true)]
    [TestCase(1, true)]
    [TestCase(2, true)]
    [TestCase(3, true)]
    [TestCase(4, true)]
    [TestCase(5, true)]
    [TestCase(6, false)]
    [TestCase(7, false)]
    // Bits 3..7 now carry the five collection permits; they are no longer reserved.
    [TestCase(8, true)]
    [TestCase(16, true)]
    [TestCase(32, true)]
    [TestCase(64, true)]
    [TestCase(128, true)]
    [TestCase(248, true)]
    [TestCase(255, false)]
    public void OwnerGearDeclarationAllowsPermitsButRejectsInvalidHeadlightLevels(int gear, bool expected)
    {
        var validate = Runtime("BusinessUpgradeNet").GetMethod("ValidDeclaration", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.AreEqual(expected, (bool)validate.Invoke(null, new object[] { 0UL, (byte)gear }));
        Assert.IsFalse((bool)validate.Invoke(null, new object[] { 3UL, (byte)gear }), "gear must not bypass garage validation");
    }

    [Test]
    public void ChangedHeadlightByteIsNotLostAsAnUnchangedGarageSnapshot()
    {
        var info = Runtime("BusinessUpgradeInfo");
        object a = Activator.CreateInstance(info), b = Activator.CreateInstance(info);
        var equals = info.GetMethod("Equals", new[] { info });
        Assert.IsTrue((bool)equals.Invoke(a, new[] { b }));
        info.GetField("gearBits").SetValue(b, (byte)4);
        Assert.IsFalse((bool)equals.Invoke(a, new[] { b }));
    }
}
