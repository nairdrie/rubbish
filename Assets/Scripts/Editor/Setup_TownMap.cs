using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Bakes the town map (Docs/UIPass.md §7.2). Opens Neighborhood.unity additively (read-only for the scan), reads every road,
/// sidewalk, curb, pad, lot, house, park path, pool, court, wall, gate, stop, exit and street sign, and writes:
/// <list type="bullet">
/// <item><c>Assets/Resources/TownMap.asset</c> (<see cref="TownMapData"/>: road segments with street names, areas, districts,
/// labels, POIs, houses, sourceHash);</item>
/// <item><c>Assets/UI/map/town_map.png</c> (1000×1360 at 2 px/m, rasterised by <see cref="MapRaster"/>; BC7, mipmaps, bilinear,
/// clamp);</item>
/// <item>a root <c>[TownMapAnchor]</c> in Neighborhood.unity, outside NeighborhoodGen (builder re-runs keep it), holding the
/// hash. The scene is saved only if the anchor changed, and never when it was already open with unsaved changes.</item>
/// </list>
/// Idempotent and deterministic: a second run writes identical bytes (and skips writing files whose bytes didn't change).
/// Street names per road segment: a vote over the addresses of stops within 12 m along it, then nearby sign texts, then a
/// small fallback table. Road rects are split at district boundaries and exit junctions so Minivan Mile, Carpool Ln and the
/// Interstate On-Ramp each get their own name. Run after the living-world Neighborhood changes and after any
/// NeighborhoodBuilder run (it hooks in at the end of Build).
/// </summary>
public static class Setup_TownMap
{
    public const string ScenePath = "Assets/Scenes/Neighborhood.unity";
    public const string AnchorName = "[TownMapAnchor]";
    const float PxPerMeter = 2f;
    static readonly Rect Bounds = new Rect(-60f, -40f, 500f, 680f);

    [MenuItem("Rubbish/Setup/Town Map")]
    public static void Run()
    {
        var log = new StringBuilder();
        bool ok = Bake(log);
        Debug.Log("[Setup_TownMap] " + (ok ? "OK" : "FAILED") + "\n" + log);
    }

    /// <summary>Bake (also called by NeighborhoodBuilder at the end of a build). Returns false on failure (logged).</summary>
    public static bool Bake(StringBuilder log)
    {
        if (Application.isPlaying) { log.AppendLine("exit play mode first"); return false; }
        var t0 = DateTime.UtcNow;
        var scene = SceneManager.GetSceneByPath(ScenePath);
        bool opened = false;
        if (!scene.isLoaded)
        {
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            opened = true;
        }
        try
        {
            Transform gen = null;
            GameObject anchorGo = null;
            foreach (var r in scene.GetRootGameObjects())
            {
                if (r.name == "NeighborhoodGen") gen = r.transform;
                else if (r.name == AnchorName) anchorGo = r;
            }
            if (gen == null) { log.AppendLine("no NeighborhoodGen in " + ScenePath); return false; }

            var data = Scan(gen, log);
            data.sourceHash = TownMapData.HashScene(gen);

            // ---- the PNG (written only if the bytes changed)
            var px = MapRaster.Render(data);
            var tex = new Texture2D(data.width, data.height, TextureFormat.RGBA32, false);
            tex.SetPixels32(px);
            tex.Apply(false);
            var png = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);
            Directory.CreateDirectory(Path.GetDirectoryName(TownMapData.TexturePath));
            bool pngChanged = !File.Exists(TownMapData.TexturePath) || !Same(File.ReadAllBytes(TownMapData.TexturePath), png);
            if (pngChanged)
            {
                File.WriteAllBytes(TownMapData.TexturePath, png);
                AssetDatabase.ImportAsset(TownMapData.TexturePath, ImportAssetOptions.ForceSynchronousImport);
            }
            ConfigureImporter();
            data.texture = AssetDatabase.LoadAssetAtPath<Texture2D>(TownMapData.TexturePath);

            // ---- the asset (updated in place, so its GUID and the references to it stay)
            var asset = AssetDatabase.LoadAssetAtPath<TownMapData>(TownMapData.AssetPath);
            if (asset == null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(TownMapData.AssetPath));
                asset = ScriptableObject.CreateInstance<TownMapData>();
                CopyInto(data, asset);
                AssetDatabase.CreateAsset(asset, TownMapData.AssetPath);
            }
            else
            {
                CopyInto(data, asset);
                EditorUtility.SetDirty(asset);
            }
            AssetDatabase.SaveAssets();
            TownMapData.Unload();
            UnityEngine.Object.DestroyImmediate(data);

