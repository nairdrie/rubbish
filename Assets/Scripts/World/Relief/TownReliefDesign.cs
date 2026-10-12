using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// Shapes the town's relief (pure maths: no scene, no assets). The land is a smooth designed surface (river valley,
/// commercial shelf, the Hills' dome, the Bluffs' plateau and cliff); every road then gets a LEVEL cross-section along
/// its own smoothed profile, and every pad (a building, a pool, a court) a level patch at the height the land had
/// there. What comes out is two grids: where things stand (bridge decks included) and the terrain surface itself
/// (river channel and shore carved in). Depot Row stays at 0, so nothing about the depots moves.
/// </summary>
public static class TownReliefDesign
{
    /// <summary>Grid spacing in metres. The terrain mesh, every draped mesh and every runtime lookup use this grid.</summary>
    public const float Cell = 3f;
    /// <summary>Bump when the shape below changes, so stored results are recognised as stale.</summary>
    public const int Revision = 1;
    /// <summary>How far the terrain surface sits under the flat town's y = 0 (as it always has).</summary>
    public const float TerrainDrop = 0.08f;
    public const float WalkWidth = 3f, RoadMargin = 0.75f, RoadFeather = 5f, RoadCoreFeather = 2f;
    public const float BridgeArch = 1.3f;
    const float RiverInner = 15f, RiverOuter = 24f, RiverBed = -5f, ShoreStart = -12f, ShoreEnd = 16f, SeaBed = -7f;

    public struct Pad
    {
        public Vector2 centre, half;        // world XZ centre, half extents along the pad's own right / forward
        public float yaw, feather;          // degrees about +Y; metres over which the patch blends back into the land
        public float bias;                  // metres added to the natural height (a sunken pond: negative)
        public string name;
    }
    public sealed class Road { public string id; public Vector3[] points; public float halfWidth; public bool bridge; }
    public sealed class Input
    {
        public Rect bounds;
        public Vector3[] river, coast;
        public readonly List<Road> roads = new List<Road>();
        public readonly List<Pad> pads = new List<Pad>();
        /// <summary>0 = the old flat town, 1 = the designed relief. For tests and the emergency switch.</summary>
        public float strength = 1f;
    }
    public sealed class RoadStat { public string id; public float min, max, grade; }
    public sealed class Result
    {
        public TownReliefField field;       // where things stand
        public float[] terrain;             // terrain mesh heights, same grid
        public readonly List<RoadStat> roads = new List<RoadStat>();
        public float min, max;
        public string Report()
        {
            var sb = new StringBuilder();
            sb.Append("[relief] grid ").Append(field.nx).Append(" x ").Append(field.nz).Append(" @ ").Append(field.cell).Append(" m, land ")
              .Append(min.ToString("F1")).Append(" .. ").Append(max.ToString("F1")).Append(" m");
            var steep = new List<RoadStat>(roads); steep.Sort((a, b) => b.grade.CompareTo(a.grade));
            sb.Append("; steepest roads:");
            for (int i = 0; i < Mathf.Min(6, steep.Count); i++)
                sb.Append(' ').Append(steep[i].id).Append(' ').Append((steep[i].grade * 100f).ToString("F1")).Append("% (").Append(steep[i].min.ToString("F1")).Append("..").Append(steep[i].max.ToString("F1")).Append(')');
            return sb.ToString();
        }
    }

    /// <summary>The blueprint's part of the input (roads, river, coast, bounds). Pads come from the scene.</summary>
    public static Input FromLayout()
    {
        var input = new Input { bounds = CoastalCityLayout.BoundsXZ, river = CoastalCityLayout.River, coast = CoastalCityLayout.Coast };
        foreach (var r in CoastalCityLayout.Roads)
            input.roads.Add(new Road { id = r.id, points = r.points, halfWidth = r.halfWidth, bridge = r.bridge });
        return input;
    }

    public static float SS(float a, float b, float x)
    {
        float t = Mathf.Clamp01((x - a) / (b - a));
        return t * t * (3f - 2f * t);
    }

