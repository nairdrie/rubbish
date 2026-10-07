using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// WorldLife W0 CONTRACT: the spatial hash of every <see cref="ILifeBody"/> shown on this machine (§4.4: the 16 m hash
/// that the sensor, car following / yielding, the horn and avoidance query). Uniform XZ grid, 16 m cells.
///
///  - Bodies <see cref="Register"/> when they appear and <see cref="Unregister"/> when they go (pool return, destroy).
///  - <see cref="Sync"/> re-bins bodies whose cell changed; the runner calls it once per frame (LateUpdate) and after a
///    director's Tick, so queries see this frame's positions within a cell. Call it yourself after teleporting a body.
///  - Queries read live positions through the interface and test against them, so a body that moved inside its cell
///    since the last Sync is still found; candidates are gathered from cells padded by <see cref="QueryMargin"/>.
/// No allocations after warm-up (pooled cell lists, caller-owned result lists). Main thread only.
/// </summary>
public static class LifeBodies
{
    public const float CellSize = 16f;
    /// <summary>Extra metres added to every query's cell range (bodies that moved since the last Sync).</summary>
    public const float QueryMargin = 2f;

    sealed class Entry
    {
        public ILifeBody body;
        public long cell;
        public bool binned;
    }

    static readonly List<Entry> entries = new List<Entry>(128);
    static readonly Dictionary<ILifeBody, Entry> byBody = new Dictionary<ILifeBody, Entry>(128);
    static readonly Dictionary<uint, ILifeBody> byId = new Dictionary<uint, ILifeBody>(128);
    static readonly Dictionary<long, List<Entry>> cells = new Dictionary<long, List<Entry>>(256);
    static readonly Stack<List<Entry>> cellPool = new Stack<List<Entry>>();
    static readonly Stack<Entry> entryPool = new Stack<Entry>();
    static readonly List<ILifeBody> allView = new List<ILifeBody>(128);
    static bool allDirty = true;

    public static int Count => entries.Count;

    /// <summary>Every registered body (a cached view; don't hold it across frames).</summary>
    public static IReadOnlyList<ILifeBody> All
    {
        get
        {
            if (allDirty)
            {
                allView.Clear();
                for (int i = 0; i < entries.Count; i++) allView.Add(entries[i].body);
                allDirty = false;
            }
            return allView;
        }
    }

    // ------------------------------------------------------------------ registration

    public static void Register(ILifeBody body)
    {
        if (body == null || byBody.ContainsKey(body)) return;
        var e = entryPool.Count > 0 ? entryPool.Pop() : new Entry();
        e.body = body; e.binned = false; e.cell = 0;
        entries.Add(e);
        byBody[body] = e;
        var id = body.Id;
        if (id.IsValid)
        {
            if (byId.TryGetValue(id.Packed, out var other) && !ReferenceEquals(other, body))
                Debug.LogWarning($"[LifeBodies] Two bodies share id {id}; Find returns the newest.");
            byId[id.Packed] = body;
        }
        Bin(e);
        allDirty = true;
    }

    public static void Unregister(ILifeBody body)
    {
        if (body == null || !byBody.TryGetValue(body, out var e)) return;
        Unbin(e);
        byBody.Remove(body);
        entries.Remove(e);
        // The id map may hold a newer body with the same id: only drop our own mapping. If an older body with the same id
        // is still registered, it becomes findable again (G: Find never loses a live body).
        uint droppedKey = 0;
        bool dropped = false;
        foreach (var kv in byId)
        {
            if (!ReferenceEquals(kv.Value, body)) continue;
            droppedKey = kv.Key;
            dropped = true;
            break;
        }
        if (dropped)
        {
            byId.Remove(droppedKey);
            for (int i = 0; i < entries.Count; i++)
            {
                var other = entries[i].body;
                if (other != null && other.Id.IsValid && other.Id.Packed == droppedKey) { byId[droppedKey] = other; break; }
            }
        }
        e.body = null;
        entryPool.Push(e);
        allDirty = true;
    }

    public static bool IsRegistered(ILifeBody body) => body != null && byBody.ContainsKey(body);

    /// <summary>The registered body with this id on this machine, or null (not shown here).</summary>
    public static ILifeBody Find(LifeId id) => id.IsValid && byId.TryGetValue(id.Packed, out var b) ? b : null;

