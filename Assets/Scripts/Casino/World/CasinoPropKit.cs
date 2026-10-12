using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Run-time meshes and materials of the presenters' props (Docs/Casino.md §11): chunky 1.6x cards with a real face and back,
/// casino chips with the amount on top, a few lathe shapes (glasses, rings). Everything is built once, cached and DontSave.
/// The card and chip materials are plain URP Lit with an atlas, NO GPU instancing and no property blocks (instanced copies
/// mixed their blocks into diagonal stripes in this room); a prop's look is its mesh's UVs, so a thousand chips share one material.
/// </summary>
public static class CasinoPropKit
{
    static Material cardMat, chipMat;
    static readonly Mesh[] cardMeshes = new Mesh[CasinoPaint.CardBlankCell + 1];
    static readonly Mesh[] chipMeshes = new Mesh[CasinoPaint.ChipStyles];
    static readonly Dictionary<int, Material> solids = new Dictionary<int, Material>();
    static readonly Dictionary<int, Material> glows = new Dictionary<int, Material>();
    static readonly Dictionary<int, Material> glasses = new Dictionary<int, Material>();
    static Shader lit;

    static Shader Lit => lit != null ? lit : lit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");

    static Material NewLit(string name)
    {
        var m = new Material(Lit) { name = name, hideFlags = HideFlags.DontSave, enableInstancing = false };
        return m;
    }

    static void SetColour(Material m, Color c)
    {
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
    }

    static void SetTexture(Material m, Texture t)
    {
        if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", t);
        if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", t);
    }

