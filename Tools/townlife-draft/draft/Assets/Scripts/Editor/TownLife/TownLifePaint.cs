// Owner: paint agent. Contract: design/CONTRACT.md.
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>Acres house palette (DESIGN section 8 'Paint'). Creates no GameObjects: fills ctx.materialOverrides
/// (Renderer -> full sharedMaterials array) with TownLifeKit.Variant copies; the builder applies them to the scene, the
/// preview to its clones.
///
/// How the pack houses are built: ONE MeshRenderer per house ("SM_house_suburb_00N"), 7-9 flat-colour URP/Lit slots, no
/// atlas. Slots by material name: M_wall* (walls), M_edges (trim, corner boards, fascia), M_roof, M_doors, plus
/// M_stoneblocks / M_stone / M_white / M_metallic / M_garagedoor / M_dirt / M_shiny / M_black 1 (left alone) and
/// M_windows / M_emissive (NEVER touched: HouseWindows keys night lighting on those names).
/// Paint swaps only the wall, trim, roof and door slots. Variants are always copied from the canonical pack material
/// loaded by path (never from whatever sits in the slot), and a slot is recognised either by its pack name or by our own
/// variant prefix, so re-running on an already painted house is a no-op and never makes a variant of a variant.</summary>
public static class TownLifePaint
{
    /// <summary>Module name used in options, reports and RNG keys.</summary>
    public const string Name = "paint";

    const string PackMaterials = "Assets/Suburbs Town Pack/MATERIALS/";
    const string WallKey = "paint.wall.", RoofKey = "paint.roof.", DoorKey = "paint.door.", TrimKey = "paint.trim";

    sealed class Tone
    {
        public string id, family; public Color color; public string[] roofs, doors;
        public Tone(string id, string family, string hex, string roofs, string doors)
        { this.id = id; this.family = family; color = Hex(hex); this.roofs = roofs.Split(','); this.doors = doors.Split(','); }
    }

    // Warm toy-town walls: soft candy colours, a notch more saturated and mid-toned than the Meadows pastels so the two
    // districts stay distinct. `roofs` / `doors` list what harmonises with each wall.
    static readonly Tone[] Walls =
    {
        new Tone("cream",      "yellow", "EFE2BF", "terracotta,slate,brown,charcoal", "red,navy,green,teal"),
        new Tone("butter",     "yellow", "EFCF7C", "slate,brown,charcoal",            "navy,white,green,red"),
        new Tone("sage",       "green",  "B2CA9F", "brown,charcoal,terracotta",       "cream,red,wood,yellow"),
        new Tone("sky",        "blue",   "A6CAE3", "charcoal,slate,brown",            "red,white,yellow,navy"),
        new Tone("brick",      "red",    "BA6B58", "charcoal,slate,brown",            "cream,white,navy,green"),
        new Tone("terracotta", "red",    "DB9468", "charcoal,brown,slate",            "teal,white,navy,green"),
        new Tone("pink",       "pink",   "E3A9AA", "charcoal,slate,brown",            "white,navy,teal,cream"),
        new Tone("teal",       "green",  "86C4BA", "charcoal,brown,terracotta",       "cream,red,yellow,wood"),
    };
    static readonly Dictionary<string, Color> Roofs = new Dictionary<string, Color>
    {
        { "charcoal",   Hex("4A4E57") },
        { "brown",      Hex("70503F") },
        { "slate",      Hex("536B82") },
        { "terracotta", Hex("A9604D") },
    };
    static readonly Dictionary<string, Color> Doors = new Dictionary<string, Color>
    {
        { "red",    Hex("B8402F") },
        { "navy",   Hex("2F4A6B") },
        { "green",  Hex("3F6B4A") },
        { "teal",   Hex("2F7F7A") },
        { "yellow", Hex("F2C744") },
        { "white",  Hex("F4F0E6") },
        { "cream",  Hex("F1E6C8") },
        { "wood",   Hex("8A5E3C") },
    };
    static readonly Color Trim = Hex("F5F1E8");

    sealed class Pick { public TownLifeScene.HouseInfo house; public int index; public Tone wall; public string roof, door; public List<int> near = new List<int>(), row = new List<int>(); }

