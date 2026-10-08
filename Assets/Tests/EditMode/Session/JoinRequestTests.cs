using System;
using NUnit.Framework;

/// <summary>B1 gate: the connection-approval payload (Docs/Multiplayer.md §2.4, §8.7.3).</summary>
public class JoinRequestTests
{
    static byte[] Bytes(int n, byte fill)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)(fill + i);
        return b;
    }

    static JoinRequest Full(int ticketBytes)
    {
        var r = JoinRequest.ForThisBuild(PlayerKey.Dev(new string('k', PlayerKey.MaxDevNameLength)));
        r.authTicket = Bytes(ticketBytes, 7);
        r.loadout = Bytes(JoinRequest.MaxLoadoutBytes, 3);
        r.choice = 2;
        r.companyGuid = new string('g', JoinRequest.MaxGuidBytes);
        r.companyVersion = long.MaxValue - 5;
        r.blobHash = Bytes(JoinRequest.HashBytes, 1);
        r.flags = 0xA5;
        return r;
    }

    [Test]
    public void RoundTrip()
    {
        var r = JoinRequest.ForThisBuild(PlayerKey.Steam(76561198000000001UL));
        r.authTicket = Bytes(240, 9);
        r.loadout = Bytes(12, 2);
        r.choice = 1;
        r.companyGuid = "6f1c2d3e-aaaa-bbbb-cccc-0123456789ab";
        r.companyVersion = 42;
        r.blobHash = Bytes(32, 5);
        r.flags = 3;
        byte[] data = r.Encode();
        Assert.IsTrue(JoinRequest.TryDecode(data, out var d, out var why), $"decode failed: {why}");
        Assert.AreEqual(JoinReject.None, why);
        Assert.AreEqual(NetVersion.Protocol, d.proto);
        Assert.AreEqual(NetVersion.Content, d.content);
        Assert.AreEqual(r.playerKey, d.playerKey);
        CollectionAssert.AreEqual(r.authTicket, d.authTicket);
        CollectionAssert.AreEqual(r.loadout, d.loadout);
        Assert.AreEqual(1, d.choice);
        Assert.AreEqual(r.companyGuid, d.companyGuid);
        Assert.AreEqual(42L, d.companyVersion);
        CollectionAssert.AreEqual(r.blobHash, d.blobHash);
        CollectionAssert.AreEqual(r.worldSignature, d.worldSignature);
        Assert.AreEqual(3, d.flags);
    }

    [Test]
    public void MinimalDevRequestRoundTrips()
    {
        byte[] data = JoinRequest.ForThisBuild(PlayerKey.Dev("bot-2")).Encode();
        Assert.Less(data.Length, 96);
        Assert.IsTrue(JoinRequest.TryDecode(data, out var d, out _));
        Assert.AreEqual("dev:bot-2", d.playerKey);
        Assert.AreEqual(0, d.authTicket.Length);
        Assert.AreEqual(0, d.blobHash.Length);
    }

    [Test]
    public void At1200BytesItFitsAndOneMoreIsRefused()
    {
        // Every other capped field at its cap; the ticket fills the rest exactly.
        int baseBytes = Full(0).Encode().Length;
        Assert.Less(baseBytes, 300);
        var max = Full(NetConstants.JoinRequestMaxBytes - baseBytes);
        byte[] data = max.Encode();
        Assert.AreEqual(NetConstants.JoinRequestMaxBytes, data.Length);
        Assert.IsTrue(JoinRequest.TryDecode(data, out var d, out _));
        Assert.AreEqual(0xA5, d.flags);

        var over = Full(NetConstants.JoinRequestMaxBytes - baseBytes + 1);
        Assert.Throws<ArgumentException>(() => over.Encode(), "encode refuses to build a request over 1,200 B");

        var big = new byte[NetConstants.JoinRequestMaxBytes + 1];
        Array.Copy(data, big, data.Length);
        Assert.IsFalse(JoinRequest.TryDecode(big, out _, out var why));
        Assert.AreEqual(JoinReject.BadRequest, why);
    }

    [Test]
    public void FieldCapsAreEnforced()
    {
        var r = JoinRequest.ForThisBuild(PlayerKey.Dev("a"));
        r.loadout = new byte[JoinRequest.MaxLoadoutBytes + 1];
        Assert.Throws<ArgumentException>(() => r.Encode());
        r.loadout = null;
        r.blobHash = new byte[31];
        Assert.Throws<ArgumentException>(() => r.Encode());
        r.blobHash = null;
        r.authTicket = new byte[JoinRequest.MaxTicketBytes + 1];
        Assert.Throws<ArgumentException>(() => r.Encode());
        r.authTicket = null;
        r.worldSignature = new byte[31];
        Assert.Throws<ArgumentException>(() => r.Encode());
    }

    [Test]
    public void BadHeaderIsBadRequest()
    {
        byte[] data = JoinRequest.ForThisBuild(PlayerKey.Dev("a")).Encode();
        data[0] = (byte)'X';
        Assert.IsFalse(JoinRequest.TryDecode(data, out _, out var why));
        Assert.AreEqual(JoinReject.BadRequest, why);
        Assert.IsFalse(JoinRequest.TryDecode(null, out _, out why));
        Assert.AreEqual(JoinReject.BadRequest, why);
        Assert.IsFalse(JoinRequest.TryDecode(new byte[0], out _, out why));
        Assert.AreEqual(JoinReject.BadRequest, why);
    }

    [Test]
    public void OtherProtocolIsProtocolMismatch()
    {
        var r = JoinRequest.ForThisBuild(PlayerKey.Dev("old-build"));
        r.proto = 999;
        Assert.IsFalse(JoinRequest.TryDecode(r.Encode(), out var d, out var why));
        Assert.AreEqual(JoinReject.ProtocolMismatch, why);
        Assert.AreEqual(999, d.proto, "the host can log which version knocked");
        StringAssert.StartsWith("Update your game", JoinRejectText.For(why));
    }

    [Test]
    public void EveryTruncationIsRefusedWithoutThrowing()
    {
        byte[] data = Full(300).Encode();
        for (int n = 0; n < data.Length; n++)
        {
            var cut = new byte[n];
            Array.Copy(data, cut, n);
            Assert.IsFalse(JoinRequest.TryDecode(cut, out _, out var why), $"truncated at {n}");
            Assert.AreEqual(JoinReject.BadRequest, why, $"truncated at {n}");
        }
    }

    [Test]
    public void TrailingBytesAreRefused()
    {
        byte[] data = JoinRequest.ForThisBuild(PlayerKey.Dev("a")).Encode();
        var longer = new byte[data.Length + 1];
        Array.Copy(data, longer, data.Length);
        Assert.IsFalse(JoinRequest.TryDecode(longer, out _, out var why));
        Assert.AreEqual(JoinReject.BadRequest, why);
    }

    [Test]
    public void LyingLengthsAreRefused()
    {
        byte[] data = JoinRequest.ForThisBuild(PlayerKey.Dev("abc")).Encode();
        data[8] = 200; // key length byte claims 200 bytes (cap 64)
        Assert.IsFalse(JoinRequest.TryDecode(data, out _, out var why));
        Assert.AreEqual(JoinReject.BadRequest, why);
    }
}
