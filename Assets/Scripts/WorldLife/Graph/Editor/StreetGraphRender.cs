using System;
using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using EdgeKind = StreetGraph.EdgeKind;
using EdgeFlags = StreetGraph.EdgeFlags;
using NodeKind = StreetGraph.NodeKind;

/// <summary>
/// WorldLife package A: edit-mode renders of the town with the street graph drawn on top (Docs/WorldLife.md §5.3, "a gizmo
/// screenshot"): a top-down orthographic map with a legend, and perspective shots (the exits). Renders only the given
/// scene (Camera.scene), with that scene's lighting and no fog; temporary label objects are removed afterwards.
/// </summary>
public static class StreetGraphRender
{
    public static readonly Color32 CRoad = new Color32(255, 196, 0, 255), CTurn = new Color32(255, 120, 20, 255),
        CPad = new Color32(255, 255, 90, 255), CGate = new Color32(255, 20, 20, 255), CWalk = new Color32(0, 200, 255, 255),
        CCross = new Color32(255, 255, 255, 255), CJay = new Color32(255, 0, 220, 255), CGarden = new Color32(40, 230, 60, 255),
        CDrive = new Color32(205, 140, 70, 255), CPark = new Color32(170, 255, 120, 255), CDoor = new Color32(230, 0, 0, 255),
        CSlot = new Color32(20, 20, 20, 255), CExit = new Color32(255, 30, 30, 255);

    public static Color32 EdgeColor(StreetGraph.Edge e)
    {
        if ((e.flags & EdgeFlags.GateLocked) != 0) return CGate;
        if ((e.flags & EdgeFlags.Pad) != 0) return CPad;
        switch (e.kind)
        {
            case EdgeKind.Road: return (e.flags & EdgeFlags.Turn) != 0 ? CTurn : CRoad;
            case EdgeKind.Walk: return CWalk;
            case EdgeKind.Crosswalk: return CCross;
            case EdgeKind.Jaywalk: return CJay;
            case EdgeKind.GardenPath: return CGarden;
            case EdgeKind.Driveway: return CDrive;
            default: return CPark;
        }
    }

    /// <summary>Top-down map of <paramref name="area"/> (x0, z0, width, depth) with the graph and labels; writes a PNG.</summary>
    public static string RenderTop(Scene scene, StreetGraph g, Rect area, int widthPx, string outPath, bool legend,
        IList<(Vector3 pos, string text, float size)> labels = null, float lineScale = 1f)
    {
        int heightPx = Mathf.RoundToInt(widthPx * area.height / area.width);
        var temp = new List<GameObject>();
        if (labels != null) foreach (var l in labels) temp.Add(MakeLabel(scene, l.pos, l.text, l.size, true));
        float mpp = area.width / widthPx;           // metres per pixel
        var legendItems = new (Color32 c, string text)[]
        {
            (CRoad, "road lane (one-way, right-hand)"), (CTurn, "turn / loop lane"), (CPad, "depot pad (yield zone)"),
            (CGate, "district gate (locked edges)"), (CWalk, "sidewalk"), (CCross, "crosswalk"), (CJay, "jaywalk"),
            (CGarden, "garden path"), (CDrive, "driveway"), (CPark, "park path"), (CDoor, "front door"), (CExit, "town exit"),
        };
        Vector3 legendAt = new Vector3(area.xMax - area.width * 0.21f, 0.5f, area.yMax - area.height * 0.33f);
        float legendRow = 26f * mpp;
        if (legend)
            for (int i = 0; i < legendItems.Length; i++)
                temp.Add(MakeLabel(scene, legendAt + new Vector3(40f * mpp, 0f, -legendRow * i), legendItems[i].text, 14f * mpp, false));
        try
        {
            var cam = MakeCamera(scene, out var camGo);
            cam.orthographic = true;
            cam.orthographicSize = area.height * 0.5f;
            cam.transform.SetPositionAndRotation(new Vector3(area.center.x, 400f, area.center.y), Quaternion.Euler(90f, 0f, 0f));
            cam.nearClipPlane = 1f; cam.farClipPlane = 1000f;
            var tex = Shoot(scene, cam, widthPx, heightPx);
            UnityEngine.Object.DestroyImmediate(camGo);
            var px = tex.GetPixels32();
            Func<Vector3, Vector2> proj = p => new Vector2((p.x - area.xMin) / mpp, (p.z - area.yMin) / mpp);
            DrawGraph(g, px, widthPx, heightPx, proj, Mathf.Max(1f, 1.1f * lineScale), true);
            if (legend)
                for (int i = 0; i < legendItems.Length; i++)
                {
                    Vector2 a = proj(legendAt + new Vector3(0f, 0f, -legendRow * i)), b = a + new Vector2(30f, 0f);
                    Line(px, widthPx, heightPx, a, b, 3f, legendItems[i].c);
                }
            tex.SetPixels32(px); tex.Apply();
            Save(tex, outPath);
            UnityEngine.Object.DestroyImmediate(tex);
            return $"{outPath} ({widthPx}x{heightPx}, {mpp:F2} m/px)";
        }
        finally { foreach (var t in temp) if (t != null) UnityEngine.Object.DestroyImmediate(t); }
    }

