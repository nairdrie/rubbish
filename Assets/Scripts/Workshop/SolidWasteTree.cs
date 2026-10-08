using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Where every blueprint sits on SolidWaste 3D's honeycomb (flat-top hexagons; odd columns sit half a row lower).
/// Pure geometry, no UI: every prerequisite inside a kit is a NEIGHBOUR, so a link is always a shared edge
/// (BlueprintTreeTests walks this). The three kits hang off Work Gloves; the Monument is a ten-cell spine on the right
/// that climbs from the bottom.
/// </summary>
public static class SolidWasteLayout
{
    /// <summary>Hexagon circumradius, and the lattice radius (the gap between neighbours is what is left over).</summary>
    public const float R = 56f, A = 62f;
    public const float ColPitch = 1.5f * A, HalfRow = 0.8660254f * A, HexW = 2f * R, HexH = 1.7320508f * R, Pad = 20f;

    /// <summary>A lattice cell: column and HALF-rows. Valid when <c>q + r2</c> is even.</summary>
    public readonly struct Cell
    {
        public readonly int q, r2;
        public Cell(int q, int r2) { this.q = q; this.r2 = r2; }
        public bool Valid => ((q + r2) & 1) == 0;
        public bool Adjacent(Cell o) => q == o.q ? Math.Abs(r2 - o.r2) == 2 : Math.Abs(q - o.q) == 1 && Math.Abs(r2 - o.r2) == 1;
    }

    static readonly Dictionary<string, Cell> cells = new Dictionary<string, Cell>(StringComparer.Ordinal)
    {
        // ---- Basics: the gloves on top, boots and hat tucked under them
        { CallingIds.WorkGloves, new Cell(4, 0) }, { CallingIds.WorkBoots, new Cell(3, 1) }, { CallingIds.HardHat, new Cell(5, 1) },
        // ---- Hauler: a diamond
        { CallingIds.GrabberPole, new Cell(0, 4) }, { CallingIds.RocketBlades, new Cell(1, 5) },
        { CallingIds.CompactorBackpack, new Cell(0, 6) }, { CallingIds.OneManRoute, new Cell(1, 7) },
        // ---- Scavenger: two columns of gadgets meeting at Call It, then the burglary branch fanning out under The Itch
        { CallingIds.Headlamp, new Cell(3, 5) }, { CallingIds.TreasureWand, new Cell(5, 5) },
        { CallingIds.NoseForMoney, new Cell(3, 7) }, { CallingIds.XRaySpecs, new Cell(5, 7) }, { CallingIds.CallIt, new Cell(4, 8) },
        { CallingIds.TheItch, new Cell(3, 9) }, { CallingIds.Lockpicks, new Cell(2, 10) }, { CallingIds.SoftSoles, new Cell(3, 11) },
        { CallingIds.SwagSack, new Cell(4, 10) }, { CallingIds.TheFence, new Cell(5, 11) },
        // ---- Raccoon: a flower around Pack Leader, with the cure (Thick Skin) hanging off the side
        { CallingIds.CritterHutch, new Cell(8, 4) }, { CallingIds.CollarChip, new Cell(7, 5) }, { CallingIds.BreedingBox, new Cell(9, 5) },
        { CallingIds.PackLeader, new Cell(8, 6) }, { CallingIds.BrainChip, new Cell(7, 7) }, { CallingIds.EmbraceIt, new Cell(9, 7) },
        { CallingIds.KingsRival, new Cell(8, 8) }, { CallingIds.RabiesVaccine, new Cell(9, 9) },
    };
    const int MonumentColumn = 11, MonumentBottom = 11, MaxR2 = 11, Columns = 13;

