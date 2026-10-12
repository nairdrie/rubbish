// Made-up streets for TruckCall.Pick (Tools/truckcall_check/check.py compiles this against a cut-out of the real source).
using System;
using System.Collections.Generic;
using UnityEngine;

sealed class FakeWorld : TruckCall.IDropWorld
{
    public readonly List<(Vector2 a, Vector2 b, float half)> roads = new List<(Vector2, Vector2, float)>();
    public readonly List<Rect> walks = new List<Rect>(), water = new List<Rect>(), solids = new List<Rect>();
    public Rect land = new Rect(-1000, -1000, 2000, 2000);
    public TruckCall.DropBox box;
    public int surfaceCalls, clearCalls;

    public bool Surface(float x, float z, float nearY, bool wantAlong, out float y, out TruckCall.DropSurface kind, out Vector2 along)
    {
        surfaceCalls++;
        y = 0f; kind = TruckCall.DropSurface.Ground; along = Vector2.zero;
        var p = new Vector2(x, z);
        if (!land.Contains(p)) return false;
        foreach (var w in water) if (w.Contains(p)) return false;
        foreach (var r in walks) if (r.Contains(p)) kind = TruckCall.DropSurface.Walk;
        foreach (var r in roads)
        {
            Vector2 d = r.b - r.a; float t = Mathf.Clamp01(Vector2.Dot(p - r.a, d) / d.sqrMagnitude);
            if ((r.a + d * t - p).magnitude <= r.half) { kind = TruckCall.DropSurface.Road; along = d.normalized; }
        }
        return true;
    }

    static void Axes(Vector2 root, float yawDeg, TruckCall.DropBox b, out Vector2 centre, out Vector2 right, out Vector2 fwd)
    {
        float r = yawDeg * Mathf.Deg2Rad;
        fwd = new Vector2(Mathf.Sin(r), Mathf.Cos(r)); right = new Vector2(fwd.y, -fwd.x);
        centre = root + right * b.cx + fwd * b.cz;
    }

    public bool Clear(Vector3 root, float yawDeg)
    {
        clearCalls++;
        Axes(new Vector2(root.x, root.z), yawDeg, box, out var c, out var right, out var fwd);
        foreach (var s in solids) if (Overlaps(c, right, fwd, box.hx, box.hz, s)) return false;
        return true;
    }

    // separating axis test: oriented box vs axis-aligned rect
    static bool Overlaps(Vector2 c, Vector2 right, Vector2 fwd, float hx, float hz, Rect s)
    {
        Vector2 sc = s.center, sh = s.size * 0.5f;
        Vector2[] axes = { Vector2.right, Vector2.up, right, fwd };
        foreach (var ax in axes)
        {
            float rb = hx * Mathf.Abs(Vector2.Dot(right, ax)) + hz * Mathf.Abs(Vector2.Dot(fwd, ax));
            float rs = sh.x * Mathf.Abs(ax.x) + sh.y * Mathf.Abs(ax.y);
            if (Mathf.Abs(Vector2.Dot(sc - c, ax)) > rb + rs) return false;
        }
        return true;
    }

    public bool Flat(Vector3 root, float yawDeg)
    {
        Axes(new Vector2(root.x, root.z), yawDeg, box, out var c, out var right, out var fwd);
        for (int k = 0; k < 4; k++)
        {
            Vector2 p = c + right * ((k & 1) == 0 ? -(box.hx - 0.2f) : box.hx - 0.2f) + fwd * ((k & 2) == 0 ? -(box.hz - 0.2f) : box.hz - 0.2f);
            if (!Surface(p.x, p.y, 0f, false, out _, out _, out _)) return false;
        }
        return true;
    }

    public bool Open(Vector3 from, Vector3 to)
    {
        Vector2 a = new Vector2(from.x, from.z), b = new Vector2(to.x, to.z);
        foreach (var s in solids) if (SegmentHits(a, b, s)) return false;
        return true;
    }