    /// <summary>Builds this module into ctx: only fills ctx.materialOverrides for Acres house building renderers.</summary>
    public static void Build(TownLifeContext ctx)
    {
        if (!ctx.options.District(TownLifeScene.Acres)) { ctx.Log(Name, "Acres not selected: nothing painted"); return; }

        var picks = new List<Pick>();
        var houses = ctx.scene.houses;
        for (int i = 0; i < houses.Length; i++)
            if (houses[i].districtId == TownLifeScene.Acres) picks.Add(new Pick { house = houses[i], index = i });
        if (picks.Count == 0) { ctx.Log(Name, "no Acres houses"); return; }

        LinkNeighbours(ctx, picks);
        ChooseColours(ctx, picks);

        // ---- materials (canonical pack sources, loaded read-only)
        var wallSrc = Load("M_wall"); var roofSrc = Load("M_roof"); var doorSrc = Load("M_doors"); var trimSrc = Load("M_edges");
        if (wallSrc == null || roofSrc == null || doorSrc == null || trimSrc == null) { ctx.Error(Name, "pack house materials not found under " + PackMaterials); return; }
        var trimMat = TownLifeKit.Variant(TrimKey, trimSrc, Trim);

        int renderers = 0, slots = 0, unchanged = 0;
        foreach (var p in picks)
        {
            var wallMat = TownLifeKit.Variant(WallKey + p.wall.id, wallSrc, p.wall.color);
            var roofMat = TownLifeKit.Variant(RoofKey + p.roof, roofSrc, Roofs[p.roof]);
            var doorMat = TownLifeKit.Variant(DoorKey + p.door, doorSrc, Doors[p.door]);
            foreach (var r in p.house.buildingRenderers)
            {
                if (!(r is MeshRenderer) || !r.name.StartsWith("SM_house", StringComparison.Ordinal)) continue;
                var mats = r.sharedMaterials; bool changed = false, any = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) continue;
                    Material want = null;
                    switch (Role(mats[i].name))
                    {
                        case 'w': want = wallMat; break;
                        case 'r': want = roofMat; break;
                        case 'd': want = doorMat; break;
                        case 't': want = trimMat; break;
                    }
                    if (want == null) continue;
                    any = true; slots++;
                    if (mats[i] != want) { mats[i] = want; changed = true; }
                }
                if (!any) continue;
                if (changed) { ctx.materialOverrides[r] = mats; renderers++; } else unchanged++;
            }
        }

        // ---- report
        var use = new SortedDictionary<string, int>(StringComparer.Ordinal); var roofUse = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int clashes = 0, familyClashes = 0;
        foreach (var p in picks)
        {
            use[p.wall.id] = (use.TryGetValue(p.wall.id, out int a) ? a : 0) + 1;
            roofUse[p.roof] = (roofUse.TryGetValue(p.roof, out int b) ? b : 0) + 1;
            foreach (int n in p.near) if (n > picks.IndexOf(p)) { if (picks[n].wall == p.wall) clashes++; else if (picks[n].wall.family == p.wall.family) familyClashes++; }
        }
        var sb = new System.Text.StringBuilder();
        sb.Append(picks.Count + " Acres houses, " + renderers + " renderers repainted (" + unchanged + " already painted), " + slots + " slots; walls");
        foreach (var kv in use) sb.Append(" " + kv.Key + "=" + kv.Value);
        sb.Append("; roofs"); foreach (var kv in roofUse) sb.Append(" " + kv.Key + "=" + kv.Value);
        sb.Append("; same-wall neighbours " + clashes + ", same-family neighbours " + familyClashes);
        if (ctx.options.Flag("paint.list", false))
            foreach (var p in picks) sb.Append("\n    " + p.house.stopId + " " + p.house.model + ": " + p.wall.id + " / " + p.roof + " roof / " + p.door + " door");
        if (clashes > 0) ctx.Error(Name, clashes + " neighbouring house pairs share a wall colour");
        ctx.Log(Name, sb.ToString());
    }

    // ---------------------------------------------------------------------------------------------- neighbours

    /// <summary>near = must not share a wall colour: parcel neighbours (side AND rear), next house along the same
    /// frontage, houses facing each other across a street (any two Acres curb stops within 40 m, which with 11.6 m
    /// between opposite stops and 22-30 m lot pitch is exactly "next door, opposite, or diagonally opposite").
    /// row = the two along-street neighbours (used to vary roofs).</summary>
    static void LinkNeighbours(TownLifeContext ctx, List<Pick> picks)
    {
        var byScene = new Dictionary<int, int>();
        for (int i = 0; i < picks.Count; i++) byScene[picks[i].index] = i;
        void Link(int a, int b) { if (a == b) return; if (!picks[a].near.Contains(b)) picks[a].near.Add(b); if (!picks[b].near.Contains(a)) picks[b].near.Add(a); }

        var parcels = ctx.plan != null ? ctx.plan.parcels : null;
        for (int i = 0; i < picks.Count; i++)
        {
            var hi = picks[i].house.plan;
            if (parcels != null && picks[i].index < parcels.Length && parcels[picks[i].index].neighbours != null)
                foreach (int n in parcels[picks[i].index].neighbours) if (byScene.TryGetValue(n, out int j)) Link(i, j);
            for (int j = i + 1; j < picks.Count; j++)
            {
                var hj = picks[j].house.plan;
                float d = TownLifeGeo.Dist(hi.origin, hj.origin);
                bool sameRow = hi.roadId == hj.roadId && hi.side == hj.side;
                if (d < 40f || (sameRow && Mathf.Abs(hi.station - hj.station) < 50f)) Link(i, j);
                if (sameRow && Mathf.Abs(hi.station - hj.station) < 50f) { picks[i].row.Add(j); picks[j].row.Add(i); }
            }
        }
        foreach (var p in picks) { p.near.Sort(); p.row.Sort(); }
    }

    /// <summary>Greedy colouring in street order (road, side, station). Each house scores every wall colour: a hard
    /// penalty for a neighbour with the same colour, a softer one for the same colour family (cream next to butter reads
    /// as "the same house"), a mild one for colours already used a lot (keeps the district balanced), plus a per-house
    /// random preference from ctx.Rng("paint/" + stopId). Roofs and doors come from the wall's own harmonious list.</summary>
    static void ChooseColours(TownLifeContext ctx, List<Pick> picks)
    {
        var order = new List<int>(); for (int i = 0; i < picks.Count; i++) order.Add(i);
        order.Sort((a, b) =>
        {
            var x = picks[a].house.plan; var y = picks[b].house.plan;
            int c = string.CompareOrdinal(x.roadId, y.roadId); if (c != 0) return c;
            c = x.side.CompareTo(y.side); if (c != 0) return c;
            c = x.station.CompareTo(y.station); if (c != 0) return c;
            return string.CompareOrdinal(x.stopId, y.stopId);
        });
        var wallUse = new int[Walls.Length]; var roofUse = new Dictionary<string, int>(); var doorUse = new Dictionary<string, int>();
        foreach (var k in Roofs.Keys) roofUse[k] = 0; foreach (var k in Doors.Keys) doorUse[k] = 0;

        foreach (int i in order)
        {
            var p = picks[i]; var rng = ctx.Rng("paint/" + p.house.stopId);
            // walls
            int best = 0; double bestScore = double.MaxValue;
            for (int w = 0; w < Walls.Length; w++)
            {
                double score = rng.NextDouble() * 2.0 + wallUse[w] * 1.3;
                foreach (int n in p.near)
                {
                    var other = picks[n].wall; if (other == null) continue;
                    if (other == Walls[w]) score += 1000;
                    else if (other.family == Walls[w].family) score += 6;
                }
                if (score < bestScore) { bestScore = score; best = w; }
            }
            p.wall = Walls[best]; wallUse[best]++;
            // roof: from the wall's list (earlier = better match), not the same as the houses either side where possible
            string roof = p.wall.roofs[0]; bestScore = double.MaxValue;
            for (int k = 0; k < p.wall.roofs.Length; k++)
            {
                string id = p.wall.roofs[k];
                double score = rng.NextDouble() * 1.5 + k * 0.25 + roofUse[id] * 0.9 + (id == "terracotta" ? 0.8 : 0);
                foreach (int n in p.row) if (picks[n].roof == id) score += 5;
                foreach (int n in p.near) if (picks[n].roof == id) score += 1;
                if (score < bestScore) { bestScore = score; roof = id; }
            }
            p.roof = roof; roofUse[roof]++;
            // door / accent
            string door = p.wall.doors[0]; bestScore = double.MaxValue;
            for (int k = 0; k < p.wall.doors.Length; k++)
            {
                string id = p.wall.doors[k];
                double score = rng.NextDouble() * 2.0 + k * 0.3 + doorUse[id] * 0.5;
                foreach (int n in p.row) if (picks[n].door == id) score += 2;
                if (score < bestScore) { bestScore = score; door = id; }
            }
            p.door = door; doorUse[door]++;
        }
    }

    // ---------------------------------------------------------------------------------------------- helpers

    /// <summary>'w' wall, 'r' roof, 'd' door, 't' trim, 0 = leave alone. Recognises pack names and our own variants
    /// (kit names are "TL_" + sanitised key), so a painted house classifies exactly like a fresh one.</summary>
    static char Role(string materialName)
    {
        if (string.IsNullOrEmpty(materialName)) return (char)0;
        string n = materialName;
        if (n.StartsWith("M_windows", StringComparison.Ordinal) || n.StartsWith("M_emissive", StringComparison.Ordinal)) return (char)0;
        if (n.StartsWith("M_wall", StringComparison.Ordinal) || n.StartsWith(Variant(WallKey), StringComparison.Ordinal)) return 'w';
        if (n.StartsWith("M_roof", StringComparison.Ordinal) || n.StartsWith(Variant(RoofKey), StringComparison.Ordinal)) return 'r';
        if (n.StartsWith("M_doors", StringComparison.Ordinal) || n.StartsWith(Variant(DoorKey), StringComparison.Ordinal)) return 'd';
        if (n.StartsWith("M_edges", StringComparison.Ordinal) || n.StartsWith(Variant(TrimKey), StringComparison.Ordinal)) return 't';
        return (char)0;
    }
    static string Variant(string key) { return TownLifeKit.Prefix + TownLifeKit.Sanitize(key); }
    static Material Load(string name) { return AssetDatabase.LoadAssetAtPath<Material>(PackMaterials + name + ".mat"); }
    static Color Hex(string rgb) { return ColorUtility.TryParseHtmlString("#" + rgb, out var c) ? c : Color.magenta; }
}