    public static bool TryCell(string nodeId, out Cell cell) => cells.TryGetValue(nodeId ?? "", out cell);
    public static IEnumerable<string> NodeIds => cells.Keys;
    /// <summary>The Monument spine zig-zags up two columns: tier 1 at the bottom, tier 10 at the top.</summary>
    public static Cell MonumentCell(int tier)
    {
        tier = Mathf.Clamp(tier, 1, MonumentCatalog.MaxLevel);
        return new Cell(MonumentColumn + ((tier - 1) & 1), MonumentBottom - (tier - 1));
    }
    /// <summary>Centre of a cell on the sheet (x right, y DOWN, origin top-left).</summary>
    public static Vector2 Center(Cell c) => new Vector2(Pad + R + c.q * ColPitch, Pad + HexH * 0.5f + c.r2 * HalfRow);
    public static Vector2 Size => new Vector2(Pad * 2f + HexW + (Columns - 1) * ColPitch, Pad * 2f + HexH + MaxR2 * HalfRow);
    /// <summary>The six corners of a flat-top hexagon of circumradius <paramref name="r"/> around <paramref name="c"/>.</summary>
    public static void Corners(Vector2 c, float r, Vector2[] into)
    {
        for (int i = 0; i < 6; i++) { float a = i * Mathf.PI / 3f; into[i] = new Vector2(c.x + Mathf.Cos(a) * r, c.y + Mathf.Sin(a) * r); }
    }
    public static bool InHex(Vector2 p, Vector2 c, float r)
    {
        float dx = Mathf.Abs(p.x - c.x), dy = Mathf.Abs(p.y - c.y), h = 0.8660254f * r;
        return dy <= h && dx <= r && h * dx + 0.5f * r * dy <= h * r;
    }
    /// <summary>Where the trunk meets each kit: the banner above its first blueprints (sheet coordinates of its centre).</summary>
    public static Vector2 KitAnchor(Calling calling) => calling switch
    {
        Calling.Hauler => new Vector2(Center(new Cell(0, 4)).x + ColPitch * 0.5f, Center(new Cell(0, 2)).y + 30f),
        Calling.Scavenger => new Vector2(Center(new Cell(4, 4)).x, Center(new Cell(4, 4)).y - 14f),
        Calling.Raccoon => new Vector2(Center(new Cell(8, 2)).x, Center(new Cell(8, 2)).y + 30f),
        _ => new Vector2(Center(new Cell(1, 1)).x + 18f, Center(new Cell(1, 1)).y - 20f),
    };
    public static Vector2 MonumentAnchor => new Vector2(Center(new Cell(MonumentColumn, 1)).x + ColPitch * 0.5f, Center(new Cell(MonumentColumn, 1)).y - 34f);
    /// <summary>The y of the trunk's horizontal run under the Basics (between Boots / Hat and the kit banners).</summary>
    public static float BusY => Center(new Cell(4, 2)).y + 4f;
}

/// <summary>The colours of SolidWaste 3D: one accent per kit, gold for the Monument, blueprint cyan for the sheet.</summary>
public static class SolidWasteColors
{
    public static readonly Color Sheet = new Color32(0x0C, 0x1A, 0x30, 0xFF), Cyan = new Color32(0x46, 0xE3, 0xF0, 0xFF);
    public static readonly Color Body = new Color32(0x14, 0x21, 0x36, 0xFF), BodyOff = new Color32(0x0F, 0x18, 0x28, 0xFF);
    public static readonly Color Off = new Color32(0x3A, 0x4A, 0x66, 0xFF), Text = new Color32(0xE6, 0xF1, 0xFF, 0xFF);
    public static readonly Color Hot = new Color32(0xFF, 0x7A, 0x1A, 0xFF), Gold = new Color32(0xFF, 0xC8, 0x3A, 0xFF);
    public static readonly Color Good = new Color32(0x6B, 0xE6, 0x75, 0xFF), Bad = new Color32(0xFF, 0x6B, 0x5E, 0xFF);
    static readonly Color[] kits =
    {
        new Color32(0x25, 0xD0, 0xB4, 0xFF), // Basics: workbench teal
        new Color32(0xFF, 0x8A, 0x2A, 0xFF), // Hauler: truck orange
        new Color32(0x4C, 0x9B, 0xFF, 0xFF), // Scavenger: bottle blue
        new Color32(0xB8, 0x84, 0xFF, 0xFF), // Raccoon: midnight purple
    };
    public static Color Kit(Calling calling) => kits[(int)calling];
    /// <summary>The four sorted types, in WorkshopMaterial order (the bins' colours, brightened for a dark sheet).</summary>
    public static readonly Color[] Materials =
    {
        new Color32(0xB9, 0xC6, 0xD6, 0xFF), new Color32(0x4C, 0x9B, 0xFF, 0xFF), new Color32(0x4F, 0xD9, 0x8B, 0xFF), new Color32(0xE2, 0xB2, 0x7A, 0xFF),
    };
    public static Color A(Color c, float a) => new Color(c.r, c.g, c.b, a);
    public static Color Mix(Color a, Color b, float t) => Color.Lerp(a, b, t);
}

/// <summary>How a hexagon reads at a glance (always a shape AND a word, never colour alone).</summary>
public enum HexLook : byte { Locked, Need, Ready, Partial, Full }

