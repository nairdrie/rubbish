using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

/// <summary>
/// Kevin's Crew is gone (save v6) and the raccoon crew is the only background collection. Pure rules only: the refund
/// migration over plain dictionaries, the day-stat counter rename, what a crew bag is worth in Rep, which ledger ids earn a
/// payroll share, the Head Office nudge, and that nothing of the old feature is left to call. The load itself (main save,
/// slots, a guest's business) is covered in BusinessSelectionTests under a temporary save root.
/// </summary>
public class KevinRemovalTests
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static Type T(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object Static(string type, string method, params object[] args) => T(type).GetMethod(method, Flags).Invoke(null, args);
    static object Const(string type, string field) => T(type).GetField(field, Flags).GetRawConstantValue();
    static long Migrate(IDictionary<string, int> levels, IDictionary<string, int> counters, int version) => (long)Static("KevinSeverance", "Migrate", levels, counters, version);
    static Dictionary<string, int> Map(params (string key, int value)[] pairs) => pairs.ToDictionary(p => p.key, p => p.value);
    const string Kevin = "hire_kevin", Note = "kevin_severance_cents";

    [Test]
    public void TheRefundIsExactlyWhatEachLevelCost()
    {
        Assert.AreEqual(Kevin, (string)Const("KevinSeverance", "LegacyUpgradeId")); Assert.AreEqual(Note, (string)Const("KevinSeverance", "PendingCounter"));
        // $600 for Kevin, $7,000 more for Other Kevin, $23,000 more for the union: the catalog prices when he was removed
        CollectionAssert.AreEqual(new long[] { 0, 0, 60000, 760000, 3060000, 3060000, 3060000 },
            new[] { -1, 0, 1, 2, 3, 4, 99 }.Select(level => (long)Static("KevinSeverance", "RefundCents", level)).ToArray());
        for (int level = 1; level <= 3; level++)
        {
            var levels = Map((Kevin, level), ("truck_capacity", 2), ("permit_minivan_meadows", 1)); var counters = Map(("wo_0_district", 3));
            long refund = Migrate(levels, counters, 5);
            Assert.AreEqual((long)Static("KevinSeverance", "RefundCents", level), refund);
            Assert.IsFalse(levels.ContainsKey(Kevin), "the level is cleared"); Assert.AreEqual(2, levels.Count, "nothing else is touched"); Assert.AreEqual(2, levels["truck_capacity"]);
            Assert.AreEqual((int)refund, counters[Note]); Assert.AreEqual(3, counters["wo_0_district"]);
        }
    }

    [Test]
    public void TheMigrationIsIdempotentVersionGatedAndDefaultSafe()
    {
        var levels = Map((Kevin, 3)); var counters = Map();
        Assert.AreEqual(3060000L, Migrate(levels, counters, 5));
        for (int i = 0; i < 3; i++) Assert.AreEqual(0L, Migrate(levels, counters, 5), "a second pass over the same business finds nothing to refund");
        Assert.AreEqual(3060000, counters[Note], "and leaves the note for the letter alone");
        // every version before the removal is migrated (0 = a v1 file); the removal's own version and later never are
        int removed = (int)Const("KevinSeverance", "RemovedInSaveVersion"); Assert.AreEqual(6, removed);
        Assert.AreEqual(removed, (int)T("GameSession").GetField("CurrentSaveVersion", Flags).GetRawConstantValue(), "saves are written at the version that stops the migration");
        foreach (int version in new[] { 0, 1, 2, 3, 4, 5 }) Assert.AreEqual(60000L, Migrate(Map((Kevin, 1)), Map(), version), "v" + version);
        foreach (int version in new[] { 6, 7, 100 })
        {
            var kept = Map((Kevin, 3)); var untouched = Map(("st0_kevin", 4));
            Assert.AreEqual(0L, Migrate(kept, untouched, version)); Assert.AreEqual(3, kept[Kevin]); Assert.AreEqual(4, untouched["st0_kevin"]); Assert.IsFalse(untouched.ContainsKey(Note));
        }
        // no Kevin, no grant and no note: a new business, somebody who never hired him, a crew member's save
        var plain = Map(("truck_capacity", 1)); var quiet = Map(("stats_day", 4));
        Assert.AreEqual(0L, Migrate(plain, quiet, 5)); Assert.AreEqual(1, plain.Count); Assert.AreEqual(1, quiet.Count);
        Assert.AreEqual(0L, Migrate(Map(), Map(), 0)); Assert.AreEqual(0L, Migrate(null, null, 0)); Assert.AreEqual(0L, Migrate(null, Map(), 5));
        // a listed level of zero or less is cleared without a grant or a note; missing counters do not stop a refund
        foreach (int level in new[] { 0, -5 })
        {
            var zero = Map((Kevin, level)); var none = Map();
            Assert.AreEqual(0L, Migrate(zero, none, 5)); Assert.AreEqual(0, zero.Count); Assert.AreEqual(0, none.Count);
        }
        var noCounters = Map((Kevin, 2)); Assert.AreEqual(760000L, Migrate(noCounters, null, 5)); Assert.AreEqual(0, noCounters.Count);
    }

    [Test]
    public void TheDayStatCounterMovesFromKevinToTheCrewWithoutLosingAnything()
    {
        var counters = Map(("st0_kevin", 7), ("st1_kevin", 11), ("st0_bags", 3), ("st1_crew", 2));
        Static("DailyStats", "MigrateLegacyCrewCounters", counters);
        Assert.AreEqual(7, counters["st0_crew"]); Assert.AreEqual(13, counters["st1_crew"], "added to what is already there"); Assert.AreEqual(3, counters["st0_bags"]);
        Assert.IsFalse(counters.ContainsKey("st0_kevin")); Assert.IsFalse(counters.ContainsKey("st1_kevin"));
        Static("DailyStats", "MigrateLegacyCrewCounters", counters);
        Assert.AreEqual(7, counters["st0_crew"]); Assert.AreEqual(13, counters["st1_crew"]); Assert.AreEqual(3, counters.Count, "idempotent");
        var zero = Map(("st0_kevin", 0)); Static("DailyStats", "MigrateLegacyCrewCounters", zero); Assert.AreEqual(0, zero.Count, "an empty old counter leaves no new one behind");
        var clean = Map(("st0_bags", 1)); Static("DailyStats", "MigrateLegacyCrewCounters", clean); Assert.AreEqual(1, clean.Count);
        Static("DailyStats", "MigrateLegacyCrewCounters", new object[] { null });
        // the same move happens as part of the save migration, refund or not, and never on a v6 save
        var viaSave = Map(("st0_kevin", 5)); Migrate(Map(), viaSave, 5); Assert.AreEqual(5, viaSave["st0_crew"]);
        Assert.NotNull(T("DayStats").GetField("crewBags")); Assert.IsNull(T("DayStats").GetField("kevinBags"));
    }

    [Test]
    public void ACrewBagEarnsHalfTheRepNoWorkOrderCreditAndIsFlaggedByRaccoonAlone()
    {
        var fact = T("BagCollectedFact");
        Assert.NotNull(fact.GetField("byRaccoon")); Assert.IsNull(fact.GetField("byKevin"), "one flag: crew work is the raccoons'");
        float onTime = (float)Const("RepValues", "BagOnTime"), late = (float)Const("RepValues", "BagLate"), share = (float)Const("RepValues", "CrewShare");
        Assert.AreEqual(0.5f, share); Assert.IsNull(T("RepValues").GetField("KevinShare"));
        Assert.AreEqual(onTime, (float)Static("Progression", "BagRep", true, false)); Assert.AreEqual(late, (float)Static("Progression", "BagRep", false, false));
        Assert.AreEqual(onTime * 0.5f, (float)Static("Progression", "BagRep", true, true), 1e-6f); Assert.AreEqual(late * 0.5f, (float)Static("Progression", "BagRep", false, true), 1e-6f);
        // Work Orders are about your own shift: a bag by hand or by the arm counts, a crew's bag never does
        object crewBag = Activator.CreateInstance(fact); fact.GetField("byRaccoon").SetValue(crewBag, true); fact.GetField("onTime").SetValue(crewBag, true);
        object handBag = Activator.CreateInstance(fact); fact.GetField("onTime").SetValue(handBag, true);
        object armBag = Activator.CreateInstance(fact); fact.GetField("byArm").SetValue(armBag, true);
        Assert.IsFalse((bool)Static("WorkOrders", "CountsBag", crewBag)); Assert.IsTrue((bool)Static("WorkOrders", "CountsBag", handBag)); Assert.IsTrue((bool)Static("WorkOrders", "CountsBag", armBag));
    }

    [Test]
    public void TheCrewsFeesEarnAPayrollShareLikeAnyCollectionIncomeAndKevinsLedgerIdsAreGone()
    {
        bool Eligible(string kind, long cents, string id) => (bool)T("PayrollShareRules").GetMethod("EligibleCredit").Invoke(null, new object[] { kind, cents, id });
        string crew = (string)Const("RaccoonTuning", "LedgerCrew"), antics = (string)Const("RaccoonTuning", "LedgerCrewEvent"), food = (string)Const("RaccoonTuning", "LedgerFood");
        Assert.AreEqual("raccoon_crew", crew);
        Assert.IsTrue(Eligible("credit", 1, crew), "the crew's collection fees are operating income, as Kevin's fees were");
        Assert.IsFalse(Eligible("credit", 0, crew)); Assert.IsFalse(Eligible("charge", 100, crew)); Assert.IsFalse(Eligible("spend", 100, crew));
        Assert.IsFalse(Eligible("credit", 1000, antics), "a found wallet is luck, not income"); Assert.IsFalse(Eligible("credit", 1000, food));
        Assert.IsFalse(Eligible("credit", 1000, "kevin_fees"), "nothing posts under the old id any more"); Assert.IsFalse(Eligible("credit", 1000, "refund"));
        Assert.IsNull(T("LedgerIds").GetField("KevinFees")); Assert.IsNull(T("LedgerIds").GetField("KevinWage"));
    }

    [Test]
    public void NothingOfTheOldFeatureIsLeftToBuyCallOrTune()
    {
        Assert.IsNull(Type.GetType("KevinCrew, Assembly-CSharp", false)); Assert.IsNull(T("ShopIds").GetField("HireKevin"));
        Assert.IsNull(Static("ScrapazonCatalog", "Get", Kevin), "Scrapazon does not sell him");
        foreach (var item in (System.Collections.IEnumerable)T("ScrapazonCatalog").GetProperty("All", Flags).GetValue(null))
        {
            var prereqs = (string[][])item.GetType().GetField("prereqs").GetValue(item);
            foreach (var level in prereqs) foreach (string need in level ?? new string[0]) StringAssert.DoesNotContain(Kevin, need ?? "");
        }
        Assert.IsNull(T("ScrapazonItem").GetField("dailyWage")); Assert.IsNull(T("ScrapazonStore").GetMethod("NextDailyWage", Flags));
        foreach (string gone in new[] { "KevinWage", "KevinWageAt", "KevinCoverage" }) Assert.IsEmpty(T("UpgradeEffects").GetMember(gone, Flags), gone);
        foreach (string gone in new[] { "KevinFeeShare", "KevinBurstChance" }) Assert.IsNull(T("Economy").GetField(gone, Flags), gone);
        Assert.IsNull(T("Progression").GetNestedType("Milestones").GetField("KevinHired"));
    }

    [Test]
    public void HeadOfficePointsAFilthyDistrictAtTheNextStepTowardARaccoonCrew()
    {
        (bool sent, string subject, string body) Nudge(int hutch, int collar, int brain, int onCrew)
        {
            var args = new object[] { hutch, collar, brain, onCrew, null, null };
            bool sent = (bool)T("Progression").GetMethod("CrewNudge", Flags).Invoke(null, args);
            return (sent, (string)args[4], (string)args[5]);
        }
        var noHutch = Nudge(0, 0, 0, 0); Assert.IsTrue(noHutch.sent); Assert.AreEqual("Have you considered raccoons?", noHutch.subject); StringAssert.Contains("Critter Hutch", noHutch.body);
        var noChip = Nudge(1, 0, 0, 0); Assert.IsTrue(noChip.sent); StringAssert.Contains("Collar Chip", noChip.body);
        var idle = Nudge(2, 1, 1, 0); Assert.IsTrue(idle.sent); StringAssert.Contains("nobody in your hutch is on the crew", idle.body);
        var collar = Nudge(1, 1, 0, 2); Assert.IsTrue(collar.sent); StringAssert.Contains("Brain Chip", collar.body);
        foreach (int brain in new[] { 1, 2 }) { var done = Nudge(2, 1, brain, 4); Assert.IsFalse(done.sent); Assert.IsNull(done.subject); Assert.IsNull(done.body); }
        foreach (var letter in new[] { noHutch, noChip, idle, collar })
        {
            StringAssert.DoesNotContain("Kevin", letter.subject + letter.body); StringAssert.Contains("filthy two nights running", letter.body);
        }
    }
}
