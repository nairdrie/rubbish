using System;
using System.IO;
using System.Text;

/// <summary>
/// The connection-approval payload (Docs/Accounts.md §6.3 step 3): versions, PlayerKey, a host-bound ticket, a cosmetic loadout
/// and the join choice. <b>Never the company itself.</b> Encoded as <c>'RBJ1'</c> + little-endian fields, at most
/// <see cref="NetConstants.JoinRequestMaxBytes"/>. Hostile input: <see cref="TryDecode"/> bounds-checks everything.
/// </summary>
public struct JoinRequest
{
    public const int MaxKeyBytes = 64, MaxTicketBytes = 1024, MaxLoadoutBytes = 64, MaxGuidBytes = 64, HashBytes = 32;
    static readonly byte[] Magic = { (byte)'R', (byte)'B', (byte)'J', (byte)'1' };

    public ushort proto, content;
    /// <summary>≤64 bytes UTF-8; checked against the verified identity by the host, never trusted alone.</summary>
    public string playerKey;
    /// <summary>Host-bound Steam session ticket (empty for dev keys). Never log it.</summary>
    public byte[] authTicket;
    /// <summary>Cosmetics only, ≤64 bytes.</summary>
    public byte[] loadout;
    /// <summary>0 crew, 1 just-me, 2 bring (M5+).</summary>
    public byte choice;
    public string companyGuid;
    public long companyVersion;
    /// <summary>32 bytes (SHA-256 of the company blob) or empty.</summary>
    public byte[] blobHash;
    public byte flags;
    /// <summary>32-byte published-town SHA-256. All-zero means unpublished and cannot join multiplayer.</summary>
    public byte[] worldSignature;

    /// <summary>A request stamped with this build's versions.</summary>
    public static JoinRequest ForThisBuild(PlayerKey key) => new JoinRequest
    {
        proto = NetVersion.Protocol, content = NetVersion.Content, playerKey = key.Value,
        authTicket = Array.Empty<byte>(), loadout = Array.Empty<byte>(), blobHash = Array.Empty<byte>(),
        worldSignature = PublishedTownSignature.CurrentBytes,
    };

    /// <summary>Throws ArgumentException when a field is over its cap or the result exceeds JoinRequestMaxBytes.</summary>
    public byte[] Encode()
    {
        byte[] key = Encoding.UTF8.GetBytes(playerKey ?? "");
        byte[] guid = Encoding.UTF8.GetBytes(companyGuid ?? "");
        byte[] ticket = authTicket ?? Array.Empty<byte>();
        byte[] look = loadout ?? Array.Empty<byte>();
        byte[] hash = blobHash ?? Array.Empty<byte>();
        byte[] world = worldSignature ?? new byte[PublishedTownSignature.ByteCount];
        if (key.Length > MaxKeyBytes) throw new ArgumentException("playerKey over 64 bytes");
        if (guid.Length > MaxGuidBytes) throw new ArgumentException("companyGuid over 64 bytes");
        if (ticket.Length > MaxTicketBytes) throw new ArgumentException("authTicket over 1024 bytes");
        if (look.Length > MaxLoadoutBytes) throw new ArgumentException("loadout over 64 bytes");
        if (hash.Length != 0 && hash.Length != HashBytes) throw new ArgumentException("blobHash must be empty or 32 bytes");
        if (world.Length != PublishedTownSignature.ByteCount) throw new ArgumentException("worldSignature must be 32 bytes");

        using (var ms = new MemoryStream(256))
        using (var w = new BinaryWriter(ms))
        {
            w.Write(Magic);
            w.Write(proto);
            w.Write(content);
            w.Write((byte)key.Length); w.Write(key);
            w.Write((ushort)ticket.Length); w.Write(ticket);
            w.Write((byte)look.Length); w.Write(look);
            w.Write(choice);
            w.Write((byte)guid.Length); w.Write(guid);
            w.Write(companyVersion);
            w.Write((byte)hash.Length); w.Write(hash);
            w.Write(flags);
            w.Write(world);
            w.Flush();
            if (ms.Length > NetConstants.JoinRequestMaxBytes)
                throw new ArgumentException($"join request is {ms.Length} B (max {NetConstants.JoinRequestMaxBytes})");
            return ms.ToArray();
        }
    }