            // ---- the scene anchor (outside NeighborhoodGen; saved only when it changed and the scene had no unsaved edits)
            bool anchorChanged = false;
            if (anchorGo == null)
            {
                if (!opened && scene.isDirty) log.AppendLine("WARNING Neighborhood is open with unsaved changes: anchor not added (re-run when it's saved)");
                else
                {
                    anchorGo = new GameObject(AnchorName);
                    SceneManager.MoveGameObjectToScene(anchorGo, scene);
                    anchorGo.AddComponent<TownMapAnchor>();
                    anchorChanged = true;
                }
            }
            if (anchorGo != null)
            {
                var a = anchorGo.GetComponent<TownMapAnchor>() ?? anchorGo.AddComponent<TownMapAnchor>();
                if (a.sourceHash != asset.sourceHash)
                {
                    if (!opened && scene.isDirty && !anchorChanged) log.AppendLine("WARNING Neighborhood has unsaved changes: anchor hash not updated");
                    else { a.sourceHash = asset.sourceHash; anchorChanged = true; }
                }
            }
            if (anchorChanged) EditorSceneManager.SaveScene(scene);

            log.AppendLine($"roads {asset.roads.Count} (named {Count(asset.roads, r => !string.IsNullOrEmpty(r.name))}), areas {asset.areas.Count}, " +
                           $"houses {asset.houses.Count}, districts {asset.districts.Count}, labels {asset.labels.Count}, pois {asset.pois.Count}");
            log.AppendLine($"png {asset.width}x{asset.height} {(pngChanged ? "written" : "unchanged")}, hash {asset.sourceHash}, anchor {(anchorChanged ? "saved" : "unchanged")}, " +
                           $"{(DateTime.UtcNow - t0).TotalSeconds:0.00} s");
            return true;
        }
        catch (Exception e)
        {
            log.AppendLine("ERROR " + e);
            return false;
        }
        finally
        {
            if (opened) EditorSceneManager.CloseScene(scene, true);
        }
    }

    /// <summary>
    /// Scan and rasterise without writing anything in the project (read-only scene inspection): the PNG goes to
    /// <paramref name="pngOut"/>, and the scan summary to <paramref name="log"/>. For previews and the checks.
    /// </summary>
    public static TownMapData DryRun(string pngOut, StringBuilder log)
    {
        var scene = SceneManager.GetSceneByPath(ScenePath);
        bool opened = false;
        if (!scene.isLoaded) { scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive); opened = true; }
        try
        {
            Transform gen = null;
            foreach (var r in scene.GetRootGameObjects()) if (r.name == "NeighborhoodGen") gen = r.transform;
            if (gen == null) { log.AppendLine("no NeighborhoodGen"); return null; }
            var t0 = DateTime.UtcNow;
            var data = Scan(gen, log);
            data.sourceHash = TownMapData.HashScene(gen);
            var t1 = DateTime.UtcNow;
            var px = MapRaster.Render(data);
            var t2 = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(pngOut))
            {
                var tex = new Texture2D(data.width, data.height, TextureFormat.RGBA32, false);
                tex.SetPixels32(px);
                tex.Apply(false);
                File.WriteAllBytes(pngOut, tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);
            }
            log.AppendLine($"dry run: roads {data.roads.Count}, areas {data.areas.Count}, houses {data.houses.Count}, labels {data.labels.Count}, " +
                           $"scan {(t1 - t0).TotalSeconds:0.00} s, raster {(t2 - t1).TotalSeconds:0.00} s, hash {data.sourceHash}");
            return data;
        }
        finally { if (opened) EditorSceneManager.CloseScene(scene, true); }
    }

    static int Count<T>(List<T> l, Func<T, bool> f) { int n = 0; foreach (var x in l) if (f(x)) n++; return n; }

    static bool Same(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    static void CopyInto(TownMapData src, TownMapData dst)
    {
        dst.schema = TownMapData.SchemaVersion;
        dst.bounds = src.bounds;
        dst.pxPerMeter = src.pxPerMeter;
        dst.width = src.width;
        dst.height = src.height;
        dst.texture = src.texture;
        dst.sourceHash = src.sourceHash;
        dst.roads = new List<TownMapData.Road>(src.roads);
        dst.areas = new List<TownMapData.Area>(src.areas);
        dst.districts = new List<TownMapData.District>(src.districts);
        dst.labels = new List<TownMapData.Label>(src.labels);
        dst.pois = new List<TownMapData.Poi>(src.pois);
        dst.houses = new List<TownMapData.House>(src.houses);
    }

    static void ConfigureImporter()
    {
        var imp = AssetImporter.GetAtPath(TownMapData.TexturePath) as TextureImporter;
        if (imp == null) return;
        bool changed = false;
        void Set<T>(T cur, T want, Action<T> apply) { if (!EqualityComparer<T>.Default.Equals(cur, want)) { apply(want); changed = true; } }
        Set(imp.textureType, TextureImporterType.Default, v => imp.textureType = v);
        Set(imp.sRGBTexture, true, v => imp.sRGBTexture = v);
        Set(imp.mipmapEnabled, true, v => imp.mipmapEnabled = v);
        Set(imp.filterMode, FilterMode.Bilinear, v => imp.filterMode = v);
        Set(imp.wrapMode, TextureWrapMode.Clamp, v => imp.wrapMode = v);
        Set(imp.npotScale, TextureImporterNPOTScale.None, v => imp.npotScale = v);
        Set(imp.alphaSource, TextureImporterAlphaSource.None, v => imp.alphaSource = v);
        Set(imp.isReadable, false, v => imp.isReadable = v);
        Set(imp.maxTextureSize, 2048, v => imp.maxTextureSize = v);
        Set(imp.textureCompression, TextureImporterCompression.CompressedHQ, v => imp.textureCompression = v);
        var ps = imp.GetPlatformTextureSettings("Standalone");
        if (!ps.overridden || ps.format != TextureImporterFormat.BC7 || ps.maxTextureSize != 2048)
        {
            ps.overridden = true;
            ps.format = TextureImporterFormat.BC7;
            ps.maxTextureSize = 2048;
            imp.SetPlatformTextureSettings(ps);
            changed = true;
        }
        if (changed) imp.SaveAndReimport();
    }

    // ------------------------------------------------------------------ scan

    static Rect R(Bounds b) => new Rect(b.min.x, b.min.z, b.size.x, b.size.z);

    static bool TryCollider(Transform t, out Rect r)
    {
        r = default;
        var bc = t.GetComponent<BoxCollider>();
        if (bc != null) { r = R(TownMapData.WorldBounds(bc)); return true; }
        var c = t.GetComponent<Collider>();
        if (c != null && c.enabled && t.gameObject.activeInHierarchy) { r = R(c.bounds); return true; }
        return false;
    }

    static bool TryRenderer(Transform t, out Rect r)
    {
        r = default;
        bool any = false;
        Bounds b = default;
        foreach (var rr in t.GetComponentsInChildren<Renderer>(true))
        {
            if (rr is ParticleSystemRenderer) continue;
            if (!any) { b = rr.bounds; any = true; } else b.Encapsulate(rr.bounds);
        }
        if (any) r = R(b);
        return any;
    }

    static TownMapData Scan(Transform gen, StringBuilder log)
    {
        var d = ScriptableObject.CreateInstance<TownMapData>();
        d.hideFlags = HideFlags.DontSave;
        d.bounds = Bounds;
        d.pxPerMeter = PxPerMeter;
        d.width = Mathf.RoundToInt(Bounds.width * PxPerMeter);
        d.height = Mathf.RoundToInt(Bounds.height * PxPerMeter);

        // ---- districts (tier order) + gates
        var gateByDistrict = new Dictionary<string, DistrictGate>();
        foreach (var g in gen.GetComponentsInChildren<DistrictGate>(true)) gateByDistrict[g.districtId] = g;

        // ---- ground: roads, sidewalks, curbs, pads
        var rawRoads = new List<Rect>();
        var ground = gen.Find("Ground");
        if (ground != null)
            foreach (Transform c in ground)
            {
                if (!TryCollider(c, out var r)) continue;
                string n = c.name;
                if (n.StartsWith("Road_")) rawRoads.Add(r);
                else if (n.StartsWith("Sidewalk_")) Area(d, r, MapAreaKind.Sidewalk);
                else if (n.StartsWith("Curb_")) Area(d, r, MapAreaKind.Curb);
                else if (n.StartsWith("DepotPad_")) Area(d, r, MapAreaKind.DepotPad);
            }

        // ---- stops (names for the street vote; the houses list)
        var stops = new List<PickupStop>(gen.GetComponentsInChildren<PickupStop>(true));
        stops.Sort((a, b) => string.CompareOrdinal(a.stopId, b.stopId));

        // ---- street signs (fallback names): StreetSign_* texts and the district Sign boards' first line
        var signs = new List<KeyValuePair<Vector2, string>>();
        foreach (var t in gen.GetComponentsInChildren<Transform>(true))
        {
            if (!t.name.StartsWith("StreetSign_")) continue;
            var tmp = t.GetComponentInChildren<TMP_Text>(true);
            if (tmp == null || string.IsNullOrEmpty(tmp.text)) continue;
            string first = tmp.text.Split('\n')[0].Trim();
            if (first.IndexOf('<') >= 0) first = FeelUi.StripTags(first).Trim();
            if (first.Length == 0) continue;
            signs.Add(new KeyValuePair<Vector2, string>(new Vector2(t.position.x, t.position.z), TitleCase(first.TrimEnd('-', '>', ' '))));
        }

        // ---- exits (TownExitPortal: id, display name, where)
        var portals = new List<TownExitPortal>(gen.GetComponentsInChildren<TownExitPortal>(true));
        portals.Sort((a, b) => string.CompareOrdinal(a.exitId, b.exitId));

        // ---- road segments: split at district edges and exit junctions, then name them
        foreach (var raw in rawRoads) SplitRoad(d, raw, rawRoads, portals);
        d.roads.Sort((a, b) => a.rect.x != b.rect.x ? a.rect.x.CompareTo(b.rect.x) : a.rect.y.CompareTo(b.rect.y));
        var nearestSeg = new Dictionary<PickupStop, int>();
        foreach (var s in stops)
        {
            var sp = new Vector2(s.transform.position.x, s.transform.position.z);
            float best = float.MaxValue; int bi = -1;
            for (int i = 0; i < d.roads.Count; i++)
            {
                float dd = RectDistance(d.roads[i].rect, sp);
                if (dd < best) { best = dd; bi = i; }
            }
            nearestSeg[s] = bi;
        }
        for (int i = 0; i < d.roads.Count; i++)
        {
            var road = d.roads[i];
            if (string.IsNullOrEmpty(road.name)) road.name = NameRoad(i, road, stops, nearestSeg, signs);
            d.roads[i] = road;
        }
        // unnamed bits (turnaround stubs) take the name of the road they touch most
        for (int i = 0; i < d.roads.Count; i++)
        {
            if (!string.IsNullOrEmpty(d.roads[i].name)) continue;
            float best = 0f; string name = null;
            for (int j = 0; j < d.roads.Count; j++)
            {
                if (j == i || string.IsNullOrEmpty(d.roads[j].name)) continue;
                var a = d.roads[i].rect.Expand(0.3f); var b = d.roads[j].rect;
                float ox = Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin), oz = Mathf.Min(a.yMax, b.yMax) - Mathf.Max(a.yMin, b.yMin);
                float shared = ox > 0f && oz > 0f ? Mathf.Max(ox, oz) : 0f;
                if (shared > best) { best = shared; name = d.roads[j].name; }
            }
            var r = d.roads[i]; r.name = name; d.roads[i] = r;
        }

        // ---- lots, houses, water, hedges, driveways
        var lotRoots = new List<Transform>();
        var lots = gen.Find("Lots");
        if (lots != null) foreach (Transform l in lots) lotRoots.Add(l);
        var dists = gen.Find("Districts");
        if (dists != null)
            foreach (Transform dd in dists)
            {
                var dl = dd.Find("Lots");
                if (dl != null) foreach (Transform l in dl) lotRoots.Add(l);
            }
        var footprints = new Dictionary<string, Rect>();
        foreach (var lot in lotRoots) ScanLot(d, lot, footprints);

        // district props, walls and the depot
        if (dists != null)
            foreach (Transform dd in dists)
            {
                var props = dd.Find("Props");
                if (props != null)
                    foreach (Transform p in props)
                    {
                        if (!TryRenderer(p, out var pr) && !TryCollider(p, out pr)) continue;
                        switch (p.name)
                        {
                            case "TennisCourt": Area(d, pr, MapAreaKind.Court); break;
                            case "HedgeMaze": Area(d, pr, MapAreaKind.Hedge); break;
                            case "Fountain": case "FountainIsland": Area(d, pr, MapAreaKind.Water); break;
                            case "GreenPath_EW": Area(d, pr, MapAreaKind.ParkPath); break;
                        }
                    }
                var style = dd.name.Contains("Meadows") ? MapAreaKind.Fence : dd.name.Contains("Snootington") ? MapAreaKind.Hedge : MapAreaKind.StoneWall;
                foreach (Transform w in dd)
                {
                    if (w.name != "Wall") continue;
                    foreach (Transform piece in w)
                    {
                        if (!piece.name.StartsWith("WallCollider") && !piece.name.StartsWith("Hedge") && !piece.name.StartsWith("StoneWall")) continue;
                        if (TryCollider(piece, out var wr) || TryRenderer(piece, out wr)) Area(d, Thicken(wr, 0.6f), style);
                    }
                }
            }
        var depotT = gen.Find("Depot");
        if (depotT != null)
        {
            var b = depotT.Find("Building");
            if (b != null && TryRenderer(b, out var br)) Area(d, br, MapAreaKind.Building);
        }

        // park: the lawn (trees and benches' extent), its paths and the sand
        var park = gen.Find("Park");
        if (park != null)
        {
            bool any = false;
            Rect lawn = default;
            foreach (Transform p in park)
            {
                if (p.name.StartsWith("ParkPath")) { if (TryCollider(p, out var pp)) Area(d, pp, MapAreaKind.ParkPath); continue; }
                if (p.name.StartsWith("PlaygroundSand")) { if (TryCollider(p, out var sp) || TryRenderer(p, out sp)) Area(d, sp, MapAreaKind.Sand); continue; }
                if (!(p.name.StartsWith("SM_tree") || p.name.StartsWith("SM_bush") || p.name.StartsWith("SM_bench") || p.name.StartsWith("SM_swing") ||
                      p.name.StartsWith("SM_slide") || p.name.StartsWith("SM_climb"))) continue;
                var pos = new Rect(p.position.x - 3f, p.position.z - 3f, 6f, 6f);
                lawn = any ? Rect.MinMaxRect(Mathf.Min(lawn.xMin, pos.xMin), Mathf.Min(lawn.yMin, pos.yMin), Mathf.Max(lawn.xMax, pos.xMax), Mathf.Max(lawn.yMax, pos.yMax)) : pos;
                any = true;
            }
            if (any) d.areas.Insert(0, new TownMapData.Area { rect = lawn, kind = MapAreaKind.Park });
            string parkName = null;
            foreach (var t in park.GetComponentsInChildren<TMP_Text>(true)) { parkName = TitleCase(FeelUi.StripTags(t.text.Split('\n')[0]).Trim()); break; }
            if (any) d.labels.Add(new TownMapData.Label { text = (parkName ?? "Pickle Park").ToUpperInvariant(), pos = lawn.center, size = 24f, kind = MapLabelKind.Park });
        }

        // tunnels at the exits
        foreach (var p in portals)
        {
            var bc = p.GetComponent<BoxCollider>();
            if (bc != null) Area(d, Thicken(R(TownMapData.WorldBounds(bc)), 1f), MapAreaKind.Tunnel);
        }

        // ---- houses (from the stops, joined to their lot footprints)
        foreach (var s in stops)
        {
            var pos = s.transform.position;
            var h = new TownMapData.House
            {
                stopId = s.stopId, address = s.address, curb = new Vector2(pos.x, pos.z),
                districtId = Districts.ForStopId(s.stopId).id,
            };
            if (footprints.TryGetValue(s.stopId, out var fp)) h.footprint = fp;
            d.houses.Add(h);
        }

        // ---- districts
        foreach (var dist in Districts.All)
        {
            var e = new TownMapData.District { id = dist.id, name = dist.name, rect = dist.boundsXZ };
            if (gateByDistrict.TryGetValue(dist.id, out var g))
            {
                e.hasGate = true;
                e.gate = new Vector2(g.transform.position.x, g.transform.position.z);
                e.gateForward = new Vector2(g.transform.forward.x, g.transform.forward.z);
            }
            // label: the middle of the district's houses (or the rect), clamped into the map
            bool any = false;
            Rect hb = default;
            foreach (var h in d.houses)
            {
                if (h.districtId != dist.id) continue;
                var r = new Rect(h.curb, Vector2.zero);
                hb = any ? Rect.MinMaxRect(Mathf.Min(hb.xMin, r.xMin), Mathf.Min(hb.yMin, r.yMin), Mathf.Max(hb.xMax, r.xMax), Mathf.Max(hb.yMax, r.yMax)) : r;
                any = true;
            }
            e.labelPos = any ? hb.center : dist.boundsXZ.center;
            if (dist.id == Districts.MildlyPleasant) e.labelPos = new Vector2(150f, 220f);
            d.districts.Add(e);
            d.labels.Add(new TownMapData.Label { text = dist.name.ToUpperInvariant(), pos = e.labelPos, angle = 6f, size = 34f, kind = MapLabelKind.District });
            if (e.hasGate) d.pois.Add(new TownMapData.Poi { kind = MapPoiKind.Gate, pos = e.gate, id = dist.id, heading = Mathf.Atan2(e.gateForward.x, e.gateForward.y) * Mathf.Rad2Deg });
        }

        // ---- POIs: depot, garage door, truck parking, exits
        foreach (var door in gen.GetComponentsInChildren<GarageDoorController>(true))
        {
            var p = door.transform.position;
            d.pois.Add(new TownMapData.Poi { kind = MapPoiKind.GarageDoor, pos = new Vector2(p.x, p.z), id = door.name });
            d.labels.Add(new TownMapData.Label { text = "RUBBISH CO.", pos = new Vector2(p.x, p.z - 14f), size = 22f, kind = MapLabelKind.Depot });
        }
        var spawns = gen.Find("Spawns");
        var parking = spawns != null ? spawns.Find("TruckParking") : null;
        if (parking != null) d.pois.Add(new TownMapData.Poi { kind = MapPoiKind.TruckParking, pos = new Vector2(parking.position.x, parking.position.z), id = "parking" });
        foreach (var p in portals)
        {
            var pos = p.transform.position;
            // heading: out of town (towards the nearest map edge)
            float dl = pos.x - Bounds.xMin, dr = Bounds.xMax - pos.x, dn = Bounds.yMax - pos.z, ds = pos.z - Bounds.yMin;
            float heading = dl <= Mathf.Min(dr, Mathf.Min(dn, ds)) ? 270f : dr <= Mathf.Min(dn, ds) ? 90f : dn <= ds ? 0f : 180f;
            var inward = Quaternion.Euler(0f, heading, 0f) * Vector3.back * 16f;
            d.pois.Add(new TownMapData.Poi { kind = MapPoiKind.Exit, pos = new Vector2(pos.x + inward.x, pos.z + inward.z), id = p.exitId, heading = heading });
        }

        // ---- street labels: one per named segment ≥ 30 m (two on long ones)
        var seen = new Dictionary<string, int>();
        foreach (var r in d.roads)
        {
            if (string.IsNullOrEmpty(r.name) || r.axis > 1) continue;
            float len = r.axis == 0 ? r.rect.width : r.rect.height;
            if (len < 30f) continue;
            int n = len > 220f ? 2 : 1;
            for (int i = 0; i < n; i++)
            {
                float f = n == 1 ? 0.5f : (i + 1f) / (n + 1f);
                var c = r.axis == 0 ? new Vector2(Mathf.Lerp(r.rect.xMin, r.rect.xMax, f), r.rect.center.y) : new Vector2(r.rect.center.x, Mathf.Lerp(r.rect.yMin, r.rect.yMax, f));
                d.labels.Add(new TownMapData.Label { text = r.name, pos = c, angle = r.axis == 0 ? 0f : 90f, size = 18f, kind = r.exit ? MapLabelKind.Exit : MapLabelKind.Street });
            }
            seen[r.name] = seen.TryGetValue(r.name, out int k) ? k + 1 : 1;
        }
        log.AppendLine("streets: " + string.Join(", ", new List<string>(seen.Keys)));
        return d;
    }

    static Rect Thicken(Rect r, float min)
    {
        if (r.width < min) { float c = r.center.x; r.xMin = c - min * 0.5f; r.xMax = c + min * 0.5f; }
        if (r.height < min) { float c = r.center.y; r.yMin = c - min * 0.5f; r.yMax = c + min * 0.5f; }
        return r;
    }

    static void Area(TownMapData d, Rect r, MapAreaKind k)
    {
        if (r.width <= 0.01f || r.height <= 0.01f) return;
        d.areas.Add(new TownMapData.Area { rect = r, kind = k });
    }

    static void ScanLot(TownMapData d, Transform lot, Dictionary<string, Rect> footprints)
    {
        bool any = false;
        Rect lotR = default, houseUnion = default;
        bool anyHouse = false;
        foreach (Transform c in lot)
        {
            string n = c.name;
            if (n.StartsWith("SM_house") || n == "Hall")
            {
                if (TryRenderer(c, out var hr))
                {
                    bool big = hr.width * hr.height > 420f || n == "Hall";
                    Area(d, hr, big ? MapAreaKind.BigHouse : MapAreaKind.House);
                    houseUnion = anyHouse ? Rect.MinMaxRect(Mathf.Min(houseUnion.xMin, hr.xMin), Mathf.Min(houseUnion.yMin, hr.yMin), Mathf.Max(houseUnion.xMax, hr.xMax), Mathf.Max(houseUnion.yMax, hr.yMax)) : hr;
                    anyHouse = true;
                    Union(ref lotR, ref any, hr);
                }
                continue;
            }
            Rect r;
            switch (n)
            {
                case "Driveway": case "Drive": case "DriveLink": case "Turnaround":
                    if (TryCollider(c, out r) || TryRenderer(c, out r)) { Area(d, r, MapAreaKind.Driveway); Union(ref lotR, ref any, r); }
                    break;
                case "Path": case "SM_path_garden":
                    if (TryCollider(c, out r) || TryRenderer(c, out r)) { Area(d, r, MapAreaKind.GardenPath); Union(ref lotR, ref any, r); }
                    break;
                case "SM_whirlpool": case "Fountain":
                    if (TryRenderer(c, out r)) Area(d, r, MapAreaKind.Water);
                    break;
                case "Helipad":
                    if (TryRenderer(c, out r)) Area(d, r, MapAreaKind.Helipad);
                    break;
                case "Hedge":
                    if (TryCollider(c, out r) || TryRenderer(c, out r)) { Area(d, Thicken(r, 0.8f), MapAreaKind.Hedge); Union(ref lotR, ref any, r); }
                    break;
                case "EstateWall":
                    if (TryCollider(c, out r)) { Area(d, Thicken(r, 0.8f), MapAreaKind.StoneWall); Union(ref lotR, ref any, r); }
                    break;
                case "SM_garden_fence_long": case "SM_mailbox_01": case "SM_mailbox_02": case "SM_garbagecan_01": case "SM_garbagecan_02": case "SM_garbagecan_03":
                    if (TryCollider(c, out r)) Union(ref lotR, ref any, r);
                    break;
            }
        }
        if (any) d.areas.Insert(0, new TownMapData.Area { rect = lotR, kind = MapAreaKind.Lot });
        if (anyHouse)
        {
            // Lot_maple_8 ↔ stop "maple-8"; Lot_mm-carpool_10 ↔ "mm-carpool-10"
            string n = lot.name.StartsWith("Lot_") ? lot.name.Substring(4) : lot.name;
            int us = n.LastIndexOf('_');
            if (us > 0) n = n.Substring(0, us) + "-" + n.Substring(us + 1);
            footprints[n] = houseUnion;
        }
    }

    static void Union(ref Rect acc, ref bool any, Rect r)
    {
        acc = any ? Rect.MinMaxRect(Mathf.Min(acc.xMin, r.xMin), Mathf.Min(acc.yMin, r.yMin), Mathf.Max(acc.xMax, r.xMax), Mathf.Max(acc.yMax, r.yMax)) : r;
        any = true;
    }

    // ------------------------------------------------------------------ road segments and names

    static void SplitRoad(TownMapData d, Rect raw, List<Rect> all, List<TownExitPortal> portals)
    {
        byte axis = raw.width > raw.height * 1.5f ? (byte)0 : raw.height > raw.width * 1.5f ? (byte)1 : (byte)2;
        if (axis == 2) { AddSeg(d, raw, axis, null, false); return; }
        bool alongX = axis == 0;
        float a0 = alongX ? raw.xMin : raw.yMin, a1 = alongX ? raw.xMax : raw.yMax;
        var cuts = new List<float> { a0, a1 };
        // district edges crossing the road
        foreach (var dist in Districts.All)
        {
            var b = dist.boundsXZ;
            if (alongX) { if (b.yMax > raw.yMin && b.yMin < raw.yMax) { cuts.Add(b.xMin); cuts.Add(b.xMax); } }
            else if (b.xMax > raw.xMin && b.xMin < raw.xMax) { cuts.Add(b.yMin); cuts.Add(b.yMax); }
        }
        // exit junctions: the edge of the last crossing road before a portal on this road
        var exitSeg = new List<(float from, float to, string name)>();
        foreach (var p in portals)
        {
            var pp = new Vector2(p.transform.position.x, p.transform.position.z);
            if (!raw.Contains(pp)) continue;
            float portalA = alongX ? pp.x : pp.y;
            float best = float.MaxValue, cut = float.NaN;
            foreach (var o in all)
            {
                if (o == raw) continue;
                bool perpendicular = alongX ? o.height > o.width : o.width > o.height;
                if (!perpendicular) continue;
                bool touches = alongX ? o.yMax >= raw.yMin - 0.2f && o.yMin <= raw.yMax + 0.2f && o.xMax > raw.xMin - 0.2f && o.xMin < raw.xMax + 0.2f
                                      : o.xMax >= raw.xMin - 0.2f && o.xMin <= raw.xMax + 0.2f && o.yMax > raw.yMin - 0.2f && o.yMin < raw.yMax + 0.2f;
                if (!touches) continue;
                float near = alongX ? (portalA > o.center.x ? o.xMax : o.xMin) : (portalA > o.center.y ? o.yMax : o.yMin);
                float dist = Mathf.Abs(portalA - near);
                if (dist < best) { best = dist; cut = near; }
            }
            if (float.IsNaN(cut)) cut = portalA > (a0 + a1) * 0.5f ? a0 : a1;
            cuts.Add(cut);
            string name = string.IsNullOrEmpty(p.displayName) ? p.exitId : p.displayName;
            exitSeg.Add(portalA < cut ? (a0, cut, name) : (cut, a1, name)); // from the junction out to the map edge
        }
        cuts.Sort();
        // drop district cuts that would leave a sliver under 12 m (the exit cuts and the ends always stay)
        for (int i = cuts.Count - 2; i >= 1; i--)
        {
            bool isExit = false;
            foreach (var e in exitSeg) if (Mathf.Approximately(cuts[i], e.from) || Mathf.Approximately(cuts[i], e.to)) isExit = true;
            if (isExit) continue;
            if (cuts[i] - a0 < 12f || a1 - cuts[i] < 12f) cuts.RemoveAt(i);
        }
        // merge cuts into segments, then merge neighbouring segments in the same district (not across an exit cut)
        var segs = new List<(float s0, float s1, string district, string exit)>();
        for (int i = 0; i + 1 < cuts.Count; i++)
        {
            float s0 = Mathf.Max(a0, cuts[i]), s1 = Mathf.Min(a1, cuts[i + 1]);
            if (s1 - s0 < 0.5f) continue;
            float m = (s0 + s1) * 0.5f;
            var mid = alongX ? new Vector3(m, 0f, raw.center.y) : new Vector3(raw.center.x, 0f, m);
            string dist = Districts.AtPosition(mid).id;
            string exit = null;
            foreach (var e in exitSeg) if (m > Mathf.Min(e.from, e.to) && m < Mathf.Max(e.from, e.to)) exit = e.name;
            if (segs.Count > 0 && segs[segs.Count - 1].district == dist && segs[segs.Count - 1].exit == exit)
                segs[segs.Count - 1] = (segs[segs.Count - 1].s0, s1, dist, exit);
            else segs.Add((s0, s1, dist, exit));
        }
        foreach (var s in segs)
        {
            var r = alongX ? Rect.MinMaxRect(s.s0, raw.yMin, s.s1, raw.yMax) : Rect.MinMaxRect(raw.xMin, s.s0, raw.xMax, s.s1);
            byte ax = (alongX ? r.width : r.height) > (alongX ? r.height : r.width) * 1.5f ? axis : (byte)2;
            AddSeg(d, r, ax, s.exit, s.exit != null);
        }
    }

    static void AddSeg(TownMapData d, Rect r, byte axis, string name, bool exit)
    {
        var mid = new Vector3(r.center.x, 0f, r.center.y);
        d.roads.Add(new TownMapData.Road { rect = r, axis = axis, name = name, exit = exit, districtId = Districts.AtPosition(mid).id });
    }

    static float RectDistance(Rect r, Vector2 p)
    {
        float dx = Mathf.Max(0f, Mathf.Max(r.xMin - p.x, p.x - r.xMax));
        float dz = Mathf.Max(0f, Mathf.Max(r.yMin - p.y, p.y - r.yMax));
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    static Rect Expand(this Rect r, float m) => Rect.MinMaxRect(r.xMin - m, r.yMin - m, r.xMax + m, r.yMax + m);

    static string NameRoad(int index, TownMapData.Road road, List<PickupStop> stops, Dictionary<PickupStop, int> nearestSeg,
                           List<KeyValuePair<Vector2, string>> signs)
    {
        var r = road.rect;
        // 1. a vote over the addresses of stops within 12 m along the segment (each stop votes only for its nearest segment,
        //    so a corner house on Birch St doesn't name the Old Money Rd stretch beside it)
        var votes = new Dictionary<string, int>();
        foreach (var s in stops)
        {
            if (nearestSeg.TryGetValue(s, out int ns) && ns != index) continue;
            var p = s.transform.position;
            bool along = road.axis == 0 ? p.x >= r.xMin && p.x <= r.xMax && Mathf.Max(r.yMin - p.z, p.z - r.yMax) <= 12f
                       : road.axis == 1 ? p.z >= r.yMin && p.z <= r.yMax && Mathf.Max(r.xMin - p.x, p.x - r.xMax) <= 12f
                       : Mathf.Max(Mathf.Max(r.xMin - p.x, p.x - r.xMax), Mathf.Max(r.yMin - p.z, p.z - r.yMax)) <= 12f;
            if (!along) continue;
            string street = StreetOf(s.address);
            if (street == null) continue;
            votes[street] = votes.TryGetValue(street, out int n) ? n + 1 : 1;
        }
        string best = null;
        int bestN = 0;
        var keys = new List<string>(votes.Keys);
        keys.Sort(string.CompareOrdinal);
        foreach (var k in keys) if (votes[k] > bestN) { bestN = votes[k]; best = k; }
        if (best != null) return best;
        // 2. the fallback table (the north–south spine and Minivan Mile carry no houses of their own)
        var c = r.center;
        if (Mathf.Abs(c.x) < 6f && c.y < 60f) return "Depot Rd";
        if (Mathf.Abs(c.x) < 6f && c.y > 165f && c.y < 460f) return "Old Money Rd";
        if (Mathf.Abs(c.x) < 6f && c.y >= 460f && c.y < 555f) return "Tax Haven Heights Rd";
        if (c.y < 0f && c.x < 206f) return "Minivan Mile";
        // 3. the nearest street sign within 15 m
        float bd = 15f * 15f;
        foreach (var kv in signs)
        {
            float dx = Mathf.Max(0f, Mathf.Max(r.xMin - kv.Key.x, kv.Key.x - r.xMax));
            float dz = Mathf.Max(0f, Mathf.Max(r.yMin - kv.Key.y, kv.Key.y - r.yMax));
            float dd = dx * dx + dz * dz;
            if (dd < bd) { bd = dd; best = kv.Value; }
        }
        return best;
    }

    /// <summary>"18 Birch St" → "Birch St".</summary>
    static string StreetOf(string address)
    {
        if (string.IsNullOrEmpty(address)) return null;
        int sp = address.IndexOf(' ');
        if (sp <= 0) return null;
        bool num = true;
        for (int i = 0; i < sp; i++) if (!char.IsDigit(address[i])) { num = false; break; }
        return num ? address.Substring(sp + 1).Trim() : address.Trim();
    }

    static string TitleCase(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length);
        bool start = true;
        foreach (char ch in s.ToLowerInvariant())
        {
            sb.Append(start && char.IsLetter(ch) ? char.ToUpperInvariant(ch) : ch);
            start = ch == ' ' || ch == '-';
        }
        return sb.ToString();
    }
}
