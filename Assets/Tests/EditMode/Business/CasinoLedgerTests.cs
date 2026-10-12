using System;
using System.Reflection;
using NUnit.Framework;

/// <summary>
/// A night out is not the business (Docs/Casino.md §4, §5): casino stakes and winnings, the bar tab and the cab home are
/// moved out of the day's credits and charges into lines of their own, so "Net" (which prices tomorrow's Work Orders) is
/// what it would have been without them, and they never earn a payroll share. Reflection because the logic lives in
/// Assembly-CSharp.
/// </summary>
public sealed class CasinoLedgerTests
{
    static Type T(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static string Id(string name) => (string)T("LedgerIds").GetField(name).GetValue(null);
    static float F(object day, string field) => (float)day.GetType().GetField(field).GetValue(day);
    static void Set(object o, string field, object value) => o.GetType().GetField(field).SetValue(o, value);
    static float Net(object day) => (float)day.GetType().GetProperty("Net").GetValue(day);

    /// <summary>What the game does for one posting: the cash change lands in credits / charges first (CashChanged), then
    /// the Ledger posting arrives and <c>DailyStats.MoveLeisure</c> sorts it out. Returns MoveLeisure's answer.</summary>
    static bool Post(object day, string kind, string id, float amount)
    {
        if (amount > 0f) Set(day, "credits", F(day, "credits") + amount);
        else Set(day, "charges", F(day, "charges") - amount);
        object e = Activator.CreateInstance(T("LedgerEntry"));
        Set(e, "kind", kind); Set(e, "id", id); Set(e, "amount", amount); Set(e, "cents", (long)Math.Round(amount * 100.0));
        return (bool)T("DailyStats").GetMethod("MoveLeisure", BindingFlags.Public | BindingFlags.Static).Invoke(null, new[] { day, e });
    }

    [Test]
    public void ANightAtTheCasinoLeavesNetIncomeExactlyWhereTheDaysWorkPutIt()
    {
        object day = Activator.CreateInstance(T("DayStats"));
        Assert.IsFalse(Post(day, "credit", Id("PickupFee"), 120f), "ordinary income is not touched");
        Assert.IsFalse(Post(day, "charge", Id("Hospital"), -35f), "an ordinary charge still counts against the day");
        Assert.AreEqual(85f, Net(day), 0.001f);

        Assert.IsTrue(Post(day, "charge", Id("CasinoWager"), -200f));
        Assert.IsTrue(Post(day, "credit", Id("CasinoPayout"), 500f));
        Assert.IsTrue(Post(day, "charge", Id("CasinoWager"), -25f));
        Assert.IsTrue(Post(day, "credit", Id("CasinoWager"), 25f), "a voided stake comes back on the wager id");
        Assert.IsTrue(Post(day, "charge", Id("BarTab"), -14f));
        Assert.IsTrue(Post(day, "charge", Id("BarTab"), -6f));
        Assert.IsTrue(Post(day, "charge", Id("CabFare"), -40f));

        Assert.AreEqual(85f, Net(day), 0.001f, "Net (tomorrow's Work Order pay) ignores the casino, the bar and the cab");
        Assert.AreEqual(120f, F(day, "credits"), 0.001f);
        Assert.AreEqual(35f, F(day, "charges"), 0.001f);
        Assert.AreEqual(0f, F(day, "spent"), 0.001f, "a stake is not a purchase either");
        Assert.AreEqual(500f, F(day, "casinoWon"), 0.001f);
        Assert.AreEqual(200f, F(day, "casinoStaked"), 0.001f, "the voided $25 is not counted as staked");
        Assert.AreEqual(60f, F(day, "barTab"), 0.001f, "drinks and the cab home share the bar line");

        string casino = (string)T("ScrapazonCareer").GetMethod("CasinoLine").Invoke(null, new[] { day });
        string bar = (string)T("ScrapazonCareer").GetMethod("BarLine").Invoke(null, new[] { day });
        Assert.AreEqual("+$500 / -$200", casino);
        Assert.AreEqual("-$60.00", bar);
        object quiet = Activator.CreateInstance(T("DayStats"));
        Assert.AreEqual("-", (string)T("ScrapazonCareer").GetMethod("CasinoLine").Invoke(null, new[] { quiet }));
        Assert.AreEqual("-", (string)T("ScrapazonCareer").GetMethod("BarLine").Invoke(null, new[] { quiet }));
    }

    [Test]
    public void OnlyTheFourLeisureIdsAreSetAside()
    {
        var isLeisure = T("LedgerIds").GetMethod("IsLeisure");
        foreach (string name in new[] { "CasinoWager", "CasinoPayout", "BarTab", "CabFare" })
            Assert.IsTrue((bool)isLeisure.Invoke(null, new object[] { Id(name) }), name);
        foreach (string name in new[] { "PickupFee", "Recycle", "Hospital", "Tow", "Purchase", "WorkOrder", "Ebae" })
            Assert.IsFalse((bool)isLeisure.Invoke(null, new object[] { Id(name) }), name);
        Assert.IsFalse((bool)isLeisure.Invoke(null, new object[] { null }));

        // a spend (a purchase) on a leisure id would be a bug elsewhere; it is left to the purchase path, not half-moved
        object day = Activator.CreateInstance(T("DayStats"));
        Set(day, "charges", 10f);
        object e = Activator.CreateInstance(T("LedgerEntry"));
        Set(e, "kind", "spend"); Set(e, "id", Id("BarTab")); Set(e, "amount", -10f);
        Assert.IsFalse((bool)T("DailyStats").GetMethod("MoveLeisure").Invoke(null, new[] { day, e }));
        Assert.AreEqual(10f, F(day, "charges"), 0.001f);
    }

    [Test]
    public void CasinoWinningsNeverEarnAnEmployeeAShare()
    {
        var eligible = T("PayrollShareRules").GetMethod("EligibleCredit");
        Assert.IsFalse((bool)eligible.Invoke(null, new object[] { "credit", 50000L, Id("CasinoPayout") }));
        Assert.IsFalse((bool)eligible.Invoke(null, new object[] { "credit", 2500L, Id("CasinoWager") }), "a voided stake");
        Assert.IsTrue((bool)eligible.Invoke(null, new object[] { "credit", 400L, Id("PickupFee") }));
    }
}