    /// <summary>No specular glint, no environment reflections: a flat card or chip under a lamp is a white glare otherwise.</summary>
    static void Matte(Material m)
    {
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.08f);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
        if (m.HasProperty("_SpecularHighlights")) m.SetFloat("_SpecularHighlights", 0f);
        if (m.HasProperty("_EnvironmentReflections")) m.SetFloat("_EnvironmentReflections", 0f);
        m.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
        m.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
    }

    public static Material CardMaterial
    {
        get
        {
            if (cardMat != null) return cardMat;
            cardMat = NewLit("CasinoCard");
            SetTexture(cardMat, CasinoPaint.CardAtlas);
            SetColour(cardMat, new Color(0.74f, 0.74f, 0.74f)); // the lamps over the tables are strong: full-white paper blooms out
            Matte(cardMat);
            return cardMat;
        }
    }

    public static Material ChipMaterial
    {
        get
        {
            if (chipMat != null) return chipMat;
            chipMat = NewLit("CasinoChip");
            SetTexture(chipMat, CasinoPaint.ChipAtlas);
            SetColour(chipMat, new Color(0.62f, 0.62f, 0.62f));
            Matte(chipMat);
            return chipMat;
        }
    }

    /// <summary>A plain opaque colour (cached by colour).</summary>
    public static Material Solid(Color c, float smoothness = 0.35f, float metallic = 0f)
    {
        int key = Pack(c) ^ (Mathf.RoundToInt(smoothness * 100f) << 24) ^ (Mathf.RoundToInt(metallic * 10f) << 20);
        if (solids.TryGetValue(key, out var m) && m != null) return m;
        m = NewLit("CasinoSolid");
        SetColour(m, c);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
        return solids[key] = m;
    }

    /// <summary>A lit colour that also glows (emission = colour x intensity). Cached.</summary>
    public static Material Glow(Color c, float intensity)
    {
        int key = Pack(c) ^ (Mathf.RoundToInt(intensity * 10f) << 24);
        if (glows.TryGetValue(key, out var m) && m != null) return m;
        m = NewLit("CasinoGlow");
        SetColour(m, c);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.5f);
        m.EnableKeyword("_EMISSION");
        if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", c * intensity);
        m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        return glows[key] = m;
    }

    /// <summary>A see-through tinted material (a glass). Cached.</summary>
    public static Material Glass(Color c)
    {
        int key = Pack(c);
        if (glasses.TryGetValue(key, out var m) && m != null) return m;
        m = NewLit("CasinoGlass");
        SetColour(m, c);
        if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);
        if (m.HasProperty("_Blend")) m.SetFloat("_Blend", 0f);
        if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0f);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.92f);
        m.SetOverrideTag("RenderType", "Transparent");
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.renderQueue = (int)RenderQueue.Transparent;
        return glasses[key] = m;
    }

    static int Pack(Color c) =>
        (Mathf.RoundToInt(Mathf.Clamp01(c.r) * 255f) << 16) | (Mathf.RoundToInt(Mathf.Clamp01(c.g) * 255f) << 8) | Mathf.RoundToInt(Mathf.Clamp01(c.b) * 255f) ^ (Mathf.RoundToInt(Mathf.Clamp01(c.a) * 15f) << 28);

    // ------------------------------------------------------------------ mesh building

    sealed class Builder
    {
        public readonly List<Vector3> v = new List<Vector3>(), n = new List<Vector3>();
        public readonly List<Vector2> uv = new List<Vector2>();
        public readonly List<int> tri = new List<int>();

        public int Add(Vector3 p, Vector3 normal, Vector2 uv0)
        {
            v.Add(p); n.Add(normal); uv.Add(uv0);
            return v.Count - 1;
        }

        /// <summary>A triangle that faces <paramref name="facing"/> (the winding is fixed up if it does not).</summary>
        public void Tri(int a, int b, int c, Vector3 facing)
        {
            Vector3 cross = Vector3.Cross(v[b] - v[a], v[c] - v[a]);
            if (Vector3.Dot(cross, facing) < 0f) { int t = b; b = c; c = t; }
            tri.Add(a); tri.Add(b); tri.Add(c);
        }

        public Mesh Build(string name)
        {
            var m = new Mesh { name = name, hideFlags = HideFlags.DontSave };
            m.SetVertices(v); m.SetNormals(n); m.SetUVs(0, uv); m.SetTriangles(tri, 0);
            m.RecalculateBounds();
            return m;
        }
    }

    static Vector2 InRect(Rect r, float x01, float y01) => new Vector2(r.x + r.width * x01, r.y + r.height * y01);

    /// <summary>
    /// A card, centred on the origin, lying face up (width along X, length along Z, <see cref="CasinoLayout.CardThickness"/> thick) with
    /// rounded corners. The top shows card <paramref name="cell"/> (0..51; <see cref="CasinoPaint.CardBackCell"/> = the back on both sides),
    /// the underside always the back. The printed top of a face is toward +Z.
    /// </summary>
    public static Mesh Card(int cell)
    {
        cell = Mathf.Clamp(cell, 0, CasinoPaint.CardBlankCell);
        if (cardMeshes[cell] != null) return cardMeshes[cell];
        var b = new Builder();
        float hw = CasinoLayout.CardWidth * 0.5f, hh = CasinoLayout.CardHeight * 0.5f, ht = CasinoLayout.CardThickness * 0.5f, r = 0.009f;
        const int arc = 3;
        var outline = new List<Vector2>();
        Vector2[] centres = { new Vector2(hw - r, hh - r), new Vector2(-hw + r, hh - r), new Vector2(-hw + r, -hh + r), new Vector2(hw - r, -hh + r) };
        for (int c = 0; c < 4; c++)
            for (int i = 0; i <= arc; i++)
            {
                float a = (c * 90f + i * 90f / arc) * Mathf.Deg2Rad;
                outline.Add(centres[c] + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r);
            }
        int m = outline.Count;
        Rect face = CasinoPaint.CardUv(cell), back = CasinoPaint.CardUv(CasinoPaint.CardBackCell), blank = CasinoPaint.CardUv(CasinoPaint.CardBlankCell);
        var topIdx = new int[m]; var botIdx = new int[m];
        int topC = b.Add(new Vector3(0, ht, 0), Vector3.up, InRect(face, 0.5f, 0.5f));
        int botC = b.Add(new Vector3(0, -ht, 0), Vector3.down, InRect(back, 0.5f, 0.5f));
        for (int i = 0; i < m; i++)
        {
            var p = outline[i];
            float fx = (p.x + hw) / (2f * hw), fz = (p.y + hh) / (2f * hh);
            topIdx[i] = b.Add(new Vector3(p.x, ht, p.y), Vector3.up, InRect(face, fx, fz));
            botIdx[i] = b.Add(new Vector3(p.x, -ht, p.y), Vector3.down, InRect(back, 1f - fx, fz));
        }
        for (int i = 0; i < m; i++)
        {
            int j = (i + 1) % m;
            b.Tri(topC, topIdx[i], topIdx[j], Vector3.up);
            b.Tri(botC, botIdx[i], botIdx[j], Vector3.down);
            // the edge: a flat strip of paper
            Vector2 d = outline[j] - outline[i];
            Vector3 nrm = new Vector3(d.y, 0f, -d.x).normalized;
            int e0 = b.Add(new Vector3(outline[i].x, ht, outline[i].y), nrm, InRect(blank, 0.5f, 0.5f));
            int e1 = b.Add(new Vector3(outline[j].x, ht, outline[j].y), nrm, InRect(blank, 0.5f, 0.5f));
            int e2 = b.Add(new Vector3(outline[j].x, -ht, outline[j].y), nrm, InRect(blank, 0.5f, 0.5f));
            int e3 = b.Add(new Vector3(outline[i].x, -ht, outline[i].y), nrm, InRect(blank, 0.5f, 0.5f));
            b.Tri(e0, e1, e2, nrm);
            b.Tri(e0, e2, e3, nrm);
        }
        // a card that shows its back on top: its top UVs are the back's
        if (cell == CasinoPaint.CardBackCell)
            for (int i = 0; i < topIdx.Length; i++)
            {
                var p = outline[i];
                b.uv[topIdx[i]] = InRect(back, (p.x + hw) / (2f * hw), (p.y + hh) / (2f * hh));
            }
        var mesh = b.Build("CasinoCard" + cell);
        return cardMeshes[cell] = mesh;
    }

    /// <summary>
    /// A casino chip standing on y = 0, its top at <see cref="CasinoLayout.ChipThickness"/>: a bevelled disc whose top carries the
    /// denomination and whose rim carries the six inserts (atlas style <paramref name="style"/>, see <see cref="CasinoPaint.BlackjackStyle"/>
    /// and <see cref="CasinoPaint.RouletteStyle"/>).
    /// </summary>
    public static Mesh Chip(int style)
    {
        style = Mathf.Clamp(style, 0, CasinoPaint.ChipStyles - 1);
        if (chipMeshes[style] != null) return chipMeshes[style];
        var b = new Builder();
        float R = CasinoLayout.ChipDiameter * 0.5f, T = CasinoLayout.ChipThickness, bev = 0.0028f, bevH = 0.0016f;
        const int seg = 24;
        Rect top = CasinoPaint.ChipTopUv(style), rim = CasinoPaint.ChipRimUv(style);
        Vector2 rimMid = InRect(rim, 0.5f, 0.5f);
        int topC = b.Add(new Vector3(0, T, 0), Vector3.up, InRect(top, 0.5f, 0.5f));
        int botC = b.Add(new Vector3(0, 0, 0), Vector3.down, rimMid);
        var innerTop = new int[seg + 1]; var rimTop = new int[seg + 1]; var rimBot = new int[seg + 1]; var botRing = new int[seg + 1];
        for (int i = 0; i <= seg; i++)
        {
            float a = i * 2f * Mathf.PI / seg;
            Vector3 dir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
            float u01 = i / (float)seg;
            Vector3 slope = (dir * bevH + Vector3.up * bev).normalized;
            float ri = R - bev;
            innerTop[i] = b.Add(dir * ri + Vector3.up * T, Vector3.up, InRect(top, 0.5f + dir.x * ri / (2f * R), 0.5f + dir.z * ri / (2f * R)));
            rimTop[i] = b.Add(dir * R + Vector3.up * (T - bevH), slope, new Vector2(Mathf.Lerp(rim.x, rim.xMax, u01), rim.yMax));
            rimBot[i] = b.Add(dir * R, dir, new Vector2(Mathf.Lerp(rim.x, rim.xMax, u01), rim.y));
            botRing[i] = b.Add(dir * (R - bev), Vector3.down, rimMid);
        }
        for (int i = 0; i < seg; i++)
        {
            int j = i + 1;
            b.Tri(topC, innerTop[i], innerTop[j], Vector3.up);
            // bevel (between the top disc and the rim)
            b.Tri(innerTop[i], rimTop[i], rimTop[j], Vector3.up + new Vector3(Mathf.Sin((i + 0.5f) * 2f * Mathf.PI / seg), 0f, Mathf.Cos((i + 0.5f) * 2f * Mathf.PI / seg)));
            b.Tri(innerTop[i], rimTop[j], innerTop[j], Vector3.up + new Vector3(Mathf.Sin((i + 0.5f) * 2f * Mathf.PI / seg), 0f, Mathf.Cos((i + 0.5f) * 2f * Mathf.PI / seg)));
            // the rim
            Vector3 mid = new Vector3(Mathf.Sin((i + 0.5f) * 2f * Mathf.PI / seg), 0f, Mathf.Cos((i + 0.5f) * 2f * Mathf.PI / seg));
            b.Tri(rimTop[i], rimBot[i], rimBot[j], mid);
            b.Tri(rimTop[i], rimBot[j], rimTop[j], mid);
            b.Tri(botC, botRing[i], botRing[j], Vector3.down);
        }
        return chipMeshes[style] = b.Build("CasinoChip" + style);
    }

    /// <summary>A solid of revolution about Y from a (radius, height) profile (bottom to top), smooth shaded, with a flat cap on top and bottom when the profile ends off the axis.</summary>
    public static Mesh Lathe(string name, Vector2[] profile, int seg = 20)
    {
        var b = new Builder();
        int rings = profile.Length;
        var idx = new int[rings, seg + 1];
        for (int r = 0; r < rings; r++)
        {
            Vector2 prev = profile[Mathf.Max(0, r - 1)], next = profile[Mathf.Min(rings - 1, r + 1)];
            Vector2 tangent = (next - prev).normalized;
            Vector2 nrm2 = new Vector2(tangent.y, -tangent.x); // outward for a profile that goes up
            for (int i = 0; i <= seg; i++)
            {
                float a = i * 2f * Mathf.PI / seg;
                Vector3 dir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
                idx[r, i] = b.Add(dir * profile[r].x + Vector3.up * profile[r].y, (dir * nrm2.x + Vector3.up * nrm2.y).normalized, new Vector2(i / (float)seg, r / (float)Mathf.Max(1, rings - 1)));
            }
        }
        for (int r = 0; r + 1 < rings; r++)
            for (int i = 0; i < seg; i++)
            {
                Vector3 mid = new Vector3(Mathf.Sin((i + 0.5f) * 2f * Mathf.PI / seg), 0f, Mathf.Cos((i + 0.5f) * 2f * Mathf.PI / seg));
                Vector2 d = profile[r + 1] - profile[r];
                Vector3 facing = mid * d.y - Vector3.up * d.x; // outward normal of the profile segment
                b.Tri(idx[r, i], idx[r + 1, i], idx[r + 1, i + 1], facing);
                b.Tri(idx[r, i], idx[r + 1, i + 1], idx[r, i + 1], facing);
            }
        return b.Build(name);
    }

    /// <summary>A flat ring (annulus) lying on y = 0, <paramref name="height"/> tall.</summary>
    public static Mesh Ring(string name, float inner, float outer, float height, int seg = 40)
    {
        return Lathe(name, new[] { new Vector2(inner, 0f), new Vector2(outer, 0f), new Vector2(outer, height), new Vector2(inner, height), new Vector2(inner, 0f) }, seg);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        // Everything here is a DontSave object: a play start without a domain reload could find destroyed ones, so drop the caches.
        cardMat = null; chipMat = null; lit = null;
        System.Array.Clear(cardMeshes, 0, cardMeshes.Length);
        System.Array.Clear(chipMeshes, 0, chipMeshes.Length);
        solids.Clear(); glows.Clear(); glasses.Clear();
    }
}