    /// <summary>
    /// Decode another machine's bytes. False with <see cref="JoinReject.BadRequest"/> for anything malformed, oversized or with
    /// trailing bytes; <see cref="JoinReject.ProtocolMismatch"/> when the header is fine but <c>proto</c> isn't this build's
    /// (then only <c>proto</c>/<c>content</c> are filled). Content-version policy is the approval code's call.
    /// </summary>
    public static bool TryDecode(byte[] data, out JoinRequest r, out JoinReject why)
    {
        r = default;
        why = JoinReject.BadRequest;
        if (data == null || data.Length < 8 || data.Length > NetConstants.JoinRequestMaxBytes) return false;
        for (int i = 0; i < 4; i++) if (data[i] != Magic[i]) return false;
        try
        {
            int p = 4;
            r.proto = ReadU16(data, ref p);
            r.content = ReadU16(data, ref p);
            if (r.proto != NetVersion.Protocol) { why = JoinReject.ProtocolMismatch; return false; }

            int keyLen = ReadU8(data, ref p);
            if (keyLen > MaxKeyBytes) return false;
            r.playerKey = Encoding.UTF8.GetString(ReadBytes(data, ref p, keyLen));
            int ticketLen = ReadU16(data, ref p);
            if (ticketLen > MaxTicketBytes) return false;
            r.authTicket = ReadBytes(data, ref p, ticketLen);
            int lookLen = ReadU8(data, ref p);
            if (lookLen > MaxLoadoutBytes) return false;
            r.loadout = ReadBytes(data, ref p, lookLen);
            r.choice = ReadU8(data, ref p);
            int guidLen = ReadU8(data, ref p);
            if (guidLen > MaxGuidBytes) return false;
            r.companyGuid = Encoding.UTF8.GetString(ReadBytes(data, ref p, guidLen));
            r.companyVersion = ReadI64(data, ref p);
            int hashLen = ReadU8(data, ref p);
            if (hashLen != 0 && hashLen != HashBytes) return false;
            r.blobHash = ReadBytes(data, ref p, hashLen);
            r.flags = ReadU8(data, ref p);
            r.worldSignature = ReadBytes(data, ref p, PublishedTownSignature.ByteCount);
            if (p != data.Length) return false; // trailing bytes
            why = JoinReject.None;
            return true;
        }
        catch (ArgumentOutOfRangeException) { r = default; return false; }
    }

    static byte ReadU8(byte[] d, ref int p)
    {
        if (p + 1 > d.Length) throw new ArgumentOutOfRangeException();
        return d[p++];
    }

    static ushort ReadU16(byte[] d, ref int p)
    {
        if (p + 2 > d.Length) throw new ArgumentOutOfRangeException();
        ushort v = (ushort)(d[p] | (d[p + 1] << 8));
        p += 2;
        return v;
    }

    static long ReadI64(byte[] d, ref int p)
    {
        if (p + 8 > d.Length) throw new ArgumentOutOfRangeException();
        long v = BitConverter.IsLittleEndian ? BitConverter.ToInt64(d, p) : ReverseI64(d, p);
        p += 8;
        return v;
    }

    static long ReverseI64(byte[] d, int p)
    {
        ulong v = 0;
        for (int i = 7; i >= 0; i--) v = (v << 8) | d[p + i];
        return unchecked((long)v);
    }

    static byte[] ReadBytes(byte[] d, ref int p, int n)
    {
        if (n < 0 || p + n > d.Length) throw new ArgumentOutOfRangeException();
        var b = new byte[n];
        Buffer.BlockCopy(d, p, b, 0, n);
        p += n;
        return b;
    }
}