    static float Distance(Vector3[] line, float x, float z, out float side)
    {
        float best = float.MaxValue; side = 0f;
        for (int i = 1; i < line.Length; i++)
        {
            float ax = line[i - 1].x, az = line[i - 1].z, dx = line[i].x - ax, dz = line[i].z - az;
            float len = dx * dx + dz * dz, t = len > 1e-9f ? Mathf.Clamp01(((x - ax) * dx + (z - az) * dz) / len) : 0f;
            float px = x - ax - dx * t, pz = z - az - dz * t, d = px * px + pz * pz;
            if (d < best) { best = d; side = dx * (z - az) - dz * (x - ax); }
        }
        return Mathf.Sqrt(best);
    }

    /// <summary>Distance to a polyline, counted only when the nearest point is not one of its two ends.</summary>
    static float DistanceInside(Vector3[] line, float x, float z)
    {
        float best = float.MaxValue; bool atEnd = false; int last = line.Length - 1;
        for (int i = 1; i < line.Length; i++)
        {
            float ax = line[i - 1].x, az = line[i - 1].z, dx = line[i].x - ax, dz = line[i].z - az;
            float len = dx * dx + dz * dz, t = len > 1e-9f ? Mathf.Clamp01(((x - ax) * dx + (z - az) * dz) / len) : 0f;
            float px = x - ax - dx * t, pz = z - az - dz * t, d = px * px + pz * pz;
            if (d < best) { best = d; atEnd = i == 1 && t <= 0f || i == last && t >= 1f; }
        }
        return atEnd ? float.MaxValue : Mathf.Sqrt(best);
    }

    static float CoastX(Vector3[] coast, float z)
    {
        for (int i = 1; i < coast.Length; i++)
        {
            var a = coast[i - 1]; var b = coast[i];
            if (z >= Mathf.Min(a.z, b.z) && z <= Mathf.Max(a.z, b.z)) return Mathf.Lerp(a.x, b.x, Mathf.InverseLerp(a.z, b.z, z));
        }
        return coast[coast.Length - 1].x;
    }

    /// <summary>The designed land before roads and pads level it: metres above the old flat ground.</summary>
    public static float Land(Input input, float x, float z)
    {
        float riverDistance = Distance(input.river, x, z, out float side);
        bool north = side > 0f;                                   // the river runs west to east; north is on its left
        float inland = CoastX(input.coast, z) - x;
        float n1 = Mathf.Sin(x * 0.0241f + 1.3f) * Mathf.Cos(z * 0.0277f - 0.4f);
        float n2 = Mathf.Sin((x + z) * 0.0152f + 2.1f) * Mathf.Cos((x - z) * 0.0131f + 0.7f);
        float n3 = Mathf.Sin(x * 0.051f + 0.5f) * Mathf.Sin(z * 0.047f + 1.9f);
        float roll = 0.55f * n1 + 0.45f * n2 + 0.18f * n3;
        float valley = SS(30f, 150f, riverDistance);              // 0 on the bank, 1 up on the valley side
        float h;
        if (!north)
        {
            // Acres and Meadows: a gentle fall to the river with mild rolls; Depot Row (the west end) stays level.
            float town = SS(135f, 265f, x);
            float amplitude = Mathf.Lerp(1.5f, 2f, SS(440f, 520f, x));
            h = (valley * 3.6f + roll * amplitude * (0.25f + 0.75f * valley)) * town;
            h += SS(5f, -50f, z) * 6f * town;                     // wooded rise behind the southern houses
        }
        else
        {
            // Commons and Heights: a shelf that tilts up away from the river.
            float heights = SS(395f, 450f, x);
            h = 0.5f * valley + SS(350f, 545f, z) * (2.2f + 1.6f * heights) + roll * (0.3f + 0.3f * heights) * valley;
            // Snootington Hills: the loop road circles a dome.
            float west = 1f - SS(395f, 465f, x);
            float dx = (x - 235f) / 190f, dz = (z - 690f) / 130f;
            h += SS(545f, 690f, z) * west * (3.2f + 7f * Mathf.Exp(-(dx * dx + dz * dz)) + roll * 1.0f);
            // Billionaire Bluffs: a plateau climbing to the north-east, where it ends in the cliff.
            float bluffs = SS(545f, 745f, z) * SS(395f, 510f, x);
            float rise = SS(0f, 1f, (x - 450f) / 350f * 0.5f + (z - 600f) / 300f * 0.5f);
            h += bluffs * (7f + 8.5f * rise + roll * 1.2f);
            h += SS(60f, -35f, x) * 6f;                           // western fringe
            h += SS(815f, 950f, z) * west * 8f;                   // ridge behind the Hills
            h += SS(905f, 955f, z) * (1f - west) * 5f;
        }
        // The coast: whatever the land was, it comes down to a beach. Where it was high, that is a cliff, and a
        // cliff is craggy.
        if (h > 0.6f)
        {
            // the face stands close over a narrow beach: the old estates' pools and pads behind it stay on top
            float edge = SS(14f, 34f, inland), face = 4f * edge * (1f - edge);
            float crag = Mathf.Sin(x * 0.41f + z * 0.23f) * Mathf.Sin(z * 0.37f - x * 0.13f) + 0.5f * Mathf.Sin(x * 0.83f - z * 0.61f);
            h = 0.6f + (h - 0.6f) * edge + face * crag * Mathf.Min(1.8f, (h - 0.6f) * 0.11f);
        }
        return h * input.strength;
    }

