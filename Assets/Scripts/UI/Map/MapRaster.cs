using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Turns a <see cref="TownMapData"/> into the cartoon map picture (Docs/UIPass.md §7.2). Pure C#: the Editor bake
/// (<c>Setup_TownMap</c>) writes the PNG with it, and the runtime can rebuild the texture from the asset if the PNG is ever
/// missing. Layers, back to front: district lawn tints · lots (a touch lighter) · park, greens, courts, hedges, sand ·
/// driveways and paths · curbs and sidewalks · roads (#4A4458) with a 2 px #F6F0E2 curb edge and #FFD640 centre dashes
/// (3 m dash, 3 m gap, world-anchored) · the depot pad (#8E8A99) with a hazard-stripe border · buildings and houses (terracotta
/// #E07A5F rounded rects with a 2 px #5B3A44 outline; big houses lighter) · water (#6EC5E9) · fences and walls · tunnels.
/// <para>Axis-aligned rects are drawn with exact per-pixel area coverage (seam-free unions per layer); rounded house roofs are
/// 4×4 supersampled (like <c>FeelUi.Supersample</c>). Deterministic: same data, same bytes.</para>
/// </summary>
public static class MapRaster
{
    // ---- palette (§7.2)
    public static readonly Color32 Wild = Hex(0x86B65E);
    public static readonly Color32 MildlyPleasant = Hex(0x9CCB6B);
    public static readonly Color32 MinivanMeadows = Hex(0xA8D8A0);
    public static readonly Color32 Snootington = Hex(0xB9C98F);
    public static readonly Color32 Bluffs = Hex(0xD9CC86);
    public static readonly Color32 Park = Hex(0x7DBE5A);
    public static readonly Color32 Sidewalk = Hex(0xE9DFC9);
    public static readonly Color32 CurbColor = Hex(0xD3C7AE);
    public static readonly Color32 Road = Hex(0x4A4458);
    public static readonly Color32 RoadEdge = Hex(0xF6F0E2);
    public static readonly Color32 Dash = Hex(0xFFD640);
    public static readonly Color32 Pad = Hex(0x8E8A99);
    public static readonly Color32 HazardInk = Hex(0x1F1A2E);
    public static readonly Color32 Roof = Hex(0xE07A5F);
    public static readonly Color32 RoofBig = Hex(0xEC9A7F);
    public static readonly Color32 RoofOutline = Hex(0x5B3A44);
    public static readonly Color32 DepotRoof = Hex(0x5B6B2F);
    public static readonly Color32 Water = Hex(0x6EC5E9);
    public static readonly Color32 Driveway = Hex(0xD6CFC0);
    public static readonly Color32 Court = Hex(0xC9744A);
    public static readonly Color32 Hedge = Hex(0x4E8C3A);
    public static readonly Color32 Sand = Hex(0xEED9A0);
    public static readonly Color32 Helipad = Hex(0xA9A6B4);
    public static readonly Color32 Tunnel = Hex(0x2A2533);
    public static readonly Color32 Fence = Hex(0xFBF7EE);
    public static readonly Color32 Stone = Hex(0xA49C8E);

    /// <summary>Dash pattern (world metres): a dash where Repeat(coord, Period) ≥ Gap, so x = 80 on Maple St is a gap.</summary>
    public const float DashPeriod = 6f, DashLength = 3f, DashWidth = 0.7f;
    /// <summary>The curb edge drawn on road pixels next to non-road (px).</summary>
    public const int EdgePx = 2;
    /// <summary>The depot pad's hazard border (m).</summary>
    public const float HazardBorder = 1.5f;

    public static Color32 Hex(int rgb) => new Color32((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF), 255);

    /// <summary>The district lawn tint for a district id (Wild for none).</summary>
    public static Color32 DistrictTint(string id)
    {
        switch (id)
        {
            case Districts.MildlyPleasant: return MildlyPleasant;
            case Districts.MinivanMeadows: return MinivanMeadows;
            case Districts.SnootingtonHills: return Snootington;
            case Districts.BillionaireBluffs: return Bluffs;
        }
        return Wild;
    }