/// <summary>
/// One hexagon of the tree: a blueprint (a Callings node) or a Monument tier. Draws itself with Painter2D, is hit-tested as
/// a hexagon, and carries the two labels that sit inside it. All of its state is set by <see cref="SolidWasteTree"/>.
/// </summary>
public sealed class HexNode : VisualElement
{
    /// <summary>Room around the hexagon for the selection ring and the ready glow.</summary>
    public const float Margin = 10f;
    public readonly string id;
    public readonly CallingNode node;
    /// <summary>1..10 for a Monument tier, 0 for a blueprint.</summary>
    public readonly int tier;
    public readonly Color accent;
    public readonly bool trunkRoot;
    public HexLook look;
    public bool selected;
    public int level, maxLevel;
    /// <summary>0..1: how much of the hexagon is filled from the bottom (a Monument load, or a print in progress).</summary>
    public float fill;
    /// <summary>≥ 0 while the "printing" animation plays (0..1), −1 otherwise.</summary>
    public float printing = -1f;
    public float pulse;
    readonly Label title, tag;
    static readonly Vector2[] hex = new Vector2[6], clip = new Vector2[8];
    static readonly string[] Roman = { "", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X" };

    public HexNode(string id, CallingNode node, int tier, Color accent, bool trunkRoot)
    {
        this.id = id; this.node = node; this.tier = tier; this.accent = accent; this.trunkRoot = trunkRoot;
        AddToClassList("sw-node");
        style.width = SolidWasteLayout.HexW + Margin * 2f; style.height = SolidWasteLayout.HexH + Margin * 2f;
        focusable = true;
        title = new Label(tier > 0 ? Roman[tier] : node != null ? node.name : id) { pickingMode = PickingMode.Ignore };
        title.AddToClassList(tier > 0 ? "sw-node-numeral" : "sw-node-name");
        Add(title);
        tag = new Label("") { pickingMode = PickingMode.Ignore };
        tag.AddToClassList("sw-node-tag");
        tag.style.bottom = Margin + 9f;
        Add(tag);
        generateVisualContent += Draw;
    }

    public void Place(Vector2 center)
    {
        style.left = center.x - SolidWasteLayout.R - Margin;
        style.top = center.y - SolidWasteLayout.HexH * 0.5f - Margin;
    }

    public void SetTag(string text) { if (tag.text != text) tag.text = text; }

    /// <summary>Apply the state classes the labels read (dim, off, full, ready, selected).</summary>
    public void ApplyClasses()
    {
        EnableInClassList("sw-node--selected", selected);
        EnableInClassList("sw-node--off", look == HexLook.Locked);
        EnableInClassList("sw-node--dim", look == HexLook.Need);
        EnableInClassList("sw-node--ready", look == HexLook.Ready);
        EnableInClassList("sw-node--full", look == HexLook.Full);
    }

    public override bool ContainsPoint(Vector2 localPoint)
    {
        var c = new Vector2(layout.width * 0.5f, layout.height * 0.5f);
        return SolidWasteLayout.InHex(localPoint, c, SolidWasteLayout.R + 2f);
    }

    static void Path(Painter2D p, Vector2[] pts, int n)
    {
        p.BeginPath(); p.MoveTo(pts[0]);
        for (int i = 1; i < n; i++) p.LineTo(pts[i]);
        p.ClosePath();
    }

    /// <summary>The part of the hexagon at or below <paramref name="y"/> (y grows downwards), into <see cref="clip"/>.</summary>
    static int ClipBelow(Vector2[] poly, float y)
    {
        int n = 0;
        for (int i = 0; i < 6; i++)
        {
            Vector2 a = poly[i], b = poly[(i + 1) % 6];
            bool ain = a.y >= y, bin = b.y >= y;
            if (ain) clip[n++] = a;
            if (ain != bin) { float t = (y - a.y) / (b.y - a.y); clip[n++] = new Vector2(a.x + (b.x - a.x) * t, y); }
        }
        return n;
    }

    void Draw(MeshGenerationContext mgc)
    {
        float w = contentRect.width, h = contentRect.height;
        if (float.IsNaN(w) || w < 8f || h < 8f) return;
        var p = mgc.painter2D;
        p.lineJoin = LineJoin.Round; p.lineCap = LineCap.Round;
        var c = new Vector2(w * 0.5f, h * 0.5f);
        float r = SolidWasteLayout.R;
        bool off = look == HexLook.Locked;
        Color line = off ? SolidWasteColors.Off : look == HexLook.Need ? SolidWasteColors.A(accent, 0.62f) : accent;

        if (look == HexLook.Ready)
        {
            // a breathing halo: this one can be printed right now
            SolidWasteLayout.Corners(c, r + 4.5f + pulse * 2.5f, hex); Path(p, hex, 6);
            p.strokeColor = SolidWasteColors.A(SolidWasteColors.Hot, 0.25f + 0.45f * pulse); p.lineWidth = 5f; p.Stroke();
        }
        if (selected)
        {
            SolidWasteLayout.Corners(c, r + 7f, hex); Path(p, hex, 6);
            p.strokeColor = Color.white; p.lineWidth = 2.5f; p.Stroke();
        }

        // body
        SolidWasteLayout.Corners(c, r - 1.5f, hex); Path(p, hex, 6);
        Color body = look == HexLook.Full ? accent
            : look == HexLook.Partial ? SolidWasteColors.Mix(SolidWasteColors.Body, accent, 0.34f)
            : off ? SolidWasteColors.BodyOff : SolidWasteColors.Body;
        p.fillColor = body; p.Fill();

        // what is loaded / printed so far rises from the bottom, in print layers
        float level01 = printing >= 0f ? printing : fill;
        if (level01 > 0.001f && look != HexLook.Full || printing >= 0f)
        {
            float top = c.y + r * 0.8660254f, y = Mathf.Lerp(top, c.y - r * 0.8660254f, Mathf.Clamp01(level01));
            int n = ClipBelow(hex, y);
            if (n >= 3)
            {
                Path(p, clip, n);
                p.fillColor = printing >= 0f ? SolidWasteColors.Mix(accent, Color.white, 0.12f) : SolidWasteColors.A(accent, 0.55f); p.Fill();
                p.strokeColor = SolidWasteColors.A(Color.white, 0.16f); p.lineWidth = 1f;
                for (float ly = top - 5f; ly > y + 1f; ly -= 5f)
                {
                    float half = (r - 1.5f) - Mathf.Abs(ly - c.y) / 1.7320508f - 4f;
                    if (half <= 2f) continue;
                    p.BeginPath(); p.MoveTo(new Vector2(c.x - half, ly)); p.LineTo(new Vector2(c.x + half, ly)); p.Stroke();
                }
                if (printing >= 0f && printing < 1f)
                {
                    // the nozzle: a bright line on the layer being laid down
                    float half = (r - 1.5f) - Mathf.Abs(y - c.y) / 1.7320508f;
                    p.strokeColor = Color.white; p.lineWidth = 2.5f;
                    p.BeginPath(); p.MoveTo(new Vector2(c.x - half, y)); p.LineTo(new Vector2(c.x + half, y)); p.Stroke();
                }
            }
        }

        // outline (dashed while locked), and an inner bevel so the cell has some depth
        SolidWasteLayout.Corners(c, r - 1.5f, hex);
        if (off)
        {
            p.strokeColor = line; p.lineWidth = 2f;
            for (int i = 0; i < 6; i++)
            {
                Vector2 a = hex[i], b = hex[(i + 1) % 6];
                for (int k = 0; k < 4; k++)
                {
                    p.BeginPath(); p.MoveTo(Vector2.Lerp(a, b, k / 4f + 0.03f)); p.LineTo(Vector2.Lerp(a, b, k / 4f + 0.15f)); p.Stroke();
                }
            }
        }
        else
        {
            Path(p, hex, 6);
            p.strokeColor = look == HexLook.Full ? SolidWasteColors.Mix(accent, Color.white, 0.55f) : line; p.lineWidth = look == HexLook.Ready ? 3.5f : 3f; p.Stroke();
            SolidWasteLayout.Corners(c, r - 8f, hex); Path(p, hex, 6);
            p.strokeColor = look == HexLook.Full ? SolidWasteColors.A(Color.white, 0.28f) : SolidWasteColors.A(accent, 0.16f); p.lineWidth = 1.2f; p.Stroke();
        }

        // version pips along the bottom (multi-level blueprints)
        if (tier == 0 && maxLevel > 1)
        {
            Color ink = look == HexLook.Full ? new Color(0.03f, 0.06f, 0.11f) : off ? SolidWasteColors.Off : SolidWasteColors.Text;
            for (int i = 0; i < maxLevel; i++)
            {
                var at = new Vector2(c.x + (i - (maxLevel - 1) * 0.5f) * 12f, c.y + 18f);
                p.BeginPath(); p.Arc(at, 3.6f, Angle.Degrees(0f), Angle.Degrees(360f)); p.ClosePath();
                if (i < level) { p.fillColor = ink; p.Fill(); }
                else { p.strokeColor = SolidWasteColors.A(ink, 0.7f); p.lineWidth = 1.4f; p.Stroke(); }
            }
        }
        // a capstone wears a small crown notch on its top edge; a kit's first blueprints a plug where the trunk feeds them
        if (node != null && node.isCapstone)
        {
            float y0 = c.y - r * 0.8660254f + 7f;
            p.BeginPath();
            p.MoveTo(new Vector2(c.x - 11f, y0 + 9f)); p.LineTo(new Vector2(c.x - 11f, y0)); p.LineTo(new Vector2(c.x - 5f, y0 + 5f));
            p.LineTo(new Vector2(c.x, y0 - 2f)); p.LineTo(new Vector2(c.x + 5f, y0 + 5f)); p.LineTo(new Vector2(c.x + 11f, y0)); p.LineTo(new Vector2(c.x + 11f, y0 + 9f));
            p.ClosePath();
            p.fillColor = look == HexLook.Full ? new Color(0.03f, 0.06f, 0.11f) : off ? SolidWasteColors.Off : SolidWasteColors.Gold; p.Fill();
        }
        // the state mark, top right inside the cell: a tick (printed), a padlock (locked)
        var m = new Vector2(c.x + r * 0.52f, c.y - r * 0.5f);
        if (look == HexLook.Full)
        {
            p.strokeColor = new Color(0.03f, 0.06f, 0.11f); p.lineWidth = 3f;
            p.BeginPath(); p.MoveTo(new Vector2(m.x - 5f, m.y)); p.LineTo(new Vector2(m.x - 1.5f, m.y + 4f)); p.LineTo(new Vector2(m.x + 5.5f, m.y - 4.5f)); p.Stroke();
        }
        else if (off)
        {
            p.strokeColor = SolidWasteColors.Off; p.lineWidth = 2f;
            p.BeginPath(); p.Arc(new Vector2(m.x, m.y - 1.5f), 3.6f, Angle.Degrees(180f), Angle.Degrees(360f)); p.Stroke();
            p.BeginPath(); p.MoveTo(new Vector2(m.x - 5f, m.y - 1f)); p.LineTo(new Vector2(m.x + 5f, m.y - 1f)); p.LineTo(new Vector2(m.x + 5f, m.y + 6f)); p.LineTo(new Vector2(m.x - 5f, m.y + 6f)); p.ClosePath();
            p.fillColor = SolidWasteColors.Off; p.Fill();
        }
    }
}

/// <summary>
/// The sheet under the hexagons: the blueprint grid, the ghost honeycomb, the trunk from Work Gloves to the kit banners
/// and the bridges between neighbours (lit when the prerequisite is printed, dashed while it is not).
/// </summary>
public sealed class HexSheet : VisualElement
{
    public struct Link { public Vector2 a, b; public Color color; public bool met; }
    public struct Trunk { public Vector2[] points; public Color color; public bool met; }
    public readonly List<Link> links = new List<Link>();
    public readonly List<Trunk> trunks = new List<Trunk>();
    public readonly List<Vector2> ghosts = new List<Vector2>();
    static readonly Vector2[] hex = new Vector2[6];

