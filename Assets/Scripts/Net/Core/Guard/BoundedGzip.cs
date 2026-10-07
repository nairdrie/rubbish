using System;
using System.IO;
using System.IO.Compression;

/// <summary>
/// Gzip for payloads from other players (D12). <see cref="TryInflate"/> stops at <c>maxBytes</c>, so a 16 KB decompression
/// bomb that would inflate to 1 GB costs at most maxBytes of memory and returns false.
/// </summary>
public static class BoundedGzip
{
    public static byte[] Deflate(byte[] raw)
    {
        if (raw == null) throw new ArgumentNullException(nameof(raw));
        using (var ms = new MemoryStream(raw.Length / 2 + 64))
        {
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                gz.Write(raw, 0, raw.Length);
            return ms.ToArray();
        }
    }

    /// <summary>False (raw = null) for null/empty input, corrupt data, or output over <paramref name="maxBytes"/>.</summary>
    public static bool TryInflate(byte[] gz, int maxBytes, out byte[] raw)
    {
        raw = null;
        if (gz == null || gz.Length < 18 || maxBytes < 0) return false; // 18 = smallest valid gzip member
        if (gz[0] != 0x1F || gz[1] != 0x8B) return false;
        try
        {
            using (var input = new MemoryStream(gz, writable: false))
            using (var unzip = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream(Math.Min(maxBytes, Math.Max(256, gz.Length * 4))))
            {
                var buf = new byte[8192];
                int total = 0;
                while (true)
                {
                    int n = unzip.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    total += n;
                    if (total > maxBytes) return false;
                    output.Write(buf, 0, n);
                }
                raw = output.ToArray();
                return true;
            }
        }
        catch (InvalidDataException) { return false; }
        catch (IOException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (ArgumentException) { return false; }
    }
}