    /// <summary>Render the whole map: <c>width × height</c> pixels, row 0 = the bottom (z0) edge.</summary>
    public static Color32[] Render(TownMapData d)
    {
        int W = Mathf.Max(1, d.width), H = Mathf.Max(1, d.height);
        var px = new float[W * H * 3];
        var c = new Canvas(d, W, H, px);
        c.Fill(Wild);

        // 1. district tints (tier order: richer districts paint over the overlap, like Districts.AtPosition)
        foreach (var dist in d.districts) c.Rects(new[] { dist.rect }, DistrictTint(dist.id), 1f);
        // 2. lots, a touch lighter
        c.Rects(Kind(d, MapAreaKind.Lot), new Color32(255, 255, 240, 255), 0.16f);
        // 3. park, greens, courts, hedges, sand, helipads
        c.Rects(Kind(d, MapAreaKind.Park), Park, 1f);
        c.Rects(Kind(d, MapAreaKind.Green), Park, 1f);
        c.Rects(Kind(d, MapAreaKind.Court), Court, 1f);
        c.Rects(Kind(d, MapAreaKind.Sand), Sand, 1f);
        c.Rects(Kind(d, MapAreaKind.Helipad), Helipad, 1f);
        // 4. driveways and paths
        c.Rects(Kind(d, MapAreaKind.Driveway), Driveway, 1f);
        c.Rects(Kind(d, MapAreaKind.GardenPath), Sidewalk, 1f);
        c.Rects(Kind(d, MapAreaKind.ParkPath), Sidewalk, 1f);
        // 5. curbs and sidewalks
        c.Rects(Kind(d, MapAreaKind.Curb), CurbColor, 1f);
        c.Rects(Kind(d, MapAreaKind.Sidewalk), Sidewalk, 1f);
        // 6. roads + curb edge + centre dashes
        var roadRects = new List<Rect>(d.roads.Count);
        var bulbs = new List<Rect>();
        foreach (var r in d.roads)
        {
            // cul-de-sac bulbs and turnarounds (square "plaza" rects of 18 m+) are drawn round
            if (r.axis == 2 && r.rect.width >= 18f && r.rect.height >= 18f) bulbs.Add(r.rect);
            else roadRects.Add(r.rect);
        }
        var padRects = Kind(d, MapAreaKind.DepotPad);
        var roadCov = c.Coverage(roadRects);
        var driveCov = c.Coverage(Concat(roadRects, padRects));
        foreach (var b in bulbs)
        {
            float rad = Mathf.Min(b.width, b.height) * 0.5f;
            c.AddRound(roadCov, b, rad);
            c.AddRound(driveCov, b, rad);
        }
        c.Composite(roadCov, Road, 1f);
        c.Edges(roadCov, driveCov, RoadEdge);
        c.Rects(Dashes(d), Dash, 1f);
        // 7. the depot pad with its hazard-stripe border
        c.Rects(padRects, Pad, 1f);
        foreach (var p in padRects) c.Hazard(p, HazardBorder);
        // 8. buildings and houses (rounded roofs, outlined)
        foreach (var a in d.areas)
        {
            if (a.kind == MapAreaKind.Building) c.RoundRect(a.rect, 1.2f, RoofOutline, DepotRoof);
            else if (a.kind == MapAreaKind.House) c.RoundRect(a.rect, 1.4f, RoofOutline, Roof);
            else if (a.kind == MapAreaKind.BigHouse) c.RoundRect(a.rect, 1.6f, RoofOutline, RoofBig);
        }
        // 9. water (rounded), hedges and walls on top of lawns, tunnels
        foreach (var a in d.areas) if (a.kind == MapAreaKind.Water) c.RoundRect(a.rect, Mathf.Min(a.rect.width, a.rect.height) * 0.5f, Hex(0x3F8FB3), Water);
        c.Rects(Kind(d, MapAreaKind.Hedge), Hedge, 1f);
        c.Rects(Kind(d, MapAreaKind.Fence), Fence, 1f);
        c.Rects(Kind(d, MapAreaKind.StoneWall), Stone, 1f);
        c.Rects(Kind(d, MapAreaKind.Tunnel), Tunnel, 1f);

        var outPx = new Color32[W * H];
        for (int i = 0; i < W * H; i++)
            outPx[i] = new Color32(To8(px[i * 3]), To8(px[i * 3 + 1]), To8(px[i * 3 + 2]), 255);
        return outPx;
    }