    public HexSheet()
    {
        pickingMode = PickingMode.Ignore;
        AddToClassList("sw-layer");
        generateVisualContent += Draw;
    }

    void Draw(MeshGenerationContext mgc)
    {
        float w = contentRect.width, h = contentRect.height;
        if (float.IsNaN(w) || w < 8f || h < 8f) return;
        var p = mgc.painter2D;
        p.lineJoin = LineJoin.Round; p.lineCap = LineCap.Butt;

        // blueprint grid
        p.lineWidth = 1f;
        for (int i = 0; i * 31f < w; i++)
        {
            p.strokeColor = SolidWasteColors.A(SolidWasteColors.Cyan, i % 4 == 0 ? 0.075f : 0.035f);
            p.BeginPath(); p.MoveTo(new Vector2(i * 31f, 0f)); p.LineTo(new Vector2(i * 31f, h)); p.Stroke();
        }
        for (int i = 0; i * 31f < h; i++)
        {
            p.strokeColor = SolidWasteColors.A(SolidWasteColors.Cyan, i % 4 == 0 ? 0.075f : 0.035f);
            p.BeginPath(); p.MoveTo(new Vector2(0f, i * 31f)); p.LineTo(new Vector2(w, i * 31f)); p.Stroke();
        }
        // the empty cells of the honeycomb
        p.strokeColor = SolidWasteColors.A(SolidWasteColors.Cyan, 0.07f); p.lineWidth = 1.2f;
        foreach (var g in ghosts)
        {
            SolidWasteLayout.Corners(g, SolidWasteLayout.R - 1.5f, hex);
            p.BeginPath(); p.MoveTo(hex[0]); for (int i = 1; i < 6; i++) p.LineTo(hex[i]); p.ClosePath(); p.Stroke();
        }

        // the trunk
        p.lineCap = LineCap.Round;
        foreach (var t in trunks)
        {
            if (t.points == null || t.points.Length < 2) continue;
            if (t.met)
            {
                Polyline(p, t.points, SolidWasteColors.A(t.color, 0.22f), 9f);
                Polyline(p, t.points, t.color, 3f);
            }
            else Dashed(p, t.points, SolidWasteColors.A(SolidWasteColors.Off, 0.9f), 2.5f);
        }
        // bridges across shared edges
        foreach (var l in links)
        {
            Vector2 mid = (l.a + l.b) * 0.5f, dir = (l.b - l.a).normalized;
            Vector2 from = mid - dir * 12f, to = mid + dir * 12f;
            if (l.met)
            {
                p.strokeColor = SolidWasteColors.A(l.color, 0.25f); p.lineWidth = 18f;
                p.BeginPath(); p.MoveTo(from); p.LineTo(to); p.Stroke();
                p.strokeColor = l.color; p.lineWidth = 9f;
                p.BeginPath(); p.MoveTo(from); p.LineTo(to); p.Stroke();
            }
            else
            {
                p.strokeColor = SolidWasteColors.Off; p.lineWidth = 3f;
                Vector2 side = new Vector2(-dir.y, dir.x) * 4f;
                p.BeginPath(); p.MoveTo(from + side); p.LineTo(to + side); p.Stroke();
                p.BeginPath(); p.MoveTo(from - side); p.LineTo(to - side); p.Stroke();
            }
        }
    }

