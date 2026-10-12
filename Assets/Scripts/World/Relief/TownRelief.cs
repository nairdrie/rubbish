using System;
using UnityEngine;

/// <summary>
/// The town's baked height grid (Docs/CityAuthoring.md, "Terrain relief"). The town is authored flat; the bake adds
/// <c>Height(x, z)</c> to everything that stands on the ground, and runtime code that used to assume y = 0 asks here.
/// A cell is two triangles (the diagonal alternates from cell to cell), exactly like the terrain mesh, so a point lifted
/// by <see cref="Height(float,float)"/> lies ON the visible ground, not merely near it.
/// </summary>
public sealed class TownReliefField
{
    public const uint Magic = 0x464C5254;      // "TRLF"
    public const int Version = 1;
    /// <summary>Stored height resolution. Heights are rounded to it before anything is placed, so the Editor that
    /// bakes and the game that loads see the same numbers.</summary>
    public const float Step = 1f / 512f;

    public float x0, z0, cell;
    public int nx, nz;
    public float min;
    public float[] h;

    public TownReliefField(float x0, float z0, float cell, int nx, int nz)
    {
        if (nx < 2 || nz < 2 || !(cell > 0f)) throw new ArgumentException("A relief grid needs at least 2 x 2 samples and a positive cell.");
        this.x0 = x0; this.z0 = z0; this.cell = cell; this.nx = nx; this.nz = nz; h = new float[nx * nz];
    }

    public float At(int ix, int iz) => h[Mathf.Clamp(iz, 0, nz - 1) * nx + Mathf.Clamp(ix, 0, nx - 1)];
    public float WorldX(int ix) => x0 + ix * cell;
    public float WorldZ(int iz) => z0 + iz * cell;

    void Locate(float x, float z, out int ix, out int iz, out float u, out float v)
    {
        float fx = Mathf.Clamp((x - x0) / cell, 0f, nx - 1f), fz = Mathf.Clamp((z - z0) / cell, 0f, nz - 1f);
        ix = Mathf.Min((int)fx, nx - 2); iz = Mathf.Min((int)fz, nz - 2);
        u = fx - ix; v = fz - iz;
    }

    /// <summary>Ground height at a world position; outside the grid the nearest edge value.</summary>
    public float Height(float x, float z)
    {
        Locate(x, z, out int ix, out int iz, out float u, out float v);
        int i = iz * nx + ix;
        float a = h[i], b = h[i + 1], c = h[i + nx], d = h[i + nx + 1];
        if (Flipped(ix, iz)) return u + v <= 1f ? a + (b - a) * u + (c - a) * v : d + (c - d) * (1f - u) + (b - d) * (1f - v);
        return v >= u ? a + (d - c) * u + (c - a) * v : a + (b - a) * u + (d - b) * v;
    }

    /// <summary>Cells alternate their diagonal like a chessboard, so slopes facet into diamonds, not one-way slashes.</summary>
    public static bool Flipped(int ix, int iz) => ((ix + iz) & 1) == 1;

    /// <summary>(dH/dx, dH/dz) of the facet under a world position.</summary>
    public Vector2 Gradient(float x, float z)
    {
        Locate(x, z, out int ix, out int iz, out float u, out float v);
        int i = iz * nx + ix;
        float a = h[i], b = h[i + 1], c = h[i + nx], d = h[i + nx + 1];
        if (Flipped(ix, iz)) return u + v <= 1f ? new Vector2((b - a) / cell, (c - a) / cell) : new Vector2((d - c) / cell, (d - b) / cell);
        return v >= u ? new Vector2((d - c) / cell, (c - a) / cell) : new Vector2((b - a) / cell, (d - b) / cell);
    }

    public bool Contains(float x, float z) => x >= x0 && z >= z0 && x <= x0 + (nx - 1) * cell && z <= z0 + (nz - 1) * cell;

    public Vector3 Normal(float x, float z)
    {
        var g = Gradient(x, z);
        return new Vector3(-g.x, 1f, -g.y).normalized;
    }