    struct Splat { public float sumW, sumWT, coreW, coreWT, strength, core, bridge; }
    sealed class RoadWork
    {
        public Road road; public float length; public float[] profile, natural, rank;
        public readonly List<Vector3> samples = new List<Vector3>(); public readonly List<float> along = new List<float>();
    }

    /// <summary>How steep the designed land is around a point (rise over run, 0.1 = 10 %). Layout code that runs
    /// while the town is still flat uses it to keep lawns' set pieces off the cliff and the steepest banks.</summary>
    public static float Steepness(Input input, float x, float z, float reach = 6f)
    {
        float c = Land(input, x, z), worst = 0f;
        for (int i = 0; i < 8; i++)
        {
            float a = i * Mathf.PI * 0.25f;
            worst = Mathf.Max(worst, Mathf.Abs(Land(input, x + Mathf.Cos(a) * reach, z + Mathf.Sin(a) * reach) - c) / reach);
        }
        return worst;
    }

    public static Result Build(Input input)
    {
        if (input == null || input.river == null || input.coast == null) throw new ArgumentNullException(nameof(input));
        var b = input.bounds;
        float x0 = Mathf.Floor(b.xMin / Cell) * Cell, z0 = Mathf.Floor(b.yMin / Cell) * Cell;
        int nx = Mathf.CeilToInt((b.xMax - x0) / Cell) + 1, nz = Mathf.CeilToInt((b.yMax - z0) / Cell) + 1;
        var field = new TownReliefField(x0, z0, Cell, nx, nz);
        var h = field.h; int n = h.Length;
        for (int iz = 0; iz < nz; iz++) for (int ix = 0; ix < nx; ix++) h[iz * nx + ix] = Land(input, x0 + ix * Cell, z0 + iz * Cell);

        var result = new Result { field = field };
        var land = (float[])h.Clone();                         // the designed surface, before any road touched it

        // Roads: level across, smooth along. Each is walked every 1.5 m; a sample claims the cells it is nearest to.
        var works = new List<RoadWork>();
        foreach (var road in input.roads)
        {
            if (road?.points == null || road.points.Length < 2) continue;
            var work = new RoadWork { road = road };
            for (int i = 1; i < road.points.Length; i++)
            {
                Vector3 a = road.points[i - 1], c = road.points[i]; float seg = Vector2.Distance(new Vector2(a.x, a.z), new Vector2(c.x, c.z));
                int steps = Mathf.Max(1, Mathf.CeilToInt(seg / 1.5f));
                for (int k = i == 1 ? 0 : 1; k <= steps; k++) { work.samples.Add(Vector3.Lerp(a, c, k / (float)steps)); work.along.Add(work.length + seg * k / steps); }
                work.length += seg;
            }
            work.profile = new float[work.samples.Count]; work.rank = new float[work.samples.Count];
            works.Add(work); result.roads.Add(new RoadStat { id = road.id, min = float.MaxValue, max = float.MinValue });
        }
        // Where a road runs inside another road's carriageway (the last metres of a side street, a corner), the land is
        // that other road's to level: this road only rides over it.
        foreach (var work in works)
        {
            for (int i = 0; i < work.rank.Length; i++) work.rank[i] = 1f;
            foreach (var other in works)
            {
                if (other == work) continue;
                float reach = other.road.halfWidth + WalkWidth + RoadMargin;
                Vector3 min = other.road.points[0], max = min;
                foreach (var p in other.road.points) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
                for (int i = 0; i < work.samples.Count; i++)
                {
                    var p = work.samples[i];
                    if (p.x < min.x - reach || p.x > max.x + reach || p.z < min.z - reach || p.z > max.z + reach) continue;
                    // only where the other road runs THROUGH: at its end the two simply meet, neither rides the other
                    if (DistanceInside(other.road.points, p.x, p.z) < reach) work.rank[i] = 0.02f;
                }
            }
        }
        var splat = new Splat[n];
        var nearest = new float[n]; var target = new float[n]; var rank = new float[n]; var touched = new List<int>();
        for (int i = 0; i < n; i++) nearest[i] = float.MaxValue;
        // A road's own height is the land's along its centreline, smoothed (narrowing to nothing at its ends, so two
        // roads that meet end to end agree there).
        foreach (var work in works)
        {
            int count = work.samples.Count; var raw = new float[count]; work.natural = new float[count];
            for (int i = 0; i < count; i++) raw[i] = Land(input, work.samples[i].x, work.samples[i].z);
            for (int i = 0; i < count; i++)
            {
                float sum = 0f, weight = 0f, reach = Mathf.Min(14f, Mathf.Min(work.along[i], work.length - work.along[i]));
                for (int k = i; k >= 0 && work.along[i] - work.along[k] <= reach; k--) { float w = 1f - (work.along[i] - work.along[k]) / (reach + 0.01f); sum += raw[k] * w; weight += w; }
                for (int k = i + 1; k < count && work.along[k] - work.along[i] <= reach; k++) { float w = 1f - (work.along[k] - work.along[i]) / (reach + 0.01f); sum += raw[k] * w; weight += w; }
                work.natural[i] = sum / weight;
                if (work.road.bridge) { float s = Mathf.Sin(Mathf.PI * work.along[i] / work.length); work.natural[i] += BridgeArch * s * s * input.strength; }
            }
        }
        // Where it runs into another road it must arrive at THAT road's level, which on a slope is not its own. So it
        // eases onto it over the last Landing metres. The other road may itself be easing onto a third: repeat until
        // nothing moves much.
        const int Passes = 4; const float Landing = 32f;
        for (int pass = 0; pass < Passes; pass++)
        {
            foreach (var work in works)
            {
                int count = work.samples.Count;
                Array.Copy(work.natural, work.profile, count);
                if (pass == 0) continue;
                var best = new float[count];
                for (int edge = 0; edge < count; edge++)
                {
                    if (work.rank[edge] >= 1f) continue;
                    work.profile[edge] = field.Height(work.samples[edge].x, work.samples[edge].z);
                    for (int dir = -1; dir <= 1; dir += 2)
                    {
                        int next = edge + dir;
                        if (next < 0 || next >= count || work.rank[next] < 1f) continue;      // only the edge of a stretch
                        float step = field.Height(work.samples[edge].x, work.samples[edge].z) - work.natural[edge];
                        for (int j = next; j >= 0 && j < count && work.rank[j] >= 1f; j += dir)
                        {
                            float away = Mathf.Abs(work.along[j] - work.along[edge]); if (away >= Landing) break;
                            float ease = 1f - SS(0f, Landing, away);
                            if (ease > best[j]) { best[j] = ease; work.profile[j] = work.natural[j] + step * ease; }
                        }
                    }
                }
            }
            Array.Clear(splat, 0, n);
            foreach (var work in works)
            {
                var road = work.road; var samples = work.samples;
                // A bridge's parapets stand outside its pavement, and nothing may dip towards the water between two
                // grid points there: the deck is level a cell and a half further out than a street is.
                float core = road.halfWidth + WalkWidth + RoadMargin + (road.bridge ? Cell * 1.5f : 0f), reachOut = core + RoadFeather;
                int span = Mathf.CeilToInt(reachOut / Cell) + 1, lastSample = samples.Count - 1;
                Vector3 outStart = (samples[0] - samples[1]).normalized, outEnd = (samples[lastSample] - samples[lastSample - 1]).normalized;
                touched.Clear();
                for (int s = 0; s < samples.Count; s++)
                {
                    int cx = Mathf.RoundToInt((samples[s].x - x0) / Cell), cz = Mathf.RoundToInt((samples[s].z - z0) / Cell);
                    for (int iz = Mathf.Max(0, cz - span); iz <= Mathf.Min(nz - 1, cz + span); iz++)
                        for (int ix = Mathf.Max(0, cx - span); ix <= Mathf.Min(nx - 1, cx + span); ix++)
                        {
                            float ddx = x0 + ix * Cell - samples[s].x, ddz = z0 + iz * Cell - samples[s].z, d = ddx * ddx + ddz * ddz;
                            int cell = iz * nx + ix;
                            if (d > reachOut * reachOut || d >= nearest[cell]) continue;
                            if (nearest[cell] == float.MaxValue) touched.Add(cell);
                            nearest[cell] = d; target[cell] = work.profile[s];
                            // Past its own end a road only rounds the corner off: any road running THROUGH there decides
                            // the height, and this one's say counts for next to nothing.
                            bool beyond = s == 0 && ddx * outStart.x + ddz * outStart.z > 0.2f || s == lastSample && ddx * outEnd.x + ddz * outEnd.z > 0.2f;
                            rank[cell] = beyond ? 0.02f : work.rank[s];
                        }
                }
                foreach (int cell in touched)
                {
                    float d = Mathf.Sqrt(nearest[cell]);
                    float w = 1f - SS(core, reachOut, d), c = 1f - SS(core, core + RoadCoreFeather, d);
                    // On its own carriageway and pavements a road decides alone; another road's fading verge has no say
                    // there. Everywhere else the roads in reach share the land, the nearer the louder.
                    if (d <= core && rank[cell] >= 1f) { splat[cell].coreW += 1f; splat[cell].coreWT += target[cell]; }
                    else { float say = w * w * rank[cell]; splat[cell].sumW += say; splat[cell].sumWT += say * target[cell]; }
                    splat[cell].strength = Mathf.Max(splat[cell].strength, w);
                    splat[cell].core = Mathf.Max(splat[cell].core, c);
                    if (road.bridge) splat[cell].bridge = Mathf.Max(splat[cell].bridge, c);
                    nearest[cell] = float.MaxValue;
                }
            }
            for (int i = 0; i < n; i++)
                h[i] = splat[i].coreW > 0f ? splat[i].coreWT / splat[i].coreW
                     : splat[i].sumW > 0f ? Mathf.Lerp(land[i], splat[i].sumWT / splat[i].sumW, splat[i].strength) : land[i];
        }

        // Pads: level patches at the height the land (roads included) already had there. Never inside a road.
        if (input.pads.Count > 0)
        {
            var padW = new float[n]; var padWT = new float[n];
            foreach (var pad in input.pads)
            {
                float rad = pad.yaw * Mathf.Deg2Rad, cs = Mathf.Cos(rad), sn = Mathf.Sin(rad);
                // local right = (cos, -sin), local forward = (sin, cos) in XZ (Unity yaw about +Y)
                float feather = Mathf.Max(0.5f, pad.feather);
                float reach = Mathf.Sqrt((pad.half.x + Cell) * (pad.half.x + Cell) + (pad.half.y + Cell) * (pad.half.y + Cell)) + feather;
                int ix0 = Mathf.Max(0, Mathf.FloorToInt((pad.centre.x - reach - x0) / Cell)), ix1 = Mathf.Min(nx - 1, Mathf.CeilToInt((pad.centre.x + reach - x0) / Cell));
                int iz0 = Mathf.Max(0, Mathf.FloorToInt((pad.centre.y - reach - z0) / Cell)), iz1 = Mathf.Min(nz - 1, Mathf.CeilToInt((pad.centre.y + reach - z0) / Cell));
                // natural height: the mean of the land under the pad (sampled, so a pad smaller than a cell still works)
                float sum = 0f; int count = 0;
                for (int a = -2; a <= 2; a++) for (int c = -2; c <= 2; c++)
                {
                    float lx = pad.half.x * a * 0.5f, lz = pad.half.y * c * 0.5f;
                    sum += field.Height(pad.centre.x + cs * lx + sn * lz, pad.centre.y - sn * lx + cs * lz); count++;
                }
                float level = sum / count + pad.bias * input.strength;
                for (int iz = iz0; iz <= iz1; iz++) for (int ix = ix0; ix <= ix1; ix++)
                {
                    float wx = x0 + ix * Cell - pad.centre.x, wz = z0 + iz * Cell - pad.centre.y;
                    // a point is level only if all three grid points round it are: the patch reaches a cell further
                    float lx = Mathf.Abs(wx * cs - wz * sn) - pad.half.x - Cell, lz = Mathf.Abs(wx * sn + wz * cs) - pad.half.y - Cell;
                    float outside = Mathf.Sqrt(Mathf.Max(lx, 0f) * Mathf.Max(lx, 0f) + Mathf.Max(lz, 0f) * Mathf.Max(lz, 0f));
                    if (outside >= feather) continue;
                    float w = 1f - SS(0f, feather, outside); int cell = iz * nx + ix;
                    padW[cell] += w; padWT[cell] += w * level;
                }
            }
            // targets were all read above from the un-padded land, so the order of the pads does not matter
            for (int i = 0; i < n; i++)
                if (padW[i] > 0f) h[i] = Mathf.Lerp(h[i], padWT[i] / padW[i], Mathf.Min(1f, padW[i]) * (1f - splat[i].core));
        }

        field.Quantise();
        // What a truck will actually meet: heights and the steepest 4.5 m along each centreline of the finished land.
        for (int r = 0; r < works.Count; r++)
        {
            var line = works[r].samples; var stat = result.roads[r];
            for (int i = 0; i < line.Count; i++)
            {
                float here = field.Height(line[i].x, line[i].z);
                stat.min = Mathf.Min(stat.min, here); stat.max = Mathf.Max(stat.max, here);
                if (i >= 3) stat.grade = Mathf.Max(stat.grade, Mathf.Abs(here - field.Height(line[i - 3].x, line[i - 3].z)) / Mathf.Max(0.1f, Vector3.Distance(line[i], line[i - 3])));
            }
        }
        // Terrain: the same surface dropped a few centimetres, with the river channel and the shore cut into it.
        // Under a bridge the deck (where things stand) and the channel (the terrain) part company.
        var terrain = new float[n]; result.terrain = terrain; result.min = float.MaxValue; result.max = float.MinValue;
        for (int iz = 0; iz < nz; iz++) for (int ix = 0; ix < nx; ix++)
        {
            int i = iz * nx + ix; float x = x0 + ix * Cell, z = z0 + iz * Cell;
            float stand = h[i], t = stand - TerrainDrop; bool cut = false;
            float bank = Distance(input.river, x, z, out _);
            if (bank < RiverOuter) { t = Mathf.Lerp(RiverBed, t, Mathf.InverseLerp(RiverInner, RiverOuter, bank)); cut = true; }
            float shore = x - CoastX(input.coast, z);
            if (shore > ShoreStart) { t = Mathf.Min(t, Mathf.Lerp(t, SeaBed, Mathf.InverseLerp(ShoreStart, ShoreEnd, shore))); cut = true; }
            terrain[i] = t;
            if (cut) h[i] = Mathf.Lerp(t + TerrainDrop, stand, splat[i].bridge);
            result.min = Mathf.Min(result.min, h[i]); result.max = Mathf.Max(result.max, h[i]);
        }
        field.Quantise();
        // Outside the cuts the terrain is exactly the standing surface minus the drop (same rounding, no drift).
        for (int i = 0; i < n; i++) if (Mathf.Abs(terrain[i] + TerrainDrop - h[i]) < 0.02f && splat[i].bridge <= 0f) terrain[i] = h[i] - TerrainDrop;
        return result;
    }
}