    static void Polyline(Painter2D p, Vector2[] pts, Color color, float width)
    {
        p.strokeColor = color; p.lineWidth = width;
        p.BeginPath(); p.MoveTo(pts[0]);
        for (int i = 1; i < pts.Length; i++) p.LineTo(pts[i]);
        p.Stroke();
    }

    static void Dashed(Painter2D p, Vector2[] pts, Color color, float width)
    {
        p.strokeColor = color; p.lineWidth = width;
        for (int i = 0; i + 1 < pts.Length; i++)
        {
            Vector2 a = pts[i], b = pts[i + 1];
            float length = Vector2.Distance(a, b);
            int n = Mathf.Max(1, Mathf.RoundToInt(length / 14f));
            for (int k = 0; k < n; k++)
            {
                p.BeginPath(); p.MoveTo(Vector2.Lerp(a, b, k / (float)n)); p.LineTo(Vector2.Lerp(a, b, (k + 0.55f) / n)); p.Stroke();
            }
        }
    }
}

/// <summary>
/// The whole tree on its sheet: built once from <see cref="CallingCatalog"/> and <see cref="MonumentCatalog"/>, then only
/// its values change. Presentation only: what a hexagon may do is decided by <see cref="WorkshopState"/>.
/// </summary>
public sealed class SolidWasteTree
{
    public readonly VisualElement root;
    public readonly HexSheet sheet;
    public readonly List<HexNode> nodes = new List<HexNode>();
    readonly Dictionary<string, HexNode> byId = new Dictionary<string, HexNode>(StringComparer.Ordinal);
    readonly List<(HexNode from, HexNode to, int level, int link)> edges = new List<(HexNode, HexNode, int, int)>();
    readonly List<VisualElement> banners = new List<VisualElement>();
    int monumentFirstLink;
    public Action<HexNode> Selected, Submitted;

