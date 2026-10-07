using System.Collections.Generic;
using NUnit.Framework;

/// <summary>B10: the <c>rb_*</c> lobby data codec (Docs/Multiplayer.md §8.7.4). Lobby data is another player's text.</summary>
public class LobbyInfoTests
{
    const ulong Lobby = 109775241045848137UL;
    const ulong Owner = 76561197960287930UL;

    static Dictionary<string, string> Store(LobbyInfo info)
    {
        var d = new Dictionary<string, string>();
        foreach (var kv in info.Encode()) d[kv.Key] = kv.Value;
        return d;
    }

    static System.Func<string, string> Getter(Dictionary<string, string> d) => k => d.TryGetValue(k, out var v) ? v : "";

    [Test]
    public void RoundTripToRelayTarget()
    {
        var host = LobbyInfo.ForRelayHost(" abc123 ", "Maple Hollow", "s-1", "Nick");
        var d = Store(host);
        Assert.AreEqual(LobbyInfo.Keys.Length, d.Count);
        Assert.AreEqual("rubbish", d[LobbyInfo.KeyGame]);
        Assert.AreEqual("relay", d[LobbyInfo.KeyNet]);
        Assert.AreEqual("ABC123", d[LobbyInfo.KeyRelay]);
        Assert.AreEqual(NetVersion.Protocol.ToString(), d[LobbyInfo.KeyProto]);

        Assert.IsTrue(LobbyInfo.TryDecode(Getter(d), out var got, out string err), err);
        Assert.AreEqual("Maple Hollow", got.town);
        Assert.AreEqual("Nick", got.hostName);
        Assert.IsTrue(got.TryGetJoinTarget(Lobby, Owner, out JoinTarget t, out JoinReject why, out err), err);
        Assert.AreEqual(JoinReject.None, why);
        Assert.AreEqual(NetMode.UnityRelay, t.mode);
        Assert.AreEqual("ABC123", t.joinCode);
        Assert.AreEqual(Lobby, t.steamLobbyId);
        Assert.AreEqual(Owner, t.hostSteamId);
    }

    [Test]
    public void OtherGamesOn480AreRejected()
    {
        var d = Store(LobbyInfo.ForRelayHost("ABC123", null, null, null));
        d[LobbyInfo.KeyGame] = "spacewar";
        Assert.IsFalse(LobbyInfo.TryDecode(Getter(d), out _, out string err));
        StringAssert.Contains("different game", err);
        Assert.IsFalse(LobbyInfo.TryDecode(_ => "", out _, out err), "an empty or closed lobby");
        Assert.IsFalse(LobbyInfo.TryDecode(null, out _, out _));
    }

    [Test]
    public void ProtocolMismatchSaysUpdate()
    {
        var d = Store(LobbyInfo.ForRelayHost("ABC123", null, null, null));
        d[LobbyInfo.KeyProto] = "999";
        Assert.IsTrue(LobbyInfo.TryDecode(Getter(d), out var got, out _), "still decodes, so the player can be told why");
        Assert.IsFalse(got.TryGetJoinTarget(Lobby, Owner, out _, out JoinReject why, out string err));
        Assert.AreEqual(JoinReject.ProtocolMismatch, why);
        StringAssert.StartsWith("Update your game", err);
    }

    [Test]
    public void ContentMismatchIsFine()
    {
        var d = Store(LobbyInfo.ForRelayHost("ABC123", null, null, null));
        d[LobbyInfo.KeyContent] = "77";
        Assert.IsTrue(LobbyInfo.TryDecode(Getter(d), out var got, out _));
        Assert.IsTrue(got.TryGetJoinTarget(Lobby, Owner, out _, out _, out _));
    }

    [TestCase("", JoinReject.BadRequest)]
    [TestCase("carrier-pigeon", JoinReject.BadRequest)]
    [TestCase("sdr", JoinReject.ProtocolMismatch)]
    public void UnusableTransports(string net, JoinReject expected)
    {
        var d = Store(LobbyInfo.ForRelayHost("ABC123", null, null, null));
        d[LobbyInfo.KeyNet] = net;
        Assert.IsTrue(LobbyInfo.TryDecode(Getter(d), out var got, out _));
        Assert.IsFalse(got.TryGetJoinTarget(Lobby, Owner, out _, out JoinReject why, out string err));
        Assert.AreEqual(expected, why);
        Assert.IsNotEmpty(err);
    }

    [TestCase("")]
    [TestCase("abc")]
    [TestCase("ABC-12")]
    [TestCase("ABCDEFGHIJKLMNOP")]
    public void BadRelayCodes(string code)
    {
        var d = Store(LobbyInfo.ForRelayHost("ABC123", null, null, null));
        d[LobbyInfo.KeyRelay] = code;
        Assert.IsTrue(LobbyInfo.TryDecode(Getter(d), out var got, out _));
        Assert.IsFalse(got.TryGetJoinTarget(Lobby, Owner, out _, out JoinReject why, out _));
        Assert.AreEqual(JoinReject.BadRequest, why);
    }

    [Test]
    public void HostileTextIsCleaned()
    {
        var d = Store(LobbyInfo.ForRelayHost("ABC123", null, null, null));
        d[LobbyInfo.KeyName] = "<color=red>Nick</color>\n\u0007" + new string('x', 500);
        d[LobbyInfo.KeyTown] = "   ";
        d[LobbyInfo.KeyProto] = "1" + new string('0', 300);
        Assert.IsFalse(LobbyInfo.TryDecode(Getter(d), out _, out _), "an absurd protocol field is malformed");
        d[LobbyInfo.KeyProto] = NetVersion.Protocol.ToString();
        Assert.IsTrue(LobbyInfo.TryDecode(Getter(d), out var got, out _));
        Assert.IsNull(got.town);
        Assert.LessOrEqual(got.hostName.Length, LobbyInfo.MaxTextChars);
        StringAssert.DoesNotContain("<", got.hostName);
        StringAssert.DoesNotContain("\n", got.hostName);
    }

    [Test]
    public void GetterThatThrowsIsContained()
    {
        Assert.IsFalse(LobbyInfo.TryDecode(_ => throw new System.InvalidOperationException("steam went away"), out var got, out string err));
        Assert.IsNull(got);
        StringAssert.Contains("InvalidOperationException", err);
    }

    [Test]
    public void NetKeys()
    {
        Assert.AreEqual("relay", LobbyInfo.NetKeyFor(NetMode.UnityRelay));
        Assert.AreEqual("sdr", LobbyInfo.NetKeyFor(NetMode.SteamSdr));
        Assert.IsTrue(LobbyInfo.TryParseNet(" RELAY ", out NetMode m));
        Assert.AreEqual(NetMode.UnityRelay, m);
    }
}