    /// <summary>Forget everything (the runner calls this when WorldLife sleeps).</summary>
    public static void Clear()
    {
        foreach (var kv in cells) { kv.Value.Clear(); cellPool.Push(kv.Value); }
        cells.Clear();
        foreach (var e in entries) { e.body = null; entryPool.Push(e); }
        entries.Clear();
        byBody.Clear();
        byId.Clear();
        allView.Clear();
        allDirty = true;
        maxFootprint = 1f;
    }

    // ------------------------------------------------------------------ binning

    static long CellKey(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;

    static bool CellOf(Vector3 p, out long key)
    {
        if (float.IsNaN(p.x) || float.IsNaN(p.z) || Mathf.Abs(p.x) > 1e6f || Mathf.Abs(p.z) > 1e6f) { key = 0; return false; }
        key = CellKey(Mathf.FloorToInt(p.x / CellSize), Mathf.FloorToInt(p.z / CellSize));
        return true;
    }

    static float maxFootprint = 1f;

    static void Bin(Entry e)
    {
        Vector3 he = e.body.HalfExtents;
        float fp = Mathf.Sqrt(he.x * he.x + he.z * he.z);
        if (fp > maxFootprint) maxFootprint = fp;
        if (!CellOf(e.body.Position, out long key)) { Unbin(e); return; }
        if (e.binned && e.cell == key) return;
        Unbin(e);
        if (!cells.TryGetValue(key, out var list))
        {
            list = cellPool.Count > 0 ? cellPool.Pop() : new List<Entry>(16);
            cells[key] = list;
        }
        list.Add(e);
        e.cell = key;
        e.binned = true;
    }

    static void Unbin(Entry e)
    {
        if (!e.binned) return;
        e.binned = false;
        if (!cells.TryGetValue(e.cell, out var list)) return;
        list.Remove(e);
        if (list.Count == 0)
        {
            cells.Remove(e.cell);
            cellPool.Push(list);
        }
    }

    /// <summary>Re-bin every body whose cell changed. Cheap: one position read per body.</summary>
    public static void Sync()
    {
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var e = entries[i];
            var b = e.body;
            // A destroyed MonoBehaviour body that forgot to unregister: drop it.
            if (ReferenceEquals(b, null) || (b is UnityEngine.Object uo && uo == null)) { DropAt(i); continue; }
            Bin(e);
        }
    }

    static void DropAt(int i)
    {
        var e = entries[i];
        Unbin(e);
        if (!ReferenceEquals(e.body, null)) byBody.Remove(e.body);
        uint deadId = 0; bool found = false;
        foreach (var kv in byId) if (ReferenceEquals(kv.Value, e.body)) { deadId = kv.Key; found = true; break; }
        if (found) byId.Remove(deadId);
        entries.RemoveAt(i);
        e.body = null;
        entryPool.Push(e);
        allDirty = true;
    }

    // ------------------------------------------------------------------ queries

    /// <summary>
    /// Bodies whose XZ bounding circle (radius √(halfWidth² + halfLength²)) comes within <paramref name="radius"/> of
    /// <paramref name="center"/> (XZ distance). Appends to <paramref name="results"/> (not cleared); returns how many it added.
    /// </summary>
    public static int Query(Vector3 center, float radius, List<ILifeBody> results, LifeBodyKind mask = LifeBodyKind.All, bool hittableOnly = false)
    {
        if (results == null || entries.Count == 0) return 0;
        int added = 0;
        float reach = radius + QueryMargin + maxFootprint;   // + the largest body's bounding radius (a limo)
        int x0 = Mathf.FloorToInt((center.x - reach) / CellSize), x1 = Mathf.FloorToInt((center.x + reach) / CellSize);
        int z0 = Mathf.FloorToInt((center.z - reach) / CellSize), z1 = Mathf.FloorToInt((center.z + reach) / CellSize);
        for (int cx = x0; cx <= x1; cx++)
        for (int cz = z0; cz <= z1; cz++)
        {
            if (!cells.TryGetValue(CellKey(cx, cz), out var list)) continue;
            for (int i = 0; i < list.Count; i++)
            {
                var b = list[i].body;
                if (b == null || (b.BodyKind & mask) == 0) continue;
                if (hittableOnly && !b.Hittable) continue;
                Vector3 p = b.Position;
                Vector3 he = b.HalfExtents;
                float r = radius + Mathf.Sqrt(he.x * he.x + he.z * he.z);
                float dx = p.x - center.x, dz = p.z - center.z;
                if (dx * dx + dz * dz > r * r) continue;
                results.Add(b);
                added++;
            }
        }
        return added;
    }