    public static string MonumentId(int tier) => MonumentCatalog.NodeId + ":" + tier;
    public HexNode Find(string id) => id != null && byId.TryGetValue(id, out var n) ? n : null;

    public SolidWasteTree(VisualElement canvas)
    {
        root = canvas;
        root.Clear();
        Vector2 size = SolidWasteLayout.Size;
        root.style.width = size.x; root.style.height = size.y;
        sheet = new HexSheet();
        root.Add(sheet);

        var used = new HashSet<long>();
        foreach (var node in CallingCatalog.All)
        {
            if (!SolidWasteLayout.TryCell(node.id, out var cell)) continue;
            bool trunkRoot = node.calling != Calling.Basics && node.prereqs.Count == 1 && node.prereqs[0].StartsWith(CallingCatalog.TrunkId, StringComparison.Ordinal);
            Add(new HexNode(node.id, node, 0, SolidWasteColors.Kit(node.calling), trunkRoot), cell, used);
        }
        for (int tier = 1; tier <= MonumentCatalog.MaxLevel; tier++)
            Add(new HexNode(MonumentId(tier), null, tier, SolidWasteColors.Gold, false), SolidWasteLayout.MonumentCell(tier), used);

        // bridges: every prerequisite inside a kit is a neighbour
        foreach (var node in CallingCatalog.All)
            foreach (string prereq in node.prereqs)
            {
                if (!CallingCatalog.TryParsePrereq(prereq, out var other, out int level) || other.calling != node.calling) continue;
                var a = Find(other.id); var b = Find(node.id);
                if (a == null || b == null) continue;
                edges.Add((a, b, level, sheet.links.Count));
                sheet.links.Add(new HexSheet.Link { a = CenterOf(a), b = CenterOf(b), color = a.accent });
            }
        monumentFirstLink = sheet.links.Count;
        for (int tier = 1; tier < MonumentCatalog.MaxLevel; tier++)
            sheet.links.Add(new HexSheet.Link { a = SolidWasteLayout.Center(SolidWasteLayout.MonumentCell(tier)), b = SolidWasteLayout.Center(SolidWasteLayout.MonumentCell(tier + 1)), color = SolidWasteColors.Gold });

        // the trunk: gloves -> the run under the Basics -> each kit's banner
        Vector2 gloves = SolidWasteLayout.Center(new SolidWasteLayout.Cell(4, 0));
        float busY = SolidWasteLayout.BusY;
        Vector2 stem = new Vector2(gloves.x, gloves.y + SolidWasteLayout.HexH * 0.5f);
        foreach (var calling in new[] { Calling.Hauler, Calling.Scavenger, Calling.Raccoon })
        {
            Vector2 at = SolidWasteLayout.KitAnchor(calling);
            sheet.trunks.Add(new HexSheet.Trunk
            {
                color = SolidWasteColors.Kit(Calling.Basics),
                points = Mathf.Abs(at.x - gloves.x) < 1f ? new[] { stem, new Vector2(at.x, at.y - 14f) }
                    : new[] { stem, new Vector2(gloves.x, busY), new Vector2(at.x, busY), new Vector2(at.x, at.y - 14f) },
            });
        }

        // the ghost honeycomb: every lattice cell on the sheet that holds nothing
        for (int q = -1; q <= 13; q++)
            for (int r2 = -1; r2 <= 13; r2++)
            {
                var cell = new SolidWasteLayout.Cell(q, r2);
                if (!cell.Valid || used.Contains(Key(cell))) continue;
                sheet.ghosts.Add(SolidWasteLayout.Center(cell));
            }

        foreach (var info in CallingCatalog.Callings) Banner(SolidWasteLayout.KitAnchor(info.calling), KitName(info.calling), info.tagline, SolidWasteColors.Kit(info.calling));
        Banner(SolidWasteLayout.MonumentAnchor, UiText.PrintMonumentBanner, UiText.PrintMonumentTagline, SolidWasteColors.Gold);
    }