    /// <summary>A perspective shot (graph drawn on top when <paramref name="g"/> is given); writes a PNG.</summary>
    public static string RenderPerspective(Scene scene, StreetGraph g, Vector3 camPos, Vector3 lookAt, float fov, int w, int h, string outPath)
    {
        var cam = MakeCamera(scene, out var camGo);
        cam.fieldOfView = fov;
        cam.transform.position = camPos;
        cam.transform.LookAt(lookAt);
        cam.nearClipPlane = 0.3f; cam.farClipPlane = 1500f;
        var tex = Shoot(scene, cam, w, h);
        if (g != null)
        {
            var px = tex.GetPixels32();
            var c = cam;
            c.aspect = w / (float)h;
            DrawGraph(g, px, w, h, p =>
            {
                var vp = c.WorldToViewportPoint(p + Vector3.up * 0.05f);     // viewport: independent of any screen size
                return vp.z <= 0.5f ? new Vector2(float.NaN, float.NaN) : new Vector2(vp.x * w, vp.y * h);
            }, 2f, false);
            tex.SetPixels32(px); tex.Apply();
        }
        UnityEngine.Object.DestroyImmediate(camGo);
        Save(tex, outPath);
        UnityEngine.Object.DestroyImmediate(tex);
        return outPath;
    }