    /// <summary>Rounds every height to <see cref="Step"/>. Idempotent. Call before the grid is used or stored.</summary>
    public void Quantise()
    {
        min = float.MaxValue;
        for (int i = 0; i < h.Length; i++) { h[i] = Mathf.Round(h[i] / Step) * Step; if (h[i] < min) min = h[i]; }
    }

    public byte[] ToBytes()
    {
        Quantise();
        var bytes = new byte[32 + h.Length * 2];
        Buffer.BlockCopy(BitConverter.GetBytes(Magic), 0, bytes, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(Version), 0, bytes, 4, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(x0), 0, bytes, 8, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(z0), 0, bytes, 12, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(cell), 0, bytes, 16, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(nx), 0, bytes, 20, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(nz), 0, bytes, 24, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(min), 0, bytes, 28, 4);
        for (int i = 0; i < h.Length; i++)
        {
            int q = Mathf.RoundToInt((h[i] - min) / Step);
            if (q < 0 || q > ushort.MaxValue) throw new InvalidOperationException("Relief height range exceeds " + ushort.MaxValue * Step + " m.");
            bytes[32 + i * 2] = (byte)(q & 255); bytes[33 + i * 2] = (byte)(q >> 8);
        }
        return bytes;
    }

    public static TownReliefField FromBytes(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 32 || BitConverter.ToUInt32(bytes, 0) != Magic || BitConverter.ToInt32(bytes, 4) != Version) return null;
        int nx = BitConverter.ToInt32(bytes, 20), nz = BitConverter.ToInt32(bytes, 24);
        if (nx < 2 || nz < 2 || (long)nx * nz * 2 + 32 != bytes.Length) return null;
        var field = new TownReliefField(BitConverter.ToSingle(bytes, 8), BitConverter.ToSingle(bytes, 12), BitConverter.ToSingle(bytes, 16), nx, nz);
        field.min = BitConverter.ToSingle(bytes, 28);
        for (int i = 0; i < field.h.Length; i++) field.h[i] = field.min + (bytes[32 + i * 2] | bytes[33 + i * 2] << 8) * Step;
        return field;
    }
}

/// <summary>Runtime access to the published relief. Without a baked grid (or while the Editor's flat bake phase has
/// it <see cref="Suspended"/>) every height is 0, i.e. the old flat town.</summary>
public static class TownRelief
{
    public const string ResourcePath = "City/TownRelief";
    public const string AssetPath = "Assets/Resources/City/TownRelief.bytes";

    static TownReliefField field;
    static bool loaded;

    /// <summary>Editor bake only: the layout code runs in a flat world and the relief is applied as the last step.</summary>
    public static bool Suspended;

    public static TownReliefField Field { get { Ensure(); return Suspended ? null : field; } }
    public static bool Flat => Field == null;