    public static string KitName(Calling calling) => calling == Calling.Basics ? UiText.PrintBasicsBanner : CallingCatalog.Info(calling).name.ToUpperInvariant() + " " + UiText.PrintKitWord;

    static long Key(SolidWasteLayout.Cell c) => ((long)(c.q + 64) << 16) | (uint)(c.r2 + 64);
    static Vector2 CenterOf(HexNode n) => n.tier > 0 ? SolidWasteLayout.Center(SolidWasteLayout.MonumentCell(n.tier))
        : SolidWasteLayout.TryCell(n.id, out var cell) ? SolidWasteLayout.Center(cell) : Vector2.zero;

    void Add(HexNode hex, SolidWasteLayout.Cell cell, HashSet<long> used)
    {
        used.Add(Key(cell));
        hex.Place(SolidWasteLayout.Center(cell));
        hex.RegisterCallback<ClickEvent>(e => { Selected?.Invoke(hex); if (e.clickCount >= 2) Submitted?.Invoke(hex); e.StopPropagation(); });
        hex.RegisterCallback<FocusInEvent>(_ => Selected?.Invoke(hex));
        hex.RegisterCallback<NavigationSubmitEvent>(e => { Submitted?.Invoke(hex); e.StopPropagation(); });
        nodes.Add(hex); byId[hex.id] = hex;
        root.Add(hex);
    }

    void Banner(Vector2 center, string name, string tagline, Color color)
    {
        var banner = new VisualElement { pickingMode = PickingMode.Ignore };
        banner.AddToClassList("sw-banner");
        banner.style.borderTopColor = banner.style.borderBottomColor = banner.style.borderLeftColor = banner.style.borderRightColor = SolidWasteColors.A(color, 0.7f);
        var title = new Label(name) { pickingMode = PickingMode.Ignore };
        title.AddToClassList("sw-banner-name"); title.style.color = color;
        banner.Add(title);
        if (!string.IsNullOrEmpty(tagline))
        {
            var sub = new Label(tagline) { pickingMode = PickingMode.Ignore };
            sub.AddToClassList("sw-banner-tag");
            banner.Add(sub);
        }
        // centred on its anchor once it has been measured
        banner.style.left = center.x; banner.style.top = center.y;
        banner.RegisterCallback<GeometryChangedEvent>(e =>
        {
            banner.style.left = center.x - e.newRect.width * 0.5f;
            banner.style.top = center.y - e.newRect.height * 0.5f;
        });
        banners.Add(banner);
        root.Add(banner);
    }

