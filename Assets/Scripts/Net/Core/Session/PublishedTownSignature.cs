using System;
using UnityEngine;

/// <summary>Published map identity, distinct from the intentionally permissive cosmetic content version.
/// The editor writes this resource only after the town scene and its derived navigation/collection data
/// have been validated and saved. Runtime never guesses a signature from a partial scene.</summary>
public sealed class PublishedTownSignature : ScriptableObject
{
    public const int Schema = 1;
    public const int ByteCount = 32;
    public const string ResourcePath = "City/PublishedTownSignature";
    public int schema = Schema;
    public string sha256;
    public int dependencyCount;

    public static byte[] CurrentBytes
    {
        get
        {
            var asset = Resources.Load<PublishedTownSignature>(ResourcePath);
            return BytesFrom(asset);
        }
    }

    /// <summary>A missing/unpublished resource is representable on the wire, but never approves a join.
    /// This keeps codec tests and offline tooling usable before a project's first publication.</summary>
    public static byte[] BytesFrom(PublishedTownSignature asset) =>
        asset && asset.schema == Schema && TryParse(asset.sha256, out var bytes) ? bytes : new byte[ByteCount];

    public static bool TryParse(string text, out byte[] bytes)
    {
        bytes = null;
        if (text == null || text.Length != ByteCount * 2) return false;
        var result = new byte[ByteCount];
        for (int i = 0; i < result.Length; i++)
        {
            int high = Hex(text[i * 2]), low = Hex(text[i * 2 + 1]);
            if (high < 0 || low < 0) return false;
            result[i] = (byte)((high << 4) | low);
        }
        if (!IsPublished(result)) return false;
        bytes = result; return true;
    }

    public static bool IsPublished(byte[] bytes)
    {
        if (bytes == null || bytes.Length != ByteCount) return false;
        for (int i = 0; i < bytes.Length; i++) if (bytes[i] != 0) return true;
        return false;
    }

    /// <summary>Pure approval policy. Content changes alone are not a mismatch: world layout is a separate
    /// mandatory contract. Missing manifests fail closed, even when both peers are missing one.</summary>
    public static JoinReject CheckCompatibility(in JoinRequest request, byte[] hostSignature)
    {
        if (request.proto != NetVersion.Protocol) return JoinReject.ProtocolMismatch;
        if (!IsPublished(hostSignature) || !IsPublished(request.worldSignature)) return JoinReject.TownNotPublished;
        int difference = 0;
        for (int i = 0; i < ByteCount; i++) difference |= hostSignature[i] ^ request.worldSignature[i];
        return difference == 0 ? JoinReject.None : JoinReject.TownMapMismatch;
    }

    static int Hex(char c) => c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
}