    static byte To8(float v) => (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);

    static List<Rect> Kind(TownMapData d, MapAreaKind k)
    {
        var l = new List<Rect>();
        foreach (var a in d.areas) if (a.kind == k) l.Add(a.rect);
        return l;
    }

    static List<Rect> Concat(List<Rect> a, List<Rect> b)
    {
        var l = new List<Rect>(a.Count + b.Count);
        l.AddRange(a);
        l.AddRange(b);
        return l;
    }

    /// <summary>Centre-line dash rects for every straight road segment (none in intersections, plazas or on the pad).</summary>
    public static List<Rect> Dashes(TownMapData d)
    {
        var l = new List<Rect>();
        for (int i = 0; i < d.roads.Count; i++)
        {
            var r = d.roads[i];
            if (r.axis > 1) continue;
            bool alongX = r.axis == 0;
            float a0 = alongX ? r.rect.xMin : r.rect.yMin, a1 = alongX ? r.rect.xMax : r.rect.yMax;
            float mid = alongX ? r.rect.center.y : r.rect.center.x;
            float start = Mathf.Floor(a0 / DashPeriod) * DashPeriod + (DashPeriod - DashLength);
            for (float s = start; s < a1; s += DashPeriod)
            {
                float s0 = Mathf.Max(s, a0 + 0.5f), s1 = Mathf.Min(s + DashLength, a1 - 0.5f);
                if (s1 - s0 < 0.6f) continue;
                float cm = (s0 + s1) * 0.5f;
                var centre = alongX ? new Vector2(cm, mid) : new Vector2(mid, cm);
                if (InCrossing(d, i, centre, alongX)) continue;
                l.Add(alongX ? Rect.MinMaxRect(s0, mid - DashWidth * 0.5f, s1, mid + DashWidth * 0.5f)
                             : Rect.MinMaxRect(mid - DashWidth * 0.5f, s0, mid + DashWidth * 0.5f, s1));
            }
        }
        return l;
    }

    /// <summary>Is this point of road <paramref name="self"/> where a crossing road meets it (or on a pad / plaza)?</summary>
    static bool InCrossing(TownMapData d, int self, Vector2 p, bool alongX)
    {
        var me = d.roads[self].rect;
        for (int j = 0; j < d.roads.Count; j++)
        {
            if (j == self) continue;
            var o = d.roads[j];
            bool perpendicular = o.axis == 2 || (o.axis == 0) != alongX;
            if (!perpendicular) continue;
            var or = o.rect;
            if (alongX)
            {
                // the crossing road's x-span (+1 m), and its z-span touching ours
                if (p.x > or.xMin - 1f && p.x < or.xMax + 1f && or.yMax >= me.yMin - 0.2f && or.yMin <= me.yMax + 0.2f) return true;
            }
            else if (p.y > or.yMin - 1f && p.y < or.yMax + 1f && or.xMax >= me.xMin - 0.2f && or.xMin <= me.xMax + 0.2f) return true;
        }
        foreach (var a in d.areas)
            if (a.kind == MapAreaKind.DepotPad && a.rect.Contains(p)) return true;
        return false;
    }

    // ------------------------------------------------------------------ the pixel canvas

    sealed class Canvas
    {
        readonly TownMapData d;
        readonly int W, H;
        readonly float[] px;
        readonly float ppm, bx, bz;

        public Canvas(TownMapData d, int w, int h, float[] px)
        {
            this.d = d; W = w; H = h; this.px = px;
            ppm = d.pxPerMeter; bx = d.bounds.x; bz = d.bounds.y;
        }

        public void Fill(Color32 c)
        {
            for (int i = 0; i < W * H; i++) { px[i * 3] = c.r / 255f; px[i * 3 + 1] = c.g / 255f; px[i * 3 + 2] = c.b / 255f; }
        }