    /// <summary>
    /// Bodies whose box overlaps an oriented box (e.g. a <see cref="TruckSweep"/>), each side padded by
    /// <paramref name="pad"/>. Yaw-only test on XZ (separating axes) plus a vertical overlap. Appends to
    /// <paramref name="results"/>; returns how many it added.
    /// </summary>
    public static int QueryBox(Vector3 center, Quaternion rotation, Vector3 halfExtents, List<ILifeBody> results,
                               LifeBodyKind mask = LifeBodyKind.All, float pad = 0f, bool hittableOnly = true)
    {
        if (results == null || entries.Count == 0) return 0;
        float reach = Mathf.Sqrt(halfExtents.x * halfExtents.x + halfExtents.z * halfExtents.z) + pad;
        int before = results.Count;
        Query(center, reach, results, mask, hittableOnly);
        // Narrow phase on the candidates just added (compact in place).
        Vector3 af = rotation * Vector3.forward; af.y = 0f;
        af = af.sqrMagnitude > 1e-6f ? af.normalized : Vector3.forward;
        Vector2 aZ = new Vector2(af.x, af.z), aX = new Vector2(af.z, -af.x);
        Vector2 ac = new Vector2(center.x, center.z);
        float ahx = halfExtents.x + pad, ahz = halfExtents.z + pad;
        float aMinY = center.y - halfExtents.y - pad, aMaxY = center.y + halfExtents.y + pad;
        int w = before;
        for (int i = before; i < results.Count; i++)
        {
            var b = results[i];
            if (OverlapsBox(b, ac, aX, aZ, ahx, ahz, aMinY, aMaxY)) results[w++] = b;
        }
        results.RemoveRange(w, results.Count - w);
        return w - before;
    }

    static bool OverlapsBox(ILifeBody b, Vector2 ac, Vector2 aX, Vector2 aZ, float ahx, float ahz, float aMinY, float aMaxY)
    {
        Vector3 p = b.Position;
        Vector3 he = b.HalfExtents;
        if (p.y + 2f * he.y < aMinY || p.y > aMaxY) return false;
        Vector3 bf = b.Rotation * Vector3.forward; bf.y = 0f;
        bf = bf.sqrMagnitude > 1e-6f ? bf.normalized : Vector3.forward;
        Vector2 bZ = new Vector2(bf.x, bf.z), bX = new Vector2(bf.z, -bf.x);
        Vector2 d = new Vector2(p.x, p.z) - ac;
        return !Separated(aX, d, aX, aZ, ahx, ahz, bX, bZ, he.x, he.z)
            && !Separated(aZ, d, aX, aZ, ahx, ahz, bX, bZ, he.x, he.z)
            && !Separated(bX, d, aX, aZ, ahx, ahz, bX, bZ, he.x, he.z)
            && !Separated(bZ, d, aX, aZ, ahx, ahz, bX, bZ, he.x, he.z);
    }

    static bool Separated(Vector2 axis, Vector2 d, Vector2 aX, Vector2 aZ, float ahx, float ahz, Vector2 bX, Vector2 bZ, float bhx, float bhz)
    {
        float ra = Mathf.Abs(Vector2.Dot(aX, axis)) * ahx + Mathf.Abs(Vector2.Dot(aZ, axis)) * ahz;
        float rb = Mathf.Abs(Vector2.Dot(bX, axis)) * bhx + Mathf.Abs(Vector2.Dot(bZ, axis)) * bhz;
        return Mathf.Abs(Vector2.Dot(d, axis)) > ra + rb;
    }

    /// <summary>The nearest body (XZ) within <paramref name="maxDistance"/>, or null.</summary>
    public static ILifeBody Nearest(Vector3 p, float maxDistance, LifeBodyKind mask = LifeBodyKind.All, ILifeBody except = null)
    {
        scratch.Clear();
        Query(p, maxDistance, scratch, mask);
        ILifeBody best = null;
        float bestD = float.MaxValue;
        for (int i = 0; i < scratch.Count; i++)
        {
            var b = scratch[i];
            if (ReferenceEquals(b, except)) continue;
            Vector3 q = b.Position;
            float dx = q.x - p.x, dz = q.z - p.z, d = dx * dx + dz * dz;
            if (d < bestD) { bestD = d; best = b; }
        }
        scratch.Clear();
        return best;
    }
    static readonly List<ILifeBody> scratch = new List<ILifeBody>(32);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Clear();
        entryPool.Clear();
        cellPool.Clear();
    }
}
