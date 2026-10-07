using System.Collections.Generic;
using NUnit.Framework;

/// <summary>
/// B10: the door check without Steam (Docs/Multiplayer.md §8.7.4 gate: "a ticket bound to another host fails; a dev: key
/// fails under the release-auth switch; a missing verifier answer times out at 5 s"), on a fake clock.
/// </summary>
public class SteamAuthRulesTests
{
    const ulong Host = 76561197960287930UL;
    const ulong Guest = 76561197960287931UL;
    static readonly string GuestKey = "steam:" + Guest;

    sealed class Outcome
    {
        public int calls;
        public bool ok;
        public JoinReject why;
        public void Done(bool approved, JoinReject reason) { calls++; ok = approved; why = reason; }
    }

    static System.Func<JoinReject, bool> ModeApprove(SteamAuthMode m) => v => SteamAuthRules.Approve(m, v);

    [Test]
    public void DevKeysFailOnlyUnderReleaseAuth()
    {
        Assert.AreEqual(JoinReject.DevKeyNotAllowed, SteamAuthRules.Precheck(SteamAuthMode.Enforce, "dev:bot-1", 0, Host, out _));
        Assert.AreEqual(JoinReject.None, SteamAuthRules.Precheck(SteamAuthMode.Warn, "dev:bot-1", 0, Host, out ulong id));
        Assert.AreEqual(0UL, id, "a dev key is let in without asking Steam");
    }

    [Test]
    public void PrecheckRefusals()
    {
        Assert.AreEqual(JoinReject.BadRequest, SteamAuthRules.Precheck(SteamAuthMode.Enforce, "nobody", 100, Host, out _));
        Assert.AreEqual(JoinReject.BadRequest, SteamAuthRules.Precheck(SteamAuthMode.Enforce, null, 100, Host, out _));
        Assert.AreEqual(JoinReject.AuthFailed, SteamAuthRules.Precheck(SteamAuthMode.Enforce, GuestKey, 0, Host, out _), "no proof");
        Assert.AreEqual(JoinReject.AuthFailed, SteamAuthRules.Precheck(SteamAuthMode.Enforce, "steam:" + Host, 100, Host, out _), "claims to be the host");
        Assert.AreEqual(JoinReject.None, SteamAuthRules.Precheck(SteamAuthMode.Enforce, GuestKey, 100, Host, out ulong id));
        Assert.AreEqual(Guest, id);
    }

    [Test]
    public void SteamAnswersMapToReasons()
    {
        Assert.AreEqual(JoinReject.None, SteamAuthRules.FromResponse(SteamAuthRules.ResponseOk));
        Assert.AreEqual(JoinReject.AuthFailed, SteamAuthRules.FromResponse(SteamAuthRules.ResponseNetworkIdentityFailure), "bound to another host");
        Assert.AreEqual(JoinReject.Banned, SteamAuthRules.FromResponse(SteamAuthRules.ResponseVacBanned));
        Assert.AreEqual(JoinReject.Banned, SteamAuthRules.FromResponse(SteamAuthRules.ResponsePublisherBan));
        Assert.AreEqual(JoinReject.AuthFailed, SteamAuthRules.FromResponse(7));
        Assert.AreEqual(JoinReject.None, SteamAuthRules.FromBeginResult(SteamAuthRules.BeginOk));
        for (int c = 1; c <= 5; c++) Assert.AreEqual(JoinReject.AuthFailed, SteamAuthRules.FromBeginResult(c));
        Assert.AreEqual("bound to another host", SteamAuthRules.DescribeResponse(10));
    }

    [Test]
    public void ProofBoundToAnotherHostIsRefusedWhenEnforcing()
    {
        var refused = new List<ulong>();
        var t = new PendingAuthTable(5, refused.Add);
        var o = new Outcome();
        Assert.IsTrue(t.Begin(2, Guest, 0, o.Done));
        Assert.IsTrue(t.Answer(Guest, SteamAuthRules.FromResponse(SteamAuthRules.ResponseNetworkIdentityFailure), true,
                               JoinReject.NotLobbyMember, out ulong client, ModeApprove(SteamAuthMode.Enforce)));
        Assert.AreEqual(2UL, client);
        Assert.AreEqual(1, o.calls);
        Assert.IsFalse(o.ok);
        Assert.AreEqual(JoinReject.AuthFailed, o.why);
        CollectionAssert.AreEqual(new[] { Guest }, refused, "the refused auth session is ended at once");
        Assert.AreEqual(0, t.ActiveCount);
    }

    [Test]
    public void WarnLetsThemInButStillDecides()
    {
        var t = new PendingAuthTable(5);
        var o = new Outcome();
        t.Begin(2, Guest, 0, o.Done);
        t.Answer(Guest, JoinReject.AuthFailed, true, JoinReject.NotLobbyMember, out _, ModeApprove(SteamAuthMode.Warn));
        Assert.IsTrue(o.ok);
        Assert.AreEqual(JoinReject.None, o.why);
        Assert.AreEqual(1, t.ActiveCount, "an approved session stays open until End");
        Assert.AreEqual(Guest, t.End(2));
    }

