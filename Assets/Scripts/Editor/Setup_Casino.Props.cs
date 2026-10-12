// Setup_Casino, part 4: the furniture. Every prop is a recipe that fills a mesh builder (front = local +Z, origin on the
// floor); props placed more than once share one mesh.
using System;
using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEngine;

public static partial class Setup_Casino
{
    // ------------------------------------------------------------------------------------------------ shared props

    sealed class PropMesh { public Mesh mesh; public Material[] mats; }
    static readonly Dictionary<string, PropMesh> props = new Dictionary<string, PropMesh>();

    /// <summary>Places a shared prop: built once from its recipe, instanced as one GameObject per placement.</summary>
    static GameObject Place(Transform parent, string id, Action<MB> recipe, Vector3 pos, float yaw = 0f, bool isStatic = true, bool shadows = false)
    {
        if (!props.TryGetValue(id, out var p))
        {
            var d = new MB(); recipe(d);
            p = new PropMesh { mesh = StoreMesh("prop_" + id, d), mats = new Material[d.mats.Count] };
            for (int i = 0; i < p.mats.Length; i++) p.mats[i] = Mat(d.mats[i]);
            props[id] = p;
        }
        var go = new GameObject(id); go.transform.SetParent(parent, false);
        go.transform.localPosition = pos; go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
        go.AddComponent<MeshFilter>().sharedMesh = p.mesh;
        var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterials = p.mats;
        mr.shadowCastingMode = shadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off; mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        if (isStatic) GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);
        return go;
    }

    static Vector2 V(float r, float y) => new Vector2(r, y);

    /// <summary>TextMeshPro label. ASCII only (the static font atlas has it; anything else would dirty the fallback atlas).
    /// With identity rotation it reads from local -Z; <paramref name="flat"/> lays it on a table, top toward yaw.</summary>
    static TextMeshPro Label(Transform parent, string name, string text, Vector3 localPos, Vector3 euler, Vector2 size, Color color, FontStyles style = FontStyles.Bold, float spacing = 0f)
    {
        var go = new GameObject(name); go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos; go.transform.localRotation = Quaternion.Euler(euler);
        var t = go.AddComponent<TextMeshPro>();
        t.text = text; t.color = color; t.fontStyle = style; t.alignment = TextAlignmentOptions.Center;
        t.textWrappingMode = TextWrappingModes.NoWrap; t.enableAutoSizing = true; t.fontSizeMin = 0.1f; t.fontSizeMax = 72f;
        t.characterSpacing = spacing;
        t.rectTransform.sizeDelta = size; t.ForceMeshUpdate();
        var r = t.GetComponent<MeshRenderer>();
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
        return t;
    }

    static Light AddLight(Transform parent, string name, Vector3 pos, Color color, float intensity, float range, bool spot = false, float angle = 90f, bool shadows = false)
    {
        var go = new GameObject(name); go.transform.SetParent(parent, false); go.transform.localPosition = pos;
        var l = go.AddComponent<Light>();
        l.type = spot ? LightType.Spot : LightType.Point; l.color = color; l.intensity = intensity; l.range = range;
        if (spot) { l.spotAngle = angle; l.innerSpotAngle = angle * 0.55f; go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f); }
        l.shadows = shadows ? LightShadows.Soft : LightShadows.None;
        l.shadowStrength = 0.75f; l.shadowBias = 0.03f; l.shadowNormalBias = 0.3f;
        l.renderMode = LightRenderMode.Auto;
        return l;
    }

    // ------------------------------------------------------------------------------------------------ neon lettering

    static List<Vector2> ArcPts(float cx, float cy, float rx, float ry, float a0, float a1, int n = 10)
    {
        var l = new List<Vector2>();
        for (int i = 0; i <= n; i++) { float a = Mathf.Lerp(a0, a1, i / (float)n) * Mathf.Deg2Rad; l.Add(new Vector2(cx + Mathf.Cos(a) * rx, cy + Mathf.Sin(a) * ry)); }
        return l;
    }
    static List<Vector2> Pts(params float[] xy) { var l = new List<Vector2>(); for (int i = 0; i < xy.Length; i += 2) l.Add(new Vector2(xy[i], xy[i + 1])); return l; }
    static List<Vector2> Join(params List<Vector2>[] parts) { var l = new List<Vector2>(); foreach (var p in parts) l.AddRange(p); return l; }

    /// <summary>A single-stroke alphabet for bent glass tube: strokes in a box 0.6 wide and 1 tall (I is narrower).</summary>
    static List<List<Vector2>> Glyph(char ch, out float width)
    {
        width = 0.6f;
        var s = new List<List<Vector2>>();
        switch (char.ToUpperInvariant(ch))
        {
            case 'A': s.Add(Pts(0, 0, .3f, 1, .6f, 0)); s.Add(Pts(.13f, .36f, .47f, .36f)); break;
            case 'B': s.Add(Join(Pts(0, 0, 0, 1, .3f, 1), ArcPts(.3f, .76f, .26f, .24f, 90, -90, 8), Pts(0, .52f))); s.Add(Join(Pts(.3f, .52f), ArcPts(.32f, .26f, .28f, .26f, 90, -90, 8), Pts(0, 0))); break;
            case 'C': s.Add(ArcPts(.32f, .5f, .3f, .5f, 48, 312, 16)); break;
            case 'E': s.Add(Pts(.56f, 1, 0, 1, 0, 0, .56f, 0)); s.Add(Pts(0, .52f, .42f, .52f)); break;
            case 'H': s.Add(Pts(0, 0, 0, 1)); s.Add(Pts(.6f, 0, .6f, 1)); s.Add(Pts(0, .5f, .6f, .5f)); break;
            case 'I': width = 0.16f; s.Add(Pts(.08f, 0, .08f, 1)); break;
            case 'K': s.Add(Pts(0, 0, 0, 1)); s.Add(Pts(.56f, 1, .02f, .44f)); s.Add(Pts(.2f, .6f, .6f, 0)); break;
            case 'L': width = 0.52f; s.Add(Pts(0, 1, 0, 0, .52f, 0)); break;
            case 'N': s.Add(Pts(0, 0, 0, 1, .6f, 0, .6f, 1)); break;
            case 'O': { var o = ArcPts(.3f, .5f, .3f, .5f, 90, 450, 20); s.Add(o); } break;
            case 'P': s.Add(Join(Pts(0, 0, 0, 1, .3f, 1), ArcPts(.3f, .74f, .28f, .26f, 90, -90, 8), Pts(0, .48f))); break;
            case 'R': s.Add(Join(Pts(0, 0, 0, 1, .3f, 1), ArcPts(.3f, .74f, .28f, .26f, 90, -90, 8), Pts(0, .48f))); s.Add(Pts(.26f, .48f, .6f, 0)); break;
            case 'S': s.Add(Join(ArcPts(.3f, .75f, .28f, .25f, 30, 270, 10), ArcPts(.3f, .25f, .28f, .25f, 90, -150, 10))); break;
            case 'T': s.Add(Pts(0, 1, .6f, 1)); s.Add(Pts(.3f, 1, .3f, 0)); break;
            case 'U': s.Add(Join(Pts(0, 1), ArcPts(.3f, .3f, .3f, .3f, 180, 360, 10), Pts(.6f, 1))); break;
            case 'X': s.Add(Pts(0, 0, .6f, 1)); s.Add(Pts(0, 1, .6f, 0)); break;
            case 'Y': s.Add(Pts(0, 1, .3f, .5f, .6f, 1)); s.Add(Pts(.3f, .5f, .3f, 0)); break;
            case ' ': width = 0.34f; break;
            default: width = 0.3f; break;
        }
        return s;
    }

    static float NeonWidth(string text, float height)
    {
        float w = 0f; foreach (char ch in text) { Glyph(ch, out float gw); w += (gw + 0.24f) * height; }
        return w - 0.24f * height;
    }

    /// <summary>Bent-tube lettering centred on <paramref name="centre"/>, in the plane spanned by right / up.</summary>
    static void NeonText(MB d, string mat, string text, Vector3 centre, Vector3 right, Vector3 up, float height, float tube)
    {
        float total = NeonWidth(text, height), x = -total * 0.5f;
        foreach (char ch in text)
        {
            var strokes = Glyph(ch, out float gw);
            foreach (var st in strokes)
            {
                var path = new List<Vector3>();
                foreach (var p in st) path.Add(centre + right * (x + p.x * height) + up * ((p.y - 0.5f) * height));
                bool closed = path.Count > 3 && (path[0] - path[path.Count - 1]).sqrMagnitude < 1e-6f;
                if (closed) path.RemoveAt(path.Count - 1);
                d.Tube(mat, path, tube, 6, closed);
            }
            x += (gw + 0.24f) * height;
        }
    }

    // ------------------------------------------------------------------------------------------------ small props

    static void StoolRecipe(MB d)
    {
        d.Lathe("brass", Vector3.zero, new[] { V(0, 0), V(.21f, 0), V(.21f, .02f), V(.09f, .05f), V(.035f, .07f), V(.035f, .07f), V(.03f, .70f), V(.03f, .70f), V(.13f, .72f), V(.13f, .74f), V(0, .74f) }, 14);
        d.Torus("brass", new Vector3(0, .3f, 0), .15f, .012f, 16, 6);
        for (int i = 0; i < 3; i++) { float a = i * 120f * Mathf.Deg2Rad; d.Tube("brass", new[] { new Vector3(.03f * Mathf.Sin(a), .3f, .03f * Mathf.Cos(a)), new Vector3(.15f * Mathf.Sin(a), .3f, .15f * Mathf.Cos(a)) }, .008f, 5); }
        d.Lathe("leather_seat", new Vector3(0, .73f, 0), new[] { V(0, 0), V(.17f, 0), V(.19f, .02f), V(.19f, .05f), V(.16f, .075f), V(0, .085f) }, 16);
        d.Torus("brass", new Vector3(0, .745f, 0), .185f, .008f, 18, 5);
    }
    const float StoolSeat = 0.815f;

    static void ChairRecipe(MB d)
    {
        d.grainAlong = false;
        d.Lathe("wood_mid", new Vector3(0, .43f, 0), new[] { V(0, 0), V(.2f, 0), V(.21f, .015f), V(.2f, .03f), V(0, .035f) }, 14);
        foreach (var p in new[] { new Vector2(-.14f, .14f), new Vector2(.14f, .14f), new Vector2(-.15f, -.15f), new Vector2(.15f, -.15f) })
            d.Tube("wood_mid", new[] { new Vector3(p.x * 1.15f, 0, p.y * 1.15f), new Vector3(p.x, .43f, p.y) }, .016f, 6);
        d.Torus("wood_mid", new Vector3(0, .2f, 0), .17f, .01f, 14, 5);
        // bentwood back: two posts rising from the rear legs into a hoop
        var hoop = new List<Vector3>();
        for (int i = 0; i <= 12; i++) { float a = Mathf.Lerp(0f, 180f, i / 12f) * Mathf.Deg2Rad; hoop.Add(new Vector3(Mathf.Cos(a) * .16f, .70f + Mathf.Sin(a) * .2f, -.17f - Mathf.Sin(a) * .03f)); }
        hoop.Insert(0, new Vector3(.15f, .45f, -.15f)); hoop.Add(new Vector3(-.15f, .45f, -.15f));
        d.Tube("wood_mid", hoop, .014f, 6);
        d.Tube("wood_mid", new[] { new Vector3(-.06f, .46f, -.17f), new Vector3(-.06f, .88f, -.2f) }, .008f, 5);
        d.Tube("wood_mid", new[] { new Vector3(.06f, .46f, -.17f), new Vector3(.06f, .88f, -.2f) }, .008f, 5);
    }
    const float ChairSeat = 0.465f;

    static void RoundTableRecipe(MB d)
    {
        d.Lathe("black_iron", Vector3.zero, new[] { V(0, 0), V(.24f, 0), V(.24f, .02f), V(.06f, .06f), V(.04f, .1f), V(.035f, .68f), V(.09f, .71f), V(0, .71f) }, 14);
        d.Lathe("wood_red", new Vector3(0, .71f, 0), new[] { V(0, 0), V(.40f, 0), V(.42f, .012f), V(.42f, .03f), V(.41f, .04f), V(0, .04f) }, 24);
        d.Torus("brass", new Vector3(0, .73f, 0), .42f, .007f, 28, 5);
    }

    static void AshtrayRecipe(MB d)
    {
        d.Lathe("glass_smoke", Vector3.zero, new[] { V(0, 0), V(.055f, 0), V(.065f, .012f), V(.062f, .028f), V(.05f, .028f), V(.045f, .008f), V(0, .008f) }, 12);
        d.Tube("paper", new[] { new Vector3(-.02f, .03f, .0f), new Vector3(.06f, .034f, .02f) }, .004f, 5);
        d.Tube("ember", new[] { new Vector3(-.026f, .0296f, -.0015f), new Vector3(-.02f, .03f, .0f) }, .0042f, 5);
    }

    static void TumblerRecipe(MB d)
    {
        d.Lathe("glass_clear", Vector3.zero, new[] { V(0, 0), V(.032f, 0), V(.038f, .085f), V(.035f, .085f), V(.03f, .008f), V(0, .008f) }, 10);
        d.Lathe("liquid_amber", new Vector3(0, .009f, 0), new[] { V(0, 0), V(.029f, 0), V(.031f, .03f), V(0, .03f) }, 10);
    }

    /// <summary>Bottle silhouettes (radius, height): whisky, wine, squat liqueur, tall slim, flask-shouldered gin, jug.</summary>
    static Vector2[] BottleProfile(int kind, float s)
    {
        Vector2[] p;
        switch (kind % 6)
        {
            case 0: p = new[] { V(0, 0), V(.036f, 0), V(.038f, .01f), V(.038f, .16f), V(.03f, .19f), V(.013f, .215f), V(.012f, .27f), V(.015f, .272f), V(.015f, .285f), V(0, .285f) }; break;
            case 1: p = new[] { V(0, 0), V(.034f, 0), V(.036f, .012f), V(.036f, .14f), V(.022f, .2f), V(.012f, .225f), V(.011f, .30f), V(.014f, .302f), V(.014f, .312f), V(0, .312f) }; break;
            case 2: p = new[] { V(0, 0), V(.045f, 0), V(.05f, .02f), V(.05f, .09f), V(.04f, .125f), V(.016f, .145f), V(.014f, .19f), V(.019f, .192f), V(.019f, .205f), V(0, .205f) }; break;
            case 3: p = new[] { V(0, 0), V(.026f, 0), V(.028f, .01f), V(.028f, .2f), V(.014f, .25f), V(.011f, .32f), V(.014f, .322f), V(.014f, .335f), V(0, .335f) }; break;
            case 4: p = new[] { V(0, 0), V(.04f, 0), V(.042f, .01f), V(.042f, .13f), V(.042f, .13f), V(.02f, .15f), V(.014f, .16f), V(.013f, .23f), V(.017f, .232f), V(.017f, .245f), V(0, .245f) }; break;
            default: p = new[] { V(0, 0), V(.03f, 0), V(.052f, .05f), V(.05f, .1f), V(.028f, .15f), V(.013f, .17f), V(.012f, .23f), V(.016f, .232f), V(.016f, .244f), V(0, .244f) }; break;
        }
        var o = new Vector2[p.Length]; for (int i = 0; i < p.Length; i++) o[i] = p[i] * s; return o;
    }
    static readonly string[] BottleTints = { "bottle_amber", "bottle_green", "bottle_clear", "bottle_brown", "bottle_blue", "bottle_red" };

    static void Bottle(MB d, Vector3 pos, int kind, int tint, float scale = 1f)
    {
        var prof = BottleProfile(kind, scale);
        d.Lathe(BottleTints[tint % BottleTints.Length], pos, prof, 8);
        float top = prof[prof.Length - 1].y;
        // a paper label round the belly, a cap
        float r = prof[2].x + 0.0015f * scale;
        d.Lathe(tint % 3 == 0 ? "label_cream" : tint % 3 == 1 ? "label_red" : "label_gold", pos + Vector3.up * (0.05f * scale), new[] { V(r, 0), V(r, 0.06f * scale) }, 8);
        d.Lathe(tint % 2 == 0 ? "brass" : "black_iron", pos + Vector3.up * (top - 0.012f * scale), new[] { V(prof[prof.Length - 2].x + .001f, 0), V(prof[prof.Length - 2].x + .001f, 0.016f * scale), V(0, 0.016f * scale) }, 8);
    }

    /// <summary>A framed picture on a wall: local +Z is out of the wall, origin at the picture's centre.</summary>
    static void Frame(MB d, string picture, float w, float h, string frameMat = "gilt", float border = 0.06f)
    {
        float t = border;
        d.Box(frameMat, new Vector3(0, h * .5f + t * .5f, .02f), new Vector3(w + t * 2, t, .045f));
        d.Box(frameMat, new Vector3(0, -h * .5f - t * .5f, .02f), new Vector3(w + t * 2, t, .045f));
        d.Box(frameMat, new Vector3(-w * .5f - t * .5f, 0, .02f), new Vector3(t, h, .045f));
        d.Box(frameMat, new Vector3(w * .5f + t * .5f, 0, .02f), new Vector3(t, h, .045f));
        d.Box("frame_inner", new Vector3(0, 0, .006f), new Vector3(w + .01f, h + .01f, .012f), default, MB.Face.Sides);
        d.Panel(picture, new Vector3(0, 0, .016f), Vector3.forward, w, h);
    }

    static void Sconce(MB d)
    {
        d.Lathe("brass", new Vector3(0, 0, .0f), new[] { V(0, 0), V(.06f, 0), V(.065f, .01f), V(.03f, .02f), V(0, .02f) }, 10, new Vector3(90, 0, 0));
        d.Tube("brass", new[] { new Vector3(0, 0, .01f), new Vector3(0, -.02f, .09f), new Vector3(0, .02f, .14f), new Vector3(0, .09f, .14f) }, .009f, 6);
        d.Lathe("brass", new Vector3(0, .09f, .14f), new[] { V(0, 0), V(.035f, 0), V(.04f, .01f), V(0, .012f) }, 10);
        d.Lathe("lamp_amber", new Vector3(0, .10f, .14f), new[] { V(.03f, 0), V(.075f, .05f), V(.085f, .11f), V(.07f, .17f), V(.05f, .19f) }, 12);
        d.Sphere("bulb", new Vector3(0, .17f, .14f), Vector3.one * .03f, 8, 5);
    }

    /// <summary>A hanging lamp: canopy on the ceiling at y = 0, shade bottom at -drop.</summary>
    static void Pendant(MB d, float drop, string glass)
    {
        d.Lathe("brass", new Vector3(0, -.05f, 0), new[] { V(0, 0), V(.03f, 0), V(.07f, .04f), V(.07f, .05f), V(0, .05f) }, 12);
        d.Tube("black_iron", new[] { new Vector3(0, -.05f, 0), new Vector3(0, -drop + .34f, 0) }, .006f, 5);
        d.Lathe("brass", new Vector3(0, -drop + .26f, 0), new[] { V(0, .08f), V(.02f, .08f), V(.035f, .04f), V(.06f, .0f), V(.06f, .0f), V(0, 0) }, 12);
        // the glass: a ribbed bell, open underneath, lit from inside
        d.Lathe(glass, new Vector3(0, -drop, 0), new[] { V(.20f, 0), V(.21f, .02f), V(.19f, .09f), V(.14f, .17f), V(.08f, .23f), V(.055f, .265f) }, 18);
        d.Lathe(glass, new Vector3(0, -drop, 0), new[] { V(.05f, .262f), V(.075f, .228f), V(.135f, .168f), V(.185f, .09f), V(.205f, .02f), V(.196f, .002f) }, 18);
        d.Torus("brass", new Vector3(0, -drop + .003f, 0), .204f, .007f, 20, 5);
        d.Sphere("bulb", new Vector3(0, -drop + .12f, 0), new Vector3(.04f, .055f, .04f), 8, 5);
    }

    static void CoatStand(MB d)
    {
        d.Lathe("wood_dark", Vector3.zero, new[] { V(0, 0), V(.22f, 0), V(.22f, .02f), V(.08f, .06f), V(.035f, .12f), V(.028f, .5f), V(.04f, .55f), V(.028f, .6f), V(.025f, 1.72f), V(.05f, 1.76f), V(.03f, 1.82f), V(0, 1.84f) }, 12);
        for (int i = 0; i < 6; i++)
        {
            float a = i * 60f * Mathf.Deg2Rad; Vector3 dir = new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a));
            d.Tube("brass", new[] { dir * .025f + Vector3.up * 1.6f, dir * .14f + Vector3.up * 1.63f, dir * .17f + Vector3.up * 1.7f }, .008f, 5);
        }
        // a forgotten coat and a hat
        d.Sphere("coat", new Vector3(.16f, 1.15f, .02f), new Vector3(.13f, .5f, .2f), 10, 6);
        d.Lathe("hat", new Vector3(-.16f, 1.72f, .02f), new[] { V(.16f, 0), V(.155f, .012f), V(.1f, .02f), V(.098f, .1f), V(.07f, .135f), V(0, .14f) }, 14, new Vector3(0, 0, -18f));
    }

    static void Clock(MB d)
    {
        d.Lathe("wood_red", Vector3.zero, new[] { V(.2f, 0), V(.26f, 0), V(.27f, .02f), V(.25f, .05f), V(.215f, .055f), V(.2f, .03f) }, 28, new Vector3(90, 0, 0));
        d.Push(new Vector3(0, 0, .03f), new Vector3(90, 0, 0)); d.Disc("clock", Vector3.zero, .205f, 28); d.Pop();
        d.Box("ink", new Vector3(.035f, .05f, .036f), new Vector3(.014f, .13f, .004f), new Vector3(0, 0, -35f));
        d.Box("ink", new Vector3(-.06f, .03f, .038f), new Vector3(.01f, .17f, .004f), new Vector3(0, 0, 62f));
        d.Lathe("brass", new Vector3(0, 0, .034f), new[] { V(0, 0), V(.014f, 0), V(.01f, .008f), V(0, .01f) }, 8, new Vector3(90, 0, 0));
    }
}