        void Blend(int i, Color32 c, float a)
        {
            if (a <= 0f) return;
            if (a > 1f) a = 1f;
            int k = i * 3;
            px[k] += (c.r / 255f - px[k]) * a;
            px[k + 1] += (c.g / 255f - px[k + 1]) * a;
            px[k + 2] += (c.b / 255f - px[k + 2]) * a;
        }

        /// <summary>Union coverage of the rects (exact area per pixel, summed and clamped so abutting rects leave no seam).</summary>
        public float[] Coverage(List<Rect> rects)
        {
            var cov = new float[W * H];
            foreach (var r in rects)
            {
                float x0 = (r.xMin - bx) * ppm, x1 = (r.xMax - bx) * ppm, y0 = (r.yMin - bz) * ppm, y1 = (r.yMax - bz) * ppm;
                int ix0 = Mathf.Max(0, Mathf.FloorToInt(x0)), ix1 = Mathf.Min(W - 1, Mathf.CeilToInt(x1) - 1);
                int iy0 = Mathf.Max(0, Mathf.FloorToInt(y0)), iy1 = Mathf.Min(H - 1, Mathf.CeilToInt(y1) - 1);
                for (int y = iy0; y <= iy1; y++)
                {
                    float cy = Mathf.Clamp01(Mathf.Min(y1, y + 1) - Mathf.Max(y0, y));
                    if (cy <= 0f) continue;
                    int row = y * W;
                    for (int x = ix0; x <= ix1; x++)
                    {
                        float cx = Mathf.Clamp01(Mathf.Min(x1, x + 1) - Mathf.Max(x0, x));
                        if (cx <= 0f) continue;
                        float v = cov[row + x] + cx * cy;
                        cov[row + x] = v > 1f ? 1f : v;
                    }
                }
            }
            return cov;
        }

        /// <summary>Add a rounded rect's coverage (4×4 supersampled) into <paramref name="cov"/> (union, clamped).</summary>
        public void AddRound(float[] cov, Rect r, float radiusM)
        {
            float x0 = (r.xMin - bx) * ppm, x1 = (r.xMax - bx) * ppm, y0 = (r.yMin - bz) * ppm, y1 = (r.yMax - bz) * ppm;
            float rad = Mathf.Min(radiusM * ppm, Mathf.Min(x1 - x0, y1 - y0) * 0.5f);
            int ix0 = Mathf.Max(0, Mathf.FloorToInt(x0)), ix1 = Mathf.Min(W - 1, Mathf.CeilToInt(x1));
            int iy0 = Mathf.Max(0, Mathf.FloorToInt(y0)), iy1 = Mathf.Min(H - 1, Mathf.CeilToInt(y1));
            float cx = (x0 + x1) * 0.5f, cy = (y0 + y1) * 0.5f, hx = (x1 - x0) * 0.5f, hy = (y1 - y0) * 0.5f;
            for (int y = iy0; y <= iy1; y++)
                for (int x = ix0; x <= ix1; x++)
                {
                    int hits = 0;
                    for (int sy = 0; sy < 4; sy++)
                        for (int sx = 0; sx < 4; sx++)
                        {
                            float px0 = x + (sx + 0.5f) / 4f - cx, py0 = y + (sy + 0.5f) / 4f - cy;
                            float qx = Mathf.Abs(px0) - (hx - rad), qy = Mathf.Abs(py0) - (hy - rad);
                            float dd = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0f) - rad;
                            if (dd <= 0f) hits++;
                        }
                    if (hits == 0) continue;
                    int i = y * W + x;
                    float v = cov[i] + hits / 16f;
                    cov[i] = v > 1f ? 1f : v;
                }
        }

        public void Composite(float[] cov, Color32 c, float alpha)
        {
            for (int i = 0; i < W * H; i++) if (cov[i] > 0f) Blend(i, c, cov[i] * alpha);
        }

        public void Rects(IList<Rect> rects, Color32 c, float alpha)
        {
            if (rects == null || rects.Count == 0) return;
            var l = rects as List<Rect> ?? new List<Rect>(rects);
            Composite(Coverage(l), c, alpha);
        }