    [Test]
    public void NoAnswerTimesOutAtFiveSeconds()
    {
        var t = new PendingAuthTable(SteamAuthRules.TimeoutSeconds);
        var o = new Outcome();
        t.Begin(7, Guest, 100.0, o.Done);
        Assert.AreEqual(0, t.Tick(104.9, _ => true, ModeApprove(SteamAuthMode.Enforce)));
        Assert.AreEqual(0, o.calls, "still waiting at 4.9 s");
        Assert.AreEqual(1, t.Tick(105.0, _ => true, ModeApprove(SteamAuthMode.Enforce)));
        Assert.AreEqual(1, o.calls);
        Assert.IsFalse(o.ok);
        Assert.AreEqual(JoinReject.AuthTimeout, o.why);
        Assert.AreEqual(0, t.PendingCount);
        Assert.AreEqual(0, t.Tick(200, _ => true, ModeApprove(SteamAuthMode.Enforce)), "callbacks run exactly once");
        Assert.AreEqual(1, o.calls);
    }

    [Test]
    public void LobbyMembershipIsWaitedForThenRequired()
    {
        var t = new PendingAuthTable(5);
        var o = new Outcome();
        bool member = false;
        t.Begin(3, Guest, 0, o.Done);
        t.Answer(Guest, JoinReject.None, member, JoinReject.NotLobbyMember, out _, ModeApprove(SteamAuthMode.Enforce));
        Assert.AreEqual(0, o.calls, "Steam said yes; waiting for the lobby to show them");
        t.Tick(1, _ => member, ModeApprove(SteamAuthMode.Enforce));
        Assert.AreEqual(0, o.calls);
        member = true;
        t.Tick(2, _ => member, ModeApprove(SteamAuthMode.Enforce));
        Assert.IsTrue(o.ok);

        var t2 = new PendingAuthTable(5);
        var o2 = new Outcome();
        t2.Begin(4, Guest, 0, o2.Done);
        t2.Answer(Guest, JoinReject.None, false, JoinReject.NotLobbyMember, out _, ModeApprove(SteamAuthMode.Enforce));
        t2.Tick(5, _ => false, ModeApprove(SteamAuthMode.Enforce));
        Assert.IsFalse(o2.ok);
        Assert.AreEqual(JoinReject.NotLobbyMember, o2.why);
    }

    [Test]
    public void OneSteamAccountOneClient()
    {
        var t = new PendingAuthTable(5);
        var a = new Outcome();
        Assert.IsTrue(t.Begin(2, Guest, 0, a.Done));
        Assert.IsFalse(t.Begin(3, Guest, 0, new Outcome().Done), "the same SteamID from a second connection");
        Assert.IsFalse(t.Begin(2, Host, 0, new Outcome().Done), "the same client twice");
        Assert.AreEqual(Guest, t.End(2));
        Assert.AreEqual(0, a.calls, "End forgets without calling back");
        Assert.IsTrue(t.Begin(3, Guest, 1, new Outcome().Done), "free again after End");
        Assert.IsFalse(t.Answer(Host, JoinReject.None, true, JoinReject.None, out _, null), "nobody waits for that SteamID");
        Assert.AreEqual(1, t.Clear().Count);
        Assert.AreEqual(0, t.PendingCount);
    }

    [Test]
    public void NewestConnectionTakesOverAnApprovedOne()
    {
        // B1's newest-wins (crash + relaunch before the old connection timed out) must work in Enforce mode too.
        var t = new PendingAuthTable(5);
        var first = new Outcome();
        Assert.IsTrue(t.Begin(2, Guest, 0, first.Done));
        t.Answer(Guest, JoinReject.None, true, JoinReject.NotLobbyMember, out _, ModeApprove(SteamAuthMode.Enforce));
        Assert.IsTrue(first.ok, "the first connection is in");

        Assert.IsFalse(t.Begin(5, Guest, 1, new Outcome().Done), "still held by client 2");
        Assert.IsTrue(t.TryTakeOver(5, Guest, out ulong old));
        Assert.AreEqual(2UL, old);
        var second = new Outcome();
        Assert.IsTrue(t.Begin(5, Guest, 1, second.Done), "the new connection is checked");
        Assert.AreEqual(0UL, t.End(2), "the old client's later End must not close the new auth session");
        t.Answer(Guest, JoinReject.None, true, JoinReject.NotLobbyMember, out ulong who, ModeApprove(SteamAuthMode.Enforce));
        Assert.AreEqual(5UL, who);
        Assert.IsTrue(second.ok);
        Assert.AreEqual(1, first.calls, "the old connection's callback isn't called again");
        Assert.AreEqual(Guest, t.End(5));
    }

    [Test]
    public void TwoJoinsAtOnceAreNotATakeOver()
    {
        var t = new PendingAuthTable(5);
        var a = new Outcome();
        Assert.IsTrue(t.Begin(2, Guest, 0, a.Done));
        Assert.IsFalse(t.TryTakeOver(3, Guest, out ulong old), "client 2 is still at the door");
        Assert.AreEqual(0UL, old);
        Assert.IsFalse(t.TryTakeOver(3, Host, out _), "nobody holds that SteamID");
        Assert.AreEqual(1, t.PendingCount);
        Assert.AreEqual(0, a.calls);
    }
}