    static bool SegmentHits(Vector2 a, Vector2 b, Rect r)
    {
        float t0 = 0f, t1 = 1f; Vector2 d = b - a;
        float[] p = { -d.x, d.x, -d.y, d.y };
        float[] q = { a.x - r.xMin, r.xMax - a.x, a.y - r.yMin, r.yMax - a.y };
        for (int i = 0; i < 4; i++)
        {
            if (Mathf.Abs(p[i]) < 1e-9f) { if (q[i] < 0f) return false; }
            else
            {
                float t = q[i] / p[i];
                if (p[i] < 0f) { if (t > t1) return false; if (t > t0) t0 = t; }
                else { if (t < t0) return false; if (t < t1) t1 = t; }
            }
        }
        return true;
    }
}

public static class Program
{
    static int failures, checks;
    static void Check(bool ok, string what)
    {
        checks++;
        if (!ok) { failures++; Console.WriteLine("FAIL  " + what); }
    }
    static void Near(float a, float b, float tol, string what) => Check(Mathf.Abs(a - b) <= tol, $"{what}: {a} vs {b} (tol {tol})");

    static readonly TruckCall.DropBox Std = new TruckCall.DropBox(0f, 0.8f, 1.3f, 3.0f, 0.41f, 3.31f);

    static float ScoreOf(FakeWorld w, Vector3 player, float playerYaw, Vector3 root, float yaw, out TruckCall.DropSurface kind)
    {
        w.Surface(root.x, root.z, 0f, true, out _, out kind, out var along);
        float pr = playerYaw * Mathf.Deg2Rad;
        Vector2 fwd = new Vector2(Mathf.Sin(pr), Mathf.Cos(pr)), right = new Vector2(fwd.y, -fwd.x);
        var p2 = new Vector2(player.x, player.z); var r2 = new Vector2(root.x, root.z);
        float gap = TruckCall.GapToBox(p2, r2, yaw, w.box);
        Vector2 dir = new Vector2(Mathf.Sin(yaw * Mathf.Deg2Rad), Mathf.Cos(yaw * Mathf.Deg2Rad));
        float rel = Vector2.Dot(r2 - p2, right);
        float score = gap + TruckCall.SurfaceCost(kind) + (rel < -1e-4f ? 0.2f : 0f) + (Vector2.Dot(dir, fwd) < 0f ? 0.4f : 0f);
        Vector2 near = TruckCall.NearestOnBox(p2, r2, yaw, w.box);
        if (!w.Open(player + Vector3.up, new Vector3(near.x, 1f, near.y))) score += 8f;
        return score;
    }

