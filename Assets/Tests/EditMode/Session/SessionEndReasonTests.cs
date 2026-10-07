using System;
using NUnit.Framework;

/// <summary>B1: readable disconnect reasons and the small session helpers.</summary>
public class SessionEndReasonTests
{
    [Test]
    public void EveryRejectRoundTripsAndReadsInOurWords()
    {
        foreach (JoinReject r in Enum.GetValues(typeof(JoinReject)))
        {
            if (r == JoinReject.None) continue;
            string wire = SessionEndReason.Encode(r);
            Assert.IsTrue(SessionEndReason.TryDecode(wire, out var d), wire);
            Assert.IsTrue(d.TryGetReject(out var back));
            Assert.AreEqual(r, back);
            Assert.AreEqual(JoinRejectText.For(r), SessionEndReason.Describe(wire, "fallback"));
        }
    }

    [Test]
    public void KnownCodesIgnoreTheSendersText()
    {
        string wire = SessionEndReason.Encode("ProtocolMismatch", "<b>click evil.example</b> to update");
        StringAssert.StartsWith("Update your game", SessionEndReason.Describe(wire, "x"));
    }

    [Test]
    public void UnknownCodesShowCleanedText()
    {
        string wire = SessionEndReason.Prefix + "SomethingNew|<color=red>Hello</color>\u0007 there " + new string('x', 400);
        string shown = SessionEndReason.Describe(wire, "fallback");
        Assert.IsFalse(shown.Contains("<") || shown.Contains(">"), shown);
        Assert.IsFalse(shown.Contains("\u0007"));
        Assert.LessOrEqual(shown.Length, SessionEndReason.MaxText);
        StringAssert.StartsWith("color=redHello", shown);
    }

    [Test]
    public void ExtraCodesHaveTheirOwnWords()
    {
        StringAssert.Contains("removed you", SessionEndReason.Describe(SessionEndReason.Encode(SessionEndReason.Kicked, "spamming"), "f"));
        StringAssert.Contains("spamming", SessionEndReason.Describe(SessionEndReason.Encode(SessionEndReason.Kicked, "spamming"), "f"));
        StringAssert.Contains("another window", SessionEndReason.Describe(SessionEndReason.Encode(SessionEndReason.Replaced, ""), "f"));
        Assert.AreEqual("f", SessionEndReason.Describe(SessionEndReason.Encode(SessionEndReason.Lost, "ignored"), "f"));
    }

    [Test]
    public void ForeignAndEmptyReasons()
    {
        Assert.AreEqual("fallback", SessionEndReason.Describe(null, "fallback"));
        Assert.AreEqual("fallback", SessionEndReason.Describe("", "fallback"));
        Assert.AreEqual("fallback", SessionEndReason.Describe("  \n ", "fallback"));
        Assert.AreEqual("Server is shutting down", SessionEndReason.Describe("Server is shutting down", "fallback"));
        Assert.IsFalse(SessionEndReason.TryDecode("RB1|", out _));
        Assert.IsFalse(SessionEndReason.TryDecode("RB2|TownFull|x", out _));
    }

    [Test]
    public void CodesCantSmuggleSeparators()
    {
        string wire = SessionEndReason.Encode("Town|Full", "x|y");
        Assert.IsTrue(SessionEndReason.TryDecode(wire, out var d));
        Assert.AreEqual("TownFull", d.Code);
        Assert.AreEqual("x|y", d.Text);
        Assert.IsTrue(d.TryGetReject(out var r));
        Assert.AreEqual(JoinReject.TownFull, r);
        Assert.IsFalse(new SessionEndReason("None", "").TryGetReject(out _), "None is not a refusal");
        Assert.IsFalse(new SessionEndReason("99", "").TryGetReject(out _), "numbers aren't names");
    }

    [Test]
    public void JoinCodesAreNormalized()
    {
        Assert.AreEqual("ABC12X", SessionKeys.NormalizeJoinCode("  ab-c 12x\n"));
        Assert.AreEqual("", SessionKeys.NormalizeJoinCode(null));
        Assert.AreEqual("", SessionKeys.NormalizeJoinCode("<>!@#"));
        Assert.AreEqual(SessionKeys.MaxJoinCode, SessionKeys.NormalizeJoinCode(new string('a', 40)).Length);
    }

    [Test]
    public void SecondWindowGetsADistinctDevKey()
    {
        var host = PlayerKey.Dev("p-1234");
        Assert.IsTrue(SessionKeys.TryDisambiguate(host, k => k == host, out var alt));
        Assert.AreEqual("dev:p-1234-2", alt.Value);
        Assert.IsTrue(SessionKeys.TryDisambiguate(host, k => k == host || k.Value == "dev:p-1234-2", out alt));
        Assert.AreEqual("dev:p-1234-3", alt.Value);

        var longKey = PlayerKey.Dev(new string('n', PlayerKey.MaxDevNameLength));
        Assert.IsTrue(SessionKeys.TryDisambiguate(longKey, k => k == longKey, out alt));
        Assert.LessOrEqual(alt.DevName.Length, PlayerKey.MaxDevNameLength);
        Assert.IsTrue(PlayerKey.TryParse(alt.Value, out _));

        Assert.IsFalse(SessionKeys.TryDisambiguate(PlayerKey.Steam(76561198000000001UL), k => true, out _), "Steam keys are never rewritten");
        Assert.IsFalse(SessionKeys.TryDisambiguate(host, k => true, out _), "gives up when everything is taken");
    }

    [Test]
    public void ServicesProfilesAreSafeNames()
    {
        Assert.AreEqual("s76561198000000001", SessionKeys.ServicesProfile(PlayerKey.Steam(76561198000000001UL)));
        Assert.AreEqual("d_editor", SessionKeys.ServicesProfile(PlayerKey.Dev("editor")));
        Assert.AreEqual("d_mppm_p2", SessionKeys.ServicesProfile(PlayerKey.Dev("mppm.p2")));
        Assert.LessOrEqual(SessionKeys.ServicesProfile(PlayerKey.Dev(new string('z', 48))).Length, 30);
        Assert.IsNull(SessionKeys.ServicesProfile(default));
    }
}
