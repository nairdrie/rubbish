using System.Collections.Generic;
using NUnit.Framework;

/// <summary>B1 gate: seats by PlayerKey (Docs/Multiplayer.md §2.4, §8.7.4 row B1).</summary>
public class SlotTableTests
{
    static PlayerKey K(string n) => PlayerKey.Dev(n);

    static int Seat(SlotTable t, string name, int company = 0)
    {
        Assert.IsTrue(t.TryAssign(K(name), company, out int slot, out var why), $"{name} should be seated, got {why}");
        Assert.AreEqual(JoinReject.None, why);
        return slot;
    }

    [Test]
    public void LowestFreeSlotFirst()
    {
        var t = new SlotTable(NetConstants.TownCap, NetConstants.CompanyCap);
        Assert.AreEqual(0, Seat(t, "host"));
        Assert.AreEqual(1, Seat(t, "b"));
        Assert.AreEqual(2, Seat(t, "c"));
        Assert.AreEqual(3, t.Count);
        Assert.AreEqual(3, t.CountIn(0));
        Assert.IsTrue(t.TryGet(K("c"), out int company, out int slot));
        Assert.AreEqual(0, company);
        Assert.AreEqual(2, slot);
    }

    [Test]
    public void SeatedPlayerGetsTheSameSeatAgain()
    {
        var t = new SlotTable(8, 4);
        Seat(t, "a");
        Assert.AreEqual(1, Seat(t, "b"));
        Assert.AreEqual(1, Seat(t, "b"));
        Assert.AreEqual(2, t.Count);
    }

    [Test]
    public void RejoinKeepsSlot()
    {
        var t = new SlotTable(NetConstants.TownCap, NetConstants.CompanyCap);
        Seat(t, "host");
        Assert.AreEqual(1, Seat(t, "b"));
        Assert.AreEqual(2, Seat(t, "c"));
        t.Release(K("b"));
        Assert.IsFalse(t.TryGet(K("b"), out _, out _), "released players aren't seated");
        Assert.IsTrue(t.TryGetRemembered(K("b"), out _, out int remembered));
        Assert.AreEqual(1, remembered);
        // A newcomer skips b's remembered seat while another is free...
        Assert.AreEqual(3, Seat(t, "d"));
        // ...and b comes back to the same slot.
        Assert.AreEqual(1, Seat(t, "b"));
    }

    [Test]
    public void RememberedSeatIsUsedWhenNothingElseIsFree()
    {
        var t = new SlotTable(4, 4);
        Seat(t, "a"); Seat(t, "b"); Seat(t, "c"); Seat(t, "d");
        t.Release(K("b"));
        Assert.AreEqual(1, Seat(t, "e"), "a full crew still seats a newcomer in a remembered slot");
        // b's seat is gone now: b gets nothing (crew is full).
        Assert.IsFalse(t.TryAssign(K("b"), 0, out _, out var why));
        Assert.AreEqual(JoinReject.CompanyFull, why);
    }

    [Test]
    public void FifthInACrewTownIsCompanyFull()
    {
        var t = new SlotTable(NetConstants.TownCap, NetConstants.CompanyCap);
        for (int i = 0; i < 4; i++) Seat(t, "p" + i);
        Assert.IsFalse(t.TryAssign(K("fifth"), 0, out int slot, out var why));
        Assert.AreEqual(-1, slot);
        Assert.AreEqual(JoinReject.CompanyFull, why);
        StringAssert.Contains("Crew is full (4/4)", JoinRejectText.For(why));
    }

    [Test]
    public void FifthInATownIsTownFull()
    {
        // §13.10: 4 players per Town, each with their own business.
        Assert.AreEqual(4, NetConstants.TownCap);
        var t = new SlotTable(NetConstants.TownCap, NetConstants.CompanyCap);
        for (int i = 0; i < 2; i++) Seat(t, "a" + i, 0);
        for (int i = 0; i < 2; i++) Seat(t, "b" + i, 1);
        Assert.AreEqual(4, t.Count);
        Assert.IsFalse(t.TryAssign(K("fifth"), 2, out _, out var why));
        Assert.AreEqual(JoinReject.TownFull, why);
        StringAssert.Contains("Town is full (4/4)", JoinRejectText.For(why));
    }

    [Test]
    public void CompanyCapIsCheckedBeforeTownCap()
    {
        var t = new SlotTable(4, 4);
        for (int i = 0; i < 4; i++) Seat(t, "p" + i);
        Assert.IsFalse(t.TryAssign(K("x"), 0, out _, out var why));
        Assert.AreEqual(JoinReject.CompanyFull, why, "the 5th crew member reads 'Crew is full', not 'Town is full'");
    }

    [Test]
    public void ReleaseRemembersForgetDoesNot()
    {
        var t = new SlotTable(8, 4);
        Seat(t, "a");
        Assert.AreEqual(1, Seat(t, "b"));
        t.Release(K("b"));
        Assert.AreEqual(1, t.Count);
        Assert.IsTrue(t.TryGetRemembered(K("b"), out _, out _));
        t.Forget(K("b"));
        Assert.IsFalse(t.TryGetRemembered(K("b"), out _, out _));
        Assert.AreEqual(1, Seat(t, "c"), "a forgotten seat is free for anyone");
        Assert.AreEqual(2, Seat(t, "b"), "b rejoins as a newcomer");
    }

    [Test]
    public void SeatsAreRememberedPerCompany()
    {
        var t = new SlotTable(8, 4);
        Assert.AreEqual(0, Seat(t, "a", 0));
        Assert.AreEqual(0, Seat(t, "b", 1));
        Assert.AreEqual(1, t.CountIn(0));
        Assert.AreEqual(1, t.CountIn(1));
        Assert.IsFalse(t.TryAssign(K("a"), 1, out _, out var why), "already seated in another company");
        Assert.AreEqual(JoinReject.BadRequest, why);
    }

    [Test]
    public void BadInputIsRefused()
    {
        var t = new SlotTable(8, 4);
        Assert.IsFalse(t.TryAssign(default, 0, out _, out var why));
        Assert.AreEqual(JoinReject.BadRequest, why);
        Assert.IsFalse(t.TryAssign(K("a"), -1, out _, out why));
        Assert.AreEqual(JoinReject.BadRequest, why);
        Assert.IsFalse(t.TryAssign(K("a"), NetConstants.MaxCompanies, out _, out why));
        Assert.AreEqual(JoinReject.BadRequest, why);
        t.Release(K("nobody"));
        t.Forget(K("nobody"));
        Assert.AreEqual(0, t.Count);
    }

    [Test]
    public void RestoreLoadsRememberedSeats()
    {
        var t = new SlotTable(8, 4);
        t.Restore(new List<(PlayerKey, int, int)> { (K("b"), 0, 2), (K("bad"), 0, 99), (default, 0, 1) });
        Assert.AreEqual(0, t.Count, "restored seats are remembered, not active");
        Assert.AreEqual(0, Seat(t, "a"));
        Assert.AreEqual(1, Seat(t, "c"), "a newcomer skips b's remembered slot 2");
        Assert.AreEqual(2, Seat(t, "b"));
        Assert.IsFalse(t.TryGetRemembered(K("bad"), out _, out _), "out-of-range rows are skipped");
    }
}