        /// <summary>The curb line: road pixels within <see cref="EdgePx"/> of a pixel that isn't drivable (map edge counts as road).</summary>
        public void Edges(float[] road, float[] drive, Color32 c)
        {
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = y * W + x;
                    if (road[i] < 0.5f) continue;
                    float edge = 0f;
                    for (int dy = -EdgePx; dy <= EdgePx && edge < 1f; dy++)
                        for (int dx = -EdgePx; dx <= EdgePx; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                            float out01 = 1f - drive[ny * W + nx];
                            if (out01 <= 0.01f) continue;
                            int dist = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy));
                            edge = Mathf.Max(edge, out01 * (dist <= EdgePx - 1 ? 1f : 0.55f));
                        }
                    if (edge > 0f) Blend(i, c, edge * road[i]);
                }
        }

        /// <summary>Yellow/ink 45° stripes in a band of <paramref name="border"/> m inside the rect's edge.</summary>
        public void Hazard(Rect r, float border)
        {
            int ix0 = Mathf.Max(0, Mathf.FloorToInt((r.xMin - bx) * ppm)), ix1 = Mathf.Min(W - 1, Mathf.CeilToInt((r.xMax - bx) * ppm) - 1);
            int iy0 = Mathf.Max(0, Mathf.FloorToInt((r.yMin - bz) * ppm)), iy1 = Mathf.Min(H - 1, Mathf.CeilToInt((r.yMax - bz) * ppm) - 1);
            for (int y = iy0; y <= iy1; y++)
                for (int x = ix0; x <= ix1; x++)
                {
                    float wx = bx + (x + 0.5f) / ppm, wz = bz + (y + 0.5f) / ppm;
                    float inset = Mathf.Min(Mathf.Min(wx - r.xMin, r.xMax - wx), Mathf.Min(wz - r.yMin, r.yMax - wz));
                    if (inset < 0f || inset > border) continue;
                    bool yellow = Mathf.Repeat(wx + wz, 2f) < 1f;
                    Blend(y * W + x, yellow ? Dash : HazardInk, 1f);
                }
        }

        /// <summary>A rounded rect with a 2 px outline (outline colour, then the fill inset by 2 px), 4×4 supersampled.</summary>
        public void RoundRect(Rect r, float radiusM, Color32 outline, Color32 fill)
        {
            float x0 = (r.xMin - bx) * ppm, x1 = (r.xMax - bx) * ppm, y0 = (r.yMin - bz) * ppm, y1 = (r.yMax - bz) * ppm;
            float rad = Mathf.Min(radiusM * ppm, Mathf.Min(x1 - x0, y1 - y0) * 0.5f);
            Shape(x0, y0, x1, y1, rad, outline);
            const float line = 2f;
            if (x1 - x0 > line * 2f + 1f && y1 - y0 > line * 2f + 1f)
                Shape(x0 + line, y0 + line, x1 - line, y1 - line, Mathf.Max(0f, rad - line), fill);
        }

        void Shape(float x0, float y0, float x1, float y1, float rad, Color32 c)
        {
            int ix0 = Mathf.Max(0, Mathf.FloorToInt(x0)), ix1 = Mathf.Min(W - 1, Mathf.CeilToInt(x1));
            int iy0 = Mathf.Max(0, Mathf.FloorToInt(y0)), iy1 = Mathf.Min(H - 1, Mathf.CeilToInt(y1));
            float cx = (x0 + x1) * 0.5f, cy = (y0 + y1) * 0.5f, hx = (x1 - x0) * 0.5f, hy = (y1 - y0) * 0.5f;
            for (int y = iy0; y <= iy1; y++)
                for (int x = ix0; x <= ix1; x++)
                {
                    int hits = 0;
                    for (int sy = 0; sy < 4; sy++)
                        for (int sx = 0; sx < 4; sx++)
                        {
                            float px0 = x + (sx + 0.5f) / 4f - cx, py0 = y + (sy + 0.5f) / 4f - cy;
                            float qx = Mathf.Abs(px0) - (hx - rad), qy = Mathf.Abs(py0) - (hy - rad);
                            float dd = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0f) - rad;
                            if (dd <= 0f) hits++;
                        }
                    if (hits > 0) Blend(y * W + x, c, hits / 16f);
                }
        }
    }
}