    public static int Main()
    {
        // ---- GapToBox / NearestOnBox
        Near(TruckCall.GapToBox(new Vector2(0, 10), Vector2.zero, 0f, Std), 6.2f, 1e-4f, "gap ahead of the nose");
        Near(TruckCall.GapToBox(new Vector2(5, 0), Vector2.zero, 0f, Std), 3.7f, 1e-4f, "gap beside");
        Near(TruckCall.GapToBox(new Vector2(0.5f, 1f), Vector2.zero, 0f, Std), 0f, 1e-4f, "inside the box");
        Near(TruckCall.GapToBox(new Vector2(10, 0), Vector2.zero, 90f, Std), 6.2f, 1e-3f, "gap ahead of the nose, truck facing +X");
        Near(TruckCall.GapToBox(new Vector2(0, -10), Vector2.zero, 90f, Std), 8.7f, 1e-3f, "gap beside, facing +X (left side is +Z? right is -Z)");
        var near = TruckCall.NearestOnBox(new Vector2(5, 0), Vector2.zero, 0f, Std);
        Near(near.x, 1.3f, 1e-4f, "nearest point x"); Near(near.y, 0f, 1e-4f, "nearest point z");
        Near(TruckCall.YawOf(new Vector2(1, 0)), 90f, 1e-3f, "yaw +X");
        Near(TruckCall.YawOf(new Vector2(0, -1)), 180f, 1e-3f, "yaw -Z");

        // ---- road frame
        var bend = new[] { new CoastalCityLayout.RoadDef { id = "bend", halfWidth = 5f, points = new[] { Vector3.zero, new Vector3(0, 0, 40), new Vector3(40, 0, 40) } } };
        Check(TruckCall.TryRoadFrame(new Vector3(20, 2, 10), bend, 60f, out var rp, out var ralong, out var rhalf, out var rdist), "road frame found");
        Near(rp.x, 0f, 1e-3f, "road frame x"); Near(rp.z, 10f, 1e-3f, "road frame z"); Near(ralong.y, 1f, 1e-3f, "road runs +Z"); Near(rdist, 20f, 1e-3f, "distance"); Near(rhalf, 5f, 1e-4f, "half width");
        Check(!TruckCall.TryRoadFrame(new Vector3(200, 0, 200), bend, 60f, out _, out _, out _, out _), "too far");
        Check(TruckCall.TryRoadFrame(new Vector3(30, 0, 41), bend, 60f, out rp, out ralong, out _, out _) && Mathf.Abs(ralong.x) > 0.99f, "second leg runs along X");

        // ---- classify
        Check(TruckCall.Classify("Road_main") == TruckCall.DropSurface.Road, "Road_");
        Check(TruckCall.Classify("DepotPad_2") == TruckCall.DropSurface.Road, "DepotPad_");
        Check(TruckCall.Classify("Sidewalk_-1") == TruckCall.DropSurface.Walk, "Sidewalk_");
        Check(TruckCall.Classify("Relief terrain 3_4") == TruckCall.DropSurface.Ground, "terrain");
        Check(TruckCall.IsSurfaceName("Relief terrain 3_4") && TruckCall.IsSurfaceName("Road_x") && !TruckCall.IsSurfaceName("SM_house_03"), "surface names");
        Check(TruckCall.Classify("River walk (pedestrian)") == TruckCall.DropSurface.Path, "river walk");

        // ---- T2: on the north pavement of an east-west road, facing north
        var w = new FakeWorld { box = Std };
        w.roads.Add((new Vector2(-200, 0), new Vector2(200, 0), 5f));
        w.walks.Add(new Rect(-200, 5, 400, 3)); w.walks.Add(new Rect(-200, -8, 400, 3));
        var player = new Vector3(0, 0, 6.5f);
        Check(TruckCall.Pick(w, Std, player, 0f, out var root, out float yaw), "T2 found");
        float gap = TruckCall.GapToBox(new Vector2(player.x, player.z), new Vector2(root.x, root.z), yaw, Std);
        Check(gap >= TruckCall.MinGap - 1e-4f && gap < 2.5f, $"T2 gap beside the player ({gap})");
        Check(Mathf.Abs(Mathf.Sin(yaw * Mathf.Deg2Rad)) > 0.99f, $"T2 faces along the road (yaw {yaw})");
        Check(root.z < 5f, $"T2 root is on the asphalt (z {root.z})");
        Near(root.y, TruckCall.RootLift, 1e-4f, "T2 height");
        Console.WriteLine($"T2 root=({root.x:0.00},{root.z:0.00}) yaw={yaw:0.0} gap={gap:0.00} surface calls={w.surfaceCalls} clear calls={w.clearCalls}");

        // facing +X on the road: the truck should be to the player's side facing the same way when the player looks along the road
        Check(TruckCall.Pick(w, Std, player, 90f, out root, out yaw), "T2b found");
        Near(Mathf.Sin(yaw * Mathf.Deg2Rad), 1f, 1e-3f, "T2b: the truck faces the way the player faces (+X)");
        Console.WriteLine($"T2b root=({root.x:0.00},{root.z:0.00}) yaw={yaw:0.0}");
        Check(TruckCall.Pick(w, Std, player, 270f, out root, out yaw), "T2c found");
        Near(Mathf.Sin(yaw * Mathf.Deg2Rad), -1f, 1e-3f, "T2c: the truck faces the way the player faces (-X)");

        // ---- T3: open park, no road: beside the player, the way they face, on their right
        w = new FakeWorld { box = Std };
        Check(TruckCall.Pick(w, Std, Vector3.zero, 30f, out root, out yaw), "T3 found");
        Near(yaw, 30f, 1e-3f, "T3 faces where the player faces");
        gap = TruckCall.GapToBox(Vector2.zero, new Vector2(root.x, root.z), yaw, Std);
        Check(gap >= TruckCall.MinGap && gap < 1.8f, $"T3 close beside ({gap})");
        float pr = 30f * Mathf.Deg2Rad; var rightDir = new Vector2(Mathf.Cos(pr), -Mathf.Sin(pr));
        Check(Vector2.Dot(new Vector2(root.x, root.z), rightDir) > 0f, "T3 on the player's right (driver's door toward them)");
        Console.WriteLine($"T3 root=({root.x:0.00},{root.z:0.00}) yaw={yaw:0.0} gap={gap:0.00}");

        // ---- T4: a 12 x 12 fenced yard north of the road; then a 5 x 5 one
        foreach (float size in new[] { 12f, 5f })
        {
            w = new FakeWorld { box = Std };
            w.roads.Add((new Vector2(-200, 0), new Vector2(200, 0), 5f));
            float x0 = -size / 2, x1 = size / 2, z0 = 10f, z1 = 10f + size;
            w.solids.Add(new Rect(x0 - 0.2f, z0 - 0.2f, size + 0.4f, 0.2f)); // south fence
            w.solids.Add(new Rect(x0 - 0.2f, z1, size + 0.4f, 0.2f));        // north fence
            w.solids.Add(new Rect(x0 - 0.2f, z0, 0.2f, size));               // west
            w.solids.Add(new Rect(x1, z0, 0.2f, size));                      // east
            var inYard = new Vector3(0, 0, z0 + size / 2);
            Check(TruckCall.Pick(w, Std, inYard, 0f, out root, out yaw), $"T4({size}) found");
            bool inside = root.z > z0 && root.z < z1 && root.x > x0 && root.x < x1;
            Console.WriteLine($"T4({size}) root=({root.x:0.00},{root.z:0.00}) yaw={yaw:0.0} inside={inside}");
            if (size > 10f) Check(inside, "T4 12 m yard: the truck fits inside, so it lands beside you in the yard");
            else Check(!inside, "T4 5 m yard: it can't fit, so it lands outside the fence");
        }

        // ---- T5: nowhere to put it
        w = new FakeWorld { box = Std, land = new Rect(-3, -3, 6, 6) };
        Check(!TruckCall.Pick(w, Std, Vector3.zero, 0f, out _, out _), "T5 a 6 m island has no room for a 6 m truck");

        // ---- T6: a river ahead
        w = new FakeWorld { box = Std };
        w.water.Add(new Rect(-100, 3, 200, 30));
        Check(TruckCall.Pick(w, Std, Vector3.zero, 0f, out root, out yaw), "T6 found");
        Check(w.Flat(root, yaw) && w.Clear(root, yaw), "T6 valid");

        // ---- T7: pruning never changes the answer (random streets, brute force over the same rings and spokes)
        var rng = new System.Random(12345);
        int compared = 0;
        for (int scene = 0; scene < 300; scene++)
        {
            w = new FakeWorld { box = Std };
            if (rng.Next(3) > 0) w.roads.Add((new Vector2(-100, rng.Next(-20, 20)), new Vector2(100, rng.Next(-20, 20)), 4f + rng.Next(4)));
            if (rng.Next(2) == 0) w.roads.Add((new Vector2(rng.Next(-30, 30), -100), new Vector2(rng.Next(-30, 30), 100), 4f));
            for (int i = 0; i < 25; i++) w.solids.Add(new Rect(rng.Next(-60, 60), rng.Next(-60, 60), 1 + rng.Next(14), 1 + rng.Next(14)));
            if (rng.Next(3) == 0) w.water.Add(new Rect(rng.Next(-40, 40), rng.Next(-40, 40), 10 + rng.Next(40), 10 + rng.Next(40)));
            var pl = new Vector3(rng.Next(-30, 30), 0, rng.Next(-30, 30));
            float pyaw = rng.Next(360);
            bool any = TruckCall.Pick(w, Std, pl, pyaw, out var r1, out float y1, out float cost1);
            // brute force: every ring, every spoke, both headings
            float best = float.MaxValue; bool anyBrute = false;
            float pr2 = pyaw * Mathf.Deg2Rad; var f2 = new Vector2(Mathf.Sin(pr2), Mathf.Cos(pr2)); var rt2 = new Vector2(f2.y, -f2.x);
            var spokes = new List<float>(); for (int i = 0; i < 24; i++) spokes.Add(-165f + i * 15f);
            for (float ring = 2.8f; ring <= TruckCall.MaxReach; ring += ring < 9f ? 0.5f : ring * 0.2f)
                foreach (float sp in spokes)
                {
                    float a = sp * Mathf.Deg2Rad;
                    Vector2 at = new Vector2(pl.x, pl.z) + (f2 * Mathf.Cos(a) + rt2 * Mathf.Sin(a)) * ring;
                    if (!w.Surface(at.x, at.y, 0f, true, out float gy, out var kind, out var along)) continue;
                    Vector2 heading = along.sqrMagnitude > 0.01f ? along.normalized : f2;
                    for (int h = 0; h < 2; h++)
                    {
                        float yy = TruckCall.YawOf(h == 0 ? heading : -heading);
                        float gp = TruckCall.GapToBox(new Vector2(pl.x, pl.z), at, yy, Std);
                        if (gp < TruckCall.MinGap) continue;
                        var spot = new Vector3(at.x, gy + TruckCall.RootLift, at.y);
                        if (!w.Clear(spot, yy) || !w.Flat(spot, yy)) continue;
                        // score exactly like Pick: the spoke's own left/right sign, not the geometric side
                        float s = gp + TruckCall.SurfaceCost(kind) + (sp < 0f ? 0.2f : 0f) + (Vector2.Dot(h == 0 ? heading : -heading, f2) < 0f ? 0.4f : 0f);
                        Vector2 nr = TruckCall.NearestOnBox(new Vector2(pl.x, pl.z), at, yy, Std);
                        if (!w.Open(pl + Vector3.up, new Vector3(nr.x, gy + 1f, nr.y))) s += 8f;
                        if (s < best) { best = s; anyBrute = true; }
                    }
                }
            Check(any == anyBrute, $"scene {scene}: Pick and brute force agree on whether there is room");
            if (any && anyBrute)
            {
                float gp1 = TruckCall.GapToBox(new Vector2(pl.x, pl.z), new Vector2(r1.x, r1.z), y1, Std);
                Check(gp1 >= TruckCall.MinGap - 1e-3f, $"scene {scene}: gap {gp1} >= MinGap");
                Check(w.Clear(r1, y1) && w.Flat(r1, y1), $"scene {scene}: the chosen spot is clear and flat");
                Check(Mathf.Abs(cost1 - best) < 1e-3f, $"scene {scene}: pruned search price {cost1} equals the brute-force minimum {best}");
                compared++;
            }
        }
        Console.WriteLine($"T7 compared {compared} scenes");
        Console.WriteLine($"{checks} checks, {failures} failures");
        return failures == 0 ? 0 : 1;
    }
}