    /// <summary>
    /// Bring every hexagon up to date. <paramref name="have"/> says how many usable kept items of a def the owner holds
    /// (null = none: nothing reads READY); <paramref name="monumentReady"/> = a load of the next tier is possible now.
    /// </summary>
    public void Refresh(WorkshopState state, Func<string, int> have, string selectedId, bool monumentReady)
    {
        state ??= new WorkshopState();
        foreach (var hex in nodes)
        {
            HexLook look; int level = 0, max = 0; float fill = 0f; string tag;
            if (hex.tier > 0)
            {
                int built = state.monument;
                if (hex.tier <= built) { look = HexLook.Full; tag = UiText.PrintTagPrinted; }
                else if (hex.tier == built + 1)
                {
                    var cost = MonumentCatalog.Cost(hex.tier);
                    fill = cost.Total > 0 ? state.fed.Total / (float)cost.Total : 0f;
                    look = monumentReady ? HexLook.Ready : HexLook.Need;
                    tag = Mathf.FloorToInt(fill * 100f) + "%";
                }
                else { look = HexLook.Locked; tag = ""; }
            }
            else
            {
                var node = hex.node;
                level = state.CallingLevelAt(node.index); max = node.maxLevel;
                switch (state.StatusOf(node, have))
                {
                    case CallingStatus.Maxed: look = HexLook.Full; tag = UiText.PrintTagPrinted; break;
                    case CallingStatus.Ready: look = HexLook.Ready; tag = UiText.PrintTagReady; break;
                    case CallingStatus.CantAfford:
                        look = level > 0 ? HexLook.Partial : HexLook.Need;
                        tag = string.Format(UiText.PrintTagNeed, Missing(state.NextRecipe(node), have)); break;
                    case CallingStatus.Locked: look = HexLook.Locked; tag = level > 0 ? UiText.PrintTagPrinted : ""; break;
                    default: look = level > 0 ? HexLook.Partial : HexLook.Locked; tag = UiText.PrintTagSoon; break;
                }
                if (max > 1) tag = ""; // the pips sit where the word would
            }
            bool selected = hex.id == selectedId;
            if (hex.look != look || hex.level != level || hex.maxLevel != max || hex.selected != selected || !Mathf.Approximately(hex.fill, fill))
            {
                hex.look = look; hex.level = level; hex.maxLevel = max; hex.selected = selected; hex.fill = fill;
                hex.ApplyClasses(); hex.MarkDirtyRepaint();
            }
            hex.SetTag(tag);
        }
        bool dirty = false;
        foreach (var (from, to, level, link) in edges)
        {
            bool met = state.CallingLevelAt(from.node.index) >= level;
            var l = sheet.links[link];
            if (l.met != met) { l.met = met; sheet.links[link] = l; dirty = true; }
        }
        for (int tier = 1; tier < MonumentCatalog.MaxLevel; tier++)
        {
            var l = sheet.links[monumentFirstLink + tier - 1]; bool met = state.monument >= tier;
            if (l.met != met) { l.met = met; sheet.links[monumentFirstLink + tier - 1] = l; dirty = true; }
        }
        bool trunk = state.CallingLevel(CallingCatalog.TrunkId) >= 1;
        for (int i = 0; i < sheet.trunks.Count; i++)
        {
            var t = sheet.trunks[i];
            if (t.met != trunk) { t.met = trunk; sheet.trunks[i] = t; dirty = true; }
        }
        if (dirty) sheet.MarkDirtyRepaint();
    }

    static int Missing(PartRecipe recipe, Func<string, int> have)
    {
        int missing = 0;
        if (recipe != null) foreach (var slot in recipe.slots) missing += slot.count - PartRecipe.Covered(slot, have);
        return missing;
    }

    /// <summary>Per frame while the app is showing: the ready halo breathes, a print in progress climbs.</summary>
    public void Animate(float time, float dt)
    {
        float pulse = 0.5f + 0.5f * Mathf.Sin(time * 3.2f);
        foreach (var hex in nodes)
        {
            bool repaint = false;
            if (hex.look == HexLook.Ready && Mathf.Abs(hex.pulse - pulse) > 0.03f) { hex.pulse = pulse; repaint = true; }
            if (hex.printing >= 0f)
            {
                hex.printing += dt / 1.5f; repaint = true;
                if (hex.printing >= 1f)
                {
                    hex.printing = -1f;
                    hex.AddToClassList("sw-node--pop");
                    hex.schedule.Execute(() => hex.RemoveFromClassList("sw-node--pop")).ExecuteLater(220);
                }
            }
            if (repaint) hex.MarkDirtyRepaint();
        }
    }

    /// <summary>Play the "being printed" fill on a hexagon (a blueprint level that just landed, a Monument tier).</summary>
    public void PlayPrint(string id)
    {
        var hex = Find(id);
        if (hex == null) return;
        hex.printing = 0f; hex.MarkDirtyRepaint();
    }
}