    static void Ensure()
    {
        if (loaded) return;
        loaded = true;
        var asset = Resources.Load<TextAsset>(ResourcePath);
        field = asset != null ? TownReliefField.FromBytes(asset.bytes) : null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { loaded = false; field = null; Suspended = false; }

    /// <summary>Forget the cached grid (the Editor calls this after writing or rolling back the asset).</summary>
    public static void Reload() { loaded = false; field = null; }
    /// <summary>Use a grid that is not on disk yet (the bake, tests).</summary>
    public static void Use(TownReliefField value) { field = value; loaded = true; }

    /// <summary>Ground height at a town position. Anything outside the town's grid (the depot interiors sit far south
    /// of it, under the map) is on level ground at 0.</summary>
    public static float Height(float x, float z) { var f = Field; return f != null && f.Contains(x, z) ? f.Height(x, z) : 0f; }
    /// <summary>True for points of the outdoor town (inside the grid and not down in an interior).</summary>
    public static bool Covers(Vector3 p) { var f = Field; return f != null && p.y > -5.5f && f.Contains(p.x, p.z); }
    public static float Height(Vector3 p) => Height(p.x, p.z);
    /// <summary>The same point <paramref name="above"/> metres over the ground, whatever its y was.</summary>
    public static Vector3 OnGround(Vector3 p, float above = 0f) { p.y = Height(p.x, p.z) + above; return p; }
    /// <summary>A point given in flat-town coordinates (y measured from the old y = 0 ground) moved onto the relief.</summary>
    public static Vector3 Lift(Vector3 p) { p.y += Height(p.x, p.z); return p; }
    public static Vector3 Normal(float x, float z) { var f = Field; return f != null && f.Contains(x, z) ? f.Normal(x, z) : Vector3.up; }
    public static Vector3 Normal(Vector3 p) => Normal(p.x, p.z);
    /// <summary>Rotation that stands something with the given heading on the slope under it.</summary>
    public static Quaternion Aligned(Vector3 p, Quaternion heading)
    {
        var n = Normal(p.x, p.z);
        return Quaternion.FromToRotation(Vector3.up, n) * heading;
    }
    /// <summary>The point u of the way from a to b that keeps the ends' height above the ground. Waypoints stand on
    /// the relief; a straight line between two of them would cut through a rise or float over a dip.</summary>
    public static Vector3 Between(Vector3 a, Vector3 b, float u)
    {
        var p = Vector3.LerpUnclamped(a, b, u);
        var f = Field;
        if (f == null || p.y < -5.5f || !f.Contains(p.x, p.z)) return p;
        p.y = f.Height(p.x, p.z) + Mathf.LerpUnclamped(a.y - Height(a.x, a.z), b.y - Height(b.x, b.z), u);
        return p;
    }
    /// <summary>Rotation of a vehicle with the given heading resting on the ground: measured over its own length and
    /// width, so it does not twitch at every terrain crease.</summary>
    public static Quaternion Aligned(Vector3 p, float yawDeg, float halfLength = 1.6f, float halfWidth = 0.8f)
    {
        var heading = Quaternion.Euler(0f, yawDeg, 0f);
        var f = Field;
        if (f == null || p.y < -5.5f || !f.Contains(p.x, p.z)) return heading;
        Vector3 fwd = heading * Vector3.forward, right = heading * Vector3.right;
        float rise = Height(p + fwd * halfLength) - Height(p - fwd * halfLength), bank = Height(p + right * halfWidth) - Height(p - right * halfWidth);
        if (Mathf.Abs(rise) < 1e-4f && Mathf.Abs(bank) < 1e-4f) return heading;
        Vector3 forward = (fwd * (2f * halfLength) + Vector3.up * rise).normalized, side = (right * (2f * halfWidth) + Vector3.up * bank).normalized;
        return Quaternion.LookRotation(forward, Vector3.Cross(forward, side));
    }
    /// <summary>Height of a point above the ground under it (its y in the old flat town).</summary>
    public static float Above(Vector3 p) => p.y - Height(p.x, p.z);
    /// <summary>The terrain, a carriageway, a pavement: the surfaces the town stands on. Clearance checks that used to
    /// skip "anything no higher than the kerb" skip these by identity now that they climb hills.</summary>
    public static bool IsGroundSurface(Collider c)
    {
        if (c == null || c.gameObject.layer != 6 || !(c is MeshCollider)) return false;
        string n = c.name;
        return n.StartsWith("Relief terrain", StringComparison.Ordinal) || n == "Coastal terrain" || n.StartsWith("Road_", StringComparison.Ordinal) ||
               n.StartsWith("Sidewalk_", StringComparison.Ordinal) || n.StartsWith("River walk", StringComparison.Ordinal);
    }
    /// <summary>Top of a collider measured from the ground under its middle (so "is it low?" works on a slope).</summary>
    public static float TopAboveGround(Collider c) { var b = c.bounds; return b.max.y - Height(b.center.x, b.center.z); }
    /// <summary>Slope under a point, in degrees from level.</summary>
    public static float SlopeDegrees(Vector3 p) => Vector3.Angle(Vector3.up, Normal(p.x, p.z));
}