    static Camera MakeCamera(Scene scene, out GameObject go)
    {
        go = new GameObject("TmpGraphCam");
        SceneManager.MoveGameObjectToScene(go, scene);
        var cam = go.AddComponent<Camera>();
        cam.scene = scene;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.55f, 0.7f, 0.85f);
        return cam;
    }

    static Texture2D Shoot(Scene scene, Camera cam, int w, int h)
    {
        var prevActive = SceneManager.GetActiveScene();
        bool prevFog = RenderSettings.fog;
        var rt = new RenderTexture(w, h, 24);
        try
        {
            if (scene != prevActive) SceneManager.SetActiveScene(scene);
            RenderSettings.fog = false;
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            return tex;
        }
        finally
        {
            RenderTexture.active = null;
            cam.targetTexture = null;
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
            RenderSettings.fog = prevFog;
            if (prevActive.IsValid() && prevActive.isLoaded && prevActive != scene) SceneManager.SetActiveScene(prevActive);
        }
    }

    static GameObject MakeLabel(Scene scene, Vector3 pos, string text, float size, bool centered)
    {
        var go = new GameObject("TmpGraphLabel");
        SceneManager.MoveGameObjectToScene(go, scene);
        go.transform.SetPositionAndRotation(new Vector3(pos.x, 60f, pos.z), Quaternion.Euler(90f, 0f, 0f));   // above houses and trees
        var t = go.AddComponent<TextMeshPro>();
        t.text = text;
        t.fontSize = size * 10f;
        t.color = Color.white;
        t.fontStyle = FontStyles.Bold;
        t.alignment = centered ? TextAlignmentOptions.Center : TextAlignmentOptions.Left;
        t.textWrappingMode = TextWrappingModes.NoWrap;
        t.rectTransform.pivot = centered ? new Vector2(0.5f, 0.5f) : new Vector2(0f, 0.5f);
        t.rectTransform.sizeDelta = new Vector2(400f, size * 1.5f);
        t.ForceMeshUpdate();
        return go;
    }

    static void DrawGraph(StreetGraph g, Color32[] px, int w, int h, Func<Vector3, Vector2> proj, float thick, bool dots)
    {
        // Walk-type first, roads over them, gates on top.
        for (int pass = 0; pass < 3; pass++)
            for (int e = 0; e < g.EdgeCount; e++)
            {
                var ed = g.edges[e];
                bool gate = (ed.flags & EdgeFlags.GateLocked) != 0;
                bool road = ed.kind == EdgeKind.Road || ed.kind == EdgeKind.Driveway;
                if ((pass == 0 && (road || gate)) || (pass == 1 && (!road || gate)) || (pass == 2 && !gate)) continue;
                Vector2 a = proj(g.nodes[ed.a].pos), b = proj(g.nodes[ed.b].pos);
                if (float.IsNaN(a.x) || float.IsNaN(b.x)) continue;
                var c = EdgeColor(ed);
                Line(px, w, h, a, b, gate ? thick * 2f : thick, c);
                if (ed.OneWay && ed.kind == EdgeKind.Road && (b - a).magnitude > 10f)
                {
                    Vector2 m = Vector2.Lerp(a, b, 0.6f), d = (b - a).normalized, r = new Vector2(d.y, -d.x);
                    Line(px, w, h, m, m - d * 4f + r * 2.5f, thick, c);
                    Line(px, w, h, m, m - d * 4f - r * 2.5f, thick, c);
                }
            }
        if (!dots) return;
        for (int i = 0; i < g.NodeCount; i++)
        {
            var k = g.nodes[i].kind;
            if (k == NodeKind.Door) Disc(px, w, h, proj(g.nodes[i].pos), 2.2f * thick, CDoor);
            else if (k == NodeKind.Slot) Disc(px, w, h, proj(g.nodes[i].pos), 1.6f * thick, CSlot);
        }
        foreach (var x in g.exits)
        {
            var c = proj(g.nodes[x.node].pos);
            for (float r = 9f; r < 12f; r += 0.5f) Ring(px, w, h, c, r * thick, CExit);
        }
    }

    public static void Line(Color32[] px, int w, int h, Vector2 a, Vector2 b, float thick, Color32 c)
    {
        float len = Vector2.Distance(a, b);
        int steps = Mathf.Max(1, Mathf.CeilToInt(len * 2f));
        for (int s = 0; s <= steps; s++) Disc(px, w, h, Vector2.Lerp(a, b, s / (float)steps), thick * 0.5f, c);
    }

    static void Disc(Color32[] px, int w, int h, Vector2 p, float r, Color32 c)
    {
        int x0 = Mathf.FloorToInt(p.x - r), x1 = Mathf.CeilToInt(p.x + r), y0 = Mathf.FloorToInt(p.y - r), y1 = Mathf.CeilToInt(p.y + r);
        float r2 = Mathf.Max(0.25f, r * r);
        for (int y = Mathf.Max(0, y0); y <= Mathf.Min(h - 1, y1); y++)
            for (int x = Mathf.Max(0, x0); x <= Mathf.Min(w - 1, x1); x++)
            {
                float dx = x + 0.5f - p.x, dy = y + 0.5f - p.y;
                if (dx * dx + dy * dy <= r2 + 0.25f) px[y * w + x] = c;
            }
    }

    static void Ring(Color32[] px, int w, int h, Vector2 c, float r, Color32 col)
    {
        int n = Mathf.Max(24, Mathf.CeilToInt(r * 6.3f));
        for (int i = 0; i < n; i++)
        {
            float a = i * Mathf.PI * 2f / n;
            int x = Mathf.RoundToInt(c.x + Mathf.Cos(a) * r), y = Mathf.RoundToInt(c.y + Mathf.Sin(a) * r);
            if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = col;
        }
    }

    static void Save(Texture2D tex, string path)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
        System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
    }
}
